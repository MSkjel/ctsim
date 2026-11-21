using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>Pools filled on demand for code compiled outside a library (a page field, a test):
    /// every operand gets a record at first use. The offsets are not CtCmp32's page layout;
    /// compare such code with <see cref="NormalCode"/>.</summary>
    public sealed class LoosePools : IPoolOffsets
    {
        public readonly TextPool TextPool = new TextPool();
        public readonly List<TagKey> Tags = new List<TagKey>();
        private readonly Dictionary<TagKey, int> _tag = new Dictionary<TagKey, int>();
        public readonly List<ScaleKey> Scales = new List<ScaleKey>();
        private readonly Dictionary<ScaleKey, int> _scale = new Dictionary<ScaleKey, int>();
        public readonly List<string> Lookups = new List<string>();
        private readonly Dictionary<string, int> _lookup = new Dictionary<string, int>(
            StringComparer.Ordinal
        );
        public readonly List<VarRef> Vars = new List<VarRef>();
        private readonly Dictionary<VarRef, int> _var = new Dictionary<VarRef, int>();

        public long Text(string s) => TextPool.Add(s);

        public long Tag(TagKey t)
        {
            if (!_tag.TryGetValue(t, out int i))
            {
                _tag[t] = i = Tags.Count;
                Tags.Add(t);
            }
            return 38L * i;
        }

        public long CodeVar(VarRef v)
        {
            if (!_var.TryGetValue(v, out int i))
            {
                _var[v] = i = Vars.Count;
                Vars.Add(v);
            }
            return 65L * i;
        }

        public long DataAddress(VarRef v) => 256L * CodeVar(v) / 65;

        public long FuncLookup(FuncInfo f)
        {
            string n = f.SymbolName;
            if (!_lookup.TryGetValue(n, out int i))
            {
                _lookup[n] = i = Lookups.Count;
                Lookups.Add(n);
            }
            return 12L * i;
        }

        public long Scale(ScaleKey s)
        {
            if (!_scale.TryGetValue(s, out int i))
            {
                _scale[s] = i = Scales.Count;
                Scales.Add(s);
            }
            return 32L * i;
        }

        public Program ToProgram()
        {
            var p = new Program { Text = TextPool.Bytes() };
            for (int i = 0; i < Tags.Count; i++)
            {
                var t = Tags[i];
                p.TagRefs[38L * i] = new Compiled.TagRef(38L * i)
                {
                    Cluster = t.Cluster,
                    Tag = t.Tag,
                    Element = t.Element,
                    Item = t.Item,
                    Percentage = t.Percentage,
                    Lightweight = t.Lightweight,
                    ValueHandle = -1,
                };
            }
            for (int i = 0; i < Scales.Count; i++)
            {
                var s = Scales[i];
                p.Scales[32L * i] = new Compiled.Scale(32L * i)
                {
                    RawZero = s.RawZero,
                    RawFull = s.RawFull,
                    EngZero = s.EngZero,
                    EngFull = s.EngFull,
                };
            }
            for (int i = 0; i < Lookups.Count; i++)
                p.FuncLookups[12L * i] = new Compiled.FuncLookup(12L * i, Lookups[i]);
            for (int i = 0; i < Vars.Count; i++)
                p.AddCodeVar(
                    new Compiled.CodeVar(65L * i, Vars[i].Name, FunctionGen.RawType(Vars[i].Type))
                    {
                        DataAddress = 256L * i,
                        UnitType = -1,
                    }
                );
            return p;
        }
    }

    /// <summary>What a page field is compiled as: an RDB code field of type 16 is a
    /// command (P6); of type 15 a range field for MVX MVY ROT SZX SZY FIL COL_EXP (P3), a display
    /// field for TXT_STR (P4), else an expression (P2).</summary>
    public static class PageFields
    {
        public static readonly HashSet<string> RangeFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "MVX",
            "MVY",
            "ROT",
            "SZX",
            "SZY",
            "FIL",
            "COL_EXP",
        };

        public static readonly HashSet<string> DisplayFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "TXT_STR",
        };

        public static PageFieldKind KindOf(string table, string field, int rdbType)
        {
            if (rdbType == RdbFile.FCode)
                return PageFieldKind.Command;
            // a Cicode object's command is statements in a type-15 field (PGADV CMD
            // 'IF ... THEN ... END'); a vertex expression is a range field (PGVERTEX EXPR: Bar)
            if ((table == "PGADV" || table == "PGTREND") && field == "CMD")
                return PageFieldKind.Command;
            if (table == "Control" && field == "BGND_FUNC")
                return PageFieldKind.Command;
            if (table == "PGNUM" && field == "EXPR")
                return PageFieldKind.Display;
            if (table == "PGVERTEX" && field == "EXPR")
                return PageFieldKind.Range;
            if (RangeFields.Contains(field))
                return PageFieldKind.Range;
            if (DisplayFields.Contains(field))
                return PageFieldKind.Display;
            return PageFieldKind.Expression;
        }

        /// <summary>The text CtCmp32 compiles for an empty field, null = none (an empty PGTREND
        /// CMD compiles to `DefaultTrend()`).</summary>
        public static string? EmptyText(string table, string field) =>
            table == "PGTREND" && field == "CMD" ? "DefaultTrend()" : null;

        /// <summary>The source table of a page RDB section: its lower-case name + '.dbf', the page's
        /// own record (Control) from pages.dbf.</summary>
        public static string TableFile(string section) =>
            section == "Control" ? "pages.dbf" : CitectText.Lower(section) + ".dbf";

        public static string PageColumn(string section) => section == "Control" ? "NAME" : "PAGE";
    }

    /// <summary>Every code block of a project's page .RDB files compiled from its pg*.DBF source
    /// fields and compared instruction for instruction, operands resolved.</summary>
    public sealed class PageReport
    {
        public int Blocks;
        public int Same;
        public int NoSource;
        public int Failed;
        public readonly Dictionary<string, int> ByRule = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> Examples = new List<string>();
        public readonly Dictionary<string, int> ExamplesPerRule = new Dictionary<string, int>(
            StringComparer.Ordinal
        );

        public void Mismatch(string rule, string text, int keepPerRule)
        {
            ByRule[rule] = ByRule.TryGetValue(rule, out int n) ? n + 1 : 1;
            int e = ExamplesPerRule.TryGetValue(rule, out int k) ? k : 0;
            if (e < keepPerRule)
            {
                ExamplesPerRule[rule] = e + 1;
                Examples.Add("[" + rule + "] " + text);
            }
        }
    }

    public static class PageCheck
    {
        /// <summary>Compare every page block of <paramref name="projectDir"/> (a compiled unit of that
        /// project) with the code compiled from its DBF fields.</summary>
        public static PageReport Run(
            CompileUnit unit,
            string projectDir,
            int keepPerRule = 3,
            Func<string, bool>? pageFilter = null
        )
        {
            var rep = new PageReport();
            var tags = TagTable.For(unit);
            var defaults = new Dictionary<string, Expr?>(StringComparer.Ordinal);
            var tables = new Dictionary<string, List<Dictionary<string, string>>?>(StringComparer.Ordinal);
            var owners = PageOwners(unit, projectDir);
            var files = new List<string>(Directory.GetFiles(projectDir, "*.RDB"));
            files.Sort(StringComparer.Ordinal);
            foreach (string path in files)
            {
                string page = Path.GetFileNameWithoutExtension(path);
                if (page.StartsWith("_", StringComparison.Ordinal))
                    continue;
                if (pageFilter != null && !pageFilter(page))
                    continue;
                RdbFile f;
                try
                {
                    f = RdbFile.Open(path);
                }
                catch (Exception)
                {
                    continue;
                }
                if (!f.Has("CODE") || f.Has("Library"))
                    continue;
                var pc = Rdb.PageFromRdb(f);
                var tdata = NormalCode.ByDataAddress(pc.Program);
                var rowsOf = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
                string owner = owners.TryGetValue(CitectText.Lower(page), out string? od) ? od : projectDir;
                // one pool per page, the blocks in their CODE order: a tag keeps the spelling of its
                // first use in the page
                var pools = new LoosePools();
                foreach (var b in pc.Blocks)
                {
                    rep.Blocks++;
                    if (!rowsOf.TryGetValue(b.Table, out var rows))
                    {
                        rows = PageRows(owner, b.Table, page, tables);
                        rowsOf[b.Table] = rows;
                    }
                    string where = page + " " + b.Table + "." + b.Record + "." + b.Field;
                    if (b.Record >= rows.Count || !rows[b.Record].TryGetValue(b.Field, out string? text))
                    {
                        rep.NoSource++;
                        rep.Mismatch("no source row", where, keepPerRule);
                        continue;
                    }
                    var kind = PageFields.KindOf(b.Table, b.Field, b.Kind);
                    if (text.Length == 0)
                        text = PageFields.EmptyText(b.Table, b.Field) ?? text;
                    var want = NormalCode.Of(b.Code, pc.Program, tdata);
                    List<string> got;
                    string? why = null;
                    try
                    {
                        string vba =
                            b.Table
                            + b.Field
                            + (rows[b.Record].TryGetValue(RecNoKey, out string? rn) ? rn : "");
                        got = Compile(unit, tags, text, kind, defaults, pools, out why, vba);
                    }
                    catch (CodegenException e)
                    {
                        rep.Failed++;
                        rep.Mismatch("not compiled: " + e.Message, where + " | " + Short(text), keepPerRule);
                        continue;
                    }
                    if (why != null)
                    {
                        rep.Failed++;
                        rep.Mismatch(why, where + " | " + Short(text), keepPerRule);
                        continue;
                    }
                    if (Same(want, got))
                    {
                        rep.Same++;
                        continue;
                    }
                    rep.Mismatch(
                        kind + " " + RuleOf(want, got),
                        where + " | " + Short(text) + "\n        " + LibraryCheck.FirstDiff(want, got),
                        keepPerRule
                    );
                }
            }
            return rep;
        }

        /// <summary>A row's 1-based DBF record number (deleted rows counted), in the row dictionary.</summary>
        public const string RecNoKey = "#RECNO";

        private static string Short(string s) => s.Length > 160 ? s.Substring(0, 160) + "..." : s;

        /// <summary>One field compiled the way the page compiler does (normalised instructions).</summary>
        public static List<string> Compile(
            CompileUnit unit,
            TagTable tags,
            string text,
            PageFieldKind kind,
            Dictionary<string, Expr?> defaults,
            LoosePools pools,
            out string? why,
            string? vbaName = null
        )
        {
            why = null;
            var res = unit.CompileField(text, kind == PageFieldKind.Command ? "command" : "expression");
            if (res.Errors.Count > 0)
            {
                why = "source error " + res.Errors[0].Name;
                return new List<string>();
            }
            var code = FunctionGen.PageField(unit, tags, res.Node, kind, defaults, vbaName);
            // the pool records in code order (as the page's TAGREF / TEXT are filled)
            foreach (var ins in code.Code.Ins)
                if ((ins.Op == Op.Load || ins.Op == Op.Store) && ins.Ref is TagKey tk)
                    pools.Tag(tk);
            byte[] bytes = code.Code.Encode(pools);
            return NormalCode.Of(bytes, pools.ToProgram());
        }

        /// <summary>Page name (lower case) to the folder of the project whose pages.dbf lists it,
        /// the compiled project first, then the rest of the unit (a page of an included project is
        /// compiled into the root's folder with its own project's pg*.DBF rows).</summary>
        private static Dictionary<string, string> PageOwners(CompileUnit unit, string projectDir)
        {
            var o = new Dictionary<string, string>(StringComparer.Ordinal);
            var dirs = new List<string> { projectDir };
            for (int i = unit.Projects.Count - 1; i >= 0; i--)
                if (!string.IsNullOrEmpty(unit.Projects[i].Folder))
                    dirs.Add(unit.Projects[i].Folder!);
            foreach (string d in dirs)
            {
                string? p = Unit0.Find(d, "pages.dbf");
                if (p == null)
                    continue;
                foreach (var r in Unit0.Rows(p, new[] { "NAME" }))
                {
                    string k = CitectText.Lower(CitectText.Trim(r[0]));
                    if (k.Length > 0 && !o.ContainsKey(k))
                        o[k] = d;
                }
            }
            return o;
        }

        private static bool Same(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        /// <summary>A short name of where two blocks differ: the two opcodes at the first different
        /// instruction ("Bar/End", "StringFmt/End" ...).</summary>
        private static string RuleOf(List<string> a, List<string> b)
        {
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
            {
                if (a[i] == b[i])
                    continue;
                return OpName(a[i]) + "/" + OpName(b[i]);
            }
            return a.Count + " vs " + b.Count + " instructions";
        }

        private static string OpName(string s)
        {
            string[] p = s.Split(' ');
            return p.Length > 1 ? p[1] : s;
        }

        /// <summary>The source rows of one page in one table: the table's rows whose PAGE (pages.dbf:
        /// NAME) is the page, deleted rows left out, in file order.</summary>
        private static List<Dictionary<string, string>> PageRows(
            string projectDir,
            string section,
            string page,
            Dictionary<string, List<Dictionary<string, string>>?> cache
        )
        {
            string file = PageFields.TableFile(section);
            string ck = projectDir + "|" + file;
            if (!cache.TryGetValue(ck, out var all))
            {
                string? path = Unit0.Find(projectDir, file);
                all = null;
                if (path != null)
                {
                    all = new List<Dictionary<string, string>>();
                    Project.Dbf.ForEach(path, rec =>
                    {
                        var d = new Dictionary<string, string>(StringComparer.Ordinal);
                        var names = rec.Header.Names;
                        for (int k = 0; k < names.Length; k++)
                            d[names[k]] = rec.Field(k);
                        d[RecNoKey] = (rec.Row + 1).ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        );
                        all.Add(d);
                    });
                }
                cache[ck] = all;
            }
            var o = new List<Dictionary<string, string>>();
            if (all == null)
                return o;
            string col = PageFields.PageColumn(section);
            string low = CitectText.Lower(page);
            foreach (var r in all)
                if (r.TryGetValue(col, out string? pv) && CitectText.Lower(CitectText.Trim(pv)) == low)
                    o.Add(r);
            return o;
        }
    }
}
