using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using HorizonClient;

internal static class DirectShowCaptureDiagnosticsTests
{
    private static int checks;
    [MTAThread]
    private static int Main()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "capture-trace-fixture-" + Guid.NewGuid().ToString("N") + ".log");
        object graphObject = null, grabber = null, renderer = null;
        try
        {
            Check(DiagnosticLog.Initialize(path), "Fixture logger opens in build folder.");
            var trace = new DirectShowCaptureDiagnostics("@device:fixture-only");
            int denied = unchecked((int)0x80070005);
            Check(trace.Call("fixture.expected_denial", delegate { return denied; }) == denied, "Logging preserves native HRESULT exactly.");
            try { trace.Call("fixture.expected_exception", delegate { throw new NotSupportedException("fixture"); }); throw new Exception("Expected exception was swallowed."); }
            catch (NotSupportedException) { checks++; }
            graphObject = trace.Create(DirectShow.FilterGraph);
            var graph = (DirectShow.IGraphBuilder)graphObject;
            // These software-only filters have no device source or connection.
            grabber = trace.Create(DirectShow.SampleGrabber);
            renderer = trace.Create(DirectShow.NullRenderer);
            DirectShow.Check(graph.AddFilter((DirectShow.IBaseFilter)grabber, "Fixture grabber"), "Add fixture");
            DirectShow.Check(graph.AddFilter((DirectShow.IBaseFilter)renderer, "Fixture renderer"), "Add fixture");
            trace.Snapshot(graph, "fixture_stopped");
            Guid classId;
            Check(((DirectShow.IBaseFilter)grabber).GetClassID(out classId) >= 0 && classId == DirectShow.SampleGrabber,
                "Inspection must keep the caller's existing grabber RCW alive.");
            Check(((DirectShow.IBaseFilter)renderer).GetClassID(out classId) >= 0 && classId == DirectShow.NullRenderer,
                "Inspection must keep the caller's existing renderer RCW alive.");
            Check(((DirectShow.ISampleGrabber)grabber).SetOneShot(false) >= 0, "A grabber remains usable after its pins were inspected.");
            int state; Check(((DirectShow.IMediaControl)graphObject).GetState(0, out state) >= 0 && state == 0, "Inspection leaves graph stopped.");
            Check(DiagnosticLog.Shutdown(), "All native graph trace records flush.");
            var records = new List<Dictionary<string, object>>();
            var serializer = new JavaScriptSerializer();
            foreach (string line in File.ReadAllLines(path)) records.Add(serializer.Deserialize<Dictionary<string, object>>(line));
            int filters = 0, pins = 0, begins = 0, ends = 0, snapshots = 0, failures = 0;
            foreach (var row in records)
            {
                string name = Convert.ToString(row["event"]);
                if (name == "capture.native.begin") begins++;
                if (name == "capture.native.end") ends++;
                if (name == "capture.native.exception") failures++;
                if (name == "capture.graph.snapshot.failed") throw new Exception("Native COM graph inspection failed: " + serializer.Serialize(row));
                if (name == "capture.graph.snapshot.end") snapshots++;
                if (name == "capture.graph.filter") { filters++; Check(Convert.ToString(row["state_hresult"]) == "0x00000000" && Convert.ToInt32(row["state"]) == 0, "Each native filter state is inspected without a transition."); }
                if (name == "capture.graph.pin") { pins++; Check(Convert.ToString(row["connected_hresult"]) == "0x80040209", "Unconnected native pins preserve VFW_E_NOT_CONNECTED."); }
                if (name.StartsWith("capture.", StringComparison.Ordinal)) Check(Convert.ToString(row["capture_id"]) == trace.CaptureId.ToString(), "Every low-level record correlates to one attempt.");
            }
            Check(filters == 2 && pins == 3 && snapshots == 1, "Native enum/filter/pin vtables enumerate two software filters and all three pins.");
            Check(begins == 5 && ends == 4 && failures == 1, "Every operation has a matching result or exception and a duration.");
            Check(DirectShowCaptureDiagnostics.EventName(3) == "EC_ERRORABORT" && DirectShowCaptureDiagnostics.EventName(0x0e) == "EC_PAUSED", "Event names match Windows SDK values.");
            Check(DirectShowCaptureDiagnostics.Describe(denied).Length > 0, "The system HRESULT explanation is available.");
            Console.WriteLine("DirectShow low-level diagnostic checks passed: " + checks + ". Software-only filters; no cameras opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            DiagnosticLog.Shutdown();
            DirectShow.Release(graphObject); DirectShow.Release(renderer); DirectShow.Release(grabber);
            if (File.Exists(path)) File.Delete(path);
        }
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
