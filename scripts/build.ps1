param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
$dotnet = Resolve-TrackerDotnet -Root $root
& (Join-Path $PSScriptRoot 'build-native-sqlcipher.ps1') -Runtime win-x64

Push-Location -LiteralPath $root
try {
    & $dotnet build (Join-Path $root 'PCActivityTracker.sln') -c $Configuration -p:UseSharedCompilation=false -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}
