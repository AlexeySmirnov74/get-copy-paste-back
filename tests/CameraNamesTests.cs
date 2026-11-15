using System;
using System.Collections.Generic;
using HorizonClient;

internal static class CameraNamesTests
{
    private static int checks;

    private static int Main()
    {
        try
        {
            IList<string> names = CameraNames.Names;
            Check(names.Count == 100, "The catalog must contain exactly 100 camera names.");
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                Check(!String.IsNullOrWhiteSpace(name) && name.Length <= 120 && name.Trim() == name, "Names must satisfy the server's camera-name bounds.");
                foreach (char character in name) Check(!Char.IsControl(character), "Names must not contain control characters.");
                Check(unique.Add(name), "Camera names must be distinct without case differences.");
                Check(CameraNames.Contains(name), "Every catalog entry must be recognized.");
            }
            Check(!CameraNames.Contains(null) && !CameraNames.Contains(String.Empty) && !CameraNames.Contains("Unknown camera"), "Membership must reject missing and unknown names.");
            Check(CameraNames.Contains(names[0].ToUpperInvariant()), "Membership must tolerate casing differences.");
            Rejected<NotSupportedException>(delegate { names[0] = "Changed"; }, "The name catalog must be read-only.");
            Rejected<NotSupportedException>(delegate { names.Add("Changed"); }, "The name catalog must not be extended by callers.");
            Check(CameraNames.Names[0] != "Changed" && CameraNames.Names.Count == 100, "Mutation attempts must leave the catalog unchanged.");

            uint[] values = { 0, 95, 96 };
            int position = 0;
            int selected = CameraNames.ChooseIndex(100, delegate { return values[position++]; });
            Check(position == 3 && selected == 96, "The first 96 UInt32 outcomes must be rejected to avoid modulo bias for 100 names.");
            Check(CameraNames.ChooseIndex(100, delegate { return UInt32.MaxValue; }) == 95, "The upper UInt32 boundary must remain a valid draw.");
            Check(CameraNames.ChooseIndex(1, delegate { return 0; }) == 0, "A one-item catalog must work without rejection.");
            Check(CameraNames.ChooseIndex(256, delegate { return UInt32.MaxValue; }) == 255, "Power-of-two catalogs must handle the upper boundary.");
            int[] frequencies = new int[100];
            for (uint value = 100; value < 200; value++)
            {
                uint draw = value;
                frequencies[CameraNames.ChooseIndex(100, delegate { return draw; })]++;
            }
            foreach (int frequency in frequencies) Check(frequency == 1, "Every index must have one representative in a complete accepted 100-value block.");
            Rejected<ArgumentOutOfRangeException>(delegate { CameraNames.ChooseIndex(0, delegate { return 100; }); }, "An empty catalog must be rejected.");
            Rejected<ArgumentNullException>(delegate { CameraNames.ChooseIndex(100, null); }, "A missing entropy source must be rejected.");
            for (int draw = 0; draw < 64; draw++) Check(CameraNames.Contains(CameraNames.PickRandom()), "Cryptographic choices must always belong to the catalog.");
            Console.WriteLine("Camera name checks passed: " + checks + ". 100 distinct names; no camera/driver/audio/clipboard I/O.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }
    private static void Rejected<T>(Action action, string description) where T : Exception
    {
        try { action(); } catch (T) { checks++; return; }
        throw new Exception(description);
    }
}
