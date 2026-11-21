using System;
using System.Collections.Generic;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;
using Index = CicodeSim.Compiler.Syntax.Index;
using Range = CicodeSim.Compiler.Syntax.Range;

namespace CicodeSim.Compiler.Semantics
{
    /// <summary>The body pass of one file: name resolution, call binding and type checks.</summary>
    public sealed class Resolver
    {
        private const int ANY = Ast.Any;
        private const int INT = Types.Long;
        private const int REAL = Types.Real;
        private const int STRING = Types.String;
        private const int OBJECT = Types.Object;
        private const int TIMESTAMP = Types.Timestamp;
        private const int QUALITY = Types.Quality;
        private const int VOID = Types.Void;

        private static bool IsNum(int t) => t == INT || t == REAL;

        private static bool IsStrict(int t) => t == OBJECT || t == TIMESTAMP || t == QUALITY;

        /// <summary>The resolver's messages the compiler does not report in a file with an
        /// earlier-pass error: tag not defined, possible missing operand, value of a void function,
        /// unknown function and missing RETURN value.</summary>
        public static readonly HashSet<int> LateCodes = new HashSet<int> { 1007, 1021, 1023, 2031, 2037 };

        /// <summary>Resolver errors that behave like syntax errors: a file with one reports none of
        /// its LateCodes (fix_builtins review v02).</summary>
        public static readonly HashSet<int> EarlyErrors = new HashSet<int>
        {
            2001,
            2008,
            2019,
            2022,
            2027,
            2052,
            2083,
        };

        private sealed class Scope
        {
            public readonly Dictionary<string, VarRef> Vars = new Dictionary<string, VarRef>(
                StringComparer.Ordinal
            );
            public int Slot;
        }

        private readonly CompileUnit _unit;
        private readonly SourceFile _f;
        private readonly int _index;
        private readonly Dictionary<string, VarRef> _modVisible = new Dictionary<string, VarRef>(
            StringComparer.Ordinal
        );
        private Scope? _scope;
        private FunctionDef? _fn;
        private readonly List<Diagnostic> _out;
        private readonly LineIndex? _lines;
        private int _valueReturns;
        private readonly List<int> _syntaxErrs;
        private readonly bool _fileBad;
        private readonly int _declStop;
        private readonly HashSet<(int, int, int)> _seen = new HashSet<(int, int, int)>();
        private readonly int _stop;

        internal bool FileBad => _fileBad;

        public Resolver(CompileUnit unit, SourceFile f, int index, bool fileBad = false)
        {
            _unit = unit;
            _f = f;
            _index = index;
            _out = f.Pass2;
            _lines = f.Module.Lines;
            // the parser's and lexer's errors, header ones included (lexical kx_param_while)
            _syntaxErrs = new List<int>();
            bool anyErr = false;
            foreach (var d in f.Module.Diagnostics)
            {
                if (d.IsError)
                {
                    anyErr = true;
                    if (d.Pos >= 0)
                        _syntaxErrs.Add(d.Pos);
                }
            }
            foreach (var d in f.Module.DeclDiagnostics)
            {
                if (d.IsError)
                {
                    anyErr = true;
                    if (d.Pos >= 0)
                        _syntaxErrs.Add(d.Pos);
                }
            }
            _syntaxErrs.Sort();
            // a file with a lexer or parser error: the compiler reports none of its W1007, W1021,
            // W1023, unknown-function E2031 or E2037, before the error as well as after it
            _fileBad = fileBad || anyErr;
            // the declaration pass's own errors of the file (E2021 ...): the messages above stay,
            // none of the late ones after them (fix_diagA s10; rev_providers n02 / n05; inc q7_10)
            int declStop = -1;
            foreach (var d in f.Pass1)
            {
                if (d.IsError && d.Pos >= 0 && !f.Module.DeclDiagnostics.Contains(d))
                    if (declStop < 0 || d.Pos < declStop)
                        declStop = d.Pos;
            }
            _declStop = declStop;
            _stop = f.Module.StopPos;
        }

        /// <summary>A page field: an empty local scope.</summary>
        public void BeginField() => _scope = new Scope();

