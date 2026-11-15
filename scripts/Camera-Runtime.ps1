# Shared camera endpoint discovery. Dot-source this file from launchers.
# Discovery never reads connection-key contents or changes processes/devices.

function Assert-HorizonCameraOutputPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $components = [Collections.Generic.List[string]]::new()
    for ($component = $full; -not [string]::IsNullOrEmpty($component); $component = [IO.Path]::GetDirectoryName($component)) {
        $components.Add($component)
    }
    for ($index = $components.Count - 1; $index -ge 0; $index--) {
        $component = $components[$index]
        try { $attributes = [IO.File]::GetAttributes($component) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Camera output path contains a redirection.' }
        $directory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
        if (($index -eq 0 -and $directory) -or ($index -gt 0 -and -not $directory)) { throw 'Camera output path has a file/directory collision.' }
    }
    # This check rejects existing redirections/collisions. Start-Process opens
    # these paths later; it does not provide an atomic no-follow open contract.
    return $full
}

function Initialize-HorizonCameraNative {
    if (-not ('HorizonClient.CameraRuntimeNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace HorizonClient {
    public sealed class CameraListener {
        public int Port { get; set; }
        public int ProcessId { get; set; }
        public string Address { get; set; }
    }
    public sealed class CameraProcess {
        public int Id { get; set; }
        public string Path { get; set; }
        public string StartTimeUtcTicks { get; set; }
    }

    public static class CameraRuntimeNative {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeTime { public uint Low, High; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out NativeTime created, out NativeTime exited, out NativeTime kernel, out NativeTime user);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
            bool order, int family, int tableClass, uint reserved);

        public static CameraProcess ProcessInfo(int processId) {
            // This access right works for the same user's elevated backend from
            // the normal GUI, without VM-read or process-termination privileges.
            IntPtr process = OpenProcess(0x1000, false, processId);
            if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                int size = 32768;
                var path = new StringBuilder(size);
                if (!QueryFullProcessImageNameW(process, 0, path, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                NativeTime created, exited, kernel, user;
                if (!GetProcessTimes(process, out created, out exited, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
                long fileTime = ((long)created.High << 32) | created.Low;
                return new CameraProcess { Id = processId, Path = path.ToString(), StartTimeUtcTicks = DateTime.FromFileTimeUtc(fileTime).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            } finally { CloseHandle(process); }
        }

        public static CameraListener[] Listeners() {
            const uint insufficientBuffer = 122;
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
            if (result != insufficientBuffer && result != 0)
                throw new Win32Exception((int)result);
            for (int attempt = 0; attempt < 3; attempt++) {
                IntPtr table = Marshal.AllocHGlobal(Math.Max(size, 4));
                try {
                    result = GetExtendedTcpTable(table, ref size, false, 2, 3, 0);
                    if (result == insufficientBuffer) continue;
                    if (result != 0) throw new Win32Exception((int)result);
                    int count = Marshal.ReadInt32(table);
                    if (count < 0 || count > (size - 4) / 24)
                        throw new InvalidOperationException("Invalid TCP owner table.");
                    var listeners = new List<CameraListener>();
                    for (int index = 0; index < count; index++) {
                        IntPtr row = IntPtr.Add(table, 4 + index * 24);
                        if (Marshal.ReadInt32(row) != 2) continue;
                        uint address = unchecked((uint)Marshal.ReadInt32(row, 4));
                        // A wildcard listener also occupies the loopback endpoint.
                        if (address != 0 && address != 0x0100007f) continue;
                        uint encodedPort = unchecked((uint)Marshal.ReadInt32(row, 8));
                        int port = (int)(((encodedPort & 255) << 8) | ((encodedPort >> 8) & 255));
                        if (port != 13260 && port != 13261) continue;
                        listeners.Add(new CameraListener {
                            Port = port,
                            ProcessId = Marshal.ReadInt32(row, 20),
                            Address = new IPAddress((long)address).ToString()
                        });
                    }
                    return listeners.ToArray();
                } finally { Marshal.FreeHGlobal(table); }
            }
            throw new InvalidOperationException("The TCP owner table changed repeatedly.");
        }
    }
}
'@ -ErrorAction Stop
    }
}

function Get-HorizonCameraListeners {
    Initialize-HorizonCameraNative
    [HorizonClient.CameraRuntimeNative]::Listeners()
}

function Get-HorizonCameraProcess {
    param([Parameter(Mandatory = $true)][int]$ProcessId)
    try {
        Initialize-HorizonCameraNative
        [HorizonClient.CameraRuntimeNative]::ProcessInfo($ProcessId)
    } catch {
        throw 'The process using the camera ports could not be verified. Existing processes and devices were left unchanged.'
    }
}

function Get-HorizonCameraFileHash {
    param([Parameter(Mandatory = $true)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash
}

function Read-HorizonCameraJsonBytes {
    param([Parameter(Mandatory = $true)][string]$Path)
    # A malformed metadata file must never allocate an unbounded buffer or put
    # its contents (which could contain unrelated user data) in an exception.
    $name = [IO.Path]::GetFileName($Path)
    $stream = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $length = $stream.Length
        if ($length -gt 65536) { throw ('Camera metadata ''{0}'' is too large ({1} bytes; maximum 65536). Existing files and keys were left unchanged.' -f $name, $length) }
        $buffer = [byte[]]::new(65537)
        $count = 0
        while ($count -lt $buffer.Length) {
            $read = $stream.Read($buffer, $count, $buffer.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        if ($count -gt 65536) { throw ('Camera metadata ''{0}'' is too large (at least {1} bytes; maximum 65536). Existing files and keys were left unchanged.' -f $name, $count) }
        $bytes = [byte[]]::new($count)
        [Array]::Copy($buffer, $bytes, $count)
        return ,$bytes
    } finally {
        if ($stream) { $stream.Dispose() }
    }
}

function ConvertFrom-HorizonCameraJsonBytes {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)
    $name = [IO.Path]::GetFileName($Path)
    if ($Bytes.Length -gt 65536) { throw ('Camera metadata ''{0}'' is too large ({1} bytes; maximum 65536). Existing files and keys were left unchanged.' -f $name, $Bytes.Length) }
    try {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xfeff) { $text = $text.Substring(1) }
        if ([String]::IsNullOrWhiteSpace($text) -or $text.IndexOf([char]0) -ge 0) { throw 'Invalid JSON text.' }
        $value = ConvertFrom-Json -InputObject $text -ErrorAction Stop
        if ($null -eq $value -or $value -isnot [System.Management.Automation.PSCustomObject]) { throw 'An object is required.' }
        return $value
    } catch {
        throw ('Camera metadata ''{0}'' is invalid ({1} bytes). Existing files and keys were left unchanged.' -f $name, $Bytes.Length)
    }
}

function Get-HorizonCameraJson {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bytes = Read-HorizonCameraJsonBytes -Path $Path
    ConvertFrom-HorizonCameraJsonBytes -Path $Path -Bytes $bytes
}

function Test-HorizonCameraEmptyMetadata {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)
    foreach ($value in $Bytes) { if ($value -ne 0) { return $false } }
    # An empty file and a file containing only NUL bytes are the narrowly
    # recognized interrupted-write case. Other malformed JSON is not recovered.
    return $true
}

function Write-HorizonCameraDurableBytes {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)
    $stream = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    } finally {
        if ($stream) { $stream.Dispose() }
    }
}

function Write-HorizonCameraJson {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Value, [switch]$BackupPrevious)
    $Path = [IO.Path]::GetFullPath($Path)
    $json = ConvertTo-Json -InputObject $Value -Depth 10
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    $null = ConvertFrom-HorizonCameraJsonBytes -Path $Path -Bytes $bytes
    # A backup is only ever made from a valid document. Recovery explicitly
    # archives damaged files before calling this writer without BackupPrevious.
    if ($BackupPrevious -and [IO.File]::Exists($Path)) { $null = Get-HorizonCameraJson -Path $Path }
    $temporary = Join-Path ([IO.Path]::GetDirectoryName($Path)) ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Write-HorizonCameraDurableBytes -Path $temporary -Bytes $bytes
        if ([IO.File]::Exists($Path)) {
            $backup = if ($BackupPrevious) { $Path + '.bak' } else { [NullString]::Value }
            # Never fall back to delete/copy: failure must keep the original.
            [IO.File]::Replace($temporary, $Path, $backup)
        } else {
            [IO.File]::Move($temporary, $Path)
        }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Save-HorizonCorruptCameraMetadata {
    param([Parameter(Mandatory = $true)][string]$Path)
    # This helper must never be used for a private key or an arbitrary path.
    if ([IO.Path]::GetFileName($Path) -cnotin @('camera.local.json', 'camera-process.local.json')) { throw 'Only fixed camera metadata files may be archived.' }
    $bytes = Read-HorizonCameraJsonBytes -Path $Path
    $archive = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))) ([IO.Path]::GetFileName($Path) + '.corrupt-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N') + '.json')
    Write-HorizonCameraDurableBytes -Path $archive -Bytes $bytes
    return $archive
}

function Get-HorizonCameraCanonicalPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
}

function Test-HorizonCameraSerial {
    param([string]$Serial)
    $Serial -ceq 'HorizonClient-Camera-1' -or $Serial -cmatch '^HorizonClient-[a-fA-F0-9]{32}$'
}

function Initialize-HorizonCameraRemovalNative {
    if (-not ('HorizonClient.CameraRemovalNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HorizonClient {
    // A present-device snapshot only. This helper never opens, disables or
    // removes a device and never returns other devices' names or identities.
    public static class CameraRemovalNative {
        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo {
            public int Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info, StringBuilder id, int capacity, out int required);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        public static bool IsPresent(string serial) {
            // The PowerShell entry point validates ownership before this call.
            string expected = "USB\\VID_1209&PID_F002\\" + serial;
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x00000006); // PRESENT | ALLCLASSES
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var id = new StringBuilder(512);
                for (uint index = 0; index < 65536; index++) {
                    var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                    if (!SetupDiEnumDeviceInfo(set, index, ref info)) {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 259) return false; // ERROR_NO_MORE_ITEMS
                        throw new Win32Exception(error);
                    }
                    id.Length = 0;
                    int required;
                    if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Capacity, out required)) {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 122 || required <= id.Capacity || required > 32768)
                            throw new Win32Exception(error);
                        id.Capacity = required;
                        if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Capacity, out required))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    if (String.Equals(id.ToString(), expected, StringComparison.OrdinalIgnoreCase)) return true;
                }
                throw new InvalidOperationException("The present-device snapshot exceeds the camera removal query bound.");
            } finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }
}
'@ -ErrorAction Stop
    }
}

