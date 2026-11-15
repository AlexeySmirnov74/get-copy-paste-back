[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [switch]$Apply
)

# This helper is embedded in Server.exe. The default is a read-only
# plan. The elevated setup helper explicitly supplies -Apply after Create or
# Apply format. Only the exact usbipd-win installation identified in the saved
# crash investigation is eligible. No camera, USBIP2 or VMware driver is changed.
$ErrorActionPreference = 'Stop'
$expectedDriver = 'C:\Program Files\usbipd-win\Drivers\VBoxUSBMon.sys'
$expectedDriverHash = 'FFCAD560AC7714460248631AA17238F4D2AD9FD5AED87531B93DAB410D23FD87'
$expectedHost = 'C:\Program Files\usbipd-win\usbipd.exe'
$expectedHostVersion = '5.3.0.1158'
$expectedForcedDeviceInstanceId = 'USB\VID_1BCF&PID_0B40\AN2020091401'
$expectedForcedDeviceGuid = 'fb313092-4a91-4844-9b08-ad3e193accf1'
$serviceNames = @('usbipd', 'VBoxUSBMon')
$runtimeFullPath = [IO.Path]::GetFullPath($RuntimeRoot)
$dataDirectory = Join-Path $runtimeFullPath 'data'
$backupPath = Join-Path $dataDirectory 'camera-driver-startup-backup.json'
$resultPath = Join-Path $dataDirectory 'camera-driver-repair-result.json'

function Assert-NoReparsePoint([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The repair path contains a reparse point.' }
        $parent = [IO.Directory]::GetParent($candidate)
        if ($null -eq $parent) { break }
        $candidate = $parent.FullName
    }
}

function Assert-OutputPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [String]::Equals([IO.Path]::GetDirectoryName($fullPath), $dataDirectory, [StringComparison]::OrdinalIgnoreCase)) { throw 'Driver repair output must stay in the application data directory.' }
    Assert-NoReparsePoint $dataDirectory
    if (Test-Path -LiteralPath $fullPath) { Assert-NoReparsePoint $fullPath }
}

function Read-MonitorState {
    try { $service = Get-Service -Name 'VBoxUSBMon' -ErrorAction Stop }
    catch {
        if ($_.FullyQualifiedErrorId -like 'NoServiceFoundForGivenName*') { return $null }
        throw
    }
    try { [int]$service.Status }
    finally { if ($service -is [IDisposable]) { $service.Dispose() } }
}

