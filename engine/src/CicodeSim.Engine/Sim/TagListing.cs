using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CicodeSim.Values;
using CicodeSim.World;

namespace CicodeSim.Sim
{
    /// <summary>Every tag a viewer can list.</summary>
    public sealed class TagListing
    {
        private readonly TagDb _db;
        private readonly List<VarTable> _tables;

        /// <summary>table index (-1 = a local variable) and row (the local's index), sorted.</summary>
        private readonly int[] _table;
        private readonly int[] _row;
        private readonly List<TagDef> _locals;
        public int Count => _table.Length;
        public long BuildMs { get; }

        public TagListing(TagDb db)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _db = db;
            _tables = db.Tables();
            _locals = new List<TagDef>(db.Locals().Values);
            var tabs = new List<int>();
            var rows = new List<int>();
            // sort keys in lower case, kept only while the listing is built
            var ekeys = new List<string>();
            var nkeys = new List<string>();
            var lowEquip = new Dictionary<string, string>(StringComparer.Ordinal);
            var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _locals.Count; i++)
            {
                localNames.Add(_locals[i].Name);
                tabs.Add(-1);
                rows.Add(i);
                ekeys.Add("");
                nkeys.Add(Project.CitectText.Lower(_locals[i].Name));
            }
            for (int t = 0; t < _tables.Count; t++)
            {
                var ix = _tables[t].Index();
                for (int r = 0; r < ix.Rows; r++)
                {
                    if (!ix.HasName(r))
                        continue;
                    string name = ix.Name(r);
                    string low = Project.CitectText.Lower(name);
                    if (localNames.Contains(name) || ix.Find(low) != r)
                        continue; // a local variable, or a later duplicate in this table
                    bool earlier = false;
                    for (int u = 0; u < t && !earlier; u++)
                        earlier = _tables[u].Find(low) >= 0;
                    if (earlier)
                        continue;
                    tabs.Add(t);
                    rows.Add(r);
                    string eq = ix.Equip(r);
                    if (!lowEquip.TryGetValue(eq, out string? le))
                        lowEquip[eq] = le = Project.CitectText.Lower(eq);
                    ekeys.Add(le);
                    nkeys.Add(low);
                }
            }
            int n = tabs.Count;
            var order = new int[n];
            for (int i = 0; i < n; i++)
                order[i] = i;
            var ekey = ekeys.ToArray();
            var nkey = nkeys.ToArray();
            ekeys = null!;
            nkeys = null!;
            Array.Sort(order, (a, b) =>
            {
                bool ea = ekey[a].Length == 0,
                    eb = ekey[b].Length == 0;
                if (ea != eb)
                    return ea ? 1 : -1;
                int c = string.CompareOrdinal(ekey[a], ekey[b]);
                if (c != 0)
                    return c;
                c = string.CompareOrdinal(nkey[a], nkey[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            _table = new int[n];
            _row = new int[n];
            for (int i = 0; i < n; i++)
            {
                _table[i] = tabs[order[i]];
                _row[i] = rows[order[i]];
            }
            BuildMs = sw.ElapsedMilliseconds;
        }

        public string Name(int i) =>
            _table[i] < 0 ? _locals[_row[i]].Name : _tables[_table[i]].Index().Name(_row[i]);

        public string Equip(int i) => _table[i] < 0 ? "" : _tables[_table[i]].Index().Equip(_row[i]);

        public string Item(int i) => _table[i] < 0 ? "" : _tables[_table[i]].Index().Item(_row[i]);

        /// <summary>
        /// A tag filter: plain text matches part of the name, wildcards match the whole name.
        /// </summary>
        public sealed class Filter
        {
            private readonly string _low;
            private readonly bool _wild;
            private readonly Regex? _rx;

            /// <summary>The longest one name may take (a pattern that backtracks a lot fails the
            /// listing instead of stalling the simulation thread).</summary>
            public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(25);

            public Filter(string text)
                : this(text, false) { }

            /// <summary>An ArgumentException when <paramref name="regex"/> and the text is no
            /// regular expression (its message says why).</summary>
            public Filter(string text, bool regex)
            {
                _low = (text ?? "").Trim().ToLowerInvariant();
                _wild = !regex && (_low.IndexOf('*') >= 0 || _low.IndexOf('?') >= 0);
                if (regex && _low.Length > 0)
                    _rx = new Regex(
                        (text ?? "").Trim(),
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        MatchTimeout
                    );
            }

            public bool Empty => _low.Length == 0;

            /// <summary>A RegexMatchTimeoutException when a regular expression takes too long
            /// on one name.</summary>
            public bool Match(string name, string equip, string item)
            {
                if (_low.Length == 0)
                    return true;
                if (_rx != null)
                    return _rx.IsMatch(name) || (equip.Length > 0 && _rx.IsMatch(equip + "." + item));
                string n = name.ToLowerInvariant();
                if (_wild)
                    return Wild(_low, 0, n, 0);
                if (n.Contains(_low))
                    return true;
                return equip.Length > 0 && (equip + "." + item).ToLowerInvariant().Contains(_low);
            }

            /// <summary>* any run, ? one character, the whole of s. Greedy with one back-track
            /// point (the last *): linear per star, never exponential (the listing runs on the
            /// simulation thread and must not stall it).</summary>
            internal static bool Wild(string p, int i, string s, int j)
            {
                int star = -1,
                    mark = 0;
                while (j < s.Length)
                {
                    if (i < p.Length && (p[i] == '?' || (p[i] != '*' && p[i] == s[j])))
                    {
                        i++;
                        j++;
                    }
                    else if (i < p.Length && p[i] == '*')
                    {
                        star = i++;
                        mark = j;
                    }
                    else if (star >= 0)
                    {
                        i = star + 1;
                        j = ++mark;
                    }
                    else
                        return false;
                }
                while (i < p.Length && p[i] == '*')
                    i++;
                return i == p.Length;
            }
        }

        /// <summary>The listing's matching positions (in order).</summary>
        public List<int> Matching(string? filter, string? equipment) =>
            Matching(new Filter(filter ?? ""), equipment);

        public List<int> Matching(Filter f, string? equipment)
        {
            string? eq = equipment;
            var r = new List<int>();
            for (int i = 0; i < _table.Length; i++)
            {
                if (eq != null && !string.Equals(Equip(i), eq, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!f.Empty && !f.Match(Name(i), Equip(i), Item(i)))
                    continue;
                r.Add(i);
            }
            return r;
        }

        /// <summary>One tag's definition and value as the protocol sends it.</summary>
        public JsonValue Entry(int i, Dictionary<VarTable, Dictionary<string, Func<int, string>>> columns) =>
            EntryAt(_table[i], _row[i], columns);

        /// <summary>The entry of a tag by name (a local variable, else the first table that
        /// defines it, else an Equip.Item's tag); null when it is not defined.</summary>
        public JsonValue? EntryFor(
            string name,
            Dictionary<VarTable, Dictionary<string, Func<int, string>>> columns
        )
        {
            string key = Project.CitectText.Trim(name ?? "");
            for (int i = 0; i < _locals.Count; i++)
                if (string.Equals(_locals[i].Name, key, StringComparison.OrdinalIgnoreCase))
                    return EntryAt(-1, i, columns);
            for (int t = 0; t < _tables.Count; t++)
            {
                int r = _tables[t].Find(key);
                if (r >= 0)
                    return EntryAt(t, r, columns);
            }
            if (key.IndexOf('.') > 0)
                for (int t = 0; t < _tables.Count; t++)
                {
                    int r = _tables[t].FindItem(key);
                    if (r >= 0)
                        return EntryAt(t, r, columns);
                }
            return null;
        }

        private JsonValue EntryAt(
            int table,
            int row,
            Dictionary<VarTable, Dictionary<string, Func<int, string>>> columns
        )
        {
            var o = JsonValue.NewObject();
            string name = table < 0 ? _locals[row].Name : _tables[table].Index().Name(row);
            o.Set("name", name);
            o.Set("equipment", table < 0 ? "" : _tables[table].Index().Equip(row));
            o.Set("item", table < 0 ? "" : _tables[table].Index().Item(row));
            int i = row;
            if (table < 0)
            {
                var d = _locals[row];
                o.Set("kind", "local");
                o.Set("project", d.Project);
                o.Set("type", d.TypeName.Length > 0 ? d.TypeName : "INT");
                o.Set("comment", d.Comment);
                o.Set("engUnits", d.EngUnits);
                o.Set("format", d.Format);
                o.Set("engZero", d.EngZero.HasValue ? JsonValue.Of(d.EngZero.Value) : JsonValue.Null);
                o.Set("engFull", d.EngFull.HasValue ? JsonValue.Of(d.EngFull.Value) : JsonValue.Null);
                o.Set("cluster", "");
            }
            else
            {
                var t = _tables[table];
                if (!columns.TryGetValue(t, out var cols))
                    columns[t] = cols = new Dictionary<string, Func<int, string>>(StringComparer.Ordinal);
                string Col(string c)
                {
                    if (!cols.TryGetValue(c, out var f))
                        cols[c] = f = t.RawColumn(c);
                    return Project.CitectText.Trim(f(i));
                }
                o.Set("kind", "variable");
                o.Set("project", t.Project);
                string type = Col("TYPE");
                o.Set("type", type.Length > 0 ? type : "INT");
                o.Set("comment", Col("COMMENT"));
                o.Set("engUnits", Col("ENG_UNITS"));
                o.Set("format", Col("FORMAT"));
                o.Set("engZero", Num(Col("ENG_ZERO")));
                o.Set("engFull", Num(Col("ENG_FULL")));
                o.Set("cluster", Col("CLUSTER"));
            }
            // the definition fields a simulation can override (TagDef.OverridableFields): as
            // configured, then the overrides on top with the configured values beside them
            if (table < 0)
            {
                var d = _locals[row];
                o.Set("rawZero", JsonValue.Null);
                o.Set("rawFull", JsonValue.Null);
                o.Set("deadband", JsonValue.Null);
                o.Set("unit", "");
                o.Set("address", "");
                for (int k = 1; k <= 8; k++)
                    o.Set("custom" + k.ToString(CultureInfo.InvariantCulture), "");
            }
            else
            {
                var t = _tables[table];
                var cols = columns[t];
                string Col(string c)
                {
                    if (!cols.TryGetValue(c, out var f))
                        cols[c] = f = t.RawColumn(c);
                    return Project.CitectText.Trim(f(i));
                }
                o.Set("rawZero", Num(Col("RAW_ZERO")));
                o.Set("rawFull", Num(Col("RAW_FULL")));
                o.Set("deadband", Num(Col("DEADBAND")));
                o.Set("unit", Col("UNIT"));
                o.Set("address", Col("ADDR"));
                for (int k = 1; k <= 8; k++)
                    o.Set(
                        "custom" + k.ToString(CultureInfo.InvariantCulture),
                        Col("CUSTOM" + k.ToString(CultureInfo.InvariantCulture))
                    );
            }
            var od = _db.Overridden(name);
            if (od != null)
            {
                var ov = JsonValue.NewObject();
                var conf = JsonValue.NewObject();
                foreach (var kv in od.Overrides)
                {
                    ov.Set(kv.Key, kv.Value);
                    conf.Set(kv.Key, od.Configured(kv.Key));
                    string key = EntryKey(kv.Key);
                    bool num =
                        kv.Key == "ENG_ZERO"
                        || kv.Key == "ENG_FULL"
                        || kv.Key == "RAW_ZERO"
                        || kv.Key == "RAW_FULL"
                        || kv.Key == "DEADBAND";
                    o.Set(key, num ? Num(kv.Value) : JsonValue.Of(kv.Value));
                }
                o.Set("overrides", ov);
                o.Set("configured", conf);
            }
            var (value, quality, defined) = _db.Peek(name);
            o.Set("value", value);
            o.Set("quality", CicodeSim.World.Quality.Name(quality));
            if (_db.IsLocked(name))
                o.Set("locked", JsonValue.True);
            return o;
        }

        /// <summary>The tag entry's key of a definition field (ENG_ZERO -> engZero).</summary>
        public static string EntryKey(string field)
        {
            switch (field)
            {
                case "ENG_ZERO":
                    return "engZero";
                case "ENG_FULL":
                    return "engFull";
                case "RAW_ZERO":
                    return "rawZero";
                case "RAW_FULL":
                    return "rawFull";
                case "ENG_UNITS":
                    return "engUnits";
                case "FORMAT":
                    return "format";
                case "COMMENT":
                    return "comment";
                case "DEADBAND":
                    return "deadband";
                case "UNIT":
                    return "unit";
                case "ADDR":
                    return "address";
            }
            return field.StartsWith("CUSTOM", StringComparison.Ordinal)
                ? "custom" + field.Substring(6)
                : field.ToLowerInvariant();
        }

        private static JsonValue Num(string s)
        {
            if (s.Length == 0)
                return JsonValue.Null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? JsonValue.Of(d)
                : JsonValue.Of(s);
        }

        /// <summary>Equipment groups (name, count) of positions in listing order.</summary>
        public List<(string Equip, int Count)> Groups(List<int> positions)
        {
            var r = new List<(string, int)>();
            string? cur = null;
            int n = 0;
            foreach (int i in positions)
            {
                string e = Equip(i);
                if (cur != null && string.Equals(e, cur, StringComparison.OrdinalIgnoreCase))
                {
                    n++;
                    continue;
                }
                if (cur != null)
                    r.Add((cur, n));
                cur = e;
                n = 1;
            }
            if (cur != null)
                r.Add((cur, n));
            return r;
        }
    }

    /// <summary>The tags a compiled page reads (its .RDB's TAGREF table), for 'subscribe page'.</summary>
    public static class PageTags
    {
        /// <summary>The page's .RDB in the unit's project folders (the root first), null when none.</summary>
        public static string? FindRdb(Project.Unit unit, string page)
        {
            string file = page.EndsWith(".rdb", StringComparison.OrdinalIgnoreCase) ? page : page + ".RDB";
            if (Path.IsPathRooted(file))
                return File.Exists(file) ? file : null;
            var dirs = new List<string> { unit.RootDir };
            foreach (var p in unit.Projects)
                if (!dirs.Contains(p.Dir))
                    dirs.Add(p.Dir);
            foreach (string d in dirs)
            {
                string f = Path.Combine(d, file);
                if (File.Exists(f))
                    return f;
            }
            return null;
        }

        /// <summary>The tag names a page's code references (TAGREF Tag, with Element / Item).</summary>
        public static List<string> Names(string rdbPath) => Names(Compiled.Rdb.LoadPage(rdbPath).Program);

        /// <summary>The tag names a page program's code references (a page compiled from rows).</summary>
        public static List<string> Names(Compiled.Program program)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var r = new List<string>();
            foreach (var tr in program.TagRefs.Values)
            {
                if (tr.Tag.Length == 0)
                    continue;
                string n = tr.Tag;
                if (seen.Add(n))
                    r.Add(n);
            }
            return r;
        }
    }
}