function Get-HorizonCameraPresentNode {
    param([Parameter(Mandatory = $true)][string]$Serial)
    if (-not (Test-HorizonCameraSerial -Serial $Serial)) { throw 'The camera removal query requires a verified HorizonServer serial.' }
    Initialize-HorizonCameraRemovalNative
    [HorizonClient.CameraRemovalNative]::IsPresent($Serial)
}

function Wait-HorizonCameraRemoval {
    param(
        [Parameter(Mandatory = $true)][string]$Serial,
        [ValidateRange(1, 20000)][int]$TimeoutMilliseconds = 20000
    )
    if (-not (Test-HorizonCameraSerial -Serial $Serial)) { throw 'The camera removal wait requires a verified HorizonServer serial.' }
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try {
            $present = Get-HorizonCameraPresentNode -Serial $Serial
            if ($present -isnot [bool]) { throw 'The camera presence query did not return a valid result.' }
        } catch {
            throw 'Windows could not confirm removal of the previous camera. Its identity files were retained; restart Windows before recreating it.'
        }
        if (-not $present) { return }
        $remaining = $TimeoutMilliseconds - $elapsed.ElapsedMilliseconds
        if ($remaining -le 0) {
            throw 'Windows has not finished removing the previous camera. Its identity files were retained; restart Windows before recreating it.'
        }
        Start-Sleep -Milliseconds ([int][Math]::Min(200, $remaining))
    }
}