        private void Err(int code, Node? node, string ctx = "") =>
            ErrAt(code, node != null ? node.Pos : 0, ctx);

        /// <summary>The code at the token after the expression: where the compiler reports a type
        /// or arity error, having read the whole operand (E2024 '"a" - "b"{;}' lexical o15, E2027
        /// 'IF s {THEN}', E2019 'ProbeLen{;}', E2022 'AlarmAckRec(){;}').</summary>
        public void ErrEnd(int code, Expr node, string ctx = "")
        {
            if (node.End >= 0)
                ErrAt(code, node.End, ctx, node.EndTok);
            else
                ErrAt(code, node.Pos, ctx);
        }

        private void ErrAt(int code, int pos, string ctx = "", int seq = -1)
        {
            // the compiler never reports one code twice at the same token
            if (0 <= _stop && _stop < pos)
                return; // after the file's first file-scope E2031
            if (LateCodes.Contains(code) && 0 <= _declStop && _declStop < pos)
                return;
            if (seq >= 0 && !_seen.Add((code, pos, seq)))
                return;
            var (line, col) = _lines != null ? _lines.LineCol(pos) : (0, 0);
            _out.Add(new Diagnostic(code, _f.Path, line, col, ctx, _f.Project.Name, pos, seq));
        }

        public void Run()
        {
            var items = _f.Module.Items;
            for (int k = 0; k < items.Count; k++)
            {
                var it = items[k];
                if (it is VarDecl v)
                {
                    if (!v.IsGlobal && v.Var != null)
                        _modVisible[Lexer.NameKey(v.Name)] = v.Var;
                    continue;
                }
                if (it is FunctionDef fn)
                    Function(fn);
            }
        }

        // ---------------------------------------------------------------------------- scopes
        private VarRef? LookupVar(string key)
        {
            if (_scope != null && _scope.Vars.TryGetValue(key, out var v))
                return v;
            if (_modVisible.TryGetValue(key, out v))
                return v;
            if (_unit.Globals.TryGetValue(key, out v))
                return v;
            return _unit.Locvars.TryGetValue(key, out v) ? v : null;
        }

        private void Function(FunctionDef fn)
        {
            _fn = fn;
            var sc = _scope = new Scope();
            foreach (var p in fn.Params)
            {
                string k = Lexer.NameKey(p.Name);
                if (sc.Vars.ContainsKey(k))
                    Err(2021, p, p.Name);
                int t = Ast.TypeCodeOr(p.Type ?? "", INT);
                sc.Vars[k] = new VarRef("param", p.Name, t, sc.Slot, p);
                sc.Slot++;
            }
            foreach (var p in fn.Params)
                if (p.Default != null)
                    Expression(p.Default);
            _valueReturns = 0;
            Block(fn.Body);
            if (
                fn.RetType != null
                && fn.Complete
                && !fn.HeaderAborted
                && _valueReturns == 0
                && fn.BrokenReturns == 0
                && !_fileBad
            )
            {
                // a typed function needs a RETURN with a value somewhere (syntax h30), reported at
                // the token after its END (inc q1_00); not after a syntax error in the function
                int at = fn.AfterEndPos != 0 ? fn.AfterEndPos : fn.EndPos;
                var (line, col) = _lines != null ? _lines.LineCol(at) : (0, 0);
                _out.Add(new Diagnostic(2037, _f.Path, line, col, "END", _f.Project.Name, at));
            }
            _scope = null;
            _fn = null;
        }

        // ------------------------------------------------------------------------ statements
        public void Block(IList<Stmt> stmts)
        {
            foreach (var s in stmts)
                Statement(s);
        }

