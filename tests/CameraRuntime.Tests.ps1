[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $projectDirectory 'scripts\Camera-Runtime.ps1')
$fixtureRoot = Join-Path $projectDirectory ('build\camera-runtime-tests-' + [Guid]::NewGuid().ToString('N'))
$existingRoot = Join-Path $fixtureRoot 'existing'
$currentRoot = Join-Path $fixtureRoot 'current'
$dataDirectory = Join-Path $existingRoot 'data'
$null = [IO.Directory]::CreateDirectory($dataDirectory)
$existingExecutable = Join-Path $existingRoot 'runtime\usbbridge-camera.exe'
$expectedExecutable = Join-Path $currentRoot 'runtime\usbbridge-camera.exe'
$statePath = Join-Path $dataDirectory 'camera-process.local.json'
$configPath = Join-Path $dataDirectory 'camera.local.json'
$statusPath = Join-Path $dataDirectory 'camera.local.status.json'
$keyPath = Join-Path $dataDirectory 'camera.local.key'
$script:processReads = 0
$script:changedIdentity = $false
$script:wrongHash = $false
$script:legacyHash = $false
$script:pcmHash = $false
$script:fixedRateHash = $false
$script:presenceReads = 0
$script:presenceResults = @($false)
$script:presenceError = $false
$script:presenceSerial = $null
$script:process = [pscustomobject]@{ Id = 4411; Path = $existingExecutable; StartTimeUtcTicks = '123456789' }
$script:state = [pscustomobject]@{
    process_id = 4411; start_time_utc_ticks = '123456789'
    executable_path = $existingExecutable; config_path = $configPath
}
$script:configuration = [pscustomobject]@{
    enabled = $true; loopback_only = $true; usb_port = 13260; video_port = 13261
    allowed_ips = @('127.0.0.1'); key_file = $keyPath
    camera = [pscustomobject]@{ name = 'Fixture webcam'; serial = 'HorizonClient-Camera-1'; width = 1280; height = 720; fps = 30; uncompressed = $false; h264_probe = $false }
}
$script:status = [pscustomobject]@{
    process_id = 4411; running = $true; device_attached = $true
    camera = [pscustomobject]@{ serial = 'HorizonClient-Camera-1'; width = 1280; height = 720; fps = 30; uncompressed = $false; h264_probe = $false }
}
$listeners = @(
    [pscustomobject]@{ Port = 13260; ProcessId = 4411; Address = '127.0.0.1' },
    [pscustomobject]@{ Port = 13261; ProcessId = 4411; Address = '127.0.0.1' }
)

