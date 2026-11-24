using System;
using System.Collections.Generic;
using System.Numerics;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Project;

namespace CicodeSim.Compiler.Syntax
{
    /// <summary>Abandon the current statement / item. Skip = the block loop skips the token at
    /// Index before going on.</summary>
    internal sealed class ParseError : Exception
    {
        public readonly int Index;
        public readonly bool Skip;
        public readonly int Code;

        public ParseError(int index, bool skip, int code)
            : base("parse error")
        {
            Index = index;
            Skip = skip;
            Code = code;
        }
    }

    /// <summary>A malformed parameter list: the header stops, the body starts at the current token.</summary>
    internal sealed class HeaderAbort : Exception
    {
        public HeaderAbort()
            : base("header abort") { }
    }

    /// <summary>The nesting passed <see cref="Parser.MaxNesting"/> (F4008 reported): the file is not
    /// parsed any further.</summary>
    internal sealed class TooDeep : Exception
    {
        public TooDeep()
            : base("too deep") { }
    }

    /// <summary>The Cicode parser for .ci files and page fields.</summary>
    public sealed class Parser
    {
        public const int FunctionNameMax = 250;
        public const long ArrayMax = 32767;
        public const int MaxParams = 128;

        /// <summary>The nesting limit. Deeper source is F4008.</summary>
        public const int MaxNesting = 150;

        private readonly List<Token> _toks;
        private readonly LexResult _lex;
        private int _i;
        public readonly string File;
        public readonly string Project;
        public readonly string Mode;
        private readonly bool _page;

        /// <summary>Lexer and body messages.</summary>
        public List<Diagnostic> Diags;

        /// <summary>Messages of the declaration pass: file-scope items and function headers (and
        /// the body of a function whose header broke off), reported before any body is compiled
        /// (builtins_editor e04, inc q12_*_label_macro).</summary>
        public List<Diagnostic> DeclDiags = new List<Diagnostic>();
        private bool _phase1;
        private readonly LineIndex _lines;
        private bool _eofReported;

        // a FUNCTION inside a nested block ended that block (E2076): the statement that owns it
        // reports its missing END at the current token without taking it (r2_grammar nest2)
        private bool _blockBroken;
        private FunctionDef? _fn;
        private bool _afterVoidReturn;

        // variables in scope while parsing: a variable hides a function of its name, so 'v(1)' is
        // the variable and then a new statement '(1)' (q16)
        private readonly HashSet<string> _globals;
        private readonly HashSet<string> _moduleVars = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _moduleArrays = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> _locals = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>FUNCTION-kind argument positions of the built-ins by lower-case name: the
        /// compiler parses such an argument as one name token (r2_diagB b09 .. b11); set by the unit.</summary>
        public IReadOnlyDictionary<string, HashSet<int>> Callbacks = Empty;

        // after an error placeholder or a rejected operator, a missing operand right after the next
        // operator is not reported again (lexical c09 '*/', o08 '&&')
        private int _quietAt = -1;
        private int _rparen = -1;
        public readonly HashSet<string> CallNames = new HashSet<string>(StringComparer.Ordinal);
        private int _depth;

        private static readonly Dictionary<string, HashSet<int>> Empty =
            new Dictionary<string, HashSet<int>>();

