[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$path = Join-Path $project 'scripts\Repair-CameraDriver.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
function Import-PureFunction([string]$Name) {
    $node = $ast.Find({ param($item) $item -is [Management.Automation.Language.FunctionDefinitionAst] -and $item.Name -eq $Name }, $true)
    if ($null -eq $node) { throw ('Missing function: ' + $Name) }
    # Define only the requested orchestration function in caller script scope.
    . ([scriptblock]::Create(($node.Extent.Text -replace '^function ', 'function script:')))
}
$script:checks = 0
function Check([bool]$Condition, [string]$Message) { $script:checks++; if (-not $Condition) { throw $Message } }
function Reject([scriptblock]$Action) { $failed = $false; try { & $Action | Out-Null } catch { $failed = $true }; Check $failed 'An unverified repair path was accepted.' }
foreach ($forbidden in @('Restart-Computer', 'Stop-Computer', 'Restart-Service', 'Remove-Service', 'Disable-PnpDevice', 'Invoke-Expression')) {
    $commands = @($ast.FindAll({ param($item) $item -is [Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
    Check ($commands -notcontains $forbidden) ('Forbidden command in production helper: ' + $forbidden)
}
Check (-not ($ast.Extent.Text -match 'Win32_Product|Win32_OperatingSystem|Get-CimInstance|Get-WmiObject')) 'Repair must never enumerate MSI/WMI product inventory.'
Check (-not ($ast.Extent.Text -match 'Stop-Service\s+[^\r\n]*-Force|--all|--hardware-id|Restart-Computer')) 'Repair must never force stop/unbind unrelated devices or restart Windows.'

$serviceNames = @('usbipd', 'VBoxUSBMon')
$expectedDriver = 'C:\Program Files\usbipd-win\Drivers\VBoxUSBMon.sys'
$expectedDriverHash = 'FFCAD560AC7714460248631AA17238F4D2AD9FD5AED87531B93DAB410D23FD87'
$expectedHost = 'C:\Program Files\usbipd-win\usbipd.exe'
$expectedForcedDeviceInstanceId = 'USB\VID_1BCF&PID_0B40\AN2020091401'
$expectedForcedDeviceGuid = 'fb313092-4a91-4844-9b08-ad3e193accf1'
$runtimeFullPath = Join-Path $project 'build\driver-repair-fixture'
$dataDirectory = Join-Path $runtimeFullPath 'data'
$backupPath = Join-Path $dataDirectory 'startup-backup.json'
$resultPath = Join-Path $dataDirectory 'repair-result.json'
$script:actions = New-Object Collections.Generic.List[string]
$script:fixtureState = $null
$script:fixtureStateAfter = 1
$script:fixtureFailure = $null
$script:readCount = 0
$script:fixtureDisabled = $false
function Read-MonitorState {
    $script:readCount++
    if ($script:fixtureFailure -eq 'query') { throw 'Synthetic permission error.' }
    if ($script:readCount -gt 1) { return $script:fixtureStateAfter }
    $script:fixtureState
}
function New-ServiceFixture([string]$Name, [int]$Start) {
    [pscustomobject]@{ Name = $Name; ImagePath = $(if ($Name -eq 'usbipd') { '"' + $expectedHost + '" server usbipd:Port=3240' } else { '\??\' + $expectedDriver }); Type = $(if ($Name -eq 'usbipd') { 16 } else { 1 }); Start = $Start; HasDelayedAutoStart = $false; DelayedAutoStart = $null }
}
function Assert-ExpectedInstallation {
    $script:actions.Add('verify')
    if ($script:fixtureFailure -eq 'identity') { throw 'Synthetic hash/filter/identity rejection.' }
    if ($script:fixtureDisabled) { $starts = @(4,4) } else { $starts = @(2,3) }
    @(New-ServiceFixture 'usbipd' $starts[0]; New-ServiceFixture 'VBoxUSBMon' $starts[1])
}
function Assert-Administrator { $script:actions.Add('administrator') }
function Assert-NoReparsePoint([string]$Path) { }
function Ensure-StartupBackup([object[]]$Current) { $script:actions.Add('backup') }
function Set-StartupDisabled([object]$Current) {
    $script:actions.Add(('disable-' + $Current.Name))
    Check ($script:actions.Contains('backup')) 'Startup changed before original backup.'
    return ($Current.Start -ne 4)
}
function Assert-StartupRemainsDisabled { $script:actions.Add('verify-disabled') }
function Release-ConflictingMonitor { $script:actions.Add('release'); [pscustomobject]@{ Stopped = $true; ReleasedDevices = 1; Reason = 'monitor_stopped' } }
function Write-JsonFile([string]$Path, [object]$Value, [switch]$CreateNew) { $script:actions.Add('write-result') }
Import-PureFunction 'Invoke-CameraDriverRepair'
function Reset-Fixture([object]$State) {
    $script:fixtureState = $State; $script:fixtureStateAfter = 1; $script:fixtureFailure = $null; $script:readCount = 0; $script:fixtureDisabled = $false; $script:actions.Clear()
}
foreach ($state in @($null, 1)) {
    Reset-Fixture $state
    $result = Invoke-CameraDriverRepair $true
    Check ($result.Success -and -not $result.Changed -and -not $result.RestartRequired) 'Absent/stopped driver must stay untouched.'
    Check (($script:actions -join ',') -eq 'administrator,write-result' -and $script:readCount -eq 1) 'Absent/stopped driver must only publish a fresh result without installation/startup actions.'
    Reset-Fixture $state
    $result = Invoke-CameraDriverRepair $false
    Check ($script:actions.Count -eq 0 -and $script:readCount -eq 1) 'Read-only absent/stopped plan must not write a result.'
}
Reset-Fixture 4
$result = Invoke-CameraDriverRepair $false
Check ($result.RestartRequired -and -not $result.Changed) 'Read-only plan must report loaded driver without pretending to repair.'
Check (($script:actions -join ',') -eq 'verify') 'Read-only plan attempted a mutation.'
Reset-Fixture 4
$result = Invoke-CameraDriverRepair $true
Check ($result.Success -and $result.Changed -and -not $result.RestartRequired -and $result.MonitorStopped) 'Verified ordinary unload should permit camera creation.'
Check (($script:actions -join ',') -eq 'verify,administrator,verify,backup,disable-usbipd,disable-VBoxUSBMon,verify-disabled,release,verify-disabled,write-result') 'Repair actions ran out of order.'
Reset-Fixture 4
$script:fixtureDisabled = $true
$script:fixtureStateAfter = 4
$result = Invoke-CameraDriverRepair $true
Check (-not $result.Changed -and $result.RestartRequired) 'Already disabled startup must be idempotent while loaded state still blocks creation.'
foreach ($failure in @('query', 'identity')) {
    Reset-Fixture 4
    $script:fixtureFailure = $failure
    Reject { Invoke-CameraDriverRepair $true }
    Check (-not $script:actions.Contains('backup') -and -not $script:actions.Contains('disable-usbipd')) 'Verification error attempted service changes.'
}
Reset-Fixture 99
Reject { Invoke-CameraDriverRepair $true }
Check ($script:actions.Count -eq 0) 'Unknown SCM state was treated optimistically.'

# Now exercise the real release orchestration. All metadata/helper/SCM calls
# below are in-memory fixtures: no Windows registry/service/exe is contacted.
Import-PureFunction 'Release-ConflictingMonitor'
$fixtureGuid = 'fb313092-4a91-4844-9b08-ad3e193accf1'
function New-Device([bool]$Forced, [object]$Guid, [object]$Client) { [pscustomobject]@{ InstanceId = $expectedForcedDeviceInstanceId; IsForced = $Forced; PersistedGuid = $Guid; ClientIPAddress = $Client } }
$script:states = @()
$script:stateRead = 0
$script:stopHost = $true
$script:stopMonitor = $true
function Read-UsbipdState {
    $index = [Math]::Min($script:stateRead, $script:states.Count - 1)
    $script:stateRead++
    $script:states[$index]
}
function Save-UnboundDeviceBackup([object]$State, [string]$PersistedGuid) { $script:actions.Add(('binding-backup-' + $PersistedGuid)) }
function Invoke-UsbipdCommand([string[]]$Arguments) {
    Check ($Arguments.Count -eq 3 -and $Arguments[0] -eq 'unbind' -and $Arguments[1] -eq '--guid' -and $Arguments[2] -eq $fixtureGuid) 'Release invoked an unexpected USB command.'
    Check ($script:actions.Contains(('binding-backup-' + $fixtureGuid))) 'USB binding changed before its backup.'
    $script:actions.Add('unbind-guid')
}
function Stop-KnownServiceOnce([string]$Name) {
    $script:actions.Add(('stop-' + $Name))
    if ($Name -eq 'usbipd') { return $script:stopHost }
    if ($Name -eq 'VBoxUSBMon') { return $script:stopMonitor }
    throw 'Unexpected service stop target.'
}
function Reset-Release([object[]]$States) {
    $script:states = $States; $script:stateRead = 0; $script:stopHost = $true; $script:stopMonitor = $true; $script:actions.Clear()
}
$forced = [pscustomobject]@{ Devices = @(New-Device $true $fixtureGuid $null) }
$empty = [pscustomobject]@{ Devices = @() }
Reset-Release @($forced, $empty, $empty)
$result = Release-ConflictingMonitor
Check ($result.Stopped -and $result.ReleasedDevices -eq 1) 'Known inactive forced binding was not released.'
Check (($script:actions -join ',') -eq ('binding-backup-' + $fixtureGuid + ',unbind-guid,stop-usbipd,stop-VBoxUSBMon')) 'Release order must be durable backup, exact unbind, host stop, monitor stop.'
Reset-Release @([pscustomobject]@{ Devices = @(New-Device $true $fixtureGuid '192.0.2.1') })
$result = Release-ConflictingMonitor
Check (-not $result.Stopped -and $result.Reason -eq 'usbipd_has_active_connections' -and $script:actions.Count -eq 0) 'Active USB connection was disrupted.'
Reset-Release @([pscustomobject]@{ Devices = @(New-Device $true $null $null) })
$result = Release-ConflictingMonitor
Check (-not $result.Stopped -and $result.Reason -eq 'forced_device_missing_persisted_identifier' -and $script:actions.Count -eq 0) 'Unknown forced device was released by a broad command.'
$otherDevice = New-Device $true $fixtureGuid $null
$otherDevice.InstanceId = 'USB\VID_FFFF&PID_0001\unrelated'
Reset-Release @([pscustomobject]@{ Devices = @($otherDevice) })
$result = Release-ConflictingMonitor
Check (-not $result.Stopped -and $result.Reason -eq 'unreviewed_forced_usb_device' -and $script:actions.Count -eq 0) 'Matching GUID must not permit unbinding another physical device.'
Reset-Release @([pscustomobject]@{ Devices = @(New-Device $true 'e7b9a758-9505-4313-82a5-f2b34c1b7d3d' $null) })
$result = Release-ConflictingMonitor
Check (-not $result.Stopped -and $result.Reason -eq 'unreviewed_forced_usb_device' -and $script:actions.Count -eq 0) 'Matching device must not permit unbinding an unreviewed persisted identifier.'
Reset-Release @($forced, $forced)
$result = Release-ConflictingMonitor
Check (-not $result.Stopped -and -not $script:actions.Contains('stop-usbipd') -and -not $script:actions.Contains('stop-VBoxUSBMon')) 'A remaining forced binding must prevent service unload.'
Reset-Release @($empty, $empty)
$script:stopHost = $false
$result = Release-ConflictingMonitor
Check (-not $result.Stopped -and -not $script:actions.Contains('stop-VBoxUSBMon')) 'Host stop refusal must prevent monitor unload.'
Reset-Release @($empty, $empty, $forced)
$result = Release-ConflictingMonitor
Check (-not $result.Stopped -and $result.Reason -eq 'usbipd_binding_changed' -and -not $script:actions.Contains('stop-VBoxUSBMon')) 'New binding before monitor stop was ignored.'
Reset-Release @($empty, $empty, $empty)
$script:stopMonitor = $false
$result = Release-ConflictingMonitor
Check (-not $result.Stopped -and $result.Reason -eq 'monitor_did_not_stop') 'Normal SCM stop refusal must request restart.'

# Validate durable startup-backup reuse: originals must not be overwritten by
# a later retry after startup has already changed to Disabled.
Import-PureFunction 'Ensure-StartupBackup'
Import-PureFunction 'Test-ReviewedServiceIdentity'
Import-PureFunction 'Test-EquivalentServiceIdentity'
$script:backupExists = $false
$script:savedBackup = $null
$script:backupWrites = 0
function Test-Path([string]$LiteralPath) { $script:backupExists }
function Read-Backup { $script:savedBackup }
function Write-JsonFile([string]$Path, [object]$Value, [switch]$CreateNew) {
    Check ($CreateNew.IsPresent) 'Original startup backup must use CreateNew.'
    $script:backupWrites++; $script:savedBackup = $Value; $script:backupExists = $true
}
$original = @(New-ServiceFixture 'usbipd' 2; New-ServiceFixture 'VBoxUSBMon' 3)
Ensure-StartupBackup $original
Check ($script:backupWrites -eq 1 -and $script:savedBackup.OriginalServices[0].Start -eq 2 -and $script:savedBackup.OriginalServices[1].Start -eq 3) 'Original startup configuration was not saved.'
Ensure-StartupBackup @(New-ServiceFixture 'usbipd' 4; New-ServiceFixture 'VBoxUSBMon' 4)
Check ($script:backupWrites -eq 1 -and $script:savedBackup.OriginalServices[0].Start -eq 2 -and $script:savedBackup.OriginalServices[1].Start -eq 3) 'Repeated repair overwrote original startup settings.'
$msiCurrent = @(New-ServiceFixture 'usbipd' 4; New-ServiceFixture 'VBoxUSBMon' 4)
$msiCurrent[0].ImagePath = '"' + $expectedHost + '" server'
$msiCurrent[1].ImagePath = $expectedDriver
Ensure-StartupBackup $msiCurrent
Check ($script:backupWrites -eq 1 -and $script:savedBackup.OriginalServices[0].Start -eq 2) 'An equivalent official MSI command line must reuse the original startup backup.'
$unreviewedHost = New-ServiceFixture 'usbipd' 4
$unreviewedHost.ImagePath = '"' + $expectedHost + '" server --unreviewed'
Check (-not (Test-ReviewedServiceIdentity $unreviewedHost)) 'A novel host command line must not be accepted as equivalent.'
$unreviewedMonitor = New-ServiceFixture 'VBoxUSBMon' 4
$unreviewedMonitor.ImagePath = 'C:\Other\VBoxUSBMon.sys'
Check (-not (Test-ReviewedServiceIdentity $unreviewedMonitor)) 'Another monitor path must not be accepted.'
$mismatch = @(New-ServiceFixture 'usbipd' 4; New-ServiceFixture 'VBoxUSBMon' 4)
$mismatch[1].ImagePath = 'different-driver'
Reject { Ensure-StartupBackup $mismatch }
Check ($script:backupWrites -eq 1) 'Changed service identity overwrote original backup.'

# Round-trip the legacy reviewed schema using only a disposable fixture file
# in build/. This does not read runtime configuration or real machine services.
Import-PureFunction 'Read-Backup'
function Assert-OutputPath([string]$Path) {
    Check ([String]::Equals([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path)), $dataDirectory, [StringComparison]::OrdinalIgnoreCase)) 'Fixture backup escaped its build directory.'
}
$backupPath = Join-Path $dataDirectory 'startup-readback-fixture.json'
$script:savedBackup | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $backupPath -Encoding UTF8
$roundTrip = Read-Backup
Check ($roundTrip.Schema -eq 1 -and $roundTrip.DriverSha256 -eq $expectedDriverHash -and $roundTrip.OriginalServices[0].Start -eq 2 -and $roundTrip.OriginalServices[1].Start -eq 3) 'The original reviewed backup schema must remain compatible.'
$invalidBackup = $script:savedBackup | ConvertTo-Json -Depth 8 | ConvertFrom-Json
$invalidBackup.MachineName = 'another-machine'
$invalidBackup | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $backupPath -Encoding UTF8
Reject { Read-Backup }
[pscustomobject]@{ ChecksPassed = $script:checks; ParseErrors = 0; ProductionScriptExecuted = $false; WindowsServiceQueries = 0; WindowsServiceChanges = 0; WindowsRegistryChanges = 0; UsbipdCommandsExecuted = 0 } | ConvertTo-Json
