using System;
using System.Collections.Generic;
using System.Globalization;

namespace CicodeSim.Compiler.Syntax
{
    /// <summary>Token kinds as ints. A one-character token's kind is the character itself.</summary>
    public static class K
    {
        public const int Name = 0x10000;

        /// <summary>value: unsigned 32-bit int (0 .. 0xFFFFFFFF).</summary>
        public const int Int = 0x10001;
        public const int Real = 0x10002;

        /// <summary>value: the decoded string (escapes resolved).</summary>
        public const int Str = 0x10003;

        /// <summary>'#...' format picture.</summary>
        public const int Pic = 0x10004;

        /// <summary>'|...|', value: the text between the bars.</summary>
        public const int Pipe = 0x10005;

        /// <summary>'?name?' super genie association.</summary>
        public const int Sg = 0x10006;

        /// <summary>A compiler intrinsic ($n) from a label.</summary>
        public const int Intr = 0x10007;

        /// <summary>'%name%' (page mode only).</summary>
        public const int Genie = 0x10008;
        public const int Eof = 0x10009;

        /// <summary>A token the lexer rejected (a failed label call E2057, a number past 32 bits
        /// E2058, a string past 255 characters E2055 or open at the end E2054): the parser
        /// reports E2041 at it.</summary>
        public const int Bad = 0x1000A;

        public const int Le = 0x10100; // "<="
        public const int Ne = 0x10101; // "<>"
        public const int Ge = 0x10102; // ">="

        public const int KeywordBase = 0x10200;

        /// <summary>The compiler's keywords. CIVBA is spelled 'CiVBA' there; lookup is
        /// case-insensitive.</summary>
        public static readonly string[] Keywords =
        {
            "NOT",
            "OR",
            "MOD",
            "AND",
            "DO",
            "END",
            "IF",
            "THEN",
            "ELSE",
            "WHILE",
            "CASE",
            "FUNCTION",
            "RETURN",
            "INT",
            "REAL",
            "STRING",
            "BITAND",
            "BITOR",
            "BITXOR",
            "FOR",
            "TO",
            "VAR",
            "SELECT",
            "IS",
            "NOP",
            "GLOBAL",
            "MODULE",
            "PUBLIC",
            "PRIVATE",
            "OBJECT",
            "CICODE",
            "CIVBA",
            "TIMESTAMP",
            "QUALITY",
        };

        public const int NOT = KeywordBase + 0,
            OR = KeywordBase + 1,
            MOD = KeywordBase + 2,
            AND = KeywordBase + 3,
            DO = KeywordBase + 4,
            END = KeywordBase + 5,
            IF = KeywordBase + 6,
            THEN = KeywordBase + 7,
            ELSE = KeywordBase + 8,
            WHILE = KeywordBase + 9,
            CASE = KeywordBase + 10,
            FUNCTION = KeywordBase + 11,
            RETURN = KeywordBase + 12,
            INT = KeywordBase + 13,
            REAL = KeywordBase + 14,
            STRING = KeywordBase + 15,
            BITAND = KeywordBase + 16,
            BITOR = KeywordBase + 17,
            BITXOR = KeywordBase + 18,
            FOR = KeywordBase + 19,
            TO = KeywordBase + 20,
            VAR = KeywordBase + 21,
            SELECT = KeywordBase + 22,
            IS = KeywordBase + 23,
            NOP = KeywordBase + 24,
            GLOBAL = KeywordBase + 25,
            MODULE = KeywordBase + 26,
            PUBLIC = KeywordBase + 27,
            PRIVATE = KeywordBase + 28,
            OBJECT = KeywordBase + 29,
            CICODE = KeywordBase + 30,
            CIVBA = KeywordBase + 31,
            TIMESTAMP = KeywordBase + 32,
            QUALITY = KeywordBase + 33;

        private static readonly Dictionary<string, int> KeywordByLower = BuildKeywords();

