using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Syntax
{
    /// <summary>A structural JSON dump of a syntax tree.</summary>
    public static class AstDump
    {
        private sealed class Block
        {
            public readonly string Label;
            public readonly List<object?> Items;

            public Block(string label, IEnumerable<object?> items)
            {
                Label = label;
                Items = new List<object?>(items);
            }
        }

        private static IEnumerable<object?> Of<T>(IEnumerable<T>? xs)
        {
            if (xs == null)
                yield break;
            foreach (var x in xs)
                yield return x;
        }

        /// <summary>The dump lines of <paramref name="root"/> (a Node, a statement list, or null).
        /// <paramref name="real"/> writes the value of a REAL number or literal (default: the
        /// shortest text that reads back as the same double; the golden tests pass the IEEE bits).</summary>
        public static IEnumerable<string> Lines(object? root, string tag, Func<double, string>? real = null)
        {
            real ??= FloatText.Shortest;
            var stack = new Stack<(object? node, int depth)>();
            if (root is System.Collections.IList list && !(root is Node))
                stack.Push((new Block("root", OfList(list)), 0));
            else
                stack.Push((root, 0));
            var sb = new StringBuilder(128);
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                sb.Clear();
                sb.Append('[');
                Str(sb, tag);
                sb.Append(',').Append(depth.ToString(CultureInfo.InvariantCulture));
                var kids = new List<object?>();
                Emit(sb, node, kids, real);
                sb.Append(']');
                yield return sb.ToString();
                for (int k = kids.Count - 1; k >= 0; k--)
                    stack.Push((kids[k], depth + 1));
            }
        }

        private static IEnumerable<object?> OfList(System.Collections.IList list)
        {
            foreach (var x in list)
                yield return x;
        }

        private static void Emit(
            StringBuilder sb,
            object? node,
            List<object?> kids,
            Func<double, string> real
        )
        {
            if (node == null)
            {
                // the dump's word for an absent node (the golden AST digests hold it)
                sb.Append(",\"None\"");
                return;
            }
            if (node is Block b)
            {
                sb.Append(",\"Block\",");
                Str(sb, b.Label);
                sb.Append(',').Append(b.Items.Count.ToString(CultureInfo.InvariantCulture));
                kids.AddRange(b.Items);
                return;
            }
            var n = (Node)node;
            sb.Append(',');
            Str(sb, n.GetType().Name);
            Int(sb, n.Line);
            Int(sb, n.Pos);
            if (n is Expr e)
            {
                if (e.Ty.HasValue)
                    Int(sb, e.Ty.Value);
                else
                    sb.Append(",null");
                Bool(sb, e.Paren);
                Int(sb, e.End);
                Int(sb, e.EndTok);
            }
            switch (n)
            {
                case Num num:
                    S(sb, num.Kind);
                    S(sb, num.IsInt ? num.IValue.ToString(CultureInfo.InvariantCulture) : real(num.RValue));
                    S(sb, num.Text);
                    break;
                case Str s:
                    S(sb, s.Value);
                    break;
                case Name nm:
                    S(sb, nm.Text);
                    S(sb, RefText(nm.Ref));
                    break;
                case Index ix:
                    kids.Add(ix.Base);
                    kids.Add(new Block("indices", Of(ix.Indices)));
                    break;
                case Call c:
                    S(sb, c.Name);
                    S(sb, RefText(c.Ref));
                    Int(sb, c.RParen);
                    kids.Add(new Block("args", Of(c.Args)));
                    break;
                case Unary u:
                    S(sb, u.Op);
                    kids.Add(u.Operand);
                    break;
                case Binary bi:
                    S(sb, bi.Op);
                    kids.Add(bi.Left);
                    kids.Add(bi.Right);
                    break;
                case FormatSpec fs:
                    S(sb, fs.Kind);
                    S(sb, fs.Text);
                    S(sb, fs.Width.ToString(CultureInfo.InvariantCulture));
                    S(sb, fs.Decimals.ToString(CultureInfo.InvariantCulture));
                    Bool(sb, fs.ZeroPad);
                    Bool(sb, fs.Left);
                    Bool(sb, fs.Exponent);
                    Bool(sb, fs.Eu);
                    break;
                case Format f:
                    kids.Add(f.Operand);
                    kids.Add(f.Spec);
                    break;
                case Intrinsic it:
                    sb.Append(',').Append(it.CodeDigits);
                    S(sb, it.Label);
                    foreach (string a in it.Args)
                        S(sb, a);
                    break;
                case Assoc a:
                    S(sb, a.Name);
                    S(sb, a.Suffix);
                    break;
                case Range r:
                    kids.Add(r.Operand);
                    kids.Add(r.Lo);
                    kids.Add(r.Hi);
                    break;
                case CiVBA v:
                    S(sb, v.Text);
                    break;
                case GenieParam g:
                    S(sb, g.Name);
                    break;
                case Empty _:
                    break;
                case LocalDecl ld:
                    S(sb, ld.Type);
                    S(sb, ld.Name);
                    S(sb, RefText(ld.Var));
                    kids.Add(ld.Init);
                    break;
                case Assign asg:
                    kids.Add(asg.Target);
                    kids.Add(asg.Value);
                    break;
                case ExprStmt es:
                    Bool(sb, es.AfterVoidReturn);
                    Int(sb, es.EndPos);
                    Bool(sb, es.NameFirst);
                    kids.Add(es.Expr);
                    break;
                case If iff:
                    Int(sb, iff.ElseLine);
                    Int(sb, iff.EndLine);
                    kids.Add(iff.Cond);
                    kids.Add(new Block("then", Of(iff.Then)));
                    kids.Add(iff.Else != null ? new Block("else", Of(iff.Else)) : null);
                    break;
                case While w:
                    Int(sb, w.EndLine);
                    kids.Add(w.Cond);
                    kids.Add(new Block("body", Of(w.Body)));
                    break;
                case For fo:
                    Bool(sb, fo.Complete);
                    Int(sb, fo.EndLine);
                    kids.Add(fo.Var);
                    kids.Add(fo.Start);
                    kids.Add(fo.Stop);
                    kids.Add(new Block("body", Of(fo.Body)));
                    break;
                case CaseItem ci:
                    S(sb, ci.Kind);
                    S(sb, ci.Op);
                    kids.Add(ci.Lo);
                    kids.Add(ci.Hi);
                    break;
                case Case cs:
                    kids.Add(new Block("items", Of(cs.Items)));
                    kids.Add(new Block("body", Of(cs.Body)));
                    break;
                case Select sel:
                    Int(sb, sel.ElseLine);
                    Int(sb, sel.EndLine);
                    kids.Add(sel.Selector);
                    kids.Add(new Block("pre", Of(sel.Pre)));
                    kids.Add(new Block("cases", Of(sel.Cases)));
                    kids.Add(sel.Else != null ? new Block("else", Of(sel.Else)) : null);
                    break;
                case Return ret:
                    kids.Add(ret.Value);
                    break;
                case Param p:
                    S(sb, p.Type);
                    S(sb, p.Name);
                    kids.Add(p.Default);
                    break;
                case Literal lit:
                    S(sb, lit.Kind);
                    S(
                        sb,
                        lit.Kind == "INT" ? lit.IValue.ToString(CultureInfo.InvariantCulture)
                            : lit.Kind == "REAL" ? real(lit.RValue)
                            : lit.SValue
                    );
                    break;
                case VarDecl vd:
                    S(sb, vd.Scope);
                    S(sb, vd.Type);
                    S(sb, vd.Name);
                    var dims = new List<string>();
                    foreach (long d in vd.Dims)
                        dims.Add(d.ToString(CultureInfo.InvariantCulture));
                    S(sb, string.Join("x", dims));
                    S(sb, RefText(vd.Var));
                    kids.Add(new Block("init", Of(vd.Init)));
                    break;
                case FunctionDef fd:
                    S(sb, fd.Name);
                    S(sb, fd.Scope);
                    S(sb, fd.RetType);
                    Bool(sb, fd.HasParens);
                    Int(sb, fd.EndLine);
                    Int(sb, fd.EndPos);
                    Bool(sb, fd.ReturnsValue);
                    Int(sb, fd.NamePos);
                    Bool(sb, fd.Complete);
                    Int(sb, fd.BrokenReturns);
                    Bool(sb, fd.HeaderAborted);
                    Int(sb, fd.AfterEndPos);
                    Int(sb, fd.Locals.Count);
                    kids.Add(new Block("params", Of(fd.Params)));
                    kids.Add(new Block("body", Of(fd.Body)));
                    break;
                case Module m:
                    S(sb, m.Project);
                    Int(sb, m.StopPos);
                    var calls = new List<string>(m.CallNames);
                    calls.Sort(StringComparer.Ordinal);
                    S(sb, string.Join(",", calls));
                    kids.Add(new Block("items", Of(m.Items)));
                    break;
            }
        }

        /// <summary>What a name resolved to, as text.</summary>
        public static string? RefText(object? r)
        {
            switch (r)
            {
                case null:
                    return null;
                case VarRef v:
                    return "var:"
                        + v.Kind
                        + ":"
                        + v.Name
                        + ":"
                        + v.Type.ToString(CultureInfo.InvariantCulture)
                        + ":"
                        + v.Slot.ToString(CultureInfo.InvariantCulture);
                case TagRefName t:
                    return "tag:"
                        + t.Name
                        + ":"
                        + (t.Defined ? "1" : "0")
                        + ":"
                        + t.Type.ToString(CultureInfo.InvariantCulture);
                case FuncRef f:
                    return "func:"
                        + f.Name
                        + ":"
                        + (
                            f.Func != null ? "user:" + f.Func.SymbolName
                            : f.Builtin != null ? "builtin:" + f.Builtin.Name
                            : "unknown"
                        )
                        + ":"
                        + f.Opcode.ToString(CultureInfo.InvariantCulture);
            }
            return r.GetType().Name;
        }

        /// <summary>One diagnostic as a JSON array: [tag, code, file, line, col, context, project,
        /// pos, seq, message].</summary>
        public static string DiagLine(string tag, Diagnostic d)
        {
            var sb = new StringBuilder(96);
            sb.Append('[');
            Str(sb, tag);
            Int(sb, d.Code);
            S(sb, d.File);
            Int(sb, d.Line);
            Int(sb, d.Col);
            S(sb, d.Context);
            S(sb, d.Project);
            Int(sb, d.Pos);
            Int(sb, d.Seq);
            S(sb, d.Message);
            sb.Append(']');
            return sb.ToString();
        }

        private static void S(StringBuilder sb, string? s)
        {
            sb.Append(',');
            Str(sb, s);
        }

        private static void Int(StringBuilder sb, long v) =>
            sb.Append(',').Append(v.ToString(CultureInfo.InvariantCulture));

        private static void Bool(StringBuilder sb, bool b) => sb.Append(b ? ",true" : ",false");

        /// <summary>A JSON string (every non-ASCII character as a \u escape).</summary>
        public static void Str(StringBuilder sb, string? s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20 || c > 0x7E)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
