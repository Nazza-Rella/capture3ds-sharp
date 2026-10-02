param([string]$Compiler = '', [string]$AssemblyPath = '')
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $Compiler = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
}
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Roslyn csc.exe not found.' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('capture3ds-audio-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$test = Join-Path $fixture 'Capture3DSAudioTests.exe'
$testSource = Join-Path $PSScriptRoot 'Capture3DSAudioTests.cs'
if ($AssemblyPath) {
    $AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
    & $Compiler /nologo /unsafe /optimize+ /langversion:9.0 "/r:$AssemblyPath" "/out:$test" $testSource
    if ($LASTEXITCODE -ne 0) { throw 'Built-library audio test compilation failed.' }
    Copy-Item -LiteralPath $AssemblyPath -Destination (Join-Path $fixture 'Capture3DS.dll')
} else {
    $sourceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Capture3DS'
    $relativeSources = @(
        'Capture3DSFrame.cs','Capture3DSModel.cs','Capture3DSAudioDecoder.cs',
        'Ftd3\N3dsxlDecoder.cs','Ftd2\DsDecoder.cs','Loopy\LoopyOld3dsDecoder.cs',
        'Cypress\LlSpa3Decoder.cs','Cypress\LlSpa3AudioDecoder.cs','Cypress\LlSpa3FrameLayout.cs'
    )
    $sources = @($testSource) + @($relativeSources | ForEach-Object { Join-Path $sourceRoot $_ })
    # Device classes, P/Invoke declarations and all native DLLs are excluded.
    & $Compiler /nologo /unsafe /optimize+ /langversion:9.0 "/out:$test" @sources
    if ($LASTEXITCODE -ne 0) { throw 'Standalone audio test compilation failed.' }
}
& $test
if ($LASTEXITCODE -ne 0) { throw 'Audio regression failed.' }
Write-Output "Disposable hardware-free fixture: $fixture"
