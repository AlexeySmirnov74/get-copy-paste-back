using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace HorizonClient
{
    // Observes this application's UI and CPU only. It does not inspect other
    // processes, install hooks, change priorities or query audio/video devices.
    internal sealed class UiResponsivenessMonitor : IDisposable
    {
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Process process = Process.GetCurrentProcess();
        private long lastTick, lastSummary, lastCpu;
        private int generation0, generation1, generation2;

        internal UiResponsivenessMonitor()
        {
            lastCpu = (long)process.TotalProcessorTime.TotalMilliseconds;
            generation0 = GC.CollectionCount(0); generation1 = GC.CollectionCount(1); generation2 = GC.CollectionCount(2);
            timer.Interval = 1000; timer.Tick += Tick; timer.Start();
        }

        private void Tick(object sender, EventArgs args)
        {
            long time = clock.ElapsedMilliseconds, gap = time - lastTick; lastTick = time;
            bool delayed = gap > 1500;
            if (!DiagnosticLog.IsEnabled || !delayed && time - lastSummary < 10000) return;
            try
            {
                process.Refresh();
                long cpu = (long)process.TotalProcessorTime.TotalMilliseconds, interval = Math.Max(1, time - lastSummary);
                int availableWorkers, availableIo; ThreadPool.GetAvailableThreads(out availableWorkers, out availableIo);
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                DiagnosticLog.Write(delayed ? "application.ui.delayed" : "application.responsiveness", "ui_gap_ms", gap,
                    "ui_delay_ms", Math.Max(0, gap - 1000), "interval_ms", interval, "cpu_ms", cpu - lastCpu,
                    "cpu_percent_one_core", (cpu - lastCpu) * 100.0 / interval, "logical_processors", Environment.ProcessorCount,
                    "working_set_bytes", process.WorkingSet64, "private_bytes", process.PrivateMemorySize64,
                    "gc0", g0 - generation0, "gc1", g1 - generation1, "gc2", g2 - generation2,
                    "available_workers", availableWorkers, "available_io_workers", availableIo);
                lastSummary = time; lastCpu = cpu; generation0 = g0; generation1 = g1; generation2 = g2;
            }
            catch (Exception error) { DiagnosticLog.Error("application.responsiveness.failed", error); lastSummary = time; }
        }

        public void Dispose() { timer.Stop(); timer.Dispose(); process.Dispose(); }
    }
}
