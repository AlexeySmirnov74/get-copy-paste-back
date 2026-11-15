// Pure fixtures only: no vendor directories, cameras or settings are accessed.
using System;
using System.IO;
using System.Text;
using System.Threading;
using HorizonClient;

internal static class HorizonCameraDiagnosticsTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            TailBounds(); RecentRelevantMessages(); TrustedRtavMessages(); MultilineContextAndCause(); Redaction(); Names(); SnapshotLimits(); FixedRoots();
            Console.WriteLine("Horizon camera diagnostic checks passed: " + checks + "; bounded 256 KiB/80-line tails, recent camera format context, explicit cause status, redaction, fixed vendor roots and 4-snapshot/30-second limits. No vendor files or devices accessed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void TailBounds()
    {
        string first = "discarded partial line\n", last = "2026-11-13T14:47:36.241Z RTAV camera start failed\n";
        using (MemoryStream input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 150000) + first + last)))
        {
            HorizonCameraDiagnostics.Tail tail = HorizonCameraDiagnostics.ReadTail(input, 100);
            Check(tail.BytesRead == 100 && tail.Offset == input.Length - 100, "Tail reads at most its byte budget");
            Check(tail.Text == last, "Partial first line is discarded");
        }
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            byte[] body = encoding.GetBytes(last), bom = encoding.GetPreamble(), bytes = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length); Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
            using (MemoryStream input = new MemoryStream(bytes))
            {
                HorizonCameraDiagnostics.Tail tail = HorizonCameraDiagnostics.ReadTail(input, 1024);
                Check(tail.Text == last && tail.BytesRead == bytes.Length, "BOM encoding is decoded without changing text");
            }
        }
        using (MemoryStream input = new MemoryStream(new byte[0])) Check(HorizonCameraDiagnostics.ReadTail(input, 10).Text.Length == 0, "Empty vendor file is harmless");
        using (MemoryStream input = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 1000))))
            Check(HorizonCameraDiagnostics.ReadTail(input, 16).Text.Length == 0, "A tail consisting only of a partial line is ignored");
        bool rejected = false;
        try { using (MemoryStream input = new MemoryStream()) HorizonCameraDiagnostics.ReadTail(input, HorizonCameraDiagnostics.MaximumTailBytes + 1); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Oversized tail budget rejected");
    }

    private static void RecentRelevantMessages()
    {
        DateTime now = new DateTime(2026, 11, 13, 14, 47, 36, DateTimeKind.Utc);
        string fixture = "2026-11-13T14:47:36.241Z VideoInputWin::StartRenderStream failed to find compatible format\n" +
            "2026-11-13T14:47:35Z unrelated service failed\n" + "2026-11-13T14:47:35Z RTAV codec settings\n" +
            "2026-11-13T14:40:35Z RTAV camera failed old session\n" + "2026-11-13T15:00:35Z RTAV camera failed future session\n" +
            "RTAV camera failed without a timestamp\n" + "2026-11-13T14:47:35Z RTAV clipboard start secret text\n" +
            "2026-11-13T14:47:35Z VirtualCamera start payload=private\n";
        string[] selected = HorizonCameraDiagnostics.SelectLines(fixture, now);
        Check(selected.Length == 2 && selected[0].Contains("compatible format") && selected[1].Contains("codec settings"), "Current camera actions include nearby format context but exclude unrelated services and private records");
        StringBuilder many = new StringBuilder();
        for (int index = 0; index < 100; index++) many.Append("2026-11-13T14:47:35Z RTAV camera failed record=").Append(index).Append('\n');
        selected = HorizonCameraDiagnostics.SelectLines(many.ToString(), now);
        Check(selected.Length == 80 && selected[0].EndsWith("record=20") && selected[79].EndsWith("record=99"), "Only the latest80 relevant lines are retained");
        Check(HorizonCameraDiagnostics.SelectLines(null, now).Length == 0, "Missing text is harmless");
    }

    private static void Redaction()
    {
        string result = HorizonCameraDiagnostics.Redact("2026-11-13T14:47:35Z RTAV start user=alice domain=example address=192.168.1.12:443 url=https://private.example/path file=C:\\Users\\TestUser\\camera.log ipv6=[fe80::1]:443");
        Check(!result.Contains("alice") && !result.Contains("example") && !result.Contains("192.168.1.12") && !result.Contains("private.example") && !result.Contains("TestUser") && !result.Contains("fe80::1"), "Accounts, URLs, IP addresses and user paths are redacted");
        Check(result.Contains("2026-11-13T14:47:35Z") && result.Contains("RTAV start"), "Timestamp and camera diagnosis remain readable");
        Check(HorizonCameraDiagnostics.Redact(new string('x', 4096)).Length == 512, "Individual lines are bounded");
    }

    private static void TrustedRtavMessages()
    {
        DateTime now = new DateTime(2026, 11, 13, 17, 40, 35, DateTimeKind.Utc);
        string fixture = "2026-11-13T17:40:35Z SelectVDOFrameFormatType: Failed to find compatible format 1920x1080\n" +
            "2026-11-13T17:40:35Z ReserveRenderResource: resource unavailable user=alice address=192.168.1.12\n" +
            "2026-11-13T17:40:35Z StopRenderStream complete\n" +
            "2026-11-13T17:40:35Z SelectFormat payload=private clipboard=text\n" +
            "2026-11-13T17:40:35Z StartRenderStream authorization=private\n" +
            "2026-11-13T17:30:35Z SelectVDOFrameFormatType failed stale\n" +
            "2026-11-13T17:40:35Z periodic heartbeat\n";
        Check(HorizonCameraDiagnostics.SelectLines(fixture, now).Length == 0, "Generic vendor logs still require an explicit camera component");
        string[] selected = HorizonCameraDiagnostics.SelectLines(fixture, now, true);
        Check(selected.Length == 3 && selected[0].Contains("compatible format") && selected[2].Contains("StopRenderStream"),
            "Trusted RTAV file context preserves function-only format, resource and stop diagnostics");
        Check(!String.Join("\n", selected).Contains("alice") && !String.Join("\n", selected).Contains("192.168.1.12") &&
            !String.Join("\n", selected).Contains("private"), "Trusted RTAV context retains sensitive-line exclusion and account/IP redaction");
        StringBuilder many = new StringBuilder();
        for (int index = 0; index < 100; index++) many.Append("2026-11-13T17:40:35Z SelectVDOFrameFormatType failed record=").Append(index).Append('\n');
        selected = HorizonCameraDiagnostics.SelectLines(many.ToString(), now, true);
        Check(selected.Length == 80 && selected[0].EndsWith("record=20") && selected[79].EndsWith("record=99"), "Trusted RTAV context retains the latest80-line limit");
    }

    private static void MultilineContextAndCause()
    {
        DateTime now = new DateTime(2026, 11, 13, 18, 7, 24, DateTimeKind.Utc);
        string fixture = "2026-11-13T18:07:23Z In(05) Version=8.13.0 Build=12345\n" +
            "2026-11-13T18:07:24Z In(05) VideoDeviceServer::StartProcessing - Received video device prefs:\n" +
            "2026-11-13T18:07:24Z In(05)+ Width=1280 Height=720\n" +
            "    FPS=30\n" + "    PixelFormat=RGB24 Codec=H264\n" + "    DeviceIndex=2\n" +
            "2026-11-13T18:07:24Z unrelated periodic heartbeat\n" +
            "2026-11-13T18:07:24Z VideoDeviceServer payload=private-data\n" +
            "    Width=9999\n" +
            "2026-11-13T18:07:24Z VideoDeviceServer::StopProcessing - Video Session Stopped\n";
        HorizonCameraDiagnostics.Selection selected = HorizonCameraDiagnostics.SelectMessages(fixture, now, true);
        string joined = String.Join("\n", selected.Lines);
        Check(joined.Contains("Version=8.13.0") && joined.Contains("Build=12345"), "Bounded preceding version/build context is retained");
        Check(joined.Contains("Width=1280 Height=720") && joined.Contains("FPS=30") && joined.Contains("PixelFormat=RGB24 Codec=H264") && joined.Contains("DeviceIndex=2"),
            "Timestamped and indented preference continuations retain actual resolution, frame rate, pixel format and codec");
        Check(!joined.Contains("heartbeat") && !joined.Contains("private-data") && !joined.Contains("9999"), "Unrelated records, private content and its unstamped continuations are excluded");
        Check(selected.FirstUtc == now.AddSeconds(-1) && selected.LastUtc == now, "Selection metadata reports the original first/last vendor log timestamps");
        Check(!selected.ExplicitFailurePresent, "Start/Ack/Stop alone explicitly supplies no vendor failure cause");
        string prefixed = "2026-11-13T18:07:24Z VideoDeviceServer::StartProcessing - preferences:\n" +
            "    dwFrameWidth=1280 dwFrameHeight=720\n    fourcc=0x32595559 AvgTimePerFrame=333333\n";
        joined = String.Join("\n", HorizonCameraDiagnostics.SelectLines(prefixed, now, true));
        Check(joined.Contains("dwFrameWidth=1280") && joined.Contains("fourcc=0x32595559"), "Native-prefixed preference fields and FourCC/frame interval details are retained");
        selected = HorizonCameraDiagnostics.SelectMessages(fixture + "2026-11-13T18:07:24Z Er(02) VideoInputWin::StartRenderStream - Failed HRESULT=0x80070005\n", now, true);
        Check(selected.ExplicitFailurePresent, "An actual vendor error line is distinguished from a normal start/stop sequence");
        foreach (string privateField in new[] { "key=fixture", "key_file=C:\\fixture.key", "private_key=fixture", "authorization=fixture", "clipboard=fixture" })
        {
            string text = "2026-11-13T18:07:24Z RTAV start failed " + privateField + "\n";
            Check(HorizonCameraDiagnostics.SelectLines(text, now, true).Length == 0, "Secret/key/clipboard fields cannot enter context diagnostics");
        }
        string old = "2026-11-13T17:00:00Z RTAV start\n    Width=9999 Height=9999\n";
        Check(HorizonCameraDiagnostics.SelectLines(old, now, true).Length == 0, "Multiline context cannot inherit a stale timestamp");
        string far = "2026-11-13T18:07:24Z RTAV start\n" + new string('\n', 12) + "2026-11-13T18:07:24Z Width=9999\n";
        Check(HorizonCameraDiagnostics.SelectLines(far, now, true).Length == 1, "Format context is limited by physical distance to a camera event");
        string noAnchor = "2026-11-13T18:07:24Z Width=9999 Height=9999\n";
        Check(HorizonCameraDiagnostics.SelectLines(noAnchor, now, true).Length == 0, "A format field alone cannot turn an unrelated record into a camera diagnosis");
    }

    private static void Names()
    {
        foreach (string good in new[] { "debug-2026-11-13.txt", "rtav_server.log", "vmware-camera.log", "log-2026-11-13.txt", "omnissa-horizon-client-2026-11-13-123400-12.txt", "horizon-protocol-12.log" }) Check(HorizonCameraDiagnostics.IsVendorFileName(good), "Known vendor name accepted");
        foreach (string bad in new[] { "private.log", "rtav-secret.bin", "..\\rtav.log", "C:\\Users\\rtav.log", "" }) Check(!HorizonCameraDiagnostics.IsVendorFileName(bad), "Unrelated/path input rejected");
    }

    private static void SnapshotLimits()
    {
        var schedule = new HorizonCameraDiagnostics.SnapshotSchedule();
        int id; string reason;
        Check(!schedule.TryAcquire(-1, out id, out reason) && reason == "invalid_clock", "An invalid clock cannot consume a snapshot");
        Check(schedule.TryAcquire(0, out id, out reason) && id == 1, "The first failure can capture immediately at process startup");
        Check(!schedule.TryAcquire(29999, out id, out reason) && reason == "minimum_interval", "Events inside30 seconds cannot queue another snapshot");
        Check(schedule.TryAcquire(30000, out id, out reason) && id == 2, "A later stop/finish event captures new log data after30 seconds");
        Check(!schedule.TryAcquire(0, out id, out reason), "A backwards clock cannot bypass the interval");
        Check(schedule.TryAcquire(60000, out id, out reason) && id == 3, "The third snapshot is bounded and numbered");
        Check(schedule.TryAcquire(90000, out id, out reason) && id == 4, "The fourth snapshot is bounded and numbered");
        Check(!schedule.TryAcquire(Int64.MaxValue, out id, out reason) && reason == "process_snapshot_limit", "No fifth snapshot is allowed even much later");
        var concurrent = new HorizonCameraDiagnostics.SnapshotSchedule();
        int accepted = 0;
        var threads = new Thread[16];
        for (int index = 0; index < threads.Length; index++)
        {
            threads[index] = new Thread(delegate() { int number; string why; if (concurrent.TryAcquire(0, out number, out why)) Interlocked.Increment(ref accepted); });
            threads[index].Start();
        }
        foreach (Thread thread in threads) thread.Join();
        Check(accepted == 1, "Concurrent capture requests cannot exceed the interval budget");
        Check(HorizonCameraDiagnostics.NormalizeTrigger(null) == "unspecified", "Missing trigger is explicit");
        Check(HorizonCameraDiagnostics.NormalizeTrigger("server_benchmark_started\r\n").IndexOf('\n') < 0 && HorizonCameraDiagnostics.NormalizeTrigger(new string('x', 100)).Length == 64,
            "Trigger metadata is single-line and bounded");
    }

    private static void FixedRoots()
    {
        HorizonCameraDiagnostics.KnownRoot[] roots = HorizonCameraDiagnostics.KnownRoots("C:\\ProgramData", "C:\\Users\\fixture\\AppData\\Local", "C:\\Users\\fixture\\AppData\\Local\\Temp", "fixture", "C:\\Users\\fixture");
        Check(roots.Length == 10, "Only fixed vendor subdirectories plus current user's two temp roots are considered");
        bool oldClient = false, newClient = false, agent = false, temp = false;
        var paths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HorizonCameraDiagnostics.KnownRoot root in roots)
        {
            Check(paths.Add(root.Path), "Current account and profile temp directories are deduplicated");
            Check(!root.Label.Contains("fixture") && !root.Label.Contains("C:"), "Source labels cannot disclose the user's profile path");
            oldClient |= root.Path == "C:\\Users\\fixture\\AppData\\Local\\VMware\\VDM\\logs";
            newClient |= root.Path == "C:\\Users\\fixture\\AppData\\Local\\Omnissa\\Horizon\\logs";
            agent |= root.Path == "C:\\ProgramData\\Omnissa\\Horizon\\logs";
            temp |= root.Path == "C:\\Users\\fixture\\AppData\\Local\\Temp\\vmware-fixture";
        }
        Check(oldClient && newClient && agent && temp, "Both VMware and Omnissa agent/client log locations are included");
        roots = HorizonCameraDiagnostics.KnownRoots(null, null, "C:\\Temp", "..\\other-user", null);
        Check(roots.Length == 0, "Invalid account path text cannot escape to another user's directory");
        roots = HorizonCameraDiagnostics.KnownRoots("relative", "relative", "relative", "fixture", null);
        Check(roots.Length == 0, "Missing absolute environment roots never fall back to working-directory scans");
    }

    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
}
