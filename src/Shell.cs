using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Seek
{
    /// <summary>The same icons Explorer shows, cached per file type.</summary>
    internal static class ShellIcons
    {
        // Types whose icon lives inside the file itself, so each file needs its own lookup.
        static readonly HashSet<string> OwnIcon = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr",
        };

        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource For(Hit hit)
        {
            string extension = "";
            if (!hit.IsFolder)
            {
                int dot = hit.Name.LastIndexOf('.');
                if (dot >= 0) extension = hit.Name.Substring(dot);
            }
            bool own = OwnIcon.Contains(extension);
            string key = hit.IsFolder ? "<folder>" : own ? hit.Path : extension;

            ImageSource image;
            if (cache.TryGetValue(key, out image)) return image;
            if (cache.Count > 512) cache.Clear();
            image = own ? Load(hit.Path, 0x80, false) : Load(hit.IsFolder ? "folder" : "file" + extension, hit.IsFolder ? 0x10u : 0x80u, true);
            cache[key] = image;
            return image;
        }

        static ImageSource Load(string path, uint attributes, bool byTypeOnly)
        {
            var info = new Shell32.SHFILEINFO();
            uint flags = Shell32.SHGFI_ICON | Shell32.SHGFI_LARGEICON | (byTypeOnly ? Shell32.SHGFI_USEFILEATTRIBUTES : 0u);
            if (Shell32.SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf(info), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return null;
            }
            finally
            {
                Shell32.DestroyIcon(info.hIcon);
            }
        }
    }

    /// <summary>Short, human dates for the result list.</summary>
    internal static class Friendly
    {
        static readonly CultureInfo English = CultureInfo.InvariantCulture;

        public static string When(long fileTime)
        {
            if (fileTime <= 0) return "";
            DateTime time = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
            DateTime now = DateTime.Now;
            TimeSpan ago = now - time;
            if (ago.TotalMinutes < 1) return "just now";
            if (ago.TotalMinutes < 60) return (int)ago.TotalMinutes + " min ago";
            if (time.Date == now.Date) return (int)ago.TotalHours + " h ago";
            if (time.Date == now.Date.AddDays(-1)) return "Yesterday";
            if (ago.TotalDays < 7) return time.ToString("dddd", English);
            if (time.Year == now.Year) return time.ToString("d MMM", English);
            return time.ToString("d MMM yyyy", English);
        }

        public static string Full(long fileTime)
        {
            return fileTime <= 0 ? "" : DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("d MMM yyyy, HH:mm", English);
        }
    }

    /// <summary>The per-user "run at sign-in" entry, toggled from the tray menu.</summary>
    internal static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Seek";

        /// <summary>Exists once the user has switched autostart off, so we stop turning it back on.</summary>
        public static string OptOutFile;

        /// <summary>
        /// Autostart is on unless the user turned it off. Re-registering on every launch also
        /// keeps the entry pointing at wherever Seek.exe lives now, so moving the exe just works.
        /// </summary>
        public static void Apply()
        {
            if (OptOutFile != null && File.Exists(OptOutFile)) return;
            Register(true);
        }

        public static bool Enabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(ValueName) != null;
            }
            set
            {
                Register(value);
                if (OptOutFile == null) return;
                if (value) File.Delete(OptOutFile);
                else File.WriteAllText(OptOutFile, "Seek will not start with Windows. Delete this file or use the tray menu to undo.");
            }
        }

        static void Register(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(ValueName, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --background");
                else key.DeleteValue(ValueName, false);
            }
        }
    }
}
