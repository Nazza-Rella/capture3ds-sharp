param([string]$Compiler = '')
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $Compiler = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
}
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Roslyn csc.exe not found.' }
$sourceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Capture3DS'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('capture3ds-ftd3-reconnect-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$statusTest = Join-Path $fixture 'Ftd3NativeStatusTests.exe'
# This separate fixture compiles the actual native wrapper but calls ONLY its
# managed constants/predicates. No native function or vendor DLL is invoked.
& $Compiler /nologo /optimize+ "/out:$statusTest" (Join-Path $PSScriptRoot 'Ftd3NativeStatusTests.cs') (Join-Path $sourceRoot 'Ftd3\Ftd3Native.cs')
if ($LASTEXITCODE -ne 0) { throw 'Real native status fixture compilation failed.' }
& $statusTest
if ($LASTEXITCODE -ne 0) { throw 'Real Ftd3Native status constants/predicates failed.' }
$test = Join-Path $fixture 'Ftd3ReconnectTests.exe'
$sources = @(
    (Join-Path $PSScriptRoot 'Ftd3ReconnectTests.cs'),
    (Join-Path $sourceRoot 'Ftd3\Ftd3N3dsxlDevice.cs'),
    (Join-Path $sourceRoot 'Ftd3\Ftd3ReadPipeline.cs'),
    (Join-Path $sourceRoot 'Ftd3\N3dsxlDecoder.cs'),
    (Join-Path $sourceRoot 'Capture3DSModel.cs'),
    (Join-Path $sourceRoot 'ICancellableCapture3DSDevice.cs'),
    (Join-Path $sourceRoot 'Capture3DSFrame.cs')
    (Join-Path $sourceRoot 'Capture3DSAudioDecoder.cs')
)
# Ftd3Native.cs, Capture3DS.dll and all native DLLs are intentionally excluded.
& $Compiler /nologo /unsafe /optimize+ /langversion:9.0 "/out:$test" @sources
if ($LASTEXITCODE -ne 0) { throw 'Managed-only FTD3 regression compilation failed.' }
& $test
if ($LASTEXITCODE -ne 0) { throw 'FTD3 reconnect regression failed.' }
Write-Output "Disposable managed-only fixture: $fixture"
