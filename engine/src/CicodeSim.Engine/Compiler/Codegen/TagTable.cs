using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>What the code generator reads of one variable tag or local variable.</summary>
    public sealed class TagRow
    {
        public string Name = "";
        public string Type = "";
        public string Format = "";
        public string EngUnits = "";
        public string RawZero = "",
            RawFull = "",
            EngZero = "",
            EngFull = "";

        /// <summary>The array size ('[n]' at the end of ADDR, or a local variable's ASIZE); 0 = none.</summary>
        public int ArraySize;
        public bool Local;

        /// <summary>The StringFmt word and suffix of this tag's value as a STRING.</summary>
        public (uint word, string suffix) FormatWord()
        {
            uint w = FormatWords.OfTagFormat(Format, out bool eu);
            return (w, eu ? EngUnits : "");
        }

        /// <summary>
        /// The tag's scale as the library SCALE pool holds it, or null when no scale field is set.
        /// </summary>
        public ScaleKey? Scale()
        {
            if (RawZero.Length == 0 && RawFull.Length == 0 && EngZero.Length == 0 && EngFull.Length == 0)
                return null;
            if (Local)
            {
                var (lz, lf) = Pair(EngZero, EngFull);
                return new ScaleKey(lz, lf, lz, lf);
            }
            var (rz, rf) = Pair(RawZero, RawFull);
            var (ez, ef) = Pair(EngZero, EngFull);
            return new ScaleKey(rz, rf, ez, ef);
        }

        private static (double, double) Pair(string zero, string full)
        {
            if (CitectText.Trim(zero).Length == 0 && CitectText.Trim(full).Length == 0)
                return (0, 32000);
            return (Num(zero, 0), Num(full, 0));
        }

        /// <summary>A scale field as a number (the World's reading, TagTypes.Float: the engine's own
        /// parser, the same on every framework; .NET's double.TryParse refuses an overflow on .NET
        /// Framework and gives Infinity on .NET Core); <paramref name="dflt"/> when empty or not a number.</summary>
        internal static double Num(string s, double dflt)
        {
            string t = CitectText.Trim(s);
            if (t.Length == 0)
                return dflt;
            return FloatText.TryParseNumber(t, out double v) ? v : dflt;
        }
    }

    /// <summary>The variable tags (variable.dbf), equipment items (variable.dbf EQUIP + ITEM) and
    /// local variables (locvar.dbf) of a compile unit's projects, the first definition of a name
    /// winning in unit order (like <see cref="CompileUnit.Tags"/>).</summary>
    public sealed class TagTable
    {
        private readonly Dictionary<string, TagRow> _vars = new Dictionary<string, TagRow>(
            StringComparer.Ordinal
        );
        private readonly Dictionary<string, TagRow> _items = new Dictionary<string, TagRow>(
            StringComparer.Ordinal
        );
        private readonly Dictionary<string, TagRow> _locals = new Dictionary<string, TagRow>(
            StringComparer.Ordinal
        );

        public static readonly TagTable Empty = new TagTable();

        private TagTable() { }

        public static TagTable For(CompileUnit unit)
        {
            var t = new TagTable();
            if (!unit.UseTags)
                return t;
            foreach (var p in unit.Projects)
            {
                if (string.IsNullOrEmpty(p.Folder) || p.IsVirtual)
                    continue;
                string? vpath = Unit0.Find(p.Folder!, "variable.dbf");
                if (vpath != null)
                {
                    var cols = new[]
                    {
                        "NAME",
                        "TYPE",
                        "ADDR",
                        "RAW_ZERO",
                        "RAW_FULL",
                        "ENG_ZERO",
                        "ENG_FULL",
                        "ENG_UNITS",
                        "FORMAT",
                        "EQUIP",
                        "ITEM",
                    };
                    foreach (var r in Unit0.Rows(vpath, cols))
                    {
                        string n = CitectText.Trim(r[0]);
                        if (n.Length == 0)
                            continue;
                        var row = new TagRow
                        {
                            Name = n,
                            Type = CitectText.Trim(r[1]),
                            RawZero = CitectText.Trim(r[3]),
                            RawFull = CitectText.Trim(r[4]),
                            EngZero = CitectText.Trim(r[5]),
                            EngFull = CitectText.Trim(r[6]),
                            EngUnits = CitectText.Trim(r[7]),
                            Format = CitectText.Trim(r[8]),
                            ArraySize = AddrArraySize(r[2]),
                        };
                        string k = Lexer.NameKey(n);
                        if (!t._vars.ContainsKey(k))
                            t._vars[k] = row;
                        string eq = CitectText.Trim(r[9]),
                            it = CitectText.Trim(r[10]);
                        if (eq.Length > 0 && it.Length > 0)
                        {
                            string ik = Lexer.NameKey(eq + "." + it);
                            if (!t._items.ContainsKey(ik))
                                t._items[ik] = row;
                        }
                    }
                }
                string? lpath = Unit0.Find(p.Folder!, "locvar.dbf");
                if (lpath != null)
                {
                    foreach (
                        var r in Unit0.Rows(
                            lpath,
                            new[] { "NAME", "TYPE", "ASIZE", "ZERO", "FULL", "ENG_UNITS", "FORMAT" }
                        )
                    )
                    {
                        string n = CitectText.Trim(r[0]);
                        if (n.Length == 0)
                            continue;
                        string k = Lexer.NameKey(n);
                        if (t._locals.ContainsKey(k))
                            continue;
                        long asz = CitectText.ParseLong(CitectText.Trim(r[2])) ?? 0;
                        t._locals[k] = new TagRow
                        {
                            Name = n,
                            Type = CitectText.Trim(r[1]),
                            ArraySize = asz > 1 ? (int)Math.Min(asz, short.MaxValue) : 0,
                            EngZero = CitectText.Trim(r[3]),
                            EngFull = CitectText.Trim(r[4]),
                            EngUnits = CitectText.Trim(r[5]),
                            Format = CitectText.Trim(r[6]),
                            Local = true,
                        };
                    }
                }
            }
            return t;
        }

        /// <summary>'[n]' at the end of an ADDR field (`R3!A[10]`), 0 when none.</summary>
        internal static int AddrArraySize(string addr)
        {
            string a = CitectText.Trim(addr);
            if (a.Length < 3 || a[a.Length - 1] != ']')
                return 0;
            int k = a.LastIndexOf('[');
            if (k < 0)
                return 0;
            long? n = CitectText.ParseLong(CitectText.Trim(a.Substring(k + 1, a.Length - k - 2)));
            return n != null && n.Value > 0 ? (int)Math.Min(n.Value, short.MaxValue) : 0;
        }

        /// <summary>The row of a variable tag (name key), null when none.</summary>
        public TagRow? Variable(string name) => _vars.TryGetValue(Lexer.NameKey(name), out var r) ? r : null;

        public TagRow? LocalVariable(string name) =>
            _locals.TryGetValue(Lexer.NameKey(name), out var r) ? r : null;

        /// <summary>The variable tag behind an Equip.Item reference.</summary>
        public TagRow? EquipItem(string name) =>
            _items.TryGetValue(Lexer.NameKey(name), out var r) ? r : null;

        /// <summary>The row a (possibly dotted) reference reads: the tag itself, the tag of a
        /// 'Tag.item', or an Equip.Item's tag.</summary>
        public TagRow? Lookup(string name)
        {
            var r = Variable(name) ?? LocalVariable(name) ?? EquipItem(name);
            if (r != null)
                return r;
            int k = name.LastIndexOf('.');
            while (k > 0)
            {
                string head = name.Substring(0, k);
                r = Variable(head) ?? EquipItem(head);
                if (r != null)
                    return r;
                k = head.LastIndexOf('.');
            }
            return null;
        }
    }
}