        public Parser(
            LexResult lex,
            string file = "",
            string project = "",
            string mode = "ci",
            IEnumerable<string>? knownVars = null
        )
        {
            _toks = lex.Tokens;
            _lex = lex;
            File = string.IsNullOrEmpty(file) ? lex.File : file;
            Project = project ?? "";
            Mode = mode;
            _page = mode == "page";
            Diags = new List<Diagnostic>(lex.Diagnostics);
            _lines = lex.Lines;
            _globals =
                knownVars != null
                    ? new HashSet<string>(knownVars, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
        }

        private bool IsVar(string name)
        {
            string k = Lexer.NameKey(name);
            return _locals.Contains(k) || _moduleVars.Contains(k) || _globals.Contains(k);
        }

        // ------------------------------------------------------------------------- helpers
        private Token Cur => _toks[_i];

        private Token Peek(int k = 1)
        {
            int j = _i + k;
            return j < _toks.Count ? _toks[j] : _toks[_toks.Count - 1];
        }

        private int LineOf(Token tok) => _lines.Line(tok.Pos);

        private void Error(int code, Token? tok = null)
        {
            tok ??= Cur;
            if (tok.Kind == K.Eof && code == 2034)
            {
                if (_eofReported)
                    return;
                _eofReported = true;
            }
            int at = tok.Pos;
            if (tok.Label == null && tok.Text.IndexOf('\n') >= 0)
            {
                // a token across lines is reported where the scan stopped, after it (fix_lexing
                // p01: a '|...|' token over two lines; an open string at the end of the input)
                at = tok.End;
            }
            var (line, col) = _lines.LineCol(at);
            var d = new Diagnostic(code, File, line, col, tok.Text, Project, tok.Pos, IndexOf(tok));
            (_phase1 ? DeclDiags : Diags).Add(d);
        }

        /// <summary>The index of <paramref name="tok"/> in the token list (it is at or near the
        /// current one).</summary>
        private int IndexOf(Token tok)
        {
            int i = _i;
            foreach (int j in new[] { i, i - 1, i + 1, i - 2, i + 2 })
                if (j >= 0 && j < _toks.Count && ReferenceEquals(_toks[j], tok))
                    return j;
            return -1;
        }

        /// <summary>Report <paramref name="code"/> at <paramref name="tok"/> (default: the current
        /// token) and abandon the statement; <paramref name="skip"/> = the block loop skips the
        /// current token (only when tok is it).</summary>
        private ParseError Fail(int code, Token? tok = null, bool skip = true)
        {
            Token cur = Cur;
            tok ??= cur;
            Error(code, tok);
            return new ParseError(_i, skip && ReferenceEquals(tok, cur) && cur.Kind != K.Eof, code);
        }

        /// <summary>The keyword or <paramref name="code"/> at the token found, which is consumed
        /// (unless it is the end of the input or a block keyword the caller still needs).</summary>
        private Token ExpectKw(int kind, int code)
        {
            Token t = Cur;
            if (t.Kind == kind)
            {
                _i++;
                return t;
            }
            if (kind == K.END && _blockBroken)
            {
                // the block was ended by a FUNCTION (E2076): E2034 at the token after it, which
                // stays for the enclosing block (nest2)
                _blockBroken = false;
                Error(code, t);
                throw new ParseError(_i, false, code);
            }
            Error(code, t);
            throw new ParseError(_i, t.Kind != K.Eof, code);
        }

        private void Recover(ParseError e, int start)
        {
            if (e.Skip && _i <= e.Index && _toks[e.Index].Kind != K.Eof)
                _i = e.Index + 1;
            else if (_i == start && Cur.Kind != K.Eof)
                _i++;
        }

        private static bool IsItemStart(int k) =>
            K.IsScopeKeyword(k) || K.IsTypeKeyword(k) || k == K.FUNCTION || k == K.Eof;

        private static bool IsBlockEnd(int k) => k == K.END || k == K.ELSE || k == K.CASE || k == K.Eof;

        private static bool IsRelop(int k) =>
            k == '=' || k == K.Ne || k == '<' || k == K.Le || k == '>' || k == K.Ge;

        /// <summary>Binding power (higher binds tighter) = 8 - the precedence level; 0 = not a
        /// binary operator.</summary>
        private static int Bp(int k)
        {
            switch (k)
            {
                case '*':
                case '/':
                case K.MOD:
                    return 8;
                case ':':
                    return 7;
                case '+':
                case '-':
                    return 6;
                case '<':
                case K.Le:
                case '>':
                case K.Ge:
                    return 5;
                case '=':
                case K.Ne:
                    return 4;
                case K.AND:
                    return 3;
                case K.OR:
                    return 2;
                case K.BITAND:
                case K.BITOR:
                case K.BITXOR:
                    return 1;
            }
            return 0;
        }

        private static string KwName(int k) => K.Keywords[k - K.KeywordBase];

        // ---------------------------------------------------------------------------- file
        public Module ParseModule()
        {
            var mod = new Module(File, Project) { Lines = _lines, Text = _lex.Text };
            var items = mod.Items;
            bool aborted = false;
            int stopPos = -1;
            while (Cur.Kind != K.Eof)
            {
                int start = _i;
                int nDiags = Diags.Count;
                try
                {
                    var got = ParseItem();
                    items.AddRange(got);
                    aborted = got.Count > 0 && got[got.Count - 1] is FunctionDef fd && fd.HeaderAborted;
                }
                catch (TooDeep)
                {
                    // F4008 was reported where the nesting passed MaxNesting: no more of the file
                    _phase1 = false;
                    _depth = 0;
                    break;
                }
                catch (ParseError e)
                {
                    Recover(e, start);
                    int k = _toks[_i].Kind;
                    bool ended = e.Code == 2031;
                    if (e.Code != 2031 && !IsItemStart(k) && k != ';' && k != '(')
                    {
                        // the bad token is taken, the next one is no item: E2031 there (fix_diagB
                        // i07 'MODULE INT a = (1);': E2006 at '(', E2031 at the 1; syntax d16; a ';'
                        // ends the declaration, and so does a '(')
                        Error(2031);
                        ended = true;
                    }
                    // then the compiler looks for the next item: one E2031 for '; RETURN 0; END'
                    // (lexical kl_end)
                    while (!IsItemStart(_toks[_i].Kind))
                        _i++;
                    if (aborted)
                    {
                        // still the declaration pass of the function whose header broke off
                        // (syntax h47 'FUNCTION F(INT end) END': E2008, then E2031 at the ')')
                        if (Diags.Count > nDiags)
                        {
                            DeclDiags.AddRange(Diags.GetRange(nDiags, Diags.Count - nDiags));
                            Diags.RemoveRange(nDiags, Diags.Count - nDiags);
                        }
                    }
                    aborted = false;
                    if (ended && stopPos < 0)
                    {
                        // the first file-scope E2031 ends what the compiler reports for the file
                        // (r2_diagA P_l13; syntax l07; rev z06 / z09). The functions after it are
                        // still declared (fix_diagA s09), so the parse goes on.
                        for (int q = nDiags; q < Diags.Count; q++)
                            if (Diags[q].Code == 2031)
                            {
                                stopPos = Diags[q].Pos;
                                break;
                            }
                    }
                }
                if (_i == start)
                    _i++;
            }
            if (stopPos >= 0)
                Diags = Diags.FindAll(d => d.Pos <= stopPos);
            mod.StopPos = stopPos;
            mod.Diagnostics = Diags;
            mod.DeclDiagnostics = DeclDiags;
            mod.CallNames = CallNames;
            return mod;
        }

        private List<Node> ParseItem()
        {
            Token startTok = Cur;
            string? scope = null;
            if (K.IsScopeKeyword(startTok.Kind))
            {
                scope = KwName(startTok.Kind);
                _i++;
                if (K.IsScopeKeyword(Cur.Kind))
                    throw Fail(2031); // two scope keywords (h07)
            }
            string? typ = null;
            if (K.IsTypeKeyword(Cur.Kind))
            {
                typ = KwName(Cur.Kind);
                _i++;
            }
            if (Cur.Kind == K.FUNCTION)
            {
                // E2069 / E2070 at the function's name, E2071 / E2072 at the variable's (the
                // recorded contexts 'MODULE FUNCTION {ProbeA}', 'PUBLIC INT {pX}')
                if (scope == "GLOBAL")
                    Error(2070, Peek());
                else if (scope == "MODULE")
                    Error(2069, Peek());
                return new List<Node>
                {
                    ParseFunction(scope == "PUBLIC" || scope == "PRIVATE" ? scope : null, typ, startTok),
                };
            }
            if (typ != null)
            {
                if (scope == "PUBLIC")
                    Error(2071, Cur);
                else if (scope == "PRIVATE")
                    Error(2072, Cur);
                var decls = ParseFileDecl(
                    scope == "GLOBAL" || scope == "MODULE" ? scope : null,
                    typ,
                    startTok
                );
                if (Cur.Kind == ';')
                    _i++;
                return decls;
            }
            throw Fail(2031);
        }

        // ------------------------------------------------------------------ file variables
        private List<Node> ParseFileDecl(string? scope, string typ, Token startTok)
        {
            var decls = new List<Node>();
            while (true)
            {
                Token t = Cur;
                if (t.Kind != K.Name)
                    throw Fail(2031);
                string name = t.SValue!;
                if (name.IndexOf('.') >= 0)
                    throw Fail(2031, t);
                _i++;
                var dims = new List<long>();
                while (Cur.Kind == '[' && dims.Count < 4)
                {
                    _i++;
                    Token st = Cur;
                    if (st.Kind == K.Name || st.Kind == K.Real || st.Kind == K.Str)
                    {
                        // one operand that is no integer is E2006 there and taken as the size, then
                        // the ']' is read as usual (fix_diagB m11 / syntax d20 without their label)
                        Error(2006, st);
                        _i++;
                        if (Cur.Kind != ']')
                            throw Fail(2011);
                        _i++;
                        dims.Add(1);
                        continue;
                    }
                    if (st.Kind != K.Int)
                        throw Fail(2006);
                    _i++;
                    if (Cur.Kind != ']')
                        throw Fail(2011);
                    _i++;
                    if (st.IValue == 0)
                        Error(2006, st);
                    dims.Add(st.IValue);
                }
                long count = VarDecl.VarDeclCount(dims);
                if (count > ArrayMax)
                    Error(2016, t);
                int line = LineOf(t);
                var decl = new VarDecl(scope, typ, name, dims, t.Pos, line);
                decls.Add(decl);
                if (scope != "GLOBAL")
                {
                    _moduleVars.Add(Lexer.NameKey(name));
                    if (dims.Count > 0)
                        _moduleArrays.Add(Lexer.NameKey(name));
                }
                if (Cur.Kind == '=')
                {
                    _i++;
                    if (typ == "QUALITY" || typ == "TIMESTAMP" || Cur.Kind == ';')
                    {
                        // the '=' is taken, no value is read: '= ;' compiles (fix_diagB m06, i25), a
                        // QUALITY '= 0' leaves the 0 at file scope (syntax m42 E2031)
                        return decls;
                    }
                    while (true)
                    {
                        decl.Init.Add(ParseInitLiteral(typ));
                        if (Cur.Kind == ',')
                        {
                            _i++;
                            if (decl.Init.Count >= count)
                                return decls;
                            continue;
                        }
                        return decls;
                    }
                }
                if (Cur.Kind == ',')
                {
                    _i++;
                    if (Cur.Kind == K.GLOBAL || Cur.Kind == K.MODULE) // ', MODULE INT b' (fix_lexing d05)
                    {
                        scope = KwName(Cur.Kind);
                        _i++;
                        if (!K.IsTypeKeyword(Cur.Kind))
                            throw Fail(2031);
                    }
                    if (K.IsTypeKeyword(Cur.Kind)) // ', INT b' starts a new declaration
                    {
                        typ = KwName(Cur.Kind);
                        int pk = _toks[_i - 1].Kind;
                        if (pk != K.GLOBAL && pk != K.MODULE)
                            scope = null;
                        _i++;
                    }
                    continue;
                }
                return decls;
            }
        }

        /// <summary>One file-scope initial value: INT an integer literal, REAL a
        /// number, STRING a string literal, OBJECT only -1; one optional sign.</summary>
        private Literal ParseInitLiteral(string typ)
        {
            Token t = Cur;
            bool neg = false;
            Token? sign = null;
            if (t.Kind == '+' || t.Kind == '-')
            {
                sign = t;
                neg = t.Kind == '-';
                _i++;
                t = Cur;
            }
            int line = LineOf(t);
            if (typ == "STRING")
            {
                if (sign != null || t.Kind != K.Str)
                {
                    if (t.Kind != K.Eof && !IsItemStart(t.Kind) && Peek().Kind == K.Str)
                    {
                        // the bad token is taken and the string after it read as the value
                        // (fix_diagB m08 '("x")': E2017 at '(', E2031 at ')')
                        Error(2017, t);
                        _i += 2;
                        if (!IsItemStart(Cur.Kind) && Cur.Kind != ';' && Cur.Kind != ',')
                            throw Fail(2031);
                        return new Literal("STRING", t.Pos, line) { SValue = _toks[_i - 1].SValue };
                    }
                    throw Fail(2017, t);
                }
                _i++;
                return new Literal("STRING", t.Pos, line) { SValue = t.SValue };
            }
            if (typ == "OBJECT")
            {
                if (t.Kind == K.Int)
                {
                    _i++;
                    if (!(neg && t.IValue == 1))
                        Error(2024, t);
                    return new Literal("INT", t.Pos, line) { IValue = neg ? -t.IValue : t.IValue };
                }
                throw Fail(2006, t);
            }
            if (t.Kind == K.Int)
            {
                _i++;
                long v = t.IValue;
                if (neg)
                {
                    if (v > 0x80000000L)
                        Error(2006, t);
                    v = -v;
                }
                return new Literal("INT", t.Pos, line) { IValue = v };
            }
            if (t.Kind == K.Real && typ == "REAL")
            {
                _i++;
                return new Literal("REAL", t.Pos, line) { RValue = neg ? -t.RValue : t.RValue };
            }
            throw Fail(2006, t);
        }

        // ----------------------------------------------------------------------- functions
        private FunctionDef ParseFunction(string? scope, string? typ, Token startTok)
        {
            _i++; // FUNCTION
            Token nt = Cur;
            if (nt.Kind == K.Eof)
            {
                Error(2034, nt);
                return new FunctionDef("", scope, typ, startTok.Pos, LineOf(startTok));
            }
            string name = nt.Kind == K.Name ? nt.SValue! : nt.Text;
            _i++;
            var fn = new FunctionDef(name, scope, typ, startTok.Pos, LineOf(startTok))
            {
                NamePos = nt.Pos,
                File = File,
                Project = Project,
            };
            if (name.Length > FunctionNameMax)
                Error(2109, nt);
            bool aborted = false;
            if (Cur.Kind == '(')
            {
                fn.HasParens = true;
                _phase1 = true; // header messages: the declaration pass
                int hdr = Cur.Pos;
                int nLex = Diags.Count;
                try
                {
                    ParseParams(fn);
                }
                catch (HeaderAbort)
                {
                    aborted = true;
                }
                _phase1 = false;
                int end = Cur.Pos;
                // lexer messages inside the header (a label used as a parameter name: fix_diagB n01
                // E2057 first) belong to the declaration pass too
                var moved = new List<Diagnostic>();
                for (int q = 0; q < nLex && q < Diags.Count; q++)
                {
                    var d = Diags[q];
                    if (hdr <= d.Pos && (aborted ? d.Pos <= end : d.Pos < end))
                        moved.Add(d);
                }
                if (moved.Count > 0)
                {
                    var keep = new List<Diagnostic>();
                    foreach (var d in Diags)
                        if (!moved.Contains(d))
                            keep.Add(d);
                    Diags = keep;
                    DeclDiags.InsertRange(0, moved);
                }
            }
            _fn = fn;
            _afterVoidReturn = false;
            _locals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in fn.Params)
                _locals.Add(Lexer.NameKey(p.Name));
            _phase1 = aborted;
            fn.HeaderAborted = aborted;
            try
            {
                // E2037 of a typed function is the resolver's; after a broken header there is none
                ParseBody(fn);
            }
            finally
            {
                _fn = null;
                _locals = new HashSet<string>(StringComparer.Ordinal);
                _phase1 = false;
            }
            return fn;
        }