function Test-HorizonVideoMode {
    param([int]$Width, [int]$Height, [int]$FramesPerSecond, [bool]$Uncompressed, [bool]$H264Probe = $false)
    if ($H264Probe -and ($Uncompressed -or $Width -ne 640 -or $Height -ne 480 -or $FramesPerSecond -ne 30)) { return $false }
    $Width -ge 16 -and $Width -le 7680 -and $Height -ge 16 -and $Height -le 4320 -and
        ($Width % 2) -eq 0 -and ($Height % 2) -eq 0 -and $FramesPerSecond -ge 1 -and $FramesPerSecond -le 60 -and
        (-not $Uncompressed -or ([long]$Width * $Height * 16 * $FramesPerSecond) -le [uint32]::MaxValue)
}

function Get-HorizonCameraRecoveryIdentity {
    param([Parameter(Mandatory = $true)][string]$DataDirectory)
    $statusPath = Join-Path $DataDirectory 'camera.local.status.json'
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { throw 'Camera metadata is empty or NUL-filled and camera.local.status.json is missing. Existing files and keys were left unchanged.' }
    $status = Get-HorizonCameraJson -Path $statusPath
    $camera = $status.camera
    $valid = $null -ne $camera -and $camera -is [System.Management.Automation.PSCustomObject]
    if ($valid) {
        foreach ($field in @('name', 'serial', 'width', 'height', 'fps', 'uncompressed', 'h264_probe', 'test_pattern', 'microphone')) {
            if ($null -eq $camera.PSObject.Properties[$field]) { $valid = $false }
        }
    }
    if ($valid) {
        $valid = $camera.name -is [string] -and -not [String]::IsNullOrWhiteSpace($camera.name) -and $camera.name.Length -le 100 -and
            $camera.name -notmatch '[\x00-\x1f\x7f-\x9f]' -and $camera.serial -is [string] -and (Test-HorizonCameraSerial -Serial $camera.serial)
        foreach ($field in @('width', 'height', 'fps')) {
            if ($camera.$field -isnot [int] -and $camera.$field -isnot [long]) { $valid = $false }
        }
        foreach ($field in @('uncompressed', 'h264_probe', 'test_pattern', 'microphone')) {
            if ($camera.$field -isnot [bool]) { $valid = $false }
        }
        if ($valid) {
            $valid = Test-HorizonVideoMode -Width $camera.width -Height $camera.height -FramesPerSecond $camera.fps -Uncompressed $camera.uncompressed -H264Probe $camera.h264_probe
        }
    }
    if (-not $valid) { throw 'Camera metadata cannot be recovered from camera.local.status.json: a complete, valid HorizonServer camera identity is required. Existing files and keys were left unchanged.' }
    # A deletion marker is a veto only, never a source of identity. A leftover
    # status belonging to an explicitly deleted camera must not resurrect it.
    $deletedPath = Join-Path $DataDirectory 'camera.identity.deleted.json'
    if (Test-Path -LiteralPath $deletedPath -PathType Leaf) {
        $deleted = Get-HorizonCameraJson -Path $deletedPath
        if ([string]$deleted.serial -ceq [string]$camera.serial) { throw 'camera.local.status.json belongs to a previously deleted camera. Existing files and keys were left unchanged.' }
    }
    # Do not return process_id, running or device_attached from a stale status.
    [pscustomobject]@{
        Name = [string]$camera.name; Serial = [string]$camera.serial
        Width = [int]$camera.width; Height = [int]$camera.height; FPS = [int]$camera.fps
        Uncompressed = [bool]$camera.uncompressed; H264Probe = [bool]$camera.h264_probe
        Microphone = [bool]$camera.microphone; TestPattern = [bool]$camera.test_pattern
    }
}

