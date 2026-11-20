// datvtx_mod - DVB-S/S2 modulator for LimeSDR and HackRF, driven by DATV TX (M0CKE).
//
// Transport stream in : UDP (ffmpeg sends to 127.0.0.1:<port>, as it would to a Pluto)
// Control in          : text lines on stdin  (ptt 0|1, gain <dB>, freq <Hz>, quit)
// Status out          : text lines on stdout (READY/STAT/ERROR/BITRATE)
// If stdin closes (the app exits or crashes) the carrier is stopped.
//
// Modulation: libdvbmod by F5OEO (reworked from G4GUO's DATV Express).

#include "core.h"
#include "libdvbmod.h"
#include "radio.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>

static std::mutex outMutex;
static void Out(const std::string &s)
{
    std::lock_guard<std::mutex> g(outMutex);
    fprintf(stdout, "%s\n", s.c_str());
    fflush(stdout);
}

static void Usage()
{
    fprintf(stderr,
            "datvtx_mod --radio lime|hackrf|file|null --freq HZ --sr SYMBOLS_PER_S\n"
            "           [--standard dvbs2|dvbs] [--const qpsk|8psk|16apsk|32apsk] [--fec 2/3]\n"
            "           [--pilots] [--short] [--rolloff 0.20|0.25|0.35] [--upsample N]\n"
            "           [--gain DB] [--amp] [--ptt] [--port 10000] [--bind 127.0.0.1]\n"
            "           [--driver-dir DIR] [--no-cal] [--out FILE] [--seconds N] [--bitrate]\n");
}

static int AutoUpsample(const std::string &radio, int sr)
{
    double minRate = radio == "hackrf" ? 2.0e6 : 1.0e6; // HackRF needs >= 2 MS/s
    int L = (int)std::ceil(minRate / sr);
    if (L < 2) L = 2;
    if (L > 64) L = 64;
    return L;
}

