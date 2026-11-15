using System;
using System.Collections.Generic;
using System.IO;
using HorizonClient;

internal static class DriverInstallerTests
{
    private static int checks;

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--install-wait-fixture") { System.Threading.Thread.Sleep(180); return 7; }
        UiText.SetLanguage(UiLanguage.Russian); // This fixture checks Russian installer guidance.
        string fixture = Path.Combine(Path.GetTempPath(), "HorizonDriverTests-" + Guid.NewGuid().ToString("N"));
        string packageDirectory = Path.Combine(fixture, "runtime", "drivers");
        string installer = Path.Combine(packageDirectory, DriverInstaller.InstallerFileName);
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(installer, "synthetic installer; must never be executed");
        try
        {
            Check(DriverInstaller.ParseReadiness("{\"ready\":true}"), "Boolean readiness must be parsed.");
            Check(DriverInstaller.ParseReadiness(" \r\n{ \"ready\": true }\n"), "Whitespace is valid JSON.");
            foreach (string malformed in new[] { "", "null", "[]", "true", "not JSON", "{\"ready\":false}", "{\"ready\":\"true\"}", "{\"other\":{\"ready\":true}}", "{\"ready\":1}", new string('x', 4097) })
                Check(!DriverInstaller.ParseReadiness(malformed), "Malformed or non-boolean readiness must not skip setup.");

            DriverInstallServices ready = Services();
            ready.IsDriverReady = delegate { return true; };
            ready.IsAdministrator = delegate { throw new Exception("Admin probe must not run for a ready driver."); };
            ready.VerifyInstaller = delegate { throw new Exception("Verification must not run for a ready driver."); };
            ready.RunInstaller = delegate { throw new Exception("Installer must not run for a ready driver."); };
            string untouchedRoot = Path.Combine(fixture, "must-not-be-created");
            DriverInstaller.EnsureInstalled(untouchedRoot, delegate { throw new Exception("No install progress for an operational driver."); }, ready);
            Check(!Directory.Exists(untouchedRoot), "Ready setup must leave the directory untouched.");

            string data = Path.Combine(fixture, "data"), log = Path.Combine(data, "driver-install.log");
            Directory.CreateDirectory(log);
            bool unsafeLaunch = false;
            DriverInstallServices unsafeLog = Services();
            unsafeLog.VerifyInstaller = delegate { unsafeLaunch = true; };
            unsafeLog.RunInstaller = delegate { unsafeLaunch = true; return 3010; };
            bool refused = false;
            try { DriverInstaller.EnsureInstalled(fixture, null, unsafeLog); }
            catch (IOException) { refused = true; }
            finally { Directory.Delete(log); }
            Check(refused && !unsafeLaunch, "A directory at the elevated installer log leaf must be rejected before verification or launch.");
            foreach (string leaf in new[] { "camera.stdout.log", "camera.stderr.log", "driver-install.log" })
            {
                string output = Path.Combine(data, leaf);
                foreach (string redirected in new[] { data, output })
                {
                    bool inspectedRedirect = false, inspectedAfterRedirect = false;
                    Rejected<IOException>(delegate {
                        ServerPaths.ValidateOutputPath(output, delegate(string path) {
                            if (inspectedRedirect) inspectedAfterRedirect = true;
                            if (path == redirected) { inspectedRedirect = true; return FileAttributes.ReparsePoint; }
                            if (path == output) throw new FileNotFoundException();
                            return FileAttributes.Directory;
                        });
                    }, "Elevated outputs must reject leaf and ancestor redirections.");
                    Check(inspectedRedirect && !inspectedAfterRedirect, "Output validation stops at an ancestor redirection without examining its children.");
                }
                Rejected<UnauthorizedAccessException>(delegate {
                    ServerPaths.ValidateOutputPath(output, delegate { throw new UnauthorizedAccessException("Fixture attribute denial."); });
                }, "Inaccessible output paths must fail closed.");
                File.WriteAllText(output, "retained ordinary diagnostic");
                ServerPaths.ValidateOutputPath(output);
                Check(File.ReadAllText(output) == "retained ordinary diagnostic", "Output validation cannot truncate an existing ordinary diagnostic file.");
                File.Delete(output);
            }

            List<string> sequence = new List<string>();
            DriverInstallServices missing = Services();
            int probes = 0;
            missing.IsDriverReady = delegate(string path, int timeout)
            {
                Check(path == Path.Combine(fixture, "runtime", "usbbridge-camera.exe"), "Readiness must use the extracted backend.");
                Check(timeout > 0 && timeout <= 10000, "Readiness must have a bounded timeout.");
                sequence.Add("probe");
                return ++probes > 1;
            };
            missing.VerifyInstaller = delegate(string path)
            {
                Check(path == installer, "Only the pinned installer path may be verified.");
                AssertWriteLocked(path);
                sequence.Add("verify");
            };
            missing.RunInstaller = delegate(string path, string arguments, int timeout)
            {
                Check(path == installer, "Only the verified installer may be launched.");
                AssertWriteLocked(path);
                Check(arguments.Contains("/NORESTART") && arguments.Contains("/RESTARTEXITCODE=3010"), "Installation must prohibit automatic restart.");
                Check(arguments.Contains("/COMPONENTS=main,client") && arguments.Contains("/TASKS=vcredist"), "Required production components must be installed.");
                Check(arguments.Contains("/VERYSILENT") && arguments.Contains("/SUPPRESSMSGBOXES"), "The installer must run silently.");
                Check(arguments.Contains("/LOG=\"" + Path.Combine(fixture, "data", "driver-install.log") + "\""), "The installer log path must be quoted.");
                Check(timeout > 0 && timeout <= 240000, "The installer process must have a bounded timeout.");
                sequence.Add("install");
                return 0;
            };
            DriverInstaller.EnsureInstalled(fixture, delegate { sequence.Add("progress"); }, missing);
            Check(String.Join(",", sequence.ToArray()) == "probe,verify,progress,install,probe", "Verify before installing, then confirm readiness.");

            DriverInstallServices nonAdmin = Services();
            nonAdmin.IsAdministrator = delegate { return false; };
            Rejected<InvalidOperationException>(delegate { DriverInstaller.EnsureInstalled(fixture, null, nonAdmin); }, "Administrator rights are required before setup.");
            DriverInstallServices wrongArchitecture = Services();
            wrongArchitecture.Is64BitOperatingSystem = delegate { return false; };
            Rejected<InvalidOperationException>(delegate { DriverInstaller.EnsureInstalled(fixture, null, wrongArchitecture); }, "x64 installer must reject a 32-bit OS.");

            DriverInstallServices unverified = Services();
            unverified.VerifyInstaller = delegate { throw new InvalidDataException("Synthetic signature rejection."); };
            Rejected<InvalidDataException>(delegate { DriverInstaller.EnsureInstalled(fixture, null, unverified); }, "Verification failure must block installation.");
            // SHA mismatch is rejected before certificate decoding/store access.
            Rejected<InvalidDataException>(delegate { BridgeOfflineInstaller.Signature.Verify(installer, DriverInstaller.InstallerSha256, DriverInstaller.InstallerSigner); }, "Tampered installer bytes must be rejected without any system operation.");

            DriverInstallServices reboot = Services();
            reboot.VerifyInstaller = delegate { };
            reboot.RunInstaller = delegate { return 3010; };
            string restart = Rejected<DriverRestartRequiredException>(delegate { DriverInstaller.EnsureInstalled(fixture, null, reboot); }, "Exit 3010 must retain a typed manual-restart result.");
            Check(restart.Contains("Перезагрузите Windows вручную"), "Restart guidance must be explicit.");

            DriverInstallServices failed = Services();
            failed.VerifyInstaller = delegate { };
            failed.RunInstaller = delegate { return 5; };
            string failure = Rejected<InvalidOperationException>(delegate { DriverInstaller.EnsureInstalled(fixture, null, failed); }, "Nonzero installer failures must propagate.");
            Check(failure.Contains("5") && failure.Contains("driver-install.log"), "Installer failure must identify its code and log.");

            DriverInstallServices pending = Services();
            pending.VerifyInstaller = delegate { };
            pending.RunInstaller = delegate { return 0; };
            int retryCount = 0;
            pending.IsDriverReady = delegate { retryCount++; return false; };
            Rejected<DriverRestartRequiredException>(delegate { DriverInstaller.EnsureInstalled(fixture, null, pending); }, "A still-unready installed driver requires a typed restart result.");
            Check(retryCount == 21, "Readiness retry attempts must be bounded.");

            Rejected<ArgumentException>(delegate { DriverInstaller.BuildInstallerArguments("C:\\invalid\"directory", "C:\\log.txt"); }, "Embedded quotes must not inject installer arguments.");
            string quoted = DriverInstaller.BuildInstallerArguments("C:\\Program Files\\USBip", "C:\\path with spaces\\driver.log");
            Check(quoted.Contains("/DIR=\"C:\\Program Files\\USBip\""), "Installation directories with spaces must be quoted.");
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            int fixtureExit = DriverInstaller.RunInstaller(typeof(DriverInstallerTests).Assembly.Location, "--install-wait-fixture", 1);
            Check(fixtureExit == 7 && waiting.ElapsedMilliseconds >= 150, "An own synthetic child must finish after its soft timeout instead of being orphaned or killed.");
            Console.WriteLine("Driver installer checks passed: " + checks + ". Driver operations mocked; only an isolated 180ms child fixture was started. No driver, certificate store or clipboard changes.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            File.Delete(installer);
            Directory.Delete(packageDirectory);
            Directory.Delete(Path.Combine(fixture, "runtime"));
            if (Directory.Exists(Path.Combine(fixture, "data"))) Directory.Delete(Path.Combine(fixture, "data"));
            Directory.Delete(fixture);
        }
    }

    private static DriverInstallServices Services()
    {
        DriverInstallServices services = new DriverInstallServices();
        services.IsDriverReady = delegate { return false; };
        services.IsAdministrator = delegate { return true; };
        services.Is64BitOperatingSystem = delegate { return true; };
        services.InstallDirectory = delegate { return "C:\\Program Files\\USBip"; };
        services.VerifyInstaller = delegate { throw new Exception("Unexpected signature verification."); };
        services.RunInstaller = delegate { throw new Exception("Unexpected installer execution."); };
        services.Pause = delegate { };
        return services;
    }

    private static void AssertWriteLocked(string path)
    {
        try
        {
            using (FileStream write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
        }
        catch (IOException) { checks++; return; }
        throw new Exception("Verified installer must remain write-locked through launch.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++;
    }

    private static string Rejected<T>(Action action, string description) where T : Exception
    {
        try { action(); }
        catch (T error) { checks++; return error.Message; }
        throw new Exception(description);
    }
}
