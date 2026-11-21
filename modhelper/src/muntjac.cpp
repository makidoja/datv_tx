// BATC Muntjac (G4EWJ) support for datvtx_mod.
//
// The Muntjac takes DVB-S2 symbols over a USB serial port (RP2040 CDC, VID 2E8A PID 000A) and does the
// pulse shaping itself. This follows the protocol of muntjacsdr_dvb.c (Portsdown 4, Brian Jordan G4EWJ,
// GPL v3): symbols are the standard DVB-S2 bit labels of each (PL-scrambled) constellation point, QPSK
// packed 4 per byte and 8PSK 2 per byte, framed by begin/end records, with a settings record every 250 ms.
// Here the frames come from libdvbmod and each output point is converted back to its label.

#include "core.h"
#include "libdvbmod.h"
#include "muntjac.h"
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <deque>
#include <fstream>
#include <mutex>
#include <sstream>
#include <thread>

#ifdef _WIN32
#include <windows.h>
#endif

namespace
{
const uint8_t ESCAPE = 0x5c;
const uint8_t DATV_RECORD = 2;
const uint8_t EOF_RECORD = 0x84;
const uint8_t STATUSREQUEST_RECORD = 0x86;
const uint8_t BEGINFRAME_RECORD = 0x88;
const uint8_t ENDFRAME_RECORD = 0x89;

// Muntjac / dvbs2neon codes (differ from libdvbmod for FEC 9/10)
int MjFec(const std::string &f)
{
    static const char *n[] = {"1/4", "1/3", "2/5", "1/2", "3/5", "2/3", "3/4", "4/5", "5/6", "8/9", "9/10"};
    for (int i = 0; i < 11; i++)
        if (f == n[i]) return i;
    return -1;
}

void Put16(std::vector<uint8_t> &b, size_t at, uint16_t v) { b[at] = v & 0xff; b[at + 1] = v >> 8; }
void Put32(std::vector<uint8_t> &b, size_t at, uint32_t v) { for (int i = 0; i < 4; i++) b[at + i] = (v >> (8 * i)) & 0xff; }

// common_record: special, type, bytesfollowing(2), spare0(4), spare1(4), 'MJAC', crc(4) = 20 bytes
std::vector<uint8_t> CommonRecord(uint8_t type)
{
    std::vector<uint8_t> r(20, 0);
    r[0] = ESCAPE;
    r[1] = type;
    Put16(r, 2, 20 - 3);
    r[12] = 'M'; r[13] = 'J'; r[14] = 'A'; r[15] = 'C';
    return r;
}

// Constellation point -> standard DVB-S2 label (checked against dvbs2neon's scrambling tables)
inline uint8_t Label(const sfcmplx &s, bool psk8)
{
    double a = std::atan2((double)s.im, (double)s.re) * 180.0 / 3.14159265358979323846;
    if (a < 0) a += 360;
    int oct = ((int)std::floor((a + 22.5) / 45.0)) & 7;
    static const uint8_t qpsk[8] = {0, 0, 2, 2, 3, 3, 1, 1}; // only odd octants occur
    static const uint8_t psk[8] = {1, 0, 4, 6, 2, 3, 7, 5};
    if (!psk8)
    {
        // QPSK points are at 45/135/225/315: decide by quadrant
        int q = (s.re >= 0) ? (s.im >= 0 ? 0 : 3) : (s.im >= 0 ? 1 : 2);
        static const uint8_t byQuadrant[4] = {0, 2, 3, 1};
        (void)qpsk;
        return byQuadrant[q];
    }
    return psk[oct];
}

void Emit(std::vector<uint8_t> &out, uint8_t v)
{
    if (v == ESCAPE || v == '#') out.push_back(ESCAPE);
    out.push_back(v);
}
} // namespace

// ============================================================================ serial port

