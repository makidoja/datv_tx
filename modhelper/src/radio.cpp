#include "radio.h"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <thread>

// Pops n samples, padding with zeros on underrun. Applies mute.
static void PullSamples(Radio *r, IqRing *ring, cf *dst, size_t n)
{
    size_t got = ring->Pop(dst, n);
    if (got < n)
    {
        std::fill(dst + got, dst + n, cf(0, 0));
        r->underruns++;
    }
    if (r->muted) std::fill(dst, dst + n, cf(0, 0));
    else
        for (size_t i = 0; i < n; i++) // hard limit, should never trigger with the scaling in core.cpp
        {
            float re = dst[i].real(), im = dst[i].imag();
            if (re > 1.f || re < -1.f || im > 1.f || im < -1.f)
                dst[i] = cf(std::max(-1.f, std::min(1.f, re)), std::max(-1.f, std::min(1.f, im)));
        }
    r->samplesOut += n;
}

// ======================================================================= LimeSDR (LimeSuite)
class LimeRadio : public Radio
{
public:
    const char *Name() override { return "LimeSDR"; }

    bool Open(const RadioConfig &c, std::string &err) override
    {
        cfg_ = c;
        if (!api_.Load(c.driverDirs, err)) return false;
        lms_info_str_t list[8];
        int n = api_.GetDeviceList(list);
        if (n < 1) { err = "no LimeSDR found"; return false; }
        if (api_.Open(&dev_, list[0], nullptr) != 0) return Fail(err, "LMS_Open");
        if (api_.Init(dev_) != 0) return Fail(err, "LMS_Init");
        if (api_.EnableChannel(dev_, true, 0, true) != 0) return Fail(err, "LMS_EnableChannel");
        if (api_.SetSampleRate(dev_, c.sampleRate, 0) != 0) return Fail(err, "LMS_SetSampleRate");
        if (api_.SetLOFrequency(dev_, true, 0, c.frequency) != 0) return Fail(err, "LMS_SetLOFrequency");
        // LimeSDR Mini TX paths: 1 = BAND1 (< 2 GHz), 2 = BAND2 (> 2 GHz)
        api_.SetAntenna(dev_, true, 0, c.frequency > 2e9 ? 2 : 1);
        api_.SetLPFBW(dev_, true, 0, std::max(5e6, c.bandwidth * 1.2));
        api_.SetNormalizedGain(dev_, true, 0, 0.0);
        if (c.calibrate && api_.Calibrate(dev_, true, 0, std::max(2.5e6, c.bandwidth), 0) != 0)
            fprintf(stdout, "WARN Lime calibration failed: %s\n", api_.GetLastErrorMessage());
        return true;
    }

    bool Start(IqRing *ring, std::string &err) override
    {
        ring_ = ring;
        memset(&stream_, 0, sizeof(stream_));
        stream_.isTx = true;
        stream_.channel = 0;
        stream_.fifoSize = (uint32_t)std::max(256.0 * 1024, cfg_.sampleRate * 0.1);
        stream_.throughputVsLatency = 0.5f;
        stream_.dataFmt = lms_stream_t::LMS_FMT_F32;
        if (api_.SetupStream(dev_, &stream_) != 0) return Fail(err, "LMS_SetupStream");
        if (api_.StartStream(&stream_) != 0) return Fail(err, "LMS_StartStream");
        ApplyGain();
        run_ = true;
        th_ = std::thread([this] { Run(); });
        return true;
    }

    void Run()
    {
        std::vector<cf> block(8192);
        lms_stream_meta_t meta;
        memset(&meta, 0, sizeof(meta));
        while (run_)
        {
            PullSamples(this, ring_, block.data(), block.size());
            api_.SendStream(&stream_, block.data(), block.size(), &meta, 1000);
        }
    }

    void Stop() override
    {
        run_ = false;
        if (th_.joinable()) th_.join();
        if (dev_)
        {
            api_.SetNormalizedGain(dev_, true, 0, 0.0);
            api_.StopStream(&stream_);
            api_.DestroyStream(dev_, &stream_);
            api_.EnableChannel(dev_, true, 0, false);
            api_.Close(dev_);
            dev_ = nullptr;
        }
    }

    bool SetFrequency(double hz, std::string &err) override
    {
        cfg_.frequency = hz;
        if (api_.SetLOFrequency(dev_, true, 0, hz) != 0) return Fail(err, "LMS_SetLOFrequency");
        api_.SetAntenna(dev_, true, 0, hz > 2e9 ? 2 : 1);
        return true;
    }

    bool SetGainDb(double db, std::string &err) override
    {
        cfg_.gainDb = db;
        ApplyGain();
        return true;
    }

    void SetMute(bool m) override
    {
        muted = m;
        ApplyGain();
    }

