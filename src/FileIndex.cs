using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace Seek
{
    /// <summary>One search result.</summary>
    internal sealed class Hit
    {
        public string Path;
        public string Name;
        public long Time;
        public bool IsFolder;

        /// <summary>The folder that holds this hit, for display.</summary>
        public string Folder
        {
            get
            {
                int cut = Path.LastIndexOf('\\');
                string folder = cut > 0 ? Path.Substring(0, cut) : Path;
                return folder.EndsWith(":") ? folder + "\\" : folder;
            }
        }
    }

    /// <summary>What a change notification told us about one path since the last full scan.</summary>
    internal sealed class Change
    {
        public string Path;
        public char[] Name;
        public long Time;
        public bool IsFolder;
        public bool Exists;
        public long Seq;
    }

    /// <summary>
    /// The live index: the last full scan plus whatever the file system watchers reported since.
    /// Searches read both; each fresh scan absorbs the watcher changes that came before it.
    /// </summary>
    internal sealed unsafe class FileIndex
    {
        const int Created = 1, Changed = 2, Deleted = 4;
        // Live changes are kept in memory; past this many, a rescan folds them into the index file.
        const int MaxPendingChanges = 50000;

        readonly object sync = new object();
        readonly string cachePath;
        readonly Dictionary<string, Change> changes = new Dictionary<string, Change>(StringComparer.OrdinalIgnoreCase);
        // Folders deleted or replaced since the last scan, with the Seq it happened at: anything
        // known beneath them from before that moment is gone.
        readonly Dictionary<string, long> removed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentQueue<KeyValuePair<string, int>> events = new ConcurrentQueue<KeyValuePair<string, int>>();
        readonly AutoResetEvent eventsQueued = new AutoResetEvent(false);
        readonly AutoResetEvent rescanRequested = new AutoResetEvent(false);
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly Timer rescanTimer;
        readonly object publishGate = new object();
        volatile Snapshot snapshot;
        volatile Scanner[] scanners;
        volatile bool complete;
        long seq;

        /// <summary>Raised (on a background thread) whenever results may have changed.</summary>
        public event Action Updated;

        public FileIndex(string cachePath)
        {
            this.cachePath = cachePath;
            rescanTimer = new Timer(delegate { rescanRequested.Set(); });
        }

        /// <summary>True once every drive has been indexed at least once (from cache or a scan).</summary>
        public bool Complete
        {
            get { return complete; }
        }

        /// <summary>Items found by the running scan, or -1 when no scan is running.</summary>
        public int ScanProgress
        {
            get
            {
                var running = scanners;
                if (running == null) return -1;
                int found = 0;
                foreach (var s in running) found += s.Found;
                return found;
            }
        }

        public void Start()
        {
            new Thread(IndexLoop) { IsBackground = true, Name = "Seek index", Priority = ThreadPriority.BelowNormal }.Start();
            new Thread(WatchLoop) { IsBackground = true, Name = "Seek watcher" }.Start();
        }

        public void Rebuild()
        {
            rescanRequested.Set();
        }

        /// <summary>Re-checks a path we believe is stale (e.g. it failed to open).</summary>
        public void Recheck(string path)
        {
            Enqueue(path, Deleted);
        }

        public void UseSnapshot(Snapshot s)
        {
            lock (sync) snapshot = s;
            complete = true;
        }

        public static List<string> FixedDrives()
        {
            var roots = new List<string>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady) roots.Add(drive.Name);
                }
                catch (IOException)
                {
                }
            }
            return roots;
        }

        // ---- Searching --------------------------------------------------------------------

        /// <param name="scope">Only search inside this folder (and its subfolders); null for everywhere.
        /// An empty query inside a folder lists its newest items.</param>
        public List<Hit> Search(string text, string scope, int max, CancellationToken cancel)
        {
            var result = new List<Hit>();
            var query = Query.Parse(text);
            if (query == null && scope != null) query = Query.Parse("*");
            if (query == null) return result;

            var exact = new List<Hit>();
            var others = new List<Hit>();
            lock (sync)
            {
                var snap = snapshot;
                if (snap != null)
                {
                    bool[] inside = scope == null ? null : FoldersInside(snap, scope);
                    if (!SearchSnapshot(snap, query, inside, max, exact, others, cancel)) return null;
                }
                SearchChanges(query, scope == null ? null : scope.TrimEnd('\\') + "\\", exact, others);
            }

            exact.Sort(Newest);
            others.Sort(Newest);
            foreach (var hit in exact)
                if (result.Count < max) result.Add(hit);
            foreach (var hit in others)
                if (result.Count < max) result.Add(hit);
            return result;
        }

        static int Newest(Hit a, Hit b)
        {
            return b.Time.CompareTo(a.Time);
        }

        sealed class ScopeTable
        {
            public Snapshot Snapshot;
            public string Folder;
            public bool[] Inside;
        }

        ScopeTable lastScope;

        /// <summary>
        /// For each snapshot folder: is it the scope folder or somewhere below it? Folder ids are
        /// handed out parent-before-child, so a single pass settles every folder.
        /// </summary>
        bool[] FoldersInside(Snapshot s, string folder)
        {
            var cached = lastScope;
            if (cached != null && cached.Snapshot == s && string.Equals(cached.Folder, folder, StringComparison.OrdinalIgnoreCase)) return cached.Inside;

            string target = folder.TrimEnd('\\');
            int count = s.DirParent.Length;
            var matched = new int[count]; // length of the scope path this folder's path is a prefix of, 0 if none
            var inside = new bool[count];
            for (int d = 0; d < count; d++)
            {
                int parent = s.DirParent[d];
                int at = parent < 0 ? 0 : matched[parent] > 0 && matched[parent] < target.Length ? matched[parent] + 1 : -1;
                if (at > 0 && target[at - 1] != '\\') at = -1;
                if (at >= 0)
                {
                    string name = s.DirName(d); // only for the few folders along the scope's path
                    int end = at + name.Length;
                    if (end <= target.Length && (end == target.Length || target[end] == '\\')
                        && string.Compare(target, at, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0) matched[d] = end;
                }
                inside[d] = matched[d] == target.Length || (parent >= 0 && inside[parent]);
            }
            lastScope = new ScopeTable { Snapshot = s, Folder = folder, Inside = inside };
            return inside;
        }

        // Few threads, each with its own block buffer: enough to keep up with Windows' file
        // cache while keeping memory small. Buffers are reused for every search and index.
        static readonly int SearchThreads = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));

        sealed class Scratch
        {
            public byte[] Buffer;
            public readonly char[] Chars = new char[1024];
        }

        readonly ConcurrentBag<Scratch> scratch = new ConcurrentBag<Scratch>();

        /// <summary>
        /// Items are newest first, so the first matches found are the answer. Blocks are read
        /// and searched in parallel; as soon as one block alone has enough hits, every later
        /// (older) block gives up. Returns false if cancelled.
        /// </summary>
        bool SearchSnapshot(Snapshot s, Query query, bool[] inside, int max, List<Hit> exact, List<Hit> others, CancellationToken cancel)
        {
            int blocks = s.BlockCount;
            var exactParts = new List<Hit>[blocks];
            var otherParts = new List<Hit>[blocks];
            var bar = new ChunkBar();
            Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = SearchThreads }, b =>
            {
                if (b > bar.EnoughAt || cancel.IsCancellationRequested) return;
                var e = new List<Hit>();
                var o = new List<Hit>();
                Scratch space;
                if (!scratch.TryTake(out space)) space = new Scratch();
                var stream = s.Rent();
                try
                {
                    if (SearchBlock(s, stream, space, b, query, inside, max, e, o, cancel, bar)) bar.Satisfied(b);
                }
                finally
                {
                    s.Return(stream);
                    scratch.Add(space);
                }
                exactParts[b] = e;
                otherParts[b] = o;
            });
            if (cancel.IsCancellationRequested) return false;
            for (int b = 0; b < blocks && b <= bar.EnoughAt; b++)
            {
                if (exactParts[b] == null) continue;
                exact.AddRange(exactParts[b]);
                others.AddRange(otherParts[b]);
            }
            return true;
        }

        /// <summary>The earliest block that has found enough on its own.</summary>
        sealed class ChunkBar
        {
            int enoughAt = int.MaxValue;

            public int EnoughAt
            {
                get { return Volatile.Read(ref enoughAt); }
            }

            public void Satisfied(int chunk)
            {
                int seen;
                while (chunk < (seen = Volatile.Read(ref enoughAt)) && Interlocked.CompareExchange(ref enoughAt, chunk, seen) != seen) { }
            }
        }

        /// <summary>
        /// Searches one block of records (layout in Record). Returns true when this block alone
        /// has enough hits: max matches, or for name searches max exact-name matches (other
        /// matches only rank after those, so we keep looking for exact names further down).
        /// </summary>
        bool SearchBlock(Snapshot s, FileStream stream, Scratch space, int block, Query query, bool[] inside, int max, List<Hit> exact, List<Hit> others, CancellationToken cancel, ChunkBar bar)
        {
            int length = s.Load(stream, block, ref space.Buffer);
            char[] name = space.Chars;
            int folders = s.DirParent.Length;
            bool ranked = query.RanksExact;
            int wanted = query.Length;
            fixed (byte* start = space.Buffer)
            {
                byte* p = start, end = start + length;
                for (int i = 0; p + 11 <= end || (p + 9 <= end && p[8] != 255); i++)
                {
                    if ((i & 0xFFF) == 0 && (cancel.IsCancellationRequested || block > bar.EnoughAt)) return false;
                    int parent = *(int*)p;
                    uint time = *(uint*)(p + 4);
                    int bytes = p[8];
                    byte* text = p + 9;
                    if (bytes == 255)
                    {
                        bytes = *(ushort*)(p + 9);
                        text = p + 11;
                    }
                    p = text + bytes;
                    if (p > end) break; // damaged block
                    int folder = parent & ~Snapshot.FolderBit;
                    if (folder >= folders || (inside != null && !inside[folder])) continue;
                    bool onlyExact = ranked && others.Count >= max;
                    // An exact name has as many chars as the query; in UTF-8 that's 1-3 bytes each.
                    if (onlyExact && (bytes < wanted || bytes > 3 * wanted)) continue;

                    // Almost every name is plain ASCII: match those on the raw bytes, and only
                    // decode the rest (or a hit, to show it).
                    bool ascii = Utf8.IsAscii(text, bytes);
                    int n = ascii ? bytes : Utf8.Decode(text, bytes, name);
                    if (ranked && (ascii ? query.IsExact(text, n) : query.IsExact(name, 0, n)))
                    {
                        if (ascii) Utf8.Decode(text, bytes, name);
                        if (TryAdd(s, parent, time, name, n, exact) && exact.Count >= max) return true;
                        continue;
                    }
                    if (onlyExact) continue;
                    if (others.Count >= max) return true;
                    if (ascii ? query.IsMatch(text, n) : query.IsMatch(name, 0, n))
                    {
                        if (ascii) Utf8.Decode(text, bytes, name);
                        if (TryAdd(s, parent, time, name, n, others) && !ranked && others.Count >= max) return true;
                    }
                }
            }
            return false;
        }

        bool TryAdd(Snapshot s, int parent, uint time, char[] chars, int length, List<Hit> into)
        {
            string name = new string(chars, 0, length);
            string path = s.DirPath(parent & ~Snapshot.FolderBit) + "\\" + name;
            if (changes.Count > 0 && changes.ContainsKey(path)) return false; // newer info below
            if (removed.Count > 0 && IsRemoved(path, 0)) return false;
            into.Add(new Hit { Path = path, Name = name, Time = Record.ToFileTime(time), IsFolder = parent < 0 });
            return true;
        }

        void SearchChanges(Query query, string scopePrefix, List<Hit> exact, List<Hit> others)
        {
            foreach (var change in changes.Values)
            {
                if (!change.Exists) continue;
                if (scopePrefix != null && !change.Path.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                char[] name = change.Name;
                bool isExact = query.RanksExact && query.IsExact(name, 0, name.Length);
                if (!isExact && !query.IsMatch(name, 0, name.Length)) continue;
                if (removed.Count > 0 && IsRemoved(change.Path, change.Seq)) continue;
                (isExact ? exact : others).Add(new Hit { Path = change.Path, Name = new string(name), Time = change.Time, IsFolder = change.IsFolder });
            }
        }

        /// <summary>True if a folder above this path was removed after we learned about it.</summary>
        bool IsRemoved(string path, long learnedAt)
        {
            for (int cut = path.LastIndexOf('\\'); cut > 0; cut = path.LastIndexOf('\\', cut - 1))
            {
                long removedAt;
                if (removed.TryGetValue(path.Substring(0, cut), out removedAt) && removedAt > learnedAt) return true;
            }
            return false;
        }

        // ---- Full scans -------------------------------------------------------------------

        void IndexLoop()
        {
            Query.Parse("warm up"); // builds the case/accent table now, not on the first keystroke
            if (cachePath == null) return; // nowhere to keep an index
            var cached = Snapshot.Open(cachePath);
            if (cached != null)
            {
                snapshot = cached;
                complete = true;
                RaiseUpdated();
            }
            StartWatchers();
            while (true)
            {
                try
                {
                    ScanAll();
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
                rescanRequested.WaitOne();
            }
        }

        /// <summary>
        /// Scans every drive at once, each with its own threads so a slow hard disk never holds
        /// up a fast SSD. On the very first run each drive becomes searchable the moment it's
        /// done; later rescans run at background priority and swap in when all are finished.
        /// </summary>
        void ScanAll()
        {
            long startSeq;
            lock (sync) startSeq = seq;
            bool firstRun = !complete;
            var roots = FixedDrives();
            var runs = new Scanner[roots.Count];
            var parts = new Scanner.Result[roots.Count];
            string spillFolder = Path.GetDirectoryName(cachePath);
            for (int k = 0; k < roots.Count; k++) runs[k] = new Scanner(new[] { roots[k] }, !firstRun, spillFolder);
            scanners = runs;
            try
            {
                if (firstRun)
                {
                    // All drives at once, each searchable as soon as it's done.
                    var threads = new Thread[runs.Length];
                    for (int k = 0; k < runs.Length; k++)
                    {
                        int drive = k;
                        threads[k] = new Thread(() =>
                        {
                            // An exception escaping a thread would take the whole app down.
                            try
                            {
                                PublishPartial(parts, drive, runs[drive].Run(CancellationToken.None));
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex);
                            }
                        }) { IsBackground = true, Name = "Seek drive scan" };
                        threads[k].Start();
                    }
                    foreach (var thread in threads) thread.Join();
                }
                else
                {
                    // A routine rescan isn't in a hurry: one drive at a time keeps memory low.
                    for (int k = 0; k < runs.Length; k++)
                    {
                        try
                        {
                            parts[k] = runs[k].Run(CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex);
                        }
                    }
                }

                var finished = Finished(parts);
                if (finished.Count < parts.Length && snapshot != null && !firstRun) return; // keep the old index over a partial one
                Install(finished, startSeq);
                complete = true;
            }
            finally
            {
                scanners = null;
                foreach (var part in parts)
                    if (part != null) part.Dispose(); // deletes the spill files
            }
            RaiseUpdated();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            Memory.Trim();
        }

        void PublishPartial(Scanner.Result[] parts, int drive, Scanner.Result part)
        {
            lock (publishGate)
            {
                parts[drive] = part;
                Install(Finished(parts), -1);
            }
            RaiseUpdated();
        }

        /// <summary>
        /// Writes a new index file from finished scans and swaps it in for the current one.
        /// With startSeq >= 0 (a complete scan), live changes from before the scan are dropped:
        /// the new index already reflects them.
        /// </summary>
        void Install(List<Scanner.Result> results, long startSeq)
        {
            string fresh = cachePath + ".new";
            Scanner.Combine(results, fresh);
            lock (sync)
            {
                var old = snapshot;
                snapshot = null;
                if (old != null) old.Dispose();
                if (File.Exists(cachePath)) File.Delete(cachePath);
                File.Move(fresh, cachePath);
                snapshot = Snapshot.Open(cachePath);
                if (snapshot == null) Log.Write("Couldn't open the index that was just written");
                if (startSeq >= 0)
                {
                    RemoveUpTo(changes, startSeq);
                    RemoveUpTo(removed, startSeq);
                }
            }
        }

        static List<Scanner.Result> Finished(Scanner.Result[] parts)
        {
            var done = new List<Scanner.Result>();
            foreach (var p in parts)
                if (p != null) done.Add(p);
            return done;
        }

        static void RemoveUpTo(Dictionary<string, Change> map, long upTo)
        {
            var stale = new List<string>();
            foreach (var entry in map)
                if (entry.Value.Seq <= upTo) stale.Add(entry.Key);
            foreach (string key in stale) map.Remove(key);
        }

        static void RemoveUpTo(Dictionary<string, long> map, long upTo)
        {
            var stale = new List<string>();
            foreach (var entry in map)
                if (entry.Value <= upTo) stale.Add(entry.Key);
            foreach (string key in stale) map.Remove(key);
        }

        void RaiseUpdated()
        {
            var handler = Updated;
            if (handler != null) handler();
        }

        // ---- Live changes -----------------------------------------------------------------

        void StartWatchers()
        {
            foreach (string root in FixedDrives())
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    };
                    watcher.Created += (s, e) => Enqueue(e.FullPath, Created);
                    watcher.Changed += (s, e) => Enqueue(e.FullPath, Changed);
                    watcher.Deleted += (s, e) => Enqueue(e.FullPath, Deleted);
                    watcher.Renamed += (s, e) =>
                    {
                        Enqueue(e.OldFullPath, Deleted);
                        Enqueue(e.FullPath, Created);
                    };
                    // Too many changes at once: the buffer overflowed and we lost some. Rescan.
                    watcher.Error += (s, e) => rescanTimer.Change(15000, Timeout.Infinite);
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            }
        }

        void Enqueue(string path, int kind)
        {
            events.Enqueue(new KeyValuePair<string, int>(path, kind));
            eventsQueued.Set();
        }

        void WatchLoop()
        {
            while (true)
            {
                eventsQueued.WaitOne();
                Thread.Sleep(250); // let a burst settle so repeated events collapse into one

                var kinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();
                KeyValuePair<string, int> e;
                while (events.TryDequeue(out e))
                {
                    int kind;
                    if (kinds.TryGetValue(e.Key, out kind))
                    {
                        kinds[e.Key] = kind | e.Value;
                    }
                    else
                    {
                        kinds[e.Key] = e.Value;
                        order.Add(e.Key);
                    }
                }

                int applied = 0;
                foreach (string path in order)
                {
                    if (Exclusions.SkipPath(path)) continue;
                    try
                    {
                        if (Apply(path, kinds[path])) applied++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex);
                    }
                }
                if (applied == 0) continue;

                bool bloated;
                lock (sync) bloated = changes.Count + removed.Count > MaxPendingChanges;
                if (bloated) rescanTimer.Change(0, Timeout.Infinite);
                RaiseUpdated();
            }
        }

        /// <summary>Brings one path up to date by looking at what is on disk right now.</summary>
        bool Apply(string path, int kind)
        {
            path = Kernel.ExpandShortPath(path);
            bool isFolder;
            long time;
            var state = Kernel.Stat(path, Kernel.FutureLimit(), out isFolder, out time);
            if (state == StatResult.Unknown) return false;
            bool exists = state == StatResult.Found;

            // A folder that appeared (created, moved or renamed in) brings its contents along.
            List<Change> contents = null;
            if (exists && isFolder && (kind & (Created | Deleted)) != 0) contents = ListTree(path);

            int cut = path.LastIndexOf('\\');
            lock (sync)
            {
                if ((kind & Deleted) != 0 || !exists) removed[path] = ++seq;
                changes[path] = new Change
                {
                    Path = path,
                    Name = path.Substring(cut + 1).ToCharArray(),
                    Time = time,
                    IsFolder = isFolder,
                    Exists = exists,
                    Seq = ++seq,
                };
                if (contents != null)
                {
                    foreach (var change in contents)
                    {
                        change.Seq = ++seq;
                        changes[change.Path] = change;
                    }
                }
            }
            return true;
        }

        static List<Change> ListTree(string root)
        {
            var found = new List<Change>();
            var folders = new Stack<string>();
            folders.Push(root);
            long limit = Kernel.FutureLimit();
            while (folders.Count > 0 && found.Count < MaxPendingChanges)
            {
                string folder = folders.Pop();
                Kernel.ListFolder(folder, limit, (name, isFolder, time) =>
                {
                    string path = folder + "\\" + name;
                    if (isFolder)
                    {
                        if (Exclusions.SkipFolder(name, path)) return;
                        folders.Push(path);
                    }
                    found.Add(new Change { Path = path, Name = name.ToCharArray(), Time = time, IsFolder = isFolder, Exists = true });
                });
            }
            return found;
        }
    }

    /// <summary>
    /// What the user typed, compiled for fast matching against file names:
    ///   .pdf          name ends with ".pdf"
    ///   thing*.pdf    wildcards: * is any run of characters, ? is one character (whole name)
    ///   report        name contains "report"; names that are exactly "report" come first
    /// All matching ignores case and accents.
    /// </summary>
    internal sealed unsafe class Query
    {
        enum Mode { Contains, Suffix, Wildcard }

        static readonly char[] Fold = BuildFoldTable();

        readonly Mode mode;
        readonly char[] pattern;
        readonly bool hasStar;
        readonly int head;      // literal characters before the first *
        readonly int tail;      // literal characters after the last *
        readonly int minLength; // non-* characters

        Query(Mode mode, char[] pattern)
        {
            this.mode = mode;
            this.pattern = pattern;
            int firstStar = Array.IndexOf(pattern, '*');
            hasStar = firstStar >= 0;
            head = hasStar ? firstStar : pattern.Length;
            tail = hasStar ? pattern.Length - 1 - Array.LastIndexOf(pattern, '*') : 0;
            foreach (char c in pattern)
                if (c != '*') minLength++;
        }

        public bool RanksExact
        {
            get { return mode == Mode.Contains; }
        }

        /// <summary>Characters in the (folded) pattern.</summary>
        public int Length
        {
            get { return pattern.Length; }
        }

        /// <summary>Null when there's nothing to search for yet (blank, or a lone ".").</summary>
        public static Query Parse(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || text == ".") return null;
            bool wildcard = text.IndexOf('*') >= 0 || text.IndexOf('?') >= 0;
            // ".pdf" means "ends in .pdf", so ".p*" should mean "extension starting with p",
            // not "name starting with .p".
            if (wildcard && text[0] == '.') text = "*" + text;

            var folded = new List<char>(text.Length);
            foreach (char c in text)
            {
                if (c == '*' && folded.Count > 0 && folded[folded.Count - 1] == '*') continue; // ** is *
                folded.Add(Fold[c]);
            }
            char[] pattern = folded.ToArray();

            if (wildcard) return new Query(Mode.Wildcard, pattern);
            if (text[0] == '.') return new Query(Mode.Suffix, pattern);
            return new Query(Mode.Contains, pattern);
        }

        public bool IsExact(char[] s, int offset, int length)
        {
            return length == pattern.Length && Same(s, offset, 0, length);
        }

        public bool IsMatch(char[] s, int offset, int length)
        {
            switch (mode)
            {
                case Mode.Suffix:
                    return length >= pattern.Length && Same(s, offset + length - pattern.Length, 0, pattern.Length);
                case Mode.Wildcard:
                    if (length < minLength) return false;
                    if (!hasStar) return length == pattern.Length && Same(s, offset, 0, length);
                    return Same(s, offset, 0, head)
                        && Same(s, offset + length - tail, pattern.Length - tail, tail)
                        && Wildcard(s, offset + head, length - head - tail, head, pattern.Length - tail);
                default:
                    return Contains(s, offset, length);
            }
        }

        /// <summary>Compares s against pattern[from..from+count), where ? matches anything.</summary>
        bool Same(char[] s, int offset, int from, int count)
        {
            for (int k = 0; k < count; k++)
            {
                char p = pattern[from + k];
                if (p != '?' && p != Fold[s[offset + k]]) return false;
            }
            return true;
        }

        bool Contains(char[] s, int offset, int length)
        {
            int n = pattern.Length;
            char first = pattern[0];
            for (int i = 0, last = length - n; i <= last; i++)
            {
                if (Fold[s[offset + i]] != first) continue;
                int k = 1;
                while (k < n && Fold[s[offset + i + k]] == pattern[k]) k++;
                if (k == n) return true;
            }
            return false;
        }

        // Byte versions of the matchers, for names that are pure ASCII (one byte per char), so
        // the search never has to decode the vast majority of names from UTF-8.

        public bool IsExact(byte* s, int length)
        {
            return length == pattern.Length && Same(s, 0, length);
        }

        public bool IsMatch(byte* s, int length)
        {
            switch (mode)
            {
                case Mode.Suffix:
                    return length >= pattern.Length && Same(s + length - pattern.Length, 0, pattern.Length);
                case Mode.Wildcard:
                    if (length < minLength) return false;
                    if (!hasStar) return length == pattern.Length && Same(s, 0, length);
                    return Same(s, 0, head)
                        && Same(s + length - tail, pattern.Length - tail, tail)
                        && Wildcard(s + head, length - head - tail, head, pattern.Length - tail);
                default:
                    return Contains(s, length);
            }
        }

        bool Same(byte* s, int from, int count)
        {
            for (int k = 0; k < count; k++)
            {
                char p = pattern[from + k];
                if (p != '?' && p != Fold[s[k]]) return false;
            }
            return true;
        }

        bool Contains(byte* s, int length)
        {
            int n = pattern.Length;
            char first = pattern[0];
            for (int i = 0, last = length - n; i <= last; i++)
            {
                if (Fold[s[i]] != first) continue;
                int k = 1;
                while (k < n && Fold[s[i + k]] == pattern[k]) k++;
                if (k == n) return true;
            }
            return false;
        }

        bool Wildcard(byte* s, int length, int from, int to)
        {
            int si = 0, pi = from, starAt = -1, resumeAt = 0;
            while (si < length)
            {
                if (pi < to && pattern[pi] != '*' && (pattern[pi] == '?' || pattern[pi] == Fold[s[si]]))
                {
                    si++;
                    pi++;
                }
                else if (pi < to && pattern[pi] == '*')
                {
                    starAt = pi++;
                    resumeAt = si;
                }
                else if (starAt >= 0)
                {
                    pi = starAt + 1;
                    si = ++resumeAt;
                }
                else
                {
                    return false;
                }
            }
            while (pi < to && pattern[pi] == '*') pi++;
            return pi == to;
        }

        /// <summary>Classic greedy * / ? matching with backtracking to the last star.</summary>
        bool Wildcard(char[] s, int offset, int length, int from, int to)
        {
            int si = 0, pi = from, starAt = -1, resumeAt = 0;
            while (si < length)
            {
                if (pi < to && pattern[pi] != '*' && (pattern[pi] == '?' || pattern[pi] == Fold[s[offset + si]]))
                {
                    si++;
                    pi++;
                }
                else if (pi < to && pattern[pi] == '*')
                {
                    starAt = pi++;
                    resumeAt = si;
                }
                else if (starAt >= 0)
                {
                    pi = starAt + 1;
                    si = ++resumeAt;
                }
                else
                {
                    return false;
                }
            }
            while (pi < to && pattern[pi] == '*') pi++;
            return pi == to;
        }

        /// <summary>
        /// Maps every character to what it's compared as: upper case, and accented Latin letters
        /// to their plain letter, so "aritmetiska" finds "Aritmētiskā". Other scripts are only
        /// case-folded (folding kana dakuten, say, would change meaning).
        /// </summary>
        static char[] BuildFoldTable()
        {
            var table = new char[65536];
            for (int c = 0; c < table.Length; c++)
            {
                char folded = char.ToUpperInvariant((char)c);
                if ((c >= 0xC0 && c < 0x250) || (c >= 0x1E00 && c < 0x1F00))
                {
                    string parts = ((char)c).ToString().Normalize(System.Text.NormalizationForm.FormD);
                    if (parts.Length > 1 && parts[0] < 0x80 && char.IsLetter(parts[0])) folded = char.ToUpperInvariant(parts[0]);
                }
                table[c] = folded;
            }
            return table;
        }
    }
}