bool MuntjacLink::Open(const std::string &port, std::string &err)
{
#ifdef _WIN32
    std::string path = port.compare(0, 4, "\\\\.\\") == 0 ? port : "\\\\.\\" + port;
    HANDLE h = CreateFileA(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) { err = "cannot open " + port + " (is the Muntjac plugged in and not in use?)"; return false; }
    DCB dcb;
    memset(&dcb, 0, sizeof(dcb));
    dcb.DCBlength = sizeof(dcb);
    GetCommState(h, &dcb);
    dcb.BaudRate = 115200; // ignored by USB CDC
    dcb.ByteSize = 8;
    dcb.Parity = NOPARITY;
    dcb.StopBits = ONESTOPBIT;
    dcb.fBinary = TRUE;
    dcb.fDtrControl = DTR_CONTROL_ENABLE;
    dcb.fRtsControl = RTS_CONTROL_ENABLE;
    dcb.fOutxCtsFlow = FALSE;
    dcb.fOutxDsrFlow = FALSE;
    dcb.fOutX = FALSE;
    dcb.fInX = FALSE;
    SetCommState(h, &dcb);
    COMMTIMEOUTS t;
    memset(&t, 0, sizeof(t));
    t.ReadIntervalTimeout = MAXDWORD; // reads return at once with whatever is waiting
    t.WriteTotalTimeoutConstant = 2000;
    SetCommTimeouts(h, &t);
    SetupComm(h, 65536, 65536);
    handle_ = h;
    return true;
#else
    file_ = fopen(port.c_str(), "wb"); // tests on Linux: write the byte stream to a file
    if (!file_) { err = "cannot open " + port; return false; }
    return true;
#endif
}

bool MuntjacLink::Write(const std::vector<uint8_t> &b)
{
    if (b.empty()) return true;
#ifdef _WIN32
    size_t off = 0;
    while (off < b.size())
    {
        DWORD n = 0;
        if (!WriteFile((HANDLE)handle_, b.data() + off, (DWORD)(b.size() - off), &n, nullptr)) return false;
        if (n == 0) return false; // timeout: the Muntjac stopped taking data
        off += n;
    }
    return true;
#else
    return fwrite(b.data(), 1, b.size(), file_) == b.size();
#endif
}

std::string MuntjacLink::ReadAvailable()
{
#ifdef _WIN32
    char buf[4096];
    DWORD n = 0;
    if (ReadFile((HANDLE)handle_, buf, sizeof(buf), &n, nullptr) && n > 0) return std::string(buf, n);
#endif
    return "";
}

void MuntjacLink::Close()
{
#ifdef _WIN32
    if (handle_) CloseHandle((HANDLE)handle_);
    handle_ = nullptr;
#else
    if (file_) fclose(file_);
    file_ = nullptr;
#endif
}

// ============================================================================ transmitter

int Muntjac::Init(const MuntjacConfig &c, std::string &err)
{
    cfg_ = c;
    if (c.constellation != M_QPSK && c.constellation != M_8PSK) { err = "Muntjac supports QPSK and 8PSK only"; return -1; }
    static const int rates[] = {125000, 250000, 333333, 500000, 1000000};
    bool ok = false;
    for (int r : rates) ok |= r == c.symbolRate;
    if (!ok) { err = "Muntjac symbol rates are 125, 250, 333, 500 and 1000 KS"; return -1; }
    if (c.constellation == M_8PSK && c.symbolRate == 1000000) { err = "8PSK is not available at 1000 KS on the Muntjac"; return -1; }
    bool low = c.frequency >= 389.5e6 && c.frequency <= 510e6;
    bool mid = c.frequency >= 770e6 && c.frequency <= 1030e6;
    bool high = c.frequency >= 2390e6 && c.frequency <= 2490e6;
    if (!low && !mid && !high) { err = "Muntjac frequency ranges are 389.5-510, 770-1030 and 2390-2490 MHz"; return -1; }
    band_ = c.frequency < 1500e6 ? 0 : 1;
    mjFec_ = MjFec(c.fec);
    if (mjFec_ < 0) { err = "invalid FEC"; return -1; }

    int ro = c.rollOff >= 0.34 ? RO_0_35 : c.rollOff >= 0.24 ? RO_0_25 : RO_0_20; // header field
    bitrate_ = Dvbs2Init(c.symbolRate, ParseCodeRate(c.fec), c.constellation, c.pilots ? 1 : 0, ro, 1, c.shortFrames);
    if (bitrate_ <= 0) { err = "this constellation / FEC combination is not valid"; return -1; }
    return bitrate_;
}

