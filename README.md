# DATV TX (M0CKE)

A Windows DATV transmitter application in the OpenTuner M0CKE style. It is a GUI rewrite of the
**DATV-NotSoEasy** batch scripts by **DL5OCD** (v1.7): the same ffmpeg encoder settings, bitrate
calculations and Pluto MQTT commands, without the console menus.

## Supported hardware

| Transmitter | How it works | Control from the app |
|---|---|---|
| ADALM-Pluto, F5OEO MQTT firmware | ffmpeg → UDP to the Pluto, settings over MQTT | Everything: frequency, SR, FEC, power, PTT, roll-off, digital gain |
| ADALM-Pluto, F5OEO 0303 firmware | ffmpeg → UDP to the Pluto; settings sent to the Pluto's own web pages (no browser needed) | Frequency, power and PTT live; SR, FEC and mode set at START. Set the Pluto once to Main mode DATV, operating mode UDP. *Not yet tested on a real 0303 Pluto* |
| ADALM-Pluto, older F5OEO firmware | ffmpeg → UDP to the Pluto | Stream only. Set the radio up in its web page |
| ADALM-Pluto (or clone), standard ADI firmware | ffmpeg → UDP → `datvtx_mod` makes the DVB-S/S2 signal → IQ to the Pluto via libiio (network or USB) | Frequency, power and PTT live; SR, FEC and mode set at START. *Not yet tested on a real Pluto* |
| LimeSDR Mini | ffmpeg → UDP → built-in modulator (`datvtx_mod.exe`) → LimeSuite | Frequency, gain and PTT live; SR, FEC and mode set at START |
| HackRF One | ffmpeg → UDP → built-in modulator (`datvtx_mod.exe`) → libhackrf | Frequency, gain and PTT live; SR, FEC and mode set at START |
| BATC Muntjac (70 cm / 13 cm) | ffmpeg → UDP → `datvtx_mod.exe` makes DVB-S2 frames → USB serial to the Muntjac | Frequency (same band), power and PTT live; SR, FEC and mode set at START |
| ESP32-C3 (SP8ESA ESP32-DATV firmware) | ffmpeg → UDP → `datvtx_mod` makes DVB-S2 QPSK frames → USB serial to the ESP32-C3, whose Wi-Fi radio transmits on 13 cm | PTT; level and frequency changes restart the transmission briefly. DVB-S2 QPSK, 25-1000 KS, 2300-2450 MHz. *Not yet tested on a real board* |
| Local test | ffmpeg → local multicast | Use MONITOR STREAM to check the picture |

### LimeSDR Mini and HackRF: the built-in modulator
`datvtx_mod.exe` sits next to the app. It takes the transport stream from ffmpeg over local UDP and
modulates DVB-S or DVB-S2 (QPSK, 8PSK, 16APSK, 32APSK, short or long frames, pilots) using F5OEO's
**libdvbmod**. It applies root-raised-cosine pulse shaping (roll-off 0.20, 0.25 or 0.35) and sends the
samples straight to the radio. It pads with null packets so the carrier stays continuous, and if the
app closes it stops transmitting.

Drivers: the installer includes **LimeSuite.dll** (LimeSuite 23.11, built from source) and **libhackrf.dll**
with libusb, so no separate SDR software is needed. Only the Windows USB drivers are required (below).
To use different DLLs, set *Settings → SDR driver folder*.

- **LimeSDR Mini:** needs the FTDI D3XX (FT601) USB driver. If Windows doesn't show it as a working
  device, install the D3XX driver from FTDI's website.
  TX calibration runs at START (it can be switched off in Settings). The app's gain of 0 dB is the
  full normalised TX gain and −50 dB is the minimum. DVB-S2 is practical up to about 2 MS.
- **HackRF One:** if Windows can't see it, plug it in, run **Zadig**, choose *Options → List All
  Devices*, select **HackRF One** and install **WinUSB**. Check it with `hackrf_info`. The app's gain of
  0 dB = TX VGA 47 dB; the +11 dB RF amp is off unless you enable it in Settings. The HackRF is 8-bit,
  so always use a bandpass filter before a PA.

