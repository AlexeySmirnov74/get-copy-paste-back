using System;
using System.Drawing;
using System.Windows.Forms;

namespace HorizonClient
{
    internal sealed partial class ServerForm
    {
        private bool HasCreatedCamera { get { return cameraLookupKnown && cameraExists; } }

        private void InitializeCameraAccess()
        {
            serverTabs.Selecting += delegate(object sender, TabControlCancelEventArgs args) {
                if ((args.TabPage == workTab || args.TabPage == transferLogTab) && !HasCreatedCamera) args.Cancel = true;
            };
            // Native TabControl still permits selecting a disabled TabPage.
            // Selecting guards navigation; owner drawing makes the state visible.
            serverTabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            serverTabs.DrawItem += delegate(object sender, DrawItemEventArgs args) {
                if (args.Index < 0 || args.Index >= serverTabs.TabCount) return;
                TabPage page = serverTabs.TabPages[args.Index];
                using (var background = new SolidBrush(args.Index == serverTabs.SelectedIndex ? SystemColors.Window : SystemColors.Control)) args.Graphics.FillRectangle(background, args.Bounds);
                TextRenderer.DrawText(args.Graphics, page.Text, serverTabs.Font, args.Bounds, page.Enabled ? SystemColors.ControlText : SystemColors.GrayText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            };
            UpdateCameraAccess(false);
        }

        private void UpdateCameraAccess(bool testsEnabled = true)
        {
            bool exists = HasCreatedCamera;
            bool changed = workTab.Enabled != exists || transferLogTab.Enabled != exists;
            workTab.Enabled = transferLogTab.Enabled = exists;
            if (!exists && (serverTabs.SelectedTab == workTab || serverTabs.SelectedTab == transferLogTab)) serverTabs.SelectedTab = settingsTab;
            benchmarkPanel.Enabled = exists && ready && !preparing && testsEnabled && !IsClosing;
            if (changed) serverTabs.Invalidate();
        }
    }
}
