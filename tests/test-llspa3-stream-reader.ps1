param([string]$Compiler = '')
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $Compiler = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
}
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Roslyn csc.exe not found.' }
$sourceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Capture3DS'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('capture3ds-llspa3-reader-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$test = Join-Path $fixture 'LlSpa3StreamReaderTests.exe'
$sources = @(Join-Path $PSScriptRoot 'LlSpa3StreamReaderTests.cs')
foreach ($name in @('Capture3DSFrame.cs','Capture3DSModel.cs','Capture3DSAudioDecoder.cs',
        'Cypress\LlSpa3Decoder.cs','Cypress\LlSpa3AudioDecoder.cs','Cypress\LlSpa3FrameLayout.cs',
        'Cypress\LlSpa3StreamReader.cs')) {
    $sources += Join-Path $sourceRoot $name
}
# The CyUSB endpoint, P/Invoke declarations and all native DLLs are excluded.
& $Compiler /nologo /unsafe /optimize+ /langversion:9.0 "/out:$test" @sources
if ($LASTEXITCODE -ne 0) { throw 'Stream reader fixture compilation failed.' }
& $test
if ($LASTEXITCODE -ne 0) { throw 'Stream reader regression failed.' }
Write-Output "Disposable hardware-free fixture: $fixture"
