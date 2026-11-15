using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HorizonClient
{
    // Only window presentation is replaceable in hidden lifecycle fixtures.
    // Camera, clipboard and sender cleanup still uses the production form.
    internal sealed class ServerTrayActions
    {
        internal Action HideWindow;
        internal Action<Rectangle, FormWindowState> RestoreWindow;
        internal Action ActivateWindow;
        internal Func<bool> IsMinimized;
        internal Func<Rectangle[]> WorkingAreas;
        internal Action<Action> Post;
    }

    // Shutdown fixtures replace only scheduling and the verified runtime stop.
    // They never enumerate processes, open devices or run an installer.
    internal sealed class ServerShutdownActions
    {
        internal Func<ServerConnection, bool> StopOwnedCamera;
        internal Action<Action> Queue;
    }

    internal sealed partial class ServerForm
    {
        private NotifyIcon serverTrayIcon;
        private ContextMenuStrip serverTrayMenu;
        private Icon serverTrayImage;
        private ServerTrayActions serverTrayActions;
        private bool serverTrayAvailable, serverHiddenToTray, serverTrayRestoring, serverExitRequested;
        private bool serverShutdownInProgress, serverShutdownCompleted, serverMayHaveStartedCamera;
        private ServerConnection ownedCameraConnection;
        private ServerShutdownActions serverShutdownActions;
        private Rectangle serverNormalBounds;
        private FormWindowState serverRestoreState = FormWindowState.Normal;
        private int serverTrayDisposed, serverResourcesStopped;
        private int serverPresentationRevision, serverQueuedHideRevision = -1;

        private void InitializeServerTray()
        {
            serverShutdownActions = new ServerShutdownActions {
                StopOwnedCamera = delegate(ServerConnection connection) { return runtime.StopOwnedCamera(connection); },
                Queue = delegate(Action action) { ThreadPool.QueueUserWorkItem(delegate { action(); }); }
            };
            serverNormalBounds = Bounds;
            serverTrayActions = new ServerTrayActions {
                HideWindow = Hide,
                RestoreWindow = delegate(Rectangle bounds, FormWindowState state) {
                    WindowState = FormWindowState.Normal;
                    Show(); ShowServerWindowNative(Handle, 9); // SW_RESTORE, including native iconic state.
                    MinimumSize = new Size(Math.Min(MinimumSize.Width, bounds.Width), Math.Min(MinimumSize.Height, bounds.Height));
                    WindowState = FormWindowState.Normal; Bounds = bounds;
                    if (state == FormWindowState.Maximized) { WindowState = state; ShowServerWindowNative(Handle, 3); }
                },
                ActivateWindow = delegate {
                    BringToFront(); Activate();
                    bool foreground = SetServerForegroundNative(Handle);
                    DiagnosticLog.Write("server.tray.activation", "foreground", foreground, "visible", Visible, "window_state", WindowState,
                        "native_minimized", IsServerIconicNative(Handle), "bounds", Bounds.ToString());
                },
                IsMinimized = delegate { return IsHandleCreated && IsServerIconicNative(Handle); },
                WorkingAreas = delegate { Screen[] screens = Screen.AllScreens; var areas = new Rectangle[screens.Length]; for (int i = 0; i < screens.Length; i++) areas[i] = screens[i].WorkingArea; return areas; },
                Post = delegate(Action action) { if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { action(); }); else action(); }
            };
            using (Stream stream = typeof(ServerForm).Assembly.GetManifestResourceStream("HorizonClient.Tray.ico"))
            {
                if (stream == null) serverTrayImage = (Icon)SystemIcons.Application.Clone();
                else using (var embedded = new Icon(stream, SystemInformation.SmallIconSize)) serverTrayImage = (Icon)embedded.Clone();
            }
            Icon = serverTrayImage;
            serverTrayMenu = new ContextMenuStrip { Name = "ServerTrayMenu", ShowImageMargin = false };
            var open = new ToolStripMenuItem(UiText.Get("Открыть", "Open")) { Name = "ServerTrayOpen" };
            var exit = new ToolStripMenuItem(UiText.Get("Выход", "Exit")) { Name = "ServerTrayExit" };
            open.Click += delegate { RestoreServerFromTray(); };
            exit.Click += delegate { ExitServerFromTray(); };
            serverTrayMenu.Items.Add(open); serverTrayMenu.Items.Add(new ToolStripSeparator()); serverTrayMenu.Items.Add(exit);
            serverTrayIcon = new NotifyIcon { Text = "Get Copy-Paste Back — Server", Icon = serverTrayImage, ContextMenuStrip = serverTrayMenu, Visible = false };
            serverTrayIcon.MouseDoubleClick += delegate(object sender, MouseEventArgs args) { if (args.Button == MouseButtons.Left) RestoreServerFromTray(); };
            serverTrayIcon.MouseClick += delegate(object sender, MouseEventArgs args) {
                DiagnosticLog.Write("server.tray.clicked", "button", args.Button, "hidden", serverHiddenToTray, "visible", Visible, "window_state", WindowState);
                if (args.Button == MouseButtons.Left) RestoreServerFromTray();
            };
            serverTrayAvailable = true;
            Resize += delegate { ObserveServerWindowState(WindowState, Bounds); };
            Move += delegate { ObserveServerWindowState(WindowState, Bounds); };
            Activated += delegate { ObserveServerWindowActivation(Visible); };
            VisibleChanged += delegate { ObserveServerWindowActivation(Visible); };
            Shown += delegate {
                if (!loadSavedSettings || IsClosing || Volatile.Read(ref serverTrayDisposed) != 0) return;
                try { serverTrayIcon.Visible = true; }
                catch (Exception error) { serverTrayAvailable = false; DiagnosticLog.Error("server.tray.unavailable", error); }
            };
        }

        private void RefreshServerTrayLanguage()
        {
            if (serverTrayMenu != null && !serverTrayMenu.IsDisposed) UiText.Apply(serverTrayMenu.Items);
        }

        private void ObserveServerWindowState(FormWindowState state, Rectangle bounds)
        {
            if (IsClosing || serverExitRequested || serverTrayRestoring || serverHiddenToTray || Volatile.Read(ref serverTrayDisposed) != 0) return;
            if (state == FormWindowState.Minimized) { QueueServerMinimizeHide(); return; }
            // WM_MOVE can precede the managed state update during minimization.
            // Never remember its parked/native-iconic coordinates as normal.
            if (ServerWindowIsMinimized()) return;
            serverRestoreState = state == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
            if (state == FormWindowState.Normal && bounds.Width > 0 && bounds.Height > 0) serverNormalBounds = bounds;
        }

        private bool ServerWindowIsMinimized()
        { return serverTrayActions.IsMinimized != null ? serverTrayActions.IsMinimized() : WindowState == FormWindowState.Minimized; }

        private void QueueServerMinimizeHide()
        {
            int revision = serverPresentationRevision;
            if (serverQueuedHideRevision == revision) return;
            serverQueuedHideRevision = revision;
            PostServerWindowAction(delegate {
                if (serverQueuedHideRevision == revision) serverQueuedHideRevision = -1;
                if (revision != serverPresentationRevision || IsClosing || IsDisposed || serverExitRequested || serverTrayRestoring || serverHiddenToTray) return;
                if (ServerWindowIsMinimized()) HideServerToTray("minimized");
            });
        }

        private void PostServerWindowAction(Action action)
        {
            try { if (serverTrayActions.Post != null) serverTrayActions.Post(action); else action(); }
            catch (InvalidOperationException error) { serverTrayRestoring = false; DiagnosticLog.Error("server.tray.dispatch_failed", error); }
        }

        private void ObserveServerWindowActivation(bool visible)
        {
            // A taskbar/native restore may make the window visible before our
            // managed hidden marker changes. Reconcile it once, without polling.
            if (visible && serverHiddenToTray && !serverTrayRestoring && !IsClosing && !IsDisposed && !ServerWindowIsMinimized()) RestoreServerFromTray();
        }

        private void HideServerToTray(string reason)
        {
            if (!serverTrayAvailable || serverHiddenToTray || IsClosing || serverExitRequested || IsDisposed) return;
            serverPresentationRevision++; serverQueuedHideRevision = -1; serverTrayRestoring = false;
            serverHiddenToTray = true;
            // Hide does not recreate the HWND or detach its clipboard listener.
            serverTrayActions.HideWindow();
            DiagnosticLog.Write("server.tray.hidden", "reason", reason, "ready", ready, "visible", Visible,
                "window_state", WindowState, "bounds", Bounds.ToString(), "normal_bounds", serverNormalBounds.ToString());
        }

        private void RestoreServerFromTray()
        {
            if (!serverTrayAvailable || serverTrayRestoring || IsClosing || IsDisposed || Volatile.Read(ref serverTrayDisposed) != 0) return;
            int revision = ++serverPresentationRevision; serverQueuedHideRevision = -1;
            Rectangle bounds = FitServerRestoreBounds(serverNormalBounds, serverTrayActions.WorkingAreas == null ? null : serverTrayActions.WorkingAreas());
            serverNormalBounds = bounds;
            serverHiddenToTray = false; serverTrayRestoring = true;
            DiagnosticLog.Write("server.tray.restore_requested", "window_state", serverRestoreState, "bounds", bounds.ToString());
            try { serverTrayActions.RestoreWindow(bounds, serverRestoreState); }
            catch { serverTrayRestoring = false; throw; }
            // Finish after the shell menu/current WM_SIZE has unwound. One
            // bounded native-state repair is allowed; never focus-poll Windows.
            PostServerWindowAction(delegate {
                if (revision != serverPresentationRevision || IsClosing || IsDisposed || Volatile.Read(ref serverTrayDisposed) != 0) return;
                try {
                    if (ServerWindowIsMinimized()) serverTrayActions.RestoreWindow(bounds, serverRestoreState);
                    if (serverTrayActions.ActivateWindow != null) serverTrayActions.ActivateWindow();
                    DiagnosticLog.Write("server.tray.restored", "window_state", WindowState, "visible", Visible,
                        "native_minimized", ServerWindowIsMinimized(), "bounds", Bounds.ToString(), "requested_bounds", bounds.ToString());
                }
                finally { serverTrayRestoring = false; }
            });
        }

        internal static Rectangle FitServerRestoreBounds(Rectangle wanted, Rectangle[] areas)
        {
            if (wanted.Width <= 0 || wanted.Height <= 0) wanted = new Rectangle(0, 0, 1060, 820);
            Rectangle chosen = Rectangle.Empty; long best = -1;
            if (areas != null) foreach (Rectangle area in areas) {
                if (area.Width <= 0 || area.Height <= 0) continue;
                Rectangle intersection = Rectangle.Intersect(wanted, area); long overlap = (long)intersection.Width * intersection.Height;
                if (overlap > best) { chosen = area; best = overlap; }
            }
            if (chosen.IsEmpty) return wanted;
            int width = Math.Min(wanted.Width, chosen.Width), height = Math.Min(wanted.Height, chosen.Height);
            return new Rectangle(Math.Max(chosen.Left, Math.Min(wanted.Left, chosen.Right - width)), Math.Max(chosen.Top, Math.Min(wanted.Top, chosen.Bottom - height)), width, height);
        }

        private bool HideInsteadOfClosing(FormClosingEventArgs args)
        {
            if (args.CloseReason != CloseReason.UserClosing || serverExitRequested || IsClosing || !serverTrayAvailable || IsDisposed) return false;
            if (!serverHiddenToTray && WindowState != FormWindowState.Minimized) ObserveServerWindowState(WindowState, Bounds);
            args.Cancel = true; HideServerToTray("window_close"); return true;
        }

        private void ExitServerFromTray()
        {
            if (serverShutdownCompleted || serverShutdownInProgress || IsClosing || IsDisposed) return;
            if (serverHiddenToTray || ServerWindowIsMinimized()) RestoreServerFromTray();
            serverExitRequested = true;
            DiagnosticLog.Write("server.tray.exit_requested", "camera_operation_pending", preparing, "keep_camera_running", keepCameraRunning.Checked);
            SetConfigurationEnabled(false);
            SetServerExitMenuEnabled(false);
            if (preparing)
            {
                UiText.SetText(StateLabel, "Завершение работы…", "Exiting…");
                UiText.SetText(DetailLabel, "Дождитесь завершения текущей настройки камеры. Установщик Windows не будет прерван.", "Waiting for camera setup to finish. The Windows installer will not be interrupted.");
                return;
            }
            ContinuePendingServerExit();
        }

        private void RememberOwnedCamera(ServerConnection connection)
        {
            ownedCameraConnection = connection != null && connection.ProcessId > 0 ? connection : null;
        }

        // Called on the UI thread after discovery/setup/remove has completed.
        // Keep that callback alive while an installer is running; closing the
        // form early would discard its result and lose the created endpoint.
        private bool ContinuePendingServerExit()
        {
            if (!serverExitRequested) return false;
            if (preparing || serverShutdownInProgress || serverShutdownCompleted || IsClosing || IsDisposed) return true;
            QuiesceServerResources();
            ServerConnection expected = ownedCameraConnection;
            bool preserveCamera = keepCameraRunning.Checked;
            bool stopCamera = !preserveCamera && (expected != null || serverMayHaveStartedCamera);
            if (preserveCamera) DiagnosticLog.Write("server.shutdown.camera_preserved", "pid", expected == null ? 0 : expected.ProcessId,
                "root", expected == null ? runtime.RootDirectory : expected.RootDirectory, "owned_helpers_pending", runtime.HasOwnedHelpers);
            if (!stopCamera && !runtime.HasOwnedHelpers)
            {
                FinishServerExit(null);
                return true;
            }
            serverShutdownInProgress = true;
            SetConfigurationEnabled(false);
            SetServerExitMenuEnabled(false);
            UiText.SetText(StateLabel, "Завершение работы…", "Exiting…");
            if (stopCamera) UiText.SetText(DetailLabel, "Остановка камеры и связанных с ней процессов…", "Stopping the camera and its related processes…");
            else UiText.SetText(DetailLabel, "Ожидание завершения фоновой настройки…", "Waiting for background setup to finish…");
            DiagnosticLog.Write("server.shutdown.helpers_wait_started", "stop_camera", stopCamera, "pid", expected == null ? 0 : expected.ProcessId,
                "root", expected == null ? runtime.RootDirectory : expected.RootDirectory, "scoped_fallback", expected == null);
            try
            {
                serverShutdownActions.Queue(delegate
                {
                    Exception failure = null;
                    try
                    {
                        runtime.WaitForOwnedHelpers();
                        if (stopCamera)
                        {
                            DiagnosticLog.Write("server.shutdown.camera_stop_started", "pid", expected == null ? 0 : expected.ProcessId);
                            bool stopped = serverShutdownActions.StopOwnedCamera(expected);
                            if (!stopped && expected != null && serverMayHaveStartedCamera)
                            {
                                // A failed setup response can leave the old
                                // connection here after its own helper replaced the
                                // PID. The fallback is scoped to this EXE's root.
                                DiagnosticLog.Write("server.shutdown.camera_stop_fallback", "previous_pid", expected.ProcessId);
                                stopped = serverShutdownActions.StopOwnedCamera(null);
                            }
                            DiagnosticLog.Write("server.shutdown.camera_stop_completed", "stopped", stopped,
                                "pid", expected == null ? 0 : expected.ProcessId);
                        }
                        else DiagnosticLog.Write("server.shutdown.helpers_wait_completed", "keep_camera_running", preserveCamera);
                    }
                    catch (Exception error) { failure = error; DiagnosticLog.Error("server.shutdown.camera_stop_failed", error); }
                    PostServerWindowAction(delegate { FinishServerExit(failure); });
                });
            }
            catch (Exception error) { DiagnosticLog.Error("server.shutdown.dispatch_failed", error); FinishServerExit(error); }
            return true;
        }

        private void FinishServerExit(Exception failure)
        {
            if (IsDisposed || IsClosing) return;
            serverShutdownInProgress = false;
            if (failure != null)
            {
                SetServerExitMenuEnabled(true);
                UiText.SetText(StateLabel, "Не удалось завершить работу камеры.", "Could not stop the camera.");
                UiText.SetText(DetailLabel, "Подробности сохранены в журнале. Нажмите «Выход» в трее ещё раз, чтобы повторить остановку.", "Details were saved to the log. Click Exit in the tray again to retry stopping the camera.");
                return;
            }
            ownedCameraConnection = null;
            serverShutdownCompleted = true;
            DiagnosticLog.Write("server.shutdown.completed");
            Close();
        }

        private void SetServerExitMenuEnabled(bool enabled)
        {
            if (serverTrayMenu == null || serverTrayMenu.IsDisposed) return;
            foreach (ToolStripItem item in serverTrayMenu.Items)
                if (item.Name == "ServerTrayExit") item.Enabled = enabled;
        }

        private void DisposeServerTray()
        {
            if (Interlocked.Exchange(ref serverTrayDisposed, 1) != 0) return;
            serverPresentationRevision++; serverQueuedHideRevision = -1; serverTrayRestoring = false;
            serverTrayAvailable = false;
            try { if (serverTrayIcon != null) { serverTrayIcon.Visible = false; serverTrayIcon.Dispose(); } }
            finally { try { if (serverTrayMenu != null) serverTrayMenu.Dispose(); } finally { if (serverTrayImage != null) serverTrayImage.Dispose(); } }
        }

        protected override void OnFormClosed(FormClosedEventArgs args)
        { DisposeServerTray(); base.OnFormClosed(args); }

        protected override void Dispose(bool disposing)
        {
            try { if (disposing) StopServerResources(); }
            finally { try { if (disposing) DisposeServerTray(); } finally { base.Dispose(disposing); } }
        }

        [DllImport("user32.dll", EntryPoint = "ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowServerWindowNative(IntPtr window, int command);
        [DllImport("user32.dll", EntryPoint = "IsIconic")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsServerIconicNative(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServerForegroundNative(IntPtr window);
    }
}
