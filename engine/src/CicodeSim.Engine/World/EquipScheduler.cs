using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CicodeSim.World
{
    /// <summary>The equipment scheduler of a simulation.</summary>
    public sealed class EquipScheduler
    {
        private static readonly ConditionalWeakTable<World, EquipScheduler> All =
            new ConditionalWeakTable<World, EquipScheduler>();
        private const long Day = 864000000000L; // one day of FILETIME (100 ns)

        public sealed class Item
        {
            public long Id;
            public string Cluster = "",
                Equipment = "",
                State = "",
                Desc = "";
            public long Start,
                End,
                Modified;
            public long Freq = -1,
                Interval = 1,
                Weekday,
                WeekdayMask,
                MaxRec = -1,
                RecUntil = -1;
            public long DayOrd,
                DayOfMonth,
                Month,
                SpecialInc;
            public string GroupIds = "";
        }

        private readonly object _lock = new object();
        private readonly List<Item> _items = new List<Item>();
        private long _next = 1;

        public static EquipScheduler Of(World w)
        {
            var s = All.GetValue(w, _ => new EquipScheduler());
            w.Equipment.ScheduledState = e => s.StateAt(e.Name, (long)w.Clock.NowFiletime());
            return s;
        }

        public long Add(
            string cluster,
            string equipment,
            string state,
            long start,
            long end,
            string desc,
            long now
        )
        {
            lock (_lock)
            {
                var it = new Item
                {
                    Id = _next++,
                    Cluster = cluster ?? "",
                    Equipment = equipment ?? "",
                    State = state ?? "",
                    Start = start,
                    End = end,
                    Desc = desc ?? "",
                    Modified = now,
                };
                _items.Add(it);
                return it.Id;
            }
        }

        public Item? Get(long id)
        {
            lock (_lock)
                return _items.Find(i => i.Id == id);
        }

        public bool Delete(long id)
        {
            lock (_lock)
                return _items.RemoveAll(i => i.Id == id) > 0;
        }

        public List<Item> Items()
        {
            lock (_lock)
                return new List<Item>(_items);
        }

        /// <summary>The occurrences of <paramref name="it"/> that overlap [from, to): (start, end).</summary>
        public static IEnumerable<(long Start, long End)> Occurrences(Item it, long from, long to)
        {
            long len = Math.Max(0, it.End - it.Start);
            if (it.Freq != 4 && it.Freq != 5)
            {
                if (it.Start < to && it.End > from)
                    yield return (it.Start, it.End);
                yield break;
            }
            long step = it.Freq == 4 ? Day * Math.Max(1, it.Interval) : Day;
            long until = it.RecUntil > 0 ? it.RecUntil : long.MaxValue;
            long count = 0;
            long first = it.Start;
            if (from - len > first) // skip ahead to the window
                first += (from - len - first) / step * step;
            for (long s = first; s < to && s <= until; s += step)
            {
                if (it.Freq == 5)
                {
                    long dayNo = (s / Day);
                    int dow = (int)((dayNo + 1) % 7); // FILETIME day 0 (1601-01-01) = a Monday
                    if ((it.WeekdayMask & (1L << dow)) == 0)
                        continue;
                    long week = (s - it.Start) / (7 * Day);
                    if (it.Interval > 1 && week % it.Interval != 0)
                        continue;
                }
                count++;
                if (it.MaxRec > 0 && count > it.MaxRec)
                    yield break;
                if (s + len > from)
                    yield return (s, s + len);
            }
        }

        /// <summary>The state an equipment's schedule says at <paramref name="ft"/> (the latest
        /// occurrence that holds it), null when none does.</summary>
        public string? StateAt(string equipment, long ft)
        {
            string? state = null;
            long best = long.MinValue;
            foreach (var it in Items())
            {
                if (!string.Equals(it.Equipment, equipment, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var (s, e) in Occurrences(it, ft, ft + 1))
                    if (s <= ft && ft < e && s >= best)
                    {
                        best = s;
                        state = it.State;
                    }
            }
            return state;
        }

        /// <summary>A browse row of an occurrence (the fields of SchdConfigOpen / SchdOpen, times
        /// as their FILETIME numbers: StrToTimestamp format 15 reads them).</summary>
        public static Dictionary<string, string> Row(Item it, long s, long e)
        {
            string N(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = N(it.Id),
                ["EQUIPMENT"] = it.Equipment,
                ["STATE"] = it.State,
                ["START"] = N(it.Start),
                ["END"] = N(it.End),
                ["OCCURRENCESTART"] = N(s),
                ["OCCURRENCEEND"] = N(e),
                ["DESC"] = it.Desc,
                ["MODIFIEDTIME"] = N(it.Modified),
                ["FREQ"] = N(it.Freq),
                ["INTERVAL"] = N(it.Interval),
                ["WEEKDAY"] = N(it.Weekday),
                ["WEEKDAYMASK"] = N(it.WeekdayMask),
                ["MAXREC"] = N(it.MaxRec),
                ["RECUNTIL"] = N(it.RecUntil),
                ["DAYORD"] = N(it.DayOrd),
                ["DAY"] = N(it.DayOfMonth),
                ["MONTH"] = N(it.Month),
                ["SPECIALINC"] = N(it.SpecialInc),
                ["GROUPIDS"] = it.GroupIds,
                ["CLUSTER"] = it.Cluster,
            };
        }

        /// <summary>The rows of the occurrences in [from, to) of the items <paramref name="keep"/>
        /// accepts (by start).</summary>
        public List<Dictionary<string, string>> Rows(long from, long to, Func<Item, bool> keep)
        {
            var occ = new List<(long, Item, long)>();
            foreach (var it in Items())
            {
                if (!keep(it))
                    continue;
                foreach (var (s, e) in Occurrences(it, from, to))
                    occ.Add((s, it, e));
            }
            occ.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            var rows = new List<Dictionary<string, string>>();
            foreach (var (s, it, e) in occ)
                rows.Add(Row(it, s, e));
            return rows;
        }
    }
}
