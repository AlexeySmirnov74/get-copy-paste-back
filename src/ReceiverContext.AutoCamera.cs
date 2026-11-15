using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace HorizonClient
{
    internal sealed partial class ReceiverContext
    {
        private bool automaticCameraSelection;
        private long automaticCameraGeneration;
        private const int AutomaticCameraOpenLimit = 5;
        private readonly Dictionary<string, int> automaticCameraAttempts = new Dictionary<string, int>(StringComparer.Ordinal);

        private static string AutomaticCameraCaption
        { get { return UiText.Get("Автоматически выбирать камеру сервера", "Automatically select server camera"); } }

        private bool DeviceMenuSnapshotMatches(List<CameraDevice> devices, AudioEndpoint[] outputs)
        {
            if (HasAudioOutput && !menu.Items.ContainsKey("audio.output")) return false;
            if (devices.Count != lastMenuDevices.Count || outputs.Length != lastMenuOutputs.Length) return false;
            for (int index = 0; index < devices.Count; index++)
            {
                CameraDevice next = devices[index], prior = lastMenuDevices[index];
                if (next == null || prior == null) { if (!Object.ReferenceEquals(next, prior)) return false; }
                else if (!String.Equals(next.Id, prior.Id, StringComparison.Ordinal) || !String.Equals(next.Name, prior.Name, StringComparison.Ordinal)) return false;
            }
            for (int index = 0; index < outputs.Length; index++)
            {
                AudioEndpoint next = outputs[index], prior = lastMenuOutputs[index];
                if (next == null || prior == null) { if (!Object.ReferenceEquals(next, prior)) return false; }
                else if (!String.Equals(next.Id, prior.Id, StringComparison.Ordinal) || !String.Equals(next.Name, prior.Name, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private void RestoreAutomaticCameraSelection()
        {
            // RestoreSelection=false is the injected, explicitly non-restoring
            // path. A production installation with no preference discovers its
            // source immediately, including when a saved moniker has changed.
            automaticCameraSelection = services.RestoreSelection;
            if (!services.RestoreSelection) return;
            try
            {
                string value = services.ReadAutomaticCameraSelection();
                if (value == "0") automaticCameraSelection = false;
                else if (value != "1") DiagnosticLog.Write("receiver.setting.invalid", "setting", "camera.auto", "reason", "expected_0_or_1");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) { LogSettingFailure("read", "camera.auto", error); }
        }

        private void AddAutomaticCameraMenu(ToolStripMenuItem settings)
        {
            var item = new ToolStripMenuItem(AutomaticCameraCaption) {
                Name = "settings.auto_camera", Checked = automaticCameraSelection, AutoToolTip = false
            };
            item.Click += delegate { SetAutomaticCameraSelection(!automaticCameraSelection, true); };
            settings.DropDownItems.Add(item);
        }

        private void SetAutomaticCameraSelection(bool enabled, bool persist)
        {
            if (closing) return;
            bool changed = automaticCameraSelection != enabled;
            automaticCameraSelection = enabled;
            if (changed)
            {
                automaticCameraGeneration++;
                automaticCameraAttempts.Clear();
                if (enabled) foreach (CaptureRetryPolicy policy in captureRetries.Values) policy.ResetDelay();
                ReceiverSession probe = current;
                if (probe != null && (probe.AutomaticProbe || enabled))
                {
                    current = null; captureEpoch++;
                    ResetPublication(probe); StopAudioSession(); QueueDispose(probe);
                }
                if (enabled) { selectedResolution = null; selectedModes = new CameraResolution[0]; ClearSourceFormat(); }
            }
            var settings = menu.Items["settings"] as ToolStripMenuItem;
            var item = settings == null ? null : settings.DropDownItems["settings.auto_camera"] as ToolStripMenuItem;
            if (item != null) item.Checked = enabled;
            if (persist)
            {
                try { services.WriteAutomaticCameraSelection(enabled); }
                catch (Exception error) { LogSettingFailure("write", "camera.auto", error); services.LogFailure(error); }
            }
            DiagnosticLog.Write("receiver.camera.automatic", "enabled", enabled, "generation", automaticCameraGeneration);
            if (enabled && changed) RefreshDevices(null);
        }

        private void StartNextAutomaticCamera(List<CameraDevice> devices)
        {
            if (closing || !automaticCameraSelection || current != null) return;
            long now = services.Milliseconds();
            // A saved exact ID is explicitly authorized, including custom names.
            // Other candidates must match the shared catalog, then prove our wire.
            var ordered = new List<CameraDevice>();
            var listed = new Dictionary<string, CameraDevice>(StringComparer.Ordinal);
            foreach (CameraDevice device in devices)
                if (device != null && !String.IsNullOrEmpty(device.Id) && !listed.ContainsKey(device.Id)) listed.Add(device.Id, device);
            var added = new HashSet<string>(StringComparer.Ordinal);
            AddAutomaticCandidate(selectedCameraId, listed, added, ordered);
            if (services.PreferredCameraIds != null) foreach (string id in services.PreferredCameraIds) AddAutomaticCandidate(id, listed, added, ordered);
            foreach (CameraDevice device in devices) if (device != null) AddAutomaticCandidate(device.Id, listed, added, ordered);
            foreach (CameraDevice device in ordered)
            {
                if (!AutomaticCameraBudgetAvailable(device.Id) || !CapturePolicy(device.Id).CanOpen(now)) continue;
                StartCapture(device, true);
                return;
            }
        }

        private bool AutomaticCameraEligible(CameraDevice device)
        {
            return device != null && !String.IsNullOrEmpty(device.Id) &&
                (String.Equals(device.Id, selectedCameraId, StringComparison.Ordinal) || CameraNames.Contains(device.Name));
        }

        private bool AutomaticCameraBudgetAvailable(string id)
        {
            int attempts;
            return !automaticCameraAttempts.TryGetValue(id, out attempts) || attempts < AutomaticCameraOpenLimit;
        }

        private bool TryBeginAutomaticCameraProbe(CameraDevice device)
        {
            if (!automaticCameraSelection || !AutomaticCameraEligible(device) || !AutomaticCameraBudgetAvailable(device.Id)) return false;
            int attempts; automaticCameraAttempts.TryGetValue(device.Id, out attempts);
            automaticCameraAttempts[device.Id] = attempts + 1;
            DiagnosticLog.Write("receiver.camera.probe", "camera_id", device.Id, "generation", automaticCameraGeneration, "attempt", attempts + 1, "maximum_attempts", AutomaticCameraOpenLimit);
            return true;
        }

        private void AddAutomaticCandidate(string id, Dictionary<string, CameraDevice> listed, HashSet<string> added, List<CameraDevice> ordered)
        {
            CameraDevice device;
            if (!String.IsNullOrEmpty(id) && listed.TryGetValue(id, out device) && AutomaticCameraEligible(device) && added.Add(id)) ordered.Add(device);
        }

        private void ReceiveAutomaticProbeFrame(ReceiverSession session, Bitmap frame)
        {
            try
            {
                // These are the same CRC-checking decoders as normal capture.
                // Probe packets never reach the assembler, clipboard publisher,
                // audio bridge, acknowledgements or received-file preparation.
                Packet[] packets = services.DecodeFramesPreferred != null ? services.DecodeFramesPreferred(frame, null) :
                    services.DecodeFrames != null ? services.DecodeFrames(frame) : new[] { services.DecodeFrame(frame) };
                if (packets == null) return;
                foreach (Packet packet in packets)
                {
                    BridgeHello hello; BridgeCameraFormat format; Guid transfer;
                    if (packet == null || packet.TransferId == Guid.Empty ||
                        !(BridgeControl.TryHello(packet, out hello) || BridgeControl.TryCameraFormat(packet, out format) || BridgeControl.TryAcknowledge(packet, out transfer))) continue;
                    Packet proof = packet;
                    Ui(delegate { ConfirmAutomaticCamera(session, proof); });
                    return;
                }
            }
            catch (Exception error) { DiagnosticLog.Error("receiver.camera.probe_decode_failed", error, "camera_id", session.DeviceId); }
        }

        private void ConfirmAutomaticCamera(ReceiverSession session, Packet proof)
        {
            if (!IsCurrent(session) || !session.AutomaticProbe || !automaticCameraSelection || session.ProbeGeneration != automaticCameraGeneration) return;
            selectedCameraId = session.DeviceId;
            selectedResolution = null; selectedModes = new CameraResolution[0]; ClearSourceFormat();
            automaticCameraAttempts.Remove(session.DeviceId);
            CapturePolicy(session.DeviceId).RecordSuccess();
            if (audioSender != null)
            {
                audioSender.DisableSession();
                if (audioBridge != null) audioBridge.Dispose();
                audioBridge = new ClientAudioBridge(dispatch, audioSender);
            }
            // Publish the transition last: a callback racing this commit remains
            // a harmless probe until all session-dependent state is initialized.
            session.AutomaticProbe = false;
            try { services.WriteSelection(selectedCameraId); }
            catch (Exception error) { LogSettingFailure("write", "camera.id", error); services.LogFailure(error); }
            DiagnosticLog.Write("receiver.camera.identified", "camera_id", session.DeviceId, "generation", automaticCameraGeneration);
            UpdateDeviceMenus(lastMenuDevices, lastMenuOutputs);
            ReceivePacketFrame(session, null, proof);
        }
    }
}