Tested without hardware: the bitrates match DATV-NotSoEasy exactly (QPSK 333 KS 2/3 = 440.3 kbit/s),
the spectrum is clean (more than 43 dB down outside ±250 kHz at 333 KS), loopback MER is 43 dB, and peaks
stay under 85% of full scale in every mode. **First RF test: use a dummy load and a MiniTiouner.**

## Using it
1. **SETTINGS**: choose the transmitter, Pluto IP, callsign and power limit. On a new Pluto, use
   *Send callsign to Pluto* once and reboot it (this replaces SETUP.bat).
2. **SOURCE & ENCODER**: choose OBS Virtual Camera (or any capture device) plus an audio device, a UDP or RTMP
   stream, or a video file. Press ↻ to rescan the devices.
3. **DVB tab**: set the mode, SR, FEC and the video/audio settings. The target TS and video bitrates update as you change them.
   **QO-100 spectrum**: the BATC wideband spectrum is shown live. Click a channel block (125 KS, 333 KS, 500 KS or 1 MS rows, per the BATC band plan)
   to set the TX frequency and symbol rate, or click the trace to pick the nearest 250 kHz channel.
   The dashed line is the beacon level (0 dBb). Your signal's level relative to the beacon is shown in dBb
   on the spectrum and in the transmit column: green below −1 dBb, amber within 1 dB, red at or above the
   beacon. The traffic light warns if you're above the beacon. (Same 3276.8 FFT units per dB as the BATC
   spectrum/OpenTuner; it's power density, not MER.)
   Set the power with the slider (−50 to 0 dB). You can set a lower ceiling in Settings → Power limit to protect a PA.
4. **START** starts the encoder with the carrier muted (amber light: STANDBY). Press **PTT** to go on air
   (red light: ON AIR). Power, frequency, roll-off and digital gain can be changed live.
   The amber light flashes if the encoder can't keep up or the radio's buffer runs dry.
5. **MEMORIES**: store named set-ups (frequency, mode, codec, power) and recall them with a double-click.
   Any old profiles 1-3 are moved into memories automatically. The last run is restored when the app starts.

Run modes (in Settings): *Encode* (normal) and *Control only* (DATV-NotSoEasy Light: OBS or another
encoder streams to the radio itself).

## Linux

A Linux version (PC, x86-64) is built from the same core code with an Avalonia interface that looks and
works like the Windows app. Download the `.AppImage` from the release, make it executable and run it:

```
chmod +x DatvTx-M0CKE-*-x86_64.AppImage
./DatvTx-M0CKE-*-x86_64.AppImage
```

There is also a `.tar.gz` (run `datvtx` from the unpacked folder). The .NET runtime is included.

**Install once (Ubuntu / Debian names):**

```
sudo apt install ffmpeg                  # encoder (uses the system FFmpeg)
sudo apt install hackrf                  # HackRF One: library + udev rules
sudo apt install limesuite limesuite-udev  # LimeSDR Mini
sudo usermod -aG dialout $USER           # BATC Muntjac serial port (log out and in afterwards)
```

- **Inputs:** cameras are V4L2 (`/dev/video*`). Audio is PulseAudio / PipeWire; pick a microphone or a
  *Monitor of …* source to send what the PC is playing.
- **OBS:** `sudo apt install v4l2loopback-dkms`, then in OBS *Tools → Start Virtual Camera* and choose
  *OBS Virtual Camera* in the app.
- **GPU encoding:** NVIDIA NVENC and Intel QSV work if your FFmpeg and drivers support them. AMD AMF is
  Windows-only.
- **Muntjac:** found automatically as `/dev/ttyACM*`; or set *Settings → Muntjac COM port* to e.g. `/dev/ttyACM0`.
- Settings and logs are in `~/.config/DatvTx`.

## Picture quality

At DATV bit rates the encoder is starved, so blocking on motion is mostly a bits-per-frame problem.
VMAF tests on Xiph's 720p test clips at 375 and 600 kb/s (`tools/quality_test.py`, run by the
*Picture quality test* workflow) found:

