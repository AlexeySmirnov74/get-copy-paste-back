using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

namespace HorizonClient
{
    internal sealed class StableLabel : Label
    { internal StableLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); } }

    internal sealed class ServerClipboardServices
    {
        internal delegate byte[] Encoder(ClipboardSnapshot snapshot, out string description);
        internal Func<uint> Sequence;
        internal Func<ClipboardSnapshot> Capture;
        internal Action Accepted;
        internal Action<Action> SchedulePreparation = delegate(Action action) { ThreadPool.QueueUserWorkItem(delegate { action(); }); };
        internal Encoder Encode = ClipboardPayload.Encode;
    }

    internal abstract class BridgeForm : Form
    {
        protected readonly Label StateLabel = new StableLabel();
        protected readonly Label DetailLabel = new StableLabel();
        protected readonly PictureBox Preview = new PictureBox();
        protected volatile bool IsClosing;
        protected volatile bool PreviewEnabled;
        protected volatile bool PreviewRefreshPending;

        protected BridgeForm(string title)
        {
            Text = title;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(900, 720);
            MinimumSize = new Size(900, 750);
            StartPosition = FormStartPosition.CenterScreen;
        }

        protected void Ui(Action action)
        {
            if (IsClosing || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsClosing && !IsDisposed) action(); }); }
            catch (InvalidOperationException) { }
        }

        protected void ShowPreview(Bitmap bitmap)
        {
            if (IsClosing || !PreviewEnabled) return;
            PreviewRefreshPending = false;
            Bitmap copy = new Bitmap(bitmap);
            if (!IsHandleCreated) { copy.Dispose(); return; }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsClosing || !PreviewEnabled) { copy.Dispose(); return; }
                    Image old = Preview.Image;
                    Preview.Image = copy;
                    if (old != null) old.Dispose();
                });
            }
            catch (InvalidOperationException) { copy.Dispose(); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            IsClosing = true;
            if (Preview.Image != null) { Preview.Image.Dispose(); Preview.Image = null; }
            base.OnFormClosed(e);
        }
    }

    internal sealed partial class ServerForm : BridgeForm
    {
        private const int ClipboardUpdate = 0x031D;
        private readonly ServerRuntime runtime;
        private readonly string legacyKey;
        private readonly bool loadSavedSettings;
        private readonly Func<string, UvcCamera> cameraFactory;
        private readonly ServerClipboardServices clipboardServices;
        private readonly Action<string, string> showWarning;
        private readonly Action delivered;
        private readonly Action<string> audioProbeNotification;
        private static string ReconnectHorizonInstructions { get { return UiText.Get("Обязательно закройте окно удалённого рабочего стола Horizon и подключитесь заново, чтобы применились настройки камеры.\r\n\r\nЗакрыть только наш Client.exe в трее недостаточно.", "Close the entire Horizon remote desktop window and reconnect to apply the camera settings.\r\n\r\nClosing only our Client.exe tray application is not enough."); } }
        private static object ReconnectHorizonValue { get { return UiText.KnownValue(ReconnectHorizonInstructions); } }
        private readonly object stateLock = new object();
        private readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        private Thread worker;
        private ManualResetEvent stopping;
        private byte[] current;
        private Guid transferId;
        private const VideoProfile DefaultProfile = VideoProfile.HighDefinitionRobust;
        private VideoProfile currentProfile = DefaultProfile;
        private VideoProfile selectedProfile = DefaultProfile;
        private string lastFingerprint;
        private int generation;
        private int currentGeneration;
        private readonly object preparationGate = new object();
        private ClipboardPreparation pendingPreparation, preparedClipboard;
        private bool preparationRunning, preparationUiQueued;
        private sealed class ClipboardPreparation
        {
            internal ClipboardSnapshot Snapshot;
            internal int Generation;
            internal uint Sequence;
            internal byte[] Encoded;
            internal string Description, Fingerprint;
            internal Exception Error;
        }
        private readonly ForwardDeliveryState forwardDelivery = new ForwardDeliveryState();
        private uint pendingCopySequence;
        private bool pendingCopyFeedback;
        private uint observedClipboardSequence;
        private bool clipboardBaselineSet, clipboardCaptureRequested;
        private int clipboardRetries;
        private bool ready;
        private bool listenerInstalled;
        private bool preparing;
        private bool restoringSettings;
        private bool cameraLookupKnown;
        private bool cameraExists;
        private bool cameraRunning;
        private bool cameraRequiresUpgrade;
        private bool cameraUpgradeAttempted;
        private bool cameraCleanupAvailable;
        private VideoModeOption activeVideoMode;
        private string ServerDataDirectory { get { return loadSavedSettings ? ServerPaths.Current.DataDirectory : Path.Combine(runtime.RootDirectory, "data"); } }
        private Guid bridgeSession;
        private Guid cameraFormatRevision = Guid.NewGuid();
        private int additionalAudioRetries = 2;
        private string activeCameraName;
        private Guid acknowledgedAudio;
        private ServerAudioBridge reverseAudio;
        private FileBenchmarkRunner speedTest;
        private Guid benchmarkDataTest;
        private long benchmarkDataFrames;
        private string selectedAudioId = ServerAudioBridge.AllOutputsId;
        private bool loadingOutputs;
        private int outputGeneration;

        public ServerForm() : this(null) { }

        public ServerForm(string key) : this(key, true) { }

        internal ServerForm(string key, bool loadSettings, Func<string, UvcCamera> cameraFactory = null, ServerClipboardServices clipboardServices = null, Action<string, string> showWarning = null, Action delivered = null, Action<string> audioProbeNotification = null) : base(UiText.Format("Get Copy-Paste Back — Server {0} — локальный ПК", "Get Copy-Paste Back — Server {0} — local PC", typeof(ServerForm).Assembly.GetName().Version.ToString()))
        {
            runtime = new ServerRuntime();
            legacyKey = key;
            loadSavedSettings = loadSettings;
            this.delivered = delivered ?? TransferSounds.Delivered;
            this.audioProbeNotification = audioProbeNotification ?? (loadSettings ? new Action<string>(ShowAudioProbeNotice) : delegate(string text) { });
            this.showWarning = showWarning ?? (loadSettings
                ? new Action<string, string>(delegate(string title, string text) { MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning); })
                : delegate(string title, string text) { });
            this.cameraFactory = cameraFactory ?? delegate(string keyFile) { return new UvcCamera("127.0.0.1", 13261, keyFile); };
            this.clipboardServices = clipboardServices ?? new ServerClipboardServices { Sequence = GetClipboardSequenceNumber, Capture = ClipboardPayload.Capture, Accepted = TransferSounds.Accepted };
            if (this.clipboardServices.Sequence == null || this.clipboardServices.Capture == null || this.clipboardServices.Accepted == null) throw new ArgumentException("Clipboard services must be complete.");
            InitializeServerLayout();
            InitializeCameraAccess();
            InitializeUiDiagnostics();
            InitializeBenchmarkEvents();
            InitializeTransferLog();
            InitializeServerTray();
            InitializeLanguageSelection();
            detailedLog.Checked = DiagnosticLog.IsEnabled;
            bool updatingDetailedLog = false;
            detailedLog.CheckedChanged += delegate {
                if (updatingDetailedLog) return;
                try { DiagnosticSettings.Apply(ServerDataDirectory, true, detailedLog.Checked, true, ServerDataDirectory); }
                catch (Exception error) { UiText.SetText(DetailLabel, "Не удалось изменить системный лог: {0}", "Could not change diagnostic logging: {0}", error.Message); }
                finally { updatingDetailedLog = true; detailedLog.Checked = DiagnosticLog.IsEnabled; updatingDetailedLog = false; }
            };
            applyMode.Click += delegate { ApplyVideoMode(); };
            InitializeColorModeEvents();
            keepCameraRunning.CheckedChanged += delegate {
                if (restoringSettings) return;
                LogUiAction(keepCameraRunning.Name, "changed", "enabled", keepCameraRunning.Checked);
                SaveSettings();
            };
            acceptedSoundEnabled.CheckedChanged += delegate {
                if (restoringSettings) return;
                LogUiAction(acceptedSoundEnabled.Name, "changed", "enabled", acceptedSoundEnabled.Checked);
                SaveSettings();
            };
            deliveredSoundEnabled.CheckedChanged += delegate {
                if (restoringSettings) return;
                LogUiAction(deliveredSoundEnabled.Name, "changed", "enabled", deliveredSoundEnabled.Checked);
                SaveSettings();
            };
            benchmark.Click += delegate { StartBenchmark(); };
            audioOutput.SelectedIndexChanged += delegate
            {
                if (loadingOutputs) return;
                AudioEndpoint endpoint = audioOutput.SelectedItem as AudioEndpoint;
                selectedAudioId = endpoint == null ? null : endpoint.Id;
                DiagnosticLog.Write("server.audio.selection_changed", "endpoint", selectedAudioId ?? "default_render",
                    "name", endpoint == null ? null : endpoint.Name, "ready", ready, "ui_action", lastUiAction);
                SaveSettings();
                if (ready) StartReverseAudio();
            };
            audioOutput.DropDown += delegate { LoadOutputs(); };
            createCamera.Click += delegate { CreateCamera(); };
            deleteCamera.Click += delegate { DeleteCamera(); };
            cameraName.DropDownClosed += delegate { cameraName.Select(0, 0); };
            cameraName.Leave += delegate { cameraName.Select(0, 0); if (!preparing) SaveSettings(); };
            profiles.SelectedIndexChanged += delegate
            {
                selectedProfile = SelectedProfileChoice;
                if (restoringSettings) return;
                ApplySelectedTransferProfile();
                SaveSettings();
                ScheduleClipboard();
            };
            debounce.Interval = 180;
            debounce.Tick += delegate { debounce.Stop(); CaptureClipboard(); };
            Shown += delegate
            {
                if (loadSavedSettings)
                {
                    restoringSettings = true;
                    try
                    {
                        LoadSettings();
                        VideoProfile savedProfile = selectedProfile;
                        PopulateProfileChoices(savedProfile);
                        selectedProfile = SelectedProfileChoice;
                    }
                    finally { restoringSettings = false; }
                }
                LoadOutputs();
                // Construction and hidden UI checks never query or create a camera.
                DiscoverCamera();
            };
        }

        private void DiscoverCamera()
        {
            if (preparing || IsClosing || serverExitRequested) return;
            preparing = true;
            cameraLookupKnown = false;
            SetConfigurationEnabled(false);
            UiText.SetText(StateLabel, "Проверка существующей камеры…", "Checking the existing camera…");
            UiText.SetText(DetailLabel, "Поиск камеры сервера без изменения устройств Windows.", "Looking for the server camera without changing Windows devices.");
            Thread previousWorker = worker;
            ManualResetEvent previousStop = stopping;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (previousWorker != null && !previousWorker.Join(35000))
                        throw new IOException(UiText.Get("Предыдущая передача ещё завершается. Повторите проверку через несколько секунд.", "The previous transfer is still finishing. Try checking again in a few seconds."));
                    if (IsClosing) return;
                    ServerConnection connection = runtime.FindExistingCamera();
                    Ui(delegate
                    {
                        RetireProducer(previousWorker, previousStop);
                        preparing = false;
                        RememberOwnedCamera(connection);
                        if (ContinuePendingServerExit()) return;
                        ApplyCameraPresence(connection != null, connection != null && connection.ProcessId > 0, connection == null ? null : connection.CameraName, connection == null, connection != null && connection.RequiresUpgrade);
                        if (connection != null && connection.MetadataRecoveryRequired)
                        {
                            UiText.SetText(StateLabel, "Камера сохранена.", "The camera is saved.");
                            UiText.SetText(DetailLabel, "Можно запустить эту камеру или удалить её и создать новую.", "You can start this camera, or delete it and create a new one.");
                        }
                        if (ShouldUpgradeActiveCamera(connection, loadSavedSettings, cameraUpgradeAttempted))
                        {
                            cameraUpgradeAttempted = true;
                            DiagnosticLog.Write("server.camera.upgrade.automatic", "reason", "verified_owned_backend_version_changed",
                                "camera_name", connection.CameraName, "preserve_identity", true);
                            ChangeCamera(false, connection.CameraName);
                            return;
                        }
                        if (connection != null) ConnectExisting(connection);
                    });
                }
                catch (Exception error)
                {
                    bool canDeleteSavedCamera = runtime.HasSavedCameraMetadata();
                    Ui(delegate
                    {
                        if (previousWorker == null || !previousWorker.IsAlive) RetireProducer(previousWorker, previousStop);
                        preparing = false;
                        if (ContinuePendingServerExit()) return;
                        cameraLookupKnown = false;
                        cameraCleanupAvailable = canDeleteSavedCamera;
                        SetConfigurationEnabled(true);
                        ShowFailure(error);
                    });
                }
            });
        }

        // Updates the view only. Hidden UI checks can exercise both device states
        // without showing a form, touching the clipboard, or opening a camera.
        internal static bool ShouldUpgradeActiveCamera(ServerConnection connection, bool loadSettings, bool attempted)
        {
            return loadSettings && !attempted && connection != null && connection.ProcessId > 0 &&
                connection.RequiresUpgrade && !connection.MetadataRecoveryRequired;
        }

        internal void ApplyCameraPresence(bool exists, string actualName, bool chooseNewName)
        {
            ApplyCameraPresence(exists, exists, actualName, chooseNewName);
        }

        internal void ApplyCameraPresence(bool exists, bool running, string actualName, bool chooseNewName, bool requiresUpgrade = false)
        {
            cameraCleanupAvailable = false;
            cameraLookupKnown = true;
            cameraExists = exists;
            cameraRunning = exists && running;
            cameraRequiresUpgrade = exists && requiresUpgrade;
            if (exists)
            {
                SetCameraName(actualName);
                if (cameraRequiresUpgrade) UiText.SetText(StateLabel, "Камера сохранена. Нажмите «Обновить камеру».", "The camera is saved. Click Update camera.");
                else if (cameraRunning) UiText.SetText(StateLabel, "Найдена камера «{0}».", "Found camera: {0}.", cameraName.Text);
                else UiText.SetText(StateLabel, "Камера сохранена. Нажмите «Запустить камеру».", "The camera is saved. Click Start camera.");
                UiText.SetText(DetailLabel, "В Horizon выберите «{0}». Для нового устройства сначала удалите эту камеру.", "Select {0} in Horizon. To create a new device, delete this camera first.", cameraName.Text);
            }
            else
            {
                if (chooseNewName) SetCameraName(CameraNames.PickRandom());
                UiText.SetText(StateLabel, "Укажите название и нажмите «Создать камеру».", "Enter a name and click Create camera.");
                UiText.SetText(DetailLabel, "Необходимые драйверы будут установлены автоматически. Windows может запросить права администратора.", "Required drivers will be installed automatically. Windows may request administrator permission.");
            }
            SetConfigurationEnabled(!preparing);
        }

        private void ConnectExisting(ServerConnection connection)
        {
            RememberOwnedCamera(connection);
            if (ContinuePendingServerExit()) return;
            if (connection.ProcessId <= 0 || connection.RequiresUpgrade) return;
            try
            {
                if (!ServerRuntime.IsSupportedVideoMode(connection.Width, connection.Height, connection.FPS, connection.Uncompressed, connection.H264Probe) || !connection.Uncompressed || connection.H264Probe)
                {
                    DiagnosticLog.Write("server.camera.mode_normalized", "previous_width", connection.Width, "previous_height", connection.Height,
                        "width", 1920, "height", 1080, "fps", 30, "raw", true, "reason", "unsupported_or_compressed_camera_mode");
                    ChangeCamera(false, null, new VideoModeOption(1920, 1080, 30, true, false));
                    return;
                }
                cameraFormatRevision = Guid.NewGuid();
                activeVideoMode = new VideoModeOption(connection.Width, connection.Height, connection.FPS, connection.Uncompressed, connection.H264Probe);
                RefreshTransferProfileChoices();
                SetConfigurationEnabled(true);
                for (int i = 0; i < cameraMode.Items.Count; i++)
                {
                    VideoModeOption option = (VideoModeOption)cameraMode.Items[i];
                    if (option.Width == connection.Width && option.Height == connection.Height && option.Fps == connection.FPS && option.Raw == connection.Uncompressed && option.Probe == connection.H264Probe) cameraMode.SelectedIndex = i;
                }
                UiText.SetText(DetailLabel, "В Horizon выберите «{0}». Источник: {1}×{2}, {3} кадров/с.", "Select {0} in Horizon. Source: {1}×{2}, {3} FPS.", cameraName.Text, connection.Width, connection.Height, connection.FPS);
                StartTransmission(String.IsNullOrEmpty(connection.KeyPath) ? legacyKey : connection.KeyPath);
                serverTabs.SelectedTab = workTab;
            }
            catch (Exception error) { ShowFailure(error); }
        }

        private void CreateCamera()
        {
            if (preparing || IsClosing || serverExitRequested || IsBenchmarkBusy) return;
            if (!cameraLookupKnown) { DiscoverCamera(); return; }
            if (cameraExists && cameraRunning && !cameraRequiresUpgrade) return;
            string name = cameraName.Text.Trim();
            if (String.IsNullOrEmpty(name) || name.Length > 100)
            {
                UiText.SetText(StateLabel, "Введите название камеры: от 1 до 100 символов.", "Enter a camera name of 1 to 100 characters.");
                return;
            }
            foreach (char character in name)
                if (Char.IsControl(character))
                {
                    UiText.SetText(StateLabel, "Название камеры не должно содержать управляющие символы.", "The camera name must not contain control characters.");
                    return;
                }
            SetCameraName(name);
            SaveSettings();
            ChangeCamera(false, name, cameraExists ? null : cameraMode.SelectedItem as VideoModeOption, !cameraExists);
        }

        private void DeleteCamera()
        {
            if (preparing || IsClosing || serverExitRequested || IsBenchmarkBusy || (!cameraExists && !cameraCleanupAvailable)) return;
            ChangeCamera(true, null);
        }

        private void ApplyVideoMode()
        {
            if (preparing || IsClosing || serverExitRequested || IsBenchmarkBusy || !cameraExists) return;
            VideoModeOption option = cameraMode.SelectedItem as VideoModeOption;
            if (option == null) return;
            if (ready && cameraRunning && !cameraRequiresUpgrade && option.Matches(activeVideoMode))
            {
                DiagnosticLog.Write("server.camera.format.already_active", "format", option.ToString(), "ui_action", lastUiAction,
                    "session", bridgeSession, "restart", false);
                ShowFormatReconnectWarning(option, true);
                return;
            }
            ChangeCamera(false, null, option);
        }

        private void ChangeCamera(bool remove, string name, VideoModeOption mode = null, bool initialSetup = false)
        {
            if (serverExitRequested || IsClosing) return;
            if (!remove) serverMayHaveStartedCamera = true;
            DiagnosticLog.Write("server.camera.change_requested", "ui_action", lastUiAction, "remove", remove,
                "format", mode == null ? cameraMode.Text : mode.ToString(), "ready", ready, "preparing", preparing, "initial_setup", initialSetup);
            // Stop only our frame producer here. The elevated helper repairs
            // the known driver conflict and checks SCM before changing USB.
            pendingCopyFeedback = false;
            preparing = true;
            CancelForwardJournal(UiText.Get("изменение камеры", "camera changed"));
            SetConfigurationEnabled(false);
            ready = false;
            InvalidateClipboardPreparation();
            debounce.Stop();
            lock (stateLock) { current = null; lastFingerprint = null; speedTest = null; acknowledgedAudio = Guid.Empty; }
            if (listenerInstalled) { RemoveClipboardFormatListener(Handle); listenerInstalled = false; }
            if (reverseAudio != null)
            {
                if (reverseAudio.IsReceiving) AppendTransferLog(UiText.Get("Приём от удалённого доступа прерван изменением камеры.", "Receiving from the remote desktop was interrupted by a camera change."));
                reverseAudio.Dispose(); reverseAudio = null;
            }
            ManualResetEvent previousStop = stopping;
            Thread previousWorker = worker;
            if (previousStop != null) previousStop.Set();
            if (remove) {
                UiText.SetText(StateLabel, "Удаление камеры…", "Deleting the camera…");
                UiText.SetText(DetailLabel, "Подтвердите удаление камеры в запросе Windows.", "Confirm camera deletion in the Windows prompt.");
            } else {
                UiText.SetText(StateLabel, "Подготовка камеры…", "Preparing the camera…");
                UiText.SetText(DetailLabel, "Проверка компонентов и драйверов.", "Checking components and drivers.");
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (previousWorker != null && !previousWorker.Join(35000))
                        throw new IOException(UiText.Get("Предыдущая передача ещё завершается. Повторите создание камеры через несколько секунд.", "The previous transfer is still finishing. Try creating the camera again in a few seconds."));
                    if (IsClosing) return;
                    Action<string> progress = delegate(string message)
                    {
                        DiagnosticLog.Write("server.camera.progress", "remove", remove, "message", message);
                        Ui(delegate { UiText.SetKnownText(DetailLabel, message); });
                    };
                    ServerConnection connection;
                    if (remove) { runtime.RemoveCamera(progress); connection = null; }
                    else if (initialSetup && mode != null) connection = runtime.PrepareCamera(name, mode.Width, mode.Height, mode.Fps, mode.Raw, mode.Probe, progress);
                    else if (mode != null) connection = runtime.ChangeVideoMode(mode.Width, mode.Height, mode.Fps, mode.Raw, mode.Probe, progress);
                    else connection = runtime.PrepareCamera(name, progress);
                    DiagnosticLog.Write("server.camera.change_completed", "remove", remove, "camera_exists", connection != null,
                        "camera_running", connection != null && connection.ProcessId > 0,
                        "width", connection == null ? 0 : connection.Width, "height", connection == null ? 0 : connection.Height);
                    Ui(delegate
                    {
                        RetireProducer(previousWorker, previousStop);
                        preparing = false;
                        RememberOwnedCamera(connection);
                        if (ContinuePendingServerExit()) return;
                        ApplyCameraPresence(connection != null, connection != null && connection.ProcessId > 0, connection == null ? null : connection.CameraName, remove, connection != null && connection.RequiresUpgrade);
                        if (connection != null) ConnectExisting(connection);
                        else
                        {
                            activeVideoMode = null;
                            RefreshTransferProfileChoices();
                            Image old = Preview.Image;
                            Preview.Image = null;
                            if (old != null) old.Dispose();
                        }
                        SaveSettings();
                        if (initialSetup && ready)
                        {
                            UiText.SetText(DetailLabel, "Камера создана. Выберите «{0}» в Horizon и в меню камеры нашего Client.exe. После пересоздания выбор старой камеры нужно заменить.", "Camera created. Select {0} in Horizon and in our Client.exe camera menu. After recreating a camera, replace the old camera selection.", connection.CameraName);
                            DiagnosticLog.Write("server.camera.new_selection_required", "camera_name", connection.CameraName, "reason", "new_device_identity");
                        }
                        if (connection != null && mode != null && !initialSetup && ready)
                            ShowFormatReconnectWarning(activeVideoMode, false);
                    });
                }
                catch (Exception error)
                {
                    DiagnosticLog.Error("server.camera.change_failed", error, "remove", remove);
                    ServerConnection remaining = null;
                    bool found = false;
                    try { if (!IsClosing) { remaining = runtime.FindExistingCamera(); found = true; } }
                    catch { }
                    bool canDeleteSavedCamera = !found && runtime.HasSavedCameraMetadata();
                    Ui(delegate
                    {
                        bool stopped = previousWorker == null || !previousWorker.IsAlive;
                        if (stopped) RetireProducer(previousWorker, previousStop);
                        preparing = false;
                        if (found) RememberOwnedCamera(remaining);
                        if (ContinuePendingServerExit()) return;
                        if (found)
                        {
                            ApplyCameraPresence(remaining != null, remaining != null && remaining.ProcessId > 0, remaining == null ? null : remaining.CameraName, remove && remaining == null, remaining != null && remaining.RequiresUpgrade);
                            if (remaining != null && stopped) ConnectExisting(remaining);
                        }
                        else { cameraLookupKnown = false; cameraCleanupAvailable = canDeleteSavedCamera; SetConfigurationEnabled(true); }
                        if (ready)
                        {
                            UiText.SetText(StateLabel, "Камера остаётся подключённой.", "The camera remains connected.");
                            ShowDiagnostics(error);
                        }
                        else ShowFailure(error);
                    });
                }
            });
        }

        private void RetireProducer(Thread previousWorker, ManualResetEvent previousStop)
        {
            if (previousStop != null) previousStop.Dispose();
            if (Object.ReferenceEquals(worker, previousWorker)) worker = null;
            if (Object.ReferenceEquals(stopping, previousStop)) stopping = null;
        }

        private void ShowFormatReconnectWarning(VideoModeOption mode, bool alreadyActive)
        {
            object source = mode == null ? (object)String.Empty : UiText.KnownValue(UiText.Format("{0}×{1}, {2} кадров/с", "{0}×{1}, {2} FPS", mode.Width, mode.Height, mode.Fps));
            WarnUser(alreadyActive ? "camera_format_already_active" : "camera_format_applied",
                alreadyActive ? "На сервере уже установлен формат камеры: {0}.\r\n\r\n{1}" : "Формат камеры на сервере применён: {0}.\r\n\r\n{1}",
                alreadyActive ? "The server camera format is already set to: {0}.\r\n\r\n{1}" : "Server camera format applied: {0}.\r\n\r\n{1}",
                source, ReconnectHorizonValue);
        }

        private void WarnUser(string reason, string text, string english = null, params object[] arguments)
        {
            DiagnosticLog.Write("server.user.warning", "reason", reason, "session", bridgeSession, "ui_action", lastUiAction);
            string message = english == null ? text : UiText.Format(text, english, arguments);
            UiText.SetText(DetailLabel, "{0}", "{0}", new SingleLineUiValue(UiText.KnownValue(message)));
            showWarning(UiText.Get("Get Copy-Paste Back — Server — проверка камеры", "Get Copy-Paste Back — Server — camera check"), message);
        }

        private sealed class SingleLineUiValue
        {
            private readonly object value;
            internal SingleLineUiValue(object value) { this.value = value; }
            public override string ToString() { return (value == null ? String.Empty : value.ToString()).Replace("\r\n", " "); }
        }

        private void RefreshCreateCameraCaption()
        {
            if (!cameraLookupKnown) UiText.SetText(createCamera, "Повторить проверку", "Check again");
            else if (cameraRequiresUpgrade) UiText.SetText(createCamera, "Обновить камеру", "Update camera");
            else if (cameraExists && !cameraRunning) UiText.SetText(createCamera, "Запустить камеру", "Start camera");
            else UiText.SetText(createCamera, "Создать камеру", "Create camera");
        }

        private void RefreshLanguagePresentation()
        {
            UiText.SetText(this, "Get Copy-Paste Back — Server {0} — локальный ПК", "Get Copy-Paste Back — Server {0} — local PC", typeof(ServerForm).Assembly.GetName().Version.ToString());
            RefreshCreateCameraCaption();
            RefreshBenchmarkLanguage();
            RefreshServerTrayLanguage();
            RefreshAudioProbeLanguage();
            serverTabs.Invalidate();
        }

        private void SetConfigurationEnabled(bool enabled)
        {
            keepCameraRunning.Enabled = enabled;
            cameraName.Enabled = enabled && cameraLookupKnown && !cameraExists;
            RefreshCreateCameraCaption();
            createCamera.Enabled = enabled && (!cameraLookupKnown || !cameraExists || !cameraRunning || cameraRequiresUpgrade);
            deleteCamera.Enabled = enabled && (cameraExists || cameraCleanupAvailable);
            profiles.Enabled = enabled;
            cameraMode.Enabled = enabled;
            applyMode.Enabled = enabled && cameraLookupKnown && cameraExists;
            benchmark.Enabled = enabled && ready;
            UpdateBenchmarkAvailability(enabled);
            if (!cameraName.Enabled && cameraName.IsHandleCreated) cameraName.Select(0, 0);
        }

        private void ShowFailure(Exception error)
        {
            DiagnosticLog.Error("server.camera.failure", error);
            ready = false;
            benchmark.Enabled = false;
            UpdateCameraAccess();
            if (error is DriverRestartRequiredException)
                UiText.SetText(StateLabel, "Драйвер установлен. Требуется перезагрузка Windows.", "The driver is installed. Windows must be restarted.");
            else UiText.SetText(StateLabel, "Камера не запущена.", "The camera is not running.");
            ShowDiagnostics(error);
        }

        private void ShowDiagnostics(Exception error)
        {
            DiagnosticLog.Error("server.operation.failed", error);
            if (error is DriverRestartRequiredException)
                UiText.SetText(DetailLabel, "Перезагрузите Windows и снова нажмите «Создать камеру».", "Restart Windows, then click Create camera again.");
            else if (error is CameraDriverRestartRequiredException)
                UiText.SetText(DetailLabel, "Драйвер подготовлен. Перезагрузите Windows, затем создайте камеру.", "The driver is prepared. Restart Windows, then create the camera.");
            else if (error is CameraDriverConflictException)
                UiText.SetText(DetailLabel, "Для подключения камеры нужно устранить конфликт USB-драйверов Windows.", "Resolve the Windows USB driver conflict to connect the camera.");
            else if (cameraExists || cameraCleanupAvailable)
                UiText.SetText(DetailLabel, "Камера недоступна. Можно удалить её и создать новую.", "The camera is unavailable. You can delete it and create a new one.");
            else UiText.SetText(DetailLabel, "Не удалось подготовить камеру. Подробности сохранены в журнале.", "Could not prepare the camera. Details were saved to the log.");
        }

        private void StartTransmission(string key)
        {
            if (String.IsNullOrWhiteSpace(key) || !File.Exists(key))
                throw new FileNotFoundException(UiText.Get("Не найден ключ подключения к камере. Создайте камеру заново.", "The camera connection key was not found. Recreate the camera."), key);
            if (!AddClipboardFormatListener(Handle))
                throw new IOException(UiText.Get("Windows не разрешила отслеживать буфер обмена.", "Windows did not allow clipboard monitoring."));
            listenerInstalled = true;
            ResetClipboardObservation("transmission_started");
            ready = true;
            benchmark.Enabled = HasCreatedCamera;
            bridgeSession = Guid.NewGuid();
            activeCameraName = cameraName.Text.Trim();
            DiagnosticLog.Write("server.transmission.start", "session", bridgeSession, "cameraName", activeCameraName,
                "profile", (int)selectedProfile);
            lock (stateLock) { speedTest = null; acknowledgedAudio = Guid.Empty; }
            StartReverseAudio();
            UiText.SetText(StateLabel, "Подключение к локальной камере…", "Connecting to the local camera…");
            UpdateBenchmarkAvailability(true);
            ManualResetEvent stop = new ManualResetEvent(false);
            stopping = stop;
            worker = new Thread(delegate() { SendLoop(key, stop); }) { IsBackground = true, Name = "Clipboard camera sender" };
            worker.Start();
        }

        private void LoadSettings()
        {
            try
            {
                string path = Path.Combine(ServerDataDirectory, "server.settings.xml");
                if (!File.Exists(path) || new FileInfo(path).Length > 16384) return;
                XmlDocument settings = new XmlDocument();
                settings.XmlResolver = null;
                using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) settings.Load(reader);
                XmlElement root = settings.DocumentElement;
                if (root == null || root.Name != "HorizonServer") return;
                LoadColorModeSettings(root);
                // The running camera owns its name. An absent camera always receives a
                // fresh catalog suggestion, even if an older name remains in this file.
                int profile;
                bool replacedProfile = false;
                if (Int32.TryParse(root.GetAttribute("videoProfile"), out profile))
                {
                    replacedProfile = !IsAvailableTransferProfile((VideoProfile)profile);
                    selectedProfile = replacedProfile ? DefaultProfile : (VideoProfile)profile;
                }
                LoadBenchmarkSettings(root);
                if (replacedProfile)
                {
                    DiagnosticLog.Write("server.settings.profile_normalized", "previous_profile", profile, "profile", selectedProfile, "reason", "profile_removed");
                }
                string output = root.GetAttribute("audioOutput");
                if (root.HasAttribute("audioOutput") && output.Length < 1024) selectedAudioId = output.Length == 0 ? null : output;
                int retries;
                if (Int32.TryParse(root.GetAttribute("audioRetries"), out retries) && retries >= 0 && retries <= 10)
                { additionalAudioRetries = retries; audioRetries.Value = retries; }
                bool journalEnabled;
                if (Boolean.TryParse(root.GetAttribute("transferLogEnabled"), out journalEnabled)) transferLogEnabled.Checked = journalEnabled;
                bool keepCamera;
                if (Boolean.TryParse(root.GetAttribute("keepCameraRunning"), out keepCamera)) keepCameraRunning.Checked = keepCamera;
                bool sound;
                if (Boolean.TryParse(root.GetAttribute("acceptedSoundEnabled"), out sound)) acceptedSoundEnabled.Checked = sound;
                if (Boolean.TryParse(root.GetAttribute("deliveredSoundEnabled"), out sound)) deliveredSoundEnabled.Checked = sound;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (XmlException) { }
        }

        private void SaveSettings()
        {
            if (!loadSavedSettings) return;
            string temporary = null;
            try
            {
                string data = ServerDataDirectory;
                Directory.CreateDirectory(data);
                string path = Path.Combine(data, "server.settings.xml");
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (XmlWriter writer = XmlWriter.Create(temporary, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                {
                    writer.WriteStartElement("HorizonServer");
                    writer.WriteAttributeString("cameraName", cameraName.Text.Trim());
                    writer.WriteAttributeString("videoProfile", ((int)selectedProfile).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("colorModesEnabled", colorModesEnabled.Checked.ToString());
                    WriteBenchmarkSettings(writer);
                    writer.WriteAttributeString("audioRetries", additionalAudioRetries.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("transferLogEnabled", transferLogEnabled.Checked.ToString());
                    writer.WriteAttributeString("keepCameraRunning", keepCameraRunning.Checked.ToString());
                    writer.WriteAttributeString("acceptedSoundEnabled", acceptedSoundEnabled.Checked.ToString());
                    writer.WriteAttributeString("deliveredSoundEnabled", deliveredSoundEnabled.Checked.ToString());
                    writer.WriteAttributeString("audioOutput", selectedAudioId ?? "");
                    writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                temporary = null;
            }
            catch (IOException error) { UiText.SetText(DetailLabel, "Не удалось сохранить настройки: {0}", "Could not save settings: {0}", error.Message); }
            catch (UnauthorizedAccessException error) { UiText.SetText(DetailLabel, "Не удалось сохранить настройки: {0}", "Could not save settings: {0}", error.Message); }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ClipboardUpdate) HandleClipboardUpdate();
            base.WndProc(ref m);
        }

        private void ResetClipboardObservation(string reason)
        {
            observedClipboardSequence = clipboardServices.Sequence(); clipboardBaselineSet = true;
            clipboardCaptureRequested = pendingCopyFeedback = false;
            InvalidateClipboardPreparation(); debounce.Stop();
            lock (stateLock) { current = null; lastFingerprint = null; }
            DiagnosticLog.Write("server.clipboard.baseline", "reason", reason, "sequence", observedClipboardSequence, "contents_read", false);
        }

        private void HandleClipboardUpdate()
        {
            uint sequence = clipboardServices.Sequence();
            if (!clipboardBaselineSet) { observedClipboardSequence = sequence; clipboardBaselineSet = true; return; }
            if (sequence == observedClipboardSequence) return;
            observedClipboardSequence = sequence;
            if (!ready || IsClosing || IsBenchmarkBusy)
            {
                DiagnosticLog.Write("server.clipboard.update.ignored", "sequence", sequence,
                    "reason", IsClosing ? "closing" : !ready ? "not_ready" : "benchmark_active");
                clipboardCaptureRequested = pendingCopyFeedback = false; return;
            }
            pendingCopySequence = sequence; clipboardCaptureRequested = pendingCopyFeedback = true;
            DiagnosticLog.Write("server.clipboard.update.observed", "sequence", sequence, "new_copy", true);
            ScheduleClipboard();
        }

        private void ApplySelectedTransferProfile()
        {
            lock (stateLock)
            {
                if (!ready || current == null || currentGeneration != generation || currentProfile == selectedProfile) return;
                uint sequence = clipboardServices.Sequence();
                if (sequence != pendingCopySequence) return;
                currentProfile = selectedProfile; transferId = Guid.NewGuid();
                forwardDelivery.Expect(bridgeSession, transferId, current.Length, currentGeneration, sequence);
                CancelForwardJournal(UiText.Get("изменён режим передачи", "transfer mode changed")); BeginForwardJournal(transferId, current.Length);
                UiText.SetText(StateLabel, "Режим передачи изменён. Передаются ранее скопированные данные: {0:N0} байт.", "Transfer mode changed. Sending the previously copied data: {0:N0} bytes.", current.Length);
                UiText.SetText(DetailLabel, "Дождитесь сигнала подтверждения клиента, затем нажмите Ctrl+V.", "Wait for the client's delivery confirmation sound, then press Ctrl+V.");
            }
        }

        private void ScheduleClipboard()
        {
            if (!ready || IsClosing || IsBenchmarkBusy || !clipboardCaptureRequested) return;
            InvalidateClipboardPreparation();
            clipboardRetries = 0;
            debounce.Stop();
            debounce.Start();
        }

        private void CaptureClipboard()
        {
            if (!ready || IsClosing || IsBenchmarkBusy || !clipboardCaptureRequested) return;
            uint sourceSequence = clipboardServices.Sequence();
            if (sourceSequence != pendingCopySequence) return;
            int ticket = generation;
            try
            {
                ClipboardSnapshot snapshot = clipboardServices.Capture();
                // Capture can invoke another application's delayed renderer.
                // Never send its result if a newer clipboard replaced it.
                if (ticket != generation || clipboardServices.Sequence() != sourceSequence) return;
                clipboardCaptureRequested = false;
                if (snapshot == null)
                {
                    pendingCopyFeedback = false;
                    DiagnosticLog.Write("server.clipboard.unsupported");
                    lock (stateLock) { current = null; lastFingerprint = null; }
                    UiText.SetText(StateLabel, "В буфере нет поддерживаемых данных. Ожидание Ctrl+C.", "The clipboard contains no supported data. Waiting for Ctrl+C.");
                    return;
                }
                if (pendingCopyFeedback && pendingCopySequence == sourceSequence && clipboardServices.Sequence() == sourceSequence)
                {
                    pendingCopyFeedback = false;
                    if (acceptedSoundEnabled.Checked) clipboardServices.Accepted();
                    DiagnosticLog.Write("server.clipboard.copy.accepted", "sequence", sourceSequence, "generation", ticket,
                        "direction", "server_to_client", "trigger", "local_clipboard_notification", "copy_start_sound_enabled", acceptedSoundEnabled.Checked);
                }
                UiText.SetText(StateLabel, "Подготовка данных буфера…", "Preparing clipboard data…");
                QueueClipboardPreparation(new ClipboardPreparation { Snapshot = snapshot, Generation = ticket, Sequence = sourceSequence });
            }
            catch (ExternalException error)
            {
                DiagnosticLog.Error("server.clipboard.capture.busy", error, "retry", clipboardRetries + 1);
                if (++clipboardRetries <= 8) debounce.Start();
                else
                {
                    pendingCopyFeedback = false;
                    clipboardCaptureRequested = false;
                    lock (stateLock) { current = null; lastFingerprint = null; }
                    UiText.SetText(StateLabel, "Буфер занят другим приложением: {0}", "Another application is using the clipboard: {0}", error.Message);
                }
            }
            catch (Exception error)
            {
                pendingCopyFeedback = false;
                clipboardCaptureRequested = false;
                DiagnosticLog.Error("server.clipboard.capture.error", error);
                lock (stateLock) { current = null; lastFingerprint = null; }
                UiText.SetText(StateLabel, "Ошибка буфера: {0}", "Clipboard error: {0}", error.Message);
            }
        }

        private void InvalidateClipboardPreparation()
        {
            lock (preparationGate)
            {
                Interlocked.Increment(ref generation);
                pendingPreparation = preparedClipboard = null;
            }
        }

        private bool IsPreparationCurrent(ClipboardPreparation work)
        {
            return work.Generation == Volatile.Read(ref generation) && Volatile.Read(ref ready) && !IsClosing && !IsDisposed;
        }

        private void QueueClipboardPreparation(ClipboardPreparation work)
        {
            lock (preparationGate)
            {
                if (!IsPreparationCurrent(work)) return;
                pendingPreparation = work;
                if (preparationRunning) return;
                preparationRunning = true;
            }
            try { clipboardServices.SchedulePreparation(PrepareClipboardLoop); }
            catch
            {
                lock (preparationGate) { preparationRunning = false; pendingPreparation = null; }
                throw;
            }
        }

        private void PrepareClipboardLoop()
        {
            while (true)
            {
                ClipboardPreparation work;
                lock (preparationGate)
                {
                    work = pendingPreparation; pendingPreparation = null;
                    if (work == null) { preparationRunning = false; return; }
                }
                bool queueUi = false;
                try
                {
                    // File traversal/compression cannot safely be interrupted. Only
                    // the one already-running encode may finish after supersession.
                    if (!IsPreparationCurrent(work) || clipboardServices.Sequence() != work.Sequence) continue;
                    work.Encoded = clipboardServices.Encode(work.Snapshot, out work.Description);
                    if (!IsPreparationCurrent(work) || clipboardServices.Sequence() != work.Sequence) continue;
                    using (SHA256 hash = SHA256.Create()) work.Fingerprint = Convert.ToBase64String(hash.ComputeHash(work.Encoded));
                }
                catch (Exception error)
                {
                    DiagnosticLog.Error("server.clipboard.encode.error", error);
                    work.Error = error;
                }
                finally { work.Snapshot = null; }
                lock (preparationGate)
                {
                    if (!IsPreparationCurrent(work)) continue;
                    // A single replaceable result slot prevents a stalled STA
                    // from accumulating encoded buffers in BeginInvoke closures.
                    preparedClipboard = work;
                    if (!preparationUiQueued) { preparationUiQueued = true; queueUi = true; }
                }
                if (queueUi) Ui(PublishPreparedClipboard);
            }
        }

        private void PublishPreparedClipboard()
        {
            ClipboardPreparation work;
            lock (preparationGate)
            {
                work = preparedClipboard; preparedClipboard = null; preparationUiQueued = false;
            }
            if (work == null || !IsPreparationCurrent(work) || IsBenchmarkBusy || clipboardServices.Sequence() != work.Sequence) return;
            if (work.Error != null)
            {
                lock (stateLock) { current = null; lastFingerprint = null; }
                UiText.SetText(StateLabel, "Данные не отправлены: {0}", "Data was not sent: {0}", work.Error.Message);
                return;
            }
            byte[] encoded = work.Encoded;
            VideoProfile profile = selectedProfile;
            lock (stateLock)
            {
                if (work.Fingerprint != lastFingerprint || current == null || currentGeneration != work.Generation)
                {
                    current = encoded;
                    transferId = Guid.NewGuid();
                    currentProfile = profile;
                    lastFingerprint = work.Fingerprint;
                    currentGeneration = work.Generation;
                    forwardDelivery.Expect(bridgeSession, transferId, encoded.Length, work.Generation, work.Sequence);
                    BeginForwardJournal(transferId, encoded.Length);
                    DiagnosticLog.Write("server.clipboard.prepared", "session", bridgeSession, "transfer", transferId,
                        "profile", (int)profile, "bytes", encoded.Length, "chunkBytes", VisualProtocol.GetChunkSize(profile));
                }
            }
            int chunkSize = VisualProtocol.GetChunkSize(profile);
            int blocks = (encoded.Length + chunkSize - 1) / chunkSize;
            UiText.SetText(StateLabel, "{0}: {1:N0} байт, {2} блоков. Передача повторяется.", "{0}: {1:N0} bytes, {2} blocks. Transmission repeats.", UiText.KnownValue(work.Description), encoded.Length, blocks);
            double blockRate = profile == VideoProfile.Compatible ? 5 : 30 * VisualProtocol.GetPacketsPerFrame(profile);
            UiText.SetText(DetailLabel, "Один круг ≈ {0} с без потерь. Дождитесь сигнала подтверждения клиента, затем нажмите Ctrl+V в удалённой Windows.", "One pass takes about {0}s without losses. Wait for the client's delivery confirmation sound, then press Ctrl+V in the remote Windows session.", Math.Max(1, (int)Math.Ceiling(blocks / blockRate)));
        }

        private void SendLoop(string keyFile, ManualResetEvent stop)
        {
            Guid sending = Guid.Empty;
            int index = 0, repeats = 0, roundOffset = 0, roundPosition = 0;
            Bitmap frame = null;
            bool idleFrameIsControl = false;
            Guid idleAcknowledgement = Guid.Empty, seenAcknowledgement = Guid.Empty;
            int urgentAcknowledgementFrames = 0;
            UvcCamera camera = null;
            bool connectionReported = false;
            DateTime lastPreview = DateTime.MinValue;
            Stopwatch pacing = Stopwatch.StartNew();
            double nextFrameMilliseconds = 0;
            int videoClock = 0;
            int targetFps = 30;
            var benchmarkSource = new FileBenchmarkVideoSource();
            var trace = new VideoSendTrace(bridgeSession, loadSavedSettings ? CameraTraceRoot(keyFile) : null);
            Stopwatch rendering = new Stopwatch();
            DiagnosticLog.Write("server.video.worker.start", "session", bridgeSession);
            bool timerPrecision = timeBeginPeriod(1) == 0;
            try
            {
                while (!stop.WaitOne(0))
                {
                    try
                    {
                        if (camera == null)
                        {
                            DiagnosticLog.Write("server.video.backend.connect", "host", "127.0.0.1", "port", 13261);
                            camera = cameraFactory(keyFile);
                            trace = new VideoSendTrace(bridgeSession, loadSavedSettings ? CameraTraceRoot(keyFile) : null);
                            DiagnosticLog.Write("server.video.backend.connected", "width", camera.Width, "height", camera.Height,
                                "fps", camera.FramesPerSecond, "uncompressed", camera.IsUncompressed, "h264Probe", camera.IsH264Probe);
                            targetFps = camera.FramesPerSecond;
                            if (frame != null) { frame.Dispose(); frame = null; } sending = Guid.Empty;
                            seenAcknowledgement = Guid.Empty; urgentAcknowledgementFrames = 0;
                            connectionReported = false;
                        }
                        FileBenchmarkRunner originalTest;
                        lock (stateLock) { originalTest = speedTest; }
                        bool ordinaryIdle = originalTest == null || originalTest.TailComplete;
                        FileBenchmarkRunner test = originalTest;
                        if (test != null && test.TailComplete) test = null;
                        if (!connectionReported)
                        {
                            Ui(delegate { if (ready && current == null) UiText.SetText(StateLabel, "Камера подключена. Ожидание Ctrl+C.", "Camera connected. Waiting for Ctrl+C."); });
                            connectionReported = true;
                        }
                        // Select the wire slot before advancing a clipboard
                        // cursor. Control frames must not consume unseen data.
                        int announcement = (videoClock++ & Int32.MaxValue) % 31;
                        FileBenchmarkCommand fileCommand = test == null ? null : test.CurrentCommand;
                        rendering.Restart();
                        byte[] bytes;
                        Guid id, acknowledged;
                        VideoProfile profile;
                        lock (stateLock) { bytes = test != null || currentGeneration != generation ? null : current; id = transferId; profile = currentProfile; acknowledged = acknowledgedAudio; }
                        if (acknowledged != seenAcknowledgement)
                        {
                            seenAcknowledgement = acknowledged;
                            urgentAcknowledgementFrames = acknowledged == Guid.Empty ? 0 : 4;
                            if (acknowledged != Guid.Empty) DiagnosticLog.Write("server.video.acknowledgement_pending", "session", bridgeSession, "transfer", acknowledged, "burst_frames", urgentAcknowledgementFrames);
                        }
                        // A completed reverse transfer must not wait for the
                        // next31-frame announcement group. Reserve these slots
                        // before advancing any forward clipboard packet cursor.
                        bool urgentAcknowledgement = urgentAcknowledgementFrames > 0 && fileCommand == null;
                        bool announce = urgentAcknowledgement || announcement < 4 || fileCommand != null && fileCommand.Phase == FileBenchmarkPhase.Prepare;
                        if (bytes == null)
                        {
                            if (frame == null || sending != Guid.Empty || idleFrameIsControl != ordinaryIdle || ordinaryIdle && idleAcknowledgement != acknowledged)
                            {
                                if (frame != null) frame.Dispose();
                                frame = ToCameraSize(CreateIdleFrame(ordinaryIdle, acknowledged), camera.Width, camera.Height); sending = Guid.Empty; idleFrameIsControl = ordinaryIdle; idleAcknowledgement = acknowledged; ShowPreview(frame);
                            }
                            else if (test == null && PreviewRefreshPending) ShowPreview(frame);
                        }
                        else
                        {
                            if (sending != id) { sending = id; index = 0; repeats = 0; roundOffset = 0; roundPosition = 0; }
                            if (repeats == 0 && !announce)
                            {
                                int chunkSize = VisualProtocol.GetChunkSize(profile);
                                int count = (bytes.Length + chunkSize - 1) / chunkSize;
                                if (frame != null) frame.Dispose();
                                frame = ToCameraSize(VisualProtocol.EncodePacketFrame(id, index, bytes, profile), camera.Width, camera.Height);
                                int used = Math.Min(VisualProtocol.GetPacketsPerFrame(profile), count - index);
                                roundPosition += used;
                                if (roundPosition >= count) { roundPosition = 0; roundOffset = (roundOffset + 1) % count; }
                                // Rotate rounds so 30-to-15 FPS conversion cannot always drop the same half.
                                index = (roundOffset + roundPosition) % count;
                                if ((DateTime.UtcNow - lastPreview).TotalMilliseconds >= 400) { lastPreview = DateTime.UtcNow; ShowPreview(frame); }
                            }
                            if (!announce) repeats = (repeats + 1) % (profile == VideoProfile.Compatible ? Math.Max(1, targetFps / 5) : 1);
                        }
                        // Four adjacent control frames, with a drifting phase, survive
                        // common 30-to-15/10 FPS conversion without a permanent alias.
                        if (fileCommand != null)
                        {
                            lock (stateLock)
                                if (benchmarkDataTest != fileCommand.Test) { benchmarkDataTest = fileCommand.Test; benchmarkDataFrames = 0; }
                        }
                        string frameKind;
                        double renderMs = rendering.Elapsed.TotalMilliseconds;
                        if (announce)
                        {
                            FileBenchmarkCommand wireCommand = fileCommand;
                            if (fileCommand != null && fileCommand.Phase == FileBenchmarkPhase.Start && announcement == 0)
                            {
                                // Repeating Prepare followed by Start lets an
                                // already connected older client join after a
                                // dropped initial announcement. It keeps the
                                // same test identity and never resets the source.
                                wireCommand = FileBenchmarkProtocol.Copy(fileCommand);
                                wireCommand.Phase = FileBenchmarkPhase.Prepare;
                            }
                            // Keep a legacy command in the announcement group;
                            // new receivers latch compact feedback for this test.
                            if (wireCommand != null && wireCommand.CompactReports && (announcement & 1) != 0)
                            {
                                wireCommand = FileBenchmarkProtocol.Copy(wireCommand);
                                wireCommand.CompactReports = false;
                            }
                            byte[] hello = urgentAcknowledgement ? BridgeControl.Acknowledge(bridgeSession, acknowledged) : announcement == 3 ? BridgeControl.CameraFormat(bridgeSession, cameraFormatRevision, camera.Width, camera.Height, camera.FramesPerSecond, Volatile.Read(ref additionalAudioRetries)) :
                                wireCommand != null ? FileBenchmarkProtocol.Command(wireCommand) : announcement >= 2 && acknowledged != Guid.Empty ? BridgeControl.Acknowledge(bridgeSession, acknowledged) : BridgeControl.Hello(bridgeSession, Guid.Empty, Guid.Empty, 0, activeCameraName, bytes == null ? Guid.Empty : id, bytes == null ? 0 : bytes.Length, (announcement & 1) == 0);
                            rendering.Restart();
                            using (Bitmap command = ToCameraSize(VisualProtocol.EncodeFrame(bridgeSession, 0, 1, hello.Length, hello), camera.Width, camera.Height))
                            {
                                renderMs = rendering.Elapsed.TotalMilliseconds;
                                camera.Send(command);
                                if (urgentAcknowledgement) urgentAcknowledgementFrames--;
                                if (fileCommand != null && fileCommand.Phase == FileBenchmarkPhase.Prepare && (DateTime.UtcNow - lastPreview).TotalMilliseconds >= 400)
                                {
                                    lastPreview = DateTime.UtcNow;
                                    ShowPreview(command);
                                }
                            }
                            frameKind = "control";
                        }
                        else if (test != null)
                        {
                            if (fileCommand.Phase == FileBenchmarkPhase.Start)
                            {
                                rendering.Restart();
                                using (Bitmap sample = ToCameraSize(benchmarkSource.Next(fileCommand, targetFps), camera.Width, camera.Height))
                                {
                                    renderMs = rendering.Elapsed.TotalMilliseconds;
                                    camera.Send(sample);
                                    lock (stateLock) benchmarkDataFrames++;
                                    if ((DateTime.UtcNow - lastPreview).TotalMilliseconds >= 400)
                                    {
                                        lastPreview = DateTime.UtcNow;
                                        ShowPreview(sample);
                                    }
                                }
                                frameKind = "file-data";
                            }
                            else { camera.Send(frame); frameKind = "idle"; }
                        }
                        else { camera.Send(frame); frameKind = bytes == null ? "idle" : "clipboard"; }
                        trace.Sent(camera, frameKind, profile, fileCommand, benchmarkSource, renderMs);
                    }
                    catch (Exception error)
                    {
                        DiagnosticLog.Error("server.video.send.error", error, "session", bridgeSession);
                        if (camera != null) { camera.Dispose(); camera = null; }
                        connectionReported = false;
                        if (!stop.WaitOne(0)) Ui(delegate { if (ready) UiText.SetText(StateLabel, "Ожидание камеры: {0}", "Waiting for the camera: {0}", error.Message); });
                        if (stop.WaitOne(1200)) break;
                        nextFrameMilliseconds = pacing.Elapsed.TotalMilliseconds;
                    }
                    nextFrameMilliseconds += 1000.0 / targetFps;
                    double nowMilliseconds = pacing.Elapsed.TotalMilliseconds;
                    if (nextFrameMilliseconds < nowMilliseconds - 250) nextFrameMilliseconds = nowMilliseconds;
                    stop.WaitOne(Math.Max(1, (int)Math.Ceiling(nextFrameMilliseconds - nowMilliseconds)));
                }
            }
            finally
            {
                DiagnosticLog.Write("server.video.worker.stop", "session", bridgeSession);
                if (frame != null) frame.Dispose();
                if (camera != null) camera.Dispose();
                if (timerPrecision) timeEndPeriod(1);
            }
        }

        private Bitmap IdleFrame(bool ordinaryIdle)
        {
            Guid acknowledged;
            lock (stateLock) acknowledged = acknowledgedAudio;
            return CreateIdleFrame(ordinaryIdle, acknowledged);
        }

        private Bitmap CreateIdleFrame(bool ordinaryIdle, Guid acknowledged)
        {
            if (ordinaryIdle)
            {
                // Retain the exact reverse ACK in cached idle frames so frame
                // conversion cannot repeatedly miss a single announcement slot.
                // Regular control slots still advertise Hello and camera format.
                // Benchmark tails retain their neutral picture for report retries.
                byte[] hello = acknowledged == Guid.Empty ? BridgeControl.Hello(bridgeSession, Guid.Empty, Guid.Empty, 0, activeCameraName) : BridgeControl.Acknowledge(bridgeSession, acknowledged);
                return VisualProtocol.EncodeFrame(bridgeSession, 0, 1, hello.Length, hello);
            }
            Bitmap bitmap = new Bitmap(640, 480);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font font = new Font("Segoe UI", 22))
            {
                graphics.Clear(Color.Black);
                graphics.DrawString(UiText.Get("Get Copy-Paste Back — Server\nОжидание данных", "Get Copy-Paste Back — Server\nWaiting for data"), font, Brushes.White, new RectangleF(35, 175, 570, 150));
            }
            return bitmap;
        }

        private static Bitmap ToCameraSize(Bitmap source, int width, int height)
        {
            if (source.Width == width && source.Height == height) return source;
            using (source)
            {
                Bitmap canvas = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    graphics.Clear(Color.Black);
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    double scale = Math.Min((double)width / source.Width, (double)height / source.Height);
                    int fittedWidth = Math.Max(1, (int)Math.Round(source.Width * scale)), fittedHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
                    graphics.DrawImage(source, new Rectangle((width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
                }
                return canvas;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (HideInsteadOfClosing(e)) return;
            if (serverExitRequested && !serverShutdownCompleted && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                ContinuePendingServerExit();
                return;
            }
            LogUiAction("Application", "closing", "reason", e.CloseReason);
            StopServerResources();
            base.OnFormClosing(e);
        }

        private void StopServerResources()
        {
            IsClosing = true;
            QuiesceServerResources();
        }

        private void QuiesceServerResources()
        {
            if (Interlocked.Exchange(ref serverResourcesStopped, 1) != 0) return;
            CloseAudioProbeNotice();
            ready = false;
            InvalidateClipboardPreparation();
            if (listenerInstalled) { RemoveClipboardFormatListener(Handle); listenerInstalled = false; }
            if (reverseAudio != null) { reverseAudio.Dispose(); reverseAudio = null; }
            debounce.Stop(); debounce.Dispose();
            benchmarkUiTimer.Stop(); benchmarkUiTimer.Dispose();
            if (speedTest != null) speedTest.Cancel();
            TransferSounds.Shutdown();
            if (stopping != null)
            {
                try { stopping.Set(); }
                catch (ObjectDisposedException) { }
            }
            if (worker != null && worker.IsAlive && Thread.CurrentThread != worker) worker.Join(300);
            lock (stateLock) { current = null; lastFingerprint = null; }
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

        private void SuppressEcho(Guid received)
        {
            CancelForwardJournal(UiText.Get("получен новый буфер от удалённого доступа", "new clipboard received from the remote desktop"));
            ResetClipboardObservation("reverse_clipboard_published");
            lock (stateLock) { acknowledgedAudio = received; }
        }

        private void LoadOutputs()
        {
            int ticket = Interlocked.Increment(ref outputGeneration);
            ThreadPool.QueueUserWorkItem(delegate
            {
                AudioEndpoint[] endpoints;
                try { endpoints = AudioCapture.EnumerateEndpoints(true); }
                catch (Exception error) { DiagnosticLog.Error("server.audio.enumerate_failed", error); endpoints = new AudioEndpoint[0]; }
                DiagnosticLog.Write("server.audio.enumerated", "count", endpoints.Length, "generation", ticket);
                foreach (AudioEndpoint endpoint in endpoints)
                    DiagnosticLog.Write("server.audio.output_candidate", "endpoint", endpoint.Id, "name", endpoint.Name);
                Ui(delegate
                {
                    ApplyAudioOutputSnapshot(endpoints, ticket);
                });
            });
        }

        private void StartReverseAudio()
        {
            DiagnosticLog.Write("server.audio.restart_requested", "session", bridgeSession, "endpoint", selectedAudioId ?? "default_render",
                "replacing_previous", reverseAudio != null, "benchmark_busy", IsBenchmarkBusy, "ui_action", lastUiAction);
            if (reverseAudio != null)
            {
                if (reverseAudio.IsReceiving) AppendTransferLog(UiText.Get("Приём от удалённого доступа перезапущен после изменения аудиовыхода.", "Receiving from the remote desktop was restarted after changing the audio output."));
                reverseAudio.Dispose();
            }
            reverseAudio = new ServerAudioBridge(this, bridgeSession, SuppressEcho,
                delegate(string text) { UiText.SetKnownText(audioStatus, text); }, OnSpeedReport);
            reverseAudio.FileReportReceived = OnFileBenchmarkReport;
            reverseAudio.CompactFileReportReceived = OnCompactFileBenchmarkReport;
            reverseAudio.AudioProbeReceived = OnAudioProbeReceived;
            reverseAudio.IncomingActivityReceived = OnIncomingTransferActivity;
            reverseAudio.DeliveryReceived = OnForwardDelivered;
            reverseAudio.FastDeliveryReceived = OnFastForwardDelivered;
            reverseAudio.AdditionalRetries = additionalAudioRetries;
            reverseAudio.IncomingStarted = OnIncomingTransferStarted;
            reverseAudio.IncomingCompleted = OnIncomingTransferCompleted;
            reverseAudio.IncomingFailed = OnIncomingTransferFailed;
            reverseAudio.IncomingRetrying = OnIncomingTransferRetrying;
            reverseAudio.IncomingResumed = OnIncomingTransferResumed;
            reverseAudio.SetBenchmarkMode(IsBenchmarkBusy);
            reverseAudio.Start(selectedAudioId);
        }

        private void OnForwardDelivered(DeliveryReceipt receipt)
        {
            if (!ready || IsClosing) return;
            lock (stateLock)
            {
                if (current == null || !forwardDelivery.Confirm(receipt, generation, clipboardServices.Sequence()))
                {
                    DiagnosticLog.Write("server.clipboard.delivery.ignored", "transfer", receipt == null ? Guid.Empty : receipt.Transfer);
                    return;
                }
                current = null; lastFingerprint = null;
            }
            CompleteForwardDelivery(receipt, false);
        }

        private void OnFastForwardDelivered(byte[] token)
        {
            if (!ready || IsClosing) return;
            DeliveryReceipt receipt;
            lock (stateLock)
            {
                if (current == null || !forwardDelivery.ConfirmFast(token, generation, clipboardServices.Sequence(), out receipt))
                {
                    DiagnosticLog.Write("server.clipboard.delivery.fast_ignored", "token_bytes", token == null ? 0 : token.Length);
                    return;
                }
                current = null; lastFingerprint = null;
            }
            CompleteForwardDelivery(receipt, true);
        }

        private void CompleteForwardDelivery(DeliveryReceipt receipt, bool fast)
        {
            try
            {
                CompleteForwardJournal(receipt.Transfer, receipt.EncodedBytes);
                UiText.SetText(StateLabel, "Клиент получил данные. Можно вставлять Ctrl+V.", "The client received the data. You can paste with Ctrl+V.");
                UiText.SetText(DetailLabel, "Получение и запись в буфер клиента подтверждены по звуку. Передача завершена.", "The audio receipt confirms that the client received and published the clipboard data. Transfer complete.");
                DiagnosticLog.Write("server.clipboard.delivery.confirmed", "session", receipt.Session, "transfer", receipt.Transfer, "encoded_bytes", receipt.EncodedBytes, "fast_receipt", fast,
                    "direction", "server_to_client", "remote_ready_sound_enabled", deliveredSoundEnabled.Checked);
            }
            finally { if (deliveredSoundEnabled.Checked) delivered(); }
        }

        private sealed class VideoModeOption
        {
            internal readonly int Width, Height, Fps;
            internal readonly bool Raw, Probe;
            internal VideoModeOption(int width, int height, int fps, bool raw, bool probe) { Width = width; Height = height; Fps = fps; Raw = raw; Probe = probe; }
            internal bool Matches(VideoModeOption other) { return other != null && Width == other.Width && Height == other.Height && Fps == other.Fps && Raw == other.Raw && Probe == other.Probe; }
            public override string ToString() { return Probe ? UiText.Get("SEI: проверка H.264 (эксперимент)", "SEI: H.264 probe (experimental)") : UiText.Format("{0}×{1}, {2} FPS, {3}", "{0}×{1}, {2} FPS, {3}", Width, Height, Fps, Raw ? UiText.Get("YUY2 без JPEG", "YUY2 without JPEG") : "MJPEG"); }
        }
    }
}