    std::string Status() override
    {
        if (!dev_) return "";
        lms_stream_status_t s;
        memset(&s, 0, sizeof(s));
        api_.GetStreamStatus(&stream_, &s);
        char buf[160];
        snprintf(buf, sizeof(buf), "fifo=%u/%u hw_underrun=%u dropped=%u", s.fifoFilledCount, s.fifoSize, s.underrun, s.droppedPackets);
        return buf;
    }

private:
    void ApplyGain()
    {
        if (!dev_) return;
        // App gain 0 dB = full output, -50 dB = minimum. Muted = minimum gain.
        double norm = muted ? 0.0 : std::min(1.0, std::max(0.0, (cfg_.gainDb + 50.0) / 50.0));
        api_.SetNormalizedGain(dev_, true, 0, norm);
    }

    bool Fail(std::string &err, const char *what)
    {
        err = std::string(what) + ": " + (api_.GetLastErrorMessage ? api_.GetLastErrorMessage() : "error");
        return false;
    }

    LimeApi api_;
    RadioConfig cfg_;
    lms_device_t *dev_ = nullptr;
    lms_stream_t stream_;
    IqRing *ring_ = nullptr;
    std::thread th_;
    std::atomic<bool> run_{false};
};

// ======================================================================= HackRF One (libhackrf)
class HackrfRadio : public Radio
{
public:
    const char *Name() override { return "HackRF"; }

    bool Open(const RadioConfig &c, std::string &err) override
    {
        cfg_ = c;
        if (!api_.Load(c.driverDirs, err)) return false;
        int r = api_.init();
        if (r != 0) return Fail(err, "hackrf_init", r);
        inited_ = true;
        r = api_.open(&dev_);
        if (r != 0) { err = std::string("no HackRF found (") + api_.error_name(r) + "). Check the WinUSB driver (Zadig)."; return false; }
        if ((r = api_.set_sample_rate(dev_, c.sampleRate)) != 0) return Fail(err, "hackrf_set_sample_rate", r);
        uint32_t bw = api_.compute_baseband_filter_bw((uint32_t)std::max(1.75e6, c.bandwidth));
        if ((r = api_.set_baseband_filter_bandwidth(dev_, bw)) != 0) return Fail(err, "hackrf_set_baseband_filter_bandwidth", r);
        if ((r = api_.set_freq(dev_, (uint64_t)c.frequency)) != 0) return Fail(err, "hackrf_set_freq", r);
        api_.set_amp_enable(dev_, c.hackrfAmp ? 1 : 0);
        api_.set_txvga_gain(dev_, 0);
        return true;
    }

    static int Callback(hackrf_transfer *t)
    {
        HackrfRadio *self = static_cast<HackrfRadio *>(t->tx_ctx);
        size_t n = (size_t)t->buffer_length / 2;
        if (self->tmp_.size() < n) self->tmp_.resize(n);
        PullSamples(self, self->ring_, self->tmp_.data(), n);
        int8_t *out = reinterpret_cast<int8_t *>(t->buffer);
        for (size_t i = 0; i < n; i++)
        {
            float re = self->tmp_[i].real() * 127.f, im = self->tmp_[i].imag() * 127.f;
            out[2 * i] = (int8_t)std::max(-127.f, std::min(127.f, re));
            out[2 * i + 1] = (int8_t)std::max(-127.f, std::min(127.f, im));
        }
        t->valid_length = t->buffer_length;
        return 0;
    }

    bool Start(IqRing *ring, std::string &err) override
    {
        ring_ = ring;
        tmp_.resize(262144);
        int r = api_.start_tx(dev_, &HackrfRadio::Callback, this);
        if (r != 0) return Fail(err, "hackrf_start_tx", r);
        ApplyGain();
        return true;
    }

    void Stop() override
    {
        if (dev_)
        {
            api_.set_txvga_gain(dev_, 0);
            api_.stop_tx(dev_);
            api_.set_amp_enable(dev_, 0);
            api_.close(dev_);
            dev_ = nullptr;
        }
        if (inited_) { api_.exit_(); inited_ = false; }
    }

    bool SetFrequency(double hz, std::string &err) override
    {
        int r = api_.set_freq(dev_, (uint64_t)hz);
        if (r != 0) return Fail(err, "hackrf_set_freq", r);
        cfg_.frequency = hz;
        return true;
    }

    bool SetGainDb(double db, std::string &err) override
    {
        cfg_.gainDb = db;
        ApplyGain();
        return true;
    }

    void SetMute(bool m) override
    {
        muted = m;
        ApplyGain();
    }

private:
    void ApplyGain()
    {
        if (!dev_) return;
        // TX VGA is 0-47 dB. App gain 0 dB = 47, -47 dB or lower = 0.
        int vga = muted ? 0 : (int)std::lround(std::min(47.0, std::max(0.0, 47.0 + cfg_.gainDb)));
        api_.set_txvga_gain(dev_, (uint32_t)vga);
    }

