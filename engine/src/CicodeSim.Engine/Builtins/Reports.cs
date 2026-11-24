using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CProgram = CicodeSim.Compiled.Program;

namespace CicodeSim.Builtins
{
    public sealed partial class SimHost
    {
        /// <summary>The unit and tag table the report server compiles format files and triggers
        /// against (a simulation's source compiler); null = a unit of the World's root, compiled
        /// for the reports at their first run.</summary>
        internal Func<(CompileUnit Unit, TagTable Tags)>? ReportSource;
    }

    /// <summary>
    /// The report server: the reports of reports.dbf run on their schedule (TIME, PERIOD), on
    /// their trigger (TRIG) or when Report() asks. A run writes its format file to its device
    /// (Devices): fixed text as it is, each {field} formatted, {CICODE} blocks run (their output
    /// is Print / PrintLn's, to DevCurr). Each field and block runs as a background task of the
    /// VM's scheduler, one after the other; DevCurr in those tasks is the report's device.
    /// </summary>
    public static class Reports
    {
        internal const int Event = 0,
            Daily = 1,
            Weekly = 2,
            Monthly = 3,
            Yearly = 4;

        private const int ReportBusy = 278,
            InvalidReport = 280,
            NoServer = 281;

        private const string RunKey = "report";

        internal enum Kind
        {
            Text,
            Field,
            Code,
            FormFeed,
            ErrFormatOn,
            ErrFormatOff,
        }

        /// <summary>One piece of a format file.</summary>
        internal sealed class Seg
        {
            public Kind Kind;

            /// <summary>Fixed text; a field's or block's source.</summary>
            public string Text = "";

            /// <summary>A field's width (its characters between the braces, or its format's).</summary>
            public int Width;
            public byte[]? Code;

            /// <summary>Why the field or block did not compile.</summary>
            public string? Error;
        }

        /// <summary>A format file compiled.</summary>
        internal sealed class FormatFile
        {
            public string Path = "";
            public long Stamp;
            public bool Rtf;
            public List<Seg> Segs = new List<Seg>();
            public CProgram Program = new CProgram();

            /// <summary>The report's variables (declared in an ASCII report's first block): their
            /// CODEVAR offsets in <see cref="Program"/> and declarations.</summary>
            public List<(long Offset, VarDecl Decl)> Vars = new List<(long, VarDecl)>();
        }

        /// <summary>One report of reports.dbf, with RepSetControl's changes.</summary>
        internal sealed class Rep
        {
            public string Name = "",
                Cluster = "",
                Trig = "",
                Format = "",
                Device = "",
                Priv = "",
                Area = "",
                Dir = "";

            /// <summary>Event, Daily, Weekly, Monthly or Yearly.</summary>
            public int Type;

            /// <summary>Daily: seconds; weekly: the day (0 Sunday); monthly: the day of the
            /// month; yearly: the day of the year (1 = 1 January, a year of 365 days).</summary>
            public long Period;

            /// <summary>Seconds from midnight.</summary>
            public long Sync;
            public bool Disabled;

            /// <summary>Unix seconds of the next scheduled run, 0 = none.</summary>
            public double Next;
            public bool TrigSeen,
                LastTrig,
                Requested;
            public int Runs;
            internal FormatFile? Compiled;
            internal Run? Running;
            internal byte[]? TrigCode;
            internal CProgram? TrigProgram;
            internal bool TrigTried;
        }

        /// <summary>A report running.</summary>
        internal sealed class Run
        {
            public Rep Rep = null!;
            internal FormatFile Format = null!;
            public long Device = -1;
            public string Cluster = "";
            public int Next;
            public bool ErrFormat = true;
            public CicodeTask? Task;
            internal Seg? Seg;
        }

        private sealed class Server
        {
            public CicodeVm? Vm;
            public List<Rep>? Reps;
            public bool Started;

            /// <summary>Why the report server does not run (null = it runs).</summary>
            public string? Off;
            public bool Inhibit = true;
            public (CompileUnit Unit, TagTable Tags)? Own;
        }

        private static readonly ConditionalWeakTable<SimHost, Server> Servers_ =
            new ConditionalWeakTable<SimHost, Server>();

        private static Server Of(SimHost h) => Servers_.GetValue(h, _ => new Server());

        private static void Log(SimHost h, string text)
        {
            var vm = Of(h).Vm;
            h.Log?.Invoke(text);
            vm?.Log?.Invoke(text);
        }

        private static string Param(SimHost h, string name, string def)
        {
            string? v = Params.Get(h, "Report", name);
            return v == null || CitectText.Trim(v).Length == 0 ? def : CitectText.Trim(v);
        }

        private static double Now(SimHost h) => h.World!.Clock.Unix();

