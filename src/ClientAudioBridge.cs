using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HorizonClient
{
    // Coordinates an explicitly announced benchmark. Construction has no clipboard,
    // endpoint, microphone, timer or playback side effects.
    internal sealed class ClientAudioBridge : IDisposable
    {
        private readonly Control owner;
        private readonly ClientAudioBridgeServices services;
        private readonly object gate = new object();
        private readonly HashSet<Guid> knownVideoTests = new HashSet<Guid>();
        private readonly Queue<Guid> knownVideoOrder = new Queue<Guid>();
        private readonly HashSet<Guid> startedTests = new HashSet<Guid>();
        private readonly Queue<Guid> startedOrder = new Queue<Guid>();
        private Guid session;
        private BenchmarkRun current;
        private FileRun currentFile;
        private readonly HashSet<Guid> knownFileTests = new HashSet<Guid>();
        private readonly Queue<Guid> knownFileOrder = new Queue<Guid>();
        private readonly HashSet<Guid> retiredFileTests = new HashSet<Guid>();
        private const int MaximumPendingFilePackets = 128, MaximumPendingFileBytes = 2 * 1024 * 1024;
        private FileBenchmarkCommand pendingFileStart;
        private readonly Queue<PendingFilePacket> pendingFilePackets = new Queue<PendingFilePacket>();
        private int pendingFileBytes;
        private long pendingFileDropped, pendingFileStartedAt;
        private bool fileModeApplied;
        private bool disposed;
        private int frameWidth, frameHeight, frameCount;
        private Guid publishedTransfer, receiptSession, announcedForward;
        private int publishedBytes, announcedForwardBytes;
        private long receiptLastSent = -1;
        private int fastReceiptAttempts;
        private bool receiptFallbackSent;
        private IDisposable receiptTimer;
        private long receiptGeneration, receiptTimerDue, receiptRetryUntil = -1;
        private bool ordinaryAnnouncement;
        private bool fastReceiptsSupported, lastReceiptWasFast;
        private readonly Dictionary<Guid, int> audioRetryPolicies = new Dictionary<Guid, int>();
        private readonly Queue<Guid> audioRetryPolicyOrder = new Queue<Guid>();
        private readonly Dictionary<string, LogStamp> logStamps = new Dictionary<string, LogStamp>();
        internal Guid SessionId { get { lock (gate) return session; } }
        internal bool HasFileBenchmark { get { lock (gate) return !disposed && currentFile != null; } }

        internal void ConfigureAudioRetries(BridgeCameraFormat format)
        {
            if (format == null || format.Session == Guid.Empty || format.AudioRetries < 0 || format.AudioRetries > 10) return;
            lock (gate)
            {
                if (disposed) return;
                int previous;
                bool existed = audioRetryPolicies.TryGetValue(format.Session, out previous);
                bool changed = !existed || previous != format.AudioRetries;
                audioRetryPolicies[format.Session] = format.AudioRetries;
                if (!existed) audioRetryPolicyOrder.Enqueue(format.Session);
                while (audioRetryPolicyOrder.Count > 4) audioRetryPolicies.Remove(audioRetryPolicyOrder.Dequeue());
                if (session == format.Session) services.SetAdditionalRetries(format.AudioRetries);
                if (changed) DiagnosticLog.Write("client.audio.retry_policy_announced", "session", format.Session, "additional_retries", format.AudioRetries, "applied", session == format.Session);
            }
        }

        private void ApplyAudioRetryPolicyLocked()
        { int retries; services.SetAdditionalRetries(audioRetryPolicies.TryGetValue(session, out retries) ? retries : 2); }

        private void LogLimited(string eventName, string identity, params object[] details)
        {
            if (!DiagnosticLog.IsEnabled) return;
            lock (gate)
            {
                long now = services.Milliseconds;
                LogStamp previous;
                logStamps.TryGetValue(eventName, out previous);
                if (previous != null && previous.Identity == identity && now - previous.At < 5000)
                { previous.Suppressed++; return; }
                var fields = new object[details.Length + 2];
                Array.Copy(details, fields, details.Length);
                fields[details.Length] = "suppressed_duplicates"; fields[details.Length + 1] = previous == null ? 0 : previous.Suppressed;
                DiagnosticLog.Write(eventName, fields);
                logStamps[eventName] = new LogStamp { Identity = identity, At = now };
            }
        }

        private sealed class LogStamp
        {
            internal string Identity;
            internal long At, Suppressed;
        }

        internal void ObserveFrame(int width, int height)
        {
            lock (gate)
            {
                if (disposed || width <= 0 || height <= 0 || width > 7680 || height > 4320) return;
                frameWidth = width; frameHeight = height;
                if (current != null && frameCount < Int32.MaxValue) frameCount++;
                if (currentFile != null && currentFile.Frames < Int32.MaxValue) currentFile.Frames++;
                // Prepare normally arrives in the frame just observed above.
                // If its STA callback won the race with the first observation,
                // or the source changed its actual geometry while waiting for
                // Start, replace unknown/old Ready metadata with actual frames.
                // Stable frames cannot keep interrupting the same audio burst.
                if (currentFile != null && currentFile.Receiver.Status == FileBenchmarkStatus.Ready &&
                    (currentFile.LastReadyWidth != width || currentFile.LastReadyHeight != height))
                    SendFileLocked(currentFile, false);
            }
        }

        internal ClientAudioBridge(Control owner, AudioClipboardSender sender) : this(owner, MakeServices(sender)) { }

        internal ClientAudioBridge(Control owner, ClientAudioBridgeServices services)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (services == null) throw new ArgumentNullException("services");
            this.owner = owner;
            this.services = services;
        }

        private static ClientAudioBridgeServices MakeServices(AudioClipboardSender sender)
        {
            if (sender == null) throw new ArgumentNullException("sender");
            return new ClientAudioBridgeServices(sender);
        }

        // The receiver calls this on its STA only after the complete payload has
        // been published to the clipboard. Matching the server's advertisement
        // also permits publication to precede a lost or delayed first hello.
        internal void NotifyPublished(Guid transfer, int encodedBytes)
        {
            if (transfer == Guid.Empty || encodedBytes <= 0) throw new ArgumentException("A published transfer and its encoded length are required.");
            lock (gate)
            {
                if (disposed) return;
                CancelReceiptTimerLocked(); receiptRetryUntil = -1;
                publishedTransfer = transfer; publishedBytes = encodedBytes;
                DiagnosticLog.Write("client.delivery.published", "session", session, "transfer", transfer, "encoded_bytes", encodedBytes);
                receiptSession = Guid.Empty; receiptLastSent = -1;
                fastReceiptAttempts = 0; receiptFallbackSent = false;
                lastReceiptWasFast = false;
                BindReceiptLocked();
                SendReceiptLocked();
            }
        }

        private void ChangeReceiptSessionLocked(Guid next)
        {
            CancelReceiptTimerLocked(); receiptRetryUntil = -1;
            if (pendingFileStart != null && pendingFileStart.Session != next) ClearPendingFileLocked();
            if (receiptSession != Guid.Empty && receiptSession != next)
            { publishedTransfer = Guid.Empty; publishedBytes = 0; receiptSession = Guid.Empty; }
            ordinaryAnnouncement = false; announcedForward = Guid.Empty; announcedForwardBytes = 0;
            receiptLastSent = -1;
            fastReceiptAttempts = 0; receiptFallbackSent = false;
            fastReceiptsSupported = lastReceiptWasFast = false;
        }

        private void BindReceiptLocked()
        {
            if (receiptSession == Guid.Empty && session != Guid.Empty && ordinaryAnnouncement &&
                publishedTransfer != Guid.Empty && announcedForward == publishedTransfer && announcedForwardBytes == publishedBytes)
            {
                receiptSession = session;
                DiagnosticLog.Write("client.delivery.receipt_bound", "session", session, "transfer", publishedTransfer, "encoded_bytes", publishedBytes);
            }
        }

        private void SendReceiptLocked()
        {
            string reason = disposed || owner.IsDisposed ? "disposed" : currentFile != null || current != null || fileModeApplied ? "benchmark_reserved" :
                session == Guid.Empty ? "waiting_for_server_hello" : publishedTransfer == Guid.Empty ? "no_published_transfer" :
                !ordinaryAnnouncement ? "waiting_for_ordinary_hello" : announcedForward != publishedTransfer ? "advertised_transfer_mismatch" :
                announcedForwardBytes != publishedBytes ? "advertised_size_mismatch" : receiptSession != session ? "session_mismatch" : null;
            if (reason != null)
            {
                CancelReceiptTimerLocked(); receiptRetryUntil = -1;
                LogLimited("client.delivery.receipt_waiting", reason + session + publishedTransfer + announcedForward, "reason", reason,
                    "session", session, "receipt_session", receiptSession, "transfer", publishedTransfer, "advertised_transfer", announcedForward,
                    "encoded_bytes", publishedBytes, "advertised_bytes", announcedForwardBytes);
                return;
            }
            long now = services.Milliseconds;
            // The first matching announcement authorizes one bounded recovery
            // train. Later matching hellos renew its peer lease; disappearance
            // cannot leave a permanent audio retry loop.
            int retryInterval = !fastReceiptsSupported ? 8000 : receiptFallbackSent ? 4000 : 750;
            bool upgraded = fastReceiptsSupported && fastReceiptAttempts == 0;
            if (receiptLastSent >= 0 && now - receiptLastSent < retryInterval && !upgraded)
            {
                LogLimited("client.delivery.receipt_rate_limited", session.ToString() + publishedTransfer, "session", session,
                    "transfer", publishedTransfer, "remaining_ms", retryInterval - (now - receiptLastSent));
                ScheduleReceiptRetryLocked();
                return;
            }
            Guid audioId = Guid.NewGuid();
            try
            {
                bool fast = fastReceiptsSupported && (fastReceiptAttempts < 3 || !lastReceiptWasFast);
                if (fast) services.SendFastReceipt(session, publishedTransfer, publishedBytes);
                else if (fastReceiptsSupported) services.SendDeliveryReceiptFallback(session, publishedTransfer, publishedBytes);
                else services.Send(DeliveryReceipt.Encode(session, publishedTransfer, publishedBytes), audioId);
                DiagnosticLog.Write("client.delivery.receipt_queued", "session", session, "transfer", publishedTransfer,
                    "encoded_bytes", publishedBytes, "audioTransfer", fastReceiptsSupported ? Guid.Empty : audioId,
                    "fast", fast, "selected_modem_fallback", fastReceiptsSupported && !fast,
                    "wire_bytes", fast ? DeliveryReceipt.FastWireLength : DeliveryReceipt.Length,
                    "retry", receiptLastSent >= 0, "fast_attempt", fast ? fastReceiptAttempts + 1 : fastReceiptAttempts,
                    "retry_interval_ms", retryInterval);
                if (receiptLastSent < 0 || upgraded)
                    receiptRetryUntil = Math.Max(receiptRetryUntil, now + (fastReceiptsSupported ? 2250 : 8000));
                receiptLastSent = now;
                lastReceiptWasFast = fast;
                if (fast) fastReceiptAttempts = Math.Min(3, fastReceiptAttempts + 1);
                else if (fastReceiptsSupported) receiptFallbackSent = true;
                ScheduleReceiptRetryLocked();
            }
            catch (Exception error)
            { DiagnosticLog.Error("client.delivery.receipt_queue_failed", error, "session", session, "transfer", publishedTransfer); }
        }

        private void CancelReceiptTimerLocked()
        {
            receiptGeneration++;
            IDisposable timer = receiptTimer; receiptTimer = null;
            if (timer != null)
                try { timer.Dispose(); }
                catch (Exception error) { DiagnosticLog.Error("client.delivery.receipt_timer_dispose_failed", error); }
        }

        private void ScheduleReceiptRetryLocked()
        {
            if (receiptLastSent < 0) return;
            int interval = !fastReceiptsSupported ? 8000 : receiptFallbackSent ? 4000 : 750;
            long due = receiptLastSent + interval;
            if (receiptTimer != null)
            {
                if (receiptTimerDue == due && due <= receiptRetryUntil) return;
                CancelReceiptTimerLocked();
            }
            if (due > receiptRetryUntil) return;
            long generation = receiptGeneration;
            Guid expectedSession = session, expectedTransfer = publishedTransfer;
            int expectedBytes = publishedBytes;
            try
            {
                receiptTimerDue = due;
                receiptTimer = services.Schedule(delegate
                {
                    services.Dispatch(owner, delegate
                    {
                        lock (gate)
                        {
                            if (generation != receiptGeneration) return;
                            CancelReceiptTimerLocked();
                            if (disposed || owner.IsDisposed || session != expectedSession || receiptSession != expectedSession ||
                                publishedTransfer != expectedTransfer || publishedBytes != expectedBytes || !ordinaryAnnouncement ||
                                announcedForward != expectedTransfer || announcedForwardBytes != expectedBytes ||
                                current != null || currentFile != null || fileModeApplied || services.Milliseconds > receiptRetryUntil) return;
                            SendReceiptLocked();
                        }
                    });
                }, (int)Math.Max(1L, Math.Min(Int32.MaxValue, due - services.Milliseconds)));
            }
            catch (Exception error) { DiagnosticLog.Error("client.delivery.receipt_schedule_failed", error, "session", session, "transfer", publishedTransfer); }
        }

        // Called on the owner's STA after its normal video/control decoder found a hello.
        internal void HandleHello(BridgeHello hello, string selectedCameraName)
        {
            if (hello == null || hello.Session == Guid.Empty || hello.Seconds < 0 || hello.Seconds > 60 || owner.IsDisposed)
            { LogLimited("client.bridge.hello_ignored", "invalid_or_owner_disposed", "reason", "invalid_or_owner_disposed"); return; }
            LogLimited("client.bridge.hello", hello.Session.ToString() + hello.VideoTest + hello.ForwardTransfer + hello.ForwardEncodedBytes,
                "session", hello.Session, "video_test", hello.VideoTest, "audio_test", hello.AudioTest, "seconds", hello.Seconds,
                "forward_transfer", hello.ForwardTransfer, "forward_bytes", hello.ForwardEncodedBytes);
            BenchmarkRun retired = null, created = null;
            FileRun retiredFile = null;
            lock (gate)
            {
                if (disposed) return;
                // A lost Cancel is followed by ordinary server announcements.
                // Do not let an older queued hello undo a fresh Prepare.
                if (currentFile != null && hello.VideoTest == Guid.Empty &&
                    (currentFile.Receiver.Terminal || (currentFile.Receiver.Status == FileBenchmarkStatus.Ready && services.Milliseconds - currentFile.PreparedAt >= 20000)))
                {
                    LogFileCommand("client.file.hello_recovery", currentFile.Receiver.Command, "ordinary_hello_after_terminal_or_ready_deadline");
                    retiredFile = currentFile; currentFile = null;
                    retiredFileTests.Add(retiredFile.Receiver.Command.Test);
                }
                if (session != hello.Session)
                {
                    DiagnosticLog.Write("client.bridge.session_changed", "previousSession", session, "session", hello.Session, "legacyTest", hello.VideoTest);
                    retired = current;
                    if (retired != null) retired.Finished = true;
                    current = null;
                    if (currentFile != null) retiredFile = currentFile;
                    currentFile = null;
                    if (retiredFile != null) retiredFileTests.Add(retiredFile.Receiver.Command.Test);
                    ChangeReceiptSessionLocked(hello.Session);
                    session = hello.Session;
                }
                ordinaryAnnouncement = hello.VideoTest == Guid.Empty;
                // Legacy hellos may alternate with the new feature advertisement
                // for older peers. Once observed, retain support for this session.
                if (hello.FastDeliveryReceipts) fastReceiptsSupported = true;
                announcedForward = ordinaryAnnouncement ? hello.ForwardTransfer : Guid.Empty;
                announcedForwardBytes = ordinaryAnnouncement ? hello.ForwardEncodedBytes : 0;
                BindReceiptLocked();
                if (ordinaryAnnouncement && announcedForward != Guid.Empty && announcedForward == publishedTransfer &&
                    announcedForwardBytes == publishedBytes && receiptSession == session && current == null && currentFile == null && !fileModeApplied)
                    receiptRetryUntil = Math.Max(receiptRetryUntil, services.Milliseconds + (fastReceiptsSupported ? 4000 : 8000));
                else { CancelReceiptTimerLocked(); receiptRetryUntil = -1; }
                if (hello.VideoTest != Guid.Empty && hello.AudioTest != Guid.Empty && hello.Seconds >= 5)
                {
                    RememberVideoLocked(hello.VideoTest);
                    if (startedTests.Add(hello.VideoTest))
                    {
                        if (currentFile != null)
                        {
                            retiredFile = currentFile; currentFile = null;
                            retiredFileTests.Add(retiredFile.Receiver.Command.Test);
                        }
                        startedOrder.Enqueue(hello.VideoTest);
                        while (startedOrder.Count > 128) startedTests.Remove(startedOrder.Dequeue());
                        if (current != null) { retired = current; retired.Finished = true; }
                        created = new BenchmarkRun(hello);
                        frameCount = 0;
                        current = created;
                    }
                }
            }
            Retire(retired);
            RetireFile(retiredFile);
            if (retiredFile != null) UpdateFileMode();
            try { services.Enable(hello.Session); }
            catch (Exception error) { DiagnosticLog.Error("client.bridge.enable_failed", error, "session", hello.Session); Retire(created); return; }
            lock (gate) { ApplyAudioRetryPolicyLocked(); SendReceiptLocked(); }
            if (created == null) return;
            BenchmarkRun run = created;
            try
            {
                IDisposable timer = services.Schedule(delegate { Complete(run); }, checked(hello.Seconds * 1000));
                bool accepted;
                lock (gate)
                {
                    accepted = IsCurrentLocked(run);
                    if (accepted) run.Timer = timer;
                }
                if (!accepted) { timer.Dispose(); return; }
                string microphoneName = String.IsNullOrEmpty(hello.CameraName) ? selectedCameraName : hello.CameraName;
                services.Queue(delegate { PrepareMicrophone(run, microphoneName); });
            }
            catch (Exception error) { DiagnosticLog.Error("client.bridge.legacy_schedule_failed", error, "session", run.Session, "test", run.VideoId); Retire(run); }
        }

        // Call for every camera packet before the clipboard assembler, including hello
        // packets. Seeing a hello here reserves its test ID before STA dispatch can lag.
        // False for a hello lets the normal control handler continue processing it.
        internal bool HandleVideo(Packet packet)
        {
            if (packet == null) return false;
            FileBenchmarkCommand fileCommand;
            if (FileBenchmarkProtocol.TryCommand(packet, out fileCommand))
            {
                LogFileCommand("receiver.control.file_command_recognized", fileCommand, null);
                lock (gate)
                {
                    if (!disposed)
                    {
                        if (fileCommand.Phase != FileBenchmarkPhase.Cancel)
                        { CancelReceiptTimerLocked(); receiptRetryUntil = -1; }
                        RememberFileLocked(fileCommand.Test);
                        bool matchesCurrent = currentFile != null && currentFile.Receiver.Command.Test == fileCommand.Test;
                        bool validCurrentIdentity = !matchesCurrent || FileBenchmarkProtocol.SameTest(currentFile.Receiver.Command, fileCommand);
                        if (fileCommand.Phase == FileBenchmarkPhase.Cancel && validCurrentIdentity)
                        {
                            retiredFileTests.Add(fileCommand.Test);
                            if (pendingFileStart != null && FileBenchmarkProtocol.SameTest(pendingFileStart, fileCommand)) ClearPendingFileLocked();
                        }
                        else if (fileCommand.Phase == FileBenchmarkPhase.Start && validCurrentIdentity && !retiredFileTests.Contains(fileCommand.Test) &&
                            (!matchesCurrent || !currentFile.Receiver.Started) &&
                            (pendingFileStart == null || pendingFileStart.Test == fileCommand.Test || !matchesCurrent))
                        {
                            if (pendingFileStart == null || pendingFileStart.Test != fileCommand.Test)
                            { ClearPendingFileLocked(); pendingFileStart = FileBenchmarkProtocol.Copy(fileCommand); pendingFileStartedAt = services.Milliseconds; }
                        }
                    }
                }
                // All control state changes share the same STA ordering as
                // normal hello callbacks; data IDs are reserved immediately.
                services.Dispatch(owner, delegate { HandleFileCommand(fileCommand); });
                return true;
            }
            lock (gate)
            {
                bool reserved = false;
                foreach (Guid test in knownFileTests)
                    if (packet.TransferId == test || BridgeControl.IsBenchmarkTransfer(packet.TransferId, test)) { reserved = true; break; }
                if (reserved)
                {
                    if (!disposed && BufferPendingFilePacketLocked(packet)) return true;
                    if (!disposed && currentFile != null)
                    {
                        FileRun run = currentFile; long before = run.Receiver.ReceivedBytes; bool terminal = run.Receiver.Terminal;
                        if (run.Receiver.Accept(packet, services.Milliseconds) && before == 0)
                            SendFileLocked(run, true);
                        else if (!terminal && run.Receiver.Terminal) SendFileLocked(run, true);
                    }
                    return true;
                }
            }
            BridgeHello announcement;
            if (BridgeControl.TryHello(packet, out announcement))
            {
                LogLimited("receiver.control.hello_recognized", announcement.Session.ToString() + announcement.VideoTest + announcement.ForwardTransfer + announcement.ForwardEncodedBytes,
                    "session", announcement.Session, "video_test", announcement.VideoTest, "audio_test", announcement.AudioTest,
                    "forward_transfer", announcement.ForwardTransfer, "forward_bytes", announcement.ForwardEncodedBytes, "seconds", announcement.Seconds);
                if (announcement.VideoTest != Guid.Empty)
                    lock (gate) { if (!disposed) RememberVideoLocked(announcement.VideoTest); }
                return false;
            }
            lock (gate)
            {
                bool known = knownVideoTests.Contains(packet.TransferId);
                if (!known) foreach (Guid id in knownVideoTests) if (BridgeControl.IsBenchmarkTransfer(packet.TransferId, id)) { known = true; break; }
                if (!known) return false;
                if (current != null && IsCurrentLocked(current) && BridgeControl.IsBenchmarkTransfer(packet.TransferId, current.VideoId))
                {
                    if (current.Video.Add(packet))
                    {
                        int p = (int)packet.Profile;
                        if (p >= 0 && p < current.ProfileBytes.Length) current.ProfileBytes[p] += packet.Data.Length;
                    }
                }
                return true;
            }
        }

        private void ClearPendingFileLocked()
        { pendingFileStart = null; pendingFilePackets.Clear(); pendingFileBytes = 0; pendingFileDropped = pendingFileStartedAt = 0; }

        private bool BufferPendingFilePacketLocked(Packet packet)
        {
            int segment;
            if (pendingFileStart == null || !FileBenchmarkProtocol.TrySegmentIndex(pendingFileStart.Test, packet.TransferId, out segment)) return false;
            int bytes = packet.Data == null ? 0 : packet.Data.Length;
            if (pendingFilePackets.Count >= MaximumPendingFilePackets || bytes > MaximumPendingFileBytes - pendingFileBytes)
            {
                pendingFileDropped++;
                LogLimited("client.file.pending_overflow", pendingFileStart.Test.ToString(), "test", pendingFileStart.Test,
                    "queued_packets", pendingFilePackets.Count, "queued_bytes", pendingFileBytes, "dropped_packets", pendingFileDropped);
                return true;
            }
            pendingFilePackets.Enqueue(new PendingFilePacket { At = services.Milliseconds,
                Packet = new Packet { TransferId = packet.TransferId, Index = packet.Index, Count = packet.Count, TotalLength = packet.TotalLength,
                    Profile = packet.Profile, ChunkSize = packet.ChunkSize, Data = packet.Data == null ? null : (byte[])packet.Data.Clone() } });
            pendingFileBytes += bytes;
            return true;
        }

        private void DrainPendingFileLocked(FileRun run)
        {
            if (pendingFileStart == null || !run.Receiver.Started || !FileBenchmarkProtocol.SameTest(pendingFileStart, run.Receiver.Command)) return;
            int packets = pendingFilePackets.Count, bytes = pendingFileBytes; long dropped = pendingFileDropped;
            while (pendingFilePackets.Count != 0)
            { PendingFilePacket packet = pendingFilePackets.Dequeue(); run.Receiver.Accept(packet.Packet, packet.At); }
            ClearPendingFileLocked();
            DiagnosticLog.Write("client.file.pending_drained", "test", run.Receiver.Command.Test, "packets", packets, "bytes", bytes,
                "dropped_packets", dropped, "received", run.Receiver.ReceivedBytes);
            if (run.Enabled && (run.Receiver.ReceivedBytes > 0 || run.Receiver.Terminal)) SendFileLocked(run, true);
        }

        private void StartFileLocked(FileRun run)
        {
            long started = services.Milliseconds;
            if (pendingFileStart != null && FileBenchmarkProtocol.SameTest(pendingFileStart, run.Receiver.Command)) started = Math.Min(started, pendingFileStartedAt);
            run.Receiver.Start(started);
            DrainPendingFileLocked(run);
        }

        // New logical-file tests use only reverse playback for control reports.
        // They never enumerate or open a microphone, assemble clipboard data,
        // allocate the complete file, or write received bytes to disk.
        private void HandleFileCommand(FileBenchmarkCommand command)
        {
            FileRun retired = null, created = null; BenchmarkRun retiredLegacy = null;
            lock (gate)
            {
                if (disposed || owner.IsDisposed) return;
                LogFileCommand("client.file.command", command, null);
                RememberFileLocked(command.Test);
                FileRun run = currentFile;
                if (run != null && run.Receiver.Command.Test == command.Test)
                {
                    if (!FileBenchmarkProtocol.SameTest(run.Receiver.Command, command)) { LogFileCommand("client.file.command_ignored", command, "same_id_different_parameters"); return; }
                    bool upgraded = command.CompactReports && !run.CompactReports;
                    if (upgraded) { run.CompactReports = true; LogFileCommand("client.file.compact_feedback_enabled", command, "capability_latched_for_exact_test"); }
                    if (command.Phase == FileBenchmarkPhase.Cancel)
                    {
                        LogFileCommand("client.file.cancel", command, null);
                        run.Receiver.Cancel(services.Milliseconds); SendFileLocked(run, true);
                        currentFile = null; retiredFileTests.Add(command.Test); retired = run;
                    }
                    else
                    {
                        if (command.Phase == FileBenchmarkPhase.Start)
                        {
                            if (!run.Receiver.Started) LogFileCommand("client.file.start", command, null);
                            StartFileLocked(run);
                        }
                        if (upgraded || run.Receiver.Status == FileBenchmarkStatus.Ready || run.Receiver.Terminal) SendFileLocked(run, upgraded);
                    }
                }
                else
                {
                    if (command.Phase == FileBenchmarkPhase.Cancel || retiredFileTests.Contains(command.Test))
                    { LogFileCommand("client.file.command_ignored", command, command.Phase == FileBenchmarkPhase.Cancel ? "cancel_without_active_test" : "retired_test"); return; }
                    if (run != null) { retired = run; retiredFileTests.Add(run.Receiver.Command.Test); }
                    retiredLegacy = current; current = null;
                    if (retiredLegacy != null) retiredLegacy.Finished = true;
                    if (session != command.Session)
                    {
                        DiagnosticLog.Write("client.bridge.session_changed", "previousSession", session, "session", command.Session, "reason", "file_prepare");
                        ChangeReceiptSessionLocked(command.Session);
                    }
                    ordinaryAnnouncement = false;
                    CancelReceiptTimerLocked(); receiptRetryUntil = -1;
                    session = command.Session;
                    created = new FileRun(command, services.Milliseconds); currentFile = created;
                    LogFileCommand("client.file.prepared", command, null);
                    if (command.Phase == FileBenchmarkPhase.Start)
                    {
                        LogFileCommand("client.file.start", command, "implicit_prepare_from_start");
                        StartFileLocked(created);
                    }
                }
            }
            Retire(retiredLegacy); RetireFile(retired);
            if (retired != null) UpdateFileMode();
            if (created != null)
            {
                FileRun run = created;
                services.Dispatch(owner, delegate
                {
                    lock (gate)
                    {
                        if (!IsCurrentFileLocked(run)) return;
                        try
                        {
                            services.Enable(run.Receiver.Command.Session); ApplyAudioRetryPolicyLocked(); ApplyFileModeLocked(); run.Enabled = true;
                            LogFileCommand("client.file.reverse_audio_enabled", run.Receiver.Command, null); SendFileLocked(run, true);
                        }
                        catch (Exception error) { DiagnosticLog.Error("client.file.reverse_audio_enable_failed", error, "session", run.Receiver.Command.Session, "test", run.Receiver.Command.Test); }
                    }
                });
                ScheduleFileTick(run);
            }
        }

        private void RememberFileLocked(Guid test)
        {
            if (!knownFileTests.Add(test)) return;
            knownFileOrder.Enqueue(test);
            while (knownFileOrder.Count > 128)
            {
                Guid oldest = knownFileOrder.Dequeue();
                if (currentFile != null && currentFile.Receiver.Command.Test == oldest) knownFileOrder.Enqueue(oldest);
                else { knownFileTests.Remove(oldest); retiredFileTests.Remove(oldest); }
            }
        }

        private bool IsCurrentFileLocked(FileRun run) { return !disposed && Object.ReferenceEquals(currentFile, run) && session == run.Receiver.Command.Session; }

        private void ApplyFileModeLocked()
        {
            bool active = !disposed && currentFile != null;
            if (fileModeApplied == active) return;
            services.SetBenchmarkMode(active); fileModeApplied = active;
            DiagnosticLog.Write("client.file.clipboard_mode", "paused", active, "session", session, "test", currentFile == null ? Guid.Empty : currentFile.Receiver.Command.Test);
        }

        private void UpdateFileMode()
        {
            services.Dispatch(owner, delegate { lock (gate) { try { ApplyFileModeLocked(); } catch (Exception error) { DiagnosticLog.Error("client.file.clipboard_mode_failed", error); } } });
        }

        private void SendFileLocked(FileRun run, bool immediate)
        {
            if (!IsCurrentFileLocked(run) || !run.Enabled)
            { LogFileCommand("client.file.report_waiting", run.Receiver.Command, !IsCurrentFileLocked(run) ? "stale_or_disposed" : "reverse_sender_not_enabled"); return; }
            long now = services.Milliseconds;
            bool ready = run.Receiver.Status == FileBenchmarkStatus.Ready;
            if (ready && (frameWidth <= 0 || frameHeight <= 0))
            { LogFileCommand("client.file.report_waiting", run.Receiver.Command, "ready_waiting_for_actual_frame_geometry"); return; }
            bool changedReadyGeometry = ready && (run.LastReadyWidth != frameWidth || run.LastReadyHeight != frameHeight);
            if (!immediate && run.LastReport >= 0 && now - run.LastReport < 8000 &&
                !(changedReadyGeometry && now - run.LastReport >= 1500))
            { LogFileCommand("client.file.report_rate_limited", run.Receiver.Command, "repeat_interval_8000ms"); return; }
            try
            {
                FileBenchmarkReport report = run.Receiver.Report(now, frameWidth, frameHeight, run.Frames, run.CompactReports);
                Guid audioId = Guid.NewGuid();
                if (run.CompactReports) services.SendCompactFileReport(CompactFileBenchmarkReport.Encode(run.Receiver.Command, report), audioId);
                else services.Send(FileBenchmarkProtocol.Report(report), audioId);
                DiagnosticLog.Write("client.file.report_queued", "session", report.Session, "test", report.Test, "profile", report.Profile,
                    "audioTransfer", audioId, "status", report.Status, "failure", report.Failure, "received", report.ReceivedBytes,
                    "total", report.TotalBytes, "elapsedMs", report.ElapsedMilliseconds, "width", report.FrameWidth, "height", report.FrameHeight,
                    "frames", report.Frames, "immediate", immediate, "sinceLastReportMs", run.LastReport < 0 ? -1 : now - run.LastReport);
                DiagnosticLog.Write("client.file.feedback_format", "test", report.Test, "audioTransfer", audioId, "compact", run.CompactReports,
                    "missing_segment", report.MissingSegment, "missing_count", report.MissingIndices == null ? 0 : report.MissingIndices.Length);
                if (ready) { run.LastReadyWidth = report.FrameWidth; run.LastReadyHeight = report.FrameHeight; }
                run.LastReport = now;
            }
            catch (Exception error) { DiagnosticLog.Error("client.file.report_queue_failed", error, "session", run.Receiver.Command.Session, "test", run.Receiver.Command.Test); }
        }

        private void ScheduleFileTick(FileRun run)
        {
            IDisposable timer;
            try { timer = services.Schedule(delegate { FileTick(run); }, 1000); }
            catch (Exception error) { DiagnosticLog.Error("client.file.tick_schedule_failed", error, "test", run.Receiver.Command.Test); return; }
            lock (gate)
            {
                if (IsCurrentFileLocked(run)) run.Timer = timer;
                else timer.Dispose();
            }
        }

        private void FileTick(FileRun run)
        {
            lock (gate)
            {
                if (!IsCurrentFileLocked(run)) return;
                IDisposable old = run.Timer; run.Timer = null;
                if (old != null) old.Dispose();
                bool terminal = run.Receiver.Terminal;
                run.Receiver.Tick(services.Milliseconds);
                if (!terminal && run.Receiver.Terminal) SendFileLocked(run, true);
                else if (run.Receiver.Status != FileBenchmarkStatus.Progress || run.Receiver.ReceivedBytes > 0) SendFileLocked(run, false);
            }
            ScheduleFileTick(run);
        }

        private void RetireFile(FileRun run)
        {
            if (run == null) return;
            LogFileCommand("client.file.retired", run.Receiver.Command, null);
            IDisposable timer;
            lock (gate)
            {
                timer = run.Timer; run.Timer = null;
                if (pendingFileStart != null && pendingFileStart.Test == run.Receiver.Command.Test) ClearPendingFileLocked();
            }
            if (timer != null) { try { timer.Dispose(); } catch (Exception error) { DiagnosticLog.Error("client.file.timer_dispose_failed", error, "test", run.Receiver.Command.Test); } }
        }

        private void LogFileCommand(string eventName, FileBenchmarkCommand command, string reason)
        {
            if (!DiagnosticLog.IsEnabled) return;
            LogLimited(eventName, command.Session.ToString() + command.Test + command.Phase + reason, "session", command.Session, "test", command.Test, "profile", command.Profile, "phase", command.Phase,
                "fileBytes", command.FileSize, "segmentBytes", command.SegmentSize, "inactivityMs", command.InactivityMilliseconds,
                "maximumRunMs", command.MaximumRunMilliseconds, "reason", reason);
        }

        private void RememberVideoLocked(Guid id)
        {
            if (!knownVideoTests.Add(id)) return;
            knownVideoOrder.Enqueue(id);
            while (knownVideoOrder.Count > 128)
            {
                Guid oldest = knownVideoOrder.Dequeue();
                if (current != null && oldest == current.VideoId) knownVideoOrder.Enqueue(oldest);
                else knownVideoTests.Remove(oldest);
            }
        }

        private bool IsCurrentLocked(BenchmarkRun run)
        {
            return !disposed && !run.Finished && Object.ReferenceEquals(current, run) && session == run.Session;
        }

        private void PrepareMicrophone(BenchmarkRun run, string cameraName)
        {
            try
            {
                lock (gate) { if (!IsCurrentLocked(run)) return; }
                AudioEndpoint endpoint = SelectMicrophone(services.EnumerateMicrophones(), cameraName);
                if (endpoint == null) { DiagnosticLog.Write("client.audio.legacy_microphone_not_found", "test", run.VideoId, "camera", cameraName); return; }
                DiagnosticLog.Write("client.audio.legacy_microphone_selected", "test", run.VideoId, "endpoint", endpoint.Id, "name", endpoint.Name);
                lock (gate) { if (!IsCurrentLocked(run)) return; }
                AudioDecoder decoder = new AudioDecoder();
                decoder.PacketReceived += delegate(Packet packet)
                {
                    lock (gate)
                    {
                        if (IsCurrentLocked(run) && packet != null && packet.TransferId == run.AudioId) run.Audio.Add(packet);
                    }
                };
                IDisposable capture = services.StartMicrophone(endpoint.Id, delegate(float[] values)
                {
                    lock (gate) { if (!IsCurrentLocked(run)) return; }
                    decoder.Feed(values);
                });
                DiagnosticLog.Write("client.audio.legacy_microphone_started", "test", run.VideoId, "endpoint", endpoint.Id);
                bool accepted;
                lock (gate)
                {
                    accepted = IsCurrentLocked(run);
                    if (accepted) { run.Capture = capture; run.MicrophoneFound = true; }
                }
                if (!accepted) DisposeCaptureAsync(capture);
            }
            catch (Exception error) { DiagnosticLog.Error("client.audio.legacy_microphone_failed", error, "test", run.VideoId); }
        }

        internal static AudioEndpoint SelectMicrophone(AudioEndpoint[] endpoints, string cameraName)
        {
            string wanted = Normalize(cameraName);
            if (wanted.Length == 0 || endpoints == null) return null;
            AudioEndpoint found = null;
            foreach (AudioEndpoint endpoint in endpoints)
            {
                if (endpoint == null || String.IsNullOrEmpty(endpoint.Id)) continue;
                string candidate = Normalize(endpoint.Name);
                string[] microphoneWords = { "usbmicrophonearray", "microphonearray", "usbmicrophone", "microphones", "microphone", "микрофонныймассив", "массивмикрофонов", "микрофоны", "микрофон" };
                if (candidate != wanted)
                    foreach (string word in microphoneWords)
                    {
                        if (candidate.StartsWith(word, StringComparison.Ordinal)) { candidate = candidate.Substring(word.Length); break; }
                        if (candidate.EndsWith(word, StringComparison.Ordinal)) { candidate = candidate.Substring(0, candidate.Length - word.Length); break; }
                    }
                if (candidate != wanted) continue;
                if (found != null) return null; // Ambiguous names never choose a physical/default microphone.
                found = endpoint;
            }
            return found;
        }

        private static string Normalize(string value)
        {
            StringBuilder normalized = new StringBuilder();
            if (value != null)
                foreach (char character in value)
                    if (Char.IsLetterOrDigit(character)) normalized.Append(Char.ToLowerInvariant(character));
            // Horizon may expand "webcam" to "web camera" in the microphone name.
            return normalized.ToString().Replace("webcamera", "webcam");
        }

        private void Complete(BenchmarkRun run)
        {
            lock (gate)
            {
                if (!IsCurrentLocked(run)) return;
                BridgeReport report = new BridgeReport
                {
                    Session = run.Session, Test = run.VideoId,
                    VideoBytes = run.Video.Bytes, AudioBytes = run.Audio.Bytes,
                    Milliseconds = (int)Math.Max(1L, Math.Min(120000L, run.Elapsed.ElapsedMilliseconds)),
                    MicrophoneFound = run.MicrophoneFound,
                    FrameWidth = frameWidth, FrameHeight = frameHeight, Frames = frameCount,
                    ProfileBytes = run.ProfileBytes, ProfileMilliseconds = run.ProfileMilliseconds
                };
                run.Finished = true;
                current = null;
                // SendMessage only queues a bounded control message; taking this lock prevents
                // a new session from interleaving between the final check and the enqueue.
                try { services.Send(BridgeControl.Report(report), Guid.NewGuid()); DiagnosticLog.Write("client.bridge.legacy_report_queued", "session", report.Session, "test", report.Test); }
                catch (Exception error) { DiagnosticLog.Error("client.bridge.legacy_report_queue_failed", error, "session", report.Session, "test", report.Test); }
            }
            Retire(run);
        }

        private void Retire(BenchmarkRun run)
        {
            if (run == null) return;
            IDisposable timer, capture;
            lock (gate)
            {
                run.Finished = true;
                if (Object.ReferenceEquals(current, run)) current = null;
                timer = run.Timer; run.Timer = null;
                capture = run.Capture; run.Capture = null;
            }
            if (timer != null) { try { timer.Dispose(); } catch (Exception error) { DiagnosticLog.Error("client.bridge.legacy_timer_dispose_failed", error); } }
            DisposeCaptureAsync(capture);
        }

        private void DisposeCaptureAsync(IDisposable capture)
        {
            if (capture == null) return;
            try { services.Queue(delegate { try { capture.Dispose(); DiagnosticLog.Write("client.audio.legacy_microphone_stopped"); } catch (Exception error) { DiagnosticLog.Error("client.audio.legacy_microphone_stop_failed", error); } }); }
            catch (Exception error) { DiagnosticLog.Error("client.audio.legacy_microphone_stop_queue_failed", error); }
        }

        public void Dispose()
        {
            BenchmarkRun retired;
            FileRun retiredFile;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                CancelReceiptTimerLocked(); receiptRetryUntil = -1;
                ClearPendingFileLocked();
                session = Guid.Empty;
                publishedTransfer = receiptSession = announcedForward = Guid.Empty;
                retired = current;
                current = null;
                retiredFile = currentFile; currentFile = null;
            }
            Retire(retired);
            RetireFile(retiredFile);
            UpdateFileMode();
        }

        private sealed class FileRun
        {
            internal readonly FileBenchmarkReceiver Receiver;
            internal readonly long PreparedAt;
            internal bool Enabled, CompactReports;
            internal long LastReport = -1;
            internal int Frames, LastReadyWidth, LastReadyHeight;
            internal IDisposable Timer;
            internal FileRun(FileBenchmarkCommand command, long preparedAt) { Receiver = new FileBenchmarkReceiver(command); PreparedAt = preparedAt; CompactReports = command.CompactReports; }
        }

        private sealed class PendingFilePacket
        {
            internal Packet Packet;
            internal long At;
        }

        private sealed class BenchmarkRun
        {
            internal readonly Guid Session, VideoId, AudioId;
            internal readonly Stopwatch Elapsed = Stopwatch.StartNew();
            internal readonly SpeedCounter Video = new SpeedCounter(), Audio = new SpeedCounter();
            internal bool Finished, MicrophoneFound;
            internal readonly long[] ProfileBytes = new long[12];
            internal readonly int[] ProfileMilliseconds = new int[12];
            internal IDisposable Capture, Timer;
            internal BenchmarkRun(BridgeHello hello)
            {
                Session = hello.Session; VideoId = hello.VideoTest; AudioId = hello.AudioTest;
                // A 60-second announcement denotes the 12-profile sweep; the
                // original 20-second command still measures a single profile.
                for (int i = 0; i < ProfileMilliseconds.Length; i++) ProfileMilliseconds[i] = hello.Seconds == 60 ? 4000 : hello.Seconds * 1000;
            }
        }
    }

    // Replaces only I/O/scheduling for pure tests; production methods defer all work
    // until an explicit server announcement and return a bounded timer/capture lifetime.
    internal class ClientAudioBridgeServices
    {
        private readonly AudioClipboardSender sender;
        internal ClientAudioBridgeServices(AudioClipboardSender sender) { this.sender = sender; }
        internal virtual void Enable(Guid session) { sender.Enable(session); }
        internal virtual void Send(byte[] bytes, Guid transferId) { sender.SendMessage(bytes, transferId); }
        internal virtual void SendCompactFileReport(byte[] wire, Guid transferId) { sender.SendCompactFileReport(wire, transferId); }
        internal virtual void SetAdditionalRetries(int retries) { if (sender != null) sender.SetAdditionalRetries(retries); }
        internal virtual void SendFastReceipt(Guid session, Guid transfer, int encodedBytes) { sender.SendFastReceipt(session, transfer, encodedBytes); }
        internal virtual void SendDeliveryReceiptFallback(Guid session, Guid transfer, int encodedBytes)
        {
            if (sender != null) sender.SendDeliveryReceiptFallback(session, transfer, encodedBytes);
            else Send(DeliveryReceipt.Encode(session, transfer, encodedBytes), Guid.NewGuid());
        }
        internal virtual void SetBenchmarkMode(bool active) { sender.SetBenchmarkMode(active); }
        internal virtual long Milliseconds { get { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); } }
        internal virtual void Dispatch(Control owner, Action action)
        {
            if (owner.IsDisposed) { DiagnosticLog.Write("client.bridge.dispatch_rejected", "reason", "owner_disposed"); return; }
            if (owner.InvokeRequired) { try { owner.BeginInvoke(action); } catch (InvalidOperationException error) { DiagnosticLog.Write("client.bridge.dispatch_rejected", "reason", "invoke_failed", "error_type", error.GetType().FullName); } }
            else action();
        }
        internal virtual AudioEndpoint[] EnumerateMicrophones() { return AudioCapture.EnumerateEndpoints(false); }
        internal virtual IDisposable StartMicrophone(string endpointId, Action<float[]> samples)
        {
            AudioCapture capture = new AudioCapture(samples, false, endpointId);
            try { capture.Start(); return capture; }
            catch { capture.Dispose(); throw; }
        }
        internal virtual IDisposable Schedule(Action action, int milliseconds)
        {
            return new System.Threading.Timer(delegate { action(); }, null, milliseconds, Timeout.Infinite);
        }
        internal virtual void Queue(Action action) { ThreadPool.QueueUserWorkItem(delegate { action(); }); }
    }
}