int Muntjac::Power(double gainDb)
{
    // App gain 0 dB = Muntjac power 0.20, the DATV maximum recommended by G4EWJ (1 dB steps).
    int p = (int)std::lround(20.0 + gainDb);
    return p < 0 ? 0 : p > 20 ? 20 : p;
}

std::vector<uint8_t> Muntjac::SettingsRecord()
{
    std::lock_guard<std::mutex> g(settingsMutex_);
    std::vector<uint8_t> r(68, 0);
    r[0] = ESCAPE;
    r[1] = DATV_RECORD;
    Put16(r, 2, 68 - 3);
    r[4] = 1; // dataversion
    r[5] = 0; // stream
    r[6 + band_] = ptt_ ? 0xc5 : 0x00;
    int srk = cfg_.symbolRate == 333333 ? 333 : cfg_.symbolRate / 1000;
    Put32(r, 8, (uint32_t)srk);
    Put32(r, 12 + 4 * band_, (uint32_t)std::llround(cfg_.frequency));
    Put32(r, 20, 0); // bufferinginitial: Muntjac default
    Put32(r, 24, 0); // bufferlowtrigger: Muntjac default
    r[28] = 0; r[29] = 0; // carriers
    r[30] = 2;            // M_DVBS2
    r[31] = cfg_.shortFrames ? 0x10 : 0x00;
    r[32] = (uint8_t)cfg_.constellation;
    r[33] = (uint8_t)mjFec_;
    r[34] = cfg_.pilots ? 1 : 0;
    r[35] = cfg_.rollOff >= 0.34 ? 0 : cfg_.rollOff >= 0.24 ? 1 : cfg_.rollOff >= 0.175 ? 2 : 3;
    // 36-39 spare, 40 spacer, 41 pause
    r[42 + band_] = (uint8_t)Power(gainDb_);
    Put32(r, 44, ++sequence_);
    Put32(r, 48, 0); // dialclicks
    r[52] = (uint8_t)iloc_[0]; r[53] = (uint8_t)iloc_[1];
    r[54] = (uint8_t)qloc_[0]; r[55] = (uint8_t)qloc_[1];
    r[60] = 'M'; r[61] = 'J'; r[62] = 'A'; r[63] = 'C';
    return r;
}

void Muntjac::LoadCalibration(const std::vector<std::string> &dirs)
{
    iloc_[0] = iloc_[1] = qloc_[0] = qloc_[1] = 99; // 99 = AT86RF215 internal value
    if (serial_.empty()) return;
    for (const auto &d : dirs)
    {
        std::ifstream f(d + "/" + serial_ + ".mjo");
        if (!f) continue;
        int table[3][32][2];
        for (auto &a : table) for (auto &b : a) b[0] = b[1] = -1;
        std::string line;
        while (std::getline(f, line))
        {
            if (line.empty() || line[0] == ' ' || line[0] == '#') continue;
            unsigned p0, p1, p2, p3;
            if (sscanf(line.c_str(), "%u %u %u %u", &p0, &p1, &p2, &p3) != 4) continue;
            int x = p0 == 440 ? 0 : p0 == 2400 ? 1 : p0 == 1300 ? 2 : -1;
            if (x < 0 || p1 > 31) continue;
            if (p2 <= 63) table[x][p1][0] = (int)p2;
            if (p3 <= 63) table[x][p1][1] = (int)p3;
        }
        int y = band_;
        if (cfg_.frequency >= 413.333e6 && cfg_.frequency < 430e6) y = 2; // tripling range
        int p = Power(gainDb_);
        int i = table[y][p][0], q = table[y][p][1];
        if (i == -1 && q == -1) { p &= ~3; i = table[y][p][0]; q = table[y][p][1]; }
        if (i != -1 || q != -1) { iloc_[band_] = i; qloc_[band_] = q; calFile_ = d + "/" + serial_ + ".mjo"; }
        return;
    }
}

