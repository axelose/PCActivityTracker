# Shared by publish and synthetic regression tests. Never stops an application.
function Assert-TrackerStopped([string]$PublishDirectory = '') {
    $running = @(Get-Process -Name PCActivityTracker,PCActivityTracker.NativeHost -ErrorAction SilentlyContinue)
    if ($PublishDirectory) {
        $prefix = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\') + '\'
        # Older releases can have a browser host still connected. Only block
        # this destination; an unreadable executable path fails closed.
        $running = @($running | Where-Object { -not $_.Path -or $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    }
    if ($running.Count -gt 0) {
        throw 'Close PC Activity Tracker and its browser Native Host before publishing. No application files have been replaced.'
    }
}

function Install-ReleaseArtifacts([string]$Boundary, [object[]]$Items) {
    $boundaryPath = [IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    # Validate the entire transaction before moving anything.
    foreach ($item in $Items) {
        foreach ($path in @($item.Source,$item.Target)) {
            $full = [IO.Path]::GetFullPath($path)
            if (-not $full.StartsWith($boundaryPath + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $paths.Add($full)) {
                throw 'Release transaction paths must be unique and inside the release boundary.'
            }
            $ancestor = $full
            while ($ancestor -and $ancestor.Length -ge $boundaryPath.Length) {
                if ((Test-Path -LiteralPath $ancestor) -and
                    ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw 'Release transactions cannot follow redirected paths.'
                }
                $ancestor = Split-Path -Parent $ancestor
            }
        }
        if (-not (Test-Path -LiteralPath $item.Source)) { throw 'A staged release artifact is missing.' }
    }
    foreach ($path in $paths) {
        foreach ($other in $paths) {
            if ($path.StartsWith($other.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Release transaction paths must not be nested.'
            }
        }
    }
    # Detect file locks before replacement; directory renames + rollback also
    # preserve old artifacts if a lock appears after this preflight.
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $entries = @((Get-Item -LiteralPath $path -Force))
        if ($entries[0].PSIsContainer) { $entries += @(Get-ChildItem -LiteralPath $path -Recurse -Force) }
        foreach ($entry in $entries) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected release content is not allowed.' }
            if (-not $entry.PSIsContainer) {
                try {
                    $handle = [IO.File]::Open($entry.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                    $handle.Dispose()
                } catch { throw 'Release files are in use or inaccessible. Close the application before publishing; existing files were preserved.' }
            }
        }
    }
    $journal = [Collections.Generic.List[object]]::new()
    $suffix = '.previous-' + [Guid]::NewGuid().ToString('N')
    try {
        foreach ($item in $Items) {
            $record = [pscustomobject]@{ Source=$item.Source; Target=$item.Target; Backup=($item.Target + $suffix); Saved=$false; Installed=$false }
            $journal.Add($record)
            if (Test-Path -LiteralPath $item.Target) {
                Move-Item -LiteralPath $item.Target -Destination $record.Backup -ErrorAction Stop
                $record.Saved = $true
            }
            Move-Item -LiteralPath $item.Source -Destination $item.Target -ErrorAction Stop
            $record.Installed = $true
        }
    } catch {
        $failure = $_
        for ($index = $journal.Count - 1; $index -ge 0; $index--) {
            $record = $journal[$index]
            try {
                if ($record.Installed) { Move-Item -LiteralPath $record.Target -Destination $record.Source -ErrorAction Stop }
                if ($record.Saved) { Move-Item -LiteralPath $record.Backup -Destination $record.Target -ErrorAction Stop }
            } catch {
                Write-Warning "Rollback could not finish. Preserve and recover the previous artifact at: $($record.Backup)"
            }
        }
        throw $failure
    }
    # Do not recursively delete the old version: it remains recoverable even if
    # antivirus or a newly started process opens it after the transaction.
    foreach ($record in $journal) {
        if ($record.Saved) { Write-Host "Previous artifact retained: $($record.Backup)" }
    }
}
