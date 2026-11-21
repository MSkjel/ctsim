using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;

// What the display layer does to the strings and fonts it draws (engine/PROTOCOL.md revision
// 10): every display-bound string a viewer gets has its '@(native)' texts replaced by the
// language's translation, else the native text, as the runtime's display client does when it
// draws (its text output looks the native string up in the language database); the
// fonts of the font table as {face, height, pt, bold ...}.
namespace CicodeSim.World.Display
{
    /// <summary>The display client's language.</summary>
    public sealed class DisplayLanguage
    {
        private readonly World? _world;
        private string _name = "";
        private Dictionary<string, string>? _table;
        private bool _loaded;

        public DisplayLanguage(World? world)
        {
            _world = world;
        }

        /// <summary>The current language's name ('' = not set: the [Language]LocalLanguage
        /// parameter, else English).</summary>
        public string Name
        {
            get
            {
                if (_name.Length == 0)
                {
                    string? v = _world?.Param("Language", "LocalLanguage");
                    _name = v != null && CitectText.Trim(v).Length > 0 ? CitectText.Trim(v) : "English";
                }
                return _name;
            }
            set
            {
                string n = CitectText.Trim(value ?? "");
                if (n == _name)
                    return;
                _name = n;
                _table = null;
                _loaded = false;
            }
        }

        private static string Norm(string name) => CitectText.Trim(name).Replace(" (", "(");

        private bool _caseSensitive;

        /// <summary>[Language]CaseSensitive: whether '@(native)' finds its row with case (the
        /// runtime's default is without). Set with the language by Builtins.LanguageInfo,
        /// which reads the parameter as ParameterGet does.</summary>
        public bool CaseSensitive
        {
            get => _caseSensitive;
            set
            {
                if (value == _caseSensitive)
                    return;
                _caseSensitive = value;
                _table = null;
                _loaded = false;
            }
        }

        /// <summary>NATIVE -> LOCAL of the current language, null when it has none.</summary>
        private Dictionary<string, string>? Table()
        {
            if (_loaded)
                return _table;
            _loaded = true;
            if (_world == null)
                return null;
            string want = Norm(Name);
            if (string.Equals(want, "English", StringComparison.OrdinalIgnoreCase))
                return null;
            try
            {
                foreach (var (p, r) in _world.Unit.Rows("lang.dbf"))
                {
                    int k = r.Header.IndexOfLoose("LANGUAGE");
                    if (k < 0 || !string.Equals(Norm(r[k]), want, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string src = CitectText.Trim(r.Get("SRC_FILE"));
                    if (src.Length == 0)
                    {
                        string first = CitectText.Trim(r[k]);
                        int b = first.IndexOf('(');
                        if (b >= 0)
                            first = first.Substring(0, b).Trim();
                        src = first + ".dbf";
                    }
                    string? path = TagScope.FindTable(p.Dir, System.IO.Path.GetFileName(src));
                    if (path == null)
                        continue;
                    // the runtime opens the table with [Language]CaseSensitive (default 0:
                    // no case)
                    var t = new Dictionary<string, string>(
                        CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase
                    );
                    foreach (var row in Dbf.Rows(path))
                    {
                        string nat = CitectText.Trim(row.Get("NATIVE"));
                        string loc = CitectText.Trim(row.Get("LOCAL"));
                        if (nat.Length > 0 && loc.Length > 0 && !t.ContainsKey(nat))
                            t[nat] = loc;
                    }
                    _table = t;
                    break;
                }
            }
            catch (Exception e)
                when (e is System.IO.IOException
                    || e is FormatException
                    || e is InvalidOperationException
                    || e is UnauthorizedAccessException
                )
            {
                _table = null;
            }
            return _table;
        }

        /// <summary>A display string as the window draws it: each '@(native)' becomes the local
        /// text of the current language, else the native text (<see cref="Translate"/>).</summary>
        public string Localise(string? s)
        {
            if (string.IsNullOrEmpty(s) || s!.IndexOf("@(", StringComparison.Ordinal) < 0)
                return s ?? "";
            var table = Table();
            return Translate(
                s,
                table == null
                    ? null
                    : (Func<string, string?>)(n => table.TryGetValue(n.Trim(), out string? l) ? l : null)
            );
        }

        /// <summary>Translate each '@(native)' in ``s``.</summary>
        public static string Translate(string s, Func<string, string?>? lookup)
        {
            if (s.IndexOf("@(", StringComparison.Ordinal) < 0)
                return s;
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '@' && i + 1 < s.Length && s[i + 1] == '(')
                {
                    int j = i + 2;
                    var inner = new StringBuilder();
                    bool closed = false,
                        format = false;
                    while (j < s.Length)
                    {
                        if (
                            s[j] == '^'
                            && j + 1 < s.Length
                            && (s[j + 1] == ')' || s[j + 1] == '(' || s[j + 1] == ',' || s[j + 1] == '@')
                        )
                        {
                            if (!format)
                                inner.Append(s[j + 1]);
                            j += 2;
                            continue;
                        }
                        if (s[j] == ')')
                        {
                            closed = true;
                            j++;
                            break;
                        }
                        if (s[j] == ',')
                            format = true;
                        else if (!format)
                            inner.Append(s[j]);
                        j++;
                    }
                    if (!closed)
                    {
                        sb.Append(s, i, s.Length - i);
                        break;
                    }
                    string native = inner.ToString();
                    sb.Append(lookup?.Invoke(native) ?? native);
                    i = j;
                    continue;
                }
                sb.Append(s[i++]);
            }
            return sb.ToString();
        }
    }

