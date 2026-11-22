## DATV TX (M0CKE) v1.0.0

The first full release of DATV TX: a Windows DATV transmitter for QO-100 and terrestrial use. It's a GUI
rewrite of DL5OCD's **DATV-NotSoEasy** batch scripts in the OpenTuner M0CKE style, with the same encoder
settings and bitrate maths and no console menus. Thanks to everyone who tested the betas.

### New since the last beta (0.1.3)
- The version number is in the title bar.
- **Update check:** at start-up the app looks on GitHub for a newer release and shows a "click to download"
  link if there is one. It says nothing when offline. Settings → Updates turns it off.
- Fixed: the frequency box frame no longer flashes white while transmitting.

### Downloads
- **DatvTx-M0CKE-1.0.0-Setup.exe**: the installer. It needs no admin rights and installs to your user folder.
- **DatvTx-M0CKE-1.0.0-portable.zip**: the same files, to unzip and run.

Both include FFmpeg (x264, x265, H.266 VVC, AV1, NVENC, AMD AMF, Intel QSV), the LimeSDR Mini library
(LimeSuite 23.11) and the HackRF library (libhackrf with libusb). NVIDIA encoding uses a bundled FFmpeg 7.1, which
works with drivers from 470 onwards. Windows 10/11 64-bit, .NET Framework 4.7.2 or later.

### Transmitters
- **ADALM-Pluto, F5OEO MQTT firmware**: full control (frequency, SR, FEC, mode, power, PTT, roll-off, digital gain). *Tested on air.*
- **BATC Muntjac** (70 cm / 13 cm): DVB-S2 QPSK/8PSK, 125–1000 KS over USB; the app finds the COM port itself.
  Frequency, power and PTT change live. *Tested on air on 70 cm and QO-100 (13 cm), 125 and 500 KS.*
- **ADALM-Pluto, older F5OEO firmware**: the app sends the stream; you set up the radio in its web page.
- **LimeSDR Mini / HackRF One**: built-in DVB-S/S2 modulator (`datvtx_mod.exe`, F5OEO libdvbmod)
  with RRC shaping. Frequency, power and PTT change live. *Not yet tested on air.*
- **Local test**: encode to a local stream and watch it, with no radio.

### Features
- Tabs: DVB, Source, Memories and Status, plus a transmit panel with traffic lights, PTT and a power slider
  from −50 to 0 dB. The power limit is in Settings.
- **QO-100 wideband spectrum** (BATC live feed) with the BATC band plan rows (125 / 333 / 500 KS / 1 MS).
  Click a channel to set the TX frequency and SR.
- **Level relative to the beacon (dBb)**, with a 0 dBb beacon line and a warning when you're at or above the beacon.
- **Compact mode** (just the transmit panel) and **ON TOP** to keep the window above other programs.
- Named **memories** for complete set-ups.
- CPU and GPU encoders (NVIDIA, AMD, Intel) with a speed preset. The encoder fps, bitrate and speed show
  current values, with a warning if the encoder can't keep up.
- A log file for each day in `%AppData%\DatvTx\logs` (OPEN LOG FOLDER on the STATUS tab). Please attach it to bug reports.
- Safety: START keeps the carrier muted until you press PTT. The radio is muted if the encoder stops,
  and the LimeSDR/HackRF/Muntjac modulator stops transmitting if the app closes or crashes.

### Before you start
- **Muntjac**: Windows 10/11 installs the USB driver itself. For LO calibration, copy your Portsdown
  `<serial>.mjo` file into `%AppData%\DatvTx`. Power 0 dB on the slider = Muntjac power 0.20.
- **HackRF One**: install the **WinUSB** driver with Zadig (*Options → List All Devices → HackRF One*).
  Check it with `hackrf_info.exe` in the install folder.
- **LimeSDR Mini**: needs the FTDI **D3XX** (FT601) USB driver.
- **OBS**: *Tools → Start Virtual Camera*, and route the audio to the OBS audio device or a virtual cable.
- For your first transmission on a new radio, use a dummy load and watch it on a MiniTiouner.

### Known limitations
- LimeSDR Mini and HackRF output, and the AMD and Intel GPU encoders, have had little testing on real hardware.
  Please report results.
- Changing SR, FEC or mode on the LimeSDR, HackRF or Muntjac needs a STOP and START.
- dBb uses the BATC spectrum scale (power density compared with the beacon) and is smoothed. Allow a second after a change.
- GSE/IP mode from DATV-NotSoEasy is not included.

### Credits
- **DL5OCD**: DATV-NotSoEasy, the original logic and parameters.
- **F5OEO**: Pluto DATV firmware, MQTT interface and libdvbmod (reworked from G4GUO's DATV Express).
- **G4EWJ**: the BATC Muntjac and its Portsdown 4 USB protocol (GPL-3.0).
- **BATC**: QO-100 wideband spectrum feed and band plan.
- Beta testers, including ON1AVO, for the bug reports.
- FFmpeg, LimeSuite (Lime Microsystems) and libhackrf (Great Scott Gadgets) are included under their own licences.
