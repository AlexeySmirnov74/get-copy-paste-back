using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HorizonClient
{
    // Construct and use on the receiver's STA/UI thread. The hook never reads
    // clipboard data; it only postpones one explicit Ctrl+V while data arrives.
    public sealed class DeferredPaste : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int VkControl = 0x11;
        private const int VkShift = 0x10;
        private const int VkMenu = 0x12;
        private readonly IntPtr ownWindow;
        private readonly Func<bool> isReceiving;
        private readonly Action onDeferred;
        private readonly SynchronizationContext uiContext;
        private readonly HookProcedure hookProcedure;
        private readonly DeferredPasteState state = new DeferredPasteState();
        private IntPtr hook;
        private volatile bool disposed;
        private int notificationQueued;

        public DeferredPaste(IntPtr ownWindow, Func<bool> isReceiving, Action onDeferred)
        {
            if (isReceiving == null) throw new ArgumentNullException("isReceiving");
            if (onDeferred == null) throw new ArgumentNullException("onDeferred");
            this.ownWindow = ownWindow;
            this.isReceiving = isReceiving;
            this.onDeferred = onDeferred;
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            state.ObserveInitialVDown(IsKeyDown(0x56));
            hookProcedure = HookCallback;
            hook = SetWindowsHookEx(WhKeyboardLl, hookProcedure, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), UiText.Get("Не удалось включить ожидание Ctrl+V.", "Could not enable waiting for Ctrl+V."));
        }

        public bool IsPending { get { return state.IsPending; } }

        public bool Complete()
        {
            if (disposed) return false;
            IntPtr target;
            IntPtr pending = state.PendingTarget;
            if (!state.TryTakeTarget(GetForegroundWindow(), pending != IntPtr.Zero && IsWindow(pending), out target))
                return false;

            // Do not change focus or alter modifiers currently held by the user.
            // A new Alt/Shift chord cancels the deferred replay; the clipboard is
            // already ready and a later ordinary Ctrl+V remains available.
            if (IsKeyDown(VkMenu) || IsKeyDown(VkShift) || GetForegroundWindow() != target)
                return false;
            bool physicalControlHeld = IsKeyDown(VkControl);
            NativeInput[] inputs = BuildPasteInputs(physicalControlHeld);
            uint inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeInput)));
            if (inserted == inputs.Length) return true;

            // Never retry a paste after a partial SendInput. Release only the
            // keys this attempt may have pressed, so a partial insertion cannot
            // leave a synthetic Ctrl/V down.
            if (inserted != 0)
            {
                NativeInput[] release = physicalControlHeld
                    ? new NativeInput[] { KeyInput(0x56, true) }
                    : new NativeInput[] { KeyInput(0x56, true), KeyInput(VkControl, true) };
                SendInput((uint)release.Length, release, Marshal.SizeOf(typeof(NativeInput)));
            }
            return inserted >= (physicalControlHeld ? 1u : 2u);
        }

        public void Cancel()
        {
            state.Cancel();
        }

        private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && !disposed)
            {
                try
                {
                    int kind = message.ToInt32();
                    bool down = kind == WmKeyDown || kind == WmSysKeyDown;
                    bool up = kind == WmKeyUp || kind == WmSysKeyUp;
                    if (down || up)
                    {
                        NativeKeyboardHook key = (NativeKeyboardHook)Marshal.PtrToStructure(data, typeof(NativeKeyboardHook));
                        bool injected = (key.Flags & 0x12) != 0;
                        bool receiving = false;
                        if (!injected && down && key.VirtualKey == 0x56 && !state.IsSwallowingV)
                        {
                            try { receiving = isReceiving(); }
                            catch { receiving = false; }
                        }
                        bool notify;
                        bool swallow = state.ProcessKey(key.VirtualKey, down, up, injected,
                            IsKeyDown(VkControl), IsKeyDown(VkMenu), IsKeyDown(VkShift),
                            receiving, GetForegroundWindow(), ownWindow, out notify);
                        if (notify) QueueNotification();
                        if (swallow) return new IntPtr(1);
                    }
                }
                catch
                {
                    // Exceptions must never escape the unmanaged hook callback.
                }
            }
            return CallNextHookEx(hook, code, message, data);
        }

        private void QueueNotification()
        {
            if (Interlocked.Exchange(ref notificationQueued, 1) != 0) return;
            try
            {
                uiContext.Post(delegate(object unused) {
                    Interlocked.Exchange(ref notificationQueued, 0);
                    if (disposed || !state.IsPending) return;
                    try { onDeferred(); }
                    catch { }
                }, null);
            }
            catch
            {
                Interlocked.Exchange(ref notificationQueued, 0);
            }
        }

        internal static NativeInput[] BuildPasteInputs(bool physicalControlHeld)
        {
            return physicalControlHeld
                ? new NativeInput[] { KeyInput(0x56, false), KeyInput(0x56, true) }
                : new NativeInput[] { KeyInput(VkControl, false), KeyInput(0x56, false),
                    KeyInput(0x56, true), KeyInput(VkControl, true) };
        }

        private static NativeInput KeyInput(int key, bool up)
        {
            NativeInput input = new NativeInput();
            input.Type = 1;
            input.Data.Keyboard.VirtualKey = (ushort)key;
            input.Data.Keyboard.Flags = up ? 2u : 0u;
            return input;
        }

        private static bool IsKeyDown(int key)
        {
            return (GetAsyncKeyState(key) & 0x8000) != 0;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool explicitDispose)
        {
            disposed = true;
            state.Cancel();
            IntPtr oldHook = Interlocked.Exchange(ref hook, IntPtr.Zero);
            if (oldHook != IntPtr.Zero) UnhookWindowsHookEx(oldHook);
        }

        ~DeferredPaste() { Dispose(false); }

        private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeKeyboardHook
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        // MOUSEINPUT is deliberately in the union even though only keyboard
        // input is sent: it determines INPUT's required 40-byte x64 ABI size.
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeInput
        {
            public uint Type;
            public NativeInputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct NativeInputUnion
        {
            [FieldOffset(0)] public NativeMouseInput Mouse;
            [FieldOffset(0)] public NativeKeyboardInput Keyboard;
            [FieldOffset(0)] public NativeHardwareInput Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeMouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeKeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeHardwareInput
        {
            public uint Message;
            public ushort ParamLow;
            public ushort ParamHigh;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int id, HookProcedure callback, IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    }

    // Pure controller, independently tested without installing a hook or
    // sending any real keyboard input.
    internal sealed class DeferredPasteState
    {
        private bool physicalVDown;
        public bool IsPending { get; private set; }
        public bool IsSwallowingV { get; private set; }
        public IntPtr PendingTarget { get; private set; }

        internal void ObserveInitialVDown(bool down) { physicalVDown = down; }

        public bool ProcessKey(uint key, bool down, bool up, bool injected, bool control,
            bool alt, bool shift, bool receiving, IntPtr foreground, IntPtr ownWindow, out bool notify)
        {
            notify = false;
            if (injected) return false;
            if (key == 0x1b && down) Cancel();
            if (key != 0x56) return false;
            if (up)
            {
                bool swallowed = IsSwallowingV;
                IsSwallowingV = false;
                physicalVDown = false;
                return swallowed;
            }
            if (!down) return false;
            if (physicalVDown) return IsSwallowingV;
            physicalVDown = true;
            if (!receiving || !control || alt || shift || foreground == IntPtr.Zero || foreground == ownWindow)
                return false;

            IsSwallowingV = true;
            notify = !IsPending || PendingTarget != foreground;
            IsPending = true;
            PendingTarget = foreground;
            return true;
        }

        public bool TryTakeTarget(IntPtr foreground, bool targetIsWindow, out IntPtr target)
        {
            target = PendingTarget;
            bool replay = IsPending && targetIsWindow && target != IntPtr.Zero && target == foreground;
            Cancel();
            return replay;
        }

        public void Cancel()
        {
            IsPending = false;
            PendingTarget = IntPtr.Zero;
            // A suppressed physical V down still needs its matching up
            // suppressed, even if Escape/cancellation/completion happened.
        }
    }
}
