using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Values;

namespace CicodeSim.Page
{
    /// <summary>Runs a recorded live page case.</summary>
    public static class LiveCase
    {
        public sealed class Frame
        {
            public string Name = "";
            public Animator Animator = null!;
            public PageState State = null!;
            public int TagsSet,
                TagsFailed;

            /// <summary>The alarms and equipment fields set from the frame (/1), and what could
            /// not be set ('name: why').</summary>
            public int AlarmsSet,
                EquipmentSet;
            public List<string> LiveProblems = new List<string>();
            public int OpenError;
        }

        /// <summary>How long the live page had been open when its window's first case started.</summary>
        public const double RecordedLead = 9.0;

        /// <summary>The seconds a frame's page runs before its two scans: RecordedLead + the
        /// case's <paramref name="caseLead"/> + the frame's <c>at</c> (without one, 0).</summary>
        public static double SettleOf(JsonValue frame, double caseLead = 0.0)
        {
            var at = frame.Get("at");
            return RecordedLead
                + Math.Max(0.0, caseLead)
                + (at != null && at.IsNumber ? Math.Max(0.0, at.ToNumber()) : 0.0);
        }

        /// <summary>The longest pause between two cases of one page's window.</summary>
        public const double SameWindowGap = 30.0;

        /// <summary>A case's start in seconds since 1970 (its <c>recorded</c> time, written when
        /// it ended, less its last frame's <c>at</c>), null when the case does not say.</summary>
        private static double? CaseStart(JsonValue cj)
        {
            string? rec = cj.Get("recorded")?.Str;
            var frames = cj.Get("frames");
            if (
                rec == null
                || frames == null
                || !DateTimeOffset.TryParse(
                    rec,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var t
                )
            )
                return null;
            double last = 0;
            foreach (var f in frames.Items)
            {
                var at = f.Get("at");
                if (at != null && at.IsNumber)
                    last = Math.Max(last, at.ToNumber());
            }
            return t.ToUnixTimeMilliseconds() / 1000.0 - last;
        }

        /// <summary>How much longer than RecordedLead the page had been open when the case started.</summary>
        public static double CaseLead(string caseDir)
        {
            double? me = CaseStart(Load(caseDir));
            string? pageDir = Path.GetDirectoryName(Path.GetFullPath(caseDir).TrimEnd('\\', '/'));
            if (me == null || pageDir == null)
                return 0.0;
            // the page's cases by start, each with its end (recorded); walk back from this case
            // while the one before ended no more than SameWindowGap before the next started
            var cases = new List<(double Start, double End)>();
            foreach (string d in Directory.GetDirectories(pageDir))
            {
                if (!File.Exists(Path.Combine(d, "case.json")))
                    continue;
                JsonValue cj;
                try
                {
                    cj = Load(d);
                }
                catch (Exception)
                {
                    continue;
                }
                double? s = CaseStart(cj);
                string? rec = cj.Get("recorded")?.Str;
                if (s == null || rec == null)
                    continue;
                cases.Add(
                    (
                        s.Value,
                        DateTimeOffset
                            .Parse(rec, System.Globalization.CultureInfo.InvariantCulture)
                            .ToUnixTimeMilliseconds() / 1000.0
                    )
                );
            }
            cases.Sort((x, y) => x.Start.CompareTo(y.Start));
            double head = me.Value;
            for (int i = cases.Count - 1; i >= 0; i--)
            {
                var c = cases[i];
                if (c.Start >= head - 1e-9)
                    continue;
                if (head - c.End > SameWindowGap)
                    break;
                head = c.Start;
            }
            return Math.Max(0.0, me.Value - head);
        }

        public static JsonValue Load(string caseDir) =>
            JsonValue.Parse(File.ReadAllText(Path.Combine(caseDir, "case.json")));

