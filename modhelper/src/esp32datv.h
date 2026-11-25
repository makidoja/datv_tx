#pragma once
#include "core.h"
#include "libdvbmod.h"
#include "muntjac.h" // MuntjacLink: the shared USB serial port (Windows COM / Linux tty / test file)
#include <atomic>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

/// <summary>
/// ESP32-C3 with SP8ESA's ESP32-DATV firmware (as flashed by HB9IIU's C3CAST): the chip's own Wi-Fi radio
/// transmits DVB-S2 on 13 cm. This side makes the DVB-S2 QPSK PL frames (libdvbmod) and streams the symbols
/// over the board's native USB serial port. Protocol, from the firmware's documented command set:
///   INFO                         -> "ESP32DATV <ver> KEY VALUE ..." (PA_GPIO, CLEAN_TX ...)
///   PA 0|1                       -> "OK PA n GPIO g"   (PTT output, high while RF is on)
///   QPSKT f_MHz baud sps amp seconds ifm target dcI dcQ g phi -> "OK QPSKT ..." then raw symbols:
///     2 bits per symbol (bit 0 = I positive, bit 1 = Q positive), 4 symbols per byte, first in the low bits.
///   The board sends 4-byte buffer reports (0xB7, fill_lo, fill_hi, underruns; fill in 2-byte pairs) and
///   "TX END ..." when the stream stops (CLEAN_TX firmware switches RF off once its buffer drains).
/// </summary>
struct Esp32Config
{
    double frequency = 2405.25e6;
    int symbolRate = 333000;
    std::string fec = "1/2";
    bool pilots = false;
    bool shortFrames = false;
    int maxAmp = 100;       // DAC amplitude at 0 dB on the power slider (firmware 1..480)
    int minAmp = 15;        // below about 15 the carrier leak shows
    int dcI = 0, dcQ = 0;   // DC trims in 1/16 DAC code
    int iqGain = 10000;     // Q gain x 1e4
    int iqPhase = 0;        // Q phase in 1e-3 degree
};

class Esp32Datv
{
public:
    int Init(const Esp32Config &c, std::string &err);
    bool Open(const std::string &port, std::string &err);
    void Start(TsQueue *q);
    void Stop();
    void SetPtt(bool on);
    void SetGain(double db);
    bool SetFrequency(double hz, std::string &err);
    std::vector<std::string> TakeMessages();

    static int Sps(int baud);
    static double OutputBaud(int baud, int sps);
    int Amp(double gainDb) const;

    const std::string &Version() const { return version_; }
    int SpsUsed() const { return sps_; }
    double BaudUsed() const { return baud_; }
    bool Ptt() const { return ptt_; }
    bool OnAir() const { return streaming_; }
    double GainDb() const { return gainDb_; }

    std::atomic<uint64_t> nullsInserted{0}, packetsSent{0}, framesMade{0}, bytesSent{0}, starts{0};
    std::atomic<int> boardFill{0}, boardUnderruns{0};
    std::atomic<bool> writeFailed{false};
    std::string error;

private:
    void FramerLoop();
    void WriterLoop();
    void Pack(const sfcmplx *s, int len);
    void Poll();
    bool NextLine(std::string &line);
    void DrainLines();
    bool WaitFor(const std::string &prefix, double seconds, std::string &line);
    bool StartTx(std::string &err);
    void EndTx();
    void Say(const std::string &m);

    Esp32Config cfg_;
    MuntjacLink link_;
    TsQueue *q_ = nullptr;
    bool fileMode_ = false;
    int sps_ = 16, bitrate_ = 0;
    double baud_ = 0;
    std::string version_, text_, rx_;
    std::vector<std::string> messages_;
    std::mutex msgMutex_, dataMutex_, ctlMutex_;
    std::deque<uint8_t> data_;        // packed symbols waiting for the board
    uint8_t pendByte_ = 0;
    int pendCount_ = 0;               // symbols already in pendByte_
    std::thread framer_, writer_;
    std::atomic<bool> run_{false}, streaming_{false}, restart_{false};
    bool ptt_ = false;
    double gainDb_ = -20;
    long sentSinceReport_ = 0;        // pairs
    int target_ = 3000;               // pairs the board should hold
};
