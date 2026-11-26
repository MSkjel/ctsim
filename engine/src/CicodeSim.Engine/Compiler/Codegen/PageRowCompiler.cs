using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Page;
using CicodeSim.Project;
using CicodeSim.World.Display;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>A page compiled from its source rows instead of read from its .RDB.</summary>
    public static class PageRowCompiler
    {
        /// <summary>The RDB schema of a page: section, then field:type per field (every page .RDB
        /// has the same 26 sections; these are the record sections).</summary>
        private const string Schema =
            "PGTEXT AN:3 VAR_A:47 VAR_B:47 VAR_C:47 STR000:1 STR001:1 STR010:1 STR011:1 STR100:1 STR101:1 STR110:1 STR111:1 FONT000:3 FONT001:3 FONT010:3 FONT011:3 FONT100:3 FONT101:3 FONT110:3 FONT111:3 VISIBLE:15 COMMENT:1\n"
            + "PGSYM AN:3 VAR_A:47 VAR_B:47 VAR_C:47 SYMB000:1 SYMB001:1 SYMB010:1 SYMB011:1 SYMB100:1 SYMB101:1 SYMB110:1 SYMB111:1 MODE:3 VISIBLE:15 COMMENT:1\n"
            + "PGNUM AN:3 EXPR:15 FONT:3 VISIBLE:15 COMMENT:1\n"
            + "PGBAR AN:3 EXPR:15 BAR:1 VISIBLE:15 COMMENT:1\n"
            + "PGSTR AN:3 EXPR:15 OFF_TEXT:15 OFF_FONT:3 ON_TEXT:15 ON_FONT:3 VISIBLE:15 COMMENT:1\n"
            + "PGADV AN:3 CMD:15 COMMENT:1\n"
            + "PGBUTTON PRIV:3 AREA:3 TEXT:1 CMD:16 MSG_LOG:1 DEVICE:1 AN:3 LEFT_AN:3 RIGHT_AN:3 UP_AN:3 DOWN_AN:3 FONT:3 WIDTH:3 HEIGHT:3 DOWN_CMD:16 REPEAT_CMD:16 DISABLED:15 GREY_STYLE:3 VIS_MODE:3 VISIBLE:15 COMMENT:1 DISMODE:3 RPT_RATE:3 MVX:15 MVXMINPT:3 MVXMAXPT:3 MVXMODE:3 MVY:15 MVYMINPT:3 MVYMAXPT:3 MVYMODE:3\n"
            + "PGTREND AN:3 TRENDNO:1 PEN1:1 PEN2:1 PEN3:1 PEN4:1 PEN5:1 PEN6:1 PEN7:1 PEN8:1 CMD:15 VISIBLE:15 PRIV:3 AREA:3 DEVICE:1 COMMENT:1 CLUSTER:1 PERIODIC:7\n"
            + "PGANSYM AN:3 EXPR:15 OFF_SYMBOL:1 SYMBOL_1:1 SYMBOL_2:1 SYMBOL_3:1 SYMBOL_4:1 SYMBOL_5:1 SYMBOL_6:1 SYMBOL_7:1 SYMBOL_8:1 MODE:3 VISIBLE:15 COMMENT:1\n"
            + "PGSLIDER PRIV:3 AN:3 TAG:23 THUMB:1 X_SPAN:3 Y_SPAN:3 MODE:3 VISIBLE:15 COMMENT:1\n"
            + "PGSIZMVE AN:3 SIZEX:15 SIZEY:15 POSITIONX:15 POSITIONY:15 HOTOFFSETX:3 HOTOFFSETY:3 COMMENT:1\n"
            + "PGDYNOBJ TYPE:1 AN:3 GROUP:3 SZX:15 SZXMINPC:3 SZXMAXPC:3 SZXORG:3 SZXMODE:3 SZY:15 SZYMINPC:3 SZYMAXPC:3 SZYORG:3 SZYMODE:3 ROT:15 ROTSL:23 ROTMINPC:3 ROTMAXPC:3 ROTXORG:3 ROTYORG:3 ROTMODE:3 ROTSLMODE:3 MVX:15 MVXSL:23 MVXMINPT:3 MVXMAXPT:3 MVXMODE:3 MVXSLMODE:3 MVY:15 MVYSL:23 MVYMINPT:3 MVYMAXPT:3 MVYMODE:3 MVYSLMODE:3 VISIBLE:15 VISMODE:3 COL_A:15 COL_B:15 COL_C:15 COL_D:15 COL_E:15 COL_ARR:15 COL_EXP:15 COLMODE:3 TXT_A:15 TXT_B:15 TXT_C:15 TXT_D:15 TXT_E:15 TXT_STR:15 SET_ARR:15 TXTMODE:3 FIL:15 FILMINPC:3 FILMAXPC:3 FILMODE:3 DISABLED:15 DISMODE:3 GREY_STYLE:3 DESC:1 COMMENT:1 PRIV:3 AREA:3 MSG_LOG:1 DEVICE:1\n"
            + "PGTOUCH AN:3 ACTION:3 RPT_RATE:3 CMD:16 DOWN_CMD:16 DBLCLK_CMD:16 REPEAT_CMD:16 MSG_LOG:1 MSG_LOGDN:1 MSG_LOGDBL:1 MSG_LOGRPT:1\n"
            + "PGVERTEX INDEX:3 AN:3 EXPR:15 X1:3 Y1:3 X2:3 Y2:3\n"
            + "PGALMIND AN:3 EXPR:1 INCLUDE:3 INSIDE:3 WIDTH:3 PADDING:3 COLOUR:26 FLAG_ON:3 FLAG_LOC:3 EQHIER:3\n"
            + "PGKEY PRIV:3 AREA:3 KEY_SEQ:1 CMD:16 MSG_LOG:1 DEVICE:1 AN:3 LEFT_AN:3 RIGHT_AN:3 UP_AN:3 DOWN_AN:3 COMMENT:1\n"
            + "Control NAME:2 ALIAS:2 TITLE:2 AREAS:6 BGND_FILE:2 SYMB_FILE:2 PAGE_INCL:2 NEXT_PAGE:2 PREV_PAGE:2 ENTRY_FUNC:16 EXIT_FUNC:16 BGND_FUNC:15 SCANTIME:3 LOGDEVICE:2 READY_FUNC:16 CLUSTER:2 CINHERIT:3 IGNOREVQ:3 CON_TYPE:2 ACT_FUNC:16 DEACT_FUNC:16";

        public const int FSlider = 23,
            FBool = 7,
            FAreas = 6,
            FColour = 26,
            FTagVar = 47;

        /// <summary>The record sections (DBF table = lower-case section name + '.dbf'; Control = pages.dbf).</summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Field, int Type)>> Sections =
            ParseSchema();

        private static IReadOnlyDictionary<string, IReadOnlyList<(string, int)>> ParseSchema()
        {
            var d = new Dictionary<string, IReadOnlyList<(string, int)>>(StringComparer.Ordinal);
            foreach (string line in Schema.Split('\n'))
            {
                string[] p = line.Split(' ');
                var fields = new List<(string, int)>();
                for (int i = 1; i < p.Length; i++)
                {
                    int c = p[i].IndexOf(':');
                    fields.Add(
                        (p[i].Substring(0, c), int.Parse(p[i].Substring(c + 1), CultureInfo.InvariantCulture))
                    );
                }
                d[p[0]] = fields;
            }
            return d;
        }

        /// <summary>The value of an empty int field.</summary>
        public static long EmptyInt(string section, string field)
        {
            switch (field)
            {
                case "GROUP":
                    return section == "PGDYNOBJ" ? -1 : 0;
                case "SZXMAXPC":
                case "SZYMAXPC":
                case "FILMAXPC":
                    return 100;
                case "ROTMAXPC":
                    return 360;
                case "SCANTIME":
                case "IGNOREVQ":
                    return section == "Control" ? -1 : 0;
                case "GREY_STYLE":
                    return section == "PGBUTTON" ? 1 : 0;
                case "RPT_RATE":
                    return section == "PGBUTTON" ? 500 : 0;
            }
            return 0;
        }

        /// <summary>The project's own rows of a page (what CtCmp32 compiles it from): per section
        /// the rows of its table whose PAGE (pages.dbf: NAME) is the page, in file order, deleted
        /// rows left out; the Control row from pages.dbf. Empty when the project has no rows.</summary>
        public static (
            Dictionary<string, List<Dictionary<string, string>>> Tables,
            Dictionary<string, string>? Control
        ) ProjectRows(string projectDir, string page)
        {
            var tables = new Dictionary<string, List<Dictionary<string, string>>>(
                StringComparer.OrdinalIgnoreCase
            );
            Dictionary<string, string>? control = null;
            string low = CitectText.Lower(CitectText.Trim(page));
            foreach (var kv in Sections)
            {
                string file = PageFields.TableFile(kv.Key);
                string? path = Unit0.Find(projectDir, file);
                if (path == null)
                    continue;
                string col = PageFields.PageColumn(kv.Key);
                var rows = new List<Dictionary<string, string>>();
                Project.Dbf.ForEach(path, rec =>
                {
                    int k = rec.Header.IndexOf(col);
                    if (k < 0 || CitectText.Lower(CitectText.Trim(rec.Field(k))) != low)
                        return;
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var names = rec.Header.Names;
                    for (int i = 0; i < names.Length; i++)
                        d[names[i]] = rec.Field(i);
                    rows.Add(d);
                });
                if (kv.Key == "Control")
                    control = rows.Count > 0 ? rows[0] : null;
                else if (rows.Count > 0)
                    tables[CitectText.Lower(kv.Key)] = rows;
            }
            return (tables, control);
        }

        /// <summary>The project has rows of the page (ProjectRows is not empty), from an index of
        /// each section table's page names, read again when the table's time or size changes.</summary>
        public static bool HasRows(string projectDir, string page)
        {
            string low = CitectText.Lower(CitectText.Trim(page));
            // one listing gives each table's time and size
            var files = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
            try
            {
                foreach (var fi in new DirectoryInfo(projectDir).EnumerateFiles())
                    files[CitectText.Lower(fi.Name)] = fi;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
            foreach (var kv in Sections)
                if (
                    files.TryGetValue(PageFields.TableFile(kv.Key), out var fi)
                    && PageNames(fi, PageFields.PageColumn(kv.Key)).Contains(low)
                )
                    return true;
            return false;
        }

        private static readonly Dictionary<
            string,
            (DateTime Time, long Length, HashSet<string> Pages)
        > PageIndex = new Dictionary<string, (DateTime, long, HashSet<string>)>(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>A section table's page names (lower case, trimmed).</summary>
        private static HashSet<string> PageNames(FileInfo table, string col)
        {
            string path = table.FullName;
            DateTime time = table.LastWriteTimeUtc;
            long length = table.Length;
            lock (PageIndex)
                if (
                    PageIndex.TryGetValue(path, out var known)
                    && known.Time == time
                    && known.Length == length
                )
                    return known.Pages;
            var pages = new HashSet<string>(StringComparer.Ordinal);
            Project.Dbf.ForEach(path, rec =>
            {
                int k = rec.Header.IndexOf(col);
                if (k >= 0)
                    pages.Add(CitectText.Lower(CitectText.Trim(rec.Field(k))));
            });
            lock (PageIndex)
                PageIndex[path] = (time, length, pages);
            return pages;
        }

        /// <summary>Compile a page from its rows.</summary>
        public static CompiledPage Compile(
            CompileUnit unit,
            TagTable tags,
            string page,
            IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables,
            IReadOnlyDictionary<string, string>? control,
            Func<string, long?> label,
            List<string>? notes = null,
            string project = ""
        )
        {
            var cp = new CompiledPage(page);
            var pools = new LoosePools();
            var defaults = new Dictionary<string, Compiler.Syntax.Expr?>(StringComparer.Ordinal);
            var code = new List<byte>(4096) { 0, 0 }; // offset 0 is never a block: a pad word
            int glbStr = GlbStrIndex(unit);
            foreach (var kv in Sections)
            {
                string section = kv.Key;
                List<Dictionary<string, string>>? rows = null;
                if (section == "Control")
                {
                    var one = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (control != null)
                        foreach (var c in control)
                            one[c.Key] = c.Value ?? "";
                    if (!one.ContainsKey("NAME") || CitectText.Trim(one["NAME"]).Length == 0)
                        one["NAME"] = page;
                    rows = new List<Dictionary<string, string>> { one };
                }
                else
                {
                    foreach (var t in tables)
                        if (
                            string.Equals(t.Key, section, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(t.Key, section + ".dbf", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            rows = t.Value;
                            break;
                        }
                }
                if (rows == null || rows.Count == 0)
                    continue;
                for (int i = 0; i < rows.Count; i++)
                {
                    var src = rows[i];
                    var rec = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["_index"] = (long)i,
                        ["_offset"] = 0L,
                    };
                    string where = section + "." + i.ToString(CultureInfo.InvariantCulture);
                    string anText = Text(src, "AN");
                    if (anText.Length > 0)
                        where = "AN " + anText + " " + section;
                    foreach (var (field, type) in kv.Value)
                    {
                        string text = Raw(src, field);
                        switch (type)
                        {
                            case RdbFile.FText:
                            case RdbFile.FChars:
                                rec[field] = CitectText.Trim(text);
                                break;
                            case RdbFile.FI16:
                            case RdbFile.FI32:
                            case FBool:
                            case FAreas:
                            {
                                string t = CitectText.Trim(text);
                                // Control AREAS = pages.dbf AREA
                                if (type == FAreas && t.Length == 0)
                                    t = CitectText.Trim(Raw(src, "AREA"));
                                rec[field] = t.Length == 0 ? EmptyInt(section, field) : label(t) ?? 0L;
                                break;
                            }
                            case FColour:
                            {
                                string t = CitectText.Trim(text);
                                long v = t.Length == 0 ? 0 : label(t) ?? 0;
                                rec[field] = BitConverter.GetBytes(unchecked((uint)v));
                                break;
                            }
                            case RdbFile.FExpr:
                            {
                                long off = Field(
                                    unit,
                                    tags,
                                    section,
                                    field,
                                    type,
                                    text,
                                    pools,
                                    defaults,
                                    code,
                                    where,
                                    i,
                                    notes,
                                    project
                                );
                                rec[field] = off;
                                break;
                            }
                            case RdbFile.FCode:
                            {
                                long off = Field(
                                    unit,
                                    tags,
                                    section,
                                    field,
                                    type,
                                    text,
                                    pools,
                                    defaults,
                                    code,
                                    where,
                                    i,
                                    notes,
                                    project
                                );
                                rec[field] = CompiledPage.Command(off);
                                break;
                            }
                            case FSlider:
                                rec[field] = Slider(
                                    unit,
                                    tags,
                                    section,
                                    field,
                                    text,
                                    pools,
                                    defaults,
                                    code,
                                    where,
                                    i,
                                    notes,
                                    project,
                                    glbStr
                                );
                                break;
                            case FTagVar:
                            {
                                var none = new byte[9];
                                for (int b = 0; b < none.Length; b++)
                                    none[b] = 0xFF;
                                rec[field] = none;
                                break;
                            }
                        }
                    }
                    cp.AddRow(section, rec);
                }
            }
            cp.Code = code.ToArray();
            cp.Program = pools.ToProgram();
            cp.Control = ControlRecord(cp, label);
            return cp;
        }

        private static string Raw(IReadOnlyDictionary<string, string> row, string field) =>
            row.TryGetValue(field, out string? v) && v != null ? v : "";

        private static string Text(IReadOnlyDictionary<string, string> row, string field) =>
            CitectText.Trim(Raw(row, field));

        /// <summary>One code field into the CODE pool: its offset, 0xFFFFFFFF when it has no code
        /// (empty, or it does not compile: noted).</summary>
        private static long Field(
            CompileUnit unit,
            TagTable tags,
            string section,
            string field,
            int type,
            string text,
            LoosePools pools,
            Dictionary<string, Compiler.Syntax.Expr?> defaults,
            List<byte> code,
            string where,
            int row,
            List<string>? notes,
            string project
        )
        {
            var kind = PageFields.KindOf(section, field, type);
            string src = CitectText.Trim(text);
            if (src.Length == 0)
                src = PageFields.EmptyText(section, field) ?? "";
            if (src.Length == 0)
                return 0xFFFFFFFFL;
            byte[]? bytes = CompileOne(
                unit,
                tags,
                src,
                kind,
                pools,
                defaults,
                where + " " + field,
                notes,
                project,
                section + field + (row + 1).ToString(CultureInfo.InvariantCulture)
            );
            return bytes == null ? 0xFFFFFFFFL : Append(code, bytes);
        }

        private static byte[]? CompileOne(
            CompileUnit unit,
            TagTable tags,
            string text,
            PageFieldKind kind,
            LoosePools pools,
            Dictionary<string, Compiler.Syntax.Expr?> defaults,
            string where,
            List<string>? notes,
            string project,
            string vbaName
        )
        {
            try
            {
                FieldResult res;
                lock (unit)
                    res = unit.CompileField(
                        text,
                        kind == PageFieldKind.Command ? "command" : "expression",
                        project
                    );
                if (res.Errors.Count > 0)
                {
                    var d = res.Errors[0];
                    notes?.Add(where + ": " + d.Name + " " + d.Message + " (" + Short(text) + ")");
                    return null;
                }
                FunctionCode fc;
                lock (unit)
                    fc = FunctionGen.PageField(unit, tags, res.Node, kind, defaults, vbaName);
                // the TAGREF records in code order (as the page's TAGREF fills)
                foreach (var ins in fc.Code.Ins)
                    if ((ins.Op == Op.Load || ins.Op == Op.Store) && ins.Ref is TagKey tk)
                        pools.Tag(tk);
                return fc.Code.Encode(pools);
            }
            catch (CodegenException e)
            {
                notes?.Add(where + ": not compiled: " + e.Message + " (" + Short(text) + ")");
                return null;
            }
        }

        private static string Short(string s) => s.Length > 120 ? s.Substring(0, 120) + "..." : s;

        /// <summary>Append a block (u16 length + code) to the CODE pool; its offset.</summary>
        private static long Append(List<byte> code, byte[] block)
        {
            int at = code.Count;
            code.Add((byte)(block.Length & 0xFF));
            code.Add((byte)((block.Length >> 8) & 0xFF));
            code.AddRange(block);
            return at + 2;
        }

        /// <summary>A slider tag field: the read and write code.</summary>
        private static byte[] Slider(
            CompileUnit unit,
            TagTable tags,
            string section,
            string field,
            string text,
            LoosePools pools,
            Dictionary<string, Compiler.Syntax.Expr?> defaults,
            List<byte> code,
            string where,
            int row,
            List<string>? notes,
            string project,
            int glbStr
        )
        {
            var b = new byte[8];
            for (int k = 0; k < 8; k++)
                b[k] = 0xFF;
            string src = CitectText.Trim(text);
            if (src.Length == 0)
                return b;
            byte[]? read = CompileOne(
                unit,
                tags,
                src,
                PageFieldKind.Range,
                pools,
                defaults,
                where + " " + field,
                notes,
                project,
                section + field + (row + 1).ToString(CultureInfo.InvariantCulture)
            );
            if (read == null)
                return b;
            long roff = Append(code, read);
            BitConverter.GetBytes((uint)roff).CopyTo(b, 0);
            // Load (0x02) + 8 operand bytes + End (0xff): the write stores through the same TAGREF
            if (read.Length == 10 && read[0] == 0x02 && read[9] == 0xFF && glbStr >= 0)
            {
                var w = new List<byte>
                {
                    0x01,
                    0x04,
                    0,
                    0,
                    0,
                    0,
                    0x09,
                    0x04,
                    0x05,
                    (byte)(glbStr & 0xFF),
                    (byte)(glbStr >> 8),
                    0x01,
                    0x03,
                };
                for (int k = 1; k < 9; k++)
                    w.Add(read[k]);
                w.Add(0xFF);
                long woff = Append(code, w.ToArray());
                BitConverter.GetBytes((uint)woff).CopyTo(b, 4);
            }
            else
                notes?.Add(
                    where
                        + " "
                        + field
                        + ": the slider tag is not one tag, so the slider does not write ("
                        + Short(src)
                        + ")"
                );
            return b;
        }

        /// <summary>GetGlbStr's built-in index (Bin\FUNC0.DBF; 22 in 8.50), -1 when unknown.</summary>
        private static int GlbStrIndex(CompileUnit unit)
        {
            var f = Func0Table.Default.Lookup("GetGlbStr");
            return f != null ? f.Index : 0x16;
        }

        /// <summary>The page record (PageRecord) of a compiled Control row.</summary>
        private static PageRecord ControlRecord(CompiledPage cp, Func<string, long?> label)
        {
            var rows = cp.Rows("Control");
            var p = new PageRecord { Name = cp.Name };
            if (rows.Count == 0)
                return p;
            var r = rows[0];
            string S(string k) => CompiledPage.Str(r, k);
            p.Name = S("NAME").Length > 0 ? S("NAME") : cp.Name;
            p.Alias = S("ALIAS");
            p.Title = S("TITLE");
            p.BgndFile = S("BGND_FILE");
            p.SymbFile = S("SYMB_FILE");
            p.NextPage = S("NEXT_PAGE");
            p.PrevPage = S("PREV_PAGE");
            p.LogDevice = S("LOGDEVICE");
            p.Cluster = S("CLUSTER");
            p.ConType = S("CON_TYPE");
            p.ScanTime = CompiledPage.Int(r, "SCANTIME", -1);
            p.Areas = CompiledPage.Int(r, "AREAS", 0);
            p.IgnoreVq = CompiledPage.Int(r, "IGNOREVQ", -1);
            return p;
        }
    }
}
