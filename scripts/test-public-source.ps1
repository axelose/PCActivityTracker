$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Public-Source-Checks.ps1')
$root = Split-Path -Parent $PSScriptRoot
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture = Join-Path $temporaryRoot ('PCActivityTracker-source-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$cases = 0
function Run-Git([string]$Arguments) {
    $result = Invoke-SourceGit $fixture ('-c core.hooksPath=NUL -c core.fsmonitor=false -c core.autocrlf=false ' + $Arguments)
    if (-not $result.Success) { throw 'Synthetic repository setup failed.' }
    $result
}
function Get-IndexIssues {
    $snapshot = Get-PublicSourceSnapshot $fixture
    @(Test-PublicSourceIndex $fixture $snapshot.Entries)
}
function Assert-Result([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "Source inspection regression failed: $Label" }
    $script:cases++
    Write-Host "PASS: $Label"
}
try {
    $null = Run-Git 'init -q'
    Copy-Item -LiteralPath (Join-Path $root '.gitignore') -Destination (Join-Path $fixture '.gitignore')
    foreach ($name in @('activity.db-wal','activity.sqlite-journal','activity.sqlite3-wal',
        'activity.sqlite3-shm','activity.sqlite3-journal','crash.dmp')) {
        $result = Invoke-SourceGit $fixture ("check-ignore --no-index -- " + $name)
        Assert-Result $result.Success "ignored: $name"
    }
    $sample = Join-Path $fixture 'sample.pubxml'
    # Deliberately fake token, constructed so it is not a secret in test source.
    $fakeToken = ('gh' + 'p' + '_') + ('A' * 32)
    Set-Content -LiteralPath $sample -Value $fakeToken -Encoding UTF8
    $null = Run-Git 'add -- sample.pubxml'
    Set-Content -LiteralPath $sample -Value '<Project />' -Encoding UTF8
    $issues = @(Get-IndexIssues)
    Assert-Result ([bool]($issues -like 'Possible credential*Git index*sample.pubxml')) 'staged secret survives worktree cleanup and pubxml is scanned'
    Assert-Result (-not (($issues -join ' ').Contains($fakeToken))) 'findings do not print secret values'
    $null = Run-Git 'add -- sample.pubxml'
    Assert-Result (@(Get-IndexIssues).Count -eq 0) 'restaging clean content clears the finding'
    Set-Content -LiteralPath $sample -Value $fakeToken -Encoding UTF8
    $issues = @(Test-PublicSourceText 'sample.pubxml' (Get-Content $sample -Raw) 'Working tree')
    Assert-Result ($issues.Count -eq 1 -and @(Get-IndexIssues).Count -eq 0) 'unstaged secret is checked independently of clean index'
    $null = Run-Git 'add -- sample.pubxml'
    Remove-Item -LiteralPath $sample
    Assert-Result (@(Get-IndexIssues).Count -eq 1) 'worktree deletion does not hide staged content'
    $null = Run-Git 'rm --cached -- sample.pubxml'
    Assert-Result (@(Get-IndexIssues).Count -eq 0) 'staged deletion removes the obsolete index content'
    foreach ($name in @('activity.sqlite3-wal','crash.dmp')) {
        Set-Content -LiteralPath (Join-Path $fixture $name) -Value 'synthetic data'
        $null = Run-Git ("add -f -- " + $name)
    }
    Assert-Result (@(Get-IndexIssues).Count -eq 2) 'force-added ignored data is rejected in index'
    $null = Run-Git 'rm --cached -- activity.sqlite3-wal crash.dmp'
    $unicodeName = '日本語 space.txt'
    Set-Content -LiteralPath (Join-Path $fixture $unicodeName) -Value $fakeToken -Encoding UTF8
    $null = Run-Git 'add -- .'
    $snapshot = Get-PublicSourceSnapshot $fixture
    Assert-Result ([bool]($snapshot.Entries | Where-Object { $_.Path -eq $unicodeName })) 'NUL-delimited UTF-8 paths preserve spaces and Japanese'
    Assert-Result (@(Get-IndexIssues).Count -eq 1) 'secret in a Unicode path is rejected'
    $null = Run-Git 'rm -r --cached -- .'
    New-Item -ItemType Directory -Path (Join-Path $fixture '.github/workflows') -Force | Out-Null
    $workflow = Join-Path $fixture '.github/workflows/test.yml'
    Set-Content -LiteralPath $workflow -Value '- uses: actions/checkout@main'
    $null = Run-Git 'add -- .github/workflows/test.yml'
    Set-Content -LiteralPath $workflow -Value ('- uses: actions/checkout@' + ('a' * 40))
    Assert-Result ([bool](@(Get-IndexIssues) -like 'GitHub Action is not pinned*Git index*')) 'unpinned staged workflow is not hidden by worktree fix'
    $null = Run-Git 'add -- .github/workflows/test.yml'
    $scriptFile = Join-Path $fixture 'broken.ps1'
    Set-Content -LiteralPath $scriptFile -Value 'function Broken {'
    $null = Run-Git 'add -- broken.ps1'
    Set-Content -LiteralPath $scriptFile -Value 'Write-Host ok'
    Assert-Result ([bool](@(Get-IndexIssues) -like 'PowerShell parse error*Git index*')) 'staged PowerShell syntax is inspected'
    $entries = @([pscustomobject]@{ Path='conflicted.txt'; Stage=1; Mode='100644'; Oid=('a' * 40) })
    Assert-Result ([bool](@(Test-PublicSourceIndex $fixture $entries) -like 'Unmerged*')) 'unmerged index fails closed'
    $entries = @([pscustomobject]@{ Path='link'; Stage=0; Mode='120000'; Oid=('a' * 40) })
    Assert-Result ([bool](@(Test-PublicSourceIndex $fixture $entries) -like 'Unsupported Git index mode*')) 'symlink index entry fails closed'
    Write-Host "Public-source regression tests passed: $cases cases."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($temporaryRoot + '\PCActivityTracker-source-test-', [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to clean an unexpected test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
