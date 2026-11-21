using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// The trend archive Cicode writes, kept for the simulation: event trends (TrnEventSetTable:
    /// samples numbered by event, read back by TrnEventGetTable) and periodic samples
    /// (TrnSetTable: TrnGetTable reads them before the tag change log), and the history files
    /// TrnAddHistory names. Nothing is written to a real archive.
    /// </summary>
    public static class TrendArchive
    {
        internal struct EventSample
        {
            public double Time; // Unix seconds
            public int Ms;
            public double Value;
        }

        private sealed class Store
        {
            public readonly Dictionary<string, SortedDictionary<long, EventSample>> Events = new Dictionary<
                string,
                SortedDictionary<long, EventSample>
            >(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, SortedDictionary<long, double>> Periodic = new Dictionary<
                string,
                SortedDictionary<long, double>
            >(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> History = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly ConditionalWeakTable<SimHost, Store> Stores =
            new ConditionalWeakTable<SimHost, Store>();

        private static Store Of(SimHost h) => Stores.GetValue(h, _ => new Store());

        // (a periodic sample's key: its time in milliseconds)
        private static long Key(double unix) => (long)Math.Round(unix * 1000.0);

        // ------------------------------------------------------------------ tables
        private static Value Element(in Value table, long i)
        {
            if (table.O is CodeVarRef cr)
                return cr.Slot.Get(cr.Element + i, out _);
            return i == 0 ? table : Value.Real(0);
        }

        private static void Store_(CicodeTask t, in Value table, long i, Value v)
        {
            if (table.O is CodeVarRef cr)
                cr.Slot.Set(cr.Element + i, v);
            else if (i == 0)
                W.Out(t, table, v);
        }

        private static bool Usable(SimHost h, CicodeTask t, string tag, long length, out Value fail)
        {
            fail = W.I(0);
            if (h.World == null)
            {
                fail = Tags.NoWorld("TrnEvent");
                return false;
            }
            if (tag.Trim().Length == 0 || length < 0 || length >= 32000)
            {
                W.Raise(t, Codes.InvalidArg);
                return false;
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ periodic samples
        /// <summary>A sample TrnSetTable wrote for <paramref name="tag"/> within half a period of
        /// <paramref name="unix"/> (Unix seconds).</summary>
        internal static bool TryWritten(SimHost h, string tag, double unix, double period, out double v)
        {
            v = 0;
            if (!Stores.TryGetValue(h, out var st) || !st.Periodic.TryGetValue(tag.Trim(), out var s))
                return false;
            long at = Key(unix),
                half = Math.Max(1, Key(period) / 2);
            foreach (var kv in s)
            {
                if (kv.Key < at - half)
                    continue;
                if (kv.Key > at + half)
                    break;
                v = kv.Value;
                return true;
            }
            return false;
        }

        [Builtin(
            "TrnSetTable",
            Doc = "1193875",
            Blocking = true,
            Errors = "274 an empty tag or a Length of 32000+ (raised, 0); 289 an unknown trend (raised, 0); 282 in foreground code (raised, 0)",
            Notes = "(Tag, Time, Period, Length, Table [, Milliseconds]): Length samples Period seconds apart (0 = the trend's sample period) ending at Time + Milliseconds (0 = now), Table[0] the newest (TrnGetTable's order), into the simulation's trend archive: TrnGetTable reads them before the tag change log. Returns Length. Waits one turn."
        )]
        private static Value TrnSetTable(SimHost h, CicodeTask t, ArgList a)
        {
            string tag = A.S(a.Get(0));
            long length = A.UShort(A.I(a.Get(3)));
            if (!Usable(h, t, tag, length, out var fail))
                return fail;
            long time = A.I(a.Get(1));
            double period = A.R(a.Get(2));
            Value table = a.Get(4);
            long ms = a.Count > 5 ? A.I(a.Get(5)) : 0;
            var w = h.World!;
            return W.BlockOneTurn(t, "TrnSetTable", () =>
            {
                var r = Trends.Find(h, tag);
                if (r == null)
                {
                    W.Raise(t, Codes.NameNotFound);
                    return W.I(0);
                }
                double p = period > 0 ? period : Trends.SamplePeriod(r);
                double end = time == 0 ? w.Clock.StartUnix + w.Clock.Now() : time + ms / 1000.0;
                var st = Of(h);
                if (!st.Periodic.TryGetValue(tag.Trim(), out var s))
                    st.Periodic[tag.Trim()] = s = new SortedDictionary<long, double>();
                for (long i = 0; i < length; i++)
                    s[Key(end - i * p)] = A.R(Element(table, i));
                return W.I(length);
            });
        }

        // ------------------------------------------------------------------ event trends
        private static Value SetEvents(SimHost h, CicodeTask t, ArgList a, bool withMs)
        {
            string tag = A.S(a.Get(0));
            long length = A.UShort(A.I(a.Get(3)));
            if (!Usable(h, t, tag, length, out var fail))
                return fail;
            long ev = A.I(a.Get(1));
            Value table = a.Get(4),
                times = a.Get(5),
                msTable = withMs && a.Count > 6 ? a.Get(6) : Value.Nothing;
            var w = h.World!;
            return W.BlockOneTurn(t, withMs ? "TrnEventSetTableMS" : "TrnEventSetTable", () =>
            {
                if (Trends.Find(h, tag) == null)
                {
                    W.Raise(t, Codes.NameNotFound);
                    return W.I(0);
                }
                var st = Of(h);
                if (!st.Events.TryGetValue(tag.Trim(), out var s))
                    st.Events[tag.Trim()] = s = new SortedDictionary<long, EventSample>();
                long last = 0;
                foreach (long k in s.Keys)
                    last = k;
                // (event 0: after the last one)
                long first = ev > 0 ? ev : last + 1;
                double now = w.Clock.StartUnix + w.Clock.Now();
                for (long i = 0; i < length; i++)
                {
                    double at = A.R(Element(times, i));
                    s[first + i] = new EventSample
                    {
                        Value = A.R(Element(table, i)),
                        Time = at > 0 ? at : Math.Floor(now),
                        Ms = msTable.IsNothing ? 0 : (int)A.I(Element(msTable, i)),
                    };
                }
                return W.I(length);
            });
        }

        [Builtin(
            "TrnEventSetTable",
            Doc = "1193822",
            Blocking = true,
            Errors = "274 an empty tag or a Length of 32000+ (raised, 0); 289 an unknown trend (raised, 0); 282 in foreground code (raised, 0)",
            Notes = "(Tag, Event, Period, Length, Table, TimeTable): Length samples numbered from Event on (0: after the last event written), each with its time (TimeTable, Unix seconds; 0 = now), into the simulation's trend archive; TrnEventGetTable reads them back. Returns Length. Waits one turn."
        )]
        private static Value TrnEventSetTable(SimHost h, CicodeTask t, ArgList a) =>
            SetEvents(h, t, a, false);

        [Builtin(
            "TrnEventSetTableMS",
            Doc = "1193823",
            Blocking = true,
            Errors = "as TrnEventSetTable",
            Notes = "TrnEventSetTable with a third table, the samples' milliseconds (Tag, Event, Period, Length, Table, TimeTable, MSTable)."
        )]
        private static Value TrnEventSetTableMS(SimHost h, CicodeTask t, ArgList a) =>
            SetEvents(h, t, a, true);

        private static Value GetEvents(SimHost h, CicodeTask t, ArgList a, bool withMs)
        {
            string tag = A.S(a.Get(0));
            long length = A.UShort(A.I(a.Get(3)));
            if (!Usable(h, t, tag, length, out var fail))
                return fail;
            long ev = A.I(a.Get(1));
            Value table = a.Get(4),
                times = a.Get(5);
            long mode = a.Count > 6 ? A.I(a.Get(6)) : 0;
            Value msTable = withMs && a.Count > 7 ? a.Get(7) : Value.Nothing;
            return W.BlockOneTurn(t, withMs ? "TrnEventGetTableMS" : "TrnEventGetTable", () =>
            {
                if (Trends.Find(h, tag) == null)
                {
                    W.Raise(t, Codes.NameNotFound);
                    return W.I(0);
                }
                Stores.TryGetValue(h, out var st);
                SortedDictionary<long, EventSample>? s = null;
                st?.Events.TryGetValue(tag.Trim(), out s);
                long last = 0;
                if (s != null)
                    foreach (long k in s.Keys)
                        last = k;
                long end = ev > 0 ? ev : last;
                bool oldestFirst = (mode & 2) != 0,
                    keep = (mode & 1) != 0;
                for (long i = 0; i < length; i++)
                {
                    long number = end - i;
                    long k = oldestFirst ? length - 1 - i : i;
                    if (s != null && number > 0 && s.TryGetValue(number, out var e))
                    {
                        Store_(t, table, k, Value.Real(e.Value));
                        Store_(t, times, k, W.I((long)e.Time));
                        if (!msTable.IsNothing)
                            Store_(t, msTable, k, W.I(e.Ms));
                    }
                    else
                    {
                        // (no such event: the invalid value; mode bit 1 clear writes 0)
                        Store_(t, table, k, Value.Real(keep ? Trends.Invalid : 0.0));
                        Store_(t, times, k, W.I(0));
                        if (!msTable.IsNothing)
                            Store_(t, msTable, k, W.I(0));
                    }
                }
                return W.I(length);
            });
        }

        [Builtin(
            "TrnEventGetTable",
            Doc = "1193820",
            Blocking = true,
            Errors = "274 an empty tag or a Length of 32000+ (raised, 0); 289 an unknown trend (raised, 0); 282 in foreground code (raised, 0)",
            Notes = "(Tag, Event, Period, Length, Table, TimeTable, Mode): the Length events ending at Event (0: the last written), newest first (Mode + 2: oldest first), their values into Table and their times (Unix seconds) into TimeTable, from the simulation's trend archive (TrnEventSetTable). An event never written is TrnGetInvalidValue() with Mode bit 1 set, else 0, at time 0. Period is ignored. Returns Length. Waits one turn."
        )]
        private static Value TrnEventGetTable(SimHost h, CicodeTask t, ArgList a) =>
            GetEvents(h, t, a, false);

        [Builtin(
            "TrnEventGetTableMS",
            Doc = "1193821",
            Blocking = true,
            Errors = "as TrnEventGetTable",
            Notes = "TrnEventGetTable with the samples' milliseconds into a third table (Tag, Event, Period, Length, Table, TimeTable, Mode, MSTable)."
        )]
        private static Value TrnEventGetTableMS(SimHost h, CicodeTask t, ArgList a) =>
            GetEvents(h, t, a, true);

        // ------------------------------------------------------------------ history files
        [Builtin(
            "TrnAddHistory",
            Doc = "1193807",
            Errors = "274 an empty file name (raised and returned)",
            Notes = "Names a history file for the trends of the simulation (its samples are not read: the simulation's archive keeps what Cicode wrote): 0."
        )]
        private static Value TrnAddHistory(SimHost h, CicodeTask t, ArgList a)
        {
            string file = A.S(a.Get(0)).Trim();
            if (file.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            Of(h).History.Add(file);
            return W.I(0);
        }

        [Builtin(
            "TrnDelHistory",
            Doc = "1193818",
            Errors = "274 a file TrnAddHistory did not name (raised and returned)",
            Notes = "Drops a history file TrnAddHistory named: 0."
        )]
        private static Value TrnDelHistory(SimHost h, CicodeTask t, ArgList a)
        {
            if (!Of(h).History.Remove(A.S(a.Get(0)).Trim()))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            return W.I(0);
        }
    }
}
