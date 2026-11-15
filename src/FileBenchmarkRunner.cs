using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;

namespace HorizonClient
{
    internal enum ProfileTestState { Pending, Preparing, Receiving, Passed, NoData, Partial, InvalidData, Cancelled, NoReply }

    internal sealed class ProfileTestResult
    {
        internal VideoProfile Profile;
        internal ProfileTestState State;
        internal long ReceivedBytes, FileBytes, ElapsedMilliseconds;
        internal int Width, Height, Frames;
        internal bool ReceiverConfirmed;
        internal FileBenchmarkFailure Failure;
        internal string ServerFinishReason;
        internal double MegabitsPerSecond { get { return ElapsedMilliseconds > 0 ? ReceivedBytes * 8.0 / ElapsedMilliseconds / 1000 : 0; } }
        internal ProfileTestResult Copy() { return (ProfileTestResult)MemberwiseClone(); }
    }

    // Pure state machine: only a matching complete-file acknowledgement can pass
    // a profile. No device, file, clipboard or network operations happen here.
    internal sealed class FileBenchmarkRunner
    {
        private readonly object gate = new object();
        private readonly Func<long> now;
        private readonly Guid session;
        private readonly ulong seed;
        private readonly long maximumRun;
        private readonly int expectedWidth, expectedHeight;
        private readonly List<ProfileTestResult> results = new List<ProfileTestResult>();
        private readonly List<FileBenchmarkCommand> commands = new List<FileBenchmarkCommand>();
        private readonly List<long> endedAt = new List<long>();
        private readonly long created;
        private int position;
        private long prepared, started = -1, lastProgress, lastReport, finished = -1;
        private long acceptedReports, ignoredReports;
        private bool receiverReplied;
        private bool cancelled;
        private FileBenchmarkCommand command;
        private int missingSegment = -1;
        private int[] missingIndices;
        private long missingRevision, missingReceivedAt, lastMissingElapsed = -1;
        private long formatReportElapsed = -1;
        private int actualWidth, actualHeight;
        private string formatCheckFailureReason;
        internal const int NoDataMilliseconds = 15000;
        internal const int ReadyTimeoutMilliseconds = 35000;
        internal const int ReportSilenceGraceMilliseconds = 35000;
        internal const int MaximumRunGraceMilliseconds = 15000;
        internal const int RetransmitLeaseMilliseconds = 12000;
        internal const int LateCompletionGraceMilliseconds = 15000;
        internal const int PrepareAnnouncementMilliseconds = 1500;

        internal FileBenchmarkRunner(Guid session, VideoProfile[] profiles, long bytes, ulong seed, long maximumRunMilliseconds)
            : this(session, profiles, bytes, seed, maximumRunMilliseconds, NewClock()) { }

        private static Func<long> NewClock() { Stopwatch watch = Stopwatch.StartNew(); return delegate { return watch.ElapsedMilliseconds; }; }

        internal FileBenchmarkRunner(Guid session, VideoProfile[] profiles, long bytes, ulong seed, long maximumRunMilliseconds, Func<long> clock)
            : this(session, profiles, bytes, seed, maximumRunMilliseconds, clock, 0, 0, false) { }

        internal FileBenchmarkRunner(Guid session, VideoProfile[] profiles, long bytes, ulong seed, long maximumRunMilliseconds, int expectedWidth, int expectedHeight)
            : this(session, profiles, bytes, seed, maximumRunMilliseconds, NewClock(), expectedWidth, expectedHeight, true) { }

        internal FileBenchmarkRunner(Guid session, VideoProfile[] profiles, long bytes, ulong seed, long maximumRunMilliseconds, Func<long> clock, int expectedWidth, int expectedHeight)
            : this(session, profiles, bytes, seed, maximumRunMilliseconds, clock, expectedWidth, expectedHeight, true) { }