        private void Statement(Stmt s)
        {
            if (s is ExprStmt es)
            {
                if (es.Expr is CiVBA)
                    return; // a CiVBA command field is a call (VbCallReturn(...)): no W1021
                if (es.Expr is Intrinsic intr && CompileUnit.IfdefCodes.Contains(intr.Code))
                {
                    // IFDEF(tag, a, b) is compiled as the text it picks: a call there leaves no value
                    // (Example EquipObject buttons), anything else W1021 ('IFDEF(1, 1,
                    // 1);', builtins_editor g ok_13). The picked text itself is not checked here.
                    Expression(es.Expr, true);
                    if (!_fileBad && !IsCallText(IfdefPick(intr)))
                        ErrAt(1021, es.EndPos);
                    return;
                }
                int t = Expression(es.Expr, true);
                Expr ex = es.Expr;
                int code = 0;
                if (ex is Call call)
                {
                    var r = call.Ref;
                    if (r != null && r.Func == null && r.Builtin == null)
                        code = 1021; // an unknown call (sem c01)
                }
                else if (!es.NameFirst && CallLed(ex))
                {
                    // a statement that does not start with a name and whose first operand is a call
                    // leaves no value the compiler warns about (fix_diagA v01 .. w36)
                    code = 0;
                }
                else if (t != VOID && !(ex is Name nm && nm.Ref is FuncRef))
                    code = es.AfterVoidReturn ? 1023 : 1021;
                if (code != 0 && !_fileBad)
                    ErrAt(code, es.EndPos);
                return;
            }
            switch (s)
            {
                case Assign a:
                    AssignStmt(a);
                    break;
                case LocalDecl ld:
                    LocalDeclStmt(ld);
                    break;
                case If f:
                    Condition(f.Cond);
                    Block(f.Then);
                    if (f.Else != null && f.Else.Count > 0)
                        Block(f.Else);
                    break;
                case While w:
                    Condition(w.Cond);
                    Block(w.Body);
                    break;
                case For fo:
                    ForStmt(fo);
                    break;
                case Select sel:
                    SelectStmt(sel);
                    break;
                case Return ret:
                    if (ret.Value != null)
                    {
                        int t = Expression(ret.Value);
                        int rt = _fn != null ? _fn.ResultCode : VOID;
                        if (t == VOID)
                        {
                            // a void value (an unknown or void function): E2037 and E2024 there, and it
                            // does not count as returning a value (inc q1_00 .. q2_02)
                            ErrEnd(2037, ret.Value);
                            ErrEnd(2024, ret.Value);
                            return;
                        }
                        _valueReturns++;
                        Convertible(t, rt, ret.Value);
                    }
                    break;
            }
        }

        /// <summary>The first operand of e (the left end of its binary operators, not through
        /// brackets) is a call, possibly under NOT / '-' / brackets.</summary>
        private static bool CallLed(Expr e)
        {
            while (e is Binary b && !b.Paren)
                e = b.Left;
            while (e is Unary u)
                e = u.Operand;
            return e is Call;
        }

        /// <summary>The argument text an IFDEF-type intrinsic compiles as: sTrue
        /// when the tag (sTag, quotes allowed) is defined, an empty sTrue = the tag itself, else
        /// sFalse. The alarm variants test the alarm tables (any of them here, not one per variant).</summary>
        internal string IfdefPick(Intrinsic e)
        {
            var args = new List<string>(e.Args) { "", "", "" };
            string tag = CitectText.Trim(args[0]);
            if (tag.Length >= 2 && tag[0] == '"' && tag[tag.Length - 1] == '"')
                tag = tag.Substring(1, tag.Length - 2);
            bool defined;
            if (e.Code == 9)
                defined = tag.Length > 0 && _unit.TagDefined(tag).defined;
            else
                defined = tag.Length > 0 && _unit.Tags.ContainsKey("!" + Lexer.NameKey(tag));
            if (defined)
            {
                string t = CitectText.Trim(args[1]);
                return t.Length > 0 ? t : tag;
            }
            return CitectText.Trim(args[2]);
        }

        /// <summary>The text (an IFDEF branch) read as statements leaves no value: calls,
        /// assignments (the Include label 'Toggle(x)' is an assignment), nothing at all; a bare
        /// value does.</summary>
        private bool IsCallText(string text)
        {
            if (text.Length == 0)
                return true;
            List<Stmt> stmts;
            try
            {
                var p = new Parser(Lexer.Tokenize(text, _unit.Labels, "", "ci", "", _unit.TagStartDigit));
                stmts = p.ParseBlock(false);
            }
            catch (Exception)
            {
                return true; // a broken branch: the compiler's own message is not modelled
            }
            foreach (var s in stmts)
                if (s is ExprStmt es && !(es.Expr is Call))
                    return false;
            return true;
        }

