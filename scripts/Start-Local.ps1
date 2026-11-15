[CmdletBinding()]
param([switch]$Legacy)
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$application = Join-Path $projectDirectory 'build\Server.exe'
if (-not (Test-Path -LiteralPath $application)) { throw 'Build the application using scripts\Build.ps1 first.' }
if ($Legacy) {
    $backend = & (Join-Path $PSScriptRoot 'Setup-Camera.ps1') -PassThru
    Start-Process -FilePath $application -ArgumentList @('--sender', '--key', ('"{0}"' -f $backend.KeyPath)) -WorkingDirectory $projectDirectory -WindowStyle Normal
} else {
    Start-Process -FilePath $application -WorkingDirectory $projectDirectory -WindowStyle Normal
}
