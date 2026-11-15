[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureRoot = Join-Path $projectDirectory ('build\camera-lifecycle-tests-' + [Guid]::NewGuid().ToString('N'))
$scripts = Join-Path $fixtureRoot 'scripts'
$data = Join-Path $fixtureRoot 'data'
$received = Join-Path $fixtureRoot 'Received'
foreach ($directory in @($scripts, $data, $received)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
try {
    # A fresh process hosts this stub instead of the native TCP enumerator. There
    # are no listeners or processes in this synthetic saved-identity scenario.
    Add-Type -TypeDefinition @'
namespace HorizonClient { public static class CameraRuntimeNative { public static object[] Listeners() { return new object[0]; } } }
'@
    # Separate from the listener stub: only an injected camera-presence result
    # is queried, so no SetupAPI call can reach the actual machine's devices.
    Add-Type -TypeDefinition @'
namespace HorizonClient {
    public static class CameraRemovalNative {
        public static bool QueryFailure;
        public static int PendingReads, Reads;
        public static string LastSerial;
        public static bool IsPresent(string serial) {
            Reads++; LastSerial = serial;
            if (QueryFailure) throw new System.InvalidOperationException("Synthetic SetupAPI query failure.");
            if (PendingReads > 0) { PendingReads--; return true; }
            return false;
        }
    }
}
'@
    foreach ($name in @('Camera-Runtime.ps1', 'Stop-Camera.ps1')) { Copy-Item -LiteralPath (Join-Path $projectDirectory ('scripts\' + $name)) -Destination $scripts }
    $config = Join-Path $data 'camera.local.json'
    $key = Join-Path $data 'camera.local.key'
    $state = Join-Path $data 'camera-process.local.json'
    $status = Join-Path $data 'camera.local.status.json'
    $receivedFile = Join-Path $received 'keep.txt'
    [IO.File]::WriteAllText($receivedFile, 'synthetic received fixture')
    $identity = [ordered]@{
        enabled = $true; loopback_only = $true; usb_port = 13260; video_port = 13261
        allowed_ips = @('127.0.0.1'); key_file = $key
        camera = @{ name = 'Saved fixture webcam'; serial = ('HorizonClient-' + [Guid]::NewGuid().ToString('N')); width = 1280; height = 720; fps = 30; microphone = $true }
    }
    [IO.File]::WriteAllText($config, ($identity | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllText($key, 'synthetic fixture only')
    [IO.File]::WriteAllText($state, '{"process_id":2147483647,"start_time_utc_ticks":"0"}')
    [IO.File]::WriteAllText($status, '{}')
    [HorizonClient.CameraRemovalNative]::QueryFailure = $true
    $rejected = $false
    try { & (Join-Path $scripts 'Stop-Camera.ps1') -Remove -PassThru | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Removal must reject an unverified PnP state.' }
    foreach ($path in @($config, $key, $state, $status)) { if (-not (Test-Path -LiteralPath $path)) { throw 'An uncertain removal deleted the saved camera identity or key.' } }
    if (Test-Path -LiteralPath (Join-Path $data 'camera.identity.deleted.json')) { throw 'An uncertain removal must not publish a deleted-identity marker.' }
    if ([HorizonClient.CameraRemovalNative]::Reads -ne 1 -or [HorizonClient.CameraRemovalNative]::LastSerial -cne $identity.camera.serial) { throw 'Removal queried another camera identity.' }
    [HorizonClient.CameraRemovalNative]::QueryFailure = $false
    [HorizonClient.CameraRemovalNative]::PendingReads = 2
    [HorizonClient.CameraRemovalNative]::Reads = 0
    $result = & (Join-Path $scripts 'Stop-Camera.ps1') -Remove -PassThru
    if (-not $result.Removed) { throw 'A verified saved identity must be removable without a running receiver.' }
    if ([HorizonClient.CameraRemovalNative]::Reads -ne 3 -or [HorizonClient.CameraRemovalNative]::LastSerial -cne $identity.camera.serial) { throw 'Removal must wait for the exact saved camera root to disappear.' }
    foreach ($path in @($config, $key, $state, $status)) { if (Test-Path -LiteralPath $path) { throw 'Removal left owned camera metadata or key behind.' } }
    if (-not (Test-Path -LiteralPath $receivedFile)) { throw 'Removal deleted received user files.' }
    if (-not (Test-Path -LiteralPath (Join-Path $data 'camera.identity.deleted.json'))) { throw 'Removal did not record that the identity was deleted.' }
    $again = & (Join-Path $scripts 'Stop-Camera.ps1') -Remove -PassThru
    if ($again.Removed) { throw 'Repeated removal must be an idempotent no-op.' }
    $identity.camera.serial = 'Foreign-Camera-1'
    [IO.File]::WriteAllText($config, ($identity | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllText($key, 'synthetic foreign fixture only')
    [HorizonClient.CameraRemovalNative]::Reads = 0
    $rejected = $false
    try { & (Join-Path $scripts 'Stop-Camera.ps1') -Remove -PassThru | Out-Null } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath $key) -or -not (Test-Path -LiteralPath $config)) { throw 'Removal must reject a foreign identity and preserve its files.' }
    if ([HorizonClient.CameraRemovalNative]::Reads -ne 0) { throw 'A foreign camera identity must never reach the device-presence query.' }
    Write-Output 'Camera lifecycle fixture checks passed (exact-root removal wait, query-error identity retention, scoped deletion, received files retained and foreign-identity protection; no hardware queries).'
} finally {
    $canonicalFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $canonicalBuild = [IO.Path]::GetFullPath((Join-Path $projectDirectory 'build')) + [IO.Path]::DirectorySeparatorChar
    if ($canonicalFixture.StartsWith($canonicalBuild, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($canonicalFixture).StartsWith('camera-lifecycle-tests-')) { [IO.Directory]::Delete($canonicalFixture, $true) }
}
