using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace HorizonClient
{
    internal sealed class DriverRestartRequiredException : IOException
    {
        internal readonly int InstallerExitCode;
        internal readonly string Reason;
        internal DriverRestartRequiredException(int exitCode, string reason)
            : base(UiText.Get("Драйвер камеры установлен. Перезагрузите Windows вручную и снова запустите Server.exe.", "The camera driver is installed. Restart Windows manually, then start Server.exe again."))
        { InstallerExitCode = exitCode; Reason = reason; }
    }

    // Invoked only by the already-elevated server setup helper. An operational
    // controller is left alone, including its installed version and services.
    internal static class DriverInstaller
    {
        internal const string InstallerFileName = "USBip-0.9.8.1-x64.exe";
        internal const string InstallerSha256 = "38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A";
        internal const string InstallerSigner = "Cloudyne Systems (Scheibling Consulting AB)";
        private const int SetupTimeoutMilliseconds = 300000;

        internal static void EnsureInstalled(string root, Action<string> progress)
        {
            EnsureInstalled(root, progress, new DriverInstallServices());
        }

        internal static void EnsureInstalled(string root, Action<string> progress, DriverInstallServices services)
        {
            if (String.IsNullOrEmpty(root)) throw new ArgumentNullException("root");
            if (services == null) throw new ArgumentNullException("services");
            root = Path.GetFullPath(root);
            string backend = Path.Combine(root, "runtime", "usbbridge-camera.exe");
            Stopwatch elapsed = Stopwatch.StartNew();
            bool initiallyReady = services.IsDriverReady(backend, 10000);
            DiagnosticLog.Write("server.driver.readiness", "ready", initiallyReady, "phase", "before_install", "backend", backend);
            if (initiallyReady) return;
            if (!services.IsAdministrator())
                throw new InvalidOperationException(UiText.Get("Для установки драйвера камеры требуются права администратора.", "Administrator rights are required to install the camera driver."));
            if (!services.Is64BitOperatingSystem())
                throw new InvalidOperationException(UiText.Get("Для драйвера виртуальной камеры требуется 64-разрядная Windows.", "The virtual camera driver requires 64-bit Windows."));

            string installer = Path.Combine(root, "runtime", "drivers", InstallerFileName);
            if (!File.Exists(installer))
                throw new FileNotFoundException(UiText.Get("В составе Server.exe отсутствует установщик драйвера камеры. Замените Server.exe полной версией.", "The camera driver installer is missing. Replace Server.exe with the complete release."), installer);
            if ((File.GetAttributes(installer) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(UiText.Get("Установщик драйвера не должен быть символической ссылкой.", "The driver installer must not be a symbolic link."));
            string dataDirectory = Path.Combine(root, "data");
            string logPath = Path.Combine(dataDirectory, "driver-install.log");
            ServerPaths.ValidateOutputPath(logPath);
            Directory.CreateDirectory(dataDirectory);
            string arguments = BuildInstallerArguments(services.InstallDirectory(), logPath);
            int exitCode;
            // Retain this read-only handle through installation so the exact
            // verified installer cannot be replaced or modified before launch.
            using (FileStream lockedInstaller = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                DiagnosticLog.Write("server.driver.package_verification", "phase", "started", "installer", installer, "expected_sha256", InstallerSha256, "offline", true);
                services.VerifyInstaller(installer);
                ServerPaths.ValidateOutputPath(logPath);
                DiagnosticLog.Write("server.driver.package_verification", "phase", "completed", "installer", installer, "bytes", lockedInstaller.Length, "offline", true);
                if (progress != null) progress(UiText.Get("Установка драйвера виртуальной камеры…", "Installing the virtual camera driver…"));
                DiagnosticLog.Write("server.driver.install_started", "installer", installer, "arguments", arguments, "offline", true);
                exitCode = services.RunInstaller(installer, arguments, Remaining(elapsed, 240000));
                DiagnosticLog.Write("server.driver.install_completed", "exit_code", exitCode, "elapsed_ms", elapsed.ElapsedMilliseconds, "driver_log", Path.Combine(dataDirectory, "driver-install.log"));
            }
            if (exitCode == 3010) throw RestartRequired(exitCode, "installer_requested_restart");
            if (exitCode != 0)
                throw new InvalidOperationException(UiText.Get("Установка драйвера камеры завершилась с кодом ", "Camera driver installation finished with code ") + exitCode + UiText.Get(". Подробности: ", ". Details: ") + Path.Combine(dataDirectory, "driver-install.log"));

            if (elapsed.ElapsedMilliseconds >= SetupTimeoutMilliseconds - 10000)
            {
                DiagnosticLog.Write("server.driver.readiness_grace", "installation_elapsed_ms", elapsed.ElapsedMilliseconds);
                elapsed.Restart();
            }
            // PnP may publish its controller shortly after the installer exits.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                bool driverReady = services.IsDriverReady(backend, Remaining(elapsed, 10000));
                DiagnosticLog.Write("server.driver.readiness", "ready", driverReady, "phase", "after_install", "attempt", attempt + 1, "elapsed_ms", elapsed.ElapsedMilliseconds);
                if (driverReady) return;
                if (attempt != 19) services.Pause(Math.Min(500, Remaining(elapsed, 500)));
            }
            throw RestartRequired(exitCode, "controller_not_ready_after_install");
        }

        private static DriverRestartRequiredException RestartRequired(int exitCode, string reason)
        {
            DiagnosticLog.Write("server.driver.restart_required", "exit_code", exitCode, "reason", reason);
            return new DriverRestartRequiredException(exitCode, reason);
        }

        private static int Remaining(Stopwatch elapsed, int maximum)
        {
            long remaining = SetupTimeoutMilliseconds - elapsed.ElapsedMilliseconds;
            if (remaining <= 0) throw new TimeoutException(UiText.Get("Настройка драйвера камеры не завершилась за 5 минут. Проверьте журнал data\\driver-install.log перед повторным запуском.", "Camera driver setup did not finish within 5 minutes. Check data\\driver-install.log before trying again."));
            return (int)Math.Min(remaining, maximum);
        }

        internal static string BuildInstallerArguments(string installDirectory, string logPath)
        {
            // Preserve the original USBBridge production installer's component
            // selection and its explicit prohibition on automatic restarts.
            return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /SP- /NOICONS /TYPE=custom /COMPONENTS=main,client /TASKS=vcredist " +
                "/DIR=" + QuotePath(installDirectory) + " /LOG=" + QuotePath(logPath);
        }

        private static string QuotePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.IndexOf('"') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0)
                throw new ArgumentException("Invalid installer path.", "path");
            return "\"" + Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + "\"";
        }

        internal static bool ParseReadiness(string output)
        {
            if (String.IsNullOrWhiteSpace(output) || output.Length > 4096) return false;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 4096, RecursionLimit = 8 };
                Dictionary<string, object> document = serializer.Deserialize<Dictionary<string, object>>(output);
                object ready;
                return document != null && document.TryGetValue("ready", out ready) && ready is bool && (bool)ready;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal static bool QueryDriver(string backend, int timeoutMilliseconds)
        {
            if (!File.Exists(backend)) throw new FileNotFoundException(UiText.Get("Не найден компонент виртуальной камеры.", "The virtual camera component was not found."), backend);
            ProcessStartInfo start = HiddenProcess(backend, "driver-ready");
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            StringBuilder output = new StringBuilder();
            StringBuilder errors = new StringBuilder();
            using (Process process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    AppendProbeLine(output, args.Data);
                };
                // Drain diagnostics without exposing a console or retaining
                // unbounded output from a failed controller probe.
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { AppendProbeLine(errors, args.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    // This process only queries readiness; no driver setup is
                    // performed by it, so terminating a stuck probe is safe.
                    try { process.Kill(); } catch { }
                    DiagnosticLog.Write("server.driver.probe_timed_out", "backend", backend, "timeout_ms", timeoutMilliseconds);
                    throw new TimeoutException(UiText.Get("Проверка драйвера камеры не ответила вовремя.", "The camera driver check timed out."));
                }
                process.WaitForExit();
                string captured, capturedErrors;
                lock (output) captured = output.ToString();
                lock (errors) capturedErrors = errors.ToString();
                bool ready = process.ExitCode == 0 && ParseReadiness(captured);
                DiagnosticLog.Write("server.driver.probe_result", "backend", backend, "exit_code", process.ExitCode,
                    "ready", ready, "stdout", captured, "stderr", capturedErrors);
                return ready;
            }
        }

        private static void AppendProbeLine(StringBuilder buffer, string line)
        {
            if (line == null) return;
            lock (buffer)
            {
                int remaining = 4096 - buffer.Length;
                if (remaining <= 0) return;
                buffer.Append(line, 0, Math.Min(line.Length, remaining));
                if (buffer.Length < 4096) buffer.Append('\n');
            }
        }

        internal static int RunInstaller(string installer, string arguments, int timeoutMilliseconds)
        {
            using (Process process = Process.Start(HiddenProcess(installer, arguments)))
            {
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    // Keep the elevated parent alive until its exact installer
                    // exits. Returning here would detach an unfinished driver
                    // transaction when the user chooses Exit in the GUI.
                    DiagnosticLog.Write("server.driver.installer_waiting_after_timeout", "process_id", process.Id, "timeout_ms", timeoutMilliseconds);
                    Stopwatch waiting = Stopwatch.StartNew();
                    while (!process.WaitForExit(1000))
                        if (waiting.ElapsedMilliseconds >= 30000)
                        {
                            DiagnosticLog.Write("server.driver.installer_still_running", "process_id", process.Id);
                            waiting.Restart();
                        }
                }
                return process.ExitCode;
            }
        }

        private static ProcessStartInfo HiddenProcess(string executable, string arguments)
        {
            return new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }
    }

    // All driver/process/system operations are replaceable by pure fixtures.
    internal sealed class DriverInstallServices
    {
        internal Func<string, int, bool> IsDriverReady = DriverInstaller.QueryDriver;
        internal Func<bool> IsAdministrator = delegate
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        };
        internal Func<bool> Is64BitOperatingSystem = delegate { return Environment.Is64BitOperatingSystem; };
        internal Func<string> InstallDirectory = delegate { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip"); };
        internal Action<string> VerifyInstaller = delegate(string installer)
        {
            BridgeOfflineInstaller.Signature.Verify(installer, DriverInstaller.InstallerSha256, DriverInstaller.InstallerSigner);
        };
        internal Func<string, string, int, int> RunInstaller = DriverInstaller.RunInstaller;
        internal Action<int> Pause = Thread.Sleep;
    }
}