bool Muntjac::Open(const std::string &port, const std::vector<std::string> &calDirs, std::string &err)
{
    if (!link_.Open(port, err)) return false;
    // ask the Muntjac who it is: "[Muntjac4-<version>:<serial> ..."
    std::string reply;
    for (int i = 0; i < 5 && serial_.empty(); i++)
    {
        link_.Write(CommonRecord(STATUSREQUEST_RECORD));
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        reply += link_.ReadAvailable();
        size_t p = reply.find("[Muntjac4-");
        if (p != std::string::npos)
        {
            size_t c = reply.find(':', p);
            size_t s = c == std::string::npos ? c : reply.find(' ', c);
            if (s != std::string::npos)
            {
                version_ = reply.substr(p + 10, c - p - 10);
                serial_ = reply.substr(c + 1, s - c - 1);
            }
        }
    }
#ifdef _WIN32
    if (serial_.empty()) { err = "no reply from a Muntjac on " + port; link_.Close(); return false; }
#endif
    LoadCalibration(calDirs);
    return true;
}

void Muntjac::BuildFrame(const sfcmplx *s, int len, std::vector<uint8_t> &out)
{
    out.clear();
    auto begin = CommonRecord(BEGINFRAME_RECORD);
    out.insert(out.end(), begin.begin(), begin.end());
    bool psk8 = cfg_.constellation == M_8PSK;
    if (!psk8)
    {
        int whole = len / 4 * 4, i = 0;
        for (; i < whole; i += 4)
            Emit(out, (uint8_t)((Label(s[i], false) << 6) | (Label(s[i + 1], false) << 4) | (Label(s[i + 2], false) << 2) | Label(s[i + 3], false)));
        if (len - whole == 2)
        {
            out.push_back(ESCAPE); // two left-over symbols in the upper nibble
            out.push_back((uint8_t)((Label(s[i], false) << 6) | (Label(s[i + 1], false) << 4)));
        }
    }
    else
    {
        for (int i = 0; i + 1 < len; i += 2)
            Emit(out, (uint8_t)((Label(s[i], true) << 4) | Label(s[i + 1], true)));
    }
    auto end = CommonRecord(ENDFRAME_RECORD);
    out.insert(out.end(), end.begin(), end.end());
}

void Muntjac::Start(TsQueue *q)
{
    q_ = q;
    run_ = true;
    framer_ = std::thread(&Muntjac::FramerLoop, this);
    writer_ = std::thread(&Muntjac::WriterLoop, this);
}

double Muntjac::FramesPerSecond() const
{
    // DVB-S2 PL frame length in symbols
    int bits = cfg_.shortFrames ? 16200 : 64800;
    int bps = cfg_.constellation == M_8PSK ? 3 : 2;
    int slots = bits / bps / 90;
    double syms = 90.0 * (slots + 1) + (cfg_.pilots ? 36.0 * ((slots - 1) / 16) : 0);
    return cfg_.symbolRate / syms;
}

void Muntjac::FramerLoop()
{
    uint8_t pkt[188];
    static const uint8_t nullPkt[188] = {0x47, 0x1F, 0xFF, 0x10};
    // keep about 0.5 s of frames queued; pad with null packets below 0.3 s
    size_t target = (size_t)std::max(2.0, FramesPerSecond() * 0.5);
    size_t low = (size_t)std::max(1.0, FramesPerSecond() * 0.3);
    std::vector<uint8_t> frame;
    while (run_)
    {
        size_t queued;
        {
            std::lock_guard<std::mutex> g(framesMutex_);
            queued = frames_.size();
        }
        const uint8_t *src = nullptr;
        if (queued >= target) { std::this_thread::sleep_for(std::chrono::milliseconds(2)); continue; }
        if (q_->Pop(pkt)) { src = pkt; packetsSent++; }
        else if (queued < low) { src = nullPkt; nullsInserted++; }
        else { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }

        int len = Dvbs2AddTsPacket(const_cast<uint8_t *>(src));
        if (len > 0)
        {
            BuildFrame(Dvbs2_get_IQ(), len, frame);
            std::lock_guard<std::mutex> g(framesMutex_);
            frames_.push_back(frame);
        }
    }
}

