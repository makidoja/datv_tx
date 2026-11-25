#pragma once
#include "core.h"
#include "libdvbmod.h"
#include <atomic>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

struct MuntjacConfig
{
    double frequency = 2405.25e6;
    int symbolRate = 333333;
    int constellation = 0; // M_QPSK / M_8PSK
    std::string fec = "1/2";
    bool pilots = false;
    bool shortFrames = false;
    double rollOff = 0.20;
};

class MuntjacLink
{
public:
    bool Open(const std::string &port, std::string &err);
    bool Write(const std::vector<uint8_t> &b);
    std::string ReadAvailable();
    void Close();

private:
    void *handle_ = nullptr;
    FILE *file_ = nullptr; // test mode: byte stream to a file
    int fd_ = -1;          // Linux serial port (/dev/ttyACM*)
};

class Muntjac
{
public:
    int Init(const MuntjacConfig &c, std::string &err);
    bool Open(const std::string &port, const std::vector<std::string> &calDirs, std::string &err);
    void Start(TsQueue *q);
    void Stop();
    void SetPtt(bool on);
    void SetGain(double db);
    bool SetFrequency(double hz, std::string &err);
    std::vector<std::string> TakeMessages();
    static int Power(double gainDb);

    const std::string &Serial() const { return serial_; }
    const std::string &Version() const { return version_; }
    const std::string &CalibrationFile() const { return calFile_; }
    bool Ptt() const { return ptt_; }
    double GainDb() const { return gainDb_; }

    std::atomic<uint64_t> nullsInserted{0}, packetsSent{0}, framesSent{0}, underruns{0};
    std::atomic<bool> writeFailed{false};
    std::string error;

private:
    std::vector<uint8_t> SettingsRecord();
    void LoadCalibration(const std::vector<std::string> &dirs);
    void BuildFrame(const sfcmplx *s, int len, std::vector<uint8_t> &out);
    double FramesPerSecond() const;
    void FramerLoop();
    void WriterLoop();
    void PollMessages();

    MuntjacConfig cfg_;
    MuntjacLink link_;
    int band_ = 1, mjFec_ = 3, bitrate_ = 0;
    int iloc_[2] = {99, 99}, qloc_[2] = {99, 99};
    std::string serial_, version_, calFile_, pending_;
    std::vector<std::string> messages_;
    std::mutex settingsMutex_, framesMutex_, msgMutex_;
    std::deque<std::vector<uint8_t>> frames_;
    std::thread framer_, writer_;
    std::atomic<bool> run_{false}, settingsDirty_{false};
    bool ptt_ = false;
    double gainDb_ = -20;
    uint32_t sequence_ = 0;
    TsQueue *q_ = nullptr;
};
