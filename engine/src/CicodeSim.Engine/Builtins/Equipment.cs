using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;

namespace CicodeSim.Builtins
{
    /// <summary>A browse session (TagBrowse*, EquipBrowse*, EquipRefBrowse*, EquipStateBrowse*):
    /// the matching records in order (indexes into the source the getter reads, so a browse of
    /// a million tags holds an int a record) and a cursor (before the first record until First).</summary>
    internal sealed class BrowseSession
    {
        public string Kind = "";
        public int[] Order = Array.Empty<int>();

        /// <summary>(source index, FIELD in upper case) to the value, null for an unknown field.</summary>
        public Func<int, string, string?> Get = (i, f) => null;

        /// <summary>The fields the session returns (null = all).</summary>
        public HashSet<string>? Fields;
        public int Cursor = -1;
        public int Count => Order.Length;
    }

    /// <summary>Browse filters and sorting.</summary>
    internal static class Browse
    {
        private static readonly Regex CondRx = new Regex(
            @"^\s*([A-Za-z_][A-Za-z0-9_ ]*?)\s*(<>|<=|>=|=|<|>)\s*(.*?)\s*$",
            RegexOptions.CultureInvariant
        );
        private static readonly Regex OrRx = new Regex(
            @"\s+OR\s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

        /// <summary>One condition of a compiled filter.</summary>
        internal sealed class Cond
        {
            public string Field = "";
            public string Op = "=";
            public bool Neg;
            public Regex[] Alts = Array.Empty<Regex>();
            public bool AnyValue;

            /// <summary>The filter value has no terms, so the condition holds for every record.</summary>
            public bool NoTerms;
            public double Number;
            public bool HasNumber;
        }

        /// <summary>A filter: every group must hold; a group holds when one of its conditions does.</summary>
        internal sealed class Filter
        {
            public readonly List<Cond[]> Groups = new List<Cond[]>();
            public readonly HashSet<string> Fields = new HashSet<string>(StringComparer.Ordinal);
        }

        private static Regex GlobRx(string pattern) =>
            new Regex(
                "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline
            );

        private static bool Num(string s, out double d) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);

        public static Filter Compile(string filter, string defField, bool noTermMatchesAll = false)
        {
            var f = new Filter();
            if (string.IsNullOrWhiteSpace(filter))
                return f;
            foreach (string part in filter.Split(';'))
            {
                if (part.Trim().Length == 0)
                    continue;
                var group = new List<Cond>();
                foreach (string alt in OrRx.Split(part))
                {
                    var c = new Cond();
                    string value;
                    var m = CondRx.Match(alt);
                    if (m.Success)
                    {
                        c.Field = m.Groups[1].Value.Trim().ToUpperInvariant();
                        c.Op = m.Groups[2].Value;
                        value = m.Groups[3].Value;
                    }
                    else
                    {
                        c.Field = defField;
                        value = alt.Trim();
                    }
                    if (c.Op == "=" || c.Op == "<>")
                    {
                        c.Neg = c.Op == "<>";
                        if (value.StartsWith("NOT ", StringComparison.OrdinalIgnoreCase))
                        {
                            c.Neg = !c.Neg;
                            value = value.Substring(4).Trim();
                        }
                        if (noTermMatchesAll && value.Trim(' ', ',', '\t', '\n').Length == 0)
                            c.NoTerms = true;
                        var alts = new List<Regex>();
                        foreach (string v in value.Split('|'))
                        {
                            string x = v.Trim();
                            if (x == "*")
                                c.AnyValue = true;
                            alts.Add(GlobRx(x));
                        }
                        c.Alts = alts.ToArray();
                    }
                    else
                        c.HasNumber = Num(value, out c.Number);
                    f.Fields.Add(c.Field);
                    group.Add(c);
                }
                f.Groups.Add(group.ToArray());
            }
            return f;
        }

        private static bool Holds(Cond c, Func<string, string?> get)
        {
            if (c.NoTerms || (c.AnyValue && !c.Neg))
                return true;
            string have = get(c.Field) ?? "";
            if (c.Op == "=" || c.Op == "<>")
            {
                bool hit = false;
                foreach (var rx in c.Alts)
                    if (rx.IsMatch(have))
                    {
                        hit = true;
                        break;
                    }
                return hit != c.Neg;
            }
            if (!c.HasNumber || !Num(have, out double a))
                return false;
            switch (c.Op)
            {
                case "<":
                    return a < c.Number;
                case "<=":
                    return a <= c.Number;
                case ">":
                    return a > c.Number;
                default:
                    return a >= c.Number;
            }
        }

