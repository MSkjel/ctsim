using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using Index = CicodeSim.Compiler.Syntax.Index;
using Range = CicodeSim.Compiler.Syntax.Range;

namespace CicodeSim.Compiler.Semantics
{
    /// <summary>What a built-in looks a string argument up as.</summary>
    public enum NameRole
    {
        None,

        /// <summary>A graphics page (_PageDisplay, WinNewAt ...).</summary>
        Page,

        /// <summary>A variable tag (TagRead, Ass ...).</summary>
        Tag,
    }

    /// <summary>A string literal of a field that reaches a looked-up argument: the built-in
    /// <see cref="Primitive"/> takes it as a <see cref="Role"/> name.</summary>
    public readonly struct NameUse
    {
        public readonly Str Literal;
        public readonly NameRole Role;
        public readonly string Primitive;

        public NameUse(Str literal, NameRole role, string primitive)
        {
            Literal = literal;
            Role = role;
            Primitive = primitive;
        }
    }

    /// <summary>Which arguments of a unit's functions reach a built-in's page or tag argument
    /// unchanged: a parameter passed on as itself to such an argument (of the built-in, or of a
    /// function whose parameter does) before the function assigns it (Include's PageDisplay passes
    /// sPageName to _PageDisplay, AssWin sPage to WinNewAt and sTag1..8 through AssVarTags to Ass).
    /// Compile time only: a value computed at runtime is not followed.</summary>
    public sealed class NameArgs
    {
        /// <summary>The role of a built-in's argument (by FUNC0 name and 0-based index).</summary>
        public delegate NameRole BuiltinRole(string builtin, int arg);

        private readonly Dictionary<FuncInfo, Dictionary<int, (NameRole Role, string Primitive)>> _params =
            new Dictionary<FuncInfo, Dictionary<int, (NameRole, string)>>();
        private readonly BuiltinRole _builtin;

        private static readonly ConditionalWeakTable<CompileUnit, NameArgs> Cache =
            new ConditionalWeakTable<CompileUnit, NameArgs>();
        private int _generation;

        private NameArgs(BuiltinRole builtin)
        {
            _builtin = builtin;
        }

        /// <summary>The analysis of <paramref name="unit"/> (kept until the unit's next edit).</summary>
        public static NameArgs For(CompileUnit unit, BuiltinRole builtin)
        {
            lock (Cache)
            {
                if (
                    Cache.TryGetValue(unit, out NameArgs? had)
                    && had._generation == unit.Generation
                    && had._builtin == builtin
                )
                    return had;
                var r = new NameArgs(builtin) { _generation = unit.Generation };
                r.Solve(unit);
                Cache.Remove(unit);
                Cache.Add(unit, r);
                return r;
            }
        }

        /// <summary>The role parameter <paramref name="index"/> of <paramref name="f"/> passes its
        /// value on to, None when none.</summary>
        public (NameRole Role, string Primitive) Param(FuncInfo f, int index) =>
            _params.TryGetValue(f, out var m) && m.TryGetValue(index, out var r) ? r : (NameRole.None, "");

        /// <summary>The role argument <paramref name="index"/> of a resolved call has.</summary>
        public (NameRole Role, string Primitive) Arg(FuncRef? r, int index)
        {
            if (r == null)
                return (NameRole.None, "");
            if (r.Builtin != null)
            {
                var role = _builtin(r.Builtin.Name, index);
                return (role, role == NameRole.None ? "" : r.Builtin.Name);
            }
            return r.Func != null ? Param(r.Func, index) : (NameRole.None, "");
        }

        /// <summary>The non-blank string literals of a field (an Expr or a statement list) that
        /// reach a page or tag argument, in source order.</summary>
        public List<NameUse> Literals(object? field)
        {
            var o = new List<NameUse>();
            Walk(
                new Walker(this, null, null, (lit, role, prim) => o.Add(new NameUse(lit, role, prim))),
                field
            );
            return o;
        }

        /// <summary>Every expression of a field (an Expr or a statement list), assignment targets
        /// included, in source order.</summary>
        public static void Visit(object? field, Action<Expr> visit) =>
            Walk(new Walker(null, null, null, null, null, visit), field);

        private static void Walk(Walker w, object? field)
        {
            if (field is List<Stmt> body)
                w.Stmts(body);
            else if (field is Expr e)
                w.Expr(e);
        }

