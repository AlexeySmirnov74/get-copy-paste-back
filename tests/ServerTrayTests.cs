using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HorizonClient;

// Actual form lifecycle/menu handlers with presentation replaced by counters.
// The real NotifyIcon stays invisible; no camera, sound or clipboard is opened.
internal static class ServerTrayTests
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--owned-helper") { Thread.Sleep(500); return 0; }
        UiText.SetLanguage(UiLanguage.Russian); // Tray caption assertions intentionally use Russian.
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            KeepCameraExitPreservesOnlyBackend(); KeepCameraExitWaitsForSetup(); KeepCameraCanBeTurnedOff();
            ExitWaitsForRetainedHelperWithoutCamera(false); ExitWaitsForRetainedHelperWithoutCamera(true); HideRestoreAndStableMenu(); MinimizedMaximizedState(); SingleClickCancelsStaleMinimize(); RestoreBoundsAndNativeState(); PendingRestoreCannotUndoClose(); TaskbarRestoreReconcilesMarker(); ExplicitExitStopsEverything(); ExitStopsTheVerifiedAdoptedCamera(); ExitWaitsForCameraSetup(); FailedCameraStopCanBeRetried(); FailedStartupUsesOnlyTheRuntimeFallback(); StaleSetupConnectionUsesTheScopedFallback(); SystemClosingPassesThrough(); DisposeHiddenStopsWorker();
            Console.WriteLine("Server tray checks passed: " + checks + "; hidden actual lifecycle/menu callbacks, no visible tray/window, camera/audio/clipboard."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static CheckBox KeepCamera(Fixture fixture)
    {
        Control[] controls = fixture.Form.Controls.Find("KeepCameraRunning", true);
        Check(controls.Length == 1 && controls[0] is CheckBox, "Settings must expose a KeepCameraRunning checkbox.");
        return (CheckBox)controls[0];
    }

    private static void KeepCameraExitPreservesOnlyBackend()
    {
        foreach (bool partialSetup in new[] { false, true })
        using (var f = new Fixture())
        {
            Check(KeepCamera(f).Checked, "Keeping the camera running is the default Exit policy.");
            f.Remember(Camera(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preserved-camera"), 731));
            f.SetValue("serverMayHaveStartedCamera", partialSetup);
            f.Menu.Items[2].PerformClick();
            Check(f.Form.IsDisposed && f.Flag("serverShutdownCompleted") && f.ShutdownWorkCount == 0,
                "Keep-camera Exit closes the application without scheduling any camera stop or partial-setup fallback.");
            Check(f.StopCameraCalls.Count == 0 && f.Menu.IsDisposed && !f.Icon.Visible,
                "Preserving the backend must still dispose the tray and never invoke a stop helper.");
            f.CheckStopped();
        }
        using (var f = new Fixture())
        {
            KeepCamera(f).Checked = true; f.SetValue("serverMayHaveStartedCamera", true);
            f.Menu.Items[2].PerformClick();
            Check(f.Form.IsDisposed && f.StopCameraCalls.Count == 0 && f.ShutdownWorkCount == 0,
                "Keep-camera Exit never discovers/stops an unparsed camera from a partial setup result.");
            f.CheckStopped();
        }
    }

    private static void KeepCameraExitWaitsForSetup()
    {
        using (var f = new Fixture())
        {
            KeepCamera(f).Checked = true; f.SetValue("preparing", true); f.SetValue("serverMayHaveStartedCamera", true);
            f.Menu.Items[2].PerformClick();
            Check(!f.Form.IsDisposed && f.ShutdownWorkCount == 0 && !KeepCamera(f).Enabled,
                "Keep-camera Exit waits for setup and freezes its preference while the installer callback is outstanding.");
            Check(f.Closing(CloseReason.UserClosing).Cancel, "A second close cannot discard pending setup in keep-camera mode.");
            f.Remember(Camera(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preserved-setup-camera"), 813));
            f.SetValue("preparing", false); f.CallNoArgs("ContinuePendingServerExit");
            Check(f.Form.IsDisposed && f.StopCameraCalls.Count == 0 && f.ShutdownWorkCount == 0,
                "After setup returns, keep-camera Exit preserves the created camera and closes without a stop request.");
            f.CheckStopped();
        }
    }

    private static void KeepCameraCanBeTurnedOff()
    {
        using (var f = new Fixture())
        {
            KeepCamera(f).Checked = true; KeepCamera(f).Checked = false;
            f.Remember(Camera(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stop-again-camera"), 924));
            f.Menu.Items[2].PerformClick();
            Check(f.ShutdownWorkCount == 1 && !f.Form.IsDisposed, "Unchecking the preference restores verified camera shutdown.");
            f.RunShutdownWork(); f.FlushPosts();
            Check(f.Form.IsDisposed && f.StopCameraCalls.Count == 1, "The unchecked preference stops the exact remembered camera once.");
            f.CheckStopped();
        }
    }

    private static void ExitWaitsForRetainedHelperWithoutCamera(bool keep)
    {
        using (var f = new Fixture())
        using (Process child = Process.Start(new ProcessStartInfo(typeof(ServerTrayTests).Assembly.Location, "--owned-helper") { UseShellExecute = false, CreateNoWindow = true }))
        {
            try
            {
                KeepCamera(f).Checked = keep;
                object runtime = f.Get("runtime");
                var helpers = (List<Process>)runtime.GetType().GetField("ownedHelpers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(runtime);
                helpers.Add(child);
                f.Menu.Items[2].PerformClick();
                Check(!f.Form.IsDisposed && f.ShutdownWorkCount == 1, "Exit without a camera must keep the form alive until its retained helper completes.");
                f.RunShutdownWork(); f.FlushPosts();
                Check(f.Form.IsDisposed && helpers.Count == 0 && f.StopCameraCalls.Count == 0, "Helper-only exit waits and releases ownership without discovering or stopping a camera.");
            }
            finally { try { child.WaitForExit(3000); } catch (InvalidOperationException) { } }
        }
    }

    private static void HideRestoreAndStableMenu()
    {
        using (var f = new Fixture())
        {
            Check(f.Icon.Text == "Get Copy-Paste Back — Server", "The tray icon identifies the renamed product and server role.");
            Check(!f.Flag("serverHiddenToTray") && f.Form.WindowState == FormWindowState.Normal, "Startup preserves the normal window instead of launching hidden.");
            Check(!f.Icon.Visible && f.Menu.Items.Count == 3 && f.Menu.Items[0].Text == "Открыть" && f.Menu.Items[2].Text == "Выход", "The stable invisible fixture menu offers Open/Exit.");
            ContextMenuStrip menu = f.Menu; object handle = f.Form.Handle;
            f.Form.Bounds = new Rectangle(125, 160, 1080, 860); Rectangle original = f.Form.Bounds;
            var closing = f.Closing(CloseReason.UserClosing);
            Check(closing.Cancel && f.HideCalls == 1 && f.Flag("serverHiddenToTray"), "The window close button hides to the tray.");
            f.Form.Close(); Check(!f.Form.IsDisposed && f.HideCalls == 1, "The actual hidden Form.Close pipeline cancels ordinary close instead of disposing the server.");
            Check(f.Flag("ready") && !f.ClosingFlag && (int)f.Get("generation") == 11 && Object.ReferenceEquals(f.Get("current"), f.Pending), "Hiding preserves the live sender, clipboard payload and generation.");
            Check(!f.Stop.WaitOne(0) && !f.Run.Finished && f.Debounce.Enabled, "Hiding neither cancels the benchmark nor stops workers/timers.");
            f.Menu.Items[0].PerformClick();
            f.FlushPosts();
            Check(f.RestoreCalls == 1 && f.RestoredBounds == original && f.RestoredState == FormWindowState.Normal, "Open restores the previous normal bounds.");
            Check(!f.Flag("serverHiddenToTray") && Object.ReferenceEquals(f.Menu, menu) && f.Form.Handle.Equals(handle), "Restore preserves the menu and clipboard HWND.");
            f.Closing(CloseReason.UserClosing); f.Closing(CloseReason.UserClosing);
            Check(f.HideCalls == 2, "Repeated close notifications cannot repeatedly hide the same window.");
            f.DoubleClick(MouseButtons.Right); Check(f.RestoreCalls == 1, "A right double click leaves the menu's own interaction intact.");
            f.DoubleClick(MouseButtons.Left); f.FlushPosts(); Check(f.RestoreCalls == 2 && !f.Flag("serverHiddenToTray"), "A left double click uses the same restore operation.");
            Check(f.Shown == 0 && !f.Form.Visible && !f.Icon.Visible, "The fixture never presents a window or shell icon.");
        }
    }

    private static void MinimizedMaximizedState()
    {
        using (var f = new Fixture())
        {
            Rectangle normal = new Rectangle(75, 95, 1100, 870);
            f.Observe(FormWindowState.Normal, normal); f.Observe(FormWindowState.Maximized, new Rectangle(0, 0, 1900, 1000));
            f.Observe(FormWindowState.Minimized, Rectangle.Empty);
            f.FlushPosts();
            Check(f.HideCalls == 1 && f.Flag("serverHiddenToTray"), "Minimizing hides the still-running server.");
            f.Menu.Items[0].PerformClick();
            f.FlushPosts();
            Check(f.RestoredBounds == normal && f.RestoredState == FormWindowState.Maximized, "Minimizing a maximized window preserves both its restore bounds and maximized state.");
            Check(!f.Stop.WaitOne(0) && f.Flag("ready"), "Minimize/restore keeps the background transfer alive.");
        }
    }

    private static void SingleClickCancelsStaleMinimize()
    {
        using (var f = new Fixture())
        {
            f.Observe(FormWindowState.Minimized, Rectangle.Empty);
            for (int i = 0; i < 30; i++) f.Observe(FormWindowState.Minimized, Rectangle.Empty);
            Check(f.HideCalls == 0 && f.PostCount == 1, "Resize/Move coalesce one deferred hide after native minimization unwinds.");
            f.Click(MouseButtons.Right); Check(f.RestoreCalls == 0, "A single right click remains available for the context menu.");
            f.Click(MouseButtons.Left); Check(f.RestoreCalls == 1, "A single left tray click restores immediately.");
            f.FlushPosts();
            Check(f.HideCalls == 0 && !f.Flag("serverHiddenToTray") && f.Activations == 1, "A stale queued minimize cannot hide the newer explicit restore.");
            f.ObserveEventOnly(FormWindowState.Minimized, Rectangle.Empty); f.FlushPosts();
            Check(f.HideCalls == 0 && !f.Flag("serverHiddenToTray"), "A late managed minimized event cannot hide a natively restored window.");
            Check(!f.Stop.WaitOne(0) && f.Flag("ready"), "The presentation race never restarts or stops transmission.");
        }
    }

    private static void RestoreBoundsAndNativeState()
    {
        using (var f = new Fixture())
        {
            Rectangle normal = new Rectangle(100, 120, 1080, 860);
            f.Observe(FormWindowState.Normal, normal); f.NativeMinimized = true;
            f.ObserveEventOnly(FormWindowState.Normal, new Rectangle(-32000, -32000, 1080, 860));
            f.OnRestore = delegate { if (f.RestoreCalls == 1) f.Observe(FormWindowState.Minimized, Rectangle.Empty); };
            f.Click(MouseButtons.Left); f.FlushPosts();
            Check(f.RestoredBounds == normal, "Native-iconic parked coordinates cannot overwrite the last normal placement.");
            Check(f.RestoreCalls == 2 && f.Activations == 1 && !f.NativeMinimized && f.PostCount == 0, "One posted repair handles late native minimization, with one activation and no polling.");
            Check(!f.Flag("serverTrayRestoring") && f.HideCalls == 0, "Native resize callbacks inside both restore stages cannot hide the window.");
        }
        Rectangle primary = new Rectangle(0, 0, 1920, 1080), left = new Rectangle(-1920, 0, 1920, 1080);
        Rectangle negative = new Rectangle(-1800, 100, 1080, 860);
        Check(ServerForm.FitServerRestoreBounds(negative, new[] { primary, left }) == negative, "Valid negative monitor coordinates retain their original bounds.");
        Check(primary.Contains(ServerForm.FitServerRestoreBounds(new Rectangle(-32000, -32000, 1080, 860), new[] { primary })), "Parked or disconnected-monitor coordinates recover to a visible work area.");
        Rectangle small = new Rectangle(0, 0, 1280, 720);
        Check(ServerForm.FitServerRestoreBounds(new Rectangle(4000, 100, 3840, 2160), new[] { small }) == small, "A smaller current work area bounds both size and position.");
    }

    private static void PendingRestoreCannotUndoClose()
    {
        using (var f = new Fixture())
        {
            f.Click(MouseButtons.Left); f.Closing(CloseReason.UserClosing); f.FlushPosts();
            Check(f.Flag("serverHiddenToTray") && f.Activations == 0 && f.HideCalls == 1, "A close after restore supersedes the pending activation.");
            f.Click(MouseButtons.Left); int restores = f.RestoreCalls;
            f.Menu.Items[2].PerformClick(); f.FlushPosts();
            Check(f.Form.IsDisposed && f.RestoreCalls == restores && f.Activations == 0, "Exit cancels pending restore/activation callbacks instead of reopening a disposed window.");
            f.CheckStopped();
        }
    }

    private static void TaskbarRestoreReconcilesMarker()
    {
        using (var f = new Fixture())
        {
            f.Closing(CloseReason.UserClosing); IntPtr handle = f.Form.Handle;
            f.Call("ObserveServerWindowActivation", true); f.FlushPosts();
            Check(!f.Flag("serverHiddenToTray") && f.RestoreCalls == 1 && f.Activations == 1 && f.Form.Handle == handle, "A taskbar/native activation reconciles the hidden marker without recreating the HWND.");
            f.Call("ObserveServerWindowActivation", true); f.FlushPosts();
            Check(f.Activations == 1 && f.PostCount == 0, "An already restored activation cannot start a focus loop.");
        }
    }

    private static void ExplicitExitStopsEverything()
    {
        using (var f = new Fixture())
        {
            f.Closing(CloseReason.UserClosing); f.Menu.Items[2].PerformClick();
            Check(f.Flag("serverExitRequested") && f.Form.IsDisposed && f.ClosingFlag, "Tray Exit closes and disposes the hidden application instead of hiding it again.");
            f.CheckStopped();
            Check(f.Menu.IsDisposed && (int)f.Get("serverTrayDisposed") == 1 && !f.Icon.Visible, "Exit removes/disposes the icon and context menu.");
            f.Form.Dispose(); Check((int)f.Get("generation") == 12, "A second disposal cannot repeat shutdown or advance generation again.");
        }
    }

    private static void SystemClosingPassesThrough()
    {
        foreach (CloseReason reason in new[] { CloseReason.WindowsShutDown, CloseReason.TaskManagerClosing, CloseReason.ApplicationExitCall, CloseReason.FormOwnerClosing })
        using (var f = new Fixture())
        {
            f.Closing(CloseReason.UserClosing);
            Check(!f.Closing(reason).Cancel, "System/application shutdown must not be cancelled: " + reason);
            f.CheckStopped();
            f.Call("OnFormClosed", new FormClosedEventArgs(reason));
            Check(f.Menu.IsDisposed && (int)f.Get("serverTrayDisposed") == 1, "A system close also removes the tray resources.");
            Check((int)f.Get("generation") == 12 && f.HideCalls == 1, "System shutdown executes cleanup once without another hide.");
        }
    }

    private static ServerConnection Camera(string root, int processId)
    {
        return new ServerConnection {
            RootDirectory = root, KeyPath = Path.Combine(root, "data", "camera.local.key"),
            ProcessId = processId, StartTimeUtcTicks = "638980001234567890", CameraSerial = "HorizonClient-fixture",
            CameraName = "Fixture camera", Width = 1920, Height = 1080, FPS = 30, Uncompressed = true
        };
    }

    private static void ExitStopsTheVerifiedAdoptedCamera()
    {
        using (var f = new Fixture())
        {
            KeepCamera(f).Checked = false;
            ServerConnection adopted = Camera(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "legacy-camera"), 317);
            f.Remember(adopted);
            f.Closing(CloseReason.UserClosing);
            Check(f.StopCameraCalls.Count == 0, "Hiding an adopted camera never stops its backend.");
            f.Menu.Items[2].PerformClick();
            Check(f.Flag("serverExitRequested") && f.Flag("serverShutdownInProgress") && !f.Form.IsDisposed,
                "True Exit waits for verified backend shutdown before disposing the form.");
            Check(f.Stop.WaitOne(0) && !f.Flag("ready") && f.ShutdownWorkCount == 1 && !f.Menu.Items[2].Enabled,
                "Exit first stops local transmission and schedules one backend stop without another user command.");
            f.CallNoArgs("ExitServerFromTray");
            Check(f.ShutdownWorkCount == 1, "Repeated Exit cannot start overlapping backend stops.");
            f.RunShutdownWork(); f.FlushPosts();
            Check(f.StopCameraCalls.Count == 1 && Object.ReferenceEquals(f.StopCameraCalls[0], adopted),
                "The verified actual legacy root, PID, start time, serial and key are passed intact to the scoped runtime stop.");
            Check(f.Form.IsDisposed && f.Flag("serverShutdownCompleted"), "The server closes only after the backend stop completes.");
            f.CheckStopped();
        }
    }

    private static void ExitWaitsForCameraSetup()
    {
        using (var f = new Fixture())
        {
            KeepCamera(f).Checked = false;
            f.SetValue("preparing", true); f.SetValue("serverMayHaveStartedCamera", true);
            f.Menu.Items[2].PerformClick();
            Check(!f.Form.IsDisposed && !f.ClosingFlag && f.ShutdownWorkCount == 0,
                "Exit during setup keeps its callback and installer alive without starting a competing stop.");
            Check(f.Closing(CloseReason.UserClosing).Cancel && !f.Form.IsDisposed,
                "A second window close while setup finishes cannot discard the queued Exit.");
            ServerConnection created = Camera(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "created-camera"), 813);
            f.Remember(created); f.SetValue("preparing", false); f.CallNoArgs("ContinuePendingServerExit");
            Check(f.ShutdownWorkCount == 1 && !f.Form.IsDisposed, "The setup completion continues the pending Exit rather than reopening capture.");
            f.RunShutdownWork(); f.FlushPosts();
            Check(f.Form.IsDisposed && f.StopCameraCalls.Count == 1 && Object.ReferenceEquals(f.StopCameraCalls[0], created),
                "Exit includes the camera created by the operation that was already running.");
        }
    }

    private static void FailedCameraStopCanBeRetried()
    {
        using (var f = new Fixture())
        {
            KeepCamera(f).Checked = false;
            f.Remember(Camera(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "retry-camera"), 924));
            f.OnStopCamera = delegate { throw new IOException("Synthetic stop failure."); };
            f.Menu.Items[2].PerformClick(); f.RunShutdownWork(); f.FlushPosts();
            Check(!f.Form.IsDisposed && !f.ClosingFlag && !f.Flag("serverShutdownInProgress") && f.Menu.Items[2].Enabled,
                "A failed stop keeps a retryable application instead of silently leaving its backend behind.");
            Check(!f.Flag("ready") && f.Stop.WaitOne(0), "A stop failure does not resume clipboard transmission.");
            f.CallNoArgs("CreateCamera"); f.CallNoArgs("DeleteCamera"); f.CallNoArgs("ApplyVideoMode");
            Check(f.ShutdownWorkCount == 0, "No new camera operation can start after Exit has been requested.");
            f.OnStopCamera = delegate { return true; };
            f.Menu.Items[2].PerformClick(); f.RunShutdownWork(); f.FlushPosts();
            Check(f.Form.IsDisposed && f.StopCameraCalls.Count == 2, "A second explicit Exit retries the exact same verified endpoint once.");
        }
    }

    private static void FailedStartupUsesOnlyTheRuntimeFallback()
    {
        using (var f = new Fixture())
        {
            KeepCamera(f).Checked = false;
            f.SetValue("serverMayHaveStartedCamera", true);
            f.Menu.Items[2].PerformClick();
            Check(f.ShutdownWorkCount == 1 && !f.Form.IsDisposed, "A failed setup without a parsed connection still runs scoped runtime cleanup.");
            f.RunShutdownWork(); f.FlushPosts();
            Check(f.StopCameraCalls.Count == 1 && f.StopCameraCalls[0] == null && f.Form.IsDisposed,
                "Missing setup results request the runtime's own-root-only fallback, never a guessed PID or process name.");
        }
    }

    private static void StaleSetupConnectionUsesTheScopedFallback()
    {
        using (var f = new Fixture())
        {
            KeepCamera(f).Checked = false;
            ServerConnection old = Camera(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "old-camera"), 921);
            f.Remember(old); f.SetValue("serverMayHaveStartedCamera", true);
            f.OnStopCamera = delegate(ServerConnection connection) { return connection == null; };
            f.Menu.Items[2].PerformClick(); f.RunShutdownWork(); f.FlushPosts();
            Check(f.StopCameraCalls.Count == 2 && Object.ReferenceEquals(f.StopCameraCalls[0], old) && f.StopCameraCalls[1] == null && f.Form.IsDisposed,
                "A replaced setup PID first rejects the stale connection, then asks only for this EXE's verified own-root fallback.");
        }
        using (var f = new Fixture())
        {
            f.Remember(Camera(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "untouched-legacy-camera"), 728));
            KeepCamera(f).Checked = false;
            f.OnStopCamera = delegate { return false; };
            f.Menu.Items[2].PerformClick(); f.RunShutdownWork(); f.FlushPosts();
            Check(f.StopCameraCalls.Count == 1 && f.Form.IsDisposed,
                "An adopted process replaced elsewhere does not authorize discovery or stopping a different installation.");
        }
    }

    private static void DisposeHiddenStopsWorker()
    {
        using (var f = new Fixture())
        {
            f.Observe(FormWindowState.Minimized, Rectangle.Empty); f.FlushPosts(); f.Form.Dispose(); f.CheckStopped();
            Check(f.Form.IsDisposed && f.Menu.IsDisposed && !f.Icon.Visible, "Direct disposal while hidden cannot leave tray resources or a waiting sender behind.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ServerForm Form;
        internal readonly ManualResetEvent Stop = new ManualResetEvent(false), Exited = new ManualResetEvent(false);
        internal readonly byte[] Pending = { 1, 2, 3 };
        internal readonly FileBenchmarkRunner Run = new FileBenchmarkRunner(Guid.NewGuid(), new[] { VideoProfile.Fast }, 1024, 1, 180000, delegate { return 0; });
        internal int HideCalls, RestoreCalls, Shown, Activations;
        internal bool NativeMinimized;
        internal Action OnRestore;
        private readonly Queue<Action> posted = new Queue<Action>();
        private readonly Queue<Action> shutdownWork = new Queue<Action>();
        internal readonly List<ServerConnection> StopCameraCalls = new List<ServerConnection>();
        internal Func<ServerConnection, bool> OnStopCamera;
        internal int ShutdownWorkCount { get { return shutdownWork.Count; } }
        internal int PostCount { get { return posted.Count; } }
        internal Rectangle RestoredBounds;
        internal FormWindowState RestoredState;
        internal NotifyIcon Icon { get { return (NotifyIcon)Get("serverTrayIcon"); } }
        internal ContextMenuStrip Menu { get { return (ContextMenuStrip)Get("serverTrayMenu"); } }
        internal System.Windows.Forms.Timer Debounce { get { return (System.Windows.Forms.Timer)Get("debounce"); } }
        internal bool ClosingFlag { get { return (bool)typeof(BridgeForm).GetField("IsClosing", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Form); } }
        internal Fixture()
        {
            Form = new ServerForm(null, false, delegate { throw new Exception("Unexpected camera open."); }, new ServerClipboardServices {
                Sequence = delegate { return 17; }, Capture = delegate { throw new Exception("Unexpected clipboard read."); }, Accepted = delegate { throw new Exception("Unexpected sound."); }
            });
            Check(Object.ReferenceEquals(Form.Icon, Icon.Icon), "The server window and tray use the same application icon instance.");
            Form.Shown += delegate { Shown++; }; IntPtr ignored = Form.Handle;
            Set("serverTrayActions", new ServerTrayActions {
                HideWindow = delegate { HideCalls++; },
                RestoreWindow = delegate(Rectangle bounds, FormWindowState state) { RestoreCalls++; RestoredBounds = bounds; RestoredState = state; Observe(FormWindowState.Normal, Rectangle.Empty); if (OnRestore != null) OnRestore(); },
                ActivateWindow = delegate { Activations++; }, IsMinimized = delegate { return NativeMinimized; },
                WorkingAreas = delegate { return new[] { new Rectangle(0, 0, 1920, 1080) }; },
                Post = delegate(Action action) { posted.Enqueue(action); }
            });
            Set("serverShutdownActions", new ServerShutdownActions {
                Queue = delegate(Action action) { shutdownWork.Enqueue(action); },
                StopOwnedCamera = delegate(ServerConnection connection) {
                    StopCameraCalls.Add(connection);
                    return OnStopCamera == null || OnStopCamera(connection);
                }
            });
            Set("ready", true); Set("generation", 11); Set("current", Pending); Set("speedTest", Run); Set("stopping", Stop);
            var worker = new Thread(delegate() { Stop.WaitOne(); Exited.Set(); }) { IsBackground = true };
            Set("worker", worker); worker.Start(); Debounce.Start();
        }
        internal object Get(string name) { return typeof(ServerForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Form); }
        private void Set(string name, object value) { typeof(ServerForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Form, value); }
        internal void SetValue(string name, object value) { Set(name, value); }
        internal void CallNoArgs(string name) { typeof(ServerForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Form, null); }
        internal void Remember(ServerConnection connection) { Call("RememberOwnedCamera", connection); }
        internal void RunShutdownWork() { if (shutdownWork.Count != 1) throw new Exception("Expected exactly one queued shutdown worker."); shutdownWork.Dequeue()(); }
        internal bool Flag(string name) { return (bool)Get(name); }
        internal void Call(string name, object argument) { typeof(ServerForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Form, new[] { argument }); }
        internal FormClosingEventArgs Closing(CloseReason reason) { var args = new FormClosingEventArgs(reason, false); Call("OnFormClosing", args); return args; }
        internal void Observe(FormWindowState state, Rectangle bounds) { NativeMinimized = state == FormWindowState.Minimized; ObserveEventOnly(state, bounds); }
        internal void ObserveEventOnly(FormWindowState state, Rectangle bounds) { typeof(ServerForm).GetMethod("ObserveServerWindowState", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Form, new object[] { state, bounds }); }
        internal void DoubleClick(MouseButtons button) { typeof(NotifyIcon).GetMethod("OnMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Icon, new object[] { new MouseEventArgs(button, 2, 0, 0, 0) }); }
        internal void Click(MouseButtons button) { typeof(NotifyIcon).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Icon, new object[] { new MouseEventArgs(button, 1, 0, 0, 0) }); }
        internal void FlushPosts() { int count = 0; while (posted.Count > 0) { if (++count > 20) throw new Exception("Unbounded presentation callback loop."); posted.Dequeue()(); } }
        internal void CheckStopped()
        {
            Check(Stop.WaitOne(0) && Exited.WaitOne(1000), "Full shutdown signals and joins the synthetic waiting sender.");
            Check(!Flag("ready") && ClosingFlag && Get("current") == null && Run.Finished, "Full shutdown retires sender state, clipboard data and benchmark.");
            Check(!Debounce.Enabled && !((System.Windows.Forms.Timer)Get("benchmarkUiTimer")).Enabled, "Full shutdown stops both UI timers.");
        }
        public void Dispose() { Form.Dispose(); FlushPosts(); Stop.Set(); Exited.WaitOne(1000); Stop.Dispose(); Exited.Dispose(); }
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
