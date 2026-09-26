using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Seek
{
    /// <summary>
    /// How one file or folder is stored, both in the index file and in scan spill files:
    ///   int32  containing folder id, FolderBit set when the item is itself a folder
    ///   uint32 time, in seconds since 1970 (0 = unknown)
    ///   name length in bytes: one byte, or 255 followed by a uint16
    ///   name, UTF-8
    /// </summary>
    internal static unsafe class Record
    {
        public const int MaxSize = 11 + 260 * 3;
        const long UnixEpoch = 116444736000000000L; // 1970-01-01 as a FILETIME

        public static uint ToSeconds(long fileTime)
        {
            if (fileTime <= UnixEpoch) return 0;
            long seconds = (fileTime - UnixEpoch) / 10000000;
            return seconds > uint.MaxValue ? uint.MaxValue : (uint)seconds;
        }

        public static long ToFileTime(uint seconds)
        {
            return seconds == 0 ? 0 : UnixEpoch + seconds * 10000000L;
        }

        /// <summary>Writes a record at p (which must have MaxSize bytes free) and returns its size.</summary>
        public static int Write(byte* p, int parent, uint time, char* name, int length)
        {
            *(int*)p = parent;
            *(uint*)(p + 4) = time;
            // Encode after the longest possible header, then close the gap for short names.
            int bytes = Utf8.Encode(name, length, p + 11);
            if (bytes >= 255)
            {
                p[8] = 255;
                *(ushort*)(p + 9) = (ushort)bytes;
                return 11 + bytes;
            }
            p[8] = (byte)bytes;
            for (int k = 0; k < bytes; k++) p[9 + k] = p[11 + k];
            return 9 + bytes;
        }

        public static int Size(byte* p)
        {
            int length = p[8];
            return length == 255 ? 11 + *(ushort*)(p + 9) : 9 + length;
        }
    }

    /// <summary>
    /// UTF-16 to UTF-8 and back. Windows allows unpaired surrogates in names; those are kept
    /// as 3-byte sequences so every name round-trips exactly.
    /// </summary>
    internal static unsafe class Utf8
    {
        public static int Encode(char* s, int length, byte* d)
        {
            byte* start = d;
            for (int i = 0; i < length; i++)
            {
                int c = s[i];
                if (c < 0x80)
                {
                    *d++ = (byte)c;
                }
                else if (c < 0x800)
                {
                    *d++ = (byte)(0xC0 | c >> 6);
                    *d++ = (byte)(0x80 | c & 0x3F);
                }
                else if (c >= 0xD800 && c < 0xDC00 && i + 1 < length && s[i + 1] >= 0xDC00 && s[i + 1] < 0xE000)
                {
                    int point = 0x10000 + ((c - 0xD800) << 10) + (s[++i] - 0xDC00);
                    *d++ = (byte)(0xF0 | point >> 18);
                    *d++ = (byte)(0x80 | (point >> 12) & 0x3F);
                    *d++ = (byte)(0x80 | (point >> 6) & 0x3F);
                    *d++ = (byte)(0x80 | point & 0x3F);
                }
                else
                {
                    *d++ = (byte)(0xE0 | c >> 12);
                    *d++ = (byte)(0x80 | (c >> 6) & 0x3F);
                    *d++ = (byte)(0x80 | c & 0x3F);
                }
            }
            return (int)(d - start);
        }

        /// <summary>True when every byte is plain ASCII, i.e. each byte is one char of the name.</summary>
        public static bool IsAscii(byte* s, int length)
        {
            ulong seen = 0;
            int i = 0;
            for (; i + 8 <= length; i += 8) seen |= *(ulong*)(s + i);
            for (; i < length; i++) seen |= s[i];
            return (seen & 0x8080808080808080UL) == 0;
        }

        /// <summary>Decodes into d (at least as long as the byte count) and returns the char count.</summary>
        public static int Decode(byte* s, int length, char[] d)
        {
            int n = 0;
            byte* end = s + length;
            while (s < end)
            {
                int b = *s++;
                if (b < 0x80)
                {
                    d[n++] = (char)b;
                }
                else if (b < 0xE0)
                {
                    if (s >= end) break;
                    d[n++] = (char)((b & 0x1F) << 6 | (*s++ & 0x3F));
                }
                else if (b < 0xF0)
                {
                    if (s + 1 >= end) break;
                    d[n++] = (char)((b & 0x0F) << 12 | (s[0] & 0x3F) << 6 | (s[1] & 0x3F));
                    s += 2;
                }
                else
                {
                    if (s + 2 >= end) break;
                    int point = ((b & 0x07) << 18 | (s[0] & 0x3F) << 12 | (s[1] & 0x3F) << 6 | (s[2] & 0x3F)) - 0x10000;
                    s += 3;
                    d[n++] = (char)(0xD800 + (point >> 10 & 0x3FF));
                    d[n++] = (char)(0xDC00 + (point & 0x3FF));
                }
            }
            return n;
        }

        public static byte[] Encode(string s)
        {
            var bytes = new byte[s.Length * 3];
            int n;
            fixed (char* c = s)
            fixed (byte* b = bytes)
                n = Encode(c, s.Length, b);
            Array.Resize(ref bytes, n);
            return bytes;
        }
    }

    /// <summary>
    /// The index of every file and folder, living in a file on disk rather than in memory:
    /// only the folder table (~4 MB) stays loaded. Items are stored newest first in blocks of
    /// BlockItems; a search reads blocks (usually straight from Windows' file cache) into a few
    /// small reusable buffers and stops as soon as it has enough hits.
    /// </summary>
    internal sealed unsafe class Snapshot : IDisposable
    {
        public const int FolderBit = unchecked((int)0x80000000);
        public const int BlockItems = 1 << 13;
        internal const int Magic = 0x4B454553; // "SEEK"
        internal const int Version = 3;
        internal const int HeaderSize = 64;

        readonly string path;
        readonly ConcurrentBag<FileStream> idle = new ConcurrentBag<FileStream>();
        readonly List<FileStream> streams = new List<FileStream>();
        int[] dirNameStart;
        byte[] dirNames;
        long[] blockStart; // blockStart[BlockCount] is the end of the item data
        int largestBlock;

        public int Count;
        public int[] DirParent; // parent folder id, -1 for a drive root; always lower than the child's id

        Snapshot(string path)
        {
            this.path = path;
        }

        public int BlockCount
        {
            get { return blockStart.Length - 1; }
        }

        public int LargestBlock
        {
            get { return largestBlock; }
        }

        /// <summary>A file handle for one thread to read blocks with (return it when done).</summary>
        public FileStream Rent()
        {
            FileStream stream;
            if (idle.TryTake(out stream)) return stream;
            stream = OpenStream(path);
            lock (streams) streams.Add(stream);
            return stream;
        }

        public void Return(FileStream stream)
        {
            idle.Add(stream);
        }

        /// <summary>
        /// Reads a block into buffer, growing it if this block is bigger than any before, and
        /// returns the block's length. Buffers belong to the caller so they outlive index swaps.
        /// </summary>
        public int Load(FileStream stream, int block, ref byte[] buffer)
        {
            long start = blockStart[block];
            int length = (int)(blockStart[block + 1] - start);
            if (buffer == null || buffer.Length < length) buffer = new byte[(length + 0xFFFF) & ~0xFFFF];
            stream.Position = start;
            for (int got = 0; got < length; )
            {
                int read = stream.Read(buffer, got, length - got);
                if (read <= 0) throw new EndOfStreamException();
                got += read;
            }
            return length;
        }

        public void Dispose()
        {
            lock (streams)
            {
                foreach (var stream in streams) stream.Dispose();
                streams.Clear();
            }
        }

        public string DirName(int dir)
        {
            int start = dirNameStart[dir], length = dirNameStart[dir + 1] - start;
            var chars = new char[length];
            fixed (byte* p = dirNames)
                return new string(chars, 0, Utf8.Decode(p + start, length, chars));
        }

        public string DirPath(int dir)
        {
            int depth = 0;
            for (int d = dir; d >= 0 && depth < 4096; d = DirParent[d]) depth++;
            var parts = new string[depth];
            int k = depth - 1;
            for (int d = dir; k >= 0; d = DirParent[d], k--) parts[k] = DirName(d);
            return string.Join("\\", parts);
        }

        static FileStream OpenStream(string path)
        {
            // Share Delete so a fresh index can replace this file while it's still open.
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.RandomAccess);
        }

        /// <summary>Opens an index file; null if it's missing, damaged or from another version.</summary>
        public static Snapshot Open(string path)
        {
            FileStream stream = null;
            try
            {
                if (!File.Exists(path)) return null;
                stream = OpenStream(path);
                long fileLength = stream.Length;
                if (fileLength < HeaderSize) return null;
                var reader = new BinaryReader(stream, Encoding.UTF8, true);
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) return null;
                int count = reader.ReadInt32(), dirs = reader.ReadInt32(), blocks = reader.ReadInt32(), blockItems = reader.ReadInt32();
                long dirOffset = reader.ReadInt64(), tableOffset = reader.ReadInt64(), dataOffset = reader.ReadInt64();
                if (count < 0 || dirs < 0 || blockItems != BlockItems || blocks != (count + BlockItems - 1) / BlockItems) return null;
                if (dirOffset != HeaderSize || dataOffset < dirOffset || tableOffset < dataOffset || tableOffset + 8L * (blocks + 1) != fileLength) return null;

                var s = new Snapshot(path) { Count = count };
                stream.Position = dirOffset;
                s.DirParent = ReadInts(reader, dirs);
                s.dirNameStart = ReadInts(reader, dirs + 1);
                if (s.dirNameStart[0] != 0 || dirOffset + 4L * (2 * dirs + 1) + s.dirNameStart[dirs] != dataOffset) return null;
                s.dirNames = reader.ReadBytes(s.dirNameStart[dirs]);
                for (int d = 0; d < dirs; d++)
                    if (s.DirParent[d] >= d || s.DirParent[d] < -1 || s.dirNameStart[d + 1] < s.dirNameStart[d]) return null;

                stream.Position = tableOffset;
                s.blockStart = new long[blocks + 1];
                for (int b = 0; b <= blocks; b++) s.blockStart[b] = reader.ReadInt64();
                if (s.blockStart[0] != dataOffset || s.blockStart[blocks] != tableOffset) return null;
                for (int b = 0; b < blocks; b++)
                {
                    long size = s.blockStart[b + 1] - s.blockStart[b];
                    if (size < 0 || size > 64L << 20) return null;
                    s.largestBlock = Math.Max(s.largestBlock, (int)size);
                }

                s.streams.Add(stream);
                s.idle.Add(stream);
                stream = null; // now owned by the snapshot
                return s;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return null;
            }
            finally
            {
                if (stream != null) stream.Dispose();
            }
        }

        static int[] ReadInts(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(4 * count);
            if (bytes.Length != 4 * count) throw new EndOfStreamException();
            var values = new int[count];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }
    }

    /// <summary>
    /// Walks a drive with several threads. Each thread collects records in a small batch, sorts
    /// it newest first and spills it to its own temp file; Combine then merges all those sorted
    /// runs straight into a new index file. Memory stays small however many files there are.
    /// </summary>
    internal sealed unsafe class Scanner
    {
        const int BatchRecords = 1 << 13;
        const int BatchBytes = 1 << 18;

        struct Job
        {
            public int Id;
            public string Path;
        }

        internal struct SortedRun
        {
            public long Start;
            public long End;
        }

        internal sealed class Worker : IDisposable
        {
            readonly byte[] batch = new byte[BatchBytes];
            readonly long[] keys = new long[BatchRecords];
            readonly int[] starts = new int[BatchRecords];
            int count, used;
            public readonly FileStream Spill;
            public readonly List<SortedRun> Runs = new List<SortedRun>();
            public int Items;

            // Folders this thread found: global id, parent id, UTF-8 name.
            public readonly List<int> FolderIds = new List<int>();
            public readonly List<int> FolderParents = new List<int>();
            public readonly List<int> FolderNameStart = new List<int>();
            public readonly List<int> FolderNameLength = new List<int>();
            public byte[] FolderNames = new byte[1 << 12];
            int folderNamesUsed;

            public Worker(string spillPath)
            {
                // DeleteOnClose: the temp file vanishes when we're done, or if Seek is killed.
                Spill = new FileStream(spillPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            }

            public void Add(int parent, long fileTime, char* name, int length)
            {
                if (count == BatchRecords || used + Record.MaxSize > BatchBytes) FlushRun();
                uint time = Record.ToSeconds(fileTime);
                fixed (byte* b = batch)
                {
                    int size = Record.Write(b + used, parent, time, name, length);
                    keys[count] = -(long)time;
                    starts[count] = used;
                    count++;
                    used += size;
                }
                Items++;
            }

            public void AddFolder(int id, int parent, char* name, int length)
            {
                if (folderNamesUsed + length * 3 > FolderNames.Length)
                    Array.Resize(ref FolderNames, Math.Max(FolderNames.Length * 2, folderNamesUsed + length * 3));
                int bytes;
                fixed (byte* d = FolderNames) bytes = Utf8.Encode(name, length, d + folderNamesUsed);
                FolderIds.Add(id);
                FolderParents.Add(parent);
                FolderNameStart.Add(folderNamesUsed);
                FolderNameLength.Add(bytes);
                folderNamesUsed += bytes;
            }

            /// <summary>Sorts the batch newest first and appends it to the spill file as one run.</summary>
            public void FlushRun()
            {
                if (count == 0) return;
                Array.Sort(keys, starts, 0, count);
                long runStart = Spill.Position;
                fixed (byte* b = batch)
                {
                    for (int k = 0; k < count; k++)
                    {
                        int at = starts[k];
                        Spill.Write(batch, at, Record.Size(b + at));
                    }
                }
                Runs.Add(new SortedRun { Start = runStart, End = Spill.Position });
                count = 0;
                used = 0;
            }

            public void Finish()
            {
                FlushRun();
                Spill.Flush();
            }

            public void Dispose()
            {
                Spill.Dispose();
            }
        }

        /// <summary>What one scan found: sorted runs in spill files plus its folder table.</summary>
        public sealed class Result : IDisposable
        {
            internal List<KeyValuePair<int, string>> Roots;
            internal Worker[] Workers;
            internal int FolderCount;

            public void Dispose()
            {
                foreach (var worker in Workers) worker.Dispose();
            }
        }

        readonly IList<string> roots;
        readonly bool background;
        readonly string spillFolder;
        readonly ConcurrentStack<Job> jobs = new ConcurrentStack<Job>();
        readonly long futureLimit = Kernel.FutureLimit();
        CancellationToken cancel;
        volatile Exception failure;
        int pending;
        int lastFolderId = -1;
        int found;

        /// <param name="background">A routine rescan: background disk priority and fewer threads,
        /// so it never slows the PC down. Otherwise (first run) it goes as fast as it can.</param>
        /// <param name="spillFolder">Where the temporary sorted runs go.</param>
        public Scanner(IList<string> roots, bool background, string spillFolder)
        {
            this.roots = roots;
            this.background = background;
            this.spillFolder = spillFolder;
        }

        /// <summary>Items found so far, for progress display.</summary>
        public int Found
        {
            get { return Volatile.Read(ref found); }
        }

        public Result Run(CancellationToken cancel)
        {
            this.cancel = cancel;
            var rootFolders = new List<KeyValuePair<int, string>>();
            foreach (string root in roots)
            {
                string name = root.TrimEnd('\\');
                int id = ++lastFolderId;
                rootFolders.Add(new KeyValuePair<int, string>(id, name));
                pending++;
                jobs.Push(new Job { Id = id, Path = name });
            }

            // Measured: past ~4 threads a drive mostly burns extra kernel time (and a spinning
            // disk gets slower); a first run on an SSD still gains a little from more.
            int threadCount = Math.Max(1, Math.Min(Environment.ProcessorCount, background ? 4 : 6));
            string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            var workers = new Worker[threadCount];
            var threads = new Thread[threadCount];
            try
            {
                for (int t = 0; t < threadCount; t++)
                {
                    var worker = workers[t] = new Worker(Path.Combine(spillFolder, "scan-" + tag + "-" + t + ".tmp"));
                    threads[t] = new Thread(() => Drain(worker)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Seek scan" };
                    threads[t].Start();
                }
                foreach (var thread in threads) thread.Join();
                if (failure != null) throw new IOException("Scan of " + string.Join(", ", roots) + " failed", failure);
                if (cancel.IsCancellationRequested) throw new OperationCanceledException();
            }
            catch
            {
                foreach (var worker in workers)
                    if (worker != null) worker.Dispose();
                throw;
            }
            return new Result { Roots = rootFolders, Workers = workers, FolderCount = lastFolderId + 1 };
        }

        void Drain(Worker worker)
        {
            // Unreadable folders are simply skipped by ListFolder, so anything thrown here is
            // our own failure (typically the disk filling up while spilling). That must fail the
            // whole scan rather than quietly leave files out of the index.
            try
            {
                if (background) Kernel.EnterBackgroundMode();
                Job job;
                while (!cancel.IsCancellationRequested && failure == null)
                {
                    if (jobs.TryPop(out job))
                    {
                        ScanFolder(job, worker);
                        Interlocked.Decrement(ref pending);
                    }
                    else if (Volatile.Read(ref pending) == 0)
                    {
                        break;
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
                worker.Finish();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        void ScanFolder(Job job, Worker worker)
        {
            int count = 0;
            Kernel.ListFolder(job.Path, futureLimit, (name, length, isFolder, time) =>
            {
                int parent = job.Id;
                if (isFolder)
                {
                    string folderName = new string(name, 0, length);
                    string path = job.Path + "\\" + folderName;
                    if (Exclusions.SkipFolder(folderName, path)) return;
                    int id = Interlocked.Increment(ref lastFolderId);
                    worker.AddFolder(id, job.Id, name, length);
                    Interlocked.Increment(ref pending);
                    jobs.Push(new Job { Id = id, Path = path });
                    parent |= Snapshot.FolderBit;
                }
                worker.Add(parent, time, name, length);
                count++;
            });
            Interlocked.Add(ref found, count);
        }

        /// <summary>
        /// Writes one index file from the results of one or more scans (one per drive): the
        /// folder table, then every item newest first, merged from all the sorted runs.
        /// </summary>
        public static void Combine(IList<Result> parts, string outputPath)
        {
            int folders = 0;
            foreach (var part in parts) folders += part.FolderCount;

            // Folder table, in global id order.
            var dirParent = new int[folders];
            var nameStart = new int[folders + 1];
            var rootNames = new Dictionary<int, byte[]>();
            int folderBase = 0;
            foreach (var part in parts)
            {
                foreach (var root in part.Roots)
                {
                    var bytes = Utf8.Encode(root.Value);
                    rootNames[folderBase + root.Key] = bytes;
                    dirParent[folderBase + root.Key] = -1;
                    nameStart[folderBase + root.Key + 1] = bytes.Length;
                }
                foreach (var w in part.Workers)
                {
                    for (int j = 0; j < w.FolderIds.Count; j++)
                    {
                        dirParent[folderBase + w.FolderIds[j]] = w.FolderParents[j] + folderBase;
                        nameStart[folderBase + w.FolderIds[j] + 1] = w.FolderNameLength[j];
                    }
                }
                folderBase += part.FolderCount;
            }
            for (int d = 0; d < folders; d++) nameStart[d + 1] += nameStart[d];
            var names = new byte[nameStart[folders]];
            foreach (var root in rootNames) Buffer.BlockCopy(root.Value, 0, names, nameStart[root.Key], root.Value.Length);
            folderBase = 0;
            foreach (var part in parts)
            {
                foreach (var w in part.Workers)
                    for (int j = 0; j < w.FolderIds.Count; j++)
                        Buffer.BlockCopy(w.FolderNames, w.FolderNameStart[j], names, nameStart[folderBase + w.FolderIds[j]], w.FolderNameLength[j]);
                folderBase += part.FolderCount;
            }

            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(new byte[Snapshot.HeaderSize]);
                long dirOffset = output.Position;
                WriteInts(writer, dirParent);
                WriteInts(writer, nameStart);
                writer.Write(names);
                long dataOffset = output.Position;

                // Merge every sorted run, newest record first.
                var heap = new RunHeap();
                folderBase = 0;
                foreach (var part in parts)
                {
                    foreach (var w in part.Workers)
                        foreach (var run in w.Runs)
                        {
                            var reader = new RunReader(w.Spill, run, folderBase);
                            if (reader.Advance()) heap.Add(reader);
                        }
                    folderBase += part.FolderCount;
                }
                var blockStarts = new List<long> { dataOffset };
                var record = new byte[Record.MaxSize];
                int written = 0;
                while (heap.Count > 0)
                {
                    var top = heap.Top;
                    writer.Write(record, 0, top.TakeCurrent(record));
                    if (++written % Snapshot.BlockItems == 0) blockStarts.Add(output.Position);
                    if (top.Advance()) heap.TopChanged();
                    else heap.RemoveTop();
                }
                long tableOffset = output.Position;
                if (blockStarts[blockStarts.Count - 1] != tableOffset) blockStarts.Add(tableOffset);
                foreach (long start in blockStarts) writer.Write(start);

                output.Position = 0;
                writer.Write(Snapshot.Magic);
                writer.Write(Snapshot.Version);
                writer.Write(written);
                writer.Write(folders);
                writer.Write(blockStarts.Count - 1);
                writer.Write(Snapshot.BlockItems);
                writer.Write(dirOffset);
                writer.Write(tableOffset);
                writer.Write(dataOffset);
                writer.Write(DateTime.UtcNow.Ticks);
            }
        }

        static void WriteInts(BinaryWriter writer, int[] values)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            writer.Write(bytes);
        }

        /// <summary>Streams one sorted run back out of a spill file through a small buffer.</summary>
        sealed class RunReader
        {
            readonly FileStream source;
            readonly long end;
            readonly int folderBase;
            readonly byte[] buffer = new byte[1 << 13]; // one per run, and there are hundreds
            long next;
            int at, filled;
            public uint Time;

            public RunReader(FileStream source, SortedRun run, int folderBase)
            {
                this.source = source;
                this.folderBase = folderBase;
                next = run.Start;
                end = run.End;
            }

            /// <summary>Moves to the next record (reading more if needed); false at the end of the run.</summary>
            public bool Advance()
            {
                if (filled - at < Record.MaxSize && next < end) Refill();
                if (at >= filled) return false;
                fixed (byte* p = buffer) Time = *(uint*)(p + at + 4);
                return true;
            }

            /// <summary>Copies the current record out, with its folder ids moved into the combined table.</summary>
            public int TakeCurrent(byte[] destination)
            {
                fixed (byte* p = buffer)
                fixed (byte* d = destination)
                {
                    byte* r = p + at;
                    int size = Record.Size(r);
                    for (int k = 0; k < size; k++) d[k] = r[k];
                    int parent = *(int*)d;
                    *(int*)d = ((parent & ~Snapshot.FolderBit) + folderBase) | (parent & Snapshot.FolderBit);
                    at += size;
                    return size;
                }
            }

            void Refill()
            {
                System.Buffer.BlockCopy(buffer, at, buffer, 0, filled - at);
                filled -= at;
                at = 0;
                int want = (int)Math.Min(buffer.Length - filled, end - next);
                source.Position = next;
                for (int got = 0; got < want; )
                {
                    int read = source.Read(buffer, filled + got, want - got);
                    if (read <= 0) throw new EndOfStreamException();
                    got += read;
                }
                filled += want;
                next += want;
            }
        }

        /// <summary>Max-heap of run readers by the time of their current record.</summary>
        sealed class RunHeap
        {
            readonly List<RunReader> items = new List<RunReader>();

            public int Count
            {
                get { return items.Count; }
            }

            public RunReader Top
            {
                get { return items[0]; }
            }

            public void Add(RunReader reader)
            {
                items.Add(reader);
                for (int i = items.Count - 1; i > 0; )
                {
                    int parent = (i - 1) / 2;
                    if (items[parent].Time >= items[i].Time) break;
                    Swap(i, parent);
                    i = parent;
                }
            }

            public void RemoveTop()
            {
                int last = items.Count - 1;
                items[0] = items[last];
                items.RemoveAt(last);
                if (items.Count > 0) TopChanged();
            }

            public void TopChanged()
            {
                for (int i = 0; ; )
                {
                    int left = 2 * i + 1, right = left + 1, best = i;
                    if (left < items.Count && items[left].Time > items[best].Time) best = left;
                    if (right < items.Count && items[right].Time > items[best].Time) best = right;
                    if (best == i) return;
                    Swap(i, best);
                    i = best;
                }
            }

            void Swap(int a, int b)
            {
                var t = items[a];
                items[a] = items[b];
                items[b] = t;
            }
        }
    }

    /// <summary>
    /// Places that are never worth showing: system stores, the recycle bin, and dependency /
    /// version-control folders that hold millions of files nobody searches for by name.
    /// </summary>
    internal static class Exclusions
    {
        static readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "$WinREAgent", "$SysReset",
            "$Windows.~BT", "$Windows.~WS", "Config.Msi", "node_modules", ".git",
        };

        static readonly HashSet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static Exclusions()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (string sub in new[] { "WinSxS", "servicing", "SoftwareDistribution", "Installer", "assembly", "Prefetch" })
                Folders.Add(Path.Combine(windows, sub));
        }

        public static void AddFolder(string path)
        {
            Folders.Add(path.TrimEnd('\\'));
        }

        public static bool SkipFolder(string name, string path)
        {
            return Names.Contains(name) || Folders.Contains(path);
        }

        /// <summary>True when the path is, or is inside, an excluded folder.</summary>
        public static bool SkipPath(string path)
        {
            int start = 0;
            for (int i = 0; i <= path.Length; i++)
            {
                if (i < path.Length && path[i] != '\\') continue;
                if (i > start && Names.Contains(path.Substring(start, i - start))) return true;
                if (Folders.Contains(path.Substring(0, i))) return true;
                start = i + 1;
            }
            return false;
        }
    }
}
