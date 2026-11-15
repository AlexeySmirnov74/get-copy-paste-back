using System;

namespace HorizonClient
{
    internal sealed partial class ServerForm
    {
        private string unavailableAudioId;

        private string AllAudioOutputsCaption
        { get { return UiText.Get("Все аудиоустройства вывода", "All audio outputs"); } }

        private string UnavailableAudioOutputCaption
        { get { return UiText.Get("Выбранное аудиоустройство недоступно", "Selected audio output is unavailable"); } }

        // Runs on the UI thread after enumeration. The persisted WASAPI identity
        // is a user preference, including while a device is temporarily absent.
        private void ApplyAudioOutputSnapshot(AudioEndpoint[] endpoints, int ticket)
        {
            if (ticket != outputGeneration) return;
            endpoints = endpoints ?? new AudioEndpoint[0];
            bool wasUnavailable = !String.IsNullOrEmpty(selectedAudioId) && String.Equals(unavailableAudioId, selectedAudioId, StringComparison.Ordinal);
            bool missing;
            loadingOutputs = true;
            audioOutput.BeginUpdate();
            try
            {
                audioOutput.Items.Clear();
                audioOutput.Items.Add(new AudioEndpoint(ServerAudioBridge.AllOutputsId, AllAudioOutputsCaption));
                audioOutput.Items.Add(new AudioEndpoint(null, UiText.Get("Устройство Windows по умолчанию", "Windows default device")));
                int selected = selectedAudioId == ServerAudioBridge.AllOutputsId ? 0 : 1;
                foreach (AudioEndpoint endpoint in endpoints)
                {
                    if (endpoint == null || String.IsNullOrEmpty(endpoint.Id)) continue;
                    int index = audioOutput.Items.Add(endpoint);
                    if (String.Equals(endpoint.Id, selectedAudioId, StringComparison.Ordinal)) selected = index;
                }
                missing = selected == 1 && !String.IsNullOrEmpty(selectedAudioId);
                unavailableAudioId = missing ? selectedAudioId : null;
                if (missing) selected = audioOutput.Items.Add(new AudioEndpoint(selectedAudioId, UnavailableAudioOutputCaption));
                audioOutput.SelectedIndex = selected;
                DiagnosticLog.Write("server.audio.selection_resolved", "requested", selectedAudioId ?? "default_render",
                    "index", selected, "missing", missing, "selected_name", audioOutput.Text, "generation", ticket);
            }
            finally { audioOutput.EndUpdate(); loadingOutputs = false; }
            // A transient enumeration failure need not interrupt an already
            // running route. On recovery, reopen exactly the requested device.
            if (!missing && wasUnavailable && ready) StartReverseAudio();
        }

        private void RefreshUnavailableAudioOutputCaption()
        {
            if (unavailableAudioId == null) return;
            int selected = audioOutput.SelectedIndex;
            for (int index = 0; index < audioOutput.Items.Count; index++)
            {
                AudioEndpoint endpoint = audioOutput.Items[index] as AudioEndpoint;
                if (endpoint != null && String.Equals(endpoint.Id, unavailableAudioId, StringComparison.Ordinal))
                    audioOutput.Items[index] = new AudioEndpoint(endpoint.Id, UnavailableAudioOutputCaption);
            }
            if (selected >= 0 && selected < audioOutput.Items.Count) audioOutput.SelectedIndex = selected;
        }
    }
}
