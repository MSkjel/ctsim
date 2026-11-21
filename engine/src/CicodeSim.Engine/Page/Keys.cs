using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Project;

namespace CicodeSim.Page
{
    /// <summary>Key names to key codes: keys.dbf NAME -&gt; CODE label -&gt; labels.dbf value of
    /// the compile unit (Include KEYS.DBF, LABELS.DBF: ENTER = KEY_ENTER = 0x000D,
    /// LBUTTON_CMD_UP = KEY_LBTN_CMD_UP = 0x0601). Mouse clicks are keys too.</summary>
    public sealed class KeyTable
    {
        private readonly Dictionary<string, int> _byName = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<int, string> _byCode = new Dictionary<int, string>();

        public const int Hash = 0x23,
            Star = 0x2A;

        /// <summary>The mouse button codes (labels.dbf): KEY_LBUTTON_UP 0x0201, _DBL 0x0401,
        /// _DN 0x0801, the command cursor forms KEY_LBTN_CMD_UP 0x0601 / _DN 0x0605; Shift adds
        /// 0x1000, Ctrl 0x2000.</summary>
        public const int LButtonUp = 0x0201,
            LButtonDbl = 0x0401,
            LButtonDown = 0x0801;

        /// <summary>The right and middle mouse button key codes.</summary>
        public const int RButtonUp = 0x0202,
            RButtonDbl = 0x0402,
            RButtonDown = 0x0802;
        public const int MButtonUp = 0x0204,
            MButtonDbl = 0x0404,
            MButtonDown = 0x0804;
        public const int LButtonCmdUp = 0x0601,
            LButtonCmdDown = 0x0605;

        public KeyTable() { }

        /// <summary>The unit's key names (World.Unit).</summary>
        public static KeyTable FromUnit(Unit unit)
        {
            var k = new KeyTable();
            foreach (var (_, r) in unit.Rows("keys.dbf"))
            {
                string name = CitectText.Trim(r.Get("NAME"));
                string code = CitectText.Trim(r.Get("CODE"));
                if (name.Length == 0 || k._byName.ContainsKey(name))
                    continue;
                long? v = unit.LabelValue(code);
                if (v == null)
                    continue;
                k.Add(name, (int)v.Value);
            }
            return k;
        }

        public void Add(string name, int code)
        {
            _byName[name] = code;
            if (!_byCode.ContainsKey(code))
                _byCode[code] = name;
        }

        public bool TryCode(string name, out int code) => _byName.TryGetValue(name, out code);

        public string NameOf(int code) =>
            _byCode.TryGetValue(code, out var n)
                ? n
                : "0x" + code.ToString("X4", CultureInfo.InvariantCulture);

        /// <summary>A key as an input names it: a key name, one character (its code), or a
        /// number (0x.. or decimal); -1 when unknown.</summary>
        public int Parse(string key)
        {
            key = key ?? "";
            if (TryCode(key, out int c))
                return c;
            if (key.Length == 1)
                return key[0];
            if (
                key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    key.Substring(2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out int h
                )
            )
                return h;
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d))
                return d;
            return -1;
        }

        /// <summary>A compiled KEY_SEQ text as key codes: tokens split on blanks, a key name is
        /// its code, anything else one code per character ('#' data, '*' variable / hot key).</summary>
        public List<int> Sequence(string seq)
        {
            var o = new List<int>();
            foreach (
                string tok in (seq ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            )
            {
                if (TryCode(tok, out int c))
                {
                    o.Add(c);
                    continue;
                }
                if (tok.Length > 1 && tok[0] == '*' && TryCode(tok.Substring(1), out int hc))
                {
                    o.Add(Star);
                    o.Add(hc);
                    continue;
                }
                foreach (char ch in tok)
                    o.Add(char.IsLetter(ch) ? char.ToUpperInvariant(ch) : ch);
            }
            return o;
        }
    }

    /// <summary>Match a key sequence against the key command line.</summary>
    public static class KeyMatch
    {
        public enum Result
        {
            None,
            Prefix,
            Complete,
        }

        /// <summary>Normalise a typed key for matching.</summary>
        public static int Normalize(int code)
        {
            if (code >= 0 && code < 0x80)
                return char.ToUpperInvariant((char)code);
            if ((code & 0x7000) != 0 && (code & ~0x7000 & 0xFFFF) < 0x80)
                return (code & 0xFF00) | char.ToUpperInvariant((char)(code & 0xFF));
            return code;
        }

        private static bool Data(int c, int sep) => c != sep && c >= 0x20 && c < 0x100 && c != 0x7F;

        /// <summary>Match <paramref name="buffer"/> against <paramref name="seq"/>; on a complete
        /// match <paramref name="args"/> holds the 8 data arguments.</summary>
        public static Result Match(
            IReadOnlyList<int> seq,
            IReadOnlyList<int> buffer,
            int separator,
            out string[] args
        )
        {
            args = new string[8];
            for (int k = 0; k < 8; k++)
                args[k] = "";
            int i = 0,
                j = 0,
                slot = 0;
            while (i < seq.Count)
            {
                int s = seq[i];
                if (s == KeyTable.Hash)
                {
                    int n = 0;
                    while (i < seq.Count && seq[i] == KeyTable.Hash)
                    {
                        n++;
                        i++;
                    }
                    bool variable = i < seq.Count && seq[i] == KeyTable.Star;
                    if (variable)
                        i++;
                    var sb = new System.Text.StringBuilder();
                    while (j < buffer.Count && Data(buffer[j], separator) && (variable || sb.Length < n))
                    {
                        sb.Append((char)buffer[j]);
                        j++;
                    }
                    if (slot < 8)
                        args[slot] = sb.ToString();
                    slot++;
                    // a separator between two data runs
                    if (
                        j < buffer.Count
                        && buffer[j] == separator
                        && i < seq.Count
                        && seq[i] == KeyTable.Hash
                    )
                        j++;
                    if (j >= buffer.Count && i < seq.Count)
                        return Result.Prefix;
                    continue;
                }
                if (s == KeyTable.Star)
                {
                    i++;
                    continue;
                }
                if (j >= buffer.Count)
                    return Result.Prefix;
                // the typed key normalised as the runtime's matcher does; the
                // sequence side too (a key sequence typed in lower case, a source that was not
                // compiled by CtCmp32)
                int b = buffer[j];
                if (b != s && Normalize(b) != Normalize(s))
                    return Result.None;
                i++;
                j++;
            }
            return j == buffer.Count ? Result.Complete : Result.None;
        }
    }
}