        private void ParseParams(FunctionDef fn)
        {
            _i++; // '('
            if (Cur.Kind == ')')
            {
                _i++;
                return;
            }
            bool seenDefault = false;
            while (true)
            {
                Param p = ParseParam();
                if (p.Default != null)
                    seenDefault = true;
                else if (seenDefault)
                    DeclDiags.Add(new Diagnostic(1003, File, p.Line, 0, p.Name, Project));
                fn.Params.Add(p);
                if (fn.Params.Count > MaxParams)
                    Error(2025);
                Token t = Cur;
                if (t.Kind == ',')
                {
                    _i++;
                    if (Cur.Kind == ')') // a trailing comma (h38)
                    {
                        _i++;
                        return;
                    }
                    continue;
                }
                if (t.Kind == ')')
                {
                    _i++;
                    return;
                }
                // E2011 at the token, which is taken; the body starts after it (sem g03)
                Error(2011, t);
                if (t.Kind != K.Eof && t.Kind != K.END && t.Kind != K.FUNCTION)
                    _i++;
                throw new HeaderAbort();
            }
        }

        private Param ParseParam()
        {
            Token t = Cur;
            string? typ = null;
            if (K.IsTypeKeyword(t.Kind))
            {
                typ = KwName(t.Kind);
                _i++;
            }
            else
                Error(2015, t);
            Token nt = Cur;
            if (nt.Kind != K.Name || nt.SValue!.IndexOf('.') >= 0)
            {
                Error(2008, nt);
                throw new HeaderAbort();
            }
            _i++;
            if (typ == null)
            {
                // the parameter is still added, without a type: a second E2015 without a line,
                // context 'AddDefaultValue' (syntax h20, r2_rev_diagA r36)
                DeclDiags.Add(new Diagnostic(2015, File, 0, 0, "AddDefaultValue", Project));
            }
            Expr? dflt = null;
            if (Cur.Kind == '=')
            {
                _i++;
                try
                {
                    dflt = ParseExpr();
                }
                catch (ParseError)
                {
                    throw new HeaderAbort();
                }
            }
            return new Param(typ, nt.SValue!, dflt, nt.Pos, LineOf(nt));
        }

