using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Seek;

// Builds a small file tree with known timestamps, indexes it with the real scanner, and checks
// every kind of query returns exactly the expected names in the expected order.
//   QueryTest.exe <scratch folder>
static class QueryTest
{
    static string root;
    static FileIndex index;
    static int failures;
    static readonly DateTime Epoch = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        root = Path.Combine(args[0], "qt-" + DateTime.Now.Ticks.ToString("x"));
        string longDir = Path.Combine(root, "deep", new string('x', 90), new string('y', 90), new string('z', 90));
        var files = new[]
        {
            Tuple.Create(Path.Combine(longDir, "longfile.pdf"), 12.0),
            Tuple.Create(@"e\report.PDF", 11.0),
            Tuple.Create(@"a\thing.pdf", 10.0),
            Tuple.Create(@"a\thing report.pdf", 9.0),
            Tuple.Create(@"b\thing-x-thing2.pdf", 8.0),
            Tuple.Create(@"b\thingthing2.pdf", 7.0),
            Tuple.Create(@"b\mything.pdf", 6.0),
            Tuple.Create(@"c\THING.PDF", 5.0),
            Tuple.Create(@"c\notes.pdf.bak", 4.0),
            Tuple.Create(@"c\Aritmētiskā progresija.docx", 3.0),
            Tuple.Create(@"d\thing\inner.pdf", 1.0),
            Tuple.Create(@"f\.gitignore", 0.5),
            Tuple.Create(@"g\東方紅魔郷.cfg", 0.4),
            Tuple.Create(@"g\ガイド.txt", 0.3),
            Tuple.Create(@"h\" + new string('k', 250) + ".log", 0.2),
            Tuple.Create(@"h\" + new string('m', 251) + ".log", 0.2),
            Tuple.Create(@"h\" + new string('あ', 100) + ".txt", 0.2),
            Tuple.Create(@"h\" + "\U0001F3B5music.mp3", 0.2),
            Tuple.Create(@"node_modules\x.pdf", 13.0),
            Tuple.Create(@".git\y.pdf", 13.0),
        };
        foreach (var f in files)
        {
            string path = Path.IsPathRooted(f.Item1) ? f.Item1 : Path.Combine(root, f.Item1);
            Directory.CreateDirectory(Long(Path.GetDirectoryName(path)));
            File.WriteAllText(Long(path), "x");
            Stamp(path, f.Item2, false);
        }
        // Folders get old timestamps (creating files bumped them) except d\thing, which gets t=2.
        foreach (string dir in Directory.EnumerateDirectories(Long(root), "*", SearchOption.AllDirectories).Concat(new[] { Long(root) }))
            Stamp(dir, 0.1, true);
        Stamp(Path.Combine(root, @"d\thing"), 2.0, true);

        var snap = Build(Path.Combine(args[0], "qt-index.bin"), root);
        index = new FileIndex(null);
        index.UseSnapshot(snap);
        Console.WriteLine("indexed {0} items under {1}", snap.Count, root);

        // Names that exercise the UTF-8 storage: long (the 255-byte length escape), 3-byte and
        // 4-byte (emoji) characters.
        Check(new string('k', 250) + ".log", new string('k', 250) + ".log");
        Check(new string('m', 251) + ".log", new string('m', 251) + ".log");
        Check("ああああ", new string('あ', 100) + ".txt");
        Check("music.mp3", "\U0001F3B5music.mp3");

        Check(".pdf", "longfile.pdf", "report.PDF", "thing.pdf", "thing report.pdf", "thing-x-thing2.pdf");
        Check("**.pdf", "longfile.pdf", "report.PDF", "thing.pdf", "thing report.pdf", "thing-x-thing2.pdf");
        Check("thing*.pdf", "thing.pdf", "thing report.pdf", "thing-x-thing2.pdf", "thingthing2.pdf", "THING.PDF");
        Check("thing*thing2.pdf", "thing-x-thing2.pdf", "thingthing2.pdf");
        Check("thing.pdf", "thing.pdf", "THING.PDF", "mything.pdf");
        Check("  thing.pdf  ", "thing.pdf", "THING.PDF", "mything.pdf");
        Check("thing", "thing", "thing.pdf", "thing report.pdf", "thing-x-thing2.pdf", "thingthing2.pdf");
        Check("?hing.pdf", "thing.pdf", "THING.PDF");
        Check(".pdf.bak", "notes.pdf.bak");
        Check(".gitignore", ".gitignore");
        Check(".p*", "longfile.pdf", "report.PDF", "thing.pdf", "thing report.pdf", "thing-x-thing2.pdf");
        Check(".doc?", "Aritmētiskā progresija.docx");
        Check("aritmetiska", "Aritmētiskā progresija.docx");
        Check("ARITMĒTISKĀ", "Aritmētiskā progresija.docx");
        Check("紅魔", "東方紅魔郷.cfg");
        Check("ガイド", "ガイド.txt");
        Check("カイド"); // kana voicing marks still matter
        Check("x.pdf");
        Check("y.pdf");
        Check("zzqq");
        Check(".");
        Check("");

        CheckIn(@"d\thing", "", "inner.pdf");
        CheckIn(@"d\thing", ".pdf", "inner.pdf");
        CheckIn(@"b", "thing", "thing-x-thing2.pdf", "thingthing2.pdf", "mything.pdf");
        CheckIn(@"b", "", "thing-x-thing2.pdf", "thingthing2.pdf", "mything.pdf");
        CheckIn(@"B", "*2.pdf", "thing-x-thing2.pdf", "thingthing2.pdf");
        CheckIn(@"deep", ".pdf", "longfile.pdf");
        CheckIn(@"nope", "");

        // Long paths must round-trip and be stat-able (the UI checks a file still exists before opening).
        var longHit = index.Search("longfile.pdf", null, 5, CancellationToken.None).FirstOrDefault();
        bool isFolder;
        long time;
        bool statOk = longHit != null && longHit.Path.Length > 260 && Kernel.Stat(longHit.Path, Kernel.FutureLimit(), out isFolder, out time) == StatResult.Found;
        Report(statOk, "long path (" + (longHit == null ? 0 : longHit.Path.Length) + " chars) found and stat-able");

        // A corrupt or truncated index file must be rejected, never crash.
        string good = Path.Combine(args[0], "qt-index.bin");
        string cache = Path.Combine(args[0], "qt-damaged.bin");
        File.WriteAllBytes(cache, Encoding.ASCII.GetBytes("garbage that is not an index"));
        Report(Snapshot.Open(cache) == null, "corrupt index rejected");
        byte[] whole = File.ReadAllBytes(good);
        File.WriteAllBytes(cache, whole.Take(whole.Length / 2).ToArray());
        Report(Snapshot.Open(cache) == null, "truncated index rejected");
        whole[Snapshot.HeaderSize + 1] ^= 0x7F; // scramble the folder table
        File.WriteAllBytes(cache, whole);
        Report(Snapshot.Open(cache) == null, "scrambled folder table rejected");
        var reopened = Snapshot.Open(good);
        Report(reopened != null && reopened.Count == snap.Count, "index reopens with " + (reopened == null ? 0 : reopened.Count) + " items");
        reopened.Dispose();
        File.Delete(cache);

        // Combining several scans (one per drive) keeps newest-first order across them.
        var merged = Build(Path.Combine(args[0], "qt-merged.bin"), Path.Combine(root, "a"), Path.Combine(root, "b"));
        var mergedIndex = new FileIndex(null);
        mergedIndex.UseSnapshot(merged);
        var mergedHits = mergedIndex.Search(".pdf", null, 5, CancellationToken.None).Select(h => h.Name).ToArray();
        var mergedWant = new[] { "thing.pdf", "thing report.pdf", "thing-x-thing2.pdf", "thingthing2.pdf", "mything.pdf" };
        Report(mergedHits.SequenceEqual(mergedWant), "merge order: " + string.Join(", ", mergedHits));

        snap.Dispose();
        merged.Dispose();
        Directory.Delete(Long(root), true);
        File.Delete(Path.Combine(args[0], "qt-index.bin"));
        File.Delete(Path.Combine(args[0], "qt-merged.bin"));
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    /// <summary>Scans each folder as if it were a drive, writes one index file and opens it.</summary>
    static Snapshot Build(string indexPath, params string[] folders)
    {
        var results = folders.Select(f => new Scanner(new[] { f }, false, Path.GetDirectoryName(indexPath)).Run(CancellationToken.None)).ToList();
        Scanner.Combine(results, indexPath);
        foreach (var r in results) r.Dispose();
        return Snapshot.Open(indexPath);
    }

    static string Long(string path)
    {
        return path.StartsWith(@"\\?\") ? path : @"\\?\" + path;
    }

    static void Stamp(string path, double hours, bool folder)
    {
        var t = Epoch.AddHours(hours);
        if (folder)
        {
            Directory.SetCreationTime(Long(path), t);
            Directory.SetLastWriteTime(Long(path), t);
        }
        else
        {
            File.SetCreationTime(Long(path), t);
            File.SetLastWriteTime(Long(path), t);
        }
    }

    static void Check(string query, params string[] want)
    {
        Compare("\"" + query + "\"", index.Search(query, null, 5, CancellationToken.None), want);
    }

    static void CheckIn(string folder, string query, params string[] want)
    {
        Compare("\"" + query + "\" in " + folder, index.Search(query, Path.Combine(root, folder), 5, CancellationToken.None), want);
    }

    static void Compare(string label, List<Hit> hits, string[] want)
    {
        var got = hits.Select(h => h.Name).ToArray();
        Report(got.SequenceEqual(want), label + " -> " + (got.Length == 0 ? "(none)" : string.Join(" | ", got)) + (got.SequenceEqual(want) ? "" : "   WANTED " + (want.Length == 0 ? "(none)" : string.Join(" | ", want))));
    }

    static void Report(bool ok, string what)
    {
        if (!ok) failures++;
        Console.WriteLine("{0}  {1}", ok ? "ok  " : "FAIL", what);
    }
}
