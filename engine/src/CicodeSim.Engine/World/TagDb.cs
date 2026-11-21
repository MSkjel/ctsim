using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using TagRef = CicodeSim.Compiled.TagRef;

namespace CicodeSim.World
{
    /// <summary>One project's variable.dbf: a name / Equip.Item index (read the first time the
    /// table is searched) and the definition columns (read when one of its tags is first used),
    /// both from the shared column-table cache (same row numbering).</summary>
    public sealed class VarTable
    {
        public string Project { get; }
        public string Path { get; }
        private TagIndex? _index;
        private ColumnTable? _defs;
        private readonly object _lock = new object();
        internal readonly Dictionary<int, TagDef> Defs = new Dictionary<int, TagDef>();

        public VarTable(string project, string path)
        {
            Project = project;
            Path = path;
        }

        /// <summary>The name / Equip.Item index (read the first time the table is searched).</summary>
        public TagIndex Index()
        {
            if (_index != null)
                return _index;
            lock (_lock)
            {
                if (_index == null)
                    _index = TagIndex.For(Path);
                return _index;
            }
        }

        public int Count => Index().Rows;

        public int Find(string low) => Index().Find(low);

        public int FindItem(string low) => Index().FindItem(low);

        public int ItemCount => Index().ItemEntries;

        /// <summary>The rows of the item index, in row order.</summary>
        public int[] ItemRows => Index().ItemRows;

        /// <summary>The definition columns when the table cannot be read again: the index's
        /// NAME / EQUIP / ITEM, '' for the rest.</summary>
        private ColumnTable FromIndex()
        {
            var ix = Index();
            var cols = new string[TagDb.DefColumns.Length][];
            for (int c = 0; c < cols.Length; c++)
            {
                var col = new string[ix.Rows];
                for (int r = 0; r < ix.Rows; r++)
                {
                    string n = TagDb.DefColumns[c];
                    col[r] =
                        n == "NAME" ? ix.Name(r)
                        : n == "EQUIP" ? ix.Equip(r)
                        : n == "ITEM" ? ix.Item(r)
                        : "";
                }
                cols[c] = col;
            }
            return ColumnTable.FromColumns(TagDb.DefColumns, cols);
        }

        public TagDef Definition(int row)
        {
            if (_defs == null)
            {
                lock (_lock)
                {
                    if (_defs == null)
                        _defs = Dbf.Columns(Path, TagDb.DefColumns) ?? FromIndex();
                }
            }
            var c = _defs;
            var cust = new string[8];
            for (int i = 0; i < 8; i++)
                cust[i] = c.Column("CUSTOM" + (i + 1).ToString(CultureInfo.InvariantCulture))[row];
            string type = c.Column("TYPE")[row],
                addr = c.Column("ADDR")[row];
            return new TagDef(
                name: c.Column("NAME")[row],
                kind: TagDef.Variable,
                project: Project,
                typeName: type,
                dims: TagTypes.DimsFromAddress(addr, type),
                rawZero: TagTypes.Float(c.Column("RAW_ZERO")[row]),
                rawFull: TagTypes.Float(c.Column("RAW_FULL")[row]),
                engZero: TagTypes.Float(c.Column("ENG_ZERO")[row]),
                engFull: TagTypes.Float(c.Column("ENG_FULL")[row]),
                engUnits: c.Column("ENG_UNITS")[row],
                format: c.Column("FORMAT")[row],
                comment: c.Column("COMMENT")[row],
                cluster: c.Column("CLUSTER")[row],
                equip: c.Column("EQUIP")[row],
                item: c.Column("ITEM")[row],
                deadband: TagTypes.Float(c.Column("DEADBAND")[row]),
                unit: c.Column("UNIT")[row],
                address: addr,
                custom: cust,
                record: row,
                historian: c.Column("HISTORIAN")[row]
            );
        }

        /// <summary>One definition column's raw text by row.</summary>
        public Func<int, string> RawColumn(string column)
        {
            string up = CitectText.Upper(column);
            var defs = _defs;
            if (defs != null)
            {
                var col = defs.Column(up);
                return r => col[r];
            }
            var ix = Index();
            if (up == "EQUIP")
                return ix.Equip;
            if (up == "ITEM")
                return ix.Item;
            var t = Dbf.Columns(Path, new[] { up });
            if (t != null)
            {
                var col = t.Column(up);
                return r => col[r];
            }
            // the table cannot be read again: what Definition's fallback (FromIndex) holds
            if (up == "NAME")
                return ix.Name;
            return r => "";
        }
    }

    /// <summary>The simulated tag database.</summary>
    public sealed class TagDb : ITagAccess, ITagElementAccess, ITagScale
    {
        // -- error codes (doc: 1130208) --------------------------------------------------------
        public const int ErrOutOfRange = 257;
        public const int ErrArrayOverrun = 259; //
        public const int ErrInvalidArgument = 274;
        public const int ErrTypeMismatch = 433;
        public const int ErrInvalidConversion = 434;
        public const int ErrTagNotFound = 424;

        /// <summary>Error for writing a QUALITY to '.q' or a TIMESTAMP to '.t' / '.vt' / '.qt'.</summary>
        public const int ErrItemWrite = 0x2009;

        public static readonly string[] Elements =
        {
            "field",
            "valid",
            "override",
            "overridemode",
            "controlmode",
            "status",
        };
        public static readonly string[] Items = { "v", "vt", "q", "qt", "t" };

        public static readonly string[] IndexColumns = { "NAME", "EQUIP", "ITEM" };
        public static readonly string[] DefColumns =
        {
            "NAME",
            "TYPE",
            "UNIT",
            "ADDR",
            "RAW_ZERO",
            "RAW_FULL",
            "ENG_ZERO",
            "ENG_FULL",
            "ENG_UNITS",
            "FORMAT",
            "COMMENT",
            "DEADBAND",
            "CLUSTER",
            "EQUIP",
            "ITEM",
            "CUSTOM1",
            "CUSTOM2",
            "CUSTOM3",
            "CUSTOM4",
            "CUSTOM5",
            "CUSTOM6",
            "CUSTOM7",
            "CUSTOM8",
            "HISTORIAN",
            "OID",
        };
        public static readonly string[] LocvarColumns =
        {
            "NAME",
            "TYPE",
            "ASIZE",
            "ZERO",
            "FULL",
            "COMMENT",
            "ENG_UNITS",
            "FORMAT",
        };

        public Unit Unit { get; }
        public SimClock Clock { get; }
        public AlarmDb? Alarms { get; internal set; }
        public double WriteDelay { get; set; }
        public ulong InitialQuality { get; set; } = Quality.QualityGood;
        public long Serial { get; private set; }

        private List<VarTable>? _tables;
        private Dictionary<string, TagDef>? _locals;

        // name lookups (hits and misses), keyed by the name as asked, compared without case
        private readonly Dictionary<string, Hit> _nameWhere = new Dictionary<string, Hit>(
            LowerComparer.Instance
        );
        private readonly Dictionary<string, Hit> _itemWhere = new Dictionary<string, Hit>(
            LowerComparer.Instance
        );
        private readonly Dictionary<ResolveKey, Resolved> _resolved = new Dictionary<ResolveKey, Resolved>();
        private readonly Dictionary<TagRef, Resolved> _refMemo = new Dictionary<TagRef, Resolved>(
            RefEq<TagRef>.Instance
        );
        private readonly Dictionary<string, TagState> _states = new Dictionary<string, TagState>(
            LowerComparer.Instance
        );
        private readonly Dictionary<string, long> _changed = new Dictionary<string, long>(
            StringComparer.Ordinal
        );
        private readonly List<Action<TagChange>> _listeners = new List<Action<TagChange>>();
        private readonly List<string> _usedOrder = new List<string>();
        private readonly Dictionary<string, string> _used = new Dictionary<string, string>(
            StringComparer.Ordinal
        );
        private HashSet<string>? _clusters;

