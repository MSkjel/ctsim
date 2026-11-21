using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CicodeSim.Tests.World;
using CicodeSim.Values;
using CicodeSim.World;
using Xunit;
using Obj = CicodeSim.Tests.Golden.GoldenJson.Obj;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The World on the synthetic install, frozen.</summary>
    [Collection("World")]
    public class SyntheticGoldenTests : IDisposable
    {
        private const string Start = "2026-10-01T08:00:00Z";
        private readonly TempDir _tmp = new TempDir();

        public void Dispose() => _tmp.Dispose();

        /// <summary>A trace or result line: a header without its "engine", a scenario event's
        /// "path" as the file name (the checkout is anywhere).</summary>
        private static string Line(JsonValue v, bool header)
        {
            if (v.Kind != JsonKind.Object)
                return GoldenJson.Write(v);
            var o = new Obj();
            foreach (var kv in v.Pairs)
            {
                if (header && kv.Key == "engine")
                    continue;
                if (kv.Key == "path" && kv.Value.Kind == JsonKind.String)
                    o.Add(kv.Key, Path.GetFileName(kv.Value.Str));
                else
                    o.Add(kv.Key, kv.Value);
            }
            return GoldenJson.Write(o);
        }

        private static string ScenarioDir() =>
            Path.GetDirectoryName(GoldenFiles.ReadPath(Path.Combine("scenarios", "main.json")))!;

        internal List<(string, string)> ScenarioLines()
        {
            string main = WorldFixture.MakeInstall(_tmp.Path);
            var got = new List<(string, string)>();
            var files = Directory.GetFiles(ScenarioDir(), "*.json").ToList();
            files.Sort(StringComparer.Ordinal);
            foreach (string sc in files)
            {
                string name = Path.GetFileNameWithoutExtension(sc);
                WorldT.ClearCaches();
                var w = new WorldT(main, root: null, start: Start);
                var buf = new StringWriter { NewLine = "\n" };
                var tw = new TraceWriter(
                    stream: buf,
                    project: w.Project,
                    clock: w.Clock,
                    scenario: Path.GetFileName(sc)
                );
                w.AttachTrace(tw);
                w.Start();
                var res = w.RunScenario(sc);
                tw.Close();
                got.Add(
                    (
                        name + "|result",
                        GoldenJson.Write(
                            new Obj()
                                .Add("scenario", name)
                                .Add("kind", "result")
                                .Add("passed", res.Passed)
                                .Add("failed", res.Failed.Count)
                                .Add("skipped", res.Skipped.Count)
                                .Add("errors", res.Errors.Count)
                                .Add("end", res.End)
                        )
                    )
                );
                int i = 0;
                foreach (string l in buf.ToString().Split('\n'))
                {
                    if (l.Length == 0)
                        continue;
                    got.Add(
                        (
                            name + "|" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "{\"i\":"
                                + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + ",\"kind\":\"trace\",\"scenario\":"
                                + GoldenJson.Write(name)
                                + ",\"v\":"
                                + Line(JsonValue.Parse(l), i == 0)
                                + "}"
                        )
                    );
                    i++;
                }
            }
            return got;
        }

        private static string KeyOf(string line)
        {
            var j = JsonValue.Parse(line);
            return j["scenario"].Str + "|" + (j["kind"].Str == "result" ? "result" : j["i"].Dump());
        }

        [WindowsGoldenFact]
        [Trait("Golden", "light")]
        public void ScenariosMatchTheGolden()
        {
            var got = ScenarioLines();
            if (GoldenFiles.WriteIfAsked("scenarios.jsonl.gz", got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles.ReadLines("scenarios.jsonl.gz").Select(l => (KeyOf(l), l)).ToList();
            GoldenFiles.AssertSame(
                "scenarios on the synthetic install (scenario|result, scenario|trace line)",
                want,
                got
            );
        }

        [WindowsGoldenFact]
        [Trait("Golden", "light")]
        public void WorldOpsMatchTheGolden()
        {
            string main = WorldFixture.MakeInstall(_tmp.Path);
            var inputs = new SortedDictionary<int, List<string>>();
            foreach (string l in GoldenFiles.ReadLines("world_ops_inputs.jsonl.gz"))
            {
                int tab = l.IndexOf('\t');
                int seed = int.Parse(l.Substring(0, tab), System.Globalization.CultureInfo.InvariantCulture);
                if (!inputs.TryGetValue(seed, out var lst))
                    inputs[seed] = lst = new List<string>();
                lst.Add(l.Substring(tab + 1));
            }
            var got = new List<(string, string)>();
            foreach (var kv in inputs)
            {
                string ops = Path.Combine(_tmp.Path, "ops.jsonl"),
                    outPath = Path.Combine(_tmp.Path, "out.txt");
                File.WriteAllText(ops, string.Join("\n", kv.Value) + "\n");
                WorldT.ClearCaches();
                WorldOps.Run(main, ops, outPath);
                int i = 0;
                bool trace = false,
                    header = false;
                foreach (string l in File.ReadAllText(outPath).Split('\n'))
                {
                    if (l.Length == 0)
                        continue;
                    string v;
                    if (l == "#trace")
                    {
                        trace = header = true;
                        v = GoldenJson.Write("#trace");
                    }
                    else
                    {
                        v = Line(JsonValue.Parse(l), trace && header);
                        if (trace)
                            header = false;
                    }
                    string seed = kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        si = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    got.Add((seed + "|" + si, "{\"i\":" + si + ",\"seed\":" + seed + ",\"v\":" + v + "}"));
                    i++;
                }
            }
            if (GoldenFiles.WriteIfAsked("world_ops.jsonl.gz", got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles
                .ReadLines("world_ops.jsonl.gz")
                .Select(l => (GoldenRecords.KeyOf(l, "seed", "i"), l))
                .ToList();
            GoldenFiles.AssertSame(
                "world operations on the synthetic install (seed|line: results, '#trace', the trace)",
                want,
                got
            );
        }
    }

    /// <summary>A golden fact that runs on Windows only (frozen with the Universal CRT's maths).</summary>
    public sealed class WindowsGoldenFactAttribute : FactAttribute
    {
        public WindowsGoldenFactAttribute()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Skip = "frozen on Windows (the Universal CRT's sin); not compared elsewhere";
        }
    }
}
