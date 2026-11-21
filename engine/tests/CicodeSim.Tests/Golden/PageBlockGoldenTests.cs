using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;
using Obj = CicodeSim.Tests.Golden.GoldenJson.Obj;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The page block sweep, frozen.</summary>
    public class PageBlockGoldenTests
    {
        private const string ExamplesFile = "page_blocks_examples.jsonl.gz";
        private const string AllFile = "page_blocks.jsonl.gz";

        internal static string Line(VmCommands.PageRun r)
        {
            var o = new Obj()
                .Add("page", r.Page)
                .Add("table", r.Block.Table)
                .Add("record", r.Block.Record)
                .Add("field", r.Block.Field);
            if (r.Task == null)
                o.Add("outcome", r.Outcome).Add("err", r.Err);
            else
                GoldenRecords.AddRun(o, r.Task, r.Outcome, VmCommands.NotSimulatedName(r.Task));
            return GoldenJson.Write(o);
        }

        private static string Key(VmCommands.PageRun r) =>
            r.Page
            + "|"
            + r.Block.Table
            + "|"
            + r.Block.Record.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|"
            + r.Block.Field;

        private static IEnumerable<string> Projects(params string[] only)
        {
            foreach (string lib in VmCommands.Libraries(GoldenFiles.User))
            {
                string dir = Path.GetDirectoryName(lib)!;
                if (
                    only.Length == 0
                    || only.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)
                )
                    yield return dir;
            }
        }

        [GoldenFact]
        [Trait("Golden", "light")]
        public void ExamplePageBlocksMatchTheGolden()
        {
            var got = new List<(string, string)>();
            foreach (string dir in Projects("ExampleSA", "Example"))
            foreach (var r in VmCommands.PageRuns(dir))
                got.Add((Key(r), Line(r)));
            if (GoldenFiles.WriteIfAsked(ExamplesFile, got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles
                .ReadLines(ExamplesFile)
                .Select(l => (GoldenRecords.KeyOf(l, "page", "table", "record", "field"), l))
                .ToList();
            Assert.Equal(77957, want.Count);
            GoldenFiles.AssertSame(
                "page blocks of ExampleSA and Example (page|table|record|field)",
                want,
                got
            );
        }

        [GoldenFact(heavy: true)]
        [Trait("Golden", "heavy")]
        public void AllPageBlocksMatchTheGolden()
        {
            var got = new List<(string, string)>();
            var dirOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string dir in Projects())
            {
                string? page = null;
                GoldenJson.Digest? d = null;
                void Flush()
                {
                    if (page == null)
                        return;
                    got.Add(
                        (
                            page,
                            GoldenJson.Write(new Obj().Add("page", page).Add("n", d!.Lines).Add("h", d.Hex()))
                        )
                    );
                    d.Dispose();
                }
                foreach (var r in VmCommands.PageRuns(dir))
                {
                    if (r.Page != page)
                    {
                        Flush();
                        page = r.Page;
                        dirOf[page] = dir;
                        d = new GoldenJson.Digest();
                    }
                    d!.Add(Line(r));
                }
                Flush();
            }
            if (GoldenFiles.WriteIfAsked(AllFile, got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles
                .ReadLines(AllFile)
                .Select(l => (GoldenRecords.KeyOf(l, "page"), l))
                .ToList();
            GoldenFiles.AssertSame(
                "page blocks of every project (page: block count and digest)",
                want,
                got,
                page =>
                {
                    if (!dirOf.TryGetValue(page, out string? dir))
                        return "";
                    var lines = VmCommands.PageRuns(dir).Where(r => r.Page == page).Select(Line).ToList();
                    return "  the page's blocks now (first 40 of "
                        + lines.Count
                        + "):\n    "
                        + string.Join("\n    ", lines.Take(40));
                }
            );
        }
    }
}
