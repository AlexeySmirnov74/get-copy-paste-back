using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace HorizonClient
{
    internal sealed partial class ServerForm
    {
        private long lastUiAction;

        private string CameraTraceRoot(string key)
        {
            // An already running verified camera can still belong to an older
            // package. Read its trace where it actually runs; never move its key
            // or redirect portable user settings back into the old runtime.
            if (!String.IsNullOrEmpty(key) && System.IO.Path.GetFileName(key).Equals("camera.local.key", StringComparison.OrdinalIgnoreCase))
            {
                string data = System.IO.Path.GetDirectoryName(key);
                if (!String.IsNullOrEmpty(data) && System.IO.Path.GetFileName(data).Equals("data", StringComparison.OrdinalIgnoreCase))
                    return System.IO.Path.GetDirectoryName(data);
            }
            return runtime.RootDirectory;
        }

        private void LogUiAction(string control, string action, params object[] details)
        {
            var fields = new List<object> { "action_id", ++lastUiAction, "control", control, "action", action,
                "session", bridgeSession, "ready", ready, "preparing", preparing,
                "benchmark_busy", IsBenchmarkBusy, "restoring_settings", restoringSettings,
                "selected_profile", selectedProfile, "selected_audio", selectedAudioId ?? "default_render",
                "camera_lookup_known", cameraLookupKnown, "camera_exists", cameraExists, "camera_running", cameraRunning,
                "camera_upgrade_needed", cameraRequiresUpgrade };
            if (details != null) fields.AddRange(details);
            DiagnosticLog.Write("server.ui.action", fields.ToArray());
        }

        private void InitializeUiDiagnostics()
        {
            foreach (Button button in new[] { createCamera, deleteCamera, applyMode, benchmark, benchmarkStop })
            {
                Button selected = button;
                selected.Click += delegate { LogUiAction(selected.Name, "click", "enabled", selected.Enabled); };
            }
            foreach (ComboBox combo in new[] { cameraName, profiles, cameraMode, audioOutput, benchmarkSizeCombo, benchmarkLimitCombo })
            {
                ComboBox selected = combo;
                selected.DropDown += delegate { LogUiAction(selected.Name, "dropdown_open", "items", selected.Items.Count, "index", selected.SelectedIndex); };
                selected.DropDownClosed += delegate { LogUiAction(selected.Name, "dropdown_close", "index", selected.SelectedIndex); };
                selected.SelectionChangeCommitted += delegate
                {
                    if (Object.ReferenceEquals(selected, cameraName))
                        LogUiAction(selected.Name, "selection_committed", "index", selected.SelectedIndex, "name_length", selected.Text.Length);
                    else
                        LogUiAction(selected.Name, "selection_committed", "index", selected.SelectedIndex, "value", selected.Text);
                };
            }
            cameraName.Leave += delegate { LogUiAction(cameraName.Name, "edit_completed", "index", cameraName.SelectedIndex, "name_length", cameraName.Text.Length); };
            serverTabs.SelectedIndexChanged += delegate { LogUiAction(serverTabs.Name, "tab_selected", "index", serverTabs.SelectedIndex); };
            benchmarkResults.CellContentClick += delegate(object sender, DataGridViewCellEventArgs args)
            { LogUiAction(benchmarkResults.Name, "cell_click", "row", args.RowIndex, "column", args.ColumnIndex); };
        }

        // One sender thread owns these counters. File contents never enter logs.
        private sealed class VideoSendTrace
        {
            private readonly Guid bridgeSession;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private long lastSummary, frames, controls, dataFrames, idleFrames, bytes;
            private double encodingMilliseconds, writingMilliseconds, renderingMilliseconds;
            private Guid test;
            private FileBenchmarkPhase phase;
            private VideoProfile profile;
            private bool initialized;
            private Guid firstDataTest;
            private readonly string runtimeRoot;
            private readonly CameraBackendTraceReader backendTrace = new CameraBackendTraceReader();

            internal VideoSendTrace(Guid bridgeSession, string runtimeRoot) { this.bridgeSession = bridgeSession; this.runtimeRoot = runtimeRoot; }

            internal void Sent(UvcCamera camera, string kind, VideoProfile selected,
                FileBenchmarkCommand command, FileBenchmarkVideoSource source, double renderMilliseconds)
            {
                if (!DiagnosticLog.IsEnabled) return;
                Guid currentTest = command == null ? Guid.Empty : command.Test;
                FileBenchmarkPhase currentPhase = command == null ? 0 : command.Phase;
                VideoProfile currentProfile = command == null ? selected : command.Profile;
                long time = clock.ElapsedMilliseconds;
                bool changed = !initialized || currentTest != test || currentPhase != phase || currentProfile != profile;
                if (changed)
                {
                    initialized = true; test = currentTest; phase = currentPhase; profile = currentProfile;
                    // Never attribute the preceding profile or connection delay
                    // to this profile's one-second FPS/timing interval.
                    lastSummary = time; frames = controls = dataFrames = idleFrames = bytes = 0;
                    encodingMilliseconds = writingMilliseconds = renderingMilliseconds = 0;
                    int sourceWidth = VisualProtocol.GetWidth(profile), sourceHeight = VisualProtocol.GetHeight(profile);
                    double scale = Math.Min((double)camera.Width / sourceWidth, (double)camera.Height / sourceHeight);
                    DiagnosticLog.Write("server.video.mode", "session", command == null ? bridgeSession : command.Session,
                        "camera_connection_id", camera.ConnectionId,
                        "test", test, "phase", command == null ? "Normal" : phase.ToString(), "profile", (int)profile, "profileName", profile.ToString(),
                        "sourceWidth", sourceWidth, "sourceHeight", sourceHeight, "cameraWidth", camera.Width,
                        "cameraHeight", camera.Height, "targetFps", camera.FramesPerSecond, "scale", scale,
                        "downscaled", scale < 1, "format", camera.IsUncompressed ? "YUY2" : "MJPEG",
                        "chunkBytes", VisualProtocol.GetChunkSize(profile), "packetsPerFrame", VisualProtocol.GetPacketsPerFrame(profile),
                        "note", scale < 1 ? "Profile pixels are reduced before Horizon; small cells may be destroyed." : "Backend write is not a remote delivery acknowledgement.");
                }
                frames++; bytes += camera.LastVideoPayloadBytes;
                if (kind == "control") controls++;
                else if (kind == "idle") idleFrames++;
                else dataFrames++;
                if (kind == "file-data" && firstDataTest != source.LastTest)
                {
                    firstDataTest = source.LastTest;
                    DiagnosticLog.Write("server.video.file_data.first", "session", bridgeSession, "test", source.LastTest,
                        "profile", (int)profile, "segment", source.LastSegment, "packet_index", source.LastPacketIndex,
                        "packets", source.LastPacketCount, "camera_width", camera.Width, "camera_height", camera.Height,
                        "encoded_frame_bytes", camera.LastVideoPayloadBytes,
                        "note", "The first synthetic file frame was successfully written to the camera backend; remote receipt requires a client report.");
                }
                encodingMilliseconds += camera.LastEncodeMilliseconds;
                writingMilliseconds += camera.LastWriteMilliseconds;
                renderingMilliseconds += renderMilliseconds;
                if (command != null)
                {
                    bool fileData = kind == "file-data";
                    DiagnosticLog.Write("server.video.frame.sent", "session", command.Session, "test", command.Test,
                        "profile", (int)command.Profile, "phase", command.Phase.ToString(), "kind", kind,
                        "backendFrame", camera.VideoFramesSent, "payloadBytes", camera.LastVideoPayloadBytes,
                        "renderMs", renderMilliseconds, "encodeMs", camera.LastEncodeMilliseconds, "writeMs", camera.LastWriteMilliseconds,
                        "segment", fileData ? source.LastSegment : -1, "packetIndex", fileData ? source.LastPacketIndex : -1,
                        "packets", fileData ? source.LastPacketCount : 0, "segmentPackets", fileData ? source.SegmentPacketCount : 0,
                        "round", fileData ? source.LastRound : -1);
                }
                long duration = time - lastSummary;
                if (duration < 1000) return;
                backendTrace.Poll(runtimeRoot);
                DiagnosticLog.Write("server.video.summary", "session", bridgeSession, "test", test, "profile", (int)profile,
                    "camera_connection_id", camera.ConnectionId,
                    "phase", command == null ? "Normal" : phase.ToString(),
                    "intervalMs", duration, "frames", frames, "controlFrames", controls, "dataFrames", dataFrames, "idleFrames", idleFrames,
                    "actualFps", frames * 1000.0 / duration, "backendBytes", bytes, "backendMbitS", bytes * 8.0 / duration / 1000,
                    "renderMeanMs", renderingMilliseconds / frames, "encodeMeanMs", encodingMilliseconds / frames,
                    "writeMeanMs", writingMilliseconds / frames, "backendFramesTotal", camera.VideoFramesSent);
                lastSummary = time; frames = controls = dataFrames = idleFrames = bytes = 0;
                encodingMilliseconds = writingMilliseconds = renderingMilliseconds = 0;
            }
        }
    }
}
