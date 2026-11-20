#pragma once
#include "core.h"
#include "sdr_api.h"
#include <atomic>
#include <memory>
#include <string>
#include <vector>

struct RadioConfig
{
    double frequency = 2403.25e6;
    double sampleRate = 2e6;
    double bandwidth = 2e6;   // occupied bandwidth (SR x (1 + roll-off))
    double gainDb = -20;      // app scale: 0 = maximum, negative = attenuation
    bool hackrfAmp = false;
    bool calibrate = true;
    std::vector<std::string> driverDirs;
    std::string filePath;     // file radio only
};

class Radio
{
public:
    virtual ~Radio() {}
    virtual bool Open(const RadioConfig &c, std::string &err) = 0;
    virtual bool Start(IqRing *ring, std::string &err) = 0;
    virtual void Stop() = 0;
    virtual bool SetFrequency(double hz, std::string &err) = 0;
    virtual bool SetGainDb(double db, std::string &err) = 0;
    virtual void SetMute(bool mute) { muted = mute; }
    virtual std::string Status() { return ""; }
    virtual const char *Name() = 0;

    std::atomic<bool> muted{true};
    std::atomic<uint64_t> underruns{0};
    std::atomic<uint64_t> samplesOut{0};

    static std::unique_ptr<Radio> Create(const std::string &kind);
};
