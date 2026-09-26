using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Seek;

// Checks that the live index follows creates, renames, folder moves and deletes, both for
// items that only the watcher knows about and for items already in a full-scan snapshot.
//   LiveTest.exe <cache file> <scratch folder>
static class LiveTest
{
    static FileIndex index;
    static int failures;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Exclusions.AddFolder(Path.GetDirectoryName(Path.GetFullPath(args[0]))); // the index and its temp files
        index = new FileIndex(args[0]);
        index.Start();
        WaitForScan("initial");

        string tag = "seektest" + DateTime.Now.Ticks.ToString("x");
        string folder = Path.Combine(args[1], tag + "-dir");
        string sub = Path.Combine(folder, "sub");
        Directory.CreateDirectory(sub);
        string alpha = Path.Combine(sub, tag + "-alpha.pdf");
        File.WriteAllText(alpha, "x");
        Settle();
        Expect("new file shows up", tag + "-alpha.pdf", alpha);
        Expect("wildcard finds it", tag + "*alpha.pdf", alpha);
        Expect("new file found searching inside its new folder", ".pdf", alpha, folder);
        Expect("not found inside an unrelated folder", tag + "-alpha.pdf", null, @"C:\Windows");

        string beta = Path.Combine(sub, tag + "-beta.pdf");
        File.Move(alpha, beta);
        Settle();
        Expect("renamed file: old name gone", tag + "-alpha.pdf", null);
        Expect("renamed file: new name found", tag + "-beta.pdf", beta);

        string moved = Path.Combine(args[1], tag + "-moved");
        Directory.Move(folder, moved);
        Settle();
        string movedBeta = Path.Combine(moved, "sub", tag + "-beta.pdf");
        Expect("folder rename carries contents", tag + "-beta.pdf", movedBeta);

        // Now fold it into a full snapshot and check the snapshot copy gets hidden correctly.
        index.Rebuild();
        WaitForScan("rescan");
        Expect("after rescan, found via snapshot", tag + "-beta.pdf", movedBeta);
        Expect("snapshot item found searching inside its folder", ".pdf", movedBeta, moved);

        string again = Path.Combine(args[1], tag + "-again");
        Directory.Move(moved, again);
        Settle();
        string againBeta = Path.Combine(again, "sub", tag + "-beta.pdf");
        Expect("snapshot item under renamed folder", tag + "-beta.pdf", againBeta);
        Expect("searching inside the renamed folder finds it", ".pdf", againBeta, again);
        Expect("searching inside the old folder name finds nothing", ".pdf", null, moved);

        Directory.Delete(again, true);
        Settle();
        Expect("deleted folder hides everything in it", tag + "-beta.pdf", null);
        Expect("deleted folder itself gone", tag + "-again", null);

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    static void Settle()
    {
        Thread.Sleep(1500);
    }

    static void WaitForScan(string label)
    {
        var start = DateTime.Now;
        Thread.Sleep(500);
        while (!index.Complete || index.ScanProgress >= 0) Thread.Sleep(200);
        Console.WriteLine("{0} scan done in {1:N1} s", label, (DateTime.Now - start).TotalSeconds);
    }

    static void Expect(string what, string query, string path, string scope = null)
    {
        var hits = index.Search(query, scope, 5, CancellationToken.None);
        bool ok = path == null ? hits.Count == 0 : hits.Count == 1 && string.Equals(hits[0].Path, path, StringComparison.OrdinalIgnoreCase);
        if (!ok) failures++;
        Console.WriteLine("{0}  {1}  [{2}] -> {3}", ok ? "ok  " : "FAIL", what, query, hits.Count == 0 ? "(none)" : string.Join(" | ", hits.Select(h => h.Path)));
    }
}
