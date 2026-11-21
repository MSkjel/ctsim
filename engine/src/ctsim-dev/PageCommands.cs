using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Page;
using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>ctsim-dev page-run and page-sweep.</summary>
    internal static class PageCommands
    {
        public const string Usage =
            "ctsim-dev page-run --project <dir> --page <name> [--model page.json] [--tags Tag=v ...] [--scans n]\n"
            + "               [--input scan:kind[:an][:arg] ...] [--window n] [--start iso] [--root name] [--full] [--json]\n"
            + "               [--compile none|all|changed]\n"
            + "ctsim-dev page-sweep --project <dir> [--scans n] [--pages name,name] [--models dir] [--missing file]\n"
            + "               [--touch n] [--settle-ms ms] [--rows] [--json] [--out file] [--compile none|all|changed]\n"
            + "ctsim-dev page-case <case dir> --project <dir> [--frame name] [--json] [--compile none|all|changed] [--settle s] [--model page.json]\n"
            + "               [--startup fn[,fn ...]]";

        public static bool Handles(string verb) =>
            verb == "page-run" || verb == "page-sweep" || verb == "page-case";

        public static int Dispatch(string[] args)
        {
            var rest = new List<string>(args);
            rest.RemoveAt(0);
            try
            {
                return args[0] == "page-run" ? PageRun(rest)
                    : args[0] == "page-case" ? PageCase(rest)
                    : PageSweep(rest);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine("usage: " + Usage);
                return 2;
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                // --compile of a project that is not there (LiveProgram.Open)
                Console.Error.WriteLine(e.Message);
                return 2;
            }
        }

        private static List<string> TakeAll(List<string> args, string name)
        {
            var o = new List<string>();
            int k;
            while ((k = args.IndexOf(name)) >= 0)
            {
                args.RemoveAt(k);
                while (k < args.Count && !args[k].StartsWith("--", StringComparison.Ordinal))
                {
                    o.Add(args[k]);
                    args.RemoveAt(k);
                }
            }
            return o;
        }

        /// <summary>--compile changed|all|none: where the library code comes from (Animator.ForProject);
        /// default none, the compiled library the page RDBs were compiled with.</summary>
        private static string TakeCompile(List<string> args)
        {
            string m = VmCommands.TakeOption(args, "--compile")
                ?? CicodeSim.Compiler.Codegen.LiveProgram.None;
            if (!CicodeSim.Compiler.Codegen.LiveProgram.IsMode(m))
                throw new ArgumentException("--compile must be changed, all or none: " + m);
            return m;
        }

        /// <summary>What the compiler said (load notes, errors, functions not compiled), on stderr.</summary>
        private static void PrintNotes(List<string> notes)
        {
            foreach (string n in notes)
                Console.Error.WriteLine("compile: " + n);
        }

        /// <summary>page-sweep keeps this many compiled pages (--rows) and page models (--models).</summary>
        private const int RowsKept = 16;

        private static int Int(string? s, int d) =>
            s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : d;

        /// <summary>One --input: applied after the given scan of the window.</summary>
        private sealed class InputStep
        {
            public int Scan;
            public string Kind = "";
            public int An;
            public string Arg = "";
            public string Text = "";
        }

        private static InputStep ParseInput(string text)
        {
            var p = text.Split(':');
            if (p.Length < 2)
                throw new ArgumentException("--input needs scan:kind[:an][:arg]: " + text);
            var s = new InputStep
            {
                Scan = Int(p[0], -1),
                Kind = p[1].ToLowerInvariant(),
                Text = text,
            };
            if (s.Scan < 0)
                throw new ArgumentException("--input: the scan is a number: " + text);
            switch (s.Kind)
            {
                case "down":
                case "up":
                case "click":
                case "dblclick":
                case "release":
                    if (p.Length < 3)
                        throw new ArgumentException("--input " + s.Kind + " needs an AN: " + text);
                    s.An = Int(p[2], 0);
                    if (s.Kind == "release" && p.Length > 3)
                        s.Arg = p[3];
                    break;
                case "drag":
                    if (p.Length < 4)
                        throw new ArgumentException("--input drag needs an AN and dx,dy or angle=a: " + text);
                    s.An = Int(p[2], 0);
                    s.Arg = p[3];
                    break;
                case "key":
                case "move":
                case "wait":
                case "activate":
                case "deactivate":
                    s.Arg = p.Length > 2 ? string.Join(":", p, 2, p.Length - 2) : "";
                    break;
                default:
                    throw new ArgumentException(
                        "--input kind: down, up, click, dblclick, drag, release, key, move, wait, activate, deactivate: "
                            + text
                    );
            }
            if (s.Arg.Length > 0 && (s.Kind == "drag" || s.Kind == "release"))
            {
                string nums = s.Arg.StartsWith("angle=", StringComparison.OrdinalIgnoreCase)
                    ? s.Arg.Substring(6)
                    : s.Arg;
                foreach (string n in nums.Split(','))
                    if (!double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        throw new ArgumentException(
                            "--input " + s.Kind + " needs dx,dy or angle=a (numbers): " + text
                        );
            }
            return s;
        }

        private static void Apply(Animator a, int window, InputStep s, StringBuilder log)
        {
            bool ok = true;
            switch (s.Kind)
            {
                case "down":
                    ok = a.Touch(window, s.An, Animator.TouchKind.Down);
                    break;
                case "up":
                    ok = a.Touch(window, s.An, Animator.TouchKind.Up);
                    break;
                case "click":
                    ok = a.Touch(window, s.An, Animator.TouchKind.Down);
                    a.RunFor(0.05);
                    ok |= a.Touch(window, s.An, Animator.TouchKind.Up);
                    break;
                case "dblclick":
                    ok = a.Touch(window, s.An, Animator.TouchKind.DoubleClick);
                    break;
                case "key":
                    ok = a.Key(window, s.Arg);
                    break;
                case "move":
                {
                    var xy = s.Arg.Split(',');
                    a.Mouse(window, Int(xy[0], -1), Int(xy.Length > 1 ? xy[1] : "-1", -1));
                    break;
                }
                case "drag":
                case "release":
                {
                    double? dx = null,
                        dy = null,
                        ang = null;
                    double Number(string text) =>
                        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                            ? v
                            : throw new ArgumentException("--input " + s.Kind + ": not a number: " + s.Text);
                    if (s.Arg.StartsWith("angle=", StringComparison.OrdinalIgnoreCase))
                        ang = Number(s.Arg.Substring(6));
                    else if (s.Arg.Length > 0)
                    {
                        var xy = s.Arg.Split(',');
                        dx = Number(xy[0]);
                        if (xy.Length > 1)
                            dy = Number(xy[1]);
                    }
                    ok = a.Drag(window, s.An, dx, dy, ang, s.Kind == "release");
                    break;
                }
                case "wait":
                    a.RunFor(Int(s.Arg, 0) / 1000.0);
                    break;
                case "activate":
                    a.Activate(window, true);
                    break;
                case "deactivate":
                    a.Activate(window, false);
                    break;
            }
            log.Append(s.Text).Append(ok ? "" : " (no command)").Append("; ");
        }

        private static void SetTags(Animator a, List<string> sets)
        {
            foreach (string s in sets)
            {
                int eq = s.IndexOf('=');
                if (eq <= 0)
                    throw new ArgumentException("--tags needs Tag=value: " + s);
                string name = s.Substring(0, eq),
                    text = s.Substring(eq + 1);
                object value = double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double d
                )
                    ? (object)d
                    : text;
                int err = a.World.Tags.Set(name, value, by: "user");
                if (err != 0)
                    throw new ArgumentException($"--tags {name}: error {err}");
            }
        }

        private static int PageRun(List<string> args)
        {
            string? project = VmCommands.TakeOption(args, "--project");
            string? page = VmCommands.TakeOption(args, "--page");
            string? modelPath = VmCommands.TakeOption(args, "--model");
            string compile = TakeCompile(args);
            string? root = VmCommands.TakeOption(args, "--root");
            string? start = VmCommands.TakeOption(args, "--start");
            int scans = Int(VmCommands.TakeOption(args, "--scans"), 3);
            int window = Int(VmCommands.TakeOption(args, "--window"), 0);
            bool json = VmCommands.TakeFlag(args, "--json");
            bool full = VmCommands.TakeFlag(args, "--full");
            var tags = TakeAll(args, "--tags");
            for (string? t; (t = VmCommands.TakeOption(args, "--tag")) != null; )
                tags.Add(t);
            var inputs = new List<InputStep>();
            for (string? s; (s = VmCommands.TakeOption(args, "--input")) != null; )
                inputs.Add(ParseInput(s));
            if (project == null || page == null)
                throw new ArgumentException("page-run needs --project and --page");
            var notes = new List<string>();
            var a = Animator.ForProject(project, root, start ?? "2026-10-01T08:00:00Z", compile, notes);
            PrintNotes(notes);
            SetTags(a, tags);
            PageModel? model = modelPath != null ? PageModel.Load(modelPath) : null;
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
            var changes = new List<List<ObjectState>>();
            a.PageChanged += (st, ch) =>
            {
                if (st.Window == window)
                    changes.Add(ch);
            };
            int err;
            if (window == 0)
                err = a.Open(page, 0, model);
            else
            {
                int n = a.NewWindow(page, 0, 0, 0, -1, model);
                err = n < 0 ? 261 : 0;
                window = n;
            }
            if (model != null)
                foreach (string note in model.Notes)
                    Console.Error.WriteLine("model: " + note);
            if (err != 0)
            {
                Console.Error.WriteLine($"page {page}: error {err}");
                return 1;
            }
            // the page's entry task runs first (no time passes); the given tags win over what it set
            a.RunFor(0.0);
            SetTags(a, tags);
            var run = a.Run(window)!;
            void Print(string inputText)
            {
                var st = run.State;
                if (json)
                {
                    var j = full ? st.ToJson() : st.ToJson(Merge(changes));
                    j.Set("t", JsonValue.Of(a.World.Clock.Now()));
                    if (inputText.Length > 0)
                        j.Set("input", inputText);
                    o.WriteLine(j.Dump());
                }
                else
                {
                    o.WriteLine(
                        $"scan {st.Scan} t={a.World.Clock.Now().ToString("F3", CultureInfo.InvariantCulture)} serial {st.Serial} flash {(st.FlashOn ? "on" : "off")}{(inputText.Length > 0 ? " inputs " + inputText : "")}"
                    );
                    foreach (var s in full ? new List<ObjectState>(st.Objects.Values) : Merge(changes))
                        o.WriteLine("  " + s.ToCompactJson().Dump());
                }
                changes.Clear();
            }
            Print("");
            for (int k = 1; k < scans || inputs.Exists(i => i.Scan >= k); k++)
            {
                var sb = new StringBuilder();
                foreach (var i in inputs)
                    if (i.Scan == (int)run.Scans)
                        Apply(a, window, i, sb);
                a.RunScans(1, window);
                Print(sb.ToString().TrimEnd(' ', ';'));
            }
            var sum = JsonValue.NewObject();
            var evs = JsonValue.NewArray();
            foreach (var e in a.Events)
                evs.Add(JsonValue.Of(e.ToString()));
            sum.Set("events", evs);
            sum.Set("notSimulated", NotSimulatedJson(a));
            var logs = JsonValue.NewArray();
            foreach (var l in a.Logs)
                logs.Add(JsonValue.Of(l));
            sum.Set("log", logs);
            if (json)
                o.WriteLine(sum.Dump());
            else
            {
                o.WriteLine("events:");
                foreach (var e in a.Events)
                    o.WriteLine("  " + e);
                o.WriteLine("not simulated: " + NotSimulatedJson(a).Dump());
                foreach (var l in a.Logs)
                    o.WriteLine("log: " + l);
            }
            o.Flush();
            return 0;
        }

        /// <summary>The changed states of several emits, the last of each AN, in AN order.</summary>
        private static List<ObjectState> Merge(List<List<ObjectState>> changes)
        {
            var by = new SortedDictionary<int, ObjectState>();
            foreach (var l in changes)
            foreach (var s in l)
                by[s.An] = s;
            return new List<ObjectState>(by.Values);
        }

        private static JsonValue NotSimulatedJson(Animator a)
        {
            var tot = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var kv in a.Stats)
            foreach (var ns in kv.Value.NotSimulated)
            {
                tot.TryGetValue(ns.Key, out long n);
                tot[ns.Key] = n + ns.Value;
            }
            var j = JsonValue.NewObject();
            foreach (var kv in tot)
                j.Set(kv.Key, JsonValue.Of(kv.Value));
            return j;
        }

        // ------------------------------------------------------------------ page-case
        /// <summary>ctsim-dev page-case: run a recorded live page case.</summary>
        private static int PageCase(List<string> args)
        {
            string? project = VmCommands.TakeOption(args, "--project");
            string? only = VmCommands.TakeOption(args, "--frame");
            string compile = TakeCompile(args);
            bool json = VmCommands.TakeFlag(args, "--json");
            string? settleText = VmCommands.TakeOption(args, "--settle");
            string? modelPath = VmCommands.TakeOption(args, "--model");
            // project functions the runtime's startup code had started before the capture (none by default)
            var startup = new List<string>();
            string? startupText = VmCommands.TakeOption(args, "--startup");
            if (!string.IsNullOrEmpty(startupText))
                foreach (string fn in startupText!.Split(','))
                    if (fn.Trim().Length > 0)
                        startup.Add(fn.Trim());
            if (args.Count < 1 || project == null)
                throw new ArgumentException("page-case needs a case folder and --project");
            double? settle = null;
            if (settleText != null)
            {
                if (
                    !double.TryParse(
                        settleText,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double sv
                    )
                    || sv < 0
                )
                    throw new ArgumentException(
                        "--settle needs a number of seconds (0 or more): " + settleText
                    );
                settle = sv;
            }
            PageModel? model = modelPath != null ? PageModel.Load(modelPath) : null;
            string dir = args[0];
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
            var notes = new List<string>();
            foreach (var fr in LiveCase.RunAll(project, dir, only, compile, notes, settle, model, startup))
            {
                PrintNotes(notes); // the compile's, once (at the first frame)
                notes.Clear();
                string fname = fr.Name;
                var a = fr.Animator;
                int set = fr.TagsSet,
                    failed = fr.TagsFailed;
                if (fr.OpenError != 0)
                {
                    o.WriteLine($"{fname}: open error {fr.OpenError}");
                    continue;
                }
                var st = fr.State;
                var line = JsonValue.NewObject();
                line.Set("frame", fname);
                line.Set("tagsSet", JsonValue.Of(set));
                line.Set("tagsFailed", JsonValue.Of(failed));
                line.Set("alarmsSet", JsonValue.Of(fr.AlarmsSet));
                line.Set("equipmentSet", JsonValue.Of(fr.EquipmentSet));
                if (fr.LiveProblems.Count > 0)
                {
                    var lp = JsonValue.NewArray();
                    foreach (string p in fr.LiveProblems)
                        lp.Add(JsonValue.Of(p));
                    line.Set("liveProblems", lp);
                }
                line.Set("notSimulated", NotSimulatedJson(a));
                var objs = JsonValue.NewArray();
                var list = new List<ObjectState>(st.Objects.Values);
                list.Sort((x, y) => x.An.CompareTo(y.An));
                foreach (var s in list)
                {
                    var c = s.ToCompactJson();
                    if (c.Count > 1)
                        objs.Add(c);
                }
                line.Set("objects", objs);
                if (json)
                    o.WriteLine(line.Dump());
                else
                {
                    o.WriteLine(
                        $"{fname}: {set} tags set ({failed} failed), {fr.AlarmsSet} alarms, {fr.EquipmentSet} equipment fields"
                            + (
                                fr.LiveProblems.Count > 0
                                    ? $" ({fr.LiveProblems.Count} not set: {fr.LiveProblems[0]})"
                                    : ""
                            )
                            + $", not simulated {NotSimulatedJson(a).Dump()}"
                    );
                    foreach (var c in objs.Items)
                        o.WriteLine("  " + c.Dump());
                }
            }
            o.Flush();
            return 0;
        }

        /// <summary>The counters of all classes added up (one page's share = after - before).</summary>
        private sealed class SweepTotals
        {
            public long Evaluations,
                Halted;
            public readonly Dictionary<int, long> HaltCodes = new Dictionary<int, long>();
            public readonly Dictionary<string, long> NotSimulated = new Dictionary<string, long>(
                StringComparer.Ordinal
            );
            public readonly Dictionary<int, long> Errors = new Dictionary<int, long>();

            public static SweepTotals Of(Animator a)
            {
                var t = new SweepTotals();
                foreach (var s in a.Stats.Values)
                {
                    t.Evaluations += s.Evaluations;
                    t.Halted += s.Halted;
                    Add(t.HaltCodes, s.HaltCodes);
                    Add(t.NotSimulated, s.NotSimulated);
                    Add(t.Errors, s.Errors);
                }
                return t;
            }

            public SweepTotals Minus(SweepTotals b)
            {
                var t = new SweepTotals
                {
                    Evaluations = Evaluations - b.Evaluations,
                    Halted = Halted - b.Halted,
                };
                Sub(t.HaltCodes, HaltCodes, b.HaltCodes);
                Sub(t.NotSimulated, NotSimulated, b.NotSimulated);
                Sub(t.Errors, Errors, b.Errors);
                return t;
            }
        }

        private static void Add<K>(Dictionary<K, long> to, Dictionary<K, long> from)
            where K : notnull
        {
            foreach (var kv in from)
            {
                to.TryGetValue(kv.Key, out long n);
                to[kv.Key] = n + kv.Value;
            }
        }

        private static void Sub<K>(Dictionary<K, long> to, Dictionary<K, long> a, Dictionary<K, long> b)
            where K : notnull
        {
            foreach (var kv in a)
            {
                b.TryGetValue(kv.Key, out long n);
                if (kv.Value - n != 0)
                    to[kv.Key] = kv.Value - n;
            }
        }

        private static void Bump<K>(Dictionary<K, long> d, K k, long by = 1)
            where K : notnull
        {
            d.TryGetValue(k, out long n);
            d[k] = n + by;
        }

        private static JsonValue CountsJson<K>(Dictionary<K, long> d, Func<K, string> key)
            where K : notnull
        {
            var l = new List<KeyValuePair<K, long>>(d);
            l.Sort(
                (x, y) =>
                    y.Value != x.Value
                        ? y.Value.CompareTo(x.Value)
                        : string.CompareOrdinal(key(x.Key), key(y.Key))
            );
            var j = JsonValue.NewObject();
            foreach (var kv in l)
                j.Set(key(kv.Key), JsonValue.Of(kv.Value));
            return j;
        }

        private static string IntKey(int k) => k.ToString(CultureInfo.InvariantCulture);

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static double R2(double v) => Math.Round(v, 2);

        private static double Percentile(List<double> sorted, double p)
        {
            if (sorted.Count == 0)
                return 0;
            int k = (int)Math.Ceiling(p * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, k))];
        }

        /// <summary>What clicking a page's touch objects did (page-sweep --touch).</summary>
        private sealed class TouchReport
        {
            public int Clicked,
                Ran,
                Popups,
                Navigated,
                Reopened;
            public readonly List<string> PopupPages = new List<string>();
            public readonly List<string> Away = new List<string>();
            public readonly List<string> Notes = new List<string>();
        }

        /// <summary>Click every object with a touch, button or keyboard command.</summary>
        private static TouchReport TouchAll(
            Animator a,
            string page,
            int limit,
            double settle,
            PageModel? model
        )
        {
            var rep = new TouchReport();
            var run = a.Run(0);
            if (run == null)
                return rep;
            // the touch objects and buttons, and the objects with keyboard commands (the mouse
            // key codes of the command cursor run them on a click: a library's genie buttons)
            var set = new HashSet<int>(run.Touches.Keys);
            foreach (var k in run.Keys)
                if (k.An > 0)
                    set.Add(k.An);
            var ans = new List<int>(set);
            ans.Sort();
            foreach (int an in ans)
            {
                if (rep.Clicked >= limit)
                    break;
                var cur = a.Run(0);
                if (cur == null || !string.Equals(cur.Page.Name, page, StringComparison.OrdinalIgnoreCase))
                {
                    if (a.Open(page, 0, model) != 0)
                        break;
                    rep.Reopened++;
                    a.RunScans(1, 0);
                }
                var before = new HashSet<int>();
                foreach (var w in a.Display.Windows)
                    if (w.Open)
                        before.Add(w.Number);
                rep.Clicked++;
                var errsBefore = SweepTotals.Of(a).Errors;
                // the pointer on the object first when the drawing gives its extent (an XP
                // button's command halts when the button is released off the object)
                var ao = a.Run(0)?.Page.Get(an);
                if (ao != null && ao.Extent.Width > 0 && ao.Extent.Height > 0)
                    a.Mouse(0, (ao.Extent.L + ao.Extent.R) / 2, (ao.Extent.T + ao.Extent.B) / 2);
                bool ran = a.Touch(0, an, Animator.TouchKind.Down);
                a.RunFor(0.05);
                ran |= a.Touch(0, an, Animator.TouchKind.Up);
                a.RunFor(settle);
                if (ran)
                    rep.Ran++;
                foreach (var w in new List<CicodeSim.World.Display.Window>(a.Display.Windows))
                {
                    if (!w.Open || w.Number == 0 || before.Contains(w.Number))
                        continue;
                    rep.Popups++;
                    string pn = w.Page?.Name ?? "";
                    if (pn.Length > 0 && rep.PopupPages.Count < 6 && !rep.PopupPages.Contains(pn))
                        rep.PopupPages.Add(pn);
                    var errsNow = SweepTotals.Of(a).Errors;
                    errsBefore.TryGetValue(323, out long e323a);
                    errsNow.TryGetValue(323, out long e323b);
                    if (e323b > e323a && rep.Notes.Count < 10)
                    {
                        var ass = new List<string>(w.Associations.Keys);
                        ass.Sort(StringComparer.OrdinalIgnoreCase);
                        var decl = new List<string>();
                        if (w.Page != null)
                            foreach (var pa in w.Page.Associations)
                                decl.Add(pa.Name);
                        rep.Notes.Add(
                            pn
                                + " (AN "
                                + an.ToString(CultureInfo.InvariantCulture)
                                + "): 323 x"
                                + (e323b - e323a).ToString(CultureInfo.InvariantCulture)
                                + ", associated {"
                                + string.Join(",", ass)
                                + "}, declared {"
                                + string.Join(",", decl)
                                + "}"
                        );
                    }
                }
                foreach (var w in new List<CicodeSim.World.Display.Window>(a.Display.Windows))
                    if (w.Open && w.Number != 0)
                        a.FreeWindow(w.Number);
                var now = a.Run(0);
                if (now == null || !string.Equals(now.Page.Name, page, StringComparison.OrdinalIgnoreCase))
                {
                    rep.Navigated++;
                    string to = now?.Page.Name ?? "";
                    if (to.Length > 0 && rep.Away.Count < 6 && !rep.Away.Contains(to))
                        rep.Away.Add(to);
                }
            }
            var last = a.Run(0);
            if (last == null || !string.Equals(last.Page.Name, page, StringComparison.OrdinalIgnoreCase))
            {
                if (a.Open(page, 0, model) == 0)
                    rep.Reopened++;
            }
            return rep;
        }

        /// <summary>ctsim-dev page-sweep: open and scan every page.</summary>
        private static int PageSweep(List<string> args)
        {
            string? project = VmCommands.TakeOption(args, "--project");
            string? only = VmCommands.TakeOption(args, "--pages");
            string compile = TakeCompile(args);
            string? outPath = VmCommands.TakeOption(args, "--out");
            string? models = VmCommands.TakeOption(args, "--models");
            string? missingOut = VmCommands.TakeOption(args, "--missing");
            int touch = Int(VmCommands.TakeOption(args, "--touch"), 0);
            bool rows = VmCommands.TakeFlag(args, "--rows");
            double settle = Int(VmCommands.TakeOption(args, "--settle-ms"), 600) / 1000.0;
            long tClicked = 0,
                tRan = 0,
                tPopups = 0,
                tNavigated = 0;
            double tSeconds = 0;
            var tPopupPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int scans = Int(VmCommands.TakeOption(args, "--scans"), 3);
            bool json = VmCommands.TakeFlag(args, "--json");
            if (project == null)
                throw new ArgumentException("page-sweep needs --project");
            var notes = new List<string>();
            var load = Stopwatch.StartNew();
            var a = Animator.ForProject(project, null, "2026-10-01T08:00:00Z", compile, notes);
            double loadSeconds = load.Elapsed.TotalSeconds;
            PrintNotes(notes);
            // --rows: every page's code compiled from its project's own pg rows (the DBFs as they
            // are now, what CtCmp32 would compile; pages-server's PageRowCompiler) instead of its
            // .RDB; the page list adds every pages.dbf page of the unit
            var rowInfo = new Dictionary<
                string,
                (int records, int notes, string first, double ms, string project)
            >(StringComparer.OrdinalIgnoreCase);
            if (rows)
            {
                CicodeSim.Compiler.Semantics.CompileUnit? unit = null;
                CicodeSim.Compiler.Codegen.TagTable? tagTable = null;
                // the last few pages only (a popup / navigation asks for the same pages again): keeping
                // every compiled page runs out of memory on a large project
                var compiled = new Dictionary<string, CompiledPage?>(StringComparer.OrdinalIgnoreCase);
                var compiledOrder = new Queue<string>();
                a.CompiledSource = name =>
                {
                    if (compiled.TryGetValue(name, out var have))
                        return have;
                    while (compiled.Count >= RowsKept && compiledOrder.Count > 0)
                        compiled.Remove(compiledOrder.Dequeue());
                    compiledOrder.Enqueue(name);
                    if (unit == null)
                    {
                        var lsw = Stopwatch.StartNew();
                        unit = CicodeSim.Compiler.Codegen.Units.ForProject(a.World.Unit.RootDir).Compile();
                        tagTable = CicodeSim.Compiler.Codegen.TagTable.For(unit);
                        Console.Error.WriteLine(
                            "rows: the unit compiled in "
                                + lsw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                                + " ms"
                        );
                    }
                    CompiledPage? cp = null;
                    foreach (var p in a.World.Unit.Search)
                    {
                        var (t, c) = CicodeSim.Compiler.Codegen.PageRowCompiler.ProjectRows(p.Dir, name);
                        if (c == null && t.Count == 0)
                            continue;
                        var rn = new List<string>();
                        var csw = Stopwatch.StartNew();
                        cp = CicodeSim.Compiler.Codegen.PageRowCompiler.Compile(
                            unit,
                            tagTable!,
                            name,
                            t,
                            c,
                            x => a.World.Unit.LabelValue(x),
                            rn,
                            p.Name
                        );
                        int recs = 0;
                        foreach (var tb in cp.Tables.Values)
                            recs += tb.Count;
                        rowInfo[name] = (
                            recs,
                            rn.Count,
                            rn.Count > 0 ? rn[0] : "",
                            csw.Elapsed.TotalMilliseconds,
                            p.Name
                        );
                        break;
                    }
                    compiled[name] = cp;
                    return cp;
                };
            }
            if (models != null)
            {
                // a page another page opens (a popup's WinNew, PageDisplay) takes its model too
                var cache = new Dictionary<string, CicodeSim.World.Display.PageDrawing?>(
                    StringComparer.OrdinalIgnoreCase
                );
                var cacheOrder = new Queue<string>();
                a.DrawingSource = rec =>
                {
                    if (cache.TryGetValue(rec.Name, out var dr))
                        return dr;
                    while (cache.Count >= RowsKept && cacheOrder.Count > 0)
                        cache.Remove(cacheOrder.Dequeue());
                    cacheOrder.Enqueue(rec.Name);
                    string mp = Path.Combine(models, rec.Name + ".json");
                    dr = File.Exists(mp) ? PageModel.Load(mp).Drawing : null;
                    cache[rec.Name] = dr;
                    return dr;
                };
            }
            var names = new List<string>();
            if (only != null)
                names.AddRange(only.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            else if (a.Display.Catalogue != null)
                foreach (var p in a.Display.Catalogue.Pages)
                    names.Add(p.Name);
            if (rows && only == null)
            {
                // the pages the unit's pages.dbf files list that the catalogue does not
                var seenNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                foreach (var p in a.World.Unit.Search)
                {
                    string? pdbf = null;
                    if (Directory.Exists(p.Dir))
                        foreach (string f in Directory.GetFiles(p.Dir, "*.dbf"))
                            if (
                                string.Equals(
                                    Path.GetFileName(f),
                                    "pages.dbf",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                                pdbf = f;
                    if (pdbf == null)
                        continue;
                    CicodeSim.Project.Dbf.ForEach(pdbf, rec =>
                    {
                        string n = CicodeSim.Project.CitectText.Trim(rec.Field(rec.Header.IndexOf("NAME")));
                        if (n.Length > 0 && seenNames.Add(n))
                            names.Add(n);
                    });
                }
            }
            TextWriter o =
                outPath != null
                    ? new StreamWriter(outPath, false, new UTF8Encoding(false)) { NewLine = "\n" }
                    : new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
                    {
                        NewLine = "\n",
                    };
            int opened = 0,
                failed = 0,
                exceptions = 0,
                withModel = 0;
            long states = 0;
            var openErrors = new Dictionary<int, long>();
            var taskHalts = new Dictionary<int, long>();
            var taskNotSim = new Dictionary<string, long>(StringComparer.Ordinal);
            var pageScanMean = new List<double>();
            var pageScanMax = new List<double>();
            var pageOpen = new List<double>();
            double worstScan = 0;
            string worstPage = "";
            Dictionary<int, long>? pageTaskHalts = null;
            Dictionary<string, long>? pageTaskNotSim = null;
            // where the halts happened: 'class code reason @ function line' over the sweep, and
            // the first few distinct ones of each page
            var places = new Dictionary<string, long>(StringComparer.Ordinal);
            List<string>? pageSamples = null;
            void Place(string what, string where = "")
            {
                Bump(places, what);
                string s = where.Length > 0 ? what + " (" + where + ")" : what;
                if (
                    pageSamples != null
                    && pageSamples.Count < 8
                    && !pageSamples.Exists(x => x.StartsWith(what, StringComparison.Ordinal))
                )
                    pageSamples.Add(s);
            }
            string Reason(CicodeSim.Vm.CicodeTask t)
            {
                string ns = Animator.NotSimulatedName(t);
                return ns.Length > 0 ? "not simulated " + ns
                    : t.HaltCode != 0 ? "halt " + t.HaltCode.ToString(CultureInfo.InvariantCulture)
                    : "halt (" + (t.HaltReason ?? "") + ")";
            }
            a.EvalHalted += (cls, an, t) =>
                Place(
                    cls + ": " + Reason(t) + " @ " + Animator.HaltPlace(t),
                    (a.Display.OpenWindow(a.Display.WindowOf(t))?.Page?.Name ?? "?")
                        + " AN "
                        + an.ToString(CultureInfo.InvariantCulture)
                );
            // the tag names code read that name no tag (424), with the page that read them first
            var missing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var missingPage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string curPage = "";
            a.World.Tags.NotFoundRead = r =>
            {
                string k =
                    r.Tag
                    + (r.Element.Length > 0 ? "." + r.Element : "")
                    + (r.Item.Length > 0 ? "." + r.Item : "");
                Bump(missing, k);
                if (!missingPage.ContainsKey(k))
                    missingPage[k] = curPage;
            };
            int killedAtClose = 0;
            // a background task's frames are unwound by the time it ends: where a halting error was raised
            var raisedAt = new Dictionary<CicodeSim.Vm.CicodeTask, string>();
            if (a.Vm.ErrorReport == null)
                a.Vm.ErrorReport = (task, code, halting, suppressed) =>
                {
                    if (halting && !task.Foreground && !raisedAt.ContainsKey(task) && raisedAt.Count < 10000)
                        raisedAt[task] = Animator.HaltPlace(task);
                };
            List<string>? pageKilled = null;
            a.Scheduler.TaskEnded += t =>
            {
                // the place a halting error was raised, dropped however the task ends (else dead
                // tasks pile up)
                string? rp = null;
                if (raisedAt.Count > 0 && raisedAt.TryGetValue(t, out rp))
                    raisedAt.Remove(t);
                if (t.State != CicodeSim.Vm.TaskState.Halt)
                    return;
                if (t.HaltReason == "page closed")
                {
                    // a page task still running when the sweep closed its page (not an error)
                    killedAtClose++;
                    if (pageKilled != null)
                        pageKilled.Add(t.Name + " @ " + Animator.HaltPlace(t));
                    return;
                }
                string ns = Animator.NotSimulatedName(t);
                if (ns.Length > 0)
                {
                    Bump(taskNotSim, ns);
                    if (pageTaskNotSim != null)
                        Bump(pageTaskNotSim, ns);
                }
                else
                {
                    Bump(taskHalts, t.HaltCode);
                    if (pageTaskHalts != null)
                        Bump(pageTaskHalts, t.HaltCode);
                }
                string where = rp ?? Animator.HaltPlace(t);
                Place("task " + t.Name + ": " + Reason(t) + " @ " + where);
            };
            var sw = Stopwatch.StartNew();
            foreach (string name in names)
            {
                curPage = name;
                int logs0 = a.Logs.Count;
                long changed = 0;
                Action<PageState, List<ObjectState>> h = (st, ch) =>
                {
                    if (st.Window == 0)
                        changed += ch.Count;
                };
                a.PageChanged += h;
                int err;
                string exc = "";
                int objects = 0;
                var before = SweepTotals.Of(a);
                pageTaskHalts = new Dictionary<int, long>();
                pageTaskNotSim = new Dictionary<string, long>(StringComparer.Ordinal);
                pageSamples = new List<string>();
                pageKilled = new List<string>();
                double openMs = 0,
                    scanMean = 0,
                    scanMax = 0,
                    scanFirst = 0,
                    scanSteady = 0,
                    scanP50 = 0;
                TouchReport? tr = null;
                long timed = 0;
                bool model = false;
                try
                {
                    PageModel? pm = null;
                    if (models != null)
                    {
                        string mp = Path.Combine(models, name + ".json");
                        if (File.Exists(mp))
                        {
                            pm = PageModel.Load(mp);
                            model = true;
                            withModel++;
                        }
                    }
                    long t0 = Stopwatch.GetTimestamp();
                    err = a.Open(name, 0, pm);
                    openMs = Ms(Stopwatch.GetTimestamp() - t0);
                    if (err == 0)
                    {
                        var run0 = a.Run(0);
                        long firstTicks = run0?.ScanTicks ?? 0;
                        a.RunScans(Math.Max(0, scans - 1), 0);
                        var run = a.Run(0);
                        objects = run?.State.Objects.Count ?? 0;
                        if (run != null && run.TimedScans > 0)
                        {
                            timed = run.TimedScans;
                            scanMean = Ms(run.ScanTicks) / run.TimedScans;
                            scanMax = Ms(run.MaxScanTicks);
                            scanFirst = Ms(run == run0 ? firstTicks : 0);
                            scanSteady =
                                run == run0 && run.TimedScans > 1
                                    ? Ms(run.ScanTicks - firstTicks) / (run.TimedScans - 1)
                                    : scanMean;
                            var costs = new List<long>(run.ScanCosts);
                            if (costs.Count > 1)
                                costs.RemoveAt(0);
                            costs.Sort();
                            scanP50 = costs.Count > 0 ? Ms(costs[costs.Count / 2]) : 0;
                            pageScanMean.Add(scanMean);
                            pageScanMax.Add(scanMax);
                            if (scanMax > worstScan)
                            {
                                worstScan = scanMax;
                                worstPage = name;
                            }
                        }
                        if (touch > 0)
                        {
                            long tt0 = Stopwatch.GetTimestamp();
                            tr = TouchAll(a, name, touch, settle, pm);
                            tSeconds += Ms(Stopwatch.GetTimestamp() - tt0) / 1000.0;
                            tClicked += tr.Clicked;
                            tRan += tr.Ran;
                            tPopups += tr.Popups;
                            tNavigated += tr.Navigated;
                            foreach (string s in tr.PopupPages)
                                tPopupPages.Add(s);
                        }
                        pageOpen.Add(openMs);
                        a.Close(0);
                        opened++;
                        states += objects;
                    }
                    else
                    {
                        failed++;
                        Bump(openErrors, err);
                    }
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    err = -1;
                    exc = e.GetType().Name + ": " + e.Message;
                    exceptions++;
                }
                a.PageChanged -= h;
                var mine = SweepTotals.Of(a).Minus(before);
                int engineLogs = 0;
                string firstEngine = "";
                for (int k = logs0; k < a.Logs.Count; k++)
                    if (a.Logs[k].StartsWith("engine exception", StringComparison.Ordinal))
                    {
                        if (engineLogs == 0)
                        {
                            firstEngine = a.Logs[k];
                            int nl = firstEngine.IndexOf('\n');
                            if (nl > 0)
                                firstEngine = firstEngine.Substring(0, nl).TrimEnd('\r');
                        }
                        engineLogs++;
                    }
                exceptions += engineLogs;
                var line = JsonValue.NewObject();
                line.Set("page", name);
                line.Set("error", JsonValue.Of(err));
                line.Set("objects", JsonValue.Of(objects));
                line.Set("changes", JsonValue.Of(changed));
                if (model)
                    line.Set("model", JsonValue.True);
                if (rows)
                {
                    if (rowInfo.TryGetValue(name, out var ri))
                    {
                        var rj = JsonValue.NewObject();
                        rj.Set("project", ri.project);
                        rj.Set("records", JsonValue.Of(ri.records));
                        rj.Set("ms", JsonValue.Of(R2(ri.ms)));
                        rj.Set("notes", JsonValue.Of(ri.notes));
                        if (ri.first.Length > 0)
                            rj.Set("firstNote", ri.first);
                        line.Set("rows", rj);
                    }
                    else
                        line.Set("rows", JsonValue.Null);
                }
                if (tr != null)
                {
                    var tj = JsonValue.NewObject();
                    tj.Set("clicked", JsonValue.Of(tr.Clicked));
                    tj.Set("ran", JsonValue.Of(tr.Ran));
                    tj.Set("popups", JsonValue.Of(tr.Popups));
                    var pp = JsonValue.NewArray();
                    foreach (string s in tr.PopupPages)
                        pp.Add(JsonValue.Of(s));
                    tj.Set("popupPages", pp);
                    tj.Set("navigated", JsonValue.Of(tr.Navigated));
                    var aw = JsonValue.NewArray();
                    foreach (string s in tr.Away)
                        aw.Add(JsonValue.Of(s));
                    tj.Set("away", aw);
                    if (tr.Notes.Count > 0)
                    {
                        var nj = JsonValue.NewArray();
                        foreach (string s in tr.Notes)
                            nj.Add(JsonValue.Of(s));
                        tj.Set("notes", nj);
                    }
                    line.Set("touch", tj);
                }
                line.Set("evaluations", JsonValue.Of(mine.Evaluations));
                line.Set("openMs", JsonValue.Of(R2(openMs)));
                line.Set("scans", JsonValue.Of(timed));
                line.Set("scanMs", JsonValue.Of(R2(scanMean)));
                line.Set("scanMsMax", JsonValue.Of(R2(scanMax)));
                line.Set("scanMsFirst", JsonValue.Of(R2(scanFirst)));
                line.Set("scanMsSteady", JsonValue.Of(R2(scanSteady)));
                line.Set("scanMsP50", JsonValue.Of(R2(scanP50)));
                if (mine.HaltCodes.Count > 0)
                    line.Set("halts", CountsJson(mine.HaltCodes, IntKey));
                if (mine.NotSimulated.Count > 0)
                    line.Set("notSimulated", CountsJson(mine.NotSimulated, x => x));
                if (mine.Errors.Count > 0)
                    line.Set("errors", CountsJson(mine.Errors, IntKey));
                if (pageTaskHalts.Count > 0)
                    line.Set("taskHalts", CountsJson(pageTaskHalts, IntKey));
                if (pageTaskNotSim.Count > 0)
                    line.Set("taskNotSimulated", CountsJson(pageTaskNotSim, x => x));
                if (pageKilled.Count > 0)
                {
                    var kj = JsonValue.NewArray();
                    foreach (string s in pageKilled)
                        kj.Add(JsonValue.Of(s));
                    line.Set("runningAtClose", kj);
                }
                if (pageSamples.Count > 0)
                {
                    var hs = JsonValue.NewArray();
                    foreach (string s in pageSamples)
                        hs.Add(JsonValue.Of(s));
                    line.Set("haltSamples", hs);
                }
                line.Set("managedMB", JsonValue.Of(Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1)));
                if (exc.Length > 0)
                    line.Set("exception", exc);
                if (engineLogs > 0)
                {
                    line.Set("engineExceptions", JsonValue.Of(engineLogs));
                    line.Set("engineException", firstEngine);
                }
                if (json)
                    o.WriteLine(line.Dump());
                else
                    o.WriteLine(
                        $"{name}: error {err}, {objects} objects, {changed} state changes, scan {R2(scanMean).ToString(CultureInfo.InvariantCulture)} ms (max {R2(scanMax).ToString(CultureInfo.InvariantCulture)}){(exc.Length > 0 ? ", " + exc : "")}{(engineLogs > 0 ? ", engine exceptions " + engineLogs : "")}"
                    );
                o.Flush();
            }
            pageTaskHalts = null;
            pageTaskNotSim = null;
            var sum = JsonValue.NewObject();
            sum.Set("pages", JsonValue.Of(names.Count));
            sum.Set("opened", JsonValue.Of(opened));
            sum.Set("failed", JsonValue.Of(failed));
            if (openErrors.Count > 0)
                sum.Set("openErrors", CountsJson(openErrors, IntKey));
            sum.Set("exceptions", JsonValue.Of(exceptions));
            if (models != null)
                sum.Set("withModel", JsonValue.Of(withModel));
            sum.Set("objectStates", JsonValue.Of(states));
            sum.Set("loadSeconds", JsonValue.Of(R2(loadSeconds)));
            sum.Set("seconds", JsonValue.Of(R2(sw.Elapsed.TotalSeconds)));
            var all = SweepTotals.Of(a);
            sum.Set("evaluations", JsonValue.Of(all.Evaluations));
            sum.Set("halted", JsonValue.Of(all.Halted));
            sum.Set("halts", CountsJson(all.HaltCodes, IntKey));
            long nsTotal = 0;
            foreach (var v in all.NotSimulated.Values)
                nsTotal += v;
            sum.Set("notSimulatedTotal", JsonValue.Of(nsTotal));
            sum.Set("notSimulated", CountsJson(all.NotSimulated, x => x));
            sum.Set("taskHalts", CountsJson(taskHalts, IntKey));
            sum.Set("taskNotSimulated", CountsJson(taskNotSim, x => x));
            sum.Set("tasksRunningAtClose", JsonValue.Of(killedAtClose));
            if (touch > 0)
            {
                var tj = JsonValue.NewObject();
                tj.Set("clicked", JsonValue.Of(tClicked));
                tj.Set("ran", JsonValue.Of(tRan));
                tj.Set("popups", JsonValue.Of(tPopups));
                tj.Set("popupPages", JsonValue.Of(tPopupPages.Count));
                tj.Set("navigated", JsonValue.Of(tNavigated));
                tj.Set("seconds", JsonValue.Of(R2(tSeconds)));
                sum.Set("touch", tj);
            }
            var top = new Dictionary<string, long>(StringComparer.Ordinal);
            var pl = new List<KeyValuePair<string, long>>(places);
            pl.Sort(
                (x, y) =>
                    y.Value != x.Value ? y.Value.CompareTo(x.Value) : string.CompareOrdinal(x.Key, y.Key)
            );
            for (int k = 0; k < pl.Count && k < 60; k++)
                top[pl[k].Key] = pl[k].Value;
            sum.Set("haltPlaces", CountsJson(top, x => x));
            sum.Set("haltPlacesDistinct", JsonValue.Of(places.Count));
            var ml = new List<KeyValuePair<string, long>>(missing);
            ml.Sort(
                (x, y) =>
                    y.Value != x.Value ? y.Value.CompareTo(x.Value) : string.CompareOrdinal(x.Key, y.Key)
            );
            var mj = JsonValue.NewObject();
            for (int k = 0; k < ml.Count && k < 60; k++)
                mj.Set(
                    ml[k].Key,
                    JsonValue.Of(
                        missingPage[ml[k].Key] + " x" + ml[k].Value.ToString(CultureInfo.InvariantCulture)
                    )
                );
            sum.Set("missingTags", mj);
            sum.Set("missingTagsDistinct", JsonValue.Of(missing.Count));
            if (missingOut != null)
                using (
                    var mw = new StreamWriter(missingOut, false, new UTF8Encoding(false)) { NewLine = "\n" }
                )
                    foreach (var kv in ml)
                        mw.WriteLine(
                            kv.Key
                                + "\t"
                                + kv.Value.ToString(CultureInfo.InvariantCulture)
                                + "\t"
                                + missingPage[kv.Key]
                        );
            var sm = JsonValue.NewObject();
            var means = new List<double>(pageScanMean);
            means.Sort();
            var maxes = new List<double>(pageScanMax);
            maxes.Sort();
            double total = 0;
            foreach (var v in means)
                total += v;
            sm.Set("pages", JsonValue.Of(means.Count));
            sm.Set("mean", JsonValue.Of(R2(means.Count > 0 ? total / means.Count : 0)));
            sm.Set("p50", JsonValue.Of(R2(Percentile(means, 0.5))));
            sm.Set("p95", JsonValue.Of(R2(Percentile(means, 0.95))));
            sm.Set("maxMean", JsonValue.Of(R2(means.Count > 0 ? means[means.Count - 1] : 0)));
            sm.Set("max", JsonValue.Of(R2(worstScan)));
            sm.Set("maxPage", worstPage);
            int over100 = 0,
                over250 = 0;
            foreach (var v in maxes)
            {
                if (v > 100)
                    over100++;
                if (v > 250)
                    over250++;
            }
            sm.Set("pagesMaxOver100", JsonValue.Of(over100));
            sm.Set("pagesMaxOver250", JsonValue.Of(over250));
            sum.Set("scanMs", sm);
            var om = JsonValue.NewObject();
            pageOpen.Sort();
            double ot = 0;
            foreach (var v in pageOpen)
                ot += v;
            om.Set("mean", JsonValue.Of(R2(pageOpen.Count > 0 ? ot / pageOpen.Count : 0)));
            om.Set("p95", JsonValue.Of(R2(Percentile(pageOpen, 0.95))));
            om.Set("max", JsonValue.Of(R2(pageOpen.Count > 0 ? pageOpen[pageOpen.Count - 1] : 0)));
            sum.Set("openMs", om);
            using (var proc = Process.GetCurrentProcess())
            {
                proc.Refresh();
                sum.Set("workingSetMB", JsonValue.Of(Math.Round(proc.WorkingSet64 / 1048576.0, 1)));
                sum.Set("peakWorkingSetMB", JsonValue.Of(Math.Round(proc.PeakWorkingSet64 / 1048576.0, 1)));
                sum.Set("privateMB", JsonValue.Of(Math.Round(proc.PrivateMemorySize64 / 1048576.0, 1)));
            }
            sum.Set("managedMB", JsonValue.Of(Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1)));
            var cls = JsonValue.NewObject();
            var keys = new List<string>(a.Stats.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys)
            {
                var s = a.Stats[k];
                var c = JsonValue.NewObject();
                c.Set("objects", JsonValue.Of(s.Objects));
                c.Set("evaluations", JsonValue.Of(s.Evaluations));
                long ns = 0;
                foreach (var v in s.NotSimulated.Values)
                    ns += v;
                c.Set("notSimulated", JsonValue.Of(ns));
                c.Set("byFunction", CountsJson(s.NotSimulated, x => x));
                c.Set("halted", JsonValue.Of(s.Halted));
                c.Set("haltCodes", CountsJson(s.HaltCodes, IntKey));
                var hr = JsonValue.NewObject();
                foreach (var kv in s.HaltReasons)
                    hr.Set(kv.Key, JsonValue.Of(kv.Value));
                c.Set("haltReasons", hr);
                var errs = JsonValue.NewObject();
                var ek = new List<int>(s.Errors.Keys);
                ek.Sort();
                foreach (int e in ek)
                    errs.Set(e.ToString(CultureInfo.InvariantCulture), JsonValue.Of(s.Errors[e]));
                c.Set("errors", errs);
                cls.Set(k, c);
            }
            sum.Set("classes", cls);
            o.WriteLine(json ? sum.Dump() : sum.DumpSpaced());
            o.Flush();
            if (outPath != null)
                o.Dispose();
            return exceptions > 0 ? 1 : 0;
        }
    }
}
