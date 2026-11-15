using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HorizonClient;

internal static class CaptureRetryPolicyTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            var policy = new CaptureRetryPolicy();
            Mode(policy, CaptureBackend.DirectShowHighest);
            Check(policy.CanOpen(0), "A selected camera first requests its highest real bounded native mode.");
            policy.RecordFailure(new CameraStreamException(3, unchecked((int)0x80070005)), false, 100);
            Mode(policy, CaptureBackend.MediaFoundation);
            Check(!policy.CanOpen(3099) && policy.CanOpen(3100), "A rejected DirectShow highest mode releases its graph before MF tries its highest real native mode.");
            policy.RecordFailure(new NotSupportedException("The selected source is not exposed to MF."), false, 3100);
            Mode(policy, CaptureBackend.DirectShowDefault);
            Check(!policy.CanOpen(8099) && policy.CanOpen(8100), "After both highest modes fail, a separate genuine DirectShow driver-default graph remains available.");
            policy.RecordFailure(new CameraFormatException("fixture", 1280, 720, unchecked((int)0x8004022A)), false, 8100);
            Mode(policy, CaptureBackend.MediaFoundationDefault);
            Check(!policy.CanOpen(18099) && policy.CanOpen(18100), "DirectShow default failure still reaches the previous MF driver-default path.");
            policy.RecordFailure(new TimeoutException("Synthetic empty graph."), false, 18100);
            Mode(policy, CaptureBackend.DirectShow720);
            Check(!policy.CanOpen(38099) && policy.CanOpen(38100), "The explicit720p compatibility mode remains the fifth distinct attempt.");
            policy.RecordFailure(new IOException("Synthetic source removal."), false, 38100);
            Mode(policy, CaptureBackend.DirectShowHighest);
            Check(!policy.CanOpen(68099) && policy.CanOpen(68100), "Repeated failures cap their delay at thirty seconds.");
            for (int index = 0; index < 20; index++)
            {
                long now = 68100 + index * 30000L;
                policy.RecordFailure(new IOException("Synthetic source failure."), false, now);
                Check(!policy.CanOpen(now + 29999) && policy.CanOpen(now + 30000), "Repeated failures never create a fast capture-open loop.");
            }
            foreach (Exception error in new Exception[] {
                new COMException("Synthetic bind access denial.", unchecked((int)0x80070005)),
                new InvalidOperationException("Synthetic wrapper.", new CameraStreamException(3, unchecked((int)0x80070005))),
                new NotSupportedException("Synthetic unavailable capture API."),
                new TimeoutException("Synthetic first-frame timeout."),
                new IOException("Synthetic source failure.") })
            {
                var startup = new CaptureRetryPolicy();
                startup.RecordFailure(error, false, 0);
                Mode(startup, CaptureBackend.MediaFoundation);
                Check(!startup.CanOpen(2999) && startup.CanOpen(3000), "Every failed startup releases its backend before trying another.");
                startup.RecordSuccess();
                Mode(startup, CaptureBackend.MediaFoundation);
                Check(startup.CanOpen(1), "Only a received frame clears the previous failure deadline.");
                startup.RecordFailure(error, true, 100);
                Mode(startup, CaptureBackend.MediaFoundation);
                Check(!startup.CanOpen(3099) && startup.CanOpen(3100), "A backend that received frames survives a later stream failure.");
                startup.ResetDelay();
                Check(startup.CanOpen(101), "An explicit user action clears delay without discarding the successful API.");
                Mode(startup, CaptureBackend.MediaFoundation);
            }
            var cameras = new Dictionary<string, CaptureRetryPolicy>(StringComparer.Ordinal);
            cameras.Add("@device:fixture-camera-A", policy);
            cameras.Add("@device:fixture-camera-a", new CaptureRetryPolicy());
            Mode(cameras["@device:fixture-camera-a"], CaptureBackend.DirectShowHighest);
            Check(cameras["@device:fixture-camera-a"].CanOpen(0), "Retries never switch to or delay another camera.");
            var overflow = new CaptureRetryPolicy();
            overflow.RecordFailure(new IOException("Synthetic boundary."), false, Int64.MaxValue - 1000);
            Check(!overflow.CanOpen(Int64.MaxValue - 1) && overflow.CanOpen(Int64.MaxValue), "Retry deadlines saturate instead of overflowing.");
            try { overflow.RecordFailure(null, false, 0); throw new Exception("A missing failure was accepted."); }
            catch (ArgumentNullException) { checks++; }
            var refresh = new CaptureRetryPolicy();
            refresh.RecordFailure(new IOException("DS failed"), false, 0);
            refresh.RecordSuccess();
            refresh.RefreshModes(false); Mode(refresh, CaptureBackend.MediaFoundation);
            Check(refresh.CanOpen(0), "Manual refresh immediately re-enumerates a working MF family rather than repeating failed DirectShow.");
            refresh.RecordFailure(new IOException("MF highest failed"), false, 0);
            refresh.RecordFailure(new IOException("DS default failed"), false, 3000);
            Mode(refresh, CaptureBackend.MediaFoundationDefault); refresh.RecordSuccess();
            refresh.RefreshModes(false); Mode(refresh, CaptureBackend.MediaFoundation);
            Check(refresh.CanOpen(0), "A manual mode refresh upgrades working MF default to highest native once.");
            var explicitMode = new CaptureRetryPolicy(); explicitMode.RecordSuccess();
            explicitMode.RefreshModes(true); Mode(explicitMode, CaptureBackend.MediaFoundation);
            var freshCamera = new CaptureRetryPolicy(); freshCamera.RefreshModes(false); Mode(freshCamera, CaptureBackend.DirectShowHighest);
            Console.WriteLine("Capture backend retry policy checks passed: " + checks + ". Pure fixtures only; no cameras or configuration access.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Mode(CaptureRetryPolicy policy, CaptureBackend backend)
    {
        Check(policy.Backend == backend && policy.ModeName == CaptureRetryPolicy.Name(backend) &&
            policy.Width == (backend == CaptureBackend.DirectShowHighest ? 1920 : backend == CaptureBackend.DirectShow720 ? 1280 : 0) &&
            policy.Height == (backend == CaptureBackend.DirectShowHighest ? 1080 : backend == CaptureBackend.DirectShow720 ? 720 : 0),
            "Capture API must be " + backend + "; highest dimensions are bounds, never invented native modes.");
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
