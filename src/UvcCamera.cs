using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HorizonClient
{
    // One producer thread owns this connection. Recreate it after any I/O failure.
    public sealed class UvcCamera : IDisposable
    {
        private const int TimeoutMilliseconds = 10000;
        private const int MaximumJpegBytes = 64 * 1024 * 1024;
        private const int MaximumRawBytes = 64 * 1024 * 1024;
        private TcpClient client;
        private NetworkStream stream;
        private byte[] session;
        private ulong sequence;
        private bool disposed;
        private int jpegQuality = 90;
        private ImageCodecInfo jpegEncoder;
        private byte[] rawFrame, rawRow;
        private static readonly byte[] GrayscaleLuma = MakeGrayscaleLuma();
        public Guid ConnectionId { get; private set; }

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int FramesPerSecond { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsUncompressed { get; private set; }
        public bool IsH264Probe { get; private set; }
        // A successful write only confirms delivery to the local camera backend.
        // Remote decoding and unique bytes are measured independently by its ACK.
        public long VideoFramesSent { get; private set; }
        public long VideoBytesSent { get; private set; }
        public int LastVideoPayloadBytes { get; private set; }
        public double LastEncodeMilliseconds { get; private set; }
        public double LastWriteMilliseconds { get; private set; }
        public int JpegQuality
        {
            get { return jpegQuality; }
            set
            {
                if (value < 1 || value > 100) throw new ArgumentOutOfRangeException("value", "JPEG quality must be between 1 and 100.");
                jpegQuality = value;
            }
        }

        public UvcCamera(string host, int port, string keyFile)
        {
            if (String.IsNullOrWhiteSpace(host)) throw new ArgumentException("Camera host is required.", "host");
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            if (String.IsNullOrWhiteSpace(keyFile)) throw new ArgumentException("Camera key file is required.", "keyFile");
            byte[] secret = null;
            ConnectionId = Guid.NewGuid();
            string stage = "load_local_key";
            Stopwatch opening = Stopwatch.StartNew();
            try
            {
                secret = LoadSecret(keyFile);
                client = new TcpClient();
                client.NoDelay = true;
                client.SendTimeout = TimeoutMilliseconds;
                client.ReceiveTimeout = TimeoutMilliseconds;
                stage = "tcp_connect";
                DiagnosticLog.Write("server.camera.connection.begin", "connection_id", ConnectionId, "host", host, "port", port,
                    "timeout_ms", TimeoutMilliseconds);
                IAsyncResult connecting = client.BeginConnect(host, port, null, null);
                using (var finished = connecting.AsyncWaitHandle)
                {
                    if (!finished.WaitOne(TimeoutMilliseconds)) throw new TimeoutException("Camera connection timed out.");
                    client.EndConnect(connecting);
                }
                stream = client.GetStream();
                stream.ReadTimeout = TimeoutMilliseconds;
                stream.WriteTimeout = TimeoutMilliseconds;
                stage = "read_backend_hello";
                byte[] hello = ReadExact(48);
                byte[] magic = { 0x55, 0x42, 0x56, 0x43, 0, 0, 0, 1 };
                for (int i = 0; i < magic.Length; i++)
                    if (hello[i] != magic[i]) throw new IOException("Unsupported camera receiver handshake.");
                Width = (hello[8] << 8) | hello[9];
                Height = (hello[10] << 8) | hello[11];
                uint flaggedRate = ReadUInt32(hello, 12);
                IsUncompressed = (flaggedRate & 0x80000000U) != 0;
                IsH264Probe = (flaggedRate & 0x20000000U) != 0;
                uint rate = flaggedRate & 0x5fffffffU;
                if (Width < 16 || Width > 7680 || (Width & 1) != 0 || Height < 16 || Height > 4320 || (Height & 1) != 0 || rate < 1 || rate > 60 ||
                    (IsUncompressed && ((long)Width * Height * 2 > MaximumRawBytes || (ulong)Width * (ulong)Height * 16UL * rate > UInt32.MaxValue)) ||
                    (IsH264Probe && (IsUncompressed || Width != 640 || Height != 480 || rate != 30)))
                    throw new IOException("Camera receiver advertised an invalid video mode.");
                FramesPerSecond = (int)rate;
                DiagnosticLog.Write("server.camera.connection.format", "connection_id", ConnectionId, "width", Width, "height", Height,
                    "fps", FramesPerSecond, "uncompressed", IsUncompressed, "h264_probe", IsH264Probe, "elapsed_ms", opening.ElapsedMilliseconds);
                stage = "authenticate_backend";
                byte[] signature = Mac(secret, Encoding.ASCII.GetBytes("USBBridge video authenticate"), hello);
                stream.Write(signature, 0, signature.Length);
                byte[] acknowledgement = ReadExact(4);
                if (Encoding.ASCII.GetString(acknowledgement) != "OKAY") throw new IOException("Camera receiver rejected authentication.");
                byte[] proof = ReadExact(32);
                byte[] expected = Mac(secret, Encoding.ASCII.GetBytes("USBBridge video receiver"), hello, signature);
                if (!EqualConstantTime(proof, expected)) throw new IOException("Camera receiver proof is invalid.");
                session = Mac(secret, Encoding.ASCII.GetBytes("USBBridge video session"), hello);
                IsConnected = true;
                DiagnosticLog.Write("server.camera.connection.ready", "connection_id", ConnectionId, "elapsed_ms", opening.ElapsedMilliseconds,
                    "send_buffer_bytes", client.SendBufferSize, "receive_buffer_bytes", client.ReceiveBufferSize,
                    "confirmation_scope", "authenticated local camera backend; does not prove USB/RTAV frame delivery");
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("server.camera.connection.failed", error, "connection_id", ConnectionId, "stage", stage, "elapsed_ms", opening.ElapsedMilliseconds);
                Dispose();
                throw;
            }
            finally
            {
                if (secret != null) Array.Clear(secret, 0, secret.Length);
            }
        }

        public void Send(Bitmap frame)
        {
            long encodingStarted = Stopwatch.GetTimestamp();
            if (disposed) throw new ObjectDisposedException("UvcCamera");
            if (frame == null) throw new ArgumentNullException("frame");
            if (!IsConnected) throw new IOException("Camera connection is closed.");
            if (IsH264Probe) throw new InvalidOperationException("The H.264 probe generates its own fixed video; Bitmap sending is disabled.");
            if (frame.Width != Width || frame.Height != Height)
                throw new ArgumentException("Frame dimensions must match the camera receiver video mode.", "frame");
            if (IsUncompressed)
            {
                byte[] raw = PrepareYuy2(frame);
                LastEncodeMilliseconds = (Stopwatch.GetTimestamp() - encodingStarted) * 1000.0 / Stopwatch.Frequency;
                SendPayload(raw, false, true);
                return;
            }
            byte[] jpeg;
            if (jpegEncoder == null)
                foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                    if (codec.FormatID == ImageFormat.Jpeg.Guid) { jpegEncoder = codec; break; }
            if (jpegEncoder == null) throw new InvalidOperationException("The Windows JPEG encoder is unavailable.");
            int limit = (int)Math.Min((long)Width * Height * 3 + 65536, MaximumJpegBytes);
            using (var output = new BoundedJpegStream(limit))
            using (var parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)jpegQuality);
                frame.Save(output, jpegEncoder, parameters);
                if (output.Length < 4 || output.Length > limit) throw new IOException("Encoded frame exceeds the camera JPEG size limit.");
                jpeg = output.ToArray();
            }
            LastEncodeMilliseconds = (Stopwatch.GetTimestamp() - encodingStarted) * 1000.0 / Stopwatch.Frequency;
            SendPayload(jpeg, false);
        }

        // PCM goes to the USB microphone of the same virtual camera, never to a
        // physical recording device. Audio and JPEG share one authenticated stream.
        public void SendAudio(float[] mono48k)
        {
            if (disposed) throw new ObjectDisposedException("UvcCamera");
            if (!IsConnected) throw new IOException("Camera connection is closed.");
            if (mono48k == null || mono48k.Length == 0 || mono48k.Length > 480000) throw new ArgumentException("Audio must contain at most ten seconds of mono 48 kHz samples.", "mono48k");
            foreach (float value in mono48k) if (Single.IsNaN(value) || Single.IsInfinity(value)) throw new ArgumentException("Audio contains a non-finite sample.", "mono48k");
            for (int offset = 0; offset < mono48k.Length; offset += 48000)
            {
                int count = Math.Min(48000, mono48k.Length - offset);
                byte[] pcm = new byte[count * 2];
                for (int i = 0; i < count; i++)
                {
                    short value = (short)Math.Round(Math.Max(-0.25f, Math.Min(0.25f, mono48k[offset + i])) * 32767);
                    pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8);
                }
                SendPayload(pcm, true);
            }
        }

        private void SendPayload(byte[] payload, bool audio, bool uncompressed = false)
        {
            if (sequence == UInt64.MaxValue) throw new IOException("Camera sequence exhausted; reconnect the camera.");
            byte[] header = new byte[44];
            WriteUInt64(header, 0, ++sequence);
            WriteUInt32(header, 8, (uint)payload.Length | (audio ? 0x80000000U : 0U) | (uncompressed ? 0x40000000U : 0U));
            byte[] prefix = new byte[12];
            Buffer.BlockCopy(header, 0, prefix, 0, prefix.Length);
            byte[] tag = Mac(session, prefix, payload);
            Buffer.BlockCopy(tag, 0, header, 12, tag.Length);
            try
            {
                long writingStarted = Stopwatch.GetTimestamp();
                stream.Write(header, 0, header.Length);
                stream.Write(payload, 0, payload.Length);
                if (!audio)
                {
                    LastWriteMilliseconds = (Stopwatch.GetTimestamp() - writingStarted) * 1000.0 / Stopwatch.Frequency;
                    LastVideoPayloadBytes = payload.Length;
                    VideoFramesSent++;
                    VideoBytesSent += payload.Length;
                    if (VideoFramesSent <= 8 || LastWriteMilliseconds >= 250 || VideoFramesSent % 120 == 0)
                        DiagnosticLog.Write("server.camera.backend.write", "connection_id", ConnectionId, "sequence", sequence,
                            "video_frame", VideoFramesSent, "payload_bytes", payload.Length, "header_bytes", header.Length,
                            "write_ms", LastWriteMilliseconds, "video_bytes_total", VideoBytesSent,
                            "format", uncompressed ? "YUY2" : "MJPEG", "confirmation_scope", "local socket write completed");
                }
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("server.camera.backend.write_failed", error, "connection_id", ConnectionId, "sequence", sequence,
                    "video_frame", VideoFramesSent, "bytes", payload.Length, "audio", audio);
                IsConnected = false;
                Dispose();
                throw;
            }
        }

        // BT.601 limited-range YUY2: Y0 U Y1 V. Chroma is averaged over each
        // horizontal pair; monochrome protocol pixels retain neutral chroma.
        internal static byte[] EncodeYuy2(Bitmap frame)
        {
            int length = ValidateYuy2(frame);
            byte[] output = new byte[length], row = new byte[checked(frame.Width * 3)];
            FillYuy2(frame, output, row);
            return output;
        }

        private byte[] PrepareYuy2(Bitmap frame)
        {
            int length = ValidateYuy2(frame);
            if (rawFrame == null) rawFrame = new byte[length];
            if (rawRow == null) rawRow = new byte[checked(frame.Width * 3)];
            FillYuy2(frame, rawFrame, rawRow);
            return rawFrame;
        }

        private static int ValidateYuy2(Bitmap frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            int width = frame.Width, height = frame.Height;
            if ((width & 1) != 0 || (long)width * height * 2 > MaximumRawBytes)
                throw new ArgumentException("YUY2 requires an even width and a frame no larger than 64 MiB.", "frame");
            return checked(width * height * 2);
        }

        private static void FillYuy2(Bitmap frame, byte[] output, byte[] row)
        {
            int length = ValidateYuy2(frame), width = frame.Width, height = frame.Height;
            if (output == null || output.Length != length || row == null || row.Length != checked(width * 3))
                throw new ArgumentException("YUY2 buffers must match the frame dimensions.");
            Bitmap converted = null;
            try
            {
                Bitmap source = frame;
                if (frame.PixelFormat != PixelFormat.Format24bppRgb)
                {
                    converted = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    using (Graphics graphics = Graphics.FromImage(converted)) graphics.DrawImageUnscaled(frame, 0, 0);
                    source = converted;
                }
                BitmapData locked = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length);
                        int target = y * width * 2;
                        for (int first = 0; first < row.Length; first += 6, target += 4)
                        {
                            int second = first + 3;
                            byte firstGray = row[first], secondGray = row[second];
                            if (firstGray == row[first + 1] && firstGray == row[first + 2] &&
                                secondGray == row[second + 1] && secondGray == row[second + 2])
                            {
                                output[target] = GrayscaleLuma[firstGray];
                                output[target + 1] = 128;
                                output[target + 2] = GrayscaleLuma[secondGray];
                                output[target + 3] = 128;
                                continue;
                            }
                            output[target] = Luma(row, first);
                            output[target + 2] = Luma(row, second);
                            int b = (row[first] + row[second] + 1) / 2;
                            int g = (row[first + 1] + row[second + 1] + 1) / 2;
                            int r = (row[first + 2] + row[second + 2] + 1) / 2;
                            output[target + 1] = (byte)Math.Max(0, Math.Min(255, ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128));
                            output[target + 3] = (byte)Math.Max(0, Math.Min(255, ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128));
                        }
                    }
                }
                finally { source.UnlockBits(locked); }
            }
            finally { if (converted != null) converted.Dispose(); }
        }

        private static byte[] MakeGrayscaleLuma()
        {
            var result = new byte[256];
            for (int value = 0; value < result.Length; value++) result[value] = (byte)(((220 * value + 128) >> 8) + 16);
            return result;
        }

        private static byte Luma(byte[] row, int offset)
        {
            return (byte)Math.Max(0, Math.Min(255, ((66 * row[offset + 2] + 129 * row[offset + 1] + 25 * row[offset] + 128) >> 8) + 16));
        }

        private sealed class BoundedJpegStream : MemoryStream
        {
            private readonly int limit;
            internal BoundedJpegStream(int limit) { this.limit = limit; }
            private void EnsureBound(long length)
            {
                if (length < 0 || length > limit) throw new IOException("Encoded frame exceeds the camera JPEG size limit.");
                if (length > Capacity) Capacity = (int)Math.Max(length, Math.Min(limit, Math.Max(256L, (long)Capacity * 2)));
            }
            public override void Write(byte[] buffer, int offset, int count) { EnsureBound(Position + count); base.Write(buffer, offset, count); }
            public override void WriteByte(byte value) { EnsureBound(Position + 1); base.WriteByte(value); }
            public override void SetLength(long value) { EnsureBound(value); base.SetLength(value); }
        }

        private byte[] ReadExact(int count)
        {
            var result = new byte[count];
            int offset = 0;
            var elapsed = Stopwatch.StartNew();
            while (offset < count)
            {
                int remaining = TimeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException("Camera handshake timed out.");
                stream.ReadTimeout = remaining;
                int read = stream.Read(result, offset, count - offset);
                if (read == 0) throw new EndOfStreamException("Camera receiver closed the connection.");
                offset += read;
            }
            stream.ReadTimeout = TimeoutMilliseconds;
            return result;
        }

        private static byte[] LoadSecret(string path)
        {
            string key;
            try
            {
                if (new FileInfo(path).Length > 4096) throw new IOException("Invalid camera key file.");
                key = File.ReadAllText(path, new UTF8Encoding(false, true)).Trim();
            }
            catch (UnauthorizedAccessException)
            {
                throw new IOException("The private camera key cannot be read by this Windows account. Run setup and the sender under the same account.");
            }
            catch (DecoderFallbackException)
            {
                throw new IOException("The camera key file is not valid UTF-8.");
            }
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            try
            {
                if (bytes.Length < 16 || bytes.Length > 128) throw new IOException("Invalid camera connection key length.");
                foreach (char value in key) if (Char.IsControl(value)) throw new IOException("Invalid camera connection key.");
                using (var sha = SHA256.Create()) return sha.ComputeHash(bytes);
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        private static byte[] Mac(byte[] key, params byte[][] parts)
        {
            using (var hmac = new HMACSHA256(key))
            {
                foreach (byte[] part in parts) hmac.TransformBlock(part, 0, part.Length, part, 0);
                hmac.TransformFinalBlock(new byte[0], 0, 0);
                return hmac.Hash;
            }
        }

        private static bool EqualConstantTime(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            for (int i = 3; i >= 0; i--) { bytes[offset + i] = (byte)value; value >>= 8; }
        }

        private static void WriteUInt64(byte[] bytes, int offset, ulong value)
        {
            for (int i = 7; i >= 0; i--) { bytes[offset + i] = (byte)value; value >>= 8; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            IsConnected = false;
            rawFrame = null; rawRow = null;
            if (stream != null) stream.Dispose();
            if (client != null) client.Close();
            if (session != null) Array.Clear(session, 0, session.Length);
        }
    }
}