        // simulator only (a deliberate deviation): tags made for names the project
        // does not define, found only while SimulateUndefined is on
        private readonly Dictionary<string, TagDef> _simulated = new Dictionary<string, TagDef>(
            LowerComparer.Instance
        );
        private bool _simulateUndefined;

        /// <summary>The project of a simulated tag (<see cref="AddSimulated"/>).</summary>
        public const string SimulatedProject = "(simulated)";

        /// <summary>Simulator only: make tags the project doesn't define findable.</summary>
        public bool SimulateUndefined
        {
            get => _simulateUndefined;
            set
            {
                if (value == _simulateUndefined)
                    return;
                _simulateUndefined = value;
                if (_simulated.Count > 0)
                    ForgetLookups();
            }
        }

        /// <summary>The simulated tags made so far (found only while <see cref="SimulateUndefined"/>).</summary>
        public IReadOnlyCollection<TagDef> SimulatedTags => _simulated.Values;

        /// <summary>A simulated tag of this name (any case), null when none was made.</summary>
        public TagDef? SimulatedTag(string name) =>
            _simulated.TryGetValue(CitectText.Trim(name ?? ""), out TagDef? d) ? d : null;

        /// <summary>A name a simulated tag can have: a plain tag name (letters, digits, '_', '\'
        /// and code page letters; no '.', '?', '[', blank), 1..79 characters.</summary>
        public static bool SimulatableName(string? name)
        {
            string n = name ?? "";
            if (n.Length == 0 || n.Length > 79)
                return false;
            foreach (char c in n)
                if (
                    !(char.IsLetterOrDigit(c) || c == '_' || c == '\\' || (c > 0x7f && !char.IsWhiteSpace(c)))
                )
                    return false;
            return true;
        }

        /// <summary>Make a simulated tag for a name the project doesn't define.</summary>
        public TagDef? AddSimulated(string name, string typeName, string comment = "")
        {
            if (!_simulateUndefined)
                return null;
            string key = CitectText.Trim(name ?? "");
            if (!SimulatableName(key))
                return null;
            if (_simulated.TryGetValue(key, out TagDef? had))
                return had;
            if (FindName(key) != null || FindItem(key) != null)
                return null;
            string t = CitectText.Upper(CitectText.Trim(typeName ?? ""));
            if (t.Length == 0)
                t = "REAL";
            var d = new TagDef(key, TagDef.Variable, SimulatedProject, typeName: t, comment: comment);
            _simulated[key] = d;
            ForgetLookups();
            return d;
        }

        /// <summary>Drop the name lookups (hits and misses): a simulated tag came or went.</summary>
        private void ForgetLookups()
        {
            _nameWhere.Clear();
            _itemWhere.Clear();
            _resolved.Clear();
            _refMemo.Clear();
        }

        private readonly struct Hit
        {
            public readonly TagDef? Local;
            public readonly VarTable? Table;
            public readonly int Row;

            public Hit(TagDef? local, VarTable? table, int row)
            {
                Local = local;
                Table = table;
                Row = row;
            }

            public bool Found => Local != null || Table != null;
        }

