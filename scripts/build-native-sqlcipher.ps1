param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$GitRoot = $env:PCAT_BUILD_GIT_ROOT
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$native = Join-Path $root 'artifacts\native'
$pinsPath = Join-Path $root '.config\native-sources.json'
$pins = Get-Content -LiteralPath $pinsPath -Raw | ConvertFrom-Json
function Get-NativeHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
}
function Invoke-NativeTool([string]$Program, [string[]]$Arguments, [string]$Log) {
    $command = Get-Command $Program -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $savedPreference = $ErrorActionPreference
    try {
        # Windows PowerShell wraps native stderr as ErrorRecord, including
        # harmless compiler warnings. The native exit code is authoritative.
        $ErrorActionPreference = 'Continue'
        & $command.Source @Arguments *> $Log
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedPreference }
    if ($code -ne 0) {
        Get-Content -LiteralPath $Log -Tail 20 | Write-Host
        throw "Native build command failed ($code). See $Log"
    }
}
$out = Join-Path $native "out\$Runtime"
$stamp = Join-Path $out 'native-build.json'
$armConfig = Join-Path $PSScriptRoot 'native\openssl-arm64.conf'
$fingerprint = (Get-NativeHash $pinsPath) + ':' + (Get-NativeHash $PSCommandPath) + ':' + (Get-NativeHash $armConfig)
if (Test-Path -LiteralPath $stamp) {
    $previous = Get-Content -LiteralPath $stamp -Raw | ConvertFrom-Json
    $dll = Join-Path $out 'pcactivity_sqlcipher.dll'
    if ($previous.buildFingerprint -eq $fingerprint -and (Test-Path -LiteralPath $dll) -and
        (Get-NativeHash $dll) -eq $previous.unsignedDllSha256) {
        Write-Host "Verified native build cache: $Runtime"
        return
    }
}
if ($native -match '\s') { throw 'Native build tools require a project path without spaces.' }
if (-not $GitRoot) {
    $portable = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PCActivityBuildTools\PortableGit'
    $installed = Join-Path $env:ProgramFiles 'Git'
    if (Test-Path -LiteralPath (Join-Path $portable 'usr\bin\perl.exe')) { $GitRoot = $portable }
    elseif (Test-Path -LiteralPath (Join-Path $installed 'usr\bin\perl.exe')) { $GitRoot = $installed }
}
if (-not $GitRoot -or -not (Test-Path -LiteralPath (Join-Path $GitRoot 'usr\bin\perl.exe'))) {
    throw 'Install or extract Git for Windows (including Perl), then pass -GitRoot or set PCAT_BUILD_GIT_ROOT.'
}
$gitBin = Join-Path (Resolve-Path -LiteralPath $GitRoot).Path 'usr\bin'
New-Item -ItemType Directory -Path (Join-Path $native 'downloads'),(Join-Path $native 'sources'),(Join-Path $native 'tools'),$out -Force | Out-Null
foreach ($entry in $pins.PSObject.Properties) {
    $pin = $entry.Value
    $archive = Join-Path $native ('downloads\' + $pin.archive)
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Host "Downloading pinned build dependency: $($entry.Name) $($pin.version)"
        Invoke-WebRequest -UseBasicParsing -Uri $pin.url -OutFile $archive
    }
    if ((Get-NativeHash $archive) -ne $pin.sha256) { throw "SHA-256 mismatch: $($pin.archive)" }
    $parent = if ($entry.Name -eq 'llvmMingw') { Join-Path $native 'tools' } else { Join-Path $native 'sources' }
    if (-not (Test-Path -LiteralPath (Join-Path $parent $pin.folder))) {
        & tar -xf $archive -C $parent
        if ($LASTEXITCODE -ne 0) { throw "Archive extraction failed: $($pin.archive)" }
    }
}
$toolRoot = Join-Path $native ('tools\' + $pins.llvmMingw.folder)
$source = Join-Path $native ('sources\' + $pins.sqlcipher.folder)
$sslSource = Join-Path $native ('sources\' + $pins.openssl.folder)
$suffix = if ($Runtime -eq 'win-x64') { 'x64' } else { 'arm64' }
$compiler = if ($Runtime -eq 'win-x64') { 'x86_64-w64-mingw32-clang' } else { 'aarch64-w64-mingw32-clang' }
$saved = @{}
foreach ($name in @('PATH','PERL5LIB','MSYS2_ENV_CONV_EXCL','LC_ALL')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:PATH = (Join-Path $toolRoot 'bin') + ';' + $gitBin + ';' + $env:PATH
    $env:LC_ALL = 'C'
    $env:PERL5LIB = (@('Locale-Maketext-Simple','ExtUtils-MakeMaker','Pod-Usage','Pod-Simple','Pod-Escapes','podlators') | ForEach-Object {
        & (Join-Path $gitBin 'cygpath.exe') -u (Join-Path $native ('sources\' + $pins.perlModules.folder + '\cpan\' + $_ + '\lib'))
    }) -join ':'
    # MSYS must preserve this Unix-format search path across native make children.
    $env:MSYS2_ENV_CONV_EXCL = 'PERL5LIB'
    $amalgamation = Join-Path $native 'build\amalgamation'
    New-Item -ItemType Directory -Path $amalgamation -Force | Out-Null
    Push-Location $amalgamation
    try {
        Invoke-NativeTool 'mingw32-make' @('-f',($source.Replace('\','/') + '/Makefile.linux-generic'),'sqlite3.c',
            'B.cc=x86_64-w64-mingw32-clang','T.cc=x86_64-w64-mingw32-clang','B.exe=.exe','T.exe=.exe',
            'CFLAGS.jimsh=-DHAVE__FULLPATH','B.tclsh=./jimsh.exe','SHELL=sh.exe') 'build.log'
    } finally { Pop-Location }
    $sslBuild = Join-Path $native "build\openssl-$suffix"
    New-Item -ItemType Directory -Path $sslBuild -Force | Out-Null
    Push-Location $sslBuild
    try {
        $target = if ($Runtime -eq 'win-x64') { 'mingw64' } else { 'pcat-mingw-arm64' }
        $configuration = @(('../../sources/' + $pins.openssl.folder + '/Configure'),$target,
            'no-shared','no-tests','no-apps','no-module','no-dso','no-autoload-config',
            "CC=$compiler",'AR=llvm-ar','RANLIB=llvm-ranlib')
        if ($Runtime -eq 'win-arm64') { $configuration += '--config=' + ((Join-Path $root 'scripts\native\openssl-arm64.conf').Replace('\','/')) }
        $configurationKey = (($configuration + $pins.openssl.sha256 + $pins.llvmMingw.sha256 + (Get-NativeHash $armConfig)) | ConvertTo-Json -Compress)
        $configurationStamp = Join-Path $sslBuild 'configure.arguments.json'
        if (-not (Test-Path -LiteralPath $configurationStamp) -or -not (Test-Path -LiteralPath (Join-Path $sslBuild 'Makefile')) -or
            (Get-Content -LiteralPath $configurationStamp -Raw).Trim() -ne $configurationKey) {
            Invoke-NativeTool 'perl' $configuration 'configure.log'
            Set-Content -LiteralPath $configurationStamp -Value $configurationKey -Encoding UTF8
        }
        Invoke-NativeTool 'mingw32-make' @('-j2','build_libs','SHELL=sh.exe','PERL=perl') 'build.log'
    } finally { Pop-Location }
    $flags = @('-O2','-shared','-DSQLITE_API=__declspec(dllexport)','-DSQLITE_HAS_CODEC',
        '-DSQLITE_TEMP_STORE=2','-DSQLITE_THREADSAFE=1','-DSQLITE_EXTRA_INIT=sqlcipher_extra_init',
        '-DSQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown','-DSQLCIPHER_CRYPTO_OPENSSL',
        '-DSQLITE_OMIT_LOAD_EXTENSION','-DSQLITE_ENABLE_FTS5','-DSQLITE_ENABLE_COLUMN_METADATA',
        '-Wl,--exclude-all-symbols','-Wl,--no-insert-timestamp',"-ffile-prefix-map=$root=.")
    $dll = Join-Path $out 'pcactivity_sqlcipher.dll'
    Invoke-NativeTool $compiler ($flags + @(("-I"+(Join-Path $sslBuild 'include')),("-I"+(Join-Path $sslSource 'include')),
        (Join-Path $amalgamation 'sqlite3.c'),(Join-Path $sslBuild 'libcrypto.a'),'-lws2_32','-lcrypt32','-lbcrypt','-ladvapi32','-o',$dll)) (Join-Path $out 'compile.log')
    [ordered]@{
        schemaVersion = 1; runtime = $Runtime; sqlcipherVersion = $pins.sqlcipher.version
        sqliteVersion = (Get-Content -LiteralPath (Join-Path $source 'VERSION') -Raw).Trim()
        opensslVersion = $pins.openssl.version; toolchain = 'LLVM-MinGW ' + $pins.llvmMingw.version
        sourcePins = $pins; compileFlags = @($flags | Where-Object { $_ -notlike '-ffile-prefix-map=*' })
        buildFingerprint = $fingerprint; unsignedDllSha256 = Get-NativeHash $dll
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $stamp -Encoding UTF8
    Write-Host "Built SQLCipher $($pins.sqlcipher.version) / OpenSSL $($pins.openssl.version): $Runtime"
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
}
