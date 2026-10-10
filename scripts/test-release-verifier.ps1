param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$drop = (Resolve-Path -LiteralPath $PublishDirectory).Path
$verifier = Join-Path $PSScriptRoot 'verify-release.ps1'
if (-not (Test-Path -LiteralPath (Join-Path $drop 'UNSIGNED-DEVELOPMENT-BUILD.txt'))) {
    throw 'These regression tests require a local unsigned development build.'
}
& $verifier -PublishDirectory $drop -ArchivePath $ArchivePath -AllowUnsigned

function Assert-Rejected([scriptblock]$Action, [string]$Expected) {
    $message = $null
    try { & $Action } catch { $message = $_.Exception.Message }
    if (-not $message -or -not $message.Contains($Expected)) {
        throw "Expected rejection '$Expected', received '$message'."
    }
    Write-Host "Rejection test passed: $Expected"
}
Assert-Rejected { & $verifier -PublishDirectory $drop -ArchivePath $ArchivePath } 'unsigned development build'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$verificationRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\verification')).TrimEnd('\')
$testRoot = Join-Path $verificationRoot ('release-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    # Synthetic ZIPs are separate from real artifacts; no activity data is used.
    $license = [IO.File]::ReadAllBytes((Join-Path $drop 'LICENSE'))
    foreach ($case in @('traversal','hash','duplicate','missing')) {
        $path = Join-Path $testRoot ($case + '.zip')
        $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
        try {
            if ($case -ne 'missing') {
                $name = if ($case -eq 'traversal') { '../escape.txt' } else { 'LICENSE' }
                $bytes = [byte[]]$license.Clone()
                if ($case -eq 'hash') { $bytes[0] = $bytes[0] -bxor 1 }
                $entry = $zip.CreateEntry($name)
                $stream = $entry.Open()
                try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
                if ($case -eq 'duplicate') { $null = $zip.CreateEntry('LICENSE') }
            }
        } finally { $zip.Dispose() }
        $expected = switch ($case) {
            'traversal' { 'Unsafe ZIP entry path' }
            'hash' { 'ZIP hash mismatch' }
            'duplicate' { 'Duplicate ZIP entry' }
            'missing' { 'ZIP is missing' }
        }
        Assert-Rejected { & $verifier -PublishDirectory $drop -ArchivePath $path -AllowUnsigned } $expected
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($verificationRoot + '\release-tests-', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a test directory outside the verification boundary.'
    }
    if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to clean a redirected test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host 'Release verifier regression tests passed.'
