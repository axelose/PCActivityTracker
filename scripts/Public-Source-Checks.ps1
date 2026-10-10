# Read Git's index objects directly, without checkout, filters, or index writes.
function Invoke-SourceGit([string]$Root, [string]$Arguments) {
    $git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $git.Source
    $info.Arguments = $Arguments
    $info.WorkingDirectory = $Root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $null = $stderr.Result # Do not echo repository output that may contain secrets.
        [pscustomobject]@{ Success=($process.ExitCode -eq 0); Text=$stdout.Result }
    } finally { $process.Dispose() }
}

function Get-PublicSourceSnapshot([string]$Root) {
    $index = Invoke-SourceGit $Root 'ls-files --stage -z'
    $others = Invoke-SourceGit $Root 'ls-files --others --exclude-standard -z'
    if (-not $index.Success -or -not $others.Success) { throw 'Git index could not be read.' }
    $entries = @(
        foreach ($record in $index.Text.Split([char]0)) {
            if (-not $record) { continue }
            if ($record -notmatch '(?s)^(?<mode>[0-7]{6}) (?<oid>[0-9a-f]{40}|[0-9a-f]{64}) (?<stage>[0-3])\t(?<path>.+)$') {
                throw 'Unexpected Git index entry.'
            }
            [pscustomobject]@{ Mode=$Matches.mode; Oid=$Matches.oid; Stage=[int]$Matches.stage; Path=$Matches.path }
        }
    )
    [pscustomobject]@{
        Entries=$entries
        Files=@(@($entries | ForEach-Object { $_.Path }) + @($others.Text.Split([char]0) | Where-Object { $_ }) | Select-Object -Unique)
    }
}

function Test-ForbiddenSourcePath([string]$Path) {
    $Path -match '(?i)(^|/)(artifacts|\.dotnet|bin|obj|TestResults|coverage)(/|$)|(^|/)(database\.key|.*\.(?:db|sqlite|sqlite3)(?:-wal|-shm|-journal)?|.*\.(?:pfx|p12|pem|key|snk|log|dmp|exe|dll|pdb|zip)|\.env(?:\.(?!example$).*)?)$'
}

function Test-PublicSourceText([string]$Path, [AllowEmptyString()][string]$Content, [string]$Origin) {
    # Report only file names and rule names, never a matched secret.
    $label = "$Origin : $Path"
    if ($Path -eq 'src/PCActivityTracker/SelfTestRunner.cs') {
        $fixturePath = [string]::Join('\', @('C:', 'Users', 'Private', ''))
        $Content = $Content.Replace($fixturePath.Replace('\','\\'), 'SYNTHETIC_TEST_PATH/').Replace($fixturePath, 'SYNTHETIC_TEST_PATH/')
    }
    if ($Content -match '(?i)[A-Z]:[\\/]+Users[\\/]+[a-z0-9_ .-]+[\\/]') {
        "User-specific absolute path found: $label"
    }
    if ($Content -match '-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----|(?:AKIA|ASIA)[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,}') {
        "Possible credential or private key found: $label"
    }
    if ($Path -match '^\.github/workflows/[^/]+\.ya?ml$') {
        $line = 0
        foreach ($value in $Content -split '\r?\n') {
            $line++
            if ($value -match '^\s*(?:-\s*)?uses:\s*(?<target>[^#]+)') {
                $target = $Matches.target.Trim().Trim('"', "'")
                if (-not $target.StartsWith('./', [StringComparison]::Ordinal) -and
                    $target -notmatch '^[\w.-]+/[\w./-]+@[0-9a-fA-F]{40}$') {
                    "GitHub Action is not pinned to a full commit SHA: $label : $line"
                }
            }
        }
    }
    if ($Path.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) {
        $tokens = $null; $errors = $null
        [Management.Automation.Language.Parser]::ParseInput($Content, [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors.Count) { "PowerShell parse error: $label" }
    }
}

function Test-PublicSourceIndex([string]$Root, [object[]]$Entries) {
    foreach ($entry in $Entries) {
        if ($entry.Stage -ne 0) { "Unmerged Git index entry: $($entry.Path)"; continue }
        if ($entry.Mode -notin @('100644','100755')) {
            "Unsupported Git index mode (symlink or submodule): $($entry.Path)"
            continue
        }
        if (Test-ForbiddenSourcePath $entry.Path) {
            "Sensitive or generated file in Git index: $($entry.Path)"
            continue
        }
        # Only repository image assets are exempt from textual pattern checks.
        if ([IO.Path]::GetExtension($entry.Path).ToLowerInvariant() -in @('.png','.ico')) { continue }
        if ($entry.Oid -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') { throw 'Invalid Git object identifier.' }
        $blob = Invoke-SourceGit $Root ("cat-file blob " + $entry.Oid)
        if (-not $blob.Success) { throw 'A staged Git object could not be read.' }
        Test-PublicSourceText $entry.Path $blob.Text 'Git index'
    }
}
