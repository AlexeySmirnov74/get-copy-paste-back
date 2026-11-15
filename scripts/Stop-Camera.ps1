[CmdletBinding()]
param(
    [switch]$Remove, [switch]$AnyOwnedRoot, [switch]$PassThru, [switch]$MetadataOnly, [string]$RuntimeRoot, [scriptblock]$CameraRuntime,
    [int]$ExpectedProcessId = 0, [string]$ExpectedStartTimeUtcTicks, [string]$ExpectedKeyPath, [string]$ExpectedCameraSerial, [string]$ExpectedBinaryHash
)
$ErrorActionPreference = 'Stop'
$projectDirectory = if ($RuntimeRoot) { [IO.Path]::GetFullPath($RuntimeRoot) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }
if ($null -ne $CameraRuntime) { . $CameraRuntime } else { . (Join-Path $PSScriptRoot 'Camera-Runtime.ps1') }
if ($MetadataOnly -and -not $Remove) { throw 'MetadataOnly requires an explicit Remove action.' }
if ($ExpectedProcessId -ne 0 -or $ExpectedStartTimeUtcTicks -or $ExpectedKeyPath -or $ExpectedCameraSerial) {
    if ($Remove -or $AnyOwnedRoot -or $MetadataOnly -or $ExpectedProcessId -le 0 -or
        $ExpectedStartTimeUtcTicks -notmatch '^[1-9][0-9]{0,18}$' -or -not $ExpectedKeyPath -or
        -not (Test-HorizonCameraSerial -Serial $ExpectedCameraSerial)) { throw 'Expected shutdown identity is incomplete or incompatible with removal.' }
}
$executable = Join-Path $projectDirectory 'runtime\usbbridge-camera.exe'
$mutex = $null
$locked = $false
$corruptStatePath = $null
try {
    $mutex = [Threading.Mutex]::new($false, 'Global\HorizonClient.CameraBootstrap.v1')
    $lockTimeout = if ($ExpectedProcessId -gt 0) { 5000 } else { 30000 }
    try { $locked = $mutex.WaitOne($lockTimeout) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another HorizonServer is configuring the camera.' }
    $resolveParameters = @{ ExpectedExecutable = $executable }
    if ($ExpectedBinaryHash) { $resolveParameters.ExpectedBinaryHash = $ExpectedBinaryHash }
    $backend = Resolve-HorizonCameraBackend @resolveParameters
    if ($ExpectedProcessId -gt 0 -and (-not $backend -or [int]$backend.ProcessId -ne $ExpectedProcessId -or
        [string]$backend.StartTimeUtcTicks -cne $ExpectedStartTimeUtcTicks -or [string]$backend.Root -ine $projectDirectory -or
        [string]$backend.KeyPath -ine $ExpectedKeyPath -or [string]$backend.CameraSerial -cne $ExpectedCameraSerial)) {
        if ($PassThru) { return [pscustomobject]@{ Removed = $false; Stopped = $false } }
        return
    }
    $hadRunningBackend = [bool]$backend
    if ($backend) {
        # This mode is used when changing devices is blocked by driver safety.
        # It must reject before stopping or detaching even a verified receiver.
        if ($MetadataOnly) { throw 'The camera receiver is active. Metadata-only removal left its process, device, files and key unchanged.' }
        if (-not $AnyOwnedRoot -and $backend.Root -ine $projectDirectory) { throw 'The camera is running from another installation. Its process and device were left unchanged.' }
        Stop-HorizonCameraBackend -Backend $backend -ExpectedExecutable $executable
    } elseif ($Remove) {
        $backend = Resolve-HorizonSavedCameraIdentity -Root $projectDirectory
        # A recovery status describes identity only. Ignore its stale process
        # ID entirely; only an absent exact PnP root can authorize file cleanup.
        if ($backend -and -not $backend.MetadataRecoveryRequired -and (Test-Path -LiteralPath $backend.StatePath -PathType Leaf)) {
            $stateBytes = Read-HorizonCameraJsonBytes -Path $backend.StatePath
            if (Test-HorizonCameraEmptyMetadata -Bytes $stateBytes) {
                # A restored valid configuration can still have interrupted
                # process state if setup failed before its receiver started.
                # The verified saved config supplies identity, never this state.
                # Cleanup still requires no endpoints and exact PnP absence.
                $corruptStatePath = $backend.StatePath
            } else {
                $state = ConvertFrom-HorizonCameraJsonBytes -Path $backend.StatePath -Bytes $stateBytes
                $recorded = Get-Process -Id ([int]$state.process_id) -ErrorAction SilentlyContinue
                if ($recorded) {
                    $identity = Get-HorizonCameraProcess -ProcessId $recorded.Id
                    if ([string]$identity.StartTimeUtcTicks -eq [string]$state.start_time_utc_ticks -and [string]$identity.Path -ieq [string]$backend.Executable) {
                        throw 'The recorded camera process is still starting or stopping. Retry removal after it finishes.'
                    }
                }
            }
        }
    }
    if ($Remove -and $backend) {
        # The saved-identity path can have no running receiver while its old USB
        # root is still present. Never forget that identity before checking it.
        if (-not $hadRunningBackend) {
            Wait-HorizonCameraRemoval -Serial $backend.CameraSerial
            if (@(Get-HorizonCameraListeners).Count -ne 0) { throw 'A camera endpoint became active during removal. Existing files and keys were left unchanged.' }
        }
        # Only fixed metadata paths from the verified own identity are removed.
        # The controller, received files, physical devices and other endpoints stay.
        $data = Join-Path $backend.Root 'data'
        if ($backend.MetadataRecoveryRequired) {
            $null = Save-HorizonCorruptCameraMetadata -Path $backend.ConfigPath
            if (Test-Path -LiteralPath $backend.StatePath -PathType Leaf) { $null = Save-HorizonCorruptCameraMetadata -Path $backend.StatePath }
        } elseif ($corruptStatePath) {
            $null = Save-HorizonCorruptCameraMetadata -Path $corruptStatePath
        }
        $marker = [ordered]@{ serial = $backend.CameraSerial; name = $backend.CameraName; deleted_utc = [DateTime]::UtcNow.ToString('o') }
        Write-HorizonCameraJson -Path (Join-Path $data 'camera.identity.deleted.json') -Value $marker
        foreach ($path in @($backend.ConfigPath, $backend.StatePath, $backend.StatusPath, $backend.KeyPath)) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        }
    }
    if ($PassThru) { return [pscustomobject]@{ Removed = [bool]($Remove -and $backend); Stopped = $hadRunningBackend } }
    if ($Remove) { Write-Output 'HorizonServer camera identity removed.' } else { Write-Output 'HorizonServer camera stopped.' }
} finally {
    if ($locked -and $mutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
