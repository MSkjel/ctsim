using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;
using Index = CicodeSim.Compiler.Syntax.Index;
using Range = CicodeSim.Compiler.Syntax.Range;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>A construct the code generator does not compile (yet), or source with errors.</summary>
    public sealed class CodegenException : Exception
    {
        public readonly int Line;

        public CodegenException(string message, int line = 0)
            : base(message)
        {
            Line = line;
        }
    }

    /// <summary>One pass-1 event of a function's code generation.</summary>
    public readonly struct GenEvent
    {
        public readonly int Kind;
        public readonly VarRef? Var;
        public readonly FuncInfo? Callee;

        /// <summary>Kind 2: a tag name read (its scale goes to the SCALE pool then, 9.10).</summary>
        public readonly TagRow? Tag;

        public GenEvent(VarRef v)
        {
            Kind = 0;
            Var = v;
            Callee = null;
            Tag = null;
        }

        public GenEvent(FuncInfo f)
        {
            Kind = 1;
            Var = null;
            Callee = f;
            Tag = null;
        }

        public GenEvent(TagRow t)
        {
            Kind = 2;
            Var = null;
            Callee = null;
            Tag = t;
        }
    }

    /// <summary>The code of one function (or page block) before the pools give its operands offsets.</summary>
    public sealed class FunctionCode
    {
        public FuncInfo? Info;
        public readonly CodeBuffer Code = new CodeBuffer();
        public readonly List<GenEvent> Events = new List<GenEvent>();

        /// <summary>Distinct user callees in parse pre-order (the FUNC pool list).</summary>
        public readonly List<FuncInfo> Callees = new List<FuncInfo>();

        /// <summary>Parameters then locals: (slot, name) (the DEBUG records before hashing).</summary>
        public readonly List<(int slot, string name)> Locals = new List<(int, string)>();
    }

    /// <summary>The code generator: one function or page field to p-code.</summary>
    public sealed class FunctionGen
    {
        private const int INT = Types.Long,
            REAL = Types.Real,
            STRING = Types.String,
            OBJECT = Types.Object;
        private const int TIMESTAMP = Types.Timestamp,
            QUALITY = Types.Quality,
            VOID = Types.Void,
            ANY = Ast.Any;

        private readonly CompileUnit _unit;
        private readonly TagTable _tags;
        private readonly FunctionCode _out;

        /// <summary>Where code goes: the function's buffer, or a side buffer for an assignment
        /// target's indexes (compiled when read, placed after the value).</summary>
        private CodeBuffer _b;
        private readonly bool _lines;
        private readonly HashSet<FuncInfo> _callees = new HashSet<FuncInfo>();
        private int _lastLine = -1;
        private readonly Dictionary<string, Expr?> _defaultCache;

        /// <summary>The page field this code is for (P3 / P4 use it), null in a library.</summary>
        internal PageFieldKind Field = PageFieldKind.None;

        private FunctionGen(
            CompileUnit unit,
            TagTable tags,
            FunctionCode output,
            bool lines,
            Dictionary<string, Expr?> defaults
        )
        {
            _unit = unit;
            _tags = tags;
            _out = output;
            _b = output.Code;
            _lines = lines;
            _defaultCache = defaults;
        }

        /// <summary>The value an expression left on the stack: its static type and, for a tag value
        /// (or NOT of one), the tag whose FORMAT turns it into a STRING.</summary>
        private struct Val
        {
            public int Ty;
            public TagRow? Tag;
            public bool TagDerived;

            /// <summary>The value is a super genie association (P5: AssFmt + String to STRING).</summary>
            public string? Assoc;
            public string? TagName;

            /// <summary>The value came out of a ':' format.</summary>
            public bool Formatted;

            public Val(int ty)
            {
                Ty = ty;
                Tag = null;
                TagDerived = false;
                Assoc = null;
                TagName = null;
                Formatted = false;
            }
        }

        // ----------------------------------------------------------------------- entry points
        /// <summary>The code of one library function.</summary>
        public static FunctionCode Function(
            CompileUnit unit,
            TagTable tags,
            FuncInfo info,
            Dictionary<string, Expr?>? defaults = null
        )
        {
            var output = new FunctionCode { Info = info };
            var g = new FunctionGen(
                unit,
                tags,
                output,
                true,
                defaults ?? new Dictionary<string, Expr?>(StringComparer.Ordinal)
            );
            g.GenFunction(info.Node);
            return output;
        }

        private void GenFunction(FunctionDef fd)
        {
            _retType = fd.RetType != null ? fd.ResultCode : VOID;
            int slot = 0;
            foreach (var p in fd.Params)
                _out.Locals.Add((slot++, p.Name));
            foreach (var l in fd.Locals)
                _out.Locals.Add((slot++, l.Name));
            // F1 / F2: Entry + the default code of each parameter
            int n = fd.Params.Count;
            if (n > 0)
            {
                var labels = new CodeLabel[n + 1];
                for (int i = 0; i <= n; i++)
                    labels[i] = _b.NewLabel();
                _b.Add(Op.Entry).Targets = labels;
                for (int i = 0; i < n; i++)
                {
                    _b.Place(labels[i]);
                    var p = fd.Params[i];
                    int pt = Ast.TypeCodeOr(p.Type, INT);
                    if (p.Default != null)
                    {
                        Scan(p.Default);
                        Emit(p.Default);
                        Convert(pt);
                    }
                    else
                        Zero(pt);
                }
                _b.Place(labels[n]);
            }
            // F3: StackVar with every local's type (REAL 2)
            if (fd.Locals.Count > 0)
            {
                var types = new byte[fd.Locals.Count];
                for (int i = 0; i < types.Length; i++)
                    types[i] = (byte)Ast.TypeCodeOr(fd.Locals[i].Type, INT);
                _b.Add(Op.StackVar).Bytes = types;
            }
            // L1 a: the line of the first body item
            if (fd.Body.Count > 0)
                Line(fd.Body[0].Line);
            Block(fd.Body);
            // F5: the END line, then Ret or the type's zero + RetValue, then the end byte
            Line(fd.EndLine);
            int rt = fd.ResultCode;
            if (fd.RetType == null || rt == VOID)
                _b.Add(Op.Ret);
            else
            {
                Zero(rt);
                _b.Add(Op.RetValue);
            }
            _b.Add(Op.End);
        }

        // ---------------------------------------------------------------------------- lines
        /// <summary>L2: a Line equal to the last one emitted is dropped.</summary>
        private void Line(int line)
        {
            if (!_lines || line == _lastLine)
                return;
            _lastLine = line;
            _b.Add(Op.Line).L = line;
        }

        // ----------------------------------------------------------------------- statements
        internal void Block(IList<Stmt> stmts)
        {
            foreach (var s in stmts)
                Statement(s);
        }

        private void Statement(Stmt s)
        {
            switch (s)
            {
                case Empty _:
                    return; // S9
                case LocalDecl ld:
                    if (ld.Init == null)
                        return; // S1: no code, no Line
                    Line(ld.Line);
                    Scan(ld.Init);

                    {
                        var v = Emit(ld.Init);
                        var lt = Ast.TypeCodeOr(ld.Type, INT);
                        if (lt == STRING)
                            StringFromTag(v); // T2
                        _b.Add(Op.StoreStack).A =
                            ld.Var != null ? ld.Var.Slot : Fail<int>("local without a slot", ld.Line);
                    }
                    return;
                case Assign a:
                    Line(a.Line);
                    AssignStmt(a);
                    return;
                case ExprStmt es:
                    Line(es.Line);
                    if (es.Expr is Intrinsic ifd && CompileUnit.IfdefCodes.Contains(ifd.Code))
                    {
                        // IFDEF(tag, a, b) as a statement: the statements of the text it picks
                        // (corpus: Example EquipObject `IFDEF(Example_Motor.Running,
                        // Toggle(Example_Motor.Running), Prompt(""))`)
                        var picked = _unit.CompileField(IfdefPick(ifd), "command");
                        if (picked.Errors.Count > 0 || !(picked.Node is List<Stmt> ps))
                            throw new CodegenException("IFDEF text does not compile", es.Line);
                        Block(ps);
                        return;
                    }
                    if (es.Expr is CiVBA vba)
                    {
                        // VbCallReturn(VbCallRun(_VbExpressionOpen("<table><field><record>"))) (
                        // 11.4; corpus: Example CiVBA page `_VbExpressionOpen("PGBUTTONCMD87")`)
                        var call = VbaCall(vba);
                        Scan(call);
                        var cv = Emit(call);
                        if (cv.Ty != VOID)
                            _b.Add(Op.Pop);
                        return;
                    }
                    Scan(es.Expr);

                    {
                        var v = EmitStatementExpr(es.Expr);
                        if (v.Ty != VOID)
                            _b.Add(Op.Pop); // S3
                    }
                    return;
                case If f:
                    IfStmt(f);
                    return;
                case While w:
                    WhileStmt(w);
                    return;
                case For fo:
                    ForStmt(fo);
                    return;
                case Select sel:
                    SelectStmt(sel);
                    return;
                case Return r:
                    ReturnStmt(r);
                    return;
            }
            throw new CodegenException("statement " + s.GetType().Name + " not compiled", s.Line);
        }

        private void AssignStmt(Assign a)
        {
            // parse order: the target's name first, then its indexes (compiled when read, placed
            // after the value: S2), then the value
            Scan(a.Target);
            Scan(a.Value);
            CodeBuffer? tail = null;
            if (a.Target is Name tn)
                ReadTag(tn);
            else if (a.Target is Index tix)
            {
                ReadTag(tix.Base);
                var main = _b;
                _b = tail = new CodeBuffer();
                var dims =
                    tix.Base.Ref is VarRef dv && (dv.Kind == "module" || dv.Kind == "global")
                        ? dv.Dims
                        : new List<long>
                        {
                            TagOf(tix.Base, out TagRow? trw) != null && trw != null ? trw.ArraySize : 0,
                        };
                IndexPrefix(tix, dims);
                _b = main;
            }
            var v = Emit(a.Value);
            switch (a.Target)
            {
                case Name nm:
                    StoreName(nm, a.Value, v);
                    return;
                case Index ix:
                {
                    if (ix.Base.Ref is VarRef vr && (vr.Kind == "module" || vr.Kind == "global"))
                    {
                        if (vr.Type == STRING)
                            StringFromTag(v); // T2
                        _b.Ins.AddRange(tail!.Ins);
                        _b.Add(Op.StoreCodeVar).Ref = vr;
                        return;
                    }
                    var tk = TagOf(ix.Base, out TagRow? row);
                    if (tk == null)
                        throw new CodegenException("indexed store to " + ix.Base.Text, a.Line);
                    if (IsStringTag(ix.Base) && T3Value(a.Value))
                        TagWord(row); // T3
                    _b.Ins.AddRange(tail!.Ins);
                    _b.Add(Op.Store).Ref = tk;
                    return;
                }
                case Assoc asc:
                {
                    _b.Add(Op.Store).Ref = AssocKey(asc);
                    return;
                }
            }
            throw new CodegenException("assignment target " + a.Target.GetType().Name, a.Line);
        }

        private void StoreName(Name nm, Expr value, Val v)
        {
            if (nm.Ref is VarRef vr)
            {
                switch (vr.Kind)
                {
                    case "param":
                    case "local":
                        if (vr.Type == STRING)
                            StringFromTag(v); // T2
                        _b.Add(Op.StoreStack).A = vr.Slot;
                        return;
                    case "module":
                    case "global":
                        Read(vr);
                        if (vr.Type == STRING)
                            StringFromTag(v);
                        _b.Add(Op.StoreCodeVar).Ref = vr;
                        return;
                    case "locvar":
                    {
                        var row = _tags.LocalVariable(vr.Name);
                        if (vr.Type == STRING && T3Value(value))
                            TagWord(row); // T3
                        _b.Add(Op.Store).Ref = TagKeyOf(nm.Text, 0);
                        return;
                    }
                }
            }
            var tk = TagOf(nm, out TagRow? trow);
            if (tk == null)
                throw new CodegenException("store to " + nm.Text, nm.Line);
            if (IsStringTag(nm) && T3Value(value))
                TagWord(trow); // T3
            _b.Add(Op.Store).Ref = tk;
        }

        /// <summary>T3: the value of a STRING-tag store gets the tag's StringFmt when it is a
        /// literal, a name or an array element, not an operator expression and not a name the
        /// project does not define ('StringTag = UndefinedName' stores it as it is).</summary>
        private static bool T3Value(Expr e) =>
            e is Num || e is Str || (e is Name n && !(n.Ref is TagRefName tr && !tr.Defined)) || e is Index;

        private bool IsStringTag(Name nm)
        {
            if (nm.Ref is TagRefName tr)
            {
                if (!tr.Defined)
                    return false;
                if (tr.Type == STRING)
                    return true;
                // an Equip.Item: the type of the variable tag behind it (corpus: ExampleSA
                // 'Company.TopMilk.Bottling.TNK20.BatchID = ""' gets the StringFmt)
                return tr.Type == ANY
                    && _tags.Lookup(tr.Name) is TagRow row
                    && CitectText.Upper(row.Type) == "STRING";
            }
            if (nm.Ref is VarRef vr && vr.Kind == "locvar")
                return vr.Type == STRING;
            return false;
        }

        private void TagWord(TagRow? row)
        {
            var (w, suf) = row != null ? row.FormatWord() : (FormatWords.NoFormat, "");
            StringFmt(w, suf);
        }

        private void IfStmt(If f)
        {
            Line(f.Line);
            Scan(f.Cond);
            Emit(f.Cond);
            var lfalse = _b.NewLabel();
            var lend = _b.NewLabel();
            _b.Add(Op.Brfalse).Target = lfalse;
            Block(f.Then);
            if (f.Else != null)
            {
                Line(f.ElseLine); // L1 c
                _b.Add(Op.Branch).Target = lend;
                _b.Place(lfalse);
                Block(f.Else);
                _b.Place(lend);
            }
            else
            {
                Line(f.EndLine);
                _b.Add(Op.Branch).Target = lend; // S5: a no-op +2
                _b.Place(lfalse);
                _b.Place(lend);
            }
        }

        private void WhileStmt(While w)
        {
            Line(w.Line);
            var ltop = _b.NewLabel();
            var lend = _b.NewLabel();
            _b.Place(ltop);
            Scan(w.Cond);
            Emit(w.Cond);
            _b.Add(Op.Brfalse).Target = lend;
            Block(w.Body);
            Line(w.EndLine); // L1 d
            _b.Add(Op.Branch).Target = ltop;
            _b.Place(lend);
        }

        private void ForStmt(For fo)
        {
            Line(fo.Line);
            if (!fo.Complete)
            {
                // 'FOR i = 5;' (no TO) is a plain assignment, 'FOR i' alone names the variable
                if (fo.Start != null)
                {
                    Scan(fo.Var);
                    if (fo.Var.Ref is VarRef fv)
                        Read(fv);
                    Scan(fo.Start);
                    var v0 = Emit(fo.Start);
                    StoreName(fo.Var, fo.Start, v0);
                }
                return;
            }
            Scan(fo.Var);
            if (fo.Var.Ref is VarRef fv2)
                Read(fv2);
            Scan(fo.Start!);
            var v = Emit(fo.Start!);
            StoreName(fo.Var, fo.Start!, v);
            var ltop = _b.NewLabel();
            var lend = _b.NewLabel();
            _b.Place(ltop);
            LoadVarName(fo.Var);
            Scan(fo.Stop!);
            Emit(fo.Stop!);
            _b.Add(Op.Le);
            _b.Add(Op.Brfalse).Target = lend;
            Block(fo.Body);
            Line(fo.EndLine); // L1 d
            LoadVarName(fo.Var);
            ConstInt(1);
            _b.Add(Op.Add);
            StoreName(fo.Var, fo.Var, new Val(INT));
            _b.Add(Op.Branch).Target = ltop;
            _b.Place(lend);
        }

        private void LoadVarName(Name nm)
        {
            if (!(nm.Ref is VarRef vr))
                throw new CodegenException("FOR variable " + nm.Text, nm.Line);
            Read(vr);
            if (vr.Kind == "module" || vr.Kind == "global")
                _b.Add(Op.LoadCodeVar).Ref = vr;
            else
                _b.Add(Op.LoadStack).A = vr.Slot;
        }

        private static int Flip(string op)
        {
            switch (op)
            {
                case "<":
                    return Op.Gt;
                case "<=":
                    return Op.Ge;
                case ">":
                    return Op.Lt;
                case ">=":
                    return Op.Le;
                case "=":
                    return Op.Eq;
                case "<>":
                    return Op.Ne;
            }
            return Op.Eq;
        }

        private void SelectStmt(Select sel)
        {
            Line(sel.Line);
            Scan(sel.Selector);
            Emit(sel.Selector);
            var lend = _b.NewLabel();
            for (int k = 0; k < sel.Cases.Count; k++)
            {
                var c = sel.Cases[k];
                var lbody = _b.NewLabel();
                var lnext = _b.NewLabel();
                for (int j = 0; j < c.Items.Count; j++)
                {
                    var it = c.Items[j];
                    bool last = j == c.Items.Count - 1;
                    if (it.Kind == "range")
                    {
                        var lafter = _b.NewLabel();
                        Scan(it.Lo);
                        Emit(it.Lo);
                        _b.Add(Op.DupEx);
                        _b.Add(Op.Le);
                        _b.Add(Op.Brfalse).Target = lafter;
                        Scan(it.Hi!);
                        Emit(it.Hi!);
                        _b.Add(Op.DupEx);
                        _b.Add(Op.Ge);
                        _b.Add(Op.BrTrue).Target = lbody;
                        _b.Place(lafter);
                        if (last)
                            _b.Add(Op.Branch).Target = lnext;
                        continue;
                    }
                    Scan(it.Lo);
                    Emit(it.Lo);
                    _b.Add(Op.DupEx);
                    _b.Add(it.Kind == "is" ? Flip(it.Op) : Op.Eq);
                    if (last)
                        _b.Add(Op.Brfalse).Target = lnext;
                    else
                        _b.Add(Op.BrTrue).Target = lbody;
                }
                _b.Place(lbody);
                Block(c.Body);
                int nextLine =
                    k + 1 < sel.Cases.Count
                        ? sel.Cases[k + 1].Line
                        : (sel.Else != null ? sel.ElseLine : sel.EndLine);
                Line(nextLine); // L1 e
                _b.Add(Op.Branch).Target = lend;
                _b.Place(lnext);
            }
            if (sel.Else != null)
            {
                Block(sel.Else);
                Line(sel.EndLine); // L1 f
            }
            _b.Place(lend);
            _b.Add(Op.Pop);
        }

        private int _retType = VOID;

        private void ReturnStmt(Return r)
        {
            if (r.Value == null)
            {
                Line(r.NextLine > 0 ? r.NextLine : r.Line); // L3
                if (_retType != VOID && _retType != 0)
                {
                    Zero(_retType);
                    _b.Add(Op.RetValue);
                }
                else
                    _b.Add(Op.Ret);
                return;
            }
            Line(r.Line);
            Scan(r.Value);
            var v = Emit(r.Value);
            ConvertArg(v, _retType); // S4
            _b.Add(Op.RetValue);
        }

        // --------------------------------------------------------------------- conversions
        /// <summary>The conversion of a value for a parameter / return of type <paramref name="t"/>
        /// (E7.1, E8, S4): INT / REAL always Convert; STRING Convert 7 or the tag's StringFmt (T2);
        /// OBJECT / TIMESTAMP / QUALITY only when the type differs.</summary>
        private void ConvertArg(Val v, int t)
        {
            switch (t)
            {
                case Types.Int16:
                case INT:
                    Convert(INT);
                    return;
                case REAL:
                    Convert(REAL);
                    return;
                case STRING:
                    if (!StringFromTag(v))
                        Convert(STRING);
                    return;
                case OBJECT:
                case TIMESTAMP:
                case QUALITY:
                    if (v.Ty != t)
                        Convert(t);
                    return;
            }
        }

        /// <summary>T2 / P5: a tag value (or NOT of one) becomes a STRING with the tag's StringFmt,
        /// an association with AssFmt + String. False when the value is neither.</summary>
        private bool StringFromTag(Val v)
        {
            if (v.Assoc != null)
            {
                _b.Add(Op.AssFmt).Ref = v.Assoc;
                _b.Add(Op.String);
                return true;
            }
            if (!v.TagDerived)
                return false;
            TagWord(v.Tag);
            return true;
        }

        private void Convert(int t) => _b.Add(Op.Convert).A = t;

        private void StringFmt(uint word, string suffix)
        {
            var i = _b.Add(Op.StringFmt);
            i.L = word;
            i.Ref = suffix;
        }

        private void ConstInt(int v)
        {
            var i = _b.Add(Op.Const);
            i.A = 0x04;
            i.L = v;
        }

        private void ConstStr(string s)
        {
            var i = _b.Add(Op.Const);
            i.A = 0x07;
            i.Ref = s;
        }

        /// <summary>F2: the type's zero constant (INT 0, REAL 0.0, STRING "", OBJECT -1, TIMESTAMP /
        /// QUALITY 0).</summary>
        private void Zero(int t)
        {
            var i = _b.Add(Op.Const);
            switch (t)
            {
                case REAL:
                    i.A = 0x02;
                    i.D = 0.0;
                    return;
                case STRING:
                    i.A = 0x07;
                    i.Ref = "";
                    return;
                case OBJECT:
                    i.A = 0x0C;
                    i.L = -1;
                    return;
                case TIMESTAMP:
                case QUALITY:
                    i.A = t;
                    i.L = 0;
                    return;
            }
            i.A = 0x04;
            i.L = 0;
        }

        private static T Fail<T>(string what, int line) => throw new CodegenException(what, line);

        // ---------------------------------------------------------------- name resolution order
        /// <summary>Walk an expression in parse order, recording variables and calls.</summary>
        private void Scan(Expr e)
        {
            var stack = new Stack<Expr>();
            stack.Push(e);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                switch (x)
                {
                    case Name nm:
                        ScanName(nm);
                        break;
                    case Index ix:
                        ScanName(ix.Base);
                        for (int k = ix.Indices.Count - 1; k >= 0; k--)
                            stack.Push(ix.Indices[k]);
                        break;
                    case Call c:
                        if (c.Ref?.Func != null)
                            Callee(c.Ref.Func);
                        for (int k = c.Args.Count - 1; k >= 0; k--)
                        {
                            if (c.Ref?.Builtin != null && ArgKind(c.Ref.Builtin, k).kind == "FUNCTION")
                                continue;
                            stack.Push(c.Args[k]);
                        }
                        break;
                    case Unary u:
                        stack.Push(u.Operand);
                        break;
                    case Binary b:
                        stack.Push(b.Right);
                        stack.Push(b.Left);
                        break;
                    case Format f:
                        stack.Push(f.Operand);
                        break;
                    case Range r:
                        stack.Push(r.Operand);
                        break;
                }
            }
        }

        private void ScanName(Name nm)
        {
            if (nm.Ref is FuncRef fr && fr.Func != null)
                Callee(fr.Func);
        }

        /// <summary>A module or global variable read by the parser.</summary>
        private void Read(VarRef vr)
        {
            if (vr.Kind == "module" || vr.Kind == "global")
                _out.Events.Add(new GenEvent(vr));
            else if (vr.Kind == "locvar" && _tags.LocalVariable(vr.Name) is TagRow lr)
                _out.Events.Add(new GenEvent(lr));
        }

        /// <summary>A tag name read by the parser (its scale enters the SCALE pool in this order:
        /// corpus Example `BLD_W = PLT_W ...` gives BLD_W's 0..120000 before PLT_W's).</summary>
        private void ReadTag(Name nm)
        {
            if (nm.Ref is VarRef vr)
            {
                Read(vr);
                if (vr.Kind == "locvar")
                    _lastRef = (object?)_tags.LocalVariable(vr.Name) ?? NoRow.It;
                return;
            }
            if (!(nm.Ref is TagRefName tr))
                return;
            var row = _tags.Lookup(tr.Name);
            // a tag item (.Q, .T, .VT ...) is no value with the tag's format: a display field's
            // conversion does not follow it (corpus: Example `QualityToStr(TagExt_DemoTag1.Q,0,1)`
            // ends with no StringFmt)
            int dot = tr.Name.LastIndexOf('.');
            string lastItem = dot > 0 ? Lexer.NameKey(tr.Name.Substring(dot + 1)) : "";
            if (!(lastItem == "q" || lastItem == "t" || lastItem == "vt" || lastItem == "qt"))
                _lastRef = (object?)row ?? NoRow.It;
            if (row != null)
                _out.Events.Add(new GenEvent(row));
        }

        private void Callee(FuncInfo f)
        {
            if (_callees.Add(f))
                _out.Callees.Add(f);
        }

        // ------------------------------------------------------------------------- expressions
        private Val EmitStatementExpr(Expr e) => Emit(e);

        /// <summary>E1: post-order, left to right. Long left spines (a + b + c ...) are walked in a
        /// loop (the parser builds them as deep as the source is long).</summary>
        private Val Emit(Expr e)
        {
            if (e is Binary || e is Format || e is Range)
                return EmitSpine(e);
            switch (e)
            {
                case Num n:
                    if (n.IsInt)
                    {
                        ConstInt(n.I32); // E2
                        return new Val(INT);
                    }

                    {
                        var i = _b.Add(Op.Const);
                        i.A = 0x02;
                        i.D = n.RValue;
                        return new Val(REAL);
                    }
                case Str s:
                    ConstStr(s.Value);
                    return new Val(STRING);
                case Name nm:
                    return EmitName(nm);
                case Index ix:
                    return EmitIndex(ix);
                case Call c:
                    return EmitCall(c);
                case Unary u:
                {
                    var v = Emit(u.Operand);
                    if (u.Op == "NOT")
                    {
                        _b.Add(Op.Not); // E5
                        return new Val(INT) { TagDerived = v.TagDerived, Tag = v.Tag };
                    }
                    if (u.Op == "-")
                    {
                        _b.Add(Op.Neg);
                        return new Val(v.Ty);
                    }
                    return v;
                }
                case Assoc a:
                    return EmitAssoc(a);
                case Intrinsic it:
                    return EmitIntrinsic(it);
                case CiVBA vba:
                    return Emit(VbaCall(vba));
            }
            throw new CodegenException("expression " + e.GetType().Name + " not compiled", e.Line);
        }

        private static int BinOp(string op)
        {
            switch (op)
            {
                case "*":
                    return Op.Mul;
                case "/":
                    return Op.Div;
                case "MOD":
                    return Op.Mod;
                case "+":
                    return Op.Add;
                case "-":
                    return Op.Sub;
                case "<":
                    return Op.Lt;
                case "<=":
                    return Op.Le;
                case ">":
                    return Op.Gt;
                case ">=":
                    return Op.Ge;
                case "=":
                    return Op.Eq;
                case "<>":
                    return Op.Ne;
                case "AND":
                    return Op.And;
                case "OR":
                    return Op.Or;
                case "BITAND":
                    return Op.BitAnd;
                case "BITOR":
                    return Op.BitOr;
                case "BITXOR":
                    return Op.BitXor;
            }
            throw new CodegenException("operator " + op);
        }

        private static Expr? SpineChild(Expr e)
        {
            switch (e)
            {
                case Binary b:
                    return b.Left;
                case Format f:
                    return f.Operand;
                case Range r:
                    return r.Operand;
            }
            return null;
        }

        private Val EmitSpine(Expr e)
        {
            var spine = new List<Expr> { e };
            Expr left = SpineChild(e)!;
            while (left is Binary || left is Format || left is Range)
            {
                spine.Add(left);
                left = SpineChild(left)!;
            }
            Val a = Emit(left);
            for (int k = spine.Count - 1; k >= 0; k--)
            {
                var node = spine[k];
                switch (node)
                {
                    case Binary b:
                        Emit(b.Right); // E4: no Convert
                        _b.Add(BinOp(b.Op));
                        a = new Val(node.Ty ?? ANY);
                        break;
                    case Format f:
                    {
                        uint w = FormatWords.OfSpec(f.Spec, out bool eu); // E6
                        string suffix = eu && a.Tag != null ? a.Tag.EngUnits : "";
                        if (Field != PageFieldKind.None && eu)
                            suffix = EuSuffix(a);
                        StringFmt(w, suffix);
                        a = new Val(STRING)
                        {
                            Tag = a.Tag,
                            Formatted = true,
                            Assoc = a.Assoc,
                        };
                        break;
                    }
                    case Range r:
                        a = EmitRange(r, a);
                        break;
                }
            }
            return a;
        }

        private Val EmitName(Name nm)
        {
            switch (nm.Ref)
            {
                case VarRef vr:
                    switch (vr.Kind)
                    {
                        case "param":
                        case "local":
                            _b.Add(Op.LoadStack).A = vr.Slot;
                            return new Val(vr.Type);
                        case "module":
                        case "global":
                            Read(vr);
                            _b.Add(Op.LoadCodeVar).Ref = vr;
                            return new Val(vr.Type);
                        case "locvar":
                            ReadTag(nm);
                            _b.Add(Op.Load).Ref = TagKeyOf(nm.Text, LoadPercentage());
                            return new Val(vr.Type)
                            {
                                TagDerived = true,
                                Tag = _tags.LocalVariable(vr.Name),
                                TagName = nm.Text,
                            };
                    }
                    break;
                case TagRefName tr:
                {
                    ReadTag(nm);
                    var tk = TagOf(nm, out TagRow? row)!;
                    _b.Add(Op.Load).Ref = tk;
                    return new Val(tr.Type == ANY ? INT : tr.Type)
                    {
                        TagDerived = tr.Defined,
                        Tag = row,
                        TagName = nm.Text,
                    };
                }
                case FuncRef fr:
                    // E3: a function named without parentheses is a call with no arguments
                    return CallTo(fr, new List<Expr>(), nm.Line);
            }
            throw new CodegenException("name " + nm.Text + " not resolved", nm.Line);
        }

        private Val EmitIndex(Index ix)
        {
            if (ix.Base.Ref is VarRef vr && (vr.Kind == "module" || vr.Kind == "global"))
            {
                Read(vr);
                IndexPrefix(ix, vr.Dims);
                _b.Add(Op.LoadCodeVar).Ref = vr;
                return new Val(vr.Type);
            }
            ReadTag(ix.Base);
            var tk = TagOf(ix.Base, out TagRow? row);
            if (tk == null)
                throw new CodegenException("index of " + ix.Base.Text, ix.Line);
            IndexPrefix(ix, new List<long> { row != null ? row.ArraySize : 0 });
            _b.Add(Op.Load).Ref = tk;
            bool defined = ix.Base.Ref is TagRefName tr ? tr.Defined : ix.Base.Ref is VarRef;
            return new Val(ix.Ty ?? INT)
            {
                TagDerived = defined,
                Tag = row,
                TagName = ix.Base.Text,
            };
        }

        /// <summary>The indexes left to right, then Index with the dimensions (E3).</summary>
        private void IndexPrefix(Index ix, List<long> dims)
        {
            foreach (var i in ix.Indices)
                Emit(i);
            var d = new short[dims.Count];
            for (int k = 0; k < d.Length; k++)
                d[k] = (short)dims[k];
            _b.Add(Op.Index).Shorts = d;
        }

        // ------------------------------------------------------------------------------ calls
        private Val EmitCall(Call c)
        {
            if (c.Ref == null || (c.Ref.Func == null && c.Ref.Builtin == null))
                throw new CodegenException("call of an unknown function " + c.Name, c.Line);
            return CallTo(c.Ref, c.Args, c.Line);
        }

        private Val CallTo(FuncRef r, List<Expr> args, int line)
        {
            if (r.Func != null)
            {
                // E8: each argument + its conversion, the callee's Entry supplies the defaults
                var ps = r.Func.Node.Params;
                for (int i = 0; i < args.Count; i++)
                {
                    var v = Emit(args[i]);
                    ConvertArg(v, i < ps.Count ? Ast.TypeCodeOr(ps[i].Type, INT) : VOID);
                }
                var ins = _b.Add(Op.FuncCallByLookup);
                ins.Ref = r.Func;
                ins.A = args.Count;
                _out.Events.Add(new GenEvent(r.Func));
                int res = r.Func.Result;
                return new Val(res);
            }
            var b = r.Builtin!;
            int pushed = 0;
            int fixedN = b.Args.Count;
            if (b.VarArg)
                for (int i = 0; i < b.Args.Count; i++)
                    if (b.Args[i].kind == "VARARG")
                    {
                        fixedN = i;
                        break;
                    }
            int given = Math.Min(args.Count, fixedN);
            for (int i = 0; i < given; i++)
            {
                BuiltinArg(args[i], b.Args[i].kind, b.Args[i].byRef);
                pushed++;
            }
            if (b.VarArg)
            {
                // E7: the extra arguments without conversion, then their count (a 0 placeholder
                // first when there are none)
                int extra = args.Count - fixedN;
                if (extra <= 0)
                {
                    ConstInt(0);
                    pushed++;
                }
                else
                {
                    for (int i = fixedN; i < args.Count; i++)
                    {
                        VarArg(args[i]);
                        pushed++;
                    }
                }
                ConstInt(Math.Max(extra, 0));
                pushed++;
            }
            else
            {
                // E7.2: the OPTARGS defaults of the omitted trailing parameters
                for (int i = args.Count; i < b.Args.Count; i++)
                {
                    string? d = i < b.Defaults.Count ? b.Defaults[i] : null;
                    if (d == null)
                        throw new CodegenException("missing argument " + (i + 1) + " of " + b.Name, line);
                    var de = DefaultExpr(d, line);
                    var v = Emit(de);
                    ConvertKind(v, b.Args[i].kind);
                    pushed++;
                }
            }
            var fi = _b.Add(Op.Func);
            fi.L = r.Opcode >= 0 ? r.Opcode : b.Opcode;
            fi.A = pushed;
            int rt = b.Result;
            return new Val(RetType(rt));
        }

        private static int RetType(int t)
        {
            switch (t)
            {
                case Types.Int16:
                case Types.Digital:
                case Types.UInt:
                case Types.Bcd:
                case Types.LongBcd:
                case Types.ULong:
                    return INT;
                case Types.Variant:
                    return ANY;
            }
            return t;
        }

        private static (string kind, bool byRef) ArgKind(Func0Row b, int i)
        {
            if (i < b.Args.Count)
                return b.Args[i];
            return (b.VarArg ? "VARARG" : "", false);
        }

        private Expr DefaultExpr(string text, int line)
        {
            if (!_defaultCache.TryGetValue(text, out Expr? e))
            {
                var res = _unit.CompileField(text, "expression");
                e = res.Node as Expr;
                if (res.Errors.Count > 0)
                    e = null;
                _defaultCache[text] = e;
            }
            if (e == null)
                throw new CodegenException("default argument " + text + " does not compile", line);
            return e;
        }

        private void BuiltinArg(Expr a, string kind, bool byRef)
        {
            if (kind == "FUNCTION")
            {
                // E7.1: the function's name as a STRING ('' for a literal 0), no conversion
                if (a is Num n && n.IsInt && n.IValue == 0)
                    ConstStr("");
                else if (a is Name nm && nm.Ref is FuncRef fr && fr.Func != null)
                    ConstStr(fr.Func.SymbolName);
                else
                    throw new CodegenException("FUNCTION argument", a.Line);
                return;
            }
            if (byRef)
            {
                Reference(a);
                return;
            }
            var v = Emit(a);
            if (kind == "VARIANT" && a is Assoc asc && v.Assoc != null && Field != PageFieldKind.None)
            {
                // an association passed to a VARIANT parameter: Convert TAG, then a 100-byte
                // area the runtime patches, ending with a write-back of a TAG constant (Q13;
                // Example !TimeScheduler READY_FUNC, reproduced as found)
                Convert(Types.Tag);
                var after = _b.NewLabel();
                var area = _b.NewLabel();
                _b.Add(Op.Branch).Target = after;
                _b.Place(area);
                _b.Add(CodeIns.Raw).Bytes = new byte[] { (byte)Op.Branch, 0, 0 };
                _b.Add(CodeIns.Raw).Bytes = new byte[100 - 3 - 6 - 9];
                var c = _b.Add(Op.Const);
                c.A = Types.Tag;
                c.L = 0;
                _b.Add(Op.Store).Ref = AssocKey(asc);
                _b.Add(Op.Branch).Target = area;
                _b.Place(after);
                return;
            }
            ConvertKind(v, kind);
        }

        private void ConvertKind(Val v, string kind)
        {
            switch (kind)
            {
                case "INT":
                case "LONG":
                    Convert(INT);
                    return;
                case "REAL":
                    Convert(REAL);
                    return;
                case "STRING":
                    if (!StringFromTag(v))
                        Convert(STRING);
                    return;
                case "OBJECT":
                    if (v.Ty != OBJECT)
                        Convert(OBJECT);
                    return;
                case "TIMESTAMP":
                    if (v.Ty != TIMESTAMP)
                        Convert(TIMESTAMP);
                    return;
                case "QUALITY":
                    if (v.Ty != QUALITY)
                        Convert(QUALITY);
                    return;
            }
            // VARIANT and the rest: nothing
        }

        /// <summary>A by-reference argument: AddrStack, AddrCodeVar (RUN DataAddress), or the
        /// indexes + Index + AddrCodeVar (E7.1).</summary>
        private void Reference(Expr a)
        {
            if (a is Name nm && nm.Ref is VarRef vr)
            {
                if (vr.Kind == "param" || vr.Kind == "local")
                {
                    _b.Add(Op.AddrStack).A = vr.Slot;
                    return;
                }
                if (vr.Kind == "module" || vr.Kind == "global")
                {
                    Read(vr);
                    var i = _b.Add(Op.AddrCodeVar);
                    i.A = RawType(vr.Type);
                    i.Ref = vr;
                    return;
                }
            }
            if (a is Index ix && ix.Base.Ref is VarRef ar && (ar.Kind == "module" || ar.Kind == "global"))
            {
                Read(ar);
                IndexPrefix(ix, ar.Dims);
                var i = _b.Add(Op.AddrCodeVar);
                i.A = RawType(ar.Type);
                i.Ref = ar;
                return;
            }
            throw new CodegenException("by-reference argument " + a.GetType().Name, a.Line);
        }

        private void VarArg(Expr a)
        {
            if (
                a is Name nm
                && nm.Ref is VarRef vr
                && (vr.Kind == "param" || vr.Kind == "local" || vr.Kind == "module" || vr.Kind == "global")
            )
            {
                Reference(a);
                return;
            }
            Emit(a);
        }

        /// <summary>CODEVAR RawType of a declared type: INT 4, REAL 6, STRING 7, OBJECT 0xC,
        /// TIMESTAMP 0x13, QUALITY 0x14.</summary>
        public static int RawType(int t) => t == REAL ? Types.LongReal : t;

        // ------------------------------------------------------------------------------ tags
        /// <summary>The TAGREF of a tag / Equip.Item / alarm reference: a trailing item '.v' /
        /// '.vt' / '.q' / '.qt' / '.t' goes to TagItem, the rest (dots kept) to TagName.</summary>
        private TagKey? TagOf(Name nm, out TagRow? row)
        {
            row = null;
            if (nm.Ref is VarRef vr && vr.Kind == "locvar")
            {
                row = _tags.LocalVariable(vr.Name);
                return TagKeyOf(nm.Text, LoadPercentage());
            }
            if (!(nm.Ref is TagRefName tr))
                return null;
            row = _tags.Lookup(tr.Name);
            return TagKeyOf(nm.Text, LoadPercentage());
        }

        internal static readonly HashSet<string> Items = new HashSet<string>(StringComparer.Ordinal)
        {
            "v",
            "vt",
            "q",
            "qt",
            "t",
        };

        private TagKey TagKeyOf(string text, int percentage)
        {
            string tag = text,
                item = "",
                element = "";
            int k = tag.LastIndexOf('.');
            if (k > 0 && Items.Contains(Lexer.NameKey(tag.Substring(k + 1))))
            {
                item = tag.Substring(k + 1);
                tag = tag.Substring(0, k);
            }
            // a tag element (.Field, .Valid, .Override, .OverrideMode, .ControlMode, .Status) goes to
            // TagElement (corpus: Example `TagExt_DemoTag1.OverrideMode` = TagName 'TagExt_DemoTag1',
            // element 'OverrideMode'; an Equip.Item `...Probe7.Valid.v` = element Valid, item v)
            k = tag.LastIndexOf('.');
            if (k > 0 && Elements.Contains(Lexer.NameKey(tag.Substring(k + 1))))
            {
                string head = tag.Substring(0, k);
                if (head.Length > 0)
                {
                    element = tag.Substring(k + 1);
                    tag = head;
                }
            }
            return new TagKey("", tag, element, item, percentage);
        }

        private int LoadPercentage() => _percentage;

        private int _percentage;

        // ------------------------------------------------------------------- page fields
        /// <summary>The code of one page field: <paramref name="node"/> is what
        /// <see cref="CompileUnit.CompileField"/> gave (an Expr, or the command's statements).
        /// No Line, Entry, StackVar or Ret; the block ends with End (P1).</summary>
        public static FunctionCode PageField(
            CompileUnit unit,
            TagTable tags,
            object? node,
            PageFieldKind kind,
            Dictionary<string, Expr?>? defaults = null,
            string? vbaName = null
        )
        {
            var output = new FunctionCode();
            var g = new FunctionGen(
                unit,
                tags,
                output,
                false,
                defaults ?? new Dictionary<string, Expr?>(StringComparer.Ordinal)
            )
            {
                Field = kind,
                VbaName = vbaName,
            };
            g.GenField(node);
            return output;
        }

        private void GenField(object? node)
        {
            if (Field == PageFieldKind.Command)
            {
                if (node is List<Stmt> stmts)
                    Block(stmts); // P6
                _b.Add(Op.End);
                return;
            }
            if (!(node is Expr e))
            {
                _b.Add(Op.End);
                return;
            }
            Scan(e);
            switch (Field)
            {
                case PageFieldKind.Range:
                    RangeField(e);
                    break;
                case PageFieldKind.Display:
                    DisplayField(e);
                    break;
                default:
                    Emit(e); // P2: no conversion
                    break;
            }
            _b.Add(Op.End);
        }

        /// <summary>P3: (e)^(lo,hi) = e; Bar (lo,hi,lo,hi); a lone tag = Load with Percentage 1;
        /// anything else e; Bar with the default 0..100 scale.</summary>
        private void RangeField(Expr e)
        {
            if (e is Range)
            {
                Emit(e);
                return;
            }
            if (LoneTag(e))
            {
                _percentage = 1;
                Emit(e);
                _percentage = 0;
                return;
            }
            Emit(e);
            _b.Add(Op.Bar).Ref = new ScaleKey(0, 100, 0, 100);
        }

        private static bool LoneTag(Expr e) =>
            (e is Name nm && (nm.Ref is TagRefName || (nm.Ref is VarRef vr && vr.Kind == "locvar")))
            || e is Assoc;

        /// <summary>P4: the value, then the conversion of a tag value to its tag's format word
        /// (a lone tag, or a ':' format of one), an association's AssFmt + String; an expression
        /// without a tag gets nothing.</summary>
        private void DisplayField(Expr e)
        {
            _lastRef = null;
            Emit(e);
            // the conversion follows the LAST tag or association the field names, whatever the
            // value (corpus: Example `TimeToStr(PUMP_A_ART, 5)` ends with PUMP_A_ART's StringFmt 4,
            // `"@" + "(" + EquipGetProperty(?PID_Equip?, ...) + ")"` with AssFmt "?PID_Equip?")
            switch (_lastRef)
            {
                case TagRow row:
                    TagWord(row);
                    break;
                case NoRow _:
                    TagWord(null);
                    break;
                case AssocRef ar:
                    // an INT element (Status, OverrideMode, ControlMode): nothing; a TIMESTAMP /
                    // QUALITY item (VT, QT, T, Q): StringFmt 0xa; anything else AssFmt + String
                    // (corpus: Example !TagExtensions)
                    if (ar.ElemType == INT)
                        break;
                    if (ar.ElemType == TIMESTAMP || ar.ElemType == QUALITY)
                        StringFmt(0xA, "");
                    else
                    {
                        _b.Add(Op.AssFmt).Ref = ar.Text;
                        _b.Add(Op.String);
                    }
                    break;
            }
        }

        /// <summary>The last tag (a TagRow, or NoRow for an undefined one) or association
        /// (AssocRef) the parser read: a display field's conversion follows it (P4).</summary>
        private object? _lastRef;

        private sealed class NoRow
        {
            public static readonly NoRow It = new NoRow();
        }

        private sealed class AssocRef
        {
            public string Text = "";

            /// <summary>The type an element / item gives it (`?Tag?.Field.VT` TIMESTAMP), -1 an
            /// untyped element / item, -2 none.</summary>
            public int ElemType = -2;
        }

        private static readonly Dictionary<string, int> AssocTypes = new Dictionary<string, int>(
            StringComparer.Ordinal
        )
        {
            ["DIGITAL"] = Types.Digital,
            ["INT"] = Types.Int16,
            ["REAL"] = Types.Real,
            ["BCD"] = Types.Bcd,
            ["LONG"] = Types.Long,
            ["LONGBCD"] = Types.LongBcd,
            ["STRING"] = Types.String,
            ["BYTE"] = Types.Byte,
            ["UINT"] = Types.UInt,
            ["ULONG"] = Types.ULong,
            ["TAG"] = Types.Tag,
        };

        /// <summary>A super genie association ?name? / ?TYPE name? (P5): a tag reference whose
        /// TagName is the text '?name?', a typed one with its Convert after the Load.</summary>
        /// <summary>Tag elements: an association's '.Element' goes to TagElement.</summary>
        internal static readonly HashSet<string> Elements = new HashSet<string>(StringComparer.Ordinal)
        {
            "field",
            "valid",
            "override",
            "overridemode",
            "controlmode",
            "status",
        };

        private (TagKey key, int type, string text, int elemType) AssocParts(Assoc a)
        {
            string n = a.Name;
            int type = -1;
            int sp = n.IndexOf(' ');
            if (sp > 0 && AssocTypes.TryGetValue(CitectText.Upper(n.Substring(0, sp)), out int t))
            {
                type = t;
                n = CitectText.Trim(n.Substring(sp + 1));
            }
            string text = "?" + n + "?";
            // '.Element.Item' after the association go to TagElement / TagItem (corpus: Example
            // `?Tag?.Field.VT` = TagName '?Tag?', element 'Field', item 'VT'); any other suffix
            // (an equipment item `?__EquipmentRef?.PV`) stays in TagName
            string item = "",
                element = "",
                rest = a.Suffix;
            var parts = new List<string>(rest.Split('.'));
            if (rest.StartsWith(".", StringComparison.Ordinal) && parts.Count >= 2)
            {
                int last = parts.Count - 1;
                if (Items.Contains(Lexer.NameKey(parts[last])))
                {
                    item = parts[last];
                    parts.RemoveAt(last);
                    last--;
                }
                if (last >= 1 && Elements.Contains(Lexer.NameKey(parts[last])))
                {
                    element = parts[last];
                    parts.RemoveAt(last);
                }
                rest = string.Join(".", parts);
            }
            int elemType = -1;
            string ik = Lexer.NameKey(item),
                ek = Lexer.NameKey(element);
            if (ik == "vt" || ik == "qt" || ik == "t")
                elemType = Types.Timestamp;
            else if (ik == "q")
                elemType = Types.Quality;
            else if (ek == "status" || ek == "overridemode" || ek == "controlmode")
                elemType = INT;
            return (
                new TagKey("", text + rest, element, item, LoadPercentage()),
                type,
                text + a.Suffix,
                item.Length + element.Length > 0 ? elemType : -2
            );
        }

        private Val EmitAssoc(Assoc a)
        {
            var (key, type, text, elemType) = AssocParts(a);
            _b.Add(Op.Load).Ref = key;
            _lastRef = new AssocRef { Text = text, ElemType = elemType };
            if (elemType >= 0)
            {
                // an element / item has its own type (corpus: `?Tag?.Status` Convert 4, `.VT` Convert 0x13, `.Q` Convert 0x14)
                Convert(elemType);
                return new Val(elemType);
            }
            if (elemType == -1)
                return new Val(ANY) { Assoc = text };
            if (type >= 0)
            {
                Convert(type);
                // a typed association still converts to STRING with AssFmt + String (corpus: ExampleSA
                // `?STRING __AlarmTagName?` passed to a STRING parameter: Load; Convert 7; AssFmt; String)
                return new Val(type == Types.Digital || type == Types.Int16 ? INT : type) { Assoc = text };
            }
            return new Val(ANY) { Assoc = text };
        }

        private TagKey AssocKey(Assoc a) => AssocParts(a).key;

        private Val EmitRange(Range r, Val a)
        {
            double lo = r.Lo.IsInt ? r.Lo.I32 : r.Lo.RValue,
                hi = r.Hi.IsInt ? r.Hi.I32 : r.Hi.RValue;
            _b.Add(Op.Bar).Ref = new ScaleKey(lo, hi, lo, hi);
            return new Val(REAL);
        }

        private string EuSuffix(Val a) => a.Tag != null ? a.Tag.EngUnits : "";

        /// <summary>The compile-time intrinsics: IFDEF-type ones compile the text
        /// they pick; __LINE__ is the INT line.</summary>
        private Val EmitIntrinsic(Intrinsic it)
        {
            if (CompileUnit.IfdefCodes.Contains(it.Code))
            {
                var picked = _unit.CompileField(IfdefPick(it), "expression");
                if (picked.Errors.Count > 0 || !(picked.Node is Expr pe))
                    throw new CodegenException("IFDEF text does not compile", it.Line);
                Scan(pe);
                return Emit(pe);
            }
            if (it.Code == 3)
            {
                ConstInt(it.Line);
                return new Val(INT);
            }
            throw new CodegenException("intrinsic $" + it.Code, it.Line);
        }

        /// <summary>The argument text an IFDEF-type intrinsic compiles as
        /// sTrue when the tag is defined (an empty sTrue = the tag itself), else
        /// sFalse; the alarm variants test the alarm tables.</summary>
        private string IfdefPick(Intrinsic e)
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

        /// <summary>The name a CiVBA field's code is stored under: table + field + its DBF record
        /// number (1-based) (corpus: Example CiVBA page).</summary>
        internal string? VbaName;

        private Expr VbaCall(CiVBA vba)
        {
            if (VbaName == null)
                throw new CodegenException("CiVBA field without a record name", vba.Line);
            var res = _unit.CompileField(
                "VbCallReturn(VbCallRun(_VbExpressionOpen(\"" + VbaName + "\")))",
                "expression"
            );
            if (res.Errors.Count > 0 || !(res.Node is Expr e))
                throw new CodegenException("CiVBA call does not compile", vba.Line);
            return e;
        }
    }

    /// <summary>What a page field is.</summary>
    public enum PageFieldKind
    {
        None,
        Expression,
        Range,
        Display,
        Command,
    }
}