        private readonly struct ResolveKey : IEquatable<ResolveKey>
        {
            private readonly string _c,
                _t,
                _e,
                _i;

            public ResolveKey(string c, string t, string e, string i)
            {
                _c = c;
                _t = t;
                _e = e;
                _i = i;
            }

            public bool Equals(ResolveKey o) => _c == o._c && _t == o._t && _e == o._e && _i == o._i;

            public override bool Equals(object? obj) => obj is ResolveKey k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (
                            (
                                StringComparer.Ordinal.GetHashCode(_t) * 31
                                + StringComparer.Ordinal.GetHashCode(_e)
                            ) * 31
                            + StringComparer.Ordinal.GetHashCode(_i)
                        ) * 31
                        + StringComparer.Ordinal.GetHashCode(_c);
                }
            }
        }

        public TagDb(Unit unit, SimClock clock, double writeDelay = 0.25)
        {
            Unit = unit;
            Clock = clock;
            WriteDelay = writeDelay;
        }

        // ------------------------------------------------------------------ tables
        public List<VarTable> Tables()
        {
            if (_tables == null)
            {
                var o = new List<VarTable>();
                foreach (var (p, path) in Unit.Tables("variable.dbf"))
                    o.Add(new VarTable(p.Name, path));
                _tables = o;
            }
            return _tables;
        }

        /// <summary>Lower-case name to local variable (locvar.dbf of the unit, compile order: a
        /// later duplicate is E2021).</summary>
        public Dictionary<string, TagDef> Locals()
        {
            if (_locals == null)
            {
                var o = new Dictionary<string, TagDef>(LowerComparer.Instance);
                foreach (var (p, path) in Unit.Tables("locvar.dbf", "compile"))
                {
                    var rows = Dbf.CitectRows(path, LocvarColumns);
                    for (int row = 0; row < rows.Count; row++)
                    {
                        string[] r = rows[row];
                        string name = r[0];
                        if (name.Length == 0 || o.ContainsKey(name))
                            continue;
                        o[name] = new TagDef(
                            name,
                            TagDef.Local,
                            p.Name,
                            typeName: r[1],
                            dims: TagTypes.DimsFromAsize(r[2]),
                            engZero: TagTypes.Float(r[3]),
                            engFull: TagTypes.Float(r[4]),
                            comment: r[5],
                            engUnits: r[6],
                            format: r[7],
                            record: row
                        );
                    }
                }
                _locals = o;
            }
            return _locals;
        }

        public HashSet<string> Clusters()
        {
            if (_clusters == null)
            {
                var s = new HashSet<string>(StringComparer.Ordinal);
                foreach (string c in Unit.Clusters())
                    s.Add(CitectText.Lower(c));
                _clusters = s;
            }
            return _clusters;
        }

        // ------------------------------------------------------------------ definitions
        /// <summary>The variable tag or local variable named <paramref name="name"/>
        /// (case-insensitive), else the variable tag an Equipment.Item names; null when none.</summary>
        public TagDef? Find(string? name, string cluster = "")
        {
            string key = CitectText.Trim(name ?? "");
            NoteCodeUsed(key, "");
            if (key.Length == 0)
                return null;
            TagDef? d = FindName(key) ?? FindItem(key);
            if (
                d != null
                && cluster.Length > 0
                && d.Cluster.Length > 0
                && !CitectText.SameName(d.Cluster, cluster)
            )
                return null;
            return d;
        }

        /// <summary>Where a name is defined, from the name indexes only: a local
        /// variable, a (table, row) or nothing. <paramref name="name"/> = the stripped name in
        /// any case (the indexes compare names in lower case).</summary>
        private Hit Where(string name, bool item = false)
        {
            var cache = item ? _itemWhere : _nameWhere;
            if (cache.TryGetValue(name, out Hit hit))
                return hit;
            hit = default;
            if (item)
            {
                if (name.IndexOf('.') >= 0)
                {
                    foreach (var t in Tables())
                    {
                        int row = t.FindItem(name);
                        if (row >= 0)
                        {
                            hit = new Hit(null, t, row);
                            break;
                        }
                    }
                }
            }
            else
            {
                if (Locals().TryGetValue(name, out TagDef? d))
                    hit = new Hit(d, null, 0);
                else
                {
                    foreach (var t in Tables())
                    {
                        int row = t.Find(name);
                        if (row >= 0)
                        {
                            hit = new Hit(null, t, row);
                            break;
                        }
                    }
                    if (!hit.Found && _simulateUndefined && _simulated.TryGetValue(name, out TagDef? sd))
                        hit = new Hit(sd, null, 0);
                }
            }
            if (cache.Count > 500_000)
                cache.Clear();
            cache[name] = hit;
            return hit;
        }

        private static TagDef? DefnAt(in Hit hit)
        {
            if (!hit.Found)
                return null;
            if (hit.Local != null)
                return hit.Local;
            var t = hit.Table!;
            if (!t.Defs.TryGetValue(hit.Row, out TagDef? d))
            {
                d = t.Definition(hit.Row);
                t.Defs[hit.Row] = d;
            }
            return d;
        }

        /// <summary>The tag or local variable of a name (no Equip.Item lookup, no trim; compared
        /// without case).</summary>
        public TagDef? FindName(string name) => DefnAt(Where(name));

        /// <summary>The variable tag of Equipment.Item (EQUIP + ITEM columns), null if none.</summary>
        public TagDef? FindItem(string? equipItem) =>
            DefnAt(Where(CitectText.Trim(equipItem ?? ""), item: true));

        /// <summary>The defined spelling of a tag / local variable / Equip.Item's tag, from the
        /// name indexes only; null when it is not defined.</summary>
        public string? Canonical(string? name)
        {
            string key = CitectText.Trim(name ?? "");
            Hit hit = Where(key);
            if (!hit.Found)
                hit = Where(key, item: true);
            if (!hit.Found)
                return null;
            if (hit.Local != null)
                return hit.Local.Name;
            return hit.Table!.Index().Name(hit.Row);
        }

        /// <summary>(engineering value, quality, defined) of a tag without reading its definition
        /// when it was never written (the initial value 0, the initial quality): what the alarm
        /// server reads. An array gives its element 0.</summary>
        public (JsonValue value, ulong quality, bool defined) Peek(string name)
        {
            string? canon = Canonical(name);
            if (canon == null)
                return (JsonValue.Of(0), Quality.QualityTagNotFound, false);
            if (!_states.TryGetValue(canon, out TagState? st))
                return (JsonValue.Of(0), InitialQuality, true);
            TagElem e = DefaultElement(st);
            RawVal raw = e.IsArray ? e.At(0) : e.Scalar;
            return (EngJson(st.Defn, raw), e.Q, true);
        }

        /// <summary>Peek as a number for the alarm server (no JSON allocation).</summary>
        internal bool PeekNumber(
            string name,
            out double value,
            out bool isNumber,
            out ulong quality,
            out RawVal rawOut
        )
        {
            value = 0;
            isNumber = true;
            rawOut = default;
            string? canon = Canonical(name);
            if (canon == null)
            {
                quality = Quality.QualityTagNotFound;
                return false;
            }
            if (!_states.TryGetValue(canon, out TagState? st))
            {
                quality = InitialQuality;
                return true;
            }
            TagElem e = DefaultElement(st);
            RawVal raw = e.IsArray ? e.At(0) : e.Scalar;
            quality = e.Q;
            rawOut = raw;
            if (st.Defn.Type == Types.String)
            {
                isNumber = false;
                rawOut = raw;
                return true;
            }
            value = EngDouble(st.Defn, raw);
            return true;
        }

        public Dictionary<string, int> Counts()
        {
            var tables = Tables();
            int vars = 0,
                items = 0;
            foreach (var t in tables)
            {
                vars += t.Count;
                items += t.ItemCount;
            }
            return new Dictionary<string, int>
            {
                ["variables"] = vars,
                ["locals"] = Locals().Count,
                ["equipment_items"] = items,
                ["tables"] = tables.Count,
            };
        }

        /// <summary>(equipment, item, tag name) of every variable-tag item (reads every index).</summary>
        public IEnumerable<(string equip, string item, string name)> IterItems()
        {
            foreach (var t in Tables())
            {
                var ix = t.Index();
                foreach (int r in ix.ItemRows)
                    yield return (ix.Equip(r), ix.Item(r), ix.Name(r));
            }
        }

        // ------------------------------------------------------------------ resolution
        /// <summary>Resolve a reference as the TAGREF fields (or one dotted name) give it.</summary>
        public Resolved Resolve(string? tag, string? element = "", string? item = "", string? cluster = "")
        {
            tag ??= "";
            element ??= "";
            item ??= "";
            cluster ??= "";
            NoteCodeUsed(tag, item);
            var key = new ResolveKey(
                CitectText.Lower(cluster),
                CitectText.Lower(tag),
                CitectText.Lower(element),
                CitectText.Lower(item)
            );
            if (_resolved.TryGetValue(key, out Resolved? hit))
                return hit;
            Resolved r = ResolveCore(
                tag,
                CitectText.Lower(CitectText.Trim(element)),
                CitectText.Lower(CitectText.Trim(item)),
                CitectText.Trim(cluster)
            );
            if (_resolved.Count > 200_000)
                _resolved.Clear();
            _resolved[key] = r;
            return r;
        }

        /// <summary>Resolve of a TAGREF, memoised by the TagRef object (a VM Load resolves
        /// once); nothing is stored on the TagRef.</summary>
        public Resolved ResolveRef(TagRef r)
        {
            if (_refMemo.TryGetValue(r, out Resolved? hit))
                return hit;
            Resolved res = Resolve(r.Tag, r.Element, r.Item, r.Cluster);
            if (_refMemo.Count > 200_000)
                _refMemo.Clear();
            _refMemo[r] = res;
            return res;
        }

        private static bool In(string s, string[] set)
        {
            foreach (string x in set)
                if (x == s)
                    return true;
            return false;
        }

        private Resolved ResolveCore(string name, string element, string item, string cluster)
        {
            name = CitectText.Trim(name);
            int? index = null;
            var m = TagTypes.ArrayMatch(name);
            if (m.Success)
            {
                var n = CitectText.ParseInt(m.Groups[1].Value);
                index = n == null ? 0 : (int)Math.Min((double)n.Value, int.MaxValue);
                name = CitectText.TrimEnd(name.Substring(0, m.Index));
            }
            if ((element.Length > 0 && !In(element, Elements)) || (item.Length > 0 && !In(item, Items)))
                return Resolved.NotFound;
            Resolved r = ResolveName(name, element, item, cluster);
            if (!r.Found && name.IndexOf('.') >= 0 && cluster.Length == 0)
            {
                int dot = name.IndexOf('.');
                string first = name.Substring(0, dot),
                    rest = name.Substring(dot + 1);
                if (Clusters().Contains(CitectText.Lower(first)))
                    r = ResolveName(rest, element, item, first);
            }
            if (r.Found && index != null)
                r.Index = index;
            return r;
        }

        private Resolved ResolveName(string name, string element, string item, string cluster)
        {
            TagDef? d = FindName(name);
            string via = "name";
            if (d == null)
            {
                d = FindItem(name);
                via = "equipment";
            }
            if (d != null)
            {
                if (cluster.Length > 0 && d.Cluster.Length > 0 && !CitectText.SameName(d.Cluster, cluster))
                    return Resolved.NotFound;
                return new Resolved
                {
                    Kind = d.Kind,
                    Tag = d,
                    Element = element,
                    Item = item,
                    Via = via,
                    Name = d.Name,
                };
            }
            string[] parts = name.Split('.');
            // trailing extension parts written into the name ('Tag.Field.vt', 'Equip.Item.q')
            if (parts.Length > 1 && item.Length == 0 && In(CitectText.Lower(parts[parts.Length - 1]), Items))
            {
                var r = ResolveName(
                    string.Join(".", parts, 0, parts.Length - 1),
                    element,
                    CitectText.Lower(parts[parts.Length - 1]),
                    cluster
                );
                if (r.Found)
                    return r;
            }
            if (
                parts.Length > 1
                && element.Length == 0
                && In(CitectText.Lower(parts[parts.Length - 1]), Elements)
            )
            {
                var r = ResolveName(
                    string.Join(".", parts, 0, parts.Length - 1),
                    CitectText.Lower(parts[parts.Length - 1]),
                    item,
                    cluster
                );
                if (r.Found)
                    return r;
            }
            // alarm properties: 'Alarm.Prop', 'Equip.Item.Prop' (doc: 1068485)
            if (Alarms != null)
            {
                var pr = Alarms.ResolveProperty(name, element, item, cluster);
                if (pr != null)
                {
                    var (adef, prop, ext) = pr.Value;
                    string pname = AlarmDb.PropertyNames.TryGetValue(prop, out string? pn) ? pn : prop;
                    return new Resolved
                    {
                        Kind = "alarm",
                        Alarm = adef,
                        Element = "",
                        Item = ext,
                        Prop = prop,
                        Via = "alarm",
                        Name = adef.Tag + "." + pname,
                    };
                }
            }
            return Resolved.NotFound;
        }

        // ------------------------------------------------------------------ state
        public TagState State(TagDef defn)
        {
            if (!_states.TryGetValue(defn.Key, out TagState? st))
            {
                // a tag nobody wrote yet carries the start of the run (its first read from the
                // device); a local variable none (doc: 1189274)
                ulong ts = defn.Kind == TagDef.Local ? 0 : (ulong)Clock.StartFiletime;
                st = new TagState(defn, ts, InitialQuality);
                _states[defn.Key] = st;
            }
            return st;
        }

        private void NoteUsed(string name, string by)
        {
            if (name.Length > 0 && !_used.ContainsKey(name))
            {
                _used[name] = by;
                _usedOrder.Add(name);
            }
        }

        /// <summary>The tags code and pages read, in first-read order.</summary>
        public List<string> Used => new List<string>(_usedOrder);

        public List<KeyValuePair<string, string>> UsedBy()
        {
            var o = new List<KeyValuePair<string, string>>(_usedOrder.Count);
            foreach (string n in _usedOrder)
                o.Add(new KeyValuePair<string, string>(n, _used[n]));
            return o;
        }

        // ------------------------------------------------------------------ reading
        /// <summary>The flag word of a task (<see cref="TaskFlags"/>): EchoError and IgnoreCase
        /// always, WriteLocal / ScaleCheck as the task has them.</summary>
        private static int FlagsOf(bool writeLocal, bool scaleCheck) =>
            TaskFlags.EchoError
            | TaskFlags.IgnoreCase
            | (writeLocal ? TaskFlags.WriteLocal : 0)
            | (scaleCheck ? TaskFlags.ScaleCheck : 0);

        /// <summary>The flag word of a VM-less task; no task = the [Code] defaults.</summary>
        private static int FlagsOf(SimpleTask? task) =>
            task == null ? TaskFlags.Default : FlagsOf(task.WriteLocal, task.ScaleCheck);

        /// <summary>An element number the VM passes (-1 after its bounds check failed) as an
        /// int index: 259 like any index outside the array.</summary>
        private static int Elem(long index) =>
            index > int.MaxValue ? int.MaxValue
            : index < int.MinValue ? -1
            : (int)index;

        // -- the VM's contracts (CicodeSim.Vm.ITagAccess / ITagElementAccess / ITagScale): the
        //    task's fields are read and written directly, the tag error only set when there is one

        /// <summary>Load (0x02) of a TAGREF (TagAccess.read).</summary>
        public Value Read(TagRef r, CicodeTask task)
        {
            Value v = ReadRef(r, null, out int err);
            if (err != 0)
                task.TagError = err;
            return v;
        }

        /// <summary>Store (0x03) of a TAGREF (TagAccess.write): 0 or a hardware error.</summary>
        public int Write(TagRef r, Value value, CicodeTask task)
        {
            int code = WriteRef(r, value, FlagsOf(task.WriteLocal, task.ScaleCheck), null, out int err);
            if (err != 0)
                task.TagError = err;
            return code;
        }

        /// <summary>The element of an array tag an Index prefix computed (-1 = the VM's bounds
        /// check failed: 259 like any index outside the array).</summary>
        public Value ReadElement(TagRef r, long index, CicodeTask task)
        {
            Value v = ReadRef(r, Elem(index), out int err);
            if (err != 0)
                task.TagError = err;
            return v;
        }

        public int WriteElement(TagRef r, long index, Value value, CicodeTask task)
        {
            int code = WriteRef(
                r,
                value,
                FlagsOf(task.WriteLocal, task.ScaleCheck),
                Elem(index),
                out int err
            );
            if (err != 0)
                task.TagError = err;
            return code;
        }

        /// <summary>Bar with operand -1: use the tag's own scale.</summary>
        public bool TryScale(TagRef? r, CicodeTask task, out double engZero, out double engFull)
        {
            engZero = 0.0;
            engFull = 0.0;
            if (r == null)
                return false;
            Resolved res = ResolveRef(r);
            if (!res.Found || res.Kind == "alarm" || res.Tag == null || res.Tag.Type == Types.String)
                return false;
            (engZero, engFull) = res.Tag.EngRange();
            return true;
        }

        // -- without a VM (hosts, tests, the API differential): a SimpleTask or none ------------

        /// <summary>A read with an optional element index, for hosts and tests without a VM.</summary>
        public Value Read(TagRef r, SimpleTask? task, int? index = null)
        {
            Value v = ReadRef(r, index, out int err);
            if (err != 0 && task != null)
                task.TagError = err;
            return v;
        }

        /// <summary>A write with an optional element index, for hosts and tests without a VM.</summary>
        public int Write(TagRef r, Value value, SimpleTask? task, int? index = null)
        {
            int code = WriteRef(r, value, FlagsOf(task), index, out int err);
            if (err != 0 && task != null)
                task.TagError = err;
            return code;
        }

        /// <summary>A tag name code used (read, written, looked up by name): for the viewer's
        /// list of the tags a page's Cicode uses; no behaviour.</summary>
        public Action<string>? CodeUsed;

        /// <summary>Lookups that are not a use by code (Ass's store check of an association's
        /// value: a page's '__Type' default '0' is no tag the page uses) run with this above 0;
        /// see <see cref="NotUsed{T}"/>.</summary>
        private int _notUsed;

        /// <summary><paramref name="lookup"/> without noting the names it resolves as used by code.</summary>
        public T NotUsed<T>(Func<T> lookup)
        {
            _notUsed++;
            try
            {
                return lookup();
            }
            finally
            {
                _notUsed--;
            }
        }

        private void NoteCodeUsed(string? tag, string? item)
        {
            var u = CodeUsed;
            if (u == null || _notUsed > 0 || string.IsNullOrEmpty(tag))
                return;
            u(string.IsNullOrEmpty(item) ? tag! : tag + "." + item);
        }

        public Value ReadRef(TagRef r, int? index, out int tagError)
        {
            NoteCodeUsed(r.Tag, r.Item);
            Resolved res = ResolveRef(r);
            if (!res.Found)
                NotFoundRead?.Invoke(r);
            return ReadResolved(res, index, r.Lightweight != 0, "cicode", out tagError);
        }

        /// <summary>Code read a TAGREF that names no tag (424): for tools (ctsim-dev page-sweep lists
        /// the names); no behaviour.</summary>
        public Action<TagRef>? NotFoundRead;

        /// <summary>A value for a viewer / scenario / built-in (<paramref name="by"/> marks it
        /// used when given).</summary>
        public Value Get(
            string name,
            string element = "",
            string item = "",
            int? index = null,
            string by = "",
            string cluster = ""
        )
        {
            Resolved r = Resolve(name, element, item, cluster);
            return ReadResolved(r, index, false, by, out _);
        }

        public Value ReadResolved(Resolved r, int? index, bool light, string by, out int tagError)
        {
            tagError = 0;
            if (!r.Found)
            {
                // no subscription: the runtime's default branch pushes REAL 0 (guess that an
                // unknown tag takes it); the quality and code are the host contract's
                tagError = ErrTagNotFound;
                return Value.Real(0.0, Quality.QualityTagNotFound);
            }
            if (by.Length > 0 && !r.Noted)
            {
                NoteUsed(r.Name, by);
                if (r != Resolved.NotFound)
                    r.Noted = true;
            }
            if (r.Kind == "alarm")
                return Alarms!.PropertyValue(r.Alarm!, r.Prop, r.Item);
            TagDef defn = r.Tag!;
            TagState st = r.CachedState ??= State(defn);
            int? idx = index ?? r.Index;
            string element = r.Element;
            if (
                defn.Kind == TagDef.Local
                && (
                    element == "override"
                    || element == "overridemode"
                    || element == "controlmode"
                    || element == "status"
                )
            )
                return ItemValue(Value.Int(Types.Long, 0, Quality.Make(Quality.Bad)), r.Item);
            if (element.Length == 0)
                element = st.OverrideMode.L != 0 ? "override" : "field";
            if (element == "overridemode" || element == "controlmode" || element == "status")
            {
                TagElem e0 =
                    element == "overridemode" ? st.OverrideMode
                    : element == "controlmode" ? st.ControlMode
                    : st.Status;
                return ItemValue(Value.Int(Types.Long, e0.L, e0.Q, e0.T, e0.Vt, e0.Qt), r.Item);
            }
            TagElem e =
                element == "override" ? st.Override
                : element == "valid" ? st.Valid
                : st.Field;
            if (element == "override" && st.OverrideMode.L == 4)
                e = st.Valid;
            RawVal raw;
            if (e.IsArray)
            {
                int i = idx ?? 0;
                if (i < 0 || i >= e.Length)
                {
                    // the subscription answers 0x103 for an index outside the array and the runtime
                    // logs '%s[%i]'
                    tagError = ErrArrayOverrun;
                    ulong bq = Quality.Make(Quality.Bad, extended: Quality.ExtValueOutOfRange);
                    int vt = defn.ValueType;
                    if (defn.Type == Types.String)
                        return Value.Str("", bq);
                    return vt == Types.Real ? Value.Real(0.0, bq) : Value.Int(vt, 0, bq);
                }
                raw = e.At(i);
            }
            else
                raw = e.Scalar;
            ulong q = e.Q;
            if (element == "override" && st.OverrideMode.L != 0)
            {
                // the default and the Override element carry the override bit while the tag is in
                // an override mode (doc: 1064783)
                q = Quality.WithFlags(q, overrideFlag: true);
            }
            if (element == "field" && st.ControlMode.L != 0)
                q = Quality.WithFlags(q, controlInhibit: true);
            Value val = MakeValue(defn, raw, q, light ? 0 : e.T, light ? 0 : e.Vt, light ? 0 : e.Qt);
            return ItemValue(val, r.Item);
        }

        private static Value MakeValue(TagDef defn, in RawVal raw, ulong q, ulong t, ulong vt, ulong qt)
        {
            int type = defn.ValueType;
            if (defn.Type == Types.String)
                return Value.Str(raw.S ?? "", q, t, vt, qt);
            if (defn.Scaled)
                return Value.Real(defn.ToEng(RawDouble(defn, raw)), q, t, vt, qt);
            if (defn.Type == Types.Real)
                return Value.RealOf(type, raw.D, q, t, vt, qt);
            return Value.Int(type, raw.L, q, t, vt, qt);
        }

        private static double RawDouble(TagDef defn, in RawVal raw) =>
            defn.RawKind == RawKind.Real ? raw.D : raw.L;

        /// <summary>The engineering value of a raw value.</summary>
        public static JsonValue EngJson(TagDef defn, in RawVal raw)
        {
            if (defn.Type == Types.String)
                return JsonValue.Of(raw.S ?? "");
            if (defn.Scaled)
                return JsonValue.Of(defn.ToEng(RawDouble(defn, raw)));
            if (defn.Type == Types.Real)
                return JsonValue.Of(raw.D);
            return JsonValue.Of(raw.L);
        }

        internal static double EngDouble(TagDef defn, in RawVal raw)
        {
            if (defn.Scaled)
                return defn.ToEng(RawDouble(defn, raw));
            if (defn.Type == Types.Real)
                return raw.D;
            return raw.L;
        }

        // ------------------------------------------------------------------ writing
        /// <summary>Store a TAGREF from Cicode. Returns 0 or a hardware error.</summary>
        public int WriteRef(TagRef r, Value value, int flags, int? index, out int tagError)
        {
            NoteCodeUsed(r.Tag, r.Item);
            tagError = 0;
            Resolved res = ResolveRef(r);
            string item = res.Found ? res.Item : CitectText.Lower(CitectText.Trim(r.Item ?? ""));
            int t = value.T;
            if (item.Length > 0 && item != "v")
            {
                if (item == "q")
                    return t == Types.Quality ? ErrItemWrite : ErrInvalidArgument;
                if (item == "t" || item == "vt" || item == "qt")
                    return t == Types.Timestamp ? ErrItemWrite : ErrInvalidArgument;
            }
            if (
                !(
                    t == Types.Real
                    || t == Types.Long
                    || t == Types.String
                    || t == Types.ULong
                    || t == Types.Timestamp
                    || t == Types.Quality
                )
            )
                return ErrInvalidArgument;
            if (!res.Found)
            {
                tagError = ErrTagNotFound;
                return ErrTagNotFound;
            }
            return WriteResolved(res, WVal.Of(value), flags, index, "cicode");
        }

        /// <summary>A written value: a VM Value or a plain scenario / viewer value (JSON).</summary>
        public readonly struct WVal
        {
            public readonly bool IsValue;
            public readonly Value V;
            public readonly JsonValue? J;

            private WVal(bool isValue, Value v, JsonValue? j)
            {
                IsValue = isValue;
                V = v;
                J = j;
            }

            public static WVal Of(in Value v) => new WVal(true, v, null);

            public static WVal Of(JsonValue? j) => new WVal(false, default, j ?? JsonValue.Null);

            public int? Type => IsValue ? V.T : (int?)null;
        }

        public int WriteResolved(Resolved r, WVal value, int flags, int? index, string by)
        {
            if (r.Item.Length > 0 && r.Item != "v")
                return ErrInvalidArgument; // items are never written (doc: 1064781)
            if (r.Kind == "alarm")
                return Alarms!.SetProperty(r.Alarm!, r.Prop, value, by);
            TagDef defn = r.Tag!;
            TagState st = r.CachedState ??= State(defn);
            if (st.Locked && by != "user")
            {
                // a simulator-only lock: the write succeeds for the writer, the value stays
                LockedWrite?.Invoke(defn.Name, by);
                return 0;
            }
            int? idx = index ?? r.Index;
            string el = r.Element;
            if (
                defn.Kind == TagDef.Local
                && (
                    el == "override"
                    || el == "overridemode"
                    || el == "controlmode"
                    || el == "status"
                    || el == "valid"
                )
            )
                return ErrInvalidArgument;
            if (el == "valid" || el == "status")
                return ErrInvalidArgument;
            if (el == "overridemode")
                return SetOverrideMode(st, value, by);
            if (el == "controlmode")
            {
                long? n = AsInt(value);
                if (n == null || (n.Value != 0 && n.Value != 1))
                    return ErrInvalidArgument;
                TouchScalar(st, st.ControlMode, RawVal.Int(n.Value), null, by, "controlmode");
                return 0;
            }
            if ((el.Length == 0 || el == "field") && st.ControlMode.L != 0)
                return ErrInvalidArgument; // control inhibit (doc: 1064782)
            if (el == "override" && st.OverrideMode.L == 4)
                return ErrInvalidArgument; // dynamic override (doc: 1064783)
            int err = RawFrom(defn, value, (flags & TaskFlags.ScaleCheck) != 0, out RawVal raw);
            if (err != 0)
                return err;
            if (defn.Dims.Length > 0)
            {
                int i = idx ?? 0;
                if (i < 0 || i >= defn.Size)
                    return ErrArrayOverrun;
            }
            if (el == "override")
                return Store(st, st.Override, raw, Quality.QualityGood, idx, by, "override");
            if ((flags & TaskFlags.WriteLocal) != 0 || WriteDelay <= 0 || defn.Kind == TagDef.Local)
                return StoreField(st, raw, null, idx, by);
            st.Pending++;
            st.Status.Update(RawVal.Int(st.Status.L | 0x2), null, Clock.NowFiletime());
            Clock.CallLater(WriteDelay, () => ApplyLate(st, raw, idx, by), label: "write " + defn.Name);
            return 0;
        }

        private void ApplyLate(TagState st, RawVal raw, int? idx, string by)
        {
            st.Pending--;
            if (st.Locked && by != "user")
            {
                LockedWrite?.Invoke(st.Defn.Name, by);
                return; // locked while the write was on its way
            }
            StoreField(st, raw, null, idx, by);
        }

        /// <summary>A write a lock ignored: (tag name, writer: 'cicode', 'scenario' ...).</summary>
        public Action<string, string>? LockedWrite;

        /// <summary>Lock or unlock a tag (ctsim/lockTag): while locked only the user's own writes
        /// change it. 0, <see cref="ErrTagNotFound"/>, or <see cref="ErrInvalidArgument"/> for an
        /// alarm property.</summary>
        public int SetLock(string name, bool locked, string cluster = "")
        {
            Resolved r = Resolve(name, "", "", cluster);
            if (!r.Found)
                return ErrTagNotFound;
            if (r.Kind == "alarm" || r.Tag == null)
                return ErrInvalidArgument;
            State(r.Tag).Locked = locked;
            return 0;
        }

        // ------------------------------------------------------------------ property overrides
        private readonly Dictionary<string, TagDef> _overridden = new Dictionary<string, TagDef>(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>Override a tag's definition field for the simulation.</summary>
        public TagDef? SetProperty(string name, string? field, string? value, string cluster = "")
        {
            Resolved r = Resolve(name, "", "", cluster);
            if (!r.Found || r.Kind == "alarm" || r.Tag == null)
                return null;
            var d = r.Tag;
            if (field == null)
                d.ClearOverrides();
            else
                d.SetOverride(field, value);
            lock (_overridden)
            {
                if (d.HasOverrides)
                    _overridden[d.Name] = d;
                else
                    _overridden.Remove(d.Name);
            }
            return d;
        }

        /// <summary>The definition of a tag with overridden fields, null when it has none.</summary>
        public TagDef? Overridden(string name)
        {
            lock (_overridden)
                return _overridden.TryGetValue(name ?? "", out var d) ? d : null;
        }

        /// <summary>The tags with overridden fields now.</summary>
        public List<TagDef> OverriddenTags()
        {
            lock (_overridden)
                return new List<TagDef>(_overridden.Values);
        }

        /// <summary>Whether a tag is locked (false for an unknown name).</summary>
        public bool IsLocked(string name, string cluster = "")
        {
            Resolved r = Resolve(name, "", "", cluster);
            return r.Found && r.Kind != "alarm" && r.Tag != null && State(r.Tag).Locked;
        }

        /// <summary>The locked tags' names.</summary>
        public List<string> LockedNames()
        {
            var o = new List<string>();
            foreach (var kv in _states)
                if (kv.Value.Locked)
                    o.Add(kv.Value.Defn.Name);
            o.Sort(StringComparer.OrdinalIgnoreCase);
            return o;
        }

        /// <summary>A device-side change from the user / a scenario: the Field value (or
        /// <paramref name="element"/>) and optionally its quality, no range check. 0 or an error.</summary>
        public int Set(
            string name,
            JsonValue? value,
            JsonValue? quality = null,
            string by = "user",
            string element = "",
            int? index = null,
            string cluster = ""
        ) =>
            SetCore(
                name,
                value == null || value.IsNull ? (WVal?)null : WVal.Of(value),
                quality,
                by,
                element,
                index,
                cluster
            );

        /// <summary><see cref="Set(string, JsonValue, JsonValue, string, string, int?, string)"/> with a VM value.</summary>
        public int Set(
            string name,
            in Value value,
            JsonValue? quality = null,
            string by = "user",
            string element = "",
            int? index = null,
            string cluster = ""
        ) => SetCore(name, WVal.Of(value), quality, by, element, index, cluster);

        /// <summary>Set with a plain .NET value (int, double, string, bool or null).</summary>
        public int Set(
            string name,
            object? value,
            string? quality = null,
            string by = "user",
            string element = "",
            int? index = null,
            string cluster = ""
        ) =>
            SetCore(
                name,
                value == null ? (WVal?)null
                    : value is Value v ? WVal.Of(v)
                    : WVal.Of(JsonValue.From(value)),
                quality == null ? null : JsonValue.Of(quality),
                by,
                element,
                index,
                cluster
            );

        private int SetCore(
            string name,
            WVal? value,
            JsonValue? quality,
            string by,
            string element,
            int? index,
            string cluster
        )
        {
            Resolved r = Resolve(name, element, "", cluster);
            if (!r.Found)
                return ErrTagNotFound;
            if (r.Item.Length > 0 && r.Item != "v")
                return ErrInvalidArgument; // 'Tag.q' / 'Tag.t' are not values
            if (r.Kind == "alarm")
                return Alarms!.SetProperty(r.Alarm!, r.Prop, value ?? WVal.Of(JsonValue.Null), by);
            TagDef defn = r.Tag!;
            TagState st = State(defn);
            ulong? q = quality == null || quality.IsNull ? (ulong?)null : Quality.Parse(quality);
            if (value == null)
            {
                if (q == null)
                    return 0;
                TouchSame(st, st.Field, q, by, "field");
                if (Quality.IsGood(q.Value))
                    st.Valid.CopyFrom(st.Field, q, Clock.NowFiletime());
                return 0;
            }
            string el = r.Element.Length > 0 ? r.Element : "field";
            if (el != "field")
            {
                var r2 = new Resolved
                {
                    Kind = r.Kind,
                    Tag = defn,
                    Element = el,
                    Item = "",
                    Index = r.Index,
                    Name = r.Name,
                };
                return WriteResolved(r2, value.Value, 0, index, by);
            }
            int err = RawFrom(defn, value.Value, false, out RawVal raw);
            if (err != 0)
                return err;
            return StoreField(st, raw, q ?? Quality.QualityGood, index ?? r.Index, by);
        }

        public int SetQuality(string name, string quality, string by = "user") =>
            SetCore(name, null, JsonValue.Of(quality), by, "", null, "");

        /// <summary>TagSetOverrideQuality: the quality of the tag's Override
        /// element, its value kept. 0, <see cref="ErrTagNotFound"/>, or
        /// <see cref="ErrInvalidArgument"/> for a local variable or an alarm property.</summary>
        public int SetOverrideQuality(string name, ulong quality, string by = "cicode", string cluster = "")
        {
            Resolved r = Resolve(name, "", "", cluster);
            if (!r.Found)
                return ErrTagNotFound;
            if (r.Kind == "alarm" || r.Tag == null || r.Tag.Kind == TagDef.Local)
                return ErrInvalidArgument;
            TagState st = State(r.Tag);
            TouchSame(st, st.Override, quality, by, "override");
            return 0;
        }

        private int SetOverrideMode(TagState st, WVal value, string by)
        {
            long? n = AsInt(value);
            if (n == null || n.Value < 0 || n.Value > 4)
                return ErrInvalidArgument;
            ulong ts = Clock.NowFiletime();
            if (n.Value == 1)
                st.Override.CopyFrom(st.Field, Quality.QualityGood, ts);
            else if (n.Value == 2)
                st.Override.CopyFrom(st.Valid, Quality.QualityGood, ts);
            TouchScalar(st, st.OverrideMode, RawVal.Int(n.Value), null, by, "overridemode");
            return 0;
        }

        private int StoreField(TagState st, RawVal raw, ulong? q, int? idx, string by)
        {
            bool wasGood = Quality.IsGood(st.Field.Q);
            int err = Store(st, st.Field, raw, q, idx, by, "field");
            if (err != 0)
                return err;
            if (Quality.IsGood(st.Field.Q))
            {
                if (st.Field.IsArray && wasGood)
                {
                    // Valid already mirrors Field: follow the one element
                    int i = idx ?? 0;
                    st.Valid.UpdateItem(i, st.Field.At(i), st.Field.Q, st.Field.T);
                }
                else
                    st.Valid.CopyFrom(st.Field, st.Field.Q, st.Field.T);
            }
            if (by == "cicode")
            {
                // write ok; write pending stays while another delayed write is on its way
                long keep = st.Pending > 0 ? 0x2 : 0;
                st.Status.Update(RawVal.Int((st.Status.L & ~0xAL) | 0x4 | keep), null, st.Field.T);
            }
            return 0;
        }

        private int Store(TagState st, TagElem e, RawVal raw, ulong? q, int? idx, string by, string element)
        {
            if (e.IsArray)
            {
                int i = idx ?? 0;
                if (i < 0 || i >= e.Length)
                    return ErrArrayOverrun;
                ulong ts = Clock.NowFiletime();
                e.UpdateItem(i, raw, q, ts);
                Notify(st, e, by, element, i);
                return 0;
            }
            TouchScalar(st, e, raw, q, by, element);
            return 0;
        }

        private void TouchScalar(TagState st, TagElem e, in RawVal value, ulong? q, string by, string element)
        {
            e.Update(value, q, Clock.NowFiletime());
            Notify(st, e, by, element, null);
        }

        private void TouchSame(TagState st, TagElem e, ulong? q, string by, string element)
        {
            e.Touch(q, Clock.NowFiletime());
            Notify(st, e, by, element, null);
        }

        private void Notify(TagState st, TagElem e, string by, string element, int? index)
        {
            Serial++;
            long serial = Serial;
            st.Serial = serial;
            st.By = by;
            _changed[st.Defn.Key] = serial;
            if (_listeners.Count == 0)
                return;
            JsonValue v;
            bool eng = element == "field" || element == "valid" || element == "override";
            if (index != null)
                v = eng ? EngJson(st.Defn, e.At(index.Value)) : RawJson(e, e.At(index.Value));
            else if (e.IsArray)
            {
                var a = JsonValue.NewArray();
                for (int i = 0; i < e.Length; i++)
                    a.Add(eng ? EngJson(st.Defn, e.At(i)) : RawJson(e, e.At(i)));
                v = a;
            }
            else
                v = eng ? EngJson(st.Defn, e.Scalar) : RawJson(e, e.Scalar);
            var ch = new TagChange(serial, st.Defn.Name, st.Defn.Kind, by, element, v, e.Q, index);
            foreach (var fn in _listeners.ToArray())
                fn(ch);
        }

        private static JsonValue RawJson(TagElem e, in RawVal raw) =>
            e.Kind == RawKind.Str ? JsonValue.Of(raw.S ?? "")
            : e.Kind == RawKind.Real ? JsonValue.Of(raw.D)
            : JsonValue.Of(raw.L);

        /// <summary>(raw value of the tag's type, error) for an engineering value.</summary>
        private int RawFrom(TagDef defn, in WVal value, bool scaleCheck, out RawVal raw)
        {
            raw = default;
            if (defn.Type == Types.String)
            {
                raw = RawVal.Str(ToTagString(value));
                return 0;
            }
            int err = ToNumber(value, out Num num);
            if (err != 0)
                return err;
            if (scaleCheck && defn.Type != Types.Digital) // a digital takes any non-zero as 1 (guess)
            {
                var (lo, hi) = defn.CheckRange();
                if (
                    num.Lt(lo - 1.19e-7 * FloatOrder.Max(1.0, Math.Abs(lo)))
                    || num.Gt(hi + 1.19e-7 * FloatOrder.Max(1.0, Math.Abs(hi)))
                )
                    return ErrOutOfRange;
            }
            bool rawIsFloat;
            double rd;
            if (defn.Scaled)
            {
                rd = defn.ToRaw(num.D);
                rawIsFloat = true;
            }
            else
            {
                rd = num.D;
                rawIsFloat = !num.IsInt;
            }
            if (defn.Type == Types.Real)
            {
                if (defn.IsDouble || double.IsNaN(rd))
                {
                    raw = RawVal.Real(rd); // an 8-byte local REAL keeps the double
                    return 0;
                }
                rd = Math.Max(-TagTypes.Float32Max, Math.Min(TagTypes.Float32Max, rd));
                raw = RawVal.Real((float)rd);
                return 0;
            }
            if (rawIsFloat && (double.IsNaN(rd) || double.IsInfinity(rd)))
            {
                // an infinite / NaN REAL has no integer value (out of range is 434)
                return ErrInvalidConversion;
            }
            if (defn.Type == Types.Digital)
            {
                raw = RawVal.Int(
                    num.IsInt && !defn.Scaled ? (num.L != 0 || num.Big ? 1 : 0) : (rd != 0 ? 1 : 0)
                );
                return 0;
            }
            double n = RoundHalfAway(rd);
            var (tlo, thi) = defn.TypeRange();
            raw = RawVal.Int((long)Math.Max(tlo, Math.Min(thi, n)));
            return 0;
        }

        /// <summary>A value as a number (<see cref="ToNumber"/>): an integer (exact when it fits
        /// a long) or a double.</summary>
        internal struct Num
        {
            public bool IsInt;
            public bool Big; // an int beyond 64 bits: D holds it rounded
            public long L;
            public double D;

            public static Num Int(long l) =>
                new Num
                {
                    IsInt = true,
                    L = l,
                    D = l,
                };

            public static Num Float(double d) => new Num { D = d };

            public bool Lt(double x) => IsInt && !Big ? JsonNumbers.Cmp(L, x) == -1 : D < x;

            public bool Gt(double x) => IsInt && !Big ? JsonNumbers.Cmp(L, x) == 1 : D > x;
        }

        /// <summary>REAL to INT as the VM converts (half away from zero), as a double.</summary>
        internal static double RoundHalfAway(double x) =>
            x >= 0 ? Math.Floor(x + 0.5) : -Math.Floor(-x + 0.5);

        internal static long? AsInt(in WVal value)
        {
            if (ToNumber(value, out Num num) != 0)
                return null;
            if (!num.IsInt && (double.IsNaN(num.D) || double.IsInfinity(num.D)))
                return null;
            if (num.IsInt && !num.Big)
                return num.L;
            double r = RoundHalfAway(num.D);
            if (r >= 9.2233720368547758e18)
                return long.MaxValue;
            if (r <= -9.2233720368547758e18)
                return long.MinValue;
            return (long)r;
        }

        /// <summary>A value's payload as a number; a STRING by <see cref="StringNumber"/> (the
        /// first ',' read as '.'; trailing characters = error 433). An empty or blank string is
        /// 0 without an error.</summary>
        internal static int ToNumber(in WVal value, out Num num)
        {
            num = Num.Int(0);
            if (value.IsValue)
            {
                Value v = value.V;
                if (Types.IsRealType(v.T))
                {
                    num = Num.Float(v.D);
                    return 0;
                }
                if (v.T == Types.String || (v.T == Types.Byte && v.S != null))
                    return StringNumber(v.S ?? "", out num);
                if (v.T == Types.Void)
                    return 0;
                if (v.T == Types.Quality)
                {
                    ulong u = unchecked((ulong)v.I);
                    num =
                        u <= long.MaxValue
                            ? Num.Int((long)u)
                            : new Num
                            {
                                IsInt = true,
                                Big = true,
                                D = u,
                            };
                    return 0;
                }
                num = Num.Int(v.I);
                return 0;
            }
            JsonValue j = value.J!;
            switch (j.Kind)
            {
                case JsonKind.Bool:
                    num = Num.Float(j.Bool ? 1.0 : 0.0);
                    return 0;
                case JsonKind.Int:
                    num = j.FitsLong
                        ? Num.Int(j.Long)
                        : new Num
                        {
                            IsInt = true,
                            Big = true,
                            D = j.ToNumber(),
                        };
                    return 0;
                case JsonKind.Float:
                    num = Num.Float(j.FloatValue);
                    return 0;
                case JsonKind.String:
                    return StringNumber(j.Str, out num);
                case JsonKind.Null:
                    return 0;
                default:
                    return ErrTypeMismatch;
            }
        }

        /// <summary>The number in a string written to a numeric tag.</summary>
        private static int StringNumber(string v, out Num num)
        {
            num = Num.Int(0);
            if (IsNumberBlank(v))
                return 0;
            int comma = v.IndexOf(',');
            string s = comma >= 0 ? v.Substring(0, comma) + "." + v.Substring(comma + 1) : v;
            int i = 0,
                n = s.Length;
            bool separator = false;
            while (i < n && IsNumberWhite(s[i]))
            {
                if (s[i] >= '\x1c' && s[i] <= '\x1f')
                    separator = true;
                i++;
            }
            var t = new StringBuilder(n - i);
            if (i < n && (s[i] == '+' || s[i] == '-'))
                t.Append(s[i++]);
            int intStart = i;
            while (i < n && IsDigit(s[i]))
                t.Append(AsciiDigit(s[i++]));
            bool intDigits = i > intStart;
            bool isInt = true;
            if (intDigits)
            {
                if (i < n && s[i] == '.')
                {
                    t.Append(s[i++]);
                    isInt = false;
                    while (i < n && IsDigit(s[i]))
                        t.Append(AsciiDigit(s[i++]));
                }
            }
            else
            {
                if (i < n && s[i] == '.' && i + 1 < n && IsDigit(s[i + 1]))
                {
                    t.Append(s[i++]);
                    isInt = false;
                    while (i < n && IsDigit(s[i]))
                        t.Append(AsciiDigit(s[i++]));
                }
                else
                    return ErrTypeMismatch;
            }
            if (i < n && (s[i] == 'e' || s[i] == 'E'))
            {
                int j = i + 1;
                var e = new StringBuilder("e");
                if (j < n && (s[j] == '+' || s[j] == '-'))
                    e.Append(s[j++]);
                if (j < n && IsDigit(s[j]))
                {
                    while (j < n && IsDigit(s[j]))
                        e.Append(AsciiDigit(s[j++]));
                    t.Append(e);
                    isInt = false;
                    i = j;
                }
            }
            if (!IsNumberBlank(s.Substring(i)) || separator)
                return ErrTypeMismatch;
            string txt = t.ToString();
            if (isInt)
            {
                var b = BigInteger.Parse(txt, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (b >= long.MinValue && b <= long.MaxValue)
                    num = Num.Int((long)b);
                else
                    num = new Num
                    {
                        IsInt = true,
                        Big = true,
                        D = FloatText.FromInteger(b),
                    };
                return 0;
            }
            num = Num.Float(FloatText.ParseDecimal(txt, 0, txt.Length));
            return 0;
        }

        /// <summary>White space around a number written as text: the Unicode white space
        /// (U+0009..U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000..U+200A, U+2028, U+2029,
        /// U+202F, U+205F, U+3000) and the information separators U+001C..U+001F.</summary>
        private static bool IsNumberWhite(char c)
        {
            if (c <= 0x20)
                return c == 0x20 || (c >= 0x09 && c <= 0x0D) || (c >= 0x1C && c <= 0x1F);
            if (c < 0x85)
                return false;
            if (c == 0x85 || c == 0xA0)
                return true;
            if (c < 0x1680)
                return false;
            return c == 0x1680
                || (c >= 0x2000 && c <= 0x200A)
                || c == 0x2028
                || c == 0x2029
                || c == 0x202F
                || c == 0x205F
                || c == 0x3000;
        }

        private static bool IsNumberBlank(string s)
        {
            foreach (char c in s)
                if (!IsNumberWhite(c))
                    return false;
            return true;
        }

        /// <summary>An ASCII digit or another script's decimal digit (Unicode category Nd).</summary>
        private static bool IsDigit(char c) => c >= '0' && c <= '9' || (c > 127 && char.IsDigit(c));

        private static char AsciiDigit(char c) => c <= '9' ? c : (char)('0' + (int)char.GetNumericValue(c));

        /// <summary>A value stored into a STRING tag: numbers as the VM converts them
        /// (INT decimal, REAL '%.15G'); non-printable characters become spaces and the string keeps
        /// 255 characters (doc: 1064761).</summary>
        internal static string ToTagString(in WVal value)
        {
            string s;
            int? t = value.Type;
            if (value.IsValue)
            {
                Value v = value.V;
                if (Types.IsRealType(v.T))
                    s = Crt.FormatG15(v.D);
                else if (v.T == Types.String || v.T == Types.Byte)
                    s = v.S ?? "";
                else if (v.T == Types.Void)
                    s = "";
                else if (v.T == Types.Quality)
                    s = unchecked((ulong)v.I).ToString(CultureInfo.InvariantCulture);
                else
                    s = v.I.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                JsonValue j = value.J!;
                switch (j.Kind)
                {
                    case JsonKind.Bool:
                        s = j.Bool ? "1" : "0";
                        break;
                    case JsonKind.Float:
                        s = Crt.FormatG15(j.FloatValue);
                        break;
                    case JsonKind.Int:
                        s = j.BigValue.ToString(CultureInfo.InvariantCulture);
                        break;
                    case JsonKind.Null:
                        s = "";
                        break;
                    default:
                        s = j.Text;
                        break;
                }
            }
            if (t == Types.Quality || t == Types.Timestamp || t == Types.Object || t == Types.Void)
                s = "";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] < ' ' || chars[i] == '\x7f')
                    chars[i] = ' ';
            s = new string(chars);
            return s.Length > Types.StringMax ? s.Substring(0, Types.StringMax) : s;
        }

        // ------------------------------------------------------------------ notification
        /// <summary>Call fn(change) after every change; returns the unsubscribe action.</summary>
        public Action Subscribe(Action<TagChange> fn)
        {
            _listeners.Add(fn);
            return () => _listeners.Remove(fn);
        }

        /// <summary>Writes still on their way (written without WriteLocal, the write delay not over).</summary>
        public int PendingWrites()
        {
            int n = 0;
            foreach (var st in _states.Values)
                n += st.Pending;
            return n;
        }

        /// <summary>The tags changed after <paramref name="serial"/> (names), oldest change first.</summary>
        public List<string> ChangesSince(long serial)
        {
            var items = new List<(long s, string k)>();
            foreach (var kv in _changed)
                if (kv.Value > serial)
                    items.Add((kv.Value, kv.Key));
            items.Sort(
                (a, b) =>
                {
                    int c = a.s.CompareTo(b.s);
                    return c != 0 ? c : string.CompareOrdinal(a.k, b.k);
                }
            );
            return items.ConvertAll(x => _states[x.k].Defn.Name);
        }

        public Dictionary<string, TagState> States() => new Dictionary<string, TagState>(_states);

        /// <summary>name to (engineering value, quality name) of the given / every live tag.</summary>
        public List<(string name, object? value, string quality)> Snapshot(IEnumerable<string>? names = null)
        {
            var o = new List<(string, object?, string)>();
            var list = new List<string>();
            if (names != null)
                list.AddRange(names);
            else
                foreach (var st in _states.Values)
                    list.Add(st.Defn.Name);
            foreach (string k in list)
            {
                Value v = Get(k);
                o.Add((k, WorldValues.Payload(v), Quality.Name(v.Q)));
            }
            return o;
        }

        // ------------------------------------------------------------------ helpers
        /// <summary>What an unqualified reference reads: Field, the Override element in an
        /// override mode, and in the dynamic mode 4 the Valid element it tracks (doc: 1064783).</summary>
        internal static TagElem DefaultElement(TagState st)
        {
            long mode = st.OverrideMode.L;
            if (mode == 0)
                return st.Field;
            return mode == 4 ? st.Valid : st.Override;
        }

        internal static Value ItemValue(in Value val, string item)
        {
            if (item.Length == 0)
                return val;
            switch (item)
            {
                case "v":
                    return val.Bare(Value.QualityGood);
                case "q":
                    return WorldValues.Quality(val.Q);
                case "t":
                    return WorldValues.Timestamp(val.Ts);
                case "vt":
                    return WorldValues.Timestamp(val.Vts);
                case "qt":
                    return WorldValues.Timestamp(val.Qts);
            }
            return val;
        }
    }
}
