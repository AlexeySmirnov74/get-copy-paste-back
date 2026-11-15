using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HorizonClient
{
    // The owner forwards WM_CLIPBOARDUPDATE on its hidden STA dispatch control.
    // Construction does not inspect the clipboard or enumerate/open audio.
    internal sealed class AudioClipboardSender : IDisposable
    {
        private readonly Control dispatch;
        private readonly AudioSenderServices services;
        private readonly object gate = new object();
        private readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private bool enabled, disposed, listening, encoding, senderStarted, benchmarkMode, needsResumeBaseline;
        private IntPtr listenerHandle;
        private Guid session;
        private long clipboardVersion;
        private uint observedSequence, suppressedSequence;
        private bool hasSuppressedSequence;
        private int captureAttempts;
        private string preferredOutput;
        private string playerOutputId, playerOutputName;
        private bool outputRequested;
        private int outputVersion;
        private int outputRequestGeneration, outputRequestsInFlight;
        private AudioModemKind selectedModem;
        private int additionalRetries = 2;
        private AudioPlayer player;
        private EncodeWork pendingEncoding;
        private Transmission clipboard, priority, diagnostic, fastReceipt, active;
        private Guid lastReceiptSession, lastReceiptTransfer;
        private int lastReceiptBytes;
        private CancellationTokenSource playback;
        private bool routeCalibration;
        private Guid verifiedPlan, verifiedSession;
        private string[] verifiedOutputs;
        private string manualOutputBeforeVerification;
        private bool manualOutputWasRequested;

        internal Action<Exception> Error { get; set; }
        internal AudioModemKind SelectedModem { get { lock (gate) return selectedModem; } }
        internal string SelectedOutputId { get { lock (gate) return verifiedOutputs != null && verifiedOutputs.Length == 0 ? null : playerOutputId ?? preferredOutput; } }

        internal void SetRouteCalibration(bool value)
        {
            lock (gate) { if (disposed || routeCalibration == value) return; }
            debounce.Stop(); uint sequence = 0; bool baseline = false;
            if (!value) { try { sequence = services.Sequence(); baseline = true; } catch (Exception error) { Report(error); } }
            lock (gate)
            {
                if (disposed || routeCalibration == value) return;
                routeCalibration = value;
                if (value)
                {
                    clipboardVersion++; pendingEncoding = null; clipboard = null; captureAttempts = 0;
                }
                if (!value)
                {
                    needsResumeBaseline = !baseline;
                    if (baseline) { observedSequence = sequence; hasSuppressedSequence = false; }
                }
                CancelPlayback(false);
                DiagnosticLog.Write("client.audio.route_calibration", "session", session, "active", value, "resume_baseline", baseline);
            }
            Signal();
        }

        // Only the exact-session verified-route controller calls this method.
        // null restores single-output policy; empty is an explicit mute.
        internal void SetVerifiedOutputs(Guid plan, string[] outputs)
        {
            Guid expectedSession;
            lock (gate) expectedSession = session;
            SetVerifiedOutputs(expectedSession, plan, outputs);
        }

        internal bool SetVerifiedOutputs(Guid expectedSession, Guid plan, string[] outputs)
        { return SetVerifiedOutputs(expectedSession, plan, outputs, null); }

        internal bool SetVerifiedOutputs(Guid expectedSession, Guid plan, string[] outputs, Func<bool> stillCurrent)
        {
            if (outputs != null)
            {
                outputs = (string[])outputs.Clone();
                if (plan == Guid.Empty || outputs.Length > 16) throw new ArgumentException("A verified route plan is required.");
                var unique = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (string output in outputs) { ValidateOutput(output, false); if (!unique.Add(output)) throw new ArgumentException("Duplicate verified output."); }
            }
            bool replace;
            lock (gate)
            {
                // The route controller's final generation check and sender
                // mutation share this gate. The predicate performs no I/O.
                if (disposed || !enabled || session != expectedSession || (stillCurrent != null && !stillCurrent())) return false;
                if (outputs != null && verifiedOutputs == null)
                { manualOutputBeforeVerification = preferredOutput; manualOutputWasRequested = outputRequested; }
                bool restore = outputs == null && verifiedOutputs != null;
                if (restore) RestoreManualOutputLocked();
                verifiedPlan = outputs == null ? Guid.Empty : plan; verifiedSession = outputs == null ? Guid.Empty : session; verifiedOutputs = outputs;
                if (outputs != null && outputs.Length != 0)
                {
                    preferredOutput = outputs[0]; outputRequested = true; outputVersion++;
                }
                CancelPlayback(false);
                replace = (restore || (outputs != null && outputs.Length != 0)) && (enabled || diagnostic != null);
                DiagnosticLog.Write("client.audio.verified_outputs", "session", session, "plan", verifiedPlan,
                    "explicit", outputs != null, "count", outputs == null ? -1 : outputs.Length, "primary", preferredOutput);
            }
            if (replace) ReplacePlayer(); Signal();
            return true;
        }

        private void RestoreManualOutputLocked()
        {
            preferredOutput = manualOutputBeforeVerification; outputRequested = manualOutputWasRequested; outputVersion++;
            manualOutputBeforeVerification = null; manualOutputWasRequested = false;
        }

        internal void SetAdditionalRetries(int retries)
        {
            if (retries < 0 || retries > 10) throw new ArgumentOutOfRangeException("retries");
            lock (gate)
            {
                if (disposed) return;
                if (additionalRetries == retries) return;
                additionalRetries = retries;
                if (clipboard != null)
                {
                    clipboard.MaximumPasses = retries + 1;
                    clipboard.AckDeadline = 0;
                    CancelPlayback(true);
                }
                DiagnosticLog.Write("client.audio.retry_policy", "session", session, "additional_retries", retries, "maximum_passes", retries + 1);
            }
            Signal();
        }

        internal void SetModem(AudioModemKind kind)
        {
            AudioModemCatalog.GetId(kind);
            lock (gate) { if (disposed) throw new ObjectDisposedException("AudioClipboardSender"); if (selectedModem == kind) return; }
            PrepareModem(kind);
            ApplyPreparedModem(kind);
        }

        internal void PrepareModem(AudioModemKind kind)
        {
            AudioModemCatalog.GetId(kind);
            lock (gate) { if (disposed) throw new ObjectDisposedException("AudioClipboardSender"); }
            // Failure leaves the active modem and current transfer intact.
            services.ValidateModem(kind);
        }

        internal void ApplyPreparedModem(AudioModemKind kind)
        {
            AudioModemCatalog.GetId(kind);
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("AudioClipboardSender");
                if (selectedModem == kind) return;
                CancelDiagnosticLocked("modem_changed");
                selectedModem = kind;
                if (active == null || !active.FixedFsk) CancelPlayback(false);
                DiagnosticLog.Write("client.audio.modem.selected", "modem", AudioModemCatalog.GetId(kind), "session", session);
            }
            Signal();
        }

        internal AudioClipboardSender(Control dispatch) : this(dispatch, new AudioSenderServices { EnableTransferStatus = true }) { }

        internal AudioClipboardSender(Control dispatch, AudioSenderServices services)
        {
            if (dispatch == null) throw new ArgumentNullException("dispatch");
            if (services == null) throw new ArgumentNullException("services");
            this.dispatch = dispatch;
            this.services = services;
            debounce.Interval = 180;
            debounce.Tick += CapturePending;
        }

        // Explicit user action. This starts no bridge session, clipboard listener
        // or clipboard read. Device enumeration and encoding run on the existing
        // serialized sender worker, so the tray click returns immediately.
        internal Guid TestOutput()
        {
            Guid id = Guid.NewGuid();
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("AudioClipboardSender");
                DiagnosticLog.Write("client.audio.probe.requested", "probe", id, "modem", AudioModemCatalog.GetId(selectedModem),
                    "endpoint", preferredOutput, "sessionEnabled", enabled);
                CancelDiagnosticLocked("new_test");
                diagnostic = new Transmission { Bytes = AudioDiagnosticProbe.Encode(id, selectedModem, DateTime.UtcNow.Ticks),
                    Id = id, Session = Guid.Empty, Priority = true, Diagnostic = true, Modem = selectedModem, OutputId = preferredOutput };
                if (active == null || (!active.FixedFsk && !active.Receipt)) CancelPlayback(false);
                LogTransmission("client.audio.priority_queued", diagnostic);
                StartSenderLocked();
            }
            Signal(); return id;
        }

        private void StartSenderLocked()
        {
            if (senderStarted) return;
            senderStarted = true;
            Thread sender = new Thread(SendLoop) { IsBackground = true, Name = "Get Copy-Paste Back client audio sender" };
            sender.Start();
        }

        // STA owner methods. Enable intentionally leaves pre-existing clipboard
        // contents alone; only a later clipboard-update notification is sent.
        internal void Enable(Guid sessionId)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A bridge session is required.", "sessionId");
            lock (gate)
            {
                if (disposed || (enabled && session == sessionId)) return;
            }
            debounce.Stop();
            try
            {
                if (!listening)
                {
                    listenerHandle = dispatch.Handle;
                    if (!services.AddListener(listenerHandle)) throw new InvalidOperationException("Cannot subscribe to clipboard changes.");
                    listening = true;
                }
                uint sequence = services.Sequence();
                lock (gate)
                {
                    if (disposed) return;
                    enabled = true;
                    if (verifiedOutputs != null && verifiedSession != sessionId)
                    { RestoreManualOutputLocked(); verifiedOutputs = null; verifiedPlan = verifiedSession = Guid.Empty; }
                    session = sessionId;
                    clipboardVersion++;
                    observedSequence = sequence;
                    hasSuppressedSequence = false;
                    pendingEncoding = null;
                    clipboard = priority = fastReceipt = null;
                    lastReceiptSession = lastReceiptTransfer = Guid.Empty; lastReceiptBytes = 0;
                    if (active == null || !active.Diagnostic) CancelPlayback(false);
                    StartSenderLocked();
                }
                DiagnosticLog.Write("client.audio.enabled", "session", sessionId, "benchmarkMode", benchmarkMode);
                ReplacePlayer();
                Signal();
            }
            catch (Exception error) { Report(error); DisableSession(); }
        }

        // A saved explicit output can be assigned before Enable without any
        // audio enumeration. Automatic selection never falls back to hardware.
        internal void SetOutput(string endpointId)
        {
            ValidateOutput(endpointId, true);
            bool replace, changed;
            lock (gate)
            {
                if (disposed) return;
                changed = !outputRequested || !String.Equals(preferredOutput, endpointId, StringComparison.Ordinal) || verifiedOutputs != null;
                verifiedOutputs = null; verifiedPlan = verifiedSession = Guid.Empty;
                manualOutputBeforeVerification = null; manualOutputWasRequested = false;
                if (changed) CancelDiagnosticLocked("output_changed");
                if (changed) outputVersion++;
                outputRequested = true;
                preferredOutput = endpointId;
                replace = enabled || diagnostic != null;
            }
            if (changed) DiagnosticLog.Write("client.audio.output_requested", "endpoint", endpointId, "enabled", enabled);
            if (replace) ReplacePlayer();
            Signal();
        }

        private static void ValidateOutput(string endpointId, bool allowAutomatic)
        {
            if (endpointId == null && allowAutomatic) return;
            uint index;
            if (endpointId == null || !endpointId.StartsWith("waveout:", StringComparison.Ordinal) ||
                !UInt32.TryParse(endpointId.Substring(8), NumberStyles.None, CultureInfo.InvariantCulture, out index) || index > 65535)
                throw new ArgumentException("An explicit waveout endpoint ID is required.", "endpointId");
        }

        private bool ReplacePlayer()
        {
            int request;
            lock (gate)
            {
                if (disposed || (!enabled && diagnostic == null)) return false;
                request = ++outputRequestGeneration; outputRequestsInFlight++;
            }
            try { return ReplacePlayerSnapshot(request); }
            finally { lock (gate) outputRequestsInFlight--; Signal(); }
        }

        private bool ReplacePlayerSnapshot(int request)
        {
            string selection;
            int selectedVersion;
            lock (gate) { if (disposed || (!enabled && diagnostic == null) || request != outputRequestGeneration) return false; selection = preferredOutput; selectedVersion = outputVersion; }
            AudioPlayer replacement = null;
            AudioEndpoint chosen = null;
            try
            {
                AudioEndpoint[] outputs = services.Outputs() ?? new AudioEndpoint[0];
                if (DiagnosticLog.IsEnabled)
                    foreach (AudioEndpoint candidate in outputs)
                        if (candidate != null) DiagnosticLog.Write("client.audio.output_candidate", "endpoint", candidate.Id, "name", candidate.Name,
                            "horizon", IsHorizonOutput(candidate.Name), "requested", selection);
                foreach (AudioEndpoint endpoint in outputs)
                {
                    if (endpoint == null) continue;
                    if (selection != null ? String.Equals(selection, endpoint.Id, StringComparison.Ordinal) : IsHorizonOutput(endpoint.Name))
                    {
                        chosen = endpoint;
                        break;
                    }
                }
                lock (gate)
                {
                    if (disposed || (!enabled && diagnostic == null) || selectedVersion != outputVersion || request != outputRequestGeneration) return false;
                    // Menu refreshes may re-enumerate devices. Keep a running
                    // packet intact when the resolved endpoint is unchanged.
                    if (player != null && chosen != null && String.Equals(playerOutputId, chosen.Id, StringComparison.Ordinal) &&
                        String.Equals(playerOutputName, chosen.Name, StringComparison.Ordinal)) return true;
                }
                if (chosen != null) replacement = services.CreatePlayer(chosen.Id);
            }
            catch (Exception error) { DiagnosticLog.Error("client.audio.output_open_failed", error, "requested", selection); Report(error); }
            AudioPlayer previous;
            string previousOutput;
            lock (gate)
            {
                if (disposed || (!enabled && diagnostic == null) || selectedVersion != outputVersion || request != outputRequestGeneration)
                {
                    if (replacement != null) services.DisposePlayer(replacement);
                    return false;
                }
                // A diagnostic worker and a simultaneous menu refresh can both
                // enumerate the same choice before either created its player.
                if (replacement != null && player != null && chosen != null && String.Equals(playerOutputId, chosen.Id, StringComparison.Ordinal) &&
                    String.Equals(playerOutputName, chosen.Name, StringComparison.Ordinal))
                { services.DisposePlayer(replacement); return true; }
                previous = player;
                previousOutput = playerOutputId;
                player = replacement;
                playerOutputId = replacement == null ? null : chosen.Id;
                playerOutputName = replacement == null ? null : chosen.Name;
                CancelPlayback(false);
            }
            DiagnosticLog.Write("client.audio.output_selected", "session", session, "requested", selection, "endpoint", playerOutputId,
                "name", playerOutputName, "found", replacement != null, "previousEndpoint", previousOutput);
            if (previous != null)
            {
                try { services.DisposePlayer(previous); DiagnosticLog.Write("client.audio.output_closed", "endpoint", previousOutput); }
                catch (Exception error) { DiagnosticLog.Error("client.audio.output_close_failed", error, "endpoint", previousOutput); Report(error); }
            }
            Signal();
            return true;
        }

        internal static bool IsHorizonOutput(string name)
        {
            return AudioOutputSelection.IsHorizonOutput(name);
        }

        internal void NotifyClipboardChanged()
        {
            uint sequence;
            lock (gate)
            {
                if (disposed || !enabled || benchmarkMode || routeCalibration)
                {
                    DiagnosticLog.Write("client.audio.clipboard_notification_skipped", "reason", disposed ? "disposed" : !enabled ? "no_video_session" : routeCalibration ? "route_calibration" : "benchmark_paused", "session", session);
                    return;
                }
            }
            try { sequence = services.Sequence(); }
            catch (Exception error) { Report(error); return; }
            lock (gate)
            {
                if (disposed || !enabled || benchmarkMode || routeCalibration)
                { DiagnosticLog.Write("client.audio.clipboard_notification_skipped", "reason", "state_changed", "session", session); return; }
                if (needsResumeBaseline)
                { observedSequence = sequence; needsResumeBaseline = false; DiagnosticLog.Write("client.audio.clipboard_notification_skipped", "reason", "resume_baseline", "session", session); return; }
                if (sequence == observedSequence || (hasSuppressedSequence && sequence == suppressedSequence))
                { DiagnosticLog.Write("client.audio.clipboard_notification_skipped", "reason", hasSuppressedSequence && sequence == suppressedSequence ? "received_clipboard_echo" : "same_sequence", "session", session); return; }
                observedSequence = sequence;
                clipboardVersion++;
                pendingEncoding = null;
                clipboard = null;
                captureAttempts = 0;
                CancelPlayback(true);
                DiagnosticLog.Write("client.audio.clipboard_notification_accepted", "session", session, "version", clipboardVersion);
            }
            debounce.Stop();
            debounce.Start();
            Signal();
        }

        // STA: benchmarks reserve playback for their small priority reports.
        // Pausing discards pending user copies without changing the session,
        // endpoint or priority queue. Resume uses only the sequence counter;
        // copies made while paused are never read or sent automatically.
        internal void SetBenchmarkMode(bool value)
        {
            lock (gate) { if (disposed || benchmarkMode == value) return; }
            debounce.Stop();
            uint sequence = 0; bool baseline = false;
            if (!value)
            {
                try { sequence = services.Sequence(); baseline = true; }
                catch (Exception error) { Report(error); }
            }
            lock (gate)
            {
                if (disposed || benchmarkMode == value) return;
                benchmarkMode = value;
                clipboardVersion++;
                pendingEncoding = null; clipboard = null; captureAttempts = 0;
                needsResumeBaseline = !value && !baseline;
                if (baseline) { observedSequence = sequence; hasSuppressedSequence = false; }
                CancelPlayback(true);
            }
            DiagnosticLog.Write("client.audio.clipboard_mode", "session", session, "paused", value, "resumeBaseline", baseline);
            Signal();
        }

        // Call immediately after publishing a remote clipboard on this STA.
        // Version invalidation also discards an older in-flight file encoder.
        internal void SuppressCurrentClipboard()
        {
            uint sequence;
            lock (gate) { if (disposed || !enabled) return; }
            try { sequence = services.Sequence(); }
            catch (Exception error) { Report(error); return; }
            debounce.Stop();
            lock (gate)
            {
                if (disposed || !enabled) return;
                suppressedSequence = observedSequence = sequence;
                hasSuppressedSequence = true;
                clipboardVersion++;
                pendingEncoding = null;
                clipboard = null;
                CancelPlayback(true);
            }
            Signal();
        }

        // Acknowledgements arrive through the independent forward channel.
        // An old acknowledgement must never cancel a newer clipboard copy.
        internal void Acknowledge(Guid transferId)
        {
            lock (gate)
            {
                if (disposed || clipboard == null || clipboard.Id != transferId) return;
                Transmission acknowledged = clipboard;
                LogTransmission("client.audio.clipboard.acknowledged", acknowledged);
                clipboard = null;
                if (active == acknowledged) CancelPlayback(true);
            }
            Signal();
        }

        private void CapturePending(object sender, EventArgs args)
        {
            debounce.Stop();
            long version;
            uint sequence;
            Guid sessionId;
            lock (gate)
            {
                if (disposed || !enabled || benchmarkMode || routeCalibration)
                { DiagnosticLog.Write("client.audio.clipboard_capture_skipped", "reason", disposed ? "disposed" : !enabled ? "no_video_session" : "benchmark_paused", "session", session); return; }
                version = clipboardVersion;
                sequence = observedSequence;
                sessionId = session;
                if (hasSuppressedSequence && sequence == suppressedSequence)
                { DiagnosticLog.Write("client.audio.clipboard_capture_skipped", "reason", "received_clipboard_echo", "session", session); return; }
            }
            try
            {
                if (services.Sequence() != sequence) { NotifyClipboardChanged(); return; }
                ClipboardSnapshot snapshot = services.Capture();
                if (services.Sequence() != sequence) { NotifyClipboardChanged(); return; }
                if (snapshot == null)
                { DiagnosticLog.Write("client.audio.clipboard_capture_skipped", "reason", "empty_or_unsupported", "session", sessionId); return; }
                lock (gate)
                {
                    if (!Current(version, sessionId, sequence)) return;
                    pendingEncoding = new EncodeWork { Snapshot = snapshot, Version = version, Session = sessionId, Sequence = sequence };
                    if (!encoding)
                    {
                        encoding = true;
                        ThreadPool.QueueUserWorkItem(delegate { EncodeLoop(); });
                    }
                }
            }
            catch (ExternalException error)
            {
                bool retry;
                lock (gate) { retry = Current(version, sessionId, sequence) && ++captureAttempts < 5; }
                if (retry) debounce.Start();
                else Report(error);
            }
            catch (Exception error) { Report(error); }
        }

        private bool Current(long version, Guid sessionId, uint sequence)
        {
            return !disposed && enabled && !benchmarkMode && !routeCalibration && clipboardVersion == version && session == sessionId && observedSequence == sequence &&
                (!hasSuppressedSequence || suppressedSequence != sequence);
        }

        private void EncodeLoop()
        {
            for (;;)
            {
                EncodeWork work;
                lock (gate)
                {
                    work = pendingEncoding;
                    pendingEncoding = null;
                    if (work == null || disposed) { encoding = false; return; }
                }
                try
                {
                    if (services.Sequence() != work.Sequence) continue;
                    lock (gate) { if (!Current(work.Version, work.Session, work.Sequence)) continue; }
                    byte[] encoded = services.Encode(work.Snapshot);
                    if (services.Sequence() != work.Sequence) continue;
                    byte[] envelope = BridgeControl.WrapClipboard(work.Session, encoded);
                    lock (gate)
                    {
                        if (!Current(work.Version, work.Session, work.Sequence)) continue;
                        clipboard = new Transmission { Bytes = envelope, Id = Guid.NewGuid(), Session = work.Session, Version = work.Version, Modem = selectedModem, MaximumPasses = additionalRetries + 1 };
                        LogTransmission("client.audio.clipboard_queued", clipboard);
                        CancelPlayback(true);
                    }
                    Signal();
                }
                catch (Exception error) { Report(error); }
            }
        }

        // Thread-safe asynchronous priority control message. The caller already
        // supplied its control envelope; clipboard wrapping is not applied.
        internal void SendMessage(byte[] bytes, Guid transferId)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > VisualProtocol.MaxTransferBytes || transferId == Guid.Empty)
                throw new ArgumentException("Invalid audio message.");
            Guid sessionId;
            lock (gate)
            {
                if (disposed || !enabled) { DiagnosticLog.Write("client.audio.priority_rejected", "audioTransfer", transferId, "reason", disposed ? "disposed" : "not_enabled"); return; }
                sessionId = session;
            }
            byte[] copy = (byte[])bytes.Clone();
            lock (gate)
            {
                if (disposed || !enabled || session != sessionId) { DiagnosticLog.Write("client.audio.priority_rejected", "audioTransfer", transferId, "reason", "session_changed_or_disabled"); return; }
                Transmission next = new Transmission { Bytes = copy, Id = transferId, Session = sessionId, Priority = true, Modem = selectedModem };
                if (priority != null) LogTransmission("client.audio.priority_superseded", priority);
                priority = next;
                LogTransmission("client.audio.priority_queued", next);
                DiagnosticLog.Write("client.audio.priority_queue_state", "session", sessionId, "audioTransfer", transferId,
                    "outputReady", player != null, "endpoint", playerOutputId, "activeTransfer", active == null ? Guid.Empty : active.Id,
                    "benchmarkMode", benchmarkMode);
                if (active == null || (!active.Diagnostic && !active.FixedFsk && !active.Receipt)) CancelPlayback(false);
            }
            Signal();
        }

        internal void SendCompactFileReport(byte[] wire, Guid transferId)
        {
            if (transferId == Guid.Empty || AudioModem.ReadCompactFileReport(wire, wire == null ? 0 : wire.Length) == null)
                throw new ArgumentException("Invalid compact benchmark report.");
            byte[] copy = (byte[])wire.Clone();
            lock (gate)
            {
                if (disposed || !enabled) { DiagnosticLog.Write("client.audio.compact_file_rejected", "audioTransfer", transferId, "reason", disposed ? "disposed" : "not_enabled"); return; }
                priority = new Transmission { Bytes = copy, Id = transferId, Session = session, Priority = true, CompactFileReport = true, Modem = AudioModemKind.Fsk };
                LogTransmission("client.audio.compact_file_queued", priority);
                // Finish an already short control burst. A fresh status replaces
                // its remaining repeats without chopping a valid frame in half.
                if (active == null || (!active.Diagnostic && !active.FixedFsk && !active.Receipt)) CancelPlayback(false);
                StartSenderLocked();
            }
            Signal();
        }

        // Publication receipts bypass bulk codec framing and its repetitions.
        // A new publication preempts immediately. Retries of that publication
        // yield at a packet boundary so reverse data and native receipts progress.
        internal void SendFastReceipt(Guid sessionId, Guid transferId, int encodedBytes)
        {
            byte[] wire = DeliveryReceipt.EncodeFast(sessionId, transferId, encodedBytes);
            QueueDeliveryReceipt(sessionId, transferId, encodedBytes, wire, true);
        }

        // A fallback uses the user's selected robust codec, still ahead of bulk
        // data, and sends one complete pass rather than a three-pass report.
        internal void SendDeliveryReceiptFallback(Guid sessionId, Guid transferId, int encodedBytes)
        {
            byte[] wire = DeliveryReceipt.Encode(sessionId, transferId, encodedBytes);
            QueueDeliveryReceipt(sessionId, transferId, encodedBytes, wire, false);
        }

        private void QueueDeliveryReceipt(Guid sessionId, Guid transferId, int encodedBytes, byte[] wire, bool fast)
        {
            lock (gate)
            {
                if (disposed || !enabled || session != sessionId)
                {
                    DiagnosticLog.Write("client.audio.fast_receipt_rejected", "session", sessionId, "transfer", transferId,
                        "reason", disposed ? "disposed" : !enabled ? "not_enabled" : "session_mismatch");
                    return;
                }
                bool repeated = lastReceiptSession == sessionId && lastReceiptTransfer == transferId && lastReceiptBytes == encodedBytes;
                lastReceiptSession = sessionId; lastReceiptTransfer = transferId; lastReceiptBytes = encodedBytes;
                fastReceipt = new Transmission { Bytes = wire, Id = Guid.NewGuid(), Session = sessionId, Priority = true,
                    FastReceipt = fast, NativeReceipt = !fast, ReceiptTransfer = transferId, ReceiptBytes = encodedBytes,
                    QueuedAt = Stopwatch.GetTimestamp(), Modem = fast ? AudioModemKind.Fsk : selectedModem };
                LogTransmission(fast ? "client.audio.fast_receipt_queued" : "client.audio.receipt_fallback_queued", fastReceipt);
                // Coalesce retry controls without chopping an active frame or
                // the same publication's selected-modem fallback. Silent ACK
                // grace has no frame to protect and must wake immediately.
                if (!repeated || (active != null && !active.Priority && active.AckDeadline != 0)) CancelPlayback(false);
                StartSenderLocked();
            }
            Signal();
        }

        private void SendLoop()
        {
            try
            {
                for (;;)
                {
                    Transmission work;
                    AudioPlayer output;
                    AudioModemKind modem;
                    CancellationTokenSource cancel;
                    Transmission opening;
                    string[] stripeOutputs;
                    lock (gate)
                    {
                        if (disposed) return;
                        bool routesReady = verifiedOutputs == null || (verifiedOutputs.Length != 0 && (player == null || String.Equals(playerOutputId, verifiedOutputs[0], StringComparison.Ordinal)));
                        opening = !routeCalibration && routesReady && diagnostic != null && player == null && outputRequestsInFlight == 0 ? diagnostic : null;
                        work = !routeCalibration && routesReady && player != null ? (fastReceipt ?? diagnostic ?? (enabled ? (priority ?? (benchmarkMode ? null : clipboard)) : null)) : null;
                        stripeOutputs = verifiedOutputs == null ? null : (string[])verifiedOutputs.Clone();
                        output = player;
                        modem = work != null && work.FixedFsk ? AudioModemKind.Fsk : selectedModem;
                        cancel = null;
                        if (work != null)
                        {
                            cancel = new CancellationTokenSource();
                            playback = cancel;
                            active = work;
                            work.OutputId = playerOutputId;
                        }
                    }
                    if (opening != null)
                    {
                        bool applied = ReplacePlayer();
                        lock (gate)
                        {
                            if (applied && diagnostic == opening && player == null && outputRequestsInFlight == 0)
                            {
                                DiagnosticLog.Write("client.audio.probe.output_unavailable", "probe", opening.Id, "modem", AudioModemCatalog.GetId(opening.Modem),
                                    "endpoint", preferredOutput, "reason", preferredOutput == null ? "no_horizon_output" : "selected_output_not_found_or_failed");
                                CancelDiagnosticLocked("output_unavailable");
                            }
                        }
                        continue;
                    }
                    if (work == null) { ReleaseUnusedPlayer(); wake.WaitOne(); continue; }
                    bool failed = false;
                    work.Modem = modem;
                    if (work.Receipt) work.QueueDelayMilliseconds = (long)((Stopwatch.GetTimestamp() - work.QueuedAt) * (1000.0 / Stopwatch.Frequency));
                    LogTransmission("client.audio.transmission_started", work);
                    try
                    {
                        int count = Math.Max(1, (work.Bytes.Length + AudioModem.MaxPayloadBytes - 1) / AudioModem.MaxPayloadBytes);
                        int maximumPasses = work.Priority ? (work.Receipt ? 1 : 3) : work.MaximumPasses;
                        for (int cycle = work.CompletedPasses; cycle < maximumPasses; cycle++)
                        {
                            if (!work.Priority && services.EnableTransferStatus)
                                PlayTransferStatus(work, output, stripeOutputs, work.NextIndex, AudioTransferPhase.Sending, cancel.Token,
                                    work.NeedsRouteBoundary || (work.CompletedPasses != 0 && work.NextIndex == 0));
                            for (int index = work.NextIndex; index < count; )
                            {
                                cancel.Token.ThrowIfCancellationRequested();
                                lock (gate)
                                {
                                    if (disposed || routeCalibration || player != output || (!work.FixedFsk && selectedModem != modem) ||
                                        (work.Receipt ? !enabled || session != work.Session || fastReceipt != work :
                                            fastReceipt != null || (work.Diagnostic ? diagnostic != work : !enabled || session != work.Session || diagnostic != null ||
                                            (work.Priority ? priority != work : benchmarkMode || clipboard != work || clipboardVersion != work.Version || priority != null))))
                                        throw new OperationCanceledException();
                                }
                                if (!work.Priority && stripeOutputs != null && stripeOutputs.Length > 1)
                                {
                                    int sent = ParallelAudioWave.Play(services, output, work.OutputId, stripeOutputs, modem, work.Id, index, count, work.Bytes, cancel.Token);
                                    cancel.Token.ThrowIfCancellationRequested();
                                    index += sent;
                                    lock (gate) { cancel.Token.ThrowIfCancellationRequested(); work.NextIndex = index; }
                                    if (services.EnableTransferStatus)
                                        PlayTransferStatus(work, output, stripeOutputs, index, index == count ? AudioTransferPhase.PassComplete : AudioTransferPhase.Sending, cancel.Token, index == count);
                                    continue;
                                }
                                AudioEncodedBlock block = work.FastReceipt ? services.EncodeFastReceipt(work.Bytes) : work.CompactFileReport ? services.EncodeCompactFileReport(work.Bytes) : services.EncodeAudio(modem, work.Id, index, work.Bytes);
                                if (block == null || block.Samples == null || block.Packets < 1 || block.Packets > count - index)
                                    throw new InvalidOperationException("Audio modem returned an invalid packet batch.");
                                float[] samples = block.Samples;
                                cancel.Token.ThrowIfCancellationRequested();
                                DiagnosticLog.Write(work.Diagnostic ? "client.audio.probe.encoded" : "client.audio.batch.encoded", "modem", AudioModemCatalog.GetId(modem), "audioTransfer", work.Id,
                                    "probe", work.Diagnostic ? work.Id : Guid.Empty, "endpoint", work.OutputId,
                                    "first_index", index, "packets", block.Packets, "wire_bytes", block.WireBytes, "samples", samples.Length);
                                long playStarted = DiagnosticLog.IsEnabled ? Stopwatch.GetTimestamp() : 0;
                                LogTransmission("client.audio.play_begin", work, cycle, index, count, samples.Length);
                                services.Play(output, samples, cancel.Token);
                                long playMilliseconds = playStarted == 0 ? 0 : (long)((Stopwatch.GetTimestamp() - playStarted) * (1000.0 / Stopwatch.Frequency));
                                LogTransmission("client.audio.play_end", work, cycle, index, count, samples.Length, playMilliseconds);
                                index += block.Packets;
                                lock (gate) { cancel.Token.ThrowIfCancellationRequested(); work.NextIndex = index; }
                                if (!work.Priority && services.EnableTransferStatus)
                                    PlayTransferStatus(work, output, stripeOutputs, index, index == count ? AudioTransferPhase.PassComplete : AudioTransferPhase.Sending, cancel.Token, index == count);
                            }
                            lock (gate)
                            {
                                work.CompletedPasses++; work.NextIndex = 0;
                                maximumPasses = work.Priority ? (work.Receipt ? 1 : 3) : work.MaximumPasses;
                                if (!work.Receipt && fastReceipt != null) throw new OperationCanceledException();
                            }
                            LogTransmission("client.audio.pass_completed", work, cycle);
                        }
                        if (!work.Priority)
                        {
                            // A final successful frame can still be travelling
                            // through audio redirection. Wait silently for ACK;
                            // newer control/copy/output actions remain cancellable.
                            int remaining;
                            lock (gate)
                            {
                                cancel.Token.ThrowIfCancellationRequested();
                                if (fastReceipt != null) throw new OperationCanceledException();
                                if (work.AckDeadline == 0) work.AckDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * services.AcknowledgementGraceMilliseconds / 1000;
                                remaining = (int)Math.Max(0, Math.Min(Int32.MaxValue, (work.AckDeadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency));
                            }
                            if (cancel.Token.WaitHandle.WaitOne(remaining)) cancel.Token.ThrowIfCancellationRequested();
                            lock (gate) { cancel.Token.ThrowIfCancellationRequested(); if (clipboard == work) clipboard = null; }
                            LogTransmission("client.audio.clipboard.retry_exhausted", work);
                        }
                        lock (gate)
                        {
                            if (priority == work) priority = null;
                            if (diagnostic == work) diagnostic = null;
                            if (fastReceipt == work) fastReceipt = null;
                        }
                        if (work.Priority) LogTransmission("client.audio.transmission_completed", work);
                    }
                    catch (OperationCanceledException)
                    { if (!work.Priority) work.NeedsRouteBoundary = true; LogTransmission("client.audio.transmission_cancelled", work); }
                    catch (Exception error)
                    {
                        bool obsolete;
                        lock (gate) { obsolete = disposed || player != output || (!work.FixedFsk && selectedModem != modem) || cancel.IsCancellationRequested ||
                            (work.Diagnostic ? diagnostic != work : !enabled || session != work.Session); }
                        // Switching output/disabling can race the old player's
                        // disposal. Its failure must not erase the same pending
                        // clipboard which is about to use the new output.
                        DiagnosticLog.Error(work.Diagnostic ? "client.audio.probe.transmission_failed" : "client.audio.transmission_failed", error,
                            "session", work.Session, "audioTransfer", work.Id, "probe", work.Diagnostic ? work.Id : Guid.Empty,
                            "modem", AudioModemCatalog.GetId(modem), "endpoint", work.OutputId, "priority", work.Priority, "obsolete", obsolete);
                        if (!obsolete) { failed = true; Report(error); }
                    }
                    finally
                    {
                        lock (gate)
                        {
                            if (playback == cancel) { playback = null; active = null; }
                            if (failed)
                            {
                                if (priority == work) priority = null;
                                if (clipboard == work) clipboard = null;
                                if (diagnostic == work) diagnostic = null;
                                if (fastReceipt == work) fastReceipt = null;
                            }
                        }
                        cancel.Dispose();
                        ReleaseUnusedPlayer();
                    }
                }
            }
            finally { wake.Dispose(); }
        }

        private void CancelPlayback(bool clipboardOnly)
        {
            if (playback != null && (!clipboardOnly || (active != null && !active.Priority))) playback.Cancel();
        }

        private void CancelDiagnosticLocked(string reason)
        {
            Transmission previous = diagnostic;
            if (previous == null) return;
            diagnostic = null; previous.CancelReason = reason;
            LogTransmission("client.audio.cancelled", previous);
            if (active == previous) CancelPlayback(false);
        }

        private void PlayTransferStatus(Transmission work, AudioPlayer output, string[] outputs, int nextIndex, AudioTransferPhase phase, CancellationToken token, bool force)
        {
            AudioTransferStatus status;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (disposed || routeCalibration || !enabled || session != work.Session || clipboard != work) return;
                long now = Stopwatch.GetTimestamp();
                if (!force && work.LastStatusAt != 0 && (now - work.LastStatusAt) * 1000 / Stopwatch.Frequency < 3000) return;
                status = new AudioTransferStatus { Session = work.Session, Transfer = work.Id, Sequence = ++work.StatusSequence,
                    SentBytes = Math.Min(work.Bytes.Length, nextIndex * (long)AudioModem.MaxPayloadBytes), TotalBytes = work.Bytes.Length,
                    Phase = phase, PassIndex = work.CompletedPasses };
            }
            byte[] wire = AudioTransferStatusProtocol.Encode(status);
            AudioEncodedBlock block = services.EncodeAudio(AudioModemKind.Fsk, AudioTransferStatusProtocol.TransferId(status), 0, wire);
            if (block == null || block.Samples == null || block.Packets != 1) throw new InvalidOperationException("Invalid audio transfer status frame.");
            token.ThrowIfCancellationRequested();
            if (outputs != null && outputs.Length > 1) ParallelAudioWave.Broadcast(services, output, work.OutputId, outputs, AudioTransferStatusProtocol.TransferId(status), block, token);
            else services.Play(output, block.Samples, token);
            token.ThrowIfCancellationRequested();
            lock (gate) { work.LastStatusAt = Stopwatch.GetTimestamp(); work.NeedsRouteBoundary = false; }
            DiagnosticLog.Write("client.audio.transfer_status", "session", status.Session, "audioTransfer", status.Transfer,
                "sequence", status.Sequence, "phase", status.Phase, "pass_index", status.PassIndex, "sent_bytes", status.SentBytes, "total_bytes", status.TotalBytes, "endpoint", work.OutputId);
        }

        private void ReleaseUnusedPlayer()
        {
            AudioPlayer previous; string previousOutput;
            lock (gate)
            {
                if (enabled || diagnostic != null || active != null || player == null) return;
                previous = player; previousOutput = playerOutputId;
                player = null; playerOutputId = playerOutputName = null;
            }
            try { services.DisposePlayer(previous); DiagnosticLog.Write("client.audio.output_closed", "endpoint", previousOutput, "reason", "diagnostic_finished_without_session"); }
            catch (Exception error) { DiagnosticLog.Error("client.audio.output_close_failed", error, "endpoint", previousOutput); Report(error); }
        }

        internal void Disable()
        { DisableCore(true); }

        // A camera retry ends its session but must not abort an explicit audio
        // diagnostic. Full Disable/Dispose and explicit output/modem changes do.
        internal void DisableSession()
        { DisableCore(false); }

        private void DisableCore(bool cancelDiagnostic)
        {
            debounce.Stop();
            AudioPlayer previous;
            lock (gate)
            {
                DiagnosticLog.Write("client.audio.disabled", "session", session, "endpoint", playerOutputId, "benchmarkMode", benchmarkMode);
                enabled = false;
                benchmarkMode = needsResumeBaseline = false;
                session = Guid.Empty;
                clipboardVersion++;
                pendingEncoding = null;
                clipboard = priority = fastReceipt = null;
                if (cancelDiagnostic) CancelDiagnosticLocked(disposed ? "disposed" : "disabled");
                previous = diagnostic == null ? player : null;
                if (previous != null) { player = null; playerOutputId = playerOutputName = null; }
                if (active == null || !active.Diagnostic || cancelDiagnostic) CancelPlayback(false);
            }
            if (listening)
            {
                try { services.RemoveListener(listenerHandle); } catch (Exception error) { Report(error); }
                listening = false;
                listenerHandle = IntPtr.Zero;
            }
            if (previous != null) { try { services.DisposePlayer(previous); } catch (Exception error) { Report(error); } }
            Signal();
        }

        private void Signal() { try { wake.Set(); } catch (ObjectDisposedException) { } }
        private void Report(Exception error)
        {
            // Generic sender failures may originate in clipboard encoders. Do
            // not persist exception messages containing private file/text data.
            DiagnosticLog.Write("client.audio.sender_error", "errorType", error.GetType().FullName, "hresult", error.HResult);
            Action<Exception> callback = Error; if (callback != null) { try { callback(error); } catch { } }
        }

        private static void LogTransmission(string eventName, Transmission work, int cycle = -1, int index = -1, int count = -1, int samples = -1, long playMilliseconds = -1)
        {
            if (!DiagnosticLog.IsEnabled) return;
            if (work.CompactFileReport)
            {
                DiagnosticLog.Write(eventName, "session", work.Session, "audioTransfer", work.Id, "modem", "fsk1200-compact-file-report",
                    "wireBytes", work.Bytes.Length, "cycle", cycle, "samples", samples, "nominalAudioMs", samples < 0 ? -1 : samples * 1000L / AudioModem.SampleRate, "playMs", playMilliseconds);
                return;
            }
            if (work.Receipt)
            {
                DiagnosticLog.Write(eventName, "session", work.Session, "audioTransfer", work.Id,
                    "transfer", work.ReceiptTransfer, "encoded_bytes", work.ReceiptBytes,
                    "modem", work.FastReceipt ? "fsk1200-fast-receipt" : AudioModemCatalog.GetId(work.Modem), "receipt_fallback", work.NativeReceipt,
                    "wireBytes", work.Bytes.Length, "cycle", cycle, "samples", samples, "nominalAudioMs", samples < 0 ? -1 : samples * 1000L / AudioModem.SampleRate,
                    "playMs", playMilliseconds, "queued_to_start_ms", work.QueueDelayMilliseconds,
                    "receipt_age_ms", (long)((Stopwatch.GetTimestamp() - work.QueuedAt) * (1000.0 / Stopwatch.Frequency)));
                return;
            }
            if (work.Diagnostic)
            {
                string suffix = eventName.StartsWith("client.audio.", StringComparison.Ordinal) ? eventName.Substring("client.audio.".Length) : eventName;
                DiagnosticLog.Write("client.audio.probe." + suffix, "probe", work.Id, "modem", AudioModemCatalog.GetId(work.Modem), "endpoint", work.OutputId,
                    "wireBytes", work.Bytes.Length, "cycle", cycle, "packetIndex", index, "packets", count,
                    "samples", samples, "playMs", playMilliseconds, "reason", work.CancelReason);
                return;
            }
            FileBenchmarkReport report = null;
            bool fileReport = work.Priority && FileBenchmarkProtocol.TryReport(work.Bytes, work.Session, out report);
            if (fileReport)
            {
                // Parse only bounded control metadata; waveform/payload bytes
                // and ordinary clipboard contents never enter the log.
                DiagnosticLog.Write(eventName, "session", work.Session, "audioTransfer", work.Id, "modem", AudioModemCatalog.GetId(work.Modem), "priority", true, "wireBytes", work.Bytes.Length,
                    "test", report.Test, "profile", report.Profile, "status", report.Status, "failure", report.Failure,
                    "received", report.ReceivedBytes, "total", report.TotalBytes, "elapsedMs", report.ElapsedMilliseconds,
                    "cycle", cycle, "packetIndex", index, "packets", count, "samples", samples, "nominalAudioMs", samples < 0 ? -1 : samples * 1000L / AudioModem.SampleRate,
                    "playMs", playMilliseconds);
            }
            else DiagnosticLog.Write(eventName, "session", work.Session, "audioTransfer", work.Id, "modem", AudioModemCatalog.GetId(work.Modem), "priority", work.Priority,
                "wireBytes", work.Bytes.Length, "cycle", cycle, "packetIndex", index, "packets", count,
                "samples", samples, "playMs", playMilliseconds, "completed_passes", work.CompletedPasses, "maximum_passes", work.MaximumPasses, "next_index", work.NextIndex);
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Disable();
            debounce.Dispose();
            lock (gate) { if (!senderStarted) wake.Dispose(); }
        }

        private sealed class EncodeWork
        {
            internal ClipboardSnapshot Snapshot;
            internal Guid Session;
            internal long Version;
            internal uint Sequence;
        }

        private sealed class Transmission
        {
            internal byte[] Bytes;
            internal Guid Id, Session;
            internal long Version;
            internal long AckDeadline;
            internal long LastStatusAt;
            internal uint StatusSequence;
            internal bool NeedsRouteBoundary;
            internal int CompletedPasses, NextIndex, MaximumPasses = 3;
            internal bool Priority;
            internal bool Diagnostic;
            internal bool FastReceipt;
            internal bool NativeReceipt;
            internal Guid ReceiptTransfer;
            internal int ReceiptBytes;
            internal long QueuedAt;
            internal long QueueDelayMilliseconds = -1;
            internal bool Receipt { get { return FastReceipt || NativeReceipt; } }
            internal bool CompactFileReport;
            internal bool FixedFsk { get { return FastReceipt || CompactFileReport; } }
            internal string OutputId, CancelReason;
            internal AudioModemKind Modem;
        }
    }

    internal sealed class AudioSenderServices
    {
        internal Action<AudioModemKind> ValidateModem = AudioTransmissionEncoder.Validate;
        internal Func<AudioModemKind, Guid, int, byte[], AudioEncodedBlock> EncodeAudio = AudioTransmissionEncoder.Encode;
        internal Func<byte[], AudioEncodedBlock> EncodeFastReceipt = AudioTransmissionEncoder.EncodeFastReceipt;
        internal Func<byte[], AudioEncodedBlock> EncodeCompactFileReport = AudioTransmissionEncoder.EncodeCompactFileReport;
        internal int AcknowledgementGraceMilliseconds = 5000;
        internal bool EnableTransferStatus;
        internal Func<AudioEndpoint[]> Outputs = AudioPlayer.EnumerateEndpoints;
        internal Func<string, AudioPlayer> CreatePlayer = delegate(string id) { return new AudioPlayer(id); };
        internal Action<AudioPlayer, float[], CancellationToken> Play = delegate(AudioPlayer player, float[] samples, CancellationToken token) { player.Play(samples, token); };
        internal Action<AudioPlayer> DisposePlayer = delegate(AudioPlayer player) { player.Dispose(); };
        internal Func<IntPtr, bool> AddListener = AddClipboardFormatListener;
        internal Func<IntPtr, bool> RemoveListener = RemoveClipboardFormatListener;
        internal Func<uint> Sequence = GetClipboardSequenceNumber;
        internal Func<ClipboardSnapshot> Capture = ClipboardPayload.Capture;
        internal Func<ClipboardSnapshot, byte[]> Encode = delegate(ClipboardSnapshot snapshot) { string ignored; return ClipboardPayload.Encode(snapshot, out ignored); };

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
    }
}
