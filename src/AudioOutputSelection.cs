using System;
using System.Globalization;
using System.Text;

namespace HorizonClient
{
    // A WinMM index is only an enumeration position. Prefer stable endpoint
    // identity; old choices use a unique exact caption, never yesterday's index.
    internal static class AudioOutputSelection
    {
        internal static AudioEndpoint Resolve(AudioEndpoint[] outputs, string storedName)
        { return Resolve(outputs, storedName, null); }

        internal static AudioEndpoint Resolve(AudioEndpoint[] outputs, string storedName, string persistentId)
        {
            if (outputs == null) return null;
            if (!String.IsNullOrEmpty(persistentId))
            {
                AudioEndpoint match = null;
                foreach (AudioEndpoint endpoint in outputs)
                {
                    if (!Valid(endpoint) || !String.Equals(PersistentIdentity(endpoint), persistentId, StringComparison.Ordinal)) continue;
                    if (match != null) return null;
                    match = endpoint;
                }
                // An explicit stable identity must never fall back to a different
                // device which happens to have the previous caption or index.
                return match;
            }
            bool explicitChoice = !String.IsNullOrEmpty(storedName);
            AudioEndpoint selected = null;
            foreach (AudioEndpoint endpoint in outputs)
            {
                if (!Valid(endpoint)) continue;
                if (explicitChoice)
                {
                    if (!String.Equals(endpoint.Name, storedName, StringComparison.Ordinal)) continue;
                    if (selected != null) return null;
                    selected = endpoint;
                }
                else if (IsHorizonOutput(endpoint.Name)) return endpoint;
            }
            if (selected != null || !explicitChoice) return selected;
            // Existing installations saved the old WinMM 31-character caption.
            // Migrate it only when exactly one full endpoint name matches.
            foreach (AudioEndpoint endpoint in outputs)
            {
                if (!Valid(endpoint) || !String.Equals(endpoint.LegacyName, storedName, StringComparison.Ordinal)) continue;
                if (selected != null) return null;
                selected = endpoint;
            }
            return selected;
        }

        internal static bool IsHorizonOutput(string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            StringBuilder normalized = new StringBuilder(name.Length);
            foreach (char value in name) if (Char.IsLetterOrDigit(value)) normalized.Append(Char.ToLowerInvariant(value));
            string text = normalized.ToString();
            return (text.Contains("vmware") && (text.Contains("virtualaudio") || text.Contains("devtap"))) ||
                text.Contains("omnissa") || text.Contains("horizon");
        }

        private static bool Valid(AudioEndpoint endpoint)
        {
            return endpoint != null && !String.IsNullOrEmpty(endpoint.Name) && IsPlaybackId(endpoint.Id);
        }

        internal static bool IsPlaybackId(string id)
        {
            if (String.IsNullOrEmpty(id) || !id.StartsWith("waveout:", StringComparison.Ordinal)) return false;
            uint index;
            return UInt32.TryParse(id.Substring(8), NumberStyles.None, CultureInfo.InvariantCulture, out index) && index <= 65535;
        }

        internal static string PersistentIdentity(AudioEndpoint endpoint)
        {
            string id = endpoint == null ? null : endpoint.PersistentId;
            // Older/fake endpoints may expose their numeric index as identity.
            return String.IsNullOrWhiteSpace(id) || id.StartsWith("waveout:", StringComparison.Ordinal) ? null : id;
        }
    }
}
