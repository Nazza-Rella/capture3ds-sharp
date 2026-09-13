param([string]$Compiler = '')
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $Compiler = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
}
if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Roslyn csc.exe not found.' }
$sourceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Capture3DS'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('capture3ds-pipeline-failures-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$test = Join-Path $fixture 'Ftd3ReadPipelineFailureTests.exe'
$sources = @(
    (Join-Path $PSScriptRoot 'Ftd3ReadPipelineFailureTests.cs'),
    (Join-Path $sourceRoot 'Ftd3\Ftd3ReadPipeline.cs')
)
# Deliberately exclude real Ftd3Native.cs, Capture3DS.dll, and vendor DLLs.
# The fake uses managed events and allocated test memory only. Its one timeout
# case spends approximately 2.5 seconds checking the real cleanup deadline.
& $Compiler /nologo /optimize+ /platform:x64 /langversion:9.0 "/out:$test" @sources
if ($LASTEXITCODE -ne 0) { throw 'Managed-only pipeline failure fixture compilation failed.' }
& $test
if ($LASTEXITCODE -ne 0) { throw 'Pipeline failure lifecycle regression failed.' }
Write-Output "Disposable managed-only fixture: $fixture"