        private void ParseBody(FunctionDef fn)
        {
            var body = fn.Body;
            while (true)
            {
                Token t = Cur;
                int k = t.Kind;
                if (k == K.END)
                {
                    _i++;
                    fn.EndLine = LineOf(t);
                    fn.EndPos = t.Pos;
                    fn.AfterEndPos = Cur.Pos;
                    fn.Complete = true;
                    return;
                }
                if (k == K.Eof)
                {
                    Error(2034, t);
                    fn.EndPos = t.Pos;
                    fn.EndLine = LineOf(t);
                    return;
                }
                if (k == K.FUNCTION)
                {
                    // a missing END (h22): E2076, the FUNCTION is taken and the body goes on, so the
                    // name after it is a statement of this function (lexical l13)
                    Error(2076, t);
                    _i++;
                    continue;
                }
                int start = _i;
                try
                {
                    if (K.IsTypeKeyword(k))
                    {
                        var decls = ParseLocalDecl();
                        body.AddRange(decls);
                        foreach (var d in decls)
                            fn.Locals.Add((LocalDecl)d);
                    }
                    else
                    {
                        Stmt? s = ParseStatement();
                        if (s != null)
                            body.Add(s);
                    }
                }
                catch (ParseError e)
                {
                    Recover(e, start);
                }
                if (_i == start && !IsBlockEnd(Cur.Kind))
                    _i++; // never stay on a token (else '/ <= NOP' loops forever)
                if (Cur.Kind == ';' && _i > start)
                    _i++;
            }
        }

        /// <summary>TYPE name [= expr] {, name [= expr]}: the declaration ends at the
        /// first token that is not a declarator ('INT;', 'INT 5' declare nothing; the rest is
        /// parsed as statements); ', TYPE' starts a new declaration.</summary>
        private List<Stmt> ParseLocalDecl()
        {
            string typ = KwName(Cur.Kind);
            _i++;
            var output = new List<Stmt>();
            if (K.IsTypeKeyword(Cur.Kind))
                throw Fail(2041); // 'INT int;' (lexical kl_int / kl_real)
            while (true)
            {
                Token t = Cur;
                if (t.Kind != K.Name)
                    return output;
                string tv = t.SValue!;
                if (tv.IndexOf('.') >= 0 && t.Label == null)
                {
                    // 'INT a.b = 1' (lexical i11): 'a' is declared, E2041 at the '.', and the
                    // statement 'b = 1' follows (one error)
                    int k = t.Text.IndexOf('.');
                    string head = t.Text.Substring(0, k),
                        rest = t.Text.Substring(k + 1);
                    _locals.Add(Lexer.NameKey(head));
                    output.Add(new LocalDecl(typ, head, null, t.Pos, LineOf(t)));
                    var dot = new Token('.', ".", t.Pos + k, t.Pos + k + 1, true, null) { SValue = "." };
                    Error(2041, dot);
                    if (rest.Length > 0)
                        _toks[_i] = new Token(K.Name, rest, t.Pos + k + 1, t.End, true, null)
                        {
                            SValue = rest,
                        };
                    else
                        _i++;
                    return output;
                }
                if (tv.IndexOf('.') >= 0)
                    throw Fail(2041, t);
                _i++;
                if (Cur.Kind == '[') // no local arrays (d25)
                {
                    _locals.Add(Lexer.NameKey(tv)); // declared all the same (sem n09)
                    throw Fail(2052);
                }
                Expr? init = null;
                if (Cur.Kind == '=')
                {
                    _i++;
                    init = ParseExpr();
                }
                _locals.Add(Lexer.NameKey(tv));
                output.Add(new LocalDecl(typ, tv, init, t.Pos, LineOf(t)));
                if (Cur.Kind == ',')
                {
                    _i++;
                    if (K.IsTypeKeyword(Cur.Kind))
                    {
                        typ = KwName(Cur.Kind);
                        _i++;
                    }
                    continue;
                }
                return output;
            }
        }

        // ---------------------------------------------------------------------- statements
        /// <summary>Statements up to END, ELSE, CASE or the end of the input.</summary>
        public List<Stmt> ParseBlock(bool strictFirst = true)
        {
            Enter();
            try
            {
                return ParseBlockInner(strictFirst);
            }
            finally
            {
                _depth--;
            }
        }

        private List<Stmt> ParseBlockInner(bool strictFirst)
        {
            var output = new List<Stmt>();
            if (strictFirst && (Cur.Kind == K.ELSE || Cur.Kind == K.CASE))
            {
                Error(2041);
                _i++;
            }
            bool first = true;
            while (!IsBlockEnd(Cur.Kind))
            {
                if (Cur.Kind == K.FUNCTION && _fn != null && !first)
                {
                    // inside a nested block: E2076, the FUNCTION is taken and the block ends; the
                    // statement around it reports its END at the next token (r2_grammar nest2). As
                    // the block's first statement it is a stray token like ELSE / CASE there (h24)
                    Error(2076);
                    _i++;
                    _blockBroken = true;
                    return output;
                }
                first = false;
                int start = _i;
                try
                {
                    Stmt? s = ParseStatement();
                    if (s != null)
                        output.Add(s);
                }
                catch (ParseError e)
                {
                    Recover(e, start);
                }
                if (_i == start && !IsBlockEnd(Cur.Kind))
                    _i++; // never stay on a token
                if (Cur.Kind == ';' && _i > start)
                    _i++;
            }
            return output;
        }

        private Stmt? ParseStatement()
        {
            Token t = Cur;
            int k = t.Kind;
            bool afterRet = _afterVoidReturn;
            _afterVoidReturn = false;
            if (k == K.IF)
                return ParseIf();
            if (k == K.WHILE)
                return ParseWhile();
            if (k == K.FOR)
                return ParseFor();
            if (k == K.SELECT)
                return ParseSelect();
            if (k == K.RETURN)
                return ParseReturn();
            if (k == ';')
            {
                _i++;
                return new Empty(t.Pos, LineOf(t));
            }
            Stmt? s = ParseSimple();
            if (afterRet && s is ExprStmt es)
                es.AfterVoidReturn = true;
            return s;
        }