function Resolve-HorizonSavedCameraIdentity {
    param([Parameter(Mandatory = $true)][string]$Root)
    $Root = Get-HorizonCameraCanonicalPath -Path $Root
    $data = Join-Path $Root 'data'
    $configPath = Join-Path $data 'camera.local.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { return $null }
    $bytes = Read-HorizonCameraJsonBytes -Path $configPath
    $keyPath = Join-Path $data 'camera.local.key'
    if (Test-HorizonCameraEmptyMetadata -Bytes $bytes) {
        $identity = Get-HorizonCameraRecoveryIdentity -DataDirectory $data
        # Discovery is deliberately read-only. An explicit elevated setup must
        # recheck the endpoints and removal fence before any file is restored.
        return [pscustomobject]@{
            Root = $Root; Executable = (Join-Path $Root 'runtime\usbbridge-camera.exe')
            ConfigPath = $configPath; KeyPath = $keyPath
            StatePath = (Join-Path $data 'camera-process.local.json'); StatusPath = (Join-Path $data 'camera.local.status.json')
            ProcessId = 0; Ready = $false; MatchesBinary = $false
            CameraName = $identity.Name; CameraSerial = $identity.Serial
            Width = $identity.Width; Height = $identity.Height; FPS = $identity.FPS
            Uncompressed = $identity.Uncompressed; H264Probe = $identity.H264Probe
            Microphone = $identity.Microphone; TestPattern = $identity.TestPattern
            MetadataRecoveryRequired = $true; RecoverySource = (Join-Path $data 'camera.local.status.json')
            MetadataByteCount = $bytes.Length
        }
    }
    $configuration = ConvertFrom-HorizonCameraJsonBytes -Path $configPath -Bytes $bytes
    if (-not (Test-HorizonCameraSerial -Serial ([string]$configuration.camera.serial)) -or
        $configuration.usb_port -ne 13260 -or $configuration.video_port -ne 13261 -or $configuration.loopback_only -ne $true -or
        (Get-HorizonCameraCanonicalPath -Path ([string]$configuration.key_file)) -ine $keyPath -or
        [String]::IsNullOrWhiteSpace([string]$configuration.camera.name) -or ([string]$configuration.camera.name).Length -gt 100 -or
        ([string]$configuration.camera.name) -match '[\x00-\x1f\x7f-\x9f]' -or
        -not (Test-HorizonVideoMode -Width $configuration.camera.width -Height $configuration.camera.height -FramesPerSecond $configuration.camera.fps -Uncompressed ([bool]$configuration.camera.uncompressed) -H264Probe ([bool]$configuration.camera.h264_probe))) {
        throw 'The saved camera identity is not owned by HorizonServer. Its files were left unchanged.'
    }
    $addresses = @($configuration.allowed_ips)
    if ($addresses.Count -eq 0) { throw 'The saved camera identity has no loopback allowlist.' }
    foreach ($address in $addresses) { if ([string]$address -notin @('127.0.0.1', '::1')) { throw 'The saved camera identity is not limited to loopback.' } }
    [pscustomobject]@{
        Root = $Root; Executable = (Join-Path $Root 'runtime\usbbridge-camera.exe')
        ConfigPath = $configPath; KeyPath = $keyPath
        StatePath = (Join-Path $data 'camera-process.local.json'); StatusPath = (Join-Path $data 'camera.local.status.json')
        ProcessId = 0; Ready = $false; MatchesBinary = $false
        CameraName = [string]$configuration.camera.name; CameraSerial = [string]$configuration.camera.serial
        Width = [int]$configuration.camera.width; Height = [int]$configuration.camera.height
        FPS = [int]$configuration.camera.fps; Uncompressed = [bool]$configuration.camera.uncompressed; H264Probe = [bool]$configuration.camera.h264_probe
        Microphone = [bool]$configuration.camera.microphone; TestPattern = [bool]$configuration.camera.test_pattern
        MetadataRecoveryRequired = $false; RecoverySource = $null; MetadataByteCount = $bytes.Length
    }
}

