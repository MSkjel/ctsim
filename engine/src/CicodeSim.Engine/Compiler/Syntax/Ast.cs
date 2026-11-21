using System;
using System.Collections.Generic;
using System.Numerics;
using CicodeSim.Compiler.Semantics;

namespace CicodeSim.Compiler.Syntax
{
    /// <summary>The Cicode syntax tree.</summary>
    public static class Ast
    {
        /// <summary>Type of an expression whose type the compiler does not know (an undefined tag,
        /// a VARIANT built-in result): it takes part in any operation.</summary>
        public const int Any = -1;

        public const int Void = 0x09;

        /// <summary>Declared Cicode types to VM type codes (user functions use 4 / 7 / 2
        /// / 12 / 0x13 / 0x14 in _FUNCSYM.DBF and the Library section).</summary>
        public static int TypeCode(string? type)
        {
            switch (type)
            {
                case "INT":
                    return 0x04;
                case "REAL":
                    return 0x02;
                case "STRING":
                    return 0x07;
                case "OBJECT":
                    return 0x0C;
                case "TIMESTAMP":
                    return 0x13;
                case "QUALITY":
                    return 0x14;
            }
            return -2;
        }

        /// <summary>A declared type's code, <paramref name="dflt"/> when the type is missing.</summary>
        public static int TypeCodeOr(string? type, int dflt)
        {
            int t = TypeCode(type);
            return t == -2 ? dflt : t;
        }

        /// <summary>Binary precedence levels, lower binds tighter, equal levels associate left
        /// (as CtCmp32 parses them).</summary>
        public static int Precedence(string op)
        {
            switch (op)
            {
                case "*":
                case "/":
                case "MOD":
                    return 0;
                case ":":
                    return 1;
                case "+":
                case "-":
                    return 2;
                case "<":
                case "<=":
                case ">":
                case ">=":
                    return 3;
                case "=":
                case "<>":
                    return 4;
                case "AND":
                    return 5;
                case "OR":
                    return 6;
                case "BITAND":
                case "BITOR":
                case "BITXOR":
                    return 7;
            }
            return -1;
        }

        /// <summary>Every statement of <paramref name="stmts"/> and of the blocks inside them, in
        /// source order (an explicit stack: blocks may nest as deep as the parser allows).</summary>
        public static IEnumerable<Stmt> WalkStatements(IList<Stmt> stmts)
        {
            var stack = new Stack<(IList<Stmt> list, int i)>();
            stack.Push((stmts, 0));
            while (stack.Count > 0)
            {
                var (list, i) = stack.Pop();
                if (i >= list.Count)
                    continue;
                stack.Push((list, i + 1));
                Stmt s = list[i];
                yield return s;
                var inner = new List<IList<Stmt>>();
                switch (s)
                {
                    case If f:
                        inner.Add(f.Then);
                        if (f.Else != null && f.Else.Count > 0)
                            inner.Add(f.Else);
                        break;
                    case While w:
                        inner.Add(w.Body);
                        break;
                    case For fo:
                        inner.Add(fo.Body);
                        break;
                    case Select sel:
                        inner.Add(sel.Pre);
                        foreach (var c in sel.Cases)
                            inner.Add(c.Body);
                        if (sel.Else != null && sel.Else.Count > 0)
                            inner.Add(sel.Else);
                        break;
                }
                for (int k = inner.Count - 1; k >= 0; k--)
                    stack.Push((inner[k], 0));
            }
        }
    }

    public abstract class Node
    {
        public int Pos;
        public int Line;

        protected Node(int pos, int line)
        {
            Pos = pos;
            Line = line;
        }
    }

    // ============================================================================ expressions
    /// <summary>Base class for expressions: type, parentheses and end position.</summary>
    public abstract class Expr : Node
    {
        public int? Ty;
        public bool Paren;
        public int End = -1;
        public int EndTok = -1;

        protected Expr(int pos, int line)
            : base(pos, line) { }
    }

    /// <summary>A number literal. <see cref="Kind"/> "INT" (<see cref="IValue"/> as written: 0 ..
    /// 0xFFFFFFFF, or negative after a folded unary minus) or "REAL"
    /// (<see cref="RValue"/>).</summary>
    public class Num : Expr
    {
        public long IValue;
        public double RValue;
        public string Kind;
        public string Text;

        public Num(long value, string text, int pos, int line)
            : base(pos, line)
        {
            IValue = value;
            Kind = "INT";
            Text = text;
        }

