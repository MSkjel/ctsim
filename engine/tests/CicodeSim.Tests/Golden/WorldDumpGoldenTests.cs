using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CicodeSim.Values;
using CicodeSim.World;
using Xunit;
using Obj = CicodeSim.Tests.Golden.GoldenJson.Obj;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The World definition dumps, frozen.</summary>
    [Collection("World")]
    public partial class WorldDumpGoldenTests
    {
        private static CicodeSim.World.World Open(string project) =>
            new CicodeSim.World.World(Path.Combine(GoldenFiles.User, project), root: null, start: 0);

        private static string Rdb(string project) => Path.Combine(GoldenFiles.User, project, "_LIBRARY.RDB");

        private static List<(string, string)> AllRecords(string project)
        {
            var lines = new List<(string, string)>();
            WorldDump.Records(Open(project), (k, o) => lines.Add((k, GoldenJson.Write(o))), Rdb(project));
            lines.Sort(
                (x, y) =>
                {
                    int c = string.CompareOrdinal(x.Item1, y.Item1);
                    return c != 0 ? c : string.CompareOrdinal(x.Item2, y.Item2);
                }
            );
            return lines;
        }

        [GoldenTheory]
        [Trait("Golden", "light")]
        [InlineData("ExampleSA", 12402)]
        [InlineData("Example", 6304)]
        public void DumpMatchesTheGolden(string project, int records)
        {
            string file = "world_" + project + ".jsonl.gz";
            var got = AllRecords(project);
            if (GoldenFiles.WriteIfAsked(file, got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles.ReadLines(file).Select(l => (GoldenRecords.KeyOf(l, "k"), l)).ToList();
            Assert.Equal(records, want.Count);
            GoldenFiles.AssertSame("world dump of " + project + " (record key: kind U+0001 key)", want, got);
        }

        // ------------------------------------------------------------------ large projects
        internal const int Buckets = 64;
        internal const ulong SampleMask = 255;

        /// <summary>The group of a record: its kind, for a tag row the kind and its table.</summary>
        internal static string Group(string k)
        {
            int s = k.IndexOf('\u0001');
            string kind = s < 0 ? k : k.Substring(0, s);
            if (kind == "tag" && s >= 0)
            {
                int bar = k.IndexOf('|', s + 1);
                if (bar > 0)
                    return kind + "|" + k.Substring(s + 1, bar - s - 1);
            }
            return kind;
        }
    }

    /// <summary>The theory form of <see cref="GoldenFactAttribute"/>.</summary>
    public sealed class GoldenTheoryAttribute : TheoryAttribute
    {
        public GoldenTheoryAttribute(bool heavy = false)
        {
            if (!GoldenFiles.CorpusPresent)
                Skip = "the R2 corpus (" + GoldenFiles.User + ") is not installed";
            else if (heavy && Environment.GetEnvironmentVariable("CTG_SKIP_HEAVY") == "1")
                Skip = "corpus-wide golden check (CTG_SKIP_HEAVY=1)";
        }
    }
}
