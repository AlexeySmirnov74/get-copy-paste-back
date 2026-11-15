using System;

namespace HorizonClient
{
    internal enum CaptureBackend { DirectShowDefault, MediaFoundation, DirectShow720, DirectShowHighest, MediaFoundationDefault }

    // One instance belongs to one exact camera moniker. The owner serializes
    // calls and supplies monotonic milliseconds; this policy never opens a
    // device, reads configuration or changes another camera's retry state.
    internal sealed class CaptureRetryPolicy
    {
        private static readonly int[] Delays = { 3000, 5000, 10000, 20000, 30000 };
        private static readonly CaptureBackend[] Backends = { CaptureBackend.DirectShowHighest, CaptureBackend.MediaFoundation, CaptureBackend.DirectShowDefault, CaptureBackend.MediaFoundationDefault, CaptureBackend.DirectShow720 };
        private int mode;
        private int lastSuccessful = -1;
        private int failures;
        private long retryAt = Int64.MinValue;

        // Highest negotiates only a real advertised mode within these bounds;
        // driver-default remains a distinct retry if SetFormat or graph Run fails.
        internal CaptureBackend Backend { get { return Backends[mode]; } }
        internal int Width { get { return Backend == CaptureBackend.DirectShowHighest ? 1920 : Backend == CaptureBackend.DirectShow720 ? 1280 : 0; } }
        internal int Height { get { return Backend == CaptureBackend.DirectShowHighest ? 1080 : Backend == CaptureBackend.DirectShow720 ? 720 : 0; } }
        internal string ModeName { get { return Name(Backend); } }

        internal static string Name(CaptureBackend backend)
        {
            switch (backend)
            {
                case CaptureBackend.DirectShowDefault: return "directshow:driver_default";
                case CaptureBackend.MediaFoundation: return "mediafoundation:highest_native";
                case CaptureBackend.MediaFoundationDefault: return "mediafoundation:driver_default";
                case CaptureBackend.DirectShow720: return "directshow:1280x720";
                case CaptureBackend.DirectShowHighest: return "directshow:highest_offered_up_to_1920x1080";
                default: throw new ArgumentOutOfRangeException("backend");
            }
        }

        internal bool CanOpen(long nowMs) { return nowMs >= retryAt; }

        // An explicit user action can retry immediately without discarding a
        // format that already delivered frames for this selected device.
        internal void ResetDelay()
        {
            failures = 0;
            retryAt = Int64.MinValue;
        }

        internal void RecordSuccess() { lastSuccessful = mode; ResetDelay(); }

        internal void RefreshModes(bool requireMediaFoundation)
        {
            CaptureBackend prior = lastSuccessful >= 0 ? Backends[lastSuccessful] : Backend;
            CaptureBackend next = requireMediaFoundation || prior == CaptureBackend.MediaFoundation || prior == CaptureBackend.MediaFoundationDefault
                ? CaptureBackend.MediaFoundation : CaptureBackend.DirectShowHighest;
            mode = Array.IndexOf(Backends, next);
            ResetDelay();
        }

        internal void RecordFailure(Exception error, bool hadFrames, long nowMs)
        {
            if (error == null) throw new ArgumentNullException("error");
            if (!hadFrames) mode = (mode + 1) % Backends.Length;
            int delay = Delays[Math.Min(failures, Delays.Length - 1)];
            if (failures < Delays.Length) failures++;
            retryAt = nowMs > Int64.MaxValue - delay ? Int64.MaxValue : nowMs + delay;
        }

    }
}