        public Num(double value, string text, int pos, int line)
            : base(pos, line)
        {
            RValue = value;
            Kind = "REAL";
            Text = text;
        }

        public bool IsInt => Kind == "INT";

        /// <summary>The INT value as the VM holds it: 32-bit two's complement (2147483648 wraps
        /// negative).</summary>
        public int I32 => unchecked((int)(uint)(IValue & 0xFFFFFFFFL));
    }

    /// <summary>The operand the parser puts where one was missing, after its E2041: the compiler
    /// goes on with an INT 0 there (an error was reported, so no code is generated).</summary>
    public sealed class ErrorOperand : Num
    {
        public ErrorOperand(int pos, int line)
            : base(0L, "", pos, line) { }
    }

    /// <summary>A string literal; Value with the escapes resolved (what the TEXT pool holds).</summary>
    public sealed class Str : Expr
    {
        public string Value;

        public Str(string value, int pos, int line)
            : base(pos, line)
        {
            Value = value;
        }
    }

    /// <summary>A name: a variable, a tag (Tag, Tag.Field, Cluster.Tag.Field, Equip.Item), or a
    /// function named without parentheses. Ref is set by the resolver.</summary>
    public sealed class Name : Expr
    {
        public string Text;
        public object? Ref;

        public Name(string name, int pos, int line)
            : base(pos, line)
        {
            Text = name;
        }

        public string[] Parts => Text.Split('.');

        public bool Dotted => Text.IndexOf('.') >= 0;
    }

    /// <summary>base[i][j]...: an element of a MODULE / GLOBAL array or of a tag array (
    /// Index 0x40: row-major, one index per dimension).</summary>
    public sealed class Index : Expr
    {
        public Name Base;
        public List<Expr> Indices;

        public Index(Name b, List<Expr> indices, int pos, int line)
            : base(pos, line)
        {
            Base = b;
            Indices = indices;
        }
    }

    /// <summary>name(args). Ref is set by the resolver (a FuncRef); RParen = the offset of the
    /// closing bracket (-1 when the call ended without one).</summary>
    public sealed class Call : Expr
    {
        public string Name;
        public List<Expr> Args;
        public FuncRef? Ref;
        public int RParen = -1;

        public Call(string name, List<Expr> args, int pos, int line)
            : base(pos, line)
        {
            Name = name;
            Args = args;
        }
    }

    /// <summary>NOT x or -x (a minus before a number literal is folded into the literal; a
    /// '+' before an operand is dropped, 7.7).</summary>
    public sealed class Unary : Expr
    {
        public string Op;
        public Expr Operand;

        public Unary(string op, Expr operand, int pos, int line)
            : base(pos, line)
        {
            Op = op;
            Operand = operand;
        }
    }

    /// <summary>A binary operation. Long chains nest to the left.</summary>
    public sealed class Binary : Expr
    {
        public string Op;
        public Expr Left;
        public Expr Right;

        public Binary(string op, Expr left, Expr right, int pos, int line)
            : base(pos, line)
        {
            Op = op;
            Left = left;
            Right = right;
        }
    }

    /// <summary>The format after ':'. Kind "picture", "width" (a number
    /// n / n.m, '-' = left justify), "tilde" ('~') or "bad" (after E2035). Width / Decimals / the
    /// flags are read from the picture as the help describes it (DOC 1064774).</summary>
    public sealed class FormatSpec : Node
    {
        public string Kind;
        public string Text;
        public BigInteger Width;
        public BigInteger Decimals;
        public bool ZeroPad;
        public bool Left;
        public bool Exponent;
        public bool Eu;

        public FormatSpec(string kind, string text, int pos, int line)
            : base(pos, line)
        {
            Kind = kind;
            Text = text;
        }
    }

    /// <summary>expr : spec: the value formatted to a STRING.</summary>
    public sealed class Format : Expr
    {
        public Expr Operand;
        public FormatSpec Spec;

        public Format(Expr operand, FormatSpec spec, int pos, int line)
            : base(pos, line)
        {
            Operand = operand;
            Spec = spec;
        }
    }

    /// <summary>A compiler intrinsic reached through a '$n' label (Include: __TIME__ $0 ..
    /// IFDEFHRESALM $17). Label the label used, Args the raw argument texts of the label call
    /// (defaults filled in).</summary>
    public sealed class Intrinsic : Expr
    {
        public int Code;

        /// <summary>The number as written, leading zeros dropped.</summary>
        public string CodeDigits;
        public string Label;
        public List<string> Args;

