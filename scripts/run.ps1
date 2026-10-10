$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runtime = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$releaseRoot = Join-Path $root 'artifacts\release'
$published = Get-ChildItem -LiteralPath $releaseRoot -Directory -Filter "PCActivityTracker-*-$runtime" -ErrorAction SilentlyContinue |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'PCActivityTracker.exe') } |
    Sort-Object Name -Descending | Select-Object -First 1
$app = if ($published) { Join-Path $published.FullName 'PCActivityTracker.exe' } else {
    Join-Path $root 'src\PCActivityTracker\bin\Release\net10.0-windows10.0.22000.0\PCActivityTracker.exe'
}
if (-not $published) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$localRuntime = Join-Path $root '.dotnet'
if (Test-Path (Join-Path $localRuntime 'shared\Microsoft.NETCore.App')) {
    $env:DOTNET_ROOT = $localRuntime
}
Start-Process -FilePath $app
