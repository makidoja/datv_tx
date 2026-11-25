#include "esp32datv.h"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <sstream>

namespace
{
const double CpuHz = 160e6;
const double BandLo = 2300e6, BandHi = 2450e6;
using Clock = std::chrono::steady_clock;
double Secs(Clock::time_point a) { return std::chrono::duration<double>(Clock::now() - a).count(); }
}

// Samples per symbol as the firmware expects them: its hand-scheduled loops update the DAC every 20 or 24
// CPU cycles; 8 at exactly 1 MBd, otherwise 16..232 with the period giving the rate within 1 %, else <= 4 MS/s.
int Esp32Datv::Sps(int baud)
{
    if (baud == 1000000) return 8;
    double bestErr = 1e9; int best = 0;
    for (int period : {20, 24})
    {
        int sps = (int)std::lround(CpuHz / ((double)period * baud));
        if (sps < 16 || sps > 232) continue;
        double err = std::fabs(CpuHz / ((double)period * sps) / baud - 1);
        if (err <= 0.01 && err < bestErr - 1e-9) { bestErr = err; best = sps; }
    }
    if (best) return best;
    return baud * 16 <= 4000000 ? 16 : baud * 8 <= 4000000 ? 8 : 4;
}

// The symbol rate the board really produces for a QPSK stream with this baud and sps.
double Esp32Datv::OutputBaud(int baud, int sps)
{
    long sampleHz = (long)baud * sps;
    long period = (long)((CpuHz + sampleHz / 2) / sampleHz);
    bool generic = sps >= 16 && sps <= 232 && (period == 20 || period == 24);
    if (generic && (long)(CpuHz / baud) == period * sps) return baud;
    return CpuHz / ((double)period * sps);
}

int Esp32Datv::Amp(double gainDb) const
{
    if (gainDb <= -60) return cfg_.minAmp;
    int a = (int)std::lround(cfg_.maxAmp * std::pow(10.0, gainDb / 20.0));
    return std::max(std::max(1, cfg_.minAmp), std::min(480, a));
}

int Esp32Datv::Init(const Esp32Config &c, std::string &err)
{
    cfg_ = c;
    if (c.symbolRate < 2000 || c.symbolRate > 1000000) { err = "ESP32-DATV symbol rates are 2 to 1000 KS"; return -1; }
    sps_ = Sps(c.symbolRate);
    baud_ = OutputBaud(c.symbolRate, sps_);
    long period = (long)((CpuHz + (long)c.symbolRate * sps_ / 2) / ((long)c.symbolRate * sps_));
    if (!((sps_ == 8 && period == 20) || (sps_ >= 16 && sps_ <= 232 && (period == 20 || period == 24)) || ((sps_ == 4 || sps_ == 8 || sps_ == 16) && period >= 16)))
    { err = "no firmware sampling fits " + std::to_string(c.symbolRate) + " Bd"; return -1; }
    double half = baud_ * 0.675; // RRC 0.35
    if (c.frequency - half < BandLo || c.frequency + half > BandHi) { err = "the ESP32-DATV firmware transmits only in 2300-2450 MHz"; return -1; }
    if (c.maxAmp < 1 || c.maxAmp > 480) { err = "ESP32 level must be 1..480"; return -1; }
    bitrate_ = Dvbs2Init((int)std::lround(baud_), ParseCodeRate(c.fec), M_QPSK, c.pilots ? 1 : 0, RO_0_35, 1, c.shortFrames);
    if (bitrate_ <= 0) { err = "this FEC is not valid for DVB-S2 QPSK"; return -1; }
    return bitrate_;
}

void Esp32Datv::Say(const std::string &m)
{
    std::lock_guard<std::mutex> g(msgMutex_);
    messages_.push_back(m);
}

std::vector<std::string> Esp32Datv::TakeMessages()
{
    std::lock_guard<std::mutex> g(msgMutex_);
    std::vector<std::string> r;
    r.swap(messages_);
    return r;
}

// Separate the board's 4-byte buffer reports (0xB7 ...) from its text, then hand out whole text lines.
void Esp32Datv::Poll()
{
    if (fileMode_) return;
    rx_ += link_.ReadAvailable();
    size_t i = 0;
    while (i < rx_.size())
    {
        if ((uint8_t)rx_[i] == 0xB7)
        {
            if (i + 4 > rx_.size()) break;
            boardFill = (uint8_t)rx_[i + 1] | ((uint8_t)rx_[i + 2] << 8);
            boardUnderruns = (uint8_t)rx_[i + 3];
            sentSinceReport_ = 0;
            i += 4;
            continue;
        }
        text_.push_back(rx_[i++]);
    }
    rx_.erase(0, i);
}

