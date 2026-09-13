# USB capture regression tests

These tests use synthetic USB packets only. They do not enumerate or open capture
devices, load native USB libraries, record real audio, or start audio playback.
The tests do not establish hardware latency or sound quality. The short N3DSXL
live-host check and the user's report of normal audio from a supported FTD2
DS Capture Board, described in the repository README, are separate from these
tests. The DS report does not cover every board generation. LL-SPA3 and original
3DS board audio playback remain unverified on hardware.

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

## N3DSXL queued-read and lifecycle tests

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\test-ftd3-reconnect.ps1
powershell -ExecutionPolicy Bypass -File .\tests\test-ftd3-pipeline-failures.ps1
```

Both runners compile the current FTD3 source into fresh temporary directories
with managed stand-ins for the native transport. They never enumerate or open
USB devices. The reconnect runner separately compiles the real `Ftd3Native.cs`
to verify status constants, the native OVERLAPPED layout, entry-point names and
parameter marshaling by reflection, without calling any native function.

Coverage includes the 12-slot ring, repeated wraparound, immediate/pending
completion, copy-before-resubmit ownership, varying valid PCM tail lengths,
short/oversized/misaligned frames, cancellation boundaries, finite timeout
restoration, diagnostic/capture exclusion, partial startup, uncertain native
errors, retained storage after cleanup failure, completion-before-release-before-
close ordering, and retryable/idempotent disposal. A real managed deadline case
spends approximately 2.5 seconds waiting for a deliberately incomplete fake read.
The tests do not prove that a native driver call which never returns can be
interrupted, nor that a disconnected physical board will recover automatically.

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

FTD3 native bindings and pending-request ownership were also checked against
[FTDI AN_379](https://ftdichip.com/wp-content/uploads/2020/08/AN_379-D3xx-Programmers-Guide.pdf),
the vendor header, and Microsoft's
[OVERLAPPED](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ns-minwinbase-overlapped)
and [GetOverlappedResult](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-getoverlappedresult)
contracts. This library retains its existing `FT_ReadPipe` entry point with
overlapped requests; it does not claim to reproduce every setting of the original
viewer or change the installed FTDI driver.
