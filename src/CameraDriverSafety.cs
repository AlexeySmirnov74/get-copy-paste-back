using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace HorizonClient
{
    internal sealed class CameraDriverConflictException : IOException
    {
        internal CameraDriverConflictException(string message) : base(message) { }
    }

    // Read-only preflight for device creation/removal/reconfiguration. Existing
    // camera enumeration and video delivery do not require this preflight.
    // The saved crash dump from this PC identifies VBoxUSBMon+0x3a70 while
    // completing a USB request during camera creation.
    internal static class CameraDriverSafety
    {
        private const string ServiceName = "VBoxUSBMon";
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStopped = 1;
        private const int ErrorServiceDoesNotExist = 1060;

        internal static void EnsureReconfigurationAllowed()
        {
            EnsureReconfigurationAllowed(ReadServiceState);
        }

        // The injected query keeps fixtures completely independent of Windows
        // services, hardware, registry state, processes and driver IOCTLs.
        internal static void EnsureReconfigurationAllowed(Func<uint?> readServiceState)
        {
            if (readServiceState == null) throw new ArgumentNullException("readServiceState");
            uint? state;
            try { state = readServiceState(); }
            catch (Exception error)
            {
                DiagnosticLog.Error("server.camera.driver_safety.query_failed", error,
                    "service", ServiceName, "decision", "blocked");
                throw new IOException(UiText.Get("Создание/перенастройка камеры остановлено: не удалось безопасно проверить состояние драйвера VBoxUSBMon из usbipd-win. Драйверы автоматически не изменялись.", "Camera setup stopped: the state of the usbipd-win VBoxUSBMon driver could not be checked safely. Drivers were not changed automatically."), error);
            }

            string stateName = DescribeState(state);
            if (!state.HasValue || state.Value == ServiceStopped)
            {
                DiagnosticLog.Write("server.camera.driver_safety.checked",
                    "service", ServiceName, "state", stateName,
                    "state_code", state.HasValue ? (object)state.Value : null,
                    "decision", "allowed");
                return;
            }

            DiagnosticLog.Write("server.camera.driver_safety.blocked",
                "service", ServiceName, "state", stateName,
                "state_code", state.Value, "decision", "blocked");
            if (state.Value >= 2 && state.Value <= 7)
                throw new CameraDriverConflictException(UiText.Get("Создание/перенастройка камеры остановлено: загружен VBoxUSBMon из usbipd-win. На этом ПК выявлен синий экран в этом драйвере при повторном создании камеры. Состояние: ", "Camera setup stopped: the usbipd-win VBoxUSBMon driver is loaded. This driver caused a blue screen on this PC when recreating the camera. State: ") + stateName + UiText.Get(". Драйверы автоматически не изменялись.", ". Drivers were not changed automatically."));
            throw new CameraDriverConflictException(UiText.Get("Создание/перенастройка камеры остановлено: неизвестное состояние драйвера VBoxUSBMon из usbipd-win (", "Camera setup stopped: the usbipd-win VBoxUSBMon driver has an unknown state (") + state.Value.ToString(CultureInfo.InvariantCulture) + UiText.Get("). Драйверы автоматически не изменялись.", "). Drivers were not changed automatically."));
        }

        private static string DescribeState(uint? state)
        {
            if (!state.HasValue) return "absent";
            switch (state.Value)
            {
                case 1: return "stopped";
                case 2: return "start_pending";
                case 3: return "stop_pending";
                case 4: return "running";
                case 5: return "continue_pending";
                case 6: return "pause_pending";
                case 7: return "paused";
                default: return "unknown";
            }
        }

        private static uint? ReadServiceState()
        {
            IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) throw QueryFailure("OpenSCManager", Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = OpenService(manager, ServiceName, ServiceQueryStatus);
                if (service == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceDoesNotExist) return null;
                    throw QueryFailure("OpenService", error);
                }
                try
                {
                    ServiceStatus status;
                    if (!QueryServiceStatus(service, out status))
                        throw QueryFailure("QueryServiceStatus", Marshal.GetLastWin32Error());
                    return status.CurrentState;
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        private static IOException QueryFailure(string operation, int error)
        {
            return new IOException(UiText.Get("Проверка VBoxUSBMon: ", "VBoxUSBMon check: ") + operation + UiText.Get(" вернула ошибку Windows ", " returned Windows error ") + error.ToString(CultureInfo.InvariantCulture) + ".", new Win32Exception(error));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            internal uint ServiceType;
            internal uint CurrentState;
            internal uint ControlsAccepted;
            internal uint Win32ExitCode;
            internal uint ServiceSpecificExitCode;
            internal uint CheckPoint;
            internal uint WaitHint;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
