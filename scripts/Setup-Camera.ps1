[CmdletBinding()]
param(
    [ValidateLength(1, 100)][string]$CameraName = '4k webcam', [switch]$PassThru, [switch]$TakeOwnership,
    [ValidateRange(0, 1920)][int]$Width = 0, [ValidateRange(0, 1080)][int]$Height = 0, [int]$FramesPerSecond = 0, [switch]$Uncompressed, [switch]$H264Probe,
    [switch]$ConfigureMode, [string]$ExpectedBinaryHash,
    [string]$RuntimeRoot, [scriptblock]$CameraRuntime
)
$ErrorActionPreference = 'Stop'
if ($ConfigureMode) { $TakeOwnership = $true }
$CameraName = $CameraName.Trim()
if ($CameraName.Length -eq 0 -or $CameraName -match '[\x00-\x1f\x7f-\x9f]') { throw 'Enter a camera name of 1 to 100 characters without control characters.' }
$projectDirectory = if ($RuntimeRoot) { [IO.Path]::GetFullPath($RuntimeRoot) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }
if ($null -ne $CameraRuntime) { . $CameraRuntime } else { . (Join-Path $PSScriptRoot 'Camera-Runtime.ps1') }

function Get-HorizonSetupCompatibleMode {
    param([Parameter(Mandatory = $true)]$Existing)
    if (-not (Test-HorizonVideoMode -Width $Existing.Width -Height $Existing.Height -FramesPerSecond $Existing.FPS -Uncompressed ([bool]$Existing.Uncompressed) -H264Probe ([bool]$Existing.H264Probe))) {
        throw 'The saved camera mode is invalid; existing identity and key were left unchanged.'
    }
    # Keep an existing supported YUY2 identity/mode. Old high-resolution, compressed and SEI
    # configurations migrate to the current1080p YUY2 default only when the
    # caller has not supplied an explicit compatibility-mode selection.
    $legacySize = -not (Test-HorizonSetupVideoMode -Width $Existing.Width -Height $Existing.Height -FramesPerSecond $Existing.FPS -Uncompressed ([bool]$Existing.Uncompressed) -H264Probe ([bool]$Existing.H264Probe)) -or -not [bool]$Existing.Uncompressed -or [bool]$Existing.H264Probe
    [pscustomobject]@{
        Width = if ($legacySize) { 1920 } else { [int]$Existing.Width }
        Height = if ($legacySize) { 1080 } else { [int]$Existing.Height }
        FPS = if ($legacySize) { 30 } else { [int]$Existing.FPS }
        Uncompressed = $true
        H264Probe = if ($legacySize) { $false } else { [bool]$Existing.H264Probe }
    }
}

function Test-HorizonSetupVideoMode {
    param([int]$Width, [int]$Height, [int]$FramesPerSecond, [bool]$Uncompressed, [bool]$H264Probe = $false)
    if (-not (Test-HorizonVideoMode -Width $Width -Height $Height -FramesPerSecond $FramesPerSecond -Uncompressed $Uncompressed -H264Probe $H264Probe)) { return $false }
    $Uncompressed -and -not $H264Probe -and (($Width -eq 640 -and $Height -eq 480) -or ($Width -eq 1280 -and $Height -eq 720) -or ($Width -eq 1920 -and $Height -eq 1080))
}