# All process/hash/JSON access is restricted to synthetic fixtures. No real
# listener enumeration, camera, process, key, or clipboard is used by this test.
function Get-HorizonCameraProcess {
    param([int]$ProcessId)
    if ($ProcessId -ne 4411) { throw 'Unexpected process lookup.' }
    $script:processReads++
    if ($script:changedIdentity -and $script:processReads -gt 1) {
        return [pscustomobject]@{ Id = 4411; Path = $existingExecutable; StartTimeUtcTicks = '987654321' }
    }
    $script:process
}
function Get-HorizonCameraFileHash {
    param([string]$Path)
    if ($Path -notin @($existingExecutable, $expectedExecutable)) { throw 'Unexpected executable hash lookup.' }
    if ($script:wrongHash -and $Path -eq $existingExecutable) { return 'DIFFERENT' }
    if ($script:legacyHash -and $Path -eq $existingExecutable) { return '071AB382ED4ED5E8ED5902BBE5B595A46879D130978193F0337F9F84623A234E' }
    if ($script:pcmHash -and $Path -eq $existingExecutable) { return '40AFE37D9BCCCDBA4B6DD1220E658F5CF7D2B57955FD4A98725F7F6065F7F9BE' }
    if ($script:fixedRateHash -and $Path -eq $existingExecutable) { return 'D19D3011ACB578AA8B28D2E47F6163E7201827B3272321E1F318C3D96D73128A' }
    'EXPECTED'
}
function Get-HorizonCameraJson {
    param([string]$Path)
    if ($Path -notin @($statePath, $configPath, $statusPath)) { throw 'Unexpected JSON lookup; keys must never be read.' }
    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Get-HorizonCameraPresentNode {
    param([string]$Serial)
    $script:presenceReads++
    $script:presenceSerial = $Serial
    if ($script:presenceError) { throw 'Synthetic SetupAPI query failure.' }
    $index = [Math]::Min($script:presenceReads - 1, $script:presenceResults.Count - 1)
    $script:presenceResults[$index]
}
function Write-Fixtures {
    foreach ($entry in @(
        @{ Path = $statePath; Value = $script:state },
        @{ Path = $configPath; Value = $script:configuration },
        @{ Path = $statusPath; Value = $script:status }
    )) {
        [IO.File]::WriteAllText($entry.Path, ($entry.Value | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    }
    $script:processReads = 0
}
function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Assert-Rejected {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { $null = & $Action } catch { $rejected = $true }
    Assert-True $rejected $Message
}

try {
    # Compile the P/Invoke declarations without invoking them. Presence checks
    # below use only the injected synthetic query, never the machine's devices.
    Initialize-HorizonCameraRemovalNative
    Assert-True ($null -ne ('HorizonClient.CameraRemovalNative' -as [type])) 'The read-only PnP helper must compile without querying hardware.'
    Wait-HorizonCameraRemoval -Serial 'HorizonClient-Camera-1' -TimeoutMilliseconds 100
    Assert-True ($script:presenceReads -eq 1 -and $script:presenceSerial -ceq 'HorizonClient-Camera-1') 'An absent exact owned root must complete without another device lookup.'
    $script:presenceReads = 0; $script:presenceResults = @($true, $false)
    Wait-HorizonCameraRemoval -Serial 'HorizonClient-Camera-1' -TimeoutMilliseconds 1000
    Assert-True ($script:presenceReads -eq 2) 'A pending root must be checked again before removal completes.'
    $script:presenceReads = 0; $script:presenceResults = @($true)
    Assert-Rejected { Wait-HorizonCameraRemoval -Serial 'HorizonClient-Camera-1' -TimeoutMilliseconds 20 } 'A root that remains present must time out instead of authorizing recreation.'
    Assert-True ($script:presenceReads -ge 1) 'A removal timeout must inspect the requested root.'
    $script:presenceReads = 0; $script:presenceError = $true
    Assert-Rejected { Wait-HorizonCameraRemoval -Serial 'HorizonClient-Camera-1' -TimeoutMilliseconds 20 } 'A query failure must not be treated as device absence.'
    Assert-True ($script:presenceReads -eq 1) 'A presence-query failure must stop the wait immediately.'
    $script:presenceError = $false; $script:presenceReads = 0; $script:presenceResults = @('unknown')
    Assert-Rejected { Wait-HorizonCameraRemoval -Serial 'HorizonClient-Camera-1' -TimeoutMilliseconds 20 } 'A nonboolean query result must fail closed.'
    $script:presenceReads = 0
    Assert-Rejected { Wait-HorizonCameraRemoval -Serial 'Foreign-Camera-1' -TimeoutMilliseconds 20 } 'A foreign identity must be rejected before device enumeration.'
    Assert-True ($script:presenceReads -eq 0) 'Invalid camera serials must never reach the presence query.'
    Assert-Rejected { Wait-HorizonCameraRemoval -Serial 'HorizonClient-Camera-1' -TimeoutMilliseconds 20001 } 'Removal waits must reject a timeout above twenty seconds.'
    Assert-True ($script:presenceReads -eq 0) 'An invalid timeout must never reach the presence query.'

    Write-Fixtures
    Assert-True ($null -eq (Resolve-HorizonCameraBackend -Listeners @() -ExpectedExecutable $expectedExecutable)) 'Unused endpoints must resolve to null.'
    $resolved = Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable
    Assert-True ($resolved.Ready -and $resolved.Root -eq $existingRoot -and $resolved.KeyPath -eq $keyPath) 'A second copy must reuse the existing verified backend and key path.'
    Assert-True (-not (Test-Path -LiteralPath $keyPath)) 'Discovery must work without opening or creating the key.'
    $saved = Resolve-HorizonSavedCameraIdentity -Root $existingRoot
    Assert-True ($saved.ProcessId -eq 0 -and $saved.CameraName -eq 'Fixture webcam' -and $saved.CameraSerial -eq 'HorizonClient-Camera-1') 'A saved identity must remain visible when its backend is stopped.'
    $serial = 'HorizonClient-' + [Guid]::NewGuid().ToString('N')
    $script:configuration.camera.serial = $serial; $script:status.camera.serial = $serial
    Write-Fixtures
    $freshIdentity = Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable
    Assert-True ($freshIdentity.Ready -and $freshIdentity.CameraSerial -ceq $serial) 'GUID identities must verify using their actual matching status serial.'
    foreach ($invalidSerial in @('Foreign-Camera-1', 'HorizonClient-other', ($serial + 'x'), 'horizonclient-01234567890123456789012345678901')) {
        Assert-True (-not (Test-HorizonCameraSerial -Serial $invalidSerial)) 'Unknown and malformed serials must remain unowned.'
    }
    $script:legacyHash = $true
    Write-Fixtures
    $legacy = Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable
    Assert-True (-not $legacy.MatchesBinary -and $legacy.CameraSerial -eq $serial) 'The pinned previous binary may be verified for upgrade without changing camera identity.'
    $script:legacyHash = $false
    $script:pcmHash = $true
    Write-Fixtures
    Assert-True (-not (Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable).MatchesBinary) 'The previous PCM backend must remain verifiable for a mode-capability upgrade.'
    $script:pcmHash = $false
    $script:fixedRateHash = $true
    Write-Fixtures
    $fixedRate = Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable
    Assert-True (-not $fixedRate.MatchesBinary -and $fixedRate.CameraSerial -ceq $serial -and $fixedRate.ProcessId -eq 4411) 'The pinned fixed-rate0.2.20 backend must be recognized for upgrade, preserving its exact camera identity and owned process.'
    $script:fixedRateHash = $false
    $script:configuration.camera.width = 7680; $script:configuration.camera.height = 4320; $script:configuration.camera.fps = 60
    $script:status.camera.width = 7680; $script:status.camera.height = 4320; $script:status.camera.fps = 60
    Write-Fixtures
    $large = Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable
    Assert-True ($large.Ready -and $large.Width -eq 7680 -and $large.Height -eq 4320 -and $large.FPS -eq 60) 'Readiness must match the configured 8K mode instead of requiring 720p.'
    $script:configuration.camera.fps = 8; $script:status.camera.fps = 8; $script:configuration.camera.uncompressed = $true
    Write-Fixtures
    Assert-True (-not (Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable).Ready) 'A JPEG status cannot satisfy a configured RAW mode.'
    $script:status.camera.uncompressed = $true
    Write-Fixtures
    Assert-True (Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable).Ready 'A matching bounded 8K RAW mode is ready.'
    $script:configuration.camera.fps = 9
    Write-Fixtures
    Assert-Rejected { Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable } 'RAW bitrate above the descriptor limit must be rejected before any mutation.'
    $script:configuration.camera.width = 640; $script:configuration.camera.height = 480; $script:configuration.camera.fps = 30; $script:configuration.camera.uncompressed = $false; $script:configuration.camera.h264_probe = $true
    $script:status.camera.width = 640; $script:status.camera.height = 480; $script:status.camera.fps = 30; $script:status.camera.uncompressed = $false
    Write-Fixtures
    Assert-True (-not (Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable).Ready) 'A MJPEG status cannot satisfy a configured H264 probe.'
    $script:status.camera.h264_probe = $true
    Write-Fixtures
    $probe = Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable
    Assert-True ($probe.Ready -and $probe.H264Probe) 'Matching fixed-profile H264 probe is ready.'
    $script:configuration.camera.uncompressed = $true
    Write-Fixtures
    Assert-Rejected { Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable } 'H264 and YUY2 descriptors cannot be enabled together.'
    $script:configuration.camera.h264_probe = $false; $script:status.camera.h264_probe = $false
    $script:configuration.camera.width = 1280; $script:configuration.camera.height = 720; $script:configuration.camera.fps = 30; $script:configuration.camera.uncompressed = $false
    $script:status.camera.width = 1280; $script:status.camera.height = 720; $script:status.camera.fps = 30; $script:status.camera.uncompressed = $false
    Write-Fixtures

    $listeners[1].ProcessId = 4412
    Assert-Rejected { Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable } 'Split endpoint ownership must be rejected.'
    $listeners[1].ProcessId = 4411
    $listeners[0].Address = '0.0.0.0'
    Assert-Rejected { Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable } 'Wildcard listeners must be rejected.'
    $listeners[0].Address = '127.0.0.1'

    $script:state.start_time_utc_ticks = 'OLD'
    Write-Fixtures
    Assert-Rejected { Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable } 'Stale PID state must be rejected.'
    $script:state.start_time_utc_ticks = '123456789'
    $script:configuration.key_file = Join-Path $fixtureRoot 'foreign.key'
    Write-Fixtures
    Assert-Rejected { Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable } 'A foreign key path must be rejected.'
    $script:configuration.key_file = $keyPath
    $script:configuration.allowed_ips = @('127.0.0.1', '192.0.2.1')
    Write-Fixtures
    Assert-Rejected { Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable } 'Nonloopback allowed clients must be rejected.'
    $script:configuration.allowed_ips = @('127.0.0.1')
    Write-Fixtures
    $script:wrongHash = $true
    Assert-Rejected { Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable } 'A different backend binary must be rejected.'
    $script:wrongHash = $false

    Write-Fixtures
    $script:changedIdentity = $true
    Assert-Rejected { Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable } 'PID reuse during discovery must be rejected.'
    $script:changedIdentity = $false
    Write-Fixtures
    Assert-True (-not (Resolve-HorizonCameraBackend -Listeners @($listeners[0]) -ExpectedExecutable $expectedExecutable).Ready) 'A half-bound verified backend must remain unready.'
    Write-Fixtures
    $script:status.device_attached = $false
    Write-Fixtures
    Assert-True (-not (Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable).Ready) 'Attachment in progress must remain unready.'
    $script:status.device_attached = $true
    Write-Fixtures
    [IO.File]::SetLastWriteTimeUtc($statusPath, [DateTime]::UtcNow.AddSeconds(-30))
    Assert-True (-not (Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable).Ready) 'An old status file must remain unready.'
    Write-Fixtures
    $script:status.camera.width = 640
    Write-Fixtures
    Assert-True (-not (Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable).Ready) 'An old video mode must remain unready.'
    [IO.File]::WriteAllText($statusPath, '{invalid')
    Assert-True (-not (Resolve-HorizonCameraBackend -Listeners $listeners -ExpectedExecutable $expectedExecutable).Ready) 'An incomplete status write must remain unready.'
    Write-Output 'Camera runtime fixture checks passed (cross-copy reuse, ownership, key isolation, readiness and bounded exact-root removal checks; no hardware queries).'
} finally {
    $canonicalFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $canonicalBuild = [IO.Path]::GetFullPath((Join-Path $projectDirectory 'build')) + [IO.Path]::DirectorySeparatorChar
    if ($canonicalFixture.StartsWith($canonicalBuild, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($canonicalFixture).StartsWith('camera-runtime-tests-')) {
        [IO.Directory]::Delete($canonicalFixture, $true)
    }
}
