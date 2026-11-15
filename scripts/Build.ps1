[CmdletBinding()]
param([switch]$SkipTests, [switch]$NoPublish)

$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Build-Inputs.ps1')
if (-not $SkipTests) {
    & (Join-Path $projectDirectory 'tests\ScriptEncoding.Tests.ps1')
    $verifiedInputs = Get-HorizonBuildInputFingerprint -ProjectDirectory $projectDirectory
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows x64 .NET Framework C# compiler is missing.' }
$buildDirectory = Join-Path $projectDirectory 'build'
$distributionDirectory = Join-Path $projectDirectory 'dist'
[IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
[IO.Directory]::CreateDirectory($distributionDirectory) | Out-Null
$references = @('/nologo', '/langversion:5', '/codepage:65001', '/platform:x64', '/optimize+', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.IO.Compression.dll', '/r:System.Web.Extensions.dll')
$audioSources = @('AudioModemCatalog.cs','AudioPacketWire.cs','NativeAudioCodec.cs','AudioTransport.cs','AudioDiagnosticProbe.cs','AudioTransferStatus.cs')
$common = @('AssemblyInfo.cs', 'Program.cs', 'ThirdPartyMaterials.cs', 'UiText.cs', 'DiagnosticLog.cs', 'DiagnosticSettings.cs', 'UiResponsivenessMonitor.cs', 'HorizonCameraDiagnostics.cs', 'VideoDecoderDiagnostics.cs', 'ClipboardPayload.cs', 'MemoryFileClipboard.cs', 'CameraNames.cs', 'VisualProtocol.cs', 'ColorVideoProtocol.cs', 'DenseVideoProtocol.cs', 'Mp4ModemProtocol.cs', 'Mp4ModemReedSolomon.cs', 'DeferredPaste.cs', 'AudioModem.cs', 'WindowsAudio.cs', 'BridgeControl.cs', 'DeliveryReceipt.cs', 'FileBenchmarkProtocol.cs', 'CompactFileBenchmarkReport.cs', 'BenchmarkData.cs') + $audioSources
$clientSources = $common + @('ClientPaths.cs', 'ReceiverContext.cs', 'ReceiverContext.AutoCamera.cs', 'CameraCapture.cs', 'DirectShowCaptureDiagnostics.cs', 'CaptureEnvironmentDiagnostics.cs', 'MediaFoundationCapture.cs', 'CaptureRetryPolicy.cs', 'AudioClipboardSender.cs', 'ParallelAudioWave.cs', 'ClientAudioBridge.cs', 'AudioOutputSelection.cs')
$serverSources = $common + @('ServerPaths.cs', 'ServerForm.cs', 'ServerForm.Layout.cs', 'ServerForm.ColorModes.cs', 'ServerForm.AudioSelection.cs', 'ServerForm.Language.cs', 'ServerForm.Benchmark.cs', 'ServerForm.Diagnostics.cs', 'ServerForm.Transfers.cs', 'ServerForm.Tray.cs', 'ServerForm.AudioProbe.cs', 'ServerForm.Access.cs', 'CameraBackendTraceReader.cs', 'ForwardDeliveryState.cs', 'TransferSounds.cs', 'FileBenchmarkRunner.cs', 'ServerRuntime.cs', 'DriverInstaller.cs', 'CameraDriverSafety.cs', 'CameraDriverRepair.cs', 'UvcCamera.cs', 'ServerAudioBridge.cs')
$icon = Join-Path $projectDirectory 'assets\HorizonClient.ico'
$iconArguments = @(('/win32icon:' + $icon), ('/resource:' + $icon + ',HorizonClient.Tray.ico'), ('/resource:' + (Join-Path $projectDirectory 'third_party\mp4modem\upstream\LICENSE') + ',HorizonClient.MP4Modem.LICENSE'), ('/resource:' + (Join-Path $projectDirectory 'third_party\mp4modem\README.md') + ',HorizonClient.MP4Modem.NOTICE'))
foreach ($audioFile in @('horizon-quiet.dll','horizon-aicodix.dll','quiet-source.zip','quiet-relink.zip')) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectDirectory ('runtime\audio\'+$audioFile)))) { throw ('Missing native audio runtime: '+$audioFile+'. Run scripts\Build-AudioNative.ps1.') }
}
& (Join-Path $PSScriptRoot 'Build-AudioNotices.ps1')
$iconArguments += @(
    ('/resource:'+(Join-Path $projectDirectory 'runtime\audio\horizon-quiet.dll')+',HorizonClient.Audio.quiet.dll'),
    ('/resource:'+(Join-Path $projectDirectory 'runtime\audio\horizon-aicodix.dll')+',HorizonClient.Audio.aicodix.dll'),
    ('/resource:'+(Join-Path $projectDirectory 'runtime\audio\quiet-source.zip')+',HorizonClient.Audio.quiet-source.zip'),
    ('/resource:'+(Join-Path $projectDirectory 'runtime\audio\quiet-relink.zip')+',HorizonClient.Audio.quiet-relink.zip'),
    ('/resource:'+(Join-Path $buildDirectory 'Audio-Licenses.zip')+',HorizonClient.Audio.Audio-Licenses.zip')
)

# Only explicit redistributable inputs: private keys, configurations and logs stay local.
$licenseZip = Join-Path $buildDirectory 'ThirdPartyNotices.zip'
Compress-Archive -Path (Join-Path $projectDirectory 'runtime\licenses'), (Join-Path $projectDirectory 'runtime\USBBridge-LICENSE.txt'), (Join-Path $projectDirectory 'runtime\THIRD-PARTY.txt'), (Join-Path $projectDirectory 'runtime\PROVENANCE.txt'), (Join-Path $projectDirectory 'defaults\CameraNameSources.txt'), (Join-Path $projectDirectory 'third_party\mp4modem') -DestinationPath $licenseZip -Force
$resourceMap = @{
    'runtime\usbbridge-camera.next.exe' = 'HorizonServer.runtime.usbbridge-camera.exe'
    'scripts\Setup-Camera.ps1' = 'HorizonServer.scripts.Setup-Camera.ps1'
    'scripts\Stop-Camera.ps1' = 'HorizonServer.scripts.Stop-Camera.ps1'
    'scripts\Camera-Runtime.ps1' = 'HorizonServer.scripts.Camera-Runtime.ps1'
    'scripts\Repair-CameraDriver.ps1' = 'HorizonServer.scripts.Repair-CameraDriver.ps1'
    'defaults\ServerDefaults.json' = 'HorizonServer.defaults.ServerDefaults.json'
    'runtime\drivers\USBip-0.9.8.1-x64.exe' = 'HorizonServer.DriverInstaller.exe'
    'build\ThirdPartyNotices.zip' = 'HorizonServer.ThirdPartyNotices.zip'
}
$resources = @($resourceMap.GetEnumerator() | ForEach-Object { '/resource:' + (Join-Path $projectDirectory $_.Key) + ',' + $_.Value })
foreach ($target in @(
    @{ Name = 'Client'; Manifest = 'app.manifest'; Sources = $clientSources; Extra = @() },
    @{ Name = 'Server'; Manifest = 'server.manifest'; Sources = $serverSources; Extra = @('/define:HORIZON_SERVER') + $resources }
)) {
    $sources = @($target.Sources | ForEach-Object { Join-Path $projectDirectory ('src\' + $_) })
    $application = Join-Path $buildDirectory ($target.Name + '.exe')
    & $compiler @references @iconArguments /target:winexe ('/win32manifest:' + (Join-Path $projectDirectory ('src\' + $target.Manifest))) ('/out:' + $application) @($target.Extra) @sources
    if ($LASTEXITCODE -ne 0) { throw ($target.Name + ' compilation failed.') }
}

if (-not $SkipTests) {
    $checks = @(
        @{ Name = 'UiTextTests'; Sources = @('UiText.cs'); Test = 'UiTextTests.cs'; Extra = @() },
        @{ Name = 'ServerLanguageTests'; Sources = $serverSources; Test = 'ServerLanguageTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerLanguageTests') },
        @{ Name = 'ServerTopRowTests'; Sources = $serverSources; Test = 'ServerTopRowTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerTopRowTests') },
        @{ Name = 'ServerStorageTests'; Sources = $serverSources; Test = 'ServerStorageTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerStorageTests') },
        @{ Name = 'ServerColorModesTests'; Sources = $serverSources; Test = 'ServerColorModesTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerColorModesTests') },
        @{ Name = 'ServerAudioSelectionTests'; Sources = $serverSources; Test = 'ServerAudioSelectionTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerAudioSelectionTests') },
        @{ Name = 'AudioSelectionPersistenceTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'AudioSelectionPersistenceTests.cs'; Extra = @('/main:AudioSelectionPersistenceTests') },
        @{ Name = 'PackagedIconTests'; Sources = @(); Test = 'PackagedIconTests.cs'; Extra = @() },
        @{ Name = 'PackagedDriverTests'; Sources = @(); Test = 'PackagedDriverTests.cs'; Extra = @() },
        @{ Name = 'ServerDynamicLanguageTests'; Sources = $serverSources; Test = 'ServerDynamicLanguageTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerDynamicLanguageTests') },
        @{ Name = 'ReceiverLanguageTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'ReceiverLanguageTests.cs'; Extra = @('/main:ReceiverLanguageTests') },
        @{ Name = 'ReceiverMemoryFileTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'ReceiverMemoryFileTests.cs'; Extra = @('/main:ReceiverMemoryFileTests') },
        @{ Name = 'MemoryFileClipboardTests'; Sources = @('ClipboardPayload.cs','MemoryFileClipboard.cs'); Test = 'MemoryFileClipboardTests.cs'; Extra = @('/main:MemoryFileClipboardTests') },
        @{ Name = 'ReceiverAutoCameraTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'ReceiverAutoCameraTests.cs'; Extra = @('/main:ReceiverAutoCameraTests') },
        @{ Name = 'DiagnosticLogTests'; Sources = @('DiagnosticLog.cs'); Test = 'DiagnosticLogTests.cs'; Extra = @() },
        @{ Name = 'DiagnosticSettingsTests'; Sources = @('DiagnosticLog.cs','DiagnosticSettings.cs'); Test = 'DiagnosticSettingsTests.cs'; Extra = @() },
        @{ Name = 'VideoDiagnosticTests'; Sources = @('VisualProtocol.cs'); Test = 'VideoDiagnosticTests.cs'; Extra = @() },
        @{ Name = 'AudioPacketWireTests'; Sources = @('VisualProtocol.cs','AudioModem.cs') + $audioSources; Test = 'AudioPacketWireTests.cs'; Extra = @() },
        @{ Name = 'AudioDiagnosticProbeTests'; Sources = @('AudioDiagnosticProbe.cs','AudioModemCatalog.cs'); Test = 'AudioDiagnosticProbeTests.cs'; Extra = @() },
        @{ Name = 'NativeAudioIntegrationTests'; Sources = @('VisualProtocol.cs','AudioModem.cs') + $audioSources; Test = 'NativeAudioIntegrationTests.cs'; Extra = @() },
        @{ Name = 'NativeAudioRecoveryTests'; Sources = @('VisualProtocol.cs','AudioModem.cs') + $audioSources; Test = 'NativeAudioRecoveryTests.cs'; Extra = @() },
        @{ Name = 'NativeAudioFailureTests'; Sources = @('VisualProtocol.cs','AudioModem.cs') + $audioSources; Test = 'NativeAudioFailureTests.cs'; Extra = @() },
        @{ Name = 'PackagedAudioTests'; Sources = @(); Test = 'PackagedAudioTests.cs'; Extra = @() },
        @{ Name = 'ThirdPartyMaterialsTests'; Sources = @('ThirdPartyMaterials.cs'); Test = 'ThirdPartyMaterialsTests.cs'; Extra = @() },
        @{ Name = 'NativeAudioStorageTests'; Sources = @('NativeAudioCodec.cs','AudioModemCatalog.cs','AudioPacketWire.cs','AudioModem.cs','VisualProtocol.cs'); Test = 'NativeAudioStorageTests.cs'; Extra = @('/main:NativeAudioStorageTests') },
        @{ Name = 'NativeAudioServerStorageTests'; Sources = @('NativeAudioCodec.cs','AudioModemCatalog.cs','AudioPacketWire.cs','AudioModem.cs','VisualProtocol.cs'); Test = 'NativeAudioStorageTests.cs'; Extra = @('/define:HORIZON_SERVER','/main:NativeAudioStorageTests') },
        @{ Name = 'DeliveryReceiptTests'; Sources = @('VisualProtocol.cs','DeliveryReceipt.cs'); Test = 'DeliveryReceiptTests.cs'; Extra = @() },
        @{ Name = 'NewFastReceiptTests'; Sources = @('VisualProtocol.cs','AudioModem.cs','DeliveryReceipt.cs','ForwardDeliveryState.cs'); Test = 'NewFastReceiptTests.cs'; Extra = @() },
        @{ Name = 'FastReceiptBurstTests'; Sources = @('VisualProtocol.cs','AudioModem.cs','DeliveryReceipt.cs','ForwardDeliveryState.cs') + $audioSources; Test = 'FastReceiptBurstTests.cs'; Extra = @() },
        @{ Name = 'ForwardDeliveryTests'; Sources = @('VisualProtocol.cs','BridgeControl.cs','DeliveryReceipt.cs','ForwardDeliveryState.cs'); Test = 'ForwardDeliveryTests.cs'; Extra = @() },
        @{ Name = 'ForwardReceiptAudioTests'; Sources = @('ServerAudioBridge.cs','AudioModem.cs','WindowsAudio.cs','VisualProtocol.cs','ClipboardPayload.cs','BridgeControl.cs','DeferredPaste.cs','DeliveryReceipt.cs','ForwardDeliveryState.cs'); Test = 'ForwardReceiptAudioTests.cs'; Extra = @() },
        @{ Name = 'DeliveryReceiptRecoveryTests'; Sources = @($clientSources | Where-Object { $_ -ne 'Program.cs' }) + @('ServerAudioBridge.cs', 'ForwardDeliveryState.cs'); Test = 'DeliveryReceiptRecoveryTests.cs'; Extra = @('/main:DeliveryReceiptRecoveryTests') },
        @{ Name = 'TransferSoundsTests'; Sources = @('TransferSounds.cs'); Test = 'TransferSoundsTests.cs'; Extra = @() },
        @{ Name = 'ProtocolTests'; Sources = @('VisualProtocol.cs'); Test = 'ProtocolTests.cs'; Extra = @() },
        @{ Name = 'ColorVideoProtocolTests'; Sources = @('VisualProtocol.cs'); Test = 'ColorVideoProtocolTests.cs'; Extra = @() },
        @{ Name = 'ColorModeIntegrationTests'; Sources = @('VisualProtocol.cs','BridgeControl.cs','FileBenchmarkProtocol.cs','CompactFileBenchmarkReport.cs','BenchmarkData.cs','AudioModem.cs'); Test = 'ColorModeIntegrationTests.cs'; Extra = @() },
        @{ Name = 'DenseVideoProtocolTests'; Sources = @('VisualProtocol.cs'); Test = 'DenseVideoProtocolTests.cs'; Extra = @() },
        @{ Name = 'PreferredProfileDecodeTests'; Sources = @('VisualProtocol.cs'); Test = 'PreferredProfileDecodeTests.cs'; Extra = @() },
        @{ Name = 'PreferredProfileFallbackTests'; Sources = @('VisualProtocol.cs'); Test = 'PreferredProfileFallbackTests.cs'; Extra = @() },
        @{ Name = 'CameraFormatControlTests'; Sources = @('VisualProtocol.cs', 'BridgeControl.cs'); Test = 'CameraFormatControlTests.cs'; Extra = @() },
        @{ Name = 'Mp4ModemTests'; Sources = @('VisualProtocol.cs'); Test = 'Mp4ModemTests.cs'; Extra = @() },
        @{ Name = 'PayloadTests'; Sources = @('VisualProtocol.cs', 'ClipboardPayload.cs'); Test = 'PayloadTests.cs'; Extra = @() },
        @{ Name = 'ClipboardCompressionTests'; Sources = @('ClipboardPayload.cs'); Test = 'ClipboardCompressionTests.cs'; Extra = @() },
        @{ Name = 'CompressionTransportTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'CompressionTransportTests.cs'; Extra = @('/main:CompressionTransportTests') },
        @{ Name = 'CaptureTests'; Sources = @('AssemblyInfo.cs', 'CameraCapture.cs'); Test = 'CaptureTests.cs'; Extra = @('/define:CAPTURE_TEST_MAIN') },
        @{ Name = 'CaptureRetryPolicyTests'; Sources = @('CameraCapture.cs', 'CaptureRetryPolicy.cs'); Test = 'CaptureRetryPolicyTests.cs'; Extra = @() },
        @{ Name = 'DirectShowCaptureDiagnosticsTests'; Sources = @('CameraCapture.cs'); Test = 'DirectShowCaptureDiagnosticsTests.cs'; Extra = @() },
        @{ Name = 'CaptureEnvironmentDiagnosticsTests'; Sources = @('CaptureEnvironmentDiagnostics.cs'); Test = 'CaptureEnvironmentDiagnosticsTests.cs'; Extra = @() },
        @{ Name = 'MediaFoundationCaptureTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'MediaFoundationCaptureTests.cs'; Extra = @('/main:MediaFoundationCaptureTests') },
        @{ Name = 'H264SeiTests'; Sources = @('CameraCapture.cs', 'H264Sei.cs', 'H264SeiCapture.cs'); Test = 'H264SeiTests.cs'; Extra = @('/define:H264_CAPTURE_TESTS') },
        @{ Name = 'UvcCameraProtocolChecks'; Sources = @('UvcCamera.cs'); Test = 'UvcCameraProtocolChecks.cs'; Extra = @() },
        @{ Name = 'CameraBackendTraceReaderTests'; Sources = @('CameraBackendTraceReader.cs'); Test = 'CameraBackendTraceReaderTests.cs'; Extra = @() },
        @{ Name = 'FilePublishPreparationTests'; Sources = @('ClipboardPayload.cs'); Test = 'FilePublishPreparationTests.cs'; Extra = @('/define:FILE_PREPARATION_TEST_MAIN') },
        @{ Name = 'DeferredPasteTests'; Sources = @('DeferredPaste.cs'); Test = 'DeferredPasteTests.cs'; Extra = @() },
        @{ Name = 'ReceiverContextTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'ReceiverContextTests.cs'; Extra = @('/main:ReceiverContextTests') },
        @{ Name = 'PortableClientTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'PortableClientTests.cs'; Extra = @('/main:PortableClientTests') },
        @{ Name = 'BenchmarkDataTests'; Sources = @('BenchmarkData.cs'); Test = 'BenchmarkDataTests.cs'; Extra = @() },
        @{ Name = 'FileBenchmarkTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'FileBenchmarkTests.cs'; Extra = @('/main:FileBenchmarkTests') },
        @{ Name = 'FileBenchmarkRunnerTests'; Sources = @('VisualProtocol.cs', 'BridgeControl.cs', 'FileBenchmarkProtocol.cs', 'BenchmarkData.cs', 'FileBenchmarkRunner.cs'); Test = 'FileBenchmarkRunnerTests.cs'; Extra = @() },
        @{ Name = 'FileBenchmarkRetransmissionTests'; Sources = @('VisualProtocol.cs', 'BridgeControl.cs', 'FileBenchmarkProtocol.cs', 'BenchmarkData.cs', 'FileBenchmarkRunner.cs'); Test = 'FileBenchmarkRetransmissionTests.cs'; Extra = @() },
        @{ Name = 'NackBenchmarkTests'; Sources = @('VisualProtocol.cs', 'BridgeControl.cs', 'FileBenchmarkProtocol.cs', 'BenchmarkData.cs', 'FileBenchmarkRunner.cs'); Test = 'NackBenchmarkTests.cs'; Extra = @() },
        @{ Name = 'ReceiverNackEligibilityTests'; Sources = @('VisualProtocol.cs', 'BridgeControl.cs', 'FileBenchmarkProtocol.cs', 'BenchmarkData.cs'); Test = 'ReceiverNackEligibilityTests.cs'; Extra = @() },
        @{ Name = 'CompactFileBenchmarkReportTests'; Sources = @('VisualProtocol.cs', 'BridgeControl.cs', 'FileBenchmarkProtocol.cs', 'BenchmarkData.cs', 'CompactFileBenchmarkReport.cs', 'AudioModem.cs'); Test = 'CompactFileBenchmarkReportTests.cs'; Extra = @() },
        @{ Name = 'ClientAudioBridgeMissingTests'; Sources = @('WindowsAudio.cs', 'VisualProtocol.cs', 'ClipboardPayload.cs', 'AudioModem.cs', 'BridgeControl.cs', 'AudioClipboardSender.cs', 'ClientAudioBridge.cs', 'AudioOutputSelection.cs'); Test = 'ClientAudioBridgeMissingTests.cs'; Extra = @() },
        @{ Name = 'UiChecks'; Sources = $serverSources; Test = 'UiChecks.cs'; Extra = @('/define:HORIZON_SERVER', '/main:UiChecks') },
        @{ Name = 'ServerCameraFormatGateTests'; Sources = $serverSources; Test = 'ServerCameraFormatGateTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerCameraFormatGateTests') },
        @{ Name = 'ServerCameraAccessTests'; Sources = $serverSources; Test = 'ServerCameraAccessTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerCameraAccessTests') },
        @{ Name = 'ServerIncomingTransferTests'; Sources = $serverSources; Test = 'ServerIncomingTransferTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerIncomingTransferTests') },
        @{ Name = 'ServerTrayTests'; Sources = $serverSources; Test = 'ServerTrayTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerTrayTests') },
        @{ Name = 'ServerAudioProbeTests'; Sources = $serverSources; Test = 'ServerAudioProbeTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerAudioProbeTests') },
        @{ Name = 'ParallelAudioWaveTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'ParallelAudioWaveTests.cs'; Extra = @('/main:ParallelAudioWaveTests') },
        @{ Name = 'AudioMemoryTests'; Sources = $clientSources | Where-Object { $_ -ne 'Program.cs' }; Test = 'AudioMemoryTests.cs'; Extra = @('/main:AudioMemoryTests') },
        @{ Name = 'ServerBenchmarkSendLoopTests'; Sources = $serverSources; Test = 'ServerBenchmarkSendLoopTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerBenchmarkSendLoopTests') },
        @{ Name = 'ServerClipboardIntentTests'; Sources = $serverSources; Test = 'ServerClipboardIntentTests.cs'; Extra = @('/define:HORIZON_SERVER', '/main:ServerClipboardIntentTests') },
        @{ Name = 'DriverInstallerTests'; Sources = @('DriverInstaller.cs','ServerPaths.cs'); Test = 'DriverInstallerTests.cs'; Extra = @() },
        @{ Name = 'CameraDriverSafetyTests'; Sources = @('CameraDriverSafety.cs'); Test = 'CameraDriverSafetyTests.cs'; Extra = @() },
        @{ Name = 'CameraDriverRepairTests'; Sources = $serverSources; Test = 'CameraDriverRepairTests.cs'; Extra = @('/define:HORIZON_SERVER','/main:CameraDriverRepairTests') },
        @{ Name = 'HorizonCameraDiagnosticsTests'; Sources = @('HorizonCameraDiagnostics.cs'); Test = 'HorizonCameraDiagnosticsTests.cs'; Extra = @() },
        @{ Name = 'AudioModemTests'; Sources = @('VisualProtocol.cs', 'AudioModem.cs'); Test = 'AudioModemTests.cs'; Extra = @() },
        @{ Name = 'WindowsAudioTests'; Sources = @('WindowsAudio.cs'); Test = 'WindowsAudioTests.cs'; Extra = @() },
        @{ Name = 'BridgeControlTests'; Sources = @('VisualProtocol.cs', 'BridgeControl.cs'); Test = 'BridgeControlTests.cs'; Extra = @() }
        @{ Name = 'ClientAudioBridgeTests'; Sources = @('WindowsAudio.cs', 'VisualProtocol.cs', 'ClipboardPayload.cs', 'AudioModem.cs', 'BridgeControl.cs', 'AudioClipboardSender.cs', 'ClientAudioBridge.cs', 'AudioOutputSelection.cs'); Test = 'ClientAudioBridgeTests.cs'; Extra = @() }
        @{ Name = 'AudioClipboardSenderTests'; Sources = @('WindowsAudio.cs', 'VisualProtocol.cs', 'ClipboardPayload.cs', 'AudioModem.cs', 'BridgeControl.cs', 'AudioClipboardSender.cs', 'AudioOutputSelection.cs'); Test = 'AudioClipboardSenderTests.cs'; Extra = @() }
        @{ Name = 'AudioOutputProbeTests'; Sources = @('WindowsAudio.cs', 'VisualProtocol.cs', 'ClipboardPayload.cs', 'AudioModem.cs', 'BridgeControl.cs', 'AudioClipboardSender.cs', 'AudioOutputSelection.cs'); Test = 'AudioOutputProbeTests.cs'; Extra = @() }
        @{ Name = 'ServerAudioBridgeTests'; Sources = @('ServerAudioBridge.cs', 'AudioModem.cs', 'WindowsAudio.cs', 'VisualProtocol.cs', 'ClipboardPayload.cs', 'BridgeControl.cs', 'DeferredPaste.cs'); Test = 'ServerAudioBridgeTests.cs'; Extra = @() }
        @{ Name = 'AudioOutputSelectionTests'; Sources = @('WindowsAudio.cs', 'AudioOutputSelection.cs'); Test = 'AudioOutputSelectionTests.cs'; Extra = @() }
        @{ Name = 'CameraNamesTests'; Sources = @('CameraNames.cs'); Test = 'CameraNamesTests.cs'; Extra = @() }
    )
    foreach ($check in $checks) {
        if ($check.Sources -notcontains 'UiText.cs') { $check.Sources += 'UiText.cs' }
        if (($check.Sources -contains 'ServerAudioBridge.cs' -or $check.Sources -contains 'ServerRuntime.cs') -and $check.Sources -notcontains 'ServerPaths.cs') { $check.Sources += 'ServerPaths.cs' }
        if ($check.Sources -notcontains 'DiagnosticLog.cs') { $check.Sources += 'DiagnosticLog.cs' }
        if ($check.Sources -contains 'ClipboardPayload.cs' -and $check.Sources -notcontains 'MemoryFileClipboard.cs') { $check.Sources += 'MemoryFileClipboard.cs' }
        if ($check.Sources -contains 'CameraCapture.cs' -and $check.Sources -notcontains 'HorizonCameraDiagnostics.cs') { $check.Sources += 'HorizonCameraDiagnostics.cs' }
        if ($check.Sources -contains 'CameraCapture.cs') {
            foreach ($captureSource in @('DirectShowCaptureDiagnostics.cs','CaptureEnvironmentDiagnostics.cs')) { if ($check.Sources -notcontains $captureSource) { $check.Sources += $captureSource } }
        }
        if ($check.Sources -contains 'AudioClipboardSender.cs' -or $check.Sources -contains 'ServerAudioBridge.cs') {
            foreach ($audioSource in $audioSources) { if ($check.Sources -notcontains $audioSource) { $check.Sources += $audioSource } }
        }
        if ($check.Sources -contains 'AudioClipboardSender.cs' -and $check.Sources -notcontains 'ParallelAudioWave.cs') { $check.Sources += 'ParallelAudioWave.cs' }
        if ($check.Sources -contains 'VisualProtocol.cs' -and $check.Sources -notcontains 'DenseVideoProtocol.cs') { $check.Sources += 'DenseVideoProtocol.cs' }
        if ($check.Sources -contains 'VisualProtocol.cs' -and $check.Sources -notcontains 'ColorVideoProtocol.cs') { $check.Sources += 'ColorVideoProtocol.cs' }
        if ($check.Sources -contains 'VisualProtocol.cs' -and $check.Sources -notcontains 'VideoDecoderDiagnostics.cs') { $check.Sources += 'VideoDecoderDiagnostics.cs' }
        if ($check.Sources -contains 'VisualProtocol.cs' -and $check.Sources -notcontains 'Mp4ModemProtocol.cs') { $check.Sources += @('Mp4ModemProtocol.cs', 'Mp4ModemReedSolomon.cs') }
        if ($check.Sources -contains 'BridgeControl.cs' -and $check.Sources -notcontains 'FileBenchmarkProtocol.cs') { $check.Sources += @('FileBenchmarkProtocol.cs', 'BenchmarkData.cs') }
        if ($check.Sources -contains 'BridgeControl.cs' -and $check.Sources -notcontains 'DeliveryReceipt.cs') { $check.Sources += 'DeliveryReceipt.cs' }
        if (($check.Sources -contains 'ClientAudioBridge.cs' -or $check.Sources -contains 'ServerAudioBridge.cs' -or $check.Sources -contains 'AudioClipboardSender.cs') -and $check.Sources -notcontains 'CompactFileBenchmarkReport.cs') { $check.Sources += 'CompactFileBenchmarkReport.cs' }
        if ($check.Sources -contains 'NativeAudioCodec.cs' -and $check.Sources -notcontains 'ClientPaths.cs') { $check.Sources += 'ClientPaths.cs' }
        $testPath = Join-Path $buildDirectory ($check.Name + '.exe')
        $testSources = @($check.Sources | ForEach-Object { Join-Path $projectDirectory ('src\' + $_) }) + @(Join-Path $projectDirectory ('tests\' + $check.Test))
        $testResources = if ($check.Name -eq 'PackagedAudioTests') { @() } else { $iconArguments }
        & $compiler @references @testResources /target:exe ('/out:' + $testPath) @($check.Extra) @testSources
        if ($LASTEXITCODE -ne 0) { throw ($check.Name + ' compilation failed.') }
        & $testPath
        if ($LASTEXITCODE -ne 0) { throw ($check.Name + ' failed.') }
    }
    & (Join-Path $projectDirectory 'tests\BuildInputs.Tests.ps1')
    & (Join-Path $projectDirectory 'tests\CameraRuntime.Tests.ps1')
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectDirectory 'tests\CameraMetadata.Tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Camera metadata fixtures failed.' }
    & (Join-Path $projectDirectory 'tests\ServerRuntime.Tests.ps1')
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectDirectory 'tests\CameraDriverRepair.Tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Camera driver repair fixtures failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectDirectory 'tests\CameraLifecycle.Tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Camera lifecycle fixtures failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectDirectory 'tests\ElevatedOutputPaths.Tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Elevated receiver output fixtures failed.' }
    Write-Output 'Verified tests: all configured suites passed.'
}
$smokeSources = @('src\AssemblyInfo.cs', 'src\DiagnosticLog.cs', 'src\HorizonCameraDiagnostics.cs', 'src\DirectShowCaptureDiagnostics.cs', 'src\CaptureEnvironmentDiagnostics.cs', 'src\VideoDecoderDiagnostics.cs', 'src\VisualProtocol.cs', 'src\ColorVideoProtocol.cs', 'src\DenseVideoProtocol.cs', 'src\Mp4ModemProtocol.cs', 'src\Mp4ModemReedSolomon.cs', 'src\UvcCamera.cs', 'src\CameraCapture.cs', 'tests\CameraSmoke.cs') | ForEach-Object { Join-Path $projectDirectory $_ }
& $compiler @references /target:exe ('/out:' + (Join-Path $buildDirectory 'CameraSmoke.exe')) @smokeSources
if ($LASTEXITCODE -ne 0) { throw 'Camera smoke check compilation failed.' }
$audioSmokeSources = @('src\DiagnosticLog.cs', 'src\VideoDecoderDiagnostics.cs', 'src\WindowsAudio.cs', 'src\AudioModem.cs', 'src\VisualProtocol.cs', 'src\ColorVideoProtocol.cs', 'src\DenseVideoProtocol.cs', 'src\Mp4ModemProtocol.cs', 'src\Mp4ModemReedSolomon.cs', 'tests\AudioSmoke.cs') | ForEach-Object { Join-Path $projectDirectory $_ }
& $compiler @references /target:exe ('/out:' + (Join-Path $buildDirectory 'AudioSmoke.exe')) @audioSmokeSources
if ($LASTEXITCODE -ne 0) { throw 'Audio smoke check compilation failed.' }
if (-not $SkipTests) {
    $finalInputs = Get-HorizonBuildInputFingerprint -ProjectDirectory $projectDirectory
    if ($finalInputs.SHA256 -ne $verifiedInputs.SHA256) { throw 'Build inputs changed during validation; rebuild the final sources.' }
    Write-Output ('Verified build input SHA256: '+$finalInputs.SHA256)
    foreach ($name in @('Server.exe','Client.exe')) {
        Write-Output ('Verified binary SHA256 '+$name+': '+(Get-FileHash -LiteralPath (Join-Path $buildDirectory $name) -Algorithm SHA256).Hash)
    }
}
if ($NoPublish) {
    Write-Output ('Built without publishing: ' + $buildDirectory)
    return
}
foreach ($name in @('Server.exe', 'Client.exe')) {
    Copy-Item -LiteralPath (Join-Path $buildDirectory $name) -Destination (Join-Path $distributionDirectory $name) -Force
    Write-Output ('Built: ' + (Join-Path $distributionDirectory $name))
}
foreach ($name in @('README.md', 'README.ru.md')) {
    Copy-Item -LiteralPath (Join-Path $projectDirectory $name) -Destination (Join-Path $distributionDirectory $name) -Force
}
