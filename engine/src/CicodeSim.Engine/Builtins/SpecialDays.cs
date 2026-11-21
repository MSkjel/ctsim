using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// The scheduler's special days, kept for the simulation: special-day groups (a holiday, a
    /// shutdown) and their dated items, added, changed and removed by Cicode and browsed with the
    /// SchdSpecial* / SchdSpecialItem* sessions. Days are FILETIME timestamps; the browse's Day
    /// field is the number (TimestampToStr format 15). The equipment scheduler's occurrences do
    /// not skip special days.
    /// </summary>
    public static class SpecialDays
    {
        private const long Day = 864000000000L;

        private sealed class Group
        {
            public long Id;
            public string Name = "",
                Cluster = "";
        }

        private sealed class Item
        {
            public long Id,
                GroupId,
                Day;
            public string Name = "";
        }

        private sealed class Store
        {
            public readonly List<Group> Groups = new List<Group>();
            public readonly List<Item> Items = new List<Item>();
            public long Next = 1;
        }

        private static readonly ConditionalWeakTable<SimHost, Store> Stores =
            new ConditionalWeakTable<SimHost, Store>();

        private static Store Of(SimHost h) => Stores.GetValue(h, _ => new Store());

        private static string Arg(in ArgList a, int k) =>
            k < a.Count && !a.Get(k).IsNothing ? A.S(a.Get(k)) : "";

        private static long DayOf(in Value v) => (long)A.U64(v) / Day * Day;

        private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

        private static Value Refused(CicodeTask t, Value result)
        {
            W.Raise(t, t.Foreground ? Codes.FgBlock : Codes.InvalidArg);
            return result;
        }

        // ------------------------------------------------------------------ the sessions
        [Builtin(
            "SchdSpecialOpen",
            Doc = "1189335",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = "([Filter] [,Fields] [,Clusters]): a session over the simulation's special-day groups (fields ID, NAME, CLUSTER), by ID; SchdSpecialFirst / Next / GetField read it."
        )]
        private static Value SchdSpecialOpen(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground)
                return Refused(t, W.I(-1));
            var rows = new List<Dictionary<string, string>>();
            foreach (var g in Of(h).Groups)
                rows.Add(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ID"] = Num(g.Id),
                        ["NAME"] = g.Name,
                        ["CLUSTER"] = g.Cluster,
                    }
                );
            return Browse.OpenRows(
                h,
                t,
                "SchdSpecial",
                rows,
                Browse.Compile(Arg(a, 0), "NAME"),
                Arg(a, 1),
                new List<(string, bool)>()
            );
        }

        [Builtin(
            "SchdSpecialItemOpen",
            Doc = "1189330",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = "([Filter] [,Fields] [,Clusters]): a session over the special days' items (fields ID, GROUPID, GROUPNAME, NAME, DAY: the FILETIME number), by day; SchdSpecialItemFirst / Next / GetField read it."
        )]
        private static Value SchdSpecialItemOpen(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground)
                return Refused(t, W.I(-1));
            var st = Of(h);
            var items = new List<Item>(st.Items);
            items.Sort((x, y) => x.Day != y.Day ? x.Day.CompareTo(y.Day) : x.Id.CompareTo(y.Id));
            var rows = new List<Dictionary<string, string>>();
            foreach (var it in items)
            {
                var g = st.Groups.Find(x => x.Id == it.GroupId);
                rows.Add(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ID"] = Num(it.Id),
                        ["GROUPID"] = Num(it.GroupId),
                        ["GROUPNAME"] = g?.Name ?? "",
                        ["NAME"] = it.Name,
                        ["DAY"] = Num(it.Day),
                    }
                );
            }
            return Browse.OpenRows(
                h,
                t,
                "SchdSpecialItem",
                rows,
                Browse.Compile(Arg(a, 0), "NAME"),
                Arg(a, 1),
                new List<(string, bool)>()
            );
        }

        // ------------------------------------------------------------------ the groups
        [Builtin(
            "SchdSpecialAdd",
            Doc = "1189315",
            Blocking = true,
            Errors = "274 an empty name (raised, -1); 282 in foreground code (raised, -1)",
            Notes = "(Cluster, Name): a new special-day group of the simulation: its ID."
        )]
        private static Value SchdSpecialAdd(SimHost h, CicodeTask t, ArgList a)
        {
            string name = Arg(a, 1).Trim();
            if (t.Foreground || name.Length == 0)
                return Refused(t, W.I(-1));
            var st = Of(h);
            var g = new Group
            {
                Id = st.Next++,
                Name = name,
                Cluster = Arg(a, 0),
            };
            st.Groups.Add(g);
            return W.BlockOneTurn(t, "SchdSpecialAdd", () => W.I(g.Id));
        }

        [Builtin(
            "SchdSpecialModify",
            Doc = "1189332",
            Blocking = true,
            Errors = "274 no such group or an empty name (raised, -1); 282 in foreground code (raised, -1)",
            Notes = "(Cluster, ID, Name) (argument order: verify): renames a special-day group: 0."
        )]
        private static Value SchdSpecialModify(SimHost h, CicodeTask t, ArgList a)
        {
            var g = Of(h).Groups.Find(x => x.Id == A.I(a.Get(1)));
            string name = Arg(a, 2).Trim();
            if (t.Foreground || g == null || name.Length == 0)
                return Refused(t, W.I(-1));
            g.Name = name;
            return W.BlockOneTurn(t, "SchdSpecialModify", () => W.I(0));
        }

        [Builtin(
            "SchdSpecialDelete",
            Doc = "1190689",
            Blocking = true,
            Errors = "274 no such group (raised, -1); 282 in foreground code (raised, -1)",
            Notes = "(Cluster, ID): removes a special-day group and its items: 0."
        )]
        private static Value SchdSpecialDelete(SimHost h, CicodeTask t, ArgList a)
        {
            var st = Of(h);
            long id = A.I(a.Get(1));
            if (t.Foreground || st.Groups.RemoveAll(x => x.Id == id) == 0)
                return Refused(t, W.I(-1));
            st.Items.RemoveAll(x => x.GroupId == id);
            return W.BlockOneTurn(t, "SchdSpecialDelete", () => W.I(0));
        }

        // ------------------------------------------------------------------ the items
        [Builtin(
            "SchdSpecialItemAdd",
            Doc = "1189319",
            Blocking = true,
            Errors = "274 no such group (raised, -1); 282 in foreground code (raised, -1)",
            Notes = "(Cluster, GroupID, Name, Day): a day of a special-day group (the Day timestamp's date): its ID."
        )]
        private static Value SchdSpecialItemAdd(SimHost h, CicodeTask t, ArgList a)
        {
            var st = Of(h);
            long group = A.I(a.Get(1));
            if (t.Foreground || !st.Groups.Exists(x => x.Id == group))
                return Refused(t, W.I(-1));
            var it = new Item
            {
                Id = st.Next++,
                GroupId = group,
                Name = Arg(a, 2),
                Day = DayOf(a.Get(3)),
            };
            st.Items.Add(it);
            return W.BlockOneTurn(t, "SchdSpecialItemAdd", () => W.I(it.Id));
        }

        [Builtin(
            "SchdSpecialItemModify",
            Doc = "1189326",
            Blocking = true,
            Errors = "274 no such item (raised, -1); 282 in foreground code (raised, -1)",
            Notes = "(Cluster, ID, Name, Day) (argument order: verify): changes an item's name and day: 0."
        )]
        private static Value SchdSpecialItemModify(SimHost h, CicodeTask t, ArgList a)
        {
            var it = Of(h).Items.Find(x => x.Id == A.I(a.Get(1)));
            if (t.Foreground || it == null)
                return Refused(t, W.I(-1));
            it.Name = Arg(a, 2);
            if (a.Count > 3 && !a.Get(3).IsNothing)
                it.Day = DayOf(a.Get(3));
            return W.BlockOneTurn(t, "SchdSpecialItemModify", () => W.I(0));
        }

        [Builtin(
            "SchdSpecialItemDelete",
            Doc = "1189322",
            Blocking = true,
            Errors = "274 no such item (raised, -1); 282 in foreground code (raised, -1)",
            Notes = "(Cluster, ID): removes an item: 0."
        )]
        private static Value SchdSpecialItemDelete(SimHost h, CicodeTask t, ArgList a)
        {
            long id = A.I(a.Get(1));
            if (t.Foreground || Of(h).Items.RemoveAll(x => x.Id == id) == 0)
                return Refused(t, W.I(-1));
            return W.BlockOneTurn(t, "SchdSpecialItemDelete", () => W.I(0));
        }

        private static IEnumerable<long> Days(in Value from, in Value to)
        {
            long a = DayOf(from),
                b = DayOf(to);
            var list = new List<long>();
            for (long d = a; d <= b && list.Count < 3660; d += Day)
                list.Add(d);
            return list;
        }

        [Builtin(
            "SchdSpecialItemAddRange",
            Doc = "1189320",
            Blocking = true,
            Errors = "274 no such group (raised and returned); 282 in foreground code (raised and returned)",
            Notes = "(Cluster, GroupID, Name, StartDay, EndDay) (argument order: verify): an item for each day from StartDay to EndDay (at most ten years): 0."
        )]
        private static Value SchdSpecialItemAddRange(SimHost h, CicodeTask t, ArgList a)
        {
            var st = Of(h);
            long group = A.I(a.Get(1));
            if (t.Foreground || !st.Groups.Exists(x => x.Id == group))
                return Refused(t, W.I(t.Foreground ? Codes.FgBlock : Codes.InvalidArg));
            foreach (long d in Days(a.Get(3), a.Get(4)))
                st.Items.Add(
                    new Item
                    {
                        Id = st.Next++,
                        GroupId = group,
                        Name = Arg(a, 2),
                        Day = d,
                    }
                );
            return W.BlockOneTurn(t, "SchdSpecialItemAddRange", () => W.I(0));
        }

        [Builtin(
            "SchdSpecialItemDeleteRange",
            Doc = "1189323",
            Blocking = true,
            Errors = "274 no such group (raised and returned); 282 in foreground code (raised and returned)",
            Notes = "(Cluster, GroupID, StartDay, EndDay) (argument order: verify): removes the group's items from StartDay to EndDay: 0."
        )]
        private static Value SchdSpecialItemDeleteRange(SimHost h, CicodeTask t, ArgList a)
        {
            var st = Of(h);
            long group = A.I(a.Get(1));
            if (t.Foreground || !st.Groups.Exists(x => x.Id == group))
                return Refused(t, W.I(t.Foreground ? Codes.FgBlock : Codes.InvalidArg));
            long from = DayOf(a.Get(2)),
                to = DayOf(a.Get(3));
            st.Items.RemoveAll(x => x.GroupId == group && x.Day >= from && x.Day <= to);
            return W.BlockOneTurn(t, "SchdSpecialItemDeleteRange", () => W.I(0));
        }

        [Builtin(
            "SchdSpecialItemModifyRange",
            Doc = "1189327",
            Blocking = true,
            Errors = "274 no such group (raised and returned); 282 in foreground code (raised and returned)",
            Notes = "(Cluster, GroupID, Name, StartDay, EndDay) (argument order: verify): renames the group's items from StartDay to EndDay: 0."
        )]
        private static Value SchdSpecialItemModifyRange(SimHost h, CicodeTask t, ArgList a)
        {
            var st = Of(h);
            long group = A.I(a.Get(1));
            if (t.Foreground || !st.Groups.Exists(x => x.Id == group))
                return Refused(t, W.I(t.Foreground ? Codes.FgBlock : Codes.InvalidArg));
            long from = DayOf(a.Get(3)),
                to = DayOf(a.Get(4));
            foreach (var it in st.Items)
                if (it.GroupId == group && it.Day >= from && it.Day <= to)
                    it.Name = Arg(a, 2);
            return W.BlockOneTurn(t, "SchdSpecialItemModifyRange", () => W.I(0));
        }
    }
}
