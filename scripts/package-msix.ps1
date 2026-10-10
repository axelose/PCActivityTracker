param(
    [Parameter(Mandatory=$true)][string]$Publisher,
    [Parameter(Mandatory=$true)][string]$PublisherDisplayName,
    [string]$IdentityName = 'PCActivityTracker',
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$CertificateThumbprint = '',
    [string]$BaseUri = '',
    [switch]$AllowUnsigned
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$buildProps = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$buildProps.Project.PropertyGroup.Version
$packageVersion = "$version.0"
$architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
if ([string]::IsNullOrWhiteSpace($Publisher) -or [string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
    throw 'Publisher and PublisherDisplayName must not be empty.'
}
if ($IdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') {
    throw 'IdentityName must be 3-50 characters containing only letters, digits, period, or hyphen.'
}
if ($CertificateThumbprint -and $CertificateThumbprint -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'CertificateThumbprint must be the 40 hexadecimal characters used by signtool /sha1.'
}
if (-not $CertificateThumbprint -and -not $AllowUnsigned) {
    throw 'A production certificate thumbprint is required. Use -AllowUnsigned only for package validation.'
}
if ($BaseUri) {
    $parsedBaseUri = $null
    if (-not [Uri]::TryCreate($BaseUri, [UriKind]::Absolute, [ref]$parsedBaseUri) -or $parsedBaseUri.Scheme -ne 'https') {
        throw 'BaseUri must be an absolute HTTPS URL.'
    }
}
$publisherXml = [System.Security.SecurityElement]::Escape($Publisher)
$publisherDisplayNameXml = [System.Security.SecurityElement]::Escape($PublisherDisplayName)
$identityNameXml = [System.Security.SecurityElement]::Escape($IdentityName)

function Find-WindowsSdkTool([string]$name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        return Get-ChildItem -LiteralPath $kits -Filter $name -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    return $null
}

$makeAppx = Find-WindowsSdkTool 'makeappx.exe'
if (-not $makeAppx) { throw 'makeappx.exe was not found. Install the Windows 11 SDK MSIX Packaging Tools.' }
$signTool = Find-WindowsSdkTool 'signtool.exe'
if ($CertificateThumbprint -and -not $signTool) { throw 'signtool.exe was not found. Install the Windows 11 SDK.' }

& (Join-Path $PSScriptRoot 'publish.ps1') -Runtime $Runtime -CertificateThumbprint $CertificateThumbprint -AllowUnsigned:$AllowUnsigned
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$msixRoot = Join-Path $root 'artifacts\msix'
$staging = Join-Path $msixRoot "staging-$Runtime"
$published = Join-Path $root "artifacts\release\PCActivityTracker-$version-$Runtime"
$msixPath = Join-Path $msixRoot "PCActivityTracker-$version-$architecture.msix"
New-Item -ItemType Directory -Path $msixRoot -Force | Out-Null
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null
Copy-Item -Path (Join-Path $published '*') -Destination $staging -Recurse

$manifest = Get-Content -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml.template') -Raw
$manifest = $manifest.Replace('__IDENTITY_NAME__', $identityNameXml)
$manifest = $manifest.Replace('__PUBLISHER__', $publisherXml)
$manifest = $manifest.Replace('__PUBLISHER_DISPLAY_NAME__', $publisherDisplayNameXml)
$manifest = $manifest.Replace('__VERSION__', $packageVersion)
$manifest = $manifest.Replace('__ARCHITECTURE__', $architecture)
Set-Content -LiteralPath (Join-Path $staging 'AppxManifest.xml') -Value $manifest -Encoding UTF8

$assets = Join-Path $staging 'Assets'
New-Item -ItemType Directory -Path $assets | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'assets\StoreLogo.png') -Destination $assets
Copy-Item -LiteralPath (Join-Path $root 'assets\Square150x150Logo.png') -Destination $assets
Copy-Item -LiteralPath (Join-Path $root 'assets\Square44x44Logo.png') -Destination $assets
Copy-Item -LiteralPath (Join-Path $root 'assets\Wide310x150Logo.png') -Destination $assets

if (Test-Path -LiteralPath $msixPath) { Remove-Item -LiteralPath $msixPath -Force }
& $makeAppx pack /o /d $staging /p $msixPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($CertificateThumbprint) {
    & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr 'https://timestamp.acs.microsoft.com' /td SHA256 $msixPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $signTool verify /pa /all /v $msixPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($BaseUri) {
    $base = $BaseUri.TrimEnd('/')
    $appInstallerName = 'PCActivityTracker.appinstaller'
    $appInstallerUri = [System.Security.SecurityElement]::Escape("$base/$appInstallerName")
    $packageUri = [System.Security.SecurityElement]::Escape("$base/$(Split-Path -Leaf $msixPath)")
    $template = Get-Content -LiteralPath (Join-Path $root 'packaging\PCActivityTracker.appinstaller.template') -Raw
    $appInstaller = $template.Replace('__APPINSTALLER_URI__', $appInstallerUri)
    $appInstaller = $appInstaller.Replace('__PACKAGE_URI__', $packageUri)
    $appInstaller = $appInstaller.Replace('__IDENTITY_NAME__', $identityNameXml)
    $appInstaller = $appInstaller.Replace('__PUBLISHER__', $publisherXml)
    $appInstaller = $appInstaller.Replace('__VERSION__', $packageVersion)
    $appInstaller = $appInstaller.Replace('__ARCHITECTURE__', $architecture)
    Set-Content -LiteralPath (Join-Path $msixRoot $appInstallerName) -Value $appInstaller -Encoding UTF8
}
$algorithm = [System.Security.Cryptography.SHA256]::Create()
$stream = [System.IO.File]::OpenRead($msixPath)
try { $hash = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
finally { $stream.Dispose(); $algorithm.Dispose() }
"$hash  $(Split-Path -Leaf $msixPath)" | Set-Content -LiteralPath (Join-Path $msixRoot 'SHA256SUMS.txt') -Encoding ASCII
Write-Host "MSIX package: $msixPath"
