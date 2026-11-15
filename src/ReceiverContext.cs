using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HorizonClient
{
    // The receiver owns a hidden dispatch handle and a notification icon, never a Form.
    internal sealed partial class ReceiverContext : ApplicationContext
    {
        private readonly ReceiverServices services;
        private readonly DispatchControl dispatch = new DispatchControl();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly NotifyIcon icon = new NotifyIcon();
        private readonly Icon trayImage;
        private readonly System.Windows.Forms.Timer publicationTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer retryTimer = new System.Windows.Forms.Timer();
        private readonly object cameraGate = new object();
        private readonly ConcurrentQueue<ReceiverSession> retired = new ConcurrentQueue<ReceiverSession>();
        private readonly string receivedRoot;
        private DeferredPaste deferredPaste;
        private readonly AudioClipboardSender audioSender;
        private ClientAudioBridge audioBridge;
        private string selectedAudioId, selectedAudioName, selectedAudioPersistentId;
        private bool invalidAudioSelection;
        private AudioModemKind selectedModem = AudioModemKind.Fsk;
        private volatile ReceiverSession current;
        private volatile bool closing;
        private string selectedCameraId;
        private CameraResolution selectedResolution;
        private CameraResolution[] selectedModes = new CameraResolution[0];
        private List<CameraDevice> lastMenuDevices = new List<CameraDevice>();
        private AudioEndpoint[] lastMenuOutputs = new AudioEndpoint[0];
        private bool enumerating;
        private bool menuRequested;
        private List<CameraDevice> pendingMenuDevices;
        private AudioEndpoint[] pendingMenuOutputs;
        private bool pendingMenuLanguage;
        private bool sessionEventsSubscribed;
        private BridgeCameraFormat sourceFormat;
        private string sourceFormatCamera;
        private readonly Queue<string> retiredSourceFormats = new Queue<string>();
        private long sourceRefreshAt = -1;
        private int sourceRefreshAttempts;
        private bool sourceRefreshRequired;
        private bool sourceRefreshDeferred;
        private long uiAction, stateLoggedAt;
        private long modemRequest, captureEpoch;
        private readonly object audioOutputGate = new object();
        private long audioOutputRequest;
        private string requestedAudioOutput;
        private bool audioOutputRequested;
        private string lastReceiverState;
        private readonly List<Action> enumerationCompletions = new List<Action>();
        private readonly Dictionary<string, CaptureRetryPolicy> captureRetries = new Dictionary<string, CaptureRetryPolicy>(StringComparer.Ordinal);

        private static ReceiverServices ProductionServices()
        {
            return new ReceiverServices {
                DecodeFrames = VisualProtocol.DecodePackets,
                DecodeFramesPreferred = VisualProtocol.DecodePackets,
                OpenCaptureAtResolution = delegate(string id, Action<Bitmap> callback, CameraResolution mode) { return new MediaFoundationCapture(id, callback, mode); },
                OpenCaptureBackendAtResolution = delegate(string id, Action<Bitmap> callback, CaptureBackend backend, CameraResolution mode) {
                    if (backend == CaptureBackend.MediaFoundation) return new MediaFoundationCapture(id, callback, mode);
                    if (backend == CaptureBackend.DirectShowHighest) return new ReceiverCapture(new CameraCapture(id, callback, mode.Width, mode.Height, CameraNativeFormatPolicy.HighestOffered));
                    throw new ArgumentException("This compatibility capture backend does not negotiate a native source preference.");
                },
                OpenCaptureBackend = delegate(string id, Action<Bitmap> callback, CaptureBackend backend) {
                    if (backend == CaptureBackend.MediaFoundation) return new MediaFoundationCapture(id, callback);
                    if (backend == CaptureBackend.MediaFoundationDefault) return new MediaFoundationCapture(id, callback, false);
                    if (backend == CaptureBackend.DirectShowHighest) return new ReceiverCapture(new CameraCapture(id, callback, 1920, 1080, CameraNativeFormatPolicy.HighestOffered));
                    return new ReceiverCapture(new CameraCapture(id, callback, 1280, 720, backend == CaptureBackend.DirectShow720));
                }
            };
        }

        internal ReceiverContext() : this(ProductionServices(), true) { }

        // The injected services keep verification independent of real cameras, the
        // user's clipboard, the keyboard hook and the notification area.
        internal ReceiverContext(ReceiverServices services) : this(services, false) { }

        private ReceiverContext(ReceiverServices services, bool enableAudio)
        {
            if (services == null) throw new ArgumentNullException("services");
            this.services = services;
            DiagnosticLog.Write("receiver.created", "audio_enabled", enableAudio);
            receivedRoot = services.ReceivedRoot;
            IntPtr unused = dispatch.Handle;
            AudioModemKind restoredModem = AudioModemKind.Fsk;
            if (enableAudio)
            {
                audioSender = new AudioClipboardSender(dispatch);
                restoredModem = AudioModemSelection.Read(services.Paths);
                dispatch.ClipboardChanged = delegate { audioSender.NotifyClipboardChanged(); };
            }
            if (HasAudioOutput && services.RestoreSelection)
            {
                try
                {
                    string[] selection = services.ReadAudioSelection();
                    if (selection != null && (selection.Length == 2 || selection.Length == 3) &&
                        AudioOutputSelection.IsPlaybackId(selection[0]) && !String.IsNullOrEmpty(selection[1]) &&
                        (selection.Length == 2 || !String.IsNullOrWhiteSpace(selection[2]) && !selection[2].StartsWith("waveout:", StringComparison.Ordinal)))
                    {
                        selectedAudioId = selection[0]; selectedAudioName = selection[1];
                        if (selection.Length == 3) selectedAudioPersistentId = selection[2];
                        DiagnosticLog.Write("receiver.audio.output.restored", "endpoint", selectedAudioId, "name", selectedAudioName, "persistent_endpoint", selectedAudioPersistentId);
                    }
                    else { invalidAudioSelection = true; DiagnosticLog.Write("receiver.setting.invalid", "setting", "audio.id", "reason", "expected_endpoint_and_name"); }
                }
                catch (Exception error)
                {
                    invalidAudioSelection = !(error is FileNotFoundException || error is DirectoryNotFoundException);
                    LogSettingFailure("read", "audio.id", error);
                }
            }
            if (audioSender != null && (invalidAudioSelection || !String.IsNullOrEmpty(selectedAudioName)))
            {
                // A camera may produce its hello before the background output
                // worker runs. Do not enable automatic routing in that gap.
                requestedAudioOutput = "waveout:65535";
                audioOutputRequested = true;
                audioSender.SetOutput(requestedAudioOutput);
            }
            trayImage = LoadTrayIcon();
            icon.Icon = trayImage;
            icon.Text = String.Empty;
            icon.MouseUp += OnIconMouseUp;
            menu.Opened += delegate { DiagnosticLog.Write("receiver.menu.opened", "items", menu.Items.Count, "action_id", uiAction); LogReceiverState(true); };
            menu.Closed += delegate(object sender, ToolStripDropDownClosedEventArgs args)
            {
                DiagnosticLog.Write("receiver.menu.closed", "reason", args.CloseReason, "pending_refresh", pendingMenuDevices != null, "action_id", uiAction);
                Ui(ApplyPendingDeviceMenus);
            };
            menu.ItemClicked += delegate(object sender, ToolStripItemClickedEventArgs args)
            { DiagnosticLog.Write("receiver.menu.item_clicked", "item", args.ClickedItem.Name, "action_id", ++uiAction); };
            publicationTimer.Interval = 300;
            publicationTimer.Tick += delegate { PublishPending(current); };
            retryTimer.Interval = 2000;
            retryTimer.Tick += delegate
            {
                LogReceiverState(false);
                PollCaptureRetry();
            };
            if (services.RestoreSelection)
            {
                try { selectedCameraId = services.ReadSelection(); DiagnosticLog.Write("receiver.camera.restored", "camera_id", selectedCameraId); }
                catch (Exception error) { LogSettingFailure("read", "camera.id", error); }
            }
            RestoreAutomaticCameraSelection();
            UpdateMenu(new List<CameraDevice>());
            if (services.EnableDeferredPaste)
            {
                try { deferredPaste = new DeferredPaste(dispatch.Handle, delegate { return IsReceiving; }, delegate { }); }
                catch { }
            }
            if (services.EnableSessionEvents)
            {
                try
                {
                    SystemEvents.SessionEnding += OnSessionEnding;
                    sessionEventsSubscribed = true;
                }
                catch { }
            }
            Application.ApplicationExit += OnApplicationExit;
            if (services.ShowTrayIcon) icon.Visible = true;
            if (services.EnableRetryTimer) retryTimer.Start();
            if (services.RestoreSelection && (automaticCameraSelection || !String.IsNullOrEmpty(selectedCameraId))) RefreshDevices(null);
            if (enableAudio && restoredModem != selectedModem) BeginAudioModemSelection(restoredModem, false, false);
        }

        private static Icon LoadTrayIcon()
        {
            using (Stream stream = typeof(ReceiverContext).Assembly.GetManifestResourceStream("HorizonClient.Tray.ico"))
            {
                if (stream == null) return (Icon)SystemIcons.Application.Clone();
                using (Icon embedded = new Icon(stream, SystemInformation.SmallIconSize))
                    return (Icon)embedded.Clone();
            }
        }

        internal ContextMenuStrip Menu { get { return menu; } }
        internal NotifyIcon TrayIcon { get { return icon; } }
        internal string SelectedCameraId { get { return selectedCameraId; } }
        internal bool IsReceiving
        {
            get
            {
                ReceiverSession session = current;
                return !closing && session != null && session.Receiving;
            }
        }

        private void OnIconMouseUp(object sender, MouseEventArgs args)
        {
            DiagnosticLog.Write("receiver.tray.clicked", "button", args.Button, "menu_visible", menu.Visible, "closing", closing, "action_id", ++uiAction);
            if (args.Button != MouseButtons.Right || closing || menu.Visible) return;
            menuRequested = true;
            RefreshDevices(null);
        }

        private static void LogSettingFailure(string operation, string setting, Exception error)
        {
            bool missing = error is FileNotFoundException || error is DirectoryNotFoundException;
            DiagnosticLog.Write("receiver.setting." + (missing ? "missing" : "failed"), "operation", operation, "setting", setting,
                "error_type", error.GetType().FullName, "hresult", error.HResult);
        }

        private void ObserveSubmenu(ToolStripMenuItem item, string stableId)
        {
            item.Name = stableId;
            item.DropDownOpened += delegate { DiagnosticLog.Write("receiver.menu.submenu_opened", "item", stableId, "action_id", uiAction); };
            item.DropDownClosed += delegate { DiagnosticLog.Write("receiver.menu.submenu_closed", "item", stableId, "action_id", uiAction); };
            item.DropDown.ItemClicked += delegate(object sender, ToolStripItemClickedEventArgs args)
            { DiagnosticLog.Write("receiver.menu.item_clicked", "item", args.ClickedItem.Name, "parent", stableId, "action_id", ++uiAction); };
        }

        private void LogReceiverState(bool force)
        {
            if (!DiagnosticLog.IsEnabled) return;
            long now = Stopwatch.GetTimestamp();
            ReceiverSession active = current;
            Guid bridgeSession = audioBridge == null ? Guid.Empty : audioBridge.SessionId;
            long lastFrame = active == null ? 0 : Interlocked.Read(ref active.LastFrameAt);
            double frameAge = lastFrame == 0 ? -1 : (now - lastFrame) * 1000.0 / Stopwatch.Frequency;
            string reason = String.IsNullOrEmpty(selectedCameraId) ? "camera_not_selected" :
                active == null ? "capture_not_active" : lastFrame == 0 ? "waiting_for_first_video_frame" :
                frameAge > 5000 ? "video_stalled" : bridgeSession == Guid.Empty ? "waiting_for_server_hello" : "video_control_active";
            if (!force && reason == lastReceiverState && now - stateLoggedAt < 5 * Stopwatch.Frequency) return;
            lastReceiverState = reason; stateLoggedAt = now;
            DiagnosticLog.Write("receiver.state", "reason", reason, "camera_id", selectedCameraId, "capture_present", active != null,
                "session", bridgeSession, "last_frame_age_ms", frameAge,
                "modem", AudioModemCatalog.GetId(selectedModem), "selected_output", selectedAudioId, "selected_output_name", selectedAudioName,
                "audio_available", audioSender != null);
        }

        // Each opening enumerates in the background. A missing or inaccessible
        // device produces no extra menu text, and no device is opened to enumerate it.
        internal void RefreshDevices(Action completion)
        {
            if (closing) return;
            if (completion != null) enumerationCompletions.Add(completion);
            if (enumerating) { DiagnosticLog.Write("receiver.refresh.coalesced", "menu_requested", menuRequested); return; }
            DiagnosticLog.Write("receiver.refresh.started", "menu_requested", menuRequested, "menu_visible", menu.Visible, "camera_id", selectedCameraId);
            enumerating = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<CameraDevice> devices;
                AudioEndpoint[] outputs = new AudioEndpoint[0];
                try { devices = services.ListDevices() ?? new List<CameraDevice>(); }
                catch (Exception error) { DiagnosticLog.Error("receiver.enumerate.failed", error); devices = new List<CameraDevice>(); }
                DiagnosticLog.Write("receiver.enumerated", "camera_count", devices.Count);
                foreach (CameraDevice listed in devices) if (listed != null) DiagnosticLog.Write("receiver.camera.available", "camera_name", listed.Name, "camera_id", listed.Id);
                if (HasAudioOutput)
                {
                    try { outputs = services.ListAudioOutputs() ?? new AudioEndpoint[0]; }
                    catch (Exception error) { DiagnosticLog.Error("receiver.audio.output.enumerate_failed", error); }
                    foreach (AudioEndpoint endpoint in outputs)
                        if (endpoint != null) DiagnosticLog.Write("receiver.audio.output.available", "endpoint", endpoint.Id, "name", endpoint.Name, "horizon", AudioOutputSelection.IsHorizonOutput(endpoint.Name));
                }
                Ui(delegate
                {
                    enumerating = false;
                    if (!DeviceMenuSnapshotMatches(devices, outputs) || !AudioOutputIdentitiesMatch(outputs)) UpdateDeviceMenus(devices, outputs);
                    bool selectedPresent = false;
                    string activeId = current == null ? selectedCameraId : current.DeviceId;
                    foreach (CameraDevice listed in devices)
                        if (listed != null && String.Equals(listed.Id, activeId, StringComparison.Ordinal)) { selectedPresent = true; break; }
                    if (!selectedPresent && current != null)
                    {
                        ReceiverSession missing = current;
                        DiagnosticLog.Write("receiver.capture.retired", "camera_id", missing.DeviceId, "reason", "selected_camera_missing");
                        current = null;
                        captureEpoch++;
                        StopAudioSession();
                        ResetPublication(missing);
                        QueueDispose(missing);
                    }
                    if (automaticCameraSelection) StartNextAutomaticCamera(devices);
                    else if (current == null && !String.IsNullOrEmpty(selectedCameraId))
                    {
                        foreach (CameraDevice device in devices)
                        {
                            if (device != null && String.Equals(device.Id, selectedCameraId, StringComparison.Ordinal))
                            {
                                StartCapture(device);
                                break;
                            }
                        }
                    }
                    Action[] completions = enumerationCompletions.ToArray();
                    enumerationCompletions.Clear();
                    foreach (Action done in completions)
                    {
                        try { done(); }
                        catch (Exception error) { DiagnosticLog.Write("receiver.refresh.completion_failed", "error_type", error.GetType().FullName, "hresult", error.HResult); }
                    }
                    LogReceiverState(false);
                    if (menuRequested)
                    {
                        menuRequested = false;
                        if (!menu.Visible && menu.Items.Count != 0)
                        {
                            try
                            {
                                bool foreground = SetForegroundWindow(dispatch.Handle);
                                DiagnosticLog.Write("receiver.menu.show_requested", "foreground_accepted", foreground, "items", menu.Items.Count);
                                menu.Show(Cursor.Position);
                            }
                            catch (Exception error) { DiagnosticLog.Error("receiver.menu.show_failed", error); }
                        }
                    }
                });
            });
        }

        private void UpdateDeviceMenus(List<CameraDevice> devices, AudioEndpoint[] outputs)
        {
            lastMenuDevices = new List<CameraDevice>(devices);
            lastMenuOutputs = (AudioEndpoint[])outputs.Clone();
            // Replacing ToolStrip items closes an active dropdown. A reconnect
            // refresh must not interrupt the user's camera or audio selection.
            if (menu.Visible)
            {
                pendingMenuDevices = devices;
                pendingMenuOutputs = outputs;
                DiagnosticLog.Write("receiver.menu.refresh_deferred", "reason", "menu_open", "camera_count", devices.Count, "output_count", outputs.Length);
                return;
            }
            pendingMenuDevices = null; pendingMenuOutputs = null;
            UpdateMenu(devices);
            if (HasAudioOutput) UpdateAudioMenu(outputs);
            DiagnosticLog.Write("receiver.menu.refresh_applied", "camera_count", devices.Count, "output_count", outputs.Length, "items", menu.Items.Count);
        }

        private void ApplyPendingDeviceMenus()
        {
            if (closing || menu.Visible) return;
            if (pendingMenuDevices != null) UpdateDeviceMenus(pendingMenuDevices, pendingMenuOutputs);
            if (pendingMenuLanguage) { pendingMenuLanguage = false; ApplyMenuLanguage(menu.Items); }
        }

        internal void SelectLanguage(UiLanguage language)
        {
            if (closing) return;
            UiText.SetLanguage(language);
            try { UiText.Save(services.Paths.LanguageSetting); }
            catch (Exception error) { LogSettingFailure("write", UiText.LanguageFileName, error); services.LogFailure(error); }
            // No device enumeration, capture restart or output restoration is
            // needed to change captions. Updating an open ToolStrip can dismiss
            // its submenu, so apply text/check changes after the menu closes.
            pendingMenuLanguage = true;
            Ui(ApplyPendingDeviceMenus);
        }

        private void ApplyMenuLanguage(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                switch (item.Name)
                {
                    case "video.resolution": item.Text = UiText.Get("Разрешение камеры", "Camera resolution"); break;
                    case "video.resolution.auto": item.Text = UiText.Get("Авто (формат сервера)", "Auto (server format)"); break;
                    case "video.refresh": item.Text = UiText.Get("Обновить режимы камеры", "Refresh camera modes"); break;
                    case "audio.modem": item.Text = UiText.Get("Аудиомодем", "Audio modem"); break;
                    case "audio.test": item.Text = UiText.Get("Проверить звук", "Test audio"); break;
                    case "audio.output": item.Text = UiText.Get("Аудиовыход", "Audio output"); break;
                    case "settings": item.Text = UiText.Get("Настройки", "Settings"); break;
                    case "settings.debug_log": item.Text = UiText.Get("Подробный системный лог", "Detailed system log"); break;
                    case "settings.auto_camera": item.Text = AutomaticCameraCaption; break;
                    case "application.version": item.Text = UiText.Format("Get Copy-Paste Back — Client {0}", "Get Copy-Paste Back — Client {0}", typeof(ReceiverContext).Assembly.GetName().Version.ToString()); break;
                    case "application.exit": item.Text = UiText.Get("Выход", "Exit"); break;
                }
                ToolStripMenuItem choice = item as ToolStripMenuItem;
                if (choice == null) continue;
                if (choice.Tag is UiLanguage) choice.Checked = (UiLanguage)choice.Tag == UiText.Language;
                else if (choice.Tag is AudioModemKind) choice.Text = AudioModemCatalog.GetCaption((AudioModemKind)choice.Tag);
                ApplyMenuLanguage(choice.DropDownItems);
            }
        }

        private void UpdateMenu(List<CameraDevice> devices)
        {
            while (menu.Items.Count != 0)
            {
                ToolStripItem old = menu.Items[0];
                menu.Items.RemoveAt(0);
                old.Dispose();
            }
            foreach (CameraDevice device in devices)
            {
                if (device == null || String.IsNullOrEmpty(device.Id)) continue;
                CameraDevice selectedDevice = device;
                // ToolStrip treats ampersands as mnemonics; doubling preserves the
                // camera's actual visible friendly name.
                ToolStripMenuItem item = new ToolStripMenuItem((device.Name ?? String.Empty).Replace("&", "&&")) { Name = "camera.select" };
                item.AutoToolTip = false;
                item.Tag = device;
                item.Checked = String.Equals(device.Id, selectedCameraId, StringComparison.Ordinal);
                item.Click += delegate { SelectDevice(selectedDevice); };
                menu.Items.Add(item);
            }
            if (menu.Items.Count != 0) menu.Items.Add(new ToolStripSeparator());
            if (!String.IsNullOrEmpty(selectedCameraId))
            {
                string catalogCamera = selectedCameraId;
                ReceiverSession catalogSession = current;
                ToolStripMenuItem resolutions = new ToolStripMenuItem(UiText.Get("Разрешение камеры", "Camera resolution")) { Name = "video.resolution", AutoToolTip = false };
                ToolStripMenuItem automatic = new ToolStripMenuItem(UiText.Get("Авто (формат сервера)", "Auto (server format)")) { Name = "video.resolution.auto", Checked = selectedResolution == null, AutoToolTip = false };
                automatic.Click += delegate { if (IsCatalogCurrent(catalogCamera, catalogSession)) SelectResolution(null); };
                resolutions.DropDownItems.Add(automatic);
                foreach (CameraResolution mode in selectedModes)
                {
                    CameraResolution chosen = mode;
                    ToolStripMenuItem option = new ToolStripMenuItem(mode.ToString()) { Name = "video.resolution." + mode.Width + "x" + mode.Height, Checked = mode.Matches(selectedResolution), AutoToolTip = false };
                    option.Click += delegate { if (IsCatalogCurrent(catalogCamera, catalogSession)) SelectResolution(chosen); };
                    resolutions.DropDownItems.Add(option);
                }
                ObserveSubmenu(resolutions, "video.resolution"); menu.Items.Add(resolutions);
                ToolStripMenuItem refresh = new ToolStripMenuItem(UiText.Get("Обновить режимы камеры", "Refresh camera modes")) { Name = "video.refresh", AutoToolTip = false };
                refresh.Click += delegate { if (String.Equals(catalogCamera, selectedCameraId, StringComparison.Ordinal)) RefreshCameraModes(); };
                menu.Items.Add(refresh); menu.Items.Add(new ToolStripSeparator());
            }
            if (HasAudioOutput)
            {
                ToolStripMenuItem modems = AudioModemSelection.BuildMenu(selectedModem, SelectAudioModem);
                ObserveSubmenu(modems, "audio.modem"); menu.Items.Add(modems);
                ToolStripMenuItem audioTest = new ToolStripMenuItem(UiText.Get("Проверить звук", "Test audio")) { Name = "audio.test", AutoToolTip = false };
                audioTest.Click += delegate
                {
                    DiagnosticLog.Write("receiver.audio.test.clicked", "modem", AudioModemCatalog.GetId(selectedModem), "endpoint", selectedAudioId, "name", selectedAudioName, "action_id", uiAction);
                    QueueAudioOutput(requestedAudioOutput, true, false);
                };
                menu.Items.Add(audioTest);
                menu.Items.Add(new ToolStripSeparator());
            }
            var settingsMenu = new ToolStripMenuItem(UiText.Get("Настройки", "Settings")) { Name = "settings", AutoToolTip = false };
            var debugItem = new ToolStripMenuItem(UiText.Get("Подробный системный лог", "Detailed system log")) { Name = "settings.debug_log", CheckOnClick = true, Checked = DiagnosticLog.IsEnabled, AutoToolTip = false };
            debugItem.Click += delegate {
                try { debugItem.Checked = DiagnosticSettings.Apply(services.Paths.SettingsDirectory, false, debugItem.Checked, true, services.Paths.BaseDirectory); }
                catch (Exception error) { debugItem.Checked = DiagnosticLog.IsEnabled; LogSettingFailure("write", "debug.enabled", error); }
            };
            settingsMenu.DropDownItems.Add(debugItem);
            AddAutomaticCameraMenu(settingsMenu);
            var languages = new ToolStripMenuItem("Язык / Language") { Name = "settings.language", AutoToolTip = false };
            foreach (UiLanguage language in new[] { UiLanguage.Russian, UiLanguage.English })
            {
                UiLanguage selectedLanguage = language;
                var option = new ToolStripMenuItem(language == UiLanguage.Russian ? "Русский" : "English") {
                    Name = "settings.language." + (language == UiLanguage.Russian ? "ru" : "en"), Tag = language,
                    Checked = UiText.Language == language, AutoToolTip = false
                };
                option.Click += delegate { SelectLanguage(selectedLanguage); };
                languages.DropDownItems.Add(option);
            }
            ObserveSubmenu(languages, "settings.language");
            settingsMenu.DropDownItems.Add(languages); menu.Items.Add(settingsMenu);
            Version version = typeof(ReceiverContext).Assembly.GetName().Version;
            menu.Items.Add(new ToolStripMenuItem(UiText.Format("Get Copy-Paste Back — Client {0}", "Get Copy-Paste Back — Client {0}", version.ToString())) { Name = "application.version", Enabled = false, AutoToolTip = false });
            ToolStripMenuItem exit = new ToolStripMenuItem(UiText.Get("Выход", "Exit"));
            exit.AutoToolTip = false;
            exit.Name = "application.exit";
            exit.Click += delegate { DiagnosticLog.Write("receiver.exit.clicked", "action_id", uiAction); ExitThread(); };
            menu.Items.Add(exit);
        }

        internal bool SelectAudioModem(AudioModemKind kind)
        {
            DiagnosticLog.Write("receiver.audio.modem.clicked", "previous", AudioModemCatalog.GetId(selectedModem), "requested", AudioModemCatalog.GetId(kind), "closing", closing, "audio_available", audioSender != null, "action_id", uiAction);
            return BeginAudioModemSelection(kind, true, true);
        }

        private bool BeginAudioModemSelection(AudioModemKind kind, bool persist, bool requireSameCapture)
        {
            AudioModemCatalog.GetId(kind);
            if (closing || audioSender == null && (services.PrepareAudioModem == null || services.ApplyAudioModem == null)) return false;
            long request = Interlocked.Increment(ref modemRequest), epoch = captureEpoch, action = uiAction;
            string camera = selectedCameraId;
            // Clicking the already active modem also cancels an older pending load.
            if (kind == selectedModem) { if (persist) PersistAudioModem(kind); return true; }
            DiagnosticLog.Write("receiver.audio.modem.preparing", "requested", AudioModemCatalog.GetId(kind), "request", request, "action_id", action, "restore", !persist);
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (closing || Interlocked.Read(ref modemRequest) != request) return;
                Exception failure = null;
                try
                {
                    if (services.PrepareAudioModem != null) services.PrepareAudioModem(kind);
                    else audioSender.PrepareModem(kind);
                }
                catch (Exception error) { failure = error; }
                Ui(delegate
                {
                    if (Interlocked.Read(ref modemRequest) != request || requireSameCapture &&
                        (captureEpoch != epoch || !String.Equals(camera, selectedCameraId, StringComparison.Ordinal)))
                    {
                        DiagnosticLog.Write("receiver.audio.modem.selection_obsolete", "requested", AudioModemCatalog.GetId(kind), "request", request,
                            "reason", "newer_selection_or_capture_changed");
                        return;
                    }
                    if (failure != null)
                    {
                        DiagnosticLog.Error("receiver.audio.modem.selection_failed", failure, "requested", AudioModemCatalog.GetId(kind), "request", request, "action_id", action);
                        services.LogFailure(failure); return;
                    }
                    try
                    {
                        if (services.ApplyAudioModem != null) services.ApplyAudioModem(kind);
                        else audioSender.ApplyPreparedModem(kind);
                    }
                    catch (Exception error)
                    {
                        DiagnosticLog.Error("receiver.audio.modem.selection_failed", error, "requested", AudioModemCatalog.GetId(kind), "request", request, "action_id", action);
                        services.LogFailure(error); return;
                    }
                    selectedModem = kind;
                    UpdateAudioModemChecks();
                    if (persist) PersistAudioModem(kind);
                    DiagnosticLog.Write("receiver.audio.modem.selected", "modem", AudioModemCatalog.GetId(kind), "request", request, "action_id", action, "restored", !persist);
                });
            });
            // The asynchronous commit updates checks in place after validation.
            return false;
        }

        private void PersistAudioModem(AudioModemKind kind)
        {
            try
            {
                if (services.WriteAudioModem != null) services.WriteAudioModem(kind);
                else AudioModemSelection.Write(services.Paths, kind);
                DiagnosticLog.Write("receiver.setting.written", "setting", "audio.modem");
            }
            catch (Exception error) { LogSettingFailure("write", "audio.modem", error); services.LogFailure(error); }
        }

        private void UpdateAudioModemChecks()
        {
            ToolStripMenuItem modems = menu.Items["audio.modem"] as ToolStripMenuItem;
            if (modems == null) return;
            foreach (ToolStripItem item in modems.DropDownItems)
            {
                ToolStripMenuItem choice = item as ToolStripMenuItem;
                if (choice != null && choice.Tag is AudioModemKind) choice.Checked = (AudioModemKind)choice.Tag == selectedModem;
            }
        }

        internal void SelectDevice(CameraDevice device)
        {
            if (closing || device == null || String.IsNullOrEmpty(device.Id)) return;
            SetAutomaticCameraSelection(false, true);
            string previousCamera = selectedCameraId;
            selectedCameraId = device.Id;
            DiagnosticLog.Write("receiver.camera.selected", "camera_name", device.Name, "camera_id", device.Id, "previous_camera_id", previousCamera, "action_id", uiAction);
            foreach (ToolStripItem listedItem in menu.Items)
            {
                ToolStripMenuItem item = listedItem as ToolStripMenuItem;
                if (item == null || !(item.Tag is CameraDevice)) continue;
                CameraDevice listed = item.Tag as CameraDevice;
                item.Checked = listed != null && String.Equals(listed.Id, selectedCameraId, StringComparison.Ordinal);
            }
            try { services.WriteSelection(selectedCameraId); DiagnosticLog.Write("receiver.setting.written", "setting", "camera.id"); }
            catch (Exception error) { LogSettingFailure("write", "camera.id", error); services.LogFailure(error); }
            if (String.Equals(previousCamera, device.Id, StringComparison.Ordinal)) RefreshCameraModes(device);
            else
            {
                selectedResolution = null; selectedModes = new CameraResolution[0];
                ClearSourceFormat();
                CapturePolicy(device.Id).ResetDelay();
                StartCapture(device);
                UpdateDeviceMenus(lastMenuDevices, lastMenuOutputs);
            }
        }

        internal void SelectResolution(CameraResolution resolution)
        {
            if (closing || String.IsNullOrEmpty(selectedCameraId)) return;
            if (resolution != null)
            {
                bool offered = false;
                foreach (CameraResolution mode in selectedModes) if (mode.Matches(resolution)) { offered = true; break; }
                if (!offered) { DiagnosticLog.Write("receiver.resolution.rejected", "camera_id", selectedCameraId, "width", resolution.Width, "height", resolution.Height, "reason", "not_advertised"); return; }
            }
            selectedResolution = resolution;
            DiagnosticLog.Write("receiver.resolution.selected", "camera_id", selectedCameraId, "mode", resolution == null ? "auto" : resolution.ToString());
            RefreshCameraModes();
        }

        private bool IsCatalogCurrent(string cameraId, ReceiverSession session)
        {
            bool valid = !closing && String.Equals(cameraId, selectedCameraId, StringComparison.Ordinal) && Object.ReferenceEquals(session, current);
            if (!valid) DiagnosticLog.Write("receiver.resolution.rejected", "camera_id", cameraId, "reason", "stale_capture_catalog");
            return valid;
        }

        internal void RefreshCameraModes()
        { RefreshCameraModes(null); }

        private void RefreshCameraModes(CameraDevice requestedDevice)
        {
            if (closing || String.IsNullOrEmpty(selectedCameraId)) return;
            CameraDevice device = requestedDevice;
            ReceiverSession active = current;
            if (active != null && String.Equals(active.DeviceId, selectedCameraId, StringComparison.Ordinal)) device = new CameraDevice(active.DeviceName, active.DeviceId);
            if (device == null) foreach (CameraDevice listed in lastMenuDevices)
                if (listed != null && String.Equals(listed.Id, selectedCameraId, StringComparison.Ordinal)) { device = listed; break; }
            if (device == null) { DiagnosticLog.Write("receiver.camera.refresh_skipped", "camera_id", selectedCameraId, "reason", "exact_device_not_listed"); return; }
            CaptureRetryPolicy policy = CapturePolicy(device.Id);
            policy.RefreshModes(selectedResolution != null && services.OpenCaptureBackendAtResolution == null);
            selectedModes = new CameraResolution[0];
            DiagnosticLog.Write("receiver.camera.refresh_requested", "camera_id", device.Id, "format_mode", policy.ModeName, "requested_resolution", selectedResolution == null ? "auto" : selectedResolution.ToString());
            StartCapture(device);
            UpdateDeviceMenus(lastMenuDevices, lastMenuOutputs);
        }

        private void ApplyModeCatalog(ReceiverSession session, CameraResolution[] modes, CameraResolution actual)
        {
            if (!IsCurrent(session)) return;
            if (actual != null) { Volatile.Write(ref session.ActualWidth, actual.Width); Volatile.Write(ref session.ActualHeight, actual.Height); }
            selectedModes = CameraResolution.Distinct(modes);
            if (session.RequestedResolution != null && !session.RequestedResolution.Matches(actual))
            {
                DiagnosticLog.Write("receiver.resolution.fallback", "camera_id", session.DeviceId, "requested", session.RequestedResolution.ToString(), "actual", actual == null ? null : actual.ToString(), "reason", "requested_mode_unavailable_or_rejected");
                selectedResolution = null;
            }
            DiagnosticLog.Write("receiver.modes.updated", "camera_id", session.DeviceId, "format_mode", session.CaptureMode, "count", selectedModes.Length, "actual", actual == null ? null : actual.ToString());
            foreach (CameraResolution mode in selectedModes) DiagnosticLog.Write("receiver.mode.available", "camera_id", session.DeviceId, "width", mode.Width, "height", mode.Height);
            UpdateDeviceMenus(lastMenuDevices, lastMenuOutputs);
        }

        private static string SourceFormatKey(BridgeCameraFormat format)
        { return format.Session.ToString("N") + ":" + format.Revision.ToString("N") + ":" + format.Width + "x" + format.Height + ":" + format.FPS; }

        private void ClearSourceFormat()
        {
            sourceFormat = null; sourceFormatCamera = null; retiredSourceFormats.Clear();
            sourceRefreshAt = -1; sourceRefreshAttempts = 0; sourceRefreshRequired = false;
            sourceRefreshDeferred = false;
        }

        // A CRC-checked control frame was received from this exact camera. Source
        // metadata changes trigger a bounded fresh-capability negotiation; opening
        // the tray or receiving the same announcement never restarts healthy video.
        private void HandleCameraFormat(ReceiverSession session, BridgeCameraFormat format)
        {
            if (!IsCurrent(session)) return;
            string key = SourceFormatKey(format);
            if (sourceFormat != null && String.Equals(sourceFormatCamera, session.DeviceId, StringComparison.Ordinal))
            {
                if (String.Equals(SourceFormatKey(sourceFormat), key, StringComparison.Ordinal))
                { if (audioBridge != null) audioBridge.ConfigureAudioRetries(format); return; }
                if (retiredSourceFormats.Contains(key))
                {
                    DiagnosticLog.Write("receiver.camera.source_format_ignored", "camera_id", session.DeviceId, "session", format.Session,
                        "revision", format.Revision, "reason", "retired_source_announcement");
                    return;
                }
                retiredSourceFormats.Enqueue(SourceFormatKey(sourceFormat));
                while (retiredSourceFormats.Count > 8) retiredSourceFormats.Dequeue();
            }
            else retiredSourceFormats.Clear();
            if (audioBridge != null) audioBridge.ConfigureAudioRetries(format);
            bool changed = sourceFormat != null && String.Equals(sourceFormatCamera, session.DeviceId, StringComparison.Ordinal);
            sourceFormat = format; sourceFormatCamera = session.DeviceId;
            sourceRefreshAttempts = 0;
            sourceRefreshDeferred = false;
            sourceRefreshRequired = changed || !SourceOutputMatches(session, format);
            sourceRefreshAt = sourceRefreshRequired ? services.Milliseconds() + 1000 : -1;
            DiagnosticLog.Write("receiver.camera.source_format", "camera_id", session.DeviceId, "session", format.Session, "revision", format.Revision,
                "source_width", format.Width, "source_height", format.Height, "source_fps", format.FPS, "changed", changed,
                "refresh_scheduled", sourceRefreshRequired, "manual_resolution", selectedResolution == null ? "auto" : selectedResolution.ToString());
        }

        private bool SourceOutputMatches(ReceiverSession session, BridgeCameraFormat format)
        {
            CameraResolution expected = selectedResolution ?? new CameraResolution(format.Width, format.Height);
            int observedWidth = Volatile.Read(ref session.ObservedWidth), observedHeight = Volatile.Read(ref session.ObservedHeight);
            int width = observedWidth > 0 ? observedWidth : Volatile.Read(ref session.ActualWidth);
            int height = observedHeight > 0 ? observedHeight : Volatile.Read(ref session.ActualHeight);
            return width == expected.Width && height == expected.Height;
        }

        private void PollSourceFormatRefresh(long now)
        {
            ReceiverSession active = current;
            if (!sourceRefreshRequired || sourceFormat == null || active == null || sourceRefreshAt < 0 || now < sourceRefreshAt ||
                !String.Equals(sourceFormatCamera, selectedCameraId, StringComparison.Ordinal) ||
                !String.Equals(active.DeviceId, selectedCameraId, StringComparison.Ordinal) || Interlocked.Read(ref active.StartedAt) < 0) return;
            if (sourceRefreshAttempts > 0 && SourceOutputMatches(active, sourceFormat))
            {
                sourceRefreshRequired = false; sourceRefreshAt = -1;
                DiagnosticLog.Write("receiver.camera.source_refresh_complete", "camera_id", active.DeviceId, "session", sourceFormat.Session,
                    "revision", sourceFormat.Revision, "attempts", sourceRefreshAttempts);
                return;
            }
            if (sourceRefreshAttempts >= 3)
            {
                sourceRefreshRequired = false; sourceRefreshAt = -1;
                DiagnosticLog.Write("receiver.camera.source_refresh_exhausted", "camera_id", active.DeviceId, "session", sourceFormat.Session,
                    "revision", sourceFormat.Revision, "attempts", sourceRefreshAttempts, "source_width", sourceFormat.Width, "source_height", sourceFormat.Height,
                    "actual_width", Volatile.Read(ref active.ActualWidth), "actual_height", Volatile.Read(ref active.ActualHeight),
                    "reason", "source_mode_not_available_after_fresh_capability_negotiation");
                return;
            }
            if (sourceRefreshAttempts > 0 && audioBridge != null && audioBridge.HasFileBenchmark)
            {
                if (!sourceRefreshDeferred)
                    DiagnosticLog.Write("receiver.camera.source_refresh_deferred", "camera_id", active.DeviceId, "session", sourceFormat.Session,
                        "revision", sourceFormat.Revision, "attempts", sourceRefreshAttempts, "reason", "file_benchmark_reserved");
                sourceRefreshDeferred = true;
                return;
            }
            sourceRefreshDeferred = false;
            // Preserve an open submenu and the selected device. Reopening and
            // disposal remain serialized on cameraGate, away from the UI thread.
            if (menu.Visible) { sourceRefreshAt = now + 1000; return; }
            sourceRefreshAttempts++;
            sourceRefreshAt = now + (sourceRefreshAttempts == 1 ? 10000 : 30000);
            DiagnosticLog.Write("receiver.camera.source_refresh", "camera_id", active.DeviceId, "session", sourceFormat.Session,
                "revision", sourceFormat.Revision, "attempt", sourceRefreshAttempts, "source_width", sourceFormat.Width,
                "source_height", sourceFormat.Height, "format_mode", active.CaptureMode);
            RefreshCameraModes(new CameraDevice(active.DeviceName, active.DeviceId));
        }

        private CaptureRetryPolicy CapturePolicy(string deviceId)
        {
            CaptureRetryPolicy policy;
            if (!captureRetries.TryGetValue(deviceId, out policy))
            {
                policy = new CaptureRetryPolicy();
                captureRetries.Add(deviceId, policy);
            }
            return policy;
        }

        internal void PollCaptureRetry()
        {
            if (closing) return;
            ReceiverSession active = current;
            long now = services.Milliseconds();
            if (active != null && active.AutomaticProbe)
            {
                if (now - active.ProbeStartedAt >= 5000)
                    CaptureFailed(active, new TimeoutException("The camera did not announce a Get Copy-Paste Back bridge within five seconds."));
                return;
            }
            long started = active == null ? -1 : Interlocked.Read(ref active.StartedAt);
            if (active != null && started >= 0 && Interlocked.Read(ref active.LastFrameAt) == 0 && now - started >= 20000)
                CaptureFailed(active, new TimeoutException("The selected camera did not deliver its first frame within 20 seconds."));
            long lastFrame = active == null ? -1 : Interlocked.Read(ref active.LastFrameObservedMs);
            if (active != null && lastFrame >= 0 && now - lastFrame >= 15000)
                CaptureFailed(active, new IOException("The selected camera stopped delivering frames for 15 seconds."));
            if (current == null && (automaticCameraSelection || !String.IsNullOrEmpty(selectedCameraId) && CapturePolicy(selectedCameraId).CanOpen(now))) RefreshDevices(null);
            PollSourceFormatRefresh(now);
        }

        private void StartCapture(CameraDevice device)
        {
            ReceiverSession active = current;
            // Capability/format refresh does not represent a manual camera
            // choice. Preserve verification until our control proved a source.
            StartCapture(device, automaticCameraSelection && (active == null || active.AutomaticProbe));
        }

        private void StartCapture(CameraDevice device, bool probe)
        {
            if (closing) return;
            ReceiverSession previous = current;
            CaptureRetryPolicy policy = CapturePolicy(device.Id);
            if (previous == null && !policy.CanOpen(services.Milliseconds())) return;
            // Every automatic entry path shares the same eligibility and finite
            // budget, including mode refresh and enumeration-driven reconnect.
            if (probe && !TryBeginAutomaticCameraProbe(device)) return;
            ReceiverSession session = new ReceiverSession(device.Id, device.Name);
            session.AutomaticProbe = probe;
            session.ProbeGeneration = automaticCameraGeneration;
            session.ProbeStartedAt = services.Milliseconds();
            session.RequestedResolution = probe ? null : selectedResolution;
            if (!probe && selectedResolution == null && sourceFormat != null && String.Equals(sourceFormatCamera, device.Id, StringComparison.Ordinal))
                session.SourceResolution = new CameraResolution(sourceFormat.Width, sourceFormat.Height);
            session.CaptureWidth = policy.Width; session.CaptureHeight = policy.Height; session.CaptureMode = policy.ModeName;
            CaptureBackend backend = policy.Backend;
            DiagnosticLog.Write("receiver.capture.open", "camera_name", device.Name, "camera_id", device.Id,
                "format_mode", session.CaptureMode, "requested_width", session.CaptureWidth, "requested_height", session.CaptureHeight,
                "requested_resolution", session.RequestedResolution == null ? "auto" : session.RequestedResolution.ToString(),
                "source_native_preference", session.SourceResolution == null ? null : session.SourceResolution.ToString(),
                "manual_native_request", session.RequestedResolution != null && backend == CaptureBackend.MediaFoundation,
                "changes_native_format", backend == CaptureBackend.DirectShow720 || backend == CaptureBackend.DirectShowHighest || backend == CaptureBackend.MediaFoundation);
            DiagnosticLog.Write("receiver.capture.session_changed", "previous_camera_id", previous == null ? null : previous.DeviceId, "camera_id", device.Id, "reason", previous == null ? "connect_or_reconnect" : "selection_or_format_changed");
            current = session;
            captureEpoch++;
            if (audioSender != null && !probe)
            {
                audioSender.DisableSession();
                if (audioBridge != null) audioBridge.Dispose();
                audioBridge = new ClientAudioBridge(dispatch, audioSender);
            }
            ResetPublication(previous);
            if (previous != null) retired.Enqueue(previous);
            ThreadPool.QueueUserWorkItem(delegate
            {
                // Opening and disposing a DirectShow graph can wait for its driver.
                // These operations are serialized off the STA/message-loop thread.
                lock (cameraGate)
                {
                    DisposeRetired();
                    if (!IsCurrent(session)) return;
                    IReceiverCapture created = null;
                    try
                    {
                        CameraResolution preferredResolution = session.RequestedResolution ?? session.SourceResolution;
                        if (preferredResolution != null && (backend == CaptureBackend.DirectShowHighest || backend == CaptureBackend.MediaFoundation) && services.OpenCaptureBackendAtResolution != null)
                            created = services.OpenCaptureBackendAtResolution(session.DeviceId, delegate(Bitmap frame) { ReceiveFrame(session, frame); }, backend, preferredResolution);
                        else if (session.RequestedResolution != null && backend == CaptureBackend.MediaFoundation && services.OpenCaptureAtResolution != null)
                            created = services.OpenCaptureAtResolution(session.DeviceId, delegate(Bitmap frame) { ReceiveFrame(session, frame); }, session.RequestedResolution);
                        else if (services.OpenCaptureBackend != null)
                            created = services.OpenCaptureBackend(session.DeviceId, delegate(Bitmap frame) { ReceiveFrame(session, frame); }, backend);
                        else created = services.OpenCapture(session.DeviceId, delegate(Bitmap frame) { ReceiveFrame(session, frame); });
                        if (created == null) throw new InvalidOperationException();
                        session.Capture = created;
                        created.OnError += delegate(Exception error) { DiagnosticLog.Error("receiver.capture.error", error, "camera_id", session.DeviceId); CaptureFailed(session, error); };
                        if (!IsCurrent(session)) { DisposeCapture(session); return; }
                        created.Start();
                        IReceiverCaptureModes modeSource = created as IReceiverCaptureModes;
                        if (modeSource != null)
                        {
                            CameraResolution[] modes = modeSource.AvailableResolutions;
                            CameraResolution actual = modeSource.CurrentResolution;
                            Ui(delegate { ApplyModeCatalog(session, modes, actual); });
                        }
                        Interlocked.Exchange(ref session.StartedAt, services.Milliseconds());
                        DiagnosticLog.Write("receiver.capture.started", "camera_name", session.DeviceName, "camera_id", session.DeviceId, "format_mode", session.CaptureMode);
                        if (!IsCurrent(session)) DisposeCapture(session);
                    }
                    catch (Exception error)
                    {
                        DiagnosticLog.Error("receiver.capture.open_failed", error, "camera_name", session.DeviceName, "camera_id", session.DeviceId);
                        DisposeCapture(session);
                        CaptureFailed(session, error);
                    }
                }
            });
        }

        private static string CaptureErrorChain(Exception error)
        {
            var chain = new StringBuilder();
            for (int depth = 0; error != null && depth < 16; depth++, error = error.InnerException)
            {
                if (chain.Length != 0) chain.Append(" -> ");
                chain.Append(error.GetType().FullName).Append("/0x").Append(error.HResult.ToString("X8"));
                CameraStreamException stream = error as CameraStreamException;
                if (stream != null) chain.Append("/event=").Append(stream.EventCode);
            }
            return chain.ToString();
        }

        private void CaptureFailed(ReceiverSession session, Exception error)
        {
            Ui(delegate
            {
                if (!IsCurrent(session)) return;
                bool hadFrames = !session.AutomaticProbe && Interlocked.Read(ref session.LastFrameAt) != 0;
                CaptureRetryPolicy policy = CapturePolicy(session.DeviceId);
                policy.RecordFailure(error, hadFrames, services.Milliseconds());
                DiagnosticLog.Write("receiver.capture.session_failed", "camera_id", session.DeviceId, "reason", "capture_graph_error",
                    "format_mode", session.CaptureMode, "had_frames", hadFrames,
                    "compatibility_retry", policy.ModeName != session.CaptureMode,
                    "next_format_mode", policy.ModeName, "next_width", policy.Width, "next_height", policy.Height,
                    "error_chain", CaptureErrorChain(error));
                current = null;
                captureEpoch++;
                StopAudioSession();
                ResetPublication(session);
                QueueDispose(session);
                if (automaticCameraSelection) RefreshDevices(null);
                // Let Horizon release this source before trying the next API
                // for the same exact selected camera.
            });
        }

        private bool IsCurrent(ReceiverSession session)
        {
            return !closing && session != null && Object.ReferenceEquals(current, session);
        }

        private static void RecordFrameDiagnostics(ReceiverSession session, Bitmap frame, Packet[] packets, long decodeTicks)
        {
            if (!DiagnosticLog.IsEnabled) return;
            long now = Stopwatch.GetTimestamp();
            if (session.DebugWidth != frame.Width || session.DebugHeight != frame.Height)
            {
                if (session.DebugWidth == 0) session.DebugLast = now;
                DiagnosticLog.Write(session.DebugWidth == 0 ? "receiver.frame.first" : "receiver.frame.size_changed", "camera_id", session.DeviceId, "width", frame.Width, "height", frame.Height);
                session.DebugWidth = frame.Width; session.DebugHeight = frame.Height;
            }
            session.DebugFrames++;
            session.DebugDecodeTicks += decodeTicks; session.DebugMaximumTicks = Math.Max(session.DebugMaximumTicks, decodeTicks);
            int count = packets == null ? 0 : packets.Length;
            if (count == 0) session.DebugEmptyFrames++; else session.DebugDecodedFrames++;
            if (packets != null)
                foreach (Packet packet in packets)
                {
                    if (packet == null) continue;
                    session.DebugPackets++;
                    if (!session.DebugFirstPacket)
                    {
                        session.DebugFirstPacket = true;
                        DiagnosticLog.Write("receiver.packet.first", "camera_id", session.DeviceId, "transfer", packet.TransferId, "profile", packet.Profile, "index", packet.Index, "count", packet.Count, "total_bytes", packet.TotalLength, "chunk_bytes", packet.Data == null ? 0 : packet.Data.Length);
                    }
                    int profile = (int)packet.Profile;
                    if (profile >= 0 && profile < session.DebugProfiles.Length) session.DebugProfiles[profile]++;
                    Guid identity = packet.TransferId;
                    FileBenchmarkCommand command;
                    if (FileBenchmarkProtocol.TryCommand(packet, out command)) identity = command.Test;
                    PacketRange range;
                    if (!session.DebugRanges.TryGetValue(identity, out range))
                    {
                        if (session.DebugRanges.Count >= 8) { session.DebugUntrackedPackets++; continue; }
                        range = new PacketRange { Minimum = packet.Index, Maximum = packet.Index, Profile = packet.Profile };
                        session.DebugRanges.Add(identity, range);
                    }
                    range.Minimum = Math.Min(range.Minimum, packet.Index); range.Maximum = Math.Max(range.Maximum, packet.Index); range.Count++;
                }
            long elapsed = now - session.DebugLast;
            if (elapsed < Stopwatch.Frequency) return;
            var profiles = new StringBuilder(); var ranges = new StringBuilder();
            for (int index = 0; index < session.DebugProfiles.Length; index++)
                if (session.DebugProfiles[index] != 0) { if (profiles.Length != 0) profiles.Append("; "); profiles.Append((VideoProfile)index).Append(':').Append(session.DebugProfiles[index]); }
            foreach (KeyValuePair<Guid, PacketRange> pair in session.DebugRanges)
            { if (ranges.Length != 0) ranges.Append("; "); ranges.Append(pair.Key.ToString("N")).Append('/').Append(pair.Value.Profile).Append(':').Append(pair.Value.Minimum).Append("..").Append(pair.Value.Maximum).Append(" n=").Append(pair.Value.Count); }
            DiagnosticLog.Write("receiver.decode.summary", "camera_id", session.DeviceId, "width", frame.Width, "height", frame.Height,
                "interval_ms", elapsed * 1000.0 / Stopwatch.Frequency, "incoming_frames", session.DebugFrames, "decoded_frames", session.DebugDecodedFrames,
                "nondecoded_frames", session.DebugEmptyFrames, "packets", session.DebugPackets, "decode_mean_ms", session.DebugDecodeTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, session.DebugFrames),
                "decode_max_ms", session.DebugMaximumTicks * 1000.0 / Stopwatch.Frequency, "profiles", profiles.ToString(), "tests_index_ranges", ranges.ToString(), "untracked_packets", session.DebugUntrackedPackets);
            session.DebugLast = now; session.DebugFrames = session.DebugDecodedFrames = session.DebugEmptyFrames = session.DebugPackets = session.DebugDecodeTicks = session.DebugMaximumTicks = session.DebugUntrackedPackets = 0;
            Array.Clear(session.DebugProfiles, 0, session.DebugProfiles.Length); session.DebugRanges.Clear();
        }

        private void ReceiveFrame(ReceiverSession session, Bitmap frame)
        {
            if (!IsCurrent(session)) return;
            Volatile.Write(ref session.ObservedWidth, frame.Width); Volatile.Write(ref session.ObservedHeight, frame.Height);
            Interlocked.Exchange(ref session.LastFrameObservedMs, services.Milliseconds());
            bool firstFrame = Interlocked.Exchange(ref session.LastFrameAt, Stopwatch.GetTimestamp()) == 0;
            if (session.AutomaticProbe) { ReceiveAutomaticProbeFrame(session, frame); return; }
            if (firstFrame)
                Ui(delegate { if (IsCurrent(session)) CapturePolicy(session.DeviceId).RecordSuccess(); });
            if (audioBridge != null) audioBridge.ObserveFrame(frame.Width, frame.Height);
            if (services.DecodeFrames == null && services.DecodeFramesPreferred == null) { ReceivePacketFrame(session, frame, null); return; }
            try
            {
                long started = Stopwatch.GetTimestamp();
                int expected = Volatile.Read(ref session.ExpectedProfile);
                VideoProfile? preferred = expected < 0 ? (VideoProfile?)null : (VideoProfile)expected;
                Packet[] packets = services.DecodeFramesPreferred != null ? services.DecodeFramesPreferred(frame, preferred) : services.DecodeFrames(frame);
                RecordFrameDiagnostics(session, frame, packets, Stopwatch.GetTimestamp() - started);
                if (packets != null) foreach (Packet packet in packets) ReceivePacketFrame(session, frame, packet);
            }
            catch (Exception error) { DiagnosticLog.Error("receiver.decode.failed", error, "camera_id", session.DeviceId, "width", frame.Width, "height", frame.Height); }
        }

        private void ReceivePacketFrame(ReceiverSession session, Bitmap frame, Packet decoded)
        {
            if (!IsCurrent(session)) return;
            try
            {
                Packet packet = decoded;
                if (packet == null)
                {
                    long started = Stopwatch.GetTimestamp();
                    packet = services.DecodeFrame(frame);
                    RecordFrameDiagnostics(session, frame, packet == null ? null : new[] { packet }, Stopwatch.GetTimestamp() - started);
                }
                if (packet == null || !IsCurrent(session)) return;
                BridgeCameraFormat source;
                if (BridgeControl.TryCameraFormat(packet, out source))
                {
                    Ui(delegate { HandleCameraFormat(session, source); });
                    return;
                }
                FileBenchmarkCommand command;
                if (FileBenchmarkProtocol.TryCommand(packet, out command))
                    Volatile.Write(ref session.ExpectedProfile, command.Phase == FileBenchmarkPhase.Cancel ? -1 : (int)command.Profile);
                else if (packet.Profile != VideoProfile.Compatible && VisualProtocol.IsDefinedProfile(packet.Profile))
                    Volatile.Write(ref session.ExpectedProfile, (int)packet.Profile);
                ClientAudioBridge bridge = audioBridge;
                if (bridge != null && bridge.HandleVideo(packet)) return;
                Guid acknowledged;
                if (BridgeControl.TryAcknowledge(packet, out acknowledged))
                {
                    long now = Stopwatch.GetTimestamp();
                    if (acknowledged != session.LastAcknowledged || now - session.LastAcknowledgedAt >= 5 * Stopwatch.Frequency)
                    {
                        session.LastAcknowledged = acknowledged; session.LastAcknowledgedAt = now;
                        DiagnosticLog.Write("receiver.control.acknowledge", "session", packet.TransferId, "audioTransfer", acknowledged);
                    }
                    Ui(delegate { if (IsCurrent(session) && audioSender != null) audioSender.Acknowledge(acknowledged); });
                    return;
                }
                BridgeHello hello;
                if (BridgeControl.TryHello(packet, out hello))
                {
                    Ui(delegate {
                        if (!IsCurrent(session) || audioBridge == null) return;
                        audioBridge.HandleHello(hello, session.DeviceName);
                    });
                    return;
                }
                // Reject a bounded remembered completion before it can replace
                // the active identity of another pending STA publication.
                if (session.Assembler.HasCompleted(packet.TransferId)) return;
                string id = packet.TransferId.ToString("N");
                bool transferChanged;
                lock (session.StateGate)
                {
                    transferChanged = !String.Equals(session.TransferId, id, StringComparison.Ordinal);
                    if (transferChanged)
                    {
                        session.TransferId = id;
                        session.FailedTransferId = null;
                        session.Receiving = !String.Equals(session.ReadyTransferId, id, StringComparison.Ordinal);
                    }
                }
                if (transferChanged)
                {
                    DiagnosticLog.Write("receiver.transfer.started", "transfer", packet.TransferId, "profile", packet.Profile, "packet_count", packet.Count, "total_bytes", packet.TotalLength);
                    Ui(delegate
                    {
                        if (!IsCurrent(session) || !String.Equals(session.TransferId, id, StringComparison.Ordinal)) return;
                        if (session.Pending != null && session.Pending.Id != packet.TransferId)
                        {
                            session.Pending = null;
                            publicationTimer.Stop();
                        }
                    });
                }
                if (String.Equals(session.FailedTransferId, id, StringComparison.Ordinal)) return;
                byte[] completed = session.Assembler.Add(packet);
                if (completed == null || !IsCurrent(session)) return;
                DiagnosticLog.Write("receiver.transfer.assembled", "transfer", packet.TransferId, "bytes", completed.Length, "profile", packet.Profile);
                DecodedPayload payload = services.DecodePayload(completed);
                if (!IsCurrent(session)) return;
                if (payload.IsFileDrop) services.PreparePayload(payload, receivedRoot, packet.TransferId);
                Ui(delegate
                {
                    if (!IsCurrent(session) || !String.Equals(session.TransferId, id, StringComparison.Ordinal)) return;
                    session.Pending = new PendingPublication { Payload = payload, Id = packet.TransferId, EncodedBytes = completed.Length };
                    PublishPending(session);
                });
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("receiver.packet.failed", error, "camera_id", session.DeviceId);
                services.LogFailure(error);
                string failedId = session.TransferId;
                Ui(delegate
                {
                    if (!IsCurrent(session) || !String.Equals(session.TransferId, failedId, StringComparison.Ordinal)) return;
                    FailPublication(session, failedId);
                });
            }
        }

        private void PublishPending(ReceiverSession session)
        {
            if (!IsCurrent(session) || session.Pending == null) { publicationTimer.Stop(); return; }
            PendingPublication pending = session.Pending;
            string id = pending.Id.ToString("N");
            if (!String.Equals(session.TransferId, id, StringComparison.Ordinal))
            {
                session.Pending = null;
                publicationTimer.Stop();
                return;
            }
            try
            {
                services.PublishPayload(pending.Payload, receivedRoot, pending.Id);
                DiagnosticLog.Write("receiver.transfer.published", "transfer", pending.Id, "attempts", pending.Attempts + 1);
                if (audioSender != null) audioSender.SuppressCurrentClipboard();
                bool completedCurrent;
                lock (session.StateGate)
                {
                    session.ReadyTransferId = id;
                    completedCurrent = String.Equals(session.TransferId, id, StringComparison.Ordinal);
                    if (completedCurrent) session.Receiving = false;
                }
                session.Pending = null;
                publicationTimer.Stop();
                if (completedCurrent)
                {
                    try
                    {
                        if (audioBridge != null) audioBridge.NotifyPublished(pending.Id, pending.EncodedBytes);
                        if (services.NotifyPublished != null) services.NotifyPublished(pending.Id, pending.EncodedBytes);
                    }
                    catch (Exception error) { DiagnosticLog.Error("receiver.delivery.receipt_failed", error, "transfer", pending.Id); }
                }
                if (completedCurrent && deferredPaste != null) deferredPaste.Complete();
            }
            catch (ExternalException)
            {
                if (++pending.Attempts < 10) publicationTimer.Start();
                else FailPublication(session, id);
            }
            catch (Exception error) { services.LogFailure(error); FailPublication(session, id); }
        }

        private void FailPublication(ReceiverSession session, string expectedId)
        {
            session.Pending = null;
            publicationTimer.Stop();
            lock (session.StateGate)
            {
                if (!String.Equals(session.TransferId, expectedId, StringComparison.Ordinal)) return;
                session.FailedTransferId = expectedId;
                session.Receiving = false;
            }
            if (deferredPaste != null) deferredPaste.Cancel();
        }

        private void ResetPublication(ReceiverSession session)
        {
            publicationTimer.Stop();
            if (session != null)
            {
                session.Receiving = false;
                session.Pending = null;
            }
            if (deferredPaste != null) deferredPaste.Cancel();
        }

        private void StopAudioSession()
        {
            DiagnosticLog.Write("receiver.audio.session_stopping", "reason", "camera_session_unavailable");
            if (audioSender != null) audioSender.DisableSession();
            if (audioBridge != null) { audioBridge.Dispose(); audioBridge = null; }
        }

        private void Ui(Action action)
        {
            if (closing || dispatch.IsDisposed) return;
            try
            {
                dispatch.BeginInvoke((MethodInvoker)delegate
                {
                    if (!closing && !dispatch.IsDisposed)
                    {
                        try { action(); }
                        catch (Exception error) { DiagnosticLog.Error("receiver.dispatch.failed", error); }
                    }
                });
            }
            catch (InvalidOperationException error) { DiagnosticLog.Write("receiver.dispatch.rejected", "error_type", error.GetType().FullName, "closing", closing); }
        }

        private void QueueDispose(ReceiverSession session)
        {
            if (session == null) return;
            retired.Enqueue(session);
            ThreadPool.QueueUserWorkItem(delegate { lock (cameraGate) { DisposeRetired(); } });
        }

        private void DisposeRetired()
        {
            ReceiverSession session;
            while (retired.TryDequeue(out session)) DisposeCapture(session);
        }

        private static void DisposeCapture(ReceiverSession session)
        {
            if (session == null) return;
            IReceiverCapture old = session.Capture;
            session.Capture = null;
            if (old != null)
            {
                try { old.Dispose(); }
                catch (Exception error) { DiagnosticLog.Error("receiver.capture.dispose_failed", error, "camera_id", session.DeviceId); }
            }
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs args) { Ui(delegate { ExitThread(); }); }
        private void OnApplicationExit(object sender, EventArgs args) { Dispose(); }

        protected override void ExitThreadCore()
        {
            Dispose();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !closing)
            {
                closing = true;
                automaticCameraGeneration++;
                Interlocked.Increment(ref modemRequest);
                Interlocked.Increment(ref audioOutputRequest);
                DiagnosticLog.Write("receiver.disposing");
                Application.ApplicationExit -= OnApplicationExit;
                if (sessionEventsSubscribed)
                {
                    try { SystemEvents.SessionEnding -= OnSessionEnding; }
                    catch { }
                }
                ReceiverSession previous = current;
                current = null;
                if (audioBridge != null) { audioBridge.Dispose(); audioBridge = null; }
                if (audioSender != null) audioSender.Dispose();
                ResetPublication(previous);
                publicationTimer.Dispose();
                retryTimer.Stop();
                retryTimer.Dispose();
                if (deferredPaste != null)
                {
                    try { deferredPaste.Dispose(); }
                    catch { }
                    deferredPaste = null;
                }
                icon.Visible = false;
                icon.Dispose();
                trayImage.Dispose();
                menu.Dispose();
                dispatch.Dispose();
                enumerationCompletions.Clear();
                QueueDispose(previous);
            }
            base.Dispose(disposing);
        }

        private sealed class ReceiverSession
        {
            internal int ExpectedProfile = -1; // Capture callback thread; atomic access for diagnostics/tests.
            internal volatile bool AutomaticProbe;
            internal long ProbeGeneration, ProbeStartedAt;
            internal int DebugWidth, DebugHeight;
            internal bool DebugFirstPacket;
            internal long DebugLast = Stopwatch.GetTimestamp();
            internal long DebugFrames, DebugDecodedFrames, DebugEmptyFrames, DebugPackets, DebugDecodeTicks, DebugMaximumTicks, DebugUntrackedPackets;
            internal readonly long[] DebugProfiles = new long[VisualProtocol.GetAllProfiles().Length];
            internal readonly Dictionary<Guid, PacketRange> DebugRanges = new Dictionary<Guid, PacketRange>();
            internal readonly string DeviceId;
            internal readonly string DeviceName;
            internal long LastFrameAt, LastAcknowledgedAt;
            internal long StartedAt = -1;
            internal long LastFrameObservedMs = -1;
            internal int ActualWidth, ActualHeight, ObservedWidth, ObservedHeight;
            internal int CaptureWidth, CaptureHeight;
            internal string CaptureMode;
            internal CameraResolution RequestedResolution;
            internal CameraResolution SourceResolution;
            internal Guid LastAcknowledged;
            internal readonly object StateGate = new object();
            internal readonly TransferAssembler Assembler = new TransferAssembler();
            internal IReceiverCapture Capture; // Protected by cameraGate.
            internal volatile bool Receiving;
            internal volatile string TransferId;
            internal volatile string ReadyTransferId;
            internal volatile string FailedTransferId;
            internal PendingPublication Pending; // STA thread only.
            internal ReceiverSession(string deviceId, string deviceName)
            { DeviceId = deviceId; DeviceName = deviceName; }
        }

        private sealed class PacketRange
        {
            internal int Minimum, Maximum;
            internal long Count;
            internal VideoProfile Profile;
        }

        private sealed class PendingPublication
        {
            internal DecodedPayload Payload;
            internal Guid Id;
            internal int Attempts, EncodedBytes;
        }

        private sealed class DispatchControl : Control
        {
            internal Action ClipboardChanged;
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x031D && ClipboardChanged != null) ClipboardChanged();
                base.WndProc(ref message);
            }
            internal DispatchControl()
            {
                // A generic Control is a child of WinForms' parking window. A
                // top-level hidden owner gives the notification menu its own root
                // HWND for standard foreground activation and dismissal behavior.
                SetTopLevel(true);
                Visible = false;
                TabStop = false;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.Style &= ~0x10000000; // WS_VISIBLE
                    parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                    return parameters;
                }
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        private void UpdateAudioMenu(AudioEndpoint[] outputs)
        {
            outputs = outputs ?? new AudioEndpoint[0];
            string active = null;
            AudioEndpoint resolved = invalidAudioSelection ? null : AudioOutputSelection.Resolve(outputs, selectedAudioName, selectedAudioPersistentId);
            int exactNames = 0;
            foreach (AudioEndpoint endpoint in outputs) if (endpoint != null && String.Equals(endpoint.Name, selectedAudioName, StringComparison.Ordinal)) exactNames++;
            string resolution = resolved != null ? !String.IsNullOrEmpty(selectedAudioPersistentId) ? "saved_persistent_id" : String.IsNullOrEmpty(selectedAudioName) ? "automatic_horizon" : "saved_unique_name" :
                outputs.Length == 0 ? "no_outputs" : invalidAudioSelection ? "invalid_saved_selection" : String.IsNullOrEmpty(selectedAudioName) ? "no_horizon_output" :
                exactNames > 1 ? "ambiguous_saved_name" : "saved_name_missing_or_invalid";
            DiagnosticLog.Write("receiver.audio.output.resolved", "reason", resolution, "saved_endpoint", selectedAudioId, "saved_name", selectedAudioName,
                "resolved_endpoint", resolved == null ? null : resolved.Id, "resolved_name", resolved == null ? null : resolved.Name,
                "persistent_endpoint", selectedAudioPersistentId, "output_count", outputs.Length);
            if (invalidAudioSelection || !String.IsNullOrEmpty(selectedAudioName) || !String.IsNullOrEmpty(selectedAudioPersistentId))
            {
                active = resolved == null ? null : resolved.Id;
                // WinMM indices can change when a headset is unplugged. Resolve
                // stable identity (or a unique legacy name) before opening it.
                QueueAudioOutput(active ?? "waveout:65535", false, false);
                if (active != null) selectedAudioId = active;
            }
            else
            {
                active = resolved == null ? null : resolved.Id;
                QueueAudioOutput(null, false, false);
            }
            ToolStripMenuItem audio = new ToolStripMenuItem(UiText.Get("Аудиовыход", "Audio output")) { Name = "audio.output" };
            audio.AutoToolTip = false;
            ObserveSubmenu(audio, "audio.output");
            foreach (AudioEndpoint endpoint in outputs)
            {
                if (endpoint == null) continue;
                AudioEndpoint selected = endpoint;
                ToolStripMenuItem item = new ToolStripMenuItem(endpoint.Name.Replace("&", "&&"));
                item.Name = "audio.output.select";
                item.AutoToolTip = false;
                item.Checked = String.Equals(active, endpoint.Id, StringComparison.Ordinal);
                item.Click += delegate
                {
                    DiagnosticLog.Write("receiver.audio.output.clicked", "previous_endpoint", selectedAudioId, "previous_name", selectedAudioName,
                        "requested_endpoint", selected.Id, "requested_name", selected.Name, "action_id", uiAction);
                    selectedAudioId = selected.Id; selectedAudioName = selected.Name;
                    selectedAudioPersistentId = AudioOutputSelection.PersistentIdentity(selected); invalidAudioSelection = false;
                    DiagnosticLog.Write("receiver.audio.output.requested", "endpoint", selectedAudioId, "name", selectedAudioName, "action_id", uiAction);
                    foreach (ToolStripMenuItem other in audio.DropDownItems) other.Checked = Object.ReferenceEquals(other, item);
                    // Save the explicit preference independently of optional
                    // diagnostic/device work, as with the camera preference.
                    try
                    {
                        services.WriteAudioSelection(selectedAudioPersistentId == null ? new[] { selectedAudioId, selectedAudioName } :
                            new[] { selectedAudioId, selectedAudioName, selectedAudioPersistentId });
                        DiagnosticLog.Write("receiver.setting.written", "setting", "audio.id");
                    }
                    catch (Exception error) { LogSettingFailure("write", "audio.id", error); services.LogFailure(error); }
                    QueueAudioOutput(selected.Id, true, true);
                };
                audio.DropDownItems.Add(item);
            }
            if (audio.DropDownItems.Count == 0) audio.Enabled = false;
            int index = menu.Items.IndexOfKey("application.version");
            if (index < 0) index = Math.Max(0, menu.Items.Count - 1);
            menu.Items.Insert(index, audio);
            menu.Items.Insert(index + 1, new ToolStripSeparator());
        }

        private bool HasAudioOutput { get { return audioSender != null || services.SetAudioOutput != null && services.TestAudioOutput != null; } }

        private bool AudioOutputIdentitiesMatch(AudioEndpoint[] outputs)
        {
            if (outputs.Length != lastMenuOutputs.Length) return false;
            for (int index = 0; index < outputs.Length; index++)
            {
                AudioEndpoint next = outputs[index], previous = lastMenuOutputs[index];
                if (next == null || previous == null) { if (!Object.ReferenceEquals(next, previous)) return false; }
                else if (!String.Equals(next.PersistentId, previous.PersistentId, StringComparison.Ordinal) ||
                    !String.Equals(next.LegacyName, previous.LegacyName, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private void QueueAudioOutput(string endpoint, bool probe, bool selectedByUser)
        {
            if (closing || !HasAudioOutput) return;
            // Identical menu refreshes preserve a pending user diagnostic. A
            // different passive resolution must supersede older queued work.
            if (!probe && audioOutputRequested && String.Equals(requestedAudioOutput, endpoint, StringComparison.Ordinal)) return;
            long request = Interlocked.Increment(ref audioOutputRequest);
            audioOutputRequested = true;
            requestedAudioOutput = endpoint;
            long action = uiAction;
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (audioOutputGate)
                {
                    if (closing || Interlocked.Read(ref audioOutputRequest) != request) return;
                    try
                    {
                        if (services.SetAudioOutput != null) services.SetAudioOutput(endpoint); else audioSender.SetOutput(endpoint);
                        if (closing || Interlocked.Read(ref audioOutputRequest) != request) return;
                        DiagnosticLog.Write("receiver.audio.output.configured", "endpoint", endpoint, "probe", probe, "request", request, "action_id", action);
                        if (probe)
                        {
                            Guid id = services.TestAudioOutput != null ? services.TestAudioOutput() : audioSender.TestOutput();
                            DiagnosticLog.Write("receiver.audio.test.queued", "probe", id, "endpoint", endpoint, "automatic", selectedByUser, "action_id", action);
                        }
                    }
                    catch (Exception error)
                    {
                        DiagnosticLog.Error("receiver.audio.output.selection_failed", error, "endpoint", endpoint, "probe", probe, "action_id", action);
                        services.LogFailure(error);
                    }
                }
            });
        }
    }

    // Opening this selector performs no codec initialization or audio I/O. A
    // failed switch preserves the previous checked item.
    internal static class AudioModemSelection
    {
        internal static AudioModemKind Read(ClientPaths paths)
        {
            try
            {
                AudioModemKind kind;
                if (AudioModemCatalog.TryParse(File.ReadAllText(paths.AudioModemSetting).Trim(), out kind)) return kind;
                DiagnosticLog.Write("receiver.audio.modem.setting.invalid");
            }
            catch (FileNotFoundException) { DiagnosticLog.Write("receiver.setting.missing", "operation", "read", "setting", "audio.modem"); }
            catch (DirectoryNotFoundException) { DiagnosticLog.Write("receiver.setting.missing", "operation", "read", "setting", "audio.modem"); }
            catch (Exception error) { DiagnosticLog.Write("receiver.setting.failed", "operation", "read", "setting", "audio.modem", "error_type", error.GetType().FullName, "hresult", error.HResult); paths.TryLogFailure(error); }
            return AudioModemCatalog.Default;
        }

        internal static void Write(ClientPaths paths, AudioModemKind kind)
        {
            File.WriteAllText(paths.AudioModemSetting, AudioModemCatalog.GetId(kind), new UTF8Encoding(false));
        }

        internal static ToolStripMenuItem BuildMenu(AudioModemKind selected, Func<AudioModemKind, bool> select)
        {
            if (select == null) throw new ArgumentNullException("select");
            ToolStripMenuItem menu = new ToolStripMenuItem(UiText.Get("Аудиомодем", "Audio modem")) { AutoToolTip = false };
            foreach (AudioModemKind kind in new[] { AudioModemKind.Fsk, AudioModemKind.Quiet, AudioModemKind.Aicodix })
            {
                AudioModemKind choice = kind;
                ToolStripMenuItem item = new ToolStripMenuItem(AudioModemCatalog.GetCaption(choice))
                {
                    Name = "audio.modem." + AudioModemCatalog.GetId(choice), AutoToolTip = false, Tag = choice, Checked = choice == selected
                };
                item.Click += delegate
                {
                    if (!select(choice)) return;
                    foreach (ToolStripMenuItem other in menu.DropDownItems) other.Checked = Object.ReferenceEquals(item, other);
                };
                menu.DropDownItems.Add(item);
            }
            return menu;
        }
    }

    internal interface IReceiverCapture : IDisposable
    {
        event Action<Exception> OnError;
        void Start();
    }

    internal sealed class ReceiverServices
    {
        internal readonly ClientPaths Paths;
        internal Action<Exception> LogFailure;
        internal ReceiverServices() : this(ClientPaths.Current) { }
        internal ReceiverServices(ClientPaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            Paths = paths;
            ReadSelection = delegate { return File.ReadAllText(Paths.CameraSelection).Trim(); };
            WriteSelection = delegate(string id) { File.WriteAllText(Paths.CameraSelection, id); };
            ReadAutomaticCameraSelection = delegate { return File.ReadAllText(Paths.AutomaticCameraSetting).Trim(); };
            WriteAutomaticCameraSelection = delegate(bool enabled) { File.WriteAllText(Paths.AutomaticCameraSetting, enabled ? "1" : "0"); };
            ReadAudioSelection = delegate { return File.ReadAllLines(Paths.AudioSelection); };
            WriteAudioSelection = delegate(string[] selection) { File.WriteAllLines(Paths.AudioSelection, selection); };
            ReceivedRoot = Paths.ReceivedRoot;
            LogFailure = Paths.TryLogFailure;
        }
        internal Func<List<CameraDevice>> ListDevices = CameraDevices.List;
        internal string[] PreferredCameraIds = new string[0];
        internal Func<string, Action<Bitmap>, IReceiverCapture> OpenCapture = delegate(string id, Action<Bitmap> onFrame)
        {
            return new ReceiverCapture(new CameraCapture(id, onFrame, 1280, 720));
        };
        internal Func<string, Action<Bitmap>, CaptureBackend, IReceiverCapture> OpenCaptureBackend;
        internal Func<string, Action<Bitmap>, CameraResolution, IReceiverCapture> OpenCaptureAtResolution;
        internal Func<string, Action<Bitmap>, CaptureBackend, CameraResolution, IReceiverCapture> OpenCaptureBackendAtResolution;
        internal Action<AudioModemKind> PrepareAudioModem;
        internal Action<AudioModemKind> ApplyAudioModem;
        internal Action<AudioModemKind> WriteAudioModem;
        internal Func<AudioEndpoint[]> ListAudioOutputs = AudioPlayer.EnumerateEndpoints;
        internal Action<string> SetAudioOutput;
        internal Func<Guid> TestAudioOutput;
        internal Func<string[]> ReadAudioSelection;
        internal Action<string[]> WriteAudioSelection;
        internal Func<long> Milliseconds = delegate { return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency); };
        internal Func<Bitmap, Packet> DecodeFrame = VisualProtocol.DecodeFrame;
        internal Func<Bitmap, Packet[]> DecodeFrames;
        internal Func<Bitmap, VideoProfile?, Packet[]> DecodeFramesPreferred;
        internal Func<byte[], DecodedPayload> DecodePayload = ClipboardPayload.Decode;
        internal Action<DecodedPayload, string, Guid> PreparePayload = delegate(DecodedPayload payload, string root, Guid id)
        {
            MemoryFileClipboard.Prepare(payload);
        };
        internal Action<DecodedPayload, string, Guid> PublishPayload = delegate(DecodedPayload payload, string root, Guid id)
        {
            MemoryFileClipboard.Publish(payload);
        };
        internal Action<Guid, int> NotifyPublished = delegate { };
        internal Func<string> ReadSelection;
        internal Action<string> WriteSelection;
        internal Func<string> ReadAutomaticCameraSelection;
        internal Action<bool> WriteAutomaticCameraSelection;
        internal string ReceivedRoot;
        internal bool ShowTrayIcon = true;
        internal bool EnableDeferredPaste = true;
        internal bool EnableRetryTimer = true;
        internal bool EnableSessionEvents = true;
        internal bool RestoreSelection = true;

    }

    internal sealed class ReceiverCapture : IReceiverCapture, IReceiverCaptureModes
    {
        private readonly CameraCapture capture;
        internal ReceiverCapture(CameraCapture capture) { this.capture = capture; }
        public CameraResolution[] AvailableResolutions { get { return capture.AvailableResolutions; } }
        public CameraResolution CurrentResolution { get { return capture.CurrentResolution; } }
        public event Action<Exception> OnError
        {
            add { capture.OnError += value; }
            remove { capture.OnError -= value; }
        }
        public void Start() { capture.Start(); }
        public void Dispose() { capture.Dispose(); }
    }
}