        /// <summary>The statement at the current NAME is an assignment: the name, its [...]
        /// indexes, then '='.</summary>
        private bool AssignAhead()
        {
            var toks = _toks;
            int j = _i + 1;
            int n = toks.Count;
            while (j < n && toks[j].Kind == '[')
            {
                int depth = 0;
                while (j < n)
                {
                    int kk = toks[j].Kind;
                    if (kk == '[')
                        depth++;
                    else if (kk == ']')
                    {
                        depth--;
                        if (depth == 0)
                            break;
                    }
                    else if (kk == K.Eof)
                        return false;
                    j++;
                }
                j++;
            }
            return j < n && toks[j].Kind == '=';
        }

        private Stmt? ParseSimple()
        {
            Token t = Cur;
            int line = LineOf(t);
            if (t.Kind == K.Sg && _page && Peek().Kind == '=')
            {
                // an association as the target of a page command ('?Tag?.Status.V = 0')
                Assoc target = MakeAssoc(t);
                _i += 2;
                return new Assign(target, ParseExpr(), t.Pos, line);
            }
            if (t.Kind == K.Name && AssignAhead())
            {
                Expr? target = ParseLvalue();
                if (target == null)
                    return null;
                _i++; // '='
                Expr value = ParseExpr();
                return new Assign(target, value, t.Pos, line);
            }
            Expr expr = ParseExpr();
            return new ExprStmt(expr, t.Pos, line) { EndPos = Cur.Pos, NameFirst = t.Kind == K.Name };
        }

        /// <summary>The assignment target; null after an index on a scalar local (E2041 at the '[',
        /// the statement ends there: sem n09 'aLocal[0] = 1').</summary>
        private Expr? ParseLvalue()
        {
            Token t = Cur;
            _i++;
            int line = LineOf(t);
            var b = new Name(t.SValue!, t.Pos, line);
            if (Cur.Kind == '[')
            {
                if (ScalarLocal(t.SValue!))
                {
                    Error(2041);
                    _i++;
                    return null;
                }
                return new Index(b, ParseIndices(), t.Pos, line);
            }
            return b;
        }

        /// <summary>A local or parameter (never an array) or a scalar module variable declared above.</summary>
        private bool ScalarLocal(string name)
        {
            string k = Lexer.NameKey(name);
            return _locals.Contains(k) || (_moduleVars.Contains(k) && !_moduleArrays.Contains(k));
        }

        private Stmt ParseIf()
        {
            Token t = Cur;
            _i++;
            Expr cond = ParseExpr();
            ExpectKw(K.THEN, 2032);
            var node = new If(cond, new List<Stmt>(), null, t.Pos, LineOf(t));
            node.Then = ParseBlock();
            if (Cur.Kind == K.ELSE)
            {
                node.ElseLine = LineOf(Cur);
                _i++;
                node.Else = ParseBlock();
            }
            Token end = Cur;
            ExpectKw(K.END, 2034);
            node.EndLine = LineOf(end);
            return node;
        }

        private Stmt ParseWhile()
        {
            Token t = Cur;
            _i++;
            Expr cond = ParseExpr();
            ExpectKw(K.DO, 2033);
            var node = new While(cond, new List<Stmt>(), t.Pos, LineOf(t));
            node.Body = ParseBlock();
            Token end = Cur;
            ExpectKw(K.END, 2034);
            node.EndLine = LineOf(end);
            return node;
        }

        /// <summary>FOR: FOR + a non-name is ignored (m63); 'FOR i = 5;' without TO
        /// is an assignment (m62); the variable is checked by the resolver (E2001 / E2027).</summary>
        private Stmt? ParseFor()
        {
            Token t = Cur;
            _i++;
            int line = LineOf(t);
            Token vt = Cur;
            if (vt.Kind != K.Name)
                return null;
            _i++;
            var v = new Name(vt.SValue!, vt.Pos, LineOf(vt)) { End = Cur.Pos, EndTok = _i };
            string k = Lexer.NameKey(vt.SValue!);
            if (vt.SValue!.IndexOf('.') >= 0 || !(_locals.Contains(k) || _moduleVars.Contains(k)))
            {
                // the loop variable must be a local, a parameter or a MODULE variable declared
                // above: E2001 at the next token, the FOR is dropped (syntax b17 / b19 / m61)
                throw Fail(2001);
            }
            if (Cur.Kind != '=')
                return new For(v, null, null, null, false, t.Pos, line);
            _i++;
            Expr start = ParseExpr();
            if (Cur.Kind != K.TO)
                return new For(v, start, null, null, false, t.Pos, line);
            _i++;
            Expr stop = ParseExpr();
            ExpectKw(K.DO, 2033);
            var node = new For(v, start, stop, null, true, t.Pos, line);
            node.Body = ParseBlock();
            Token end = Cur;
            ExpectKw(K.END, 2034);
            node.EndLine = LineOf(end);
            return node;
        }

        private Stmt ParseSelect()
        {
            Token t = Cur;
            _i++;
            ExpectKw(K.CASE, 2065);
            Expr sel;
            if (Cur.Kind == K.CASE)
            {
                // no selector: E2041 at the CASE, which is taken in its place, so the clauses are
                // missing and END is expected (syntax s16)
                Token ct = Cur;
                Error(2041, ct);
                _i++;
                sel = Placeholder(ct);
                sel.End = Cur.Pos;
                sel.EndTok = _i;
            }
            else
            {
                if (Cur.Kind == K.END || Cur.Kind == K.ELSE || Cur.Kind == K.Eof || Cur.Kind == ';')
                    throw Fail(2041, null, Cur.Kind == ';');
                sel = ParseExpr();
            }
            var node = new Select(sel, t.Pos, LineOf(t));
            // the clauses follow the selector directly
            while (Cur.Kind == K.CASE)
            {
                Token ct = Cur;
                _i++;
                if (Cur.Kind == K.ELSE)
                {
                    if (node.Else != null)
                        throw Fail(2034, ct); // a second CASE ELSE (s19)
                    node.ElseLine = LineOf(ct);
                    _i++;
                    node.Else = ParseBlock();
                    continue;
                }
                if (node.Else != null)
                    throw Fail(2034, ct); // CASE after CASE ELSE (s04)
                var items = new List<CaseItem> { ParseCaseItem() };
                while (Cur.Kind == ',')
                {
                    _i++;
                    items.Add(ParseCaseItem());
                }
                if (Cur.Kind == K.DO)
                    _i++; // optional DO (s05)
                var c = new Case(items, new List<Stmt>(), ct.Pos, LineOf(ct));
                c.Body = ParseBlock();
                node.Cases.Add(c);
            }
            Token end = Cur;
            ExpectKw(K.END, 2034);
            node.EndLine = LineOf(end);
            ExpectKw(K.SELECT, 2034); // a plain END does not close (s02)
            return node;
        }