bool Esp32Datv::NextLine(std::string &line)
{
    size_t nl = text_.find('\n');
    if (nl == std::string::npos) return false;
    line = text_.substr(0, nl);
    text_.erase(0, nl + 1);
    if (!line.empty() && line.back() == '\r') line.pop_back();
    if (line.compare(0, 6, "TX END") == 0) streaming_ = false;
    return true;
}

// While streaming: pass the board's messages on (TX END, ERR ...).
void Esp32Datv::DrainLines()
{
    std::string line;
    while (NextLine(line)) if (!line.empty()) Say(line);
}

bool Esp32Datv::WaitFor(const std::string &prefix, double seconds, std::string &line)
{
    auto t0 = Clock::now();
    while (Secs(t0) < seconds)
    {
        Poll();
        std::string l;
        while (NextLine(l))
        {
            if (l.compare(0, prefix.size(), prefix) == 0) { line = l; return true; }
            if (l.compare(0, 3, "ERR") == 0) { line = l; return false; }
            if (l.compare(0, 6, "TX END") == 0) Say(l);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    line = "no answer";
    return false;
}

bool Esp32Datv::Open(const std::string &port, std::string &err)
{
    fileMode_ = false;
#ifndef _WIN32
    fileMode_ = port.compare(0, 5, "/dev/") != 0; // tests: write the command and symbol stream to a file
#endif
    if (!link_.Open(port, err)) return false;
    if (fileMode_) { version_ = "file"; return true; }
    std::this_thread::sleep_for(std::chrono::milliseconds(1200)); // opening the port can reset the C3
    std::string line;
    for (int i = 0; i < 8; i++)
    {
        link_.Write(std::vector<uint8_t>{'\n', 'I', 'N', 'F', 'O', '\n'});
        if (WaitFor("ESP32DATV ", 0.7, line)) break;
        line.clear();
    }
    if (line.compare(0, 10, "ESP32DATV ") != 0)
    { err = "no ESP32-DATV firmware answered on " + port + " (flash the board with C3CAST first)"; link_.Close(); return false; }
    std::istringstream ss(line);
    std::string tag, key, value;
    ss >> tag >> version_;
    bool clean = false, pa = false;
    while (ss >> key >> value)
    {
        if (key == "CLEAN_TX") clean = value == "1";
        if (key == "PA_GPIO") pa = true;
    }
    if (!clean || !pa)
    { err = "this ESP32-DATV firmware is too old (needs CLEAN_TX and the PTT output): flash it with a current C3CAST"; link_.Close(); return false; }
    text_.clear();
    return true;
}

void Esp32Datv::Pack(const sfcmplx *s, int len)
{
    std::lock_guard<std::mutex> g(dataMutex_);
    for (int i = 0; i < len; i++)
    {
        uint8_t code = (uint8_t)((s[i].re > 0 ? 1 : 0) | (s[i].im > 0 ? 2 : 0));
        pendByte_ |= (uint8_t)(code << (2 * pendCount_));
        if (++pendCount_ == 4) { data_.push_back(pendByte_); pendByte_ = 0; pendCount_ = 0; }
    }
}

void Esp32Datv::Start(TsQueue *q)
{
    q_ = q;
    run_ = true;
    framer_ = std::thread(&Esp32Datv::FramerLoop, this);
    writer_ = std::thread(&Esp32Datv::WriterLoop, this);
}

// Keep about half a second of symbols ready; pad with null packets when the encoder falls behind.
void Esp32Datv::FramerLoop()
{
    uint8_t pkt[188];
    static const uint8_t nullPkt[188] = {0x47, 0x1F, 0xFF, 0x10};
    const size_t bytesPerSec = (size_t)(baud_ / 4);
    const size_t high = std::max<size_t>(20000, bytesPerSec / 2), low = std::max<size_t>(12000, bytesPerSec * 3 / 10);
    while (run_)
    {
        size_t have;
        { std::lock_guard<std::mutex> g(dataMutex_); have = data_.size(); }
        if (have >= high) { std::this_thread::sleep_for(std::chrono::milliseconds(2)); continue; }
        const uint8_t *src = nullptr;
        if (q_->Pop(pkt)) { src = pkt; packetsSent++; }
        else if (have < low) { src = nullPkt; nullsInserted++; }
        else { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
        int len = Dvbs2AddTsPacket(const_cast<uint8_t *>(src));
        if (len > 0) { Pack(Dvbs2_get_IQ(), len); framesMade++; }
    }
}

bool Esp32Datv::StartTx(std::string &err)
{
    int amp;
    double f;
    { std::lock_guard<std::mutex> g(ctlMutex_); amp = Amp(gainDb_); f = cfg_.frequency; }
    char cmd[256];
    snprintf(cmd, sizeof cmd, "QPSKT %.3f %d %d %d %d %d %d %d %d %d %d\n",
             f / 1e6, cfg_.symbolRate, sps_, amp, 86400, 0, target_, cfg_.dcI, cfg_.dcQ, cfg_.iqGain, cfg_.iqPhase);
    std::string pa = "PA 1\n", line;
    if (!link_.Write(std::vector<uint8_t>(pa.begin(), pa.end()))) { err = "write failed"; return false; }
    if (!fileMode_ && !WaitFor("OK PA 1", 3, line)) { err = "PTT output not confirmed (" + line + ")"; return false; }
    std::string c(cmd);
    if (!link_.Write(std::vector<uint8_t>(c.begin(), c.end()))) { err = "write failed"; return false; }
    if (!fileMode_ && !WaitFor("OK QPSKT", 8, line)) { err = line; return false; }
    sentSinceReport_ = 0;
    boardFill = 0;
    streaming_ = true;
    starts++;
    Say(fileMode_ ? std::string("TX START ") + cmd : line);
    return true;
}

// Stop sending: the CLEAN_TX firmware switches RF off when its buffer is empty and says TX END.
void Esp32Datv::EndTx()
{
    auto t0 = Clock::now();
    while (!fileMode_ && streaming_ && Secs(t0) < 3) { Poll(); DrainLines(); std::this_thread::sleep_for(std::chrono::milliseconds(10)); }
    streaming_ = false;
    std::string pa = "PA 0\n", line;
    link_.Write(std::vector<uint8_t>(pa.begin(), pa.end()));
    if (!fileMode_) WaitFor("OK PA 0", 2, line);
}

void Esp32Datv::WriterLoop()
{
    std::vector<uint8_t> chunk;
    auto idleClock = Clock::now();
    double idleCredit = 0;
    const double bytesPerSec = baud_ / 4.0;
    while (run_)
    {
        bool want;
        { std::lock_guard<std::mutex> g(ctlMutex_); want = ptt_; }
        if (restart_ && streaming_) { restart_ = false; EndTx(); }
        if (want && !streaming_)
        {
            std::string err;
            if (!StartTx(err)) { error = err; writeFailed = !fileMode_; break; }
        }
        else if (!want && streaming_) EndTx();

        if (!streaming_)
        {
            // Off air: drop symbols at the real-time rate so the encoder keeps flowing.
            double dt = Secs(idleClock); idleClock = Clock::now();
            idleCredit += dt * bytesPerSec;
            std::lock_guard<std::mutex> g(dataMutex_);
            size_t n = std::min(data_.size(), (size_t)idleCredit);
            data_.erase(data_.begin(), data_.begin() + n);
            idleCredit -= n;
            if (idleCredit > bytesPerSec) idleCredit = bytesPerSec;
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
            if (!fileMode_) { Poll(); DrainLines(); }
            continue;
        }
        idleClock = Clock::now(); idleCredit = 0;

        Poll();
        DrainLines();
        if (!streaming_) { if (want) Say("the board stopped transmitting; restarting"); continue; }
        long todo;
        if (fileMode_)
        {
            // no board to report its fill: pace at the symbol rate
            static Clock::time_point t0 = Clock::now();
            static double sent = 0;
            todo = (long)((Secs(t0) * bytesPerSec - sent) / 2);
            if (todo > 0) sent += todo * 2;
        }
        else todo = target_ - (boardFill + sentSinceReport_);
        if (todo < 32) { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
        size_t nb = 2 * (size_t)std::min<long>(todo, 1024);
        chunk.clear();
        {
            std::lock_guard<std::mutex> g(dataMutex_);
            nb = std::min(nb, data_.size() & ~(size_t)1);
            chunk.assign(data_.begin(), data_.begin() + nb);
            data_.erase(data_.begin(), data_.begin() + nb);
        }
        if (chunk.empty()) { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
        if (!link_.Write(chunk)) { writeFailed = true; break; }
        sentSinceReport_ += (long)(chunk.size() / 2);
        bytesSent += chunk.size();
    }
    if (streaming_) EndTx();
}

void Esp32Datv::SetPtt(bool on) { std::lock_guard<std::mutex> g(ctlMutex_); ptt_ = on; }

// The level and frequency are start parameters: a change restarts the transmission (a short gap).
void Esp32Datv::SetGain(double db)
{
    bool restart;
    { std::lock_guard<std::mutex> g(ctlMutex_); restart = Amp(db) != Amp(gainDb_); gainDb_ = db; }
    if (restart && streaming_) restart_ = true;
}

bool Esp32Datv::SetFrequency(double hz, std::string &err)
{
    double half = baud_ * 0.675;
    if (hz - half < BandLo || hz + half > BandHi) { err = "outside 2300-2450 MHz"; return false; }
    { std::lock_guard<std::mutex> g(ctlMutex_); cfg_.frequency = hz; }
    if (streaming_) restart_ = true;
    return true;
}

void Esp32Datv::Stop()
{
    SetPtt(false);
    run_ = false;
    if (framer_.joinable()) framer_.join();
    if (writer_.joinable()) writer_.join();
    link_.Close();
}
