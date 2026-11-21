using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Sim
{
    /// <summary>An expression the debugger cannot evaluate (unknown name, bad syntax, a call in
    /// a hover ...).</summary>
    public sealed class EvalException : Exception
    {
        public readonly int Code;

        public EvalException(string message, int code = 0)
            : base(message)
        {
            Code = code;
        }
    }

    /// <summary>The debugger's expressions: watch, hover, debug console and breakpoint conditions.</summary>
    public sealed class WatchEval
    {
        private enum K
        {
            End,
            Num,
            Str,
            Name,
            Op,
        }

        private struct Tok
        {
            public K Kind;
            public string Text;
            public Value Val;
        }

        /// <summary>A name's value (null = no such name). indices: the [i] parts (empty for none).</summary>
        public Func<string, IReadOnlyList<long>, Value?> Name = (n, i) => null;

        /// <summary>A call's result (throws EvalException); null = calls are refused.</summary>
        public Func<string, List<Value>, Value>? Call;
        public IOpContext Ctx = new OpCtx();

        private List<Tok> _toks = new List<Tok>();
        private int _p;
        private int _depth;

        /// <summary>The deepest nesting an expression may have.</summary>
        public const int MaxDepth = 100;

        private void Enter()
        {
            if (++_depth > MaxDepth)
                throw new EvalException(
                    "the expression is nested too deeply (more than "
                        + MaxDepth.ToString(CultureInfo.InvariantCulture)
                        + " levels)"
                );
        }

        // the tokens of the expressions evaluated lately (a breakpoint's condition runs at each
        // hit): read only once lexed
        private static readonly Dictionary<string, List<Tok>> Lexed = new Dictionary<string, List<Tok>>();
        private const int LexedMax = 256;

        private static List<Tok> Tokens(string text)
        {
            lock (Lexed)
            {
                if (Lexed.TryGetValue(text, out var toks))
                    return toks;
            }
            var lexed = Lex(text);
            lock (Lexed)
            {
                if (Lexed.Count >= LexedMax)
                    Lexed.Clear();
                Lexed[text] = lexed;
            }
            return lexed;
        }

        public Value Evaluate(string text)
        {
            _toks = Tokens(text ?? "");
            _p = 0;
            _depth = 0;
            if (_toks.Count == 1)
                throw new EvalException("an empty expression");
            Value v = Or();
            if (Peek.Kind != K.End)
                throw new EvalException("unexpected " + Show(Peek) + " after the expression");
            return v;
        }

        private Tok Peek => _toks[_p];

        private Tok Next() => _toks[_p++];

        private static string Show(Tok t) => t.Kind == K.End ? "the end" : "'" + t.Text + "'";

        private bool IsOp(string op)
        {
            var t = Peek;
            return (t.Kind == K.Op || t.Kind == K.Name)
                && string.Equals(t.Text, op, StringComparison.OrdinalIgnoreCase);
        }

        private void Expect(string op)
        {
            if (!IsOp(op))
                throw new EvalException("expected '" + op + "', found " + Show(Peek));
            _p++;
        }

        // lowest: BITAND / BITOR / BITXOR (all equal)
        private Value Or()
        {
            Value a = Bits();
            return a;
        }

        private Value Bits()
        {
            Value a = OrWord();
            while (true)
            {
                int op =
                    IsOp("BITAND") ? Op.BitAnd
                    : IsOp("BITOR") ? Op.BitOr
                    : IsOp("BITXOR") ? Op.BitXor
                    : -1;
                if (op < 0)
                    return a;
                _p++;
                a = Ops.Binary(op, a, OrWord(), Ctx);
            }
        }

        private Value OrWord()
        {
            Value a = AndWord();
            while (IsOp("OR"))
            {
                _p++;
                a = Ops.Binary(Op.Or, a, AndWord(), Ctx);
            }
            return a;
        }

        private Value AndWord()
        {
            Value a = Equality();
            while (IsOp("AND"))
            {
                _p++;
                a = Ops.Binary(Op.And, a, Equality(), Ctx);
            }
            return a;
        }

        private Value Equality()
        {
            Value a = Relational();
            while (true)
            {
                int op =
                    IsOp("=") ? Op.Eq
                    : IsOp("<>") ? Op.Ne
                    : -1;
                if (op < 0)
                    return a;
                _p++;
                a = Ops.Binary(op, a, Relational(), Ctx);
            }
        }

        private Value Relational()
        {
            Value a = Additive();
            while (true)
            {
                int op =
                    IsOp("<") ? Op.Lt
                    : IsOp("<=") ? Op.Le
                    : IsOp(">") ? Op.Gt
                    : IsOp(">=") ? Op.Ge
                    : -1;
                if (op < 0)
                    return a;
                _p++;
                a = Ops.Binary(op, a, Additive(), Ctx);
            }
        }

        private Value Additive()
        {
            Value a = Multiplicative();
            while (true)
            {
                int op =
                    IsOp("+") ? Op.Add
                    : IsOp("-") ? Op.Sub
                    : -1;
                if (op < 0)
                    return a;
                _p++;
                a = Ops.Binary(op, a, Multiplicative(), Ctx);
            }
        }

        private Value Multiplicative()
        {
            Value a = Unary();
            while (true)
            {
                int op =
                    IsOp("*") ? Op.Mul
                    : IsOp("/") ? Op.Div
                    : IsOp("MOD") ? Op.Mod
                    : -1;
                if (op < 0)
                    return a;
                _p++;
                a = Ops.Binary(op, a, Unary(), Ctx);
            }
        }

        private Value Unary()
        {
            Enter();
            try
            {
                if (IsOp("NOT"))
                {
                    _p++;
                    return Ops.Not(Unary(), Ctx);
                }
                if (IsOp("-"))
                {
                    _p++;
                    return Ops.Neg(Unary(), Ctx);
                }
                if (IsOp("+"))
                {
                    _p++;
                    return Unary();
                }
                return Primary();
            }
            finally
            {
                _depth--;
            }
        }

        private Value Primary()
        {
            var t = Next();
            switch (t.Kind)
            {
                case K.Num:
                case K.Str:
                    return t.Val;
                case K.Op when t.Text == "(":
                {
                    Value v = Or();
                    Expect(")");
                    return v;
                }
                case K.Name:
                {
                    if (IsOp("("))
                    {
                        _p++;
                        var args = new List<Value>();
                        if (!IsOp(")"))
                        {
                            while (true)
                            {
                                args.Add(Or());
                                if (IsOp(","))
                                {
                                    _p++;
                                    continue;
                                }
                                break;
                            }
                        }
                        Expect(")");
                        if (Call == null)
                            throw new EvalException(
                                "a function call is not evaluated here (a hover reads names only)"
                            );
                        return Call(t.Text, args);
                    }
                    var idx = new List<long>();
                    while (IsOp("["))
                    {
                        _p++;
                        while (true)
                        {
                            Value iv = Or();
                            idx.Add(Conv.ToInt(iv, NoErr.Instance).I);
                            if (IsOp(","))
                            {
                                _p++;
                                continue;
                            }
                            break;
                        }
                        Expect("]");
                    }
                    Value? v = Name(t.Text, idx);
                    if (v == null)
                        throw new EvalException("unknown name '" + t.Text + "'");
                    return v.Value;
                }
            }
            throw new EvalException("unexpected " + Show(t));
        }

        // ------------------------------------------------------------------ lexer
        // the compiler's name rule (Lexer) plus '\' (a watch may name a module variable)
        private static bool NameStart(char c) => CicodeSim.Compiler.Syntax.Lexer.IsNameStart(c) || c == '\\';

        private static bool NameChar(char c) =>
            CicodeSim.Compiler.Syntax.Lexer.IsNameChar(c) || c == '.' || c == '@';

        /// <summary>An ASCII digit (char.IsDigit also takes other scripts' digits, which the
        /// number code below would read as garbage).</summary>
        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static bool IsHex(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

        private static readonly string[] Ops2 = { "<=", ">=", "<>" };

        private static List<Tok> Lex(string s)
        {
            var o = new List<Tok>();
            int i = 0;
            while (true)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                    i++;
                if (i >= s.Length)
                {
                    o.Add(new Tok { Kind = K.End, Text = "" });
                    return o;
                }
                char c = s[i];
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    bool closed = false;
                    while (i < s.Length)
                    {
                        char ch = s[i];
                        if (ch == '^' && i + 1 < s.Length)
                        {
                            char e = s[i + 1];
                            i += 2;
                            switch (char.ToLowerInvariant(e))
                            {
                                case 'n':
                                    sb.Append('\n');
                                    break;
                                case 'r':
                                    sb.Append('\r');
                                    break;
                                case 't':
                                    sb.Append('\t');
                                    break;
                                case 'f':
                                    sb.Append('\f');
                                    break;
                                case 'v':
                                    sb.Append('\x11');
                                    break;
                                case '0':
                                    sb.Append('\0');
                                    break;
                                default:
                                    sb.Append(e);
                                    break;
                            }
                            continue;
                        }
                        if (ch == '"')
                        {
                            closed = true;
                            i++;
                            break;
                        }
                        sb.Append(ch);
                        i++;
                    }
                    if (!closed)
                        throw new EvalException("a string with no closing quote");
                    string str = sb.ToString();
                    if (str.Length > Types.StringMax)
                        str = str.Substring(0, Types.StringMax);
                    o.Add(
                        new Tok
                        {
                            Kind = K.Str,
                            Text = str,
                            Val = Value.Str(str),
                        }
                    );
                    continue;
                }
                if (IsDigit(c) || (c == '.' && i + 1 < s.Length && IsDigit(s[i + 1])))
                {
                    int st = i;
                    if (c == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                    {
                        i += 2;
                        long v = 0;
                        while (i < s.Length && IsHex(s[i]))
                            v = (v * 16 + HexValue(s[i++])) & 0xFFFFFFFFL;
                        o.Add(
                            new Tok
                            {
                                Kind = K.Num,
                                Text = s.Substring(st, i - st),
                                Val = Value.Long((int)(uint)v),
                            }
                        );
                        continue;
                    }
                    while (i < s.Length && IsDigit(s[i]))
                        i++;
                    bool real = false;
                    if (i < s.Length && s[i] == '.')
                    {
                        real = true;
                        i++;
                        while (i < s.Length && IsDigit(s[i]))
                            i++;
                    }
                    if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                    {
                        int save = i;
                        i++;
                        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                            i++;
                        if (i < s.Length && IsDigit(s[i]))
                        {
                            real = true;
                            while (i < s.Length && IsDigit(s[i]))
                                i++;
                        }
                        else
                            i = save;
                    }
                    string txt = s.Substring(st, i - st);
                    // the engine's strtod: 1e999 is infinity on every target (net48's double.Parse
                    // throws OverflowException there, out of an Evaluate that must not throw)
                    double rv = 0;
                    if (real && !FloatText.TryParseNumber(txt, out rv))
                        throw new EvalException("not a number: " + txt);
                    if (real)
                        o.Add(
                            new Tok
                            {
                                Kind = K.Num,
                                Text = txt,
                                Val = Value.Real(rv),
                            }
                        );
                    else
                    {
                        ulong u = 0;
                        foreach (char d in txt)
                            u = unchecked(u * 10 + (ulong)(d - '0')) & 0xFFFFFFFFUL; // 32-bit like the compiler's literals
                        o.Add(
                            new Tok
                            {
                                Kind = K.Num,
                                Text = txt,
                                Val = Value.Long((int)(uint)u),
                            }
                        );
                    }
                    continue;
                }
                if (NameStart(c))
                {
                    int st = i;
                    while (i < s.Length && NameChar(s[i]))
                        i++;
                    o.Add(new Tok { Kind = K.Name, Text = s.Substring(st, i - st) });
                    continue;
                }
                string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (Array.IndexOf(Ops2, two) >= 0)
                {
                    o.Add(new Tok { Kind = K.Op, Text = two });
                    i += 2;
                    continue;
                }
                if ("()[],+-*/<>=".IndexOf(c) >= 0)
                {
                    o.Add(new Tok { Kind = K.Op, Text = c.ToString() });
                    i++;
                    continue;
                }
                throw new EvalException("unexpected character '" + c + "'");
            }
        }
    }
}