        /// <summary>Run one frame of a case against a compiled project folder.</summary>
        public static Frame Run(
            string projectDir,
            JsonValue caseJson,
            JsonValue frame,
            int scans = 2,
            string compile = Compiler.Codegen.LiveProgram.None,
            CicodeSim.Compiled.Program? library = null,
            List<string>? notes = null,
            double? settle = null,
            PageModel? model = null,
            IReadOnlyList<string>? startup = null
        )
        {
            var r = new Frame { Name = frame.Get("name")?.Str ?? "" };
            var a = Animator.ForProject(projectDir, null, "2026-10-01T08:00:00Z", compile, notes, library);
            r.Animator = a;
            var snap = frame.Get("snapshot");
            void Snapshot()
            {
                r.TagsSet = r.TagsFailed = 0;
                if (snap == null)
                    return;
                foreach (var kv in snap.Pairs)
                {
                    var e = kv.Value;
                    if (e.Get("error") != null)
                        continue;
                    var v = e.Get("value");
                    string q = e.Get("quality")?.Str ?? "good";
                    object? val =
                        v == null || v.IsNull ? null
                        : v.IsNumber ? (object)v.ToNumber()
                        : v.Str;
                    int err = a.World.Tags.Set(kv.Key, val, by: "user");
                    if (err == 0 && q != "good")
                        err = a.World.Tags.SetQuality(kv.Key, q);
                    if (err == 0)
                        r.TagsSet++;
                    else
                        r.TagsFailed++;
                }
                // the alarm server and the equipment as the frame recorded them (after the tags:
                // a tag set marks its alarms for the next scan, a set alarm leaves that work)
                var defs = caseJson.Get("alarmDefs");
                var checkedAlarms = new List<string>();
                if (defs != null)
                    foreach (var kv in defs.Pairs)
                        checkedAlarms.Add(kv.Key);
                var (na, pa) = CicodeSim.World.LiveState.SetAlarms(
                    a.World,
                    frame.Get("alarms"),
                    "live",
                    defs != null ? checkedAlarms : null
                );
                var (ne, pe) = CicodeSim.World.LiveState.SetEquipment(a.World, frame.Get("equipment"));
                r.AlarmsSet = na;
                r.EquipmentSet = ne;
                r.LiveProblems = pa;
                r.LiveProblems.AddRange(pe);
            }
            if (startup != null && startup.Count > 0)
            {
                foreach (string fn in startup)
                {
                    // a name the library does not have is an error, not a silent no-op (a typo
                    // would quietly run the case without the startup)
                    var f = a.Vm.Library.Function(fn);
                    if (f == null)
                        throw new System.ArgumentException("--startup: the project has no function " + fn);
                    a.Scheduler.Spawn(f);
                }
                a.RunFor(0.0);
            }
            Snapshot();
            var ass = caseJson.Get("associations");
            if (ass != null)
                foreach (var kv in ass.Pairs)
                    a.Display.Ass(-2, kv.Key, kv.Value.Text, 0);
            string page = caseJson["page"]["name"].Str;
            r.OpenError = a.Open(page, 0, model);
            if (r.OpenError != 0)
                return r;
            a.RunFor(0.0);
            Snapshot();
            double s = settle ?? SettleOf(frame);
            if (s > 0)
            {
                // scan by scan, as page-run and RunScans step the page (one RunFor of the whole
                // time gives the page's tasks fewer slices: Animator.RunFor runs the ready tasks
                // once more at the end of each call)
                var run = a.Run(0);
                double end = a.World.Clock.Now() + s;
                while (run != null && !run.Closed && a.World.Clock.Now() < end)
                    a.RunFor(Math.Max(0.001, Math.Min(run.NextDue, end) - a.World.Clock.Now()));
                Snapshot();
            }
            a.RunScans(scans, 0);
            r.State = a.Run(0)!.State;
            return r;
        }

        /// <summary>Every frame of a case, in order.</summary>
        public static IEnumerable<Frame> RunAll(
            string projectDir,
            string caseDir,
            string? only = null,
            string compile = Compiler.Codegen.LiveProgram.None,
            List<string>? notes = null,
            double? settle = null,
            PageModel? model = null,
            IReadOnlyList<string>? startup = null
        )
        {
            var cj = Load(caseDir);
            double lead = settle == null ? CaseLead(caseDir) : 0.0;
            CicodeSim.Compiled.Program? library = null;
            foreach (var f in cj["frames"].Items)
            {
                if (only != null && f.Get("name")?.Str != only)
                    continue;
                var fr = Run(
                    projectDir,
                    cj,
                    f,
                    2,
                    compile,
                    library,
                    library == null ? notes : null,
                    settle ?? SettleOf(f, lead),
                    model,
                    startup
                );
                if (compile != Compiler.Codegen.LiveProgram.None)
                    library = fr.Animator.Library;
                yield return fr;
            }
        }
    }
}
