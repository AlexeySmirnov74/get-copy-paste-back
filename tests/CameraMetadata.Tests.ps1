[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sourceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureRoot = Join-Path $sourceDirectory ('build\camera-metadata-tests-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixtureRoot)
$runtimeSource = [IO.File]::ReadAllText((Join-Path $sourceDirectory 'scripts\Camera-Runtime.ps1'), [Text.Encoding]::UTF8)
. ([ScriptBlock]::Create($runtimeSource))
$script:checks = 0
$ownedSerial = 'HorizonClient-0123456789abcdef0123456789abcdef'
$deletedSerial = 'HorizonClient-fedcba9876543210fedcba9876543210'
$syntheticHelpers = @'
function Get-HorizonCameraListeners {
    $global:HorizonMetadataFixtureListenerReads++
    if ($global:HorizonMetadataFixturePortsActive) { return [pscustomobject]@{ Port = 13260; ProcessId = 4411; Address = '127.0.0.1' } }
    return @()
}
function Wait-HorizonCameraRemoval {
    param([string]$Serial)
    $global:HorizonMetadataFixturePresenceReads++
    if ($Serial -cne 'HorizonClient-0123456789abcdef0123456789abcdef') { throw 'Unexpected synthetic camera presence query.' }
    if ($global:HorizonMetadataFixturePresent) { throw 'Synthetic previous camera root is still present.' }
}
function Get-HorizonCameraProcess {
    param([int]$ProcessId)
    $global:HorizonMetadataFixtureProcessReads++
    throw 'A stale status must not authorize a real process lookup in metadata fixtures.'
}
function Get-Process {
    param([int]$Id, [string]$ErrorAction)
    $global:HorizonMetadataFixtureProcessReads++
    throw 'A corrupt saved process state must not reach process lookup.'
}
function Stop-HorizonCameraBackend {
    param($Backend, [string]$ExpectedExecutable)
    $global:HorizonMetadataFixtureStopCalls++
    throw 'MetadataOnly must never stop a camera backend.'
}
'@
. ([ScriptBlock]::Create($syntheticHelpers))

function Reset-SyntheticState {
    $global:HorizonMetadataFixtureListenerReads = 0
    $global:HorizonMetadataFixturePresenceReads = 0
    $global:HorizonMetadataFixtureProcessReads = 0
    $global:HorizonMetadataFixtureStopCalls = 0
    $global:HorizonMetadataFixturePortsActive = $false
    $global:HorizonMetadataFixtureBackendActive = $false
    $global:HorizonMetadataFixturePresent = $false
}
function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:checks++
    if (-not $Condition) { throw $Message }
}
function Assert-Rejected {
    param([scriptblock]$Action, [string]$ExpectedMessage, [string]$Message)
    $caught = $null
    try { $null = & $Action } catch { $caught = $_ }
    Assert-True ($null -ne $caught) $Message
    if ($ExpectedMessage) { Assert-True ($caught.Exception.Message -like $ExpectedMessage) ($Message + ' Unexpected error: ' + $caught.Exception.Message) }
    return $caught
}
function New-Fixture {
    param([string]$Name)
    Reset-SyntheticState
    $root = Join-Path $fixtureRoot $Name
    $data = Join-Path $root 'data'
    $null = [IO.Directory]::CreateDirectory($data)
    $camera = [pscustomobject]@{
        name = 'Recovered fixture webcam'; serial = $ownedSerial; width = 1280; height = 720; fps = 30
        uncompressed = $false; h264_probe = $false; test_pattern = $false; microphone = $true
    }
    $status = [pscustomobject]@{
        camera = $camera; process_id = 2147483647; running = $true; device_attached = $true
        updated_unix_ms = 1794581382704; name_applied = $true; attach_error = $null; camera_fps = 0; source_fps = 0
        received_frames = 0; supplied_frames = 0; source_connected = $false
    }
    $key = Join-Path $data 'camera.local.key'
    $config = Join-Path $data 'camera.local.json'
    $state = Join-Path $data 'camera-process.local.json'
    $statusPath = Join-Path $data 'camera.local.status.json'
    # These are dummy fixture bytes, never a key or metadata from this machine.
    [IO.File]::WriteAllText($key, 'synthetic fixture key; do not replace', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes($config, [byte[]]::new(733))
    [IO.File]::WriteAllBytes($state, [byte[]]::new(285))
    [IO.File]::WriteAllText($statusPath, ($status | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $configuration = [pscustomobject]@{
        enabled = $true; loopback_only = $true; usb_port = 13260; video_port = 13261
        allowed_ips = @('127.0.0.1'); key_file = $key; camera = $camera
    }
    [pscustomobject]@{ Root = $root; Data = $data; Config = $config; State = $state; StatusPath = $statusPath; Key = $key; Camera = $camera; Status = $status; Configuration = $configuration }
}
function Get-FixtureSnapshot {
    param($Fixture)
    $parts = foreach ($path in @($Fixture.Config, $Fixture.State, $Fixture.StatusPath, $Fixture.Key)) {
        $file = [IO.FileInfo]::new($path)
        $file.Name + ':' + $file.LastWriteTimeUtc.Ticks + ':' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
    }
    return ($parts -join '|')
}
function Write-FixtureStatus {
    param($Fixture)
    [IO.File]::WriteAllText($Fixture.StatusPath, ($Fixture.Status | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
}
function Copy-StopFixture {
    param($Fixture)
    $scripts = Join-Path $Fixture.Root 'scripts'
    $null = [IO.Directory]::CreateDirectory($scripts)
    $backendStub = @'
function Resolve-HorizonCameraBackend {
    param([string]$ExpectedExecutable)
    if (-not $global:HorizonMetadataFixtureBackendActive) { return $null }
    $root = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($ExpectedExecutable))
    [pscustomobject]@{ Root = $root; ProcessId = 4411; CameraSerial = 'HorizonClient-0123456789abcdef0123456789abcdef' }
}
'@
    [IO.File]::WriteAllText((Join-Path $scripts 'Camera-Runtime.ps1'), ($runtimeSource + [Environment]::NewLine + $syntheticHelpers + [Environment]::NewLine + $backendStub), [Text.UTF8Encoding]::new($false))
    $stop = [IO.File]::ReadAllText((Join-Path $sourceDirectory 'scripts\Stop-Camera.ps1'), [Text.Encoding]::UTF8)
    # Keep the real stop logic, but its fixture mutex cannot block the user's app.
    $stop = $stop.Replace('Global\HorizonClient.CameraBootstrap.v1', ('Local\HorizonMetadataFixture.' + [Guid]::NewGuid().ToString('N')))
    $path = Join-Path $scripts 'Stop-Camera.ps1'
    [IO.File]::WriteAllText($path, $stop, [Text.UTF8Encoding]::new($false))
    return $path
}

try {
    # Durable create/replace, valid previous backup, UTF-8 and incomplete-write
    # isolation are exercised only in this workspace's temporary fixture tree.
    $durable = Join-Path $fixtureRoot 'durable.json'
    $unicodeName = 'Webcam ' + [char]0x041a + [char]0x0430
    Write-HorizonCameraJson -Path $durable -Value ([pscustomobject]@{ generation = 1; name = $unicodeName })
    Assert-True ((Get-HorizonCameraJson -Path $durable).name -ceq $unicodeName) 'UTF-8 metadata must round trip without a BOM or replacement characters.'
    Write-HorizonCameraJson -Path $durable -Value ([pscustomobject]@{ generation = 2 }) -BackupPrevious
    Assert-True ((Get-HorizonCameraJson -Path $durable).generation -eq 2 -and (Get-HorizonCameraJson -Path ($durable + '.bak')).generation -eq 1) 'Atomic replacement must retain the previous valid document as its backup.'
    $interrupted = Join-Path $fixtureRoot '.durable.json.interrupted.tmp'
    [IO.File]::WriteAllText($interrupted, '{')
    Assert-True ((Get-HorizonCameraJson -Path $durable).generation -eq 2) 'An interrupted temporary write must leave the published JSON intact.'
    $null = Assert-Rejected { Write-HorizonCameraJson -Path $durable -Value ([pscustomobject]@{ large = ('x' * 65536) }) -BackupPrevious } '*durable.json*maximum 65536*' 'An oversized replacement must fail before touching the original or backup.'
    Assert-True ((Get-HorizonCameraJson -Path $durable).generation -eq 2 -and (Get-HorizonCameraJson -Path ($durable + '.bak')).generation -eq 1) 'A rejected replacement must preserve both generations.'
    $badBackup = Join-Path $fixtureRoot 'bad-backup.json'
    [IO.File]::WriteAllBytes($badBackup, [byte[]]::new(9))
    $null = Assert-Rejected { Write-HorizonCameraJson -Path $badBackup -Value ([pscustomobject]@{ valid = $true }) -BackupPrevious } '*bad-backup.json*9 bytes*' 'Corrupt previous metadata must be archived explicitly, not saved as a valid backup.'
    Assert-True ((Test-HorizonCameraEmptyMetadata -Bytes ([IO.File]::ReadAllBytes($badBackup))) -and -not [IO.File]::Exists($badBackup + '.bak')) 'A rejected corrupt-backup write must keep the original bytes.'

    foreach ($empty in @($false, $true)) {
        $fixture = New-Fixture ('discovery-' + $empty)
        if ($empty) { [IO.File]::WriteAllBytes($fixture.Config, [byte[]]::new(0)) }
        $before = Get-FixtureSnapshot $fixture
        $recovered = Resolve-HorizonSavedCameraIdentity -Root $fixture.Root
        Assert-True ($recovered.MetadataRecoveryRequired -and $recovered.MetadataByteCount -eq ([IO.File]::ReadAllBytes($fixture.Config)).Length -and $recovered.RecoverySource -eq $fixture.StatusPath) 'Only the fixed same-root status may describe an empty/NUL interrupted configuration.'
        Assert-True ($recovered.CameraSerial -ceq $ownedSerial -and $recovered.CameraName -ceq $fixture.Camera.name -and $recovered.Width -eq 1280 -and $recovered.Height -eq 720 -and $recovered.FPS -eq 30 -and -not $recovered.Uncompressed) 'Recovered discovery must preserve the exact camera identity and mode.'
        Assert-True ($recovered.ProcessId -eq 0 -and -not $recovered.Ready -and -not $recovered.MatchesBinary) 'A stale status must never be reported as a running receiver.'
        Assert-True ((Get-FixtureSnapshot $fixture) -ceq $before -and @(Get-ChildItem -LiteralPath $fixture.Data -Filter '*.corrupt-*').Count -eq 0) 'Read-only recovery must not modify files, keys or timestamps or make archives.'
        Assert-True ($global:HorizonMetadataFixtureListenerReads -eq 0 -and $global:HorizonMetadataFixturePresenceReads -eq 0 -and $global:HorizonMetadataFixtureProcessReads -eq 0) 'Saved identity discovery must not query processes or devices.'
    }
    $fixture = New-Fixture 'malformed-config'
    $privateMarker = 'DO_NOT_LEAK_THESE_BYTES'
    [IO.File]::WriteAllText($fixture.Config, ('{"secret":"' + $privateMarker + '",'), [Text.UTF8Encoding]::new($false))
    $caught = Assert-Rejected { Resolve-HorizonSavedCameraIdentity -Root $fixture.Root } '*camera.local.json*bytes*' 'Nonzero malformed configuration must fail without falling back to status.'
    Assert-True (-not $caught.Exception.Message.Contains($privateMarker)) 'A parser error must not disclose document contents.'
    [IO.File]::WriteAllBytes($fixture.Config, [byte[]]@(0xc3, 0x28))
    $null = Assert-Rejected { Resolve-HorizonSavedCameraIdentity -Root $fixture.Root } '*camera.local.json*2 bytes*' 'Invalid UTF-8 must fail strictly.'
    [IO.File]::WriteAllBytes($fixture.Config, [byte[]]::new(65537))
    $null = Assert-Rejected { Resolve-HorizonSavedCameraIdentity -Root $fixture.Root } '*camera.local.json*65537*maximum 65536*' 'Large all-NUL files must not bypass the metadata size bound.'

    foreach ($invalid in @('foreign', 'missing-field', 'bool-string', 'integer-string', 'zero-width', 'deleted')) {
        $fixture = New-Fixture ('invalid-status-' + $invalid)
        switch ($invalid) {
            'foreign' { $fixture.Status.camera.serial = 'Foreign-Camera-1' }
            'missing-field' { $fixture.Status.camera.PSObject.Properties.Remove('microphone') }
            'bool-string' { $fixture.Status.camera.uncompressed = 'false' }
            'integer-string' { $fixture.Status.camera.width = '1280' }
            'zero-width' { $fixture.Status.camera.width = 0 }
            'deleted' { [IO.File]::WriteAllText((Join-Path $fixture.Data 'camera.identity.deleted.json'), ('{"serial":"' + $ownedSerial + '"}')) }
        }
        Write-FixtureStatus $fixture
        $before = Get-FixtureSnapshot $fixture
        $null = Assert-Rejected { Resolve-HorizonSavedCameraIdentity -Root $fixture.Root } '*camera.local.status.json*' 'Foreign, incomplete, mistyped or explicitly deleted identities must not be recovered.'
        Assert-True ((Get-FixtureSnapshot $fixture) -ceq $before) 'Rejected recovery must preserve all metadata and the key.'
    }
    $fixture = New-Fixture 'missing-status'
    [IO.File]::Delete($fixture.StatusPath)
    [IO.File]::WriteAllText((Join-Path $fixture.Data 'camera.identity.deleted.json'), ('{"serial":"' + $deletedSerial + '","name":"Old deleted camera"}'))
    $null = Assert-Rejected { Resolve-HorizonSavedCameraIdentity -Root $fixture.Root } '*camera.local.status.json*missing*' 'A deletion marker or another installation must never replace a missing same-root status.'

    $fixture = New-Fixture 'restore'
    [IO.File]::WriteAllText((Join-Path $fixture.Data 'camera.identity.deleted.json'), ('{"serial":"' + $deletedSerial + '","name":"Previous camera"}'))
    $before = Get-FixtureSnapshot $fixture
    $null = Assert-Rejected { Restore-HorizonCameraMetadata -Root $fixture.Root } '*explicit*TakeOwnership*' 'Discovery or a setup lacking explicit ownership must not restore metadata.'
    $global:HorizonMetadataFixturePortsActive = $true
    $null = Assert-Rejected { Restore-HorizonCameraMetadata -Root $fixture.Root -TakeOwnership } '*endpoint is active*' 'An active endpoint must prevent metadata recovery.'
    $global:HorizonMetadataFixturePortsActive = $false; $global:HorizonMetadataFixturePresent = $true
    $null = Assert-Rejected { Restore-HorizonCameraMetadata -Root $fixture.Root -TakeOwnership } '*Synthetic previous camera root*' 'A present old device must prevent recovery before any archive or write.'
    Assert-True ((Get-FixtureSnapshot $fixture) -ceq $before -and @(Get-ChildItem -LiteralPath $fixture.Data -Filter '*.corrupt-*').Count -eq 0) 'Failed recovery preconditions must keep original metadata and keys intact.'
    $global:HorizonMetadataFixturePresent = $false
    $keyBefore = [Convert]::ToBase64String([IO.File]::ReadAllBytes($fixture.Key))
    $restored = Restore-HorizonCameraMetadata -Root $fixture.Root -TakeOwnership
    $config = Get-HorizonCameraJson -Path $fixture.Config
    Assert-True (-not $restored.MetadataRecoveryRequired -and $restored.ProcessId -eq 0 -and $config.camera.serial -ceq $ownedSerial -and $config.camera.name -ceq $fixture.Camera.name -and $config.key_file -eq $fixture.Key) 'Explicit recovery must restore the same stopped camera and fixed key path.'
    Assert-True ($config.camera.width -eq 1280 -and $config.camera.height -eq 720 -and $config.camera.fps -eq 30 -and -not $config.camera.uncompressed -and $config.camera.microphone -and $config.loopback_only -and $config.usb_port -eq 13260 -and $config.video_port -eq 13261) 'Recovery must preserve the camera mode and restore only fixed loopback endpoints.'
    $archives = @(Get-ChildItem -LiteralPath $fixture.Data -Filter '*.corrupt-*')
    Assert-True ($archives.Count -eq 2) 'Original corrupt configuration and process state must be archived before replacement.'
    foreach ($archive in $archives) { Assert-True (Test-HorizonCameraEmptyMetadata -Bytes ([IO.File]::ReadAllBytes($archive.FullName))) 'A corrupt archive must preserve original NUL-filled bytes.' }
    Assert-True (([Convert]::ToBase64String([IO.File]::ReadAllBytes($fixture.Key))) -ceq $keyBefore -and (Test-HorizonCameraEmptyMetadata -Bytes ([IO.File]::ReadAllBytes($fixture.State))) -and $global:HorizonMetadataFixtureProcessReads -eq 0) 'Recovery must leave the key and old state untouched without looking up its PID.'
    Assert-True (@(Get-ChildItem -LiteralPath $fixture.Data -Filter '*.tmp').Count -eq 0) 'A completed metadata write must not leave temporary files.'
    # Simulate setup failing at driver-ready after metadata restoration. The
    # valid config plus old NUL state must still permit the user's Delete.
    $stop = Copy-StopFixture $fixture
    $removedAfterRestore = & $stop -Remove -MetadataOnly -PassThru
    Assert-True ($removedAfterRestore.Removed -and -not $removedAfterRestore.Stopped -and -not [IO.File]::Exists($fixture.Config) -and -not [IO.File]::Exists($fixture.State) -and -not [IO.File]::Exists($fixture.Key)) 'Delete must remain available if setup stopped between restoring the config and starting its receiver.'
    Assert-True (@(Get-ChildItem -LiteralPath $fixture.Data -Filter '*.corrupt-*').Count -eq 3 -and $global:HorizonMetadataFixtureProcessReads -eq 0 -and $global:HorizonMetadataFixtureStopCalls -eq 0) 'Restore-then-Delete must archive the remaining corrupt state without a stale-PID lookup or stop.'
    $fixture = New-Fixture 'missing-key'
    [IO.File]::Delete($fixture.Key)
    $null = Assert-Rejected { Restore-HorizonCameraMetadata -Root $fixture.Root -TakeOwnership } '*key is missing*does not create or replace keys*' 'Recovery must not silently generate a new private key.'
    Assert-True (-not [IO.File]::Exists($fixture.Key) -and (Test-HorizonCameraEmptyMetadata -Bytes ([IO.File]::ReadAllBytes($fixture.Config)))) 'Missing-key recovery must leave the corrupt configuration untouched.'

    # Execute only a copied Stop script. All listener, backend, process and PnP
    # operations are replaced by synthetic helpers, and its mutex is isolated.
    foreach ($stateType in @('nul', 'truncated')) {
        $fixture = New-Fixture ('remove-recovered-' + $stateType)
        if ($stateType -eq 'truncated') { [IO.File]::WriteAllText($fixture.State, '{"process_id":') }
        $received = Join-Path $fixture.Root 'Received'
        $null = [IO.Directory]::CreateDirectory($received)
        $receivedFile = Join-Path $received 'keep.txt'
        [IO.File]::WriteAllText($receivedFile, 'synthetic received file')
        $stop = Copy-StopFixture $fixture
        $removed = & $stop -Remove -MetadataOnly -PassThru
        Assert-True ($removed.Removed -and -not $removed.Stopped -and $global:HorizonMetadataFixturePresenceReads -eq 1) 'Metadata-only Delete must work for a recovered identity only after exact-root absence.'
        foreach ($path in @($fixture.Config, $fixture.State, $fixture.StatusPath, $fixture.Key)) { Assert-True (-not [IO.File]::Exists($path)) 'Explicit metadata-only Delete must remove only fixed identity files and its key.' }
        Assert-True (@(Get-ChildItem -LiteralPath $fixture.Data -Filter '*.corrupt-*').Count -eq 2 -and [IO.File]::Exists($receivedFile)) 'Delete must retain corrupt archives and received files.'
        Assert-True ((Get-HorizonCameraJson -Path (Join-Path $fixture.Data 'camera.identity.deleted.json')).serial -ceq $ownedSerial -and $global:HorizonMetadataFixtureStopCalls -eq 0 -and $global:HorizonMetadataFixtureProcessReads -eq 0) 'Recovered Delete must record the identity without any stop, detach or stale-PID lookup.'
    }
    foreach ($stateType in @('empty', 'nul')) {
        $fixture = New-Fixture ('remove-valid-config-' + $stateType)
        Write-HorizonCameraJson -Path $fixture.Config -Value $fixture.Configuration
        if ($stateType -eq 'empty') { [IO.File]::WriteAllBytes($fixture.State, [byte[]]::new(0)) }
        # The valid owned config is sufficient identity; no stale status/PID is
        # needed for metadata cleanup after proving exact root absence.
        [IO.File]::Delete($fixture.StatusPath)
        $stop = Copy-StopFixture $fixture
        $removed = & $stop -Remove -MetadataOnly -PassThru
        Assert-True ($removed.Removed -and -not $removed.Stopped -and -not [IO.File]::Exists($fixture.Key)) 'A valid owned config with interrupted state must remain deletable without a stale status.'
        Assert-True (@(Get-ChildItem -LiteralPath $fixture.Data -Filter '*.corrupt-*').Count -eq 1) 'Only the interrupted process-state file needs an archive when the config is valid.'
        Assert-True ($global:HorizonMetadataFixturePresenceReads -eq 1 -and $global:HorizonMetadataFixtureProcessReads -eq 0 -and $global:HorizonMetadataFixtureStopCalls -eq 0) 'Empty/NUL process state must never authorize a process lookup or stop.'
    }
    foreach ($failure in @('active-backend', 'present-root', 'foreign-status', 'truncated-state-only')) {
        $fixture = New-Fixture ('remove-blocked-' + $failure)
        if ($failure -like '*state-only') {
            Write-HorizonCameraJson -Path $fixture.Config -Value $fixture.Configuration
            if ($failure -eq 'truncated-state-only') { [IO.File]::WriteAllText($fixture.State, '{"process_id":') }
        }
        if ($failure -eq 'active-backend') { $global:HorizonMetadataFixtureBackendActive = $true; $global:HorizonMetadataFixturePortsActive = $true }
        if ($failure -eq 'present-root') { $global:HorizonMetadataFixturePresent = $true }
        if ($failure -eq 'foreign-status') { $fixture.Status.camera.serial = 'Foreign-Camera-1'; Write-FixtureStatus $fixture }
        $stop = Copy-StopFixture $fixture
        $before = Get-FixtureSnapshot $fixture
        $null = Assert-Rejected { & $stop -Remove -MetadataOnly -PassThru } '' 'Unverified endpoints, present roots, foreign status and standalone damaged process state must prevent metadata deletion.'
        Assert-True ((Get-FixtureSnapshot $fixture) -ceq $before -and @(Get-ChildItem -LiteralPath $fixture.Data -Filter '*.corrupt-*').Count -eq 0) 'A rejected metadata-only Delete must retain the original files and key.'
        Assert-True ($global:HorizonMetadataFixtureStopCalls -eq 0 -and $global:HorizonMetadataFixtureProcessReads -eq 0) 'Metadata-only rejection must happen without stopping or looking up any process.'
    }

    foreach ($path in @('scripts\Camera-Runtime.ps1', 'scripts\Setup-Camera.ps1', 'scripts\Stop-Camera.ps1')) {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $sourceDirectory $path), [ref]$tokens, [ref]$errors)
        Assert-True ($errors.Count -eq 0) ('Metadata changes must parse in Windows PowerShell: ' + $path)
    }
    Write-Output ('Camera metadata fixture checks passed: ' + $script:checks + ' (bounded UTF-8, durable writes, read-only same-identity crash recovery, scoped metadata-only Delete, archives and key retention; no hardware or real processes).')
} finally {
    $canonicalFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $canonicalBuild = [IO.Path]::GetFullPath((Join-Path $sourceDirectory 'build')) + [IO.Path]::DirectorySeparatorChar
    if ($canonicalFixture.StartsWith($canonicalBuild, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($canonicalFixture).StartsWith('camera-metadata-tests-')) { [IO.Directory]::Delete($canonicalFixture, $true) }
}
