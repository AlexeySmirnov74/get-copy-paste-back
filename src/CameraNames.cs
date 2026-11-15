using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace HorizonClient
{
    // Server label suggestions and the client's discovery candidate filter;
    // a name never proves ownership. Sources and scope are recorded in
    // defaults/CameraNameSources.txt; their order is not a popularity ranking.
    internal static class CameraNames
    {
        private static readonly string[] Values =
        {
            "Logitech C270 HD Webcam",
            "Logitech C310 HD Webcam",
            "Logitech C505 HD Webcam",
            "Logitech C505e HD Webcam",
            "Logitech C920 Pro HD Webcam",
            "Logitech C920s Pro HD Webcam",
            "Logitech C920e Business Webcam",
            "Logitech C922 Pro Stream Webcam",
            "Logitech C925e Business Webcam",
            "Logitech C930e Business Webcam",
            "Logitech Brio 4K",
            "Logitech Brio 100",
            "Logitech Brio 105",
            "Logitech Brio 300",
            "Logitech Brio 305",
            "Logitech Brio 500",
            "Logitech Brio 505",
            "Logitech MX Brio",
            "Logitech MX Brio 705 for Business",
            "Logitech StreamCam",
            "Razer Kiyo",
            "Razer Kiyo X",
            "Razer Kiyo Pro",
            "Razer Kiyo Pro Ultra",
            "Razer Kiyo V2",
            "Razer Kiyo V2 X",
            "Razer Kiyo V2 Pro",
            "Elgato Facecam",
            "Elgato Facecam Pro",
            "Elgato Facecam Neo",
            "Elgato Facecam MK.2",
            "Elgato Facecam 4K",
            "Anker PowerConf C200",
            "Anker PowerConf C300",
            "Anker PowerConf C302",
            "AnkerWork C310 Webcam",
            "Insta360 Link",
            "Insta360 Link 2",
            "Insta360 Link 2C",
            "Insta360 Link 2 Pro",
            "Insta360 Link 2C Pro",
            "OBSBOT Tiny",
            "OBSBOT Tiny 4K",
            "OBSBOT Tiny 2",
            "OBSBOT Tiny 2 Lite",
            "OBSBOT Tiny SE",
            "OBSBOT Tiny 3",
            "OBSBOT Tiny 3 Lite",
            "OBSBOT Meet 2",
            "OBSBOT Meet SE",
            "OBSBOT Meet Flip",
            "OBSBOT Meet SE 2",
            "AVerMedia PW515 4K Ultra HD Webcam",
            "AVerMedia Live Streamer CAM 513",
            "AVerMedia Live Streamer CAM 313",
            "AVerMedia PW315 Wide Angle Webcam",
            "AVerMedia PW313D DUALCAM",
            "AVerMedia PW310P Autofocus Webcam",
            "Creative Live! Cam Sync HD",
            "Creative Live! Cam Chat HD",
            "Creative Live! Cam Connect HD",
            "Creative Live! Cam Connect HD 1080",
            "Creative Live! Cam Sync 1080p",
            "Creative Live! Cam Sync 1080p V2",
            "Creative Live! Cam Sync V3",
            "Creative Live! Cam Sync 4K",
            "Trust Trino HD Video Webcam",
            "Trust Exis Webcam",
            "Trust Tolar Full HD Webcam",
            "Trust Teza 4K Ultra HD Webcam",
            "Trust Tyro Full HD Webcam",
            "Trust Taxon QHD Webcam",
            "Trust GXT 1160 Vero Streaming Webcam",
            "Trust SpotLight Webcam",
            "Trust eLight Full HD Webcam",
            "Trust Primo Webcam",
            "Dell UltraSharp Webcam WB7022",
            "Dell Pro Webcam WB5023",
            "Dell Webcam WB3023",
            "HP 320 FHD Webcam",
            "HP 325 FHD Webcam",
            "HP 430 FHD Webcam",
            "HP 435 FHD Webcam",
            "HP 620 FHD Webcam",
            "HP 625 FHD Webcam",
            "HP 960 4K Streaming Webcam",
            "HP 965 4K Streaming Webcam",
            "Lenovo 300 FHD Webcam",
            "Lenovo 310 FHD Webcam",
            "Lenovo 500 FHD Webcam",
            "Lenovo 510 FHD Webcam",
            "Lenovo 4K Pro Webcam",
            "Lenovo Essential FHD Webcam",
            "Lenovo Performance FHD Webcam",
            "NexiGo N60 1080p Webcam",
            "NexiGo N660P Autofocus Webcam",
            "NexiGo N930AF Autofocus Webcam",
            "NexiGo N950P 4K Webcam",
            "NexiGo N960E Streaming Webcam",
            "NexiGo N680P Autofocus Webcam"
        };
        private static readonly ReadOnlyCollection<string> Catalog = Array.AsReadOnly(Values);
        private static readonly HashSet<string> Lookup = new HashSet<string>(Values, StringComparer.OrdinalIgnoreCase);

        internal static IList<string> Names { get { return Catalog; } }

        internal static bool Contains(string name) { return name != null && Lookup.Contains(name); }

        internal static string PickRandom()
        {
            byte[] bytes = new byte[4];
            using (RNGCryptoServiceProvider random = new RNGCryptoServiceProvider())
                return Values[ChooseIndex(Values.Length, delegate { random.GetBytes(bytes); return BitConverter.ToUInt32(bytes, 0); })];
        }

        internal static int ChooseIndex(int count, Func<uint> draw)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException("count");
            if (draw == null) throw new ArgumentNullException("draw");
            uint size = (uint)count;
            // The accepted interval has a length divisible by count. Rejecting
            // the short initial interval avoids modulo bias (96 values for 100).
            uint threshold = unchecked(0U - size) % size;
            uint value;
            do { value = draw(); } while (value < threshold);
            return (int)(value % size);
        }
    }
}