        private CaseItem ParseCaseItem()
        {
            Token t = Cur;
            int line = LineOf(t);
            if (t.Kind == K.IS)
            {
                _i++;
                Token op = Cur;
                if (!IsRelop(op.Kind))
                {
                    // IS without a relational operator: E2024 there, the item goes on (syntax s07
                    // 'IS + 1', s17 'IS 5': E2024 only)
                    Error(2024, op);
                    Expr e0 = ParseExpr();
                    return new CaseItem("is", e0, null, "=", t.Pos, line);
                }
                _i++;
                Expr e = ParseExpr();
                return new CaseItem("is", e, null, K.Text(op.Kind), t.Pos, line);
            }
            Expr lo = ParseExpr();
            if (Cur.Kind == K.TO)
            {
                _i++;
                Expr hi = ParseExpr();
                return new CaseItem("range", lo, hi, "", t.Pos, line);
            }
            return new CaseItem("value", lo, null, "", t.Pos, line);
        }

        private Stmt ParseReturn()
        {
            Token t = Cur;
            _i++;
            int line = LineOf(t);
            FunctionDef? fn = _fn;
            if (fn == null || fn.RetType == null)
            {
                _afterVoidReturn = true;
                return new Return(null, t.Pos, line) { NextLine = LineOf(Cur) };
            }
            fn.ReturnsValue = true;
            Expr value;
            try
            {
                value = ParseExpr();
            }
            catch (ParseError)
            {
                // a RETURN whose value has a syntax error still counts as returning a value: no
                // E2037 at END (fix_grammar g_ch_80 .. g_ch_A7)
                fn.BrokenReturns++;
                throw;
            }
            return new Return(value, t.Pos, line);
        }

        // --------------------------------------------------------------------- expressions
        public Expr ParseExpr(int minBp = 1)
        {
            Expr left = ParseUnary();
            var toks = _toks;
            while (true)
            {
                Token t = toks[_i];
                int k = t.Kind;
                int bp = Bp(k);
                if (bp == 0)
                {
                    if (k == '^' && _page)
                    {
                        // the range of a page value, at the precedence of ':' (Range)
                        if (Bp(':') < minBp)
                            return left;
                        _i++;
                        left = ParseRange(left, t);
                        left.End = Cur.Pos;
                        left.EndTok = _i;
                        continue;
                    }
                    if (k == '^')
                    {
                        // no power operator: E2020 at it, it is taken and the expression ends
                        // (lexical o10; r2_diagB L38 '2 ^ 3' then E2011 at the 3)
                        Error(2020, t);
                        _i++;
                        return left;
                    }
                    if ((k == '&' || k == '%') && minBp <= 1)
                    {
                        // a rejected operator: E2041 at it, the operand after it is taken without
                        // a second message (lexical o06, o07, o08)
                        Error(2041, t);
                        _i++;
                        _quietAt = _i;
                        ParseUnary();
                        left = Placeholder(t);
                        left.End = Cur.Pos;
                        left.EndTok = _i;
                        continue;
                    }
                    return left;
                }
                if (bp < minBp)
                    return left;
                if (left is ErrorOperand && left.EndTok == _i)
                {
                    // an operator right after an error placeholder: its missing operand is not
                    // reported again (lexical c09 '*/' then RETURN)
                    _quietAt = _i + 1;
                }
                _i++;
                if (k == ':')
                {
                    FormatSpec spec = ParseFormatSpec();
                    left = new Format(left, spec, t.Pos, left.Line) { End = Cur.Pos, EndTok = _i };
                    continue;
                }
                Expr right = ParseExpr(bp + 1);
                left = new Binary(K.Text(k), left, right, t.Pos, left.Line) { End = Cur.Pos, EndTok = _i };
            }
        }

        private void Enter()
        {
            _depth++;
            if (_depth > MaxNesting)
            {
                Error(4008);
                throw new TooDeep();
            }
        }

        private Expr ParseUnary()
        {
            Enter();
            Expr e;
            try
            {
                e = ParseUnaryInner();
            }
            finally
            {
                _depth--;
            }
            e.End = Cur.Pos;
            e.EndTok = _i;
            return e;
        }

        private Expr ParseUnaryInner()
        {
            Token t = Cur;
            int k = t.Kind;
            if (k == K.NOT)
            {
                _i++;
                return new Unary("NOT", ParseUnary(), t.Pos, LineOf(t));
            }
            if (k == '-')
            {
                _i++;
                Expr operand = ParseUnary();
                if (operand is ErrorOperand)
                    return operand;
                if (operand is Num num)
                {
                    if (num.IsInt)
                    {
                        if (num.IValue > 0x80000000L)
                        {
                            // below INT_MIN: E2006 at the number (n27, n28), and the token after it
                            // is passed over (r2_diagB s20 '(-2147483649);' then E2011 at the ';')
                            Token nt = _toks[_i - 1];
                            Error(2006, nt);
                            if (!IsBlockEnd(Cur.Kind) && Cur.Kind != K.FUNCTION)
                                _i++;
                        }
                        return new Num(-num.IValue, "-" + num.Text, t.Pos, LineOf(t));
                    }
                    return new Num(-num.RValue, "-" + num.Text, t.Pos, LineOf(t));
                }
                return new Unary("-", operand, t.Pos, LineOf(t));
            }
            if (k == '+')
            {
                // ONE '+' before an operand is passed over:
                // '+5' and '1 + +2' are 5 and 3, but a second '+' is no operand ('++5' E2041 at
                // it, lexical o18); a '-' or NOT after it is an operand (not probed)
                _i++;
                if (Cur.Kind == '+')
                    return ParsePrimary();
                return ParseUnary();
            }
            return ParsePrimary();
        }

