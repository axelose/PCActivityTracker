param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$ArchivePath,
    [switch]$AllowUnsigned
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
$dotnet = Resolve-TrackerDotnet -Root $root
$drop = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$buildInfo = Get-Content -LiteralPath (Join-Path $drop 'BUILD-INFO.json') -Raw | ConvertFrom-Json
if ($buildInfo.architecture -notin @('win-x64','win-arm64')) { throw 'Invalid release architecture.' }
$ownBinaries = @('PCActivityTracker.exe','PCActivityTracker.dll',
    'PCActivityTracker.NativeHost.exe','PCActivityTracker.NativeHost.dll','LocalAi.Runtime.dll',
    "runtimes\$($buildInfo.architecture)\native\pcactivity_sqlcipher.dll")
$required = $ownBinaries + @('BUILD-INFO.json','LICENSE','PRIVACY.md','SECURITY.md',
    'DOTNET-LICENSE.txt','DOTNET-THIRD-PARTY-NOTICES.txt','licenses\Apache-2.0.txt',
    'licenses\SQLCipher-LICENSE.txt','licenses\OpenSSL-LICENSE.txt','licenses\LLVM-LICENSE.txt',
    'licenses\MinGW-RUNTIME-LICENSE.txt',"runtimes\$($buildInfo.architecture)\native\native-build.json",
    'NATIVE-SBOM.spdx.json','_manifest\spdx_2.2\manifest.spdx.json')
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $drop $file) -PathType Leaf)) { throw "Missing redistribution file: $file" }
}
$developmentMarker = Test-Path -LiteralPath (Join-Path $drop 'UNSIGNED-DEVELOPMENT-BUILD.txt')
if (-not $AllowUnsigned -and $developmentMarker) { throw 'An unsigned development build cannot pass public-release verification.' }
if ($AllowUnsigned -and -not $developmentMarker) { throw 'Development verification requires the unsigned-development marker.' }
if (-not $AllowUnsigned) {
    foreach ($binary in $ownBinaries) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $drop $binary)
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate) {
            throw "A valid timestamped signature is required: $binary"
        }
    }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $drop 'PCActivityTracker.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtimeConfig.runtimeOptions.includedFrameworks) { throw 'The desktop build is not self-contained.' }
if ($buildInfo.architecture -notin @('win-x64','win-arm64')) { throw 'Invalid release architecture.' }
$executableVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $drop 'PCActivityTracker.exe')).FileVersion
if ($executableVersion -ne "$($buildInfo.applicationVersion).0") { throw 'Executable and build-info versions do not match.' }
$nativeRelative = "runtimes\$($buildInfo.architecture)\native\pcactivity_sqlcipher.dll"
$nativeMetadata = Get-Content -LiteralPath (Join-Path $drop "runtimes\$($buildInfo.architecture)\native\native-build.json") -Raw | ConvertFrom-Json
if ($nativeMetadata.runtime -ne $buildInfo.architecture -or $nativeMetadata.sqlcipherVersion -ne '4.19.0' -or
    $nativeMetadata.sqliteVersion -ne '3.53.4' -or $nativeMetadata.opensslVersion -ne '3.5.8') { throw 'Unexpected native database metadata.' }
$nativeStream = [IO.File]::OpenRead((Join-Path $drop $nativeRelative))
$reader = [IO.BinaryReader]::new($nativeStream)
try {
    if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Invalid native DLL DOS header.' }
    $nativeStream.Position = 0x3c
    $offset = $reader.ReadInt32()
    if ($offset -lt 64 -or $offset -gt $nativeStream.Length - 6) { throw 'Invalid native DLL PE offset.' }
    $nativeStream.Position = $offset
    if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid native DLL PE header.' }
    $expectedMachine = if ($buildInfo.architecture -eq 'win-x64') { 0x8664 } else { 0xAA64 }
    if ($reader.ReadUInt16() -ne $expectedMachine) { throw 'Native DLL architecture does not match the package.' }
} finally { $reader.Dispose() }
$nativeBom = Get-Content -LiteralPath (Join-Path $drop 'NATIVE-SBOM.spdx.json') -Raw | ConvertFrom-Json
foreach ($component in @('sqlcipher','openssl')) {
    if (-not ($nativeBom.packages | Where-Object { $_.name -eq $component -and $_.versionInfo -eq $nativeMetadata.sourcePins.$component.version })) {
        throw "Native SBOM component mismatch: $component"
    }
}

$files = @(Get-ChildItem -LiteralPath $drop -Recurse -Force)
foreach ($item in $files) {
    if ($item.Name -match '^(e_sqlcipher\.dll|SQLitePCLRaw\..*\.dll)$') { throw 'An obsolete database dependency was packaged.' }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release artifacts must not contain symbolic links or junctions.' }
    if (-not $item.PSIsContainer -and $item.Name -match '(?i)(\.(db|sqlite|sqlite3)(-wal|-shm|-journal)?$|\.(key|pfx|p12|pem|snk|log|dmp)$|^diagnostics.*\.zip$|^\.env($|\.))') {
        throw "Local data or key material found in release: $($item.Name)"
    }
}
# Compare every ZIP entry to the already verified folder without extracting.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
try {
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name.Contains(':') -or $name -match '(^|/)\.\.(/|$)') { throw 'Unsafe ZIP entry path.' }
        if ($name.EndsWith('/')) { continue }
        if (-not $seen.Add($name)) { throw "Duplicate ZIP entry: $name" }
        $path = [IO.Path]::GetFullPath((Join-Path $drop $name))
        if (-not $path.StartsWith($drop + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP entry escapes release directory.' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Unexpected ZIP file: $name" }
        $source = Get-Item -LiteralPath $path
        if ($entry.Length -ne $source.Length) { throw "ZIP size mismatch: $name" }
        $stream = $entry.Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $zipHash = ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose(); $hash.Dispose() }
        $sourceStream = [IO.File]::OpenRead($path)
        $sourceHashAlgorithm = [Security.Cryptography.SHA256]::Create()
        try { $sourceHash = ([BitConverter]::ToString($sourceHashAlgorithm.ComputeHash($sourceStream))).Replace('-', '') }
        finally { $sourceStream.Dispose(); $sourceHashAlgorithm.Dispose() }
        if ($zipHash -ne $sourceHash) { throw "ZIP hash mismatch: $name" }
    }
    foreach ($item in $files | Where-Object { -not $_.PSIsContainer }) {
        $relative = $item.FullName.Substring($drop.Length + 1).Replace('\', '/')
        if (-not $seen.Contains($relative)) { throw "ZIP is missing: $relative" }
    }
} finally { $zip.Dispose() }

$resultDirectory = Join-Path $root 'artifacts\verification'
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$resultPath = Join-Path $resultDirectory ('sbom-' + [Guid]::NewGuid().ToString('N') + '.json')
$oldRollForward = $env:DOTNET_ROLL_FORWARD
Push-Location -LiteralPath $root
try {
    $env:DOTNET_ROLL_FORWARD = 'Major'
    & $dotnet tool run sbom-tool -- validate -b $drop -o $resultPath -mi SPDX:2.2 -V Warning
    if ($LASTEXITCODE -ne 0) { throw 'SBOM file-integrity verification failed.' }
} finally { $env:DOTNET_ROLL_FORWARD = $oldRollForward; Pop-Location }
Write-Host "Package structural verification passed: $($buildInfo.applicationVersion) $($buildInfo.architecture); development=$([bool]$AllowUnsigned)"
