[CmdletBinding()]
param([switch]$SkipTests, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory 'build\audio' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

# The checked-in upstream headers are sufficient; builds need no downloads.
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools (x64) are required to rebuild aicodix.' }
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $installation) { throw 'No Visual Studio C++ x64 toolchain was found.' }
    $developerCommand = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
    $environmentLines = & $env:ComSpec /s /c ('""' + $developerCommand + '" -no_logo -arch=x64 -host_arch=x64 >nul && set"')
    if ($LASTEXITCODE -ne 0) { throw 'Visual Studio x64 build environment setup failed.' }
    foreach ($line in $environmentLines) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) {
            [Environment]::SetEnvironmentVariable($line.Substring(0, $separator), $line.Substring($separator + 1), 'Process')
        }
    }
}
if ($env:VSCMD_ARG_TGT_ARCH -and $env:VSCMD_ARG_TGT_ARCH -ne 'x64') { throw 'aicodix must be built for x64.' }
$nativeDirectory = Join-Path $projectDirectory 'native\audio'
$vendorDirectory = Join-Path $projectDirectory 'third_party\aicodix'
$commonArguments = @('/nologo', '/std:c++17', '/EHsc', '/O2', '/MT', '/GR-', '/fp:precise', '/W3', '/D_CRT_SECURE_NO_WARNINGS', '/D_CRT_NONSTDC_NO_WARNINGS')
foreach ($dependency in @('modem', 'dsp', 'code')) { $commonArguments += '/external:I' + (Join-Path $vendorDirectory $dependency) }
$commonArguments += '/external:W0'
Push-Location $OutputDirectory
try {
    & cl.exe @commonArguments /LD (Join-Path $nativeDirectory 'aicodix.cpp') /Foaicodix.obj /Fehorizon-aicodix.dll /link /INCREMENTAL:NO /OPT:REF /OPT:ICF /MACHINE:X64 /IMPLIB:horizon-aicodix.lib
    if ($LASTEXITCODE -ne 0) { throw 'aicodix DLL compilation failed.' }
    if (-not $SkipTests) {
        & cl.exe @commonArguments (Join-Path $nativeDirectory 'aicodix_roundtrip.cpp') horizon-aicodix.lib /Foaicodix_roundtrip.obj /Feaicodix-roundtrip.exe /link /INCREMENTAL:NO /MACHINE:X64
        if ($LASTEXITCODE -ne 0) { throw 'aicodix roundtrip compilation failed.' }
        & (Join-Path $OutputDirectory 'aicodix-roundtrip.exe')
        if ($LASTEXITCODE -ne 0) { throw 'aicodix native chunked-PCM roundtrip failed.' }
    }
} finally { Pop-Location }
Write-Output ('Built aicodix x64: ' + (Join-Path $OutputDirectory 'horizon-aicodix.dll'))