// The following strict offline Authenticode verifier is copied unchanged from
// USBBridge app/Verify-DriverInstaller.ps1. It caches only one pinned public
// intermediate CA; Windows must already trust the upstream publisher root.
namespace BridgeOfflineInstaller {
 public sealed class VerifiedInstaller {
  public string Sha256 {get;set;}
  public string Signer {get;set;}
 }
 public static class Signature {
  private const string ClientInstallerSha256="38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A";
  private const string ClientIntermediateSha256="CD0E144DD10BAC221FE2FB901058D16450A0578B3C47C770908F2E9ADA28EF12";
  // Public intermediate from the pinned signer's CA Issuers extension:
  // https://secure.globalsign.com/cacert/gsgccr45evcodesignca2020.crt
  private const string ClientIntermediateBase64="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";
  private static string CertificateDigest(byte[] encoded) {
   using(var hash=SHA256.Create()) {return BitConverter.ToString(hash.ComputeHash(encoded)).Replace("-", "");}
  }
  private static X509Certificate2 DecodePinnedClientIntermediate(byte[] encoded) {
   if(!String.Equals(CertificateDigest(encoded),ClientIntermediateSha256,StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Bundled client intermediate certificate SHA256 mismatch.");
   var certificate=new X509Certificate2(encoded);
   try {
    X509BasicConstraintsExtension constraints=null;
    X509KeyUsageExtension usage=null;
    foreach(X509Extension extension in certificate.Extensions) {
     if(extension.Oid.Value=="2.5.29.19") constraints=new X509BasicConstraintsExtension(extension,extension.Critical);
     if(extension.Oid.Value=="2.5.29.15") usage=new X509KeyUsageExtension(extension,extension.Critical);
    }
    if(certificate.HasPrivateKey || constraints==null || !constraints.CertificateAuthority || usage==null ||
       (usage.KeyUsages & X509KeyUsageFlags.KeyCertSign)==0 ||
       String.Equals(certificate.Subject,certificate.Issuer,StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("The bundled client intermediate must be a non-self-issued certificate-signing CA.");
    return certificate;
   } catch {certificate.Dispose();throw;}
  }
  private static void EnsurePinnedClientIntermediate(string installerDigest) {
   // The upstream Client EXE embeds only its leaf signer. Supply its exact
   // missing intermediate only after the complete installer digest matched.
   if(!String.Equals(installerDigest,ClientInstallerSha256,StringComparison.OrdinalIgnoreCase))return;
   using(var certificate=DecodePinnedClientIntermediate(Convert.FromBase64String(ClientIntermediateBase64)))
   using(var store=new X509Store(StoreName.CertificateAuthority,StoreLocation.CurrentUser)) {
    store.Open(OpenFlags.ReadWrite);
    bool present=false;
    foreach(X509Certificate2 existing in store.Certificates) {
     using(existing) {
      if(String.Equals(CertificateDigest(existing.RawData),ClientIntermediateSha256,StringComparison.OrdinalIgnoreCase))present=true;
     }
    }
    if(!present)store.Add(certificate);
   }
   // CA is the intermediate store, never Root/TrustedPeople/TrustedPublisher.
   // This adds no trust anchor. The unchanged full WinVerifyTrust FILE policy
   // below still validates PE digest, timestamp and a Windows-trusted root.
  }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  private struct FileInfo {
   public uint Size;
   [MarshalAs(UnmanagedType.LPWStr)] public string Path;
   public IntPtr Handle;
   public IntPtr KnownSubject;
  }
  [StructLayout(LayoutKind.Sequential)]
  private struct TrustData {
   public uint Size;
   public IntPtr PolicyCallbackData;
   public IntPtr SIPClientData;
   public uint UIChoice;
   public uint RevocationChecks;
   public uint UnionChoice;
   public IntPtr File;
   public uint StateAction;
   public IntPtr StateData;
   public IntPtr URLReference;
   public uint ProviderFlags;
   public uint UIContext;
   public IntPtr SignatureSettings;
  }
  [StructLayout(LayoutKind.Sequential)]
  private struct ProviderCertificatePrefix {
   public uint Size;
   public IntPtr Certificate;
  }
  [DllImport("wintrust.dll", ExactSpelling=true)]
  private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
  [DllImport("wintrust.dll", ExactSpelling=true)]
  private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
  [DllImport("wintrust.dll", ExactSpelling=true)]
  private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
  [DllImport("wintrust.dll", ExactSpelling=true)]
  private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
  public static VerifiedInstaller Verify(string path, string expectedSha256, string expectedSigner) {
   // FileShare.Read prevents modification/deletion between digest and trust checks.
   using(var input=new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
    string digest;
    using(var hash=SHA256.Create()) {digest=BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");}
    if(!String.Equals(digest, expectedSha256, StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("Bundled driver installer SHA256 mismatch. Extract a fresh complete release; no download was attempted.");
    EnsurePinnedClientIntermediate(digest);
    input.Position=0;
    var file=new FileInfo {Size=(uint)Marshal.SizeOf(typeof(FileInfo)), Path=path, Handle=input.SafeFileHandle.DangerousGetHandle()};
    IntPtr filePointer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
    var trust=new TrustData {
     Size=(uint)Marshal.SizeOf(typeof(TrustData)), UIChoice=2, RevocationChecks=0,
     UnionChoice=1, File=filePointer, StateAction=1,
     // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE.
     // This forbids certificate/revocation URL fetches even on a new offline PC.
     ProviderFlags=0x00001010
    };
    var action=new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    bool fileMarshaled=false;
    try {
     Marshal.StructureToPtr(file, filePointer, false);fileMarshaled=true;
     int status=WinVerifyTrust(new IntPtr(-1), ref action, ref trust);
     if(status!=0)
      throw new InvalidDataException("Bundled driver installer offline signature verification failed (0x"+unchecked((uint)status).ToString("X8")+"). Windows must trust the upstream publisher locally; no download was attempted.");
     IntPtr provider=WTHelperProvDataFromStateData(trust.StateData);
     IntPtr signer=provider==IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider,0,false,0);
     IntPtr certificate=signer==IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer,0);
     if(certificate==IntPtr.Zero) throw new InvalidDataException("The verified installer has no publisher certificate.");
     var prefix=(ProviderCertificatePrefix)Marshal.PtrToStructure(certificate,typeof(ProviderCertificatePrefix));
     if(prefix.Certificate==IntPtr.Zero) throw new InvalidDataException("The verified installer publisher certificate is unavailable.");
     using(var cert=new X509Certificate2(prefix.Certificate)) {
      string name=cert.GetNameInfo(X509NameType.SimpleName,false);
      if(!String.Equals(name,expectedSigner,StringComparison.OrdinalIgnoreCase))
       throw new InvalidDataException("Bundled driver installer publisher mismatch (expected '"+expectedSigner+"', received '"+name+"'). No download was attempted.");
      return new VerifiedInstaller {Sha256=digest,Signer=name};
     }
    } finally {
     if(trust.StateData!=IntPtr.Zero) {trust.StateAction=2;WinVerifyTrust(new IntPtr(-1),ref action,ref trust);}
     if(fileMarshaled) Marshal.DestroyStructure(filePointer,typeof(FileInfo));
     Marshal.FreeHGlobal(filePointer);
    }
   }
  }
 }
}
