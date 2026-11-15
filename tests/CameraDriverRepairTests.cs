using System;
using System.IO;
using HorizonClient;

internal static class CameraDriverRepairTests
{
    private static int checks;
    private static int Main()
    {
        UiText.SetLanguage(UiLanguage.Russian); // This fixture checks Russian restart guidance.
        try
        {
            var absent = CameraDriverRepair.ParseResult(Result(false, false, "null", "none"));
            Check(!absent.Changed && !absent.RestartRequired && !absent.DriverState.HasValue, "Absent driver needs no repair.");
            var stopped = CameraDriverRepair.ParseResult(Result(false, false, "1", "none"));
            Check(stopped.DriverState == 1 && !stopped.RestartRequired, "Stopped driver needs no restart.");
            var changed = CameraDriverRepair.ParseResult(Result(true, true, "4", "disable_known_conflicting_monitor_startup"));
            Check(changed.Changed && changed.RestartRequired && changed.DriverState == 4, "Startup change never pretends to unload the driver.");
            var retry = CameraDriverRepair.ParseResult(Result(false, true, "4", "disable_known_conflicting_monitor_startup"));
            Check(!retry.Changed && retry.RestartRequired, "An idempotent repeat still requires unload/restart.");
            foreach (int state in new[] { 2, 3, 4, 5, 6, 7 })
            {
                string value = Result(false, true, state.ToString(), "disable_known_conflicting_monitor_startup");
                Check(CameraDriverRepair.ParseResult(value).RestartRequired, "Pending/running/paused state remains blocked: " + state);
                Rejected(value.Replace("\"RestartRequired\":true", "\"RestartRequired\":false"));
            }
            foreach (string bad in new[] {
                null, "", " ", "{}", "null", "[]", "invalid", new String('x', 65537),
                Result(false,false,"0","none"), Result(false,false,"8","none"),
                Result(false,false,"\"1\"","none"), Result(false,false,"1.1","none"),
                Result(false,false,"true","none"), Result(false,false,"1","unknown"),
                Result(false,false,"1","none").Replace("\"Success\":true", "\"Success\":false"),
                Result(false,false,"1","none").Replace("\"Schema\":1", "\"Schema\":2"),
                Result(false,false,"1","none").Replace("\"Changed\":false", "\"Changed\":0"),
                Result(false,false,"1","none").Replace("\"RestartRequired\":false,", ""),
                Result(false,false,"1","none").Replace("\"DriverState\":1,", "") }) Rejected(bad);
            var unloaded = CameraDriverRepair.ParseResult(Result(true,false,"1","disable_known_conflicting_monitor_startup"));
            Check(unloaded.Changed && !unloaded.RestartRequired, "A separately verified stopped state permits setup.");
            var restart = new CameraDriverRestartRequiredException();
            Check(restart.Message.Contains("Перезагрузите Windows") && restart.Message.Contains("Создать камеру"), "Restart status gives the user one concrete action.");
            Console.WriteLine("Camera driver repair checks passed: " + checks + ". Pure JSON fixtures only; no Windows service, registry, driver or camera operation.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static string Result(bool changed, bool restart, string state, string operation)
    {
        return "{\"Schema\":1,\"Success\":true,\"Changed\":" + changed.ToString().ToLowerInvariant() +
            ",\"RestartRequired\":" + restart.ToString().ToLowerInvariant() + ",\"DriverState\":" + state + ",\"Operation\":\"" + operation + "\"}";
    }
    private static void Rejected(string json)
    {
        try { CameraDriverRepair.ParseResult(json); }
        catch (IOException) { checks++; return; }
        throw new Exception("Invalid or unverified repair response was accepted.");
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
