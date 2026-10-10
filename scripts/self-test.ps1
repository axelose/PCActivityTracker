$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
$dotnet = Resolve-TrackerDotnet -Root $root
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release

Push-Location -LiteralPath $root
try {
    & $dotnet (Join-Path $root 'src\PCActivityTracker\bin\Release\net10.0-windows10.0.22000.0\PCActivityTracker.dll') --self-test
    if ($LASTEXITCODE -ne 0) { throw "Self-test failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}
