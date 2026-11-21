using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;
using Xunit.Abstractions;
using Obj = CicodeSim.Tests.Golden.GoldenJson.Obj;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The probe corpus, frozen.</summary>
    public class ProbeGoldenTests
    {
        private const string File_ = "probes.jsonl.gz";
        private readonly ITestOutputHelper _out;

        public ProbeGoldenTests(ITestOutputHelper output) => _out = output;

        internal static (string rel, string bytes, string line) Line(FrontendProbes.ProbeRun r)
        {
            string bytes = File.Exists(r.Entry.Path)
                ? GoldenJson.HashBytes(File.ReadAllBytes(r.Entry.Path))
                : "";
            var o = new Obj().Add("probe", r.Rel).Add("bytes", bytes);
            var verdicts = new List<object?>();
            if (r.Text == null)
                o.Add("status", "unreadable");
            else if (r.Crash != null)
                o.Add("status", "crash").Add("crash", r.Crash);
            else
            {
                o.Add("status", "ok");
                var ours = r.Ours!;
                var keys = new List<string>(ours.Keys);
                keys.Sort(StringComparer.Ordinal);
                var targets = new List<object?>();
                foreach (string k in keys)
                    targets.Add(
                        new Obj()
                            .Add("target", k)
                            .Add(
                                "view",
                                ours[k].View.Select(d => (object?)FrontendGoldenTests.Diag(d)).ToList()
                            )
                    );
                o.Add("targets", targets);
                foreach (string t in r.Entry.TargetOrder)
                {
                    var mine = FrontendProbes.Mine(ours, t, r.Multi);
                    var v = new Obj().Add("target", t);
                    if (mine == null)
                        v.Add("no_target", true);
                    else
                    {
                        var rec = FrontendProbes.RecordedErrors(r.Entry.Verdicts[t]);
                        v.Add("recorded", rec)
                            .Add("ours", mine.Errors)
                            .Add("agree", (rec.Count == 0) == mine.Ok);
                    }
                    verdicts.Add(v);
                }
            }
            o.Add("verdicts", verdicts);
            return (r.Rel, bytes, GoldenJson.Write(o));
        }

        /// <summary>The line with each verdict whose recorded codes changed since the freeze taken
        /// from the golden (that is the recorder's change, not the engine's).</summary>
        private static string KeepFrozenVerdicts(string now, string golden, ref int changedVerdicts)
        {
            var n = JsonValue.Parse(now);
            var g = JsonValue.Parse(golden);
            var gv = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            foreach (var v in g["verdicts"].Items)
                gv[v["target"].Str] = v;
            var nv = JsonValue.NewArray();
            foreach (var v in n["verdicts"].Items)
            {
                if (
                    gv.TryGetValue(v["target"].Str, out var w)
                    && v.Has("recorded")
                    && w.Has("recorded")
                    && v["recorded"].Dump() != w["recorded"].Dump()
                )
                {
                    changedVerdicts++;
                    nv.Add(w);
                }
                else
                    nv.Add(v);
            }
            n.Set("verdicts", nv);
            return GoldenJson.Write(n);
        }

        [ProbeFact]
        [Trait("Golden", "heavy")]
        public void ProbesMatchTheGolden()
        {
            var now = new List<(string rel, string bytes, string line)>();
            foreach (var r in FrontendProbes.ProbeRuns(GoldenFiles.User))
                now.Add(Line(r));
            if (GoldenFiles.WriteIfAsked(File_, now.Select(x => x.line)))
                return;
            var byRel = new Dictionary<string, (string bytes, string line)>(StringComparer.Ordinal);
            foreach (var (rel, bytes, line) in now)
                byRel[rel] = (bytes, line);
            var want = new List<(string, string)>();
            var got = new List<(string, string)>();
            int gone = 0,
                changed = 0,
                changedVerdicts = 0,
                agree = 0,
                compiles = 0;
            foreach (string gl in GoldenFiles.ReadLines(File_))
            {
                var g = JsonValue.Parse(gl);
                string rel = g["probe"].Str;
                if (!byRel.TryGetValue(rel, out var x))
                {
                    gone++;
                    continue;
                }
                if (x.bytes != g["bytes"].Str)
                {
                    changed++;
                    continue;
                }
                want.Add((rel, gl));
                string line = KeepFrozenVerdicts(x.line, gl, ref changedVerdicts);
                got.Add((rel, line));
                foreach (var v in JsonValue.Parse(line)["verdicts"].Items)
                    if (v.Has("agree"))
                    {
                        compiles++;
                        if (v["agree"].Bool)
                            agree++;
                    }
            }
            _out.WriteLine(
                $"probes compared {want.Count}, gone {gone}, changed {changed}, not in the golden "
                    + $"{now.Count - want.Count - changed}; recorded verdicts changed {changedVerdicts}; "
                    + $"accept / reject agree {agree} of {compiles}"
            );
            Assert.True(want.Count > 0, "no probe of the golden is in the corpus any more");
            GoldenFiles.AssertSame("probes (path below the probe cases folder)", want, got);
        }
    }

    /// <summary>A golden fact over the probe corpus (skipped without the probe cases or the R2 corpus).</summary>
    public sealed class ProbeFactAttribute : FactAttribute
    {
        public ProbeFactAttribute()
        {
            if (!GoldenFiles.CorpusPresent)
                Skip = "the R2 corpus (" + GoldenFiles.User + ") is not installed";
            else if (!Directory.Exists(FrontendProbes.Cases))
                Skip = "the probe corpus " + FrontendProbes.Cases + " is not here";
            else if (Environment.GetEnvironmentVariable("CTG_SKIP_HEAVY") == "1")
                Skip = "corpus-wide golden check (CTG_SKIP_HEAVY=1)";
        }
    }
}
