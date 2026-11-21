## DATV TX (M0CKE) v0.1.0 beta

The first beta of a Windows DATV transmitter for QO-100 and terrestrial use. It's a GUI rewrite of
DL5OCD's **DATV-NotSoEasy** batch scripts in the OpenTuner M0CKE style: the same encoder settings and
bitrate maths, without the console menus.

### Downloads
- **DatvTx-M0CKE-0.1.0-beta-Setup.exe**: the installer. It needs no admin rights and installs to your user folder.
- **DatvTx-M0CKE-0.1.0-beta-portable.zip**: the same files, to unzip and run.

Both include FFmpeg (x264, x265, H.266 VVC, AV1, NVENC, AMD AMF, Intel QSV), the LimeSDR Mini library
(LimeSuite 23.11) and the HackRF library (libhackrf with libusb). Windows 10/11 64-bit, .NET Framework 4.7.2+.

### Transmitters
- **ADALM-Pluto, F5OEO MQTT firmware**: full control (frequency, SR, FEC, mode, power, PTT, roll-off, digital gain). *Tested on air.*
- **ADALM-Pluto, older F5OEO firmware**: the app sends the stream; you set up the radio in its web page.
- **LimeSDR Mini / HackRF One**: built-in DVB-S/S2 modulator (`datvtx_mod.exe`, F5OEO libdvbmod)
  with RRC shaping. Frequency, power and PTT change live. *Not yet tested on air.*
- **Local test**: encode to a local stream and watch it, with no radio.

### Features
- Tabs: DVB, Source, Memories and Status, plus a transmit panel with traffic lights and a power slider
  from −50 to 0 dB. The power limit is in Settings.
- **QO-100 wideband spectrum** (BATC live feed) with the BATC band plan rows (125 / 333 / 500 KS / 1 MS).
  Click a channel to set the TX frequency and SR.
- **Level relative to the beacon (dBb)**, with a 0 dBb beacon line and a warning when you're at or above the beacon.
- **Compact mode**: just the transmit panel in a small window that stays on top.
- Named **memories** for complete set-ups.
- CPU and GPU encoders with a speed preset, and a warning when the encoder can't keep up.
- Safety: START keeps the carrier muted until you press PTT. The radio is muted if the encoder stops,
  and the LimeSDR/HackRF modulator stops transmitting if the app closes or crashes.

### Before you start
- **HackRF One**: install the **WinUSB** driver with Zadig (*Options → List All Devices → HackRF One*).
  Check it with `hackrf_info.exe` in the install folder.
- **LimeSDR Mini**: needs the FTDI **D3XX** (FT601) USB driver.
- **OBS**: *Tools → Start Virtual Camera*, and route the audio to the OBS audio device or a virtual cable.
- For your first transmission on a new radio, use a dummy load and watch it on a MiniTiouner.

### Known limitations (beta)
- LimeSDR Mini and HackRF output, and the AMD/NVIDIA/Intel GPU encoders, have not yet been tested
  on real hardware. Please report results.
- Changing SR, FEC or mode on the LimeSDR/HackRF needs a STOP and START.
- dBb uses the BATC spectrum scale (power density compared with the beacon) and is smoothed. Allow a second after a change.
- GSE/IP mode from DATV-NotSoEasy is not included.

### Credits
- **DL5OCD**: DATV-NotSoEasy, the original logic and parameters.
- **F5OEO**: Pluto DATV firmware, MQTT interface and libdvbmod (reworked from G4GUO's DATV Express).
- **BATC**: QO-100 wideband spectrum feed and band plan.
- FFmpeg, LimeSuite (Lime Microsystems) and libhackrf (Great Scott Gadgets) are included under their own licences.