void Muntjac::WriterLoop()
{
    // let a little buffer build up before the first frame. Must not exceed the framer's
    // null-padding threshold (0.3 s), or with no TS arriving the two would wait for each other.
    size_t initial = (size_t)std::max(1.0, FramesPerSecond() * 0.3);
    for (int i = 0; i < 2000 && run_; i++)
    {
        { std::lock_guard<std::mutex> g(framesMutex_); if (frames_.size() >= initial) break; }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    auto lastSettings = std::chrono::steady_clock::now() - std::chrono::seconds(1);
    std::vector<uint8_t> frame;
    while (run_)
    {
        auto now = std::chrono::steady_clock::now();
        if (now - lastSettings >= std::chrono::milliseconds(250) || settingsDirty_)
        {
            settingsDirty_ = false;
            lastSettings = now;
            if (!link_.Write(SettingsRecord())) { writeFailed = true; break; }
        }
        bool have = false;
        {
            std::lock_guard<std::mutex> g(framesMutex_);
            if (!frames_.empty()) { frame.swap(frames_.front()); frames_.pop_front(); have = true; }
        }
        if (!have) { underruns++; std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
        if (!link_.Write(frame)) { writeFailed = true; break; }
        framesSent++;
        PollMessages();
    }
}

void Muntjac::PollMessages()
{
    std::string s = link_.ReadAvailable();
    if (s.empty()) return;
    std::lock_guard<std::mutex> g(msgMutex_);
    pending_ += s;
    size_t p;
    while ((p = pending_.find(']')) != std::string::npos)
    {
        size_t b = pending_.rfind('[', p);
        std::string msg = pending_.substr(b == std::string::npos ? 0 : b, p + 1 - (b == std::string::npos ? 0 : b));
        pending_.erase(0, p + 1);
        messages_.push_back(msg);
        if (msg.find("*]") != std::string::npos && msg.find("*UNDERFLOW") == std::string::npos) error = msg;
    }
}

std::vector<std::string> Muntjac::TakeMessages()
{
    std::lock_guard<std::mutex> g(msgMutex_);
    std::vector<std::string> m;
    m.swap(messages_);
    return m;
}

void Muntjac::SetPtt(bool on) { { std::lock_guard<std::mutex> g(settingsMutex_); ptt_ = on; } settingsDirty_ = true; }
void Muntjac::SetGain(double db) { { std::lock_guard<std::mutex> g(settingsMutex_); gainDb_ = db; } settingsDirty_ = true; }

bool Muntjac::SetFrequency(double hz, std::string &err)
{
    int newBand = hz < 1500e6 ? 0 : 1;
    if (newBand != band_) { err = "change band with STOP / START"; return false; }
    { std::lock_guard<std::mutex> g(settingsMutex_); cfg_.frequency = hz; }
    settingsDirty_ = true;
    return true;
}

void Muntjac::Stop()
{
    run_ = false;
    if (framer_.joinable()) framer_.join();
    if (writer_.joinable()) writer_.join();
    // carrier off, then end of data as Portsdown does
    { std::lock_guard<std::mutex> g(settingsMutex_); ptt_ = false; }
    std::vector<uint8_t> tail(256, 0xff);
    auto eof = CommonRecord(EOF_RECORD);
    tail.insert(tail.end(), eof.begin(), eof.end());
    link_.Write(SettingsRecord());
    link_.Write(tail);
    for (int i = 0; i < 10; i++) { std::this_thread::sleep_for(std::chrono::milliseconds(100)); PollMessages(); }
    link_.Close();
}
