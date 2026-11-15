[CmdletBinding()]
param([string]$ToolchainRoot, [string]$CMakePath, [string]$NinjaPath, [int]$Jobs = 8)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $workspace 'build/quiet'
$outputRoot = Join-Path $workspace 'build/audio'
$logs = Join-Path $workspace 'build/quiet-logs'
New-Item -ItemType Directory -Force -Path $buildRoot, $outputRoot, $logs | Out-Null
if (-not $ToolchainRoot) { $ToolchainRoot = Join-Path $workspace 'build/quiet-tools/llvm-mingw-20250114-ucrt-x86_64' }
$ToolchainRoot = [IO.Path]::GetFullPath($ToolchainRoot)
if (-not (Test-Path -LiteralPath (Join-Path $ToolchainRoot 'bin/x86_64-w64-mingw32-clang.exe'))) {
    $archive = Join-Path $workspace 'build/quiet-downloads/llvm-mingw-20250114-ucrt-x86_64.zip'
    New-Item -ItemType Directory -Force -Path (Split-Path $archive), (Split-Path $ToolchainRoot) | Out-Null
    if (-not (Test-Path -LiteralPath $archive)) {
        & curl.exe -sSL --fail --retry 3 -o $archive 'https://github.com/mstorsjo/llvm-mingw/releases/download/20250114/llvm-mingw-20250114-ucrt-x86_64.zip'
        if ($LASTEXITCODE -ne 0) { throw 'Portable compiler download failed.' }
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash -ne '9395D95C089611E6CD728C9A07219FFC5AC706E0C719833A2F7E64A706A81541') { throw 'Portable compiler archive SHA256 mismatch.' }
    & tar.exe -xf $archive -C (Split-Path $ToolchainRoot)
    if ($LASTEXITCODE -ne 0) { throw 'Portable compiler extraction failed.' }
}
if (-not $CMakePath) {
    $command = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if ($command) { $CMakePath = $command.Source }
}
if (-not $CMakePath -or -not $NinjaPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installations = @(& $vswhere -all -products '*' -property installationPath)
        foreach ($installation in $installations) {
            $cmakeTools = Join-Path $installation 'Common7/IDE/CommonExtensions/Microsoft/CMake'
            $cmakeCandidate = Join-Path $cmakeTools 'CMake/bin/cmake.exe'
            $ninjaCandidate = Join-Path $cmakeTools 'Ninja/ninja.exe'
            if (-not $CMakePath -and (Test-Path -LiteralPath $cmakeCandidate)) { $CMakePath = $cmakeCandidate }
            if (-not $NinjaPath -and (Test-Path -LiteralPath $ninjaCandidate)) { $NinjaPath = $ninjaCandidate }
        }
    }
}
if (-not $NinjaPath) {
    $command = Get-Command ninja.exe -ErrorAction SilentlyContinue
    if ($command) { $NinjaPath = $command.Source }
}
if (-not $CMakePath -or -not (Test-Path -LiteralPath $CMakePath)) { throw 'Install CMake or pass -CMakePath.' }
if (-not $NinjaPath -or -not (Test-Path -LiteralPath $NinjaPath)) { throw 'Install Ninja or pass -NinjaPath.' }
$compiler = Join-Path $ToolchainRoot 'bin/x86_64-w64-mingw32-clang.exe'
$cppCompiler = Join-Path $ToolchainRoot 'bin/x86_64-w64-mingw32-clang++.exe'
$common = @('-G', 'Ninja', ('-DCMAKE_MAKE_PROGRAM=' + $NinjaPath), ('-DCMAKE_C_COMPILER=' + $compiler), '-DCMAKE_BUILD_TYPE=Release')
function Invoke-QuietCMake([string]$LogName, [string[]]$Arguments) {
    $log = Join-Path $logs ($LogName + '.log')
    # Windows PowerShell turns native stderr warnings into terminating errors
    # with ErrorActionPreference=Stop; the native exit status decides success.
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $CMakePath @Arguments *> $log
        $nativeExitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedPreference }
    if ($nativeExitCode -ne 0) { Get-Content -LiteralPath $log -Tail 60; throw ('Quiet build failed: ' + $LogName) }
}
function Build-QuietTarget([string]$Directory, [string]$Target) {
    Invoke-QuietCMake ($Target + '-build') @('--build', $Directory, '--target', $Target, '-j', $Jobs)
}
Push-Location $workspace
try {
    Invoke-QuietCMake 'fec-configure' (@('-S', 'third_party/quiet/libfec', '-B', 'build/quiet/libfec') + $common + @('-DBUILD_SHARED_LIBS=OFF', '-DCMAKE_C_FLAGS=-Drandom=rand'))
    Build-QuietTarget 'build/quiet/libfec' 'fec'
    Invoke-QuietCMake 'jansson-configure' (@('-S', 'third_party/quiet/jansson', '-B', 'build/quiet/jansson') + $common + @('-DJANSSON_BUILD_SHARED_LIBS=OFF', '-DJANSSON_BUILD_DOCS=OFF', '-DJANSSON_WITHOUT_TESTS=ON', '-DJANSSON_EXAMPLES=OFF'))
    Build-QuietTarget 'build/quiet/jansson' 'jansson'
    Invoke-QuietCMake 'liquid-configure' (@('-S', 'third_party/quiet/liquid-dsp', '-B', 'build/quiet/liquid-dsp') + $common + @(
        ('-DCMAKE_CXX_COMPILER=' + $cppCompiler), '-DLIQUID_BUILD_SHARED=OFF', '-DLIQUID_BUILD_STATIC=ON', '-DLIQUID_BUILD_EXAMPLES=OFF', '-DLIQUID_BUILD_SANDBOX=OFF',
        '-DLIQUID_SIMDOVERRIDE=ON', '-DLIQUID_FFTOVERRIDE=ON', '-D_CPUID_CHARACTERISTICS=@sse2@',
        ('-Dfec_include=' + (Join-Path $workspace 'third_party/quiet/libfec')), ('-Dfec_library=' + (Join-Path $buildRoot 'libfec/libfec.a'))))
    Build-QuietTarget 'build/quiet/liquid-dsp' 'liquid-static'
    New-Item -ItemType Directory -Force -Path (Join-Path $buildRoot 'include/liquid') | Out-Null
    Copy-Item -LiteralPath 'third_party/quiet/liquid-dsp/include/liquid.h' -Destination (Join-Path $buildRoot 'include/liquid/liquid.h') -Force
    Invoke-QuietCMake 'adapter-configure' (@('-S', 'native/audio/quiet', '-B', 'build/quiet/adapter') + $common)
    Build-QuietTarget 'build/quiet/adapter' 'quiet_roundtrip'
    & './native/audio/bin/quiet_roundtrip.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Quiet real native roundtrip checks failed.' }
    Copy-Item -LiteralPath 'native/audio/bin/horizon_quiet.dll' -Destination (Join-Path $outputRoot 'horizon-quiet.dll') -Force
    $readobj = Join-Path $ToolchainRoot 'bin/llvm-readobj.exe'
    $imports = & $readobj --coff-imports (Join-Path $outputRoot 'horizon-quiet.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Quiet DLL import inspection failed.' }
    $imports | Set-Content -LiteralPath (Join-Path $outputRoot 'quiet-imports.txt') -Encoding UTF8
    $dllNames = @($imports | Select-String '^  Name: (.+)$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    foreach ($dllName in $dllNames) { if ($dllName -notmatch '^(KERNEL32\.dll|api-ms-win-crt-.+\.dll)$') { throw ('Unexpected Quiet runtime dependency: ' + $dllName) } }
    # A unique temporary directory avoids deleting or moving any existing tree.
    $stage = Join-Path $buildRoot ('package-' + [Guid]::NewGuid().ToString('N'))
    $sourceStage = Join-Path $stage 'source'
    $relinkStage = Join-Path $stage 'relink'
    New-Item -ItemType Directory -Force -Path (Join-Path $sourceStage 'third_party'), (Join-Path $sourceStage 'native/audio'), (Join-Path $sourceStage 'scripts'), (Join-Path $relinkStage 'objects'), (Join-Path $relinkStage 'libraries') | Out-Null
    Copy-Item -LiteralPath 'third_party/quiet' -Destination (Join-Path $sourceStage 'third_party/quiet') -Recurse
    Copy-Item -LiteralPath 'native/audio/quiet' -Destination (Join-Path $sourceStage 'native/audio/quiet') -Recurse
    Copy-Item -LiteralPath 'scripts/Build-Quiet.ps1' -Destination (Join-Path $sourceStage 'scripts/Build-Quiet.ps1')
    Get-ChildItem -LiteralPath (Join-Path $buildRoot 'adapter/CMakeFiles/horizon_quiet.dir') -Recurse -Filter '*.obj' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $relinkStage ('objects/' + $_.Name))
    }
    Copy-Item -LiteralPath (Join-Path $buildRoot 'liquid-dsp/libliquid.a'), (Join-Path $buildRoot 'libfec/libfec.a'), (Join-Path $buildRoot 'jansson/lib/libjansson.a') -Destination (Join-Path $relinkStage 'libraries')
    Copy-Item -LiteralPath 'native/audio/quiet/Relink-Quiet.ps1' -Destination (Join-Path $relinkStage 'Relink-Quiet.ps1')
    Copy-Item -LiteralPath 'third_party/quiet/README.txt' -Destination (Join-Path $relinkStage 'README.txt')
    Copy-Item -LiteralPath 'third_party/quiet/SOURCES.json' -Destination (Join-Path $relinkStage 'SOURCES.json')
    New-Item -ItemType Directory -Force -Path (Join-Path $relinkStage 'licenses') | Out-Null
    Copy-Item -LiteralPath 'third_party/quiet/quiet/LICENSE' -Destination (Join-Path $relinkStage 'licenses/quiet-BSD-3-Clause.txt')
    Copy-Item -LiteralPath 'third_party/quiet/liquid-dsp/LICENSE' -Destination (Join-Path $relinkStage 'licenses/liquid-dsp-MIT.txt')
    Copy-Item -LiteralPath 'third_party/quiet/jansson/LICENSE' -Destination (Join-Path $relinkStage 'licenses/jansson-MIT.txt')
    Copy-Item -LiteralPath 'third_party/quiet/libfec/lesser.txt' -Destination (Join-Path $relinkStage 'licenses/libfec-LGPL-2.1.txt')
    Copy-Item -LiteralPath 'third_party/quiet/toolchain-licenses' -Destination (Join-Path $relinkStage 'licenses/toolchain') -Recurse
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($package in @(@($sourceStage, 'quiet-source.zip'), @($relinkStage, 'quiet-relink.zip'))) {
        $zip = Join-Path $outputRoot $package[1]
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
        [IO.Compression.ZipFile]::CreateFromDirectory($package[0], $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    }
    Get-Item (Join-Path $outputRoot 'horizon-quiet.dll'), (Join-Path $outputRoot 'quiet-source.zip'), (Join-Path $outputRoot 'quiet-relink.zip') | Select-Object Name, Length
} finally { Pop-Location }
