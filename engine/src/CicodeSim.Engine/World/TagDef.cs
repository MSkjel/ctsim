using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>What kind of raw value a tag holds: integer types as a long, REAL as a
    /// double, STRING as text.</summary>
    public enum RawKind : byte
    {
        Int,
        Real,
        Str,
    }

    /// <summary>One variable tag or local variable.</summary>
    public sealed class TagDef
    {
        public const string Variable = "variable",
            Local = "local";

        public string Name { get; }
        public string Kind { get; }
        public string Project { get; }
        public string TypeName { get; }
        public int Type { get; }
        public int[] Dims { get; }

        // the definition as configured; the simulator's overrides (SetOverride) go over it
        private readonly double? _rawZero,
            _rawFull,
            _engZero,
            _engFull,
            _deadband;
        private readonly string _engUnits,
            _format,
            _comment,
            _unitText,
            _address;
        private readonly string[] _custom;

        public double? RawZero => Num("RAW_ZERO", _rawZero);
        public double? RawFull => Num("RAW_FULL", _rawFull);
        public double? EngZero => Num("ENG_ZERO", _engZero);
        public double? EngFull => Num("ENG_FULL", _engFull);
        public string EngUnits => Text("ENG_UNITS", _engUnits);
        public string Format => Text("FORMAT", _format);
        public string Comment => Text("COMMENT", _comment);
        public string Cluster { get; }
        public string Equip { get; }
        public string Item { get; }
        public double? Deadband => Num("DEADBAND", _deadband);
        public string UnitText => Text("UNIT", _unitText);
        public string Address => Text("ADDR", _address);
        public string[] Custom
        {
            get
            {
                if (_ov == null)
                    return _custom;
                var c = (string[])_custom.Clone();
                for (int i = 0; i < 8; i++)
                    if (
                        _ov.TryGetValue(
                            "CUSTOM" + (i + 1).ToString(CultureInfo.InvariantCulture),
                            out string? v
                        )
                    )
                        c[i] = v;
                return c;
            }
        }

        // ------------------------------------------------------------------ the simulator's overrides
        /// <summary>The definition fields a simulation may override (variable.dbf columns; a
        /// simulator deviation: ctsim/setTagProperty). The name, type, cluster and
        /// Equip.Item are the tag's identity and storage, not overridable.</summary>
        public static readonly string[] OverridableFields =
        {
            "ENG_ZERO",
            "ENG_FULL",
            "RAW_ZERO",
            "RAW_FULL",
            "ENG_UNITS",
            "FORMAT",
            "COMMENT",
            "DEADBAND",
            "UNIT",
            "ADDR",
            "CUSTOM1",
            "CUSTOM2",
            "CUSTOM3",
            "CUSTOM4",
            "CUSTOM5",
            "CUSTOM6",
            "CUSTOM7",
            "CUSTOM8",
        };

        private static readonly HashSet<string> NumericFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "ENG_ZERO",
            "ENG_FULL",
            "RAW_ZERO",
            "RAW_FULL",
            "DEADBAND",
        };

        private Dictionary<string, string>? _ov;
        private readonly object _ovLock = new object();

        private static readonly IReadOnlyDictionary<string, string> NoOverrides =
            new Dictionary<string, string>();

        /// <summary>The fields overridden now (field -> the text set; '' = blank), a copy.</summary>
        public IReadOnlyDictionary<string, string> Overrides
        {
            get
            {
                lock (_ovLock)
                    return _ov == null
                        ? NoOverrides
                        : new Dictionary<string, string>(_ov, StringComparer.Ordinal);
            }
        }

        public bool HasOverrides => _ov != null;

        /// <summary>A field's configured text (the project's variable.dbf), whatever is overridden.</summary>
        public string Configured(string field)
        {
            switch (field)
            {
                case "ENG_ZERO":
                    return NumText(_engZero);
                case "ENG_FULL":
                    return NumText(_engFull);
                case "RAW_ZERO":
                    return NumText(_rawZero);
                case "RAW_FULL":
                    return NumText(_rawFull);
                case "DEADBAND":
                    return NumText(_deadband);
                case "ENG_UNITS":
                    return _engUnits;
                case "FORMAT":
                    return _format;
                case "COMMENT":
                    return _comment;
                case "UNIT":
                    return _unitText;
                case "ADDR":
                    return _address;
            }
            if (
                field.StartsWith("CUSTOM", StringComparison.Ordinal)
                && field.Length == 7
                && field[6] >= '1'
                && field[6] <= '8'
            )
                return _custom[field[6] - '1'];
            return "";
        }

        private static string NumText(double? v) =>
            v.HasValue ? v.Value.ToString("R", CultureInfo.InvariantCulture) : "";

        /// <summary>Override a definition field.</summary>
        public void SetOverride(string field, string? value)
        {
            string f = (field ?? "").Trim().ToUpperInvariant();
            if (Array.IndexOf(OverridableFields, f) < 0)
                throw new ArgumentException(
                    "'"
                        + field
                        + "' is not a tag property the simulator can override ("
                        + string.Join(", ", OverridableFields)
                        + ")"
                );
            if (
                value != null
                && NumericFields.Contains(f)
                && value.Trim().Length > 0
                && !double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            )
                throw new ArgumentException(f + " must be a number: '" + value + "'");
            lock (_ovLock)
            {
                var ov =
                    _ov == null
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : new Dictionary<string, string>(_ov, StringComparer.Ordinal);
                if (value == null)
                    ov.Remove(f);
                else
                    ov[f] = value.Trim();
                _ov = ov.Count > 0 ? ov : null;
                _haveRanges = false;
            }
        }

        /// <summary>Every field back to its configured value.</summary>
        public void ClearOverrides()
        {
            lock (_ovLock)
            {
                _ov = null;
                _haveRanges = false;
            }
        }

        private string Text(string field, string configured)
        {
            var ov = _ov;
            return ov != null && ov.TryGetValue(field, out string? v) ? v : configured;
        }

        private double? Num(string field, double? configured)
        {
            var ov = _ov;
            if (ov == null || !ov.TryGetValue(field, out string? v))
                return configured;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d
                : (double?)null;
        }

        public int Record { get; }
        public string Historian { get; }

        // the derived ranges, at first use
        private bool _haveRanges;
        private double _rz,
            _rf,
            _ez,
            _ef;
        private bool _scaled;
        private readonly string _key;

        private static readonly string[] NoCustom = { "", "", "", "", "", "", "", "" };

        public TagDef(
            string name,
            string kind,
            string project,
            string typeName = "",
            int[]? dims = null,
            double? rawZero = null,
            double? rawFull = null,
            double? engZero = null,
            double? engFull = null,
            string engUnits = "",
            string format = "",
            string comment = "",
            string cluster = "",
            string equip = "",
            string item = "",
            double? deadband = null,
            string unit = "",
            string address = "",
            string[]? custom = null,
            int record = -1,
            string historian = ""
        )
        {
            Name = name;
            Kind = kind;
            Project = project;
            TypeName = typeName;
            Type = TagTypes.TypeCode(typeName);
            Dims = dims ?? Array.Empty<int>();
            _rawZero = rawZero;
            _rawFull = rawFull;
            _engZero = engZero;
            _engFull = engFull;
            _engUnits = engUnits;
            _format = format;
            _comment = comment;
            Cluster = cluster;
            Equip = equip;
            Item = item;
            _deadband = deadband;
            _unitText = unit;
            _address = address;
            _custom = custom ?? NoCustom;
            Record = record;
            Historian = historian;
            _key = CitectText.Lower(name);
            RawKind =
                Type == Types.String ? RawKind.Str
                : Type == Types.Real ? RawKind.Real
                : RawKind.Int;
        }

        public override string ToString() =>
            $"TagDef({Kind} {Name} {(TypeName.Length > 0 ? TypeName : "INT")})";

        public string Key => _key;

        /// <summary>The raw value kind (long, double or text).</summary>
        public RawKind RawKind { get; }

        public long Size
        {
            get
            {
                long n = 1;
                foreach (int d in Dims)
                    n *= d;
                return n;
            }
        }

        public bool IsArray => Dims.Length > 0;
        public bool IsString => Type == Types.String;
        public bool IsReal => Type == Types.Real;

        /// <summary>A REAL held as 8 bytes (a local variable, doc: 1064776).</summary>
        public bool IsDouble => Type == Types.Real && Kind == Local;

        public (double lo, double hi) TypeRange()
        {
            if (IsDouble)
                return TagTypes.LocalRealRange;
            return TagTypes.TypeRanges.TryGetValue(Type, out var r)
                ? r
                : (double.NegativeInfinity, double.PositiveInfinity);
        }

        private void Ranges()
        {
            if (_haveRanges)
                return;
            var (tlo, thi) = TypeRange();
            double? lo = RawZero,
                hi = RawFull;
            double rlo,
                rhi;
            if (Type == Types.Real || Type == Types.String || Kind == Local)
            {
                rlo = lo ?? (EngZero ?? tlo);
                rhi = hi ?? (EngFull ?? thi);
            }
            else
            {
                rlo = lo ?? tlo;
                rhi = hi ?? thi;
            }
            double ez = EngZero ?? rlo,
                ef = EngFull ?? rhi;
            _rz = rlo;
            _rf = rhi;
            _ez = ez;
            _ef = ef;
            _scaled = Type != Types.Digital && Type != Types.String && !(rlo == ez && rhi == ef);
            _haveRanges = true;
        }

        /// <summary>(raw zero, raw full): each end the configured one, else the type's end.</summary>
        public (double lo, double hi) RawRange()
        {
            Ranges();
            return (_rz, _rf);
        }

        /// <summary>(eng zero, eng full): each end the configured one, else the raw end.</summary>
        public (double lo, double hi) EngRange()
        {
            Ranges();
            return (_ez, _ef);
        }

        public bool Scaled
        {
            get
            {
                Ranges();
                return _scaled;
            }
        }

        /// <summary>The type code a read pushes. A scaled tag reads as REAL.</summary>
        public int ValueType
        {
            get
            {
                if (Type == Types.String)
                    return Types.String;
                if (Type == Types.Real || Scaled)
                    return Types.Real;
                if (Type == Types.ULong)
                    return Types.ULong;
                return Types.Long;
            }
        }

        public double ToEng(double raw)
        {
            Ranges();
            if (_rf == _rz)
                return _ez;
            return _ez + (raw - _rz) * (_ef - _ez) / (_rf - _rz);
        }

        public double ToRaw(double eng)
        {
            Ranges();
            if (_ef == _ez)
                return _rz;
            return _rz + (eng - _ez) * (_rf - _rz) / (_ef - _ez);
        }

        /// <summary>What ScaleCheck compares an engineering value with (doc: 1064875).</summary>
        public (double lo, double hi) CheckRange()
        {
            Ranges();
            return (FloatOrder.Min(_ez, _ef), FloatOrder.Max(_ez, _ef));
        }

        // ------------------------------------------------------------------ TagGetProperty / TagInfo
        public int GeneralType => TagTypes.GeneralTypes.TryGetValue(Type, out int g) ? g : 8;

        public int BitWidth
        {
            get
            {
                if (IsDouble)
                    return 64;
                return TagTypes.BitWidths.TryGetValue(Type, out int b) ? b : 16;
            }
        }

        /// <summary>The format a value is shown with: its own, else '####.#' for a variable tag
        /// (doc: 1064774) and '####.##' for a local variable (doc: 1064776).</summary>
        public string DisplayFormat =>
            !CitectText.IsBlank(Format) ? Format
            : Kind == Local ? "####.##"
            : TagTypes.DefaultFormat;

        private string FullName => Cluster.Length > 0 ? Cluster + "." + Name : Name;

        /// <summary>A TagGetProperty property (doc: 1192738; names are case sensitive), null
        /// for an unknown one.</summary>
        public string? Property(string name)
        {
            var (w, d, _) = TagTypes.FormatInfo(DisplayFormat);
            var (rz, rf) = RawRange();
            var (ez, ef) = EngRange();
            switch (name)
            {
                case "Address":
                    return Address;
                case "ArraySize":
                    return (Dims.Length > 0 ? Size : 1).ToString(CultureInfo.InvariantCulture);
                case "ClusterName":
                    return Cluster;
                case "DataBitWidth":
                    return BitWidth.ToString(CultureInfo.InvariantCulture);
                case "Description":
                    return Comment;
                case "EngUnitsHigh":
                    return TagTypes.NumText(ef);
                case "EngUnitsLow":
                    return TagTypes.NumText(ez);
                case "Equipment":
                    return Equip;
                case "Format":
                    return TagTypes.FormatWord(DisplayFormat).ToString(CultureInfo.InvariantCulture);
                case "FormatDecPlaces":
                    return d.ToString(CultureInfo.InvariantCulture);
                case "FormatWidth":
                    return w.ToString(CultureInfo.InvariantCulture);
                case "FullName":
                    return FullName;
                case "Item":
                    return Item;
                case "RangeHigh":
                    return TagTypes.NumText(rf);
                case "RangeLow":
                    return TagTypes.NumText(rz);
                case "TagName":
                    return Name;
                case "Type":
                    return GeneralType.ToString(CultureInfo.InvariantCulture);
                case "Units":
                    return EngUnits;
            }
            if (
                name.StartsWith("Custom", StringComparison.Ordinal)
                && name.Length == 7
                && name[6] >= '1'
                && name[6] <= '8'
            )
            {
                int i = name[6] - '1';
                return i < Custom.Length ? Custom[i] : "";
            }
            return null;
        }

        /// <summary>TagInfo(Name, Type) (doc: 1192741) for the types the tables answer; null for
        /// a type that needs the I/O device configuration (9, 12, 13, 19, 21, 24) or an unknown
        /// one. A local variable answers '' for 12..15 (doc).</summary>
        public string? Info(int kind)
        {
            if (Kind == Local && kind >= 12 && kind <= 15)
                return "";
            var (w, d, _) = TagTypes.FormatInfo(DisplayFormat);
            var (rz, rf) = RawRange();
            var (ez, ef) = EngRange();
            switch (kind)
            {
                case 0:
                    return Name;
                case 1:
                    return EngUnits;
                case 2:
                    return TagTypes.NumText(rz);
                case 3:
                    return TagTypes.NumText(rf);
                case 4:
                    return TagTypes.NumText(ez);
                case 5:
                    return TagTypes.NumText(ef);
                case 6:
                    return w.ToString(CultureInfo.InvariantCulture);
                case 7:
                    return d.ToString(CultureInfo.InvariantCulture);
                case 8:
                    return TagTypes.FormatWord(DisplayFormat).ToString(CultureInfo.InvariantCulture);
                case 10:
                    return Type.ToString(CultureInfo.InvariantCulture);
                case 11:
                    return BitWidth.ToString(CultureInfo.InvariantCulture);
                case 14:
                    return (Dims.Length > 0 ? Size : 1).ToString(CultureInfo.InvariantCulture);
                case 15:
                    return Record.ToString(CultureInfo.InvariantCulture);
                case 16:
                    return Comment;
                case 17:
                    return Cluster;
                case 18:
                    return FullName;
                case 20:
                    return Address;
                case 22:
                    return Equip;
                case 23:
                    return GeneralType.ToString(CultureInfo.InvariantCulture);
                case 25:
                    return Item;
            }
            if (kind >= 26 && kind <= 33)
            {
                int i = kind - 26;
                return i < Custom.Length ? Custom[i] : "";
            }
            return null;
        }
    }

    /// <summary>The tag type tables and the format picture rules.</summary>
    public static class TagTypes
    {
        public static readonly Dictionary<string, int> TypeCodesByName = new Dictionary<string, int>(
            StringComparer.Ordinal
        )
        {
            ["DIGITAL"] = Types.Digital,
            ["INT"] = Types.Int16,
            ["UINT"] = Types.UInt,
            ["LONG"] = Types.Long,
            ["ULONG"] = Types.ULong,
            ["REAL"] = Types.Real,
            ["STRING"] = Types.String,
            ["BCD"] = Types.Bcd,
            ["LONGBCD"] = Types.LongBcd,
            ["BYTE"] = Types.Byte,
        };

        public static readonly Dictionary<int, (double, double)> TypeRanges = new Dictionary<
            int,
            (double, double)
        >
        {
            [Types.Digital] = (0, 1),
            [Types.Int16] = (-32768, 32767),
            [Types.UInt] = (0, 65535),
            [Types.Long] = (-2147483648.0, 2147483647.0),
            [Types.ULong] = (0, 4294967295.0),
            [Types.Bcd] = (0, 9999),
            [Types.LongBcd] = (0, 99999999),
            [Types.Byte] = (0, 255),
            [Types.Real] = (-3.4e38, 3.4e38),
        };

        /// <summary>A local variable's REAL is 8 bytes (doc: 1064776).</summary>
        public static readonly (double, double) LocalRealRange = (-1.7e308, 1.7e308);
        public const double Float32Max = 3.4028234663852886e38;

        public static readonly Dictionary<int, int> BitWidths = new Dictionary<int, int>
        {
            [Types.Digital] = 1,
            [Types.Int16] = 16,
            [Types.UInt] = 16,
            [Types.Long] = 32,
            [Types.ULong] = 32,
            [Types.Bcd] = 16,
            [Types.LongBcd] = 32,
            [Types.Byte] = 8,
            [Types.Real] = 32,
            [Types.String] = 2048,
        };

        /// <summary>TagGetProperty 'Type' / TagInfo 23 general type (doc: 1192738); BCD: guess.</summary>
        public static readonly Dictionary<int, int> GeneralTypes = new Dictionary<int, int>
        {
            [Types.Digital] = 0,
            [Types.Byte] = 1,
            [Types.Int16] = 2,
            [Types.Bcd] = 2,
            [Types.UInt] = 3,
            [Types.Long] = 4,
            [Types.LongBcd] = 4,
            [Types.Real] = 5,
            [Types.String] = 6,
            [Types.ULong] = 7,
        };

        /// <summary>doc: 1064774 (a tag with no format); locals ####.## 1064776.</summary>
        public const string DefaultFormat = "####.#";

        /// <summary>The blanks of C isspace inside a regular expression character class.</summary>
        internal const string BlankClass = @" \t\n\x0B\f\r";

        /// <summary>The VM type code of a variable.dbf / locvar.dbf TYPE (case-insensitive;
        /// blank or unknown = 16-bit INT, doc: 1064875 / 1064776).</summary>
        public static int TypeCode(string? typeName) =>
            TypeCodesByName.TryGetValue(CitectText.Upper(CitectText.Trim(typeName ?? "")), out int t)
                ? t
                : Types.Int16;

        private static readonly Regex EuRx = new Regex("[eE][uU]", RegexOptions.CultureInvariant);
        private static readonly Regex ShortRx = new Regex(
            $"^[{BlankClass}]*([0-9]+)(?:\\.([0-9]+))?[{BlankClass}]*$",
            RegexOptions.CultureInvariant
        );
        private static readonly Regex NotBody = new Regex(@"[^#0\-.sS]", RegexOptions.CultureInvariant);

        /// <summary>(width, decimals, flags) of a format picture.</summary>
        public static (int width, int dec, int flags) FormatInfo(string? fmt)
        {
            // a pure function of the picture, read on every TagGetProperty / TagInfo (a large project's
            // pages call TagGetProperty(tag, "Equipment") ... in their scans): memoised
            string key = fmt ?? "";
            if (FormatMemo.TryGetValue(key, out var hit))
                return hit;
            var r = FormatInfoCore(key);
            if (FormatMemo.Count < 8192)
                FormatMemo.TryAdd(key, r);
            return r;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            (int, int, int)
        > FormatMemo = new System.Collections.Concurrent.ConcurrentDictionary<string, (int, int, int)>(
            StringComparer.Ordinal
        );

        private static (int width, int dec, int flags) FormatInfoCore(string? fmt)
        {
            string f = CitectText.Trim(fmt ?? "");
            if (f.Length == 0)
                f = DefaultFormat;
            int flags = 0;
            string low = CitectText.Lower(f);
            if (low.Contains("eu"))
            {
                flags |= 1 << 18;
                f = EuRx.Replace(f, "");
            }
            Match m = ShortRx.Match(f);
            if (m.Success)
            {
                long whole = (long)(CitectText.ParseInt(m.Groups[1].Value) ?? 0);
                long dec = m.Groups[2].Success ? (long)(CitectText.ParseInt(m.Groups[2].Value) ?? 0) : 0;
                return ((int)(whole + (dec != 0 ? dec + 1 : 0)), (int)dec, flags);
            }
            if (f.Length > 1 && f[1] == '0')
                flags |= 1 << 16;
            if (f.IndexOf('-') >= 0)
                flags |= 1 << 17;
            if (CitectText.Lower(f).IndexOf('s') >= 0)
                flags |= 1 << 20;
            // '#0-###.##EU' shows six digits before the point (doc: 1064774): the padding '0' and
            // the '-' count as positions
            string body = NotBody.Replace(f, "");
            int width = body.Length;
            int dot = body.IndexOf('.');
            int d = dot >= 0 ? body.Length - dot - 1 : 0;
            return (width, d, flags);
        }

        /// <summary>TagGetProperty 'Format' / TagInfo 8: width | decimals &lt;&lt; 8 | flags.</summary>
        public static int FormatWord(string? fmt)
        {
            var (w, d, flags) = FormatInfo(fmt);
            return (w & 0xFF) | ((d & 0xFF) << 8) | flags;
        }

        /// <summary>A number as TagGetProperty prints it (guess: %g-like, no trailing zeros).</summary>
        public static string NumText(double x)
        {
            if (double.IsInfinity(x))
                return "";
            if (!double.IsNaN(x) && Math.Floor(x) == x && Math.Abs(x) < 1e15)
                return ((long)x).ToString(CultureInfo.InvariantCulture);
            return FloatText.Shortest(x);
        }

        // ------------------------------------------------------------------ dims
        private static readonly Regex ArrayRx = new Regex(
            $"\\[[{BlankClass}]*([0-9]+)[{BlankClass}]*\\][{BlankClass}]*$",
            RegexOptions.CultureInvariant
        );

        /// <summary>The '[n]' at the end of a name ($: the end or before a final newline).</summary>
        internal static Match ArrayMatch(string s) => ArrayRx.Match(s);

        /// <summary>An array tag's size from its address 'V500[5]' (doc: 1064766). A STRING tag's
        /// '[n]' is the registers that hold ONE string (doc: 1064766), not an array (corpus:
        /// '400769[50]').</summary>
        public static int[] DimsFromAddress(string? addr, string typeName = "")
        {
            if (TypeCode(typeName) == Types.String)
                return Array.Empty<int>();
            Match m = ArrayRx.Match(addr ?? "");
            if (m.Success)
            {
                var n = CitectText.ParseInt(m.Groups[1].Value);
                if (n != null && n.Value > 1)
                    return new[] { (int)Math.Min((double)n.Value, int.MaxValue) };
            }
            return Array.Empty<int>();
        }

        public static int[] DimsFromAsize(string? asize)
        {
            var parts = new List<string>();
            foreach (string p in (asize ?? "").Split(','))
            {
                string s = CitectText.Trim(p);
                if (s.Length > 0)
                    parts.Add(s);
            }
            var dims = new List<int>();
            for (int i = 0; i < parts.Count && i < 2; i++)
            {
                var n = CitectText.ParseInt(parts[i]);
                if (n == null)
                    return Array.Empty<int>();
                if (n.Value > 0)
                    dims.Add((int)Math.Min((double)n.Value, int.MaxValue));
            }
            if (dims.Count == 0 || (dims.Count == 1 && dims[0] <= 1))
                return Array.Empty<int>();
            return dims.ToArray();
        }

        /// <summary>A DBF number field (<see cref="FloatText.TryParseNumber"/>; blank or not a
        /// number = null).</summary>
        public static double? Float(string? text)
        {
            string t = CitectText.Trim(text ?? "");
            if (t.Length == 0)
                return null;
            return FloatText.TryParseNumber(t, out double d) ? d : (double?)null;
        }
    }
}
