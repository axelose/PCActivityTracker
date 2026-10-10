param(
    [ValidateSet('memory','browser','aggregation','settings','ui','migration')][string[]]$Suites = @('memory','browser','aggregation','settings','ui','migration'),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
$dotnet = Resolve-TrackerDotnet -Root $root
. (Join-Path $PSScriptRoot 'Public-Source-Checks.ps1')
function Get-RegressionFileHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}
function Get-SourceEvidence {
    $hasGit = Test-Path -LiteralPath (Join-Path $root '.git')
    $head = [pscustomobject]@{ Success=$false; Text='' }
    $status = [pscustomobject]@{ Success=$false; Text='' }
    if ($hasGit) {
        $head = Invoke-SourceGit $root 'rev-parse HEAD'
        $status = Invoke-SourceGit $root 'status --porcelain'
        # A present but inaccessible Git repository is a real access failure.
        $snapshot = Get-PublicSourceSnapshot $root
        $files = $snapshot.Files
    } else {
        # GitHub source archives have no .git. Enumerate source only, pruning all
        # generated/data directories without reading any history or secret files.
        function Get-ArchiveSourceFiles([string]$Directory) {
            foreach ($entry in (Get-ChildItem -LiteralPath $Directory -Force)) {
                if ($entry.PSIsContainer) {
                    if ($entry.Name -notin @('.git','.dotnet','artifacts','bin','obj','TestResults','coverage','.vs','.idea','.vscode') -and
                        -not ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                        Get-ArchiveSourceFiles $entry.FullName
                    }
                } else {
                    $relative = $entry.FullName.Substring($root.Length + 1).Replace('\','/')
                    if (-not (Test-ForbiddenSourcePath $relative) -and -not ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $relative }
                }
            }
        }
        $files = @(Get-ArchiveSourceFiles $root)
    }
    $hashes = @(
        foreach ($relative in ($files | Sort-Object)) {
            $path = Join-Path $root $relative
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                [ordered]@{ path=$relative.Replace('\','/'); sha256=Get-RegressionFileHash $path }
            } else { [ordered]@{ path=$relative.Replace('\','/'); missing=$true } }
        }
    )
    $data = [Text.Encoding]::UTF8.GetBytes(($hashes | ConvertTo-Json -Depth 4 -Compress))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($data))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    [ordered]@{ commit=if ($head.Success) { $head.Text.Trim() } else { $null };
        dirty=if ($status.Success) { [bool]$status.Text.Trim() } else { $null };
        fingerprint=$fingerprint; fileCount=$hashes.Count }
}
$started = [DateTimeOffset]::UtcNow
$evidence = Get-SourceEvidence
$browserPassed = $false
$corePassed = $false
$completed = $false
Push-Location -LiteralPath $root
try {
    $node = Get-Command node -CommandType Application -ErrorAction Stop
    & $node.Source --test (Join-Path $root 'tests\browser\bridge.test.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Browser extension regression tests failed.' }
    $browserPassed = $true
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'build-native-sqlcipher.ps1') -Runtime win-x64
        & $dotnet restore tests/CoreRegression/CoreRegression.csproj --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Locked regression restore failed.' }
        & $dotnet build tests/CoreRegression/CoreRegression.csproj -c Release --no-restore -p:UseSharedCompilation=false -nodeReuse:false
        if ($LASTEXITCODE -ne 0) { throw 'Core regression build failed.' }
    }
    & $dotnet (Join-Path $root 'tests\CoreRegression\bin\Release\net10.0-windows10.0.22000.0\CoreRegression.dll') @Suites
    if ($LASTEXITCODE -ne 0) { throw 'Core regression tests failed.' }
    $corePassed = $true
    $completed = $true
} finally {
    Pop-Location
    $after = Get-SourceEvidence
    $reportDirectory = Join-Path $root 'artifacts\verification'
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    [ordered]@{
        schemaVersion=1; startedUtc=$started.ToString('O'); completedUtc=[DateTimeOffset]::UtcNow.ToString('O')
        source=$evidence; sourceAfter=$after
        sourceUnchanged=($evidence.fingerprint -eq $after.fingerprint)
        buildSkipped=[bool]$SkipBuild; suites=$Suites
        browserPassed=$browserPassed; corePassed=$corePassed
        passed=($completed -and $evidence.fingerprint -eq $after.fingerprint)
        limitations=@('Synthetic local data only','No real-browser or ARM64 hardware test','No game FPS or 72-hour soak evidence')
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportDirectory 'regression-report.json') -Encoding UTF8
}
if ($evidence.fingerprint -ne $after.fingerprint) { throw 'Source changed during verification; rerun against stable source.' }