        private void LocalDeclStmt(LocalDecl s)
        {
            var sc = _scope!;
            int t = 0;
            if (s.Init != null)
                t = Expression(s.Init);
            string k = Lexer.NameKey(s.Name);
            if (sc.Vars.ContainsKey(k))
                Err(2021, s, s.Name);
            var r = new VarRef("local", s.Name, Ast.TypeCode(s.Type), sc.Slot, s);
            sc.Slot++;
            s.Var = r;
            sc.Vars[k] = r;
            if (s.Init != null)
                Convertible(t, r.Type, s.Init);
        }

        private void AssignStmt(Assign s)
        {
            int t = Expression(s.Value);
            int tt = Target(s.Target);
            Convertible(t, tt, s.Value);
        }

        private int Target(Expr e)
        {
            if (e is Index ix)
            {
                foreach (var i in ix.Indices)
                    Expression(i);
                int tt = NameRef(ix.Base, asTarget: true);
                ix.Ty = tt;
                return tt;
            }
            if (e is Assoc || e is GenieParam)
                return ANY; // a page field's '?Tag? = ...'
            return NameRef((Name)e, asTarget: true);
        }

        private void Condition(Expr c)
        {
            int t = Expression(c);
            if (t == STRING || t == OBJECT || t == TIMESTAMP || t == QUALITY)
                ErrEnd(2027, c);
            else if (t == VOID)
                ErrEnd(2024, c);
        }

        private void ForStmt(For s)
        {
            VarRef? v = !s.Var.Dotted ? LookupVar(Lexer.NameKey(s.Var.Text)) : null;
            if (v == null || v.Kind == "global" || v.Kind == "locvar")
                Err(2001, s.Var, s.Var.Text);
            else if (!IsNum(v.Type))
            {
                // E2027 after the variable, then E2024 for the start (at TO) and the end value (at DO)
                // of an OBJECT / TIMESTAMP / QUALITY variable (fix_diagB f01 .. f03)
                ErrEnd(2027, s.Var, s.Var.Text);
            }
            else
            {
                s.Var.Ref = v;
                s.Var.Ty = v.Type;
            }
            if (s.Start != null)
            {
                int t = Expression(s.Start);
                if (v != null)
                    Convertible(t, v.Type, s.Start);
            }
            if (s.Stop != null)
            {
                int t2 = Expression(s.Stop);
                // (a STRING variable: E2027 only, fix_diagB f07, syntax b21)
                if (v != null && IsStrict(v.Type) && !Comparable(v.Type, t2))
                    ErrEnd(2024, s.Stop);
            }
            Block(s.Body);
        }

        private void SelectStmt(Select s)
        {
            int st = Expression(s.Selector);
            if (s.Selector is ErrorOperand)
                st = ANY; // no item checks against a broken selector (sem k16)
            Block(s.Pre);
            foreach (var c in s.Cases)
            {
                foreach (var it in c.Items)
                {
                    foreach (var e in new[] { it.Lo, it.Hi })
                    {
                        if (e == null)
                            continue;
                        int t = Expression(e);
                        if (!Comparable(st, t))
                            ErrEnd(2024, e);
                    }
                }
                Block(c.Body);
            }
            if (s.Else != null && s.Else.Count > 0)
                Block(s.Else);
        }

        // ----------------------------------------------------------------------------- types
        private static bool Comparable(int a, int b)
        {
            if (a == ANY || b == ANY)
                return true;
            if (IsNum(a) && IsNum(b))
                return true;
            return a == b && a != VOID;
        }

        /// <summary>Assignment / argument / return conversions: INT, REAL and
        /// STRING convert to each other; OBJECT, TIMESTAMP and QUALITY only to themselves (OBJECT
        /// also to INT, REAL and STRING: semantics l04, builtins_editor b07); a void value never.</summary>
        private void Convertible(int src, int dst, Expr node)
        {
            if (src == ANY || dst == ANY)
                return;
            if (src == VOID)
            {
                ErrEnd(2024, node);
                return;
            }
            if (IsStrict(dst))
            {
                if (src != dst)
                    ErrEnd(2024, node);
                return;
            }
            if (IsStrict(src) && src != OBJECT)
                ErrEnd(2024, node);
        }

