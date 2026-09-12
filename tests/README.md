# USB audio regression tests

These tests use synthetic USB packets only. They do not enumerate or open capture
devices, load native USB libraries, record real audio, or start audio playback.
Audio decoding is implemented, but playback on each physical board remains unverified.

## Run the source-only tests

On Windows with the Visual Studio 2022 Roslyn compiler and .NET Framework 4.8:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\test-audio.ps1
```

Pass `-Compiler <path-to-csc.exe>` if Roslyn is installed elsewhere. The default
path is the Visual Studio 2022 Community installation. The script compiles only
the frame DTO, model/exception declarations, and pure decoders into a fresh
temporary directory. No vendor DLLs are required. It currently checks 126 assertions.

## Verify a built library

After building the library as described in the repository README:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\test-audio.ps1 -AssemblyPath .\src\Capture3DS\bin\Debug\net48\Capture3DS.dll
powershell -ExecutionPolicy Bypass -File .\tests\test-audio.ps1 -AssemblyPath .\src\Capture3DS\bin\Release\net48\Capture3DS.dll
```

This also checks the DS transport's existing pure synchronization-trimming helpers
against synthetic leading and trailing padding, for 138 assertions per build.
No device object is constructed, and no native entry point is called.

## Coverage and limits

- Existing frame constructor compatibility; signed PCM16 stereo order and exact
  source sample clock; owned output and defensive copying.
- Actual transfer lengths, model-specific maxima, incomplete stereo pairs, invalid
  packet bounds, and exclusion of N3DSXL transport-error buffers.
- N3DSXL, Loopy original-3DS, and FTD2 DS video/audio tails, including DS alignment
  prefixes and synchronization padding. Legacy DS decoder calls remain video-only.
- LL-SPA3 normal and 401-column extra-header packets; repeated sample indices,
  wraparound, duplicates across frames, partial/invalid headers, and reconnect reset.
- Adding audio does not alter RGB output for the same valid packet. This is not
  a claim of hardware validation, latency measurement, or sound quality testing.

The runner leaves its temporary executable directory in place and prints its path
for inspection. It does not modify application settings or the development source.

## Protocol reference

The audio layout follows the MIT-licensed cc3dsfs revision
[`e58edc4d34002b095f8fd0cd4f60df532df25fb0`](https://github.com/Lorenzooone/cc3dsfs/tree/e58edc4d34002b095f8fd0cd4f60df532df25fb0):

- [`conversions.cpp`](https://github.com/Lorenzooone/cc3dsfs/blob/e58edc4d34002b095f8fd0cd4f60df532df25fb0/source/conversions.cpp):
  `convertAudioToOutput`, `copyAudioOptimize3DSLE`, and
  `copyAudioFromSoundDataOptimize3DSLE`.
- [`devicecapture.cpp`](https://github.com/Lorenzooone/cc3dsfs/blob/e58edc4d34002b095f8fd0cd4f60df532df25fb0/source/devicecapture.cpp):
  `get_audio_n_samples`.
- [`capture_structs.hpp`](https://github.com/Lorenzooone/cc3dsfs/blob/e58edc4d34002b095f8fd0cd4f60df532df25fb0/include/capture_structs.hpp):
  Optimize header/sample records and capture packet layouts.
