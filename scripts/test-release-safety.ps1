$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Safety.ps1')
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture = Join-Path $temporaryRoot ('PCActivityTracker-release-safety-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
function Assert-Equal($Actual,$Expected) {
    if ($Actual -ne $Expected) { throw "Expected '$Expected', received '$Actual'." }
}
try {
    $old = Join-Path $fixture 'app'
    $staged = Join-Path $fixture 'staged'
    New-Item -ItemType Directory -Path $old,$staged | Out-Null
    Set-Content -LiteralPath (Join-Path $old 'data.txt') -Value 'old'
    Set-Content -LiteralPath (Join-Path $staged 'data.txt') -Value 'new'
    $items = @(@{ Source=$staged; Target=$old })
    $locked = [IO.File]::Open((Join-Path $old 'data.txt'),'Open','Read','None')
    try {
        $rejected = $false
        try { Install-ReleaseArtifacts $fixture $items } catch {
            if ($_.Exception.Message -notlike 'Release files are in use*') { throw }
            $rejected = $true
        }
        Assert-Equal $rejected $true
        Assert-Equal (Test-Path $staged) $true
    } finally { $locked.Dispose() }
    Assert-Equal (Get-Content (Join-Path $old 'data.txt')) 'old'
    Write-Host 'PASS: locked files rejected without deleting old or staged files.'

    # Inject a failure during the second artifact promotion, not during rollback.
    $zipOld = Join-Path $fixture 'app.zip'
    $zipNew = Join-Path $fixture 'staged.zip'
    Set-Content -LiteralPath $zipOld -Value 'old-zip'
    Set-Content -LiteralPath $zipNew -Value 'new-zip'
    $items += @{ Source=$zipNew; Target=$zipOld }
    $script:moveCount = 0
    function Move-Item {
        param($LiteralPath,$Destination,$ErrorAction)
        $script:moveCount++
        if ($script:moveCount -eq 4) { throw 'Synthetic promotion failure' }
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -ErrorAction Stop
    }
    try {
        $rejected = $false
        try { Install-ReleaseArtifacts $fixture $items } catch {
            if ($_.Exception.Message -ne 'Synthetic promotion failure') { throw }
            $rejected = $true
        }
        Assert-Equal $rejected $true
    } finally { Remove-Item Function:\Move-Item }
    Assert-Equal (Get-Content (Join-Path $old 'data.txt')) 'old'
    Assert-Equal (Get-Content (Join-Path $staged 'data.txt')) 'new'
    Assert-Equal (Get-Content $zipOld) 'old-zip'
    Assert-Equal (Get-Content $zipNew) 'new-zip'
    Write-Host 'PASS: interrupted multi-artifact promotion rolled back.'
    Install-ReleaseArtifacts $fixture $items
    Assert-Equal (Get-Content (Join-Path $old 'data.txt')) 'new'
    Assert-Equal (Get-Content $zipOld) 'new-zip'
    $backup = @(Get-ChildItem $fixture -Directory -Filter 'app.previous-*')
    Assert-Equal $backup.Count 1
    Assert-Equal (Get-Content (Join-Path $backup[0].FullName 'data.txt')) 'old'
    Write-Host 'PASS: successful replacement retains the previous artifact.'
    function Get-Process { [pscustomobject]@{ ProcessName='PCActivityTracker'; Path=(Join-Path $old 'PCActivityTracker.exe') } }
    try {
        $rejected = $false
        try { Assert-TrackerStopped -PublishDirectory $old } catch {
            if ($_.Exception.Message -notlike 'Close PC Activity Tracker*') { throw }
            $rejected = $true
        }
        Assert-Equal $rejected $true
        Assert-TrackerStopped -PublishDirectory (Join-Path $fixture 'other-release')
    } finally { Remove-Item Function:\Get-Process }
    Write-Host 'PASS: running tracker prevents publishing.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($temporaryRoot + '\PCActivityTracker-release-safety-', [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to clean an unexpected test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