        // ------------------------------------------------------------------------ expressions
        public int Expression(Expr e, bool statement = false)
        {
            int t = ExprInner(e, statement);
            e.Ty = t;
            return t;
        }

        private int ExprInner(Expr e, bool statement)
        {
            switch (e)
            {
                case Num n:
                    return n.IsInt ? INT : REAL;
                case Str _:
                    return STRING;
                case Name nm:
                    return NameRef(nm);
                case Call c:
                    return CallExpr(c);
                case Binary b:
                    return SpineExpr(b);
                case Unary u:
                {
                    int t = Expression(u.Operand);
                    if (t == VOID || t == STRING || IsStrict(t))
                    {
                        // one message: the result is not checked again (lexical o20 'NOT "a" = "b"')
                        ErrEnd(2024, u);
                        return ANY;
                    }
                    if (u.Op == "NOT")
                        return INT;
                    return t;
                }
                case Format f:
                    if (f.Spec.Kind == "bad")
                    {
                        // after E2035 the operand is not looked at (syntax m20)
                        return ANY;
                    }
                    return SpineExpr(f);
                case Index ix:
                    foreach (var i in ix.Indices)
                        Expression(i);
                    return NameRef(ix.Base, indexed: true);
                case Range r:
                    return SpineExpr(r);
                case Assoc _:
                case GenieParam _:
                case CiVBA _:
                    return ANY; // page-field operands whose value comes at run time
                case Intrinsic it:
                    switch (it.Code)
                    {
                        case 0:
                        case 1:
                        case 2:
                        case 4:
                        case 6:
                        case 8:
                        case 11:
                        case 15:
                            return STRING;
                        case 3:
                        case 5:
                        case 16:
                            return INT;
                    }
                    return ANY;
            }
            return ANY;
        }

        /// <summary>The left-spine child of a node, or null.</summary>
        private static Expr? SpineChild(Expr e)
        {
            switch (e)
            {
                case Binary b:
                    return b.Left;
                case Format f:
                    return f.Spec.Kind == "bad" ? null : f.Operand;
                case Range r:
                    return r.Operand;
            }
            return null;
        }

        /// <summary>
        /// The type of a spine node, walked in a loop so deep chains don't overflow the stack.
        /// </summary>
        private int SpineExpr(Expr e)
        {
            var spine = new List<Expr> { e };
            Expr left = SpineChild(e)!;
            for (Expr? next = SpineChild(left); next != null; next = SpineChild(left))
            {
                spine.Add(left);
                left = next;
            }
            int a = Expression(left);
            for (int k = spine.Count - 1; k >= 0; k--)
            {
                var node = spine[k];
                switch (node)
                {
                    case Binary b:
                        a = BinaryType(b, a, Expression(b.Right));
                        break;
                    case Format f:
                        if (a == VOID)
                        {
                            // E2083 at the ':', and the format has no result either (lexical f18)
                            ErrEnd(2083, f.Operand);
                            a = VOID;
                        }
                        else
                            a = STRING;
                        break;
                    // a page value '(expr)^(lo,hi)' (Range): its operand is checked like any
                    // expression, the range has its type
                }
                if (!ReferenceEquals(node, e))
                    node.Ty = a;
            }
            return a;
        }

