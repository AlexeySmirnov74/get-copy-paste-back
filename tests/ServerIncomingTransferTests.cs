using System;
using System.Reflection;
using System.Windows.Forms;
using HorizonClient;

// Actual hidden form callbacks with notification/clipboard seams. No audio,
// clipboard, camera, file payloads, applications or visible windows are opened.
internal static class ServerIncomingTransferTests
{
    private static int checks;
    [STAThread]
    private static int Main()
    {
        UiText.SetLanguage(UiLanguage.Russian); // Journal assertions intentionally use Russian prose.
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            int tones = 0, clipboardReads = 0;
            using (var form = new ServerForm(null, false, null, new ServerClipboardServices {
                Sequence = delegate { return 0; },
                Capture = delegate { clipboardReads++; throw new Exception("No clipboard read is authorized in this fixture."); },
                Accepted = delegate { throw new Exception("Receiving a file cannot play the local-copy acceptance cue."); }
            }, null, delegate { tones++; }))
            {
                var log = (TextBox)form.Controls.Find("TransferLogText", true)[0];
                var enabled = (CheckBox)form.Controls.Find("TransferLogEnabled", true)[0];
                Check(enabled.Checked, "The transfer journal starts enabled.");
                Guid first = Guid.NewGuid(), second = Guid.NewGuid();
                Call(form, "OnIncomingTransferStarted", first, 1536);
                Check(tones == 0 && log.Text.Contains("Начался приём"), "A start event records reception without claiming delivery or playing completion.");
                Call(form, "OnIncomingTransferCompleted", first, 1536, 12500L);
                Check(tones == 1 && Completions(log.Text) == 1 && log.Text.Contains("Можно нажать Ctrl+V"), "A published reverse transfer records completion and plays one injected delivery cue.");
                Call(form, "OnIncomingTransferStarted", second, 1536);
                Call(form, "OnIncomingTransferCompleted", second, 1536, 14000L);
                Check(tones == 2 && Completions(log.Text) == 2 && first != second, "Copying the same-size file again under a new identity records another independent completion.");
                enabled.Checked = false; string before = log.Text;
                Call(form, "OnIncomingTransferStarted", Guid.NewGuid(), 1536);
                Call(form, "OnIncomingTransferCompleted", Guid.NewGuid(), 1536, 15000L);
                Check(tones == 3 && log.Text == before, "Disabling the optional journal suppresses text while retaining the confirmed delivery cue.");
                Call(form, "OnIncomingTransferFailed", Guid.NewGuid(), "audio_inactive_partial");
                Call(form, "OnIncomingTransferRetrying", Guid.NewGuid(), 1, 2);
                Check(tones == 3 && log.Text == before, "Failure or retry metadata cannot claim completed delivery or play its cue.");
                enabled.Checked = true;
                Call(form, "OnIncomingTransferFailed", Guid.NewGuid(), "audio_inactive_partial");
                Check(log.Text.Contains("не завершён") && Completions(log.Text) == 2 && tones == 3, "An enabled journal distinguishes incomplete reception from the two verified completions.");
                EventHandler brokenJournal = delegate { throw new InvalidOperationException("Synthetic journal repaint failure."); };
                bool journalFailed = false;
                log.TextChanged += brokenJournal;
                try { Call(form, "OnIncomingTransferCompleted", Guid.NewGuid(), 1536, 16000L); }
                catch (InvalidOperationException) { journalFailed = true; }
                finally { log.TextChanged -= brokenJournal; }
                Check(journalFailed, "The fixture actually exercised a journal failure while appending completion.");
                Check(tones == 4, "An exception in the optional journal cannot suppress the already-published transfer's delivery cue.");
                Guid forward = Guid.NewGuid();
                Call(form, "BeginForwardJournal", forward, 1536);
                Call(form, "CompleteForwardDelivery", new DeliveryReceipt { Session = Guid.NewGuid(), Transfer = forward, EncodedBytes = 1536 }, true);
                Check(tones == 5 && log.Text.Contains("переданы на удалённый доступ"), "A confirmed forward receipt records completion and plays exactly one injected delivery cue.");
                enabled.Checked = false; before = log.Text; forward = Guid.NewGuid();
                Call(form, "BeginForwardJournal", forward, 1536);
                Call(form, "CompleteForwardDelivery", new DeliveryReceipt { Session = Guid.NewGuid(), Transfer = forward, EncodedBytes = 1536 }, false);
                Check(tones == 6 && log.Text == before, "Disabling the journal never suppresses a confirmed forward delivery cue.");
                enabled.Checked = true; forward = Guid.NewGuid();
                Call(form, "BeginForwardJournal", forward, 1536);
                journalFailed = false; log.TextChanged += brokenJournal;
                try { Call(form, "CompleteForwardDelivery", new DeliveryReceipt { Session = Guid.NewGuid(), Transfer = forward, EncodedBytes = 1536 }, true); }
                catch (InvalidOperationException) { journalFailed = true; }
                finally { log.TextChanged -= brokenJournal; }
                Check(journalFailed, "The fixture exercised an actual journal failure on confirmed forward delivery.");
                Check(tones == 7, "Journal failure cannot suppress the confirmed forward delivery cue either.");
                Control[] sounds = form.Controls.Find("DeliveredSoundEnabled", true);
                Check(sounds.Length == 1 && sounds[0] is CheckBox && ((CheckBox)sounds[0]).Checked, "Remote clipboard ready sound defaults on.");
                var readySound = (CheckBox)sounds[0]; readySound.Checked = false;
                forward = Guid.NewGuid(); Call(form, "BeginForwardJournal", forward, 1536);
                Call(form, "CompleteForwardDelivery", new DeliveryReceipt { Session = Guid.NewGuid(), Transfer = forward, EncodedBytes = 1536 }, true);
                Check(tones == 7 && log.Text.Contains("переданы на удалённый доступ"), "Muting forward completion keeps verified completion state and journal without playing the cue.");
                Call(form, "OnIncomingTransferCompleted", Guid.NewGuid(), 1536, 17000L);
                Check(tones == 8, "The forward sound preference preserves the independent client-to-server reception cue.");
                readySound.Checked = true; forward = Guid.NewGuid(); Call(form, "BeginForwardJournal", forward, 1536);
                Call(form, "CompleteForwardDelivery", new DeliveryReceipt { Session = Guid.NewGuid(), Transfer = forward, EncodedBytes = 1536 }, false);
                Check(tones == 9, "Reenabling forward completion sound restores its cue for a later confirmed receipt.");
                Check(clipboardReads == 0, "Transfer callbacks never inspect the actual Windows clipboard.");
                StopTimer(form, "debounce"); StopTimer(form, "benchmarkUiTimer");
            }
            Console.WriteLine("Server incoming transfer checks passed: " + checks + ". Hidden actual form callbacks and injected delivery cue only; no audio, clipboard or devices.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static int Completions(string text) { return text.Split(new[] { "От удалённого доступа поступили данные" }, StringSplitOptions.None).Length - 1; }
    private static void Call(ServerForm form, string name, params object[] arguments)
    {
        try { typeof(ServerForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void StopTimer(ServerForm form, string name)
    { ((Timer)typeof(ServerForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form)).Stop(); }
    private static void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
}
