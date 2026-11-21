using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>One alarm event of the World's sequence of events (the transitions AlarmDb
    /// reports: on, off, ack, clear, state, disable, enable).</summary>
    public sealed class AlarmEvent
    {
        public long Seq;
        public double T;
        public AlarmDef Def = null!;
        public string Event = "";
        public long State;
        public string? Sub;
        public string By = "";
    }

    /// <summary>One alarm summary entry: an ON occurrence of an alarm with its OFF and
    /// acknowledge times (-1 = not yet).</summary>
    public sealed class AlarmSummaryEntry
    {
        public long Seq;
        public AlarmDef Def = null!;
        public double OnT,
            OffT = -1,
            AckT = -1;
        public string By = "";
    }

    /// <summary>The sequence of events and the alarm summary of a World (alarm lists of types 10,
    /// 15, 16): the transitions from the attach on (the page animator attaches it when it is
    /// made), at most <see cref="Max"/> of each (the oldest dropped).</summary>
    public sealed class AlarmEventLog
    {
        private static readonly ConditionalWeakTable<World.World, AlarmEventLog> All =
            new ConditionalWeakTable<World.World, AlarmEventLog>();
        public const int Max = 10000;
        public readonly List<AlarmEvent> Events = new List<AlarmEvent>();
        public readonly List<AlarmSummaryEntry> Summary = new List<AlarmSummaryEntry>();
        private long _seq;
        private readonly World.World _world;

        private AlarmEventLog(World.World w)
        {
            _world = w;
            w.Alarms.Listeners.Add(OnTransition);
        }

        public static AlarmEventLog Attach(World.World w) => All.GetValue(w, x => new AlarmEventLog(x));

        public static AlarmEventLog? Of(World.World w) => All.TryGetValue(w, out var l) ? l : null;

        /// <summary>Changes on every event.</summary>
        public long Serial => _seq;

        /// <summary>A new sequence number for an entry made outside a transition (AlarmSumAppend,
        /// AlarmSumSplit, AlarmSplit); it changes the Serial too.</summary>
        public long NextSeq() => ++_seq;

        private void OnTransition(Transition tr)
        {
            var a = _world.Alarms.Get(tr.Tag);
            if (a == null)
                return;
            long seq = ++_seq;
            Events.Add(
                new AlarmEvent
                {
                    Seq = seq,
                    T = tr.T,
                    Def = a,
                    Event = tr.Event,
                    State = tr.State,
                    Sub = tr.Sub,
                    By = tr.By,
                }
            );
            if (Events.Count > Max)
                Events.RemoveRange(0, Events.Count - Max);
            switch (tr.Event)
            {
                case "on":
                    Summary.Add(
                        new AlarmSummaryEntry
                        {
                            Seq = seq,
                            Def = a,
                            OnT = tr.T,
                            By = tr.By,
                        }
                    );
                    if (Summary.Count > Max)
                        Summary.RemoveRange(0, Summary.Count - Max);
                    break;
                case "off":
                case "ack":
                    for (int i = Summary.Count - 1; i >= 0; i--)
                    {
                        var e = Summary[i];
                        if (e.Def != a)
                            continue;
                        if (tr.Event == "off" && e.OffT < 0)
                            e.OffT = tr.T;
                        if (tr.Event == "ack" && e.AckT < 0)
                            e.AckT = tr.T;
                        break;
                    }
                    break;
                case "set":
                    SetSummary(a, tr);
                    break;
            }
        }

        /// <summary>A state set directly (AlarmDb.SetState: a live case's frame, a scenario's
        /// setAlarm): the summary follows the alarm's state and its times; a listed alarm with no
        /// open entry gets one (its OnTime, its OffTime when OFF, its AckTime when acknowledged).</summary>
        private void SetSummary(AlarmDef a, Transition tr)
        {
            var st = _world.Alarms.PeekState(a);
            if (st == null)
                return;
            AlarmSummaryEntry? open = null;
            for (int i = Summary.Count - 1; i >= 0; i--)
            {
                if (Summary[i].Def != a)
                    continue;
                if (Summary[i].OffT < 0 || Summary[i].AckT < 0)
                    open = Summary[i];
                break;
            }
            if (open == null)
            {
                if (!st.InList)
                    return;
                Summary.Add(
                    new AlarmSummaryEntry
                    {
                        Seq = _seq,
                        Def = a,
                        OnT = st.OnTime,
                        By = tr.By,
                        OffT = st.On ? -1 : st.OffTime,
                        AckT = st.Ack ? st.AckTime : -1,
                    }
                );
                if (Summary.Count > Max)
                    Summary.RemoveRange(0, Summary.Count - Max);
                return;
            }
            if (!st.On && open.OffT < 0)
                open.OffT = st.OffTime;
            if (st.Ack && open.AckT < 0)
                open.AckT = st.AckTime;
        }
    }

    /// <summary>Alarm lists on pages: the classic AlarmDsp lists and the SA lists.</summary>
    public static class AlarmLists
    {
        /// <summary>One row of a list: an alarm (with its state), a hardware alarm, an event or a
        /// summary entry.</summary>
        internal sealed class Row
        {
            public AlarmDef? Def;
            public AlarmState? St;
            public HardwareAlarm? Hw;
            public AlarmEvent? Ev;
            public AlarmSummaryEntry? Sum;

            public object Key => (object?)Ev ?? (object?)Sum ?? (object?)Hw ?? Def!;
        }

        private static DisplayState D(SimHost h) => DisplayBuiltins.Of(h);

        private static PageObjectTables Tables(SimHost h) => PageObjectTables.Of(D(h));

        private static Window? Win(SimHost h, CicodeTask t) => D(h).OpenWindow(D(h).WindowOf(t));

        internal static bool Hardware(int type) => (type >= 5 && type <= 9) || type == 13 || type == 14;

        internal static bool Events(int type) => type == 15 || type == 16;

        // ------------------------------------------------------------------ groups (category / priority filters)
        /// <summary>A value names a category / priority, or a group of them when it is an open
        /// group handle (bit 0x4000, UsersGroups); 0 = all.</summary>
        private static bool InSet(SimHost h, long set, long v)
        {
            if (set == 0)
                return true;
            if ((set & UsersGroups.HandleBit) != 0)
            {
                var g = ProcessData.Of(h).GroupTable.Get(set & 0xBFFF);
                if (g != null)
                    return v >= 0 && v < g.Has.Length && g.Has[v];
            }
            return set == v;
        }

        // ------------------------------------------------------------------ the rows
        /// <summary>Every row of the list's type and filters, in display order.</summary>
        internal static List<Row> Items(SimHost h, PageAlarmList l)
        {
            var w = h.World!;
            var db = w.Alarms;
            var o = new List<Row>();
            int type = l.Type;
            if (Hardware(type))
            {
                foreach (var hw in db.HardwareAlarms)
                    if (AlarmDb.Classify(hw.St, type))
                        o.Add(new Row { Hw = hw, St = hw.St });
                // newest first; at the same time the one raised later first
                o.Reverse();
                StableSort(o, (x, y) => y.St!.Time.CompareTo(x.St!.Time));
                return o;
            }
            string filter = l.CustomFilter;
            if (filter.Length == 0 && l.NamedFilter.Length > 0)
                ProcessData.Of(h).NamedFilters.TryGetValue(l.NamedFilter, out filter!);
            filter ??= "";
            var af = filter.Length > 0 ? new AlarmFilter(filter) : null;
            // category.dbf SHOW_ALM keeps a category off the active lists (help 1068445), and off
            // the configured list (type 4) when [Alarm]ShowAllConfigured is 0 (help 1057795,
            // default 1); SHOW_SUM keeps it off summary and event lists (help 1180291)
            bool activeKind =
                type == 0
                || type == 1
                || type == 2
                || type == 11
                || type == 12
                || type == 17
                || type == 18
                || (type == 4 && CitectText.Trim(Params.Get(h, "Alarm", "ShowAllConfigured") ?? "1") == "0");
            bool summaryKind = type == 10 || Events(type);
            // the flag once per category (a list's rows are read every scan)
            var shown = new Dictionary<long, bool>();
            bool Shown(long cat)
            {
                if (!shown.TryGetValue(cat, out bool v))
                    shown[cat] = v =
                        (!activeKind || db.ShownOnActive(cat)) && (!summaryKind || db.ShownOnSummary(cat));
                return v;
            }
            bool Keep(AlarmDef a)
            {
                if ((activeKind || summaryKind) && !Shown(a.Category))
                    return false;
                if (
                    l.Cluster.Length > 0
                    && a.Cluster.Length > 0
                    && !CitectText.SameName(a.Cluster, l.Cluster)
                )
                    return false;
                if (!InSet(h, l.Category, a.Category))
                    return false;
                if (!InSet(h, l.Priority, db.Priority(a)))
                    return false;
                return true;
            }
            if (type == 10 || Events(type))
            {
                var log = AlarmEventLog.Attach(w);
                if (type == 10)
                {
                    for (int i = log.Summary.Count - 1; i >= 0; i--)
                    {
                        var e = log.Summary[i];
                        if (Keep(e.Def) && (af == null || af.Match(f => FieldValue(h, db, e.Def)(f))))
                            o.Add(
                                new Row
                                {
                                    Def = e.Def,
                                    St = db.PeekState(e.Def),
                                    Sum = e,
                                }
                            );
                    }
                }
                else
                {
                    for (int i = log.Events.Count - 1; i >= 0; i--)
                    {
                        var e = log.Events[i];
                        if (type == 15 && (e.Event == "disable" || e.Event == "enable"))
                            continue;
                        if (Keep(e.Def) && (af == null || af.Match(f => FieldValue(h, db, e.Def)(f))))
                            o.Add(
                                new Row
                                {
                                    Def = e.Def,
                                    St = db.PeekState(e.Def),
                                    Ev = e,
                                }
                            );
                    }
                }
                return o;
            }
            foreach (var (a, st) in db.ListOf(type, filter, (a, _) => Keep(a)))
                o.Add(new Row { Def = a, St = st });
            // the alarms raised at the same time keep ListOf's order (priority, then definition)
            if (l.Last)
                StableSort(o, (x, y) =>
                    (y.St?.Time ?? double.NegativeInfinity).CompareTo(x.St?.Time ?? double.NegativeInfinity)
                );
            else if (l.SortMode == 1 && l.OrderBy.Length > 0)
                SortBy(h, o, l.OrderBy);
            return o;
        }

        /// <summary>Sort keeping the order of equal rows. List.Sort is an unstable introsort
        /// whose order of equal elements is not the same on .NET Framework and .NET (and
        /// alarms raised in the same scan have the same time).</summary>
        internal static void StableSort<T>(List<T> items, Comparison<T> cmp)
        {
            var keyed = new List<(T item, int i)>(items.Count);
            for (int i = 0; i < items.Count; i++)
                keyed.Add((items[i], i));
            keyed.Sort(
                (x, y) =>
                {
                    int c = cmp(x.item, y.item);
                    return c != 0 ? c : x.i.CompareTo(y.i);
                }
            );
            for (int i = 0; i < keyed.Count; i++)
                items[i] = keyed[i].item;
        }

        private static Func<string, AlarmFilter.FieldValue> FieldValue(SimHost h, AlarmDb db, AlarmDef a) =>
            f =>
            {
                string v = AlarmQueries.Field(h, db, a, f) ?? "";
                string fu = CitectText.Upper(f);
                if (
                    (fu == "CATEGORY" || fu == "PRIORITY" || fu == "AREA" || fu == "PRIV" || fu == "TYPENUM")
                    && long.TryParse(
                        v,
                        System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out long n
                    )
                )
                    return AlarmFilter.FieldValue.Num(n);
                return AlarmFilter.FieldValue.Str(v);
            };

        /// <summary>The order-by keys '{Key[,dir]}...' (dir 0 descending, the default; 1 ascending; doc 1033498).</summary>
        private static void SortBy(SimHost h, List<Row> rows, string keys)
        {
            var spec = new List<(string key, bool asc)>();
            foreach (string part in keys.Split('{'))
            {
                string p = part.Trim().TrimEnd('}').Trim();
                if (p.Length == 0)
                    continue;
                string[] kv = p.Split(',');
                spec.Add((CitectText.Trim(kv[0]), kv.Length > 1 && CitectText.Trim(kv[1]) == "1"));
            }
            if (spec.Count == 0)
                return;
            // each key once per row: a number (numbers before texts) or the upper-case text, so
            // the comparison is a total order
            var keyed = rows.ConvertAll(r =>
                (
                    r,
                    spec.ConvertAll(s =>
                    {
                        string v = FieldText(h, r, s.key) ?? "";
                        double d = 0;
                        bool num = v.Length > 0 && FloatText.TryParseNumber(v, out d) && !double.IsNaN(d);
                        return (num: num, d: num ? d : 0.0, text: num ? "" : CitectText.Upper(v));
                    })
                )
            );
            StableSort(keyed, (x, y) =>
            {
                for (int i = 0; i < spec.Count; i++)
                {
                    var a = x.Item2[i];
                    var b = y.Item2[i];
                    int c =
                        a.num != b.num ? (a.num ? -1 : 1)
                        : a.num ? a.d.CompareTo(b.d)
                        : string.CompareOrdinal(a.text, b.text);
                    if (c != 0)
                        return spec[i].asc ? c : -c;
                }
                return 0;
            });
            rows.Clear();
            foreach (var k in keyed)
                rows.Add(k.r);
        }

        /// <summary>The state of a row: unack_on | ack_on | unack_off | ack_off | disabled.</summary>
        internal static string StateName(AlarmState? st)
        {
            if (st == null)
                return "ack_off";
            if (st.Disabled)
                return "disabled";
            bool unack = st.InList && !st.Ack;
            return st.On ? (unack ? "unack_on" : "ack_on") : (unack ? "unack_off" : "ack_off");
        }

        /// <summary>SA's state summary (SA_Controls AlarmList.ci): 0 normal (acknowledged off), 1
        /// unacknowledged on, 2 unacknowledged off, 3 acknowledged on, 4 disabled.</summary>
        private static int SaState(AlarmState? st)
        {
            switch (StateName(st))
            {
                case "unack_on":
                    return 1;
                case "unack_off":
                    return 2;
                case "ack_on":
                    return 3;
                case "disabled":
                    return 4;
            }
            return 0;
        }

        private static string Time(SimHost h, double sim, int fmt)
        {
            long unix = (long)Math.Floor((h.World?.Clock.StartUnix ?? 0) + sim);
            return Builtins.Time.TimeToStr(h, null, unix, fmt, false);
        }

        /// <summary>A field of a row as the list shows it (AlarmGetDsp's fields, doc 1033478);
        /// null for a field the row does not know.</summary>
        internal static string? FieldText(SimHost h, Row r, string field)
        {
            string f = CitectText.Upper(CitectText.Trim(field));
            var w = h.World!;
            var db = w.Alarms;
            if (r.Hw != null)
            {
                var hw = r.Hw;
                switch (f)
                {
                    case "TAG":
                    case "NAME":
                        return hw.Name;
                    case "DESC":
                    case "ERRDESC":
                        return hw.Desc;
                    case "ERRPAGE":
                        return hw.Page;
                    case "CATEGORY":
                        return "255";
                    case "STATE":
                        return StateName(hw.St).Replace("_", " ").ToUpperInvariant();
                    case "TIME":
                        return Time(h, hw.St.Time, 1);
                    case "DATE":
                        return Time(h, hw.St.Time, 2);
                    case "COUNT":
                        return C.Inv(hw.Count);
                    case "RECNO":
                    case "RECORDID":
                        return "";
                    case "PRIORITY":
                        return "0";
                }
                return "";
            }
            var a = r.Def!;
            var st = r.St;
            if (r.Ev != null)
            {
                var e = r.Ev;
                switch (f)
                {
                    case "DATE":
                        return Time(h, e.T, 2);
                    case "TIME":
                        return Time(h, e.T, 1);
                    case "MESSAGE":
                        return MessageOf(e);
                    case "STATE":
                    case "LOGSTATE":
                        return EventState(e);
                    case "CLASSIFICATION":
                        return e.By == "alarm" ? "Alarm"
                            : e.By == "user" || e.By == "cicode" ? "Operator"
                            : "System";
                    case "USERNAME":
                        return e.By == "alarm" ? "" : w.Users.Session.Name;
                    case "USERLOCATION":
                        return "";
                    case "SUMMARYID":
                    case "RECORDID":
                        return C.Inv(e.Seq);
                }
            }
            if (r.Sum != null)
            {
                var s = r.Sum;
                switch (f)
                {
                    case "ONDATE":
                        return Time(h, s.OnT, 2);
                    case "ONTIME":
                        return Time(h, s.OnT, 1);
                    case "OFFDATE":
                        return s.OffT >= 0 ? Time(h, s.OffT, 2) : "";
                    case "OFFTIME":
                        return s.OffT >= 0 ? Time(h, s.OffT, 1) : "";
                    case "ACKDATE":
                        return s.AckT >= 0 ? Time(h, s.AckT, 2) : "";
                    case "ACKTIME":
                        return s.AckT >= 0 ? Time(h, s.AckT, 1) : "";
                    case "DELTATIME":
                        return s.OffT >= 0 ? C.Inv((long)Math.Floor(s.OffT - s.OnT)) : "";
                    case "SUMSTATE":
                        return s.OffT < 0 ? (s.AckT < 0 ? "ON" : "ON ACK") : (s.AckT < 0 ? "OFF" : "OFF ACK");
                    case "SUMMARYID":
                        return C.Inv(s.Seq);
                    case "FULLNAME":
                        return s.By == "alarm" ? "system" : w.Users.Session.FullName;
                }
            }
            switch (f)
            {
                case "RECORDID":
                    return C.Inv(a.Ordinal);
                case "SUMMARYID":
                    return "";
                case "STATENUMERIC":
                    return C.Inv(db.StateValue(a));
                case "SASTATEOFFSET":
                    return C.Inv(SaState(st));
                case "CLUSTER":
                    return a.Cluster.Length > 0 ? a.Cluster : FirstCluster(w);
                case "PRIORITYANDSTATE":
                    return C.Inv(db.Priority(a)) + "," + C.Inv(SaState(st));
                case "RESPONSENUM":
                    return "0";
                case "MESSAGE":
                    return "";
                case "USERNAME":
                case "USERLOCATION":
                case "CLASSIFICATION":
                    return "";
            }
            if (
                f.Length > 4
                && (
                    f.StartsWith("CAUSE", StringComparison.Ordinal)
                    || f.StartsWith("RESPONSE", StringComparison.Ordinal)
                    || f.StartsWith("RESPTIME", StringComparison.Ordinal)
                    || f.StartsWith("CONSEQUENCE", StringComparison.Ordinal)
                )
            )
                return "";
            return AlarmQueries.Field(h, db, a, f);
        }

        private static string FirstCluster(World.World w)
        {
            foreach (string c in w.Unit.Clusters())
                return c;
            return "";
        }

        private static string EventState(AlarmEvent e) =>
            e.Event switch
            {
                "on" => "ON",
                "off" => "OFF",
                "ack" => "ACK",
                "disable" => "DISABLED",
                "enable" => "ENABLED",
                "clear" => "CLEARED",
                _ => CitectText.Upper(e.Event),
            };

        private static string MessageOf(AlarmEvent e)
        {
            string d = e.Def.Desc.Length > 0 ? e.Def.Desc : e.Def.Name;
            return e.Event switch
            {
                "on" => "Alarm ON" + (d.Length > 0 ? ": " + d : ""),
                "off" => "Alarm OFF" + (d.Length > 0 ? ": " + d : ""),
                "ack" => "Alarm acknowledged",
                "disable" => "Alarm disabled",
                "enable" => "Alarm enabled",
                _ => CitectText.Upper(e.Event),
            };
        }

        // ------------------------------------------------------------------ formats and fonts
        /// <summary>A category.dbf format text with its '^t' / '^v' / '^n' / '^^' escapes turned
        /// into tab, 0x11, newline and '^' (the compiler's string escapes; guess for the alarm
        /// server's own reading).</summary>
        private static string Unescape(string s)
        {
            if (s.IndexOf('^') < 0)
                return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '^' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(
                        n == 't' || n == 'T' ? '\t'
                        : n == 'v' || n == 'V' ? '\u0011'
                        : n == 'n' || n == 'N' ? '\n'
                        : n
                    );
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>The category.dbf field of a list type's format: DSPFMT, SUMDSPFMT for the
        /// summary, SOEDSPFMT for event lists.</summary>
        internal static string FormatField(int type) =>
            type == 10 ? "SUMDSPFMT"
            : Events(type) ? "SOEDSPFMT"
            : "DSPFMT";

        /// <summary>A category's own format of a list type (unescaped), "" when it has none.</summary>
        internal static string OwnCategoryFormat(SimHost h, long category, int type)
        {
            var cats = h.World!.Alarms.Categories();
            return cats.TryGetValue(category, out var row)
                ? Unescape(CitectText.Trim(row.Get(FormatField(type))))
                : "";
        }

        /// <summary>The default format of a list type when no category gives one (help
        /// 1068445): [Alarm]DefDspFmt / DefSumFmt / DefSOEFmt, else the help's defaults.</summary>
        internal static string DefaultFormat(SimHost h, int type)
        {
            string p =
                type == 10 ? "DefSumFmt"
                : Events(type) ? "DefSOEFmt"
                : "DefDspFmt";
            string v = CitectText.Trim(Params.Get(h, "Alarm", p) ?? "");
            if (v.Length > 0)
                return Unescape(v);
            return type == 10 ? "{Name,20} {OnTime,8} {OffTime,8} {DeltaTime,8} {Comment,22}"
                : Events(type)
                    ? "{DATE,16} {TIME,16} {TAG,10} {NAME,15} {MESSAGE,64} {STATE,16} {CLASSIFICATION,13} {USERNAME,16} {USERLOCATION,16}"
                : "{Time,12} {Tag,10} {Name,20} {Desc,32}";
        }

        /// <summary>The display format of a category for a list type (DSPFMT; SUMDSPFMT for the
        /// summary, SOEDSPFMT for event lists); category 0's when the category has none, then
        /// the default (DefaultFormat).</summary>
        internal static string CategoryFormat(SimHost h, long category, int type)
        {
            string v = OwnCategoryFormat(h, category, type);
            if (v.Length == 0 && category != 0)
                v = OwnCategoryFormat(h, 0, type);
            return v.Length > 0 ? v : DefaultFormat(h, type);
        }

        /// <summary>Column header titles for AlarmListCreate lists, by format field.</summary>
        private static readonly Dictionary<string, string> ColumnTitles = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["OnDate"] = "On Date",
            ["OnTime"] = "On Time",
            ["OnMs"] = "On ms",
            ["OffDate"] = "Off Date",
            ["OffTime"] = "Off Time",
            ["OffMs"] = "Off ms",
            ["AckDate"] = "Ack Date",
            ["AckTime"] = "Ack Time",
            ["AckMs"] = "Ack ms",
            ["ShelvedEndDate"] = "Shelved End Date",
            ["ShelvedEndTime"] = "Shelved End Time",
            ["ShelvedComment"] = "Shelved Comment",
            ["DeltaTime"] = "Delta Time",
            ["DeltaMs"] = "Delta ms",
            ["Ms"] = "ms",
            ["Description"] = "Description",
            ["UserDescription"] = "User Description",
            ["UserLocation"] = "User Location",
            ["UserName"] = "User Name",
            ["Comment"] = "User Comment",
            ["LogState"] = "Log State",
            ["SumState"] = "Sum State",
            ["SumType"] = "Sum Type",
            ["TimeQuality"] = "Time Quality",
            ["ErrorDetails"] = "Error Details",
            ["Source"] = "Source",
            ["AlmComment"] = "Comment",
            ["Privilege"] = "Privilege",
            ["Tag"] = "Tag",
            ["Name"] = "Name",
            ["Cluster"] = "Cluster",
            ["Equipment"] = "Equipment",
            ["Item"] = "Item",
            ["Date"] = "Date",
            ["Time"] = "Time",
            ["State"] = "State",
            ["Type"] = "Type",
            ["Category"] = "Category",
            ["Priority"] = "Priority",
            ["Message"] = "Message",
        };

        /// <summary>The '@(...)' header title of a format field (see <see cref="ColumnTitles"/>).</summary>
        internal static string ColumnTitle(string field)
        {
            string f = field.Trim();
            if (f.Length == 0)
                return "";
            if (!ColumnTitles.TryGetValue(f, out string? t))
            {
                var sb = new StringBuilder();
                for (int i = 0; i < f.Length; i++)
                {
                    if (i > 0 && char.IsUpper(f[i]) && char.IsLower(f[i - 1]))
                        sb.Append(' ');
                    sb.Append(f[i]);
                }
                t = sb.ToString();
            }
            return "@(" + t + ")";
        }

        /// <summary>The default format of an AlarmListCreate list made without one.</summary>
        internal static string ArrayListDefaultFormat(SimHost h, int type)
        {
            if (Hardware(type))
                return CategoryFormat(h, 255, type);
            if (Events(type))
                return "{PriorityAndState,25}{Date,80}{Time,90}{Message,250}{Name,250}{State,40}{Cluster,70}{Equipment,150}{Item,100}{UserName,100}{UserLocation,100}{Category,60}";
            if (type == 3)
                return "{PriorityAndState,25}{OnDate,80}{OnTime,90}{Name,250}{State,40}{Cluster,70}{Equipment,150}{Item,100}{DisableEndDate,80}{DisableEndTime,90}{DisableComment,250}{UserName,100}{UserLocation,100}{Category,60}";
            if (type == 10)
                return "{PriorityAndState,25}{Name,250}{SumState,40}{OnDate,80}{OnTime,90}{OffDate,80}{OffTime,90}{DeltaTime,90}{Cluster,70}{Equipment,150}{Item,100}{Comment,250}{Category,60}";
            return "{PriorityAndState,25}{OnDate,80}{OnTime,90}{Name,250}{State,40}{Cluster,70}{Equipment,220}{Item,160}{UserName,100}{Comment,250}{Category,60}";
        }

        /// <summary>A list's own format: a '{...}' text or a [Format] name (AlarmListCreate's
        /// sFormat; ParameterGet), null when it has none.</summary>
        internal static string? ListFormat(SimHost h, PageAlarmList l)
        {
            if (l.FormatHandle >= 0)
            {
                var t = h.Formats.Get(l.FormatHandle);
                if (t != null)
                {
                    var sb = new StringBuilder();
                    foreach (var f in t.Fields)
                        sb.Append('{').Append(f.Name).Append(',').Append(C.Inv(f.Width)).Append('}');
                    return sb.ToString();
                }
            }
            string n = CitectText.Trim(l.FormatName);
            if (n.Length == 0)
                return null;
            if (n.StartsWith("{", StringComparison.Ordinal))
                return n;
            return Params.Get(h, "FORMAT", n);
        }

        private static readonly ConditionalWeakTable<
            SimHost,
            Dictionary<string, (long? fg, long? bg)>
        > FontCache = new ConditionalWeakTable<SimHost, Dictionary<string, (long?, long?)>>();

        /// <summary>A fonts.dbf font's foreground / background colour (labels or numbers), nulls
        /// when the font is not defined.</summary>
        internal static (long? fg, long? bg) FontColours(SimHost h, string name)
        {
            var cache = FontCache.GetValue(h, _ =>
            {
                var d = new Dictionary<string, (long?, long?)>(StringComparer.OrdinalIgnoreCase);
                var u = h.World!.Unit;
                foreach (var (_, r) in u.Rows("fonts.dbf"))
                {
                    string n = CitectText.Trim(r.Get("NAME"));
                    if (n.Length == 0 || d.ContainsKey(n))
                        continue;
                    long? fg = u.LabelValue(r.Get("FGND_COLR"));
                    long? bg = u.LabelValue(r.Get("BGND_COLR"));
                    d[n] = (fg, bg);
                }
                return d;
            });
            return cache.TryGetValue(CitectText.Trim(name), out var c) ? c : (null, null);
        }

        /// <summary>The category font of a row's state (UNON_COL / ACKON_COL / UNOFF_COL /
        /// ACKOFF_COL / DIS_COL).</summary>
        internal static string StateFont(SimHost h, long category, string state)
        {
            var cats = h.World!.Alarms.Categories();
            string field = state switch
            {
                "unack_on" => "UNON_COL",
                "ack_on" => "ACKON_COL",
                "unack_off" => "UNOFF_COL",
                "disabled" => "DIS_COL",
                _ => "ACKOFF_COL",
            };
            string v = cats.TryGetValue(category, out var row) ? CitectText.Trim(row.Get(field)) : "";
            if (v.Length == 0 && cats.TryGetValue(0, out var row0))
                v = CitectText.Trim(row0.Get(field));
            return v;
        }

        /// <summary>The line and fields of a row through a format.</summary>
        internal static (string text, List<KeyValuePair<string, string>> fields) Line(
            SimHost h,
            Row r,
            string format
        )
        {
            var t = FmtTemplate.Parse("", format);
            var fields = new List<KeyValuePair<string, string>>();
            foreach (var f in t.Fields)
            {
                f.Data = FieldText(h, r, f.Name) ?? "";
                fields.Add(new KeyValuePair<string, string>(f.Name, f.Data));
            }
            return (t.ToStr(), fields);
        }

        // ------------------------------------------------------------------ refresh (the animator's scan)
        /// <summary>The rows a list shows now: its items from the offset on, Count of them (the
        /// latest for AlarmDspLast). Updates the list's row items, total and digest.</summary>
        internal static List<Row> Refresh(SimHost h, PageAlarmList l)
        {
            var all = Items(h, l);
            l.Total = all.Count;
            int start = l.Last ? 0 : Math.Max(0, Math.Min(l.Offset, Math.Max(0, all.Count - 1)));
            if (l.Last)
                l.Offset = 0;
            int n = Math.Max(1, l.Count);
            var rows = new List<Row>();
            for (int i = start; i < all.Count && rows.Count < n; i++)
                rows.Add(all[i]);
            l.RowItems.Clear();
            var sb = new StringBuilder();
            sb.Append(C.Inv(l.Total)).Append('|').Append(C.Inv(l.Offset));
            foreach (var r in rows)
            {
                l.RowItems.Add(r);
                sb.Append('|')
                    .Append(r.Key.GetHashCode())
                    .Append(':')
                    .Append(StateName(r.St))
                    .Append(':')
                    .Append(
                        r.St?.Time.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? ""
                    );
            }
            l.Digest = sb.ToString();
            return rows;
        }

        /// <summary>The row at an AN of a list (AN - list AN = the row; an SA list's first data
        /// row is its AN), null when the row is empty.</summary>
        internal static Row? RowAt(PageAlarmList l, int an)
        {
            int k = an - l.An;
            return k >= 0 && k < l.RowItems.Count ? l.RowItems[k] as Row : null;
        }

        /// <summary>
        /// List states of a window's page; starts the data-changed callback of lists that changed.
        /// </summary>
        public static List<(
            int an,
            global::CicodeSim.Page.AlarmListState state,
            List<(int an, global::CicodeSim.Page.AlarmRowState row)> rows
        )> Scan(SimHost h, CicodeVm vm, int window, string page, bool flashOn)
        {
            var o =
                new List<(
                    int,
                    global::CicodeSim.Page.AlarmListState,
                    List<(int, global::CicodeSim.Page.AlarmRowState)>
                )>();
            foreach (var l in Tables(h).AlarmListsOf(window, page))
            {
                var rows = Refresh(h, l);
                if (l.ArrayList)
                    FillArray(h, l, rows);
                var ls = new global::CicodeSim.Page.AlarmListState
                {
                    Type = l.Type,
                    Count = l.Count,
                    Offset = l.Offset,
                    Total = l.Total,
                    Array = l.ArrayList,
                    NoDraw = l.NoDraw,
                    FontHandle = l.Font >= 0 ? l.Font : l.FontRow,
                    HeaderFontHandle = l.ArrayList ? l.FontHeader : -1,
                };
                foreach (var c in l.Columns)
                    ls.Columns.Add(new KeyValuePair<string, int>(c.Name, c.Width));
                if (l.ArrayList)
                {
                    // the display settings AlarmListCreate was given, and the column titles as
                    // the array holds them now (row 0 z 4: what the code cleared or set wins)
                    ls.DrawHeader = l.DrawHeader;
                    ls.RowHeight = l.RowHeight;
                    ls.Width = l.Width;
                    ls.Height = l.Height;
                    var arr = ListArray(h, l);
                    for (int c = 0; c < l.Columns.Count; c++)
                    {
                        string? title = null;
                        if (arr != null && c + 1 < arr.X && arr.Z > 4)
                            title = arr.Items.TryGetValue(((0 * arr.X) + c + 1) * arr.Z + 4, out string? v)
                                ? v
                                : "";
                        ls.Titles.Add(title ?? ColumnTitle(l.Columns[c].Name));
                    }
                }
                var drawn = new List<(int, global::CicodeSim.Page.AlarmRowState)>();
                for (int i = 0; i < rows.Count; i++)
                {
                    var rs = RowState(h, l, rows[i], i, flashOn);
                    ls.Rows.Add(rs);
                    if (!l.NoDraw && !l.ArrayList)
                        drawn.Add((l.An + i, rs));
                }
                o.Add((l.An, ls, drawn));
                // the data of the list arrived (the first fill too): its callback
                if (l.Notified != l.Digest)
                {
                    l.Notified = l.Digest;
                    if (l.Callback.Length > 0)
                        StartCallback(vm, l);
                }
            }
            return o;
        }

        private static void StartCallback(CicodeVm vm, PageAlarmList l)
        {
            var fn = vm.Library.Function(l.Callback);
            if (fn == null || vm.Scheduler == null)
                return;
            try
            {
                var t = vm.TaskForCall(fn, new[] { Value.Long(l.An) }, false, null, fn.Name);
                TaskContext.Set(t, l.Window, l.An);
                vm.Scheduler.Add(t);
            }
            catch (HardwareError) { }
        }

        internal static global::CicodeSim.Page.AlarmRowState RowState(
            SimHost h,
            PageAlarmList l,
            Row r,
            int i,
            bool flashOn
        )
        {
            long cat = r.Hw != null ? 255 : r.Def!.Category;
            string state = r.Ev != null ? r.Ev.Event : StateName(r.St);
            string fmt = ListFormat(h, l)
                ?? CategoryFormat(h, l.FormatCategory >= 0 ? l.FormatCategory : cat, l.Type);
            var (text, fields) = Line(h, r, fmt);
            string font = StateFont(
                h,
                l.FormatCategory >= 0 ? l.FormatCategory : cat,
                r.Ev != null ? StateName(r.St) : state
            );
            var (fg, bg) = font.Length > 0 ? FontColours(h, font) : (null, null);
            var rs = new global::CicodeSim.Page.AlarmRowState
            {
                Row = i,
                Text = text,
                Fields = fields,
                Tag = r.Hw?.Name ?? r.Def!.Tag,
                Priority = r.Hw != null ? 0 : h.World!.Alarms.Priority(r.Def!),
                Category = cat,
                State = state,
                Font = font,
                FontHandle = l.Font >= 0 ? l.Font : l.FontRow,
                Colour = fg,
                Background = bg,
                Flash = state == "unack_on" || state == "unack_off",
            };
            return rs;
        }

        // ------------------------------------------------------------------ the array of an SA list
        /// <summary>The array of an AlarmListCreate list (SA_Controls ArrayView.ci layout: cell
        /// (0, 0) the list's settings by z, row 0 the column headers, column 0 the rows' font /
        /// id / cluster / selected flag, cells z 1 the value). Rows 1 .. are the rows shown.</summary>
        internal static void FillArray(SimHost h, PageAlarmList l, List<Row> rows)
        {
            var arr = ListArray(h, l);
            if (arr == null)
                return;
            bool changed = false;
            void Set(int x, int y, int z, string v)
            {
                if (x < 0 || x >= arr.X || y < 0 || y >= arr.Y || z < 0 || z >= arr.Z)
                    return;
                int i = (y * arr.X + x) * arr.Z + z;
                string nv = v.Length > Types.StringMax ? v.Substring(0, Types.StringMax) : v;
                if (arr.Items.TryGetValue(i, out string? old) && old == nv)
                    return;
                arr.Items[i] = nv;
                changed = true;
            }
            Set(0, 0, 3, C.Inv(l.Type));
            Set(0, 0, 12, C.Inv(l.Total));
            // the data rows start at row 1 with a header or without one: row 0 holds the
            // columns (ArrayView.ci: _ArrayView_PageRows = Y - 1, rows read at nRowOffset + nRow
            // from 1; GenericList.ci sizes a list without a header one row taller)
            const int first = 1;
            int fieldsFrom = 1;
            for (int y = first; y < arr.Y; y++)
            {
                int k = y - first;
                var r = k < rows.Count ? rows[k] : null;
                Set(0, y, 1, r != null ? C.Inv(l.FontRow) : "");
                Set(0, y, 2, r != null ? FieldText(h, r, "RecordId") ?? "" : "");
                Set(0, y, 3, r != null ? FieldText(h, r, "Cluster") ?? "" : "");
                for (int c = 0; c < l.Columns.Count && c + fieldsFrom < arr.X; c++)
                    Set(c + fieldsFrom, y, 1, r != null ? FieldText(h, r, l.Columns[c].Name) ?? "" : "");
            }
            if (changed)
                arr.Dirty = true;
        }

        /// <summary>The array of an AlarmListCreate list, null when it has none or the code
        /// destroyed it (ArrayDestroy of the handle: the handle may name another array since).</summary>
        internal static Arrays.CicodeArray? ListArray(SimHost h, PageAlarmList l)
        {
            if (!l.ArrayList || l.ArrayRecord == null)
                return null;
            var arr = ProcessData.Of(h).ArrayTable.Get(l.ArrayHandle);
            return ReferenceEquals(arr, l.ArrayRecord) ? arr : null;
        }

        /// <summary>Free the array of an AlarmListCreate list as ArrayDestroy does (its hidden map
        /// closed too): AlarmListDestroy, a list again at its AN, the page closing (the array is
        /// the page's; no task then).</summary>
        internal static void FreeListArray(SimHost h, PageAlarmList l, CicodeTask? t = null)
        {
            if (ListArray(h, l) != null)
                Arrays.Destroy(h, t, l.ArrayHandle);
            l.ArrayRecord = null;
        }

        // ================================================================== the built-ins
        /// <summary>The lists an AN names in the task's window: -1 every list of the page, 0 the
        /// list at the keyboard cursor, else the list whose rows include the AN (its first AN
        /// when <paramref name="exact"/>).</summary>
        internal static List<PageAlarmList> ListsAt(SimHost h, CicodeTask t, long an, bool exact = true)
        {
            var w = Win(h, t);
            var o = new List<PageAlarmList>();
            if (w?.Page == null)
                return o;
            var tb = Tables(h);
            if (an == -1)
                return tb.AlarmListsOf(w.Number, w.Page.Name);
            int a = an == 0 ? w.CommandCursor : (int)an;
            var l = tb.AlarmList(w.Number, w.Page.Name, a, exact && an != 0);
            if (l == null && exact && an != 0)
                l = tb.AlarmList(w.Number, w.Page.Name, a, false);
            if (l != null)
                o.Add(l);
            return o;
        }

        private static Value MakeList(SimHost h, CicodeTask t, ArgList a, bool last)
        {
            var w = Win(h, t);
            long an = A.Short(A.I(a.Get(0)));
            long count = a.Count > 1 ? A.Short(A.I(a.Get(1))) : 1;
            long type = a.Count > 2 ? A.Short(A.I(a.Get(2))) : 1;
            if (w?.Page == null || an < 1 || an > 31999 || count < 1 || type < 0 || type > 18)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var tb = Tables(h);
            var l = new PageAlarmList
            {
                Window = w.Number,
                Page = w.Page.Name,
                An = (int)an,
                Count = (int)count,
                Type = (int)type,
                Last = last,
                Cluster = a.Count > 3 ? CitectText.Trim(A.S(a.Get(3))) : "",
                NoDraw = a.Count > 4 && A.I(a.Get(4)) != 0,
                Callback = a.Count > 5 ? CitectText.Trim(A.S(a.Get(5))) : "",
            };
            l.CreateKey =
                C.Inv(l.Count)
                + "|"
                + C.Inv(l.Type)
                + "|"
                + (last ? "1" : "0")
                + "|"
                + (l.NoDraw ? "1" : "0")
                + "|"
                + CitectText.Upper(l.Cluster)
                + "|"
                + CitectText.Upper(l.Callback);
            // the same call again (a Cicode object calling it every scan): the list is kept, with
            // its offset and the settings AlarmSetInfo changed since (guess)
            var old = tb.AlarmList(w.Number, w.Page.Name, (int)an, exact: true);
            if (old != null && !old.ArrayList && old.CreateKey == l.CreateKey)
                return W.I(0);
            tb.AlarmLists.RemoveAll(x => x.Window == w.Number && C.IEq(x.Page, w.Page.Name) && x.An == an);
            tb.AlarmLists.Add(l);
            if (h.World != null)
                AlarmEventLog.Attach(h.World);
            Refresh(h, l);
            return W.I(0);
        }

        [Builtin(
            "AlarmDsp",
            Doc = "1033462",
            Effects = "shows an alarm list on the ANs AN .. AN + Count - 1 of the current window's page (the page state: alarm_list on AN, alarm_row on each row AN)",
            Errors = "274 no page, an AN outside 1..31999, a Count below 1 or a type outside 0..18 (raised and returned; guess on the code)",
            Notes = "Type 1 by default (doc). Rows: AlarmCount's alarms of the type in the user's areas (priority first, newest first), hardware alarms (5..9, 13, 14), the alarm summary (10) or the sequence of events (15, 16) since the page animator started. NoDraw keeps the rows for AlarmGetDsp without drawing; CallbackFunc runs as a task with the list's AN each time the list's data changes (the first fill included). A list again at the same AN with the same arguments is kept (its offset too); with others it replaces it."
        )]
        private static Value AlarmDsp(SimHost h, CicodeTask t, ArgList a) => MakeList(h, t, a, false);

        [Builtin(
            "AlarmDspLast",
            Doc = "1033466",
            Effects = "shows the latest alarms on the ANs AN .. AN + Count - 1 of the current window's page",
            Errors = "274 no page, an AN outside 1..31999, a Count below 1 or a type outside 0..18 (raised and returned; guess on the code)",
            Notes = "As AlarmDsp, Count 1 and type 1 by default, the newest first whatever their priority, never scrolled."
        )]
        private static Value AlarmDspLast(SimHost h, CicodeTask t, ArgList a) => MakeList(h, t, a, true);

        private static Value ScrollList(SimHost h, CicodeTask t, ArgList a, int dir)
        {
            long an = A.Short(A.I(a.Get(0)));
            var ls = ListsAt(h, t, an);
            if (ls.Count == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            foreach (var l in ls)
            {
                if (l.Last)
                    continue;
                int n = Math.Max(1, l.Count);
                int off = l.Offset + dir * n;
                if (dir > 0 && off >= l.Total)
                    continue;
                l.Offset = Math.Max(0, off);
                l.Serial++;
            }
            return W.I(0);
        }

        [Builtin(
            "AlarmDspNext",
            Doc = "1033467",
            Effects = "scrolls the list(s) one page down",
            Errors = "274 no list at the AN (raised and returned; guess)",
            Notes = "AN -1 = every list of the page, 0 = the list at the keyboard cursor. A list already on its last page stays."
        )]
        private static Value AlarmDspNext(SimHost h, CicodeTask t, ArgList a) => ScrollList(h, t, a, 1);

        [Builtin(
            "AlarmDspPrev",
            Doc = "1033468",
            Effects = "scrolls the list(s) one page up",
            Errors = "274 no list at the AN (raised and returned; guess)",
            Notes = "AN -1 = every list of the page, 0 = the list at the keyboard cursor; not past the first alarm."
        )]
        private static Value AlarmDspPrev(SimHost h, CicodeTask t, ArgList a) => ScrollList(h, t, a, -1);

        [Builtin(
            "AlarmDspClusterAdd",
            Doc = "1033463",
            Errors = "274 no list at the AN (raised and returned; guess)",
            Notes = "One simulated process is every cluster's alarm server: the list shows every cluster's alarms already; 0."
        )]
        private static Value AlarmDspClusterAdd(SimHost h, CicodeTask t, ArgList a) => ClusterOp(h, t, a, 0);

        [Builtin(
            "AlarmDspClusterRemove",
            Doc = "1033465",
            Errors = "274 no list at the AN (raised and returned; guess)",
            Notes = "0 (see AlarmDspClusterAdd)."
        )]
        private static Value AlarmDspClusterRemove(SimHost h, CicodeTask t, ArgList a) =>
            ClusterOp(h, t, a, 0);

        [Builtin(
            "AlarmDspClusterInUse",
            Doc = "1033464",
            Errors = "274 no list at the AN (raised, 0; guess)",
            Notes = "1 when the list shows the cluster's alarms: its cluster is that one or not set."
        )]
        private static Value AlarmDspClusterInUse(SimHost h, CicodeTask t, ArgList a) =>
            ClusterOp(h, t, a, 1);

        private static Value ClusterOp(SimHost h, CicodeTask t, ArgList a, int kind)
        {
            var ls = ListsAt(h, t, A.Short(A.I(a.Get(0))));
            if (ls.Count == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(kind == 1 ? 0 : Codes.InvalidArg);
            }
            if (kind == 0)
                return W.I(0);
            string c = CitectText.Trim(A.S(a.Get(1)));
            var l = ls[0];
            return W.I(l.Cluster.Length == 0 || C.IEq(l.Cluster, c) ? 1 : 0);
        }

        [Builtin(
            "AlarmGetDsp",
            Doc = "1033478",
            Blocking = true,
            Errors = "274 no list row at the AN (raised, ''; guess)",
            Notes = "A field of the alarm at the row AN (AN - the list's AN = the row; an AlarmListCreate list: its first data row is its AN): AlmBrowse's fields plus RecordId, SummaryId, StateNumeric (the .State value with its masks), SAStateOffset (0 normal, 1 unack on, 2 unack off, 3 ack on, 4 disabled), Cluster, the summary fields (OnDate .. SumState) and the event fields (Message, Classification, UserName). An empty row gives ''. In background code the answer waits one turn (doc: 'becomes a blocking function')."
        )]
        private static Value AlarmGetDsp(SimHost h, CicodeTask t, ArgList a)
        {
            long an = A.Short(A.I(a.Get(0)));
            string field = A.S(a.Get(1));
            Value Get()
            {
                var w = Win(h, t);
                if (w?.Page == null || h.World == null)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                var l = Tables(h).AlarmList(w.Number, w.Page.Name, (int)an);
                if (l == null)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                if (l.Digest.Length == 0)
                    Refresh(h, l);
                var r = RowAt(l, (int)an);
                if (r == null)
                    return W.Empty;
                return W.S(C.Cut(FieldText(h, r, field) ?? ""));
            }
            if (t.Foreground)
                return Get();
            return W.BlockOneTurn(t, "AlarmGetDsp", () => Get());
        }

        [Builtin(
            "AlarmGetInfo",
            Doc = "1033480",
            Blocking = true,
            Errors = "274 no list at the AN or an unknown type (raised, 0; guess)",
            Notes = "AN 0 = the list at the keyboard cursor. 0 the page (offset / count), 1 the offset in lines, 2 the category, 3 the type, 7 the priority, 8 the display mode, 9 the sort mode, 11 0 (no buffer overflow), 12 0 / 1 / 2 / 3 (named filter, custom filter), 13 2 (every cluster ready), 14 1 (the cluster's data is ready), 15 the auto-refresh mode, 16 0 (no timeout), 17 the page the AN's row is on. In background code the answer waits one turn."
        )]
        private static Value AlarmGetInfo(SimHost h, CicodeTask t, ArgList a)
        {
            long an = A.Short(A.I(a.Get(0))),
                type = A.Short(A.I(a.Get(1)));
            Value Get()
            {
                var ls = ListsAt(h, t, an, false);
                if (ls.Count == 0)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                }
                var l = ls[0];
                int n = Math.Max(1, l.Count);
                switch (type)
                {
                    case 0:
                        return W.I(l.Offset / n);
                    case 1:
                        return W.I(l.Offset);
                    case 2:
                        return W.I(l.Category);
                    case 3:
                        return W.I(l.Type);
                    case 7:
                        return W.I(l.Priority);
                    case 8:
                        return W.I(l.DisplayMode);
                    case 9:
                        return W.I(l.SortMode);
                    case 11:
                        return W.I(0);
                    case 12:
                        return W.I((l.NamedFilter.Length > 0 ? 1 : 0) + (l.CustomFilter.Length > 0 ? 2 : 0));
                    case 13:
                        return W.I(2);
                    case 14:
                        return W.I(1);
                    case 15:
                        return W.I(l.AutoRefresh);
                    case 16:
                        return W.I(0);
                    case 17:
                        return W.I((l.Offset + Math.Max(0, (int)an - l.An)) / n);
                }
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            if (t.Foreground)
                return Get();
            return W.BlockOneTurn(t, "AlarmGetInfo", () => Get());
        }

        [Builtin(
            "AlarmSetInfo",
            Doc = "1033498",
            Effects = "changes the list(s)' offset, filters, format, font, sort or named filter",
            Errors = "274 no list at the AN or a type that cannot be set (11, 16, others) (raised and returned); 569 an offset outside the list's alarms (returned; doc)",
            Notes = "AN -1 = every list of the page, 0 = the list at the keyboard cursor. 0 the page (offset = page x count), 1 the offset in lines, 2 the category (or a group), 3 the type, 4 every row in the format and fonts of a category, 5 a format handle, 6 a font handle, 7 the priority (or a group), 8 0 by category / 1 by priority, 9 the sort mode, 10 the order-by keys, 12 a named filter ('' unlinks), 15 the auto-refresh mode."
        )]
        private static Value AlarmSetInfo(SimHost h, CicodeTask t, ArgList a)
        {
            long an = A.Short(A.I(a.Get(0))),
                type = A.Short(A.I(a.Get(1)));
            Value v = a.Get(2);
            var ls = ListsAt(h, t, an, false);
            if (
                ls.Count == 0
                || type == 11
                || type == 16
                || type < 0
                || type > 15
                || type == 13
                || type == 14
            )
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            string s = A.S(v);
            long n = C.Atol(s);
            foreach (var l in ls)
            {
                switch (type)
                {
                    case 0:
                    case 1:
                    {
                        long off = type == 0 ? n * Math.Max(1, l.Count) : n;
                        if (off < 0 || (off > 0 && off >= Math.Max(1, l.Total)))
                            return W.I(569);
                        l.Offset = (int)off;
                        break;
                    }
                    case 2:
                        l.Category = n;
                        break;
                    case 3:
                        if (n < 0 || n > 18)
                        {
                            W.Raise(t, Codes.InvalidArg);
                            return W.I(Codes.InvalidArg);
                        }
                        l.Type = (int)n;
                        l.Offset = 0;
                        break;
                    case 4:
                        l.FormatCategory = n;
                        break;
                    case 5:
                        l.FormatHandle = n;
                        break;
                    case 6:
                        l.Font = n;
                        break;
                    case 7:
                        l.Priority = n;
                        break;
                    case 8:
                        l.DisplayMode = n;
                        break;
                    case 9:
                        l.SortMode = n;
                        break;
                    case 10:
                        l.OrderBy = s;
                        break;
                    case 12:
                        l.NamedFilter = CitectText.Trim(s);
                        break;
                    case 15:
                        l.AutoRefresh = n;
                        break;
                }
                l.Serial++;
            }
            return W.I(0);
        }

        [Builtin(
            "AlarmGetOrderbyKey",
            Doc = "1033481",
            Errors = "274 no list at the AN (raised, '')",
            Notes = "The list's order-by keys (AlarmSetInfo 10), '' when none."
        )]
        private static Value AlarmGetOrderbyKey(SimHost h, CicodeTask t, ArgList a)
        {
            var ls = ListsAt(h, t, A.Short(A.I(a.Get(0))), false);
            if (ls.Count == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            return W.S(C.Cut(ls[0].OrderBy));
        }

        [Builtin(
            "AlarmGetFilterName",
            Doc = "1069692",
            Errors = "274 no list at the AN (raised, '')",
            Notes = "The named filter the list is linked to (AlarmSetInfo 12): '' when none or when the name is not an open named filter."
        )]
        private static Value AlarmGetFilterName(SimHost h, CicodeTask t, ArgList a)
        {
            var ls = ListsAt(h, t, A.Short(A.I(a.Get(0))), false);
            if (ls.Count == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            string n = ls[0].NamedFilter;
            return W.S(n.Length > 0 && ProcessData.Of(h).NamedFilters.ContainsKey(n) ? C.Cut(n) : "");
        }

        // ------------------------------------------------------------------ SA lists (AlarmListCreate)
        [Builtin(
            "AlarmListCreate",
            Doc = "1033486",
            Effects = "makes an alarm list at the AN with an array bound to the AN (Array*ByAn) that its rows fill",
            Errors = "274 no page, an AN outside 1..31999, a row height below 1, a width below 1, a height below the row height, a type outside 0..18 or a format that has no fields (raised and returned; guess on the code)",
            Notes = "The array: (columns of the format + 1) x (height / row height, one more without a header) x 16 (SA_Controls GenericListCreate's layout): cell (0, 0) z 3 the type, 4 / 5 the width / height, 6 the row height, 7 the header flag, 8 the cluster, 9 / 10 the row / header font, 11 the columns' width, 12 the alarms in the list; row 0 the columns (z 1 the field, 2 the type 1, 3 the width, 4 the title: ColumnTitle, the runtime's Localization.ci strings); the data rows from row 1 with a header or without one; each row's column 0 z 1 the font, 2 the record id, 3 the cluster; the cells z 1 the field values. sFormat = '{...}' or a [Format] name, else the runtime's default SA format of the type (ArrayListDefaultFormat: PriorityAndState, On Date, On Time, Name, State, Cluster, Equipment, Item, User Name, Comment, Category for active lists). The array is bound to (AN, window) in its own record as ArrayCreateByAn binds one (ArrayGetArrayByAn, ArrayExistsByAn, ArrayGetMapNameByAn and the other Array*ByAn find it; ArrayDestroy of the handle ends it; the window's page close frees it). A list again at the AN replaces it (and its array); another array bound to the AN is destroyed first, as ArrayCreateByAn does (guess)."
        )]
        private static Value AlarmListCreate(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Win(h, t);
            long an = A.Short(A.I(a.Get(0))),
                type = A.Short(A.I(a.Get(1)));
            long width = A.Short(A.I(a.Get(2))),
                height = A.Short(A.I(a.Get(3))),
                rowh = A.Short(A.I(a.Get(4)));
            bool header = A.I(a.Get(5)) != 0;
            string cluster = a.Count > 6 ? CitectText.Trim(A.S(a.Get(6))) : "";
            string fmtName = a.Count > 7 ? CitectText.Trim(A.S(a.Get(7))) : "";
            long fontRow = a.Count > 8 ? A.Short(A.I(a.Get(8))) : -1,
                fontHdr = a.Count > 9 ? A.Short(A.I(a.Get(9))) : -1;
            if (
                w?.Page == null
                || h.World == null
                || an < 1
                || an > 31999
                || rowh < 1
                || width < 1
                || height < rowh
                || type < 0
                || type > 18
            )
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var l = new PageAlarmList
            {
                Window = w.Number,
                Page = w.Page.Name,
                An = (int)an,
                Type = (int)type,
                ArrayList = true,
                Cluster = cluster,
                Width = (int)width,
                Height = (int)height,
                RowHeight = (int)rowh,
                DrawHeader = header,
                FormatName = fmtName,
                FontRow = fontRow,
                FontHeader = fontHdr,
                NoDraw = true,
            };
            string fmt = ListFormat(h, l) ?? ArrayListDefaultFormat(h, (int)type);
            var tpl = FmtTemplate.Parse("", fmt);
            if (tpl.Fields.Count == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            foreach (var f in tpl.Fields)
                l.Columns.Add((f.Name, f.Width));
            if (l.FormatName.Length == 0)
                l.FormatName = fmt;
            int visible = (int)(height / rowh);
            int arrRows = header ? visible : visible + 1;
            l.Count = header ? Math.Max(1, visible - 1) : visible;
            var tb = Tables(h);
            // a list again at the AN: its old list and array go
            foreach (var old in tb.AlarmLists)
                if (old.Window == w.Number && C.IEq(old.Page, w.Page.Name) && old.An == an)
                    FreeListArray(h, old, t);
            tb.AlarmLists.RemoveAll(x => x.Window == w.Number && C.IEq(x.Page, w.Page.Name) && x.An == an);
            // another array bound to the AN goes too, as ArrayCreateByAn destroys the AN's
            // previous array (guess: the managed code is not read), so the AN names one array
            int prev = DisplayAns.ArrayOfAn(h, (int)an, w.Number);
            if (prev != -1)
                Arrays.Destroy(h, t, prev);
            int hd = Arrays.Create(h, t, "", tpl.Fields.Count + 1, arrRows, 16);
            if (hd < 0)
                return W.I(t.Error != 0 ? t.Error : Codes.InvalidArg);
            var arr = ProcessData.Of(h).ArrayTable.Get(hd)!;
            // the array is the list's and bound to (AN, window) in its own record, as
            // ArrayCreateByAn binds one: SA_Controls AlarmList.ci
            // reads it with ArrayGetArrayByAn / ArrayExistsByAn / ArrayGetMapNameByAn, and the
            // window's page close frees it (DisplayAns' PageClosing handler, once: ReleaseArray
            // then finds the record gone)
            DisplayAns.BindArray(h, t, hd, (int)an, w.Number);
            l.ArrayHandle = hd;
            l.ArrayRecord = arr;
            l.ReleaseArray = () => FreeListArray(h, l);
            tb.AlarmLists.Add(l);
            AlarmEventLog.Attach(h.World);
            void Set(int x, int y, int z, string v) => arr.Items[(y * arr.X + x) * arr.Z + z] = v;
            Set(0, 0, 3, C.Inv(type));
            Set(0, 0, 4, C.Inv(width));
            Set(0, 0, 5, C.Inv(height));
            Set(0, 0, 6, C.Inv(rowh));
            Set(0, 0, 7, header ? "1" : "0");
            Set(0, 0, 8, cluster);
            Set(0, 0, 9, C.Inv(fontRow));
            Set(0, 0, 10, C.Inv(fontHdr));
            int total = 0;
            for (int c = 0; c < tpl.Fields.Count; c++)
            {
                Set(c + 1, 0, 1, tpl.Fields[c].Name);
                Set(c + 1, 0, 2, "1");
                Set(c + 1, 0, 3, C.Inv(tpl.Fields[c].Width));
                Set(c + 1, 0, 4, ColumnTitle(tpl.Fields[c].Name));
                total += tpl.Fields[c].Width;
            }
            Set(0, 0, 11, C.Inv(total));
            FillArray(h, l, Refresh(h, l));
            return W.I(0);
        }

        /// <summary>The SA list at an AN (its first AN), refreshed into its array.</summary>
        private static Value Fill(SimHost h, CicodeTask t, ArgList a, string name)
        {
            var w = Win(h, t);
            long an = A.Short(A.I(a.Get(0)));
            var l = w?.Page != null ? Tables(h).AlarmList(w.Number, w.Page.Name, (int)an, exact: true) : null;
            if (l == null || h.World == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            // the array's y offset (cell (0, 0) z 2: the rows scrolled, AlarmList_DspNext)
            if (l.ArrayList)
            {
                var arr = ListArray(h, l);
                if (arr != null && arr.Items.TryGetValue(2, out string? yoff))
                    l.Offset = (int)Math.Max(0, C.Atol(yoff));
            }
            var rows = Refresh(h, l);
            if (l.ArrayList)
                FillArray(h, l, rows);
            return W.I(0);
        }

        [Builtin(
            "AlarmListDisplay",
            Doc = "1033488",
            Effects = "refills the AlarmListCreate list's array from the alarms (from its y offset, cell (0, 0) z 2)",
            Errors = "274 no list at the AN (raised and returned)"
        )]
        private static Value AlarmListDisplay(SimHost h, CicodeTask t, ArgList a) =>
            Fill(h, t, a, "AlarmListDisplay");

        [Builtin(
            "AlarmListFill",
            Doc = "1033489",
            Effects = "refills the AlarmListCreate list's array from the alarms",
            Errors = "274 no list at the AN (raised and returned)"
        )]
        private static Value AlarmListFill(SimHost h, CicodeTask t, ArgList a) =>
            Fill(h, t, a, "AlarmListFill");

        [Builtin(
            "ArrayFillFromAlarmDataByAn",
            Doc = "1069793",
            Effects = "refills the array of the AN's alarm list from the alarms",
            Errors = "274 no alarm list at the AN (raised and returned)"
        )]
        private static Value ArrayFillFromAlarmDataByAn(SimHost h, CicodeTask t, ArgList a) =>
            Fill(h, t, a, "ArrayFillFromAlarmDataByAn");

        [Builtin(
            "AlarmListDestroy",
            Doc = "1033487",
            Effects = "removes the AlarmListCreate list and frees its array",
            Errors = "274 no list at the AN (raised and returned)"
        )]
        private static Value AlarmListDestroy(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Win(h, t);
            long an = A.Short(A.I(a.Get(0)));
            var tb = Tables(h);
            var l = w?.Page != null ? tb.AlarmList(w.Number, w.Page.Name, (int)an, exact: true) : null;
            if (l == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            tb.AlarmLists.Remove(l);
            FreeListArray(h, l, t);
            return W.I(0);
        }

        // ------------------------------------------------------------------ acknowledging from a list
        [Builtin(
            "AlarmAck",
            Doc = "1033445",
            Blocking = true,
            Effects = "acknowledges the alarm at a row AN (mode 0), the rows a list shows (1), a category (2) or a priority (3)",
            Errors = "274 a bad mode or no list / row at the AN (returned; guess)",
            Notes = "Mode 0: Value = a row AN (0 = the keyboard cursor); 1: a list's AN (0 = the cursor's list, -1 = every AlarmDsp list of the page); 2: a category (0 = all, a group handle); 3: a priority (0 = all, a group). Only alarms the user can see (AlarmDb's areas). In background code the answer waits one turn (doc)."
        )]
        private static Value AlarmAck(SimHost h, CicodeTask t, ArgList a)
        {
            long mode = A.Short(A.I(a.Get(0))),
                value = A.I(a.Get(1));
            Value Do()
            {
                var w = h.World;
                if (w == null)
                    return W.I(Codes.InvalidArg);
                var db = w.Alarms;
                switch (mode)
                {
                    case 0:
                    {
                        var win = Win(h, t);
                        int an = value == 0 && win != null ? win.CommandCursor : (int)value;
                        var l = win?.Page != null ? Tables(h).AlarmList(win.Number, win.Page.Name, an) : null;
                        var r = l != null ? RowAt(l, an) : null;
                        if (r?.Def == null)
                            return W.I(Codes.InvalidArg);
                        db.Acknowledge(r.Def.Tag, "user");
                        return W.I(0);
                    }
                    case 1:
                    {
                        var ls = ListsAt(h, t, value, false);
                        if (ls.Count == 0)
                            return W.I(Codes.InvalidArg);
                        foreach (var l in ls)
                        {
                            if (value == -1 && l.Last)
                                continue;
                            if (l.Digest.Length == 0)
                                Refresh(h, l);
                            foreach (var o in l.RowItems)
                                if (o is Row r && r.Def != null)
                                    db.Acknowledge(r.Def.Tag, "user");
                        }
                        return W.I(0);
                    }
                    case 2:
                    case 3:
                    {
                        foreach (
                            var (x, _) in db.ListOf(1, "", (d, _) =>
                                mode == 2 ? InSet(h, value, d.Category) : InSet(h, value, db.Priority(d))
                            )
                        )
                            db.Acknowledge(x.Tag, "user");
                        if (mode == 2 && (value == 0 || value == 255))
                            db.AcknowledgeHardware();
                        return W.I(0);
                    }
                }
                return W.I(Codes.InvalidArg);
            }
            if (t.Foreground)
                return Do();
            return W.BlockOneTurn(t, "AlarmAck", () => Do());
        }

        [Builtin(
            "AlarmSetQuery",
            Status = BuiltinStatus.Stub,
            Returns = "274",
            Doc = "",
            Errors = "274 (raised and returned)",
            Notes = "An obsolete query hook of the alarm lists (no help topic; its table entry is the 'not supported' stub): 274."
        )]
        private static Value AlarmSetQuery(SimHost h, CicodeTask t, ArgList a)
        {
            W.Raise(t, Codes.InvalidArg);
            return W.I(Codes.InvalidArg);
        }
    }
}