        private int BinaryType(Binary e, int a, int b)
        {
            string op = e.Op;
            if (a == VOID || b == VOID)
            {
                // the result stays void (fix_diagA P_k04 / sem k04)
                ErrEnd(2024, e);
                return VOID;
            }
            if (op == "+")
            {
                if (a == ANY || b == ANY)
                    return a == ANY && b == ANY ? ANY : (b == ANY ? a : b);
                if (a == STRING && b == STRING)
                    return STRING;
                if (IsNum(a) && IsNum(b))
                    return a == INT && b == INT ? INT : REAL;
                if (a == TIMESTAMP && b == TIMESTAMP)
                    return TIMESTAMP;
                ErrEnd(2024, e);
                return ANY;
            }
            if (op == "-" || op == "*" || op == "/" || op == "MOD")
            {
                if (a == STRING || b == STRING || a == OBJECT || a == QUALITY || b == OBJECT || b == QUALITY)
                {
                    ErrEnd(2024, e);
                    return ANY;
                }
                if (a == ANY || b == ANY)
                    return ANY;
                if (a == TIMESTAMP || b == TIMESTAMP)
                    return TIMESTAMP;
                return a == INT && b == INT ? INT : REAL;
            }
            if (op == "<" || op == "<=" || op == ">" || op == ">=" || op == "=" || op == "<>")
            {
                if (!Comparable(a, b))
                    ErrEnd(2024, e);
                else if (a == QUALITY && op != "=" && op != "<>")
                    ErrEnd(2024, e);
                return INT;
            }
            // AND OR BITAND BITOR BITXOR
            if (a == STRING || b == STRING || IsStrict(a) || IsStrict(b))
                ErrEnd(2024, e);
            return INT;
        }

        private static int Norm(int t)
        {
            switch (t)
            {
                case Types.Int16:
                case Types.Long:
                case Types.Digital:
                case Types.UInt:
                case Types.Bcd:
                case Types.LongBcd:
                case Types.ULong:
                    return INT;
                case Types.Byte:
                    return STRING;
                case Types.Real:
                case Types.LongReal:
                    return REAL;
                case Types.Variant:
                    return ANY;
            }
            return t;
        }

        private static int BuiltinKind(string kind)
        {
            switch (kind)
            {
                case "INT":
                case "LONG":
                    return INT;
                case "REAL":
                    return REAL;
                case "STRING":
                    return STRING;
                case "OBJECT":
                    return OBJECT;
                case "TIMESTAMP":
                    return TIMESTAMP;
                case "QUALITY":
                    return QUALITY;
            }
            return ANY; // VARIANT, FUNCTION, VARARG and unknown words
        }

        private int NameRef(Name e, bool asTarget = false, bool indexed = false)
        {
            string name = e.Text;
            bool dotted = name.IndexOf('.') >= 0;
            string first = dotted ? name.Substring(0, name.IndexOf('.')) : name;
            VarRef? v = LookupVar(Lexer.NameKey(first));
            if (v != null && v.Kind != "locvar")
            {
                if (dotted)
                {
                    Err(2041, e, name); // 'i.Value' on a variable (i08)
                    return ANY;
                }
                if (indexed && v.Dims.Count == 0)
                    Err(2041, e, name); // '[ ]' on a scalar (o27)
                e.Ref = v;
                return v.Type;
            }
            if (v != null && !dotted) // a locvar tag
            {
                e.Ref = v;
                return v.Type;
            }
            if (!dotted && !asTarget)
            {
                var (fi, b) = _unit.LookupFunction(Lexer.NameKey(name), _index);
                if (fi != null)
                {
                    // a user function needs '()' (d14); then it counts as a call without arguments
                    // (W1004 when it has some) whose type is not checked (sem q05)
                    ErrEnd(2019, e, name);
                    if (fi.Mandatory > 0)
                        ErrEnd(1004, e, name);
                    e.Ref = new FuncRef(fi.Name, fi);
                    return ANY;
                }
                if (b != null)
                {
                    if (b.MinArgs > 0)
                        ErrEnd(2022, e, name); // p03 / p04
                    e.Ref = new FuncRef(b.Name, builtin: b, opcode: b.Opcode);
                    BuiltinFlags(b, e);
                    return Norm(b.Result);
                }
            }
            var (defined, t) = _unit.TagDefined(name);
            if (dotted)
            {
                // tag items: .Q is QUALITY, .T / .VT / .QT TIMESTAMP
                string item = CitectText.Lower(name.Substring(name.LastIndexOf('.') + 1));
                if (item == "q")
                    t = QUALITY;
                else if (item == "t" || item == "vt" || item == "qt")
                    t = TIMESTAMP;
            }
            e.Ref = new TagRefName(name, defined, t);
            if (!defined)
            {
                // an undefined tag is numeric to the compiler: '"x" + ZzTag' is E2024
                // (r2_grammar g09_picture_eu_next_line)
                if (!_fileBad)
                    Err(1007, e, name);
                return t == QUALITY || t == TIMESTAMP ? t : INT;
            }
            return t;
        }