        private Expr ParsePrimary()
        {
            Token t = Cur;
            int k = t.Kind;
            if (k == K.Name)
            {
                _i++;
                int line = LineOf(t);
                int nk = Cur.Kind;
                string tv = t.SValue!;
                if (nk == '(')
                {
                    if (!IsVar(tv))
                    {
                        string key = Lexer.NameKey(tv);
                        CallNames.Add(key);
                        Callbacks.TryGetValue(key, out var cb);
                        var call = new Call(tv, ParseArgs(cb), t.Pos, line) { RParen = _rparen };
                        return call;
                    }
                    return new Name(tv, t.Pos, line); // the variable; '(...)' starts anew
                }
                var b = new Name(tv, t.Pos, line);
                if (nk == '[')
                {
                    if (ScalarLocal(tv))
                    {
                        // an index on a scalar: E2041 at the '[', which is taken; the rest is new
                        // statements (lexical o27 'i[0]': E2041 at '[' and at ']')
                        Error(2041);
                        _i++;
                        return b;
                    }
                    return new Index(b, ParseIndices(), t.Pos, line);
                }
                return b;
            }
            if (k == K.Int)
            {
                _i++;
                return new Num(t.IValue, t.Text, t.Pos, LineOf(t));
            }
            if (k == K.Str)
            {
                _i++;
                return new Str(t.SValue!, t.Pos, LineOf(t));
            }
            if (k == '(')
            {
                _i++;
                Expr e = ParseExpr();
                if (Cur.Kind != ')')
                    throw Fail(2011);
                _i++;
                e.Paren = true;
                return e;
            }
            if (k == K.Real)
            {
                _i++;
                return new Num(t.RValue, t.Text, t.Pos, LineOf(t));
            }
            if (k == K.Intr)
            {
                _i++;
                var v = t.Intr!;
                return new Intrinsic(v.Code, v.CodeDigits, v.Label, v.Args, t.Pos, LineOf(t));
            }
            if (k == K.Sg)
            {
                _i++;
                if (!_page)
                {
                    Error(2061, t);
                    return Placeholder(t);
                }
                return MakeAssoc(t);
            }
            if (k == K.Genie)
            {
                _i++;
                return new GenieParam(t.SValue!, t.Pos, LineOf(t));
            }
            if (k == K.ELSE || k == K.CASE || k == K.Eof)
                throw Fail(2041, t, false);
            // no operand here: the compiler reports the token, takes it and goes on with a number
            // in its place, so the statement continues ('IF NOP THEN' = E2041 only, r2_diagB L31;
            // 'IF THEN' E2041 + E2032, s08; a failed label call E2057 + E2041, sem k16; 'RETURN' +
            // 'END' E2041 at END and E2034 at the end of the file, fix_diagA t01). Right after an
            // error placeholder and an operator the message is not repeated (lexical c09).
            bool isKw = K.IsKeyword(k);
            if (_i == _quietAt)
            {
                _quietAt = -1; // the next operand only, once
                if (isKw)
                {
                    // a keyword there is left for the statement parser (builtins_editor f03: '*/'
                    // then END closes the function)
                    return Placeholder(t);
                }
            }
            else
                Error(k == K.CIVBA ? 2074 : 2041, t);
            _i++;
            if (isKw && Cur.Kind == '(')
            {
                // a keyword called like a function ('nop()', lexical kx_funcname_nop_call): the
                // bracketed arguments go with it, one E2041
                ParseArgs(null);
            }
            else if (Cur.Kind == '[')
            {
                // a '[' right after it is passed over (fix_diagB n04 'INT a[2][3];': E2052, then
                // E2041 at the two ']' only)
                _i++;
            }
            return Placeholder(t);
        }

        private Assoc MakeAssoc(Token t)
        {
            int k = t.Text.LastIndexOf('?');
            return new Assoc(t.SValue!, t.Pos, LineOf(t), k >= 0 ? t.Text.Substring(k + 1) : "");
        }

        /// <summary>'^ ( [sign] number , [sign] number )' after a page value (Range). Anything else
        /// is E2020 at the token found (not probed).</summary>
        private Expr ParseRange(Expr left, Token caret)
        {
            if (Cur.Kind != '(')
                throw Fail(2020, null, false);
            _i++;
            var bounds = new List<Num>();
            foreach (int closer in new[] { ',', ')' })
            {
                Expr num = ParseUnary();
                if (!(num is Num nn) || num is ErrorOperand)
                    throw Fail(2020, null, false);
                bounds.Add(nn);
                if (Cur.Kind != closer)
                    throw Fail(closer == ')' ? 2011 : 2020, null, false);
                _i++;
            }
            return new Range(left, bounds[0], bounds[1], caret.Pos, left.Line);
        }

        private Expr Placeholder(Token t) => new ErrorOperand(t.Pos, LineOf(t));

        /// <summary>The arguments after '('.</summary>
        private List<Expr> ParseArgs(HashSet<int>? callbacks)
        {
            _i++; // '('
            var args = new List<Expr>();
            if (Cur.Kind == ')')
            {
                _rparen = Cur.Pos;
                _i++;
                return args;
            }
            while (true)
            {
                if (callbacks != null && callbacks.Count > 0 && callbacks.Contains(args.Count))
                    args.Add(CallbackArg());
                else
                {
                    Expr arg = ParseExpr();
                    args.Add(arg);
                    if (Cur.Kind == K.Str && arg is ErrorOperand && _i > 0 && _toks[_i - 1].Kind == ',')
                    {
                        // an empty argument followed by a string: the string goes with the error
                        // placeholder and the list goes on ('ZzUn2(1, , "x")' = E2041 only,
                        // r2_rev_diagA ext r15; sem k22), while a number there is E2011 (sem d15)
                        _i++;
                    }
                }
                Token t = Cur;
                if (t.Kind == ',')
                {
                    _i++;
                    if (Cur.Kind == ')')
                    {
                        _rparen = Cur.Pos;
                        _i++;
                        return args;
                    }
                    continue;
                }
                if (t.Kind == ')')
                {
                    _rparen = t.Pos;
                    _i++;
                    return args;
                }
                if (IsBlockEnd(t.Kind))
                    throw Fail(2011, t, false);
                Error(2011, t);
                _i++;
                _rparen = -1;
                return args;
            }
        }

        /// <summary>A FUNCTION argument: a name (or a number) is taken as one token; any other
        /// token is E2008 there and taken (r2_diagB b09 '(ZCb)', x09 a failed label call).</summary>
        private Expr CallbackArg()
        {
            Token t = Cur;
            int line = LineOf(t);
            Expr e;
            if (t.Kind == K.Name)
            {
                _i++;
                e = new Name(t.SValue!, t.Pos, line);
            }
            else if (t.Kind == K.Int)
            {
                _i++;
                e = new Num(t.IValue, t.Text, t.Pos, line);
            }
            else if (t.Kind == K.Real)
            {
                _i++;
                e = new Num(t.RValue, t.Text, t.Pos, line);
            }
            else if (t.Kind == ',' || t.Kind == ')' || IsBlockEnd(t.Kind))
                return ParseExpr();
            else
            {
                Error(2008, t);
                _i++;
                e = Placeholder(t);
            }
            e.End = Cur.Pos;
            e.EndTok = _i;
            return e;
        }

        private List<Expr> ParseIndices()
        {
            var output = new List<Expr>();
            while (Cur.Kind == '[')
            {
                _i++;
                output.Add(ParseExpr());
                if (Cur.Kind != ']')
                    throw Fail(2011);
                _i++;
            }
            return output;
        }

        /// <summary>The format after ':': a '#' picture, a width n / n.m ('-' =
        /// left justified) or '~'; anything else E2035.</summary>
        private FormatSpec ParseFormatSpec()
        {
            Token t = Cur;
            int line = LineOf(t);
            if (t.Kind == K.Pic)
            {
                _i++;
                if (t.Label != null) // a label giving a picture (fix_providers c01)
                {
                    Error(2035, t);
                    return new FormatSpec("bad", t.Text, t.Pos, line);
                }
                return PictureSpec(t.SValue!, t.Pos, line);
            }
            bool left = false;
            if (t.Kind == '-' && (Peek().Kind == K.Int || Peek().Kind == K.Real))
            {
                left = true;
                _i++;
                t = Cur;
            }
            if (t.Kind == K.Int || t.Kind == K.Real)
            {
                _i++;
                var spec = new FormatSpec("width", (left ? "-" : "") + t.Text, t.Pos, line) { Left = left };
                if (t.Kind == K.Int)
                    spec.Width = t.IValue;
                else
                {
                    // the compiler reads the real: width = its integer part, decimals =
                    // ((int)(x * 10 + 0.5)) % 10: '3.25' is
                    // 3 decimals, '3.99' none
                    double v = t.RValue;
                    spec.Width = TruncBig(v);
                    spec.Decimals = BigInteger.Remainder(TruncBig(v * 10 + 0.5), 10);
                }
                return spec;
            }
            if (t.Kind == '~')
            {
                _i++;
                return new FormatSpec("tilde", "~", t.Pos, line);
            }
            // not a format: E2035 at the token, which is taken (a ';' or a block keyword stays), and
            // the expression goes on ('done:' + 'a = 1' is E2035 at a only, syntax m20)
            Error(2035, t);
            if (!IsBlockEnd(t.Kind) && t.Kind != ';')
                _i++;
            return new FormatSpec("bad", t.Text, t.Pos, line);
        }