function Restore-HorizonCameraMetadata {
    param([Parameter(Mandatory = $true)][string]$Root, [switch]$TakeOwnership)
    if (-not $TakeOwnership) { throw 'Restoring crash-damaged camera metadata requires an explicit camera setup with TakeOwnership. Existing files and keys were left unchanged.' }
    $identity = Resolve-HorizonSavedCameraIdentity -Root $Root
    if (-not $identity -or -not $identity.MetadataRecoveryRequired) { throw 'No narrowly recoverable crash-damaged camera identity was found. Existing files and keys were left unchanged.' }
    if (@(Get-HorizonCameraListeners).Count -ne 0) { throw 'Camera metadata cannot be restored while a camera endpoint is active. Existing processes, files, keys and devices were left unchanged.' }
    Wait-HorizonCameraRemoval -Serial $identity.CameraSerial
    # The caller holds the camera bootstrap mutex. Still re-read after waiting:
    # another tool must not change the recovery identity under an old snapshot.
    $current = Resolve-HorizonSavedCameraIdentity -Root $Root
    if (-not $current -or -not $current.MetadataRecoveryRequired -or
        $current.CameraSerial -cne $identity.CameraSerial -or $current.CameraName -cne $identity.CameraName -or
        $current.Width -ne $identity.Width -or $current.Height -ne $identity.Height -or $current.FPS -ne $identity.FPS -or
        $current.Uncompressed -ne $identity.Uncompressed -or $current.H264Probe -ne $identity.H264Probe -or
        $current.Microphone -ne $identity.Microphone -or $current.TestPattern -ne $identity.TestPattern -or
        @(Get-HorizonCameraListeners).Count -ne 0) { throw 'The camera recovery identity or endpoint changed. Existing files and keys were left unchanged.' }
    if (-not (Test-Path -LiteralPath $current.KeyPath -PathType Leaf)) { throw 'The existing private camera key is missing. Metadata recovery does not create or replace keys.' }
    $null = Save-HorizonCorruptCameraMetadata -Path $current.ConfigPath
    if (Test-Path -LiteralPath $current.StatePath -PathType Leaf) { $null = Save-HorizonCorruptCameraMetadata -Path $current.StatePath }
    $configuration = [ordered]@{
        enabled = $true
        camera = [ordered]@{
            name = $current.CameraName; serial = $current.CameraSerial; width = $current.Width; height = $current.Height; fps = $current.FPS
            uncompressed = $current.Uncompressed; h264_probe = $current.H264Probe; test_pattern = $current.TestPattern; microphone = $current.Microphone
        }
        usb_port = 13260; video_port = 13261; loopback_only = $true; allowed_ips = @('127.0.0.1'); key_file = $current.KeyPath
    }
    Write-HorizonCameraJson -Path $current.ConfigPath -Value $configuration
    Resolve-HorizonSavedCameraIdentity -Root $Root
}