        public static bool Match(Filter f, Func<string, string?> get)
        {
            foreach (var g in f.Groups)
            {
                bool any = false;
                foreach (var c in g)
                    if (Holds(c, get))
                    {
                        any = true;
                        break;
                    }
                if (!any)
                    return false;
            }
            return true;
        }

        /// <summary>Every field the filter names is known (an unknown one makes the open fail, doc).</summary>
        public static bool FieldsKnown(Filter f, ICollection<string> known)
        {
            foreach (string x in f.Fields)
                if (!known.Contains(x))
                    return false;
            return true;
        }

        /// <summary>The sort spec: 'FIELD[:A|:D], ...' then the default keys ascending.</summary>
        public static List<(string Field, bool Desc)> SortSpec(string sort, params string[] keys)
        {
            var spec = new List<(string, bool)>();
            foreach (string s in (sort ?? "").Split(','))
            {
                string x = s.Trim();
                if (x.Length == 0)
                    continue;
                bool desc = false;
                int c = x.LastIndexOf(':');
                if (c > 0)
                {
                    desc = x.Substring(c + 1).Trim().StartsWith("D", StringComparison.OrdinalIgnoreCase);
                    x = x.Substring(0, c).Trim();
                }
                spec.Add((x.ToUpperInvariant(), desc));
            }
            foreach (string k in keys)
                spec.Add((k, false));
            return spec;
        }

        /// <summary>Sort the matches (stable), reading each sort field only when needed.</summary>
        public static int[] Sorted(
            List<int> matches,
            Func<int, string, string?> get,
            List<(string Field, bool Desc)> spec
        )
        {
            int n = matches.Count;
            var keys = new string?[spec.Count][];
            for (int k = 0; k < spec.Count; k++)
                keys[k] = new string?[n];
            string Key(int k, int i) => keys[k][i] ??= get(matches[i], spec[k].Field) ?? "";
            int Cmp(int i, int j)
            {
                for (int k = 0; k < spec.Count; k++)
                {
                    string x = Key(k, i),
                        y = Key(k, j);
                    int r =
                        Num(x, out double dx) && Num(y, out double dy)
                            ? dx.CompareTo(dy)
                            : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                    if (r != 0)
                        return spec[k].Desc ? -r : r;
                }
                return i.CompareTo(j);
            }
            var idx = new int[n];
            for (int i = 0; i < n; i++)
                idx[i] = i;
            var tmp = new int[n];
            for (int width = 1; width < n; width *= 2)
            {
                for (int lo = 0; lo < n; lo += 2 * width)
                {
                    int mid = Math.Min(lo + width, n),
                        hi = Math.Min(lo + 2 * width, n);
                    int a = lo,
                        b = mid,
                        o = lo;
                    while (a < mid && b < hi)
                        tmp[o++] = Cmp(idx[b], idx[a]) < 0 ? idx[b++] : idx[a++];
                    while (a < mid)
                        tmp[o++] = idx[a++];
                    while (b < hi)
                        tmp[o++] = idx[b++];
                }
                var sw = idx;
                idx = tmp;
                tmp = sw;
            }
            var order = new int[n];
            for (int i = 0; i < n; i++)
                order[i] = matches[idx[i]];
            return order;
        }

        public static HashSet<string>? FieldSet(string fields)
        {
            if (string.IsNullOrWhiteSpace(fields))
                return null;
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in fields.Split(','))
                if (f.Trim().Length > 0)
                    s.Add(f.Trim().ToUpperInvariant());
            return s;
        }

