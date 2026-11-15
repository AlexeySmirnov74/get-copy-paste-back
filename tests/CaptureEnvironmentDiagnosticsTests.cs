using System;
using System.Collections.Generic;
using System.IO;
using HorizonClient;

internal static class CaptureEnvironmentDiagnosticsTests
{
    private static int checks;
    private const string Moniker = @"@device:pnp:\\?\vmwhzbus#vmwvwebcam#1&79f5d87&0&vmwhzbus1_{7749f2cd-4723-f9dc-2e2a-d3629e6ee95b}_1#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global";
    private const string Instance = @"vmwhzbus\vmwvwebcam\1&79f5d87&0&vmwhzbus1_{7749f2cd-4723-f9dc-2e2a-d3629e6ee95b}_1";
    private const string Driver = @"{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\0000";
    private static int Main()
    {
        try
        {
            Paths(); BoundsAndThrottle(); CompleteSnapshot(); ReadFailuresAndBudget();
            Console.WriteLine("Capture environment diagnostic checks passed: " + checks + "; pure injected metadata/clock fixtures, no OS registry/process/file/camera queries."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Paths()
    {
        string value;
        Check(CaptureEnvironmentDiagnostics.TryGetDeviceInstanceId(Moniker, out value) && value == Instance, "The exact Horizon PnP interface moniker resolves only its selected hardware instance");
        foreach (string bad in new[] { null, "", "@device:sw:private", Moniker.Replace("vmwvwebcam", ".."), Moniker.Replace("vmwvwebcam", @"other\key"), Moniker.Replace("65e8773d-8f56-11d0-a3b9-00a0c9223196", "bad-guid"), new String('a',4097) })
            Check(!CaptureEnvironmentDiagnostics.TryGetDeviceInstanceId(bad, out value), "Unrecognized/malformed monikers must never trigger registry searching");
        Check(CaptureEnvironmentDiagnostics.TryGetDriverKey(Driver, out value) && value == @"SYSTEM\CurrentControlSet\Control\Class\" + Driver, "Driver class metadata stays under its exact software key");
        foreach (string bad in new[] { null, "", @"..\0000", Driver + @"\child", Driver.Replace("0000", "-001"), Driver.Replace("0000", "00000") })
            Check(!CaptureEnvironmentDiagnostics.TryGetDriverKey(bad, out value), "Malformed driver metadata paths rejected");
        foreach (string image in new[] { @"\SystemRoot\System32\drivers\vmw.sys", @"%SystemRoot%\System32\drivers\vmw.sys", @"system32\drivers\vmw.sys", @"\??\C:\Windows\System32\drivers\vmw.sys", @"C:\Windows\System32\drivers\vmw.sys" })
            Check(CaptureEnvironmentDiagnostics.TryResolveDriverImage(image, @"C:\Windows", out value) && String.Equals(value, @"C:\Windows\System32\drivers\vmw.sys", StringComparison.OrdinalIgnoreCase), "Known driver image spelling resolves for metadata-only reading: " + image);
        foreach (string image in new[] { null, "", @"C:\Other\vmw.sys", @"\\server\share\vmw.sys", @"C:\Windows\..\Other\vmw.sys", @"C:\Windows\notdriver.exe", @"C:\Windows\vmw.sys --argument", @"C:\WindowsEvil\vmw.sys" })
            Check(!CaptureEnvironmentDiagnostics.TryResolveDriverImage(image, @"C:\Windows", out value), "Driver file reads are restricted to a local Windows .sys image");
    }
    private static void BoundsAndThrottle()
    {
        Check(((string)CaptureEnvironmentDiagnostics.BoundValue(new String('x',3000))).Length == 1024, "Individual string metadata is bounded");
        var many = new string[40]; for (int i=0;i<many.Length;i++) many[i] = new String((char)('a'+i%20),500);
        Check(((string)CaptureEnvironmentDiagnostics.BoundValue(many)).Length == 1024, "Multi-string metadata is bounded and directly serializable by DiagnosticLog");
        Check(((string)CaptureEnvironmentDiagnostics.BoundValue(new byte[100])).Length == 95, "Binary metadata is at most thirty-two bytes rendered as hexadecimal");
        Check((int)CaptureEnvironmentDiagnostics.BoundValue(3) == 3 && CaptureEnvironmentDiagnostics.BoundValue(null) == null, "Scalar metadata retains its type");
        foreach(string allowed in CaptureEnvironmentDiagnostics.ProcessNames) Check(CaptureEnvironmentDiagnostics.IsAllowedProcessName(allowed.ToUpperInvariant()), "Process allowlist is case insensitive");
        foreach(string denied in new[] { null,"", "chrome", "Teams-private", "Squadus --password", "ffmpeg.exe" }) Check(!CaptureEnvironmentDiagnostics.IsAllowedProcessName(denied), "Unrelated names/command lines are never process-query targets");
        var throttle = new CaptureEnvironmentDiagnostics.SnapshotThrottle();
        Check(throttle.TryReserve(Moniker,"run"), "Initial failure snapshot reserved");
        Check(!throttle.TryReserve(Moniker,"run"), "Repeated same-stage failures do not flood diagnostics");
        Check(throttle.TryReserve(Moniker,"connect") && throttle.TryReserve(Moniker,"format") && throttle.TryReserve(Moniker,"stream"), "Distinct bounded failure stages may retain separate snapshots");
        Check(!throttle.TryReserve(Moniker,"another"), "At most four snapshots are queued per process");
        Check(!new CaptureEnvironmentDiagnostics.SnapshotThrottle().TryReserve(Moniker,new String('x',129)), "Oversized stage identifiers rejected");
    }
    private static void CompleteSnapshot()
    {
        var service = new FixtureServices(); var records = new Records();
        CaptureEnvironmentDiagnostics.Collect(Moniker,"EC_ERRORABORT",service,records.Emit);
        Check(service.RegistryQueries.Count == 9, "Snapshot reads OS identity, exact selected device/driver/service and five relevant privacy keys only");
        Check(service.RegistryQueries.Contains(@"HKLM|SYSTEM\CurrentControlSet\Enum\"+Instance), "The selected device instance is queried exactly");
        Check(service.RegistryQueries.Contains(@"HKLM|SYSTEM\CurrentControlSet\Control\Class\"+Driver), "Its own driver software metadata is queried exactly");
        Check(service.FileReads == 1 && service.LastFile == @"C:\Windows\System32\drivers\vmw.sys", "Only the selected driver's local image version is inspected");
        Check(service.ProcessQueries.Count == CaptureEnvironmentDiagnostics.ProcessNames.Length, "Only explicit allowlisted names are queried");
        Check(service.ProcessQueries.Contains("Client") && service.ProcessQueries.Contains("Server"), "The renamed application executables remain covered by scoped process diagnostics.");
        foreach(string name in service.ProcessQueries) Check(CaptureEnvironmentDiagnostics.IsAllowedProcessName(name), "No all-process enumeration is requested");
        Check(records.Count("capture.environment.process_presence") == 1, "Only allowed returned process records survive filtering");
        Check(records.Has("capture.environment.process_presence","note","Process presence does not establish that this process is using the camera."), "Process presence never claims camera ownership");
        Check(records.Has("capture.environment.process","executable_path",@"C:\fixture\Client.exe") && records.Has("capture.environment.driver_file","file_version","fixture-version"), "Environment/file metadata is flattened into actual logged scalar fields");
        Check(records.Has("capture.environment.registry_value","value","Allow"), "Relevant privacy consent values are retained without application history");
        Check(records.Count("capture.environment.completed") == 1 && records.Has("capture.environment.completed","budget_exhausted",false), "Successful bounded snapshot reports completion");
        Check(records.Count("capture.environment.read_failed") == 0, "Fixture complete snapshot has no hidden read errors");
    }
    private static void ReadFailuresAndBudget()
    {
        var denied = new FixtureServices { DenyRegistry = true }; var records = new Records();
        CaptureEnvironmentDiagnostics.Collect(Moniker,"stream",denied,records.Emit);
        Check(records.Count("capture.environment.read_failed") > 0 && records.Has("capture.environment.read_failed","hresult","0x80070005"), "Permission failures retain their actual HRESULT");
        Check(!records.ContainsValue("synthetic-private-message"), "Exception messages or unrelated paths are not copied into environment diagnostics");
        Check(records.Count("capture.environment.completed") == 1, "Denied metadata reads never abort the snapshot or camera recovery");
        var budget = new FixtureServices { ClockStep = 1000 }; records = new Records();
        CaptureEnvironmentDiagnostics.Collect(Moniker,"stream",budget,records.Emit);
        Check(budget.ProcessQueries.Count == 0 && budget.RegistryQueries.Count <= 2 && records.Has("capture.environment.completed","budget_exhausted",true), "Elapsed budget stops further registry/process queries");
        var unknown = new FixtureServices(); records = new Records();
        CaptureEnvironmentDiagnostics.Collect("@device:sw:unknown","stream",unknown,records.Emit);
        Check(unknown.RegistryQueries.Count == 6 && unknown.FileReads == 0, "A software/non-PnP moniker never falls back to enumerating unrelated devices");
    }
    private sealed class FixtureServices : CaptureEnvironmentDiagnosticServices
    {
        internal readonly List<string> RegistryQueries = new List<string>(), ProcessQueries = new List<string>();
        internal int FileReads; internal string LastFile; internal bool DenyRegistry; internal long ClockStep, Clock;
        internal override long Milliseconds { get { long result=Clock; Clock+=ClockStep; return result; } }
        internal override string WindowsDirectory { get { return @"C:\Windows"; } }
        internal override Dictionary<string,object> EnvironmentMetadata() { return new Dictionary<string,object> { {"executable_path",@"C:\fixture\Client.exe"},{"process_64bit",true},{"session_id",7} }; }
        internal override Dictionary<string,object> RegistryValues(string hive,string path,string[] names)
        {
            RegistryQueries.Add(hive+"|"+path);
            if(DenyRegistry) throw new UnauthorizedAccessException("synthetic-private-message");
            if(path == @"SYSTEM\CurrentControlSet\Enum\"+Instance) return new Dictionary<string,object> { {"Service","vmw"},{"Driver",Driver},{"FriendlyName","Fixture camera"},{"HardwareID",new[]{"fixture-id"}} };
            if(path == @"SYSTEM\CurrentControlSet\Services\vmw") return new Dictionary<string,object> { {"ImagePath",@"\SystemRoot\System32\drivers\vmw.sys"},{"Start",3} };
            return new Dictionary<string,object> { {"Value","Allow"},{"DriverVersion","fixture-driver-version"} };
        }
        internal override Dictionary<string,object> FileMetadata(string path) { FileReads++;LastFile=path;return new Dictionary<string,object>{{"file_version","fixture-version"}}; }
        internal override CaptureProcessPresence[] ProcessesByName(string name)
        {
            ProcessQueries.Add(name);
            return name == "Squadus" ? new[] { new CaptureProcessPresence { Name="Squadus",ProcessId=42,SessionId=7,SessionQueryHresult="0x00000000" },new CaptureProcessPresence {Name="unrelated",ProcessId=99} } : new CaptureProcessPresence[0];
        }
    }
    private sealed class Records
    {
        private readonly List<KeyValuePair<string,Dictionary<string,object>>> all = new List<KeyValuePair<string,Dictionary<string,object>>>();
        internal void Emit(string name,object[] fields) { var values=new Dictionary<string,object>();for(int i=0;i+1<fields.Length;i+=2)values[(string)fields[i]]=fields[i+1];all.Add(new KeyValuePair<string,Dictionary<string,object>>(name,values)); }
        internal int Count(string name) {int count=0;foreach(var item in all)if(item.Key==name)count++;return count;}
        internal bool Has(string name,string field,object expected) {foreach(var item in all){object value;if(item.Key==name&&item.Value.TryGetValue(field,out value)&&Object.Equals(value,expected))return true;}return false;}
        internal bool ContainsValue(string text) {foreach(var item in all)foreach(object value in item.Value.Values)if(value is string&&((string)value).Contains(text))return true;return false;}
    }
    private static void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
}
