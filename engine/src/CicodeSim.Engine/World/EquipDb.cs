using System;
using System.Collections.Generic;
using CicodeSim.Project;

namespace CicodeSim.World
{
    /// <summary>One equip.dbf instance: its name, project and non-empty fields (kept sparse: the
    /// column numbers of EquipDb.EquipColumns and their values).</summary>
    public sealed class EquipDef
    {
        public string Name { get; }
        public string Project { get; }
        private readonly byte[] _cols;
        private readonly string[] _vals;

        public EquipDef(string name, string project, byte[] cols, string[] vals)
        {
            Name = name;
            Project = project;
            _cols = cols;
            _vals = vals;
        }

        /// <summary>The non-empty fields in column order (equipment.EquipDef.fields).</summary>
        public IEnumerable<KeyValuePair<string, string>> Fields
        {
            get
            {
                for (int i = 0; i < _cols.Length; i++)
                    yield return new KeyValuePair<string, string>(EquipDb.EquipColumns[_cols[i]], _vals[i]);
            }
        }

        public string Key => CitectText.Lower(Name);
        public string Type => Field(2);
        public string Cluster => Field(1);

        private string Field(int col)
        {
            for (int i = 0; i < _cols.Length; i++)
                if (_cols[i] == col)
                    return _vals[i];
            return "";
        }

        /// <summary>The path without the last part ('' at the top level).</summary>
        public string Parent
        {
            get
            {
                int i = Name.LastIndexOf('.');
                return i >= 0 ? Name.Substring(0, i) : "";
            }
        }

        /// <summary>An equip.dbf field (EquipGetProperty's sField names it, doc: 1105206).</summary>
        public string Get(string name, string def = "")
        {
            if (!EquipDb.ColumnIndex.TryGetValue(CitectText.Upper(name), out int col))
                return def;
            for (int i = 0; i < _cols.Length; i++)
                if (_cols[i] == col)
                    return _vals[i];
            return def;
        }
    }

    /// <summary>The equipment of a compile unit.</summary>
    public sealed class EquipDb
    {
        public static readonly string[] EquipColumns =
        {
            "NAME",
            "CLUSTER",
            "TYPE",
            "AREA",
            "LOCATION",
            "COMMENT",
            "CUSTOM1",
            "CUSTOM2",
            "CUSTOM3",
            "CUSTOM4",
            "CUSTOM5",
            "CUSTOM6",
            "CUSTOM7",
            "CUSTOM8",
            "IODEVICE",
            "PAGE",
            "HELP",
            "DEFSTATE",
            "SCHEDULED",
            "TAGPREFIX",
            "PARAM",
            "COMPOSITE",
            "REFERENCE",
            "ALIAS",
            "CONTENT",
            "HIDDEN",
        };

        internal static readonly Dictionary<string, int> ColumnIndex = BuildColumnIndex();

