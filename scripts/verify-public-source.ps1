param([switch]$RequireGit)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()
. (Join-Path $PSScriptRoot 'Public-Source-Checks.ps1')

function Add-Failure([string]$message) {
    $failures.Add($message)
}

$requiredFiles = @(
    '.config\native-sources.json', 'scripts\build-native-sqlcipher.ps1',
    'scripts\test-native-compatibility.ps1', 'licenses\OpenSSL-LICENSE.txt',
    'licenses\LLVM-LICENSE.txt', 'licenses\MinGW-RUNTIME-LICENSE.txt',
    '.config\dotnet-tools.json', 'scripts\write-release-sbom.ps1',
    'scripts\verify-release.ps1', 'scripts\test-release-verifier.ps1',
    'scripts\Release-Safety.ps1', 'scripts\test-release-safety.ps1',
    'scripts\Public-Source-Checks.ps1', 'scripts\test-public-source.ps1',
    'docs\CODE_SIGNING.md', 'docs\NATIVE_DATABASE_UPGRADE.md',
    'README.md', 'LICENSE', 'PRIVACY.md', 'SECURITY.md', 'SUPPORT.md',
    'CODE_OF_CONDUCT.md', 'CONTRIBUTING.md', 'THIRD-PARTY-NOTICES.md',
    'docs\THREAT_MODEL.md', 'docs\RELEASE_CHECKLIST.md', 'docs\GITHUB_RELEASE.md',
    'licenses\Apache-2.0.txt', 'licenses\SQLCipher-LICENSE.txt',
    '.github\PULL_REQUEST_TEMPLATE.md',
    '.github\ISSUE_TEMPLATE\bug_report.yml',
    '.github\ISSUE_TEMPLATE\feature_request.yml'
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relativePath) -PathType Leaf)) {
        Add-Failure "Required public file is missing: $relativePath"
    }
}

try {
    $toolManifest = Get-Content -LiteralPath (Join-Path $root '.config\dotnet-tools.json') -Raw | ConvertFrom-Json
    if ($toolManifest.tools.'microsoft.sbom.dotnettool'.version -notmatch '^\d+\.\d+\.\d+$') {
        Add-Failure 'Microsoft SBOM Tool must be pinned to an exact stable version.'
    }
    [xml]$buildProps = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $desktopVersion = [string]$buildProps.Project.PropertyGroup.Version
    $extensionManifest = Get-Content -LiteralPath (Join-Path $root 'browser-extension\manifest.json') -Raw | ConvertFrom-Json
    $extensionVersion = [string]$extensionManifest.version
    $bridgeSource = Get-Content -LiteralPath (Join-Path $root 'src\PCActivityTracker\Browser\BrowserNativeMessagingServer.cs') -Raw
    if ($bridgeSource -notmatch 'ExpectedExtensionVersion\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"') {
        Add-Failure 'ExpectedExtensionVersion was not found in BrowserNativeMessagingServer.cs.'
    } else {
        $bridgeVersion = $Matches.version
        if ($extensionVersion -ne $bridgeVersion) {
            Add-Failure "Version mismatch: desktop=$desktopVersion, extension=$extensionVersion, bridge=$bridgeVersion"
        }
    }
} catch {
    Add-Failure "Could not validate product versions: $($_.Exception.Message)"
}

foreach ($relativePath in @(
    'src\LocalAi.Runtime\packages.lock.json',
    'src\PCActivityTracker\packages.lock.json',
    'src\PCActivityTracker.NativeHost\packages.lock.json'
    'tests\CoreRegression\packages.lock.json'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relativePath) -PathType Leaf)) {
        Add-Failure "NuGet lock file is missing: $relativePath (run dotnet restore --force-evaluate)"
    }
}

$gitIgnore = Get-Content -LiteralPath (Join-Path $root '.gitignore') -Raw
foreach ($requiredPattern in @('artifacts/', '.dotnet/', '*.db', 'database.key', '*.pfx', '*.sqlite-journal', '*.sqlite3-wal', '*.sqlite3-shm', '*.sqlite3-journal', '*.dmp')) {
    if ($gitIgnore.IndexOf($requiredPattern, [StringComparison]::Ordinal) -lt 0) {
        Add-Failure ".gitignore does not protect: $requiredPattern"
    }
}

$candidateFiles = @()
$gitChecked = $false
$indexCount = 0
$git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue
if ($git -and (Test-Path -LiteralPath (Join-Path $root '.git'))) {
    $snapshot = $null
    try {
        $snapshot = Get-PublicSourceSnapshot $root
    } catch {
        # Source ZIP / untrusted-owner fallback is allowed only without RequireGit.
    }
    if ($null -ne $snapshot) {
      try {
        $candidateFiles = $snapshot.Files
        $indexCount = @($snapshot.Entries).Count
        foreach ($issue in @(Test-PublicSourceIndex $root $snapshot.Entries)) { Add-Failure $issue }
        $gitChecked = $true
      } catch {
        # Fail closed on enumeration or object-read errors; no trust-policy changes.
        Add-Failure 'Git index inspection failed. Resolve repository access and retry.'
      }
    }
}
if (-not $gitChecked) {
    if ($RequireGit) { Add-Failure 'Git index could not be checked. Run git status and resolve repository ownership/access before publication.' }
    Write-Warning 'Git index check unavailable. Checking source files only; this is not approval to publish a Git index.'
    function Get-SourceFiles([string]$directory) {
        Get-ChildItem -LiteralPath $directory -Force | ForEach-Object {
            if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                Add-Failure 'A redirected source entry cannot be inspected safely.'
            } elseif ($_.PSIsContainer) {
                if ($_.Name -notin @('.git', '.dotnet', 'artifacts', 'bin', 'obj', '.vs', '.idea', '.vscode', 'TestResults', 'coverage')) {
                    Get-SourceFiles $_.FullName
                }
            } else { $_.FullName.Substring($root.Length + 1).Replace('\', '/') }
        }
    }
    $candidateFiles = @(Get-SourceFiles $root)
}
foreach ($candidateFile in $candidateFiles | Select-Object -Unique) {
    if (Test-ForbiddenSourcePath $candidateFile) {
        Add-Failure "Sensitive or generated file is a source candidate: $candidateFile"
        continue
    }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $root $candidateFile))
    if (-not $fullPath.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Add-Failure 'A source candidate escapes the repository.'
        continue
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        Add-Failure "Git candidate is missing in the working directory; index was checked separately: $candidateFile"
        continue
    }
    $redirected = $false
    $ancestor = $fullPath
    while ($ancestor.Length -gt $root.Length) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { $redirected = $true; break }
        $ancestor = Split-Path -Parent $ancestor
    }
    if ($redirected) { Add-Failure "Redirected source candidate: $candidateFile"; continue }
    if ([IO.Path]::GetExtension($candidateFile).ToLowerInvariant() -notin @('.png','.ico')) {
        $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
        foreach ($issue in @(Test-PublicSourceText $candidateFile $content 'Working tree')) { Add-Failure $issue }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    throw "Public-source verification failed with $($failures.Count) issue(s)."
}

Write-Host "Source verification passed. Version: $desktopVersion; Git index checked: $gitChecked; index entries: $indexCount; source candidates: $(@($candidateFiles).Count)"
