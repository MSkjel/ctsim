using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>One field of a format template (name, data,
    /// width, justification 0 left / 1 right / 2 none / 3 zero, the template
    /// character after the field, the runtime its offset in the output).</summary>
    internal sealed class FmtField
    {
        public string Name = "";
        public int Width;
        public int Just;
        public char Term;
        public int Offset;
        public string Data = "";
    }

    /// <summary>A format template (FmtOpen).</summary>
    internal sealed class FmtTemplate
    {
        public string Name = "";

        /// <summary>The template as literal text and fields, in order.</summary>
        public readonly List<object> Parts = new List<object>();
        public readonly List<FmtField> Fields = new List<FmtField>();

        /// <summary>A valid format: every '{...}' has a name, at most two commas,
        /// a width of digits and a justification of one of l L r R z Z n N.</summary>
        public static bool Valid(string fmt)
        {
            int i = 0;
            while (i < fmt.Length)
            {
                if (fmt[i] != '{')
                {
                    i++;
                    continue;
                }
                int close = fmt.IndexOf('}', i + 1);
                int nextOpen = fmt.IndexOf('{', i + 1);
                if (close < 0 || (nextOpen >= 0 && nextOpen < close))
                    return false;
                string inner = fmt.Substring(i + 1, close - i - 1);
                string[] parts = inner.Split(',');
                if (parts.Length > 3)
                    return false;
                if (parts[0].Trim(' ').Length == 0)
                    return false;
                if (parts.Length > 1)
                {
                    string w = parts[1].Trim(' ');
                    if (w.Length == 0)
                        return false;
                    foreach (char c in w)
                        if (c < '0' || c > '9')
                            return false;
                }
                if (parts.Length > 2)
                {
                    string j = parts[2].Trim(' ');
                    if (j.Length != 1 || "lLrRzZnN,".IndexOf(j[0]) < 0)
                        return false;
                }
                i = close + 1;
            }
            return true;
        }

        public static FmtTemplate Parse(string name, string fmt)
        {
            var t = new FmtTemplate { Name = name };
            var lit = new StringBuilder();
            int outPos = 0;
            int i = 0;
            while (i < fmt.Length)
            {
                char c = fmt[i];
                if (c != '{' || !FieldAt(fmt, i))
                {
                    lit.Append(c);
                    outPos++;
                    i++;
                    continue;
                }
                if (lit.Length > 0)
                {
                    t.Parts.Add(lit.ToString());
                    lit.Clear();
                }
                // the name to ',' '{' '}' or a space; width = the given one (< 1 = 1),
                // else the length of the '{...}' text; justification L R N Z
                int k = i + 1,
                    count = 1;
                while (k < fmt.Length && char.IsWhiteSpace(fmt[k]))
                {
                    k++;
                    count++;
                }
                var nm = new StringBuilder();
                while (
                    k < fmt.Length
                    && fmt[k] != ','
                    && fmt[k] != '{'
                    && fmt[k] != '}'
                    && !char.IsWhiteSpace(fmt[k])
                )
                {
                    if (nm.Length < 254)
                        nm.Append(fmt[k]);
                    k++;
                    count++;
                }
                while (k < fmt.Length && char.IsWhiteSpace(fmt[k]))
                {
                    k++;
                    count++;
                }
                int width = 0,
                    just = 0;
                if (k < fmt.Length && fmt[k] == ',')
                {
                    k++;
                    while (k < fmt.Length && char.IsWhiteSpace(fmt[k]))
                        k++;
                    int w = 0,
                        s = k;
                    while (k < fmt.Length && fmt[k] >= '0' && fmt[k] <= '9')
                        w = Math.Min(w * 10 + (fmt[k++] - '0'), 0x7fff);
                    width = k > s && w >= 1 ? w : 1;
                    while (k < fmt.Length && char.IsWhiteSpace(fmt[k]))
                        k++;
                    if (k < fmt.Length && fmt[k] == ',')
                    {
                        k++;
                        while (k < fmt.Length && char.IsWhiteSpace(fmt[k]))
                            k++;
                        char j = k < fmt.Length ? fmt[k] : '\0';
                        just =
                            j == 'N' || j == 'n' ? 2
                            : j == 'R' || j == 'r' ? 1
                            : j == 'Z' || j == 'z' ? 3
                            : 0;
                    }
                }
                while (k < fmt.Length)
                {
                    count++;
                    char x = fmt[k++];
                    if (x == '}')
                        break;
                }
                if (width == 0)
                    width = count;
                char term = k < fmt.Length && fmt[k] != '{' ? fmt[k] : '\0';
                var f = new FmtField
                {
                    Name = nm.ToString(),
                    Width = width,
                    Just = just,
                    Term = term,
                    Offset = outPos,
                };
                t.Fields.Add(f);
                t.Parts.Add(f);
                outPos += width;
                i = k;
            }
            if (lit.Length > 0)
                t.Parts.Add(lit.ToString());
            return t;
        }

        /// <summary>a '{' that starts a valid field.</summary>
        private static bool FieldAt(string fmt, int i)
        {
            int close = fmt.IndexOf('}', i + 1);
            int open = fmt.IndexOf('{', i + 1);
            if (close < 0 || (open >= 0 && open < close))
                return false;
            return Valid(fmt.Substring(i, close - i + 1));
        }

        public FmtField? Field(string name)
        {
            foreach (var f in Fields)
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }

        /// <summary>FmtToStr: the template printed with each field's data ('%-W.Ws' left,
        /// '%W.Ws' right, '%0W.Ws' zero, '%s' none), 255 characters at most.</summary>
        public string ToStr()
        {
            var sb = new StringBuilder();
            foreach (object p in Parts)
            {
                if (p is string s)
                {
                    sb.Append(s);
                    continue;
                }
                var f = (FmtField)p;
                string d = f.Data.Length > f.Width && f.Just != 2 ? f.Data.Substring(0, f.Width) : f.Data;
                switch (f.Just)
                {
                    case 1:
                        sb.Append(d.PadLeft(f.Width));
                        break;
                    case 2:
                        sb.Append(d);
                        break;
                    case 3:
                        sb.Append(d.PadLeft(f.Width, '0'));
                        break;
                    default:
                        sb.Append(d.PadRight(f.Width));
                        break;
                }
            }
            return A.Cap(sb.ToString());
        }

        /// <summary>StrToFmt: each field takes Width characters at its offset (less
        /// what earlier 'none' fields gave back); a 'none' field ends at its terminator character;
        /// a field past the end of the string is ''.</summary>
        public void FromStr(string s)
        {
            int shrink = 0;
            foreach (var f in Fields)
            {
                int at = f.Offset - shrink;
                if (at < s.Length && at >= 0)
                {
                    string d = s.Substring(at, Math.Min(f.Width, s.Length - at));
                    if (f.Just == 2 && f.Term != '\0' && s.Length > 0)
                    {
                        int k = d.IndexOf(f.Term);
                        if (k >= 0)
                            d = d.Substring(0, k);
                        shrink += f.Width - d.Length;
                    }
                    f.Data = d;
                }
                else
                    f.Data = "";
            }
        }
    }

    /// <summary>Format template built-ins. Handles from 0, at most 512 templates (258 past that).</summary>
    public static class Formats
    {
        public const int MaxFormats = 512;

        private static FmtTemplate? Fmt(SimHost h, in Value v) => h.Formats.Get(A.Short(A.I(v)));

        private static int Count(SimHost h)
        {
            int n = 0;
            foreach (var _ in h.Formats.Live())
                n++;
            return n;
        }

        [Builtin(
            "FmtOpen",
            Doc = "1106155",
            Effects = "creates a format template of the process",
            Errors = "274 an invalid format (raised; returns 274, as the runtime does); 258 more than 512 templates (raised); 274 bad mode",
            Notes = "Mode 0 opens the named one (-1 when none; 'Cat<n>' with no template of that name = category n's display format), 1 creates, 5 replaces a template of that name; 2 / 3 / 4 open the display / summary / SOE format of the alarm category Name (category.dbf DSPFMT / SUMDSPFMT / SOEDSPFMT; one template per category and mode, the same handle again; -1 without an error when the category or its format is not defined). A field is {name[,width[,justification L R N Z]]}; without a width it is as wide as its '{...}' text."
        )]
        private static Value FmtOpen(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)),
                fmt = A.S(a.Get(1));
            long mode = A.Short(A.I(a.Get(2)));
            if (mode != 2 && mode != 3 && mode != 4 && !FmtTemplate.Valid(fmt))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            switch (mode)
            {
                case 0:
                    foreach (var (hd, f) in h.Formats.Live())
                        if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                            return W.I(hd);
                    // 'Cat<n>': the display format of alarm category n (Tab_Style
                    // TabAlarm_GetFormatString opens "Cat255" for a hardware list)
                    string nm = CitectText.Trim(name);
                    if (
                        nm.Length > 3
                        && nm.StartsWith("Cat", StringComparison.OrdinalIgnoreCase)
                        && CitectText.IsDigits(nm.Substring(3))
                    )
                        return W.I(CategoryFormat(h, nm.Substring(3), 2));
                    return W.I(-1);
                case 2:
                case 3:
                case 4:
                    return W.I(CategoryFormat(h, name, (int)mode));
                case 5:
                    foreach (var (hd, f) in h.Formats.Live())
                        if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            h.Formats.Free(hd);
                            break;
                        }
                    goto case 1;
                case 1:
                    if (Count(h) >= MaxFormats)
                        W.Raise(t, 258);
                    return W.I(
                        h.Formats.Add(FmtTemplate.Parse(Sync.NameCopy(A.SkipSpace(name), false), fmt))
                    );
            }
            W.Raise(t, Codes.InvalidArg);
            return W.I(-1);
        }

        private static readonly ConditionalWeakTable<
            SimHost,
            Dictionary<string, (int handle, FmtTemplate tpl)>
        > CatFormats = new ConditionalWeakTable<SimHost, Dictionary<string, (int, FmtTemplate)>>();

        /// <summary>The template of an alarm category's format.</summary>
        private static int CategoryFormat(SimHost h, string name, int mode)
        {
            if (h.World == null)
                return -1;
            long? cat = h.World.Unit.LabelValue(name);
            if (cat == null || !h.World.Alarms.Categories().ContainsKey(cat.Value))
                return -1;
            int type =
                mode == 3 ? 10
                : mode == 4 ? 15
                : 0;
            string fmt = AlarmLists.OwnCategoryFormat(h, cat.Value, type);
            if (fmt.Length == 0 || !FmtTemplate.Valid(fmt))
                return -1;
            var cache = CatFormats.GetValue(h, _ => new Dictionary<string, (int, FmtTemplate)>(
                StringComparer.Ordinal
            ));
            string key = C.Inv(mode) + ":" + C.Inv(cat.Value);
            if (cache.TryGetValue(key, out var e) && ReferenceEquals(h.Formats.Get(e.handle), e.tpl))
                return e.handle;
            if (Count(h) >= MaxFormats)
                return -1;
            var tpl = FmtTemplate.Parse(C.Inv(cat.Value), fmt);
            int hd = h.Formats.Add(tpl);
            cache[key] = (hd, tpl);
            return hd;
        }

        [Builtin("FmtClose", Doc = "1106148", Errors = "269 bad handle (returned)")]
        private static Value FmtClose(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.Short(A.I(a.Get(0)));
            if (h.Formats.Get(hd) == null)
                return W.I(Codes.BadHandle);
            h.Formats.Free(hd);
            return W.I(0);
        }

        [Builtin(
            "FmtSetField",
            Doc = "1106156",
            Errors = "269 bad handle; 266 unknown field (raised and returned)",
            Notes = "Data is cut to the field's width."
        )]
        private static Value FmtSetField(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Fmt(h, a.Get(0));
            if (f == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(Codes.BadHandle);
            }
            var fd = f.Field(A.S(a.Get(1)));
            if (fd == null)
            {
                W.Raise(t, Codes.FieldNotFound);
                return W.I(Codes.FieldNotFound);
            }
            string d = A.S(a.Get(2));
            fd.Data = d.Length > fd.Width ? d.Substring(0, fd.Width) : d;
            return W.I(0);
        }

        [Builtin(
            "FmtGetField",
            Doc = "1106150",
            Errors = "269 bad handle; 266 unknown field (raised, '' returned)"
        )]
        private static Value FmtGetField(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Fmt(h, a.Get(0));
            if (f == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.Empty;
            }
            var fd = f.Field(A.S(a.Get(1)));
            if (fd == null)
            {
                W.Raise(t, Codes.FieldNotFound);
                return W.Empty;
            }
            return W.S(fd.Data);
        }

        private static FmtField? ByHnd(SimHost h, ArgList a, out FmtTemplate? f)
        {
            f = Fmt(h, a.Get(0));
            long k = A.Short(A.I(a.Get(1)));
            return f != null && k >= 0 && k < f.Fields.Count ? f.Fields[(int)k] : null;
        }

        [Builtin(
            "FmtSetFieldHnd",
            Doc = "1106157",
            Errors = "269 bad handle; 266 bad field handle (raised and returned)"
        )]
        private static Value FmtSetFieldHnd(SimHost h, CicodeTask t, ArgList a)
        {
            var fd = ByHnd(h, a, out var f);
            if (fd == null)
            {
                int e = f == null ? Codes.BadHandle : Codes.FieldNotFound;
                W.Raise(t, e);
                return W.I(e);
            }
            string d = A.S(a.Get(2));
            fd.Data = d.Length > fd.Width ? d.Substring(0, fd.Width) : d;
            return W.I(0);
        }

        [Builtin("FmtGetFieldHnd", Doc = "1106152", Errors = "269 / 266 (raised, '' returned)")]
        private static Value FmtGetFieldHnd(SimHost h, CicodeTask t, ArgList a)
        {
            var fd = ByHnd(h, a, out var f);
            if (fd == null)
            {
                W.Raise(t, f == null ? Codes.BadHandle : Codes.FieldNotFound);
                return W.Empty;
            }
            return W.S(fd.Data);
        }

        [Builtin(
            "FmtFieldHnd",
            Doc = "1106149",
            Errors = "269 bad handle; 266 unknown field (raised, -1)",
            Notes = "The field's index (0-based)."
        )]
        private static Value FmtFieldHnd(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Fmt(h, a.Get(0));
            if (f == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(-1);
            }
            var fd = f.Field(A.S(a.Get(1)));
            if (fd == null)
            {
                W.Raise(t, Codes.FieldNotFound);
                return W.I(-1);
            }
            return W.I(f.Fields.IndexOf(fd));
        }

        [Builtin("FmtToStr", Doc = "1106158", Errors = "269 bad handle (raised, '' returned)")]
        private static Value FmtToStr(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Fmt(h, a.Get(0));
            if (f == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.Empty;
            }
            return W.S(f.ToStr());
        }

        [Builtin(
            "StrToFmt",
            Doc = "1192588",
            Errors = "269 bad handle (raised and returned)",
            Notes = "Splits the string into the fields by their offsets and widths."
        )]
        private static Value StrToFmt(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Fmt(h, a.Get(0));
            if (f == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(Codes.BadHandle);
            }
            f.FromStr(A.S(a.Get(1)));
            return W.I(0);
        }

        [Builtin("FmtGetFieldCount", Doc = "1106151", Errors = "269 bad handle (raised, -1)")]
        private static Value FmtGetFieldCount(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Fmt(h, a.Get(0));
            if (f == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(-1);
            }
            return W.I(f.Fields.Count);
        }

        [Builtin(
            "FmtGetFieldName",
            Doc = "1106153",
            Errors = "269 bad handle or field (raised, '' returned)"
        )]
        private static Value FmtGetFieldName(SimHost h, CicodeTask t, ArgList a)
        {
            var fd = ByHnd(h, a, out _);
            if (fd == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.Empty;
            }
            return W.S(fd.Name);
        }

        [Builtin("FmtGetFieldWidth", Doc = "1106154", Errors = "269 bad handle or field (raised, -1)")]
        private static Value FmtGetFieldWidth(SimHost h, CicodeTask t, ArgList a)
        {
            var fd = ByHnd(h, a, out _);
            if (fd == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(-1);
            }
            return W.I(fd.Width);
        }
    }
}
