using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

internal static class ServerAudioProbeTests
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    [STAThread]
    static int Main()
    {
        UiText.SetLanguage(UiLanguage.Russian); // This fixture checks the Russian probe notification.
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            int notices = 0; string last = null;
            using (var form = new ServerForm(null, false, audioProbeNotification: delegate(string text) { notices++; last = text; }))
            {
                Check(form.Text.Contains(typeof(ServerForm).Assembly.GetName().Version.ToString(3)), "Server title contains its compiled version.");
                Check(form.Controls.Find("AudioRoutesResults", true).Length == 0 && form.Controls.Find("AudioRoutesTest", true).Length == 0, "No route calibration table/button remains.");
                Action<AudioDiagnosticProbe> receive = (Action<AudioDiagnosticProbe>)Delegate.CreateDelegate(typeof(Action<AudioDiagnosticProbe>), form,
                    typeof(ServerForm).GetMethod("OnAudioProbeReceived", BindingFlags.Instance | BindingFlags.NonPublic));
                var services = new FakeServices();
                using (var bridge = new ServerAudioBridge(form, Guid.NewGuid(), delegate { throw new Exception("No clipboard publication for probes."); }, delegate { }, delegate { }, services))
                using (var mux = new AudioReceiveMultiplexer(bridge.ReceivePacket, true))
                {
                    bridge.AudioProbeReceived = receive;
                    Guid id = Guid.NewGuid(); byte[] bytes = AudioDiagnosticProbe.Encode(id, AudioModemKind.Quiet, DateTime.UtcNow.Ticks);
                    float[] pcm = AudioTransmissionEncoder.Encode(AudioModemKind.Quiet, id, 0, bytes).Samples;
                    for (int pass = 0; pass < 3; pass++)
                        for (int offset = 0; offset < pcm.Length; offset += 480) {
                            float[] part = new float[Math.Min(480, pcm.Length - offset)];
                            for (int i = 0; i < part.Length; i++) part[i] = (short)Math.Round(pcm[offset + i] * .67 * 32767) / 32768f;
                            mux.Feed(part);
                        }
                    var clock = Stopwatch.StartNew();
                    while (notices == 0 && clock.ElapsedMilliseconds < 10000) { services.Drain(); Thread.Sleep(1); }
                    Check(notices == 1 && last.Contains("Тест аудио прошёл успешно") && last.Contains("Quiet"), "Real Quiet PCM reaches production bridge and triggers the actual form notification.");
                    for (int i = 0; i < 3; i++) bridge.ReceivePacket(new Packet { TransferId = id, Index = 0, Count = 1, TotalLength = bytes.Length, Data = bytes, ChunkSize = VisualProtocol.ChunkSize });
                    services.Drain(); Check(notices == 1 && !bridge.IsReceiving, "Repeated probe passes cannot cause repeated popup or clipboard receive.");
                    Guid next = Guid.NewGuid(); bytes = AudioDiagnosticProbe.Encode(next, AudioModemKind.Aicodix, DateTime.UtcNow.Ticks);
                    bridge.ReceivePacket(new Packet { TransferId = next, Index = 0, Count = 1, TotalLength = bytes.Length, Data = bytes, ChunkSize = VisualProtocol.ChunkSize });
                    services.Drain(); Check(notices == 2 && last.Contains("aicodix"), "A different user verification may notify again with its chosen codec.");
                    receive(new AudioDiagnosticProbe { Id = id, Modem = AudioModemKind.Quiet });
                    Check(notices == 2, "Form-level deduplication also suppresses old passes after an audio receiver restart.");
                }
                Check(!form.Visible, "The test never opens a server or notification window.");
            }
            using (var notice = new AudioProbeNotice()) {
                Check(!notice.ShowInTaskbar && notice.TopMost && !notice.Visible, "The small notice has independent tray-compatible presentation without showing in this test.");
                Check((bool)typeof(AudioProbeNotice).GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(notice, null), "The popup does not take the user's keyboard focus.");
                notice.Dispose(); notice.Dispose();
                Check((bool)typeof(AudioProbeNotice).GetField("resourcesDisposed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(notice), "Notice timer is disposed exactly with its form.");
            }
            Console.WriteLine("Server audio probe checks passed: " + checks + "; real Quiet PCM, production bridge/form, repeated packets, hidden injected notifications only."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    sealed class FakeServices : ServerAudioBridgeServices
    {
        readonly ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();
        internal override void Dispatch(Control owner, Action action) { actions.Enqueue(action); }
        internal void Drain() { Action action; while (actions.TryDequeue(out action)) action(); }
    }
}
