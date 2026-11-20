#include "core.h"
#include "libdvbmod.h"
#include <cmath>
#include <cstring>
#include <chrono>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
typedef int socklen_t;
#define CLOSESOCK closesocket
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>
#define CLOSESOCK close
#endif

static const double PI = 3.14159265358979323846;

// Output amplitude for a symbol of magnitude 1. RRC peaks reach ~1.4x the symbol
// level, so this keeps the waveform inside full scale without clipping.
static float OutScale(const ModConfig &c)
{
    // Measured RRC peak-to-symbol ratios (roll-off 0.20): QPSK 1.34, 8PSK 1.84,
    // 16APSK 1.76, 32APSK 1.71. Scaled for peaks of about 0.85 of full scale.
    if (!c.dvbs2) return 0.63f;
    switch (c.constellation)
    {
    case M_8PSK: return 0.46f;
    case M_16APSK: return 0.48f;
    case M_32APSK: return 0.50f;
    default: return 0.63f;
    }
}

// ------------------------------------------------------------------ parsing

int ParseCodeRate(const std::string &f)
{
    static const char *names[] = {"1/4", "1/3", "2/5", "1/2", "3/5", "2/3", "3/4", "4/5", "5/6", "8/9", "7/8", "9/10"};
    for (int i = 0; i < 12; i++)
        if (f == names[i]) return i; // matches CR_1_4 .. CR_9_10 in libdvbmod.h
    return -1;
}

int ParseConstellation(const std::string &c)
{
    if (c == "qpsk" || c == "QPSK") return M_QPSK;
    if (c == "8psk" || c == "8PSK") return M_8PSK;
    if (c == "16apsk" || c == "16APSK") return M_16APSK;
    if (c == "32apsk" || c == "32APSK") return M_32APSK;
    return -1;
}

// ------------------------------------------------------------------ RRC

static double RrcTap(double t, double beta)
{
    if (std::fabs(t) < 1e-9) return 1.0 - beta + 4.0 * beta / PI;
    if (beta > 0 && std::fabs(std::fabs(t) - 1.0 / (4.0 * beta)) < 1e-9)
        return beta / std::sqrt(2.0) * ((1 + 2 / PI) * std::sin(PI / (4 * beta)) + (1 - 2 / PI) * std::cos(PI / (4 * beta)));
    double num = std::sin(PI * t * (1 - beta)) + 4 * beta * t * std::cos(PI * t * (1 + beta));
    double den = PI * t * (1 - (4 * beta * t) * (4 * beta * t));
    return num / den;
}

void RrcInterpolator::Init(double rollOff, int upsample, int spanSymbols)
{
    L_ = upsample < 1 ? 1 : upsample;
    span_ = spanSymbols;
    int M = span_ * L_;
    std::vector<double> h(M);
    double sum = 0;
    for (int m = 0; m < M; m++)
    {
        double t = (m - M / 2.0) / L_;
        h[m] = RrcTap(t, rollOff);
        sum += h[m];
    }
    taps_.assign(M, 0.f);
    for (int p = 0; p < L_; p++)
        for (int k = 0; k < span_; k++)
            taps_[p * span_ + k] = (float)(h[k * L_ + p] * L_ / sum);
    hist_.assign(span_, cf(0, 0));
    pos_ = 0;
}

void RrcInterpolator::Process(cf sym, cf *out)
{
    pos_ = (pos_ + span_ - 1) % span_;
    hist_[pos_] = sym; // hist_[(pos_ + k) % span_] is x[n - k]
    for (int p = 0; p < L_; p++)
    {
        const float *t = &taps_[p * span_];
        float re = 0, im = 0;
        for (int k = 0; k < span_; k++)
        {
            const cf &x = hist_[(pos_ + k) % span_];
            re += t[k] * x.real();
            im += t[k] * x.imag();
        }
        out[p] = cf(re, im);
    }
}

// ------------------------------------------------------------------ modulator

int Modulator::Init(const ModConfig &cfg)
{
    cfg_ = cfg;
    int bitrate;
    if (cfg.dvbs2)
    {
        int ro = cfg.rollOff >= 0.34 ? RO_0_35 : cfg.rollOff >= 0.24 ? RO_0_25 : RO_0_20;
        bitrate = Dvbs2Init(cfg.symbolRate, cfg.codeRate, cfg.constellation, cfg.pilots ? 1 : 0, ro, 1, cfg.shortFrames);
    }
    else
    {
        bitrate = DvbsInit(cfg.symbolRate, cfg.codeRate, M_QPSK, 1);
    }
    rrc_.Init(cfg.dvbs2 ? cfg.rollOff : 0.35, cfg.upsample);
    // Largest frame: 64800-bit normal frame in QPSK + headers/pilots ~ 33282 symbols.
    scratch_.resize(40000 * (size_t)cfg.upsample);
    return bitrate;
}

