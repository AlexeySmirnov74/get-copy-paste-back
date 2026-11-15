using System;
using System.Xml;

namespace HorizonClient
{
    internal sealed partial class ServerForm
    {
        private void InitializeColorModeEvents()
        {
            colorModesEnabled.CheckedChanged += delegate
            {
                if (restoringSettings) return;
                if (IsBenchmarkBusy || preparing || IsClosing)
                {
                    restoringSettings = true;
                    try { colorModesEnabled.Checked = !colorModesEnabled.Checked; }
                    finally { restoringSettings = false; }
                    return;
                }
                LogUiAction(colorModesEnabled.Name, "changed", "enabled", colorModesEnabled.Checked);
                VideoProfile previous = selectedProfile;
                RefreshTransferProfileChoices();
                if (selectedProfile != previous)
                {
                    // Repacketization changes the transfer identity so an old
                    // color receipt cannot complete this monochrome transfer.
                    ApplySelectedTransferProfile();
                    ScheduleClipboard();
                }
                SaveSettings();
            };
        }

        private void LoadColorModeSettings(XmlElement root)
        {
            bool enabled; int savedProfile;
            if (!Boolean.TryParse(root.GetAttribute("colorModesEnabled"), out enabled))
            {
                // Preserve an actual old experiment selection, while explicit
                // false/malformed flags retain ordinary behavior.
                enabled = !root.HasAttribute("colorModesEnabled") &&
                    Int32.TryParse(root.GetAttribute("videoProfile"), out savedProfile) &&
                    ColorVideoProtocol.Supports((VideoProfile)savedProfile);
            }
            colorModesEnabled.Checked = enabled;
        }
    }
}
