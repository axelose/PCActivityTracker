param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$CertificateThumbprint = '',
    [switch]$AllowUnsigned
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
. (Join-Path $PSScriptRoot 'Release-Safety.ps1')
$dotnet = Resolve-TrackerDotnet -Root $root

[xml]$buildProps = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$buildProps.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props must contain a numeric three-part version.' }
$extensionVersion = (Get-Content -LiteralPath (Join-Path $root 'browser-extension\manifest.json') -Raw | ConvertFrom-Json).version
if ($extensionVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The extension must contain a numeric three-part version.' }
if (-not $CertificateThumbprint -and -not $AllowUnsigned) {
    throw 'A production code-signing certificate thumbprint is required. Use -AllowUnsigned only for local verification.'
}
if ($CertificateThumbprint -and $CertificateThumbprint -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'CertificateThumbprint must be the 40 hexadecimal characters used by signtool /sha1.'
}

$releaseRoot = Join-Path $root 'artifacts\release'
function Assert-ReleasePath([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    $boundary = [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\')
    if (-not $resolved.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release operations must remain below artifacts/release.'
    }
    $ancestor = $resolved
    while ($ancestor -and $ancestor.Length -ge $root.Length) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Release operations do not follow directory junctions or symbolic links.'
        }
        $ancestor = Split-Path -Parent $ancestor
    }
}
$publishDirectory = Join-Path $releaseRoot "PCActivityTracker-$version-$Runtime"
$zipPath = "$publishDirectory.zip"
$extensionZip = Join-Path $releaseRoot "PCActivityTracker-Browser-Extension-$extensionVersion.zip"
$publishWorkDirectory = "$publishDirectory.build-$PID"
$zipWorkPath = Join-Path $releaseRoot ".PCActivityTracker-$version-$Runtime.build-$PID.zip"
$extensionZipWorkPath = Join-Path $releaseRoot ".PCActivityTracker-Browser-Extension-$extensionVersion.build-$PID.zip"
$nativeHostWorkDirectory = Join-Path $releaseRoot ".PCActivityTracker-NativeHost-$Runtime.build-$PID"
foreach ($releasePath in @($publishDirectory, $zipPath, $extensionZip, $publishWorkDirectory,
    $zipWorkPath, $extensionZipWorkPath, $nativeHostWorkDirectory)) { Assert-ReleasePath $releasePath }
Assert-TrackerStopped -PublishDirectory $publishDirectory
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
foreach ($workingPath in @($publishWorkDirectory, $zipWorkPath, $extensionZipWorkPath, $nativeHostWorkDirectory)) {
    if (Test-Path -LiteralPath $workingPath) { Remove-Item -LiteralPath $workingPath -Recurse -Force }
}

Push-Location -LiteralPath $root
try {
    & (Join-Path $PSScriptRoot 'verify-public-source.ps1') -RequireGit:([bool]$CertificateThumbprint)
    & (Join-Path $PSScriptRoot 'build-native-sqlcipher.ps1') -Runtime $Runtime
    # Restore both supported RIDs once, using the committed dependency graph.
    # dotnet restore -r replaces RuntimeIdentifiers and would mutate the lock files.
    & $dotnet restore (Join-Path $root 'PCActivityTracker.sln') --locked-mode -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    # Cross-architecture self-contained publishing needs the official Microsoft
    # runtime packs when they are not already installed with the local SDK.
    & $dotnet publish (Join-Path $root 'src\PCActivityTracker\PCActivityTracker.csproj') -c Release -r $Runtime --self-contained true --no-restore `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=embedded -p:UseSharedCompilation=false -nodeReuse:false `
        -o $publishWorkDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    & $dotnet publish (Join-Path $root 'src\PCActivityTracker.NativeHost\PCActivityTracker.NativeHost.csproj') `
        -c Release -r $Runtime --self-contained true --no-restore `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=embedded -p:UseSharedCompilation=false -nodeReuse:false `
        -o $nativeHostWorkDirectory
    if ($LASTEXITCODE -ne 0) { throw "Native Messaging host publish failed with exit code $LASTEXITCODE." }
    # Both self-contained outputs carry the same .NET runtime file names. The WPF
    # output contains the full WindowsBase implementation while the console host
    # contains a small facade with the same name, so never overwrite desktop files.
    # Add host-specific and otherwise missing files to the shared runtime instead.
    Get-ChildItem -LiteralPath $nativeHostWorkDirectory -File | ForEach-Object {
        $destination = Join-Path $publishWorkDirectory $_.Name
        if (-not (Test-Path -LiteralPath $destination) -or $_.Name -like 'PCActivityTracker.NativeHost.*') {
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
    }

    foreach ($document in @('README.md','LICENSE','PRIVACY.md','SECURITY.md','SUPPORT.md','THIRD-PARTY-NOTICES.md','CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $root $document) -Destination $publishWorkDirectory
    }
    Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination (Join-Path $publishWorkDirectory 'licenses') -Recurse
    $dotnetRoot = Split-Path -Parent $dotnet
    $dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
    $dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
    if (-not (Test-Path -LiteralPath $dotnetLicense) -or -not (Test-Path -LiteralPath $dotnetNotices)) {
        throw 'The .NET redistribution license files were not found next to dotnet.exe.'
    }
    Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $publishWorkDirectory 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $publishWorkDirectory 'DOTNET-THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination (Join-Path $publishWorkDirectory 'docs') -Recurse

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

    if ($CertificateThumbprint) {
        $signTool = Find-WindowsSdkTool 'signtool.exe'
        if (-not $signTool) { throw 'signtool.exe was not found. Install the Windows 11 SDK.' }
        foreach ($executableName in @('PCActivityTracker.exe','PCActivityTracker.dll',
            'PCActivityTracker.NativeHost.exe','PCActivityTracker.NativeHost.dll','LocalAi.Runtime.dll',
            "runtimes\$Runtime\native\pcactivity_sqlcipher.dll")) {
            $executablePath = Join-Path $publishWorkDirectory $executableName
            & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr 'https://timestamp.acs.microsoft.com' /td SHA256 $executablePath
            if ($LASTEXITCODE -ne 0) { throw "signtool failed for $executableName with exit code $LASTEXITCODE." }
            & $signTool verify /pa /all /v $executablePath
            if ($LASTEXITCODE -ne 0) { throw "signtool verification failed for $executableName with exit code $LASTEXITCODE." }
        }
    } else {
        Set-Content -LiteralPath (Join-Path $publishWorkDirectory 'UNSIGNED-DEVELOPMENT-BUILD.txt') `
            -Value 'This build is unsigned and must not be distributed as a public release.' -Encoding UTF8
    }

    & (Join-Path $PSScriptRoot 'write-release-sbom.ps1') -PublishDirectory $publishWorkDirectory -Version $version -Runtime $Runtime
    Compress-Archive -Path (Join-Path $publishWorkDirectory '*') -DestinationPath $zipWorkPath -CompressionLevel Optimal
    & (Join-Path $PSScriptRoot 'verify-release.ps1') -PublishDirectory $publishWorkDirectory -ArchivePath $zipWorkPath `
        -AllowUnsigned:([string]::IsNullOrWhiteSpace($CertificateThumbprint))
    Compress-Archive -Path (Join-Path $root 'browser-extension\*') -DestinationPath $extensionZipWorkPath -CompressionLevel Optimal

    Assert-TrackerStopped -PublishDirectory $publishDirectory
    Install-ReleaseArtifacts -Boundary $releaseRoot -Items @(
        @{ Source=$publishWorkDirectory; Target=$publishDirectory },
        @{ Source=$zipWorkPath; Target=$zipPath },
        @{ Source=$extensionZipWorkPath; Target=$extensionZip }
    )

    function Get-Sha256([string]$path) {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        $stream = [System.IO.File]::OpenRead($path)
        try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $algorithm.Dispose() }
    }
    $hashTargets = @(Get-ChildItem -LiteralPath $releaseRoot -Filter "PCActivityTracker-$version-win-*.zip" -File |
        Sort-Object Name | Select-Object -ExpandProperty FullName)
    $hashTargets += $extensionZip
    $hashTargets | ForEach-Object { "$(Get-Sha256 $_)  $(Split-Path -Leaf $_)" } |
        Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ASCII
    Write-Host "Published: $publishDirectory"
    Write-Host "Desktop ZIP: $zipPath"
    Write-Host "Extension ZIP: $extensionZip"
}
finally {
    try {
        foreach ($workingPath in @($publishWorkDirectory, $zipWorkPath, $extensionZipWorkPath, $nativeHostWorkDirectory)) {
            Assert-ReleasePath $workingPath
            if (Test-Path -LiteralPath $workingPath) { Remove-Item -LiteralPath $workingPath -Recurse -Force }
        }
    } finally {
        Pop-Location
    }
}