        private FileBenchmarkRunner(Guid session, VideoProfile[] profiles, long bytes, ulong seed, long maximumRunMilliseconds, Func<long> clock, int expectedWidth, int expectedHeight, bool strictFormat)
        {
            if (session == Guid.Empty || profiles == null || profiles.Length == 0 || bytes < 1024 || bytes > 100L * 1024 * 1024 || maximumRunMilliseconds < 0 || maximumRunMilliseconds > 7L * 86400000 || (maximumRunMilliseconds > 0 && maximumRunMilliseconds < 1000) || clock == null) throw new ArgumentException("Invalid file benchmark configuration.");
            if (strictFormat && (expectedWidth < 1 || expectedWidth > 7680 || expectedHeight < 1 || expectedHeight > 4320)) throw new ArgumentException("Invalid expected camera format.");
            this.session = session; this.seed = seed; maximumRun = maximumRunMilliseconds; now = clock; created = now();
            this.expectedWidth = expectedWidth; this.expectedHeight = expectedHeight;
            foreach (VideoProfile profile in profiles)
            {
                if (!VisualProtocol.IsDefinedProfile(profile)) throw new ArgumentException("Unknown benchmark profile.");
                results.Add(new ProfileTestResult { Profile = profile, FileBytes = bytes, State = ProfileTestState.Pending });
            }
            PrepareNext();
        }

        internal bool Finished { get { lock (gate) return finished >= 0; } }
        internal bool TailComplete { get { lock (gate) return finished >= 0 && now() - finished >= 3000; } }
        internal long TotalMilliseconds { get { lock (gate) return (finished >= 0 ? finished : now()) - created; } }
        internal int CurrentIndex { get { lock (gate) return Math.Min(position, results.Count - 1); } }
        internal long NoReplyMilliseconds { get { lock (gate) return NoReplyBudget(command.Profile, command.FileSize); } }
        internal int ExpectedWidth { get { return expectedWidth; } }
        internal int ExpectedHeight { get { return expectedHeight; } }
        internal int ActualWidth { get { lock (gate) return actualWidth; } }
        internal int ActualHeight { get { lock (gate) return actualHeight; } }
        internal string FormatCheckFailureReason { get { lock (gate) return formatCheckFailureReason; } }
        private bool StrictFormat { get { return expectedWidth > 0; } }

        private long NoReplyBudget(VideoProfile profile, long fileBytes)
        {
            long packets = (fileBytes + VisualProtocol.GetChunkSize(profile) - 1) / VisualProtocol.GetChunkSize(profile);
            long frames = (packets + VisualProtocol.GetPacketsPerFrame(profile) - 1) / VisualProtocol.GetPacketsPerFrame(profile);
            double framesPerSecond = profile == VideoProfile.Compatible ? 5.0 : 30.0;
            // Include one complete source pass, control-frame overhead, pacing
            // margin and reverse-audio grace before declaring it unconfirmed.
            long budget = Math.Max(ReadyTimeoutMilliseconds, (long)Math.Ceiling(frames / framesPerSecond * 31.0 / 27.0 * 1.2 * 1000.0) + 5000);
            return maximumRun > 0 ? Math.Min(budget, maximumRun + MaximumRunGraceMilliseconds) : budget;
        }

        internal ProfileTestResult[] Snapshot()
        {
            lock (gate) { var copy = new ProfileTestResult[results.Count]; for (int i = 0; i < copy.Length; i++) copy[i] = results[i].Copy(); return copy; }
        }

        internal FileBenchmarkCommand[] ReportCommands()
        {
            lock (gate)
            {
                var candidates = new List<FileBenchmarkCommand>(); long time = now();
                for (int i = commands.Count - 1; i >= 0 && candidates.Count < 8; i--)
                    if ((i == position && finished < 0) || EligibleLateResult(i, time)) candidates.Add(FileBenchmarkProtocol.Copy(commands[i]));
                return candidates.ToArray();
            }
        }

        private bool EligibleLateResult(int index, long time)
        {
            ProfileTestResult result = results[index];
            return !cancelled && endedAt[index] >= 0 && time >= endedAt[index] && time - endedAt[index] <= LateCompletionGraceMilliseconds &&
                (result.State == ProfileTestState.NoReply || result.State == ProfileTestState.NoData || result.State == ProfileTestState.Partial) &&
                (result.ServerFinishReason == "receiver_reply_unavailable" || result.ServerFinishReason == "no_data" ||
                result.ServerFinishReason == "receiver_report_timeout" || result.ServerFinishReason == "receiver_timeout" || result.ServerFinishReason == "maximum_run");
        }

