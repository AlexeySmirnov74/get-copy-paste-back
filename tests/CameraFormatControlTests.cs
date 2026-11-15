using System;
using HorizonClient;

internal static class CameraFormatControlTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static int Main()
    {
        try
        {
            Guid session = Guid.NewGuid(), revision = Guid.NewGuid();
            foreach (int[] mode in new[] { new[] { 640, 480 }, new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 } })
            {
                byte[] wire = BridgeControl.CameraFormat(session, revision, mode[0], mode[1], 30, 2);
                BridgeCameraFormat parsed;
                Check(wire.Length == 64 && BridgeControl.TryCameraFormat(BridgeControl.Single(session, wire), out parsed) &&
                    parsed.Session == session && parsed.Revision == revision && parsed.Width == mode[0] && parsed.Height == mode[1] && parsed.FPS == 30 && parsed.AudioRetries == 2,
                    "The camera's source mode, generation and retry policy survive as a bounded distinct control packet.");
                for (int index = 0; index < wire.Length; index++) for (int bit = 0; bit < 8; bit++)
                {
                    byte[] corrupt = (byte[])wire.Clone(); corrupt[index] ^= (byte)(1 << bit);
                    Check(!BridgeControl.TryCameraFormat(BridgeControl.Single(session, corrupt), out parsed), "A damaged source generation/mode cannot trigger capture reconfiguration.");
                }
                Check(!BridgeControl.TryCameraFormat(BridgeControl.Single(Guid.NewGuid(), wire), out parsed), "Format metadata cannot switch another video session.");
                Packet split = BridgeControl.Single(session, wire); split.Count = 2;
                Check(!BridgeControl.TryCameraFormat(split, out parsed), "Only complete control packets can configure the source.");
                BridgeHello hello;
                Check(!BridgeControl.TryHello(BridgeControl.Single(session, wire), out hello), "Source metadata is never mistaken for a clipboard/session Hello.");
            }
            foreach (int retries in new[] { 0, 10 })
            {
                BridgeCameraFormat parsed;
                Check(BridgeControl.TryCameraFormat(BridgeControl.Single(session, BridgeControl.CameraFormat(session, revision, 1920, 1080, 15, retries)), out parsed) && parsed.AudioRetries == retries,
                    "The retry-policy boundaries and negotiated15FPS survive unchanged.");
            }
            foreach (Action invalid in new Action[] {
                () => BridgeControl.CameraFormat(Guid.Empty, revision, 1920, 1080, 30),
                () => BridgeControl.CameraFormat(session, Guid.Empty, 1920, 1080, 30),
                () => BridgeControl.CameraFormat(session, revision, 3840, 2160, 60),
                () => BridgeControl.CameraFormat(session, revision, 2560, 1600, 30),
                () => BridgeControl.CameraFormat(session, revision, 7680, 4320, 8),
                () => BridgeControl.CameraFormat(session, revision, 1921, 1080, 30),
                () => BridgeControl.CameraFormat(session, revision, 1920, 1080, 0),
                () => BridgeControl.CameraFormat(session, revision, 1920, 1080, 30, -1),
                () => BridgeControl.CameraFormat(session, revision, 1920, 1080, 30, 11) })
            {
                bool rejected = false; try { invalid(); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid or unsupported source parameters are rejected before the video command is encoded.");
            }
            Console.WriteLine("Camera source control checks passed: " + checks + "; bounded modes, generation/retries, all single-bit errors and envelope isolation. Synthetic bytes only."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