        // ------------------------------------------------------------------ the server
        /// <summary>Start the report server of <paramref name="h"/>'s World (once): it checks the
        /// schedules and triggers every [Report]WatchTime ms on the World's clock and runs
        /// the reports as tasks of <paramref name="vm"/>'s scheduler. It does not run without a
        /// report server in the project (repsrv.dbf) or with [Report]Disable 1.</summary>
        public static void Start(SimHost h, CicodeVm vm)
        {
            var s = Of(h);
            if (s.Started)
                return;
            s.Started = true;
            s.Vm = vm;
            if (h.World == null)
            {
                s.Off = "no World";
                return;
            }
            if (!Servers.All(h).Exists(x => x.Type == "Report"))
            {
                s.Off = "the project has no report server";
                return;
            }
            if (Param(h, "Disable", "0") == "1")
            {
                s.Off = "[Report]Disable is 1";
                return;
            }
            if (vm.Scheduler == null)
            {
                s.Off = "the VM has no scheduler";
                return;
            }
            s.Inhibit = Param(h, "InhibitEvent", "1") != "0";
            vm.Scheduler.TaskEnded += t => Ended(h, t);
            double now = Now(h);
            foreach (var r in Reps(h))
                if (r.Type != Event)
                    r.Next = NextAfter(h, r, now);
            string startup = Param(h, "Startup", "Startup");
            var first = Find(h, startup);
            if (first != null)
                first.Requested = true;
            int watch = int.TryParse(
                Param(h, "WatchTime", "1000"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int w
            )
                ? Math.Max(100, Math.Min(60000, w))
                : 1000;
            h.World.Clock.Every(watch / 1000.0, () => Tick(h), label: "report server");
        }

        /// <summary>The reports of the unit (reports.dbf, compile order; the first of a name).</summary>
        internal static List<Rep> Reps(SimHost h)
        {
            var s = Of(h);
            if (s.Reps != null)
                return s.Reps;
            var o = new List<Rep>();
            s.Reps = o;
            if (h.World == null)
                return o;
            foreach (var (p, row) in h.World.Unit.Rows("reports.dbf"))
            {
                string name = CitectText.Trim(row.Get("NAME"));
                if (name.Length == 0 || o.Exists(x => C.IEq(x.Name, name)))
                    continue;
                var r = new Rep
                {
                    Name = name,
                    Cluster = CitectText.Trim(row.Get("CLUSTER")),
                    Trig = CitectText.Trim(row.Get("TRIG")),
                    Format = CitectText.Trim(row.Get("REPORT")),
                    Device = CitectText.Trim(row.Get("DEVICE")),
                    Priv = CitectText.Trim(row.Get("PRIV")),
                    Area = CitectText.Trim(row.Get("AREA")),
                    Dir = p.Dir,
                };
                Schedule(h, r, CitectText.Trim(row.Get("TIME")), CitectText.Trim(row.Get("PERIOD")));
                o.Add(r);
            }
            return o;
        }

        private static Rep? Find(SimHost h, string name)
        {
            foreach (var r in Reps(h))
                if (C.IEq(r.Name, name))
                    return r;
            return null;
        }

        /// <summary>The device handle of the report the task runs, -1.</summary>
        internal static long DeviceOf(CicodeTask t) =>
            t.Data.TryGetValue(RunKey, out object? o) && o is Run r ? r.Device : -1;

        private static Run? RunOf(CicodeTask t) =>
            t.Data.TryGetValue(RunKey, out object? o) && o is Run r ? r : null;

        // ------------------------------------------------------------------ schedules
        private static readonly string[] Days =
        {
            "sunday",
            "monday",
            "tuesday",
            "wednesday",
            "thursday",
            "friday",
            "saturday",
        };

        private static readonly string[] Months =
        {
            "january",
            "february",
            "march",
            "april",
            "may",
            "june",
            "july",
            "august",
            "september",
            "october",
            "november",
            "december",
        };

        private static readonly int[] MonthDays = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

        /// <summary>hh:mm:ss (also h:mm, or seconds) in seconds; -1 when it is not one.</summary>
        internal static long Hms(string s)
        {
            string[] p = s.Split(':');
            if (p.Length > 3)
                return -1;
            long v = 0;
            foreach (string x in p)
            {
                if (!long.TryParse(x.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long n))
                    return -1;
                v = v * 60 + n;
            }
            if (p.Length == 2)
                v *= 60;
            return v;
        }

        private static int NameIndex(string[] names, string word)
        {
            string w = CitectText.Lower(word.Trim());
            if (w.Length < 3)
                return -1;
            for (int i = 0; i < names.Length; i++)
                if (names[i].StartsWith(w, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private static readonly Regex DayOfMonth = new Regex(
            @"^(\d{1,2})(st|nd|rd|th)?(\s+([A-Za-z]+))?$",
            RegexOptions.CultureInvariant
        );

        /// <summary>The schedule of TIME and PERIOD (help 1094537, 1094539).</summary>
        private static void Schedule(SimHost h, Rep r, string time, string period)
        {
            if (time.Length == 0 && period.Length == 0)
            {
                r.Type = Event;
                return;
            }
            long sync = time.Length == 0 ? 0 : Hms(time);
            if (sync < 0)
            {
                Log(
                    h,
                    "report " + r.Name + ": TIME " + CitectText.Quote(time) + " is not hh:mm:ss: midnight"
                );
                sync = 0;
            }
            r.Sync = sync % 86400;
            r.Type = Daily;
            r.Period = 86400;
            if (period.Length == 0)
                return;
            long secs = Hms(period);
            if (secs >= 0)
            {
                r.Period = secs > 0 ? secs : 86400;
                return;
            }
            int day = NameIndex(Days, period);
            if (day >= 0)
            {
                r.Type = Weekly;
                r.Period = day;
                return;
            }
            var m = DayOfMonth.Match(period.Trim());
            if (m.Success)
            {
                int d = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!m.Groups[4].Success && d >= 1 && d <= 31)
                {
                    r.Type = Monthly;
                    r.Period = d;
                    return;
                }
                int month = m.Groups[4].Success ? NameIndex(Months, m.Groups[4].Value) : -1;
                if (month >= 0 && d >= 1 && d <= 31)
                {
                    r.Type = Yearly;
                    r.Period = DayOfYear(month, d);
                    return;
                }
            }
            Log(h, "report " + r.Name + ": PERIOD " + CitectText.Quote(period) + " is not understood: daily");
        }

        /// <summary>The day of the year (1 = 1 January) of a month (0 January) and day, in a
        /// year of 365 days.</summary>
        private static long DayOfYear(int month, int day)
        {
            long n = 0;
            for (int i = 0; i < month; i++)
                n += MonthDays[i];
            return n + Math.Min(day, MonthDays[month]);
        }

        private static DateTime Local(SimHost h, double unix) =>
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(
                    DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(unix * 1000)).UtcDateTime,
                    DateTimeKind.Utc
                ),
                h.TimeZone
            );

        private static double Unix(SimHost h, DateTime local)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            DateTime utc;
            try
            {
                utc = TimeZoneInfo.ConvertTimeToUtc(local, h.TimeZone);
            }
            catch (ArgumentException)
            {
                // a local time the clocks skip (summer time starts): the hour after
                utc = TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), h.TimeZone);
            }
            return (utc - DateTime.SpecifyKind(new DateTime(1970, 1, 1), DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>The first scheduled time after <paramref name="now"/> (Unix seconds); 0 for an
        /// event report.</summary>
        internal static double NextAfter(SimHost h, Rep r, double now)
        {
            if (r.Type == Event)
                return 0;
            DateTime local = Local(h, now);
            DateTime today = local.Date;
            TimeSpan sync = TimeSpan.FromSeconds(Math.Max(0, Math.Min(86399, r.Sync)));
            switch (r.Type)
            {
                case Weekly:
                    for (int d = 0; d <= 7; d++)
                    {
                        DateTime day = today.AddDays(d);
                        if ((int)day.DayOfWeek == (int)(((r.Period % 7) + 7) % 7) && day + sync > local)
                            return Unix(h, day + sync);
                    }
                    break;
                case Monthly:
                    for (int k = 0; k <= 13; k++)
                    {
                        DateTime first = new DateTime(today.Year, today.Month, 1).AddMonths(k);
                        int dd = (int)
                            Math.Max(1, Math.Min(r.Period, DateTime.DaysInMonth(first.Year, first.Month)));
                        DateTime at = first.AddDays(dd - 1) + sync;
                        if (at > local)
                            return Unix(h, at);
                    }
                    break;
                case Yearly:
                    for (int y = 0; y <= 2; y++)
                    {
                        long doy = Math.Max(1, Math.Min(365, r.Period));
                        int month = 0;
                        while (month < 11 && doy > MonthDays[month])
                            doy -= MonthDays[month++];
                        DateTime at = new DateTime(today.Year + y, month + 1, (int)doy) + sync;
                        if (at > local)
                            return Unix(h, at);
                    }
                    break;
                default:
                {
                    double period = Math.Max(1, r.Period);
                    DateTime anchor = today + sync;
                    double since = (local - anchor).TotalSeconds;
                    double k = Math.Floor(since / period) + 1;
                    return Unix(h, anchor) + k * period;
                }
            }
            return now + 86400;
        }

        // ------------------------------------------------------------------ the watch
        private static void Tick(SimHost h)
        {
            var s = Of(h);
            if (s.Off != null)
                return;
            try
            {
                double now = Now(h);
                foreach (var r in Reps(h))
                {
                    if (r.Requested)
                    {
                        r.Requested = false;
                        Begin(h, r, "startup");
                    }
                    if (r.Disabled)
                        continue;
                    if (r.Next > 0 && now >= r.Next)
                    {
                        // (an event report's one-time run, RepSetControl 1, does not look at
                        // its trigger: guess)
                        bool once = r.Type == Event;
                        r.Next = once ? 0 : NextAfter(h, r, now);
                        if (once || r.Trig.Length == 0 || Trigger(h, r) == true)
                            Due(h, r, "schedule");
                        continue;
                    }
                    if (r.Type != Event || r.Trig.Length == 0)
                        continue;
                    bool? v = Trigger(h, r);
                    if (v == null)
                        continue;
                    if (!r.TrigSeen)
                    {
                        r.TrigSeen = true;
                        if (s.Inhibit)
                        {
                            r.LastTrig = v.Value;
                            continue;
                        }
                    }
                    if (v.Value && !r.LastTrig)
                        Due(h, r, "trigger");
                    r.LastTrig = v.Value;
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                EngineNotes.Swallowed("Reports.Tick", e);
            }
        }

        private static void Due(SimHost h, Rep r, string why)
        {
            if (Begin(h, r, why) == ReportBusy)
                Log(h, "report " + r.Name + " is busy: its " + why + " run is skipped (error 278)");
        }

        /// <summary>The trigger's value now (a foreground evaluation); null when it does not
        /// compile or run.</summary>
        private static bool? Trigger(SimHost h, Rep r)
        {
            var vm = Of(h).Vm!;
            if (!r.TrigTried)
            {
                r.TrigTried = true;
                var pools = new LoosePools();
                byte[]? code = Compile(h, r.Trig, PageFieldKind.Expression, pools, out string? err, out _);
                if (code == null)
                    Log(
                        h,
                        "report "
                            + r.Name
                            + ": TRIG "
                            + CitectText.Quote(r.Trig)
                            + " does not compile: "
                            + err
                    );
                else
                {
                    r.TrigCode = code;
                    r.TrigProgram = pools.ToProgram();
                }
            }
            if (r.TrigCode == null)
                return null;
            var t = vm.TaskForBlock(r.TrigCode, r.TrigProgram!, true, "Report " + r.Name + " trigger");
            if (r.Cluster.Length > 0)
                t.Data["cluster"] = r.Cluster;
            try
            {
                vm.Run(t, 100);
            }
            catch (InvalidOperationException) { }
            if (t.State != TaskState.Stop || t.Result == null)
                return null;
            return A.I(t.Result.Value) != 0;
        }

        // ------------------------------------------------------------------ compiling
        private static (CompileUnit Unit, TagTable Tags) Source(SimHost h)
        {
            if (h.ReportSource != null)
                return h.ReportSource();
            var s = Of(h);
            if (s.Own == null)
            {
                var unit = Units.ForProject(h.World!.Unit.RootDir).Compile();
                s.Own = (unit, TagTable.For(unit));
            }
            return s.Own.Value;
        }

        /// <summary>A field (an expression the way a page shows it) or a block (statements),
        /// compiled; null and why when it does not compile.</summary>
        private static byte[]? Compile(
            SimHost h,
            string text,
            PageFieldKind kind,
            LoosePools pools,
            out string? error,
            out object? node,
            Dictionary<string, VarRef>? vars = null,
            bool declare = false
        )
        {
            error = null;
            node = null;
            try
            {
                var (unit, tags) = Source(h);
                lock (unit)
                {
                    string project = h.World!.Unit.Project;
                    var res =
                        declare && vars != null
                            ? unit.CompileReportBlock(text, vars, project)
                            : unit.CompileField(
                                text,
                                kind == PageFieldKind.Command ? "command" : "expression",
                                project,
                                vars: vars
                            );
                    if (res.Errors.Count > 0)
                    {
                        error = res.Errors[0].Name + " " + res.Errors[0].Message;
                        return null;
                    }
                    node = res.Node;
                    var fc = FunctionGen.PageField(unit, tags, res.Node, kind);
                    foreach (var ins in fc.Code.Ins)
                        if ((ins.Op == Op.Load || ins.Op == Op.Store) && ins.Ref is TagKey tk)
                            pools.Tag(tk);
                    return fc.Code.Encode(pools);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                error = e.Message;
                return null;
            }
        }

        /// <summary>The format file of a report: REPORT (no extension: .RPT), a path
        /// substitution, else in the folder of the project that defines the report, else the
        /// compiled project's folder.</summary>
        private static string? FormatPath(SimHost h, Rep r)
        {
            string name = r.Format;
            if (name.Length == 0)
                return null;
            if (Path.GetExtension(name).Length == 0)
                name += ".RPT";
            if (name.TrimStart().StartsWith("[", StringComparison.Ordinal))
                name = Misc.PathOf(h, name);
            if (Path.IsPathRooted(name))
                return WinPath.IsFile(name) ? name : null;
            foreach (string dir in new[] { r.Dir, h.World!.Unit.RootDir })
            {
                string p = WinPath.Join(dir, name);
                if (WinPath.IsFile(p))
                    return p;
            }
            return null;
        }

        private static FormatFile? FormatOf(SimHost h, Rep r, out string why)
        {
            why = "";
            string? path = FormatPath(h, r);
            if (path == null)
            {
                why = "its format file " + CitectText.Quote(r.Format) + " is not found";
                return null;
            }
            long stamp = WinPath.Stat(path)?.mtime ?? 0;
            if (
                r.Compiled != null
                && WinPath.Key(r.Compiled.Path) == WinPath.Key(path)
                && r.Compiled.Stamp == stamp
            )
                return r.Compiled;
            string text;
            try
            {
                text = Cp1252.Decode(File.ReadAllBytes(path));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                why = "its format file cannot be read: " + e.Message;
                return null;
            }
            bool rtf =
                string.Equals(Path.GetExtension(path), ".rtf", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("{\\rtf", StringComparison.Ordinal);
            var f = new FormatFile
            {
                Path = path,
                Stamp = stamp,
                Rtf = rtf,
                Segs = Parse(text, rtf),
            };
            var pools = new LoosePools();
            // an ASCII report declares its variables at the start of its first block, the file's
            // first piece (help 1094538: not in an RTF or HTML report, whose block then does not
            // compile)
            var vars = new Dictionary<string, VarRef>(StringComparer.Ordinal);
            string ext = Path.GetExtension(path);
            bool ascii =
                !rtf
                && !string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase);
            Seg? first = f.Segs.Find(x => x.Kind != Kind.Text || x.Text.Trim().Length > 0);
            foreach (var seg in f.Segs)
            {
                if (seg.Kind != Kind.Field && seg.Kind != Kind.Code)
                    continue;
                var kind = seg.Kind == Kind.Field ? PageFieldKind.Display : PageFieldKind.Command;
                seg.Code = Compile(
                    h,
                    seg.Text,
                    kind,
                    pools,
                    out seg.Error,
                    out object? node,
                    vars,
                    ascii && seg == first && seg.Kind == Kind.Code
                );
                if (seg.Error != null)
                    Log(
                        h,
                        "report "
                            + r.Name
                            + ": "
                            + (seg.Kind == Kind.Field ? "field " : "block ")
                            + CitectText.Quote(Short(seg.Text))
                            + " does not compile: "
                            + seg.Error
                    );
                if (node is Compiler.Syntax.Format fmt && fmt.Spec.Width > 0 && fmt.Spec.Width < 1000)
                    seg.Width = (int)fmt.Spec.Width;
            }
            f.Program = pools.ToProgram();
            for (int i = 0; i < pools.Vars.Count; i++)
                if (
                    pools.Vars[i].Decl is VarDecl d
                    && vars.TryGetValue(Lexer.NameKey(d.Name), out var v)
                    && v == pools.Vars[i]
                )
                    f.Vars.Add((pools.CodeVar(v), d));
            r.Compiled = f;
            return f;
        }

        private static string Short(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 60 ? s.Substring(0, 60) + "..." : s;
        }

        // ------------------------------------------------------------------ format files
        /// <summary>The pieces of a format file (help 1094538): fixed text; {expr} or
        /// {expr:format} fields; {!comment}; {FF}; {ERR_FORMAT_OFF} / {ERR_FORMAT_ON};
        /// {CICODE} statements {END} (to the end of the file without {END}). An RTF file writes
        /// the braces escaped (\{ \}) and keeps its other markup as fixed text. A directive alone
        /// on its line takes the line with it.</summary>
        private static List<Seg> Parse(string src, bool rtf)
        {
            var o = new List<Seg>();
            var text = new StringBuilder();
            int i = 0,
                n = src.Length;

            void Flush()
            {
                if (text.Length > 0)
                    o.Add(new Seg { Kind = Kind.Text, Text = text.ToString() });
                text.Clear();
            }

            while (i < n)
            {
                int open = FindBrace(src, i, rtf, '{');
                if (open < 0)
                {
                    text.Append(src, i, n - i);
                    break;
                }
                int innerAt = open + (rtf ? 2 : 1);
                int close = FindBrace(src, innerAt, rtf, '}');
                if (close < 0)
                {
                    text.Append(src, i, n - i);
                    break;
                }
                text.Append(src, i, open - i);
                string raw = src.Substring(innerAt, close - innerAt);
                string inner = rtf ? RtfPlain(raw) : raw;
                int after = close + (rtf ? 2 : 1);
                string word = CitectText.Lower(inner.Trim());
                Seg? seg = null;
                bool directive = true;
                if (word.StartsWith("!", StringComparison.Ordinal))
                    seg = null;
                else if (word == "ff")
                    seg = new Seg { Kind = Kind.FormFeed };
                else if (word == "err_format_off")
                    seg = new Seg { Kind = Kind.ErrFormatOff };
                else if (word == "err_format_on")
                    seg = new Seg { Kind = Kind.ErrFormatOn };
                else if (word == "end")
                    seg = null;
                else if (word == "cicode")
                {
                    int end = FindEnd(src, after, rtf, out int endAfter);
                    string code = src.Substring(after, (end < 0 ? n : end) - after);
                    seg = new Seg { Kind = Kind.Code, Text = rtf ? RtfPlain(code) : code };
                    after = end < 0 ? n : endAfter;
                }
                else
                {
                    directive = false;
                    // a value right after a control word ('\cf4\{X\}'): a space ends the word
                    // (the compiled report text has it there; guess for the rule)
                    if (rtf && EndsWithControlWord(text))
                        text.Append(' ');
                    seg = new Seg
                    {
                        Kind = Kind.Field,
                        Text = inner.Trim(),
                        Width = inner.Length,
                    };
                }
                if (directive && !rtf && AloneOnLine(src, open, after, out int lineEnd))
                {
                    // the line goes: its blanks before the directive and its line break
                    int k = text.Length;
                    while (k > 0 && (text[k - 1] == ' ' || text[k - 1] == '\t'))
                        k--;
                    text.Length = k;
                    after = lineEnd;
                }
                if (seg != null)
                {
                    Flush();
                    o.Add(seg);
                }
                i = after;
            }
            Flush();
            return o;
        }

        /// <summary>The text ends with an RTF control word (\word or \word-12) without its
        /// delimiter.</summary>
        private static bool EndsWithControlWord(StringBuilder b)
        {
            int k = b.Length;
            while (k > 0 && char.IsDigit(b[k - 1]))
                k--;
            if (k > 0 && b[k - 1] == '-')
                k--;
            int letters = k;
            while (k > 0 && char.IsLetter(b[k - 1]))
                k--;
            if (k == letters || k == 0 || b[k - 1] != '\\')
                return false;
            int slashes = 0;
            for (int j = k - 1; j >= 0 && b[j] == '\\'; j--)
                slashes++;
            return slashes % 2 == 1;
        }

        /// <summary>The next brace of a field (RTF: '\{' or '\}', a '\\' skipped), -1.</summary>
        private static int FindBrace(string s, int from, bool rtf, char brace)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (!rtf)
                {
                    if (s[i] == brace)
                        return i;
                    continue;
                }
                if (s[i] != '\\' || i + 1 >= s.Length)
                    continue;
                if (s[i + 1] == brace)
                    return i;
                i++; // an escaped character or a control word's first letter
            }
            return -1;
        }

        /// <summary>The {END} after a {CICODE} block: its start (-1 none) and the index after it.</summary>
        private static int FindEnd(string s, int from, bool rtf, out int after)
        {
            after = -1;
            int i = from;
            while (true)
            {
                int open = FindBrace(s, i, rtf, '{');
                if (open < 0)
                    return -1;
                int innerAt = open + (rtf ? 2 : 1);
                int close = FindBrace(s, innerAt, rtf, '}');
                if (close < 0)
                    return -1;
                string inner = s.Substring(innerAt, close - innerAt);
                if (CitectText.Lower((rtf ? RtfPlain(inner) : inner).Trim()) == "end")
                {
                    after = close + (rtf ? 2 : 1);
                    return open;
                }
                i = innerAt;
            }
        }

        /// <summary>Only blanks before <paramref name="open"/> on its line and after
        /// <paramref name="after"/> up to the line break; <paramref name="lineEnd"/> = the index
        /// after that line break.</summary>
        private static bool AloneOnLine(string s, int open, int after, out int lineEnd)
        {
            lineEnd = after;
            for (int k = open - 1; k >= 0 && s[k] != '\n'; k--)
                if (s[k] != ' ' && s[k] != '\t')
                    return false;
            int j = after;
            while (j < s.Length && (s[j] == ' ' || s[j] == '\t'))
                j++;
            if (j < s.Length && s[j] == '\r')
                j++;
            if (j < s.Length && s[j] == '\n')
                j++;
            else if (j < s.Length)
                return false;
            lineEnd = j;
            return true;
        }

        /// <summary>RTF text as plain text: \par and \line a line break, \tab a tab, \'hh the
        /// character, \\ \{ \} the character; other control words and group braces go.</summary>
        internal static string RtfPlain(string s)
        {
            var b = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{' || c == '}' || c == '\r' || c == '\n')
                    continue;
                if (c != '\\' || i + 1 >= s.Length)
                {
                    b.Append(c);
                    continue;
                }
                char d = s[i + 1];
                if (d == '\\' || d == '{' || d == '}')
                {
                    b.Append(d);
                    i++;
                    continue;
                }
                if (
                    d == '\''
                    && i + 3 < s.Length
                    && int.TryParse(
                        s.Substring(i + 2, 2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out int hex
                    )
                )
                {
                    b.Append(Cp1252.Decode(new[] { (byte)hex }));
                    i += 3;
                    continue;
                }
                if (!char.IsLetter(d))
                {
                    i++;
                    continue;
                }
                int j = i + 1;
                while (j < s.Length && char.IsLetter(s[j]))
                    j++;
                string word = s.Substring(i + 1, j - i - 1);
                if (j < s.Length && s[j] == '-')
                    j++;
                while (j < s.Length && char.IsDigit(s[j]))
                    j++;
                if (j < s.Length && s[j] == ' ')
                    j++;
                if (word == "par" || word == "line")
                    b.Append('\n');
                else if (word == "tab")
                    b.Append('\t');
                i = j - 1;
            }
            return b.ToString();
        }

        private static string RtfEscape(string s) =>
            s.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");

        // ------------------------------------------------------------------ running
        /// <summary>Start a run of the report: 0, or 278 when it runs already.</summary>
        private static int Begin(SimHost h, Rep r, string why)
        {
            var s = Of(h);
            if (r.Running != null)
                return ReportBusy;
            var f = FormatOf(h, r, out string notFound);
            if (f == null)
            {
                Log(h, "report " + r.Name + " does not run: " + notFound);
                return 0;
            }
            var run = new Run
            {
                Rep = r,
                Format = f,
                Cluster = ClusterOf(h, r),
            };
            if (r.Device.Length > 0)
            {
                bool roll = Param(h, f.Rtf ? "RtfRollOver" : "TxtRollOver", f.Rtf ? "0" : "1") != "0";
                run.Device = Devices.OpenReport(
                    h,
                    r.Device,
                    roll,
                    h.World!.Clock.NowFiletime(),
                    out string dev
                );
                if (run.Device < 0)
                    Log(h, "report " + r.Name + ": its output is not written: " + dev);
            }
            Fresh(Of(h).Vm!, f);
            r.Running = run;
            r.Runs++;
            Log(h, "report " + r.Name + " runs (" + why + ")");
            Advance(h, run);
            return 0;
        }

        /// <summary>The report's variables at their declared values (0, "" without one).</summary>
        private static void Fresh(CicodeVm vm, FormatFile f)
        {
            foreach (var (offset, decl) in f.Vars)
            {
                if (!f.Program.CodeVars.TryGetValue(offset, out var cv))
                    continue;
                var slot = vm.Slot(f.Program, cv);
                slot.Reset(f.Program.RunImage);
                for (int k = 0; k < decl.Init.Count && k < slot.Count; k++)
                {
                    var lit = decl.Init[k];
                    slot.Set(
                        k,
                        lit.Kind == "STRING" ? Value.Str(lit.SValue ?? "")
                            : lit.Kind == "REAL" ? Value.Real(lit.RValue)
                            : Value.Long(lit.IValue)
                    );
                }
            }
        }

        private static string ClusterOf(SimHost h, Rep r)
        {
            if (r.Cluster.Length > 0)
                return r.Cluster;
            foreach (var sv in Servers.All(h))
                if (sv.Type == "Report" && sv.Cluster.Length > 0)
                    return sv.Cluster;
            var cl = Servers.Clusters(h);
            return cl.Count > 0 ? cl[0] : "";
        }

        private static void Write(SimHost h, Run run, string text)
        {
            if (run.Device >= 0 && text.Length > 0)
                Devices.ReportAppend(h, run.Device, text);
        }

        /// <summary>Write the pieces up to the next field or block, and start its task; the
        /// run ends after the last piece.</summary>
        private static void Advance(SimHost h, Run run)
        {
            var vm = Of(h).Vm!;
            var segs = run.Format.Segs;
            while (run.Next < segs.Count)
            {
                var seg = segs[run.Next++];
                switch (seg.Kind)
                {
                    case Kind.Text:
                        Write(h, run, h.World!.Display.Language.Localise(seg.Text));
                        continue;
                    case Kind.FormFeed:
                        Write(h, run, "\f");
                        continue;
                    case Kind.ErrFormatOn:
                        run.ErrFormat = true;
                        continue;
                    case Kind.ErrFormatOff:
                        run.ErrFormat = false;
                        continue;
                }
                if (seg.Code == null)
                {
                    if (seg.Kind == Kind.Field)
                        Field(h, run, seg, null);
                    continue;
                }
                var t = vm.TaskForBlock(seg.Code, run.Format.Program, false, "Report " + run.Rep.Name);
                t.Data[RunKey] = run;
                if (run.Cluster.Length > 0)
                    t.Data["cluster"] = run.Cluster;
                run.Task = t;
                run.Seg = seg;
                try
                {
                    vm.Scheduler!.Add(t);
                }
                catch (HardwareError e)
                {
                    Log(h, "report " + run.Rep.Name + " stops: " + e.Message);
                    break;
                }
                if (!t.Done)
                    return;
                run.Task = null;
                if (!Ended(h, run, seg, t))
                    break;
            }
            Finish(h, run);
        }

        /// <summary>A report task ended: its field written, then the run goes on.</summary>
        private static void Ended(SimHost h, CicodeTask t)
        {
            var run = RunOf(t);
            if (run == null || run.Task != t || run.Seg == null)
                return;
            run.Task = null;
            try
            {
                if (Ended(h, run, run.Seg, t))
                    Advance(h, run);
                else
                    Finish(h, run);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                EngineNotes.Swallowed("Reports.Ended", e);
                Finish(h, run);
            }
        }

        /// <summary>A field's or block's task ended: false when the report stops (a block halted).</summary>
        private static bool Ended(SimHost h, Run run, Seg seg, CicodeTask t)
        {
            if (seg.Kind == Kind.Field)
            {
                bool ok = t.State == TaskState.Stop && t.Result != null;
                if (!ok)
                    Log(
                        h,
                        "report "
                            + run.Rep.Name
                            + ": field "
                            + CitectText.Quote(Short(seg.Text))
                            + " did not end ("
                            + (t.HaltReason.Length > 0 ? t.HaltReason : t.StateName)
                            + ")"
                    );
                Field(h, run, seg, ok ? t.Result : null);
                return true;
            }
            if (t.State == TaskState.Halt)
            {
                Log(
                    h,
                    "report "
                        + run.Rep.Name
                        + " stops: its Cicode halted ("
                        + (t.HaltReason.Length > 0 ? t.HaltReason : "error " + W.Inv(t.HaltCode))
                        + ")"
                );
                return false;
            }
            return true;
        }

        /// <summary>Write a field's value at its width (null: it did not compile or run).</summary>
        private static void Field(SimHost h, Run run, Seg seg, Value? v)
        {
            string text;
            if (v == null)
                text = run.ErrFormat ? "#ERR" : "";
            else
            {
                Value x = v.Value;
                text = x.T == Types.String ? x.S ?? "" : Conv.ValueText(x, out _);
            }
            if (text.Length < seg.Width)
                text = text.PadRight(seg.Width);
            Write(h, run, run.Format.Rtf ? RtfEscape(text) : text);
        }

        private static void Finish(SimHost h, Run run)
        {
            if (run.Device >= 0)
                Devices.CloseReport(h, run.Device, h.World!.Clock.NowFiletime());
            run.Rep.Running = null;
            run.Task = null;
            Log(h, "report " + run.Rep.Name + " ended");
        }

        // ------------------------------------------------------------------ the built-ins
        /// <summary>The report a name names ('Cluster.Name' or Name, in the cluster asked), null.</summary>
        private static Rep? Named(SimHost h, string name, string cluster)
        {
            name = CitectText.Trim(name);
            cluster = CitectText.Trim(cluster);
            int dot = name.IndexOf('.');
            if (
                cluster.Length == 0
                && dot > 0
                && Servers.Clusters(h).Exists(x => C.IEq(x, name.Substring(0, dot)))
            )
            {
                cluster = name.Substring(0, dot);
                name = name.Substring(dot + 1);
            }
            var r = Find(h, name);
            if (r == null)
                return null;
            if (cluster.Length > 0 && r.Cluster.Length > 0 && !C.IEq(cluster, r.Cluster))
                return null;
            if (cluster.Length > 0 && !Servers.Clusters(h).Exists(x => C.IEq(x, cluster)))
                return null;
            return r;
        }

        private static long Number(SimHost h, string text)
        {
            if (text.Length == 0)
                return 0;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                return v;
            return h.World?.Unit.LabelValue(text) ?? 0;
        }

        [Builtin(
            "Report",
            Doc = "1189291; 1094539; 1094537; 1094538",
            Blocking = true,
            Effects = "starts a run of the report: its format file written to its device (a file of the sandbox)",
            Errors = "282 in foreground code (returned); 280 no such report, not in the cluster asked, or the user lacks its privilege or area (returned); 278 the report is running (returned); 281 the project has no report server or [Report]Disable is 1 (returned; guess)",
            Notes = "ReportName may be 'Cluster.Name'. The request waits one turn; the run starts then (guess) and goes on in tasks of its own (named 'Report <name>', guess), the caller does not wait for it. The report server (a repsrv.dbf server and [Report]Disable not 1; a simulation starts it with its servers, else the first Report / RepGetControl / RepSetControl call) looks every [Report]WatchTime ms (default 1000): a daily report runs at TIME + k * PERIOD (hh:mm:ss; no PERIOD: every 24 h; no TIME: midnight), a weekly (Monday), monthly (15th) or yearly (25th June) one on that day at TIME, the first time after the server starts; with a TRIG too, only when the trigger is true then; a report with TRIG alone runs when it becomes true ([Report]InhibitEvent 1, the default: not at the server's first look); the report [Report]Startup (default 'Startup') runs at the first look. Times missed between two looks run once (guess); a run due while the report runs is skipped (guess); [Report]ComBreak is not simulated (no communication breaks). A run: the format file (REPORT; no extension = .RPT; in the defining project's folder, else the compiled project's) read at the run (compiled again when it changed); fixed text written as it is, '@(text)' translated as the display does (guess); a field {expr} or {expr:format} is the expression shown as a page shows it (no format: the last tag's format, guess), padded with blanks to its width: the characters between the braces, or the format's width (longer values are not cut, guess); a field that does not compile or halts writes #ERR, after {ERR_FORMAT_OFF} blanks (guess); {!comment} nothing; {FF} a form feed; a {CICODE} block runs its statements (Print / PrintLn: DevPrint to DevCurr, Include's labels), a block that halts ends the run (guess); a directive alone on its line takes the line with it (guess, as the help's examples print). An RTF file (.rtf) writes its fields between \\{ and \\} (the value's \\ { } escaped, guess; a value right after a control word gets a space before it, guess), its other markup as fixed text, its blocks as plain text. An ASCII report (not .rtf, .htm, .html) that starts with a {CICODE} block may declare variables at its start (INT n; STRING s = \"x\"; arrays too): the report's variables, seen by its later blocks and fields, at their declared values (0, \"\") at the start of each run; in an RTF or HTML report such a block does not compile (logged; guess for the runtime's handling). The device (DEVICE) opens for the run as plain text (the device's format ignored) and closes at its end: [Report]TxtRollOver (default 1) / RtfRollOver (default 0) 1 makes the file there the device's first history file before the run, 0 adds to its end (both for every run, guess); a dBASE or SQL device takes no output in the simulator. A disabled report runs on Report() (guess)."
        )]
        private static Value Report(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            Start(h, t.Vm);
            if (Of(h).Off != null)
                return W.I(NoServer);
            var r = Named(h, A.S(a.Get(0)), a.Count > 1 ? A.S(a.Get(1)) : "");
            if (r == null)
                return W.I(InvalidReport);
            int priv = (int)Number(h, r.Priv),
                area = (int)Number(h, r.Area);
            var users = h.World?.Users;
            if ((priv > 0 || area > 0) && users != null && !users.CanOperate(priv, area))
                return W.I(InvalidReport);
            return W.BlockOneTurn(t, "Report", () => W.I(Begin(h, r, "Report()")));
        }

        [Builtin(
            "RepGetControl",
            Doc = "1189290",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1; guess); 280 no such report (raised, -1; guess); 274 a type other than 0..5 (raised, -1; guess)",
            Notes = "Type 0: 3 while the report runs, else 0 (it never waits for I/O data); 1: the time of the next scheduled run (a Cicode time, guess; 0 for an event report without one); 2: the period (daily: seconds; weekly: the day, 0 Sunday; monthly: the day of the month; yearly: the day of the year in a year of 365 days, 1 = 1 January, guess from RepSetControl's example); 3: the synchronisation time (seconds from midnight); 4: the schedule (0 event, 1 daily, 2 weekly, 3 monthly, 4 yearly); 5: 1 disabled, 0 enabled. A report without TIME and PERIOD is an event report (also one with neither and no TRIG: Report() runs it); a PERIOD alone is synchronised at midnight. The server starts if it has not (as Report). Waits one turn."
        )]
        private static Value RepGetControl(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground)
                return Fail(t, Codes.FgBlock);
            Start(h, t.Vm);
            var r = Named(h, A.S(a.Get(0)), a.Count > 2 ? A.S(a.Get(2)) : "");
            if (r == null)
                return Fail(t, InvalidReport);
            long type = A.I(a.Get(1));
            if (type < 0 || type > 5)
                return Fail(t, Codes.InvalidArg);
            return W.BlockOneTurn(t, "RepGetControl", () =>
                W.I(
                    type switch
                    {
                        0 => r.Running != null ? 3 : 0,
                        1 => (long)Math.Floor(r.Next),
                        2 => r.Period,
                        3 => r.Sync,
                        4 => r.Type,
                        _ => r.Disabled ? 1 : 0,
                    }
                )
            );
        }