        private bool AcceptLateCompletion(FileBenchmarkReport report)
        {
            if (report.Status != FileBenchmarkStatus.Completed || report.Failure != FileBenchmarkFailure.None || report.Session != session ||
                report.ReceivedBytes != report.TotalBytes || report.ElapsedMilliseconds < 1 || report.ElapsedMilliseconds > 30L * 86400000 ||
                report.FrameWidth < 0 || report.FrameWidth > 7680 || report.FrameHeight < 0 || report.FrameHeight > 4320 || report.Frames < 0 ||
                report.MissingIndices != null && report.MissingIndices.Length != 0) return false;
            long time = now();
            for (int i = commands.Count - 1; i >= 0; i--)
            {
                FileBenchmarkCommand known = commands[i];
                if (!EligibleLateResult(i, time) || report.Test != known.Test || report.Profile != known.Profile || report.TotalBytes != known.FileSize) continue;
                if (StrictFormat && (report.FrameWidth != expectedWidth || report.FrameHeight != expectedHeight)) continue;
                ProfileTestResult result = results[i];
                result.State = ProfileTestState.Passed; result.Failure = FileBenchmarkFailure.None; result.ServerFinishReason = "late_verified_complete";
                result.ReceivedBytes = known.FileSize; result.ElapsedMilliseconds = report.ElapsedMilliseconds; result.Width = report.FrameWidth;
                result.Height = report.FrameHeight; result.Frames = report.Frames; result.ReceiverConfirmed = true;
                DiagnosticLog.Write("server.file.late_completed", "session", session, "test", known.Test, "profile", (int)known.Profile,
                    "age_ms", time - endedAt[i], "received_bytes", result.ReceivedBytes, "receiver_elapsed_ms", result.ElapsedMilliseconds);
                return true;
            }
            return false;
        }

        internal FileBenchmarkCommand CurrentCommand
        {
            get
            {
                lock (gate)
                {
                    TickLocked();
                    long time = now();
                    bool retransmitActive = missingIndices != null && time >= missingReceivedAt && time - missingReceivedAt <= RetransmitLeaseMilliseconds;
                    return new FileBenchmarkCommand { Session = command.Session, Test = command.Test, Profile = command.Profile, FileSize = command.FileSize,
                        SegmentSize = command.SegmentSize, Seed = command.Seed, InactivityMilliseconds = command.InactivityMilliseconds,
                        MaximumRunMilliseconds = command.MaximumRunMilliseconds, Phase = finished >= 0 ? FileBenchmarkPhase.Cancel : command.Phase,
                        CompactReports = command.CompactReports,
                        RetransmitSegment = retransmitActive ? missingSegment : -1,
                        RetransmitIndices = retransmitActive ? (int[])missingIndices.Clone() : null,
                        RetransmitRevision = retransmitActive ? missingRevision : 0 };
                }
            }
        }

        internal void Tick() { lock (gate) TickLocked(); }
        private void TickLocked()
        {
            if (finished >= 0) return;
            long time = now();
            ProfileTestResult result = results[position];
            if (started < 0)
            {
                if (StrictFormat)
                {
                    // A fresh, identity-bound Ready must confirm the actual
                    // capture format before even one test-file frame is sent.
                    if (time - prepared >= ReadyTimeoutMilliseconds) FailFormatCheck("capture_format_unconfirmed", 0, 0);
                    return;
                }
                // Video is the channel under test. A missing reverse-audio Ready
                // must not prevent the server from ever transmitting its data.
                if (time - prepared >= PrepareAnnouncementMilliseconds)
                    StartTransmission("prepare_announcement_complete");
                return;
            }
            // Before any data, keep the original no-data bound. Once data has
            // arrived, a fresh receiver heartbeat proves an active retry loop
            // even when all its recent valid packets were duplicates.
            bool maximumExpired = maximumRun > 0 && time - started >= maximumRun + MaximumRunGraceMilliseconds;
            long activity = result.ReceivedBytes == 0 ? lastProgress : lastReport;
            bool activityExpired = !receiverReplied ? time - started >= NoReplyBudget(result.Profile, result.FileBytes) : time - activity >= ReportSilenceGraceMilliseconds;
            if (activityExpired || maximumExpired)
            {
                result.State = !receiverReplied ? ProfileTestState.NoReply : result.ReceivedBytes == 0 ? ProfileTestState.NoData : ProfileTestState.Partial;
                result.Failure = !receiverReplied ? FileBenchmarkFailure.None : maximumExpired ? FileBenchmarkFailure.MaximumRun : result.ReceivedBytes == 0 ? FileBenchmarkFailure.NoData : FileBenchmarkFailure.Stalled;
                result.ServerFinishReason = !receiverReplied ? "receiver_reply_unavailable" : maximumExpired ? "maximum_run" : result.ReceivedBytes == 0 ? "no_data" : "receiver_report_timeout";
                result.ElapsedMilliseconds = Math.Max(result.ElapsedMilliseconds, Math.Max(1, time - started));
                Log("server.file.timeout", "reason", result.ServerFinishReason);
                Advance();
            }
        }

