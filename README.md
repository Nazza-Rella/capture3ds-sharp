# capture3ds-sharp

A C# (.NET Framework 4.8) library that reads video frames directly from
3DS / DS capture boards over USB, without running the official viewer
applications. The capture protocols are C# ports of the MIT-licensed
[cc3dsfs](https://github.com/Lorenzooone/cc3dsfs), following the approach of
[ponkan-python](https://github.com/niart120/ponkan-python).

Frames are returned at native screen resolutions as RGB8 buffers
(top / bottom screens separately), with helpers for a stacked mosaic and a
1280x720 letterbox canvas.

## Current Scope

| Device | Chip | Protocol | Status |
|---|---|---|---|
| New 3DS XL capture board (3dscapture.com "N3DSXL") | FTDI FT600 (D3XX) | `3DSCapture_FTD3` port | Verified on hardware |
| DS capture board | FTDI FT232H + Lattice FPGA | `DSCapture_FTD2` port | Verified on hardware |
| 3DS LL capture board "LL-SPA3" (non-standard.com) | Cypress FX2LP | `Optimize_3DS` port via CyUSB.NET | Verified on hardware |

Screen sizes: 3DS top 400x240 / bottom 320x240, DS 256x192 x2.
Only 2D capture is implemented.

## Building

Prerequisites: Windows, .NET Framework 4.8 developer pack.

Two third-party components are **not** included in this repository for
licensing reasons and must be placed manually before building:

1. **CyUSB.dll** (LL-SPA3 backend) — clone
   [kategray/CyUSB](https://github.com/kategray/CyUSB) into `external/CyUSB`
   and build `library/c_sharp` so that the assembly exists at
   `external/CyUSB/library/c_sharp/lib/CyUSB.dll`.
   (Cypress Software License; its source cannot be redistributed here.)
2. **FTDI native DLLs** — download from [FTDI](https://ftdichip.com/) and place
   into `native/`:
   - `native/FTD3XX.dll` (D3XX, for N3DSXL)
   - `native/ftd2xx.dll` (D2XX, for the DS capture board)

Then:

```
dotnet build src/Capture3DS.sln -c Release
```

The firmware binaries under `firmware/` (DS FPGA bitstreams and the Optimize
FX2 firmware) originate from the MIT-licensed cc3dsfs repository and are
embedded into the assembly at build time.

## Usage

```csharp
using Capture3DS;

var devices = Capture3DSApi.ListDevices();
using (var dev = Capture3DSApi.Open(devices[0]))
{
    dev.Connect();
    Capture3DSFrame frame = dev.ReadFrame();

    // frame.Top / frame.Bottom : RGB8, row-major, 3 bytes per pixel
    byte[] mosaic = frame.ToMosaic(out int w, out int h);
    byte[] canvas = frame.ToLetterbox720(); // 1280x720 RGB8
}
```

`ListDevices()` silently skips backends whose native DLL is missing, so the
library still works when only some of the DLLs above are installed.

## Command Line Tools

`CaptureProbe.exe [outputDir] [frameCount]` — enumerates devices, connects to
the first one and saves PNG frames (top / bottom / 720p letterbox).

Diagnostic modes:

- `--cypress` — raw enumeration of Cypress FX2 devices (VID/PID/bcdDevice)
- `--ftd3raw` — unfiltered FTD3 device dump (driver / exclusive-open triage)
- `--ftd3sizes [n]` — transfer-length statistics for frame alignment triage
- `--ftd3stream <dir> <n>` — high-rate stream test, saves misaligned frames
- `--ftd3verify <dir> <n>` — production `ReadFrame` verification under load

## Notes

- **Close the official viewers first.** The capture boards are exclusive-open
  devices; while `3ds_capture.exe` or `n3DS_view.exe` is running the device
  either disappears from enumeration or fails to connect.
- **N3DSXL** requires the FTDI D3XX driver (the one used by the official
  3ds_capture software).
- **LL-SPA3** works with the vendor's stock `cyusb3` driver — no driver
  replacement (Zadig/WinUSB) and no manual firmware flashing are needed. When
  the board enumerates as a blank FX2 (`04B4:8613`), the library uploads the
  cc3dsfs Optimize firmware itself and the device renumerates to `04B4:1004`.
- **LL-SPA3 product key** is optional. If present it is read from the
  `n3DS_view` EEPROM cache (`%APPDATA%\non-standard.com\VROM_*.bin`) or from a
  `llspa3_key.txt` file next to the executable. The key is only folded into
  the capture setup buffer and is never logged.

## License and Attribution

MIT License. See [LICENSE](LICENSE).

This project ports protocol implementations from:

- [cc3dsfs](https://github.com/Lorenzooone/cc3dsfs) (MIT) — capture protocols
  and the firmware binaries under `firmware/`
- [ponkan-python](https://github.com/niart120/ponkan-python) (MIT)

Not affiliated with Nintendo, 3dscapture.com, non-standard.com, FTDI or
Infineon/Cypress.