        private static Value Fail(CicodeTask t, int code)
        {
            W.Raise(t, code);
            return W.I(-1);
        }

        [Builtin(
            "RepSetControl",
            Doc = "1189292",
            Blocking = true,
            Effects = "changes the report's schedule for the simulation (reports.dbf is not written)",
            Errors = "282 in foreground code (returned); 280 no such report (returned; guess); 274 a type other than 1..5, or a schedule other than 0..4 (returned; guess)",
            Notes = "Type 1: the next run at Data (a Cicode time), later runs from the period; 2: the period (as RepGetControl 2); 3: the synchronisation time (seconds from midnight); 4: the schedule (0 event .. 4 yearly); 5: 1 disables, 0 enables. After 2, 3, 4 or an enable the next run is computed again from now (an event report has none). A one-time run of an event report does not look at its trigger (guess). The server starts if it has not (as Report). Waits one turn; 0."
        )]
        private static Value RepSetControl(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            var r = Named(h, A.S(a.Get(0)), a.Count > 3 ? A.S(a.Get(3)) : "");
            if (r == null)
                return W.I(InvalidReport);
            long type = A.I(a.Get(1));
            long data = A.I(a.Get(2));
            if (type < 1 || type > 5 || (type == 4 && (data < 0 || data > 4)))
                return W.I(Codes.InvalidArg);
            Start(h, t.Vm);
            double now = h.World != null ? Now(h) : 0;
            switch (type)
            {
                case 1:
                    r.Next = data;
                    break;
                case 2:
                    r.Period = data;
                    break;
                case 3:
                    r.Sync = ((data % 86400) + 86400) % 86400;
                    break;
                case 4:
                    r.Type = (int)data;
                    break;
                default:
                    r.Disabled = data != 0;
                    break;
            }
            if (type != 1 && !r.Disabled && h.World != null)
                r.Next = r.Type == Event ? 0 : NextAfter(h, r, now);
            return W.BlockOneTurn(t, "RepSetControl", () => W.I(0));
        }

        [Builtin(
            "RepGetCluster",
            Doc = "1189289",
            Notes = "In a report's task: the report's CLUSTER, else the report server's cluster, else the project's first cluster. Outside a report: \"\" (guess)."
        )]
        private static Value RepGetCluster(SimHost h, CicodeTask t, ArgList a)
        {
            var run = RunOf(t);
            return W.S(run == null ? "" : run.Cluster);
        }

        [Builtin(
            "PrintFont",
            Doc = "1076402",
            Errors = "269 with the halt flag: the task runs no report with a device (returned; guess)",
            Notes = "In a report with a device: 0, the font changes nothing (the help: no effect on an ASCII or dBASE device; a printer device spools text in the simulator)."
        )]
        private static Value PrintFont(SimHost h, CicodeTask t, ArgList a)
        {
            if (DeviceOf(t) >= 0)
                return W.I(0);
            t.RaiseError(Codes.BadHandle, true);
            return W.I(Codes.BadHandle);
        }
    }
}
