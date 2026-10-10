$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')

$dotnet = Resolve-TrackerDotnet -Root $root -AllowMissing
if ($dotnet) {
    Write-Host "A .NET SDK satisfying global.json is already available: $dotnet"
    exit 0
}

$sdkVersion = (Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ($sdkVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'global.json must pin a stable .NET SDK version.' }
$installDirectory = Join-Path $root '.dotnet'
$installerPath = Join-Path ([IO.Path]::GetTempPath()) ("pc-activity-tracker-dotnet-install-$([Guid]::NewGuid().ToString('N')).ps1")
$installerUrl = 'https://raw.githubusercontent.com/dotnet/install-scripts/47940ac9fc30a2f2dd19167165d0bb0774625f67/src/dotnet-install.ps1'
# Hash of the raw LF bytes served by this pinned URL, not a CRLF Git checkout.
$installerSha256 = '3BB07BC8025211836C1E4F9D3F6A044E55B1FB6EEC518A6C78851D04E210442B'

try {
    Write-Host 'Downloading the pinned Microsoft dotnet-install script...'
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing -Uri $installerUrl -OutFile $installerPath
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installerPath).Hash
    if ($actualHash -ne $installerSha256) { throw "dotnet-install.ps1 hash mismatch: $actualHash" }

    Write-Host "Installing .NET SDK $sdkVersion locally into $installDirectory..."
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath -Version $sdkVersion -InstallDir $installDirectory -NoPath
    if ($LASTEXITCODE -ne 0) { throw "The .NET installer failed with exit code $LASTEXITCODE." }
    $dotnet = Resolve-TrackerDotnet -Root $root
    & $dotnet --info
    if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed with exit code $LASTEXITCODE." }
} finally {
    if (Test-Path -LiteralPath $installerPath -PathType Leaf) { Remove-Item -LiteralPath $installerPath -Force }
}