    /// <summary>What the JSON of a page state needs from the display client: the language (the
    /// strings localised) and the font table (fontInfo). Null = raw (the CLI, logs).</summary>
    public sealed class DisplayJson
    {
        public readonly DisplayState Display;

        public DisplayJson(DisplayState display)
        {
            Display = display;
        }

        public string Text(string? s) => Display.Language.Localise(s);

        // the localised cells of an array output by its raw cells (the same raw cells object
        // while the array does not change: a page change sends the whole drawn list, so an
        // unchanged array of up to 500 x 64 cells is localised once, not on every change)
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonValue, Localised> _cells =
            new System.Runtime.CompilerServices.ConditionalWeakTable<JsonValue, Localised>();

        private sealed class Localised
        {
            public string Language = "";
            public JsonValue? Value;
        }

        /// <summary>An array output's cells for a viewer (<paramref name="localise"/> makes them;
        /// kept while the raw cells and the language are the same).</summary>
        public JsonValue Cells(JsonValue raw, Func<JsonValue, DisplayJson, JsonValue> localise)
        {
            var e = _cells.GetValue(raw, _ => new Localised());
            string lang = Display.Language.Name;
            if (e.Value == null || e.Language != lang)
            {
                e.Value = localise(raw, this);
                e.Language = lang;
            }
            return e.Value;
        }

        /// <summary>The fontInfo of a font handle, null when the handle names no font.</summary>
        public JsonValue? Font(long handle) =>
            handle >= 0 && handle <= int.MaxValue ? Display.Font((int)handle)?.ToJson() : null;

        /// <summary>The fontInfo of a fonts.dbf font by name, null when none.</summary>
        public JsonValue? Font(string name)
        {
            int h = Display.FontByName(name);
            return h >= 0 ? Font(h) : null;
        }
    }

    public sealed partial class FontInfo
    {
        /// <summary>The face (TYPE before its first comma: 'Calibri,B' = Calibri bold).</summary>
        public string Face
        {
            get
            {
                string t = Type;
                int k = t.IndexOf(',');
                return (k >= 0 ? t.Substring(0, k) : t).Trim();
            }
        }

        /// <summary>A style letter after the face ('Courier,B,I,U': B bold, I italic, U underline;
        /// S strikeout: guess, the help names B, I and U only).</summary>
        private bool Style(char c)
        {
            string[] parts = Type.Split(',');
            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 1 && char.ToUpperInvariant(p[0]) == c)
                    return true;
            }
            return false;
        }

        public bool Bold => Style('B');
        public bool Italic => Style('I');
        public bool Underline => Style('U');
        public bool Strikeout => Style('S');

        /// <summary>The logical pixels per inch the sizes convert at (the runtime's screen; 96
        /// assumed).</summary>
        public const int Dpi = 96;

        /// <summary>The character height in pixels the runtime makes the font with: SIZE is
        /// pixels when positive, points when negative (help 1095439, DspFont 1104667: '-12' =
        /// 12 points = MulDiv(12, 96, 72) = 16 pixels); 0 = the default (guess: 12 pixels).</summary>
        public int Height
        {
            get
            {
                if (Size > 0)
                    return Size;
                if (Size < 0)
                    return (int)Math.Round(-Size * (double)Dpi / 72.0, MidpointRounding.AwayFromZero);
                return 12;
            }
        }

        /// <summary>The size in points (fonts.dbf's '-12' = 12; a pixel size converted at 96 dpi).</summary>
        public double Points => Size < 0 ? -Size : Height * 72.0 / Dpi;

        private static JsonValue Colour(uint c) =>
            (c & 0xFF000000u) != 0 ? JsonValue.Of(-1) : JsonValue.Of((long)(c & 0xFFFFFF));

        /// <summary>{handle, name, face, height, pt, bold, italic, underline, strikeout, fg: [on,
        /// off], bg: [on, off], flash} (colours 0xRRGGBB, -1 = transparent).</summary>
        public JsonValue ToJson()
        {
            var o = JsonValue.NewObject();
            o.Set("handle", JsonValue.Of(Handle));
            if (Name.Length > 0)
                o.Set("name", Name);
            o.Set("face", Face);
            o.Set("height", JsonValue.Of(Height));
            o.Set("pt", JsonValue.Of(Points));
            o.Set("bold", JsonValue.Of(Bold));
            o.Set("italic", JsonValue.Of(Italic));
            o.Set("underline", JsonValue.Of(Underline));
            o.Set("strikeout", JsonValue.Of(Strikeout));
            o.Set("fg", JsonValue.Array(new[] { Colour(FgOn), Colour(FgOff) }));
            o.Set("bg", JsonValue.Array(new[] { Colour(BgOn), Colour(BgOff) }));
            o.Set("flash", JsonValue.Of(FgOn != FgOff || BgOn != BgOff));
            return o;
        }
    }
}