        private static Dictionary<string, int> BuildKeywords()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Keywords.Length; i++)
                d[Keywords[i].ToLowerInvariant()] = KeywordBase + i;
            return d;
        }

        private static readonly Dictionary<string, int> KeywordByFold = new Dictionary<string, int>(
            KeywordByLower,
            AsciiFold.Instance
        );

        /// <summary>The keyword of a name as written (its name key looked up), -1 when it is none.</summary>
        public static int KeywordFolded(string name) => KeywordByFold.TryGetValue(name, out int k) ? k : -1;

        /// <summary>The keyword of a lower-case name key, -1 when it is none.</summary>
        public static int Keyword(string lowerKey) =>
            KeywordByLower.TryGetValue(lowerKey, out int k) ? k : -1;

        public static bool IsKeyword(int kind) => kind >= KeywordBase && kind < KeywordBase + Keywords.Length;

        /// <summary>INT REAL STRING OBJECT TIMESTAMP QUALITY.</summary>
        public static bool IsTypeKeyword(int kind) =>
            kind == INT
            || kind == REAL
            || kind == STRING
            || kind == OBJECT
            || kind == TIMESTAMP
            || kind == QUALITY;

        /// <summary>GLOBAL MODULE PUBLIC PRIVATE.</summary>
        public static bool IsScopeKeyword(int kind) =>
            kind == GLOBAL || kind == MODULE || kind == PUBLIC || kind == PRIVATE;

        /// <summary>The kind's name ('name', 'IF', '&lt;=', '(').</summary>
        public static string Text(int kind)
        {
            if (kind < 0x10000)
                return ((char)kind).ToString();
            switch (kind)
            {
                case Name:
                    return "name";
                case Int:
                    return "int";
                case Real:
                    return "real";
                case Str:
                    return "str";
                case Pic:
                    return "pic";
                case Pipe:
                    return "pipe";
                case Sg:
                    return "sg";
                case Intr:
                    return "intr";
                case Genie:
                    return "genie";
                case Eof:
                    return "eof";
                case Bad:
                    return "bad";
                case Le:
                    return "<=";
                case Ne:
                    return "<>";
                case Ge:
                    return ">=";
            }
            if (IsKeyword(kind))
                return Keywords[kind - KeywordBase];
            return "?" + kind.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A compiler intrinsic reached through a label whose text is $n (the Include
    /// project's __LINE__ = $3, IFDEF(sTag,sTrue,sFalse) = $9 ...).
    /// <see cref="Args"/> are the raw argument texts of the label call (defaults filled in).</summary>
    public sealed class IntrinsicCall
    {
        /// <summary>The number (int.MaxValue for one past it: no intrinsic has such a number).</summary>
        public readonly int Code;

        /// <summary>The number as written, leading zeros dropped.</summary>
        public readonly string CodeDigits;
        public readonly string Label;
        public readonly List<string> Args;

        public IntrinsicCall(int code, string codeDigits, string label, IList<string> args)
        {
            Code = code;
            CodeDigits = codeDigits;
            Label = label;
            Args = new List<string>(args);
        }
    }

    /// <summary>One token.</summary>
    public sealed class Token
    {
        public int Kind;

        /// <summary>The string value: a name, a keyword (upper case), a string literal (decoded),
        /// a picture, a pipe text, an association, a genie name, an operator's text, or a bad
        /// token's text / label name.</summary>
        public string? SValue;

        /// <summary>The value of an INT token (and of a bad number: 0).</summary>
        public long IValue;
        public double RValue;
        public IntrinsicCall? Intr;
        public string Text;
        public int Pos;
        public int End;
        public bool Glued;
        public string? Label;

        /// <summary>A number past 32 bits (E2058): its kind is the token's before it (CtCmp32
        /// leaves the kind as it was). As a name kind it is no operand (E2041 at it).</summary>
        public bool Overflow;

        public Token(int kind, string text, int pos, int end, bool glued, string? label)
        {
            Kind = kind;
            Text = text;
            Pos = pos;
            End = end;
            Glued = glued;
            Label = label;
        }

        public override string ToString() =>
            "Token(" + K.Text(Kind) + " " + Text + " @" + Pos.ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>Offset to 1-based (line, column) of a text (lines counted by '\n', like the
    /// compiler).</summary>
    public sealed class LineIndex
    {
        private readonly int[] _nl;

        public LineIndex(string text)
        {
            var nl = new List<int>();
            int i = text.IndexOf('\n');
            while (i >= 0)
            {
                nl.Add(i);
                i = text.IndexOf('\n', i + 1);
            }
            _nl = nl.ToArray();
        }

        // bisect_left
        private int Left(int pos)
        {
            int lo = 0,
                hi = _nl.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_nl[mid] < pos)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        public int Line(int pos) => Left(pos) + 1;

        public (int line, int col) LineCol(int pos)
        {
            int n = Left(pos);
            int start = n > 0 ? _nl[n - 1] + 1 : 0;
            return (n + 1, pos - start + 1);
        }
    }
}