        internal void Receive(FileBenchmarkReport report)
        {
            lock (gate)
            {
                if (report == null) { Ignore("null_report", report); return; }
                if (!FileBenchmarkProtocol.ValidReport(report)) { Ignore("invalid_report", report); return; }
                if (StrictFormat && started < 0) TickLocked();
                if (AcceptLateCompletion(report)) return;
                if (finished >= 0) { Ignore("finished", report); return; }
                if (report.Session != session) { Ignore("session_mismatch", report); return; }
                if (report.Test != command.Test) { Ignore("test_mismatch", report); return; }
                if (report.Profile != command.Profile) { Ignore("profile_mismatch", report); return; }
                if (report.TotalBytes != command.FileSize) { Ignore("file_size_mismatch", report); return; }
                if (report.ReceivedBytes < 0 || report.ReceivedBytes > command.FileSize || report.ElapsedMilliseconds < 0 || report.ElapsedMilliseconds > 30L * 86400000)
                { Ignore("invalid_counters", report); return; }
                if (report.MissingIndices != null && report.MissingIndices.Length != 0 &&
                    (report.Status != FileBenchmarkStatus.Progress || report.Failure != FileBenchmarkFailure.None || report.ReceivedBytes < 1 ||
                    report.ReceivedBytes >= command.FileSize || !FileBenchmarkProtocol.ValidMissing(command, report.MissingSegment, report.MissingIndices)))
                { Ignore("invalid_missing_request", report); return; }
                ProfileTestResult result = results[position];
                if (report.Status == FileBenchmarkStatus.Ready)
                {
                    if (!receiverReplied)
                    {
                        if (StrictFormat)
                        {
                            if (report.FrameWidth < 1 || report.FrameHeight < 1) { Ignore("capture_format_unknown", report); return; }
                            actualWidth = report.FrameWidth; actualHeight = report.FrameHeight;
                            result.Width = actualWidth; result.Height = actualHeight; result.ReceiverConfirmed = true;
                            if (actualWidth != expectedWidth || actualHeight != expectedHeight)
                            { receiverReplied = true; acceptedReports++; FailFormatCheck("capture_format_mismatch", actualWidth, actualHeight); return; }
                            formatReportElapsed = report.ElapsedMilliseconds;
                        }
                        receiverReplied = true;
                        result.ReceiverConfirmed = true;
                        if (started < 0) StartTransmission("receiver_ready");
                        else lastProgress = lastReport = now();
                        acceptedReports++; Log("server.file.ready", "receiver_elapsed_ms", report.ElapsedMilliseconds);
                    }
                    else Ignore("repeated_ready", report);
                    return;
                }
                if (report.Status == FileBenchmarkStatus.Completed && report.ReceivedBytes != command.FileSize) { Ignore("incomplete_completion", report); return; }
                if (report.ReceivedBytes < result.ReceivedBytes) { Ignore("stale_byte_count", report); return; }
                if (report.Status != FileBenchmarkStatus.Progress && report.Status != FileBenchmarkStatus.Completed &&
                    report.Status != FileBenchmarkStatus.InvalidData && report.Status != FileBenchmarkStatus.TimedOut)
                { Ignore("unsupported_status", report); return; }
                if (StrictFormat)
                {
                    if (started < 0) { Ignore("capture_format_ready_required", report); return; }
                    if (report.FrameWidth < 1 || report.FrameHeight < 1) { Ignore("capture_format_unknown", report); return; }
                    if (report.ElapsedMilliseconds > formatReportElapsed)
                    {
                        // Delayed reports must not mistake an older capture
                        // format for a change in the current receiving stream.
                        formatReportElapsed = report.ElapsedMilliseconds;
                        actualWidth = report.FrameWidth; actualHeight = report.FrameHeight;
                        if (actualWidth != expectedWidth || actualHeight != expectedHeight)
                        { acceptedReports++; FailFormatCheck("capture_format_mismatch", actualWidth, actualHeight); return; }
                    }
                    else if (report.FrameWidth != expectedWidth || report.FrameHeight != expectedHeight)
                    { Ignore("stale_capture_format", report); return; }
                }
                if (started < 0) StartTransmission("matching_receiver_report");
                receiverReplied = true;
                result.ReceiverConfirmed = true;
                acceptedReports++;
                if (report.MissingIndices != null && report.MissingIndices.Length != 0 && report.ElapsedMilliseconds > lastMissingElapsed &&
                    report.ElapsedMilliseconds >= result.ElapsedMilliseconds)
                {
                    missingSegment = report.MissingSegment; missingIndices = (int[])report.MissingIndices.Clone();
                    missingReceivedAt = now(); lastMissingElapsed = report.ElapsedMilliseconds; missingRevision++;
                    DiagnosticLog.Write("server.file.nack.accepted", "session", session, "test", command.Test, "profile", (int)command.Profile,
                        "segment", missingSegment, "indices", String.Join(",", Array.ConvertAll(missingIndices, delegate(int index) { return index.ToString(); })),
                        "revision", missingRevision, "receiver_elapsed_ms", report.ElapsedMilliseconds);
                }
                if (report.ReceivedBytes >= result.ReceivedBytes)
                {
                    if (report.ReceivedBytes > result.ReceivedBytes || report.ElapsedMilliseconds > result.ElapsedMilliseconds) lastReport = now();
                    if (report.ReceivedBytes > result.ReceivedBytes) lastProgress = now();
                    result.ReceivedBytes = report.ReceivedBytes;
                    result.ElapsedMilliseconds = Math.Max(result.ElapsedMilliseconds, Math.Max(1, report.ElapsedMilliseconds));
                    result.Width = report.FrameWidth; result.Height = report.FrameHeight; result.Frames = report.Frames;
                }
                Log("server.file.report.accepted", "report_status", report.Status, "report_failure", report.Failure, "receiver_elapsed_ms", report.ElapsedMilliseconds);
                if (report.Status == FileBenchmarkStatus.Completed)
                {
                    if (report.ReceivedBytes != command.FileSize) return;
                    result.State = ProfileTestState.Passed; result.Failure = FileBenchmarkFailure.None; result.ServerFinishReason = "verified_complete"; Advance();
                }
                else if (report.Status == FileBenchmarkStatus.InvalidData) { result.State = ProfileTestState.InvalidData; result.Failure = report.Failure; result.ServerFinishReason = "receiver_invalid_data"; Advance(); }
                else if (report.Status == FileBenchmarkStatus.TimedOut) { result.State = result.ReceivedBytes == 0 ? ProfileTestState.NoData : ProfileTestState.Partial; result.Failure = report.Failure; result.ServerFinishReason = "receiver_timeout"; Advance(); }
            }
        }