        private void BuiltinFlags(Func0Row b, Node node)
        {
            if (b.Flags == 1)
                Err(2101, node, b.Name);
            else if (b.Flags == 2)
                Err(1008, node, b.Name);
            else if (b.Flags == 3)
                Err(1009, node, b.Name);
            else if (b.Flags == 4)
                Err(1010, node, b.Name);
        }

        private int CallExpr(Call e)
        {
            string key = Lexer.NameKey(e.Name);
            VarRef? v = LookupVar(key);
            if (v != null && v.Kind != "locvar")
            {
                // a variable hides a function: the name, then '(args)' as a new statement (q16)
                foreach (var a in e.Args)
                    Expression(a);
                if (e.Args.Count == 0)
                    Err(2041, e, e.Name);
                else if (e.Args.Count > 1)
                    Err(2011, e, e.Name);
                else
                    Err(1021, e, e.Name);
                e.Ref = null;
                return v.Type;
            }
            var (fi, b) = _unit.LookupFunction(key, _index);
            if (fi == null && b == null)
            {
                // an unknown function (or another file's PRIVATE one): E2031; as a value it is void
                // (E2024, sem c02), as a statement it leaves a value (W1021, c01 / h53)
                foreach (var a in e.Args)
                    Expression(a);
                e.Ref = new FuncRef(e.Name);
                if (_fileBad)
                {
                    // nothing in a file with a syntax error in the function (builtins_editor g2 err_00)
                    return ANY;
                }
                Err(2031, e, e.Name);
                return VOID;
            }
            int n = e.Args.Count;
            if (fi != null)
            {
                e.Ref = new FuncRef(fi.Name, fi);
                var ps = fi.Node.Params;
                if (n > ps.Count)
                {
                    // too many: E2022 at every argument too many, and the arguments are not checked
                    // (q12, q5_05; inc q6_04)
                    for (int i = ps.Count; i < n; i++)
                        ErrAt(2022, e.Args[i].Pos, e.Name);
                    return fi.Result;
                }
                for (int i = 0; i < n; i++)
                {
                    int t = Expression(e.Args[i]);
                    Convertible(t, Ast.TypeCodeOr(ps[i].Type ?? "", INT), e.Args[i]);
                }
                if (n < fi.Mandatory)
                    ErrEnd(1004, e, e.Name); // after the call: 'F(1){;}'
                return fi.Result;
            }
            e.Ref = new FuncRef(b!.Name, builtin: b, opcode: b.Opcode);
            int mx = b.MaxArgs;
            if ((mx >= 0 && n > mx) || n < b.MinArgs)
            {
                // the arity first: then neither the argument types nor E2101 / W1008 are reported
                // (builtins_editor a08 / a24, sem p09). Too many: at every argument too many
                // (r2_diagA c03), too few: after the call, for a VARARG built-in also at the ')'
                // (builtins_editor a14 / v03)
                if (mx >= 0 && n > mx)
                {
                    for (int i = mx; i < n; i++)
                        ErrAt(2022, e.Args[i].Pos, e.Name);
                }
                else
                {
                    if (b.VarArg && e.RParen >= 0)
                        ErrAt(2022, e.RParen, e.Name);
                    ErrEnd(2022, e, e.Name);
                }
                return Norm(b.Result);
            }
            BuiltinFlags(b, e);
            var kinds = b.Args;
            for (int i = 0; i < n; i++)
            {
                Expr a = e.Args[i];
                string kind;
                bool byRef;
                if (i < kinds.Count)
                {
                    kind = kinds[i].kind;
                    byRef = kinds[i].byRef;
                }
                else
                {
                    kind = b.VarArg ? "VARARG" : "";
                    byRef = false;
                }
                if (kind == "FUNCTION")
                {
                    CallbackArg(a);
                    continue;
                }
                if (byRef)
                {
                    ReferenceArg(a, BuiltinKind(kind));
                    continue;
                }
                if (
                    kind == "VARARG"
                    && a is Name an
                    && !an.Dotted
                    && LookupVar(Lexer.NameKey(an.Text)) == null
                )
                {
                    string ak = Lexer.NameKey(an.Text);
                    FuncInfo? fi2 = _unit.Private.TryGetValue((_index, ak), out var pf)
                        ? pf
                        : (_unit.Public.TryGetValue(ak, out var uf) ? uf : null);
                    if (fi2 != null) // r2_diagB b22 / b43 / b44
                    {
                        an.Ref = new FuncRef(fi2.Name, fi2);
                        ErrEnd(2024, an, an.Text);
                        continue;
                    }
                }
                int t = Expression(a);
                int want = BuiltinKind(kind);
                if (want != ANY)
                    Convertible(t, want, a);
            }
            return Norm(b.Result);
        }

