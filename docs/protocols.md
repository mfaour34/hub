# Device protocols

This page documents what Hub sends to devices, so others can check it and build on it. It covers only the commands Hub uses. Every one has been tested on real hardware (a Razer Viper V3 Pro and a Logitech PRO X 2 LIGHTSPEED).

Interoperability notes like these are how open-source drivers work. See also [OpenRazer](https://github.com/openrazer/openrazer) and [HeadsetControl](https://github.com/Sapd/HeadsetControl).

## Razer mice: 90-byte feature reports

Hub talks to the mouse's HID collection with usage page `0x01` and usage `0x02`, using 91-byte feature reports (report ID 0 plus a 90-byte packet). Code: `src/Hub.Core/Razer/RazerPacket.cs` and `RazerMouse.cs`.

| Byte | Meaning |
|---|---|
| 0 | Status in replies: 0 new, 1 busy, 2 success, 3 failure, 4 timeout, 5 not supported |
| 1 | Transaction id: `0x1F` on current mice, `0x3F` or `0xFF` on older ones (Hub probes for it) |
| 5 | Data size |
| 6, 7 | Command class and command id. A `GET` is the `SET` id with bit 7 set. |
| 8–87 | Arguments, big-endian |
| 88 | XOR of bytes 2–87 |

Hub writes a request, then reads replies until the status is no longer *busy*. It checks that the reply echoes the transaction id, class and id. It waits 25 ms between reads over a wireless dongle and 5 ms over a cable.

| Feature | GET / SET | Arguments |
|---|---|---|
| Firmware | `00,87` | → 4 bytes |
| Device mode | `00,84` / `00,04` | `[mode, 0]`. Mode 0 is normal, 3 is driver mode (buttons handled by the vendor app). |
| Wireless link up | `00,B8` | → `[connected]` |
| Polling (HyperPolling dongle) | `00,C0` / `00,40` | `[profile=1, code]` with codes 1, 2, 4, 8, 16, 32, 64 = 8000 … 125 Hz |
| Polling (profile, wired) | `00,8E` / `00,0E` | `[1, code]` with codes 1, 2, 8 = 1000, 500, 125 Hz |
| Polling (classic) | `00,85` / `00,05` | `[code]` |
| Current DPI | `04,85` / `04,05` | `[0, xH, xL, yH, yL, 0, 0]` |
| DPI stages | `04,86` / `04,06` | `[1, active, n] + n × [id, xH, xL, yH, yL, 0, 0]`, data size 7n + 3 |
| Battery | `07,80`, `07,84` | `[_, 0–255]` level, `[_, charging]` |
| Low-battery threshold | `07,81` / `07,01` | `[0–255]` |
| Sleep timeout | `07,83` / `07,03` | seconds, big-endian |
| Lift-off | `0B,85`, `0B,8B` / `0B,03`, `0B,0B`, `0B,05` | Asymmetric: `[0,4,1,lift-1,land-1]`. Symmetric: `[0,4,1,distance-1]`. |
| Dynamic sensitivity | `0B,90`, `0B,91` / `0B,10`, `0B,11` | `[1, enabled]` and `[1, mode]`. Modes: 0 classic, 1 natural, 2 jump, 3 custom. |
| Custom curve | `0B,93` / `0B,13` | `[1, 3, 250, offset, n] + n × float32 LE`, in chunks of 8 |
| Rotation | `0B,94` / `0B,14` | `[1, enabled, angle int8]` |
| Button | `02,8C` / `02,0C` | `[1, button, layer=0, function, len, data…]`. Functions: 1 mouse button, 2 key, 6 DPI, 11 double-click, 0 off. |

## Logitech headsets: Centurion over HID

The dongle exposes a vendor collection on usage page `0xFFA0`. Hub uses 64-byte reports with ID `0x51`. Code: `src/Hub.Core/Logitech/`.

```
frame   = [0x51][len = payload + 1][flags = fragment << 1 | more][payload…] zero-padded to 64
direct  = [feature index][function << 4 | software id][params…]            (the dongle itself)
bridged = [bridge idx][0x10 | swid][lenHi][lenLo] + [0x00][sub-feature idx][function << 4 | swid][params…]
```

The headset is reached through the dongle's bridge feature (`0x0003`). Hub finds feature indexes at runtime: function 0 of index 0 (root) takes a 16-bit feature id and returns its index, or 0 if the feature is missing. Replies with software id 0 are unsolicited events: battery, sidetone, noise reduction, and connection changes.

| Feature | Id | Used functions |
|---|---|---|
| Device info | `0x0100` | fn1 firmware version |
| Battery | `0x0104` | fn0 → `[percent, _, state]`, where state 1 or 2 means charging |
| Auto power-off | `0x0108` | fn0 / fn1 `[minutes]`, where 0 means never |
| Mic noise reduction | `0x0602` | fn0 / fn1 `[on]` |
| Sidetone | `0x0604` | fn0 → `[…, level]`, fn1 `[1, level 0–100]` |
| Onboard EQ | `0x0636` | fn1 `[0x80]` reads live bands `[flags][n] + n × [freqBE16][gain int8][Q]`; fn2 writes them (slot `0x00` live only, `0x80` also saves) |

The EQ write carries the band list plus biquad coefficients for the headset's 48 kHz and 16 kHz paths. Hub only writes EQ to headsets that report the 5-band layout it was verified with (80, 240, 750, 2200 and 6600 Hz).

## Audio channels

Hub doesn't use a custom driver. Its audio pipeline is:

```
apps → Game / Chat / Media (VoiceMeeter Potato virtual inputs 5–7)
     → Equalizer APO on each input: preamp → filters → hub-limiter (VST2, look-ahead)
     → VoiceMeeter bus A1 (WDM, exclusive) → your headphones
```

Hub writes Equalizer APO's `config.txt` with one `Device:` block per channel, matched by endpoint GUID, and keeps a backup that the uninstaller restores. It sets VoiceMeeter through the official Remote API, and default devices and sample rates through `IPolicyConfig`, like EarTrumpet does. Per-app routing uses `IAudioPolicyConfigFactory`, the API behind Windows' *App volume and device preferences* page.