function Read-ServiceConfiguration([string]$Name) {
    if ($serviceNames -notcontains $Name) { throw 'Unexpected service name.' }
    $registryPath = 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\' + $Name
    $key = Get-Item -LiteralPath $registryPath -ErrorAction Stop
    try {
        if ($key.GetValueKind('Start') -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw 'Unexpected startup registry type.' }
        $start = [int]$key.GetValue('Start')
        if ($start -lt 0 -or $start -gt 4) { throw 'Unexpected service startup value.' }
        $hasDelayed = $key.GetValueNames() -contains 'DelayedAutoStart'
        $delayed = $null
        if ($hasDelayed) {
            if ($key.GetValueKind('DelayedAutoStart') -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw 'Unexpected delayed-start registry type.' }
            $delayed = [int]$key.GetValue('DelayedAutoStart')
            if ($delayed -ne 0 -and $delayed -ne 1) { throw 'Unexpected delayed-start flag.' }
        }
        [pscustomobject][ordered]@{
            Name = $Name
            ImagePath = [string]$key.GetValue('ImagePath', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            Type = [int]$key.GetValue('Type')
            Start = $start
            HasDelayedAutoStart = [bool]$hasDelayed
            DelayedAutoStart = $delayed
        }
    }
    finally { $key.Close() }
}

function Find-MonitorFilters([Microsoft.Win32.RegistryKey]$RegistryKey, [string]$RelativePath, [bool]$Recurse, [Collections.Generic.List[string]]$References, [int]$RemainingDepth = 3) {
    foreach ($valueName in @('UpperFilters', 'LowerFilters')) {
        $value = $RegistryKey.GetValue($valueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        foreach ($entry in @($value)) {
            if ($null -ne $entry -and [String]::Equals([string]$entry, 'VBoxUSBMon', [StringComparison]::OrdinalIgnoreCase)) { $References.Add($RelativePath + '\' + $valueName) }
        }
    }
    if (-not $Recurse -or $RemainingDepth -eq 0) { return }
    foreach ($childName in $RegistryKey.GetSubKeyNames()) {
        $child = $RegistryKey.OpenSubKey($childName, $false)
        if ($null -eq $child) { throw 'A registry key changed during mandatory-filter inspection.' }
        try { Find-MonitorFilters $child ($RelativePath + '\' + $childName) $true $References ($RemainingDepth - 1) }
        finally { $child.Close() }
    }
}

function Assert-NoRequiredMonitorFilters {
    $references = New-Object Collections.Generic.List[string]
    $classPath = 'SYSTEM\CurrentControlSet\Control\Class'
    $classRoot = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($classPath, $false)
    if ($null -eq $classRoot) { throw 'Cannot inspect mandatory device-class filters.' }
    try {
        foreach ($className in $classRoot.GetSubKeyNames()) {
            $classKey = $classRoot.OpenSubKey($className, $false)
            if ($null -eq $classKey) { throw 'A device class changed during filter inspection.' }
            try { Find-MonitorFilters $classKey ($classPath + '\' + $className) $false $references }
            finally { $classKey.Close() }
        }
    }
    finally { $classRoot.Close() }
    $enumPath = 'SYSTEM\CurrentControlSet\Enum'
    $enumRoot = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($enumPath, $false)
    if ($null -eq $enumRoot) { throw 'Cannot inspect per-device mandatory filters.' }
    # Inspect only hardware-instance keys, not protected Properties or Device
    # Parameters descendants. A read error fails closed before any mutation.
    try { Find-MonitorFilters $enumRoot $enumPath $true $references 3 }
    finally { $enumRoot.Close() }
    if ($references.Count -gt 0) { throw 'VBoxUSBMon is registered as a mandatory PnP filter; startup repair is not safe.' }
}

function Assert-ExpectedInstallation {
    Assert-NoReparsePoint $expectedDriver
    Assert-NoReparsePoint $expectedHost
    if (-not [String]::Equals((Get-FileHash -LiteralPath $expectedDriver -Algorithm SHA256).Hash, $expectedDriverHash, [StringComparison]::OrdinalIgnoreCase)) { throw 'The USB monitor does not match the driver identified in the crash investigation.' }
    $hostVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($expectedHost).FileVersion
    if ($hostVersion -ne $expectedHostVersion) { throw 'The usbipd-win host version is not the reviewed installation.' }
    $hostConfiguration = Read-ServiceConfiguration 'usbipd'
    $monitor = Read-ServiceConfiguration 'VBoxUSBMon'
    if (-not (Test-ReviewedServiceIdentity $monitor)) { throw 'Unexpected USB monitor service identity.' }
    if (-not (Test-ReviewedServiceIdentity $hostConfiguration)) { throw 'Unexpected usbipd-win host service identity.' }
    Assert-NoRequiredMonitorFilters
    @($hostConfiguration, $monitor)
}

function Test-ReviewedServiceIdentity([object]$Record) {
    if ($null -eq $Record -or $Record.ImagePath -isnot [string] -or $Record.Type -isnot [int]) { return $false }
    if ($Record.Name -eq 'VBoxUSBMon') {
        return ($Record.Type -eq 1 -and @($expectedDriver, ('\??\' + $expectedDriver)) -contains $Record.ImagePath)
    }
    if ($Record.Name -eq 'usbipd') {
        $allowedHostImages = @(('"' + $expectedHost + '" server usbipd:Port=3240'), ('"' + $expectedHost + '" server'))
        return ($Record.Type -eq 16 -and $allowedHostImages -contains $Record.ImagePath)
    }
    return $false
}

function Test-EquivalentServiceIdentity([object]$Saved, [object]$Current) {
    # Official usbipd MSI repair removes its explicit default-port argument.
    # Preserve the original startup backup across that equivalent command line,
    # and the two accepted NT/DOS spellings of the same monitor image path.
    return ($Saved.Name -eq $Current.Name -and (Test-ReviewedServiceIdentity $Saved) -and (Test-ReviewedServiceIdentity $Current))
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights are required for USB driver repair.' }
    }
    finally { $identity.Dispose() }
}

function Write-JsonFile([string]$Path, [object]$Value, [switch]$CreateNew) {
    Assert-OutputPath $Path
    $encoding = New-Object Text.UTF8Encoding($false)
    $mode = [IO.FileMode]::Create
    if ($CreateNew) { $mode = [IO.FileMode]::CreateNew }
    $stream = New-Object IO.FileStream($Path, $mode, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = New-Object IO.StreamWriter($stream, $encoding)
        try { $writer.WriteLine(($Value | ConvertTo-Json -Depth 8)); $writer.Flush(); $stream.Flush($true) }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Read-Backup {
    Assert-OutputPath $backupPath
    $file = Get-Item -LiteralPath $backupPath -ErrorAction Stop
    if ($file.Length -lt 1 -or $file.Length -gt 16384) { throw 'Unexpected driver startup backup size.' }
    $backup = [IO.File]::ReadAllText($backupPath) | ConvertFrom-Json
    if ($backup.Schema -ne 1 -or $backup.MachineName -ne [Environment]::MachineName -or $backup.DriverSha256 -ne $expectedDriverHash) { throw 'The startup backup does not match this PC and driver.' }
    $saved = @($backup.OriginalServices)
    if ($saved.Count -ne 2) { throw 'The startup backup must contain exactly two service records.' }
    for ($index = 0; $index -lt 2; $index++) {
        $record = $saved[$index]
        if ($record.Name -ne $serviceNames[$index] -or $record.Start -isnot [int] -or $record.Start -lt 0 -or $record.Start -gt 4 -or $record.HasDelayedAutoStart -isnot [bool] -or -not (Test-ReviewedServiceIdentity $record)) { throw 'Invalid startup record in backup.' }
        if ($record.HasDelayedAutoStart) {
            if ($record.DelayedAutoStart -isnot [int] -or ($record.DelayedAutoStart -ne 0 -and $record.DelayedAutoStart -ne 1)) { throw 'Invalid delayed-start flag in backup.' }
        }
        elseif ($null -ne $record.DelayedAutoStart) { throw 'Unexpected delayed-start value in backup.' }
    }
    $backup
}

function Ensure-StartupBackup([object[]]$Current) {
    if (Test-Path -LiteralPath $backupPath) {
        $backup = Read-Backup
        for ($index = 0; $index -lt 2; $index++) {
            if (-not (Test-EquivalentServiceIdentity $backup.OriginalServices[$index] $Current[$index])) { throw 'The service identity changed since the saved startup backup.' }
        }
        return
    }
    $backup = [pscustomobject][ordered]@{
        Schema = 1
        MachineName = [Environment]::MachineName
        CreatedUtc = [DateTime]::UtcNow.ToString('o')
        DriverSha256 = $expectedDriverHash
        OriginalServices = $Current
    }
    # Flush the original durable snapshot before changing either service.
    # Never overwrite it, including after partial failure or repeated Create.
    Write-JsonFile $backupPath $backup -CreateNew
}

function Set-StartupDisabled([object]$Current) {
    if ($serviceNames -notcontains $Current.Name) { throw 'Unexpected startup target.' }
    if ($Current.Start -eq 4) { return $false }
    Set-Service -Name $Current.Name -StartupType Disabled -ErrorAction Stop
    $registryPath = 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\' + $Current.Name
    $actual = Read-ServiceConfiguration $Current.Name
    if ($Current.HasDelayedAutoStart) {
        if (-not $actual.HasDelayedAutoStart -or $actual.DelayedAutoStart -ne $Current.DelayedAutoStart) { New-ItemProperty -LiteralPath $registryPath -Name DelayedAutoStart -PropertyType DWord -Value ([int]$Current.DelayedAutoStart) -Force -ErrorAction Stop | Out-Null }
    }
    elseif ($actual.HasDelayedAutoStart) { Remove-ItemProperty -LiteralPath $registryPath -Name DelayedAutoStart -ErrorAction Stop }
    $actual = Read-ServiceConfiguration $Current.Name
    if ($actual.Start -ne 4 -or $actual.HasDelayedAutoStart -ne $Current.HasDelayedAutoStart -or $actual.DelayedAutoStart -ne $Current.DelayedAutoStart) { throw ('Startup verification failed for ' + $Current.Name + '. The original backup is preserved.') }
    return $true
}

function Assert-StartupRemainsDisabled {
    foreach ($name in $serviceNames) {
        if ((Read-ServiceConfiguration $name).Start -ne 4) { throw 'Another USB installer changed the driver startup configuration during camera repair.' }
    }
}

function Invoke-UsbipdCommand([string[]]$Arguments) {
    if ($Arguments.Count -eq 1 -and $Arguments[0] -eq 'state') { $commandLine = 'state' }
    elseif ($Arguments.Count -eq 3 -and $Arguments[0] -eq 'unbind' -and $Arguments[1] -eq '--guid') {
        $guid = [Guid]::Empty
        if (-not [Guid]::TryParse($Arguments[2], [ref]$guid) -or $guid -eq [Guid]::Empty) { throw 'Invalid persisted USB binding identifier.' }
        $commandLine = 'unbind --guid ' + $guid.ToString('D')
    }
    else { throw 'Unexpected usbipd-win repair command.' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $expectedHost
    $start.Arguments = $commandLine
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($expectedHost)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $started = $false
    try {
        if (-not $process.Start()) { throw 'Cannot launch the reviewed USB repair helper.' }
        $started = $true
        $buffer = New-Object char[] 4096
        $text = New-Object Text.StringBuilder
        while ($true) {
            $remaining = 15000 - [int]$clock.ElapsedMilliseconds
            if ($remaining -le 0) { throw 'The USB repair helper timed out.' }
            $read = $process.StandardOutput.ReadAsync($buffer, 0, $buffer.Length)
            if (-not $read.Wait($remaining)) { throw 'The USB repair helper timed out.' }
            $count = $read.Result
            if ($count -eq 0) { break }
            if ($text.Length + $count -gt 65536) { throw 'The USB repair helper response exceeds the metadata limit.' }
            [void]$text.Append($buffer, 0, $count)
        }
        $remaining = 15000 - [int]$clock.ElapsedMilliseconds
        if ($remaining -le 0 -or -not $process.WaitForExit($remaining)) { throw 'The USB repair helper timed out.' }
        if ($process.ExitCode -ne 0) { throw ('The USB repair helper failed with code ' + $process.ExitCode + '.') }
        $text.ToString()
    }
    finally {
        # Only the short-lived CLI instance started above can be terminated.
        # The persistent host service and kernel driver are never force-stopped.
        if ($started -and -not $process.HasExited) { try { $process.Kill() } catch { } }
        $process.Dispose()
    }
}

function Read-UsbipdState {
    $json = Invoke-UsbipdCommand -Arguments @('state')
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 65536) { throw 'USB binding metadata is too large.' }
    $state = $json | ConvertFrom-Json
    if ($null -eq $state -or $state.PSObject.Properties.Name -notcontains 'Devices' -or $null -eq $state.Devices) { throw 'USB binding inventory is missing.' }
    foreach ($device in @($state.Devices)) {
        foreach ($name in @('InstanceId', 'IsForced', 'PersistedGuid', 'ClientIPAddress')) {
            if ($device.PSObject.Properties.Name -notcontains $name) { throw 'Incomplete USB binding inventory.' }
        }
        if ($device.InstanceId -isnot [string] -or [string]::IsNullOrWhiteSpace($device.InstanceId) -or $device.IsForced -isnot [bool]) { throw 'Invalid USB binding inventory.' }
        if ($null -ne $device.ClientIPAddress -and $device.ClientIPAddress -isnot [string]) { throw 'Invalid USB client address.' }
        if ($null -ne $device.PersistedGuid) {
            $guid = [Guid]::Empty
            if ($device.PersistedGuid -isnot [string] -or -not [Guid]::TryParse($device.PersistedGuid, [ref]$guid) -or $guid -eq [Guid]::Empty) { throw 'Invalid persisted USB identifier.' }
        }
    }
    $state
}

function Save-UnboundDeviceBackup([object]$State, [string]$PersistedGuid) {
    $guid = [Guid]::Parse($PersistedGuid).ToString('D')
    $path = Join-Path $dataDirectory ('camera-driver-usbipd-binding-' + $guid + '.json')
    if (Test-Path -LiteralPath $path) {
        Assert-OutputPath $path
        $file = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($file.Length -lt 1 -or $file.Length -gt 131072) { throw 'Invalid saved USB binding backup.' }
        $saved = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        if ($saved.Schema -ne 1 -or $saved.MachineName -ne [Environment]::MachineName -or $saved.PersistedGuid -ne $guid -or $saved.DriverSha256 -ne $expectedDriverHash) { throw 'The USB binding backup does not match this device and PC.' }
        return
    }
    $backup = [pscustomobject][ordered]@{
        Schema = 1; MachineName = [Environment]::MachineName; CreatedUtc = [DateTime]::UtcNow.ToString('o')
        DriverSha256 = $expectedDriverHash; PersistedGuid = $guid; OriginalUsbipdState = $State
    }
    Write-JsonFile $path $backup -CreateNew
}

function Stop-KnownServiceOnce([string]$Name) {
    if ($serviceNames -notcontains $Name) { throw 'Unexpected USB service stop target.' }
    $service = Get-Service -Name $Name -ErrorAction Stop
    try {
        $service.Refresh()
        if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped) { return $true }
        if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running -or -not $service.CanStop) { return $false }
        # One ordinary SCM stop only: never -Force, driver unload IOCTLs,
        # arbitrary device disable, service restart, or automatic Windows reboot.
        $service.Stop()
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(10))
        $service.Refresh()
        return ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped)
    }
    finally { $service.Dispose() }
}

function Release-ConflictingMonitor {
    $state = Read-UsbipdState
    # Active remote USB connections are not disrupted by an automatic repair.
    foreach ($device in @($state.Devices)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$device.ClientIPAddress)) { return [pscustomobject]@{ Stopped = $false; ReleasedDevices = 0; Reason = 'usbipd_has_active_connections' } }
        if ($device.IsForced -and $null -eq $device.PersistedGuid) { return [pscustomobject]@{ Stopped = $false; ReleasedDevices = 0; Reason = 'forced_device_missing_persisted_identifier' } }
        if ($device.IsForced -and (-not [String]::Equals($device.InstanceId, $expectedForcedDeviceInstanceId, [StringComparison]::OrdinalIgnoreCase) -or
            -not [String]::Equals($device.PersistedGuid, $expectedForcedDeviceGuid, [StringComparison]::OrdinalIgnoreCase))) {
            return [pscustomobject]@{ Stopped = $false; ReleasedDevices = 0; Reason = 'unreviewed_forced_usb_device' }
        }
    }
    $released = 0
    foreach ($device in @($state.Devices)) {
        if (-not $device.IsForced) { continue }
        Save-UnboundDeviceBackup $state $device.PersistedGuid
        Invoke-UsbipdCommand -Arguments @('unbind', '--guid', $device.PersistedGuid) | Out-Null
        $released++
    }
    $verified = Read-UsbipdState
    foreach ($device in @($verified.Devices)) {
        if ($device.IsForced -or -not [string]::IsNullOrWhiteSpace([string]$device.ClientIPAddress)) { return [pscustomobject]@{ Stopped = $false; ReleasedDevices = $released; Reason = 'usbipd_binding_remains' } }
    }
    if (-not (Stop-KnownServiceOnce 'usbipd')) { return [pscustomobject]@{ Stopped = $false; ReleasedDevices = $released; Reason = 'usbipd_did_not_stop' } }
    # Recheck binding state after stopping the service and before the monitor.
    $verified = Read-UsbipdState
    foreach ($device in @($verified.Devices)) {
        if ($device.IsForced -or -not [string]::IsNullOrWhiteSpace([string]$device.ClientIPAddress)) { return [pscustomobject]@{ Stopped = $false; ReleasedDevices = $released; Reason = 'usbipd_binding_changed' } }
    }
    $stopped = Stop-KnownServiceOnce 'VBoxUSBMon'
    [pscustomobject]@{ Stopped = $stopped; ReleasedDevices = $released; Reason = $(if ($stopped) { 'monitor_stopped' } else { 'monitor_did_not_stop' }) }
}

function Invoke-CameraDriverRepair([bool]$ApplyChanges) {
    $state = Read-MonitorState
    $result = [pscustomobject][ordered]@{
        Schema = 1; Success = $true; ApplyRequested = $ApplyChanges
        Changed = $false; RestartRequired = $false; DriverState = $state
        Operation = 'none'; BackupPath = $backupPath; Error = $null
        ReleasedDevices = 0; MonitorStopped = $false; ReleaseReason = 'not_needed'
        RecordedUtc = [DateTime]::UtcNow.ToString('o')
    }
    # No loaded conflicting driver: keep an unrelated stopped installation intact.
    if ($null -eq $state -or $state -eq 1) {
        if ($ApplyChanges) {
            Assert-Administrator
            Assert-NoReparsePoint $runtimeFullPath
            [IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
            Assert-NoReparsePoint $dataDirectory
            Write-JsonFile $resultPath $result
        }
        return $result
    }
    if ($state -lt 2 -or $state -gt 7) { throw 'The USB monitor returned an unknown state.' }
    $result.RestartRequired = $true
    $result.Operation = 'disable_known_conflicting_monitor_startup'
    $current = @(Assert-ExpectedInstallation)
    if (-not $ApplyChanges) { return $result }
    Assert-Administrator
    Assert-NoReparsePoint $runtimeFullPath
    [IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
    Assert-NoReparsePoint $dataDirectory
    $fresh = @(Assert-ExpectedInstallation)
    if ($fresh.Count -ne 2 -or $current.Count -ne 2) { throw 'Unexpected USB service inventory.' }
    for ($index = 0; $index -lt 2; $index++) {
        if (($fresh[$index] | ConvertTo-Json -Compress) -ne ($current[$index] | ConvertTo-Json -Compress)) { throw 'USB service configuration changed while preparing repair.' }
    }
    Ensure-StartupBackup $current
    foreach ($service in $current) {
        if (Set-StartupDisabled $service) { $result.Changed = $true }
    }
    Assert-StartupRemainsDisabled
    # Use the official host's exact-GUID unbind operation before one ordinary
    # monitor stop. A refusal or incomplete release requests a manual restart.
    try {
        $release = Release-ConflictingMonitor
        $result.ReleasedDevices = $release.ReleasedDevices
        $result.MonitorStopped = $release.Stopped
        $result.ReleaseReason = $release.Reason
    }
    catch { $result.ReleaseReason = 'normal_release_failed'; $result.Error = $_.Exception.Message }
    Assert-StartupRemainsDisabled
    $result.DriverState = Read-MonitorState
    $result.RestartRequired = ($null -ne $result.DriverState -and $result.DriverState -ne 1)
    Write-JsonFile $resultPath $result
    $result
}

$repairMutex = $null
$repairLockHeld = $false
try {
    if ($Apply) {
        $repairMutex = New-Object Threading.Mutex -ArgumentList $false, 'Global\HorizonServer.CameraDriverRepair'
        try { $repairLockHeld = $repairMutex.WaitOne(5000) }
        catch [Threading.AbandonedMutexException] { $repairLockHeld = $true }
        if (-not $repairLockHeld) { throw 'Another camera driver repair is still running. Wait for it to complete.' }
    }
    Invoke-CameraDriverRepair ([bool]$Apply) | ConvertTo-Json -Depth 8 -Compress
}
catch {
    $failure = [pscustomobject][ordered]@{
        Schema = 1; Success = $false; ApplyRequested = [bool]$Apply
        Changed = $false; RestartRequired = $true; DriverState = $null
        Operation = 'repair_failed'; BackupPath = $backupPath
        Error = $_.Exception.Message; RecordedUtc = [DateTime]::UtcNow.ToString('o')
    }
    if ($Apply -and $repairLockHeld -and (Test-Path -LiteralPath $dataDirectory)) {
        try { Write-JsonFile $resultPath $failure } catch { }
    }
    throw
}
finally {
    if ($repairLockHeld) { $repairMutex.ReleaseMutex() }
    if ($null -ne $repairMutex) { $repairMutex.Dispose() }
}