    bool Fail(std::string &err, const char *what, int code)
    {
        err = std::string(what) + ": " + (api_.error_name ? api_.error_name(code) : std::to_string(code));
        return false;
    }

    HackrfApi api_;
    RadioConfig cfg_;
    hackrf_device *dev_ = nullptr;
    bool inited_ = false;
    IqRing *ring_ = nullptr;
    std::vector<cf> tmp_;
};

// ======================================================================= File / null (testing without hardware)
class FileRadio : public Radio
{
public:
    explicit FileRadio(bool realtime) : realtime_(realtime) {}
    const char *Name() override { return "file"; }
    bool Open(const RadioConfig &c, std::string &err) override
    {
        cfg_ = c;
        if (!c.filePath.empty())
        {
            f_ = fopen(c.filePath.c_str(), "wb");
            if (!f_) { err = "cannot open " + c.filePath; return false; }
        }
        return true;
    }
    bool Start(IqRing *ring, std::string &) override
    {
        ring_ = ring;
        run_ = true;
        th_ = std::thread([this] { Run(); });
        return true;
    }
    void Run()
    {
        const size_t n = 8192;
        std::vector<cf> block(n);
        auto t0 = std::chrono::steady_clock::now();
        uint64_t sent = 0;
        while (run_)
        {
            if (realtime_)
            {
                double due = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count() * cfg_.sampleRate;
                if ((double)sent > due) { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
            }
            else if (ring_->Size() < n) { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
            PullSamples(this, ring_, block.data(), n);
            if (f_) fwrite(block.data(), sizeof(cf), n, f_);
            sent += n;
        }
    }
    void Stop() override
    {
        run_ = false;
        if (th_.joinable()) th_.join();
        if (f_) fclose(f_);
        f_ = nullptr;
    }
    bool SetFrequency(double hz, std::string &) override { cfg_.frequency = hz; return true; }
    bool SetGainDb(double db, std::string &) override { cfg_.gainDb = db; return true; }

private:
    bool realtime_;
    RadioConfig cfg_;
    FILE *f_ = nullptr;
    IqRing *ring_ = nullptr;
    std::thread th_;
    std::atomic<bool> run_{false};
};

// ======================================================================= Pluto, standard (ADI) firmware via libiio
// The DVB-S2 signal is made here and streamed as IQ to the AD9363, like the LimeSDR / HackRF.
// PTT = TX LO power-down (out_altvoltage1_TX_LO_powerdown), power = out_voltage0_hardwaregain (-89.75..0 dB).
class PlutoRadio : public Radio
{
public:
    const char *Name() override { return "Pluto"; }

    bool Open(const RadioConfig &c, std::string &err) override
    {
        cfg_ = c;
        if (!api_.Load(c.driverDirs, err)) return false;
        std::string uri = c.uri.empty() ? "ip:192.168.2.1" : c.uri;
        if (uri == "usb" || uri == "usb:")
        {
            uri.clear();
            iio_scan_context *sc = api_.create_scan_context("usb", 0);
            iio_context_info **list = nullptr;
            std::ptrdiff_t n = sc ? api_.scan_context_get_info_list(sc, &list) : -1;
            for (std::ptrdiff_t i = 0; i < n && uri.empty(); i++)
            {
                std::string d = api_.context_info_get_description(list[i]) ? api_.context_info_get_description(list[i]) : "";
                if (d.find("PlutoSDR") != std::string::npos || d.find("ADALM") != std::string::npos || n == 1)
                    uri = api_.context_info_get_uri(list[i]);
            }
            if (list) api_.context_info_list_free(list);
            if (sc) api_.scan_context_destroy(sc);
            if (uri.empty()) { err = "no Pluto found on USB (install ADI's PlutoSDR drivers, or use its IP address)"; return false; }
        }
        ctx_ = api_.create_context_from_uri(uri.c_str());
        if (!ctx_) { err = "no Pluto answered at " + uri + " (check the IP address / USB cable)"; return false; }
        phy_ = api_.context_find_device(ctx_, "ad9361-phy");
        tx_ = api_.context_find_device(ctx_, "cf-ad9361-dds-core-lpc");
        if (!phy_ || !tx_) { err = "this doesn't look like a Pluto (no AD936x devices at " + uri + ")"; return false; }
        txCh_ = api_.device_find_channel(phy_, "voltage0", true);
        lo_ = api_.device_find_channel(phy_, "altvoltage1", true);
        i_ = api_.device_find_channel(tx_, "voltage0", true);
        q_ = api_.device_find_channel(tx_, "voltage1", true);
        if (!txCh_ || !lo_ || !i_ || !q_) { err = "Pluto TX channels not found"; return false; }
        api_.channel_attr_write_longlong(lo_, "powerdown", 1); // off air until PTT
        int r;
        if ((r = api_.channel_attr_write_longlong(txCh_, "sampling_frequency", (long long)c.sampleRate)) < 0)
        { err = "the Pluto refused " + std::to_string((long long)c.sampleRate) + " S/s (" + std::to_string(r) + ")"; return false; }
        api_.channel_attr_write_longlong(txCh_, "rf_bandwidth", (long long)std::max(200e3, c.bandwidth * 1.2));
        if ((r = api_.channel_attr_write_longlong(lo_, "frequency", (long long)c.frequency)) < 0)
        { err = "the Pluto refused " + std::to_string((long long)c.frequency) + " Hz (stock range 325-3800 MHz)"; return false; }
        ApplyGain();
        uri_ = uri;
        return true;
    }

    bool Start(IqRing *ring, std::string &err) override
    {
        ring_ = ring;
        api_.channel_enable(i_);
        api_.channel_enable(q_);
        api_.device_set_kernel_buffers_count(tx_, 4);
        // ~20 ms per buffer keeps the latency low and the USB/network link busy
        block_ = (size_t)std::max(16384.0, cfg_.sampleRate * 0.02);
        buf_ = api_.device_create_buffer(tx_, block_, false);
        if (!buf_) { err = "could not create the Pluto TX buffer"; return false; }
        tmp_.resize(block_);
        run_ = true;
        th_ = std::thread([this] { Loop(); });
        ApplyGain();
        return true;
    }

    void Loop()
    {
        while (run_)
        {
            PullSamples(this, ring_, tmp_.data(), block_);
            char *p = (char *)api_.buffer_start(buf_);
            char *end = (char *)api_.buffer_end(buf_);
            std::ptrdiff_t step = api_.buffer_step(buf_);
            size_t i = 0;
            for (; p < end && i < block_; p += step, i++)
            {
                // 12-bit DAC, MSB-aligned in 16 bits
                int16_t *s = (int16_t *)p;
                s[0] = (int16_t)std::lround(tmp_[i].real() * 2047.f) << 4;
                s[1] = (int16_t)std::lround(tmp_[i].imag() * 2047.f) << 4;
            }
            if (api_.buffer_push(buf_) < 0) { failed = true; break; }
        }
    }

    void Stop() override
    {
        run_ = false;
        if (th_.joinable()) th_.join();
        if (ctx_)
        {
            if (lo_) api_.channel_attr_write_longlong(lo_, "powerdown", 1);
            if (txCh_) api_.channel_attr_write_double(txCh_, "hardwaregain", -89.75);
            if (buf_) api_.buffer_destroy(buf_);
            api_.context_destroy(ctx_);
            ctx_ = nullptr; buf_ = nullptr;
        }
    }

    bool SetFrequency(double hz, std::string &err) override
    {
        cfg_.frequency = hz;
        if (lo_ && api_.channel_attr_write_longlong(lo_, "frequency", (long long)hz) < 0) { err = "the Pluto refused the frequency"; return false; }
        return true;
    }

    bool SetGainDb(double db, std::string &) override { cfg_.gainDb = db; ApplyGain(); return true; }

    void SetMute(bool m) override
    {
        muted = m;
        ApplyGain();
    }

    std::string Status() override { return failed ? "link=failed" : "uri=" + uri_; }

    std::atomic<bool> failed{false};

private:
    void ApplyGain()
    {
        if (!txCh_) return;
        double g = std::max(-89.75, std::min(0.0, std::round(cfg_.gainDb * 4) / 4));
        api_.channel_attr_write_double(txCh_, "hardwaregain", muted ? -89.75 : g);
        if (lo_) api_.channel_attr_write_longlong(lo_, "powerdown", muted ? 1 : 0);
    }

    IioApi api_;
    RadioConfig cfg_;
    std::string uri_;
    iio_context *ctx_ = nullptr;
    iio_device *phy_ = nullptr, *tx_ = nullptr;
    iio_channel *txCh_ = nullptr, *lo_ = nullptr, *i_ = nullptr, *q_ = nullptr;
    iio_buffer *buf_ = nullptr;
    IqRing *ring_ = nullptr;
    size_t block_ = 0;
    std::vector<cf> tmp_;
    std::thread th_;
    std::atomic<bool> run_{false};
};

std::unique_ptr<Radio> Radio::Create(const std::string &kind)
{
    if (kind == "lime") return std::unique_ptr<Radio>(new LimeRadio());
    if (kind == "hackrf") return std::unique_ptr<Radio>(new HackrfRadio());
    if (kind == "pluto") return std::unique_ptr<Radio>(new PlutoRadio());
    if (kind == "file") return std::unique_ptr<Radio>(new FileRadio(false));
    if (kind == "null") return std::unique_ptr<Radio>(new FileRadio(true));
    return nullptr;
}