        // a real truncated toward zero to an exact integer (NaN or an infinity: 0)
        private static BigInteger TruncBig(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return BigInteger.Zero;
            return new BigInteger(Math.Truncate(v));
        }

        // -------------------------------------------------------------------------- fields
        /// <summary>The optional language prefix of a field (CICODE or CiVBA).</summary>
        private CiVBA? LanguagePrefix()
        {
            int j = _i + (Cur.Kind == '(' ? 1 : 0);
            Token t = j < _toks.Count ? _toks[j] : _toks[_toks.Count - 1];
            if (t.Kind == K.CIVBA)
            {
                _i = _toks.Count - 1;
                string rest = t.End <= _lex.Text.Length ? _lex.Text.Substring(t.End) : "";
                return new CiVBA(CitectText.Trim(rest), t.Pos, LineOf(t));
            }
            if (t.Kind == K.CICODE)
                _i = j + 1;
            return null;
        }

        /// <summary>An expression field of a page: one expression (NOP alone = nothing).</summary>
        public Expr? ParseExpressionField()
        {
            if (Cur.Kind == K.Eof || (Cur.Kind == K.NOP && Peek().Kind == K.Eof))
            {
                // nothing but a comment ('!') or NOP
                return null;
            }
            CiVBA? vba = LanguagePrefix();
            if (vba != null)
                return vba;
            Expr e;
            try
            {
                e = ParseExpr();
            }
            catch (ParseError)
            {
                return null;
            }
            catch (TooDeep)
            {
                return null;
            }
            if (Cur.Kind != K.Eof)
                Error(2091);
            return e;
        }

        /// <summary>Module variables declared outside the text (a report's variables, declared
        /// in its first {CICODE} block): its statements read them as declared above.</summary>
        public void AddModuleVars(IEnumerable<VarRef> vars)
        {
            foreach (var v in vars)
            {
                string k = Lexer.NameKey(v.Name);
                _moduleVars.Add(k);
                if (v.Dims.Count > 0)
                    _moduleArrays.Add(k);
            }
        }

        /// <summary>A report's first {CICODE} block (help 1094538): variable declarations at its
        /// start (the file variables' syntax; a scope keyword is read and left), added to
        /// <paramref name="decls"/>, then statements as a command field.</summary>
        public List<Stmt> ParseReportBlock(List<VarDecl> decls)
        {
            while (K.IsScopeKeyword(Cur.Kind) || K.IsTypeKeyword(Cur.Kind))
            {
                int start = _i;
                try
                {
                    foreach (var n in ParseItem())
                    {
                        if (n is VarDecl v)
                        {
                            decls.Add(v);
                            _moduleVars.Add(Lexer.NameKey(v.Name));
                            if (v.Dims.Count > 0)
                                _moduleArrays.Add(Lexer.NameKey(v.Name));
                        }
                        else
                            Error(2031, _toks[start]); // a FUNCTION
                    }
                }
                catch (ParseError e)
                {
                    Recover(e, start);
                    while (Cur.Kind != ';' && Cur.Kind != K.Eof && !IsItemStart(Cur.Kind))
                        _i++;
                    if (Cur.Kind == ';')
                        _i++;
                }
                catch (TooDeep)
                {
                    return new List<Stmt>();
                }
                if (_i == start)
                    _i++;
            }
            return ParseCommandField();
        }

        /// <summary>A command field: statements, no declarations.</summary>
        public List<Stmt> ParseCommandField()
        {
            CiVBA? vba = LanguagePrefix();
            if (vba != null)
                return new List<Stmt> { new ExprStmt(vba, vba.Pos, vba.Line) { EndPos = _lex.Text.Length } };
            var output = new List<Stmt>();
            try
            {
                output.AddRange(ParseBlock());
                while (Cur.Kind != K.Eof)
                {
                    int start = _i;
                    try
                    {
                        throw Fail(2041);
                    }
                    catch (ParseError e)
                    {
                        Recover(e, start);
                    }
                    output.AddRange(ParseBlock());
                }
            }
            catch (TooDeep)
            {
                // F4008 reported: the rest of the field is not parsed
            }
            return output;
        }

        /// <summary>A '#' picture read as DOC 1064774 describes it: '#' / '0' / '-'
        /// count width, a '0' right after the first '#' pads with zeros, a '-' there left
        /// justifies, '.' + '#'s are decimals, 's' exponential, 'EU' / 'engunit' units.</summary>
        public static FormatSpec PictureSpec(string text, int pos = 0, int line = 0)
        {
            var spec = new FormatSpec("picture", text, pos, line);
            string body = text;
            string low = CitectText.Lower(text);
            int k = low.IndexOf("engunit", StringComparison.Ordinal);
            if (k < 0)
                k = low.IndexOf("eu", StringComparison.Ordinal);
            if (k >= 0)
            {
                spec.Eu = true;
                body = CitectText.TrimEnd(text.Substring(0, k));
            }
            int width = 0;
            int i = 0;
            int n = body.Length;
            while (i < n && (body[i] == '#' || body[i] == '0' || body[i] == '-'))
            {
                width++;
                i++;
            }
            string lead = i > 1 ? body.Substring(1, i - 1) : "";
            spec.ZeroPad = lead.IndexOf('0') >= 0;
            spec.Left = lead.IndexOf('-') >= 0;
            if (i < n && body[i] == '!')
                spec.Eu = true;
            if (i < n && (body[i] == '.' || body[i] == 's'))
            {
                if (body[i] == 's')
                    spec.Exponent = true;
                i++;
                int d = 0;
                while (i < n && body[i] == '#')
                {
                    d++;
                    i++;
                }
                spec.Decimals = d;
                width += d + 1;
            }
            spec.Width = width;
            return spec;
        }

        /// <summary>Lex and parse one decoded source text.</summary>
        public static Module ParseSource(
            string text,
            LabelTable? labels = null,
            string file = "",
            string project = "",
            string mode = "ci"
        )
        {
            var lex = Lexer.Tokenize(text, labels, file, mode, project);
            return new Parser(lex, file, project, mode).ParseModule();
        }
    }
}
