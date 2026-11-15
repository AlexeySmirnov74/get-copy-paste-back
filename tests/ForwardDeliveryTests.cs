using System;
using HorizonClient;

internal static class ForwardDeliveryTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static int Main()
    {
        try
        {
            Announcements(); ConfirmLatestCopy();
            Console.WriteLine("Forward delivery checks passed: " + checks + ". Synthetic announcements/receipts only; no clipboard, capture or sound."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Announcements()
    {
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid(); BridgeHello hello;
        byte[] legacy = BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0, "Legacy camera");
        Check(BridgeControl.TryHello(BridgeControl.Single(session, legacy), out hello) && hello.ForwardTransfer == Guid.Empty && hello.ForwardEncodedBytes == 0, "Legacy hello remains supported.");
        foreach (int bytes in new[] { 1, 10240, VisualProtocol.MaxTransferBytes })
        foreach (string name in new[] { "", "Веб-камера", new string('a', 170), new string('я', 85), new string('я', 86) })
        {
            byte[] wire = BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0, name, transfer, bytes);
            Check(wire.Length >= 82 && wire.Length <= 252, "Hello fits compatible256-byte packet.");
            Check(BridgeControl.TryHello(BridgeControl.Single(session, wire), out hello), "Forward hello parsed.");
            Check(hello.Session == session && hello.ForwardTransfer == transfer && hello.ForwardEncodedBytes == bytes && hello.VideoTest == Guid.Empty, "Forward identity preserved.");
            if (name.Length < 170 && name != new string('я', 86)) Check(hello.CameraName == name, "UTF8 camera name preserved.");
        }
        byte[] active = BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0, "", transfer, 10240);
        foreach (int offset in new[] { 24, 40, 56, 80, 81 })
        {
            byte[] bad = (byte[])active.Clone(); bad[offset] = 255;
            Check(!BridgeControl.TryHello(BridgeControl.Single(session, bad), out hello), "Invalid forward metadata rejected: " + offset);
        }
        byte[] zeroSize = (byte[])active.Clone(); Array.Clear(zeroSize, 76, 4);
        Check(!BridgeControl.TryHello(BridgeControl.Single(session, zeroSize), out hello), "Zero advertised size rejected.");
        byte[] excessiveSize = (byte[])active.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(VisualProtocol.MaxTransferBytes + 1), 0, excessiveSize, 76, 4);
        Check(!BridgeControl.TryHello(BridgeControl.Single(session, excessiveSize), out hello), "Oversized advertised size rejected.");
        byte[] empty = (byte[])active.Clone(); Array.Clear(empty, 60, 16);
        Check(!BridgeControl.TryHello(BridgeControl.Single(session, empty), out hello), "Empty advertised transfer rejected.");
        Check(!BridgeControl.TryHello(BridgeControl.Single(Guid.NewGuid(), active), out hello), "Wrong transport/session rejected.");
        byte[] idle = BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0, "", Guid.Empty, 0);
        Check(BridgeControl.TryHello(BridgeControl.Single(session, idle), out hello) && hello.ForwardTransfer == Guid.Empty, "Removing advertisement signals completion to client.");
        try { BridgeControl.Hello(session, Guid.NewGuid(), Guid.NewGuid(), 5, "", transfer, 10); Check(false, "Forward announcement mixed with benchmark."); } catch (ArgumentException) { checks++; }
        try { BridgeControl.Hello(session, Guid.Empty, Guid.Empty, 0, "", Guid.Empty, 10); Check(false, "Size without transfer accepted."); } catch (ArgumentException) { checks++; }
    }

    private static void ConfirmLatestCopy()
    {
        Guid session = Guid.NewGuid(), transfer = Guid.NewGuid(); var state = new ForwardDeliveryState();
        var receipt = new DeliveryReceipt { Session = session, Transfer = transfer, EncodedBytes = 10240 };
        Check(!state.Confirm(receipt, 5, 100), "No pending data cannot complete.");
        state.Expect(session, transfer, 10240, 5, 100);
        Check(!state.Confirm(null, 5, 100), "Null receipt ignored.");
        Check(!state.Confirm(new DeliveryReceipt { Session = Guid.NewGuid(), Transfer = transfer, EncodedBytes = 10240 }, 5, 100), "Foreign session ignored.");
        Check(!state.Confirm(new DeliveryReceipt { Session = session, Transfer = Guid.NewGuid(), EncodedBytes = 10240 }, 5, 100), "Other transfer ignored.");
        Check(!state.Confirm(new DeliveryReceipt { Session = session, Transfer = transfer, EncodedBytes = 10239 }, 5, 100), "Partial/wrong size ignored.");
        Check(!state.Confirm(receipt, 6, 100), "New command invalidates prior completion before encoding.");
        Check(!state.Confirm(receipt, 5, 101), "New clipboard sequence invalidates prior completion before WM processing.");
        Check(state.Confirm(receipt, 5, 100), "Exact receipt completes transfer.");
        Check(!state.Confirm(receipt, 5, 100), "Repeated audio cannot trigger a second success sound.");
        Guid recopy = Guid.NewGuid(); state.Expect(session, recopy, 10240, 6, 101);
        Check(!state.Confirm(receipt, 6, 101), "Copying identical bytes again requires its new transfer ID.");
        Check(state.Confirm(new DeliveryReceipt { Session = session, Transfer = recopy, EncodedBytes = 10240 }, 6, 101), "Identical second copy can complete once.");
        state.Expect(Guid.NewGuid(), Guid.NewGuid(), 1, 7, 102);
        Check(!state.Confirm(receipt, 7, 102), "Reconnection cannot accept prior session receipt.");
    }
}
