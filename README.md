# DATV TX (M0CKE)

A Windows DATV transmitter application in the OpenTuner M0CKE style. It is a GUI rewrite of the
**DATV-NotSoEasy** batch scripts by **DL5OCD** (v1.7): the same ffmpeg encoder settings, bitrate
calculations and Pluto MQTT commands, without the console menus.

## Supported hardware

| Transmitter | How it works | Control from the app |
|---|---|---|
| ADALM-Pluto, F5OEO MQTT firmware | ffmpeg → UDP to the Pluto, settings over MQTT | Everything: frequency, SR, FEC, gain, PTT, roll-off, digital gain, Longmynd RX, relay |
| ADALM-Pluto, older F5OEO firmware | ffmpeg → UDP to the Pluto | Stream only. Set the radio up in its web page |
| LimeSDR Mini | ffmpeg → UDP → built-in modulator (`datvtx_mod.exe`) → LimeSuite | Frequency, gain and PTT live; SR, FEC and mode set at START |
| HackRF One | ffmpeg → UDP → built-in modulator (`datvtx_mod.exe`) → libhackrf | Frequency, gain and PTT live; SR, FEC and mode set at START |
| Local test | ffmpeg → local multicast | Use MONITOR STREAM to check the picture |

### LimeSDR Mini and HackRF: the built-in modulator
`datvtx_mod.exe` sits next to the app. It takes the transport stream from ffmpeg over local UDP and
modulates DVB-S or DVB-S2 (QPSK, 8PSK, 16APSK, 32APSK, short or long frames, pilots) using F5OEO's
**libdvbmod**. It applies root-raised-cosine pulse shaping (roll-off 0.20, 0.25 or 0.35) and sends the
samples straight to the radio. It pads with null packets so the carrier stays continuous, and if the
app closes it stops transmitting.

Drivers: the modulator loads **LimeSuite.dll** or **hackrf.dll** at run time. The easiest way to get
both on Windows is the PothosSDR bundle, which installs to `C:\Program Files\PothosSDR\bin`; the
modulator looks there automatically. If the DLLs are somewhere else, set *Settings → SDR driver folder*.

- **LimeSDR Mini:** also needs its USB driver (FTDI D3XX, from the LimeSuite/PothosSDR install).
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
3. **MODULATOR**: set the mode, SR, FEC, frequency and gain. The TS and video bitrates update as you change them.
4. **START** starts the encoder with the carrier muted. Press **PTT** to go on air. Gain, frequency,
   roll-off, digital gain and the RX frequency can be changed live.
5. **Profiles 1-3** replace favorite-1..3.ini. The last run is restored when the app starts.

Run modes (in Settings): *Encode* (normal), *Control only* (DATV-NotSoEasy Light: OBS or another
encoder streams to the Pluto itself), *Relay* (Longmynd RX re-transmitted).

## Building
Push to `main` and the GitHub Actions workflow builds `DatvTx.exe`, bundles a GPL FFmpeg build
(BtbN, includes x264, x265, H.266 VVC, AV1 and NVENC) and creates an Inno Setup installer. Both appear as workflow artifacts. Locally: `dotnet build -c Release`
(targets .NET Framework 4.7.2, so nothing extra needs installing on Windows 10/11).

## Not yet ported
- GSE / IP mode (iptables and routing on the Pluto).
- Changing SR, FEC or mode on the Lime/HackRF while transmitting. Stop and START again for now.

## Credits and licences
- DATV-NotSoEasy by DL5OCD: the original logic and parameters.
- Pluto DATV firmware and MQTT interface by F5OEO.
- libdvbmod (DVB-S/S2 modulator) by F5OEO, reworked from G4GUO's DATV Express. The build fetches it from
  github.com/F5OEO/libdvbmod. That repository has no licence file, so please confirm with F5OEO before you
  distribute builds publicly.
- FFmpeg (GPL build) is bundled separately under its own licence.
