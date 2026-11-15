using System;
using System.ComponentModel;
using System.IO;
using HorizonClient;

internal static class CameraDriverSafetyTests
{
    private static int checks;

    private static int Main()
    {
        UiText.SetLanguage(UiLanguage.Russian); // This fixture checks Russian safety explanations.
        try
        {
            int queries = 0;
            CameraDriverSafety.EnsureReconfigurationAllowed(delegate { queries++; return null; });
            Check(queries == 1, "An absent driver permits reconfiguration after exactly one read-only query.");
            CameraDriverSafety.EnsureReconfigurationAllowed(delegate { queries++; return 1; });
            Check(queries == 2, "An explicitly stopped driver permits reconfiguration.");

            foreach (uint state in new uint[] { 2, 3, 4, 5, 6, 7 })
            {
                uint fixtureState = state;
                int reads = 0;
                IOException blocked = Rejected(delegate
                {
                    CameraDriverSafety.EnsureReconfigurationAllowed(delegate { reads++; return fixtureState; });
                });
                Check(reads == 1, "Pending, running or paused drivers require one query and fail closed: " + state);
                Check(blocked.Message.Contains("VBoxUSBMon") && blocked.Message.Contains("usbipd-win"), "The rejection identifies the loaded driver and its package.");
                Check(blocked.Message.Contains("синий экран") && blocked.Message.Contains("автоматически не изменялись"), "The rejection explains the observed crash and absence of automatic changes.");
            }

            foreach (uint state in new uint[] { 0, 8, UInt32.MaxValue })
            {
                uint fixtureState = state;
                IOException blocked = Rejected(delegate { CameraDriverSafety.EnsureReconfigurationAllowed(delegate { return fixtureState; }); });
                Check(blocked.Message.Contains("неизвестное состояние"), "An unknown service state is not treated as absent or stopped.");
            }

            foreach (Exception failure in new Exception[] {
                new Win32Exception(5),
                new Win32Exception(1072),
                new IOException("Synthetic query failure."),
                new UnauthorizedAccessException("Synthetic access restriction.") })
            {
                Exception fixtureFailure = failure;
                int reads = 0;
                IOException blocked = Rejected(delegate
                {
                    CameraDriverSafety.EnsureReconfigurationAllowed(delegate { reads++; throw fixtureFailure; });
                });
                Check(reads == 1, "Query errors never fall back to an optimistic retry.");
                Check(Object.ReferenceEquals(blocked.InnerException, fixtureFailure), "The native/query failure is preserved for diagnosis.");
                Check(blocked.Message.Contains("не удалось безопасно проверить") && blocked.Message.Contains("VBoxUSBMon"), "Query failures clearly explain that camera changes are blocked.");
            }

            try { CameraDriverSafety.EnsureReconfigurationAllowed(null); throw new Exception("Null query was accepted."); }
            catch (ArgumentNullException) { checks++; }

            Console.WriteLine("Camera driver safety checks passed: " + checks + ". Pure fixtures only; no Windows service, driver, device or clipboard operation was performed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static IOException Rejected(Action action)
    {
        try { action(); }
        catch (IOException error) { checks++; return error; }
        throw new Exception("Unsafe or unverified driver state did not block camera reconfiguration.");
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }
}
