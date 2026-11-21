using System;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>The interpreter loop.</summary>
    internal static class Interpreter
    {
        private const int UPop = 1,
            UIndex = 2,
            UAssign = 3;

        private static string UnderMsg(int k) =>
            k == UPop ? "a pop from an empty stack"
            : k == UAssign ? "an assignment past the stack"
            : "an index past the stack";

        private static Wait PausedWait(CicodeTask task) =>
            new Wait(TaskState.Wait, "paused") { Poll = () => !task.Pause };

        /// <summary>The stack cut back to <paramref name="bas"/> (a negative base counts from
        /// the top, clamped to the stack).</summary>
        private static int DelFrom(int bas, int sp)
        {
            if (bas < 0)
            {
                bas += sp;
                if (bas < 0)
                    bas = 0;
            }
            return bas < sp ? bas : sp;
        }

        public static int RunSlice(CicodeVm vm, CicodeTask task)
        {
            int s0 = task.State;
            if (s0 == TaskState.Stop || s0 == TaskState.Halt || s0 == TaskState.Dead)
                return s0;
            if (s0 == TaskState.Wait || s0 == TaskState.Sleep)
                return s0;
            bool fg = task.Foreground;
            int running = fg ? TaskState.RunExec : TaskState.Run;
            task.State = running;
            task.YieldNow = false;
            task.Unwinding = false;
            task.PcOverride = -1;
            CodeConfig cfg = vm.Config;
            IClock clock = vm.Clock;
            IClockReader? reader = clock as IClockReader;
            double startMs = clock.MonotonicMs();
            int budget = fg ? cfg.TimeSlicePage : cfg.TimeSlice;
            task.NowFt = clock.NowFiletime();
            task.Slices++;
            task.OpError = 0;
            CodeBlock? code = task.Code;
            if (code == null)
            {
                task.State = TaskState.Stop;
                return TaskState.Stop;
            }
            Ins[] ins = code.Ins;
            Value[] consts = code.D.Consts;
            Program prog = code.Program;
            int ip = task.Ip;
            int bas = task.Base;
            int limit = cfg.Stack;
            LineHook? hook = vm.LineHook;
            Recorder? rec = vm.Recorder;
            FunctionProfile? fprof = vm.Functions;
            int maxOps = cfg.MaxSliceOps;
            bool fullSlice = cfg.FullTimeSlice;
            Value[] st = task.Stack;
            int sp = task.Sp;
            int n = 0;
            long opsDone = 0,
                skew = 0;
            int cur = ip;
            CodeBlock curCode = code;
            int under = 0;
            string underText = "";
            try
            {
                while (true)
                {
                    cur = ip;
                    curCode = code;
                    ref Ins x = ref ins[ip];
                    switch (x.Op)
                    {
                        case 0x36: // LoadStack
                        {
                            int i = bas + x.A;
                            if (i < 0)
                                i += sp;
                            if (i < 0 || i >= sp)
                            {
                                under = UIndex;
                                goto underflow;
                            }
                            if (sp + 1 >= st.Length)
                            {
                                task.Sp = sp;
                                task.EnsureCapacity(sp + 2);
                                st = task.Stack;
                            }
                            ref Value s = ref st[i];
                            int t = s.T;
                            if (
                                t == Types.Long
                                || t == Types.String
                                || t == Types.Real
                                || t == 0x0C
                                || t == 0x13
                                || t == 0x14
                            )
                                st[sp] = s;
                            else
                                st[sp] = s.Bare(0);
                            sp++;
                            if (sp >= limit)
                            {
                                sp--;
                                vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                                goto halted;
                            }
                            ip = x.Next;
                            break;
                        }
                        case 0x34: // Line
                            task.Line = x.A;
                            ip = x.Next;
                            if (rec != null)
                            {
                                task.Sp = sp;
                                task.Base = bas;
                                rec.Line(task, x.A);
                            }
                            if (hook != null)
                            {
                                task.Sp = sp;
                                task.Ip = ip;
                                task.Base = bas;
                                task.Code = code;
                                task.AtReturn = false;
                                hook(task, x.A);
                                st = task.Stack;
                                sp = task.Sp;
                                if (task.Pause && (!fg || task.ParkForeground))
                                {
                                    task.State = TaskState.Wait;
                                    task.Wait = PausedWait(task);
                                    goto done;
                                }
                            }
                            break;
                        case 0x09: // Convert
                        {
                            if (sp < 1)
                            {
                                under = UIndex;
                                goto underflow;
                            }
                            int tt = x.A;
                            int vt = st[sp - 1].T;
                            if (vt != tt || !(tt == Types.Long || tt == Types.String || tt == Types.Real))
                            {
                                task.Sp = sp;
                                Value r = Conv.Convert(st[sp - 1], tt, task);
                                if (task.Unwinding)
                                    goto halted;
                                st[sp - 1] = r;
                            }
                            ip = x.Next;
                            break;
                        }
                        case 0x01: // Const
                            if (sp + 1 >= st.Length)
                            {
                                task.Sp = sp;
                                task.EnsureCapacity(sp + 2);
                                st = task.Stack;
                            }
                            st[sp++] = consts[x.B];
                            if (sp >= limit)
                            {
                                sp--;
                                vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                                goto halted;
                            }
                            ip = x.Next;
                            break;
                        case 0x35: // StoreStack
                        {
                            int i = bas + x.A;
                            if (i < 0)
                                i += sp;
                            if (i < 0 || i >= sp)
                            {
                                under = UIndex;
                                goto underflow;
                            }
                            Value s = st[i];
                            Value v = st[sp - 1];
                            sp--;
                            int t = s.T;
                            Value nv;
                            if (
                                v.T == t
                                && (t == Types.Long || t == Types.String || t == Types.Real || t == 0x0C)
                            )
                                nv = v;
                            else
                            {
                                task.Sp = sp;
                                nv = CicodeVm.StoreStackConv(task, s, v);
                                if (task.Unwinding)
                                    goto halted;
                            }
                            if (i >= sp)
                            {
                                under = UAssign;
                                goto underflow;
                            }
                            st[i] = nv;
                            if (rec != null)
                                rec.Local(task, x.A, s, nv);
                            ip = x.Next;
                            break;
                        }
                        case 0x05: // Func
                        {
                            int argc = x.B;
                            task.OpError = 0;
                            int start = sp;
                            if (argc != 0)
                            {
                                start = sp - argc;
                                if (start < 0)
                                    start = 0;
                            }
                            var args = new ArgList(st, start, sp - start);
                            sp = start;
                            task.Ip = x.Next;
                            task.Sp = sp;
                            task.Base = bas;
                            task.Code = code;
                            long bt0 = fprof != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                            int kind = vm.CallBuiltin(task, x.A, args, out Value r, out string nsName);
                            if (fprof != null)
                                fprof.Builtin(
                                    task,
                                    vm.Table.Name(x.A),
                                    System.Diagnostics.Stopwatch.GetTimestamp() - bt0
                                );
                            st = task.Stack;
                            sp = task.Sp;
                            if (kind == CicodeVm.BNotSim)
                            {
                                vm.NotSimulatedCounts.TryGetValue(nsName, out int cnt);
                                vm.NotSimulatedCounts[nsName] = cnt + 1;
                                task.State = TaskState.Halt;
                                task.HaltReason = "built-in function " + nsName + " is not simulated";
                                goto halted;
                            }
                            if (kind == CicodeVm.BHalted || task.Unwinding)
                                goto halted;
                            task.OpError = 0;
                            ip = x.Next;
                            if (kind == CicodeVm.BBlock)
                            {
                                task.PendingResult = true;
                                if (task.Wait == null)
                                    task.Wait = new Wait(TaskState.Wait, "built-in");
                            }
                            else if (kind == CicodeVm.BValue)
                            {
                                if (sp + 1 >= st.Length)
                                {
                                    task.Sp = sp;
                                    task.EnsureCapacity(sp + 2);
                                    st = task.Stack;
                                }
                                st[sp++] = r;
                                if (sp >= limit)
                                {
                                    sp--;
                                    vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                                    goto halted;
                                }
                            }
                            if (task.State != running || task.Wait != null || task.YieldNow)
                            {
                                if (task.Wait != null && task.State == running)
                                    task.State = task.Wait.State;
                                else if (task.YieldNow && task.State == running)
                                    task.State = TaskState.Ready;
                                goto done;
                            }
                            if (n < 198)
                            {
                                skew += 198 - n;
                                n = 198;
                            }
                            break;
                        }
                        case 0x1B: // Add / compare
                        case 0x20:
                        case 0x23:
                        case 0x26:
                        case 0x29:
                        case 0x2C:
                        case 0x2F:
                        {
                            if (sp < 1)
                            {
                                under = UPop;
                                goto underflow;
                            }
                            Value b = st[sp - 1];
                            sp--;
                            if (sp < 1)
                            {
                                under = UIndex;
                                goto underflow;
                            }
                            ref Value a = ref st[sp - 1];
                            if (a.T == Types.Long && b.T == Types.Long)
                            {
                                ulong now = task.NowFt; // merge, inline
                                ulong q = a.Q;
                                if (q != b.Q)
                                    q = Quality.Merge(q, b.Q);
                                ulong ts = a.Ts != 0 ? a.Ts : now;
                                ulong y = b.Ts != 0 ? b.Ts : now;
                                if (y > ts)
                                    ts = y;
                                ulong vts = a.Vts != 0 ? a.Vts : now;
                                y = b.Vts != 0 ? b.Vts : now;
                                if (y > vts)
                                    vts = y;
                                ulong qts = a.Qts != 0 ? a.Qts : now;
                                y = b.Qts != 0 ? b.Qts : now;
                                if (y > qts)
                                    qts = y;
                                long xa = a.I,
                                    xb = b.I,
                                    res;
                                switch (x.Op)
                                {
                                    case 0x1B:
                                        res = xa + xb;
                                        if (res > 0x7FFFFFFF || res < -0x80000000L)
                                            res = (int)res;
                                        break;
                                    case 0x2C:
                                        res = xa == xb ? 1 : 0;
                                        break;
                                    case 0x2F:
                                        res = xa != xb ? 1 : 0;
                                        break;
                                    case 0x20:
                                        res = xa < xb ? 1 : 0;
                                        break;
                                    case 0x26:
                                        res = xa > xb ? 1 : 0;
                                        break;
                                    case 0x23:
                                        res = xa <= xb ? 1 : 0;
                                        break;
                                    default:
                                        res = xa >= xb ? 1 : 0;
                                        break;
                                }
                                a.I = res;
                                a.Q = q;
                                a.Ts = ts;
                                a.Vts = vts;
                                a.Qts = qts;
                            }
                            else
                            {
                                task.Sp = sp;
                                Value r = Ops.Full(x.Op, a, b, task);
                                if (task.Unwinding)
                                    goto halted;
                                st[sp - 1] = r;
                            }
                            ip = x.Next;
                            break;
                        }
                        case 0x0B: // Branch
                            task.OpError = 0;
                            ip = x.A;
                            break;
                        case 0x0D: // Brfalse / BrTrue
                        case 0x44:
                        {
                            if (sp < 1)
                            {
                                under = UPop;
                                goto underflow;
                            }
                            Value v = st[sp - 1];
                            sp--;
                            int tv;
                            if (v.T == Types.Long)
                                tv = v.I != 0 ? 1 : 0;
                            else if (v.T == Types.Real)
                                tv = v.D != 0.0 ? 1 : 0;
                            else
                            {
                                task.Sp = sp;
                                tv = Ops.Truth(v, task);
                                if (task.Unwinding)
                                    goto halted;
                            }
                            if (tv < 0)
                                ip = x.Next; // binary leaves pc on the operand
                            else
                            {
                                task.OpError = 0;
                                if (x.Op == 0x0D)
                                    ip = tv != 0 ? x.Next : x.A;
                                else
                                    ip = tv != 0 ? x.A : x.Next;
                            }
                            break;
                        }
                        case 0x49: // FuncCallByLookup / ByName
                        case 0x48:
                        {
                            string? name = (string?)x.O;
                            task.Sp = sp;
                            Function? fn =
                                x.Op == 0x49
                                    ? vm.ResolveCall(code, cur, name)
                                    : (!string.IsNullOrEmpty(name) ? vm.Library.Function(name!) : null);
                            if (fn == null)
                            {
                                vm.Fatal(
                                    task,
                                    Errors.InvalidFunction,
                                    "invalid function " + (name ?? "(no name)"),
                                    opError: true
                                );
                                goto halted;
                            }
                            if (task.Frames.Count >= limit - 1)
                            {
                                vm.Fatal(task, Errors.Stack, "Cicode stack overflow (calls)");
                                goto halted;
                            }
                            task.OpError = 0;
                            int argc = x.A;
                            task.Frames.Add(
                                new Frame
                                {
                                    Code = code,
                                    Ip = x.Next,
                                    Base = bas,
                                    Func = task.Func,
                                    Line = task.Line,
                                }
                            );
                            var callCode = code.CallCode ??= new CodeBlock?[ins.Length];
                            CodeBlock? callee = callCode[cur];
                            if (callee == null || callee.Func != fn)
                                callCode[cur] = callee = vm.CodeFor(fn);
                            code = callee;
                            ins = code.Ins;
                            consts = code.D.Consts;
                            prog = code.Program;
                            task.Func = fn;
                            bas = sp - argc;
                            if (rec != null)
                                rec.Enter(task);
                            if (fprof != null)
                                fprof.Enter(task, fn);
                            if (fn.NotCompiled != null)
                            {
                                // a placeholder (the source compiler never reached the function and
                                // there is no compiled code): the reason, not 'too many arguments'
                                cur = code.Start;
                                curCode = code;
                                vm.Fatal(
                                    task,
                                    Errors.InvalidFunction,
                                    fn.Name + " did not compile: " + fn.NotCompiled,
                                    opError: true
                                );
                                goto halted;
                            }
                            var ent = code.D.Entry;
                            bool hasEnt = ent != null && ent.Length > 0;
                            int maxargs = hasEnt ? ent!.Length - 1 : 0;
                            if (argc < fn.MinParam || argc > maxargs)
                            {
                                cur = code.Start; // halted at the callee's start (binary)
                                curCode = code;
                                if (argc < fn.MinParam)
                                {
                                    vm.Fatal(
                                        task,
                                        Errors.TooFewArgs,
                                        fn.Name + ": too few arguments",
                                        opError: true
                                    );
                                    goto halted;
                                }
                                vm.Fatal(
                                    task,
                                    Errors.TooManyArgs,
                                    fn.Name + ": too many arguments",
                                    opError: true
                                );
                                goto halted;
                            }
                            ip = hasEnt ? ent![argc] : code.Start;
                            break;
                        }
                        case 0x07: // Pop
                            if (sp < 1)
                            {
                                under = UPop;
                                goto underflow;
                            }
                            sp--;
                            ip = x.Next;
                            break;
                        case 0x47: // LoadCodeVar
                        {
                            var slots = code.Slots ??= new CodeVarSlot?[ins.Length];
                            CodeVarSlot sl = slots[ip] ??= vm.Slot(prog, (CodeVar?)x.O, x.L);
                            Value v = sl.Get(0, out int e);
                            task.SetOpError(e);
                            if (sp + 1 >= st.Length)
                            {
                                task.Sp = sp;
                                task.EnsureCapacity(sp + 2);
                                st = task.Stack;
                            }
                            st[sp++] = v;
                            if (sp >= limit)
                            {
                                sp--;
                                vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                                goto halted;
                            }
                            ip = x.Next;
                            break;
                        }
                        case 0x3F: // RetValue / Ret
                        case 0x0E:
                        {
                            bool retValue = x.Op == 0x3F;
                            if (retValue)
                            {
                                if (sp < 1)
                                {
                                    under = UPop;
                                    goto underflow;
                                }
                                Value v = st[sp - 1];
                                sp--;
                                sp = DelFrom(bas, sp);
                                st[sp++] = v;
                            }
                            else
                                sp = DelFrom(bas, sp);
                            int nf = task.Frames.Count;
                            if (nf == 0)
                            {
                                task.State = TaskState.Stop;
                                task.Result = sp > 0 ? st[sp - 1] : (Value?)null;
                                goto done;
                            }
                            Frame fr = task.Frames[nf - 1];
                            task.Frames.RemoveAt(nf - 1);
                            if (rec != null)
                                rec.Leave(task, task.Func);
                            if (fprof != null)
                                fprof.Leave(task);
                            code = fr.Code;
                            ip = fr.Ip;
                            bas = fr.Base;
                            task.Func = fr.Func;
                            task.Line = fr.Line;
                            ins = code.Ins;
                            consts = code.D.Consts;
                            prog = code.Program;
                            task.OpError = 0;
                            if (retValue && sp >= limit)
                            {
                                sp--;
                                vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                                goto halted;
                            }
                            if (hook != null)
                            {
                                task.Sp = sp;
                                task.Ip = ip;
                                task.Base = bas;
                                task.Code = code;
                                task.AtReturn = true;
                                hook(task, task.Line);
                                task.AtReturn = false;
                                st = task.Stack;
                                sp = task.Sp;
                                if (task.Pause && (!fg || task.ParkForeground))
                                {
                                    task.State = TaskState.Wait;
                                    task.Wait = PausedWait(task);
                                    goto done;
                                }
                            }
                            break;
                        }
                        case 0xFF: // End
                            task.State = TaskState.Stop;
                            task.Result = sp > 0 ? st[sp - 1] : (Value?)null;
                            goto done;
                        case Decoded.OpOutOfCode:
                            under = UIndex;
                            goto underflow;
                        default:
                        {
                            task.Sp = sp;
                            task.Base = bas;
                            task.Code = code;
                            int r = Slow(vm, task, ref x, code, prog, bas, ip, out under);
                            st = task.Stack;
                            sp = task.Sp;
                            if (r == -1)
                                goto halted;
                            if (r == -2)
                                goto underflow;
                            ip = r;
                            break;
                        }
                    }
                    // ---- slice check (every ~200 instructions) ----
                    if (n > 199)
                    {
                        n = 0;
                        opsDone += 201;
                        double used;
                        if (reader != null)
                        {
                            reader.Read(out ulong ft, out double ms);
                            task.NowFt = ft;
                            used = ms - startMs;
                        }
                        else
                        {
                            task.NowFt = clock.NowFiletime();
                            used = clock.MonotonicMs() - startMs;
                        }
                        if (
                            used >= budget
                            || (maxOps != 0 && opsDone >= maxOps)
                            || (!fullSlice && !fg && vm.OtherWork())
                        )
                        {
                            if (fg)
                            {
                                cur = ip;
                                curCode = code;
                                vm.Fatal(task, Errors.FgTooLong, "foreground Cicode run is too long");
                                goto halted;
                            }
                            task.State = TaskState.Ready;
                            goto done;
                        }
                    }
                    else
                        n++;
                }
            }
            catch (DecodeError e)
            {
                // a called function whose code does not decode
                task.State = TaskState.Halt;
                task.HaltReason = "invalid code: " + e.Message;
                goto halted;
            }
            catch (ReferenceCycleException)
            {
                task.State = TaskState.Halt;
                task.HaltReason = "recursion";
                goto halted;
            }
            catch (IndexOutOfRangeException e)
            {
                // a reference into the stack past its end
                st = task.Stack;
                sp = task.Sp;
                underText = e.Message;
                under = -1;
                goto underflow;
            }

            underflow:
            task.State = TaskState.Halt;
            task.HaltReason =
                $"stack underflow at 0x{curCode.Ins[cur].Pc:x} ({(under == -1 ? underText : UnderMsg(under))})";
            halted:
            if (curCode != code)
                task.PcOverride = curCode.Ins[cur].Pc;
            ip = cur;
            if (curCode != code)
            {
                // the pc stays the other code's; the task cannot run again
                ip = code.Start;
            }
            done:
            task.Ip = ip;
            task.Base = bas;
            task.Code = code;
            task.Sp = sp;
            task.Instructions += opsDone + n - skew;
            return task.State;
        }

        // ------------------------------------------------------------------ slow paths
        /// <summary>Every opcode that is not inline. Returns the next instruction, -1 when the
        /// task halted (task.Unwinding or state Halt), -2 for a stack underflow.</summary>
        private static int Slow(
            CicodeVm vm,
            CicodeTask task,
            ref Ins x,
            CodeBlock code,
            Program prog,
            int bas,
            int ip,
            out int under
        )
        {
            under = 0;
            int limit = vm.Config.Stack;
            int nxt = x.Next;
            int op = x.Op;
            Value[] st = task.Stack;
            switch (op)
            {
                case Op.Sub:
                case Op.Mul:
                case Op.Div:
                case Op.Mod:
                case Op.And:
                case Op.Or:
                case Op.BitAnd:
                case Op.BitOr:
                case Op.BitXor:
                {
                    if (task.Sp < 1)
                    {
                        under = UPop;
                        return -2;
                    }
                    Value b = st[task.Sp - 1];
                    task.Sp--;
                    if (task.Sp < 1)
                    {
                        under = UIndex;
                        return -2;
                    }
                    Value a = st[task.Sp - 1];
                    Value r;
                    if (op == Op.Sub || op == Op.Mul)
                        r = Ops.Numeric(op, a, b, task);
                    else if (op == Op.Div)
                        r = Ops.Div(a, b, task);
                    else if (op == Op.Mod)
                        r = Ops.Mod(a, b, task);
                    else
                        r = Ops.Logic(op, a, b, task);
                    if (task.Unwinding)
                        return -1;
                    st[task.Sp - 1] = r;
                    return nxt;
                }
                case Op.Not:
                case Op.Neg:
                {
                    if (task.Sp < 1)
                    {
                        under = UIndex;
                        return -2;
                    }
                    Value a = st[task.Sp - 1];
                    Value r = op == Op.Not ? Ops.Not(a, task) : Ops.Neg(a, task);
                    if (task.Unwinding)
                        return -1;
                    st[task.Sp - 1] = r;
                    return nxt;
                }
                case Op.StackVar:
                {
                    foreach (byte t in (byte[])x.O!)
                    {
                        if (
                            t == Types.Real
                            || t == Types.Long
                            || t == Types.String
                            || t == Types.Object
                            || t == Types.Timestamp
                            || t == Types.Quality
                        )
                            task.Push(Value.Default(t));
                        else
                            task.Push(Value.Int(t, 0, 0));
                        if (task.Sp >= limit)
                        {
                            task.Sp--;
                            vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                            return -1;
                        }
                    }
                    return nxt;
                }
                case Op.Index:
                    return Index(vm, task, ref x, code, prog, out under);
                case Op.StoreCodeVar:
                {
                    var slots = code.Slots ??= new CodeVarSlot?[code.Ins.Length];
                    CodeVarSlot sl = slots[ip] ??= vm.Slot(prog, (CodeVar?)x.O, x.L);
                    if (task.Sp < 1)
                    {
                        under = UIndex;
                        return -2;
                    }
                    vm.StoreCodeVar(task, sl, 0, st[task.Sp - 1]);
                    if (task.Unwinding)
                        return -1;
                    task.Sp--;
                    return nxt;
                }
                case Op.AddrStack:
                {
                    int i = bas + x.A;
                    if (i < 0)
                        i += task.Sp;
                    if (i < 0 || i >= task.Sp)
                    {
                        under = UIndex;
                        return -2;
                    }
                    Value s = st[i];
                    task.Push(Value.Variant(task.RefTo(i), s.Q, s.Ts, s.Vts, s.Qts));
                    if (task.Sp >= limit)
                    {
                        task.Sp--;
                        vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                        return -1;
                    }
                    return nxt;
                }
                case Op.AddrCodeVar:
                {
                    int sub = x.A;
                    var refs = code.Refs ??= new CodeVarRef?[code.Ins.Length];
                    CodeVarRef? cref = refs[ip];
                    int bt;
                    switch (sub)
                    {
                        case 1:
                            bt = 3;
                            break;
                        case 2:
                            bt = 6;
                            break;
                        case 4:
                            bt = 5;
                            break;
                        case 6:
                            bt = 6;
                            break;
                        case 7:
                            bt = 8;
                            break;
                        case 0xC:
                            bt = 0xE;
                            break;
                        default:
                            bt = 0x15;
                            break;
                    }
                    if (cref == null)
                        refs[ip] = cref = new CodeVarRef(vm.Slot(prog, (CodeVar?)x.O, x.L), 0, bt);
                    task.Push(Value.Variant(cref));
                    if (task.Sp >= limit)
                    {
                        task.Sp--;
                        vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                        return -1;
                    }
                    if (sub == 2)
                    {
                        vm.RaiseError(task, Errors.General, true);
                        if (task.Unwinding)
                            return -1;
                    }
                    return nxt;
                }
                case Op.DupEx:
                {
                    if (task.Sp < 2)
                    {
                        under = UIndex;
                        return -2;
                    }
                    Value s = st[task.Sp - 2];
                    int t = s.T;
                    task.Push(
                        t == Types.Real || t == Types.Long || t == Types.String || t == Types.Object
                            ? s
                            : s.Bare(0)
                    );
                    if (task.Sp >= limit)
                    {
                        task.Sp--;
                        vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                        return -1;
                    }
                    return nxt;
                }
                case Op.Load:
                    vm.Load(task, (TagRef)x.O!, null);
                    return task.Unwinding ? -1 : nxt;
                case Op.Store:
                {
                    if (task.Sp < 1)
                    {
                        under = UPop;
                        return -2;
                    }
                    Value v = st[task.Sp - 1];
                    task.Sp--;
                    vm.Store(task, (TagRef)x.O!, v, null, true);
                    return task.Unwinding ? -1 : nxt;
                }
                case Op.StringFmt:
                {
                    if (task.Sp < 1)
                    {
                        under = UIndex;
                        return -2;
                    }
                    string suffix = (string)x.O!;
                    Func<string, string>? rs =
                        suffix.IndexOf('?') >= 0
                            ? (s => vm.ResolveSuffix(task, s))
                            : (Func<string, string>?)null;
                    Value r = Conv.FormatValue(st[task.Sp - 1], x.L, suffix, task, rs);
                    if (task.Unwinding)
                        return -1;
                    st[task.Sp - 1] = r;
                    return nxt;
                }
                case Op.Bar:
                {
                    if (task.Sp < 1)
                    {
                        under = UIndex;
                        return -2;
                    }
                    Value r = vm.Bar(task, st[task.Sp - 1], x);
                    if (task.Unwinding)
                        return -1;
                    st[task.Sp - 1] = r;
                    return nxt;
                }
                case Op.AssFmt:
                {
                    if (task.Sp < 1)
                    {
                        under = UIndex;
                        return -2;
                    }
                    Value v = st[task.Sp - 1];
                    int t = v.T;
                    if (t == Types.Long || t == Types.Object)
                    {
                        st[task.Sp - 1] = Value.Real(v.I, v.Q, v.Ts, v.Vts, v.Qts);
                        t = Types.Real;
                    }
                    string fmt = vm.AssociationFormat(task, (string)x.O!, t, out bool ok);
                    if (!ok)
                        task.NoteExprError(Errors.NotAssociated);
                    task.Push(
                        Value.Str(fmt.Length > Types.StringMax ? fmt.Substring(0, Types.StringMax) : fmt)
                    );
                    if (task.Sp >= limit)
                    {
                        task.Sp--;
                        vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                        return -1;
                    }
                    return nxt;
                }
                case Op.String:
                case Op.StringPtr:
                {
                    if (task.Sp < 1)
                    {
                        under = UPop;
                        return -2;
                    }
                    Value f = st[task.Sp - 1];
                    task.Sp--;
                    string fmt = f.T == Types.String ? (f.S ?? "") : "";
                    if (task.Sp < 1)
                    {
                        under = UIndex;
                        return -2;
                    }
                    Value r = Conv.StringOp(st[task.Sp - 1], fmt, task);
                    if (task.Unwinding)
                        return -1;
                    st[task.Sp - 1] = r;
                    return nxt;
                }
            }
            if (op == Decoded.OpTagRef)
            {
                const string name = "<tag reference>";
                vm.NotSimulatedCounts.TryGetValue(name, out int cnt);
                vm.NotSimulatedCounts[name] = cnt + 1;
                task.State = TaskState.Halt;
                task.HaltReason =
                    (string)x.O! + " at " + Hex(x.Pc) + ": a tag reference argument is not simulated";
                return -1;
            }
            if (op == Decoded.OpBadPc)
            {
                task.State = TaskState.Halt;
                task.HaltReason = "jump to " + Hex(x.Pc) + ", which starts no instruction";
                return -1;
            }
            if (Op.IsUnsupported(op) || op == Op.UnsupportedConst)
            {
                task.State = TaskState.Halt;
                task.HaltReason = $"unsupported opcode {Op.Name(op)} at 0x{x.Pc:x}";
                return -1;
            }
            // Stop (0x00), Ufunc, Entry reached inline, anything else: the default case halts
            task.State = TaskState.Halt;
            task.HaltReason = $"opcode {Op.Name(op)} at 0x{x.Pc:x}";
            return -1;
        }

        private static CodeVarSlot SlotOf(CicodeVm vm, CodeBlock code, Program prog, int index, ref Ins inner)
        {
            var slots = code.Slots ??= new CodeVarSlot?[code.Ins.Length];
            return slots[index] ??= vm.Slot(prog, (CodeVar?)inner.O, inner.L);
        }

        /// <summary>0x and the hex digits of a pc (a negative pc prints -0x...).</summary>
        private static string Hex(int pc) =>
            pc < 0 ? "-0x" + (-(long)pc).ToString("x") : "0x" + pc.ToString("x");

        /// <summary>Index (0x40) + the access it prefixes: n INT indices
        /// (converted, REAL rounds), row-major acc = dim * acc + i, each 0 &lt;= i &lt; dim or 259
        /// (halt flag); not checked for a 1-dimension tag Load / Store.</summary>
        private static int Index(
            CicodeVm vm,
            CicodeTask task,
            ref Ins x,
            CodeBlock code,
            Program prog,
            out int under
        )
        {
            under = 0;
            var dims = (short[])x.O!;
            int nd = dims.Length;
            ref Ins inner = ref code.Ins[x.Next];
            int iop = inner.Op;
            if (iop == Decoded.OpOutOfCode)
            {
                under = UIndex;
                return -2;
            }
            int sp = task.Sp;
            int start = sp - nd;
            if (start < 0)
            {
                start += sp;
                if (start < 0)
                    start = 0;
            }
            int cnt = sp - start;
            task.Sp = start;
            Value[] st = task.Stack;
            bool ok = true;
            long acc = 0;
            bool big = false;
            for (int k = 0; k < nd; k++)
            {
                if (k >= cnt)
                {
                    under = UIndex;
                    return -2;
                }
                Value v = st[start + k];
                if (Types.TypeClass(v.T) != Types.ClassInt)
                {
                    v = Conv.ToInt(v, task);
                    if (task.Unwinding)
                        return -1;
                }
                long i = (int)v.I;
                int d = dims[k];
                if ((!(iop == Op.Load || iop == Op.Store) || nd > 1) && (i < 0 || i >= d))
                {
                    vm.RaiseError(task, Errors.Overrun, true);
                    if (task.Unwinding)
                        return -1;
                    ok = false;
                }
                if (!big)
                {
                    long m;
                    try
                    {
                        m = checked(d * acc + i);
                    }
                    catch (OverflowException)
                    {
                        big = true;
                        m = long.MaxValue;
                    }
                    acc = m;
                }
            }
            int nxt = inner.Next;
            int limit = vm.Config.Stack;
            switch (iop)
            {
                case Op.Load:
                    vm.Load(task, (TagRef)inner.O!, ok ? acc : -1);
                    if (task.Unwinding)
                        return -1;
                    break;
                case Op.Store:
                {
                    if (task.Sp < 1)
                    {
                        under = UPop;
                        return -2;
                    }
                    Value v = st[task.Sp - 1];
                    task.Sp--;
                    vm.Store(task, (TagRef)inner.O!, v, acc, ok);
                    if (task.Unwinding)
                        return -1;
                    break;
                }
                case Op.LoadCodeVar:
                {
                    CodeVarSlot sl = SlotOf(vm, code, prog, x.Next, ref inner);
                    Value v = sl.Get(ok ? acc : -1, out int e);
                    task.SetOpError(e);
                    task.Push(v);
                    if (task.Sp >= limit)
                    {
                        task.Sp--;
                        vm.Fatal(task, Errors.Stack, "Cicode stack overflow");
                        return -1;
                    }
                    break;
                }
                case Op.StoreCodeVar:
                {
                    CodeVarSlot sl = SlotOf(vm, code, prog, x.Next, ref inner);
                    if (task.Sp < 1)
                    {
                        under = UIndex;
                        return -2;
                    }
                    Value v = st[task.Sp - 1];
                    if (ok)
                    {
                        vm.StoreCodeVar(task, sl, acc, v);
                        if (task.Unwinding)
                            return -1;
                    }
                    else
                    {
                        if (task.OpError != 0 && task.ExprError == 0)
                            task.ExprError = task.OpError;
                        task.OpError = 0;
                    }
                    task.Sp--;
                    break;
                }
                case Op.AddrCodeVar:
                {
                    CodeVarSlot sl = SlotOf(vm, code, prog, x.Next, ref inner);
                    task.Push(Value.Variant(new CodeVarRef(sl, acc, 0x15)));
                    if (inner.A == 2)
                    {
                        vm.RaiseError(task, Errors.General, true);
                        if (task.Unwinding)
                            return -1;
                    }
                    break;
                }
                default:
                    nxt = x.B;
                    break;
            }
            return nxt;
        }
    }
}
