using System;
using System.Collections.Generic;
using System.Text;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Syntax
{
    /// <summary>The Cicode lexer, as CtCmp32 tokenises a .ci file or an expression field.</summary>
    public sealed class Lexer
    {
        // --- character classes (cp1252: Windows IsCharAlphaA / IsCharAlphaNumericA) -----------
        // Read from user32 on a 1252 machine: the letters of 0x80..0xFF are 83 8A 8C 8E 9A 9C 9E
        // 9F AA B5 BA C0..D6 D8..F6 F8..FF; the extra alphanumerics are 0..9 and B2 B3 B9
        // (superscripts).
        public const int NameMax = 254;
        public const int StringMax = 255;

        private static readonly bool[] StartTable = new bool[0x10000];
        private static readonly bool[] CharTable = new bool[0x10000];
        private static readonly bool[] AssocTable = new bool[0x10000];

        static Lexer()
        {
            for (char c = 'A'; c <= 'Z'; c++)
                StartTable[c] = true;
            for (char c = 'a'; c <= 'z'; c++)
                StartTable[c] = true;
            StartTable['_'] = true;
            var high = new List<int> { 0x83, 0x8A, 0x8C, 0x8E, 0x9A, 0x9C, 0x9E, 0x9F, 0xAA, 0xB5, 0xBA };
            for (int b = 0xC0; b <= 0xD6; b++)
                high.Add(b);
            for (int b = 0xD8; b <= 0xF6; b++)
                high.Add(b);
            for (int b = 0xF8; b <= 0xFF; b++)
                high.Add(b);
            foreach (int b in high)
                StartTable[TextCodec.Decode1252[b]] = true;
            for (int i = 0; i < 0x10000; i++)
                CharTable[i] = StartTable[i];
            for (char c = '0'; c <= '9'; c++)
                CharTable[c] = true;
            foreach (int b in new[] { 0xB2, 0xB3, 0xB9 })
                CharTable[TextCodec.Decode1252[b]] = true;
            CharTable['\\'] = true;
            for (int i = 0; i < 0x10000; i++)
                AssocTable[i] = CharTable[i];
            AssocTable[' '] = true;
            AssocTable['.'] = true;
        }

        public static bool IsNameStart(char c) => StartTable[c];

        public static bool IsNameChar(char c) => CharTable[c];

        internal static bool IsWs(char c) =>
            c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f';

        internal static bool IsDigit(char c) => c >= '0' && c <= '9';

        internal static bool IsHex(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        /// <summary>Lookup key of a name: ASCII letters folded only (as the compiler compares names).</summary>
        public static string NameKey(string name)
        {
            int i = 0,
                n = name.Length;
            while (i < n && !(name[i] >= 'A' && name[i] <= 'Z'))
                i++;
            if (i == n)
                return name;
            var a = name.ToCharArray();
            for (; i < n; i++)
            {
                char c = a[i];
                if (c >= 'A' && c <= 'Z')
                    a[i] = (char)(c + 32);
            }
            return new string(a);
        }

        /// <summary>Decode a source file the way the compiler reads it: ANSI code page bytes, the
        /// input ends at the first NUL or Ctrl-Z.</summary>
        public static string SourceText(byte[] data)
        {
            int n = data.Length;
            int k = Array.IndexOf(data, (byte)0);
            if (k >= 0)
                n = k;
            k = Array.IndexOf(data, (byte)0x1A, 0, n);
            if (k >= 0)
                n = k;
            return TextCodec.Decode(data, 0, n);
        }

        /// <summary>The input as the lexer sees it: everything from a NUL or Ctrl-Z on is ignored.</summary>
        public static string CutInput(string text)
        {
            int k = text.IndexOf('\0');
            if (k >= 0)
                text = text.Substring(0, k);
            k = text.IndexOf('\x1a');
            if (k >= 0)
                text = text.Substring(0, k);
            return text;
        }

        private static string Slice(string s, int a, int b)
        {
            if (a < 0)
                a = 0;
            if (b > s.Length)
                b = s.Length;
            return b > a ? s.Substring(a, b - a) : "";
        }

        /// <summary>The end of a format picture token starting at ``i``.</summary>
        public static int PictureEnd(string text, int i, int n)
        {
            int j = i;
            while (j < n && (text[j] == '#' || text[j] == '0' || text[j] == '-'))
                j++;
            if (j < n && text[j] == '!')
                return j + 1;
            if (j < n && (text[j] == '.' || text[j] == 's'))
            {
                j++;
                while (j < n && text[j] == '#')
                    j++;
            }
            int k = j;
            while (k < n && IsWs(text[k]))
                k++;
            string w = CitectText.Lower(Slice(text, k, k + 7));
            if (w.StartsWith("eu", StringComparison.Ordinal))
                return k + 2;
            if (w == "engunit")
                return k + 7;
            return j;
        }

        /// <summary>The end of a super genie association starting at ``i``, or 0.</summary>
        public static int PageAssocEnd(string text, int i, int n)
        {
            int k = text.IndexOf('?', i + 1);
            if (k < 0)
                return 0;
            for (int q = i + 1; q < k; q++)
                if (!AssocTable[text[q]])
                    return 0;
            int j = k + 1;
            while (j < n && CharTable[text[j]])
                j++; // the run goes on: '?SENSOR?_LL_AL'
            return DottedEnd(text, j, n, true);
        }

        /// <summary><paramref name="j"/> moved over '.Name' parts written without spaces
        /// (<paramref name="digitParts"/>: a page field's, see PartEnd).</summary>
        public static int DottedEnd(string text, int j, int n, bool digitParts = false)
        {
            int k;
            while ((k = PartEnd(text, j, n, digitParts)) > 0)
                j = k;
            return j;
        }

        /// <summary>The end of a '.Part' of a dotted name, or -1.</summary>
        private static int PartEnd(string text, int j, int n, bool digitParts)
        {
            if (j + 1 >= n || text[j] != '.')
                return -1;
            char c = text[j + 1];
            if (StartTable[c])
                return NameEnd(text, j + 1, n);
            if (!digitParts || !IsDigit(c))
                return -1;
            int k = j + 1;
            while (k < n && IsDigit(text[k]))
                k++;
            return k < n && StartTable[text[k]] ? NameEnd(text, k, n) : -1;
        }

        /// <summary>
        /// With TagStartDigit=1, the end of a name that starts with a digit, or -1 for a number.
        /// </summary>
        public static int DigitLedNameEnd(string text, int i, int n)
        {
            int k = i;
            while (k < n && IsDigit(text[k]))
                k++;
            if (k == i || k >= n || !StartTable[text[k]])
                return -1;
            char c = text[k];
            bool zeros = true;
            for (int z = i; z < k; z++)
                if (text[z] != '0')
                {
                    zeros = false;
                    break;
                }
            if (zeros && (c == 'x' || c == 'b' || c == 'o'))
                return -1;
            return NameEnd(text, k, n);
        }

        private static int NameEnd(string text, int i, int n)
        {
            int j = i + 1;
            while (j < n && CharTable[text[j]])
                j++;
            return j;
        }

        /// <summary>Scan the number starting at ``i``.</summary>
        public static void ScanNumber(
            string text,
            int i,
            int n,
            out int kind,
            out long ivalue,
            out double rvalue,
            out int end,
            out bool tooBig
        )
        {
            ivalue = 0;
            rvalue = 0;
            tooBig = false;
            int p = i;
            while (p < n && text[p] == '0')
                p++;
            int first = p;
            while (p < n && IsDigit(text[p]))
                p++;
            char c = p < n ? text[p] : '\0';
            if (p < n && (c == '.' || c == 'e' || c == 'E'))
            {
                if (c == '.')
                {
                    p++;
                    while (p < n && IsDigit(text[p]))
                        p++;
                }
                if (p < n && (text[p] == 'e' || text[p] == 'E'))
                {
                    p++;
                    if (p < n && (text[p] == '+' || text[p] == '-'))
                        p++;
                    while (p < n && IsDigit(text[p]))
                        p++;
                }
                // the longest prefix matching \d*\.?\d*(?:[eE][+-]?\d+)? inside [i, p)
                int q = i;
                while (q < p && IsDigit(text[q]))
                    q++;
                if (q < p && text[q] == '.')
                    q++;
                while (q < p && IsDigit(text[q]))
                    q++;
                if (q < p && (text[q] == 'e' || text[q] == 'E'))
                {
                    int r = q + 1;
                    if (r < p && (text[r] == '+' || text[r] == '-'))
                        r++;
                    int d0 = r;
                    while (r < p && IsDigit(text[r]))
                        r++;
                    if (r > d0)
                        q = r;
                }
                string s = text.Substring(i, q - i);
                double v = 0.0;
                if (s.Length > 0 && s != ".")
                    v = FloatText.ParseDecimal(s, 0, s.Length);
                rvalue = v;
                kind = K.Real;
                end = p;
                return;
            }
            int radix = 0;
            if (p < n)
            {
                if (c == 'b' || c == 'B')
                    radix = 2;
                else if (c == 'o' || c == 'O')
                    radix = 8;
                else if (c == 'x' || c == 'X')
                    radix = 16;
            }
            if (radix != 0)
            {
                int q = first + 1;
                if (p == first)
                {
                    // '0x1F': the radix letter right after the zeros. A radix number stops before
                    // the digit that would pass 32 bits: E2058 there ('0x100000000' =
                    // '{0x10000000}0', lexical n21)
                    long v = 0;
                    while (q < n && RadixDigit(text[q], radix) >= 0)
                    {
                        long nv = v * radix + RadixDigit(text[q], radix);
                        if (nv > 0xFFFFFFFFL)
                        {
                            kind = K.Int;
                            end = q;
                            tooBig = true;
                            return;
                        }
                        v = nv;
                        q++;
                    }
                    kind = K.Int;
                    ivalue = v;
                    end = q;
                    return;
                }
                while (q < n && RadixDigit(text[q], radix) >= 0)
                    q++;
                // '12b' rescanned: the decimal digits of text[first:q]; never E2058 here
                // ('4294967296x' and '99999999999o' compile without one, r2_grammar dump big.ci;
                // the value of such a token is not pinned by any probe)
                ulong dv = 0;
                bool over = false;
                for (int t = first; t < q; t++)
                {
                    char ch = text[t];
                    if (!IsDigit(ch))
                        continue;
                    if (!over)
                    {
                        dv = dv * 10 + (ulong)(ch - '0');
                        if (dv > 0xFFFFFFFFUL)
                            over = true;
                    }
                }
                kind = K.Int;
                ivalue = over ? 0 : (long)dv;
                end = q;
                return;
            }
            // plain decimal
            {
                ulong v = 0;
                bool over = false;
                for (int t = first; t < p; t++)
                {
                    v = v * 10 + (ulong)(text[t] - '0');
                    if (v > 0xFFFFFFFFUL)
                    {
                        over = true;
                        break;
                    }
                }
                if (over)
                {
                    // a decimal number stops at the digit that passed 32 bits ('99999999999' =
                    // '{9999999999}9', fix_grammar g_num_dec_11digits)
                    ulong w = 0;
                    int q = first;
                    while (q < p)
                    {
                        w = w * 10 + (ulong)(text[q] - '0');
                        q++;
                        if (w > 0xFFFFFFFFUL)
                        {
                            kind = K.Int;
                            end = q;
                            tooBig = true;
                            return;
                        }
                    }
                }
                kind = K.Int;
                ivalue = (long)v;
                end = p;
            }
        }

        private static int RadixDigit(char c, int radix)
        {
            int d;
            if (c >= '0' && c <= '9')
                d = c - '0';
            else if (c >= 'a' && c <= 'f')
                d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F')
                d = c - 'A' + 10;
            else
                return -1;
            return d < radix ? d : -1;
        }

        /// <summary>The offset where the limit + 1-th character of the string literal at
        /// <paramref name="i"/> starts (an escape counts as the characters it stands for,
        /// '^(' keeps its caret, so it counts two).</summary>
        public static int StringCut(string text, int i, int n, int limit)
        {
            int j = i + 1;
            int count = 0;
            while (j < n && count < limit)
            {
                if (text[j] == '^' && j + 1 < n)
                {
                    if (
                        text[j + 1] == '0'
                        && j + 4 < n
                        && (text[j + 2] == 'x' || text[j + 2] == 'X')
                        && IsHex(text[j + 3])
                        && IsHex(text[j + 4])
                    )
                        j += 5;
                    else
                    {
                        char e = text[j + 1];
                        if (e == '(' || e == ')' || e == ',' || e == '@')
                            count++;
                        j += 2;
                    }
                }
                else
                    j++;
                count++;
            }
            return j;
        }

        private static int HexVal(char c) => c <= '9' ? c - '0' : (c >= 'a' ? c - 'a' + 10 : c - 'A' + 10);

        /// <summary>Scan the string literal starting at ``i``. '^' escapes the next character.</summary>
        public static string ScanString(string text, int i, int n, out int end, out bool closed)
        {
            var sb = new StringBuilder();
            int j = i + 1;
            // the next '"' at or after j: found again only once an escape has passed it (a search
            // from every escape is quadratic)
            int q = text.IndexOf('"', j);
            while (true)
            {
                if (q >= 0 && q < j)
                    q = text.IndexOf('"', j);
                // only a caret before the closing quote matters: search up to it (not to the end of
                // the file for every literal)
                int c = q >= 0 ? text.IndexOf('^', j, q - j) : text.IndexOf('^', j);
                if (c >= 0 && c < (q >= 0 ? q : n + 1))
                {
                    sb.Append(text, j, c - j);
                    if (c + 1 >= n)
                    {
                        end = n;
                        closed = false;
                        return sb.ToString();
                    }
                    char e = text[c + 1];
                    switch (e)
                    {
                        case 'n':
                        case 'N':
                            sb.Append('\n');
                            j = c + 2;
                            continue;
                        case 'r':
                        case 'R':
                            sb.Append('\r');
                            j = c + 2;
                            continue;
                        case 't':
                        case 'T':
                            sb.Append('\t');
                            j = c + 2;
                            continue;
                        case 'f':
                        case 'F':
                            sb.Append('\f');
                            j = c + 2;
                            continue;
                        case 'v':
                        case 'V':
                            sb.Append('\x11');
                            j = c + 2;
                            continue;
                        case '"':
                            sb.Append('"');
                            j = c + 2;
                            continue;
                        case '^':
                            sb.Append('^');
                            j = c + 2;
                            continue;
                    }
                    if (e == '0')
                    {
                        if (
                            c + 4 < n
                            && (text[c + 2] == 'x' || text[c + 2] == 'X')
                            && IsHex(text[c + 3])
                            && IsHex(text[c + 4])
                        )
                        {
                            sb.Append(TextCodec.Decode1252[HexVal(text[c + 3]) * 16 + HexVal(text[c + 4])]);
                            j = c + 5;
                        }
                        else
                        {
                            sb.Append('\0');
                            j = c + 2;
                        }
                    }
                    else if (e == '(' || e == ')' || e == ',' || e == '@')
                    {
                        sb.Append('^').Append(e);
                        j = c + 2;
                    }
                    else
                    {
                        sb.Append(e);
                        j = c + 2;
                    }
                    continue;
                }
                if (q < 0)
                {
                    if (n > j)
                        sb.Append(text, j, n - j);
                    end = n;
                    closed = false;
                    return sb.ToString();
                }
                sb.Append(text, j, q - j);
                end = q + 1;
                closed = true;
                return sb.ToString();
            }
        }

        // ---------------------------------------------------------------------------- instance
        public readonly string Text;
        private readonly LabelTable? _labels;
        public readonly string File;
        public readonly string Mode;
        public readonly string Project;
        private readonly List<Diagnostic> _diags = new List<Diagnostic>();
        public readonly LineIndex Lines;
        private string _intrLabel = "";
        private List<string> _intrArgs = new List<string>();
        private int _sgEnd;
        private readonly bool _page;

        /// <summary>[General]TagStartDigit=1: a name may start with digits (DigitLedNameEnd).</summary>
        private readonly bool _tagStartDigit;

        /// <summary>Tokenise one source text. <paramref name="mode"/> 'ci' for a .ci file, 'page' for
        /// an expression or command field ('%name%' genie tokens are kept there).
        /// <paramref name="tagStartDigit"/> = the project's [General]TagStartDigit.</summary>
        public Lexer(
            string text,
            LabelTable? labels = null,
            string file = "",
            string mode = "ci",
            string project = "",
            bool tagStartDigit = false
        )
        {
            _tagStartDigit = tagStartDigit;
            Text = CutInput(text);
            _labels = labels;
            File = file ?? "";
            Mode = mode;
            _page = mode == "page";
            Project = project ?? "";
            Lines = new LineIndex(Text);
        }

        private void Err(int code, int pos, string context = "")
        {
            var (line, col) = Lines.LineCol(pos);
            _diags.Add(new Diagnostic(code, File, line, col, context, Project, pos));
        }

        public LexResult Run()
        {
            var output = new List<Token>(Math.Max(16, Text.Length / 4));
            Scan(Text, 0, Text.Length, output, false, 0, 0, null, Array.Empty<string>());
            int end = Text.Length;
            output.Add(new Token(K.Eof, "", end, end, false, null));
            return new LexResult(output, _diags, Lines, Text, File);
        }

        private static Token Tok(
            int kind,
            string? svalue,
            string text,
            int pos,
            int end,
            bool glued,
            string? label
        )
        {
            return new Token(kind, text, pos, end, glued, label) { SValue = svalue };
        }

        private static bool Contains(string[] active, string word)
        {
            foreach (string a in active)
                if (AsciiFold.Instance.Equals(a, word))
                    return true;
            return false;
        }

        /// <summary>Append the tokens of text[i:n]. <paramref name="expanding"/> = false for the
        /// top-level source, else (usePos, useEnd) of the label use whose expansion this is (every
        /// token gets those offsets, so its line is the line of the use).</summary>
        private void Scan(
            string text,
            int i,
            int n,
            List<Token> output,
            bool expanding,
            int usePos,
            int useEnd,
            string? label,
            string[] active
        )
        {
            var labels = _labels;
            int prevEnd = i;
            while (i < n)
            {
                char ch = text[i];
                // whitespace
                if (IsWs(ch))
                {
                    i++;
                    while (i < n && IsWs(text[i]))
                        i++;
                    continue;
                }
                bool glued = i == prevEnd;
                int pos = expanding ? usePos : i;
                // comments
                if (ch == '!')
                {
                    int k = text.IndexOf('\n', i);
                    i = k < 0 ? n : k;
                    prevEnd = -1;
                    continue;
                }
                if (ch == '/' && i + 1 < n)
                {
                    char nx = text[i + 1];
                    if (nx == '/')
                    {
                        int k = text.IndexOf('\n', i);
                        i = k < 0 ? n : k;
                        prevEnd = -1;
                        continue;
                    }
                    if (nx == '*')
                    {
                        int k = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        if (k < 0)
                        {
                            // reported at the end of the input (builtins_editor f13: the last line)
                            Err(2056, expanding ? pos : n, "/*");
                            i = n;
                        }
                        else
                            i = k + 2;
                        prevEnd = -1;
                        continue;
                    }
                }
                // super genie run: name characters and exactly two '?' (tested first)
                if (ch == '?')
                {
                    if (_page)
                    {
                        int j = PageAssocEnd(text, i, n);
                        if (j != 0)
                        {
                            int k = text.LastIndexOf('?', j - 1, j - i);
                            output.Add(
                                Tok(
                                    K.Sg,
                                    text.Substring(i + 1, k - i - 1),
                                    text.Substring(i, j - i),
                                    pos,
                                    expanding ? useEnd : j,
                                    glued,
                                    label
                                )
                            );
                            i = prevEnd = j;
                            continue;
                        }
                    }
                    if (SuperGenie(text, i, output, pos, expanding ? useEnd : -1, glued, label))
                    {
                        i = prevEnd = _sgEnd;
                        continue;
                    }
                }
                // names (labels, keywords, Tag.Field)
                if (StartTable[ch])
                {
                    int j = NameEnd(text, i, n);
                    int mEnd = j;
                    if (
                        j < n
                        && text[j] == '?'
                        && SuperGenie(text, i, output, pos, expanding ? useEnd : -1, glued, label)
                    )
                    {
                        i = prevEnd = _sgEnd;
                        if (_page)
                        {
                            // 'Pump?n?.Field' in a page field: the fields go with it
                            int k = DottedEnd(text, i, n, true);
                            if (k > i)
                            {
                                Token t = output[output.Count - 1];
                                t.Text += text.Substring(i, k - i);
                                if (!expanding)
                                    t.End = k;
                                i = prevEnd = k;
                            }
                        }
                        continue;
                    }
                    string raw = text.Substring(i, j - i);
                    string word = raw.Length > NameMax ? raw.Substring(0, NameMax) : raw;
                    // labels and keywords are looked up by the name key (ASCII letters folded),
                    // compared without building it
                    if (labels != null && !Contains(active, word))
                    {
                        Label? lab = labels.GetFolded(word);
                        if (lab != null)
                        {
                            i = prevEnd = Expand(
                                lab,
                                text,
                                j,
                                n,
                                output,
                                pos,
                                expanding ? useEnd : -1,
                                expanding,
                                label ?? lab.Name,
                                active,
                                glued
                            );
                            continue;
                        }
                    }
                    int kw = K.KeywordFolded(word);
                    if (kw >= 0)
                    {
                        output.Add(
                            Tok(
                                kw,
                                K.Keywords[kw - K.KeywordBase],
                                raw,
                                pos,
                                expanding ? useEnd : j,
                                glued,
                                label
                            )
                        );
                        i = prevEnd = j;
                        continue;
                    }
                    // Tag.Field.Item glued without spaces
                    for (int pe; (pe = PartEnd(text, j, n, _page)) > 0; )
                        j = pe;
                    string tokText = raw;
                    if (j != mEnd)
                        word = tokText = text.Substring(i, j - i);
                    output.Add(Tok(K.Name, word, tokText, pos, expanding ? useEnd : j, glued, label));
                    i = prevEnd = j;
                    continue;
                }
                // a name that starts with digits ([General]TagStartDigit=1)
                if (_tagStartDigit && IsDigit(ch))
                {
                    int ne = DigitLedNameEnd(text, i, n);
                    if (ne > 0)
                    {
                        int j = ne;
                        for (int pe; (pe = PartEnd(text, j, n, _page)) > 0; )
                            j = pe;
                        string word = text.Substring(i, j - i);
                        if (word.Length > NameMax)
                            word = word.Substring(0, NameMax);
                        output.Add(
                            Tok(
                                K.Name,
                                word,
                                text.Substring(i, j - i),
                                pos,
                                expanding ? useEnd : j,
                                glued,
                                label
                            )
                        );
                        i = prevEnd = j;
                        continue;
                    }
                }
                // numbers
                if (IsDigit(ch))
                {
                    ScanNumber(
                        text,
                        i,
                        n,
                        out int kind,
                        out long iv,
                        out double rv,
                        out int j,
                        out bool tooBig
                    );
                    string t = text.Substring(i, j - i);
                    if (tooBig)
                    {
                        // E2058; the number becomes an error token the parser reports (E2041)
                        Err(2058, pos, t);
                        kind = K.Bad;
                    }
                    var tok = new Token(kind, t, pos, expanding ? useEnd : j, glued, label)
                    {
                        IValue = iv,
                        RValue = rv,
                    };
                    output.Add(tok);
                    i = prevEnd = j;
                    continue;
                }
                // strings
                if (ch == '"')
                {
                    string value = ScanString(text, i, n, out int j, out bool closed);
                    int kind = K.Str;
                    if (!closed)
                    {
                        // E2054 and the parser's E2041 are reported where the scan stopped, the end
                        // of the input (lexical s10 / dapaudit p04: both on the last line)
                        if (!expanding)
                            pos = n;
                        Err(2054, pos, Slice(text, i, i + 20));
                        kind = K.Bad;
                    }
                    else if (value.Length > StringMax)
                    {
                        // E2055: the literal ends after 255 characters, what follows is lexed again
                        // (lexical s10: E2055 + E2041, then the closing quote opens a string that
                        // runs to the end, E2054 + E2041 + E2034)
                        Err(2055, pos, Slice(text, i, i + 20));
                        j = StringCut(text, i, n, StringMax);
                        value = value.Substring(0, StringMax);
                        kind = K.Bad;
                    }
                    output.Add(
                        Tok(kind, value, Slice(text, i, j), pos, expanding ? useEnd : j, glued, label)
                    );
                    i = prevEnd = j;
                    if (!closed)
                        return;
                    continue;
                }
                // operators
                if (ch == '<' || ch == '>' || ch == '=')
                {
                    if (i + 1 < n)
                    {
                        char c2 = text[i + 1];
                        int two =
                            ch == '<' && c2 == '=' ? K.Le
                            : ch == '<' && c2 == '>' ? K.Ne
                            : ch == '>' && c2 == '=' ? K.Ge
                            : 0;
                        if (two != 0)
                        {
                            string ts = text.Substring(i, 2);
                            output.Add(Tok(two, ts, ts, pos, expanding ? useEnd : i + 2, glued, label));
                            i = prevEnd = i + 2;
                            continue;
                        }
                    }
                    string one = ch.ToString();
                    output.Add(Tok(ch, one, one, pos, expanding ? useEnd : i + 1, glued, label));
                    i = prevEnd = i + 1;
                    continue;
                }
                if (ch == '#')
                {
                    int j = PictureEnd(text, i, n);
                    string pic = text.Substring(i, j - i);
                    output.Add(Tok(K.Pic, pic, pic, pos, expanding ? useEnd : j, glued, label));
                    i = prevEnd = j;
                    continue;
                }
                if (ch == '|')
                {
                    int k = text.IndexOf('|', i + 1);
                    if (k < 0 || k >= n)
                    {
                        // at the end of the input, like an open string (lexical x07)
                        Err(2054, expanding ? pos : n, "|");
                        output.Add(
                            Tok(
                                K.Pipe,
                                Slice(text, i + 1, n),
                                Slice(text, i, n),
                                pos,
                                expanding ? useEnd : n,
                                glued,
                                label
                            )
                        );
                        return;
                    }
                    output.Add(
                        Tok(
                            K.Pipe,
                            text.Substring(i + 1, k - i - 1),
                            text.Substring(i, k + 1 - i),
                            pos,
                            expanding ? useEnd : k + 1,
                            glued,
                            label
                        )
                    );
                    i = prevEnd = k + 1;
                    continue;
                }
                if (ch == '$' && expanding && i + 1 < n && IsDigit(text[i + 1]))
                {
                    int j = i + 1;
                    while (j < n && IsDigit(text[j]))
                        j++;
                    int code = ParseDecimalInt(text, i + 1, j);
                    string digits = text.Substring(i + 1, j - i - 1).TrimStart('0');
                    var tok = new Token(K.Intr, text.Substring(i, j - i), pos, useEnd, glued, label)
                    {
                        Intr = new IntrinsicCall(
                            code,
                            digits.Length > 0 ? digits : "0",
                            _intrLabel,
                            _intrArgs
                        ),
                    };
                    output.Add(tok);
                    i = prevEnd = j;
                    continue;
                }
                if (ch == '%' && _page)
                {
                    int k = text.IndexOf('%', i + 1);
                    if (k > i + 1 && k < n)
                    {
                        bool all = true;
                        for (int q = i + 1; q < k; q++)
                            if (!CharTable[text[q]])
                            {
                                all = false;
                                break;
                            }
                        if (all)
                        {
                            output.Add(
                                Tok(
                                    K.Genie,
                                    text.Substring(i + 1, k - i - 1),
                                    text.Substring(i, k + 1 - i),
                                    pos,
                                    expanding ? useEnd : k + 1,
                                    glued,
                                    label
                                )
                            );
                            i = prevEnd = k + 1;
                            continue;
                        }
                    }
                }
                string single = ch.ToString();
                output.Add(Tok(ch, single, single, pos, expanding ? useEnd : i + 1, glued, label));
                i = prevEnd = i + 1;
            }
        }

        private static int ParseDecimalInt(string s, int a, int b)
        {
            long v = 0;
            for (int i = a; i < b; i++)
            {
                v = v * 10 + (s[i] - '0');
                if (v > int.MaxValue)
                    return int.MaxValue;
            }
            return (int)v;
        }

        /// <summary>A run of name characters and '?' at <paramref name="i"/> holding exactly two '?'
        /// is one super genie token.
        /// Sets _sgEnd to the end of the run.</summary>
        private bool SuperGenie(
            string text,
            int i,
            List<Token> output,
            int pos,
            int useEnd,
            bool glued,
            string? label
        )
        {
            int n = text.Length;
            int j = i,
                qs = 0;
            while (j < n && (CharTable[text[j]] || text[j] == '?'))
            {
                if (text[j] == '?' && ++qs > 2)
                {
                    // a third '?': no super genie whatever follows, and the end of the run is not
                    // needed (scanning on would make 'a?a?a? ...' quadratic)
                    return false;
                }
                j++;
            }
            if (j == i || qs != 2)
                return false;
            _sgEnd = j;
            string run = text.Substring(i, j - i);
            string value =
                run.Length >= 2 && run[0] == '?' && run[run.Length - 1] == '?'
                    ? run.Substring(1, run.Length - 2)
                    : run;
            output.Add(Tok(K.Sg, value, run, pos, useEnd >= 0 ? useEnd : j, glued, label));
            return true;
        }

        // ------------------------------------------------------------------------------- labels
        /// <summary>Expand a label used at ``pos``; returns where lexing continues.</summary>
        private int Expand(
            Label lab,
            string text,
            int j,
            int n,
            List<Token> output,
            int pos,
            int useEnd,
            bool expanding,
            string outer,
            string[] active,
            bool glued
        )
        {
            List<string>? args = LabelTable.ReadLabelArgs(text, j, n, out int after, out bool err);
            if (lab.FuncLike && args == null && !expanding)
            {
                // no '(' after a function-like label: the lexer has read the next token looking
                // for it, so the expansion (or the E2057) is placed there (r2_diagA l05,
                // r2_rev_diagA r01 / r03: 'FUNCTION TimeHour' + newline 'END' is E2057 on line 2)
                if (j < n && IsWs(text[j]))
                {
                    int m = j;
                    while (m < n && IsWs(text[m]))
                        m++;
                    if (m < n)
                        pos = m;
                }
            }
            if (err)
                return LabelError(lab, pos, j, after, output, glued, outer);
            string body;
            List<string> actual;
            if (!lab.FuncLike)
            {
                if (args != null && args.Count > 0)
                    return LabelError(lab, pos, j, after, output, glued, outer);
                if (args == null)
                    after = j;
                body = lab.Body;
                actual = new List<string>();
            }
            else
            {
                if (args == null)
                {
                    after = j;
                    args = new List<string>();
                }
                List<string>? bound = lab.Bind(args);
                if (bound == null)
                    return LabelError(lab, pos, j, after, output, glued, outer);
                actual = bound;
                body = lab.Substitute(actual);
            }
            if (body.Length > 8192 || active.Length > 64)
            {
                Err(2059, pos, lab.Name);
                return after;
            }
            string savedLabel = _intrLabel;
            List<string> savedArgs = _intrArgs;
            _intrLabel = lab.Name;
            _intrArgs = actual;
            int start = output.Count;
            int end = expanding ? useEnd : after;
            var inner = new string[active.Length + 1];
            Array.Copy(active, inner, active.Length);
            inner[active.Length] = lab.Key;
            Scan(body, 0, body.Length, output, true, pos, end, outer, inner);
            _intrLabel = savedLabel;
            _intrArgs = savedArgs;
            if (output.Count > start)
                output[start].Glued = glued;
            return after;
        }

        /// <summary>E2057 for a label call with the wrong arguments; the call becomes one bad token
        /// that the parser reports as E2041 (PROBED l19 and builtins_editor g2 err_00: E2057 + E2041
        /// at the call).</summary>
        private int LabelError(
            Label lab,
            int pos,
            int j,
            int after,
            List<Token> output,
            bool glued,
            string outer
        )
        {
            Err(2057, pos, lab.Name);
            output.Add(Tok(K.Bad, lab.Name, lab.Name, pos, Math.Max(after, j), glued, outer));
            return after;
        }

        /// <summary>Tokenise <paramref name="text"/> (already decoded).</summary>
        public static LexResult Tokenize(
            string text,
            LabelTable? labels = null,
            string file = "",
            string mode = "ci",
            string project = "",
            bool tagStartDigit = false
        ) => new Lexer(text, labels, file, mode, project, tagStartDigit).Run();
    }

    /// <summary>Strings equal when their name keys are (ASCII letters folded, as the
    /// compiler compares names): lookups by a name without building its key.</summary>
    public sealed class AsciiFold : IEqualityComparer<string>
    {
        public static readonly AsciiFold Instance = new AsciiFold();

        private static char F(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;

        public bool Equals(string? a, string? b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i] && F(a[i]) != F(b[i]))
                    return false;
            return true;
        }

        public int GetHashCode(string s)
        {
            unchecked
            {
                int h = (int)2166136261;
                foreach (char c in s)
                    h = (h ^ F(c)) * 16777619;
                return h;
            }
        }
    }

    /// <summary>The token list of one source plus its diagnostics and line index.</summary>
    public sealed class LexResult
    {
        public readonly List<Token> Tokens;
        public readonly List<Diagnostic> Diagnostics;
        public readonly LineIndex Lines;
        public readonly string Text;
        public readonly string File;

        public LexResult(
            List<Token> tokens,
            List<Diagnostic> diagnostics,
            LineIndex lines,
            string text,
            string file
        )
        {
            Tokens = tokens;
            Diagnostics = diagnostics;
            Lines = lines;
            Text = text;
            File = file;
        }
    }
}