        internal void Cancel()
        {
            lock (gate)
            {
                if (finished >= 0) return;
                cancelled = true;
                for (int i = position; i < results.Count; i++) { results[i].State = ProfileTestState.Cancelled; results[i].ServerFinishReason = "cancelled"; }
                Log("server.file.cancelled");
                finished = now();
            }
        }

        private void FailFormatCheck(string reason, int width, int height)
        {
            cancelled = true; formatCheckFailureReason = reason;
            actualWidth = width; actualHeight = height;
            ProfileTestResult current = results[position];
            current.Width = width; current.Height = height;
            for (int i = position; i < results.Count; i++)
            { results[i].State = ProfileTestState.Cancelled; results[i].ServerFinishReason = reason; }
            Log("server.file.capture_format_rejected", "reason", reason, "expected_width", expectedWidth, "expected_height", expectedHeight,
                "actual_width", width, "actual_height", height);
            finished = now();
        }

        private void Advance()
        {
            Log("server.file.profile.finished");
            endedAt[position] = now();
            position++;
            if (position >= results.Count) { finished = now(); return; }
            PrepareNext();
        }

        private void PrepareNext()
        {
            prepared = now(); started = -1; lastProgress = lastReport = prepared; acceptedReports = ignoredReports = 0; receiverReplied = false;
            formatReportElapsed = -1; actualWidth = actualHeight = 0;
            missingSegment = -1; missingIndices = null; missingRevision = 0; lastMissingElapsed = -1;
            ProfileTestResult result = results[position]; result.State = ProfileTestState.Preparing;
            command = new FileBenchmarkCommand { Session = session, Test = Guid.NewGuid(), Profile = result.Profile, FileSize = result.FileBytes, Seed = seed,
                SegmentSize = VisualProtocol.MaxTransferBytes, InactivityMilliseconds = NoDataMilliseconds, MaximumRunMilliseconds = maximumRun,
                Phase = FileBenchmarkPhase.Prepare, CompactReports = true };
            commands.Add(command); endedAt.Add(-1);
            Log("server.file.prepare");
        }

