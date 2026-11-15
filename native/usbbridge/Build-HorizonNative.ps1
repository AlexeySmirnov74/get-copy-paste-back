[CmdletBinding()]
param([string]$ToolchainRoot, [string]$CargoHome, [string]$VcvarsPath, [switch]$SkipRuntimeCopy)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$nativeRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..'))
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $nativeRoot '..'))
$environmentNames = @('PATH', 'INCLUDE', 'LIB', 'LIBPATH', 'CARGO_HOME', 'RUSTC', 'CARGO_TARGET_DIR')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    if ($ToolchainRoot) {
        $ToolchainRoot = [IO.Path]::GetFullPath($ToolchainRoot)
        $env:PATH = (Join-Path $ToolchainRoot 'bin') + ';' + $env:PATH
        $env:RUSTC = Join-Path $ToolchainRoot 'bin\rustc.exe'
        $cargo = Join-Path $ToolchainRoot 'bin\cargo.exe'
    } else { $cargo = (Get-Command cargo.exe -ErrorAction Stop).Source }
    if ($CargoHome) { $env:CARGO_HOME = [IO.Path]::GetFullPath($CargoHome) }
    $env:CARGO_TARGET_DIR = Join-Path $nativeRoot 'target'
    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
        if (-not $VcvarsPath) {
            $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
            $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if (-not $installation) { throw 'Install MSVC x64 C++ Build Tools and the Windows SDK, or pass -VcvarsPath.' }
            $VcvarsPath = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
        }
        $VcvarsPath = [IO.Path]::GetFullPath($VcvarsPath)
        $buildEnvironment = & cmd.exe /d /c ('"' + $VcvarsPath + '" >nul && set')
        if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize the MSVC compiler environment.' }
        foreach ($entry in $buildEnvironment) {
            if ($entry -match '^(PATH|INCLUDE|LIB|LIBPATH)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
        }
    }
    Push-Location $sourceRoot
    try {
        & $cargo fmt --all -- --check
        if ($LASTEXITCODE -ne 0) { throw 'Native formatting check failed.' }
        foreach ($scenario in @('pcm_', 'silent_microphone', 'raw_', 'h264_probe', 'uvc::tests')) {
            & $cargo test --offline --locked --lib $scenario
            if ($LASTEXITCODE -ne 0) { throw ('Native scenario checks failed: ' + $scenario) }
        }
        & $cargo clippy --offline --locked --lib --bin usbbridge-camera -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw 'Native Clippy checks failed.' }
        & $cargo build --offline --locked --release --bin usbbridge-camera
        if ($LASTEXITCODE -ne 0) { throw 'Native release build failed.' }
    } finally { Pop-Location }
    $built = Join-Path $env:CARGO_TARGET_DIR 'release\usbbridge-camera.exe'
    # Preserve the image of any active backend; standalone setup migrates only
    # its verified owned instance when the user explicitly creates the camera.
    if (-not $SkipRuntimeCopy) { Copy-Item -LiteralPath $built -Destination (Join-Path $workspaceRoot 'runtime\usbbridge-camera.next.exe') -Force }
    Write-Output ('Native receiver built: ' + $built)
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