int main(int argc, char **argv)
{
    setvbuf(stdout, nullptr, _IONBF, 0);

    std::string radioKind = "null", standard = "dvbs2", constel = "qpsk", fec = "1/2", bindIp = "127.0.0.1", outFile;
    double freq = 2403.25e6, gain = -20, rolloff = 0.20;
    int sr = 333000, upsample = 0, port = 10000, seconds = 0;
    bool pilots = false, shortFrames = false, amp = false, ptt = false, bitrateOnly = false, cal = true;
    std::vector<std::string> driverDirs;

    for (int i = 1; i < argc; i++)
    {
        std::string a = argv[i];
        auto next = [&](void) -> std::string {
            if (i + 1 >= argc) { Usage(); exit(2); }
            return argv[++i];
        };
        if (a == "--radio") radioKind = next();
        else if (a == "--freq") freq = atof(next().c_str());
        else if (a == "--sr") sr = atoi(next().c_str());
        else if (a == "--standard") standard = next();
        else if (a == "--const") constel = next();
        else if (a == "--fec") fec = next();
        else if (a == "--pilots") pilots = true;
        else if (a == "--short") shortFrames = true;
        else if (a == "--rolloff") rolloff = atof(next().c_str());
        else if (a == "--upsample") upsample = atoi(next().c_str());
        else if (a == "--gain") gain = atof(next().c_str());
        else if (a == "--amp") amp = true;
        else if (a == "--ptt") ptt = true;
        else if (a == "--port") port = atoi(next().c_str());
        else if (a == "--bind") bindIp = next();
        else if (a == "--driver-dir") driverDirs.push_back(next());
        else if (a == "--no-cal") cal = false;
        else if (a == "--out") outFile = next();
        else if (a == "--seconds") seconds = atoi(next().c_str());
        else if (a == "--bitrate") bitrateOnly = true;
        else { Usage(); return 2; }
    }

    ModConfig mc;
    mc.dvbs2 = standard != "dvbs";
    mc.constellation = ParseConstellation(constel);
    mc.codeRate = ParseCodeRate(fec);
    mc.pilots = pilots;
    mc.shortFrames = shortFrames;
    mc.rollOff = rolloff;
    mc.symbolRate = sr;
    mc.upsample = upsample > 0 ? upsample : AutoUpsample(radioKind, sr);
    if (mc.constellation < 0 || mc.codeRate < 0 || sr < 10000 || sr > 8000000)
    {
        Out("ERROR invalid constellation, FEC or symbol rate");
        return 2;
    }
    if (!mc.dvbs2 && (mc.constellation != 0))
    {
        Out("ERROR DVB-S supports QPSK only");
        return 2;
    }

    Modulator mod;
    int bitrate = mod.Init(mc);
    if (bitrate <= 0)
    {
        Out("ERROR this constellation / FEC combination is not valid");
        return 2;
    }
    if (bitrateOnly)
    {
        Out("BITRATE " + std::to_string(bitrate));
        return 0;
    }

    double rate = (double)sr * mc.upsample;
    RadioConfig rc;
    rc.frequency = freq;
    rc.sampleRate = rate;
    rc.bandwidth = sr * (1.0 + (mc.dvbs2 ? rolloff : 0.35));
    rc.gainDb = gain;
    rc.hackrfAmp = amp;
    rc.calibrate = cal;
    rc.driverDirs = driverDirs;
    rc.filePath = outFile;
#ifdef _WIN32
    rc.driverDirs.push_back("C:/Program Files/PothosSDR/bin");
#endif

    auto radio = Radio::Create(radioKind);
    if (!radio) { Out("ERROR unknown radio " + radioKind); return 2; }

    std::string err;
    if (!radio->Open(rc, err)) { Out("ERROR " + err); return 1; }

    // Ring: 0.5 s of samples; the modulator keeps at least 0.15 s queued (padding with null packets).
    IqRing ring((size_t)std::max(rate * 0.5, 200000.0));
    TsQueue tsq(20000);
    UdpTsReceiver udp;
    if (radioKind != "file" || port > 0)
    {
        if (!udp.Start(bindIp, port, &tsq, err)) { Out("ERROR " + err); radio->Stop(); return 1; }
    }
    mod.Start(&tsq, &ring, (size_t)(rate * 0.15));

    // let the modulator fill the ring before the radio starts pulling
    for (int i = 0; i < 200 && ring.Size() < (size_t)(rate * 0.15); i++)
        std::this_thread::sleep_for(std::chrono::milliseconds(5));

    radio->SetMute(!ptt);
    if (!radio->Start(&ring, err))
    {
        Out("ERROR " + err);
        mod.Stop();
        udp.Stop();
        radio->Stop();
        return 1;
    }

    {
        std::ostringstream ss;
        ss << "READY radio=" << radio->Name() << " rate=" << (long long)rate << " upsample=" << mc.upsample
           << " bitrate=" << bitrate << " udp=" << bindIp << ":" << port;
        Out(ss.str());
    }

    std::atomic<bool> quit{false};
    std::thread control([&] {
        std::string line;
        while (!quit && std::getline(std::cin, line))
        {
            std::istringstream ss(line);
            std::string cmd;
            ss >> cmd;
            std::string e;
            if (cmd == "ptt")
            {
                int v = 0;
                ss >> v;
                radio->SetMute(v == 0);
                Out(std::string("PTT ") + (v ? "on" : "off"));
            }
            else if (cmd == "gain")
            {
                double g = gain;
                ss >> g;
                if (radio->SetGainDb(g, e)) Out("GAIN " + std::to_string(g));
                else Out("ERROR " + e);
            }
            else if (cmd == "freq")
            {
                double f = freq;
                ss >> f;
                if (radio->SetFrequency(f, e)) Out("FREQ " + std::to_string((long long)f));
                else Out("ERROR " + e);
            }
            else if (cmd == "quit") break;
        }
        quit = true; // stdin closed or quit: stop transmitting
    });
    control.detach();

    auto start = std::chrono::steady_clock::now();
    while (!quit)
    {
        std::this_thread::sleep_for(std::chrono::seconds(1));
        double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        std::ostringstream ss;
        ss << "STAT ts_kbps=" << (long long)(udp.bytes * 8 / 1000 / std::max(1.0, elapsed))
           << " queue=" << tsq.Count() << " ring=" << (int)(100.0 * ring.Size() / ring.Capacity()) << "%"
           << " nulls=" << mod.nullsInserted << " packets=" << mod.packetsSent << " dropped=" << tsq.dropped
           << " sync_err=" << udp.syncErrors << " underruns=" << radio->underruns << " peak=" << mod.peak
           << (radio->muted ? " ptt=off" : " ptt=on") << " " << radio->Status();
        Out(ss.str());
        if (seconds > 0 && elapsed >= seconds) break;
    }

    radio->SetMute(true);
    radio->Stop();
    mod.Stop();
    udp.Stop();
    Out("STOPPED");
    std::_Exit(0); // the control thread may still be blocked reading stdin
}
