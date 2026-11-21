using System;
using System.Collections.Generic;
using System.Linq;
using CicodeSim.Host;
using Xunit;
using Obj = CicodeSim.Tests.Golden.GoldenJson.Obj;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The VM over the World with the pure built-ins, frozen.</summary>
    [Collection("World")]
    public class WorldSweepGoldenTests
    {
        private const string File = "world_sweep.jsonl.gz";
        internal const string Start = "2026-10-01T08:00:00Z";

        internal static (string key, string line) Line(WorldSweepCommand.Record r)
        {
            var o = new Obj().Add("lib", r.Lib);
            if (r.End)
            {
                o.Add("end", true).Add("used", r.Used);
                return (r.Lib + "|end", GoldenJson.Write(o));
            }
            var t = r.Task!;
            o.Add("fn", r.Fn!.Name).Add("set", r.Set);
            GoldenRecords.AddRun(o, t, r.Outcome, VmCommands.NotSimulatedName(t));
            o.Add("now", r.Now);
            var changes = new List<object?>();
            foreach (var ch in r.Changes)
                changes.Add(new object?[] { ch.Name, ch.Element, ch.Index, ch.Value, ch.Quality });
            o.Add("changes", changes);
            return (
                r.Lib
                    + "|"
                    + r.Fn.Name
                    + "|"
                    + r.Set.ToString(System.Globalization.CultureInfo.InvariantCulture),
                GoldenJson.Write(o)
            );
        }

        private static string KeyOf(string line)
        {
            var j = CicodeSim.Values.JsonValue.Parse(line);
            string lib = j["lib"].Str;
            if (j.Has("end"))
                return lib + "|end";
            return lib + "|" + j["fn"].Str + "|" + j["set"].Dump();
        }

        [GoldenFact(heavy: true)]
        [Trait("Golden", "heavy")]
        public void WorldSweepMatchesTheGolden()
        {
            var bopt = new BuiltinsOptions { Default = true };
            var got = new List<(string, string)>();
            foreach (var r in WorldSweepCommand.Records(GoldenFiles.User, Start, bopt, new HashSet<string>()))
                got.Add(Line(r));
            if (GoldenFiles.WriteIfAsked(File, got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles.ReadLines(File).Select(l => (KeyOf(l), l)).ToList();
            GoldenFiles.AssertSame("world sweep (project|function|argument set, project|end)", want, got);
            // the runs share a World per project: they must also come in the same order
            for (int i = 0; i < want.Count; i++)
                Assert.True(
                    want[i].Item1 == got[i].Item1,
                    $"world sweep: run {i} is {got[i].Item1}, the golden has {want[i].Item1}"
                );
        }
    }
}
