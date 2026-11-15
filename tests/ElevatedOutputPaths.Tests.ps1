[CmdletBinding()]
param([string]$SetupScript)
$ErrorActionPreference='Stop'
$taskProject=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskFixture=Join-Path $taskProject ('build\elevated-output-tests-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($taskFixture)
try {
    . (Join-Path $taskProject 'scripts\Camera-Runtime.ps1')
    $taskTokens=$null; $taskErrors=$null
    if(-not $SetupScript){$SetupScript=Join-Path $taskProject 'scripts\Setup-Camera.ps1'}
    $taskAst=[Management.Automation.Language.Parser]::ParseFile($SetupScript,[ref]$taskTokens,[ref]$taskErrors)
    if($taskErrors.Count){throw 'Setup script syntax failed.'}
    $taskLaunch=$taskAst.Find({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$cameraProcess' -and $node.Right.Extent.Text.StartsWith('Start-Process ')},$true)
    if($null -eq $taskLaunch){throw 'Missing receiver launch.'}
    $taskProbe=[ScriptBlock]::Create($taskLaunch.Extent.Text)
    $executable='inert-fixture.exe'; $configPath='fixture.json'; $projectDirectory=$taskFixture; $dataDirectory=$taskFixture
    $script:taskLaunches=0
    function Start-Process { param($FilePath,$ArgumentList,$WorkingDirectory,$WindowStyle,[switch]$PassThru,$RedirectStandardOutput,$RedirectStandardError) $script:taskLaunches++; [pscustomobject]@{Id=1} }
    foreach($taskName in @('camera.stdout.log','camera.stderr.log')) {
        $taskLeaf=Join-Path $taskFixture $taskName
        $null=[IO.Directory]::CreateDirectory($taskLeaf)
        $taskRejected=$false
        try { & $taskProbe } catch { $taskRejected=$true }
        [IO.Directory]::Delete($taskLeaf)
        if(-not $taskRejected -or $script:taskLaunches -ne 0){throw ('Receiver launch must refuse unsafe output leaf before Start-Process: '+$taskName)}
    }
    $taskParentFile=Join-Path $taskFixture 'parent-is-file'
    [IO.File]::WriteAllText($taskParentFile,'untouched parent')
    $dataDirectory=$taskParentFile
    $taskRejected=$false
    try { & $taskProbe } catch { $taskRejected=$true }
    if(-not $taskRejected -or $script:taskLaunches -ne 0 -or [IO.File]::ReadAllText($taskParentFile) -ne 'untouched parent'){throw 'Receiver launch must reject a regular file ancestor without changing it.'}
    $dataDirectory=$taskFixture
    $taskOrdinary=Join-Path $taskFixture 'camera.stdout.log'
    [IO.File]::WriteAllText($taskOrdinary,'existing log')
    & $taskProbe
    if($script:taskLaunches -ne 1 -or [IO.File]::ReadAllText($taskOrdinary) -ne 'existing log'){throw 'Output validation must accept ordinary paths without truncating them.'}
    Write-Output 'Elevated receiver output checks passed: both directory leaves and regular-file ancestor rejected before launch; ordinary paths accepted without validation writes. No backend, UAC or device actions.'
} finally { if([IO.Directory]::Exists($taskFixture)){[IO.Directory]::Delete($taskFixture,$true)} }
