using System;
using HorizonClient;

internal static class AudioOutputSelectionTests
{
    private static int checks;

    private static int Main()
    {
        try
        {
            AudioEndpoint hardware = Endpoint("waveout:0", "Speakers (Realtek HD Audio)");
            AudioEndpoint virtualOutput = Endpoint("waveout:4", "VMware VirtualAudio (DevTap)");
            AudioEndpoint[] outputs = { hardware, virtualOutput };
            Check(AudioOutputSelection.Resolve(outputs, null) == virtualOutput, "The default checked output must be the first Horizon virtual endpoint.");
            Check(AudioOutputSelection.Resolve(outputs, String.Empty) == virtualOutput, "An absent saved name permits automatic virtual output selection.");
            Check(AudioOutputSelection.Resolve(new[] { hardware }, null) == null, "Automatic selection must not fall back to a hardware/default output.");
            Check(AudioOutputSelection.Resolve(outputs, hardware.Name) == hardware, "An explicit hardware choice must be honored.");

            AudioEndpoint renumbered = Endpoint("waveout:1", virtualOutput.Name);
            AudioEndpoint reassignedHardware = Endpoint("waveout:4", hardware.Name);
            AudioEndpoint[] changed = { reassignedHardware, renumbered };
            Check(AudioOutputSelection.Resolve(changed, virtualOutput.Name) == renumbered,
                "A saved output name must resolve to its new index and never yesterday's hardware index.");
            Check(AudioOutputSelection.Resolve(new[] { hardware }, virtualOutput.Name) == null,
                "A missing explicit output must remain unavailable even if hardware is present.");
            Check(AudioOutputSelection.Resolve(outputs, "missing output") == null,
                "A missing explicit output must not silently change to an automatic virtual output.");
            Check(AudioOutputSelection.Resolve(new[] { virtualOutput, renumbered }, virtualOutput.Name) == null,
                "Ambiguous identical captions must remain unavailable.");
            Check(AudioOutputSelection.Resolve(new[] { Endpoint("waveout:2", virtualOutput.Name.ToUpperInvariant()) }, virtualOutput.Name) == null,
                "Stored captions must use exact matching.");
            Check(AudioOutputSelection.Resolve(outputs, " ") == null, "A nonempty invalid caption must not become automatic mode.");
            Check(AudioOutputSelection.Resolve(null, null) == null && AudioOutputSelection.Resolve(new AudioEndpoint[0], null) == null,
                "An unavailable enumeration must produce no selected output.");

            AudioEndpoint[] malformed = { null, Endpoint(null, "Horizon Audio"), Endpoint("waveout:1", null),
                Endpoint("waveout:65536", "Horizon Audio"), Endpoint("waveout:-1", "Horizon Audio"),
                Endpoint("waveout: 1", "Horizon Audio"), Endpoint("wasapi:1", "Horizon Audio"), virtualOutput };
            Check(AudioOutputSelection.Resolve(malformed, null) == virtualOutput, "Malformed endpoints must not displace a valid automatic output.");
            Check(AudioOutputSelection.Resolve(malformed, "Horizon Audio") == null, "Malformed saved endpoints must remain unavailable.");
            Check(AudioOutputSelection.IsHorizonOutput("Speakers (VMware Virtual Audio)"), "VMware virtual audio captions must be recognized despite spaces.");
            Check(AudioOutputSelection.IsHorizonOutput("VMware DevTap") && AudioOutputSelection.IsHorizonOutput("Omnissa Audio") &&
                AudioOutputSelection.IsHorizonOutput("Horizon Audio"), "Supported product render captions must be recognized.");
            Check(!AudioOutputSelection.IsHorizonOutput("VMware microphone") && !AudioOutputSelection.IsHorizonOutput("Realtek HD Audio"),
                "Unrelated device captions must not become automatic render outputs.");
            string legacy = "Speakers (Long virtual device na";
            var full = new AudioEndpoint("waveout:5", "Speakers (Long virtual device name A)", "persistent-a", legacy);
            var collision = new AudioEndpoint("waveout:6", "Speakers (Long virtual device name B)", "persistent-b", legacy);
            Check(AudioOutputSelection.Resolve(new[] { full }, legacy) == full, "A unique saved legacy WinMM caption migrates to the full endpoint name.");
            Check(AudioOutputSelection.Resolve(new[] { full, collision }, legacy) == null, "Truncated legacy captions shared by two devices cannot silently choose either.");
            Check(AudioOutputSelection.Resolve(new[] { full, collision }, collision.Name) == collision, "Full captions disambiguate endpoints with identical truncated WinMM names.");
            var exact = new AudioEndpoint("waveout:7", legacy, "persistent-c", legacy);
            Check(AudioOutputSelection.Resolve(new[] { full, exact }, legacy) == exact, "An exact current caption takes precedence over a legacy alias.");
            var renamed = new AudioEndpoint("waveout:2", "Renamed speakers", "persistent-a", "Renamed speakers");
            Check(AudioOutputSelection.Resolve(new[] { collision, renamed }, full.Name, "persistent-a") == renamed,
                "A stable endpoint identity survives caption and numeric-index changes.");
            var sameCaption = new AudioEndpoint("waveout:5", full.Name, "persistent-other", legacy);
            Check(AudioOutputSelection.Resolve(new[] { sameCaption, renamed }, full.Name, "persistent-a") == renamed,
                "Stable identity takes precedence over another device at the old index with the old caption.");
            Check(AudioOutputSelection.Resolve(new[] { sameCaption }, full.Name, "persistent-a") == null,
                "A missing saved identity cannot silently fall back to a matching caption.");
            var duplicateIdentity = new AudioEndpoint("waveout:9", "Alias speakers", "persistent-a", "Alias speakers");
            Check(AudioOutputSelection.Resolve(new[] { renamed, duplicateIdentity }, full.Name, "persistent-a") == null,
                "Ambiguous stable identities cannot silently choose a numeric route.");
            var malformedIdentity = new AudioEndpoint("waveout:-1", full.Name, "persistent-a", legacy);
            Check(AudioOutputSelection.Resolve(new[] { malformedIdentity, sameCaption }, full.Name, "persistent-a") == null,
                "A malformed current endpoint cannot satisfy a saved stable identity.");
            Check(AudioOutputSelection.PersistentIdentity(virtualOutput) == null && AudioOutputSelection.PersistentIdentity(renamed) == "persistent-a",
                "Numeric legacy identities are not persisted as if they were stable device IDs.");
            Console.WriteLine("Audio output selection checks passed: " + checks + ". No clipboard or audio APIs were invoked.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static AudioEndpoint Endpoint(string id, string name) { return new AudioEndpoint(id, name); }
    private static void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }
}