size_t Modulator::AddPacket(const uint8_t *pkt, cf *out)
{
    int len = cfg_.dvbs2 ? Dvbs2AddTsPacket(const_cast<uint8_t *>(pkt)) : DvbsAddTsPacket(const_cast<uint8_t *>(pkt));
    if (len <= 0) return 0;
    sfcmplx *s = cfg_.dvbs2 ? Dvbs2_get_IQ() : Dvbs_get_IQ();
    const float k = OutScale(cfg_) / 32767.0f;
    size_t n = 0;
    for (int i = 0; i < len; i++)
    {
        rrc_.Process(cf(s[i].re * k, s[i].im * k), out + n);
        n += cfg_.upsample;
    }
    for (size_t i = 0; i < n; i++)
    {
        float a = std::fabs(out[i].real()), b = std::fabs(out[i].imag());
        if (a > peak) peak = a;
        if (b > peak) peak = b;
    }
    return n;
}

void Modulator::Start(TsQueue *q, IqRing *ring, size_t lowWater)
{
    q_ = q;
    ring_ = ring;
    lowWater_ = lowWater;
    run_ = true;
    th_ = std::thread(&Modulator::Run, this);
}

void Modulator::Stop()
{
    run_ = false;
    if (th_.joinable()) th_.join();
}

void Modulator::Run()
{
    uint8_t pkt[188];
    static const uint8_t nullPkt[188] = {0x47, 0x1F, 0xFF, 0x10};
    const size_t maxFrame = scratch_.size();

    while (run_)
    {
        if (ring_->Free() < maxFrame)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
            continue;
        }
        const uint8_t *src;
        if (q_->Pop(pkt))
        {
            src = pkt;
            packetsSent++;
        }
        else if (ring_->Size() < lowWater_)
        {
            src = nullPkt; // keep the carrier going when the encoder is slow or stopped
            nullsInserted++;
        }
        else
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            continue;
        }
        size_t n = AddPacket(src, scratch_.data());
        if (n) ring_->Push(scratch_.data(), n);
    }
}

// ------------------------------------------------------------------ UDP input

bool UdpTsReceiver::Start(const std::string &bindIp, int port, TsQueue *q, std::string &err)
{
#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#endif
    q_ = q;
    intptr_t s = (intptr_t)socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s < 0) { err = "socket() failed"; return false; }
    int rcv = 8 * 1024 * 1024;
    setsockopt((decltype(socket(0, 0, 0)))s, SOL_SOCKET, SO_RCVBUF, (const char *)&rcv, sizeof(rcv));
    int one = 1;
    setsockopt((decltype(socket(0, 0, 0)))s, SOL_SOCKET, SO_REUSEADDR, (const char *)&one, sizeof(one));
    sockaddr_in a;
    memset(&a, 0, sizeof(a));
    a.sin_family = AF_INET;
    a.sin_port = htons((unsigned short)port);
    inet_pton(AF_INET, bindIp.c_str(), &a.sin_addr);
    if (bind((decltype(socket(0, 0, 0)))s, (sockaddr *)&a, sizeof(a)) != 0)
    {
        err = "cannot listen on UDP " + bindIp + ":" + std::to_string(port) + " (port in use?)";
        CLOSESOCK((decltype(socket(0, 0, 0)))s);
        return false;
    }
    sock_ = s;
    run_ = true;
    th_ = std::thread(&UdpTsReceiver::Run, this);
    return true;
}

void UdpTsReceiver::Stop()
{
    run_ = false;
    if (th_.joinable()) th_.join();
    if (sock_ >= 0) CLOSESOCK((decltype(socket(0, 0, 0)))sock_);
    sock_ = -1;
}

void UdpTsReceiver::Run()
{
    std::vector<uint8_t> buf(65536);
    auto s = (decltype(socket(0, 0, 0)))sock_;
    while (run_)
    {
        fd_set fds;
        FD_ZERO(&fds);
        FD_SET(s, &fds);
        timeval tv{0, 100000};
        if (select((int)s + 1, &fds, nullptr, nullptr, &tv) <= 0) continue;
        int n = recv(s, (char *)buf.data(), (int)buf.size(), 0);
        if (n <= 0) continue;
        bytes += n;
        carry_.insert(carry_.end(), buf.begin(), buf.begin() + n);

        size_t i = 0;
        while (carry_.size() - i >= 188)
        {
            if (carry_[i] != 0x47)
            {
                syncErrors++;
                i++;
                continue;
            }
            q_->Push(&carry_[i]);
            i += 188;
        }
        carry_.erase(carry_.begin(), carry_.begin() + i);
    }
}
