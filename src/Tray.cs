using System;
using System.Drawing;
using System.Reflection;
using WinForms = System.Windows.Forms;

namespace Seek
{
    /// <summary>Seek lives in the notification area so the index stays warm between searches.</summary>
    internal sealed class Tray : IDisposable
    {
        readonly WinForms.NotifyIcon icon;

        public Tray(SearchWindow window, FileIndex index)
        {
            var search = new WinForms.ToolStripMenuItem("Search", null, delegate { window.ShowBar(); });
            search.ShortcutKeyDisplayString = window.HotkeyRegistered ? "Alt+Space" : "";
            search.Font = new Font(search.Font, FontStyle.Bold);
            var rebuild = new WinForms.ToolStripMenuItem("Rebuild index", null, delegate { index.Rebuild(); });
            var startup = new WinForms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
            try
            {
                startup.Checked = Startup.Enabled;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            startup.CheckedChanged += delegate
            {
                try
                {
                    Startup.Enabled = startup.Checked;
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            };
            var exit = new WinForms.ToolStripMenuItem("Exit", null, delegate { System.Windows.Application.Current.Shutdown(); });

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.AddRange(new WinForms.ToolStripItem[] { search, rebuild, new WinForms.ToolStripSeparator(), startup, exit });

            icon = new WinForms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = window.HotkeyRegistered ? "Seek — Alt+Space to search" : "Seek — click to search",
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.MouseClick += (s, e) =>
            {
                // Let the taskbar finish handling the click first, or it takes focus straight back.
                if (e.Button == WinForms.MouseButtons.Left)
                    window.Dispatcher.BeginInvoke(new Action(window.ShowBar), System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        public void Notify(string title, string text)
        {
            icon.ShowBalloonTip(6000, title, text, WinForms.ToolTipIcon.None);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }

        static Icon LoadIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Seek.seek.ico"))
                return stream != null ? new Icon(stream, WinForms.SystemInformation.SmallIconSize) : SystemIcons.Application;
        }
    }
}