        public Intrinsic(int code, string codeDigits, string label, IList<string> args, int pos, int line)
            : base(pos, line)
        {
            Code = code;
            CodeDigits = codeDigits;
            Label = label;
            Args = new List<string>(args);
        }
    }

    /// <summary>A super genie association ?name? / ?TYPE name? (page code only; E2061 in a .ci
    /// file). Name = the text between the '?', Suffix = what is written right after the closing
    /// '?' ('.Field.VT', '_LL_AL.Delay', '' when nothing).</summary>
    public sealed class Assoc : Expr
    {
        public string Name;
        public string Suffix;

        public Assoc(string name, int pos, int line, string suffix = "")
            : base(pos, line)
        {
            Name = name;
            Suffix = suffix;
        }
    }

    /// <summary>expr ^ (lo, hi) in a page field: the value as a percentage of the range lo .. hi
    /// (the Bar opcode 0x37 with a SCALE record; the precedence of ':'). Lo / Hi are Num
    /// nodes (a sign folded in).</summary>
    public sealed class Range : Expr
    {
        public Expr Operand;
        public Num Lo;
        public Num Hi;

        public Range(Expr operand, Num lo, Num hi, int pos, int line)
            : base(pos, line)
        {
            Operand = operand;
            Lo = lo;
            Hi = hi;
        }
    }

    /// <summary>A page field that starts with CiVBA: the rest is CitectVBA, compiled as
    /// VbCallReturn(VbCallRun(_VbExpressionOpen("&lt;text&gt;"))).
    /// Text = the field after the keyword, as written.</summary>
    public sealed class CiVBA : Expr
    {
        public string Text;

        public CiVBA(string text, int pos, int line)
            : base(pos, line)
        {
            Text = text;
        }
    }

    /// <summary>A %name% genie token left in a page expression (substituted before compiling).</summary>
    public sealed class GenieParam : Expr
    {
        public string Name;

        public GenieParam(string name, int pos, int line)
            : base(pos, line)
        {
            Name = name;
        }
    }

    // ============================================================================= statements
    public abstract class Stmt : Node
    {
        protected Stmt(int pos, int line)
            : base(pos, line) { }
    }

    /// <summary>A lone ';'.</summary>
    public sealed class Empty : Stmt
    {
        public Empty(int pos, int line)
            : base(pos, line) { }
    }

    /// <summary>TYPE name [= init] at the top level of a function body. Init compiles as the
    /// assignment name = init.</summary>
    public sealed class LocalDecl : Stmt
    {
        public string Type;
        public string Name;
        public Expr? Init;
        public VarRef? Var;

        public LocalDecl(string type, string name, Expr? init, int pos, int line)
            : base(pos, line)
        {
            Type = type;
            Name = name;
            Init = init;
        }
    }

    /// <summary>target = value; target a Name or Index (or a page field's association). In a
    /// statement the first '=' after the target assigns, later ones compare.</summary>
    public sealed class Assign : Stmt
    {
        public Expr Target;
        public Expr Value;

        public Assign(Expr target, Expr value, int pos, int line)
            : base(pos, line)
        {
            Target = target;
            Value = value;
        }
    }

    /// <summary>An expression used as a statement (a call, or a value that is discarded: W1021,
    /// or W1023 right after a RETURN of a void function).</summary>
    public sealed class ExprStmt : Stmt
    {
        public Expr Expr;
        public bool AfterVoidReturn;

        /// <summary>Offset of the token after the expression (W1021 is there).</summary>
        public int EndPos;

        /// <summary>The statement starts with a name (the compiler's call form).</summary>
        public bool NameFirst;

        public ExprStmt(Expr expr, int pos, int line)
            : base(pos, line)
        {
            Expr = expr;
            EndPos = pos;
        }
    }

    public sealed class If : Stmt
    {
        public Expr Cond;
        public List<Stmt> Then;
        public List<Stmt>? Else;
        public int ElseLine;
        public int EndLine;

        public If(Expr cond, List<Stmt> then, List<Stmt>? els, int pos, int line)
            : base(pos, line)
        {
            Cond = cond;
            Then = then;
            Else = els;
        }
    }

    public sealed class While : Stmt
    {
        public Expr Cond;
        public List<Stmt> Body;
        public int EndLine;

        public While(Expr cond, List<Stmt> body, int pos, int line)
            : base(pos, line)
        {
            Cond = cond;
            Body = body;
        }
    }

