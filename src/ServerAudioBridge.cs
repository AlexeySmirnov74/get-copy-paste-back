using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HorizonClient
{
    internal sealed class ServerAudioBridge : IDisposable
    {
        internal const string AllOutputsId = "all_render";
        private readonly Control owner;
        private readonly Guid session;
        private readonly Action<Guid> published;
        private readonly Action<string> status;
        private readonly Action<BridgeReport> report;
        private readonly ServerAudioBridgeServices services;
        private readonly string receivedRoot;
        private readonly object gate = new object();
        private readonly HashSet<Guid> retired = new HashSet<Guid>();
        private readonly Queue<Guid> retiredOrder = new Queue<Guid>();
        private readonly List<CaptureRoute> captures = new List<CaptureRoute>();
        private readonly HashSet<Guid> acceptedProbes = new HashSet<Guid>();
        private readonly Queue<Guid> acceptedProbeOrder = new Queue<Guid>();
        internal const int MaximumRememberedProbes = 512;
        private IServerDeferredPaste paste;
        private IServerPublicationTimer timer;
        private Transfer current;
        private EarlyTransfer early;
        internal const int MaximumEarlyPackets = 256, MaximumEarlyBytes = 64 * 1024, EarlyPacketLifetimeMilliseconds = 5000;
        private volatile bool disposed;
        private bool started, captureFaulted, openingCaptures, benchmarkMode;
        private int additionalRetries = 2;
        private Guid activityTransfer;
        private uint activitySequence;
        private int activityPass;
        private long activitySent, activityTotal;
        internal int AdditionalRetries
        {
            get { lock (gate) return additionalRetries; }
            set
            {
                if (value < 0 || value > 10) throw new ArgumentOutOfRangeException("value");
                lock (gate)
                {
                    additionalRetries = value;
                    if (current != null && !current.Published && !current.Failed) current.MaximumRetries = value;
                }
            }
        }
        internal Action<Guid, int> IncomingStarted { get; set; }
        internal Action<Guid, int, int, long> IncomingProgress { get; set; }
        internal Action<Guid, int, long> IncomingCompleted { get; set; }
        internal Action<Guid, int, int> IncomingRetrying { get; set; }
        internal Action<Guid, int, int> IncomingResumed { get; set; }
        internal Action<Guid, string> IncomingFailed { get; set; }
        internal Action<FileBenchmarkReport> FileReportReceived { get; set; }
        internal Action<DeliveryReceipt> DeliveryReceived { get; set; }
        internal Action<byte[]> FastDeliveryReceived { get; set; }
        internal Action<byte[]> CompactFileReportReceived { get; set; }
        internal Action<AudioDiagnosticProbe> AudioProbeReceived { get; set; }
        internal Action<AudioTransferStatus, int> IncomingActivityReceived { get; set; }

        // Called on the owner's STA. Control reports remain enabled while any
        // already queued reverse clipboard publication is retired.
        internal void SetBenchmarkMode(bool value)
        {
            lock (gate)
            {
                if (disposed || benchmarkMode == value) return;
                benchmarkMode = value;
                DiagnosticLog.Write("server.audio.clipboard_mode", "session", session, "paused", value, "retiredTransfer", current == null ? Guid.Empty : current.Id);
                if (!value) return;
                ClearEarlyLocked(true);
                if (current != null) RememberRetired(current.Id);
                current = null;
                if (timer != null) timer.Stop();
                if (paste != null) { try { paste.Cancel(); } catch { } }
            }
        }

        internal ServerAudioBridge(Control owner, Guid session, Action<Guid> published, Action<string> status, Action<BridgeReport> report)
            : this(owner, session, published, status, report, new ServerAudioBridgeServices { NativeModemsEnabled = true }) { }

        internal ServerAudioBridge(Control owner, Guid session, Action<Guid> published, Action<string> status, Action<BridgeReport> report, ServerAudioBridgeServices services)
        {
            if (owner == null || services == null || published == null || status == null || report == null) throw new ArgumentNullException();
            if (session == Guid.Empty) throw new ArgumentException("A bridge session is required.", "session");
            this.owner = owner; this.session = session; this.published = published; this.status = status; this.report = report; this.services = services;
            receivedRoot = ServerPaths.Current.ReceivedRoot;
        }

        internal bool IsReceiving { get { lock (gate) { return !disposed && current != null && !current.Published && !current.Failed && !current.WaitingForPackets; } } }
        internal int EarlyPacketCount { get { lock (gate) { ExpireEarlyLocked(); return early == null ? 0 : early.Packets.Count; } } }
        internal int EarlyPacketBytes { get { lock (gate) { ExpireEarlyLocked(); return early == null ? 0 : early.Bytes; } } }

        internal void Start(string outputEndpointId)
        { StartCore(outputEndpointId, null); }

        internal void StartWithRoutes(string[] endpointIds)
        {
            if (endpointIds == null || endpointIds.Length > 16) throw new ArgumentOutOfRangeException("endpointIds");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in endpointIds) if (String.IsNullOrEmpty(id) || !unique.Add(id)) throw new ArgumentException("Verified routes must be explicit and distinct.");
            StartCore(null, (string[])endpointIds.Clone());
        }

        private void StartCore(string outputEndpointId, string[] verifiedEndpoints)
        {
            bool allOutputs = String.Equals(outputEndpointId, AllOutputsId, StringComparison.Ordinal);
            string captureMode = verifiedEndpoints != null ? "verified" : allOutputs ? "all_render" : outputEndpointId == null ? "default_render" : "explicit";
            lock (gate)
            {
                if (disposed || started) return;
                started = true;
                openingCaptures = true;
                DiagnosticLog.Write("server.audio.capture_requested", "session", session, "endpoint", outputEndpointId,
                    "mode", captureMode);
                timer = services.CreateTimer(Publish);
                try { paste = services.CreatePaste(owner, delegate { return IsReceiving; }); } catch { }
            }
            services.Queue(delegate
            {
                lock (gate) { if (disposed) return; }
                List<string> endpoints = new List<string>();
                if (verifiedEndpoints != null) endpoints.AddRange(verifiedEndpoints);
                else if (allOutputs)
                {
                    try
                    {
                        var unique = new HashSet<string>(StringComparer.Ordinal);
                        foreach (AudioEndpoint endpoint in services.EnumerateRenderEndpoints() ?? new AudioEndpoint[0])
                            if (endpoint != null && !String.IsNullOrEmpty(endpoint.Id) && endpoint.Id != AllOutputsId && unique.Add(endpoint.Id)) endpoints.Add(endpoint.Id);
                    }
                    catch (Exception error)
                    {
                        lock (gate) { openingCaptures = false; }
                        DiagnosticLog.Error("server.audio.enumerate_failed", error, "session", session, "mode", captureMode);
                        CaptureFailed(error);
                        return;
                    }
                }
                else if (outputEndpointId != null) endpoints.Add(outputEndpointId);
                else endpoints.Add(null);
                DiagnosticLog.Write("server.audio.routes_selected", "session", session, "count", endpoints.Count,
                    "mode", captureMode);
                if (verifiedEndpoints != null && verifiedEndpoints.Length == 0)
                {
                    lock (gate) { openingCaptures = false; }
                    Ui(delegate { Status(UiText.Get("Все проверенные обратные аудиоканалы отключены пользователем.", "All verified return audio channels were disabled by the user.")); });
                    return;
                }
                foreach (string endpoint in endpoints)
                {
                    lock (gate) { if (disposed) return; }
                    CaptureRoute route = new CaptureRoute(endpoint, services.CreateReceiver(delegate(Packet packet)
                    {
                        DiagnosticLog.Write("server.audio.route_packet_decoded", "session", session, "endpoint", endpoint,
                            "audioTransfer", packet == null ? Guid.Empty : packet.TransferId, "index", packet == null ? -1 : packet.Index);
                        ReceivePacket(packet, endpoint);
                    }, ReceiveFastReceipt, ReceiveCompactFileReport, endpoint));
                    bool obsolete;
                    lock (gate)
                    {
                        obsolete = disposed;
                        if (!obsolete) captures.Add(route);
                    }
                    if (obsolete) { route.Decoder.Dispose(); return; }
                    // Workers start in the mux constructor. Subscribe first, then
                    // replay snapshots; route/kind de-duplication handles overlap.
                    route.Decoder.DecoderFailed += delegate(AudioModemKind kind, Exception error) { NativeDecoderFailed(route, kind, error); };
                    foreach (AudioModemKind kind in new[] { AudioModemKind.Quiet, AudioModemKind.Aicodix })
                    {
                        Exception error = route.Decoder.GetDecoderFailure(kind);
                        if (error != null) NativeDecoderFailed(route, kind, error);
                    }
                    IDisposable created = null;
                    try
                    {
                        created = services.StartCapture(endpoint, delegate(float[] values)
                        {
                            if (disposed || route.Faulted) return;
                            ObserveWaveform(values, route); route.Decoder.Feed(values);
                        }, delegate(Exception error) { CaptureRouteFailed(route, error); });
                        DiagnosticLog.Write("server.audio.capture_started", "session", session, "endpoint", endpoint);
                        lock (gate)
                        {
                            if (!disposed) { route.Capture = created; created = null; }
                        }
                        if (created != null) { created.Dispose(); DiagnosticLog.Write("server.audio.capture_stopped", "session", session, "endpoint", endpoint, "reason", "start_obsolete"); return; }
                    }
                    catch (Exception error)
                    {
                        DiagnosticLog.Error("server.audio.capture_start_failed", error, "session", session, "endpoint", endpoint);
                        if (created != null) { try { created.Dispose(); } catch { } }
                        CaptureRouteFailed(route, error);
                    }
                }
                Exception failure = null;
                int healthy = 0;
                lock (gate)
                {
                    openingCaptures = false;
                    if (disposed) return;
                    foreach (CaptureRoute route in captures)
                        if (!route.Faulted) healthy++; else if (failure == null) failure = route.Error;
                }
                if (healthy == 0)
                {
                    CaptureFailed(failure ?? new InvalidOperationException("No audio output could be captured."));
                    return;
                }
                int routeCount = healthy;
                Ui(delegate
                {
                    lock (gate)
                    {
                        if (!captureFaulted) Status(allOutputs ? UiText.Format("Приём обратного звука со всех аудиовыходов: {0}.", "Receiving return audio from all audio outputs: {0}.", routeCount) : verifiedEndpoints != null ? UiText.Format("Приём обратного звука: выбранных устройств — {0}.", "Receiving return audio: selected devices — {0}.", routeCount) : outputEndpointId == null
                            ? UiText.Get("Приём обратного звука включён для устройства Windows по умолчанию.", "Return audio reception is enabled for the default Windows device.")
                            : UiText.Get("Приём обратного звука включён для выбранного устройства.", "Return audio reception is enabled for the selected device."));
                    }
                });
            });
        }

        private void CaptureRouteFailed(CaptureRoute route, Exception error)
        {
            bool allFailed = true, opening;
            lock (gate)
            {
                if (disposed) return;
                route.Faulted = true; route.Error = error;
                opening = openingCaptures;
                if (openingCaptures) allFailed = false;
                else foreach (CaptureRoute available in captures) if (!available.Faulted) { allFailed = false; break; }
            }
            DiagnosticLog.Error("server.audio.route_failed", error, "session", session, "endpoint", route.Endpoint, "all_routes_failed", allFailed);
            if (allFailed) CaptureFailed(error);
            else if (!opening) Ui(delegate { lock (gate) { if (!captureFaulted) Status(UiText.Get("Приём обратного звука продолжается на доступных устройствах.", "Return audio reception continues on available devices.")); } });
        }

        private void NativeDecoderFailed(CaptureRoute route, AudioModemKind kind, Exception error)
        {
            lock (gate)
            {
                if (disposed || route.Faulted || !captures.Contains(route) || route.DecoderErrors.ContainsKey(kind)) return;
                route.DecoderErrors.Add(kind, error);
            }
            DiagnosticLog.Error("server.audio.route_modem_failed", error, "session", session, "endpoint", route.Endpoint, "modem", AudioModemCatalog.GetId(kind));
            Ui(delegate { lock (gate) { if (!captureFaulted && !route.Faulted) Status(UiText.Get("Приём обратного звука работает с ограничениями.", "Return audio reception is degraded.")); } });
        }

        // Called under gate. Capture health and decoder availability are separate:
        // one failed worker never faults a healthy WASAPI/FSK route.
        private object NativeAvailabilityLocked()
        {
            object details = null;
            foreach (AudioModemKind kind in new[] { AudioModemKind.Quiet, AudioModemKind.Aicodix })
            {
                int healthy = 0, failed = 0; object endpoints = null;
                foreach (CaptureRoute route in captures)
                {
                    if (route.Faulted) continue;
                    healthy++;
                    if (!route.DecoderErrors.ContainsKey(kind)) continue;
                    failed++;
                    // Explicit endpoint identities are literal data, even when
                    // their text happens to match a registered UI caption.
                    object endpoint = route.Endpoint == null ? UiText.Value("устройство Windows по умолчанию", "default Windows device") : (object)route.Endpoint;
                    endpoints = endpoints == null ? endpoint : UiText.KnownValue(UiText.Format("{0}, {1}", "{0}, {1}", endpoints, endpoint));
                }
                if (failed == 0) continue;
                object availability = failed == healthy ? UiText.Value("недоступен", "unavailable") : UiText.Value("доступен частично", "partially available");
                object detail = UiText.KnownValue(UiText.Format("{0}: {1} ({2})", "{0}: {1} ({2})", AudioModemCatalog.GetId(kind), availability, endpoints));
                details = details == null ? detail : UiText.KnownValue(UiText.Format("{0}; {1}", "{0}; {1}", details, detail));
            }
            return details == null ? null : UiText.KnownValue(UiText.Format(" Ограничения декодеров: {0}. FSK остаётся доступен.", " Decoder availability: {0}. FSK remains available.", details));
        }

        // The audio decoder and synthetic coordination tests use this same path.
        // Every transfer identity/state access is synchronized; Guid is not atomic.
        internal void ReceiveFastReceipt(byte[] token)
        {
            if (disposed || token == null || token.Length != DeliveryReceipt.FastTokenLength) return;
            byte[] copy = (byte[])token.Clone();
            DiagnosticLog.Write("server.audio.delivery_fast_received", "session", session, "token_bytes", copy.Length,
                "note", "Owner must match the active transfer, generation and clipboard sequence before confirmation.");
            Ui(delegate
            {
                Action<byte[]> handler = FastDeliveryReceived;
                if (handler != null)
                {
                    try { handler(copy); }
                    catch (Exception error) { DiagnosticLog.Error("server.audio.delivery_fast_handler_failed", error, "session", session); }
                }
            });
        }

        internal void ReceiveCompactFileReport(byte[] wire)
        {
            if (disposed || AudioModem.ReadCompactFileReport(wire, wire == null ? 0 : wire.Length) == null) return;
            byte[] copy = (byte[])wire.Clone();
            DiagnosticLog.Write("server.audio.compact_file_report_received", "session", session, "wire_bytes", copy.Length,
                "note", "Owner must bind the token to its exact active command before accepting counters or retransmission requests.");
            Ui(delegate
            {
                if (disposed) return;
                Action<byte[]> handler = CompactFileReportReceived;
                DiagnosticLog.Write("server.audio.compact_file_report_dispatched", "session", session, "wire_bytes", copy.Length, "handlerPresent", handler != null);
                if (handler != null) try { handler(copy); } catch (Exception error) { DiagnosticLog.Error("server.audio.compact_file_report_handler_failed", error, "session", session); }
            });
        }

        internal void ReceivePacket(Packet packet)
        { ReceivePacket(packet, null); }

        private void ReceivePacket(Packet packet, string endpoint)
        {
            if (disposed || packet == null || packet.TransferId == Guid.Empty ||
                !VisualProtocol.ValidMetadata(packet.Index, packet.Count, packet.TotalLength, packet.Data, packet.Profile, packet.ChunkSize)) return;
            DiagnosticLog.Write("server.audio.packet_decoded", "session", session, "audioTransfer", packet.TransferId, "index", packet.Index,
                "count", packet.Count, "totalBytes", packet.TotalLength, "chunkBytes", packet.Data.Length, "profile", packet.Profile);
            BridgeReport benchmark;
            FileBenchmarkReport fileReport;
            DeliveryReceipt delivery;
            AudioDiagnosticProbe probe;
            AudioTransferStatus activity;
            if (packet.Count == 1 && packet.Index == 0 && AudioTransferStatusProtocol.TryDecode(packet.Data, packet.TransferId, out activity))
            {
                ObserveTransferActivity(activity);
                return;
            }
            if (packet.Count == 1 && packet.Index == 0 && AudioDiagnosticProbe.TryDecode(packet.Data, out probe))
            {
                if (probe.Id != packet.TransferId)
                {
                    DiagnosticLog.Write("server.audio.probe_rejected", "reason", "outer_transfer_mismatch", "probe", probe.Id, "audioTransfer", packet.TransferId);
                    return;
                }
                lock (gate)
                {
                    if (disposed || captureFaulted) return;
                    if (!acceptedProbes.Add(probe.Id))
                    { DiagnosticLog.Write("server.audio.probe_duplicate_ignored", "session", session, "probe", probe.Id); return; }
                    acceptedProbeOrder.Enqueue(probe.Id);
                    while (acceptedProbeOrder.Count > MaximumRememberedProbes) acceptedProbes.Remove(acceptedProbeOrder.Dequeue());
                }
                DiagnosticLog.Write("server.audio.probe_received", "session", session, "probe", probe.Id,
                    "declared_modem", AudioModemCatalog.GetId(probe.Modem), "sent_utc_ticks", probe.SentUtcTicks,
                    "clock_difference_ms", (services.UtcNow.Ticks - probe.SentUtcTicks) / (double)TimeSpan.TicksPerMillisecond,
                    "note", "Independent clocks; timestamp is diagnostic only. One callback per remembered probe ID; no clipboard access.");
                Ui(delegate
                {
                    lock (gate) { if (disposed || captureFaulted) return; }
                    Action<AudioDiagnosticProbe> handler = AudioProbeReceived;
                    DiagnosticLog.Write("server.audio.probe_dispatched", "session", session, "probe", probe.Id, "modem", AudioModemCatalog.GetId(probe.Modem), "handler_present", handler != null);
                    Status(UiText.Format("Проверочный звук получен: {0}. Обратный аудиоканал работает.", "Test audio received: {0}. The return audio channel is working.", AudioModemCatalog.GetId(probe.Modem)));
                    if (handler != null) try { handler(probe); } catch (Exception error) { DiagnosticLog.Error("server.audio.probe_handler_failed", error, "session", session, "probe", probe.Id); }
                });
                return;
            }
            if (packet.Count == 1 && packet.Index == 0 && FileBenchmarkProtocol.TryReport(packet.Data, session, out fileReport))
            {
                DiagnosticLog.Write("server.audio.file_report_received", "session", session, "audioTransfer", packet.TransferId, "test", fileReport.Test,
                    "profile", fileReport.Profile, "status", fileReport.Status, "failure", fileReport.Failure, "received", fileReport.ReceivedBytes,
                    "total", fileReport.TotalBytes, "elapsedMs", fileReport.ElapsedMilliseconds, "width", fileReport.FrameWidth, "height", fileReport.FrameHeight, "frames", fileReport.Frames);
                Ui(delegate
                {
                    Action<FileBenchmarkReport> handler = FileReportReceived;
                    DiagnosticLog.Write("server.audio.file_report_dispatched", "session", session, "test", fileReport.Test, "status", fileReport.Status, "handlerPresent", handler != null);
                    if (handler != null) { try { handler(fileReport); } catch (Exception error) { DiagnosticLog.Error("server.audio.file_report_handler_failed", error, "test", fileReport.Test); } }
                });
                return;
            }
            if (packet.Count == 1 && packet.Index == 0 && DeliveryReceipt.TryDecode(packet.Data, session, out delivery))
            {
                DiagnosticLog.Write("server.audio.delivery_received", "session", session, "audioTransfer", packet.TransferId,
                    "transfer", delivery.Transfer, "encodedBytes", delivery.EncodedBytes);
                Ui(delegate
                {
                    Action<DeliveryReceipt> handler = DeliveryReceived;
                    DiagnosticLog.Write("server.audio.delivery_dispatched", "session", session, "transfer", delivery.Transfer,
                        "encodedBytes", delivery.EncodedBytes, "handlerPresent", handler != null);
                    if (handler != null)
                    {
                        try { handler(delivery); }
                        catch (Exception error) { DiagnosticLog.Error("server.audio.delivery_handler_failed", error, "transfer", delivery.Transfer); }
                    }
                });
                return;
            }
            if (packet.Count == 1 && packet.Index == 0 && BridgeControl.TryReport(packet.Data, session, out benchmark))
            {
                DiagnosticLog.Write("server.audio.legacy_report_received", "session", session, "audioTransfer", packet.TransferId, "test", benchmark.Test,
                    "videoBytes", benchmark.VideoBytes, "audioBytes", benchmark.AudioBytes, "elapsedMs", benchmark.Milliseconds);
                Ui(delegate { try { report(benchmark); } catch { } });
                return;
            }
            Transfer transfer = null;
            try
            {
                byte[] completed;
                List<Packet> earlyPackets = null;
                int progressBytes = 0;
                bool began = false, resumed = false;
                lock (gate)
                {
                    if (disposed || captureFaulted || benchmarkMode || retired.Contains(packet.TransferId)) return;
                    ExpireEarlyLocked();
                    if (current == null || current.Id != packet.TransferId)
                    {
                        byte[] first;
                        if (packet.Index != 0) { BufferEarlyLocked(packet); return; }
                        if (!BridgeControl.TryClipboard(packet.Data, session, out first))
                        {
                            if (early != null && early.Id == packet.TransferId) ClearEarlyLocked(false);
                            RememberRetired(packet.TransferId); return;
                        }
                        earlyPackets = TakeEarlyLocked(packet);
                        if (current != null) RememberRetired(current.Id);
                        current = new Transfer(packet.TransferId, packet.TotalLength, services.UtcNow, additionalRetries);
                        began = true;
                    }
                    transfer = current;
                    if (transfer.Assembled || transfer.Published || transfer.Failed) return;
                    transfer.LastActivity = services.UtcNow;
                    int previousCount = transfer.Assembler.ReceivedCount;
                    completed = transfer.Assembler.Add(packet);
                    int addedBytes = transfer.Assembler.ReceivedCount > previousCount ? packet.Data.Length : 0;
                    if (earlyPackets != null)
                    {
                        foreach (Packet buffered in earlyPackets)
                        {
                            int before = transfer.Assembler.ReceivedCount; byte[] assembled = transfer.Assembler.Add(buffered);
                            if (transfer.Assembler.ReceivedCount > before) addedBytes += buffered.Data.Length;
                            if (assembled != null) completed = assembled;
                        }
                        DiagnosticLog.Write("server.audio.early_packets_drained", "session", session, "transfer", transfer.Id, "packets", earlyPackets.Count, "received_bytes", transfer.ReceivedBytes + addedBytes);
                    }
                    if (addedBytes > 0)
                    {
                        resumed = transfer.WaitingForPackets;
                        transfer.WaitingForPackets = false;
                        transfer.ReceivedBytes += addedBytes;
                        if (resumed || (services.UtcNow - transfer.LastProgress).TotalSeconds >= 1)
                        {
                            transfer.LastProgress = services.UtcNow;
                            progressBytes = transfer.ReceivedBytes;
                        }
                    }
                    if (completed != null) transfer.Assembled = true;
                }
                if (began)
                {
                    DiagnosticLog.Write("server.audio.transfer.started", "session", session, "transfer", transfer.Id, "encoded_bytes", transfer.TotalBytes, "additional_retries", transfer.MaximumRetries);
                    UiFor(transfer, delegate {
                        if (transfer.Published || transfer.Failed || transfer.WaitingForPackets) return;
                        NotifyStarted(transfer); if (timer != null) timer.Start();
                    });
                }
                if (resumed)
                {
                    DiagnosticLog.Write("server.audio.transfer.resumed", "session", session, "transfer", transfer.Id,
                        "received_bytes", transfer.ReceivedBytes, "total_bytes", transfer.TotalBytes);
                    UiFor(transfer, delegate {
                        if (transfer.Published || transfer.Failed || transfer.WaitingForPackets) return;
                        if (timer != null) timer.Start();
                        NotifyIncoming(delegate { if (IncomingResumed != null) IncomingResumed(transfer.Id, transfer.ReceivedBytes, transfer.TotalBytes); });
                    });
                }
                if (progressBytes > 0)
                {
                    int uniqueBytes = progressBytes;
                    UiFor(transfer, delegate
                    {
                        if (!transfer.Published && !transfer.Failed && !transfer.WaitingForPackets)
                        {
                            NotifyStarted(transfer);
                            Status(UiText.Format("Приём из Horizon по звуку: {0:N0} из {1:N0} байт.", "Receiving audio data from Horizon: {0:N0} of {1:N0} bytes.", uniqueBytes, packet.TotalLength));
                            NotifyIncoming(delegate { if (IncomingProgress != null) IncomingProgress(transfer.Id, uniqueBytes, transfer.TotalBytes, Elapsed(transfer)); });
                        }
                    });
                }
                if (completed == null) return;
                byte[] encoded;
                if (!BridgeControl.TryClipboard(completed, session, out encoded)) throw new InvalidDataException(UiText.Get("Неверный сеанс обратного буфера.", "Invalid return clipboard session."));
                DecodedPayload payload = services.Decode(encoded);
                lock (gate) { if (!IsCurrent(transfer)) return; }
                if (payload.IsFileDrop) services.PrepareFiles(payload, receivedRoot, transfer.Id);
                UiFor(transfer, delegate { if (transfer.Failed || transfer.Published) return; transfer.Pending = payload; transfer.Attempts = 0; Publish(); });
            }
            catch (Exception error) { DiagnosticLog.Error("server.audio.transfer.decode_failed", error, "session", session, "transfer", transfer == null ? Guid.Empty : transfer.Id); Fail(transfer, UiText.Format("Данные из Horizon не приняты: {0}", "Data from Horizon could not be received: {0}", error.Message), true); }
        }

        private bool IsCurrent(Transfer transfer) { return !disposed && transfer != null && Object.ReferenceEquals(current, transfer); }
        private void ExpireEarlyLocked()
        {
            if (early != null && (services.UtcNow - early.Created).TotalMilliseconds >= EarlyPacketLifetimeMilliseconds) ClearEarlyLocked(false);
        }
        private void ClearEarlyLocked(bool retire)
        {
            if (early == null) return;
            Guid id = early.Id; early = null;
            if (retire) RememberRetired(id);
        }
        private void BufferEarlyLocked(Packet packet)
        {
            // Packets have already passed the decoder CRC and metadata checks,
            // but no session is trusted until packet zero carries our envelope.
            if (packet.Index <= 0 || packet.Data.Length > MaximumEarlyBytes) return;
            if (early == null) early = new EarlyTransfer(packet, services.UtcNow);
            if (!early.Matches(packet) || early.Packets.ContainsKey(packet.Index) || early.Packets.Count >= MaximumEarlyPackets || early.Bytes > MaximumEarlyBytes - packet.Data.Length) return;
            var copy = new Packet { TransferId = packet.TransferId, Index = packet.Index, Count = packet.Count, TotalLength = packet.TotalLength,
                Profile = packet.Profile, ChunkSize = VisualProtocol.GetChunkSize(packet.Profile), Data = (byte[])packet.Data.Clone() };
            early.Packets.Add(copy.Index, copy); early.Bytes += copy.Data.Length;
            DiagnosticLog.Write("server.audio.early_packet_buffered", "session", session, "transfer", copy.TransferId, "index", copy.Index, "buffered_packets", early.Packets.Count, "buffered_bytes", early.Bytes);
        }
        private List<Packet> TakeEarlyLocked(Packet first)
        {
            if (early == null) return null;
            // A later copy's route can arrive before the earlier copy's header.
            // The unknown ID has no ordering/session proof yet; retain its one
            // bounded TTL slot rather than retiring a potentially newer copy.
            if (early.Id != first.TransferId) return null;
            var saved = early; early = null;
            if (!saved.Matches(first)) return null;
            var packets = new List<Packet>(saved.Packets.Values); packets.Sort(delegate(Packet a, Packet b) { return a.Index.CompareTo(b.Index); }); return packets;
        }
        private void ObserveTransferActivity(AudioTransferStatus activity)
        {
            lock (gate)
            {
                if (disposed || captureFaulted || benchmarkMode || activity.Session != session || retired.Contains(activity.Transfer)) return;
                if (current != null && current.Id == activity.Transfer && (current.Published || current.Failed)) return;
                if (current != null && !current.Published && !current.Failed && current.Id != activity.Transfer) return;
                if (activityTransfer == activity.Transfer && (activity.Sequence <= activitySequence || activity.TotalBytes != activityTotal ||
                    activity.PassIndex < activityPass || activity.PassIndex == activityPass && activity.SentBytes < activitySent)) return;
                if (current != null && current.Id == activity.Transfer && current.TotalBytes != activity.TotalBytes) return;
                activityTransfer = activity.Transfer; activitySequence = activity.Sequence; activityPass = activity.PassIndex;
                activitySent = activity.SentBytes; activityTotal = activity.TotalBytes;
                // A heartbeat proves sender activity, never receipt of file bytes.
                // It neither creates an assembler nor resumes/publishes one.
                if (current != null && current.Id == activity.Transfer && activity.Phase != AudioTransferPhase.Cancelled)
                    current.LastActivity = services.UtcNow;
            }
            DiagnosticLog.Write("server.audio.transfer.activity", "session", session, "transfer", activity.Transfer, "sequence", activity.Sequence,
                "phase", activity.Phase, "pass", activity.PassIndex, "sender_bytes", activity.SentBytes, "total_bytes", activity.TotalBytes);
            Ui(delegate {
                int received;
                lock (gate)
                {
                    if (disposed || benchmarkMode || activityTransfer != activity.Transfer || activitySequence != activity.Sequence || retired.Contains(activity.Transfer)) return;
                    if (current != null && current.Id == activity.Transfer && (current.Published || current.Failed)) return;
                    received = current != null && current.Id == activity.Transfer ? current.ReceivedBytes : 0;
                }
                string phase = activity.Phase == AudioTransferPhase.PassComplete ? UiText.Get("Клиент завершил проход отправки", "The client completed a transmission pass") :
                    activity.Phase == AudioTransferPhase.Cancelled ? UiText.Get("Клиент прекратил отправку", "The client stopped sending") : UiText.Get("Клиент отправляет данные", "The client is sending data");
                Status(UiText.Format("{0}: {1:N0} из {2:N0} байт; сервер принял {3:N0} байт. Попытка {4}.",
                    "{0}: {1:N0} of {2:N0} bytes; the server received {3:N0} bytes. Attempt {4}.",
                    UiText.KnownValue(phase), activity.SentBytes, activity.TotalBytes, received, activity.PassIndex + 1));
                NotifyIncoming(delegate { if (IncomingActivityReceived != null) IncomingActivityReceived(activity, received); });
            });
        }
        private void RememberRetired(Guid id)
        {
            if (!retired.Add(id)) return;
            retiredOrder.Enqueue(id);
            while (retiredOrder.Count > 256) retired.Remove(retiredOrder.Dequeue());
        }

        private void Publish()
        {
            lock (gate)
            {
                Transfer transfer = current;
                if (!disposed && !benchmarkMode && transfer != null && !transfer.Assembled && !transfer.Published && !transfer.Failed)
                {
                    if (transfer.WaitingForPackets) return;
                    if ((services.UtcNow - transfer.LastActivity).TotalSeconds < 40) return;
                    // The sender may still be in its first pass of a large file.
                    // Retain the one bounded assembler so later verified chunks
                    // can continue it; only actual completion earns an ACK.
                    transfer.WaitingForPackets = true;
                    if (timer != null) timer.Stop();
                    if (paste != null) { try { paste.Cancel(); } catch { } }
                    DiagnosticLog.Write("server.audio.transfer.inactive", "session", session, "transfer", transfer.Id, "received_bytes", transfer.ReceivedBytes, "total_bytes", transfer.TotalBytes, "elapsed_ms", Elapsed(transfer), "partial_retained", true);
                    NotifyIncoming(delegate { if (IncomingFailed != null) IncomingFailed(transfer.Id, "audio_inactive_partial"); });
                    return;
                }
                if (disposed || benchmarkMode || transfer == null || transfer.Pending == null || transfer.Failed || transfer.Published) { if (timer != null) timer.Stop(); return; }
                NotifyStarted(transfer);
                try
                {
                    // The short clipboard operation and identity check are one
                    // transaction; a newer transfer cannot publish stale bytes.
                    DecodedPayload payload = transfer.Pending;
                    services.Publish(payload, receivedRoot, transfer.Id);
                    transfer.Pending = null; transfer.Published = true;
                    if (timer != null) timer.Stop();
                    // Echo suppression runs once, after successful publication
                    // and before replaying an explicitly deferred paste.
                    try { published(transfer.Id); } catch { }
                    long elapsed = Elapsed(transfer);
                    DiagnosticLog.Write("server.audio.transfer.published", "session", session, "transfer", transfer.Id, "encoded_bytes", transfer.TotalBytes,
                        "elapsed_ms", elapsed, "decode_retries", transfer.DecodeRetries, "file_drop", payload.IsFileDrop, "file_count", payload.Files.Count);
                    Action<Guid, int, long> completed = IncomingCompleted;
                    DiagnosticLog.Write("server.audio.incoming_completion_dispatched", "session", session, "transfer", transfer.Id,
                        "encoded_bytes", transfer.TotalBytes, "elapsed_ms", elapsed, "handler_present", completed != null);
                    NotifyIncoming(delegate { if (completed != null) completed(transfer.Id, transfer.TotalBytes, elapsed); });
                    if (paste != null) { try { paste.Complete(); } catch { } }
                    Status(UiText.Get("Буфер из Horizon получен. Можно вставлять обычным Ctrl+V.", "The Horizon clipboard was received. Press Ctrl+V to paste."));
                }
                catch (ExternalException)
                {
                    if (++transfer.Attempts < 12 && timer != null) timer.Start();
                    else Fail(transfer, UiText.Get("Буфер Windows занят. Скопируйте данные в Horizon ещё раз.", "The Windows clipboard is busy. Copy the data in Horizon again."));
                }
                catch (Exception error) { Fail(transfer, UiText.Format("Обратный звук: {0}", "Return audio: {0}", error.Message)); }
            }
        }

        private long Elapsed(Transfer transfer) { return Math.Max(0, (long)(services.UtcNow - transfer.Started).TotalMilliseconds); }
        private void NotifyStarted(Transfer transfer)
        {
            if (transfer.StartedNotified) return;
            transfer.StartedNotified = true;
            NotifyIncoming(delegate { if (IncomingStarted != null) IncomingStarted(transfer.Id, transfer.TotalBytes); });
        }
        private static void NotifyIncoming(Action action) { try { action(); } catch (Exception error) { DiagnosticLog.Error("server.audio.incoming_callback_failed", error); } }

        private void Fail(Transfer transfer, string message, bool allowRetry = false)
        {
            bool retrying;
            int retryNumber;
            lock (gate)
            {
                if (!IsCurrent(transfer) || transfer.Published) return;
                retrying = allowRetry && transfer.DecodeRetries < transfer.MaximumRetries;
                transfer.Pending = null;
                if (retrying)
                {
                    transfer.DecodeRetries++; transfer.Assembler = new TransferAssembler(); transfer.Assembled = false;
                    transfer.ReceivedBytes = 0; transfer.LastActivity = services.UtcNow; transfer.LastProgress = DateTime.MinValue; transfer.WaitingForPackets = false;
                    DiagnosticLog.Write("server.audio.transfer.retrying", "session", session, "transfer", transfer.Id, "retry", transfer.DecodeRetries, "maximum_retries", transfer.MaximumRetries, "reason", "payload_decode_failed");
                }
                else { transfer.Failed = true; transfer.Assembler.Reset(); }
                retryNumber = transfer.DecodeRetries;
            }
            UiFor(transfer, delegate
            {
                if (retrying)
                {
                    if (timer != null) timer.Start();
                    NotifyIncoming(delegate { if (IncomingRetrying != null) IncomingRetrying(transfer.Id, retryNumber, transfer.MaximumRetries); });
                }
                else
                {
                    if (timer != null) timer.Stop(); if (paste != null) { try { paste.Cancel(); } catch { } }
                    NotifyIncoming(delegate { if (IncomingFailed != null) IncomingFailed(transfer.Id, "decode_or_publish_failed"); });
                }
                Status(message);
            });
        }

        private void CaptureFailed(Exception error)
        {
            DiagnosticLog.Error("server.audio.capture_failed", error, "session", session);
            lock (gate)
            {
                if (disposed || captureFaulted) return;
                captureFaulted = true;
                ClearEarlyLocked(true);
                if (current != null && !current.Published) { current.Failed = true; current.Pending = null; current.Assembler.Reset(); }
            }
            Ui(delegate
            {
                lock (gate)
                {
                    if (timer != null) timer.Stop();
                    if (paste != null) { try { paste.Cancel(); } catch { } }
                    Status(UiText.Format("Обратный звук: {0}", "Return audio: {0}", error.Message));
                }
            });
        }

        private void Status(string message)
        {
            lock (gate)
            {
                if (disposed) return;
                object value = UiText.KnownValue(message);
                object availability = NativeAvailabilityLocked();
                // Keep both localized values alive across the string callback's
                // bounded UiText hand-off, rather than flattening their text.
                if (availability != null) message = UiText.Format("{0}{1}", "{0}{1}", value, availability);
            }
            try { status(message); } catch { }
        }
        private void ObserveWaveform(float[] values, CaptureRoute route)
        {
            if (!DiagnosticLog.IsEnabled || values == null) return;
            long samples = 0, ticks = Stopwatch.GetTimestamp(); int blocks = 0; float peak = 0;
            lock (route)
            {
                route.WaveformBlocks++; route.WaveformSamples += values.Length;
                for (int i = 0; i < values.Length; i++) { float absolute = Math.Abs(values[i]); if (absolute > route.WaveformPeak) route.WaveformPeak = absolute; }
                if (route.WaveformLastTicks == 0) route.WaveformLastTicks = ticks;
                if (ticks - route.WaveformLastTicks < Stopwatch.Frequency) return;
                samples = route.WaveformSamples; blocks = route.WaveformBlocks; peak = route.WaveformPeak;
                route.WaveformSamples = 0; route.WaveformBlocks = 0; route.WaveformPeak = 0; route.WaveformLastTicks = ticks;
            }
            DiagnosticLog.Write("server.audio.waveform", "session", session, "endpoint", route.Endpoint, "blocks", blocks, "samples", samples,
                "peak", peak, "nominalAudioMs", samples * 1000 / AudioModem.SampleRate);
        }
        private void UiFor(Transfer transfer, Action action)
        {
            Ui(delegate { lock (gate) { if (IsCurrent(transfer)) action(); } });
        }
        private void Ui(Action action)
        {
            if (disposed) return;
            services.Dispatch(owner, delegate { if (!disposed) action(); });
        }

        public void Dispose()
        {
            CaptureRoute[] previous;
            lock (gate)
            {
                if (disposed) return;
                disposed = true; current = null; early = null; acceptedProbes.Clear(); acceptedProbeOrder.Clear();
                DiagnosticLog.Write("server.audio.disposed", "session", session, "captureCount", captures.Count, "benchmarkMode", benchmarkMode);
                previous = captures.ToArray(); captures.Clear();
                if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
                if (paste != null) { paste.Dispose(); paste = null; }
            }
            foreach (CaptureRoute route in previous)
            {
                AudioReceiveMultiplexer decoder = route.Decoder;
                IDisposable capture = route.Capture;
                string endpoint = route.Endpoint;
                services.Queue(delegate
                {
                    try { try { decoder.Dispose(); } finally { if (capture != null) capture.Dispose(); } DiagnosticLog.Write("server.audio.capture_stopped", "session", session, "endpoint", endpoint, "reason", "disposed"); }
                    catch (Exception error) { DiagnosticLog.Error("server.audio.capture_stop_failed", error, "session", session, "endpoint", endpoint); }
                });
            }
        }

        private sealed class CaptureRoute
        {
            internal readonly string Endpoint;
            internal readonly AudioReceiveMultiplexer Decoder;
            internal IDisposable Capture;
            internal volatile bool Faulted;
            internal Exception Error;
            internal readonly Dictionary<AudioModemKind, Exception> DecoderErrors = new Dictionary<AudioModemKind, Exception>();
            internal long WaveformSamples, WaveformLastTicks;
            internal int WaveformBlocks;
            internal float WaveformPeak;
            internal CaptureRoute(string endpoint, AudioReceiveMultiplexer decoder) { Endpoint = endpoint; Decoder = decoder; }
        }

        private sealed class EarlyTransfer
        {
            internal readonly Guid Id;
            internal readonly DateTime Created;
            internal readonly int Count, Total, Chunk;
            internal readonly VideoProfile Profile;
            internal readonly Dictionary<int, Packet> Packets = new Dictionary<int, Packet>();
            internal int Bytes;
            internal EarlyTransfer(Packet packet, DateTime created)
            { Id = packet.TransferId; Created = created; Count = packet.Count; Total = packet.TotalLength; Profile = packet.Profile; Chunk = VisualProtocol.GetChunkSize(packet.Profile); }
            internal bool Matches(Packet packet)
            { return Id == packet.TransferId && Count == packet.Count && Total == packet.TotalLength && Profile == packet.Profile && Chunk == VisualProtocol.GetChunkSize(packet.Profile); }
        }

        private sealed class Transfer
        {
            internal readonly Guid Id;
            internal TransferAssembler Assembler = new TransferAssembler();
            internal int ReceivedBytes, Attempts;
            internal readonly int TotalBytes;
            internal int MaximumRetries;
            internal readonly DateTime Started;
            internal DateTime LastActivity;
            internal int DecodeRetries;
            internal DateTime LastProgress = DateTime.MinValue;
            internal bool Assembled, Published, Failed, StartedNotified, WaitingForPackets;
            internal DecodedPayload Pending;
            internal Transfer(Guid id, int totalBytes, DateTime started, int retries) { Id = id; TotalBytes = totalBytes; Started = LastActivity = started; MaximumRetries = retries; }
        }
    }

    internal interface IServerDeferredPaste : IDisposable { void Complete(); void Cancel(); }
    internal interface IServerPublicationTimer : IDisposable { void Start(); void Stop(); }

    internal class ServerAudioBridgeServices
    {
        internal bool NativeModemsEnabled;
        internal virtual AudioReceiveMultiplexer CreateReceiver(Action<Packet> receive, Action<byte[]> fastReceipt, Action<byte[]> compactFileReport, string endpoint)
        { return new AudioReceiveMultiplexer(receive, NativeModemsEnabled, fastReceipt, compactFileReport, endpoint); }
        internal virtual AudioEndpoint[] EnumerateRenderEndpoints() { return AudioCapture.EnumerateEndpoints(true); }
        internal virtual DateTime UtcNow { get { return DateTime.UtcNow; } }
        internal virtual void Queue(Action action) { ThreadPool.QueueUserWorkItem(delegate { action(); }); }
        internal virtual void Dispatch(Control owner, Action action)
        {
            if (owner.IsDisposed || !owner.IsHandleCreated) return;
            try { owner.BeginInvoke((MethodInvoker)delegate { if (!owner.IsDisposed) action(); }); }
            catch (InvalidOperationException) { }
        }
        internal virtual IDisposable StartCapture(string endpointId, Action<float[]> samples, Action<Exception> error)
        {
            var capture = new AudioCapture(samples, true, endpointId);
            capture.OnError += error;
            try { capture.Start(); return capture; }
            catch { capture.Dispose(); throw; }
        }
        internal virtual IServerDeferredPaste CreatePaste(Control owner, Func<bool> receiving) { return new ServerDeferredPaste(new DeferredPaste(owner.Handle, receiving, delegate { })); }
        internal virtual IServerPublicationTimer CreateTimer(Action tick) { return new ServerPublicationTimer(tick); }
        internal virtual DecodedPayload Decode(byte[] encoded) { return ClipboardPayload.Decode(encoded); }
        internal virtual void PrepareFiles(DecodedPayload payload, string root, Guid id) { string directory; ClipboardPayload.PrepareFileDrop(payload, root, id, out directory); }
        internal virtual void Publish(DecodedPayload payload, string root, Guid id) { ClipboardPayload.Publish(payload, root, id); }

        private sealed class ServerDeferredPaste : IServerDeferredPaste
        {
            private readonly DeferredPaste paste;
            internal ServerDeferredPaste(DeferredPaste paste) { this.paste = paste; }
            public void Complete() { paste.Complete(); }
            public void Cancel() { paste.Cancel(); }
            public void Dispose() { paste.Dispose(); }
        }
        private sealed class ServerPublicationTimer : IServerPublicationTimer
        {
            private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            internal ServerPublicationTimer(Action tick) { timer.Interval = 250; timer.Tick += delegate { tick(); }; }
            public void Start() { timer.Start(); }
            public void Stop() { timer.Stop(); }
            public void Dispose() { timer.Dispose(); }
        }
    }
}
