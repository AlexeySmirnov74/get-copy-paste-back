using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HorizonClient
{
    public sealed class AudioEndpoint
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        internal string PersistentId { get; private set; }
        internal string LegacyName { get; private set; }
        internal AudioEndpoint(string id, string name) : this(id, name, id, name) { }
        internal AudioEndpoint(string id, string name, string persistentId, string legacyName)
        { Id = id; Name = name; PersistentId = persistentId; LegacyName = legacyName; }
        public override string ToString() { return Name; }
    }

    // Synchronous worker-thread playback. Construction never opens an audio device.
    // Explicit IDs come from EnumerateEndpoints and have the form "waveout:0".
    public sealed class AudioPlayer : IDisposable
    {
        private readonly object gate = new object();
        private readonly uint deviceId;
        private volatile bool disposed;
        private int playing;

        public AudioPlayer(string endpointId = null)
        {
            deviceId = UInt32.MaxValue; // WAVE_MAPPER is used only when requested by a null ID.
            if (endpointId != null)
            {
                uint index;
                if (!endpointId.StartsWith("waveout:", StringComparison.Ordinal) ||
                    !UInt32.TryParse(endpointId.Substring(8), NumberStyles.None, CultureInfo.InvariantCulture, out index) || index > 65535)
                    throw new ArgumentException("Playback endpoint must be a waveout device ID returned by EnumerateEndpoints.", "endpointId");
                deviceId = index;
            }
        }

        public static AudioEndpoint[] EnumerateEndpoints()
        {
            List<AudioEndpoint> endpoints = new List<AudioEndpoint>();
            uint count = AudioNative.waveOutGetNumDevs();
            if (count > 65536) throw new InvalidDataException("Invalid playback endpoint count.");
            for (uint index = 0; index < count; index++)
            {
                AudioNative.WaveOutCaps caps;
                uint error = AudioNative.waveOutGetDevCapsW(new UIntPtr(index), out caps, (uint)Marshal.SizeOf(typeof(AudioNative.WaveOutCaps)));
                if (error != 0) continue;
                string persistentId, fullName;
                AudioPlaybackIdentity.Resolve(index, out persistentId, out fullName);
                endpoints.Add(new AudioEndpoint("waveout:" + index.ToString(CultureInfo.InvariantCulture),
                    String.IsNullOrEmpty(fullName) ? caps.Name : fullName, persistentId, caps.Name));
            }
            return endpoints.ToArray();
        }

        public void Play(float[] mono48k) { Play(mono48k, CancellationToken.None); }

        public void Play(float[] mono48k, CancellationToken cancellationToken)
        { Play(mono48k, cancellationToken, null); }

        internal void Play(float[] mono48k, CancellationToken cancellationToken, Action bufferWritten)
        {
            // Validate and convert before the first native audio call.
            byte[] pcm = AudioSamples.ToPcm16(mono48k);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) { ThrowIfDisposed(); }
            if (Interlocked.CompareExchange(ref playing, 1, 0) != 0)
                throw new InvalidOperationException("Only one playback may run at a time.");
            AudioMemory data = null, header = null;
            bool prepared = false;
            IntPtr device = IntPtr.Zero;
            string stage = "allocate", playbackId = null;
            Stopwatch elapsed = null;
            if (DiagnosticLog.IsEnabled)
            {
                playbackId = Guid.NewGuid().ToString("N"); elapsed = Stopwatch.StartNew();
                AudioSignalStatistics signal = new AudioSignalStatistics(); signal.ObservePcm16(pcm);
                DiagnosticLog.Write("audio.playback.requested", "playback", playbackId, "requested_endpoint", PlaybackEndpoint,
                    "samples", signal.Samples, "duration_ms", signal.Samples * 1000.0 / 48000, "pcm_peak", signal.Peak,
                    "pcm_rms", signal.Rms, "nonzero_samples", signal.NonZeroSamples);
            }
            try
            {
                data = new AudioMemory(pcm.Length);
                Marshal.Copy(pcm, 0, data.DangerousGetHandle(), pcm.Length);
                header = new AudioMemory(Marshal.SizeOf(typeof(AudioNative.WaveHeader)));
                AudioNative.WaveHeader nativeHeader = new AudioNative.WaveHeader { Data = data.DangerousGetHandle(), BufferLength = (uint)pcm.Length };
                Marshal.StructureToPtr(nativeHeader, header.DangerousGetHandle(), false);
                AudioNative.WaveFormat format = new AudioNative.WaveFormat
                {
                    Tag = 1, Channels = 1, SamplesPerSecond = 48000, AverageBytesPerSecond = 96000,
                    BlockAlign = 2, BitsPerSample = 16, ExtraSize = 0
                };
                lock (gate)
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    stage = "waveOutOpen";
                    CheckWave(AudioNative.waveOutOpen(out device, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, 0), "open audio output");
                    LogPlaybackDevice(device, playbackId);
                    DiagnosticLog.Write("audio.playback.format.accepted", "api", "WinMM", "playback", playbackId, "endpoint", PlaybackEndpoint,
                        "format_scope", "application stream accepted by waveOutOpen; endpoint engine may convert", "sample_kind", "PCM", "wave_tag", format.Tag,
                        "sample_rate", format.SamplesPerSecond, "channels", format.Channels, "bits", format.BitsPerSample, "block_align", format.BlockAlign, "average_bytes_per_second", format.AverageBytesPerSecond);
                    stage = "waveOutPrepareHeader";
                    CheckWave(AudioNative.waveOutPrepareHeader(device, header.DangerousGetHandle(), (uint)header.Size), "prepare audio buffer");
                    prepared = true;
                    stage = "waveOutWrite";
                    CheckWave(AudioNative.waveOutWrite(device, header.DangerousGetHandle(), (uint)header.Size), "start audio playback");
                    if (bufferWritten != null) bufferWritten();
                    if (DiagnosticLog.IsEnabled) DiagnosticLog.Write("audio.playback.buffer_written", "playback", playbackId, "endpoint", PlaybackEndpoint,
                        "pcm_bytes", pcm.Length, "elapsed_ms", elapsed == null ? 0 : elapsed.ElapsedMilliseconds);
                }
                stage = "wait_completion";
                int flagsOffset = (int)Marshal.OffsetOf(typeof(AudioNative.WaveHeader), "Flags");
                while ((Marshal.ReadInt32(header.DangerousGetHandle(), flagsOffset) & 1) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (gate) { ThrowIfDisposed(); }
                    Thread.Sleep(5);
                }
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate) { ThrowIfDisposed(); }
                if (DiagnosticLog.IsEnabled) DiagnosticLog.Write("audio.playback.completed", "playback", playbackId, "endpoint", PlaybackEndpoint,
                    "elapsed_ms", elapsed == null ? 0 : elapsed.ElapsedMilliseconds, "samples", pcm.Length / 2);
            }
            catch (OperationCanceledException error)
            {
                if (DiagnosticLog.IsEnabled) DiagnosticLog.Write("audio.playback.cancelled", "playback", playbackId, "endpoint", PlaybackEndpoint,
                    "stage", stage, "elapsed_ms", elapsed == null ? 0 : elapsed.ElapsedMilliseconds, "hresult", "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture));
                throw;
            }
            catch (Exception error)
            {
                if (DiagnosticLog.IsEnabled) DiagnosticLog.Error("audio.playback.failed", error, "playback", playbackId, "endpoint", PlaybackEndpoint,
                    "stage", stage, "elapsed_ms", elapsed == null ? 0 : elapsed.ElapsedMilliseconds, "disposed", disposed);
                throw;
            }
            finally
            {
                lock (gate)
                {
                    if (device != IntPtr.Zero)
                    {
                        uint reset = AudioNative.waveOutReset(device);
                        uint unprepare = prepared ? AudioNative.waveOutUnprepareHeader(device, header.DangerousGetHandle(), (uint)header.Size) : 0;
                        uint close = AudioNative.waveOutClose(device);
                        if (DiagnosticLog.IsEnabled) DiagnosticLog.Write("audio.playback.closed", "playback", playbackId, "endpoint", PlaybackEndpoint,
                            "reset_result", reset, "unprepare_result", unprepare, "close_result", close,
                            "elapsed_ms", elapsed == null ? 0 : elapsed.ElapsedMilliseconds);
                        // A defective driver can refuse both reset/unprepare and close. In that
                        // case retain its memory rather than free an outstanding native buffer.
                        if (unprepare != 0 && close != 0)
                        {
                            if (data != null) data.RetainForFaultedDriver();
                            if (header != null) header.RetainForFaultedDriver();
                        }
                    }
                }
                if (header != null) header.Dispose();
                if (data != null) data.Dispose();
                Interlocked.Exchange(ref playing, 0);
            }
        }

        private string PlaybackEndpoint { get { return deviceId == UInt32.MaxValue ? "default_mapper" : "waveout:" + deviceId.ToString(CultureInfo.InvariantCulture); } }

        private void LogPlaybackDevice(IntPtr device, string playbackId)
        {
            if (!DiagnosticLog.IsEnabled) return;
            try
            {
                uint actualId, volume;
                uint idResult = AudioNative.waveOutGetID(device, out actualId);
                AudioNative.WaveOutCaps caps = new AudioNative.WaveOutCaps();
                uint capsResult = idResult == 0 ? AudioNative.waveOutGetDevCapsW(new UIntPtr(actualId), out caps, (uint)Marshal.SizeOf(typeof(AudioNative.WaveOutCaps))) : idResult;
                uint volumeResult = AudioNative.waveOutGetVolume(device, out volume);
                DiagnosticLog.Write("audio.playback.device.opened", "playback", playbackId, "requested_endpoint", PlaybackEndpoint,
                    "resolved_endpoint", idResult == 0 ? "waveout:" + actualId.ToString(CultureInfo.InvariantCulture) : null,
                    "device_name", capsResult == 0 ? caps.Name : null, "device_channels", capsResult == 0 ? (int)caps.Channels : 0,
                    "device_id_result", idResult, "device_caps_result", capsResult, "volume_result", volumeResult,
                    "volume_left", volumeResult == 0 ? (object)((volume & 65535) / 65535.0) : null,
                    "volume_right", volumeResult == 0 ? (object)((volume >> 16) / 65535.0) : null);
            }
            catch (Exception error) { DiagnosticLog.Error("audio.playback.device_diagnostics_failed", error, "playback", playbackId); }
        }

        private static void CheckWave(uint error, string operation)
        {
            if (error != 0)
            {
                if (DiagnosticLog.IsEnabled) DiagnosticLog.Write("audio.playback.native_error", "operation", operation, "winmm_result", error);
                throw new IOException("Could not " + operation + " (WinMM " + error.ToString(CultureInfo.InvariantCulture) + ").");
            }
        }

        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException("AudioPlayer"); }

        public void Dispose()
        {
            // The Play worker observes this within its 5 ms poll, then owns reset, unprepare
            // and close. Disposing on a UI thread never waits inside a native audio driver.
            disposed = true;
        }
    }

    internal static class AudioPlaybackIdentity
    {
        internal const uint QueryInstanceId = 2065, QueryInstanceIdSize = 2066;
        internal static void Resolve(uint index, out string id, out string name)
        {
            id = name = null;
            IntPtr size = IntPtr.Zero, text = IntPtr.Zero;
            AudioNative.IMMDeviceEnumerator enumerator = null; AudioNative.IMMDevice device = null;
            bool uninitialize = false;
            try
            {
                size = Marshal.AllocHGlobal(4); Marshal.WriteInt32(size, 0);
                IntPtr legacyDevice = new IntPtr((long)index);
                uint queryResult = AudioNative.waveOutMessage(legacyDevice, QueryInstanceIdSize, size, IntPtr.Zero);
                if (queryResult != 0) { DiagnosticLog.Write("audio.playback.identity_unavailable", "waveout_index", index, "stage", "instance_id_size", "wave_result", queryResult); return; }
                int bytes = Marshal.ReadInt32(size);
                if (bytes < 2 || bytes > 65536 || (bytes & 1) != 0) { DiagnosticLog.Write("audio.playback.identity_unavailable", "waveout_index", index, "stage", "instance_id_size_invalid", "bytes", bytes); return; }
                text = Marshal.AllocHGlobal(bytes);
                for (int offset = 0; offset < bytes; offset += 2) Marshal.WriteInt16(text, offset, 0);
                queryResult = AudioNative.waveOutMessage(legacyDevice, QueryInstanceId, text, new IntPtr(bytes));
                if (queryResult != 0) { DiagnosticLog.Write("audio.playback.identity_unavailable", "waveout_index", index, "stage", "instance_id", "wave_result", queryResult); return; }
                Marshal.WriteInt16(text, bytes - 2, 0);
                id = Marshal.PtrToStringUni(text);
                if (String.IsNullOrEmpty(id)) { id = null; DiagnosticLog.Write("audio.playback.identity_unavailable", "waveout_index", index, "stage", "empty_instance_id"); return; }
                DiagnosticLog.Write("audio.playback.identity_resolved", "waveout_index", index, "persistent_endpoint", id);
                int initialized = AudioNative.CoInitializeEx(IntPtr.Zero, 0);
                uninitialize = initialized >= 0;
                if (initialized < 0 && initialized != unchecked((int)0x80010106)) return;
                enumerator = (AudioNative.IMMDeviceEnumerator)new AudioNative.MMDeviceEnumerator();
                if (enumerator.GetDevice(id, out device) >= 0)
                {
                    name = AudioNative.GetDeviceName(device, id);
                    if (String.Equals(name, id, StringComparison.Ordinal)) name = null;
                }
            }
            catch (Exception error) { DiagnosticLog.Error("audio.playback.identity_failed", error, "waveout_index", index); }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
                if (uninitialize) AudioNative.CoUninitialize();
                if (text != IntPtr.Zero) Marshal.FreeHGlobal(text);
                if (size != IntPtr.Zero) Marshal.FreeHGlobal(size);
            }
        }
    }

    // WASAPI capture owns every COM interface on one MTA thread. Decoder callbacks
    // use another single worker and a bounded queue, never the audio engine thread.
    public sealed class AudioCapture : IDisposable
    {
        private const int QueueLimit = 8;
        private readonly Action<float[]> samples;
        private readonly bool loopback;
        private readonly string endpointId;
        private readonly object gate = new object();
        private readonly object queueGate = new object();
        private readonly Queue<float[]> queue = new Queue<float[]>();
        private ManualResetEvent quit;
        private Thread captureThread, callbackThread;
        private volatile bool stopping;
        private volatile bool running;
        private bool started, disposed, captureFinished;
        private Exception pendingError;
        private long droppedChunks, discontinuities;
        private long deliveredChunks, deliveredSamples, callbackErrors;
        private readonly string diagnosticId = Guid.NewGuid().ToString("N");

        public event Action<Exception> OnError;
        public bool IsRunning { get { return running; } }
        public long DroppedChunks { get { return Interlocked.Read(ref droppedChunks); } }
        public long Discontinuities { get { return Interlocked.Read(ref discontinuities); } }

        public AudioCapture(Action<float[]> samples, bool loopback, string endpointId = null)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (endpointId != null && (String.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 4096))
                throw new ArgumentException("Invalid audio endpoint ID.", "endpointId");
            // Recording a microphone requires an explicit selection. There is no implicit
            // fallback from the virtual webcam microphone to a physical microphone.
            if (!loopback && endpointId == null)
                throw new ArgumentException("Select an explicit virtual microphone endpoint before capture.", "endpointId");
            this.samples = samples;
            this.loopback = loopback;
            this.endpointId = endpointId;
        }

        public static AudioEndpoint[] EnumerateEndpoints(bool loopback)
        {
            AudioEndpoint[] result = null;
            Exception failure = null;
            Thread worker = new Thread(delegate()
            {
                int initialized = AudioNative.CoInitializeEx(IntPtr.Zero, 0);
                if (initialized < 0) { failure = Marshal.GetExceptionForHR(initialized); return; }
                AudioNative.IMMDeviceEnumerator enumerator = null;
                AudioNative.IMMDeviceCollection collection = null;
                try
                {
                    enumerator = (AudioNative.IMMDeviceEnumerator)new AudioNative.MMDeviceEnumerator();
                    AudioNative.Check(enumerator.EnumAudioEndpoints(loopback ? 0 : 1, 1, out collection));
                    uint count;
                    AudioNative.Check(collection.GetCount(out count));
                    List<AudioEndpoint> endpoints = new List<AudioEndpoint>();
                    for (uint index = 0; index < count; index++)
                    {
                        AudioNative.IMMDevice device = null;
                        try
                        {
                            AudioNative.Check(collection.Item(index, out device));
                            string id = AudioNative.GetDeviceId(device);
                            endpoints.Add(new AudioEndpoint(id, AudioNative.GetDeviceName(device, id)));
                        }
                        finally { AudioNative.Release(device); }
                    }
                    result = endpoints.ToArray();
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    AudioNative.Release(collection);
                    AudioNative.Release(enumerator);
                    AudioNative.CoUninitialize();
                }
            }) { IsBackground = true, Name = "Audio endpoint enumeration" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            if (!worker.Join(5000)) throw new TimeoutException("Audio endpoint enumeration timed out.");
            if (failure != null) throw new InvalidOperationException("Could not enumerate audio endpoints.", failure);
            return result ?? new AudioEndpoint[0];
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("AudioCapture");
                if (started) throw new InvalidOperationException("Audio capture has already been started.");
                started = true;
                quit = new ManualResetEvent(false);
                callbackThread = new Thread(DeliverSamples) { IsBackground = true, Name = "Audio decoder delivery" };
                captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = loopback ? "Horizon audio loopback" : "Selected virtual microphone" };
                captureThread.SetApartmentState(ApartmentState.MTA);
                try { callbackThread.Start(); captureThread.Start(); }
                catch
                {
                    stopping = true;
                    quit.Set();
                    quit.Dispose();
                    quit = null;
                    if ((captureThread.ThreadState & ThreadState.Unstarted) != 0) captureThread = null;
                    if ((callbackThread.ThreadState & ThreadState.Unstarted) != 0) callbackThread = null;
                    lock (queueGate) { captureFinished = true; Monitor.PulseAll(queueGate); }
                    throw;
                }
            }
        }

        private void CaptureLoop()
        {
            AudioNative.IMMDeviceEnumerator enumerator = null;
            AudioNative.IMMDevice device = null;
            AudioNative.IAudioClient client = null;
            AudioNative.IAudioCaptureClient capture = null;
            IntPtr formatPointer = IntPtr.Zero;
            bool initialized = false, audioStarted = false;
            string stage = "CoInitializeEx", resolvedEndpoint = null, resolvedName = null;
            CaptureTrace trace = DiagnosticLog.IsEnabled ? new CaptureTrace(this) : null;
            try
            {
                AudioNative.Check(AudioNative.CoInitializeEx(IntPtr.Zero, 0));
                initialized = true;
                if (stopping) return;
                enumerator = (AudioNative.IMMDeviceEnumerator)new AudioNative.MMDeviceEnumerator();
                stage = endpointId == null ? "GetDefaultAudioEndpoint" : "GetDevice";
                if (endpointId == null) AudioNative.Check(enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 1, out device));
                else AudioNative.Check(enumerator.GetDevice(endpointId, out device));
                if (trace != null)
                {
                    try
                    {
                        resolvedEndpoint = AudioNative.GetDeviceId(device);
                        resolvedName = AudioNative.GetDeviceName(device, resolvedEndpoint);
                        uint state; int stateResult = device.GetState(out state);
                        DiagnosticLog.Write("audio.capture.device.resolved", "capture", diagnosticId, "requested_endpoint", endpointId,
                            "endpoint", resolvedEndpoint, "device_name", resolvedName, "flow", loopback ? "render" : "capture",
                            "default_role", endpointId == null ? "multimedia" : "explicit", "default_role_value", endpointId == null ? 1 : -1,
                            "loopback", loopback, "device_state", stateResult >= 0 ? (object)state : null, "state_hresult", "0x" + stateResult.ToString("X8", CultureInfo.InvariantCulture));
                    }
                    catch (Exception error) { DiagnosticLog.Error("audio.capture.device_diagnostics_failed", error, "capture", diagnosticId); }
                    trace.Endpoint = resolvedEndpoint;
                }
                object audioObject;
                Guid clientId = typeof(AudioNative.IAudioClient).GUID;
                stage = "Activate_IAudioClient";
                AudioNative.Check(device.Activate(ref clientId, 23, IntPtr.Zero, out audioObject));
                client = (AudioNative.IAudioClient)audioObject;
                stage = "GetMixFormat";
                AudioNative.Check(client.GetMixFormat(out formatPointer));
                AudioFormat format = AudioFormat.FromNative(formatPointer);
                AudioResampler resampler = new AudioResampler(format.SampleRate);
                stage = "Initialize";
                AudioNative.Check(client.Initialize(0, loopback ? 0x00020000U : 0, 2000000, 0, formatPointer, IntPtr.Zero));
                ushort waveTag = (ushort)Marshal.ReadInt16(formatPointer, 0);
                DiagnosticLog.Write("audio.capture.format.initialized", "api", "WASAPI_shared", "capture", diagnosticId, "loopback", loopback,
                    "endpoint", resolvedEndpoint ?? endpointId ?? "default_render", "requested_endpoint", endpointId, "device_name", resolvedName,
                    "sample_kind", format.IsFloat ? "IEEE_FLOAT" : "PCM", "wave_tag", waveTag, "sample_rate", format.SampleRate, "channels", format.Channels,
                    "bits", format.Bits, "valid_bits", waveTag == 0xfffe ? (ushort)Marshal.ReadInt16(formatPointer, 18) : format.Bits,
                    "block_align", format.BlockAlign, "average_bytes_per_second", (uint)Marshal.ReadInt32(formatPointer, 8),
                    "normalized_sample_rate", 48000, "normalized_channels", 1, "normalized_sample_kind", "float32");
                Guid captureId = typeof(AudioNative.IAudioCaptureClient).GUID;
                object captureObject;
                stage = "GetService_IAudioCaptureClient";
                AudioNative.Check(client.GetService(ref captureId, out captureObject));
                capture = (AudioNative.IAudioCaptureClient)captureObject;
                if (stopping) return;
                stage = "Start";
                AudioNative.Check(client.Start());
                audioStarted = true;
                running = true;
                if (trace != null) DiagnosticLog.Write("audio.capture.engine.started", "capture", diagnosticId, "endpoint", resolvedEndpoint, "loopback", loopback);
                while (!stopping && !quit.WaitOne(5))
                {
                    uint available;
                    stage = "GetNextPacketSize";
                    AudioNative.Check(capture.GetNextPacketSize(out available));
                    if (trace != null) trace.Poll(available);
                    while (available != 0 && !stopping)
                    {
                        IntPtr pointer;
                        uint frames, flags;
                        ulong devicePosition, clockPosition;
                        stage = "GetBuffer";
                        AudioNative.Check(capture.GetBuffer(out pointer, out frames, out flags, out devicePosition, out clockPosition));
                        if (frames == 0) break;
                        float[] converted;
                        try
                        {
                            stage = "convert_samples";
                            if (frames > format.SampleRate * 2) throw new InvalidDataException("The audio endpoint returned an oversized packet.");
                            int packetBytes = checked((int)frames * format.BlockAlign);
                            if (packetBytes > 16 * 1024 * 1024) throw new InvalidDataException("The audio endpoint packet exceeds its memory limit.");
                            byte[] bytes = new byte[packetBytes];
                            if ((flags & 2) == 0)
                            {
                                if (pointer == IntPtr.Zero) throw new InvalidDataException("The audio endpoint returned a null packet.");
                                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                            }
                            if ((flags & 1) != 0)
                            {
                                Interlocked.Increment(ref discontinuities);
                                resampler.Reset();
                            }
                            converted = resampler.Convert(format.ToMono(bytes, (int)frames));
                        }
                        finally
                        {
                            string previousStage = stage; stage = "ReleaseBuffer";
                            AudioNative.Check(capture.ReleaseBuffer(frames)); stage = previousStage;
                        }
                        if (trace != null) trace.Packet(frames, flags, converted, devicePosition, clockPosition);
                        if (converted.Length > 0) Enqueue(converted);
                        stage = "GetNextPacketSize";
                        AudioNative.Check(capture.GetNextPacketSize(out available));
                        if (trace != null) { trace.Poll(available); trace.Summary(false, stage); }
                    }
                    if (trace != null) trace.Summary(false, stage);
                }
            }
            catch (Exception error)
            {
                if (DiagnosticLog.IsEnabled) DiagnosticLog.Error("audio.capture.failed", error, "capture", diagnosticId, "endpoint", resolvedEndpoint ?? endpointId,
                    "stage", stage, "stopping", stopping, "loopback", loopback);
                if (!stopping)
                    lock (queueGate) { pendingError = error; Monitor.PulseAll(queueGate); }
            }
            finally
            {
                running = false;
                if (trace != null) trace.Summary(true, stage);
                if (audioStarted) { try { client.Stop(); } catch { } }
                AudioNative.Release(capture);
                AudioNative.Release(client);
                if (formatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPointer);
                AudioNative.Release(device);
                AudioNative.Release(enumerator);
                if (initialized) AudioNative.CoUninitialize();
                lock (queueGate) { captureFinished = true; Monitor.PulseAll(queueGate); }
                lock (gate)
                {
                    if (disposed && quit != null) { quit.Dispose(); quit = null; }
                }
            }
        }

        private void Enqueue(float[] values)
        {
            lock (queueGate)
            {
                if (stopping) return;
                if (queue.Count >= QueueLimit) { Interlocked.Increment(ref droppedChunks); return; }
                queue.Enqueue(values);
                Monitor.PulseAll(queueGate);
            }
        }

        private void DeliverSamples()
        {
            while (!stopping)
            {
                float[] values = null;
                Exception error = null;
                lock (queueGate)
                {
                    while (!stopping && pendingError == null && queue.Count == 0 && !captureFinished) Monitor.Wait(queueGate);
                    if (stopping) return;
                    if (pendingError != null) { error = pendingError; pendingError = null; }
                    else if (queue.Count != 0) values = queue.Dequeue();
                    else if (captureFinished) return;
                }
                try
                {
                    if (error != null) { Action<Exception> handler = OnError; if (handler != null) handler(error); }
                    else if (values != null)
                    {
                        long blocks = Interlocked.Increment(ref deliveredChunks);
                        Interlocked.Add(ref deliveredSamples, values.Length);
                        if (blocks == 1 && DiagnosticLog.IsEnabled) DiagnosticLog.Write("audio.capture.delivery.first", "capture", diagnosticId,
                            "requested_endpoint", endpointId, "samples", values.Length);
                        samples(values);
                    }
                }
                catch (Exception callbackError)
                {
                    Interlocked.Increment(ref callbackErrors);
                    if (DiagnosticLog.IsEnabled) DiagnosticLog.Error("audio.capture.delivery.failed", callbackError, "capture", diagnosticId, "error_callback", error != null);
                    // An error subscriber is application code too. Never let it tear down a
                    // native capture thread or create an unbounded recursive error callback.
                    if (error == null)
                    {
                        try { Action<Exception> handler = OnError; if (handler != null) handler(callbackError); }
                        catch { }
                    }
                }
            }
        }

        public void Dispose()
        {
            Thread capture, delivery;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                stopping = true;
                capture = captureThread;
                delivery = callbackThread;
                if (quit != null) quit.Set();
                if (capture == null && quit != null) { quit.Dispose(); quit = null; }
            }
            lock (queueGate) { queue.Clear(); pendingError = null; Monitor.PulseAll(queueGate); }
            if (capture != null && capture != Thread.CurrentThread) capture.Join(1000);
            if (delivery != null && delivery != Thread.CurrentThread) delivery.Join(250);
            lock (gate)
            {
                if (quit != null && (capture == null || !capture.IsAlive)) { quit.Dispose(); quit = null; }
            }
            if (started && DiagnosticLog.IsEnabled) DiagnosticLog.Write("audio.capture.disposed", "capture", diagnosticId, "requested_endpoint", endpointId,
                "delivered_blocks", Interlocked.Read(ref deliveredChunks), "delivered_samples", Interlocked.Read(ref deliveredSamples),
                "dropped_blocks", Interlocked.Read(ref droppedChunks), "discontinuities", Interlocked.Read(ref discontinuities),
                "callback_errors", Interlocked.Read(ref callbackErrors), "capture_thread_alive", capture != null && capture.IsAlive,
                "delivery_thread_alive", delivery != null && delivery.IsAlive);
        }

        // Capture-thread counters include idle polls, so a healthy engine with no
        // callbacks is distinguishable from silence, conversion or decoder failure.
        private sealed class CaptureTrace
        {
            private readonly AudioCapture owner;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly AudioSignalStatistics intervalSignal = new AudioSignalStatistics();
            private readonly AudioSignalStatistics totalSignal = new AudioSignalStatistics();
            private long polls, emptyPolls, packets, frames, silentPackets, silentFrames, lastPacketAt = -1, lastNonzeroAt = -1;
            private long priorPolls, priorEmptyPolls, priorPackets, priorFrames, priorSilentPackets, priorSilentFrames;
            private long priorDeliveredBlocks, priorDeliveredSamples, priorDropped, priorDiscontinuities, lastSummaryAt;
            internal string Endpoint;

            internal CaptureTrace(AudioCapture owner) { this.owner = owner; }
            internal void Poll(uint available) { polls++; if (available == 0) emptyPolls++; }
            internal void Packet(uint rawFrames, uint flags, float[] converted, ulong devicePosition, ulong clockPosition)
            {
                packets++; frames += rawFrames; lastPacketAt = clock.ElapsedMilliseconds;
                if ((flags & 2) != 0) { silentPackets++; silentFrames += rawFrames; }
                intervalSignal.Observe(converted); totalSignal.Observe(converted);
                if (packets == 1) DiagnosticLog.Write("audio.capture.packet.first", "capture", owner.diagnosticId, "endpoint", Endpoint,
                    "elapsed_ms", lastPacketAt, "raw_frames", rawFrames, "converted_samples", converted.Length, "flags", flags,
                    "silent", (flags & 2) != 0, "discontinuity", (flags & 1) != 0, "timestamp_error", (flags & 4) != 0,
                    "device_position", devicePosition, "clock_position", clockPosition);
                if (totalSignal.NonZeroSamples > 0)
                {
                    if (lastNonzeroAt < 0) DiagnosticLog.Write("audio.capture.nonzero.first", "capture", owner.diagnosticId,
                        "endpoint", Endpoint, "elapsed_ms", lastPacketAt, "peak", intervalSignal.Peak, "rms", intervalSignal.Rms);
                    // Silence after an earlier signal must not refresh this age.
                    bool nonzero = false;
                    for (int index = 0; index < converted.Length; index++) if (Math.Abs(converted[index]) > 0.000001f) { nonzero = true; break; }
                    if (nonzero) lastNonzeroAt = lastPacketAt;
                }
            }

            internal void Summary(bool final, string stage)
            {
                if (!DiagnosticLog.IsEnabled) return;
                long now = clock.ElapsedMilliseconds;
                if (!final && now - lastSummaryAt < 1000) return;
                long delivered = Interlocked.Read(ref owner.deliveredChunks), deliveredSamples = Interlocked.Read(ref owner.deliveredSamples);
                long dropped = Interlocked.Read(ref owner.droppedChunks), discontinuities = Interlocked.Read(ref owner.discontinuities);
                DiagnosticLog.Write("audio.capture.heartbeat", "capture", owner.diagnosticId, "endpoint", Endpoint, "requested_endpoint", owner.endpointId,
                    "loopback", owner.loopback, "stage", stage, "running", owner.running, "final", final, "elapsed_ms", now, "interval_ms", now - lastSummaryAt,
                    "polls", polls - priorPolls, "empty_polls", emptyPolls - priorEmptyPolls, "native_packets", packets - priorPackets,
                    "raw_frames", frames - priorFrames, "converted_samples", intervalSignal.Samples,
                    "silent_packets", silentPackets - priorSilentPackets, "silent_frames", silentFrames - priorSilentFrames,
                    "delivered_blocks", delivered - priorDeliveredBlocks, "delivered_samples", deliveredSamples - priorDeliveredSamples,
                    "dropped_blocks", dropped - priorDropped, "discontinuities", discontinuities - priorDiscontinuities,
                    "peak", intervalSignal.Peak, "rms", intervalSignal.Rms, "nonzero_samples", intervalSignal.NonZeroSamples,
                    "nonfinite_samples", intervalSignal.NonFiniteSamples, "last_packet_age_ms", lastPacketAt < 0 ? -1 : now - lastPacketAt,
                    "last_nonzero_age_ms", lastNonzeroAt < 0 ? -1 : now - lastNonzeroAt, "total_polls", polls, "total_empty_polls", emptyPolls,
                    "total_native_packets", packets, "total_raw_frames", frames, "total_converted_samples", totalSignal.Samples,
                    "total_silent_packets", silentPackets, "total_silent_frames", silentFrames, "total_peak", totalSignal.Peak, "total_rms", totalSignal.Rms,
                    "total_delivered_blocks", delivered, "total_delivered_samples", deliveredSamples, "total_dropped_blocks", dropped,
                    "total_discontinuities", discontinuities, "total_callback_errors", Interlocked.Read(ref owner.callbackErrors));
                priorPolls = polls; priorEmptyPolls = emptyPolls; priorPackets = packets; priorFrames = frames;
                priorSilentPackets = silentPackets; priorSilentFrames = silentFrames;
                priorDeliveredBlocks = delivered; priorDeliveredSamples = deliveredSamples; priorDropped = dropped; priorDiscontinuities = discontinuities;
                intervalSignal.Reset(); lastSummaryAt = now;
            }
        }
    }

    // Only scalar signal statistics are retained; no samples or PCM are logged.
    internal sealed class AudioSignalStatistics
    {
        internal long Samples { get; private set; }
        internal long NonZeroSamples { get; private set; }
        internal long NonFiniteSamples { get; private set; }
        internal double Peak { get; private set; }
        private double squares;
        internal double Rms { get { return Samples == 0 ? 0 : Math.Sqrt(squares / Samples); } }

        internal void Observe(float[] values)
        {
            if (values == null) return;
            foreach (float value in values) Add(value);
        }
        internal void ObservePcm16(byte[] values)
        {
            if (values == null || (values.Length & 1) != 0) throw new ArgumentException("Complete PCM16 samples are required.", "values");
            for (int index = 0; index < values.Length; index += 2) Add((short)(values[index] | (values[index + 1] << 8)) / 32768.0);
        }
        private void Add(double value)
        {
            Samples++;
            if (Double.IsNaN(value) || Double.IsInfinity(value)) { NonFiniteSamples++; return; }
            double absolute = Math.Abs(value);
            if (absolute > 0.000001) NonZeroSamples++;
            if (absolute > Peak) Peak = absolute;
            squares += value * value;
        }
        internal void Reset() { Samples = NonZeroSamples = NonFiniteSamples = 0; Peak = squares = 0; }
    }

    internal static class AudioSamples
    {
        internal static byte[] ToPcm16(float[] values)
        {
            if (values == null) throw new ArgumentNullException("mono48k");
            if (values.Length == 0 || values.Length > 48000 * 120) throw new ArgumentOutOfRangeException("mono48k", "Audio packets must contain between one sample and two minutes of audio.");
            byte[] bytes = new byte[checked(values.Length * 2)];
            for (int index = 0; index < values.Length; index++)
            {
                float value = values[index];
                if (Single.IsNaN(value) || Single.IsInfinity(value)) throw new ArgumentException("Audio samples must be finite.", "mono48k");
                short sample = (short)Math.Round(Math.Max(-0.25, Math.Min(0.25, value)) * 32767.0);
                bytes[index * 2] = (byte)sample;
                bytes[index * 2 + 1] = (byte)(sample >> 8);
            }
            return bytes;
        }
    }

    internal sealed class AudioFormat
    {
        internal readonly int SampleRate, Channels, Bits, BlockAlign;
        internal readonly bool IsFloat;
        internal AudioFormat(int sampleRate, int channels, int bits, int blockAlign, bool isFloat)
        {
            if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException("sampleRate");
            if (channels < 1 || channels > 32) throw new ArgumentOutOfRangeException("channels");
            if ((isFloat && bits != 32) || (!isFloat && bits != 16 && bits != 24 && bits != 32)) throw new NotSupportedException("The audio endpoint must provide float32 or PCM16/24/32 samples.");
            if (blockAlign < channels * (bits / 8) || blockAlign > channels * (bits / 8) + 64) throw new ArgumentOutOfRangeException("blockAlign");
            SampleRate = sampleRate; Channels = channels; Bits = bits; BlockAlign = blockAlign; IsFloat = isFloat;
        }

        internal static AudioFormat FromNative(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) throw new ArgumentNullException("pointer");
            ushort tag = (ushort)Marshal.ReadInt16(pointer, 0);
            int channels = (ushort)Marshal.ReadInt16(pointer, 2);
            int rate = Marshal.ReadInt32(pointer, 4);
            int align = (ushort)Marshal.ReadInt16(pointer, 12);
            int bits = (ushort)Marshal.ReadInt16(pointer, 14);
            bool isFloat;
            if (tag == 0xfffe)
            {
                int extra = (ushort)Marshal.ReadInt16(pointer, 16);
                if (extra < 22) throw new InvalidDataException("Truncated WAVEFORMATEXTENSIBLE descriptor.");
                int validBits = (ushort)Marshal.ReadInt16(pointer, 18);
                if (validBits < 1 || validBits > bits) throw new InvalidDataException("Invalid PCM valid-bit count.");
                byte[] subFormatBytes = new byte[16];
                Marshal.Copy(IntPtr.Add(pointer, 24), subFormatBytes, 0, 16);
                Guid subFormat = new Guid(subFormatBytes);
                if (subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) isFloat = true;
                else if (subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) isFloat = false;
                else throw new NotSupportedException("The audio endpoint mix format is not PCM or IEEE float.");
            }
            else if (tag == 1) isFloat = false;
            else if (tag == 3) isFloat = true;
            else throw new NotSupportedException("The audio endpoint mix format is not PCM or IEEE float.");
            return new AudioFormat(rate, channels, bits, align, isFloat);
        }

        internal float[] ToMono(byte[] bytes, int frames)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (frames < 0 || frames > SampleRate * 2 || bytes.Length != checked(frames * BlockAlign)) throw new ArgumentOutOfRangeException("frames");
            float[] mono = new float[frames];
            int sampleBytes = Bits / 8;
            for (int frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                for (int channel = 0; channel < Channels; channel++)
                {
                    int offset = frame * BlockAlign + channel * sampleBytes;
                    double value;
                    if (IsFloat) value = BitConverter.ToSingle(bytes, offset);
                    else if (Bits == 16) value = (short)(bytes[offset] | (bytes[offset + 1] << 8)) / 32768.0;
                    else if (Bits == 24)
                    {
                        int packed = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
                        if ((packed & 0x800000) != 0) packed |= unchecked((int)0xff000000);
                        value = packed / 8388608.0;
                    }
                    else value = BitConverter.ToInt32(bytes, offset) / 2147483648.0;
                    if (Double.IsNaN(value) || Double.IsInfinity(value)) value = 0;
                    sum += Math.Max(-1, Math.Min(1, value));
                }
                mono[frame] = (float)(sum / Channels);
            }
            return mono;
        }
    }

    // Linear interpolation carries both phase and the previous sample across packets.
    // Integer phase arithmetic makes chunking independent of stream length and packet size.
    internal sealed class AudioResampler
    {
        private readonly int inputRate;
        private long inputIndex, nextOutput;
        private float previous;
        private bool hasPrevious;
        internal AudioResampler(int inputRate)
        {
            if (inputRate < 8000 || inputRate > 384000) throw new ArgumentOutOfRangeException("inputRate");
            this.inputRate = inputRate;
        }
        internal void Reset() { inputIndex = 0; nextOutput = 0; previous = 0; hasPrevious = false; }
        internal float[] Convert(float[] source)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (source.Length > inputRate * 2) throw new ArgumentOutOfRangeException("source");
            if (inputRate == 48000) return source; // Owned mono PCM, already at the target rate.
            List<float> output = new List<float>((int)Math.Min(96001L, ((long)source.Length * 48000 / inputRate) + 2));
            foreach (float sample in source)
            {
                long edge = checked(inputIndex * 48000);
                if (!hasPrevious)
                {
                    output.Add(sample);
                    nextOutput = inputRate;
                    hasPrevious = true;
                }
                else
                {
                    while (nextOutput <= edge)
                    {
                        double fraction = (nextOutput - (edge - 48000)) / 48000.0;
                        output.Add((float)(previous + (sample - previous) * fraction));
                        nextOutput += inputRate;
                    }
                }
                previous = sample;
                inputIndex++;
            }
            return output.ToArray();
        }
    }

    internal sealed class AudioMemory : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal int Size { get; private set; }
        internal AudioMemory(int bytes) : base(true) { Size = bytes; SetHandle(Marshal.AllocHGlobal(bytes)); }
        internal void RetainForFaultedDriver() { SetHandleAsInvalid(); }
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }
    }

    internal static class AudioNative
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal struct WaveFormat
        {
            internal ushort Tag, Channels;
            internal uint SamplesPerSecond, AverageBytesPerSecond;
            internal ushort BlockAlign, BitsPerSample, ExtraSize;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct WaveHeader
        {
            internal IntPtr Data;
            internal uint BufferLength, BytesRecorded;
            internal IntPtr User;
            internal uint Flags, Loops;
            internal IntPtr Next, Reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WaveOutCaps
        {
            internal ushort ManufacturerId, ProductId;
            internal uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
            internal uint Formats;
            internal ushort Channels, Reserved;
            internal uint Support;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { internal Guid Format; internal uint Id; }
        [StructLayout(LayoutKind.Sequential)] internal struct PropBlob { internal uint Size; internal IntPtr Data; }
        [StructLayout(LayoutKind.Explicit)] internal struct PropVariant
        {
            [FieldOffset(0)] internal ushort Type;
            [FieldOffset(8)] internal IntPtr Pointer;
            [FieldOffset(8)] internal PropBlob Blob;
        }

        [DllImport("winmm.dll")] internal static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll")] internal static extern uint waveOutMessage(IntPtr device, uint message, IntPtr parameter1, IntPtr parameter2);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern uint waveOutGetDevCapsW(UIntPtr device, out WaveOutCaps caps, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveOutOpen(out IntPtr output, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] internal static extern uint waveOutGetID(IntPtr output, out uint deviceId);
        [DllImport("winmm.dll")] internal static extern uint waveOutGetVolume(IntPtr output, out uint volume);
        [DllImport("winmm.dll")] internal static extern uint waveOutPrepareHeader(IntPtr output, IntPtr header, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveOutWrite(IntPtr output, IntPtr header, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveOutReset(IntPtr output);
        [DllImport("winmm.dll")] internal static extern uint waveOutUnprepareHeader(IntPtr output, IntPtr header, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveOutClose(IntPtr output);
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint model);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        internal interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        internal interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid interfaceId, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
            [PreserveSig] int GetId(out IntPtr id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        internal interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        internal interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint flags, long duration, long period, IntPtr format, IntPtr session);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        internal interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong performancePosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        internal static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        internal static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try { Marshal.FinalReleaseComObject(value); }
                catch (InvalidComObjectException) { }
            }
        }
        internal static string GetDeviceId(IMMDevice device)
        {
            IntPtr id;
            Check(device.GetId(out id));
            try { return Marshal.PtrToStringUni(id); }
            finally { if (id != IntPtr.Zero) Marshal.FreeCoTaskMem(id); }
        }
        internal static string GetDeviceName(IMMDevice device, string fallback)
        {
            IPropertyStore store = null;
            PropVariant value = new PropVariant();
            try
            {
                Check(device.OpenPropertyStore(0, out store));
                PropertyKey key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                Check(store.GetValue(ref key, out value));
                return value.Type == 31 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : fallback;
            }
            catch (COMException) { return fallback; }
            finally { PropVariantClear(ref value); Release(store); }
        }
    }
}