    /// <summary>FOR var = start TO stop DO body END (no STEP; the step is +1 and the test '&lt;=',
    /// re-evaluated every iteration). Complete false = the compiler's quirks: 'FOR i = 5;'
    /// (no TO) is a plain assignment, 'FOR i' alone only names the variable.</summary>
    public sealed class For : Stmt
    {
        public Name Var;
        public Expr? Start;
        public Expr? Stop;
        public List<Stmt> Body;
        public bool Complete;
        public int EndLine;

        public For(Name v, Expr? start, Expr? stop, List<Stmt>? body, bool complete, int pos, int line)
            : base(pos, line)
        {
            Var = v;
            Start = start;
            Stop = stop;
            Body = body ?? new List<Stmt>();
            Complete = complete;
        }
    }

    /// <summary>One item of a CASE list: Kind "value" (Lo), "range" (Lo TO Hi) or "is" (IS op Lo).</summary>
    public sealed class CaseItem : Node
    {
        public string Kind;
        public Expr Lo;
        public Expr? Hi;
        public string Op;

        public CaseItem(string kind, Expr lo, Expr? hi, string op, int pos, int line)
            : base(pos, line)
        {
            Kind = kind;
            Lo = lo;
            Hi = hi;
            Op = op;
        }
    }

    public sealed class Case : Node
    {
        public List<CaseItem> Items;
        public List<Stmt> Body;

        public Case(List<CaseItem> items, List<Stmt> body, int pos, int line)
            : base(pos, line)
        {
            Items = items;
            Body = body;
        }
    }

    /// <summary>SELECT CASE expr ... END SELECT. Pre stays empty: the clauses follow the selector
    /// directly (PROBED syntax s16); kept for the contract.</summary>
    public sealed class Select : Stmt
    {
        public Expr Selector;
        public List<Stmt> Pre = new List<Stmt>();
        public List<Case> Cases = new List<Case>();
        public List<Stmt>? Else;
        public int ElseLine;
        public int EndLine;

        public Select(Expr selector, int pos, int line)
            : base(pos, line)
        {
            Selector = selector;
        }
    }

    /// <summary>RETURN [value]: a void function's RETURN never takes a value (the tokens after it
    /// start a new statement).</summary>
    public sealed class Return : Stmt
    {
        public Expr? Value;

        /// <summary>The line of the token after RETURN.</summary>
        public int NextLine;

        public Return(Expr? value, int pos, int line)
            : base(pos, line)
        {
            Value = value;
        }
    }

    // ============================================================================= file scope
    /// <summary>TYPE name [= default]; Type null when missing (E2015).</summary>
    public sealed class Param : Node
    {
        public string? Type;
        public string Name;
        public Expr? Default;

        public Param(string? type, string name, Expr? dflt, int pos, int line)
            : base(pos, line)
        {
            Type = type;
            Name = name;
            Default = dflt;
        }
    }

    /// <summary>A file-scope initial value: one literal with an optional sign.
    /// Kind "INT", "REAL" or "STRING".</summary>
    public sealed class Literal : Node
    {
        public string Kind;
        public long IValue;
        public double RValue;
        public string? SValue;

        public Literal(string kind, int pos, int line)
            : base(pos, line)
        {
            Kind = kind;
        }
    }

    /// <summary>A file-scope variable. Scope "GLOBAL", "MODULE" or null (= MODULE);
    /// Dims the array sizes (1 .. 4), Init the initial values in row-major order.</summary>
    public sealed class VarDecl : Node
    {
        public string? Scope;
        public string Type;
        public string Name;
        public List<long> Dims;
        public List<Literal> Init = new List<Literal>();
        public VarRef? Var;

        public VarDecl(string? scope, string type, string name, List<long> dims, int pos, int line)
            : base(pos, line)
        {
            Scope = scope;
            Type = type;
            Name = name;
            Dims = dims;
        }

        public bool IsGlobal => Scope == "GLOBAL";

        /// <summary>The element count (saturating: only compared with small numbers).</summary>
        public long Count => VarDeclCount(Dims);

        internal static long VarDeclCount(List<long> dims)
        {
            long n = 1;
            foreach (long d in dims)
            {
                if (d == 0)
                    return 0;
                if (n > long.MaxValue / Math.Max(1, d))
                    n = long.MaxValue;
                else
                    n *= d;
            }
            return n;
        }
    }

