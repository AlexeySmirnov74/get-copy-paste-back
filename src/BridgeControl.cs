using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace HorizonClient
{
    internal sealed class BridgeCameraFormat
    {
        internal Guid Session, Revision;
        internal int Width, Height, FPS, AudioRetries;
    }

    internal sealed class BridgeHello
    {
        internal Guid Session, VideoTest, AudioTest;
        internal Guid ForwardTransfer;
        internal int ForwardEncodedBytes;
        internal int Seconds;
        internal string CameraName;
        internal bool FastDeliveryReceipts;
    }

    internal sealed class BridgeReport
    {
        internal Guid Session, Test;
        internal long VideoBytes, AudioBytes;
        internal int Milliseconds;
        internal bool MicrophoneFound;
        internal int FrameWidth, FrameHeight, Frames;
        internal long[] ProfileBytes;
        internal int[] ProfileMilliseconds;
    }

    // Explicit envelopes keep commands and speed samples out of the user's clipboard.
    internal static class BridgeControl
    {
        private static readonly byte[] HelloMagic = Encoding.ASCII.GetBytes("HCBCTL02");
        private static readonly byte[] ForwardHelloMagic = Encoding.ASCII.GetBytes("HCBCTL03");
        private static readonly byte[] CapabilitiesHelloMagic = Encoding.ASCII.GetBytes("HCBCTL04");
        private static readonly byte[] ReportMagic = Encoding.ASCII.GetBytes("HCBRPT02");
        private static readonly byte[] ClipboardMagic = Encoding.ASCII.GetBytes("HCBCBP02");
        private static readonly byte[] AckMagic = Encoding.ASCII.GetBytes("HCBACK02");
        private static readonly byte[] CameraFormatMagic = Encoding.ASCII.GetBytes("HCBCAM01");

        internal static byte[] CameraFormat(Guid session, Guid revision, int width, int height, int fps)
        { return CameraFormat(session, revision, width, height, fps, 2); }

        internal static byte[] CameraFormat(Guid session, Guid revision, int width, int height, int fps, int audioRetries)
        {
            if (!ValidCameraFormat(session, revision, width, height, fps, audioRetries)) throw new ArgumentException("Invalid source camera format.");
            byte[] bytes = new byte[64]; Buffer.BlockCopy(CameraFormatMagic, 0, bytes, 0, 8);
            PutGuid(bytes, 8, session); PutGuid(bytes, 24, revision);
            PutInt(bytes, 40, width); PutInt(bytes, 44, height); PutInt(bytes, 48, fps); bytes[52] = (byte)audioRetries;
            PutInt(bytes, 60, unchecked((int)CameraFormatCrc(bytes))); return bytes;
        }

        internal static bool TryCameraFormat(Packet packet, out BridgeCameraFormat format)
        {
            format = null;
            if (packet == null || packet.Count != 1 || packet.Index != 0 || packet.TotalLength != 64 || packet.Data == null || packet.Data.Length != 64 || !Starts(packet.Data, CameraFormatMagic)) return false;
            byte[] bytes = packet.Data;
            for (int index = 53; index < 60; index++) if (bytes[index] != 0) return false;
            Guid session = GetGuid(bytes, 8), revision = GetGuid(bytes, 24);
            int width = GetInt(bytes, 40), height = GetInt(bytes, 44), fps = GetInt(bytes, 48), retries = bytes[52];
            if (session != packet.TransferId || !ValidCameraFormat(session, revision, width, height, fps, retries) || unchecked((uint)GetInt(bytes, 60)) != CameraFormatCrc(bytes)) return false;
            format = new BridgeCameraFormat { Session = session, Revision = revision, Width = width, Height = height, FPS = fps, AudioRetries = retries }; return true;
        }

        private static bool ValidCameraFormat(Guid session, Guid revision, int width, int height, int fps, int retries)
        { return session != Guid.Empty && revision != Guid.Empty && width >= 160 && (width & 1) == 0 && height >= 120 &&
            (width <= 1920 && height <= 1080 && fps >= 1 && fps <= 60 || fps == 30 && (width == 2560 && height == 1440 || width == 3840 && height == 2160)) && retries >= 0 && retries <= 10; }

        private static uint CameraFormatCrc(byte[] bytes)
        {
            uint crc = UInt32.MaxValue;
            for (int index = 0; index < 60; index++) { crc ^= bytes[index]; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1; }
            return ~crc;
        }

        internal static byte[] Acknowledge(Guid session, Guid transfer)
        {
            byte[] bytes = new byte[40]; Buffer.BlockCopy(AckMagic, 0, bytes, 0, 8);
            PutGuid(bytes, 8, session); PutGuid(bytes, 24, transfer); return bytes;
        }

        internal static bool TryAcknowledge(Packet packet, out Guid transfer)
        {
            transfer = Guid.Empty;
            if (packet == null || packet.Count != 1 || packet.Index != 0 || packet.TotalLength != 40 || packet.Data == null || packet.Data.Length != 40 || !Starts(packet.Data, AckMagic) || GetGuid(packet.Data, 8) != packet.TransferId) return false;
            transfer = GetGuid(packet.Data, 24); return transfer != Guid.Empty;
        }

        internal static byte[] Hello(Guid session, Guid video, Guid audio, int seconds)
        { return Hello(session, video, audio, seconds, null); }

        internal static byte[] Hello(Guid session, Guid video, Guid audio, int seconds, string cameraName)
        {
            if (session == Guid.Empty || seconds < 0 || seconds > 60) throw new ArgumentException("Invalid bridge announcement.");
            byte[] name = String.IsNullOrEmpty(cameraName) ? new byte[0] : Encoding.UTF8.GetBytes(cameraName);
            if (name.Length > 190) name = new byte[0];
            byte[] bytes = new byte[60 + (name.Length == 0 ? 0 : name.Length + 2)];
            Buffer.BlockCopy(HelloMagic, 0, bytes, 0, 8);
            PutGuid(bytes, 8, session); PutGuid(bytes, 24, video); PutGuid(bytes, 40, audio);
            PutInt(bytes, 56, seconds);
            if (name.Length != 0) { bytes[60] = (byte)name.Length; Buffer.BlockCopy(name, 0, bytes, 62, name.Length); }
            return bytes;
        }

        internal static bool TryHello(Packet packet, out BridgeHello hello)
        {
            hello = null;
            if (packet == null || packet.Count != 1 || packet.Index != 0 || packet.Data == null || packet.Data.Length < 60 || packet.Data.Length > 252 || packet.TotalLength != packet.Data.Length) return false;
            bool capabilities = Starts(packet.Data, CapabilitiesHelloMagic);
            bool forward = capabilities || Starts(packet.Data, ForwardHelloMagic);
            if (!forward && !Starts(packet.Data, HelloMagic)) return false;
            Guid session = GetGuid(packet.Data, 8);
            int seconds = GetInt(packet.Data, 56);
            if (session == Guid.Empty || session != packet.TransferId || seconds < 0 || seconds > 60) return false;
            hello = new BridgeHello { Session = session, VideoTest = GetGuid(packet.Data, 24), AudioTest = GetGuid(packet.Data, 40), Seconds = seconds };
            if (forward)
            {
                if (packet.Data.Length < 82 || (capabilities ? (packet.Data[81] & ~1) != 0 : packet.Data[81] != 0) || packet.Data[80] != packet.Data.Length - 82) { hello = null; return false; }
                hello.ForwardTransfer = GetGuid(packet.Data, 60); hello.ForwardEncodedBytes = GetInt(packet.Data, 76);
                bool hasTransfer = hello.ForwardTransfer != Guid.Empty;
                if ((!capabilities && !hasTransfer) || (hasTransfer ? hello.ForwardEncodedBytes < 1 || hello.ForwardEncodedBytes > VisualProtocol.MaxTransferBytes : hello.ForwardEncodedBytes != 0) ||
                    hello.VideoTest != Guid.Empty || hello.AudioTest != Guid.Empty || seconds != 0) { hello = null; return false; }
                hello.FastDeliveryReceipts = capabilities && (packet.Data[81] & 1) != 0;
                try { hello.CameraName = new UTF8Encoding(false, true).GetString(packet.Data, 82, packet.Data.Length - 82); }
                catch (DecoderFallbackException) { hello = null; return false; }
            }
            else if (packet.Data.Length != 60)
            {
                if (packet.Data.Length < 62 || packet.Data[61] != 0 || packet.Data[60] != packet.Data.Length - 62) { hello = null; return false; }
                try { hello.CameraName = new UTF8Encoding(false, true).GetString(packet.Data, 62, packet.Data.Length - 62); }
                catch (DecoderFallbackException) { hello = null; return false; }
            }
            if ((hello.VideoTest == Guid.Empty) != (hello.AudioTest == Guid.Empty) || (hello.VideoTest != Guid.Empty && seconds < 5)) { hello = null; return false; }
            return true;
        }

        internal static byte[] Hello(Guid session, Guid video, Guid audio, int seconds, string cameraName, Guid forwardTransfer, int forwardEncodedBytes)
        {
            if (forwardTransfer == Guid.Empty)
            {
                if (forwardEncodedBytes != 0) throw new ArgumentException("A forward size requires a transfer ID.");
                return Hello(session, video, audio, seconds, cameraName);
            }
            if (session == Guid.Empty || video != Guid.Empty || audio != Guid.Empty || seconds != 0 || forwardEncodedBytes < 1 || forwardEncodedBytes > VisualProtocol.MaxTransferBytes)
                throw new ArgumentException("Invalid forward clipboard announcement.");
            byte[] name = String.IsNullOrEmpty(cameraName) ? new byte[0] : Encoding.UTF8.GetBytes(cameraName);
            if (name.Length > 170) name = new byte[0];
            byte[] bytes = new byte[82 + name.Length]; Buffer.BlockCopy(ForwardHelloMagic, 0, bytes, 0, 8);
            PutGuid(bytes, 8, session); PutGuid(bytes, 60, forwardTransfer); PutInt(bytes, 76, forwardEncodedBytes);
            bytes[80] = (byte)name.Length; Buffer.BlockCopy(name, 0, bytes, 82, name.Length); return bytes;
        }

        internal static byte[] Hello(Guid session, Guid video, Guid audio, int seconds, string cameraName, Guid forwardTransfer, int forwardEncodedBytes, bool fastDeliveryReceipts)
        {
            if (!fastDeliveryReceipts) return Hello(session, video, audio, seconds, cameraName, forwardTransfer, forwardEncodedBytes);
            if (session == Guid.Empty || video != Guid.Empty || audio != Guid.Empty || seconds != 0 ||
                (forwardTransfer == Guid.Empty ? forwardEncodedBytes != 0 : forwardEncodedBytes < 1 || forwardEncodedBytes > VisualProtocol.MaxTransferBytes))
                throw new ArgumentException("Invalid capability announcement.");
            byte[] name = String.IsNullOrEmpty(cameraName) ? new byte[0] : Encoding.UTF8.GetBytes(cameraName);
            if (name.Length > 170) name = new byte[0];
            byte[] bytes = new byte[82 + name.Length]; Buffer.BlockCopy(CapabilitiesHelloMagic, 0, bytes, 0, 8);
            PutGuid(bytes, 8, session); PutGuid(bytes, 60, forwardTransfer); PutInt(bytes, 76, forwardEncodedBytes);
            bytes[80] = (byte)name.Length; bytes[81] = 1; Buffer.BlockCopy(name, 0, bytes, 82, name.Length);
            return bytes;
        }

        internal static byte[] Report(BridgeReport report)
        {
            if (report == null || report.Session == Guid.Empty || report.Test == Guid.Empty || report.Milliseconds < 1 || report.Milliseconds > 120000 || report.VideoBytes < 0 || report.AudioBytes < 0) throw new ArgumentException("Invalid speed report.");
            int profiles = report.ProfileBytes == null ? 0 : report.ProfileBytes.Length;
            if (profiles > 16 || (profiles != 0 && (report.ProfileMilliseconds == null || report.ProfileMilliseconds.Length != profiles))) throw new ArgumentException("Invalid profile measurements.");
            byte[] bytes = new byte[profiles == 0 && report.FrameWidth == 0 ? 64 : 80 + profiles * 9];
            Buffer.BlockCopy(ReportMagic, 0, bytes, 0, 8);
            PutGuid(bytes, 8, report.Session); PutGuid(bytes, 24, report.Test);
            Buffer.BlockCopy(BitConverter.GetBytes(report.VideoBytes), 0, bytes, 40, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(report.AudioBytes), 0, bytes, 48, 8);
            PutInt(bytes, 56, report.Milliseconds); bytes[60] = report.MicrophoneFound ? (byte)1 : (byte)0;
            if (bytes.Length > 64)
            {
                PutInt(bytes, 64, report.FrameWidth); PutInt(bytes, 68, report.FrameHeight); PutInt(bytes, 72, report.Frames); bytes[76] = 3; bytes[77] = (byte)profiles;
                for (int i = 0; i < profiles; i++)
                {
                    if (report.ProfileBytes[i] < 0 || report.ProfileBytes[i] > UInt32.MaxValue || report.ProfileMilliseconds[i] < 0 || report.ProfileMilliseconds[i] > 120000) throw new ArgumentException("Invalid profile measurement.");
                    bytes[80 + i * 9] = (byte)i;
                    Buffer.BlockCopy(BitConverter.GetBytes((uint)report.ProfileBytes[i]), 0, bytes, 81 + i * 9, 4);
                    PutInt(bytes, 85 + i * 9, report.ProfileMilliseconds[i]);
                }
            }
            return bytes;
        }

        internal static bool TryReport(byte[] bytes, Guid session, out BridgeReport report)
        {
            report = null;
            if (bytes == null || (bytes.Length != 64 && (bytes.Length < 80 || bytes.Length > 224)) || !Starts(bytes, ReportMagic) || GetGuid(bytes, 8) != session || session == Guid.Empty || bytes[60] > 1) return false;
            BridgeReport result = new BridgeReport { Session = session, Test = GetGuid(bytes, 24), VideoBytes = BitConverter.ToInt64(bytes, 40), AudioBytes = BitConverter.ToInt64(bytes, 48), Milliseconds = GetInt(bytes, 56), MicrophoneFound = bytes[60] == 1 };
            if (result.Test == Guid.Empty || result.VideoBytes < 0 || result.VideoBytes > 16L * 1024 * 1024 * 1024 || result.AudioBytes < 0 || result.AudioBytes > 1024 * 1024 || result.Milliseconds < 1 || result.Milliseconds > 120000) return false;
            if (bytes.Length > 64)
            {
                int count = bytes[77];
                result.FrameWidth = GetInt(bytes, 64); result.FrameHeight = GetInt(bytes, 68); result.Frames = GetInt(bytes, 72);
                if (bytes[76] != 3 || count > 16 || bytes.Length != 80 + count * 9 || result.FrameWidth < 0 || result.FrameWidth > 7680 || result.FrameHeight < 0 || result.FrameHeight > 4320 || result.Frames < 0 || result.Frames > 100000) return false;
                result.ProfileBytes = new long[count]; result.ProfileMilliseconds = new int[count];
                for (int i = 0; i < count; i++)
                {
                    if (bytes[80 + i * 9] != i) return false;
                    result.ProfileBytes[i] = BitConverter.ToUInt32(bytes, 81 + i * 9);
                    int ms = GetInt(bytes, 85 + i * 9); if (ms < 0 || ms > 120000) return false;
                    result.ProfileMilliseconds[i] = ms;
                }
            }
            report = result; return true;
        }

        internal static byte[] WrapClipboard(Guid session, byte[] encoded)
        {
            if (session == Guid.Empty || encoded == null || encoded.Length < 1 || encoded.Length > VisualProtocol.MaxTransferBytes - 24) throw new ArgumentException("Invalid audio clipboard envelope.");
            byte[] result = new byte[24 + encoded.Length];
            Buffer.BlockCopy(ClipboardMagic, 0, result, 0, 8); PutGuid(result, 8, session);
            Buffer.BlockCopy(encoded, 0, result, 24, encoded.Length); return result;
        }

        internal static bool TryClipboard(byte[] bytes, Guid session, out byte[] encoded)
        {
            encoded = null;
            if (bytes == null || bytes.Length < 25 || bytes.Length > VisualProtocol.MaxTransferBytes || session == Guid.Empty || !Starts(bytes, ClipboardMagic) || GetGuid(bytes, 8) != session) return false;
            encoded = new byte[bytes.Length - 24]; Buffer.BlockCopy(bytes, 24, encoded, 0, encoded.Length); return true;
        }

        internal static Packet Single(Guid id, byte[] bytes)
        {
            return new Packet { TransferId = id, Index = 0, Count = 1, TotalLength = bytes.Length, Data = bytes, Profile = VideoProfile.Compatible, ChunkSize = 256 };
        }

        // A benchmark may send more than the clipboard's 32 MiB limit: each epoch
        // has its own ID. The random 96-bit prefix reserves it as test data.
        internal static Guid BenchmarkEpoch(Guid test, int epoch)
        {
            byte[] id = test.ToByteArray(); Buffer.BlockCopy(BitConverter.GetBytes(epoch | Int32.MinValue), 0, id, 12, 4); return new Guid(id);
        }

        internal static bool IsBenchmarkTransfer(Guid transfer, Guid test)
        {
            if (transfer == test) return true;
            byte[] left = transfer.ToByteArray(), right = test.ToByteArray();
            if ((left[15] & 128) == 0) return false;
            for (int i = 0; i < 12; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool Starts(byte[] bytes, byte[] magic)
        {
            if (bytes == null || bytes.Length < magic.Length) return false;
            for (int i = 0; i < magic.Length; i++) if (bytes[i] != magic[i]) return false;
            return true;
        }
        private static Guid GetGuid(byte[] bytes, int offset) { byte[] id = new byte[16]; Buffer.BlockCopy(bytes, offset, id, 0, 16); return new Guid(id); }
        private static void PutGuid(byte[] bytes, int offset, Guid id) { Buffer.BlockCopy(id.ToByteArray(), 0, bytes, offset, 16); }
        private static int GetInt(byte[] bytes, int offset) { return BitConverter.ToInt32(bytes, offset); }
        private static void PutInt(byte[] bytes, int offset, int value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
    }

    internal sealed class SpeedCounter
    {
        private readonly Dictionary<Guid, HashSet<int>> seen = new Dictionary<Guid, HashSet<int>>();
        private int entries;
        internal long Bytes { get; private set; }
        internal bool Add(Packet packet)
        {
            if (packet == null || packet.Data == null || packet.Index < 0 || packet.Index >= 1048576 || entries >= 1048576) return false;
            HashSet<int> indices;
            if (!seen.TryGetValue(packet.TransferId, out indices))
            {
                if (seen.Count >= 1024) return false;
                indices = new HashSet<int>(); seen.Add(packet.TransferId, indices);
            }
            if (!indices.Add(packet.Index)) return false;
            entries++; Bytes += packet.Data.Length; return true;
        }
    }
}
