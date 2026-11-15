using System;
using System.Diagnostics;
using System.Globalization;

namespace HorizonClient
{
    internal sealed partial class ServerForm
    {
        private Guid journalForwardTransfer;
        private long journalForwardStarted;
        private int journalForwardBytes;
        private Guid journalIncomingActivity;
        private long journalIncomingSent;
        private int journalIncomingPass;

        private void OnIncomingTransferActivity(AudioTransferStatus activity, int received)
        {
            bool first = journalIncomingActivity != activity.Transfer;
            if (!first && activity.PassIndex == journalIncomingPass && activity.SentBytes - journalIncomingSent < 32768 && activity.Phase == AudioTransferPhase.Sending) return;
            journalIncomingActivity = activity.Transfer; journalIncomingSent = activity.SentBytes; journalIncomingPass = activity.PassIndex;
            AppendTransferLog(UiText.Format(first
                ? "Клиент начал отправку: {0}; отправлено в этой попытке {1}, принято сервером {2}. Это ещё не подтверждение доставки."
                : "Клиент продолжает отправку: {0}; отправлено в этой попытке {1}, принято сервером {2}. Это ещё не подтверждение доставки.", first
                ? "The client started sending {0}; sent this attempt: {1}, received by the server: {2}. Delivery is not yet confirmed."
                : "The client is still sending {0}; sent this attempt: {1}, received by the server: {2}. Delivery is not yet confirmed.",
                FormatTransferBytes(activity.TotalBytes), FormatTransferBytes(activity.SentBytes), FormatTransferBytes(received)));
        }

        private void InitializeTransferLog()
        {
            clearTransferLog.Click += delegate { transferLogText.Clear(); LogUiAction(clearTransferLog.Name, "click"); };
            transferLogEnabled.CheckedChanged += delegate
            {
                if (restoringSettings) return;
                LogUiAction(transferLogEnabled.Name, "changed", "enabled", transferLogEnabled.Checked);
                if (loadSavedSettings) SaveSettings();
            };
            audioRetries.ValueChanged += delegate
            {
                additionalAudioRetries = (int)audioRetries.Value;
                if (reverseAudio != null) reverseAudio.AdditionalRetries = additionalAudioRetries;
                if (restoringSettings) return;
                LogUiAction(audioRetries.Name, "changed", "additional_retries", additionalAudioRetries);
                if (loadSavedSettings) SaveSettings();
            };
        }

        private void AppendTransferLog(string message)
        {
            if (IsClosing || !transferLogEnabled.Checked || String.IsNullOrEmpty(message)) return;
            // The human journal stays in memory. Bound its size independently
            // of the detailed asynchronous diagnostic log beside the EXE.
            if (transferLogText.TextLength > 180000)
            {
                string retained = transferLogText.Text;
                int firstLine = retained.IndexOf('\n', Math.Min(60000, retained.Length - 1));
                transferLogText.Text = firstLine < 0 ? String.Empty : retained.Substring(firstLine + 1);
            }
            transferLogText.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
        }

        private void BeginForwardJournal(Guid transfer, int bytes)
        {
            if (journalForwardTransfer != Guid.Empty && journalForwardTransfer != transfer)
                AppendTransferLog(UiText.Get("Предыдущая передача заменена новым копированием.", "The previous transfer was replaced by a new copy."));
            journalForwardTransfer = transfer; journalForwardBytes = bytes; journalForwardStarted = Stopwatch.GetTimestamp();
            AppendTransferLog(UiText.Format("Началась передача данных объёмом {0} на удалённый доступ.", "Started sending {0} to the remote desktop.", FormatTransferBytes(bytes)));
        }

        private void CompleteForwardJournal(Guid transfer, int bytes)
        {
            if (transfer != journalForwardTransfer || bytes != journalForwardBytes) return;
            long milliseconds = (long)((Stopwatch.GetTimestamp() - journalForwardStarted) * (1000.0 / Stopwatch.Frequency));
            journalForwardTransfer = Guid.Empty;
            AppendTransferLog(UiText.Format("Данные {0} переданы на удалённый доступ за {1} с. Можно нажать Ctrl+V.", "Sent {0} to the remote desktop in {1}s. You can press Ctrl+V.", FormatTransferBytes(bytes), FormatTransferSeconds(milliseconds)));
        }

        private void CancelForwardJournal(string reason)
        {
            if (journalForwardTransfer == Guid.Empty) return;
            journalForwardTransfer = Guid.Empty;
            AppendTransferLog(UiText.Format("Передача на удалённый доступ прервана: {0}.", "Transfer to the remote desktop interrupted: {0}.", reason));
        }

        private void OnIncomingTransferStarted(Guid transfer, int bytes)
        { AppendTransferLog(UiText.Format("Начался приём от удалённого доступа: {0}.", "Started receiving {0} from the remote desktop.", FormatTransferBytes(Math.Max(0, bytes - 24)))); }

        private void OnIncomingTransferCompleted(Guid transfer, int bytes, long milliseconds)
        {
            DiagnosticLog.Write("server.transfer_journal.incoming_completed", "transfer", transfer, "encoded_bytes", bytes,
                "elapsed_ms", milliseconds, "journal_enabled", transferLogEnabled.Checked, "closing", IsClosing, "direction", "client_to_server");
            try
            {
                AppendTransferLog(UiText.Format("От удалённого доступа поступили данные {0} за {1} с. Можно нажать Ctrl+V.", "Received {0} from the remote desktop in {1}s. You can press Ctrl+V.", FormatTransferBytes(Math.Max(0, bytes - 24)), FormatTransferSeconds(milliseconds)));
                DiagnosticLog.Write("server.transfer_journal.incoming_completed_recorded", "transfer", transfer,
                    "appended", !IsClosing && transferLogEnabled.Checked);
            }
            finally { delivered(); }
        }

        private void OnIncomingTransferFailed(Guid transfer, string reason)
        { AppendTransferLog(reason == "audio_inactive_partial"
            ? UiText.Get("Приём от удалённого доступа не завершён. Пока нет новых блоков; принятая часть сохранена, ожидаем продолжение.", "Receiving from the remote desktop is incomplete. No new blocks yet; received data is retained while waiting for the rest.")
            : UiText.Get("Приём от удалённого доступа не завершён. Данные не прошли проверку или буфер Windows недоступен.", "Receiving from the remote desktop is incomplete. Data verification failed or the Windows clipboard is unavailable.")); }

        private void OnIncomingTransferResumed(Guid transfer, int received, int total)
        { AppendTransferLog(UiText.Format("Приём от удалённого доступа продолжен: {0} из {1}.", "Receiving from the remote desktop resumed: {0} of {1}.", FormatTransferBytes(received), FormatTransferBytes(total))); }

        private void OnIncomingTransferRetrying(Guid transfer, int attempt, int maximum)
        { AppendTransferLog(UiText.Format("Повтор приёма от удалённого доступа: {0} из {1}.", "Retrying reception from the remote desktop: {0} of {1}.", attempt, maximum)); }

        internal static string FormatTransferBytes(long bytes)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException("bytes");
            if (bytes < 1024) return UiText.Format("{0} байт", "{0} bytes", bytes);
            return bytes < 1024 * 1024 ? UiText.Format("{0:0.#} КБ", "{0:0.#} KB", bytes / 1024.0) :
                UiText.Format("{0:0.#} МБ", "{0:0.#} MB", bytes / (1024.0 * 1024));
        }

        private static string FormatTransferSeconds(long milliseconds)
        { return (Math.Max(0, milliseconds) / 1000.0).ToString("0.#", UiText.Culture); }
    }
}
