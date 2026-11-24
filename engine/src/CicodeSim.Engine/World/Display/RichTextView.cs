using System;
using System.Collections.Generic;

namespace CicodeSim.World.Display
{
    /// <summary>A rich text box (DspRichText): its size in pixels, mode, scroll position and the
    /// RTF it shows. The engine measures the RTF only by its paragraphs (lines) and their plain
    /// text lengths; the viewer renders it.</summary>
    public sealed class RichTextView
    {
        /// <summary>A line's height in pixels for scrolling (guess: a 10 point line).</summary>
        public const int LineHeight = 16;

        /// <summary>A horizontal scroll unit in pixels: one average character (guess).</summary>
        public const int CharWidth = 8;

        /// <summary>A tab stop every this many characters (RTF's default 720 twips; guess).</summary>
        public const int TabChars = 6;

        /// <summary>The largest file DspRichTextLoad loads (the help's 512 KB).</summary>
        public const int MaxBytes = 512 * 1024;

        public int Height,
            Width,
            Mode,
            Row,
            Col;

        public bool Enabled,
            Editable;

        /// <summary>The RTF shown ("" before a load).</summary>
        public string Rtf = "";

        /// <summary>Each paragraph's plain text length in characters.</summary>
        public List<int> Lines = new List<int>();

        /// <summary>The lines the box shows at once (at least 1).</summary>
        public int PageLines => Math.Max(1, Height / LineHeight);

        /// <summary>The characters the box shows across (at least 1).</summary>
        public int PageCols => Math.Max(1, Width / CharWidth);

        public int Longest
        {
            get
            {
                int n = 0;
                foreach (int k in Lines)
                    n = Math.Max(n, k);
                return n;
            }
        }

        /// <summary>The last first line: the last line on the box's last row.</summary>
        public int MaxRow => Math.Max(0, Lines.Count - PageLines);

        /// <summary>The last column offset: the longest line's end at the box's right edge.</summary>
        public int MaxCol => Math.Max(0, Longest - PageCols);

        public RichTextView Copy() =>
            new RichTextView
            {
                Height = Height,
                Width = Width,
                Mode = Mode,
                Row = Row,
                Col = Col,
                Enabled = Enabled,
                Editable = Editable,
                Rtf = Rtf,
                Lines = Lines,
            };

        /// <summary>The groups whose text is not shown, by their first control word.</summary>
        private static readonly HashSet<string> Hidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "fonttbl",
            "colortbl",
            "stylesheet",
            "info",
            "pict",
            "object",
            "header",
            "headerl",
            "headerr",
            "headerf",
            "footer",
            "footerl",
            "footerr",
            "footerf",
            "listtable",
            "listoverridetable",
            "rsidtbl",
            "xmlnstbl",
            "generator",
        };

        /// <summary>The plain text length of each paragraph of <paramref name="rtf"/>: a paragraph
        /// ends at \par; text after the last \par is one more. \tab moves to the next tab stop,
        /// \'hh, \uN and the escaped { } \ are one character each; {\* ...} groups and the font,
        /// colour, style and information groups are not text.</summary>
        public static List<int> Paragraphs(string rtf)
        {
            var o = new List<int>();
            var hidden = new Stack<bool>();
            bool skip = false;
            int len = 0,
                uc = 1,
                fallback = 0;
            int i = 0,
                n = rtf.Length;
            while (i < n)
            {
                char ch = rtf[i];
                if (ch == '{')
                {
                    hidden.Push(skip);
                    i++;
                    if (i + 1 < n && rtf[i] == '\\' && rtf[i + 1] == '*')
                        skip = true;
                    else if (i < n && rtf[i] == '\\' && Hidden.Contains(Word(rtf, i + 1)))
                        skip = true;
                    continue;
                }
                if (ch == '}')
                {
                    skip = hidden.Count > 0 && hidden.Pop();
                    i++;
                    continue;
                }
                if (ch == '\r' || ch == '\n')
                {
                    i++;
                    continue;
                }
                if (ch != '\\')
                {
                    i++;
                    Text(ref len, ref fallback, skip, 1);
                    continue;
                }
                // a control word or symbol
                i++;
                if (i >= n)
                    break;
                char c = rtf[i];
                if (!IsLetter(c))
                {
                    i++;
                    if (c == '\'')
                    {
                        i = Math.Min(n, i + 2);
                        Text(ref len, ref fallback, skip, 1);
                    }
                    else if (c == '{' || c == '}' || c == '\\' || c == '~' || c == '_')
                        Text(ref len, ref fallback, skip, 1);
                    continue;
                }
                string word = Word(rtf, i);
                i += word.Length;
                int a = i;
                if (i < n && rtf[i] == '-')
                    i++;
                while (i < n && rtf[i] >= '0' && rtf[i] <= '9')
                    i++;
                bool hasArg = i > a && rtf[i - 1] != '-';
                int arg = 0;
                if (hasArg)
                    int.TryParse(rtf.Substring(a, i - a), out arg);
                if (i < n && rtf[i] == ' ')
                    i++;
                if (skip)
                    continue;
                switch (word)
                {
                    case "par":
                        o.Add(len);
                        len = 0;
                        fallback = 0;
                        break;
                    case "tab":
                        len += TabChars - len % TabChars;
                        break;
                    case "uc":
                        uc = hasArg ? Math.Max(0, arg) : 1;
                        break;
                    case "u":
                        len++;
                        fallback = uc;
                        break;
                    case "emdash":
                    case "endash":
                    case "bullet":
                    case "lquote":
                    case "rquote":
                    case "ldblquote":
                    case "rdblquote":
                        Text(ref len, ref fallback, skip, 1);
                        break;
                }
            }
            if (len > 0)
                o.Add(len);
            return o;
        }

        private static void Text(ref int len, ref int fallback, bool skip, int k)
        {
            if (skip)
                return;
            if (fallback > 0)
            {
                fallback--;
                return;
            }
            len += k;
        }

        private static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        /// <summary>The control word's letters from <paramref name="i"/> ("" when none).</summary>
        private static string Word(string s, int i)
        {
            int a = i;
            while (i < s.Length && IsLetter(s[i]))
                i++;
            return s.Substring(a, i - a);
        }
    }
}
