using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// Run-time call text: TaskNew / TaskCall compile a name and argument text into one call.
    /// </summary>
    public static class CallText
    {
        private const int TEnd = 0,
            TComma = 0x2c,
            TMinus = 0x2d,
            TClose = 0x29,
            TName = 0x100,
            TString = 0x101,
            TInt = 0x103,
            TReal = 0x104;

        private struct Tok
        {
            public int Kind;
            public string Text;
            public long I;
            public double D;
        }

        // the compiler's name characters (Lexer: the code-page letters decoded) plus '.'
        private static bool NameChar(char c) => CicodeSim.Compiler.Syntax.Lexer.IsNameChar(c) || c == '.';

        private static List<Tok> Lex(string s)
        {
            var o = new List<Tok>();
            int i = 0;
            while (true)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
                    i++;
                if (i >= s.Length || s[i] == '\0')
                {
                    o.Add(new Tok { Kind = TEnd, Text = "" });
                    return o;
                }
                char c = s[i];
                if (c == '"')
                {
                    // ^ escapes (^0 = NUL, ^v = 0x11, ^(^) ^, ^@ kept)
                    var sb = new StringBuilder();
                    i++;
                    while (i < s.Length && s[i] != '"')
                    {
                        char ch = s[i];
                        if (ch == '^' && i + 1 < s.Length)
                        {
                            char e = s[i + 1];
                            char el = char.ToLowerInvariant(e);
                            i += 2;
                            if (el == 'n')
                                sb.Append('\n');
                            else if (el == 'r')
                                sb.Append('\r');
                            else if (el == 't')
                                sb.Append('\t');
                            else if (el == 'f')
                                sb.Append('\f');
                            else if (el == 'v')
                                sb.Append('\x11');
                            else if (e == '0')
                            {
                                if (
                                    i + 2 < s.Length
                                    && (s[i] == 'x' || s[i] == 'X')
                                    && Hex(s[i + 1]) >= 0
                                    && Hex(s[i + 2]) >= 0
                                )
                                {
                                    sb.Append((char)(Hex(s[i + 1]) * 16 + Hex(s[i + 2])));
                                    i += 3;
                                }
                                else
                                    sb.Append('\0');
                            }
                            else if (e == '(' || e == ')' || e == ',' || e == '@')
                                sb.Append('^').Append(e);
                            else
                                sb.Append(e);
                            continue;
                        }
                        sb.Append(ch);
                        i++;
                    }
                    if (i < s.Length)
                        i++;
                    o.Add(new Tok { Kind = TString, Text = sb.ToString() });
                    continue;
                }
                if (c >= '0' && c <= '9')
                {
                    int st = i;
                    if (c == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                    {
                        i += 2;
                        long v = 0;
                        while (i < s.Length && Hex(s[i]) >= 0)
                            v = (v * 16 + Hex(s[i++])) & 0xFFFFFFFFL;
                        o.Add(
                            new Tok
                            {
                                Kind = TInt,
                                I = (int)v,
                                Text = s.Substring(st, i - st),
                            }
                        );
                        continue;
                    }
                    while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                        i++;
                    bool real = false;
                    if (i < s.Length && s[i] == '.')
                    {
                        real = true;
                        i++;
                        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                            i++;
                    }
                    if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                    {
                        int save = i;
                        i++;
                        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                            i++;
                        if (i < s.Length && s[i] >= '0' && s[i] <= '9')
                        {
                            real = true;
                            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                                i++;
                        }
                        else
                            i = save;
                    }
                    string txt = s.Substring(st, i - st);
                    if (real)
                        o.Add(
                            new Tok
                            {
                                Kind = TReal,
                                D = double.Parse(txt, NumberStyles.Float, CultureInfo.InvariantCulture),
                                Text = txt,
                            }
                        );
                    else
                    {
                        // 32-bit like the compiler's literals (wraps)
                        ulong u = 0;
                        foreach (char d in txt)
                            u = unchecked(u * 10 + (ulong)(d - '0')) & 0xFFFFFFFFUL;
                        o.Add(
                            new Tok
                            {
                                Kind = TInt,
                                I = (int)(uint)u,
                                Text = txt,
                            }
                        );
                    }
                    continue;
                }
                if (NameChar(c))
                {
                    int st = i;
                    while (i < s.Length && NameChar(s[i]))
                        i++;
                    o.Add(new Tok { Kind = TName, Text = s.Substring(st, i - st) });
                    continue;
                }
                o.Add(new Tok { Kind = c, Text = c.ToString() });
                i++;
            }
        }

        private static int Hex(char c) =>
            c >= '0' && c <= '9' ? c - '0'
            : c >= 'a' && c <= 'f' ? c - 'a' + 10
            : c >= 'A' && c <= 'F' ? c - 'A' + 10
            : -1;

        /// <summary>_gcvt(x, 5): %.5g with the exponent as the CRT prints it (e+NNN).</summary>
        internal static string Gcvt5(double x)
        {
            if (x == 0)
                return "0";
            string r = x.ToString("G5", CultureInfo.InvariantCulture);
            int e = r.IndexOf('E');
            if (e >= 0)
            {
                string mant = r.Substring(0, e);
                string exp = r.Substring(e + 1);
                bool neg = exp.StartsWith("-", StringComparison.Ordinal);
                string digits = exp.TrimStart('+', '-');
                while (digits.Length < 3)
                    digits = "0" + digits;
                if (!mant.Contains("."))
                    mant += ".";
                return mant + "e" + (neg ? "-" : "+") + digits;
            }
            if (!r.Contains(".") && Math.Abs(x) >= 1)
                r += ".";
            return r;
        }

        /// <summary>DLLCall's argument text, as (text, integer, real) per argument.</summary>
        internal static List<(string Text, long I, double D)> DllArgs(string argText, int count)
        {
            var toks = Lex(argText ?? "");
            int p = 0;
            Tok Next() => p < toks.Count ? toks[p++] : new Tok { Kind = TEnd, Text = "" };
            var r = new List<(string, long, double)>();
            Tok cur = Next();
            for (int n = 0; n < count; n++)
            {
                bool neg = false;
                if (cur.Kind == TMinus)
                {
                    neg = true;
                    cur = Next();
                }
                switch (cur.Kind)
                {
                    case TName:
                    case TString:
                        r.Add((cur.Text, Conv.StrToInt(cur.Text, out _), Conv.ParseReal(cur.Text, out _)));
                        break;
                    case TInt:
                    {
                        long iv = neg ? (int)-cur.I : cur.I;
                        r.Add((iv.ToString(CultureInfo.InvariantCulture), iv, iv));
                        break;
                    }
                    case TReal:
                    {
                        double dv = neg ? -cur.D : cur.D;
                        long iv = double.IsNaN(dv) || Math.Abs(dv) >= 2147483648.0 ? int.MinValue : (int)dv;
                        r.Add((Gcvt5(dv), iv, dv));
                        break;
                    }
                    default:
                        r.Add(("", 0, 0.0));
                        break;
                }
                cur = Next();
                if (cur.Kind == TComma)
                    cur = Next();
            }
            return r;
        }

        /// <summary>_gcvt(x, digits): %.{digits}g with the exponent and the trailing point as the CRT prints them.</summary>
        internal static string Gcvt(double x, int digits)
        {
            if (x == 0)
                return "0";
            string r = x.ToString(
                "G" + digits.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture
            );
            int e = r.IndexOf('E');
            if (e >= 0)
            {
                string mant = r.Substring(0, e);
                string exp = r.Substring(e + 1);
                bool neg = exp.StartsWith("-", StringComparison.Ordinal);
                string d = exp.TrimStart('+', '-');
                while (d.Length < 3)
                    d = "0" + d;
                if (!mant.Contains("."))
                    mant += ".";
                return mant + "e" + (neg ? "-" : "+") + d;
            }
            if (!r.Contains(".") && Math.Abs(x) >= 1)
                r += ".";
            return r;
        }

        /// <summary>The arguments of a call of <paramref name="fn"/> with <paramref name="argText"/>;
        /// error = 0, or 433 / 529 (no call), or 518 (call made, extra text).</summary>
        public static List<Value> Bind(Function fn, string argText, out int error) =>
            Bind(fn, argText, out error, out _);

        /// <summary><see cref="Bind(Function, string, out int)"/> with the kernel message
        /// the runtime writes for 433 and 529, '' otherwise (live:
        /// conformance/runtime/ctapi/tasknew_int_param_non_numeric.json).</summary>
        public static List<Value> Bind(Function fn, string argText, out int error, out string message)
        {
            error = 0;
            message = "";
            var toks = Lex(argText ?? "");
            int p = 0;
            Tok Next() => p < toks.Count ? toks[p++] : new Tok { Kind = TEnd, Text = "" };
            var vals = new List<Value>();
            Tok cur = Next();
            int n = 0;
            foreach (char sig in fn.Args)
            {
                n++;
                int type = Types.SigType(sig);
                bool neg = false;
                if (cur.Kind == TMinus)
                {
                    neg = true;
                    cur = Next();
                }
                string text;
                long iv;
                double dv;
                bool present = true;
                switch (cur.Kind)
                {
                    case TComma:
                        text = "";
                        iv = 0;
                        dv = 0.0;
                        break;
                    case TName:
                    case TString:
                        text = cur.Text;
                        if (
                            type == Types.Int16
                            || type == Types.Real
                            || type == Types.Long
                            || type == Types.Timestamp
                            || type == Types.Quality
                        )
                        {
                            string t = text.TrimStart(' ', '\t', '\n', '\v', '\f', '\r');
                            if (t.Length > 0 && !(t[0] >= '0' && t[0] <= '9') && t[0] != '-' && t[0] != '+')
                            {
                                error = Errors.TypeMismatch;
                                // a value of 11+ characters is cut: '%-10.10s...'
                                message =
                                    "Function "
                                    + fn.Name
                                    + ", parameter "
                                    + W.Inv(n)
                                    + ", number type ("
                                    + W.Inv(type)
                                    + ") parameter expected, value="
                                    + (text.Length < 11 ? text : text.Substring(0, 10) + "...");
                                return vals;
                            }
                        }
                        iv = Conv.StrToInt(text, out _);
                        dv = Conv.ParseReal(text, out _);
                        break;
                    case TInt:
                        iv = neg ? (int)-cur.I : cur.I;
                        dv = iv;
                        text = iv.ToString(CultureInfo.InvariantCulture);
                        break;
                    case TReal:
                        dv = neg ? -cur.D : cur.D;
                        iv = double.IsNaN(dv) || Math.Abs(dv) >= 2147483648.0 ? int.MinValue : (int)dv;
                        text = Gcvt5(dv);
                        break;
                    default:
                        present = false;
                        text = "";
                        iv = 0;
                        dv = 0.0;
                        if (n <= fn.MinParam)
                        {
                            error = Errors.TooFewArgs;
                            message = "Not enough arguments for function " + fn.Name;
                            return vals;
                        }
                        break;
                }
                if (!present)
                    break;
                if (cur.Kind != TEnd)
                    cur = Next();
                if (cur.Kind == TComma)
                    cur = Next();
                switch (type)
                {
                    case Types.Real:
                        vals.Add(Value.Real(dv));
                        break;
                    case Types.String:
                        vals.Add(Value.Str(A.Cap(text)));
                        break;
                    case Types.Timestamp:
                    case Types.Quality:
                        vals.Add(Value.Int(type, iv));
                        break;
                    case Types.Object:
                        // default: no OBJECT parameter from call text
                        error = Errors.TypeMismatch;
                        return vals;
                    default:
                        vals.Add(Value.Long((int)iv));
                        break;
                }
            }
            if (cur.Kind == TClose)
                cur = Next();
            if (cur.Kind != TEnd)
                error = Errors.TooManyArgs;
            return vals;
        }

        /// <summary>The user function a call text names: its own name, else the
        /// caller's file's PRIVATE function of that name.</summary>
        public static Function? Find(CicodeTask t, string name)
        {
            var lib = t.Vm.Library;
            string n = A.SkipSpace(name).TrimEnd(' ', '\t');
            Function? f = lib.Function(n);
            if (f != null && !f.Private)
                return f;
            Function? cur = t.Func;
            if (cur != null && cur.Filename.Length > 0)
            {
                string stem = Path.GetFileNameWithoutExtension(cur.Filename);
                string? dir = Path.GetDirectoryName(cur.Filename);
                string proj = dir != null ? Path.GetFileName(dir) : "";
                Function? p = lib.Function(n, stem, proj);
                if (p != null)
                    return p;
            }
            return f != null && !f.Private ? f : null;
        }

        /// <summary>A task that calls the built-in a call text names, or null.</summary>
        public static CicodeTask? BuiltinTask(
            CicodeTask t,
            string name,
            string args,
            out Compiled.Builtin? b,
            out int err,
            out string msg
        )
        {
            err = 0;
            msg = "";
            b = t.Vm.Table.Lookup(A.SkipSpace(name).TrimEnd(' ', '	'));
            if (b == null)
            {
                err = Errors.InvalidFunction;
                return null;
            }
            for (int k = 0; k < b.Args.Length; k++)
                if (b.ByRef(k))
                {
                    err = Errors.TypeMismatch;
                    return null;
                }
            var sig = new Function(b.Name, b.Args, b.Ret, Array.Empty<byte>()) { MinParam = b.MinArgs };
            var vals = Bind(sig, args, out err, out msg);
            if (err == Errors.TypeMismatch || err == Errors.TooFewArgs)
                return null;
            var pools = new Pools();
            var cb = new CodeBuilder(pools);
            foreach (var v in vals)
            {
                if (v.T == Types.String)
                    cb.ConstStr(v.S ?? "");
                else if (Types.IsRealType(v.T))
                    cb.ConstReal(v.D);
                else if (v.T == Types.Timestamp || v.T == Types.Quality)
                    cb.ConstU64(v.T, unchecked((ulong)v.I));
                else
                    cb.ConstInt(v.I);
            }
            cb.Func(b.Index, vals.Count);
            cb.End();
            var prog = pools.Fill(new Program());
            var task = t.Vm.TaskForBlock(cb.Code(), prog, false, b.Name);
            return task.Done ? null : task;
        }
    }

    /// <summary>Task built-ins over the VM scheduler.</summary>
    public static class Tasks
    {
        internal static CicodeTask? Find(CicodeTask t, long h)
        {
            var s = t.Vm.Scheduler;
            if (h == t.Handle && !t.Done)
                return t;
            if (s == null)
                return null;
            return s.Tasks.TryGetValue((int)h, out var x) && !x.Done ? x : null;
        }

        /// <summary>A live task named <paramref name="name"/> (ASCII letters without case on the
        /// task name), its handle or -1.</summary>
        internal static int ByName(CicodeTask t, string name)
        {
            var s = t.Vm.Scheduler;
            if (s != null)
                foreach (var x in s.All)
                    if (!x.Done && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                        return x.Handle;
            if (!t.Done && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                return t.Handle;
            return -1;
        }

        private static Value NewTask(
            SimHost h,
            CicodeTask t,
            ArgList a,
            string name,
            string args,
            long mode,
            long pollMs,
            string cluster
        )
        {
            if ((mode & 8) != 0 && ByName(t, name) != -1)
                return W.I(-1);
            if (pollMs != -1 && (pollMs < 1 || pollMs > 60000))
            {
                W.Raise(t, Errors.InvalidArg);
                return W.I(-1);
            }
            if (cluster.Length > 0 && cluster != "-" && h.World != null)
            {
                // the cluster name looked up; unknown: 274 (raised)
                bool known = false;
                foreach (string c in h.World.Tags.Clusters())
                    if (string.Equals(c, cluster, StringComparison.OrdinalIgnoreCase))
                        known = true;
                if (!known)
                {
                    W.Raise(t, Errors.InvalidArg);
                    return W.I(-1);
                }
            }
            var sched = t.Vm.Scheduler;
            if (sched == null)
                return W.I(-1);
            Function? fn = CallText.Find(t, name);
            CicodeTask? bt = null;
            if (fn == null)
            {
                // a built-in of that name (the runtime compiles any function call text)
                bt = CallText.BuiltinTask(t, name, args, out _, out int berr, out string bmsg);
                if (bmsg.Length > 0)
                    h.Note(t, bmsg);
                if (bt == null)
                    return W.I(-1); // the call text does not compile: -1, no error code
            }
            List<Value>? vals = null;
            if (fn != null)
            {
                vals = CallText.Bind(fn, args, out int err, out string msg);
                if (msg.Length > 0)
                    h.Note(t, msg);
                if (err == Errors.TypeMismatch || err == Errors.TooFewArgs)
                    return W.I(-1);
            }
            CicodeTask nt;
            try
            {
                nt = sched.Add(bt ?? t.Vm.TaskForCall(fn!, vals!, false, null, fn!.Name));
            }
            catch (HardwareError)
            {
                return W.I(-1); // out of Cicode threads: -> -1
            }
            nt.Data["taskmode"] = mode;
            // no cluster: the caller's cluster context
            if (cluster.Length == 0 && t.Data.TryGetValue("cluster", out object? own) && own is string oc)
                cluster = oc;
            if (cluster.Length > 0)
                nt.Data["cluster"] = cluster;
            // a new task runs in its creator's window (the scheduler selects the
            // task's window before it runs), its AN -1
            if (t.Data.TryGetValue(CicodeSim.World.Display.TaskContext.WindowKey, out object? win))
                nt.Data[CicodeSim.World.Display.TaskContext.WindowKey] = win;
            return W.I(nt.Handle);
        }

        [Builtin(
            "TaskNew",
            Doc = "1193669",
            Effects = "starts a Cicode task in the VM's scheduler",
            Errors = "274 unknown cluster (returns -1)",
            Notes = "Mode 8: -1 when a live task of that name exists; modes 1 (page) and 2 (window) are kept in the task's data (no page / window ends it yet); 16 (no I/O wait) starts at once like every simulated task. A name with no user function starts the built-in of that name (as TaskCall). An unknown function or bad argument text returns -1 without an error (433 / 529 write a kernel message to the host's log). A handle is the scheduler's."
        )]
        private static Value TaskNew(SimHost h, CicodeTask t, ArgList a) =>
            NewTask(h, t, a, A.S(a.Get(0)), A.S(a.Get(1)), A.I(a.Get(2)) & 0xFFFF, -1, A.S(a.Get(3)));

        [Builtin(
            "TaskNewEx",
            Doc = "1193670",
            Effects = "starts a Cicode task in the VM's scheduler",
            Errors = "274 when SubscriptionRate is not -1 or 1..60000, or an unknown cluster (returns -1)",
            Notes = "As TaskNew; SubscriptionRate (the task's tag subscription rate, -1 = the caller's) is checked, not used. Mode 8 is tested before the rate."
        )]
        private static Value TaskNewEx(SimHost h, CicodeTask t, ArgList a)
        {
            // (sName, sArg, Mode, SubscriptionRate [, ClusterName]):
            long poll = A.I(a.Get(3));
            return NewTask(
                h,
                t,
                a,
                A.S(a.Get(0)),
                A.S(a.Get(1)),
                A.I(a.Get(2)) & 0xFFFF,
                poll,
                A.S(a.Get(4))
            );
        }

        [Builtin(
            "TaskCall",
            Doc = "1194741",
            Blocking = true,
            Errors = "282 in foreground code; 292 function not found; 433 / 529 / 518 from the argument text",
            Notes = "Runs the function as a new task and blocks until it ends; the result as a string ('' for VOID). A halted callee gives '' (verify). A name with no user function calls the built-in of that name (help 1194741's own example calls StrFill), its arguments bound by the built-in's parameter types; a built-in with a by-reference parameter cannot be called (433)."
        )]
        private static Value TaskCall(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground)
            {
                W.Raise(t, Errors.FgBlock);
                return W.Empty;
            }
            Function? fn = CallText.Find(t, A.S(a.Get(0)));
            if (fn == null)
                return CallBuiltin(h, t, A.S(a.Get(0)), A.S(a.Get(1)));
            var vals = CallText.Bind(fn, A.S(a.Get(1)), out int err, out string msg);
            if (msg.Length > 0)
                h.Note(t, msg);
            if (err != 0)
            {
                W.Raise(t, err);
                if (err != Errors.TooManyArgs)
                    return W.Empty;
            }
            var sched = t.Vm.Scheduler;
            CicodeTask child;
            if (sched == null)
            {
                // no scheduler: run it here to its end (a VM without tasks)
                CicodeTask? nested = W.RunNested(h, t, fn, vals, 1000000);
                return nested == null ? W.Empty : CallResult(nested, fn);
            }
            try
            {
                child = sched.Add(t.Vm.TaskForCall(fn, vals, false, null, fn.Name));
            }
            catch (HardwareError e)
            {
                W.Raise(t, e.Code);
                return W.Empty;
            }
            InheritContext(t, child);
            t.Wait = new Wait(TaskState.Wait, "TaskCall(" + fn.Name + ")")
            {
                Poll = () => child.Done,
                Result = () => CallResult(child, fn),
            };
            return Value.Block;
        }

        /// <summary>TaskCall's task inherits the caller's window and cluster context.</summary>
        private static void InheritContext(CicodeTask t, CicodeTask child)
        {
            if (t.Data.TryGetValue("cluster", out object? own) && own is string oc && oc.Length > 0)
                child.Data["cluster"] = oc;
            if (t.Data.TryGetValue(CicodeSim.World.Display.TaskContext.WindowKey, out object? win))
                child.Data[CicodeSim.World.Display.TaskContext.WindowKey] = win;
        }

        /// <summary>TaskCall of a built-in: the same new task and wait as for a user function;
        /// the result as a string ('' for VOID).</summary>
        private static Value CallBuiltin(SimHost h, CicodeTask t, string name, string args)
        {
            CicodeTask? child = CallText.BuiltinTask(
                t,
                name,
                args,
                out Builtin? b,
                out int err,
                out string msg
            );
            if (msg.Length > 0)
                h.Note(t, msg);
            if (child == null)
            {
                W.Raise(t, err != 0 ? err : Errors.InvalidFunction);
                return W.Empty;
            }
            if (err != 0)
                W.Raise(t, err);
            bool isVoid = b!.Ret == Types.Void;
            Value Outcome() =>
                child.State != TaskState.Stop || child.Result == null || isVoid
                    ? W.Empty
                    : Conv.ToStr(child.Result.Value, NoErr.Instance);
            var sched = t.Vm.Scheduler;
            if (sched == null)
            {
                t.Vm.Run(child);
                return Outcome();
            }
            try
            {
                sched.Add(child);
            }
            catch (HardwareError e)
            {
                W.Raise(t, e.Code);
                return W.Empty;
            }
            InheritContext(t, child);
            t.Wait = new Wait(TaskState.Wait, "TaskCall(" + b.Name + ")")
            {
                Poll = () => child.Done,
                Result = () => Outcome(),
            };
            return Value.Block;
        }

        private static Value CallResult(CicodeTask child, Function fn)
        {
            if (child.State != TaskState.Stop || child.Result == null || fn.Result == Types.Void)
                return W.Empty;
            return Conv.ToStr(child.Result.Value, NoErr.Instance);
        }

        [Builtin(
            "TaskKill",
            Doc = "1193668",
            Effects = "ends the task",
            Errors = "269 bad handle (returned)",
            Notes = "The calling task halts (state Halt); another task ends at once (state Dead). Semaphores it holds become 'owner died' (297) for their waiters."
        )]
        private static Value TaskKill(SimHost h, CicodeTask t, ArgList a)
        {
            CicodeTask? x = Find(t, A.Short(A.I(a.Get(0))));
            if (x == null)
                return W.I(Codes.BadHandle);
            if (x == t)
            {
                t.Vm.Halt(t, "TaskKill");
                return W.I(0);
            }
            x.Wait = null;
            x.PendingResult = false;
            x.State = TaskState.Dead;
            x.HaltReason = "TaskKill";
            return W.I(0);
        }

        /// <summary>The wait a suspended task stays in until TaskResume.</summary>
        internal const string SuspendedReason = "TaskSuspend";

        [Builtin(
            "TaskSuspend",
            Doc = "1193673",
            Effects = "suspends the task",
            Errors = "269 bad handle or handle 0 (returned)",
            Notes = "A foreground task is halted instead (state 6 -> 3). A task waiting in a built-in stays out of that wait until resumed."
        )]
        private static Value TaskSuspend(SimHost h, CicodeTask t, ArgList a)
        {
            long hn = A.Short(A.I(a.Get(0)));
            CicodeTask? x = hn == 0 ? null : Find(t, hn);
            if (x == null)
                return W.I(Codes.BadHandle);
            if (x.Foreground)
            {
                x.State = TaskState.Halt;
                x.HaltReason = "TaskSuspend";
                return W.I(0);
            }
            Wait? old = x.Wait;
            x.Wait = new Wait(TaskState.Wait, SuspendedReason) { Poll = () => false, Result = old?.Result };
            if (x != t)
                x.State = TaskState.Wait;
            return W.I(0);
        }

        [Builtin(
            "TaskResume",
            Doc = "1193671",
            Effects = "makes the task ready",
            Errors = "269 bad handle or handle 0 (returned)",
            Notes = "CodeReady takes the task out of any wait (a Sleep ends early; a blocked built-in returns what it has then)."
        )]
        private static Value TaskResume(SimHost h, CicodeTask t, ArgList a)
        {
            long hn = A.Short(A.I(a.Get(0)));
            CicodeTask? x = hn == 0 ? null : Find(t, hn);
            if (x == null)
                return W.I(Codes.BadHandle);
            if (x != t && (x.State == TaskState.Wait || x.State == TaskState.Sleep))
            {
                Wait? old = x.Wait;
                x.Wait = new Wait(old?.State ?? TaskState.Wait, "TaskResume") { Result = old?.Result };
            }
            return W.I(0);
        }

        [Builtin(
            "TaskHnd",
            Doc = "1193667",
            Notes = "'' = the calling task; else the live task of that name (case-insensitive), -1 when none."
        )]
        private static Value TaskHnd(SimHost h, CicodeTask t, ArgList a)
        {
            string n = A.S(a.Get(0));
            if (n.Length == 0)
                return W.I(t.Handle);
            return W.I(ByName(t, n));
        }

        [Builtin("TaskGetSignal", Doc = "1193666", Notes = "The task's signal word; 0 for a bad handle.")]
        private static Value TaskGetSignal(SimHost h, CicodeTask t, ArgList a)
        {
            CicodeTask? x = Find(t, A.Short(A.I(a.Get(0))));
            if (x == null)
                return W.I(0);
            return W.I(x.Data.TryGetValue("signal", out object? v) && v is long l ? l : 0);
        }

        [Builtin(
            "TaskSetSignal",
            Doc = "1193672",
            Effects = "sets the task's signal word",
            Errors = "269 bad handle (returned)"
        )]
        private static Value TaskSetSignal(SimHost h, CicodeTask t, ArgList a)
        {
            CicodeTask? x = Find(t, A.Short(A.I(a.Get(0))));
            if (x == null)
                return W.I(Codes.BadHandle);
            x.Data["signal"] = A.Short(A.I(a.Get(1)));
            return W.I(0);
        }

        [Builtin(
            "TaskCluster",
            Doc = "1193665",
            Notes = "The task's cluster context: the cluster TaskNew gave it (a task started without one takes its starter's), else '' (verify against a multi-cluster project)."
        )]
        private static Value TaskCluster(SimHost h, CicodeTask t, ArgList a) =>
            W.S(t.Data.TryGetValue("cluster", out object? c) && c is string s ? s : "");
    }
}
