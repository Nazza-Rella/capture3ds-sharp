param(
    [string]$Compiler = '',
    [string]$AssemblyPath = ''
)
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $Compiler = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
}
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) {
    throw 'Roslyn csc.exe was not found. Pass -Compiler with a C# 9-capable compiler path.'
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('capture3ds-frame-copy-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$test = Join-Path $fixture 'Capture3DSFrameCopyTests.exe'
$testSource = Join-Path $PSScriptRoot 'Capture3DSFrameCopyTests.cs'
if ($AssemblyPath) {
    $AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
    & $Compiler /nologo /unsafe /optimize+ "/r:$AssemblyPath" "/out:$test" $testSource
    if ($LASTEXITCODE -ne 0) { throw 'Built-library frame test compilation failed.' }
    Copy-Item -LiteralPath $AssemblyPath -Destination (Join-Path $fixture 'Capture3DS.dll')
} else {
    $frameSource = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Capture3DS\Capture3DSFrame.cs'
    & $Compiler /nologo /unsafe /optimize+ "/out:$test" $testSource $frameSource
    if ($LASTEXITCODE -ne 0) { throw 'Standalone frame test compilation failed.' }
}
& $test
if ($LASTEXITCODE -ne 0) { throw 'Frame-copy regression test failed.' }
Write-Output "Disposable test fixture: $fixture"
