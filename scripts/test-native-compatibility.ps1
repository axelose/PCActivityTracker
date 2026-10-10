param([string]$OldDll = '', [string]$NewDll = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
$dotnet = Resolve-TrackerDotnet -Root $root
if (-not $NewDll) { $NewDll = Join-Path $root 'artifacts\native\out\win-x64\pcactivity_sqlcipher.dll' }
$NewDll = (Resolve-Path -LiteralPath $NewDll).Path
if (-not $OldDll) {
    # Obsolete engine is a test-only reference, never a production dependency.
    $reference = Join-Path $root 'artifacts\native\compat-reference'
    New-Item -ItemType Directory -Path $reference -Force | Out-Null
    $package = Join-Path $reference 'sqlitepclraw.lib.e_sqlcipher.2.1.11.nupkg'
    if (-not (Test-Path -LiteralPath $package)) {
        Invoke-WebRequest -UseBasicParsing -Uri 'https://api.nuget.org/v3-flatcontainer/sqlitepclraw.lib.e_sqlcipher/2.1.11/sqlitepclraw.lib.e_sqlcipher.2.1.11.nupkg' -OutFile $package
    }
    $hash = [Security.Cryptography.SHA512]::Create()
    $stream = [IO.File]::OpenRead($package)
    try { $actual = [Convert]::ToBase64String($hash.ComputeHash($stream)) }
    finally { $stream.Dispose(); $hash.Dispose() }
    # Raw signed .nupkg SHA-512, NOT NuGet's normalized contentHash from a lock file.
    # This archive was also checked with dotnet nuget verify --all.
    if ($actual -ne 'BVLfqwGRTFPhKpSVCGyCL9SbSyaOIz0R/L7plnwWF6g8odRIKkol3j3NXmPQCnjRktMSTObESssUMRf/DdM5yA==') { throw 'Legacy test-reference checksum mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    $OldDll = Join-Path $reference 'e_sqlcipher.dll'
    try {
        $entry = $zip.GetEntry('runtimes/win-x64/native/e_sqlcipher.dll')
        if (-not $entry) { throw 'Legacy test engine was not found.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$OldDll,$true)
    } finally { $zip.Dispose() }
}
$OldDll = (Resolve-Path -LiteralPath $OldDll).Path
& $dotnet build (Join-Path $root 'tests\NativeCompatibility\NativeCompatibility.csproj') -c Release -p:RestoreLockedMode=true -p:UseSharedCompilation=false -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'Native compatibility probe build failed.' }
$probe = Join-Path $root 'tests\NativeCompatibility\bin\Release\net10.0\NativeCompatibility.dll'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture = Join-Path $temporaryRoot ('PCActivityTracker-native-compat-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
function Invoke-Probe([string]$Dll,[string]$File,[string]$Action,[string]$Version,[string]$Rows) {
    & $dotnet $probe $Dll $fixture $File $Action $Version $Rows
    if ($LASTEXITCODE -ne 0) { throw "Native compatibility test failed: $Action $File ($Version)" }
}
try {
    Invoke-Probe $OldDll 'database.db' 'create' '4.5.2' '2'
    Invoke-Probe $NewDll 'wal.db' 'verify' '4.19.0' '2'
    Invoke-Probe $NewDll 'database.db' 'append' '4.19.0' '3'
    Invoke-Probe $OldDll 'database.db' 'verify' '4.5.2' '3'
    Invoke-Probe $NewDll 'new.db' 'create' '4.19.0' '2'
    Invoke-Probe $OldDll 'new.db' 'verify' '4.5.2' '2'
    Invoke-Probe $NewDll 'database.db' 'wrong-key' '4.19.0' '0'
    $bytes = [IO.File]::ReadAllBytes((Join-Path $fixture 'new.db'))
    if ($bytes.Length -lt 8192) { throw 'The synthetic fixture is unexpectedly small.' }
    $bytes[4116] = $bytes[4116] -bxor 1
    [IO.File]::WriteAllBytes((Join-Path $fixture 'corrupt.db'), $bytes)
    Invoke-Probe $NewDll 'corrupt.db' 'corrupt' '4.19.0' '0'
    foreach ($file in Get-ChildItem -LiteralPath $fixture -File) {
        $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($file.FullName))
        if ($text.Contains('SQLite format 3') -or $text.Contains('SYNTHETIC_PRIVATE_MARKER') -or $text.Contains('SYNTHETIC_NEW_ENGINE_WRITE')) {
            throw 'A synthetic private marker or plaintext database header leaked to disk.'
        }
    }
    Write-Host 'Native engine compatibility passed: old/new round trips, WAL recovery, wrong key, corruption, and plaintext checks.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($temporaryRoot + '\PCActivityTracker-native-compat-', [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to clean an unexpected test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