function Resolve-HorizonCameraBackend {
    param(
        [AllowNull()][object[]]$Listeners,
        [Parameter(Mandatory = $true)][string]$ExpectedExecutable,
        [string]$ExpectedBinaryHash
    )
    if (-not $PSBoundParameters.ContainsKey('Listeners')) { $Listeners = @(Get-HorizonCameraListeners) }
    $records = @($Listeners | Where-Object { $null -ne $_ })
    if ($records.Count -eq 0) { return $null }
    $unverified = 'The camera ports are occupied by an unverified endpoint. Existing processes, keys and devices were left unchanged.'
    foreach ($record in $records) {
        if ([int]$record.Port -notin @(13260, 13261) -or [int]$record.ProcessId -le 0) { throw $unverified }
        if ($record.PSObject.Properties['Address'] -and [string]$record.Address -ne '127.0.0.1') { throw $unverified }
    }
    $owners = @($records | ForEach-Object { [int]$_.ProcessId } | Select-Object -Unique)
    if ($owners.Count -ne 1) { throw $unverified }
    $process = Get-HorizonCameraProcess -ProcessId $owners[0]
    try {
        $executable = Get-HorizonCameraCanonicalPath -Path ([string]$process.Path)
        $runtime = [IO.Path]::GetDirectoryName($executable)
        if ([IO.Path]::GetFileName($executable) -ine 'usbbridge-camera.exe' -or [IO.Path]::GetFileName($runtime) -ine 'runtime') { throw $unverified }
        $root = [IO.Path]::GetDirectoryName($runtime)
        $dataDirectory = Join-Path $root 'data'
        $configPath = Join-Path $dataDirectory 'camera.local.json'
        $keyPath = Join-Path $dataDirectory 'camera.local.key'
        $statePath = Join-Path $dataDirectory 'camera-process.local.json'
        $statusPath = Join-Path $dataDirectory 'camera.local.status.json'
        $state = Get-HorizonCameraJson -Path $statePath
        $configuration = Get-HorizonCameraJson -Path $configPath
        if ([int]$process.Id -ne $owners[0] -or [int]$state.process_id -ne $owners[0] -or
            [string]$state.start_time_utc_ticks -ne [string]$process.StartTimeUtcTicks -or
            (Get-HorizonCameraCanonicalPath -Path ([string]$state.executable_path)) -ine $executable -or
            (Get-HorizonCameraCanonicalPath -Path ([string]$state.config_path)) -ine $configPath) { throw $unverified }
        if ($configuration.enabled -ne $true -or $configuration.loopback_only -ne $true -or
            [int]$configuration.usb_port -ne 13260 -or [int]$configuration.video_port -ne 13261 -or
            -not (Test-HorizonCameraSerial -Serial ([string]$configuration.camera.serial)) -or
            (Get-HorizonCameraCanonicalPath -Path ([string]$configuration.key_file)) -ine $keyPath -or
            -not (Test-HorizonVideoMode -Width $configuration.camera.width -Height $configuration.camera.height -FramesPerSecond $configuration.camera.fps -Uncompressed ([bool]$configuration.camera.uncompressed) -H264Probe ([bool]$configuration.camera.h264_probe))) { throw $unverified }
        $allowedAddresses = @($configuration.allowed_ips)
        if ($allowedAddresses.Count -eq 0) { throw $unverified }
        foreach ($address in $allowedAddresses) {
            if ([string]$address -notin @('127.0.0.1', '::1')) { throw $unverified }
        }
        $expectedHash = if ($ExpectedBinaryHash) { $ExpectedBinaryHash } else { [string](Get-HorizonCameraFileHash -Path $ExpectedExecutable) }
        $existingHash = [string](Get-HorizonCameraFileHash -Path $executable)
        $knownPrevious = @('071AB382ED4ED5E8ED5902BBE5B595A46879D130978193F0337F9F84623A234E', '40AFE37D9BCCCDBA4B6DD1220E658F5CF7D2B57955FD4A98725F7F6065F7F9BE', 'D19D3011ACB578AA8B28D2E47F6163E7201827B3272321E1F318C3D96D73128A')
        if ([String]::IsNullOrWhiteSpace($expectedHash) -or ($expectedHash -ine $existingHash -and $existingHash -notin $knownPrevious)) { throw $unverified }
    } catch { throw $unverified }

    $ready = $false
    try {
        $status = Get-HorizonCameraJson -Path $statusPath
        $ageSeconds = ([DateTime]::UtcNow - [IO.File]::GetLastWriteTimeUtc($statusPath)).TotalSeconds
        $ports = @($records | ForEach-Object { [int]$_.Port } | Select-Object -Unique)
        $ready = $ports.Count -eq 2 -and $ageSeconds -ge -2 -and $ageSeconds -le 5 -and
            [int]$status.process_id -eq $owners[0] -and $status.running -eq $true -and $status.device_attached -eq $true -and
            [string]$status.camera.serial -ceq [string]$configuration.camera.serial -and
            [int]$status.camera.width -eq [int]$configuration.camera.width -and [int]$status.camera.height -eq [int]$configuration.camera.height -and
            [int]$status.camera.fps -eq [int]$configuration.camera.fps -and [bool]$status.camera.uncompressed -eq [bool]$configuration.camera.uncompressed -and [bool]$status.camera.h264_probe -eq [bool]$configuration.camera.h264_probe
    } catch { $ready = $false }
    # Reject PID reuse or process exit while the files were being checked.
    $confirmed = Get-HorizonCameraProcess -ProcessId $owners[0]
    if ([int]$confirmed.Id -ne $owners[0] -or [string]$confirmed.StartTimeUtcTicks -ne [string]$process.StartTimeUtcTicks -or
        (Get-HorizonCameraCanonicalPath -Path ([string]$confirmed.Path)) -ine $executable) { throw $unverified }
    [pscustomobject]@{
        Root = $root
        Executable = $executable
        ConfigPath = $configPath
        KeyPath = $keyPath
        StatePath = $statePath
        StatusPath = $statusPath
        ProcessId = $owners[0]
        StartTimeUtcTicks = [string]$process.StartTimeUtcTicks
        Ready = [bool]$ready
        MatchesBinary = ($expectedHash -ieq $existingHash)
        CameraName = [string]$configuration.camera.name
        CameraSerial = [string]$configuration.camera.serial
        Width = [int]$configuration.camera.width
        Height = [int]$configuration.camera.height
        FPS = [int]$configuration.camera.fps
        Uncompressed = [bool]$configuration.camera.uncompressed
        H264Probe = [bool]$configuration.camera.h264_probe
    }
}

