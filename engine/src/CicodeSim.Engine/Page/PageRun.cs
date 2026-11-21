using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Page
{
    /// <summary>One page shown in one window.</summary>
    public sealed partial class PageRun
    {
        public readonly Animator Animator;
        public readonly Window Window;
        public readonly PageInstance Page;

        /// <summary>The page's start (open events, entry code, first scan) while it waits for its
        /// drawing (Animator.WantDrawing / Release), null once started.</summary>
        public Action? PendingStart;
        public readonly CompiledPage Compiled;
        public readonly List<AnimObject> Objects = new List<AnimObject>();
        private readonly Dictionary<int, AnimObject> _byAn = new Dictionary<int, AnimObject>();

        /// <summary>The objects parents first (the evaluation order: the runtime's is its hash
        /// table's bucket order; only Cicode side effects can see the difference, guess).</summary>
        private readonly List<AnimObject> _order = new List<AnimObject>();
        public readonly List<CicodeObject> CicodeObjects = new List<CicodeObject>();
        public readonly List<TrendObject> Trends = new List<TrendObject>();
        public readonly Dictionary<int, List<TouchCommand>> Touches =
            new Dictionary<int, List<TouchCommand>>();
        public readonly List<KeyCommand> Keys = new List<KeyCommand>();

        /// <summary>Page-bound tasks (mode bit 1: killed when the page changes).</summary>
        public readonly List<CicodeTask> Tasks = new List<CicodeTask>();
        public CicodeTask? EntryTask,
            ReadyTask;
        public long Scans;
        public bool FirstScanDone;
        public World.Timer? ScanTimer;
        public double NextDue;
        public readonly PageState State;
        public bool Closed;

        /// <summary>Wall-clock cost of the scans (Stopwatch ticks, Animator.DoScan): their sum,
        /// the longest and how many; a measurement only (ctsim-dev page-sweep), no behaviour.</summary>
        public long ScanTicks,
            MaxScanTicks,
            TimedScans;

        /// <summary>The cost of each of the first 4,096 scans (Stopwatch ticks), for medians.</summary>
        public readonly List<long> ScanCosts = new List<long>();

        private byte[]? _bgnd,
            _entry,
            _exit,
            _ready,
            _act,
            _deact;

        internal PageRun(Animator a, Window w, PageInstance page, CompiledPage compiled)
        {
            Animator = a;
            Window = w;
            Page = page;
            Compiled = compiled;
            // the serial goes on from the window's last one (a page shown again in the window,
            // a reload: a viewer tells this display's states from the last one's by serial)
            State = new PageState
            {
                Window = w.Number,
                Page = page.Name,
                Drawn = w.Drawn,
                Serial = w.PageSerial,
            };
            _drawnSent = w.DrawnSerial;
            Build();
        }

        public AnimObject? Object(int an) => _byAn.TryGetValue(an, out var o) ? o : null;

        private DisplayConfig Cfg => Animator.Display.Config;

        // ================================================================== building
        private AnimObject Ensure(int an, string table, int type)
        {
            if (!_byAn.TryGetValue(an, out var o))
            {
                o = new AnimObject(an) { Table = table, Type = type };
                o.Drawing = Page.Drawing?.Get(an);
                if (o.Type == 0 && o.Drawing != null)
                    o.Type = o.Drawing.Type;
                _byAn[an] = o;
                Objects.Add(o);
            }
            return o;
        }

        private void Build()
        {
            var c = Compiled;
            var ctl = c.Rows("Control");
            if (ctl.Count > 0)
            {
                var r = ctl[0];
                _entry = c.FieldCode(r, "ENTRY_FUNC");
                _exit = c.FieldCode(r, "EXIT_FUNC");
                _bgnd = c.FieldCode(r, "BGND_FUNC");
                _ready = c.FieldCode(r, "READY_FUNC");
                _act = c.FieldCode(r, "ACT_FUNC");
                _deact = c.FieldCode(r, "DEACT_FUNC");
            }
            foreach (var r in c.Rows("PGDYNOBJ"))
            {
                int an = CompiledPage.Int(r, "AN");
                if (an <= 0)
                    continue;
                var o = Ensure(an, "PGDYNOBJ", CompiledPage.TypeCode(CompiledPage.Str(r, "TYPE")));
                o.Table = "PGDYNOBJ";
                o.Type = CompiledPage.TypeCode(CompiledPage.Str(r, "TYPE"));
                Fill(o, r);
            }
            foreach (var r in c.Rows("PGBUTTON"))
            {
                int an = CompiledPage.Int(r, "AN");
                if (an <= 0)
                    continue;
                var o = Ensure(an, "PGBUTTON", 0x1001);
                o.Table = "PGBUTTON";
                o.Type = 0x1001;
                Fill(o, r);
                o.ButtonText = TextOf(r, "TEXT");
                var t = new TouchCommand
                {
                    An = an,
                    Button = true,
                    RptRate = CompiledPage.Int(r, "RPT_RATE"),
                    Up = c.FieldCode(r, "CMD"),
                    Down = c.FieldCode(r, "DOWN_CMD"),
                    Repeat = c.FieldCode(r, "REPEAT_CMD"),
                    LogUp = TextOf(r, "MSG_LOG"),
                };
                AddTouch(t);
            }
            foreach (var r in c.Rows("PGTOUCH"))
            {
                int an = CompiledPage.Int(r, "AN");
                var t = new TouchCommand
                {
                    An = an,
                    Action = CompiledPage.Int(r, "ACTION", 1),
                    RptRate = CompiledPage.Int(r, "RPT_RATE"),
                    Up = c.FieldCode(r, "CMD"),
                    Down = c.FieldCode(r, "DOWN_CMD"),
                    Dbl = c.FieldCode(r, "DBLCLK_CMD"),
                    Repeat = c.FieldCode(r, "REPEAT_CMD"),
                    LogUp = TextOf(r, "MSG_LOG"),
                    LogDown = TextOf(r, "MSG_LOGDN"),
                    LogDbl = TextOf(r, "MSG_LOGDBL"),
                    LogRepeat = TextOf(r, "MSG_LOGRPT"),
                };
                AddTouch(t);
            }
            var keys = Animator.Keys;
            foreach (var r in c.Rows("PGKEY"))
            {
                var k = new KeyCommand
                {
                    An = CompiledPage.Int(r, "AN"),
                    Sequence = TextOf(r, "KEY_SEQ"),
                    Cmd = c.FieldCode(r, "CMD"),
                    Priv = CompiledPage.Int(r, "PRIV"),
                    Area = CompiledPage.Int(r, "AREA"),
                    MsgLog = TextOf(r, "MSG_LOG"),
                };
                k.Codes = keys.Sequence(k.Sequence);
                Keys.Add(k);
            }
            foreach (var r in c.Rows("PGADV"))
                CicodeObjects.Add(
                    new CicodeObject { An = CompiledPage.Int(r, "AN"), Cmd = c.FieldCode(r, "CMD") }
                );
            foreach (var r in c.Rows("PGTREND"))
            {
                int an = CompiledPage.Int(r, "AN");
                var t = new TrendObject(an)
                {
                    Cmd = c.FieldCode(r, "CMD"),
                    Visible = c.FieldCode(r, "VISIBLE"),
                };
                for (int k = 1; k <= 8; k++)
                    t.Pens.Add(TextOf(r, "PEN" + k.ToString(CultureInfo.InvariantCulture)));
                // the display client's trend object: pens, period, mode, cursor
                if (an > 0)
                    t.Data = TrendObjects.Open(
                        Animator.World,
                        Window,
                        an,
                        t.Pens,
                        TextOf(r, "CLUSTER"),
                        CompiledPage.Int(r, "PERIODIC", 1) != 0,
                        CompiledPage.Int(r, "TRENDNO")
                    );
                Trends.Add(t);
            }
            foreach (var r in c.Rows("PGVERTEX"))
            {
                int an = CompiledPage.Int(r, "AN");
                if (an <= 0)
                    continue;
                var vr = new VertexRow
                {
                    Index = CompiledPage.Int(r, "INDEX"),
                    Expr = c.FieldCode(r, "EXPR"),
                    X1 = CompiledPage.Int(r, "X1"),
                    Y1 = CompiledPage.Int(r, "Y1"),
                    X2 = CompiledPage.Int(r, "X2"),
                    Y2 = CompiledPage.Int(r, "Y2"),
                };
                if (!vr.Animated)
                    continue;
                Ensure(an, "PGVERTEX", 0).Vertices.Add(vr);
            }
            foreach (var r in c.Rows("PGALMIND"))
            {
                int an = CompiledPage.Int(r, "AN");
                if (an <= 0)
                    continue;
                var o = Ensure(an, "PGALMIND", 0x08);
                o.Indicator = new AlarmIndicator
                {
                    Expr = TextOf(r, "EXPR"),
                    Include = CompiledPage.Int(r, "INCLUDE"),
                    Inside = CompiledPage.Int(r, "INSIDE"),
                    Width = CompiledPage.Int(r, "WIDTH"),
                    Padding = CompiledPage.Int(r, "PADDING"),
                    FlagOn = CompiledPage.Int(r, "FLAG_ON"),
                    FlagLoc = CompiledPage.Int(r, "FLAG_LOC"),
                    EqHier = CompiledPage.Int(r, "EQHIER", 1),
                    Colour =
                        r.TryGetValue("COLOUR", out var col) && col is byte[] cb && cb.Length >= 4
                            ? BitConverter.ToUInt32(cb, 0)
                            : 0,
                };
            }
            BuildLegacy();
            // the forest: GROUP = the parent AN
            foreach (var o in Objects)
            {
                if (o.GroupAn > 0 && _byAn.TryGetValue(o.GroupAn, out var p) && p != o)
                    o.Parent = p;
                else if (
                    o.Drawing != null
                    && o.Drawing.Parent > 0
                    && _byAn.TryGetValue(o.Drawing.Parent, out var p2)
                    && p2 != o
                )
                    o.Parent = p2;
            }
            // a parent chain that comes back to itself (a malformed model: the parents are client
            // data) is cut where it closes, so every walk up the chain ends
            foreach (var o in Objects)
            {
                var path = new HashSet<AnimObject>();
                for (var x = o; x != null && path.Add(x); x = x.Parent)
                    if (x.Parent != null && path.Contains(x.Parent))
                    {
                        x.Parent = null;
                        break;
                    }
            }
            var seen = new HashSet<AnimObject>();
            void Visit(AnimObject o, int depth)
            {
                if (seen.Contains(o) || depth > 64)
                    return;
                if (o.Parent != null && !seen.Contains(o.Parent))
                    Visit(o.Parent, depth + 1);
                if (seen.Add(o))
                    _order.Add(o);
            }
            foreach (var o in Objects)
                Visit(o, 0);
        }

        private string TextOf(Dictionary<string, object?> r, string field) => CompiledPage.Str(r, field);

        private void AddTouch(TouchCommand t)
        {
            if (!Touches.TryGetValue(t.An, out var l))
                Touches[t.An] = l = new List<TouchCommand>();
            l.Add(t);
        }

        private void Fill(AnimObject o, Dictionary<string, object?> r, CompiledPage? from = null)
        {
            var c = from ?? Compiled;
            o.Row = r;
            o.GroupAn = CompiledPage.Int(r, "GROUP", -1);
            o.Vis = c.FieldCode(r, "VISIBLE");
            o.Dis = c.FieldCode(r, "DISABLED");
            o.Szx = c.FieldCode(r, "SZX");
            o.Szy = c.FieldCode(r, "SZY");
            o.Rot = c.FieldCode(r, "ROT");
            o.Mvx = c.FieldCode(r, "MVX");
            o.Mvy = c.FieldCode(r, "MVY");
            for (int k = 0; k < 5; k++)
            {
                char x = (char)('A' + k);
                o.Col[k] = c.FieldCode(r, "COL_" + x);
                o.Txt[k] = c.FieldCode(r, "TXT_" + x);
            }
            o.ColArr = c.FieldCode(r, "COL_ARR");
            o.ColExp = c.FieldCode(r, "COL_EXP");
            o.TxtStr = c.FieldCode(r, "TXT_STR");
            o.SetArr = c.FieldCode(r, "SET_ARR");
            o.Fil = c.FieldCode(r, "FIL");
            o.RotSl = c.SliderCode(r, "ROTSL");
            o.MvxSl = c.SliderCode(r, "MVXSL");
            o.MvySl = c.SliderCode(r, "MVYSL");
            int I(string f, int d = 0) => CompiledPage.Int(r, f, d);
            o.SzxMin = I("SZXMINPC");
            o.SzxMax = I("SZXMAXPC", 100);
            o.SzxOrg = I("SZXORG");
            o.SzxMode = I("SZXMODE");
            o.SzyMin = I("SZYMINPC");
            o.SzyMax = I("SZYMAXPC", 100);
            o.SzyOrg = I("SZYORG");
            o.SzyMode = I("SZYMODE");
            o.RotMin = I("ROTMINPC");
            o.RotMax = I("ROTMAXPC", 360);
            o.RotXOrg = I("ROTXORG");
            o.RotYOrg = I("ROTYORG");
            o.RotMode = I("ROTMODE");
            o.RotSlMode = I("ROTSLMODE");
            o.MvxMin = I("MVXMINPT");
            o.MvxMax = I("MVXMAXPT");
            o.MvxSlMode = I("MVXSLMODE");
            o.MvyMin = I("MVYMINPT");
            o.MvyMax = I("MVYMAXPT");
            o.MvySlMode = I("MVYSLMODE");
            o.ColMode = I("COLMODE");
            o.TxtMode = I("TXTMODE");
            o.FilMin = I("FILMINPC");
            o.FilMax = I("FILMAXPC", 100);
            o.FilMode = I("FILMODE");
            o.DisMode = I("DISMODE");
            o.GreyStyle = I("GREY_STYLE");
            o.Priv = I("PRIV");
            o.Area = I("AREA");
            o.Comment = TextOf(r, "COMMENT");
            o.Desc = TextOf(r, "DESC");
        }

        private void BuildLegacy()
        {
            var c = Compiled;
            foreach (string table in new[] { "PGNUM", "PGSTR", "PGANSYM", "PGSYM", "PGTEXT" })
            foreach (var r in c.Rows(table))
            {
                int an = CompiledPage.Int(r, "AN");
                if (an <= 0)
                    continue;
                int type =
                    table == "PGNUM" ? 0x1004
                    : table == "PGSTR" ? 0x1005
                    : table == "PGANSYM" ? 0x100d
                    : table == "PGSYM" ? 0x1007
                    : 0x05;
                var o = Ensure(an, table, type);
                o.Vis ??= c.FieldCode(r, "VISIBLE");
                if (o.Comment.Length == 0)
                    o.Comment = TextOf(r, "COMMENT");
                var lr = new LegacyRow
                {
                    Table = table,
                    Expr = c.FieldCode(r, "EXPR"),
                    Mode = CompiledPage.Int(r, "MODE"),
                };
                if (table == "PGSTR")
                {
                    lr.OffText = c.FieldCode(r, "OFF_TEXT");
                    lr.OnText = c.FieldCode(r, "ON_TEXT");
                }
                else if (table == "PGANSYM")
                {
                    lr.OffSymbol = TextOf(r, "OFF_SYMBOL");
                    for (int k = 1; k <= 8; k++)
                        lr.Symbols.Add(TextOf(r, "SYMBOL_" + k.ToString(CultureInfo.InvariantCulture)));
                }
                else if (table == "PGSYM" || table == "PGTEXT")
                {
                    string[] v = { "VAR_A", "VAR_B", "VAR_C" };
                    for (int k = 0; k < 3; k++)
                        if (
                            r.TryGetValue(v[k], out var tv)
                            && tv is byte[] tb
                            && tb.Length >= 9
                            && tb[0] != 0xFF
                        )
                            lr.Vars[k] = (long)BitConverter.ToUInt64(tb, 1);
                    string pre = table == "PGSYM" ? "SYMB" : "STR";
                    for (int s = 0; s < 8; s++)
                    {
                        string bits = Convert.ToString(s, 2).PadLeft(3, '0');
                        lr.Strings.Add(TextOf(r, pre + bits));
                    }
                }
                o.Legacy = lr;
            }
        }

        // ================================================================== evaluation
        /// <summary>Run an expression block in the foreground for this window and AN:
        /// type 2 REAL, 4 INT, 7 STRING; the value zeroed unless the code ends with
        /// one; the error = the task's expression error, 257 dropped unless RangeCheck.</summary>
        public EvalResult Eval(
            byte[] code,
            int type,
            int an,
            string cls,
            CicodeSim.Compiled.Program? program = null
        )
        {
            var r = new EvalResult
            {
                S = "",
                NotSimulated = "",
                Quality = Values.Quality.Good,
            };
            var timed = Animator.EvalTimed;
            long t0 = timed != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var t = Animator.RunForeground(code, program ?? Compiled.Program, Window.Number, an, cls);
            if (timed != null)
                timed(Window.Number, an, cls, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            if (t.State == TaskState.Stop && t.Result != null)
            {
                Value v = Conv.Convert(t.Result.Value, type, NoErr.Instance);
                r.Quality = t.Result.Value.Q;
                if (type == Types.Real)
                    r.D =
                        v.T == Types.Real ? v.D
                        : v.PayloadIsInt ? v.I
                        : 0.0;
                else if (type == Types.String)
                    r.S = v.T == Types.String ? v.S ?? "" : Conv.ValueText(v, out _);
                else
                    r.I = v.PayloadIsInt ? (int)v.I : 0;
            }
            else if (t.State != TaskState.Stop)
            {
                r.Halted = true;
                r.HaltReason = t.HaltReason ?? "";
                r.HaltCode = t.HaltCode;
                r.NotSimulated = Animator.NotSimulatedName(t);
                Animator.NoteEvalHalt(cls, an, t);
            }
            int err = t.ExprError;
            // a halting hardware error ends the code before it reaches (Div's 273): the
            // halt code is the error shown (#DIV/0, guess: the runtime's value cell after a halt)
            if (
                err == 0
                && t.State == TaskState.Halt
                && t.HaltCode > 0
                && (t.HaltReason ?? "").StartsWith("hardware error", StringComparison.Ordinal)
            )
                err = t.HaltCode;
            if (err == 257 && !Cfg.RangeCheck)
                err = 0;
            r.Error = err;
            r.Flags = Indication.Flags(r.Quality, err, Cfg.RangeCheck);
            // a bad / uncertain result counts as an errored one with code 0: the text shows #BAD
            // (live: conformance/pages/ExampleSA/Mixing_L3/tank_level f3_level_bad); doc
            // 1118038's table says the same for both: 'No Error' (0) shows #BAD/#UNC by default
            // and #OK (the error text of 0) with ShowBadText = ShowUncertainText = 0 (verify #UNC
            // live)
            if ((r.Flags & (Indication.Bad | Indication.Uncertain)) != 0)
                r.Flags |= Indication.Error;
            Animator.Count(cls, r);
            return r;
        }

        private EvalResult Int(byte[] code, AnimObject o, string cls) =>
            Note(o, Eval(code, Types.Long, o.An, cls, o.Program));

        private EvalResult Real(byte[] code, AnimObject o, string cls) =>
            Note(o, Eval(code, Types.Real, o.An, cls, o.Program));

        private EvalResult Str(byte[] code, AnimObject o, string cls) =>
            Note(o, Eval(code, Types.String, o.An, cls, o.Program));

        private static EvalResult Note(AnimObject o, EvalResult r)
        {
            o.Flags |= r.Flags;
            if (o.ErrCode == 0 && (r.Flags & Indication.Error) != 0)
                o.ErrCode = r.Error;
            return r;
        }

        // ================================================================== the scan
        /// <summary>One page scan.</summary>
        public void Scan()
        {
            if (Closed)
                return;
            var timed = Animator.ScanTimed;
            if (timed == null)
            {
                ScanOnce();
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                ScanOnce();
            }
            finally
            {
                timed(Window.Number, Page.Name, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            }
        }

        private void ScanOnce()
        {
            var a = Animator;
            Window.InAnimation = true;
            try
            {
                Scans++;
                State.Scan = Scans;
                // the genie instances DspSym placed since the last scan (their rows join here:
                // the scan's own lists never change while it walks them)
                ApplyInstances();
                // 1. first-scan gate: [Page]WaitForValidData holds the first scan while a page
                //    subscription is pending (453); the simulator's tags always have data
                // 3. first scan: a subscription error -> OnEvent 7 then 3
                bool animate = true;
                if (!FirstScanDone)
                {
                    FirstScanDone = true;
                    if (SubscriptionError())
                    {
                        long r7 = a.RaiseEvent(7, Window.Number);
                        long r3 = a.RaiseEvent(3, Window.Number);
                        if (r7 != 0 && r3 != 0)
                            animate = false;
                    }
                }
                // 4. the title: the page TITLE, else its name, unless WinTitle set
                //    one (the window's +100 flag, kept until a page is displayed in the window)
                if (!Window.TitleSet)
                    Window.Title = Page.Record.Title.Length > 0 ? Page.Record.Title : Page.Name;
                if (!animate)
                    return;
                // 5. _PageBack, OnEvent 6 "Page user always", OnEvent 10 "Page always"
                a.CallProjectFunction("_PageBack", Window.Number);
                a.RaiseEvent(6, Window.Number);
                a.RaiseEvent(10, Window.Number);
                // 6. BGND_FUNC (While page shown), value discarded
                if (_bgnd != null)
                    Eval(_bgnd, Types.Long, -1, "page.bgnd");
                // 7. objects
                foreach (var o in _order)
                    Update(o);
                // 8. Cicode objects (PGADV): CMD as a foreground expression, AN = its AN
                foreach (var co in CicodeObjects)
                    if (co.Cmd != null)
                        Eval(co.Cmd, Types.Long, co.An, "cicode_object", co.Program);
                // 9. trends: VISIBLE (INT) then CMD every scan (and the trends TrnNew made)
                SyncTrends();
                foreach (var tr in Trends)
                    UpdateTrend(tr);
                // the alarm lists of the page (not a numbered step: the runtime redraws a list
                // when the alarm server's data arrives)
                UpdateAlarmLists();
                // 11. ReadyCicodeDelay: READY_FUNC as a page task when the countdown reaches 0
                if (Window.ReadyCountdown > 0)
                {
                    Window.ReadyCountdown--;
                    if (Window.ReadyCountdown == 0)
                    {
                        a.Trace(Window.Number, Page.Name, "shown");
                        if (_ready != null)
                            ReadyTask = a.StartTask(
                                _ready,
                                Compiled.Program,
                                Window.Number,
                                -1,
                                "page.ready",
                                this
                            );
                    }
                }
            }
            finally
            {
                Window.InAnimation = false;
                // PageInfo(7) = the PREVIOUS display count (doc 1188754): 0 inside the first
                // scan, so it counts once the scan is done
                Window.Shows++;
            }
            Emit();
        }

        /// <summary>A page TAGREF that does not resolve (the first scan's communication error check).</summary>
        private bool SubscriptionError()
        {
            var tags = Animator.Tags;
            foreach (var r in Compiled.Program.TagRefs.Values)
            {
                TagRef? t = r;
                if (r.Tag.IndexOf('?') >= 0)
                {
                    var (rr, lit, err) = tags.Resolve(r, Window.Number);
                    if (lit != null)
                        continue;
                    if (rr == null || err != 0)
                        return true;
                    t = rr;
                }
                if (!tags.Db.ResolveRef(t).Found)
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ one object
        /// <summary>The per-object update.</summary>
        private void Update(AnimObject o)
        {
            o.Flags = 0;
            o.ErrCode = 0;
            var p = o.Parent;
            // 1. only an object with an animation: Show/Hide (VISIBLE), Disable (DISABLED)
            if (o.HasAnimation)
            {
                if (o.Vis != null)
                    o.OwnShown = Int(o.Vis, o, "visibility").I == 0;
                if (o.Dis != null)
                    o.OwnDisabled = Int(o.Dis, o, "disable").I != 0;
            }
            // 2. area / privilege (the page's AREAS when AREA = 0)
            int area = o.Area != 0 ? o.Area : Page.Record.Areas;
            o.AreaOk = Animator.Display.AreaOk(area);
            o.PrivOk = Animator.Display.PrivOk(o.Priv, area);
            bool groupDis = p != null && p.Disabled;
            bool parentShown = p == null || p.Shown;
            o.EffStyle = groupDis ? p!.EffStyle : o.GreyStyle;
            o.EffDisMode = groupDis ? p!.EffDisMode : o.DisMode;
            o.Disabled = o.OwnDisabled || groupDis || (o.EffDisMode == 1 && (!o.AreaOk || !o.PrivOk));
            bool shown = o.OwnShown && parentShown;
            if (shown)
            {
                bool ok = true;
                if (o.EffStyle == 4)
                    ok = !o.Disabled;
                shown = o.EffDisMode == 1 ? ok : ok && o.AreaOk;
            }
            // DspShow(AN, 0) hides the object
            if (shown && Page.Get(o.An)?.ShownByCicode == false)
                shown = false;
            o.Shown = shown;
            var s = o.State;
            s.Shown = shown;
            s.Disabled = o.Disabled;
            s.DisableStyle = o.Disabled ? (o.EffStyle >= 1 && o.EffStyle <= 5 ? o.EffStyle : 1) : 5;
            if (Animator.Display.Object(Window.Number, o.An) is AnObject ao)
                ao.Hidden = !shown;
            // 3. only a shown object evaluates the rest
            if (shown)
            {
                UpdateVertices(o);
                UpdateGeometry(o);
                UpdateColour(o);
                UpdateFill(o);
                UpdateText(o);
                UpdateLegacy(o);
                UpdateIndicator(o);
            }
            // a text sets its own indication from its text expression (UpdateText)
            if (!o.HasText && shown)
                s.Indication = Indication.Name(o.Flags, Window.IgnoreValueQuality);
            UpdateTip(o);
        }

        private void UpdateVertices(AnimObject o)
        {
            if (o.Vertices.Count == 0)
                return;
            int max = -1;
            foreach (var v in o.Vertices)
                max = Math.Max(max, v.Index);
            if (o.Drawing?.Vertices != null)
                max = Math.Max(max, o.Drawing.Vertices.Value - 1);
            var list = new List<double[]>();
            for (int i = 0; i <= max; i++)
                list.Add(new double[] { 0.0, 0.0 });
            foreach (var v in o.Vertices)
            {
                double pc = Real(v.Expr!, o, "vertex").D;
                v.Dx = Formulas.Vertex(pc, v.X1, v.X2);
                v.Dy = Formulas.Vertex(pc, v.Y1, v.Y2);
                if (v.Index >= 0 && v.Index < list.Count)
                    list[v.Index] = new[] { v.Dx, v.Dy };
            }
            o.State.VertexOffsets = list;
        }

        private Rect? RectOf(AnimObject o) => o.Drawing?.Rect;

        private void UpdateGeometry(AnimObject o)
        {
            var s = o.State;
            Rect? rect = RectOf(o);
            // Size X / Size Y
            if (o.Szx != null || o.Szy != null)
            {
                double sx =
                    o.Szx != null
                        ? Formulas.ScaleFactor(Real(o.Szx, o, "scaling").D, o.SzxMin, o.SzxMax)
                        : 1.0;
                double sy =
                    o.Szy != null
                        ? Formulas.ScaleFactor(Real(o.Szy, o, "scaling").D, o.SzyMin, o.SzyMax)
                        : 1.0;
                s.Scale = new[] { sx, sy };
                if (rect != null)
                {
                    var rc = rect.Value;
                    s.ScaleOrigin = new double[]
                    {
                        rc.L + Formulas.Pivot(o.SzxMode, o.SzxOrg, rc.Width),
                        rc.T + Formulas.Pivot(o.SzyMode, o.SzyOrg, rc.Height),
                    };
                }
            }
            // Move X / Slider X, Move Y / Slider Y
            bool mx = o.Mvx != null || o.SliderX,
                my = o.Mvy != null || o.SliderY;
            if (mx || my)
            {
                double magX = o.Drawing?.MagnitudeX ?? 1.0,
                    magY = o.Drawing?.MagnitudeY ?? 1.0;
                double dx = 0,
                    dy = 0;
                if (o.SliderX)
                {
                    if (!o.SliderActiveX)
                        dx = Formulas.MoveX(Real(o.MvxSl.Read!, o, "slider").D, o.MvxMin, o.MvxMax);
                    else
                        dx = s.Move != null ? s.Move[0] / magX : 0;
                }
                else if (o.Mvx != null)
                    dx = Formulas.MoveX(Real(o.Mvx, o, "movement").D, o.MvxMin, o.MvxMax);
                if (o.SliderY)
                {
                    if (!o.SliderActiveY)
                        dy = Formulas.MoveY(Real(o.MvySl.Read!, o, "slider").D, o.MvyMin, o.MvyMax);
                    else
                        dy = s.Move != null ? s.Move[1] / magY : 0;
                }
                else if (o.Mvy != null)
                    dy = Formulas.MoveY(Real(o.Mvy, o, "movement").D, o.MvyMin, o.MvyMax);
                s.Move = new[] { dx * magX, dy * magY };
            }
            // Rotate / Slider Rotate
            if (o.Rot != null || o.SliderRot)
            {
                if (o.SliderRot)
                {
                    if (!o.SliderActiveRot)
                        s.Rotate = Formulas.RotateDegrees(
                            Real(o.RotSl.Read!, o, "slider").D,
                            o.RotMin,
                            o.RotMax
                        );
                }
                else
                    s.Rotate = Formulas.RotateDegrees(Real(o.Rot!, o, "rotation").D, o.RotMin, o.RotMax);
                if (rect != null)
                {
                    var rc = rect.Value;
                    s.RotateOrigin = new double[]
                    {
                        rc.L + Formulas.Pivot(o.RotMode, o.RotXOrg, rc.Width),
                        rc.T + Formulas.Pivot(o.RotMode, o.RotYOrg, rc.Height),
                    };
                }
            }
        }

        private void UpdateColour(AnimObject o)
        {
            if (!o.HasColour)
                return;
            var cs = o.Drawing?.Colour;
            ColourPair? sel = null;
            switch (o.ColMode)
            {
                case 0:
                    sel = Colours.OnOff(Int(o.Col[0]!, o, "fill_colour_onoff").I, cs);
                    break;
                case 1:
                {
                    int n = Colours.Conditions(cs?.Sta.Count ?? 0, AnimObject.LastPresent(o.Col));
                    int index = 0;
                    for (int k = 0; k < n && k < 5; k++)
                    {
                        if (o.Col[k] == null)
                            continue;
                        // a 257 on a condition is ignored unless RangeCheck: Eval applies that
                        // rule already
                        var r = Note(o, Eval(o.Col[k]!, Types.Long, o.An, "fill_colour_multi"));
                        if (r.I != 0)
                            index |= 1 << k;
                    }
                    sel = Colours.MultiState(index, cs);
                    break;
                }
                case 2:
                {
                    long v = Int(o.ColArr!, o, "fill_colour_array").I;
                    var arr = cs?.Arr;
                    int count = arr?.Count ?? 256;
                    int idx = Colours.ArrayIndex(v, count, out bool bad);
                    if (bad)
                        ArrayError(o);
                    sel = arr != null && idx < arr.Count ? arr[idx] : (ColourPair?)null;
                    break;
                }
                case 3:
                {
                    double v = Real(o.ColExp!, o, "fill_colour_threshold").D;
                    var thr =
                        cs != null && cs.Thr.Count > 0
                            ? (IReadOnlyList<Threshold>)cs.Thr
                            : Colours.DefaultThresholds;
                    sel = Colours.ThresholdColour(v, Colours.Sort(thr)) ?? cs?.State0;
                    break;
                }
                case 4:
                {
                    double v = Real(o.ColExp!, o, "fill_colour_gradient").D;
                    ColourPair? lo = cs?.State0;
                    ColourPair hi = cs?.MaxOn ?? ColourPair.Steady(Colours.DefaultMaxOn);
                    sel = lo != null ? Colours.Gradient(v, lo.Value, hi) : (ColourPair?)null;
                    break;
                }
            }
            o.ColourSel = sel;
        }

        /// <summary>An array index outside the array (the object's error handler
        /// with 274, 0x112): a hardware alarm (doc: 1175480 'with a hardware alarm'), logged, not
        /// an indication on the object (guess).</summary>
        private void ArrayError(AnimObject o)
        {
            Animator.HardwareAlarm(
                274,
                "AN " + o.An.ToString(CultureInfo.InvariantCulture) + " array index out of range"
            );
        }

        private void UpdateFill(AnimObject o)
        {
            if (o.Fil == null)
                return;
            double p = Real(o.Fil, o, "fill_level").D;
            o.State.FillLevel = Formulas.FillFraction(p, o.FilMin, o.FilMax);
            o.State.FillLevelDirection = o.FilMode;
            o.FilColSel = o.Drawing?.Colour?.FilCol;
        }

        private static string Unset(string? s) => s == null || s == "#\n" || s == "#\r\n" ? "" : s;

        private void UpdateText(AnimObject o)
        {
            var s = o.State;
            if (o.HasSymbolSet)
            {
                UpdateSymbolSet(o);
                return;
            }
            if (!o.HasText)
                return;
            var ts = o.Drawing?.Text;
            string? value = null;
            int flags = 0,
                err = 0;
            EvalResult r;
            switch (o.TxtMode)
            {
                case 0:
                    r = Int(o.Txt[0]!, o, "display_onoff");
                    flags = r.Flags;
                    err = r.Error;
                    if (ts != null)
                        value = Unset(r.I != 0 ? ts.On : ts.Off);
                    break;
                case 1:
                {
                    int index = 0;
                    for (int k = 0; k < 5; k++)
                    {
                        if (o.Txt[k] == null)
                            continue;
                        r = Int(o.Txt[k]!, o, "display_multi");
                        flags |= r.Flags;
                        if (err == 0)
                            err = r.Error;
                        if (r.I != 0)
                            index |= 1 << k;
                    }
                    if (ts != null)
                        value = index < ts.States.Count ? Unset(ts.States[index]) : "";
                    break;
                }
                case 2:
                {
                    r = Int(o.Txt[0]!, o, "display_array");
                    flags = r.Flags;
                    err = r.Error;
                    if (ts != null)
                    {
                        int idx = Colours.ArrayIndex(r.I, ts.Array.Count, out bool bad);
                        if (bad)
                            ArrayError(o);
                        value = idx < ts.Array.Count ? Unset(ts.Array[idx]) : "";
                    }
                    break;
                }
                default:
                    r = Str(o.TxtStr!, o, o.TxtMode == 3 ? "display_numeric" : "display_string");
                    flags = r.Flags;
                    err = r.Error;
                    value = r.S;
                    break;
            }
            // every text result goes through the quality / error indication
            string shown = Indication.Text(
                value ?? "",
                flags,
                err,
                Window.IgnoreValueQuality,
                Cfg,
                out string? replaced
            );
            s.Text = value == null && replaced == null ? null : shown;
            s.Error = replaced;
            s.Indication = Indication.Name(flags, Window.IgnoreValueQuality);
        }

        private void UpdateSymbolSet(AnimObject o)
        {
            var ss = o.Drawing?.SymbolSet;
            int states = ss?.States ?? 0;
            int declared = ss?.Declared ?? 256;
            int index;
            switch (o.TxtMode)
            {
                case 0:
                {
                    index = Int(o.Txt[0]!, o, "symbolset_onoff").I != 0 ? 1 : 0;
                    // an index past the declared count shows no state
                    if (ss != null && index >= Math.Max(states, 1))
                        index = -1;
                    break;
                }
                case 1:
                {
                    index = 0;
                    for (int k = 0; k < 5; k++)
                        if (o.Txt[k] != null && Int(o.Txt[k]!, o, "symbolset_multi").I != 0)
                            index |= 1 << k;
                    if (ss != null && index >= states)
                        index = -1;
                    break;
                }
                case 2:
                {
                    long v = Int(o.SetArr!, o, "symbolset_array").I;
                    // [0, states) the state, [states, declared) none, else none + error
                    if (v < 0 || v >= declared)
                    {
                        index = -1;
                        ArrayError(o);
                    }
                    else if (ss != null && v >= states)
                        index = -1;
                    else
                        index = (int)v;
                    break;
                }
                default:
                {
                    // Animated: while the value is not 0 every AnmDelay tick moves to
                    // the next frame 1..n, wrapping to 1; 0 = the Off frame (guess)
                    long v = Int(o.Txt[0]!, o, "symbolset_animated").I;
                    o.AnimValue = v;
                    if (v == 0)
                        o.Frame = 0;
                    else if (o.AnimTick || o.Frame == 0)
                    {
                        int last = states > 1 ? states - 1 : 1;
                        o.Frame = o.Frame >= last ? 1 : o.Frame + 1;
                    }
                    o.AnimTick = false;
                    index = o.Frame;
                    break;
                }
            }
            o.State.SymbolState = index;
            o.State.Symbol = ss != null && index >= 0 && index < ss.Names.Count ? ss.Names[index] : null;
        }

        private void UpdateLegacy(AnimObject o)
        {
            var lr = o.Legacy;
            if (lr == null)
                return;
            var s = o.State;
            switch (lr.Table)
            {
                case "PGNUM":
                    if (lr.Expr != null)
                    {
                        var r = Str(lr.Expr, o, "legacy_number");
                        s.Text = Indication.Text(
                            r.S,
                            r.Flags,
                            r.Error,
                            Window.IgnoreValueQuality,
                            Cfg,
                            out string? rep
                        );
                        s.Error = rep;
                    }
                    break;
                case "PGSTR":
                    if (lr.Expr != null)
                    {
                        long v = Int(lr.Expr, o, "legacy_string").I;
                        byte[]? t = v != 0 ? lr.OnText : lr.OffText;
                        s.Text = t != null ? Str(t, o, "legacy_string").S : "";
                    }
                    break;
                case "PGANSYM":
                    if (lr.Expr != null)
                    {
                        long v = Int(lr.Expr, o, "legacy_animated_symbol").I;
                        s.Symbol =
                            v == 0 ? lr.OffSymbol
                            : lr.Symbols.Count > 0
                                ? lr.Symbols[(int)((Math.Max(v, 1) - 1) % lr.Symbols.Count)]
                            : "";
                    }
                    break;
                case "PGSYM":
                case "PGTEXT":
                {
                    int index = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        if (lr.Vars[k] < 0 || !Compiled.Program.TagRefs.TryGetValue(lr.Vars[k], out var tr))
                            continue;
                        Value v = Animator.Tags.Db.Get(tr.Tag, tr.Element, tr.Item, cluster: tr.Cluster);
                        bool on = v.PayloadIsInt ? v.I != 0 : Types.IsRealType(v.T) && v.D != 0;
                        // STR000 .. STR111: VAR_A the high digit (guess)
                        if (on)
                            index |= 4 >> k;
                    }
                    Animator.Count(lr.Table == "PGSYM" ? "legacy_symbol" : "legacy_text", default);
                    string x = index < lr.Strings.Count ? lr.Strings[index] : "";
                    if (lr.Table == "PGSYM")
                        s.Symbol = x;
                    else
                        s.Text = x;
                    break;
                }
            }
        }

        /// <summary>Update an alarm indicator from the top active priority of its equipment.</summary>
        private void UpdateIndicator(AnimObject o)
        {
            var ind = o.Indicator;
            if (ind == null)
                return;
            var world = Animator.World;
            string eq = SubstituteAssociations(ind.Expr);
            Animator.Count("alarm_indicator", default);
            if (eq.Length == 0)
            {
                o.State.Alarm = new AlarmIndicatorState();
                return;
            }
            string filter = ind.EqHier switch
            {
                0 => "EQUIPMENT=" + eq + ", EQUIPMENT=" + eq + ".*",
                2 => "EQUIPMENT=" + eq + ".*",
                _ => "EQUIPMENT=" + eq,
            };
            var st = new AlarmIndicatorState();
            try
            {
                var (p, state) = world.Alarms.Highest("", filter, ind.Include != 0, indicatorOnly: true);
                if (p != 0 && state >= 1 && state <= 3)
                {
                    st.Priority = p;
                    st.State =
                        state == 1 ? "unack_on"
                        : state == 2 ? "unack_off"
                        : "ack_on";
                    var pair = Animator.PriorityColours(p, state);
                    st.Colour = st.Fg = pair?.fg;
                    st.Bg = pair?.bg;
                    st.Flash = pair != null && pair.Value.fg != pair.Value.bg;
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Animator.LogLine(
                    "alarm indicator AN " + o.An.ToString(CultureInfo.InvariantCulture) + ": " + e.Message
                );
            }
            o.State.Alarm = st;
        }

        /// <summary>A text with '?name?' tokens replaced by the window's associations.</summary>
        public string SubstituteAssociations(string text)
        {
            if (text.IndexOf('?') < 0)
                return text;
            var parts = text.Split('?');
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < parts.Length; k++)
            {
                if (k % 2 == 1 && k < parts.Length - 1)
                {
                    var e = DisplayState.Find(Window, "?" + parts[k] + "?");
                    sb.Append(e != null ? e.Tag : "");
                }
                else
                    sb.Append(parts[k]);
            }
            return sb.ToString();
        }

        /// <summary>The tool tip: the COMMENT of the innermost object under the resting pointer
        /// (doc: 1172178; the TipTimeOut wait is not modelled: guess).</summary>
        private void UpdateTip(AnimObject o)
        {
            string? tip = null;
            // a tip DspSetTip gave replaces the configured one (the runtime writes the object's
            // tip)
            string comment = Page.Get(o.An) is AnObject ao && ao.TipSet ? ao.Tip : o.Comment;
            if (
                comment.Length > 0
                && Window.MouseInside
                && RectOf(o) is Rect r
                && r.ContainsInclusive(Window.MouseX, Window.MouseY)
            )
                tip = SubstituteAssociations(comment);
            o.State.Tooltip = tip;
        }

        /// <summary>The page's trend objects now: a PGTREND row's object follows the display
        /// client's (TrnNew may have remade it, TrnDelete removed it); a trend TrnNew made at an
        /// AN with no row joins as a dynamic one.</summary>
        private void SyncTrends()
        {
            var tb = PageObjectTables.Of(Animator.Display);
            foreach (var t in Trends)
                if (t.An > 0)
                    t.Data = tb.Trend(Window.Number, Page.Name, t.An);
            foreach (var d in tb.TrendsOf(Window.Number, Page.Name))
                if (!Trends.Exists(x => x.An == d.An))
                    Trends.Add(new TrendObject(d.An) { Data = d, Dynamic = true });
        }

        private void UpdateTrend(TrendObject tr)
        {
            // window state 1 only; VISIBLE when the trend object exists, then CMD (type 9)
            if (tr.Visible != null && tr.Data != null)
                tr.Shown = Eval(tr.Visible, Types.Long, tr.An, "trend").I == 0;
            if (tr.Cmd != null)
                Eval(tr.Cmd, Types.Long, tr.An, "trend");
            // an AN that is animated too (a PGDYNOBJ row) carries the trend in its own state
            var s = Object(tr.An)?.State ?? tr.State;
            if (s == tr.State)
                s.Shown = tr.Shown;
            s.Trend = tr.Data != null ? TrendStateOf(tr.Data) : null;
        }

        /// <summary>A trend object's state: period and span in seconds, the samples, mode,
        /// cursor, focus and per configured pen its trend tag, scale, colour (the flash phase's
        /// half of the drawing's pen colour) and samples oldest first.</summary>
        internal TrendState TrendStateOf(PageTrend d)
        {
            var w = Animator.World;
            bool flashOn = Animator.Display.FlashPhase == 0;
            var ts = new TrendState
            {
                Period = TrendData.PeriodTicks(w, d) / (double)TrendData.Tick,
                Span = TrendData.SpanTicks(w, d) / (double)TrendData.Tick,
                Samples = Math.Max(1, d.Samples),
                Historical = d.Historical,
                Cursor = d.Cursor,
                Focus = d.Focus + 1,
            };
            for (int k = 0; k < d.Pens.Length; k++)
            {
                var p = d.Pens[k];
                if (!p.Used)
                    continue;
                Animator.Sampler.Want(p.Tag);
                ts.Pens.Add(
                    new TrendPenState
                    {
                        Pen = k + 1,
                        Tag = p.Tag,
                        Lo = p.Lo,
                        Hi = p.Hi,
                        Colour = k < d.PenColours.Count ? d.PenColours[k].At(flashOn) : (long?)null,
                        End = w.Clock.TOfFiletime((ulong)TrendData.EndOf(w, d, p)),
                        Values = TrendData.Samples(w, d, p),
                    }
                );
            }
            return ts;
        }

        // ------------------------------------------------------------------ alarm lists
        /// <summary>The states of ANs that are not animated but show a page object (alarm list
        /// rows on AN points), with the copy last sent.</summary>
        private readonly Dictionary<int, ObjectState> _extra = new Dictionary<int, ObjectState>();
        private readonly Dictionary<int, ObjectState?> _extraSent = new Dictionary<int, ObjectState?>();
        private HashSet<int> _listAns = new HashSet<int>();

        private ObjectState StateFor(int an)
        {
            var o = Object(an);
            if (o != null)
                return o.State;
            var tr = Trends.Find(x => x.An == an);
            if (tr != null)
                return tr.State;
            if (!_extra.TryGetValue(an, out var e))
            {
                e = new ObjectState(an);
                _extra[an] = e;
                _extraSent[an] = null;
            }
            return e;
        }

        /// <summary>The alarm lists of the page (AlarmDsp / AlarmDspLast / AlarmListCreate):
        /// alarm_list on each list's AN, alarm_row on each drawn row's AN; a list whose data
        /// changed starts its callback (Builtins.AlarmLists.Scan).</summary>
        private void UpdateAlarmLists()
        {
            var h = Animator.Host;
            var now = new HashSet<int>();
            if (h != null && Animator.World != null)
            {
                bool flashOn = Animator.Display.FlashPhase == 0;
                foreach (
                    var (an, ls, rows) in AlarmLists.Scan(h, Animator.Vm, Window.Number, Page.Name, flashOn)
                )
                {
                    Animator.Count("alarm_list", default);
                    var s = StateFor(an);
                    s.AlarmList = ls;
                    if (!rows.Exists(x => x.an == an))
                        s.AlarmRow = null;
                    now.Add(an);
                    foreach (var (ran, rs) in rows)
                    {
                        StateFor(ran).AlarmRow = rs;
                        now.Add(ran);
                    }
                }
            }
            // the ANs a list showed on the last scan and shows no more
            foreach (int an in _listAns)
                if (!now.Contains(an))
                {
                    var s = StateFor(an);
                    s.AlarmList = null;
                    s.AlarmRow = null;
                }
            _listAns = now;
        }

        // ================================================================== states
        /// <summary>The state of every object with the current flash phase, and the ones that
        /// changed since the last emit (they go to the viewer).</summary>
        internal List<ObjectState> Emit()
        {
            bool flashOn = Animator.Display.FlashPhase == 0;
            State.FlashOn = flashOn;
            var changed = new List<ObjectState>();
            EchoAndPrompt(changed);
            foreach (var o in Objects)
            {
                var s = o.State;
                if (o.HasColour)
                    s.FillColour = o.ColourSel?.At(flashOn);
                if (o.Fil != null)
                    s.FillLevelColour = o.FilColSel?.At(flashOn);
                if (s.Alarm != null && s.Alarm.Flash)
                    s.Alarm = s.Alarm.InPhase(flashOn);
                if (o.Sent == null || !o.Sent.SameAs(s))
                {
                    o.Sent = s.Clone();
                    changed.Add(o.Sent);
                }
                State.Objects[o.An] = s;
            }
            foreach (var kv in _extra)
            {
                var own = kv.Value;
                _extraSent.TryGetValue(kv.Key, out var sent);
                if (sent == null || !sent.SameAs(own))
                {
                    var c = own.Clone();
                    _extraSent[kv.Key] = c;
                    changed.Add(c);
                }
                if (!State.Objects.ContainsKey(kv.Key))
                    State.Objects[kv.Key] = own;
            }
            foreach (var t in Trends)
            {
                if (t.An <= 0 || Object(t.An) != null)
                    continue;
                if (t.Sent == null || !t.Sent.SameAs(t.State))
                {
                    t.Sent = t.State.Clone();
                    changed.Add(t.Sent);
                }
                if (!State.Objects.ContainsKey(t.An))
                    State.Objects[t.An] = t.State;
            }
            // what Cicode drew (DspStr, DspText ...) changed: the page state changed too
            bool drawn = Window.DrawnSerial != _drawnSent;
            _drawnSent = Window.DrawnSerial;
            // the page variables (PageSetInt ...) changed or were first used: sent too
            bool vars = Window.VarSerial != _varsSent;
            _varsSent = Window.VarSerial;
            State.PageVarsFresh = vars;
            if (vars)
                State.PageVars = Window.VarsJson();
            // the page's ActiveX controls (a property the code or a viewer wrote): sent too
            bool controls = false;
            var host = Animator.Host;
            long cs = host != null ? Outside.ControlsSerial(host) : 0;
            if (cs != _controlsSent)
            {
                _controlsSent = cs;
                var cj = host != null ? Outside.ControlsJson(host, Window) : null;
                string text = cj?.ToString() ?? "";
                if (text != _controlsText)
                {
                    _controlsText = text;
                    State.Controls = cj;
                    controls = true;
                }
            }
            State.ControlsFresh = controls;
            // a flash phase change alone is news too: a viewer alternates the colours and the
            // pictures that flash of their own (a flashing colour pair or bitmap of the drawing,
            // no expression behind it) on the phase pageChanged carries
            bool flash = flashOn != _flashSent;
            _flashSent = flashOn;
            if (changed.Count > 0 || drawn || vars || controls || flash)
            {
                State.Serial++;
                Window.PageSerial = State.Serial;
                Animator.Changed(this, changed);
            }
            return changed;
        }

        private long _drawnSent;
        private bool _flashSent = true;
        private long _varsSent = -1;
        private long _controlsSent = -1;
        private string _controlsText = "";

        private ObjectState? _echo,
            _echoSent,
            _prompt,
            _promptSent;

        /// <summary>The key command line on AN [Page]KeyEcho (1) and the prompt line on AN
        /// [Page]Prompt (2) when the page has those ANs and they show no display value of their
        /// own (doc: 1118040, 1118061).</summary>
        private void EchoAndPrompt(List<ObjectState> changed)
        {
            void One(int an, string text, ref ObjectState? own, ref ObjectState? sent)
            {
                if (an <= 0 || !Page.Objects.ContainsKey(an))
                    return;
                var o = Object(an);
                if (o != null)
                {
                    if (!o.HasText)
                        o.State.Text = text.Length > 0 ? text : null;
                    return;
                }
                own ??= new ObjectState(an);
                own.Text = text.Length > 0 ? text : null;
                if (sent == null || !sent.SameAs(own))
                {
                    sent = own.Clone();
                    changed.Add(sent);
                }
                State.Objects[an] = own;
            }
            var sb = new System.Text.StringBuilder();
            foreach (int k in Window.KeyBuffer)
                if (k >= 0x20 && k < 0x100 && k != 0x7F)
                    sb.Append((char)k);
            One(Cfg.KeyEcho, sb.ToString(), ref _echo, ref _echoSent);
            One(Cfg.Prompt, Window.Prompt, ref _prompt, ref _promptSent);
        }

        /// <summary>The page events' code: entry / exit / activate / deactivate (Control fields).</summary>
        internal byte[]? EntryCode => _entry;
        internal byte[]? ExitCode => _exit;
        internal byte[]? ActivateCode => _act;
        internal byte[]? DeactivateCode => _deact;
    }
}
