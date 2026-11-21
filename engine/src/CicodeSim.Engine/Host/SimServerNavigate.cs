using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using CicodeSim.Project;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;
using Timer = System.Threading.Timer;

namespace CicodeSim.Host
{
    /// <summary>ctsim/navigate: show a page the way the project's own navigation would.</summary>
    public sealed partial class SimServer
    {
        /// <summary>How long ctsim/navigate waits for the SA workspace to initialise (real ms).</summary>
        public const int NavigateWaitMs = 60000;

        /// <summary>How often it asks Workspace_IsSystemInitialised while it waits (real ms).</summary>
        public const int NavigatePollMs = 200;

        /// <summary>How often it looks at the startup code while it waits for it (real ms).</summary>
        public const int StartupPollMs = 50;

        private readonly HashSet<Timer> _navTimers = new HashSet<Timer>();

        private void Navigate(Client c, JsonValue req, JsonValue a)
        {
            string page = CitectText.Trim(Str(a, "page"));
            if (page.Length == 0)
                throw new ArgumentException("navigate needs a page");
            if (page.IndexOf('"') >= 0 || page.IndexOf('^') >= 0)
                throw new ArgumentException("navigate: a page name cannot hold '\"' or '^'");
            int window = Window(a) ?? 0;
            long timeout = Int(a, "timeoutMs", NavigateWaitMs);
            string how = Str(a, "how", "auto");
            if (how != "auto" && how != "workspace" && how != "pageDisplay")
                throw new ArgumentException("navigate how must be auto, workspace or pageDisplay");
            // the page shows once the startup code asked for has settled, as
            // the runtime's user reaches a page after the start (RuntimeStartup.SettleState)
            bool afterStartup = Bool(a, "afterStartup", true);
            JsonValue? startup = null;
            var notes = new List<string>();
            string source = "";
            // the viewer's drawing (and, for a page that is not current, its rows): what the
            // page runs when the navigation's Cicode displays it
            var mj = a.Get("model");
            if (mj != null && !mj.IsNull)
            {
                if (mj.Kind != JsonKind.Object)
                    throw new ArgumentException("model must be a ctsim-page/0 object");
                var model = Page.PageModel.FromJson(mj);
                string? rdb = Simulation.Invoke(() => Simulation.Pages.RdbOf(page));
                Page.CompiledPage? cp = null;
                if (
                    model.Tables.Count > 0
                    && (rdb == null || !ModelCurrent(mj) || !RdbMatches(rdb, model, notes))
                )
                    cp = Simulation.Pages.CompileRows(page, model, notes);
                foreach (string n in model.Notes)
                    notes.Add("model: " + n);
                var keep = cp;
                Simulation.Invoke(() =>
                {
                    Simulation.Pages.Prime(page, model, keep);
                    return 0;
                });
                source =
                    cp != null ? "rows"
                    : rdb != null ? "rdb"
                    : "projectRows";
            }
            var sink = c.Sink;
            var sw = Stopwatch.StartNew();
            int answered = 0;
            string q = "\"" + page + "\"";

            JsonValue Body(string method)
            {
                var b = JsonValue.NewObject();
                b.Set("page", page);
                b.Set("method", method);
                if (source.Length > 0)
                    b.Set("source", source);
                if (startup != null)
                    b.Set("startup", startup);
                var arr = JsonValue.NewArray();
                for (int i = 0; i < notes.Count && i < 50; i++)
                    arr.Add(JsonValue.Of(notes[i]));
                b.Set("notes", arr);
                return b;
            }

            void Answer(JsonValue b, string? fail)
            {
                if (Interlocked.Exchange(ref answered, 1) != 0)
                    return;
                if (fail != null)
                    Fail(sink, req, fail, b);
                else
                    Ok(sink, req, b);
            }

            void ShownIn(JsonValue b)
            {
                var w = JsonValue.NewArray();
                foreach (int n in Simulation.Pages.WindowsShowing(page))
                    w.Add(JsonValue.Of(n));
                b.Set("shownIn", w);
            }

            // (every step below runs on the simulation thread)
            void PageDisplay(string why)
            {
                if (why.Length > 0)
                    notes.Add(why);
                var o = Simulation.Pages.ShowAsCicode(page, window);
                var b = Body("pageDisplay");
                foreach (var kv in o.Pairs)
                    b.Set(kv.Key, kv.Value);
                ShownIn(b);
                long err = o["error"].Long;
                Answer(
                    b,
                    err != 0
                        ? "navigate "
                            + page
                            + ": error "
                            + err.ToString(CultureInfo.InvariantCulture)
                            + " "
                            + (o.Get("errorText")?.Str ?? "")
                        : null
                );
            }

            CicodeTask? Start(string text, Action<CallOutcome> done)
            {
                try
                {
                    var t = Simulation.StartCall(text, done, noDebug: true, taskName: "navigate");
                    // the workspace is the window's (Workspace_GetWorkspaceFromName("") reads WinNumber())
                    TaskContext.Set(t, window, -1);
                    return t;
                }
                catch (CallTextException e)
                {
                    notes.Add(text + " did not start: " + e.Message);
                    return null;
                }
            }

            void Show()
            {
                var t = Start("Workspace_ShowContent(" + q + ")", o =>
                {
                    var b = Body("workspace");
                    b.Set("task", JsonValue.Of(o.Task));
                    int shown = int.TryParse(
                        o.Result,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int r
                    )
                        ? r
                        : -1;
                    b.Set("window", JsonValue.Of(shown));
                    ShownIn(b);
                    if (o.State == "halt")
                    {
                        b.Set("call", o.ToJson());
                        Answer(
                            b,
                            "navigate "
                                + page
                                + ": Workspace_ShowContent halted: error "
                                + o.Error.ToString(CultureInfo.InvariantCulture)
                                + " "
                                + o.ErrorText
                        );
                        return;
                    }
                    if (shown < 0)
                        notes.Add(
                            "Workspace_ShowContent returned "
                                + o.Result
                                + ": the workspace did not show "
                                + page
                                + (
                                    b["shownIn"].Count == 0
                                        ? " (no pane of the workspace takes its content type?)"
                                        : " (it shows already)"
                                )
                        );
                    b.Set("notes", Body("workspace")["notes"]);
                    Answer(b, null);
                });
                if (t == null)
                    PageDisplay("");
            }

            void Retry(int pollMs = NavigatePollMs)
            {
                Timer? timer = null;
                timer = new Timer(
                    _ =>
                    {
                        lock (_navTimers)
                            if (timer != null)
                                _navTimers.Remove(timer);
                        timer?.Dispose();
                        try
                        {
                            Simulation.Post(Try);
                        }
                        catch (ObjectDisposedException)
                        {
                            Answer(Body("none"), "the simulation has ended");
                        }
                    },
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite
                );
                lock (_navTimers)
                    _navTimers.Add(timer);
                timer.Change(pollMs, Timeout.Infinite);
            }

            void Try()
            {
                if (startup == null)
                {
                    var boot = Simulation.Boot;
                    string state = afterStartup ? boot.SettleState() : "notWaited";
                    if ((state == "waiting" || state == "running") && sw.ElapsedMilliseconds < timeout)
                    {
                        Retry(StartupPollMs);
                        return;
                    }
                    var st = JsonValue.NewObject();
                    if (state == "waiting" || state == "running")
                    {
                        notes.Add(
                            "the startup code had not settled in "
                                + timeout.ToString(CultureInfo.InvariantCulture)
                                + " ms ("
                                + state
                                + "): the page shows now"
                        );
                        state = "timedOut";
                    }
                    st.Set("state", state);
                    st.Set("waitedMs", JsonValue.Of(sw.ElapsedMilliseconds));
                    st.Set("simulatedMs", JsonValue.Of(boot.SinceRunMs));
                    var running = boot.RunningJson();
                    if (running.Count > 0)
                        st.Set("running", running);
                    startup = st;
                }
                if (how == "pageDisplay")
                {
                    PageDisplay("");
                    return;
                }
                var fn = Simulation.Library.Function("Workspace_ShowContent");
                var init = Simulation.Library.Function("Workspace_IsSystemInitialised");
                if (fn == null || fn.Private || init == null || init.Private)
                {
                    if (how == "workspace")
                    {
                        Answer(
                            Body("none"),
                            "navigate "
                                + page
                                + ": the project has no SA workspace (no Workspace_ShowContent)"
                        );
                        return;
                    }
                    PageDisplay(
                        "the project has no SA workspace (no Workspace_ShowContent): PageDisplay in window "
                            + window.ToString(CultureInfo.InvariantCulture)
                    );
                    return;
                }
                // the workspace is ready once its panes exist (Workspace_IsSystemInitialised) and
                // the navigation has shown each workspace's home page (Navigation_IsInitialised:
                // SA_Include Navigation.ci _Navigation_OnEquipmentLoaded, a callback after the
                // workspaces initialised): a page shown before that is replaced by the home page
                var nav = Simulation.Library.Function("Navigation_IsInitialised");
                bool askNav = nav != null && !nav.Private;
                Ask("Workspace_IsSystemInitialised()", () =>
                {
                    if (askNav)
                        Ask("Navigation_IsInitialised()", Settled);
                    else
                        Settled();
                });
            }

            // the home page the navigation shows runs as a menu command task (Navigation.ci
            // _Navigation_RunCommandFromMenuHandle -> MenuNodeRunCommand), and a page it displays
            // may wait for its drawing (ctsim/modelWanted): both are done first, else the home
            // page would replace the page shown now
            void Settled()
            {
                if (Busy())
                    Wait();
                else
                    Show();
            }

            bool Busy()
            {
                foreach (var t in Simulation.Scheduler.All)
                    if (!t.Done && t.Data.ContainsKey("menunode"))
                        return true;
                var an = Simulation.Pages.Animator;
                if (an != null)
                    foreach (var w in Simulation.World.Display.Windows)
                        if (w.Open && an.Run(w.Number)?.PendingStart != null)
                            return true;
                return false;
            }

            // run a readiness function: its answer 1 = then, else wait and try again
            void Ask(string fnCall, Action then)
            {
                var t = Start(fnCall, o =>
                {
                    bool ready =
                        int.TryParse(o.Result, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)
                        && r != 0;
                    if (ready)
                        then();
                    else
                        Wait();
                });
                if (t == null)
                    PageDisplay("");
            }

            void Wait()
            {
                bool waiting = Simulation.Boot.Pending || Simulation.Boot.Ran;
                if (waiting && sw.ElapsedMilliseconds < timeout)
                {
                    Retry();
                    return;
                }
                string why = waiting
                    ? "the SA workspace did not initialise in "
                        + timeout.ToString(CultureInfo.InvariantCulture)
                        + " ms"
                    : "the SA workspace is not initialised and the startup code was not asked for (load {startup: \"auto\"})";
                if (how == "workspace")
                {
                    Answer(Body("none"), "navigate " + page + ": " + why);
                    return;
                }
                PageDisplay(why + ": PageDisplay in window " + window.ToString(CultureInfo.InvariantCulture));
            }

            Simulation.Post(Try);
        }
    }
}
