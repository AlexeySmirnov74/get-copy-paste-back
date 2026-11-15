using System;
using HorizonClient;

internal static class AudioDiagnosticProbeTests
{
    private static int checks;
    private static int Main()
    {
        try
        {
            Guid id = Guid.NewGuid();
            foreach (AudioModemKind kind in new[] { AudioModemKind.Fsk, AudioModemKind.Quiet, AudioModemKind.Aicodix })
                foreach (long ticks in new[] { 1L, DateTime.UtcNow.Ticks, DateTime.MaxValue.Ticks })
                {
                    byte[] bytes = AudioDiagnosticProbe.Encode(id, kind, ticks); AudioDiagnosticProbe probe;
                    Check(bytes.Length == 40, "A probe fits exactly 40 bytes and one audio packet.");
                    Check(AudioDiagnosticProbe.TryDecode(bytes, out probe) && probe.Id == id && probe.Modem == kind && probe.SentUtcTicks == ticks,
                        "Probe preserves its stable identity, selected modem and UTC timestamp.");
                    for (int offset = 0; offset < bytes.Length; offset++)
                        for (int bit = 0; bit < 8; bit++)
                        {
                            bytes[offset] ^= (byte)(1 << bit);
                            Check(!AudioDiagnosticProbe.TryDecode(bytes, out probe) && probe == null, "Every single-bit corruption is rejected.");
                            bytes[offset] ^= (byte)(1 << bit);
                        }
                }
            AudioDiagnosticProbe invalid;
            Check(!AudioDiagnosticProbe.TryDecode(null, out invalid) && invalid == null, "Null input rejected.");
            byte[] reference = AudioDiagnosticProbe.Encode(id, AudioModemKind.Fsk, DateTime.UtcNow.Ticks);
            for (int length = 0; length < reference.Length; length++)
            {
                byte[] truncated = new byte[length]; Array.Copy(reference, truncated, length);
                Check(!AudioDiagnosticProbe.TryDecode(truncated, out invalid), "Truncated frame rejected.");
            }
            byte[] trailing = new byte[41]; Array.Copy(reference, trailing, reference.Length);
            Check(!AudioDiagnosticProbe.TryDecode(trailing, out invalid), "Trailing bytes rejected.");
            Throws(delegate { AudioDiagnosticProbe.Encode(Guid.Empty, AudioModemKind.Fsk, 1); });
            Throws(delegate { AudioDiagnosticProbe.Encode(id, (AudioModemKind)3, 1); });
            Throws(delegate { AudioDiagnosticProbe.Encode(id, (AudioModemKind)(-1), 1); });
            Throws(delegate { AudioDiagnosticProbe.Encode(id, AudioModemKind.Fsk, 0); });
            Throws(delegate { AudioDiagnosticProbe.Encode(id, AudioModemKind.Fsk, -1); });
            Throws(delegate { AudioDiagnosticProbe.Encode(id, AudioModemKind.Fsk, DateTime.MaxValue.Ticks + 1); });
            Console.WriteLine("Audio diagnostic probe checks passed: " + checks + " assertions (all modems, timestamp bounds, identity, CRC corruption and exact wire length). No devices or clipboard opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Throws(Action action)
    { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception("Invalid probe metadata must not be encoded."); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
