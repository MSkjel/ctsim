using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Builtins;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Sim
{
    /// <summary>Start like the runtime: run the startup code, then show the startup page.</summary>
    public sealed class RuntimeStartup
    {
        private readonly Simulation _sim;
        private bool _asked,
            _ran;
        private readonly List<(string Function, int Task)> _tasks = new List<(string, int)>();
        private readonly List<string> _notes = new List<string>();

        internal RuntimeStartup(Simulation sim)
        {
            _sim = sim;
        }

        /// <summary>True once it was asked for and has not run (waiting for window 0).</summary>
        public bool Pending => _asked && !_ran;
        public bool Ran => _ran;

        /// <summary>It was asked for (load {startup: "auto"}, serve --startup auto).</summary>
        public bool Asked => _asked;

        // ------------------------------------------------------------------ settling
        /// <summary>Simulated time the startup gets before a navigated page shows.</summary>
        public const long SettleMinMs = 1000;

        /// <summary>A startup task that does not end (a StartupCode that loops) is waited for
        /// this long, simulated, at most.</summary>
        public const long SettleCapMs = 10000;

        /// <summary>How much faster the simulated clock runs during startup.</summary>
        public const double FastFactor = 20.0;

        /// <summary>Run the startup code fast (FastFactor).</summary>
        public bool Fast = true;
        private bool _boosting;

        /// <summary>The clock runs at FastFactor for the startup code now.</summary>
        public bool Boosting => _boosting;

        /// <summary>(simulation thread, every loop while Boosting) The start-up boost ends
        /// once the startup code has settled (or is capped, or the clock is paused).</summary>
        public void Tick()
        {
            if (!_boosting)
                return;
            string s = SettleState();
            // the boost only while the startup code runs: a page start held for its drawing
            // (window 0 shows no running page: 'waiting') waits in real time, and a fast clock
            // there would skip simulated time for nothing
            if (s == "running" || s == "waiting")
            {
                double want = s == "running" ? FastFactor : 1.0;
                if (_sim.World.Clock.Boost != want)
                    _sim.World.Clock.SetBoost(want);
                return;
            }
            _boosting = false;
            _sim.World.Clock.SetBoost(1.0);
            _sim.Note(
                "info",
                "startup: "
                    + s
                    + " after "
                    + SinceRunMs.ToString(CultureInfo.InvariantCulture)
                    + " ms simulated: the clock runs at its own speed again"
            );
        }

        private long _ranAtMs;
        private readonly List<CicodeSim.Vm.CicodeTask> _started = new List<CicodeSim.Vm.CicodeTask>();

        /// <summary>Where the startup is: 'notAsked', 'waiting', 'running' or 'settled'.</summary>
        public string SettleState()
        {
            if (!_asked)
                return "notAsked";
            if (!_ran || !WindowZeroShows())
                return "waiting";
            long since = SinceRunMs;
            var watched = Watched();
            bool ended = watched.TrueForAll(t => t.Done);
            if (ended && since >= SettleMinMs)
                return "settled";
            if (since >= SettleCapMs)
                return "capped";
            if (
                _sim.World.Clock.Mode != CicodeSim.World.SimClock.Run
                && watched.TrueForAll(t =>
                    t.Done
                    || t.State == CicodeSim.Vm.TaskState.Sleep
                    || t.State == CicodeSim.Vm.TaskState.Wait
                )
            )
                return "paused";
            return "running";
        }

        /// <summary>The tasks the settling waits for.</summary>
        private List<CicodeSim.Vm.CicodeTask> Watched()
        {
            var o = new List<CicodeSim.Vm.CicodeTask>(_started);
            var run = _sim.Pages.Animator?.Run(0);
            if (run?.EntryTask != null)
                o.Add(run.EntryTask);
            if (run?.ReadyTask != null)
                o.Add(run.ReadyTask);
            return o;
        }

        /// <summary>A page the user goes to may show now: the startup was not asked for, or it
        /// is settled, capped or paused (SettleState).</summary>
        public bool Settled
        {
            get
            {
                string s = SettleState();
                return s != "waiting" && s != "running";
            }
        }

        /// <summary>The simulated ms since the startup tasks started (0 before).</summary>
        public long SinceRunMs => _ran ? Math.Max(0, _sim.World.Clock.NowMs - _ranAtMs) : 0;

        /// <summary>The startup tasks and window 0's page event tasks that have not ended:
        /// [{function, task, state}].</summary>
        public JsonValue RunningJson()
        {
            var a = JsonValue.NewArray();
            foreach (var t in _ran ? Watched() : _started)
                if (!t.Done)
                {
                    var o = JsonValue.NewObject();
                    o.Set("function", t.Func?.Name ?? t.Name);
                    o.Set("task", JsonValue.Of(t.Handle));
                    o.Set("state", t.StateName);
                    a.Add(o);
                }
            return a;
        }

        /// <summary>One role's startup code and where it came from.</summary>
        public sealed class Step
        {
            public string Role = "",
                Code = "",
                Source = "";
        }

        /// <summary>Ask for the startup (on the simulation thread): runs at once when window 0
        /// shows a page, else when it first does. The body: {startupTasks: [{function, task}],
        /// notes, pending?}.</summary>
        public JsonValue Request()
        {
            var b = JsonValue.NewObject();
            if (_ran)
            {
                b.Set("startupTasks", TasksJson());
                var n = NotesJson();
                n.Add(JsonValue.Of("the startup code ran already (it runs once a simulation)"));
                b.Set("notes", n);
                return b;
            }
            _asked = true;
            if (WindowZeroShows())
                Run();
            b.Set("startupTasks", TasksJson());
            var notes = NotesJson();
            if (!_ran)
            {
                b.Set("pending", JsonValue.Of(true));
                notes.Add(
                    JsonValue.Of(
                        "waiting for window 0 to show a page: the startup code runs after the first openPage (its tasks are logged then)"
                    )
                );
            }
            b.Set("notes", notes);
            return b;
        }

        /// <summary>A page was shown in a window (simulation thread): runs a waiting startup
        /// when window 0 shows one.</summary>
        internal void PageShown(int window)
        {
            if (window == 0 && Pending && WindowZeroShows())
                Run();
        }

        /// <summary>Window 0 shows a page that has started (a page held for its drawing,
        /// ctsim/modelWanted, has not: its panes are not there yet, and the SA workspace reads
        /// them when it initialises).</summary>
        private bool WindowZeroShows()
        {
            var ws = _sim.World.Display.Windows;
            if (!(ws.Length > 0 && ws[0].Open && ws[0].Page != null))
                return false;
            var run = _sim.Pages.Animator?.Run(0);
            return run == null || run.PendingStart == null;
        }

        // ------------------------------------------------------------------ the startup page
        /// <summary>
        /// The page the runtime shows in window 0 at startup, and where the name came from.
        /// </summary>
        public (string Page, string Source)? StartupPage(List<string> notes)
        {
            var cands = new List<(string Page, string Source)>();
            foreach (var (sec, key) in new[] { ("MultiMonitors", "StartupPageScreen1"), ("Page", "Startup") })
            foreach (var c in Values(sec, key))
                if (!cands.Exists(x => string.Equals(x.Page, c.Value, StringComparison.OrdinalIgnoreCase)))
                    cands.Add((c.Value, "[" + sec + "] " + key + " (" + c.Source + ")"));
            if (!cands.Exists(x => string.Equals(x.Page, "Startup", StringComparison.OrdinalIgnoreCase)))
                cands.Add(("Startup", "[Page] Startup's default"));
            bool multi = cands.Exists(x => x.Source.StartsWith("[MultiMonitors]", StringComparison.Ordinal));
            foreach (var c in cands)
            {
                if (_sim.Pages.PageExists(c.Page))
                    return c;
                notes.Add(
                    "startup page "
                        + CitectText.Quote(c.Page)
                        + " from "
                        + c.Source
                        + " is not a page of the project"
                );
            }
            // the runtime's fallback when the startup page does not show (its log
            // line "Startup: Couldn't show startup page '%s', attempting to show
            // fallback page"): '!CSV_Splash' for the single startup page, then Include's '!Startup'
            foreach (string f in multi ? new[] { "!Startup" } : new[] { "!CSV_Splash", "!Startup" })
                if (_sim.Pages.PageExists(f))
                    return (f, "the runtime's fallback page");
            return null;
        }

        /// <summary>The values a parameter has, in the order the startup code's are tried: the
        /// project folder's citect.ini when it has the key (also empty: then no other), else
        /// ParameterGet's; then the project's parameter record. Empty values are left out.</summary>
        private List<(string Value, string Source)> Values(string section, string key)
        {
            var o = new List<(string, string)>();
            void Add(string? v, string src)
            {
                v = CitectText.Trim(v ?? "");
                if (
                    v.Length > 0
                    && !o.Exists(c => string.Equals(c.Item1, v, StringComparison.OrdinalIgnoreCase))
                )
                    o.Add((v, src));
            }
            const string Absent = "\u0001";
            string own = WinPath.Join(_sim.ProjectDir, "citect.ini");
            string ownValue = WinPath.IsFile(own) ? CitectIni.IniValueIn(own, section, key, Absent) : Absent;
            if (ownValue != Absent)
                Add(ownValue, "the project folder's citect.ini");
            else
                Add(Params.Get(_sim.Host, section, key), "ParameterGet");
            Add(Params.ParamRecord(ProcessData.Of(_sim.Host), section, key), "the project's parameters");
            return o;
        }

        /// <summary>Show the startup page in window 0, as the runtime does.</summary>
        public JsonValue ShowStartupPage()
        {
            var b = JsonValue.NewObject();
            var notes = new List<string>();
            var ws = _sim.World.Display.Windows;
            if (ws.Length > 0 && ws[0].Open && ws[0].Page != null)
            {
                b.Set("page", ws[0].Page!.Name);
                b.Set("window", JsonValue.Of(0));
                b.Set("error", JsonValue.Of(0));
                b.Set("already", JsonValue.True);
                notes.Add("window 0 shows " + ws[0].Page!.Name + " already: the startup page is not shown");
            }
            else
            {
                var sp = StartupPage(notes);
                if (sp == null)
                {
                    b.Set("page", "");
                    b.Set("window", JsonValue.Of(0));
                    b.Set("error", JsonValue.Of(261));
                    b.Set("errorText", "the project has no startup page");
                }
                else
                {
                    var o = _sim.Pages.ShowAsCicode(sp.Value.Page, 0);
                    foreach (var kv in o.Pairs)
                        b.Set(kv.Key, kv.Value);
                    b.Set("source", sp.Value.Source);
                    _sim.Note(
                        "info",
                        "startup page: " + sp.Value.Page + " in window 0 (from " + sp.Value.Source + ")"
                    );
                }
            }
            var a = JsonValue.NewArray();
            foreach (string n in notes)
                a.Add(JsonValue.Of(n));
            b.Set("notes", a);
            return b;
        }

        private JsonValue TasksJson()
        {
            var a = JsonValue.NewArray();
            foreach (var (fn, task) in _tasks)
            {
                var o = JsonValue.NewObject();
                o.Set("function", fn);
                o.Set("task", JsonValue.Of(task));
                a.Add(o);
            }
            return a;
        }

        private JsonValue NotesJson()
        {
            var a = JsonValue.NewArray();
            foreach (string n in _notes)
                a.Add(JsonValue.Of(n));
            return a;
        }

        /// <summary>What would run, in order (no task started).</summary>
        public List<Step> Plan()
        {
            var o = new List<Step>();
            var lib = _sim.Library;
            var begin = lib.Function("_CitectBegin");
            if (begin != null && !begin.Private)
                o.Add(
                    new Step
                    {
                        Role = "Include",
                        Code = "_CitectBegin()",
                        Source = "library",
                    }
                );
            var roles = new List<string> { "Client" };
            var clusters = Servers.Clusters(_sim.Host);
            foreach (var s in Servers.All(_sim.Host))
            {
                // a server row without a cluster is in the project's only cluster
                string cl =
                    s.Cluster.Length > 0 ? s.Cluster
                    : clusters.Count == 1 ? clusters[0]
                    : "";
                string sec = s.Type + "." + cl + "." + s.Name;
                if (
                    s.Type.Length > 0
                    && s.Name.Length > 0
                    && !roles.Exists(r => string.Equals(r, sec, StringComparison.OrdinalIgnoreCase))
                )
                    roles.Add(sec);
            }
            foreach (string role in roles)
            {
                var st = Pick(role);
                if (st != null)
                    o.Add(st);
            }
            return o;
        }

        /// <summary>The role's StartupCode: the project folder's citect.ini when it has the key,
        /// else ParameterGet's value; then (when that names no function of the project) the
        /// compiled parameter record.</summary>
        private Step? Pick(string section)
        {
            var cands = new List<(string Code, string Source)>();
            void Add(string? v, string src)
            {
                v = CitectText.Trim(v ?? "");
                if (
                    v.Length > 0
                    && !cands.Exists(c => string.Equals(c.Code, v, StringComparison.OrdinalIgnoreCase))
                )
                    cands.Add((v, src));
            }
            var d = ProcessData.Of(_sim.Host);
            const string Absent = "\u0001";
            string own = WinPath.Join(_sim.ProjectDir, "citect.ini");
            string ownValue = WinPath.IsFile(own)
                ? CitectIni.IniValueIn(own, section, "StartupCode", Absent)
                : Absent;
            if (ownValue != Absent)
                Add(ownValue, "the project folder's citect.ini");
            else
                Add(Params.Get(_sim.Host, section, "StartupCode"), "ParameterGet");
            Add(Params.ParamRecord(d, section, "StartupCode"), "the project's parameters");
            if (cands.Count == 0)
                return null;
            for (int i = 0; i < cands.Count; i++)
                if (Callable(cands[i].Code))
                {
                    var (code, src) = cands[i];
                    if (i > 0)
                        _notesPlan.Add(
                            "["
                                + section
                                + "] StartupCode "
                                + CitectText.Quote(cands[0].Code)
                                + " ("
                                + cands[0].Source
                                + ") names no function of the project: "
                                + CitectText.Quote(code)
                                + " from "
                                + src
                                + " runs instead"
                        );
                    return new Step
                    {
                        Role = section,
                        Code = code,
                        Source = src,
                    };
                }
            return new Step
            {
                Role = section,
                Code = cands[0].Code,
                Source = cands[0].Source,
            };
        }

        private readonly List<string> _notesPlan = new List<string>();

        /// <summary>A call text whose function the program or the built-ins have.</summary>
        private bool Callable(string code)
        {
            string text = code.IndexOf('(') >= 0 ? code : code + "()";
            try
            {
                Simulation.SplitCall(text, out string name, out _);
                var fn = _sim.Library.Function(name);
                if (fn != null)
                    return !fn.Private;
                return _sim.Vm.Table.Lookup(name) != null;
            }
            catch (CallTextException)
            {
                return false;
            }
        }

        private void Run()
        {
            _ran = true;
            _ranAtMs = _sim.World.Clock.NowMs;
            if (Fast && !_boosting)
            {
                _boosting = true;
                _sim.World.Clock.SetBoost(FastFactor);
                _sim.Note(
                    "info",
                    "startup: the clock runs "
                        + FastFactor.ToString(CultureInfo.InvariantCulture)
                        + " times faster until the startup code has settled"
                );
            }
            _notesPlan.Clear();
            var plan = Plan();
            _notes.AddRange(_notesPlan);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in plan)
            {
                string text = st.Code.IndexOf('(') >= 0 ? st.Code : st.Code + "()";
                string key = text.Replace(" ", "");
                if (!seen.Add(key))
                {
                    _notes.Add(
                        "["
                            + st.Role
                            + "] StartupCode "
                            + CitectText.Quote(st.Code)
                            + " runs once (an earlier role runs it)"
                    );
                    continue;
                }
                try
                {
                    var t = _sim.StartCall(text, null);
                    string fn = t.Func?.Name ?? t.Name;
                    _tasks.Add((fn, t.Handle));
                    _started.Add(t);
                    _sim.Note(
                        "info",
                        "startup: ["
                            + st.Role
                            + "] "
                            + text
                            + " runs as task "
                            + t.Handle.ToString(CultureInfo.InvariantCulture)
                            + (
                                st.Source == "library" || st.Source == "ParameterGet"
                                    ? ""
                                    : " (from " + st.Source + ")"
                            )
                    );
                }
                catch (CallTextException e)
                {
                    string why =
                        "startup: ["
                        + st.Role
                        + "] "
                        + CitectText.Quote(st.Code)
                        + " did not start: "
                        + e.Message;
                    _notes.Add(why);
                    _sim.Note("error", why);
                }
            }
            if (plan.Count == 0)
                _notes.Add("the project has no startup code (no _CitectBegin, no StartupCode)");
        }
    }
}
