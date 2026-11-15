[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ToolchainRoot,
    [string]$LibfecPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'horizon-quiet.dll')
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path ([IO.Path]::GetFullPath($ToolchainRoot)) 'bin/x86_64-w64-mingw32-clang.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Pass the root of a portable LLVM-MinGW x64 toolchain.' }
if (-not $LibfecPath) { $LibfecPath = Join-Path $PSScriptRoot 'libraries/libfec.a' }
$LibfecPath = [IO.Path]::GetFullPath($LibfecPath)
if (-not (Test-Path -LiteralPath $LibfecPath)) { throw ('Missing library: ' + $LibfecPath) }
$objects = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'objects') -Filter '*.obj' | ForEach-Object FullName)
if ($objects.Count -ne 9) { throw 'The relink package must contain the nine matching adapter/Quiet object files.' }
$arguments = @('-shared', '-static', '-Wl,--exclude-all-symbols', '-o', [IO.Path]::GetFullPath($OutputPath)) + $objects + @(
    (Join-Path $PSScriptRoot 'libraries/libliquid.a'), $LibfecPath,
    (Join-Path $PSScriptRoot 'libraries/libjansson.a'), '-lm', '-ladvapi32')
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native Quiet relink failed.' }
Write-Output ('Relinked replaceable Quiet DLL: ' + [IO.Path]::GetFullPath($OutputPath))