    /// <summary>[scope] [type] FUNCTION name [(params)] body END. Scope "PUBLIC", "PRIVATE" or
    /// null (= PUBLIC); RetType null = void. ReturnsValue = a RETURN with a value occurs (E2037
    /// when a typed function has none). Locals = every LocalDecl in order.</summary>
    public sealed class FunctionDef : Node
    {
        public string Name;
        public string? Scope;
        public string? RetType;
        public List<Param> Params = new List<Param>();
        public List<Stmt> Body = new List<Stmt>();
        public bool HasParens;
        public int EndLine;
        public int EndPos;
        public bool ReturnsValue;
        public int NamePos;
        public List<LocalDecl> Locals = new List<LocalDecl>();
        public string File = "";
        public string Project = "";

        /// <summary>Closed by its END.</summary>
        public bool Complete;

        /// <summary>RETURNs whose value had a syntax error.</summary>
        public int BrokenReturns;

        /// <summary>The parameter list broke off (E2015 / E2008 ...).</summary>
        public bool HeaderAborted;

        /// <summary>The token after END (E2037 is reported there).</summary>
        public int AfterEndPos;

        public FunctionDef(string name, string? scope, string? retType, int pos, int line)
            : base(pos, line)
        {
            Name = name;
            Scope = scope;
            RetType = retType;
            NamePos = pos;
        }

        public bool Private => Scope == "PRIVATE";

        public int ResultCode => RetType != null ? Ast.TypeCode(RetType) : Ast.Void;

        public int[] ArgCodes
        {
            get
            {
                var a = new int[Params.Count];
                for (int i = 0; i < a.Length; i++)
                    a[i] = Ast.TypeCodeOr(Params[i].Type, 0x04);
                return a;
            }
        }
    }

    /// <summary>One parsed source file (or expression field). Items = FunctionDef / VarDecl in
    /// source order; CallNames = the (name key) names called anywhere in the file.</summary>
    public sealed class Module : Node
    {
        public string File;
        public string Project;
        public List<Node> Items = new List<Node>();

        /// <summary>Lexer + body messages.</summary>
        public List<Diagnostic> Diagnostics = new List<Diagnostic>();

        /// <summary>File-scope items and function headers.</summary>
        public List<Diagnostic> DeclDiagnostics = new List<Diagnostic>();
        public LineIndex? Lines;
        public string Text = "";
        public HashSet<string> CallNames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Offset of the first file-scope E2031: the compiler reports nothing of the file
        /// after it (-1 = none); the items after it are still declared.</summary>
        public int StopPos = -1;

        public Module(string file = "", string project = "")
            : base(0, 1)
        {
            File = file;
            Project = project;
        }

        public IEnumerable<FunctionDef> Functions
        {
            get
            {
                foreach (var it in Items)
                    if (it is FunctionDef f)
                        yield return f;
            }
        }

        public IEnumerable<VarDecl> Variables
        {
            get
            {
                foreach (var it in Items)
                    if (it is VarDecl v)
                        yield return v;
            }
        }
    }

    // ============================================================================= resolution
    /// <summary>What a variable name resolved to. Kind "local", "param", "module", "global" or
    /// "locvar". Slot = the stack slot of a local / parameter (parameters first, then locals in
    /// declaration order: DEBUG section); Decl the declaring node.</summary>
    public sealed class VarRef
    {
        public string Kind;
        public string Name;
        public int Type;
        public int Slot;
        public Node? Decl;
        public List<long> Dims;
        public string File;
        public string Project;

        public VarRef(
            string kind,
            string name,
            int type,
            int slot = -1,
            Node? decl = null,
            List<long>? dims = null,
            string file = "",
            string project = ""
        )
        {
            Kind = kind;
            Name = name;
            Type = type;
            Slot = slot;
            Decl = decl;
            Dims = dims ?? new List<long>();
            File = file;
            Project = project;
        }
    }

    /// <summary>A name resolved to a variable tag / Equip.Item reference (Defined = found in the
    /// unit's tag tables; else W1007 'Tag not defined').</summary>
    public sealed class TagRefName
    {
        public string Name;
        public bool Defined;
        public int Type;

        public TagRefName(string name, bool defined, int type = Ast.Any)
        {
            Name = name;
            Defined = defined;
            Type = type;
        }
    }

    /// <summary>What a call resolved to: a user function (Func) or a built-in (Builtin its FUNC0
    /// row, Opcode the FUNC0 OP_CODE).</summary>
    public sealed class FuncRef
    {
        public string Name;
        public FuncInfo? Func;
        public Func0Row? Builtin;
        public int Opcode;

        public FuncRef(string name, FuncInfo? func = null, Func0Row? builtin = null, int opcode = -1)
        {
            Name = name;
            Func = func;
            Builtin = builtin;
            Opcode = opcode;
        }

        public bool IsBuiltin => Builtin != null;
    }
}
