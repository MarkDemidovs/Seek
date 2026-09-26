using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Seek
{
    internal static class Program
    {
        const string InstanceName = @"Local\Seek.Instance.7c2e";
        const string ShowSignalName = @"Local\Seek.Show.7c2e";

        static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seek");

        [STAThread]
        static void Main(string[] args)
        {
            // One copy runs in the background. Launching Seek.exe again just pops the bar up.
            bool first;
            var instance = new Mutex(true, InstanceName, out first);
            if (!first)
            {
                WakeRunningCopy();
                return;
            }

            Directory.CreateDirectory(DataDir);
            Log.FilePath = Path.Combine(DataDir, "seek.log");
            if (!Memory.Cap()) Log.Write("Couldn't set the " + (Memory.Limit >> 20) + " MB memory limit");
            Exclusions.AddFolder(DataDir);
            string cachePath = Path.Combine(DataDir, "index.bin");
            bool firstRun = !File.Exists(cachePath);

            Startup.OptOutFile = Path.Combine(DataDir, "no-autostart");
            try
            {
                Startup.Apply();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            System.Windows.Forms.Application.EnableVisualStyles();
            // A small bar gains nothing from the GPU, and skipping the graphics driver saves ~15 MB.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Error(e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error(e.ExceptionObject as Exception);

            var index = new FileIndex(cachePath);
            var window = new SearchWindow(index);
            var tray = new Tray(window, index);

            var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            new Thread(() =>
            {
                while (true)
                {
                    showSignal.WaitOne();
                    window.Dispatcher.BeginInvoke(new Action(window.ShowBar));
                }
            }) { IsBackground = true, Name = "Seek wake" }.Start();

            index.Start();
            if (!args.Contains("--background")) app.Dispatcher.BeginInvoke(new Action(window.ShowBar));
            if (!window.HotkeyRegistered)
                tray.Notify("Alt+Space is taken by another app", "Click the Seek tray icon or run Seek.exe to search.");
            else if (firstRun)
                tray.Notify("Seek is indexing your drives", "It lives in the tray. Press Alt+Space any time to search.");

            app.Run();
            tray.Dispose();
            GC.KeepAlive(instance);
        }

        static void WakeRunningCopy()
        {
            User32.AllowSetForegroundWindow(-1); // ASFW_ANY: let the running copy take focus
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    using (var signal = EventWaitHandle.OpenExisting(ShowSignalName))
                    {
                        signal.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(100); // the first copy is still starting up
                }
            }
        }
    }
}
