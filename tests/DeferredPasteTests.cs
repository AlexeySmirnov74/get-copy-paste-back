using System;
using System.Runtime.InteropServices;
using HorizonClient;

// Pure state and ABI tests. Never constructs DeferredPaste, installs a hook,
// obtains clipboard contents, or calls SendInput on the user's desktop.
internal static class DeferredPasteTests
{
    private static readonly IntPtr Own = new IntPtr(10);
    private static readonly IntPtr Editor = new IntPtr(20);
    private static readonly IntPtr Other = new IntPtr(30);
    private static int assertions;

    public static int Main()
    {
        try
        {
            NormalKeysPass();
            DeferredPairsAndRepeats();
            PassedKeyPressKeepsPassing();
            TargetsAndCancellation();
            InjectedKeysPass();
            InputLayoutAndPlans();
            Console.WriteLine("Deferred paste controller tests passed ({0} assertions, {1}-bit ABI).",
                assertions, IntPtr.Size * 8);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void NormalKeysPass()
    {
        DeferredPasteState state = new DeferredPasteState();
        bool notify;
        Check(!Key(state, 0x56, true, false, false, true, false, false, false, Editor, out notify)
            && !notify && !state.IsPending, "Normal Ctrl+V passes when nothing is receiving");
        Check(!Key(state, 0x56, false, true, false, true, false, false, false, Editor, out notify),
            "Normal V up passes");
        Check(!Key(state, 0x56, true, false, false, false, false, false, true, Editor, out notify),
            "Plain V passes during receipt");
        Release(state);
        Check(!Key(state, 0x56, true, false, false, true, true, false, true, Editor, out notify),
            "Ctrl+Alt+V passes");
        Release(state);
        Check(!Key(state, 0x56, true, false, false, true, false, true, true, Editor, out notify),
            "Ctrl+Shift+V passes");
        Release(state);
        Check(!Key(state, 0x56, true, false, false, true, false, false, true, Own, out notify),
            "Ctrl+V in own window passes");
        Release(state);
        Check(!Key(state, 0x56, true, false, false, true, false, false, true, IntPtr.Zero, out notify),
            "No foreground target passes");
        Check(!Key(state, 0x43, true, false, false, true, false, false, true, Editor, out notify),
            "Ctrl+C passes");
        Check(!state.IsPending && !state.IsSwallowingV, "Passing keys leave no pending state");
    }

    private static void PassedKeyPressKeepsPassing()
    {
        DeferredPasteState state = new DeferredPasteState();
        bool notify;
        Check(!PressWithoutReceipt(state, Editor), "Original Ctrl+V down passes before transfer begins");
        Check(!Press(state, Editor, out notify) && !notify && !state.IsPending,
            "Receiving transition cannot suppress an already-passed V key repeat");
        Check(!Release(state), "Already-passed V retains matching key up");
        Check(Press(state, Editor, out notify) && notify, "Fresh V keypress can defer after transition");
        Release(state);

        DeferredPasteState initiallyHeld = new DeferredPasteState();
        initiallyHeld.ObserveInitialVDown(true);
        Check(!Press(initiallyHeld, Editor, out notify) && !initiallyHeld.IsPending,
            "Key held before hook installation keeps passing");
        Check(!Release(initiallyHeld), "Previously held key up passes");
        Check(Press(initiallyHeld, Editor, out notify), "Next fresh keypress can defer");
    }

    private static void DeferredPairsAndRepeats()
    {
        DeferredPasteState state = new DeferredPasteState();
        bool notify;
        Check(Press(state, Editor, out notify) && notify && state.IsPending && state.IsSwallowingV,
            "First Ctrl+V waits and requests status");
        Check(state.PendingTarget == Editor, "Foreground editor remembered");
        Check(Press(state, Editor, out notify) && !notify, "Auto-repeat swallowed without new notification");
        Check(Key(state, 0x56, true, false, false, false, true, true, false, Other, out notify)
            && !notify && state.PendingTarget == Editor,
            "Held V repeats keep original target despite modifier/focus changes");
        Check(Key(state, 0x56, false, true, false, false, false, false, false, Other, out notify)
            && !state.IsSwallowingV, "Matching V up swallowed across focus changes");
        Check(!Key(state, 0x56, false, true, false, false, false, false, false, Editor, out notify),
            "Unpaired next V up passes");
        IntPtr target;
        Check(state.TryTakeTarget(Editor, true, out target) && target == Editor && !state.IsPending,
            "Ready transfer replays once into original foreground");
        Check(!state.TryTakeTarget(Editor, true, out target), "Second completion cannot replay again");

        Press(state, Editor, out notify);
        Check(state.TryTakeTarget(Editor, true, out target), "Can finish while physical V is still held");
        Check(Key(state, 0x56, true, false, false, true, false, false, false, Editor, out notify),
            "Physical repeats stay suppressed after completion");
        Check(Key(state, 0x56, false, true, false, true, false, false, false, Editor, out notify),
            "Physical V up stays suppressed after completion");
        Check(!PressWithoutReceipt(state, Editor), "Later normal Ctrl+V passes");
    }

    private static void TargetsAndCancellation()
    {
        DeferredPasteState state = new DeferredPasteState();
        bool notify;
        IntPtr target;
        Press(state, Editor, out notify);
        Check(!state.TryTakeTarget(Other, true, out target) && !state.IsPending,
            "Changed foreground cancels replay");
        Check(!state.TryTakeTarget(Editor, true, out target), "Returning to editor cannot revive canceled paste");
        Release(state);
        Press(state, Editor, out notify);
        Check(!state.TryTakeTarget(Editor, false, out target) && !state.IsPending,
            "Closed target cancels replay");
        Release(state);
        Press(state, Editor, out notify);
        state.Cancel();
        Check(!state.IsPending && state.PendingTarget == IntPtr.Zero && state.IsSwallowingV,
            "Cancel clears request but preserves physical key pairing");
        Check(Release(state), "Canceled key up swallowed");
        Press(state, Editor, out notify);
        Check(!Key(state, 0x1b, true, false, false, false, false, false, true, Editor, out notify)
            && !state.IsPending, "Escape cancels pending paste and passes through");
        Check(Release(state), "Escape cancellation preserves key pairing");

        Press(state, Editor, out notify);
        Release(state);
        Check(Press(state, Other, out notify) && notify && state.PendingTarget == Other,
            "A fresh explicit Ctrl+V can select a new target");
        Check(state.TryTakeTarget(Other, true, out target) && target == Other, "Fresh target completes");
    }

    private static void InjectedKeysPass()
    {
        DeferredPasteState state = new DeferredPasteState();
        bool notify;
        Check(!Key(state, 0x56, true, false, true, true, false, false, true, Editor, out notify)
            && !state.IsPending, "Injected Ctrl+V never starts deferral");
        Press(state, Editor, out notify);
        Check(!Key(state, 0x56, true, false, true, true, false, false, true, Editor, out notify)
            && !notify, "Synthetic replay V down passes while physical key is held");
        Check(!Key(state, 0x56, false, true, true, true, false, false, true, Editor, out notify)
            && state.IsSwallowingV, "Synthetic replay V up preserves physical pair");
        Check(!Key(state, 0x1b, true, false, true, false, false, false, true, Editor, out notify)
            && state.IsPending, "Injected Escape does not cancel user's request");
        Check(Release(state), "Physical key up still swallowed after injected key up");
    }

    private static void InputLayoutAndPlans()
    {
        Check(Marshal.SizeOf(typeof(DeferredPaste.NativeInput)) == (IntPtr.Size == 8 ? 40 : 28),
            "INPUT size matches native ABI");
        Check(Marshal.OffsetOf(typeof(DeferredPaste.NativeInput), "Data").ToInt32() == (IntPtr.Size == 8 ? 8 : 4),
            "INPUT union native alignment");
        Check(Marshal.SizeOf(typeof(DeferredPaste.NativeMouseInput)) == (IntPtr.Size == 8 ? 32 : 24),
            "MOUSEINPUT determines union size");
        Check(Marshal.SizeOf(typeof(DeferredPaste.NativeKeyboardInput)) == (IntPtr.Size == 8 ? 24 : 16),
            "KEYBDINPUT native size");
        DeferredPaste.NativeInput[] held = DeferredPaste.BuildPasteInputs(true);
        Check(held.Length == 2, "Held physical Ctrl is left unchanged");
        AssertInput(held[0], 0x56, false);
        AssertInput(held[1], 0x56, true);
        DeferredPaste.NativeInput[] released = DeferredPaste.BuildPasteInputs(false);
        Check(released.Length == 4, "Released physical Ctrl gets balanced synthetic pair");
        AssertInput(released[0], 0x11, false);
        AssertInput(released[1], 0x56, false);
        AssertInput(released[2], 0x56, true);
        AssertInput(released[3], 0x11, true);
    }

    private static void AssertInput(DeferredPaste.NativeInput input, ushort key, bool up)
    {
        Check(input.Type == 1 && input.Data.Keyboard.VirtualKey == key
            && input.Data.Keyboard.Flags == (up ? 2u : 0u)
            && input.Data.Keyboard.ScanCode == 0 && input.Data.Keyboard.Time == 0,
            "Expected keyboard input and key direction");
    }

    private static bool Press(DeferredPasteState state, IntPtr foreground, out bool notify)
    {
        return Key(state, 0x56, true, false, false, true, false, false, true, foreground, out notify);
    }

    private static bool Release(DeferredPasteState state)
    {
        bool notify;
        return Key(state, 0x56, false, true, false, true, false, false, true, Editor, out notify);
    }

    private static bool PressWithoutReceipt(DeferredPasteState state, IntPtr foreground)
    {
        bool notify;
        return Key(state, 0x56, true, false, false, true, false, false, false, foreground, out notify);
    }

    private static bool Key(DeferredPasteState state, uint key, bool down, bool up, bool injected,
        bool control, bool alt, bool shift, bool receiving, IntPtr foreground, out bool notify)
    {
        return state.ProcessKey(key, down, up, injected, control, alt, shift, receiving, foreground, Own, out notify);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }
}
