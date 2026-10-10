param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][ValidateSet('win-x64','win-arm64')][string]$Runtime
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Get-Dotnet.ps1')
$dotnet = Resolve-TrackerDotnet -Root $root
$drop = (Resolve-Path -LiteralPath $PublishDirectory).Path
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\release')).TrimEnd('\')
if (-not $drop.StartsWith($releaseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SBOM output must be inside artifacts/release.'
}
if (Test-Path -LiteralPath (Join-Path $drop '_manifest')) { throw 'SBOM already exists; use a fresh staging directory.' }
$scanRoot = Join-Path $releaseRoot ('.sbom-input-' + [Guid]::NewGuid().ToString('N'))
$validationPath = Join-Path $scanRoot 'validation.json'
$oldRollForward = $env:DOTNET_ROLL_FORWARD
Push-Location -LiteralPath $root
try {
    $env:DOTNET_ROLL_FORWARD = 'Major'
    & $dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the pinned SBOM tool.' }
    New-Item -ItemType Directory -Path $scanRoot | Out-Null
    # Only dependency manifests are scanned. Never scan the workspace, user DB,
    # test artifacts, or diagnostic directories with the component detector.
    foreach ($project in @('PCActivityTracker','PCActivityTracker.NativeHost','LocalAi.Runtime')) {
        $projectInput = Join-Path $scanRoot $project
        New-Item -ItemType Directory -Path $projectInput | Out-Null
        Copy-Item -LiteralPath (Join-Path $root "src\$project\packages.lock.json") -Destination $projectInput
        # The NuGet detector requires PackageSpec/project metadata from resolved
        # assets. These temporary build paths are never copied into the release;
        # the generated SBOM is checked for local paths before publication.
        Copy-Item -LiteralPath (Join-Path $root "src\$project\obj\project.assets.json") -Destination $projectInput
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $drop 'PCActivityTracker.runtimeconfig.json') -Raw | ConvertFrom-Json
    $nativeInfo = Get-Content -LiteralPath (Join-Path $drop "runtimes\$Runtime\native\native-build.json") -Raw | ConvertFrom-Json
    $nativePackages = @()
    foreach ($component in @('sqlcipher','openssl','llvm-mingw-runtime')) {
        $pin = if ($component -eq 'llvm-mingw-runtime') { $nativeInfo.sourcePins.llvmMingw } else { $nativeInfo.sourcePins.$component }
        $nativePackages += [ordered]@{
            name = $component; SPDXID = "SPDXRef-$component"; versionInfo = $pin.version
            downloadLocation = $pin.url; filesAnalyzed = $false
            licenseConcluded = 'NOASSERTION'; licenseDeclared = switch ($component) { 'openssl' { 'Apache-2.0' }; 'sqlcipher' { 'BSD-3-Clause' }; default { 'NOASSERTION' } }
            copyrightText = 'NOASSERTION'
            checksums = @(@{ algorithm = 'SHA256'; checksumValue = $pin.sha256 })
            externalRefs = @(@{ referenceCategory = 'PACKAGE-MANAGER'; referenceType = 'purl'; referenceLocator = "pkg:generic/$component@$($pin.version)" })
        }
    }
    # Explicit source component inventory for our static native build. The
    # ordinary NuGet detector cannot see into a locally compiled DLL.
    [ordered]@{
        spdxVersion = 'SPDX-2.2'; dataLicense = 'CC0-1.0'; SPDXID = 'SPDXRef-DOCUMENT'
        name = 'PCActivityTracker native crypto sources'
        documentDescribes = @('SPDXRef-sqlcipher')
        documentNamespace = 'https://spdx.org/spdxdocs/pcactivity-native-' + [Guid]::NewGuid().ToString()
        creationInfo = @{ creators = @('Tool: PCActivityTracker native inventory'); created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') }
        packages = $nativePackages
        relationships = @(
            @{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = 'SPDXRef-sqlcipher' },
            @{ spdxElementId = 'SPDXRef-sqlcipher'; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = 'SPDXRef-openssl' },
            @{ spdxElementId = 'SPDXRef-sqlcipher'; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = 'SPDXRef-llvm-mingw-runtime' }
        )
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $scanRoot 'native.spdx.json') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $scanRoot 'native.spdx.json') -Destination (Join-Path $drop 'NATIVE-SBOM.spdx.json')
    $sdkVersion = (& $dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine SDK version.' }
    $buildInfo = [ordered]@{
        formatVersion = 1
        applicationVersion = $Version
        architecture = $Runtime
        sdkVersion = $sdkVersion
        runtimeFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks | ForEach-Object {
            [ordered]@{ name = $_.name; version = $_.version }
        })
        sbomToolVersion = (Get-Content -LiteralPath (Join-Path $root '.config\dotnet-tools.json') -Raw | ConvertFrom-Json).tools.'microsoft.sbom.dotnettool'.version
        nativeDatabase = @{ sqlcipher = $nativeInfo.sqlcipherVersion; sqlite = $nativeInfo.sqliteVersion; openssl = $nativeInfo.opensslVersion }
        provenance = 'Local build metadata only; not a signed provenance attestation.'
    }
    $buildInfo | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $drop 'BUILD-INFO.json') -Encoding UTF8
    & $dotnet tool run sbom-tool -- generate -b $drop -bc $scanRoot -pn PCActivityTracker -pv $Version `
        -ps 'PC Activity Tracker Project' -mi SPDX:2.2 -li false -F false -P 2 -V Warning
    if ($LASTEXITCODE -ne 0) { throw 'SBOM generation failed.' }
    & $dotnet tool run sbom-tool -- validate -b $drop -o $validationPath -mi SPDX:2.2 -V Warning
    if ($LASTEXITCODE -ne 0) { throw 'SBOM validation failed.' }
    $manifestPath = Join-Path $drop '_manifest\spdx_2.2\manifest.spdx.json'
    $manifestText = Get-Content -LiteralPath $manifestPath -Raw
    $manifest = $manifestText | ConvertFrom-Json
    $nativeBom = Get-Content -LiteralPath (Join-Path $drop 'NATIVE-SBOM.spdx.json') -Raw | ConvertFrom-Json
    if (-not ($manifest.externalDocumentRefs | Where-Object { $_.spdxDocument -eq $nativeBom.documentNamespace })) {
        throw 'The main SBOM does not reference the bundled native component inventory.'
    }
    if ($manifestText.Replace('\\', '\').IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $manifestText -match '(?i)[A-Z]:[\\/]+Users[\\/]') { throw 'SBOM contains a local user path.' }
    Write-Host 'SPDX SBOM generated and validated.'
} finally {
    $env:DOTNET_ROLL_FORWARD = $oldRollForward
    Pop-Location
    $resolvedScanRoot = [IO.Path]::GetFullPath($scanRoot)
    if ($resolvedScanRoot.StartsWith($releaseRoot + '\.sbom-input-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedScanRoot)) {
        if ((Get-Item -LiteralPath $resolvedScanRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Refusing to remove a redirected SBOM staging directory.'
        }
        Remove-Item -LiteralPath $resolvedScanRoot -Recurse -Force
    }
}
