using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HorizonClient
{
    internal sealed partial class ServerForm
    {
        private AudioProbeNotice audioProbeNotice;
        private AudioModemKind? latestAudioProbeModem;
        private readonly HashSet<Guid> displayedAudioProbes = new HashSet<Guid>();
        private readonly Queue<Guid> displayedAudioProbeOrder = new Queue<Guid>();

        private void OnAudioProbeReceived(AudioDiagnosticProbe probe)
        {
            if (IsClosing || probe == null || !displayedAudioProbes.Add(probe.Id)) return;
            displayedAudioProbeOrder.Enqueue(probe.Id);
            while (displayedAudioProbeOrder.Count > ServerAudioBridge.MaximumRememberedProbes) displayedAudioProbes.Remove(displayedAudioProbeOrder.Dequeue());
            latestAudioProbeModem = probe.Modem;
            string message = UiText.Format("Тест аудио прошёл успешно.\r\nПроверочный сигнал клиента получен сервером.\r\nКодек: {0}.", "Audio test passed.\r\nThe server received the client's test signal.\r\nCodec: {0}.", AudioModemCatalog.GetCaption(probe.Modem));
            UiText.SetText(audioStatus, "Тест аудио прошёл успешно: {0}.", "Audio test passed: {0}.", UiText.KnownValue(AudioModemCatalog.GetCaption(probe.Modem)));
            DiagnosticLog.Write("server.audio.probe.confirmed", "probe", probe.Id, "modem", probe.Modem, "endpoint", selectedAudioId ?? "default_render");
            try { audioProbeNotification(message); }
            catch (Exception error) { DiagnosticLog.Error("server.audio.probe.notification_failed", error, "probe", probe.Id); }
        }

        private void ShowAudioProbeNotice(string message)
        {
            if (IsClosing) return;
            if (audioProbeNotice == null || audioProbeNotice.IsDisposed)
            {
                audioProbeNotice = new AudioProbeNotice();
                Rectangle area = Screen.FromControl(this).WorkingArea;
                audioProbeNotice.Location = new Point(Math.Max(area.Left, area.Right - audioProbeNotice.Width - 16), Math.Max(area.Top, area.Bottom - audioProbeNotice.Height - 16));
            }
            audioProbeNotice.Present(message);
            if (latestAudioProbeModem.HasValue) audioProbeNotice.SetProbeCaption(latestAudioProbeModem.Value);
        }

        private void RefreshAudioProbeLanguage()
        {
            if (audioProbeNotice != null && !audioProbeNotice.IsDisposed) UiText.Apply(audioProbeNotice);
        }

        private void CloseAudioProbeNotice()
        {
            AudioProbeNotice previous = audioProbeNotice; audioProbeNotice = null;
            if (previous != null) previous.Dispose();
        }
    }

    // One modeless notice, including when the main server is hidden in the tray.
    // It never takes keyboard focus away from a remote desktop or blocks receive.
    internal sealed class AudioProbeNotice : Form
    {
        private readonly Label message = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Timer expiry = new Timer { Interval = 8000 };
        private bool resourcesDisposed;
        internal AudioProbeNotice()
        {
            UiText.SetText(this, "Проверка аудио", "Audio test"); Font = new Font("Segoe UI", 10F); ClientSize = new Size(435, 115);
            FormBorderStyle = FormBorderStyle.FixedToolWindow; ShowInTaskbar = false; TopMost = true;
            MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.Manual; Padding = new Padding(12);
            Controls.Add(message); expiry.Tick += delegate { Close(); };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        internal void Present(string text) { UiText.SetKnownText(message, text); expiry.Stop(); expiry.Start(); if (!Visible) Show(); }
        internal void SetProbeCaption(AudioModemKind kind)
        { UiText.SetText(message, "Тест аудио прошёл успешно.\r\nПроверочный сигнал клиента получен сервером.\r\nКодек: {0}.", "Audio test passed.\r\nThe server received the client's test signal.\r\nCodec: {0}.", UiText.KnownValue(AudioModemCatalog.GetCaption(kind))); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed) { resourcesDisposed = true; expiry.Stop(); expiry.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
