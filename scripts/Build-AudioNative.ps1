[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Build-Aicodix.ps1')
& (Join-Path $PSScriptRoot 'Build-Quiet.ps1')
$taskRuntime = Join-Path $taskWorkspace 'runtime\audio'
[IO.Directory]::CreateDirectory($taskRuntime) | Out-Null
foreach ($taskFile in @('horizon-quiet.dll','horizon-aicodix.dll','quiet-source.zip','quiet-relink.zip')) {
    Copy-Item -LiteralPath (Join-Path $taskWorkspace ('build\audio\'+$taskFile)) -Destination (Join-Path $taskRuntime $taskFile) -Force
}
Write-Output ('Audio native runtime updated: '+$taskRuntime)