        /// <summary>A session over rows of dictionaries (the small equipment tables).</summary>
        public static Value OpenRows(
            SimHost h,
            CicodeTask t,
            string kind,
            List<Dictionary<string, string>> rows,
            Filter filter,
            string fields,
            List<(string, bool)> spec
        )
        {
            var m = new List<int>();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (Match(filter, f => r.TryGetValue(f, out string? v) ? v : null))
                    m.Add(i);
            }
            Func<int, string, string?> get = (i, f) => rows[i].TryGetValue(f, out string? v) ? v : null;
            return Open(h, t, kind, Sorted(m, get, spec), get, fields);
        }

        // -- the session functions (shared by every browse kind)
        public static BrowseSession? Session(SimHost h, in Value v, string kind)
        {
            var s = h.Browses.Get(A.I(v));
            return s != null && s.Kind == kind ? s : null;
        }

        public static Value Move(SimHost h, CicodeTask t, ArgList a, string kind, int how, bool blocking)
        {
            var s = Session(h, a.Get(0), kind);
            if (s == null)
                return W.I(Codes.BrowseSessionNotFound);
            if (blocking && t.Foreground)
                return W.I(Codes.FgBlock);
            int c =
                how == 0 ? 0
                : how > 0 ? s.Cursor + 1
                : s.Cursor - 1;
            if (c < 0 || c >= s.Count)
            {
                if (how != 0)
                    s.Cursor = c < 0 ? -1 : s.Count;
                return W.I(Codes.BrowseEof);
            }
            s.Cursor = c;
            return W.I(0);
        }

        public static Value GetField(SimHost h, CicodeTask t, ArgList a, string kind)
        {
            var s = Session(h, a.Get(0), kind);
            string f = A.S(a.Get(1)).Trim().ToUpperInvariant();
            if (s == null || s.Cursor < 0 || s.Cursor >= s.Count)
            {
                W.Raise(t, s == null ? Codes.BrowseSessionNotFound : Codes.BrowseEof);
                return W.Empty;
            }
            string? v = s.Fields != null && !s.Fields.Contains(f) ? null : s.Get(s.Order[s.Cursor], f);
            if (v == null)
            {
                W.Raise(t, Codes.BrowseFieldNotFound);
                return W.Empty;
            }
            return W.S(A.Cap(v));
        }

        public static Value Count(SimHost h, CicodeTask t, ArgList a, string kind)
        {
            var s = Session(h, a.Get(0), kind);
            if (s == null)
            {
                W.Raise(t, Codes.BrowseSessionNotFound);
                return W.I(-1);
            }
            return W.I(s.Count);
        }

        public static Value Close(SimHost h, CicodeTask t, ArgList a, string kind)
        {
            long hd = A.I(a.Get(0));
            var s = h.Browses.Get(hd);
            if (s == null || s.Kind != kind)
                return W.I(Codes.BrowseSessionNotFound);
            h.Browses.Free(hd);
            return W.I(0);
        }

        /// <summary>Open a session (one turn: the browse request goes to a server).</summary>
        public static Value Open(
            SimHost h,
            CicodeTask t,
            string kind,
            int[] order,
            Func<int, string, string?> get,
            string fields
        )
        {
            if (h.Browses.Count >= SimHost.MaxBrowses)
            {
                W.Raise(t, Codes.NoHandles);
                return W.I(-1);
            }
            var s = new BrowseSession
            {
                Kind = kind,
                Order = order,
                Get = get,
                Fields = FieldSet(fields),
            };
            int hd = h.Browses.Add(s);
            return W.BlockOneTurn(t, kind + "BrowseOpen", () => W.I(hd));
        }
    }

    /// <summary>Equipment built-ins over equip.dbf, eqref.dbf, eqstate.dbf and eqparam.dbf.</summary>
    public static class Equipment
    {
        private static readonly string[] EquipFields =
        {
            "NAME",
            "CLUSTER",
            "TYPE",
            "AREA",
            "LOCATION",
            "IODEVICE",
            "PAGE",
            "HELP",
            "COMMENT",
            "COMPOSITE",
            "PARENT",
            "ALIAS",
            "CONTENT",
            "HIDDEN",
            "CUSTOM1",
            "CUSTOM2",
            "CUSTOM3",
            "CUSTOM4",
            "CUSTOM5",
            "CUSTOM6",
            "CUSTOM7",
            "CUSTOM8",
            "DEFSTATE",
            "SCHEDULED",
        };

        /// <summary>An equipment browse field (null = not one of the fields).</summary>
        private static string? EquipBrowseField(EquipDef e, string f)
        {
            if (f == "NAME")
                return e.Name;
            if (f == "PARENT")
                return e.Parent;
            return Array.IndexOf(EquipFields, f) >= 0 ? e.Get(f) : null;
        }

        private static bool BrowseBlocked(CicodeTask t)
        {
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return true;
            }
            return false;
        }

        [Builtin(
            "EquipBrowseOpen",
            Doc = "1105202",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1); 274 an unknown filter field (raised, -1)",
            Notes = "Equipment of equip.dbf (the include chain's, the first definition of a name), sorted by name; filter as Browse; sClusters checks CLUSTER."
        )]
        private static Value EquipBrowseOpen(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("EquipBrowseOpen");
            if (BrowseBlocked(t))
                return W.I(-1);
            string filter = A.S(a.Get(0)),
                fields = A.S(a.Get(1)),
                clusters = A.S(a.Get(2));
            var fl = Browse.Compile(filter, "NAME");
            if (!Browse.FieldsKnown(fl, EquipFields))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var cl = Browse.FieldSet(clusters);
            // the session holds indexes into the World's equipment list, not copies
            var all = w.Equipment.InOrder();
            Func<int, string, string?> get = (i, f) => EquipBrowseField(all[i], f);
            var m = new List<int>();
            for (int i = 0; i < all.Count; i++)
            {
                if (cl != null && !cl.Contains(all[i].Cluster.ToUpperInvariant()))
                    continue;
                int k = i;
                if (fl.Groups.Count == 0 || Browse.Match(fl, f => get(k, f)))
                    m.Add(i);
            }
            return Browse.Open(
                h,
                t,
                "Equip",
                Browse.Sorted(m, get, Browse.SortSpec("", "NAME")),
                get,
                fields
            );
        }

        [Builtin(
            "EquipBrowseFirst",
            Doc = "1105198",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)"
        )]
        private static Value EquipBrowseFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Equip", 0, true);

        [Builtin(
            "EquipBrowseNext",
            Doc = "1105200",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)"
        )]
        private static Value EquipBrowseNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Equip", 1, true);

        [Builtin(
            "EquipBrowsePrev",
            Doc = "1105203",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)"
        )]
        private static Value EquipBrowsePrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Equip", -1, true);

        [Builtin(
            "EquipBrowseGetField",
            Doc = "1105199",
            Errors = "410 bad session; 412 no current record; 413 unknown field (raised, '' returned)"
        )]
        private static Value EquipBrowseGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "Equip");

        [Builtin("EquipBrowseNumRecords", Doc = "1105201", Errors = "410 bad session (raised, -1)")]
        private static Value EquipBrowseNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "Equip");

        [Builtin("EquipBrowseClose", Doc = "1105197", Errors = "410 bad session (returned)")]
        private static Value EquipBrowseClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "Equip");

        // -- equipment references (eqref.dbf)
        private static readonly string[] RefFields =
        {
            "EQUIP",
            "REFEQUIP",
            "REFITEM",
            "ORDER",
            "COMMENT",
            "CLUSTER",
            "REFCLUST",
            "REFCAT",
            "ASSOC",
            "CUSTOM1",
            "CUSTOM2",
            "CUSTOM3",
            "CUSTOM4",
            "CUSTOM5",
            "CUSTOM6",
            "CUSTOM7",
            "CUSTOM8",
        };

        [Builtin(
            "EquipRefBrowseOpen",
            Doc = "1105212",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1); 274 an unknown filter field (raised, -1)",
            Notes = "Rows of eqref.dbf (every project of the unit, compile order); a filter value without a field is EQUIP; sorted by sSort then EQUIP, ORDER."
        )]
        private static Value EquipRefBrowseOpen(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("EquipRefBrowseOpen");
            if (BrowseBlocked(t))
                return W.I(-1);
            string filter = A.S(a.Get(0)),
                fields = A.S(a.Get(1)),
                sort = A.S(a.Get(2));
            var fl = Browse.Compile(filter, "EQUIP");
            if (!Browse.FieldsKnown(fl, RefFields.Concat(new[] { "NAME" }).ToArray()))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var rows = h.TableRows("eqref.dbf", () =>
            {
                var l = new List<Dictionary<string, string>>();
                foreach (var (_, row) in w.Unit.Rows("eqref.dbf"))
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string f in RefFields)
                        d[f] = row.Get(f);
                    d["NAME"] = d["EQUIP"];
                    l.Add(d);
                }
                return l;
            });
            return Browse.OpenRows(
                h,
                t,
                "EquipRef",
                rows,
                fl,
                fields,
                Browse.SortSpec(sort, "EQUIP", "ORDER")
            );
        }

        [Builtin(
            "EquipRefBrowseFirst",
            Doc = "1105208",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)"
        )]
        private static Value EquipRefBrowseFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "EquipRef", 0, true);

        [Builtin(
            "EquipRefBrowseNext",
            Doc = "1105210",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)"
        )]
        private static Value EquipRefBrowseNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "EquipRef", 1, true);

        [Builtin(
            "EquipRefBrowsePrev",
            Doc = "1105213",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)"
        )]
        private static Value EquipRefBrowsePrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "EquipRef", -1, true);

        [Builtin(
            "EquipRefBrowseGetField",
            Doc = "1105209",
            Errors = "410 bad session; 412 no current record; 413 unknown field (raised, '' returned)"
        )]
        private static Value EquipRefBrowseGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "EquipRef");

        [Builtin("EquipRefBrowseNumRecords", Doc = "1105211", Errors = "410 bad session (raised, -1)")]
        private static Value EquipRefBrowseNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "EquipRef");

        [Builtin("EquipRefBrowseClose", Doc = "1105207", Errors = "410 bad session (returned)")]
        private static Value EquipRefBrowseClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "EquipRef");

        // -- equipment states (eqstate.dbf)
        private static readonly string[] StateFields =
        {
            "NAME",
            "CLUSTER",
            "EQUIPMENT",
            "ENTRY",
            "DELAY",
            "PRESTATE",
            "REPEAT",
            "PERIOD",
            "DESC",
            "PRIORITY",
            "DRMODE",
        };

        [Builtin(
            "EquipStateBrowseOpen",
            Doc = "1105219",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1); 274 an unknown filter field (raised, -1)",
            Notes = "Rows of eqstate.dbf; a filter value without a field is EQUIPMENT; sorted by EQUIPMENT, NAME (verify the order)."
        )]
        private static Value EquipStateBrowseOpen(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("EquipStateBrowseOpen");
            if (BrowseBlocked(t))
                return W.I(-1);
            string filter = A.S(a.Get(0)),
                fields = A.S(a.Get(1));
            var fl = Browse.Compile(filter, "EQUIPMENT");
            if (!Browse.FieldsKnown(fl, StateFields))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var rows = h.TableRows("eqstate.dbf", () =>
            {
                var l = new List<Dictionary<string, string>>();
                foreach (var (_, row) in w.Unit.Rows("eqstate.dbf"))
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string f in StateFields)
                        d[f] = row.Get(f);
                    l.Add(d);
                }
                return l;
            });
            return Browse.OpenRows(
                h,
                t,
                "EquipState",
                rows,
                fl,
                fields,
                Browse.SortSpec("", "EQUIPMENT", "NAME")
            );
        }

        [Builtin(
            "EquipStateBrowseFirst",
            Doc = "1105216",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)"
        )]
        private static Value EquipStateBrowseFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "EquipState", 0, true);

        [Builtin(
            "EquipStateBrowseNext",
            Doc = "1105218",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)"
        )]
        private static Value EquipStateBrowseNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "EquipState", 1, true);

        [Builtin(
            "EquipStateBrowsePrev",
            Doc = "1105220",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)"
        )]
        private static Value EquipStateBrowsePrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "EquipState", -1, true);

        [Builtin(
            "EquipStateBrowseGetField",
            Doc = "1105217",
            Errors = "410 bad session; 412 no current record; 413 unknown field (raised, '' returned)"
        )]
        private static Value EquipStateBrowseGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "EquipState");

        [Builtin("EquipStateBrowseNumRecords", Doc = "1105218", Errors = "410 bad session (raised, -1)")]
        private static Value EquipStateBrowseNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "EquipState");

        [Builtin("EquipStateBrowseClose", Doc = "1105215", Errors = "410 bad session (returned)")]
        private static Value EquipStateBrowseClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "EquipState");

        // -- properties
        private static readonly HashSet<string> RuntimeFields = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "MODE",
            "DRMODE",
            "STATE",
        };

        private static string? EquipField(SimHost h, EquipDef e, string field)
        {
            string f = field.Trim().ToUpperInvariant();
            if (RuntimeFields.Contains(f))
                return h.World != null ? h.World.Equipment.Runtime(e, f)
                    : f == "STATE" ? e.Get("DEFSTATE")
                    : "0";
            if (f == "PARENT")
                return e.Parent;
            if (f == "NAME")
                return e.Name;
            if (Array.IndexOf(EquipFields, f) < 0)
                return null;
            return e.Get(f);
        }

        private static EquipDef? FindEquip(World.World w, string name, string cluster)
        {
            var e = w.Equipment.Get(name);
            if (e == null && cluster.Length == 0)
            {
                int dot = name.IndexOf('.');
                if (dot > 0 && w.Tags.Clusters().Contains(name.Substring(0, dot).ToLowerInvariant()))
                    e = w.Equipment.Get(name.Substring(dot + 1));
            }
            if (
                e != null
                && cluster.Length > 0
                && e.Cluster.Length > 0
                && !string.Equals(e.Cluster, cluster, StringComparison.OrdinalIgnoreCase)
            )
                return null;
            return e;
        }

        [Builtin(
            "EquipGetProperty",
            Doc = "1105206",
            Blocking = true,
            Errors = "274 bad mode; 289 unknown equipment; 348 unknown field, or a runtime field in mode 2; 282 mode 0 in foreground code (raised, '' returned)",
            Notes = "Fields of equip.dbf (+ PARENT); runtime fields MODE / DRMODE (default 0) and STATE (default DEFSTATE) as EquipSetProperty left them; mode 0 blocks one turn (the report server), 1 and 3 answer from the cache at once, 2 from the local tables."
        )]
        private static Value EquipGetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("EquipGetProperty");
            string name = A.S(a.Get(0)),
                field = A.S(a.Get(1));
            long mode = a.Count > 2 ? A.Short(A.I(a.Get(2))) : 3;
            string cluster = A.S(a.Get(3));
            if (mode < 0 || mode > 3)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            Value Now()
            {
                var e = FindEquip(w, name, cluster);
                if (e == null)
                {
                    W.Raise(t, Codes.NameNotFound);
                    return W.Empty;
                }
                if (mode == 2 && RuntimeFields.Contains(field.Trim()))
                {
                    W.Raise(t, Codes.PropertyNotFound);
                    return W.Empty;
                }
                string? v = EquipField(h, e, field);
                if (v == null)
                {
                    W.Raise(t, Codes.PropertyNotFound);
                    return W.Empty;
                }
                return W.S(A.Cap(v));
            }
            if (mode == 0)
            {
                if (t.Foreground)
                {
                    W.Raise(t, Codes.FgBlock);
                    return W.Empty;
                }
                return W.BlockOneTurn(t, "EquipGetProperty(" + name + ")", () => Now());
            }
            return Now();
        }

        [Builtin(
            "EquipSetProperty",
            Doc = "1105214",
            Blocking = true,
            Effects = "sets the equipment's runtime MODE, STATE or DRMODE",
            Errors = "289 unknown equipment; 274 a field other than MODE / STATE / DRMODE, or STATE when MODE is not 2; 282 in foreground code (returned)",
            Notes = "Blocks one turn (the scheduler engine)."
        )]
        private static Value EquipSetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("EquipSetProperty");
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            string name = A.S(a.Get(0)),
                field = A.S(a.Get(1)).Trim().ToUpperInvariant(),
                value = A.S(a.Get(2));
            var e = FindEquip(w, name, A.S(a.Get(3)));
            if (e == null)
                return W.I(Codes.NameNotFound);
            if (!RuntimeFields.Contains(field))
                return W.I(Codes.InvalidArg);
            if (field == "STATE" && EquipField(h, e, "MODE") != "2")
                return W.I(Codes.InvalidArg);
            w.Equipment.SetRuntime(e, field, value);
            return W.BlockOneTurn(t, "EquipSetProperty(" + name + ")", () => W.I(0));
        }

        [Builtin(
            "EquipGetParameter",
            Doc = "1105205",
            Blocking = true,
            Errors = "289 unknown equipment or parameter (raised, '' returned); 282 in foreground code (raised)",
            Notes = "eqparam.dbf then eqconfig.dbf (EquipDb.Parameter); blocks one turn."
        )]
        private static Value EquipGetParameter(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("EquipGetParameter");
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.Empty;
            }
            string equip = A.S(a.Get(1)),
                pname = A.S(a.Get(2));
            return W.BlockOneTurn(t, "EquipGetParameter(" + equip + ")", () =>
            {
                string? v = w.Equipment.Parameter(equip, pname);
                if (v == null)
                {
                    W.Raise(t, Codes.NameNotFound);
                    return W.Empty;
                }
                return W.S(A.Cap(v));
            });
        }

        [Builtin(
            "EquipCheckUpdate",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1105204",
            Notes = "The simulator reads the tables once: never changed, returns 0."
        )]
        private static Value EquipCheckUpdate(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        // ------------------------------------------------------------------ tag browse
        internal static readonly string[] TagFields =
        {
            "TAG",
            "CLUSTER",
            "TYPE",
            "ADDR",
            "ARR_SIZE",
            "COMMENT",
            "DEADBAND",
            "ENG_FULL",
            "ENG_UNITS",
            "ENG_ZERO",
            "EQUIPMENT",
            "ITEM",
            "FORMAT",
            "FULLNAME",
            "IODEV",
            "RAW_FULL",
            "RAW_ZERO",
            "SCALED_TYPE",
            "PSI_TYPE",
            "LOG_UNIT",
            "NET_UNIT",
            "CUSTOM1",
            "CUSTOM2",
            "CUSTOM3",
            "CUSTOM4",
            "CUSTOM5",
            "CUSTOM6",
            "CUSTOM7",
            "CUSTOM8",
            "VALUE",
            "FIELD",
            "OVERRIDE",
            "VALID",
            "STATUS",
            "OVR_MODE",
            "CTRL_MODE",
            "NUM_SUBS",
            "TAG ITEM",
        };

        /// <summary>A tag browse field of a definition (null = not a field); the runtime fields
        /// (VALUE, FIELD ...) need only the name (<paramref name="d"/> may be null for them).</summary>
        internal static string? TagField(TagDb db, string name, TagDef? d, string f)
        {
            string? el =
                f == "VALUE" ? ""
                : f == "FIELD" ? "field"
                : f == "VALID" ? "valid"
                : f == "OVERRIDE" ? "override"
                : f == "STATUS" ? "status"
                : f == "OVR_MODE" ? "overridemode"
                : f == "CTRL_MODE" ? "controlmode"
                : null;
            if (el != null)
                return Conv.ToStr(db.Get(name, el), NoErr.Instance).S ?? "";
            if (d == null)
                return null;
            switch (f)
            {
                case "TAG":
                    return d.Name;
                case "CLUSTER":
                    return d.Cluster;
                case "TYPE":
                    return d.GeneralType.ToString(CultureInfo.InvariantCulture);
                case "ADDR":
                    return d.Address;
                case "ARR_SIZE":
                    return (d.IsArray ? d.Size : 1).ToString(CultureInfo.InvariantCulture);
                case "COMMENT":
                    return d.Comment;
                case "DEADBAND":
                    return d.Deadband.HasValue ? TagTypes.NumText(d.Deadband.Value) : "";
                case "ENG_FULL":
                    return TagTypes.NumText(d.EngRange().hi);
                case "ENG_ZERO":
                    return TagTypes.NumText(d.EngRange().lo);
                case "RAW_FULL":
                    return TagTypes.NumText(d.RawRange().hi);
                case "RAW_ZERO":
                    return TagTypes.NumText(d.RawRange().lo);
                case "ENG_UNITS":
                    return d.EngUnits;
                case "EQUIPMENT":
                    return d.Equip;
                case "ITEM":
                case "TAG ITEM":
                    return d.Item;
                case "FORMAT":
                    return d.Format;
                case "FULLNAME":
                    return d.Cluster.Length > 0 ? d.Cluster + "." + d.Name : d.Name;
                case "IODEV":
                    return d.UnitText;
                case "SCALED_TYPE":
                    return d.Scaled ? "1" : "0";
                case "PSI_TYPE":
                    return d.TypeName;
                case "LOG_UNIT":
                case "NET_UNIT":
                    return "";
                case "NUM_SUBS":
                    return "0";
            }
            if (
                f.Length == 7
                && f.StartsWith("CUSTOM", StringComparison.Ordinal)
                && f[6] >= '1'
                && f[6] <= '8'
            )
            {
                int i = f[6] - '1';
                return i < d.Custom.Length ? d.Custom[i] : "";
            }
            return null;
        }

        /// <summary>Reads the fields of tag browse records, decoding only what's asked for.</summary>
        private sealed class TagFieldReader
        {
            private readonly TagDb _db;
            private readonly List<VarTable> _tables;
            private readonly Dictionary<string, Func<int, string>>?[] _raw;
            private int _lastT = -1,
                _lastR = -1;
            private TagDef? _last;

            public TagFieldReader(TagDb db, List<VarTable> tables)
            {
                _db = db;
                _tables = tables;
                _raw = new Dictionary<string, Func<int, string>>?[tables.Count];
            }

            private string Raw(int ti, int row, string column)
            {
                var d = _raw[ti] ??= new Dictionary<string, Func<int, string>>(StringComparer.Ordinal);
                if (!d.TryGetValue(column, out var f))
                    d[column] = f = _tables[ti].RawColumn(column);
                return f(row);
            }

            public TagDef Definition(int ti, int row)
            {
                if (ti != _lastT || row != _lastR)
                {
                    _last = _tables[ti].Definition(row);
                    _lastT = ti;
                    _lastR = row;
                }
                return _last!;
            }

            public string? Field(int ti, int row, string name, string f)
            {
                switch (f)
                {
                    case "TAG":
                        return name;
                    case "CLUSTER":
                        return Raw(ti, row, "CLUSTER");
                    case "ADDR":
                        return Raw(ti, row, "ADDR");
                    case "COMMENT":
                        return Raw(ti, row, "COMMENT");
                    case "ENG_UNITS":
                        return Raw(ti, row, "ENG_UNITS");
                    case "EQUIPMENT":
                        return Raw(ti, row, "EQUIP");
                    case "ITEM":
                    case "TAG ITEM":
                        return Raw(ti, row, "ITEM");
                    case "FORMAT":
                        return Raw(ti, row, "FORMAT");
                    case "IODEV":
                        return Raw(ti, row, "UNIT");
                    case "FULLNAME":
                    {
                        string c = Raw(ti, row, "CLUSTER");
                        return c.Length > 0 ? c + "." + name : name;
                    }
                    case "LOG_UNIT":
                    case "NET_UNIT":
                        return "";
                    case "NUM_SUBS":
                        return "0";
                    case "VALUE":
                    case "FIELD":
                    case "VALID":
                    case "OVERRIDE":
                    case "STATUS":
                    case "OVR_MODE":
                    case "CTRL_MODE":
                        return TagField(_db, name, null, f);
                }
                if (
                    f.Length == 7
                    && f.StartsWith("CUSTOM", StringComparison.Ordinal)
                    && f[6] >= '1'
                    && f[6] <= '8'
                )
                    return Raw(ti, row, f);
                return Array.IndexOf(TagFields, f) >= 0 ? TagField(_db, name, Definition(ti, row), f) : null;
            }
        }

        [Builtin(
            "TagBrowseOpen",
            Doc = "1192732",
            Blocking = true,
            Errors = "282 in foreground code; 274 an unknown filter field (raised, -1)",
            Notes = "Every variable tag of the unit (variable.dbf, not local variables), filtered, sorted by sSort then CLUSTER, TAG; runtime fields (VALUE, FIELD ...) are the World's values when the session opens."
        )]
        private static Value TagBrowseOpen(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Tags.Db(h);
            if (db == null)
                return Tags.NoWorld("TagBrowseOpen");
            if (BrowseBlocked(t))
                return W.I(-1);
            string filter = A.S(a.Get(0)),
                fields = A.S(a.Get(1)),
                sort = A.S(a.Get(2)),
                clusters = A.S(a.Get(3));
            if (!Browse.FieldsKnown(Browse.Compile(filter, "TAG"), TagFields))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var cl = Browse.FieldSet(clusters);
            var recs = TagRecords.Of(h, db);
            var fl = Browse.Compile(filter, "TAG");
            var m = new List<int>();
            for (int i = 0; i < recs.Count; i++)
            {
                if (cl != null && !cl.Contains(recs.Field(i, "CLUSTER")!.ToUpperInvariant()))
                    continue;
                int k = i;
                if (fl.Groups.Count == 0 || Browse.Match(fl, f => recs.Field(k, f)))
                    m.Add(i);
            }
            // the implicit CLUSTER key only where the unit has clusters (read only when the
            // earlier keys tie: never for distinct names sorted by TAG)
            int[] order = Browse.Sorted(
                m,
                recs.Field,
                db.Clusters().Count > 1
                    ? Browse.SortSpec(sort, "CLUSTER", "TAG")
                    : Browse.SortSpec(sort, "TAG")
            );
            // the session keeps an index into the host's record list a record
            return Browse.Open(h, t, "Tag", order, recs.Field, fields);
        }

        /// <summary>The records a tag browse chooses from, built once per host.</summary>
        internal sealed class TagRecords
        {
            private int[] _tab = Array.Empty<int>(),
                _row = Array.Empty<int>();
            private string[] _name = Array.Empty<string>();
            private TagFieldReader _reader = null!;

            public int Count => _name.Length;

            public string? Field(int i, string f) => _reader.Field(_tab[i], _row[i], _name[i], f);

            public static TagRecords Of(SimHost h, TagDb db)
            {
                if (h.TagRecords is TagRecords r)
                    return r;
                var tables = db.Tables();
                var tab = new List<int>();
                var rows = new List<int>();
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int ti = 0; ti < tables.Count; ti++)
                {
                    var ix = tables[ti].Index();
                    for (int row = 0; row < ix.Rows; row++)
                    {
                        if (!ix.HasName(row))
                            continue;
                        string nm = ix.Name(row);
                        if (!seen.Add(nm))
                            continue;
                        tab.Add(ti);
                        rows.Add(row);
                        names.Add(nm);
                    }
                }
                r = new TagRecords
                {
                    _tab = tab.ToArray(),
                    _row = rows.ToArray(),
                    _name = names.ToArray(),
                    _reader = new TagFieldReader(db, tables),
                };
                h.TagRecords = r;
                return r;
            }
        }

        [Builtin(
            "TagBrowseFirst",
            Doc = "1192728",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)"
        )]
        private static Value TagBrowseFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Tag", 0, true);

        [Builtin(
            "TagBrowseNext",
            Doc = "1192730",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)"
        )]
        private static Value TagBrowseNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Tag", 1, true);

        [Builtin(
            "TagBrowsePrev",
            Doc = "1192733",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)"
        )]
        private static Value TagBrowsePrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Tag", -1, true);

        [Builtin(
            "TagBrowseGetField",
            Doc = "1192729",
            Errors = "410 bad session; 412 no current record; 413 unknown field (raised, '' returned)"
        )]
        private static Value TagBrowseGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "Tag");

        [Builtin("TagBrowseNumRecords", Doc = "1192731", Errors = "410 bad session (raised, -1)")]
        private static Value TagBrowseNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "Tag");

        [Builtin("TagBrowseClose", Doc = "1192727", Blocking = true, Errors = "410 bad session (returned)")]
        private static Value TagBrowseClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "Tag");
    }
}
