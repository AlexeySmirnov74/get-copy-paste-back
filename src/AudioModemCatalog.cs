using System;

namespace HorizonClient
{
    internal enum AudioModemKind { Fsk = 0, Quiet = 1, Aicodix = 2 }

    internal static class AudioModemCatalog
    {
        internal const AudioModemKind Default = AudioModemKind.Aicodix;
        internal static string GetId(AudioModemKind kind)
        {
            switch (kind)
            {
                case AudioModemKind.Fsk: return "fsk";
                case AudioModemKind.Quiet: return "quiet";
                case AudioModemKind.Aicodix: return "aicodix";
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }
        internal static string GetCaption(AudioModemKind kind)
        {
            switch (kind)
            {
                case AudioModemKind.Fsk: return UiText.Get("Совместимый FSK", "Compatible FSK");
                case AudioModemKind.Quiet: return "Quiet (OFDM)";
                case AudioModemKind.Aicodix: return "aicodix (OFDM)";
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }
        internal static bool TryParse(string value, out AudioModemKind kind)
        {
            kind = AudioModemKind.Fsk;
            if (String.Equals(value, "fsk", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(value, "quiet", StringComparison.OrdinalIgnoreCase)) { kind = AudioModemKind.Quiet; return true; }
            if (String.Equals(value, "aicodix", StringComparison.OrdinalIgnoreCase)) { kind = AudioModemKind.Aicodix; return true; }
            return false;
        }
    }
}