        private static Dictionary<string, int> BuildColumnIndex()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < EquipColumns.Length; i++)
                d[EquipColumns[i]] = i;
            return d;
        }

        public Unit Unit { get; }
        public TagDb? Tags { get; }
        private Dictionary<string, EquipDef>? _equip;
        private List<EquipDef>? _order;
        private Dictionary<string, List<string>>? _children;
        private Dictionary<string, (string path, string project)>? _types;
        private Dictionary<(string, string), string>? _params;

        public EquipDb(Unit unit, TagDb? tags = null)
        {
            Unit = unit;
            Tags = tags;
        }

        /// <summary>Lower-case name to definition (compile order, the first definition wins).</summary>
        public Dictionary<string, EquipDef> All()
        {
            if (_equip == null)
            {
                // keyed by the name itself, compared without case (no lowered copies)
                var o = new Dictionary<string, EquipDef>(LowerComparer.Instance);
                var order = new List<EquipDef>();
                var share = new StringInterner(); // equal values shared over the unit's tables
                foreach (var (p, path) in Unit.Tables("equip.dbf", "compile"))
                {
                    int[]? picks = null;
                    var row = new string[EquipColumns.Length];
                    Dbf.ForEach(path, rec =>
                    {
                        if (picks == null)
                        {
                            picks = new int[EquipColumns.Length];
                            for (int c = 0; c < picks.Length; c++)
                                picks[c] = rec.Header.IndexOf(EquipColumns[c]);
                        }
                        string name = rec.Field(picks[0]);
                        if (name.Length == 0 || o.ContainsKey(name))
                            return;
                        row[0] = name;
                        int n = 1;
                        for (int c = 1; c < EquipColumns.Length; c++)
                        {
                            row[c] = picks[c] < 0 ? "" : rec.Field(picks[c], share);
                            if (row[c].Length > 0)
                                n++;
                        }
                        var cols = new byte[n];
                        var vals = new string[n];
                        n = 0;
                        for (int c = 0; c < EquipColumns.Length; c++)
                            if (row[c].Length > 0)
                            {
                                cols[n] = (byte)c;
                                vals[n] = row[c];
                                n++;
                            }
                        var e = new EquipDef(name, p.Name, cols, vals);
                        o[name] = e;
                        order.Add(e);
                    });
                }
                _equip = o;
                _order = order;
            }
            return _equip;
        }

        /// <summary>Every definition in definition order.</summary>
        public List<EquipDef> InOrder()
        {
            All();
            return _order!;
        }

        public EquipDef? Get(string? name) =>
            All().TryGetValue(CitectText.Trim(name ?? ""), out var e) ? e : null;

        public int Count => All().Count;

        /// <summary>The equipment directly below <paramref name="name"/> ('' = the top level).</summary>
        public List<string> Children(string? name)
        {
            if (_children == null)
            {
                var ch = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var e in InOrder())
                {
                    string k = CitectText.Lower(e.Parent);
                    if (!ch.TryGetValue(k, out var l))
                        ch[k] = l = new List<string>();
                    l.Add(e.Name);
                }
                _children = ch;
            }
            return _children.TryGetValue(CitectText.Lower(name ?? ""), out var c)
                ? new List<string>(c)
                : new List<string>();
        }

        /// <summary><paramref name="name"/> is <paramref name="root"/> or (with children) below
        /// it; case-insensitive.</summary>
        public static bool IsBelow(string? name, string? root, bool includeChildren = true)
        {
            string n = CitectText.Lower(name ?? ""),
                r = CitectText.Lower(root ?? "");
            return n == r || (includeChildren && n.StartsWith(r + ".", StringComparison.Ordinal));
        }

        /// <summary>An equipment filter value: 'equipA' = that one, 'equipA*' = it and below
        /// (doc: 1033455 examples).</summary>
        public bool Match(string? name, string? pattern)
        {
            string p = CitectText.Trim(pattern ?? "");
            if (p.EndsWith("*", StringComparison.Ordinal))
            {
                string b = p.Substring(0, p.Length - 1).TrimEnd('.');
                return IsBelow(name, b)
                    || CitectText.Lower(name ?? "").StartsWith(CitectText.Lower(b), StringComparison.Ordinal);
            }
            return CitectText.Lower(name ?? "") == CitectText.Lower(p);
        }

        /// <summary>Lower-case type name to (template file path or '', project).</summary>
        public Dictionary<string, (string path, string project)> Types()
        {
            if (_types == null)
            {
                var o = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
                foreach (var (p, r) in Unit.Rows("eqtype.dbf"))
                {
                    string n = r.Get("NAME");
                    if (n.Length == 0 || o.ContainsKey(CitectText.Lower(n)))
                        continue;
                    string tmpl = r.Get("TEMPLATE");
                    string path = tmpl.Length > 0 ? WinPath.Join(p.Dir, tmpl) : "";
                    o[CitectText.Lower(n)] = (path.Length > 0 && WinPath.IsFile(path) ? path : "", p.Name);
                }
                _types = o;
            }
            return _types;
        }

        /// <summary>(lower equipment, lower name) to value: eqparam.dbf then eqconfig.dbf rows.</summary>
        public Dictionary<(string, string), string> Parameters()
        {
            if (_params == null)
            {
                var o = new Dictionary<(string, string), string>();
                foreach (string table in new[] { "eqparam.dbf", "eqconfig.dbf" })
                {
                    foreach (var (_, r) in Unit.Rows(table))
                    {
                        string e = r.Get("EQUIP"),
                            n = r.Get("NAME");
                        if (e.Length > 0 && n.Length > 0)
                        {
                            var k = (CitectText.Lower(e), CitectText.Lower(n));
                            if (!o.ContainsKey(k))
                                o[k] = r.Get("VALUE");
                        }
                    }
                }
                _params = o;
            }
            return _params;
        }

        private List<(UnitProject project, DbfRow row)>? _runtimeParameters;

        internal IEnumerable<DbfRow> RuntimeParameters(string equipment, string cluster)
        {
            _runtimeParameters ??= Unit.Rows("eqparam.dbf");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, row) in _runtimeParameters)
            {
                string rowCluster = row.Get("CLUSTER");
                string name = row.Get("NAME");
                if (
                    name.Length > 0
                    && string.Equals(row.Get("EQUIP"), equipment, StringComparison.OrdinalIgnoreCase)
                    && (
                        rowCluster.Length == 0
                        || string.Equals(rowCluster, cluster, StringComparison.OrdinalIgnoreCase)
                    )
                    && seen.Add(name)
                )
                    yield return row;
            }
        }

        public string? Parameter(string? equipment, string? name) =>
            Parameters()
                .TryGetValue((CitectText.Lower(equipment ?? ""), CitectText.Lower(name ?? "")), out string? v)
                ? v
                : null;

        // ------------------------------------------------------------------ runtime fields
        /// <summary>The runtime fields (EquipGetProperty / EquipSetProperty, doc: 1105206,
        /// 1105214): what the scheduler engine (and EquipSetProperty) sets, not in equip.dbf.</summary>
        public static readonly string[] RuntimeFieldNames = { "MODE", "STATE", "DRMODE" };

        private readonly Dictionary<(string, string), string> _runtime =
            new Dictionary<(string, string), string>();

        public static bool IsRuntimeField(string? field) =>
            Array.IndexOf(RuntimeFieldNames, CitectText.Upper(CitectText.Trim(field ?? ""))) >= 0;

        /// <summary>A runtime field of an equipment instance.</summary>
        public string Runtime(EquipDef e, string field)
        {
            string f = CitectText.Upper(CitectText.Trim(field));
            if (_runtime.TryGetValue((e.Key, f), out string? v))
                return v;
            if (!Scheduled(e))
                return "Unknown";
            if (f != "STATE")
                return "0";
            // the state the simulation's scheduler gives it now (World.EquipScheduler), else DEFSTATE
            return ScheduledState?.Invoke(e) ?? e.Get("DEFSTATE");
        }

        /// <summary>The scheduler's state of a scheduled equipment now (set by World.Scheduler
        /// when a simulation has one), null = no schedule holds it.</summary>
        public Func<EquipDef, string?>? ScheduledState;

        /// <summary>equip.dbf SCHEDULED is TRUE (any case; 1 too).</summary>
        public static bool Scheduled(EquipDef e)
        {
            string s = CitectText.Upper(CitectText.Trim(e.Get("SCHEDULED")));
            return s == "TRUE" || s == "1";
        }

        /// <summary>Set a runtime field of an instance (no MODE check: EquipSetProperty makes
        /// that one itself).</summary>
        public void SetRuntime(EquipDef e, string field, string value) =>
            _runtime[(e.Key, CitectText.Upper(CitectText.Trim(field)))] = value ?? "";

        /// <summary>Set a runtime field by equipment name: 0, 289 no such equipment, 274 a field
        /// that is not a runtime field.</summary>
        public int SetRuntime(string? name, string? field, string? value)
        {
            var e = Get(name);
            if (e == null)
                return 289;
            if (!IsRuntimeField(field))
                return 274;
            SetRuntime(e, field!, value ?? "");
            return 0;
        }

        /// <summary>The runtime fields of an instance now (field, value), MODE STATE DRMODE order.</summary>
        public List<(string field, string value)> RuntimeOf(EquipDef e)
        {
            var o = new List<(string, string)>();
            foreach (string f in RuntimeFieldNames)
                o.Add((f, Runtime(e, f)));
            return o;
        }

        /// <summary>(item, kind, target) of an instance: its variable-tag items (kind 'tag',
        /// target = the tag name) and, with an alarm database on the tag database, its alarm items
        /// (kind 'alarm', target = the alarm tag).</summary>
        public List<(string item, string kind, string target)> Items(string? equipment)
        {
            var o = new List<(string, string, string)>();
            string low = CitectText.Lower(equipment ?? "");
            if (Tags != null)
            {
                foreach (var (eq, it, nm) in Tags.IterItems())
                    if (CitectText.Lower(eq) == low)
                        o.Add((it, "tag", nm));
                var al = Tags.Alarms;
                if (al != null)
                {
                    foreach (var a in al.All())
                        if (a.Equip.Length > 0 && CitectText.Lower(a.Equip) == low && a.Item.Length > 0)
                            o.Add((a.Item, "alarm", a.Tag));
                }
            }
            return o;
        }
    }
}