- **Frame rate matters most.** 10 fps instead of 15 gives each frame 50% more bits and scored far better,
  especially on the worst (most blocky) frames. The DVB tab suggests it when the bit rate is low for the
  picture size and frame rate.
- **Smooth motion** (on by default) blends the dropped frames at 10 fps and below, like a camera's
  motion blur. Pans look smoother, and it also encoded better (+2 to +8 VMAF). At 12.5 / 15 fps a
  two-frame blend gave ghosted edges that encoded worse, so those rates just drop frames.
- **Match the source.** OBS at 30 fps: use 10 or 15 fps. A 25 / 50 fps source: 12.5 fps.
- **A slower x265 preset** ("fast", "slow") helps, if your CPU keeps up.
- **H.265 beats H.264** by a clear margin. B-frames on or off made little difference at these rates.
- Letting the encoder vary its rate under a cap (the default) is slightly better than forcing it constant.
  The transport stream is padded to the exact channel rate either way. *Settings → Constant video rate*
  restores the DATV-NotSoEasy behaviour.

## Building
Push to `main` and the GitHub Actions workflow builds `DatvTx.exe`, bundles a GPL FFmpeg build
(BtbN, includes x264, x265, H.266 VVC, AV1 and NVENC) and creates an Inno Setup installer. Both appear as workflow artifacts. Locally: `dotnet build -c Release`
(targets .NET Framework 4.7.2, so nothing extra needs installing on Windows 10/11).

## Not yet ported
- GSE / IP mode (iptables and routing on the Pluto).
- Changing SR, FEC or mode on the Lime/HackRF while transmitting. Stop and START again for now.

### BATC Muntjac

The Muntjac (G4EWJ, RP2040 + AT86RF215) plugs in by USB and shows up as a COM port; Windows 10/11 installs
the driver itself. The app finds it automatically (USB 2E8A:000A), or you can set the port in
Settings → *Muntjac COM port*. Limits: DVB-S2 only, QPSK or 8PSK, 125 / 250 / 333 / 500 / 1000 KS (no 8PSK
at 1000 KS), 389.5–510, 770–1030 and 2390–2490 MHz. The power slider's 0 dB = Muntjac power 0.20, the
DATV maximum G4EWJ recommends. For carrier/LO calibration, copy your `<serial>.mjo` file from the
Portsdown into `%AppData%\DatvTx`; otherwise it uses the internal default. **Tested on air on 70 cm and QO-100 (13 cm), 125 and 500 KS.**

### ESP32-C3 (SP8ESA ESP32-DATV)

A Seeed XIAO ESP32-C3 (about 5 EUR) running Kacper SP8ESA's [ESP32-DATV](https://github.com/SP8ESA/ESP32-DATV)
firmware turns the chip's own Wi-Fi radio into a DVB-S2 transmitter on 13 cm. Flash the board with HB9IIU's
[C3CAST](https://github.com/HB9IIU/C3CAST) (its Flash button), then choose *ESP32-C3 DATV* here. This app sends the
DVB-S2 QPSK symbols over the board's USB-C cable; it does not include or install the firmware (PolyForm Noncommercial
licence). The board is found automatically (Espressif USB 303A:1001). PTT output: pin D10. The power slider's 0 dB is
*Settings → ESP32 level at 0 dB* (default 100; not calibrated power). As both authors stress: use RF output filtering
and check the spectrum before going on air, and start on a dummy load. The RRC roll-off is fixed at 0.35.

## Credits and licences
- DATV-NotSoEasy by DL5OCD: the original logic and parameters.
- Pluto DATV firmware and MQTT interface by F5OEO.
- libdvbmod (DVB-S/S2 modulator) by F5OEO, reworked from G4GUO's DATV Express. The build fetches it from
  github.com/F5OEO/libdvbmod. That repository has no licence file, so please confirm with F5OEO before you
  distribute builds publicly.
- FFmpeg (GPL build) is bundled separately under its own licence.
- ESP32-C3 transmitter firmware: Kacper SP8ESA (ESP32-DATV), packaged for the XIAO by HB9IIU (C3CAST). Not included.
- Muntjac USB protocol from G4EWJ's code in the BATC Portsdown 4 (`src/muntjac`, GPL-3.0).