        /// <summary>
        /// A FUNCTION argument of a built-in: the bare name of a parameterless INT function.
        /// </summary>
        private void CallbackArg(Expr a)
        {
            if (a is Num num && num.IsInt && num.IValue == 0)
            {
                a.Ty = INT;
                return;
            }
            if (!(a is Name nm) || nm.Dotted || nm.Paren)
            {
                // the compiler takes one name token there: after a name the next token must end the
                // argument (E2011, r2_diagB b10 / b11), else the token is no name (E2008, b09)
                Expr lead = a;
                while (lead is Binary lb && !lb.Paren)
                    lead = lb.Left;
                bool nameLed = !a.Paren && !lead.Paren && (lead is Name || lead is Call || lead is Index);
                Err(nameLed ? 2011 : 2008, a);
                return;
            }
            string key = Lexer.NameKey(nm.Text);
            if (LookupVar(key) != null)
            {
                Expression(a);
                ErrEnd(2024, a, nm.Text);
                return;
            }
            FuncInfo? fi = _unit.Private.TryGetValue((_index, key), out var pf)
                ? pf
                : (_unit.Public.TryGetValue(key, out var uf) ? uf : null);
            if (fi != null)
            {
                nm.Ref = new FuncRef(fi.Name, fi);
                a.Ty = INT;
                if (fi.Result != INT || fi.Node.Params.Count > 0)
                    ErrEnd(2024, a, nm.Text);
                return;
            }
            Func0Row? bt = _unit.Func0.Get(key);
            if (bt != null)
            {
                // a built-in's name there is taken as a tag: W1007 and E2024 (r2_diagB b06 PageNext,
                // b07 / b39 Beep)
                nm.Ref = new FuncRef(nm.Text, builtin: bt);
                a.Ty = INT;
                if (!_fileBad)
                    Err(1007, a, nm.Text);
                ErrEnd(2024, a, nm.Text);
                return;
            }
            Expression(a);
            ErrEnd(2024, a, nm.Text);
        }

        /// <summary>A by-reference (var) argument of a built-in: a variable of exactly that type;
        /// not a name is E2008, another type E2024 (builtins_editor c05 / c06 / c08 / c16 / c17).</summary>
        private void ReferenceArg(Expr a, int want)
        {
            if (!(a is Name || a is Index))
            {
                Err(2008, a);
                Expression(a);
                return;
            }
            int t = Expression(a);
            if (want != ANY && t != ANY && t != want)
                ErrEnd(2024, a);
        }

        /// <summary>The body pass of one file; again in the 'early error' mode when it found one of
        /// the EarlyErrors, so that none of its late messages stays (also those above the error).</summary>
        internal static void Resolve(CompileUnit unit, SourceFile f, int index)
        {
            int n0 = f.Pass2.Count;
            var r = new Resolver(unit, f, index);
            r.Run();
            if (r._fileBad)
                return;
            bool early = false;
            for (int i = n0; i < f.Pass2.Count; i++)
                if (EarlyErrors.Contains(f.Pass2[i].Code))
                {
                    early = true;
                    break;
                }
            if (early)
            {
                f.Pass2.RemoveRange(n0, f.Pass2.Count - n0);
                new Resolver(unit, f, index, true).Run();
            }
        }
    }
}
