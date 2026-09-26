using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Seek;

// Console harness for the index: full scan into an index file, a check that every record in
// it is readable and newest-first, peak memory, and timed sample queries.
//   Bench.exe <index file> [query ...]
static unsafe class Bench
{
    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string indexPath = Path.GetFullPath(args[0]);
        Exclusions.AddFolder(Path.GetDirectoryName(indexPath));
        var watch = Stopwatch.StartNew();
        var results = new List<Scanner.Result>();
        int found = 0;
        foreach (string root in FileIndex.FixedDrives())
        {
            var scanner = new Scanner(new[] { root }, true, Path.GetDirectoryName(indexPath));
            results.Add(scanner.Run(CancellationToken.None));
            found += scanner.Found;
        }
        Console.WriteLine("scan   {0:N0} items in {1:N1} s", found, watch.Elapsed.TotalSeconds);
        watch.Restart();
        Scanner.Combine(results, indexPath);
        foreach (var r in results) r.Dispose();
        Console.WriteLine("write  {0:N1} s, {1:N0} MB file", watch.Elapsed.TotalSeconds, new FileInfo(indexPath).Length >> 20);

        watch.Restart();
        var snap = Snapshot.Open(indexPath);
        Console.WriteLine("open   {0:N0} ms, {1:N0} items, {2:N0} folders, {3} blocks", watch.ElapsedMilliseconds, snap.Count, snap.DirParent.Length, snap.BlockCount);
        Verify(snap, found);
        var me = Process.GetCurrentProcess();
        me.Refresh();
        Console.WriteLine("memory peak working set {0} MB, managed heap now {1} MB", me.PeakWorkingSet64 >> 20, GC.GetTotalMemory(true) >> 20);

        var index = new FileIndex(null);
        index.UseSnapshot(snap);
        for (int i = 1; i < args.Length; i++)
        {
            index.Search(args[i], null, 5, CancellationToken.None); // warm up
            watch.Restart();
            var hits = index.Search(args[i], null, 5, CancellationToken.None);
            Console.WriteLine();
            Console.WriteLine("\"{0}\"  {1:N1} ms", args[i], watch.Elapsed.TotalMilliseconds);
            foreach (var hit in hits)
                Console.WriteLine("   {0:yyyy-MM-dd HH:mm}  {1}{2}", DateTime.FromFileTimeUtc(hit.Time).ToLocalTime(), hit.Path, hit.IsFolder ? "  [folder]" : "");
        }
    }

    /// <summary>Walks every block: each record parses, folder ids are valid, times never increase.</summary>
    static void Verify(Snapshot snap, int expected)
    {
        var stream = snap.Rent();
        byte[] buffer = null;
        int records = 0, badFolders = 0, outOfOrder = 0;
        uint last = uint.MaxValue;
        for (int b = 0; b < snap.BlockCount; b++)
        {
            int length = snap.Load(stream, b, ref buffer);
            int inBlock = 0;
            fixed (byte* start = buffer)
            {
                byte* p = start, end = start + length;
                while (p < end)
                {
                    int parent = *(int*)p;
                    uint time = *(uint*)(p + 4);
                    if ((parent & ~Snapshot.FolderBit) >= snap.DirParent.Length) badFolders++;
                    if (time > last) outOfOrder++;
                    last = time;
                    p += Record.Size(p);
                    inBlock++;
                }
            }
            if (b < snap.BlockCount - 1 && inBlock != Snapshot.BlockItems) Console.WriteLine("  block {0} has {1} records", b, inBlock);
            records += inBlock;
        }
        snap.Return(stream);
        bool ok = records == expected && records == snap.Count && badFolders == 0 && outOfOrder == 0;
        Console.WriteLine("verify {0}: {1:N0} records (scanner found {2:N0}), {3} bad folder ids, {4} out of order", ok ? "OK" : "FAILED", records, expected, badFolders, outOfOrder);
    }
}
