function Resolve-TrackerDotnet {
    param(
        [string]$Root = (Split-Path -Parent $PSScriptRoot),
        [switch]$AllowMissing
    )

    $Root = [IO.Path]::GetFullPath($Root)
    $sdk = (Get-Content -LiteralPath (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json).sdk
    if (-not $sdk.version) { throw 'global.json must specify the required .NET SDK version.' }

    $candidates = @((Join-Path $Root '.dotnet\dotnet.exe'))
    $pathDotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($pathDotnet) { $candidates += $pathDotnet.Source }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        # --version resolves global.json from WorkingDirectory. Checking only
        # --list-sdks could select a host that cannot satisfy this repository.
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo.FileName = $candidate
        $process.StartInfo.Arguments = '--version'
        $process.StartInfo.WorkingDirectory = $Root
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        try {
            if (-not $process.Start()) { continue }
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            $selectedVersion = $stdout.GetAwaiter().GetResult().Trim()
            $null = $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -eq 0 -and $selectedVersion -match '^\d+\.\d+\.\d+') {
                return $candidate
            }
        } finally {
            $process.Dispose()
        }
    }

    if ($AllowMissing) { return $null }
    throw "The .NET SDK required by global.json ($($sdk.version), rollForward=$($sdk.rollForward)) was not found. Run scripts\bootstrap-dotnet.cmd."
}