function Get-HorizonSetupVideoMode {
    param([Parameter(Mandatory = $true)]$Defaults, [Parameter(Mandatory = $true)][AllowEmptyCollection()][Collections.IDictionary]$Parameters, $Existing)
    $explicitMode = $false
    foreach ($name in @('Width', 'Height', 'FramesPerSecond', 'Uncompressed', 'H264Probe')) {
        if ($Parameters.Keys -contains $name) { $explicitMode = $true; break }
    }
    if ($Existing -and -not $Parameters['ConfigureMode'] -and -not $explicitMode) {
        return Get-HorizonSetupCompatibleMode -Existing $Existing
    }
    [pscustomobject]@{
        Width = if ($Parameters['Width']) { [int]$Parameters['Width'] } else { [int]$Defaults.width }
        Height = if ($Parameters['Height']) { [int]$Parameters['Height'] } else { [int]$Defaults.height }
        FPS = if ($Parameters['FramesPerSecond']) { [int]$Parameters['FramesPerSecond'] } else { [int]$Defaults.fps }
        Uncompressed = if ($Parameters.Keys -contains 'Uncompressed') { [bool]$Parameters['Uncompressed'] } else { [bool]$Defaults.uncompressed }
        H264Probe = if ($Parameters.Keys -contains 'H264Probe') { [bool]$Parameters['H264Probe'] } else { [bool]$Defaults.h264_probe }
    }
}
$dataDirectory = Join-Path $projectDirectory 'data'
$executable = Join-Path $projectDirectory 'runtime\usbbridge-camera.exe'
$stagedExecutable = Join-Path $projectDirectory 'runtime\usbbridge-camera.next.exe'
$expectedExecutable = if (Test-Path -LiteralPath $stagedExecutable -PathType Leaf) { $stagedExecutable } else { $executable }
$configPath = Join-Path $dataDirectory 'camera.local.json'
$keyPath = Join-Path $dataDirectory 'camera.local.key'
$statePath = Join-Path $dataDirectory 'camera-process.local.json'
$statusPath = Join-Path $dataDirectory 'camera.local.status.json'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The bundled camera receiver is missing.' }
if ($ExpectedBinaryHash -and (Get-HorizonCameraFileHash -Path $expectedExecutable) -ine $ExpectedBinaryHash) { throw 'The staged camera receiver does not match this HorizonServer build.' }
$defaults = Get-HorizonCameraJson -Path (Join-Path $projectDirectory 'defaults\ServerDefaults.json')
if ($defaults.camera_serial_prefix -cne 'HorizonClient-' -or $defaults.width -ne 1920 -or $defaults.height -ne 1080 -or $defaults.fps -ne 30 -or
    $defaults.uncompressed -ne $true -or $defaults.h264_probe -ne $false -or
    $defaults.usb_port -ne 13260 -or $defaults.video_port -ne 13261 -or $defaults.loopback_only -ne $true -or $defaults.microphone -ne $true) {
    throw 'The bundled camera settings do not match this HorizonServer version.'
}
$bootstrapMutex = $null
$lockTaken = $false
$cameraSerial = $null
$requestedMode = Get-HorizonSetupVideoMode -Defaults $defaults -Parameters $PSBoundParameters
$modeWidth = $requestedMode.Width; $modeHeight = $requestedMode.Height; $modeFps = $requestedMode.FPS; $modeRaw = $requestedMode.Uncompressed; $modeH264 = $requestedMode.H264Probe
if (-not (Test-HorizonSetupVideoMode -Width $modeWidth -Height $modeHeight -FramesPerSecond $modeFps -Uncompressed $modeRaw -H264Probe $modeH264)) { throw 'Unsupported camera mode: only640x480,1280x720 and1920x1080 YUY2 are available.' }
try {
    $bootstrapMutex = [Threading.Mutex]::new($false, 'Global\HorizonClient.CameraBootstrap.v1')
    try { $lockTaken = $bootstrapMutex.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $lockTaken = $true }
    if (-not $lockTaken) { throw 'Another HorizonServer is configuring the camera. Try again after it finishes.' }
    $existing = Resolve-HorizonCameraBackend -ExpectedExecutable $expectedExecutable
    if ($existing) {
        if (-not $TakeOwnership -and -not (Test-HorizonSetupVideoMode -Width $existing.Width -Height $existing.Height -FramesPerSecond $existing.FPS -Uncompressed ([bool]$existing.Uncompressed) -H264Probe ([bool]$existing.H264Probe))) {
            throw 'The existing camera needs migration to 1920x1080. Use HorizonServer or run setup with TakeOwnership; its identity and key were left unchanged.'
        }
        if ($TakeOwnership) {
            $existingConfiguration = Get-HorizonCameraJson -Path $existing.ConfigPath
            $retainedMode = Get-HorizonSetupVideoMode -Defaults $defaults -Parameters $PSBoundParameters -Existing $existing
            $modeWidth = $retainedMode.Width; $modeHeight = $retainedMode.Height; $modeFps = $retainedMode.FPS; $modeRaw = $retainedMode.Uncompressed; $modeH264 = $retainedMode.H264Probe
            $sameMode = $existing.Width -eq $modeWidth -and $existing.Height -eq $modeHeight -and $existing.FPS -eq $modeFps -and $existing.Uncompressed -eq $modeRaw -and $existing.H264Probe -eq $modeH264
            $sameReadyCamera = $existing.Ready -and $existing.MatchesBinary -and $existingConfiguration.camera.microphone -eq $true -and $sameMode
            if (-not $sameReadyCamera) {
                # Upgrading the process preserves the actual camera identity and
                # name. A differently typed UI name cannot create another camera.
                $CameraName = [string]$existingConfiguration.camera.name
                $cameraSerial = [string]$existingConfiguration.camera.serial
                Stop-HorizonCameraBackend -Backend $existing -ExpectedExecutable $expectedExecutable
                $existing = $null
            }
        }
        if ($existing) {
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while (-not $existing.Ready -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 200
                $existing = Resolve-HorizonCameraBackend -ExpectedExecutable $expectedExecutable
                if (-not $existing) { throw 'The existing camera receiver stopped while waiting for it.' }
            }
            if (-not $existing.Ready) { throw 'The verified camera receiver is running but is not ready. Stop it before retrying setup.' }
            if ($PassThru) { return $existing }
            Write-Output 'HorizonServer camera is already running and ready.'
            return
        }
    }
    [IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        $savedIdentity = Resolve-HorizonSavedCameraIdentity -Root $projectDirectory
        if ($savedIdentity.MetadataRecoveryRequired) {
            # Only the explicit GUI create/setup action may restore files. The
            # discovery path only returns an identity and never trusts a PID
            # from the status left behind by the crash.
            $savedIdentity = Restore-HorizonCameraMetadata -Root $projectDirectory -TakeOwnership:$TakeOwnership
        }
        if (-not $cameraSerial) {
            $cameraSerial = $savedIdentity.CameraSerial; $CameraName = $savedIdentity.CameraName
            $retainedMode = Get-HorizonSetupVideoMode -Defaults $defaults -Parameters $PSBoundParameters -Existing $savedIdentity
            $modeWidth = $retainedMode.Width; $modeHeight = $retainedMode.Height; $modeFps = $retainedMode.FPS; $modeRaw = $retainedMode.Uncompressed; $modeH264 = $retainedMode.H264Probe
        }
    }
    if (-not $cameraSerial -and $ConfigureMode) { throw 'Create a camera before changing its video mode.' }
    if (-not $cameraSerial) { $cameraSerial = [string]$defaults.camera_serial_prefix + [Guid]::NewGuid().ToString('N') }
    if (Test-Path -LiteralPath $stagedExecutable -PathType Leaf) {
        if ($ExpectedBinaryHash -and (Get-HorizonCameraFileHash -Path $stagedExecutable) -ine $ExpectedBinaryHash) { throw 'The staged receiver changed before installation.' }
        Copy-Item -LiteralPath $stagedExecutable -Destination $executable -Force
        Remove-Item -LiteralPath $stagedExecutable -Force
    }
    if ($ExpectedBinaryHash -and (Get-HorizonCameraFileHash -Path $executable) -ine $ExpectedBinaryHash) { throw 'The installed receiver does not match this HorizonServer build.' }
    try { $readinessOutput = & $executable driver-ready 2>&1 } catch { throw 'USBIP-WIN2 is unavailable. Run HorizonServer and create the camera to install its driver.' }
    if ($LASTEXITCODE -ne 0) { throw 'USBIP-WIN2 is unavailable. Run HorizonServer and create the camera to install its driver.' }
    $readiness = ($readinessOutput | Out-String) | ConvertFrom-Json
    if (-not $readiness.ready) { throw 'The USBIP-WIN2 controller is not ready. A Windows restart may be required.' }
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        & $executable keygen $keyPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the private camera connection key.' }
    }
    try {
        $keyHandle = [IO.File]::Open($keyPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $keyHandle.Dispose()
    } catch { throw 'The private camera key is not readable by this Windows account. Start setup and HorizonServer under the same Windows account.' }
    $configuration = [ordered]@{
        enabled = $true
        camera = [ordered]@{ name = $CameraName; serial = $cameraSerial; width = $modeWidth; height = $modeHeight; fps = $modeFps; uncompressed = $modeRaw; h264_probe = $modeH264; test_pattern = $false; microphone = $defaults.microphone }
        usb_port = $defaults.usb_port; video_port = $defaults.video_port; loopback_only = $defaults.loopback_only; allowed_ips = @('127.0.0.1'); key_file = $keyPath
    }
    Write-HorizonCameraJson -Path $configPath -Value $configuration -BackupPrevious
    & $executable validate receive $configPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Camera receiver configuration validation failed.' }
    $cameraProcess = $null
    $observedAttached = $false
    try {
        $cameraProcess = Start-Process -FilePath $executable -ArgumentList @('receive', '--config', ('"{0}"' -f $configPath)) -WorkingDirectory $projectDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Assert-HorizonCameraOutputPath -Path (Join-Path $dataDirectory 'camera.stdout.log')) -RedirectStandardError (Assert-HorizonCameraOutputPath -Path (Join-Path $dataDirectory 'camera.stderr.log'))
        $null = $cameraProcess.Handle
        $cameraState = [ordered]@{ process_id = $cameraProcess.Id; start_time_utc_ticks = $cameraProcess.StartTime.ToUniversalTime().Ticks.ToString(); executable_path = $executable; config_path = $configPath }
        Write-HorizonCameraJson -Path $statePath -Value $cameraState
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $cameraProcess.Refresh()
            if ($cameraProcess.HasExited) {
                $detail = ''
                try { $detail = [IO.File]::ReadAllText((Join-Path $dataDirectory 'camera.stderr.log'), [Text.Encoding]::UTF8).Trim() } catch { }
                if ($detail.Length -gt 2000) { $detail = $detail.Substring($detail.Length - 2000) }
                if ($detail -match '10048') { throw 'Camera ports 13260/13261 are already in use. The existing camera was left unchanged.' }
                throw ('Camera receiver exited. {0}' -f $detail)
            }
            $cameraStatus = $null
            try { $cameraStatus = Get-HorizonCameraJson -Path $statusPath } catch { }
            $observedAttached = $observedAttached -or ($cameraStatus -and [int]$cameraStatus.process_id -eq $cameraProcess.Id -and $cameraStatus.device_attached -eq $true -and $cameraStatus.camera.serial -ceq $cameraSerial -and [IO.File]::GetLastWriteTimeUtc($statusPath).Ticks -ge [long]$cameraState.start_time_utc_ticks)
            $resolved = Resolve-HorizonCameraBackend -ExpectedExecutable $executable
            if ($resolved -and $resolved.ProcessId -eq $cameraProcess.Id -and $resolved.Ready) {
                if ($PassThru) { return $resolved }
                Write-Output ('HorizonServer camera is ready ({0} x {1}, {2} FPS).' -f $modeWidth, $modeHeight, $modeFps)
                return
            }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        throw ('Camera attachment did not finish. Inspect {0}.' -f $statusPath)
    } catch {
        $originalError = $_
        if ($cameraProcess) {
            $cameraProcess.Refresh()
            if (-not $cameraProcess.HasExited) {
                Stop-Process -InputObject $cameraProcess -Force
                if (-not $cameraProcess.WaitForExit(10000)) { throw 'The receiver started by this setup did not stop after setup failed.' }
            }
        }
        # A bind failure never authorizes detaching a previously running device.
        if ($observedAttached -and @(Get-HorizonCameraListeners).Count -eq 0) { & $executable detach-local '127.0.0.1:13260' | Out-Null }
        throw $originalError
    }
} finally {
    if ($lockTaken -and $bootstrapMutex) { $bootstrapMutex.ReleaseMutex() }
    if ($bootstrapMutex) { $bootstrapMutex.Dispose() }
}
