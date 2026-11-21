using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>The TAGREF section of a compiled _LIBRARY.RDB.</summary>
    public static class RdbTagRefs
    {
        private sealed class Table
        {
            public Dictionary<string, (int size, int off, uint type)> Fields = new Dictionary<
                string,
                (int, int, uint)
            >(StringComparer.Ordinal);
            public int RecSize,
                NRec,
                Data;
        }

        private static string Latin1(byte[] b, int off, int n)
        {
            var c = new char[n];
            for (int i = 0; i < n; i++)
                c[i] = (char)b[off + i];
            return new string(c);
        }

        private static Dictionary<string, (int off, int size)> Sections(byte[] b)
        {
            int nsec = BitConverter.ToUInt16(b, 0x10a);
            var o = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            for (int i = 0; i < nsec; i++)
            {
                int e = 0x110 + 32 * i;
                int z = 0;
                while (z < 14 && b[e + 6 + z] != 0)
                    z++;
                string name = Latin1(b, e + 6, z);
                int off = (int)BitConverter.ToUInt32(b, e + 22);
                int size = (int)BitConverter.ToUInt32(b, e + 26);
                o[name] = (off, size);
            }
            return o;
        }

        private static Table ReadTable(byte[] b, int off)
        {
            var t = new Table();
            int nf = BitConverter.ToUInt16(b, off + 32);
            t.RecSize = BitConverter.ToUInt16(b, off + 34);
            t.NRec = (int)BitConverter.ToUInt32(b, off + 40);
            int p = off + 52;
            for (int i = 0; i < nf; i++)
            {
                int z = 0;
                while (z < 32 && b[p + z] != 0)
                    z++;
                string name = Latin1(b, p, z);
                t.Fields[name] = (
                    BitConverter.ToUInt16(b, p + 32),
                    BitConverter.ToUInt16(b, p + 34),
                    BitConverter.ToUInt32(b, p + 36)
                );
                p += 40;
            }
            t.Data = p;
            return t;
        }

        /// <summary>[(cluster, tag, element, item, lightweight)] of a compiled _LIBRARY.RDB.</summary>
        public static List<(string cluster, string tag, string element, string item, int lightweight)> Read(
            string path
        )
        {
            byte[] b = File.ReadAllBytes(path);
            var secs = Sections(b);
            int text = ReadTable(b, secs["TEXT"].off).Data;
            var t = ReadTable(b, secs["TAGREF"].off);

            string S(long o)
            {
                int start = (int)(text + o);
                int end = Array.IndexOf(b, (byte)0, start);
                return TextCodec.Decode(b, start, end - start);
            }

            var o2 = new List<(string, string, string, string, int)>();
            for (int i = 0; i < t.NRec; i++)
            {
                int r = t.Data + i * t.RecSize;
                var vals = new string[4];
                string[] keys = { "ClusterName", "TagName", "TagElement", "TagItem" };
                for (int k = 0; k < 4; k++)
                {
                    var (fsz, foff, _) = t.Fields[keys[k]];
                    long off =
                        fsz == 8
                            ? (long)BitConverter.ToUInt64(b, r + foff)
                            : BitConverter.ToUInt32(b, r + foff);
                    vals[k] = S(off);
                }
                int lw = t.Fields.TryGetValue("Lightweight", out var lf) ? b[r + lf.off] : 0;
                o2.Add((vals[0], vals[1], vals[2], vals[3], lw));
            }
            return o2;
        }
    }

    /// <summary>The acceptance check of the World on real projects.</summary>
    public static class WorldCheck
    {
        public const string DefaultUser = @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";
        public static readonly string[] DefaultProjects = { "ExampleSA", "Example" };

        private static double WorkingSetMb()
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return p.WorkingSet64 / 1e6;
        }

        private static double ManagedMb() => GC.GetTotalMemory(true) / 1e6;

        private static double R(double x, int d) => FloatText.Round(x, d);

        public static JsonValue CheckOne(string project, string rootMode = "self", string user = DefaultUser)
        {
            string pdir = Path.Combine(user, project);
            double baseWs = WorkingSetMb(),
                baseManaged = ManagedMb();
            var sw = Stopwatch.StartNew();
            var w = new World(pdir, root: rootMode == "self" ? project : null);
            double tCreate = sw.Elapsed.TotalSeconds;
            sw.Restart();
            JsonValue stats = w.Stats();
            double tLoad = sw.Elapsed.TotalSeconds;
            double memTablesRaw = WorkingSetMb() - baseWs;
            double managedTables = ManagedMb() - baseManaged; // a full collection
            double memTables = WorkingSetMb() - baseWs; // the working set after it
            var o = JsonValue.NewObject();
            o.Set("project", project);
            o.Set("root", w.Root);
            o.Set("unit", JsonValue.From(w.Unit.Names()));
            o.Set("stats", stats);
            o.Set("create_s", R(tCreate, 3));
            o.Set("load_s", R(tLoad, 2));
            o.Set("memory_mb", R(memTables, 1));
            o.Set("memory_before_gc_mb", R(memTablesRaw, 1));
            o.Set("managed_mb", R(managedTables, 1));
            string lib = Path.Combine(pdir, "_LIBRARY.RDB");
            if (File.Exists(lib))
            {
                var refs = RdbTagRefs.Read(lib);
                sw.Restart();
                var kinds = JsonValue.NewObject();
                var counts = new Dictionary<string, int>();
                var order = new List<string>();
                var unresolved = JsonValue.NewArray();
                var unresolvedRefs = new List<(string, string, string, string)>();
                foreach (var (cl, tag, el, it, _) in refs)
                {
                    var r = w.Tags.Resolve(tag, el, it, cl);
                    if (r.Found)
                    {
                        string key = r.Kind + "/" + r.Via;
                        if (!counts.ContainsKey(key))
                        {
                            counts[key] = 0;
                            order.Add(key);
                        }
                        counts[key]++;
                    }
                    else
                        unresolvedRefs.Add((cl, tag, el, it));
                }
                double tResolve = sw.Elapsed.TotalSeconds;
                foreach (var (cl, tag, el, it) in unresolvedRefs)
                    unresolved.Add(Why(user, cl, tag, el, it));
                foreach (string k in order)
                    kinds.Set(k, counts[k]);
                o.Set("tagrefs", refs.Count);
                o.Set("resolved", kinds);
                o.Set("unresolved", unresolved);
                o.Set("resolve_s", R(tResolve, 3));
            }
            double beforeManaged = ManagedMb(),
                beforeAlarms = WorkingSetMb();
            sw.Restart();
            w.Start();
            o.Set("alarm_start_s", R(sw.Elapsed.TotalSeconds, 2));
            sw.Restart();
            w.RunFor(1.0);
            o.Set("alarm_first_scans_s", R(sw.Elapsed.TotalSeconds, 2));
            o.Set("alarms_listed", w.Alarms.Count(0));
            o.Set("alarms_not_evaluated", w.Alarms.NotEvaluated.Count);
            double managedNow = ManagedMb();
            o.Set("memory_after_alarms_mb", R(WorkingSetMb() - baseWs, 1));
            o.Set("memory_alarms_mb", R(WorkingSetMb() - beforeAlarms, 1));
            o.Set("managed_alarms_mb", R(managedNow - beforeManaged, 1));
            o.Set("managed_total_mb", R(managedNow - baseManaged, 1));
            GC.KeepAlive(w);
            return o;
        }

        /// <summary>Why a TAGREF does not resolve: is it defined anywhere in the install?</summary>
        private static JsonValue Why(string user, string cl, string tag, string el, string it)
        {
            string reason =
                "defined in no variable.dbf / locvar.dbf of the install (the compiler warns W1007 Tag not defined; the runtime answers 424)";
            try
            {
                string low = CitectText.Lower(tag);
                var hits = new List<string>();
                var projects = new List<string>(WinPath.ListDir(user) ?? new List<string>());
                projects.Sort(StringComparer.Ordinal);
                foreach (string p in projects)
                {
                    foreach (string table in new[] { "variable.dbf", "locvar.dbf" })
                    {
                        string f = Path.Combine(user, p, table);
                        if (!File.Exists(f))
                            continue;
                        var t = Dbf.Columns(f, new[] { "NAME" });
                        if (t == null)
                            continue;
                        foreach (string n in t.Column("NAME"))
                            if (CitectText.Lower(n) == low)
                            {
                                hits.Add(p + "\\" + table);
                                break;
                            }
                    }
                }
                if (hits.Count > 0)
                    reason =
                        "defined only outside the unit: "
                        + string.Join(", ", hits.GetRange(0, Math.Min(4, hits.Count)));
            }
            catch (Exception e)
            {
                reason += " (" + e.Message + ")";
            }
            var o = JsonValue.NewObject();
            o.Set("cluster", cl);
            o.Set("tag", tag);
            o.Set("element", el);
            o.Set("item", it);
            o.Set("why", reason);
            return o;
        }

        /// <summary>One line of the report per project.</summary>
        public static string Report(JsonValue res)
        {
            var sb = new StringBuilder();
            var st = res["stats"];
            sb.AppendLine(
                $"{res["project"].Str} (root {res["root"].Str}, {st["projects"].Long} projects): {st["variables"].Long} tags, "
                    + $"{st["locals"].Long} local variables, {st["equipment"].Long} equipment, {st["equipment_items"].Long} equipment items"
            );
            sb.AppendLine($"   alarms {st["alarms"].Dump()}");
            sb.AppendLine(
                $"   create {res["create_s"].Text} s, read all tables {res["load_s"].Text} s, memory +{res["memory_mb"].Text} MB"
                    + $" (+{res["memory_before_gc_mb"].Text} MB before a full collection, managed +{res["managed_mb"].Text} MB); alarm start {res["alarm_start_s"].Text} s, first scans "
                    + $"{res["alarm_first_scans_s"].Text} s, alarm memory +{res["memory_alarms_mb"].Text} MB (managed +"
                    + $"{res["managed_alarms_mb"].Text} MB), process +{res["memory_after_alarms_mb"].Text} MB"
            );
            if (res.Has("tagrefs"))
            {
                int nUn = res["unresolved"].Count;
                long n = res["tagrefs"].Long;
                sb.AppendLine(
                    $"   TAGREF {n}: resolved {n - nUn} {res["resolved"].Dump()}, unresolved {nUn} ({res["resolve_s"].Text} s)"
                );
                int k = 0;
                foreach (var u in res["unresolved"].Items)
                {
                    if (k++ >= 20)
                        break;
                    sb.AppendLine(
                        $"      {CitectText.Quote(u["tag"].Str)} el={CitectText.Quote(u["element"].Str)} item={CitectText.Quote(u["item"].Str)}: {u["why"].Str}"
                    );
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>The World's definitions as normalised JSON lines.</summary>
    public static class WorldDump
    {
        public static readonly string[] PropertyNames =
        {
            "Address",
            "ArraySize",
            "ClusterName",
            "DataBitWidth",
            "Description",
            "EngUnitsHigh",
            "EngUnitsLow",
            "Equipment",
            "Format",
            "FormatDecPlaces",
            "FormatWidth",
            "FullName",
            "Item",
            "RangeHigh",
            "RangeLow",
            "TagName",
            "Type",
            "Units",
            "Custom1",
            "Custom2",
            "Custom3",
            "Custom4",
            "Custom5",
            "Custom6",
            "Custom7",
            "Custom8",
        };

        private static JsonValue Num(double? d) => d == null ? JsonValue.Null : JsonValue.Of(d.Value);

        private static JsonValue Pair(double a, double b) =>
            JsonValue.Array(new[] { JsonValue.Of(a), JsonValue.Of(b) });

        private static JsonValue SortedObject(IEnumerable<KeyValuePair<string, JsonValue>> pairs)
        {
            var l = new List<KeyValuePair<string, JsonValue>>(pairs);
            l.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            var o = JsonValue.NewObject();
            foreach (var kv in l)
                o.Set(kv.Key, kv.Value);
            return o;
        }

        private static JsonValue RowJson(DbfRow r)
        {
            var pairs = new List<KeyValuePair<string, JsonValue>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string n in r.Header.Names)
                if (seen.Add(n))
                    pairs.Add(new KeyValuePair<string, JsonValue>(n, JsonValue.Of(r.Get(n))));
            return SortedObject(pairs);
        }

        public static JsonValue TagJson(TagDef d)
        {
            var o = JsonValue.NewObject();
            o.Set("name", d.Name);
            o.Set("kind", d.Kind);
            o.Set("project", d.Project);
            o.Set("type_name", d.TypeName);
            o.Set("type", (long)d.Type);
            o.Set("dims", JsonValue.From(d.Dims));
            o.Set("raw_zero", Num(d.RawZero));
            o.Set("raw_full", Num(d.RawFull));
            o.Set("eng_zero", Num(d.EngZero));
            o.Set("eng_full", Num(d.EngFull));
            o.Set("eng_units", d.EngUnits);
            o.Set("format", d.Format);
            o.Set("comment", d.Comment);
            o.Set("cluster", d.Cluster);
            o.Set("equip", d.Equip);
            o.Set("item", d.Item);
            o.Set("deadband", Num(d.Deadband));
            o.Set("unit", d.UnitText);
            o.Set("address", d.Address);
            o.Set("custom", JsonValue.From(d.Custom));
            o.Set("record", d.Record);
            o.Set("historian", d.Historian);
            var (rz, rf) = d.RawRange();
            var (ez, ef) = d.EngRange();
            var (clo, chi) = d.CheckRange();
            o.Set("raw_range", Pair(rz, rf));
            o.Set("eng_range", Pair(ez, ef));
            o.Set("check_range", Pair(clo, chi));
            o.Set("scaled", d.Scaled);
            o.Set("value_type", (long)d.ValueType);
            o.Set("size", d.Size);
            o.Set("display_format", d.DisplayFormat);
            var (w, dec, flags) = TagTypes.FormatInfo(d.DisplayFormat);
            o.Set("format_info", JsonValue.From(new long[] { w, dec, flags }));
            o.Set("general_type", d.GeneralType);
            o.Set("bit_width", d.BitWidth);
            var props = JsonValue.NewObject();
            foreach (string p in PropertyNames)
                props.Set(p, JsonValue.Of(d.Property(p)));
            o.Set("props", props);
            var info = JsonValue.NewArray();
            for (int k = 0; k <= 33; k++)
                info.Add(JsonValue.Of(d.Info(k)));
            o.Set("info", info);
            return o;
        }

        private static JsonValue VarJson(AlarmVar v) =>
            !v.IsSet
                ? JsonValue.Null
                : JsonValue.Array(new[] { JsonValue.Of(v.Name), JsonValue.Of(v.Negated) });

        public static JsonValue AlarmJson(AlarmDb al, AlarmDef a)
        {
            var o = JsonValue.NewObject();
            o.Set("kind", a.Kind);
            o.Set("tag", a.Tag);
            o.Set("project", a.Project);
            o.Set("name", a.Name);
            o.Set("desc", a.Desc);
            o.Set("cluster", a.Cluster);
            o.Set("category", a.Category);
            o.Set("category_text", a.CategoryText);
            o.Set("area", a.Area);
            o.Set("priv", a.Priv);
            o.Set("comment", a.Comment);
            o.Set("help", a.Help);
            o.Set("equip", a.Equip);
            o.Set("item", a.Item);
            o.Set("custom", JsonValue.From(a.Custom));
            o.Set("delay", a.Delay);
            var vars = JsonValue.NewArray();
            foreach (var v in a.Vars)
                vars.Add(VarJson(v));
            o.Set("vars", vars);
            o.Set("expr", a.Expr);
            o.Set("setpoint", a.Setpoint);
            var lim = new List<KeyValuePair<string, JsonValue>>();
            if (a.Limits != null)
                foreach (var kv in a.Limits)
                    lim.Add(new KeyValuePair<string, JsonValue>(kv.Key, Num(kv.Value)));
            o.Set("limits", SortedObject(lim));
            o.Set("deadband", a.Deadband == 0 ? 0.0 : a.Deadband);
            var del = new List<KeyValuePair<string, JsonValue>>();
            if (a.Delays != null)
                foreach (var kv in a.Delays)
                    del.Add(new KeyValuePair<string, JsonValue>(kv.Key, JsonValue.Of(kv.Value)));
            o.Set("delays", SortedObject(del));
            var states = JsonValue.NewArray();
            foreach (var (desc, type) in a.States)
                states.Add(JsonValue.Array(new[] { JsonValue.Of(desc), JsonValue.Of(type) }));
            o.Set("states", states);
            o.Set("on_func", a.OnFunc);
            o.Set("off_func", a.OffFunc);
            o.Set("format", a.Format);
            o.Set("paging", a.Paging);
            o.Set("paging_group", a.PagingGroup);
            o.Set("historian", a.Historian);
            o.Set("priority", al.Priority(a));
            o.Set("is_analog", a.IsAnalog);
            return o;
        }

        /// <summary>Write the dump of <paramref name="w"/> (with the TAGREFs of
        /// <paramref name="rdb"/> when given): every record of <see cref="Records"/> as a JSON
        /// line, sorted by key, then by line.</summary>
        public static int Dump(World w, TextWriter output, string? rdb = null, bool lookups = true)
        {
            var lines = new List<(string k, string line)>();
            Records(w, (k, o) => lines.Add((k, o.Dump())), rdb, lookups);
            lines.Sort(
                (x, y) =>
                {
                    int c = string.CompareOrdinal(x.k, y.k);
                    return c != 0 ? c : string.CompareOrdinal(x.line, y.line);
                }
            );
            foreach (var (_, line) in lines)
            {
                output.Write(line);
                output.Write('\n');
            }
            return lines.Count;
        }

        /// <summary>Every record of the dump, unsorted.</summary>
        public static void Records(
            World w,
            Action<string, JsonValue> sink,
            string? rdb = null,
            bool lookups = true
        )
        {
            void Emit(string rec, string key, JsonValue body)
            {
                var o = JsonValue.NewObject();
                string k = rec + "\u0001" + key;
                o.Set("k", k);
                foreach (var kv in body.Pairs)
                    o.Set(kv.Key, kv.Value);
                sink(k, o);
            }

            // the unit
            var u = JsonValue.NewObject();
            u.Set("project", w.Project);
            u.Set("root", w.Root);
            u.Set("projects", JsonValue.From(w.Unit.Projects.ConvertAll(p => p.Name)));
            u.Set("search", JsonValue.From(w.Unit.Search.ConvertAll(p => p.Name)));
            u.Set("tables", JsonValue.From(w.Tags.Tables().ConvertAll(t => t.Project)));
            Emit("unit", "", u);
            Emit("stats", "", w.Stats());
            // variable tag rows
            foreach (var t in w.Tags.Tables())
            {
                var ix = t.Index();
                for (int row = 0; row < ix.Rows; row++)
                {
                    if (!ix.HasName(row))
                        continue;
                    Emit(
                        "tag",
                        t.Project + "|" + row.ToString("D7", CultureInfo.InvariantCulture),
                        TagJson(t.Definition(row))
                    );
                }
            }
            foreach (var kv in w.Tags.Locals())
                Emit("local", CitectText.Lower(kv.Key), TagJson(kv.Value));
            if (lookups)
            {
                // name and Equip.Item lookups: which definition each distinct name finds
                var done = new HashSet<string>(StringComparer.Ordinal);
                var doneItem = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in w.Tags.Tables())
                {
                    var ix = t.Index();
                    for (int row = 0; row < ix.Rows; row++)
                    {
                        if (!ix.HasName(row))
                            continue;
                        string low = CitectText.Lower(ix.Name(row));
                        string eqr = ix.Equip(row),
                            itr = ix.Item(row);
                        if (done.Add(low))
                        {
                            var d = w.Tags.FindName(low);
                            var o = JsonValue.NewObject();
                            o.Set(
                                "found",
                                d == null
                                    ? JsonValue.Null
                                    : JsonValue.Of(
                                        d.Kind
                                            + "|"
                                            + d.Project
                                            + "|"
                                            + d.Record.ToString(CultureInfo.InvariantCulture)
                                    )
                            );
                            Emit("where", low, o);
                        }
                        if (eqr.Length > 0 && itr.Length > 0)
                        {
                            string ilow = CitectText.Lower(eqr + "." + itr);
                            if (doneItem.Add(ilow))
                            {
                                var d = w.Tags.FindItem(ilow);
                                var o = JsonValue.NewObject();
                                o.Set(
                                    "found",
                                    d == null
                                        ? JsonValue.Null
                                        : JsonValue.Of(
                                            d.Kind
                                                + "|"
                                                + d.Project
                                                + "|"
                                                + d.Record.ToString(CultureInfo.InvariantCulture)
                                        )
                                );
                                Emit("where_item", ilow, o);
                            }
                        }
                    }
                }
            }
            foreach (var (eq, it, nm) in w.Tags.IterItems())
            {
                var o = JsonValue.NewObject();
                o.Set("equip", eq);
                o.Set("item", it);
                o.Set("tag", nm);
                Emit("item", CitectText.Lower(eq + "." + it) + "|" + nm, o);
            }
            // equipment
            foreach (var e in w.Equipment.InOrder())
            {
                var o = JsonValue.NewObject();
                o.Set("name", e.Name);
                o.Set("project", e.Project);
                o.Set("parent", e.Parent);
                o.Set("type", e.Type);
                var f = new List<KeyValuePair<string, JsonValue>>();
                foreach (var kv in e.Fields)
                    f.Add(new KeyValuePair<string, JsonValue>(kv.Key, JsonValue.Of(kv.Value)));
                o.Set("fields", SortedObject(f));
                o.Set("children", JsonValue.From(w.Equipment.Children(e.Name)));
                Emit("equip", e.Key, o);
            }
            foreach (var kv in w.Equipment.Types())
            {
                var o = JsonValue.NewObject();
                o.Set("path", kv.Value.path);
                o.Set("project", kv.Value.project);
                Emit("eqtype", kv.Key, o);
            }
            foreach (var kv in w.Equipment.Parameters())
            {
                var o = JsonValue.NewObject();
                o.Set("value", kv.Value);
                Emit("eqparam", kv.Key.Item1 + "|" + kv.Key.Item2, o);
            }
            // alarms
            foreach (var a in w.Alarms.All())
                Emit("alarm", a.Kind + "|" + a.Key, AlarmJson(w.Alarms, a));
            foreach (var kv in w.Alarms.Categories())
                Emit("category", kv.Key.ToString(CultureInfo.InvariantCulture), RowObj(kv.Value));
            foreach (var kv in w.Alarms.Priorities())
                Emit("priority", kv.Key.ToString(CultureInfo.InvariantCulture), RowObj(kv.Value));
            foreach (var kv in w.Alarms.HardwareMessages())
                Emit("hardalm", kv.Key.ToString(CultureInfo.InvariantCulture), RowObj(kv.Value));
            // labels, parameters, users
            foreach (var kv in w.Unit.Labels())
            {
                var o = JsonValue.NewObject();
                o.Set("expr", kv.Value);
                Emit("label", kv.Key, o);
            }
            foreach (var kv in w.Unit.Params())
            {
                var o = JsonValue.NewObject();
                o.Set("data", kv.Value);
                Emit("param", kv.Key.Item1 + "|" + kv.Key.Item2, o);
            }
            foreach (var kv in w.Users.UserDefs())
            {
                var o = JsonValue.NewObject();
                o.Set("name", kv.Value.Name);
                o.Set("full_name", kv.Value.FullName);
                o.Set("type", kv.Value.Type);
                o.Set("roles", JsonValue.From(kv.Value.Roles));
                o.Set("comment", kv.Value.Comment);
                o.Set("project", kv.Value.Project);
                Emit("user", kv.Key, o);
            }
            foreach (var kv in w.Users.Roles())
            {
                var r = kv.Value;
                var o = JsonValue.NewObject();
                o.Set("name", r.Name);
                o.Set("group", r.Group);
                o.Set("view_areas", Sorted(r.ViewAreas));
                o.Set("global_privs", Sorted(r.GlobalPrivs));
                var ap = new List<KeyValuePair<string, JsonValue>>();
                foreach (var kv2 in r.AreaPrivs)
                    ap.Add(
                        new KeyValuePair<string, JsonValue>(
                            kv2.Key.ToString(CultureInfo.InvariantCulture),
                            Sorted(kv2.Value)
                        )
                    );
                o.Set("area_privs", SortedObject(ap));
                o.Set("entry_func", r.EntryFunc);
                o.Set("exit_func", r.ExitFunc);
                o.Set("comment", r.Comment);
                o.Set("project", r.Project);
                Emit("role", kv.Key, o);
            }
            foreach (var kv in w.Users.Groups())
            {
                var o = JsonValue.NewObject();
                o.Set("members", JsonValue.From(kv.Value));
                Emit("group", kv.Key, o);
            }
            // the compiled TAGREFs
            if (rdb != null && File.Exists(rdb))
            {
                int i = 0;
                foreach (var (cl, tag, el, it, lw) in RdbTagRefs.Read(rdb))
                {
                    var r = w.Tags.Resolve(tag, el, it, cl);
                    var o = JsonValue.NewObject();
                    o.Set("cluster", cl);
                    o.Set("tag", tag);
                    o.Set("element", el);
                    o.Set("item", it);
                    o.Set("lightweight", lw);
                    o.Set("found", r.Found);
                    o.Set("kind", JsonValue.Of(r.Kind));
                    o.Set("via", r.Via);
                    o.Set("name", r.Name);
                    o.Set("r_element", r.Element);
                    o.Set("r_item", r.Item);
                    o.Set("prop", r.Prop);
                    o.Set("index", r.Index == null ? JsonValue.Null : JsonValue.Of(r.Index.Value));
                    Emit("tagref", i.ToString("D6", CultureInfo.InvariantCulture), o);
                    i++;
                }
            }
        }

        private static JsonValue Sorted(IEnumerable<int> s)
        {
            var l = new List<int>(s);
            l.Sort();
            return JsonValue.From(l);
        }

        private static JsonValue RowObj(DbfRow r)
        {
            var o = JsonValue.NewObject();
            o.Set("row", RowJson(r));
            return o;
        }
    }

    /// <summary>The world commands of ctsim: world-check, world-dump, world-scenario, world-ops.</summary>
    public static class WorldCli
    {
        public static bool Handles(string cmd) =>
            cmd == "world-check" || cmd == "world-dump" || cmd == "world-scenario" || cmd == "world-ops";

        private static string? Opt(List<string> args, string name)
        {
            int i = args.IndexOf(name);
            if (i < 0 || i + 1 >= args.Count)
                return null;
            string v = args[i + 1];
            args.RemoveRange(i, 2);
            return v;
        }

        private static bool Flag(List<string> args, string name) => args.Remove(name);

        public static int Main(string[] argv)
        {
            var args = new List<string>(argv);
            string cmd = args[0];
            args.RemoveAt(0);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                NewLine = "\n",
                AutoFlush = false,
            };
            try
            {
                if (cmd == "world-check")
                    return Check(args, stdout);
                if (cmd == "world-dump")
                    return DumpCmd(args, stdout);
                if (cmd == "world-ops")
                {
                    // world-ops <project dir> <ops.jsonl> <out.txt>
                    WorldOps.Run(args[0], args[1], args[2]);
                    return 0;
                }
                return ScenarioCmd(args, stdout);
            }
            finally
            {
                stdout.Flush();
            }
        }

        private static int Check(List<string> args, TextWriter stdout)
        {
            string root = Opt(args, "--root") ?? "self";
            string user = Opt(args, "--user") ?? WorldCheck.DefaultUser;
            string? json = Opt(args, "--json");
            string? one = Opt(args, "--one");
            if (one != null)
            {
                stdout.Write(WorldCheck.CheckOne(one, root, user).Dump());
                stdout.Write('\n');
                return 0;
            }
            var projects = args.Count > 0 ? args : new List<string>(WorldCheck.DefaultProjects);
            var results = JsonValue.NewArray();
            string exe = Process.GetCurrentProcess().MainModule!.FileName!;
            foreach (string p in projects)
            {
                // each project in its own process: clean memory numbers
                var psi = new ProcessStartInfo(
                    exe,
                    $"world-check --one \"{p}\" --root {root} --user \"{user}\""
                )
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var proc = Process.Start(psi)!;
                string outText = proc.StandardOutput.ReadToEnd();
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    stdout.Write(p + " FAILED " + err + "\n");
                    continue;
                }
                var lines = CitectIni.SplitLines(outText.Trim());
                var res = JsonValue.Parse(lines[lines.Count - 1]);
                results.Add(res);
                stdout.Write(WorldCheck.Report(res));
                stdout.Flush();
            }
            if (json != null)
                File.WriteAllText(json, results.DumpSpaced(), new UTF8Encoding(false));
            return 0;
        }

        private static int DumpCmd(List<string> args, TextWriter stdout)
        {
            string? root = Opt(args, "--root");
            string? output = Opt(args, "--out");
            string? rdb = Opt(args, "--rdb");
            bool noLookups = Flag(args, "--no-lookups");
            string dir = args[0];
            var w = new World(dir, root: root, start: 0);
            rdb ??= Path.Combine(dir, "_LIBRARY.RDB");
            var sw = Stopwatch.StartNew();
            int n;
            using (
                var fh =
                    output != null
                        ? new StreamWriter(output, false, new UTF8Encoding(false)) { NewLine = "\n" }
                        : null
            )
            {
                n = WorldDump.Dump(w, (TextWriter?)fh ?? stdout, rdb, !noLookups);
            }
            Console.Error.WriteLine($"{n} records in {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }

        private static int ScenarioCmd(List<string> args, TextWriter stdout)
        {
            string? start = Opt(args, "--start");
            string? root = Opt(args, "--root");
            string dir = args[0],
                scenario = args[1],
                trace = args[2];
            var w = new World(dir, root: root, start: start ?? "2026-10-01T08:00:00Z");
            using var tw = new TraceWriter(
                trace,
                project: w.Project,
                clock: w.Clock,
                scenario: Path.GetFileName(scenario)
            );
            w.AttachTrace(tw);
            w.Start();
            var res = w.RunScenario(scenario);
            tw.Close();
            var o = JsonValue.NewObject();
            o.Set("passed", res.Passed);
            o.Set("failed", res.Failed.Count);
            o.Set("skipped", res.Skipped.Count);
            o.Set("errors", res.Errors.Count);
            o.Set("end", res.End);
            stdout.Write(o.Dump());
            stdout.Write('\n');
            return 0;
        }
    }
}