        private void StartTransmission(string reason)
        {
            started = lastProgress = lastReport = now();
            command.Phase = FileBenchmarkPhase.Start;
            results[position].State = ProfileTestState.Receiving;
            Log("server.file.transmit_started", "reason", reason, "receiver_replied", receiverReplied);
        }

        private void Ignore(string reason, FileBenchmarkReport report)
        {
            ignoredReports++;
            Log("server.file.report.ignored", "reason", reason, "report_test", report == null ? Guid.Empty : report.Test, "report_status", report == null ? "null" : report.Status.ToString(),
                "report_bytes", report == null ? -1 : report.ReceivedBytes, "report_elapsed_ms", report == null ? -1 : report.ElapsedMilliseconds);
        }

        private void Log(string eventName, params object[] extra)
        {
            if (!DiagnosticLog.IsEnabled || command == null) return;
            ProfileTestResult result = results[Math.Min(position, results.Count - 1)]; long time = now();
            var fields = new List<object> { "session", session, "test", command.Test, "profile", (int)command.Profile, "phase", command.Phase,
                "state", result.State, "failure", result.Failure, "finish_reason", result.ServerFinishReason, "received_bytes", result.ReceivedBytes,
                "file_bytes", command.FileSize, "elapsed_ms", result.ElapsedMilliseconds, "goodput_mbit_s", result.MegabitsPerSecond,
                "server_elapsed_ms", started < 0 ? 0 : Math.Max(0, time - started), "last_report_age_ms", Math.Max(0, time - lastReport),
                "last_progress_age_ms", Math.Max(0, time - lastProgress), "accepted_reports", acceptedReports, "ignored_reports", ignoredReports,
                "receiver_replied", receiverReplied,
                "strict_capture_format", StrictFormat, "expected_width", expectedWidth, "expected_height", expectedHeight,
                "no_reply_budget_ms", NoReplyBudget(result.Profile, result.FileBytes),
                "maximum_run_ms", maximumRun, "width", result.Width, "height", result.Height, "frames", result.Frames };
            fields.AddRange(extra); DiagnosticLog.Write(eventName, fields.ToArray());
        }
    }

    // Keeps at most one <=32MiB source segment in memory, even for a 100MiB file.
    internal sealed class FileBenchmarkVideoSource
    {
        private Guid test;
        private int segment, index, repeat, round, position, offset;
        private int cachedSegment = -1;
        private byte[] bytes;
        private long retransmitRevision;
        private int retransmitSegment, retransmitPosition, retransmitRepeat;
        private int[] retransmitIndices;
        internal Guid LastTest { get; private set; }
        internal int LastSegment { get; private set; }
        internal int LastPacketIndex { get; private set; }
        internal int LastPacketCount { get; private set; }
        internal int LastRound { get; private set; }
        internal int SegmentPacketCount { get; private set; }
        internal bool LastWasRetransmission { get; private set; }