        // ------------------------------------------------------------------ the fixed point
        private void Solve(CompileUnit unit)
        {
            var fns = new List<FuncInfo>(unit.Functions);
            var seen = new HashSet<FuncInfo>(fns);
            foreach (var f in unit.Private.Values)
                if (seen.Add(f))
                    fns.Add(f);
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var f in fns)
                {
                    var w = new Walker(this, f, new HashSet<int>(), null, (slot, role, prim) =>
                    {
                        if (!_params.TryGetValue(f, out var m))
                            _params[f] = m = new Dictionary<int, (NameRole, string)>();
                        if (m.ContainsKey(slot))
                            return;
                        m[slot] = (role, prim);
                        changed = true;
                    });
                    w.Stmts(f.Node.Body);
                }
            }
        }

        /// <summary>One pass over code in source order: a parameter assigned (or passed by
        /// reference) stops counting from there on.</summary>
        private sealed class Walker
        {
            private readonly NameArgs? _a;
            private readonly FuncInfo? _fn;
            private readonly HashSet<int>? _assigned;
            private readonly Action<Str, NameRole, string>? _literal;
            private readonly Action<int, NameRole, string>? _param;
            private readonly Action<Expr>? _visit;

            public Walker(
                NameArgs? a,
                FuncInfo? fn,
                HashSet<int>? assigned,
                Action<Str, NameRole, string>? literal,
                Action<int, NameRole, string>? param = null,
                Action<Expr>? visit = null
            )
            {
                _a = a;
                _fn = fn;
                _assigned = assigned;
                _literal = literal;
                _param = param;
                _visit = visit;
            }

            private int ParamSlot(Expr e) =>
                _fn != null
                && e is Name n
                && n.Ref is VarRef v
                && v.Kind == "param"
                && v.Slot >= 0
                && v.Slot < _fn.Node.Params.Count
                    ? v.Slot
                    : -1;

            private void Assigned(Expr target)
            {
                int s = ParamSlot(target);
                if (s >= 0)
                    _assigned!.Add(s);
            }

            public void Stmts(List<Stmt>? body)
            {
                if (body == null)
                    return;
                foreach (var s in body)
                    Stmt(s);
            }

            private void Stmt(Stmt s)
            {
                switch (s)
                {
                    case LocalDecl d:
                        Expr(d.Init);
                        break;
                    case Assign a:
                        Expr(a.Target);
                        Expr(a.Value);
                        Assigned(a.Target);
                        break;
                    case ExprStmt x:
                        Expr(x.Expr);
                        break;
                    case If i:
                        Expr(i.Cond);
                        Stmts(i.Then);
                        Stmts(i.Else);
                        break;
                    case While w:
                        Expr(w.Cond);
                        Stmts(w.Body);
                        break;
                    case For f:
                        Expr(f.Var);
                        Expr(f.Start);
                        Expr(f.Stop);
                        Assigned(f.Var);
                        Stmts(f.Body);
                        break;
                    case Select sel:
                        Expr(sel.Selector);
                        Stmts(sel.Pre);
                        foreach (var c in sel.Cases)
                        {
                            foreach (var it in c.Items)
                            {
                                Expr(it.Lo);
                                Expr(it.Hi);
                            }
                            Stmts(c.Body);
                        }
                        Stmts(sel.Else);
                        break;
                    case Return r:
                        Expr(r.Value);
                        break;
                }
            }

            public void Expr(Expr? e)
            {
                if (e != null)
                    _visit?.Invoke(e);
                switch (e)
                {
                    case Call c:
                        Call(c);
                        break;
                    case Unary u:
                        Expr(u.Operand);
                        break;
                    case Binary b:
                        Expr(b.Left);
                        Expr(b.Right);
                        break;
                    case Index ix:
                        Expr(ix.Base);
                        foreach (var k in ix.Indices)
                            Expr(k);
                        break;
                    case Format f:
                        Expr(f.Operand);
                        break;
                    case Range r:
                        Expr(r.Operand);
                        break;
                }
            }

            private void Call(Call c)
            {
                for (int i = 0; i < c.Args.Count; i++)
                {
                    var arg = c.Args[i];
                    Expr(arg);
                    var (role, prim) = _a != null ? _a.Arg(c.Ref, i) : (NameRole.None, "");
                    if (role != NameRole.None)
                    {
                        if (_literal != null && arg is Str lit && CitectText.Trim(lit.Value).Length > 0)
                            _literal(lit, role, prim);
                        int slot = ParamSlot(arg);
                        if (_param != null && slot >= 0 && !_assigned!.Contains(slot))
                            _param(slot, role, prim);
                    }
                    // a built-in's reference argument writes it
                    if (
                        _assigned != null
                        && c.Ref?.Builtin is Func0Row b
                        && i < b.Args.Count
                        && b.Args[i].byRef
                    )
                        Assigned(arg);
                }
            }
        }
    }
}