function Stop-HorizonCameraBackend {
    param([Parameter(Mandatory = $true)]$Backend, [Parameter(Mandatory = $true)][string]$ExpectedExecutable)
    # The caller holds the bootstrap mutex. Stale state never authorizes detach.
    $current = Resolve-HorizonCameraBackend -ExpectedExecutable $ExpectedExecutable
    if (-not $current -or [int]$current.ProcessId -ne [int]$Backend.ProcessId -or
        [string]$current.StartTimeUtcTicks -ne [string]$Backend.StartTimeUtcTicks -or
        [string]$current.Executable -ine [string]$Backend.Executable) {
        throw 'The camera endpoint changed before it could be stopped. Existing devices were left unchanged.'
    }
    $attached = $false
    try {
        $status = Get-HorizonCameraJson -Path $current.StatusPath
        $attached = [int]$status.process_id -eq [int]$current.ProcessId -and
            $status.device_attached -eq $true -and [string]$status.camera.serial -ceq [string]$current.CameraSerial -and
            [IO.File]::GetLastWriteTimeUtc($current.StatusPath).Ticks -ge [long]$current.StartTimeUtcTicks
    } catch { }
    $process = Get-Process -Id ([int]$current.ProcessId) -ErrorAction Stop
    $null = $process.Handle
    if ($process.StartTime.ToUniversalTime().Ticks.ToString() -ne [string]$current.StartTimeUtcTicks -or
        [IO.Path]::GetFullPath($process.Path) -ine [string]$current.Executable) { throw 'The camera process identity changed. No process was stopped.' }
    $process.Refresh()
    if (-not $process.HasExited) {
        Stop-Process -InputObject $process -Force -ErrorAction Stop
        if (-not $process.WaitForExit(10000)) { throw 'The verified camera process did not stop.' }
    }
    if (@(Get-HorizonCameraListeners).Count -ne 0) { throw 'The camera ports now belong to another process. Its device was left unchanged.' }
    if ($attached) {
        & $current.Executable detach-local '127.0.0.1:13260' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The verified camera stopped, but its previously attached endpoint could not be released.' }
    }
    # Driver detach completion does not prove that its old PnP device is gone.
    # Keep the identity intact and the caller busy until this exact root is absent.
    Wait-HorizonCameraRemoval -Serial $current.CameraSerial
}