        internal Bitmap Next(FileBenchmarkCommand command, int framesPerSecond)
        {
            if (command == null || command.Phase != FileBenchmarkPhase.Start) throw new InvalidOperationException("The benchmark must be in its transmission phase before data is sent.");
            if (test != command.Test) { test = command.Test; segment = index = repeat = round = position = offset = 0; cachedSegment = -1; bytes = null; retransmitRevision = 0; retransmitIndices = null; }
            Bitmap retransmission = NextRetransmission(command);
            if (retransmission != null) return retransmission;
            LastWasRetransmission = false;
            if (cachedSegment != segment)
            {
                bytes = new byte[FileBenchmarkProtocol.SegmentLength(command, segment)];
                BenchmarkData.Fill(command.Seed, (long)segment * command.SegmentSize, bytes, 0, bytes.Length); cachedSegment = segment;
            }
            int size = VisualProtocol.GetChunkSize(command.Profile), slots = VisualProtocol.GetPacketsPerFrame(command.Profile);
            int count = Math.Max(1, (bytes.Length + size - 1) / size);
            Bitmap frame = VisualProtocol.EncodePacketFrame(FileBenchmarkProtocol.SegmentTransferId(command.Test, segment), index, bytes, command.Profile);
            LastTest = command.Test; LastSegment = segment; LastPacketIndex = index; LastPacketCount = Math.Min(slots, count - index); LastRound = round; SegmentPacketCount = count;
            if (++repeat >= (command.Profile == VideoProfile.Compatible ? Math.Max(1, framesPerSecond / 5) : 1))
            {
                repeat = 0; position += Math.Min(slots, count - index);
                if (position >= count)
                {
                    segment++; index = position = 0;
                    if (segment >= FileBenchmarkProtocol.SegmentCount(command)) { segment = 0; round++; }
                    // Drift by one packet: a batch-sized step may share factors
                    // with packet count and permanently miss periodic frame losses.
                    int nextLength = FileBenchmarkProtocol.SegmentLength(command, segment);
                    int nextCount = Math.Max(1, (nextLength + size - 1) / size);
                    offset = index = round % nextCount;
                }
                else index = (offset + position) % count;
            }
            return frame;
        }

        private Bitmap NextRetransmission(FileBenchmarkCommand command)
        {
            if (command.RetransmitRevision <= 0 || command.RetransmitIndices == null)
            { retransmitIndices = null; return null; }
            if (command.RetransmitRevision > retransmitRevision)
            {
                retransmitRevision = command.RetransmitRevision;
                retransmitIndices = FileBenchmarkProtocol.ValidMissing(command, command.RetransmitSegment, command.RetransmitIndices) ?
                    (int[])command.RetransmitIndices.Clone() : null;
                retransmitSegment = command.RetransmitSegment; retransmitPosition = retransmitRepeat = 0;
            }
            if (retransmitIndices == null || retransmitPosition >= retransmitIndices.Length) return null;
            int packetIndex = retransmitIndices[retransmitPosition], size = VisualProtocol.GetChunkSize(command.Profile);
            int length = FileBenchmarkProtocol.SegmentLength(command, retransmitSegment), count = (length + size - 1) / size;
            int packetLength = Math.Min(size, length - packetIndex * size);
            var chunk = new byte[packetLength];
            BenchmarkData.Fill(command.Seed, (long)retransmitSegment * command.SegmentSize + (long)packetIndex * size, chunk, 0, chunk.Length);
            Bitmap frame = VisualProtocol.EncodeFrame(FileBenchmarkProtocol.SegmentTransferId(command.Test, retransmitSegment), packetIndex, count, length, chunk, command.Profile);
            LastTest = command.Test; LastSegment = retransmitSegment; LastPacketIndex = packetIndex; LastPacketCount = 1; LastRound = round; SegmentPacketCount = count; LastWasRetransmission = true;
            // Three adjacent frames survive ordinary30→15 FPS decimation; a
            // finite request never prevents the normal sweep from resuming.
            if (++retransmitRepeat == 3) { retransmitRepeat = 0; retransmitPosition++; }
            return frame;
        }
    }
}
