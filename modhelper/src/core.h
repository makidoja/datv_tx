// DVB-S / DVB-S2 modulation pipeline: TS packets -> libdvbmod symbols -> RRC interpolation -> IQ ring.
#pragma once
#include <atomic>
#include <complex>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

typedef std::complex<float> cf;

struct ModConfig
{
    bool dvbs2 = true;
    int constellation = 0;   // libdvbmod M_QPSK..M_32APSK
    int codeRate = 3;        // libdvbmod CR_x_y
    bool pilots = false;
    bool shortFrames = false;
    double rollOff = 0.20;   // 0.20 / 0.25 / 0.35
    int symbolRate = 333000; // symbols/s
    int upsample = 4;        // output samples per symbol
};

/// Lock-free single producer / single consumer ring of complex samples.
class IqRing
{
public:
    explicit IqRing(size_t capacity) : buf_(capacity), cap_(capacity) {}
    size_t Size() const { return w_.load(std::memory_order_acquire) - r_.load(std::memory_order_acquire); }
    size_t Free() const { return cap_ - Size(); }
    size_t Capacity() const { return cap_; }
    void Push(const cf *src, size_t n)
    {
        size_t w = w_.load(std::memory_order_relaxed);
        for (size_t i = 0; i < n; i++) buf_[(w + i) % cap_] = src[i];
        w_.store(w + n, std::memory_order_release);
    }
    /// Pops up to n samples; returns how many were available.
    size_t Pop(cf *dst, size_t n)
    {
        size_t r = r_.load(std::memory_order_relaxed);
        size_t avail = w_.load(std::memory_order_acquire) - r;
        if (n > avail) n = avail;
        for (size_t i = 0; i < n; i++) dst[i] = buf_[(r + i) % cap_];
        r_.store(r + n, std::memory_order_release);
        return n;
    }

private:
    std::vector<cf> buf_;
    size_t cap_;
    std::atomic<size_t> w_{0}, r_{0};
};

/// Thread-safe FIFO of 188-byte TS packets.
class TsQueue
{
public:
    explicit TsQueue(size_t maxPackets) : max_(maxPackets) {}
    void Push(const uint8_t *pkt)
    {
        std::lock_guard<std::mutex> g(m_);
        if (q_.size() >= max_) { q_.pop_front(); dropped++; }
        std::vector<uint8_t> v(pkt, pkt + 188);
        q_.push_back(std::move(v));
    }
    bool Pop(uint8_t *out)
    {
        std::lock_guard<std::mutex> g(m_);
        if (q_.empty()) return false;
        std::copy(q_.front().begin(), q_.front().end(), out);
        q_.pop_front();
        return true;
    }
    size_t Count()
    {
        std::lock_guard<std::mutex> g(m_);
        return q_.size();
    }
    std::atomic<uint64_t> dropped{0};

private:
    std::mutex m_;
    std::deque<std::vector<uint8_t>> q_;
    size_t max_;
};

/// Root-raised-cosine polyphase interpolator (symbols in, upsample x samples out).
class RrcInterpolator
{
public:
    void Init(double rollOff, int upsample, int spanSymbols = 12);
    /// Appends `upsample` output samples for one input symbol.
    void Process(cf sym, cf *out);

private:
    int L_ = 1, span_ = 12;
    std::vector<float> taps_;   // taps_[phase * span + k]
    std::vector<cf> hist_;
    int pos_ = 0;
};

/// Pulls TS from the queue (or null packets), modulates and fills the ring.
class Modulator
{
public:
    /// Returns the useful TS bitrate (bit/s) for the configuration, or <= 0 on error.
    int Init(const ModConfig &cfg);
    void Start(TsQueue *q, IqRing *ring, size_t lowWater);
    void Stop();

    std::atomic<uint64_t> nullsInserted{0};
    std::atomic<uint64_t> packetsSent{0};
    float peak = 0;

private:
    void Run();
    size_t AddPacket(const uint8_t *pkt, cf *out);

    ModConfig cfg_;
    RrcInterpolator rrc_;
    TsQueue *q_ = nullptr;
    IqRing *ring_ = nullptr;
    size_t lowWater_ = 0;
    std::thread th_;
    std::atomic<bool> run_{false};
    std::vector<cf> scratch_;
};

/// Receives the transport stream by UDP (as ffmpeg sends it to a Pluto).
class UdpTsReceiver
{
public:
    bool Start(const std::string &bindIp, int port, TsQueue *q, std::string &err);
    void Stop();
    std::atomic<uint64_t> bytes{0};
    std::atomic<uint64_t> syncErrors{0};

private:
    void Run();
    intptr_t sock_ = -1;
    TsQueue *q_ = nullptr;
    std::thread th_;
    std::atomic<bool> run_{false};
    std::vector<uint8_t> carry_;
};

int ParseCodeRate(const std::string &fec);
int ParseConstellation(const std::string &c);
