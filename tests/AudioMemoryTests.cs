using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HorizonClient;

internal static class AudioMemoryTests
{
    static int checks;
    static void Check(bool value, string text) { if (!value) throw new Exception(text); checks++; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference DecoderCycle()
    {
        var mux = new AudioReceiveMultiplexer(delegate { }, true);
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        Array workers = (Array)typeof(AudioReceiveMultiplexer).GetField("workers", fields).GetValue(mux);
        var threads = new List<Thread>();
        foreach (object worker in workers) threads.Add((Thread)worker.GetType().GetField("thread", fields).GetValue(worker));
        mux.Feed(new float[4800]);
        var clock = Stopwatch.StartNew();
        foreach (object worker in workers)
            while ((long)worker.GetType().GetField("processedSamples", fields).GetValue(worker) < 4800 && clock.ElapsedMilliseconds < 10000) Thread.Sleep(1);
        foreach (object worker in workers) Check((long)worker.GetType().GetField("processedSamples", fields).GetValue(worker) >= 4800, "Each actual native decoder processes PCM before disposal.");
        mux.Dispose(); mux.Dispose();
        foreach (Thread thread in threads) Check(thread.Join(10000), "Disposed native worker terminates and destroys its native decoder.");
        return new WeakReference(mux);
    }
    static int Main()
    {
        try {
            float[] pcm = new float[480]; var resampler = new AudioResampler(48000);
            for (int i = 0; i < 10000; i++) Check(Object.ReferenceEquals(pcm, resampler.Convert(pcm)), "48k capture does not duplicate its owned PCM buffer.");
            for (int i = 0; i < 5; i++) DecoderCycle();
            GC.Collect(); GC.WaitForPendingFinalizers(); long before = GC.GetTotalMemory(true);
            long privateBefore; using (var process = Process.GetCurrentProcess()) { process.Refresh(); privateBefore = process.PrivateMemorySize64; }
            var weak = new List<WeakReference>();
            for (int i = 0; i < 40; i++) weak.Add(DecoderCycle());
            GC.Collect(); GC.WaitForPendingFinalizers(); long after = GC.GetTotalMemory(true);
            foreach (WeakReference reference in weak) Check(!reference.IsAlive, "Closed multiplexer, queue and callback closure are collectible.");
            Check(after - before < 2 * 1024 * 1024, "Repeated decoder cycles do not retain growing managed PCM queues.");
            long privateAfter; using (var process = Process.GetCurrentProcess()) { process.Refresh(); privateAfter = process.PrivateMemorySize64; }
            Console.WriteLine("Audio memory checks passed: " + checks + "; after 40 real native decoder cycles: managed delta=" + (after - before) + ", private delta=" + (privateAfter - privateBefore) + " bytes. Private bytes include allocator caching; no audio devices.");
            var second = new List<WeakReference>(); for (int i = 0; i < 40; i++) second.Add(DecoderCycle());
            GC.Collect(); GC.WaitForPendingFinalizers(); long finalManaged = GC.GetTotalMemory(true);
            foreach (WeakReference reference in second) Check(!reference.IsAlive, "The second stress batch also releases every decoder owner.");
            Check(finalManaged - before < 2 * 1024 * 1024, "Managed audio retention stays bounded after 80 actual PCM lifetimes.");
            long finalPrivate; using (var process = Process.GetCurrentProcess()) { process.Refresh(); finalPrivate = process.PrivateMemorySize64; }
            Console.WriteLine("Audio memory second batch: managed delta=" + (finalManaged - after) + ", private delta=" + (finalPrivate - privateAfter) + "; cumulative cycles=80; all native workers joined.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
