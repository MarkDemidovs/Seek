using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Seek
{
    // CharSet.Unicode keeps the char buffers blittable, so the struct can be passed by pointer.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct FindData
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh;
        public uint AccessLow, AccessHigh;
        public uint WriteLow, WriteHigh;
        public uint SizeHigh, SizeLow;
        public uint Reserved0, Reserved1;
        public fixed char FileName[260];
        public fixed char AlternateFileName[14];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeData
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh;
        public uint AccessLow, AccessHigh;
        public uint WriteLow, WriteHigh;
        public uint SizeHigh, SizeLow;
    }

    internal enum StatResult { Missing, Found, Unknown }

    /// <summary>Raw file system access, fast enough to walk every drive on the machine.</summary>
    [SuppressUnmanagedCodeSecurity]
    internal static unsafe class Kernel
    {
        const uint AttrDirectory = 0x10;
        const uint AttrReparsePoint = 0x400;
        const uint TagMountPoint = 0xA0000003;
        const uint TagSymlink = 0xA000000C;
        const int FindExInfoBasic = 1;
        const int FindFirstExLargeFetch = 2;
        static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string fileName, int infoLevel, FindData* data, int searchOp, IntPtr filter, int flags);

        [DllImport("kernel32.dll")]
        static extern int FindNextFileW(IntPtr handle, FindData* data);

        [DllImport("kernel32.dll")]
        static extern int FindClose(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetFileAttributesExW(string fileName, int infoLevel, out FileAttributeData data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetLongPathNameW(string shortPath, StringBuilder longPath, int size);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        static extern int SetThreadPriority(IntPtr thread, int priority);

        /// <summary>Drops the calling thread to background CPU and disk priority.</summary>
        public static void EnterBackgroundMode()
        {
            SetThreadPriority(GetCurrentThread(), 0x00010000); // THREAD_MODE_BACKGROUND_BEGIN
        }

        public delegate void EntryHandler(string name, bool isFolder, long time);

        /// <summary>The name points into a buffer that is reused for the next entry.</summary>
        public delegate void RawEntryHandler(char* name, int length, bool isFolder, long time);

        public static bool ListFolder(string folder, long futureLimit, EntryHandler onEntry)
        {
            return ListFolder(folder, futureLimit, (name, length, isFolder, time) => onEntry(new string(name, 0, length), isFolder, time));
        }

        /// <summary>
        /// Lists one folder, skipping "." / ".." and folder links (junctions, symlinks) so the
        /// walk never loops or double counts. Returns false when the folder can't be opened.
        /// </summary>
        public static bool ListFolder(string folder, long futureLimit, RawEntryHandler onEntry)
        {
            FindData data;
            IntPtr handle = FindFirstFileExW(@"\\?\" + folder + @"\*", FindExInfoBasic, &data, 0, IntPtr.Zero, FindFirstExLargeFetch);
            if (handle == InvalidHandle) return false;
            try
            {
                do
                {
                    char* name = data.FileName;
                    if (name[0] == '.' && (name[1] == '\0' || (name[1] == '.' && name[2] == '\0'))) continue;
                    bool isFolder = (data.Attributes & AttrDirectory) != 0;
                    if (isFolder && (data.Attributes & AttrReparsePoint) != 0 && IsLinkTag(data.Reserved0)) continue;
                    int length = 0;
                    while (length < 260 && name[length] != '\0') length++;
                    long time = Recency(data.CreationLow, data.CreationHigh, data.WriteLow, data.WriteHigh, futureLimit);
                    onEntry(name, length, isFolder, time);
                }
                while (FindNextFileW(handle, &data) != 0);
            }
            finally
            {
                FindClose(handle);
            }
            return true;
        }

        /// <summary>Looks up one path. Folder links report as Unknown so callers ignore them.</summary>
        public static StatResult Stat(string path, long futureLimit, out bool isFolder, out long time)
        {
            FileAttributeData data;
            isFolder = false;
            time = 0;
            if (GetFileAttributesExW(@"\\?\" + path, 0, out data) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                return error == 2 || error == 3 ? StatResult.Missing : StatResult.Unknown;
            }
            isFolder = (data.Attributes & AttrDirectory) != 0;
            if (isFolder && (data.Attributes & AttrReparsePoint) != 0 && IsLink(path)) return StatResult.Unknown;
            time = Recency(data.CreationLow, data.CreationHigh, data.WriteLow, data.WriteHigh, futureLimit);
            return StatResult.Found;
        }

        /// <summary>Change notifications sometimes carry 8.3 names (PROGRA~1); expand them.</summary>
        public static string ExpandShortPath(string path)
        {
            if (path.IndexOf('~') < 0) return path;
            var buffer = new StringBuilder(1024);
            int length = GetLongPathNameW(path, buffer, buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
        }

        /// <summary>FILETIME one day from now; anything later is a bogus timestamp.</summary>
        public static long FutureLimit()
        {
            return DateTime.UtcNow.AddDays(1).ToFileTimeUtc();
        }

        /// <summary>
        /// "How recent" a file is: the later of created and modified. Copying or downloading a
        /// file keeps its old modified date but gives it a fresh created date, and both count.
        /// </summary>
        static long Recency(uint createdLow, uint createdHigh, uint writtenLow, uint writtenHigh, long futureLimit)
        {
            long created = ((long)createdHigh << 32) | createdLow;
            long written = ((long)writtenHigh << 32) | writtenLow;
            long newest = Math.Max(created, written);
            if (newest <= futureLimit) return newest;
            long other = Math.Min(created, written);
            return other <= futureLimit ? other : 0;
        }

        static bool IsLinkTag(uint tag)
        {
            return tag == TagMountPoint || tag == TagSymlink;
        }

        static bool IsLink(string path)
        {
            FindData data;
            IntPtr handle = FindFirstFileExW(@"\\?\" + path, FindExInfoBasic, &data, 0, IntPtr.Zero, 0);
            if (handle == InvalidHandle) return false;
            FindClose(handle);
            return IsLinkTag(data.Reserved0);
        }
    }

    /// <summary>Keeps Seek's physical memory use small and bounded.</summary>
    internal static class Memory
    {
        public const long Limit = 128L << 20;
        const uint HardMaxEnable = 0x4;
        const uint MinDisable = 0x2;

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessWorkingSetSizeEx(IntPtr process, IntPtr minimum, IntPtr maximum, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")]
        static extern bool EmptyWorkingSet(IntPtr process);

        /// <summary>
        /// Tells Windows never to let Seek hold more than Limit of RAM. Seek is built to stay well
        /// under it; this is the guarantee on top.
        /// </summary>
        public static bool Cap()
        {
            return SetProcessWorkingSetSizeEx(GetCurrentProcess(), new IntPtr(4L << 20), new IntPtr(Limit), HardMaxEnable | MinDisable);
        }

        /// <summary>Hands memory we aren't using right now back to Windows (e.g. while the bar is hidden).</summary>
        public static void Trim()
        {
            EmptyWorkingSet(GetCurrentProcess());
        }
    }

    internal static class User32
    {
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint VK_SPACE = 0x20;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int AccentBlurBehind = 3;
        const int WcaAccentPolicy = 19;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy
        {
            public int State;
            public int Flags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CompositionData
        {
            public int Attribute;
            public IntPtr Data;
            public int Size;
        }

        /// <summary>Frosted-glass blur behind the window (Windows 10 and 11).</summary>
        public static void EnableBlur(IntPtr hwnd)
        {
            var accent = new AccentPolicy { State = AccentBlurBehind };
            int size = Marshal.SizeOf(accent);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, buffer, false);
                var data = new CompositionData { Attribute = WcaAccentPolicy, Data = buffer, Size = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Keeps the bar out of Alt+Tab.</summary>
        public static void MakeToolWindow(IntPtr hwnd)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        }

        /// <summary>Windows only lets the foreground app hand out focus; borrow its input queue if needed.</summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            IntPtr current = GetForegroundWindow();
            if (current == hwnd) return;
            uint theirs = GetWindowThreadProcessId(current, IntPtr.Zero);
            uint ours = GetCurrentThreadId();
            if (theirs != 0 && theirs != ours)
            {
                AttachThreadInput(ours, theirs, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(ours, theirs, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
        }
    }

    internal static class Shell32
    {
        public const uint SHGFI_ICON = 0x100;
        public const uint SHGFI_LARGEICON = 0x0;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);
    }
}
