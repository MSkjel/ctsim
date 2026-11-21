using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>The event handler table (OnEvent, SetEvent, GetEvent, ChainEvent) and TableMath.</summary>
    public static class Events
    {
        /// <summary>The function a handle (FUNCSYM OP_CODE) names.</summary>
        internal static Function? ByHandle(CicodeVm vm, long hnd)
        {
            var fs = vm.Library.Functions;
            if (hnd >= 0 && hnd < fs.Count && fs[(int)hnd].Index == hnd)
                return fs[(int)hnd];
            foreach (var f in fs)
                if (f.Index == hnd)
                    return f;
            return null;
        }

        /// <summary>
        /// Run an event handler to its end in the calling task's VM; returns its INT result.
        /// </summary>
        internal static long RunHandler(SimHost h, CicodeTask t, Function fn, IReadOnlyList<Value> args)
        {
            CicodeTask? x = W.RunNested(h, t, fn, args, 1000);
            return x != null && x.State == TaskState.Stop && x.Result != null ? A.I(x.Result.Value) : 0;
        }

        [Builtin(
            "OnEvent",
            Doc = "1105349",
            Effects = "sets the handler of an event type",
            Errors = "274 a type of 256+ (returned)",
            Notes = "Fn is the handler's name (the compiler passes a FUNCTION argument as its name); an unknown name (0 among them) clears the type (-1)."
        )]
        private static Value OnEvent(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.I(a.Get(0)) & 0xFFFF;
            if (type >= 0x100)
                return W.I(Codes.InvalidArg);
            Function? fn = CallText.Find(t, A.S(a.Get(1)));
            h.Events[type] = fn != null ? fn.Index : -1;
            return W.I(0);
        }

        [Builtin(
            "SetEvent",
            Doc = "1105350",
            Effects = "sets the handler of an event type",
            Errors = "274 a type of 256+ or a bad function handle (returned)"
        )]
        private static Value SetEvent(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.I(a.Get(0)) & 0xFFFF;
            long hnd = A.Short(A.I(a.Get(1)));
            if (type >= 0x100 || hnd < -1 || hnd >= t.Vm.Library.Functions.Count)
                return W.I(Codes.InvalidArg);
            h.Events[type] = hnd;
            return W.I(0);
        }

        [Builtin(
            "GetEvent",
            Doc = "1105348",
            Errors = "274 a type of 256+ (raised, -1)",
            Notes = "The handler's function handle, -1 when none."
        )]
        private static Value GetEvent(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.I(a.Get(0)) & 0xFFFF;
            if (type >= 0x100)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            return W.I(h.EventHandler((int)type));
        }

        [Builtin(
            "ChainEvent",
            Doc = "1105347",
            Notes = "Calls the handler of a function handle (from GetEvent) and returns its value; -1 returns 0. The handler runs to its end at once (it must not block)."
        )]
        private static Value ChainEvent(SimHost h, CicodeTask t, ArgList a)
        {
            long hnd = A.Short(A.I(a.Get(0)));
            if (hnd == -1)
                return W.I(0);
            var fn = ByHandle(t.Vm, hnd);
            return W.I(fn == null ? 0 : RunHandler(h, t, fn, Array.Empty<Value>()));
        }

        [Builtin(
            "CallEvent",
            Doc = "1105346",
            Errors = "274 a type of 256+ (raised, 0)",
            Notes = "Calls the handler of an event type (as if the event happened in hWin) and returns its value; 0 without a handler."
        )]
        private static Value CallEvent(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.I(a.Get(1)) & 0xFFFF;
            if (type >= 0x100)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            long hnd = h.EventHandler((int)type);
            var fn = hnd == -1 ? null : ByHandle(t.Vm, hnd);
            return W.I(fn == null ? 0 : RunHandler(h, t, fn, Array.Empty<Value>()));
        }

        // ------------------------------------------------------------------ TableMath
        /// <summary>A trend table's invalid / gated value: both 32-bit halves 0xFFFFBBBB or
        /// 0xFFFFAAAA.</summary>
        internal static bool Marker(double v)
        {
            long bits = BitConverter.DoubleToInt64Bits(v);
            int lo = (int)bits,
                hi = (int)(bits >> 32);
            return (lo == -0x4445 && hi == -0x4445) || (lo == -0x5556 && hi == -0x5556);
        }

        [Builtin(
            "TableMath",
            Doc = "1192709",
            Errors = "274 Size below 2 or Command above 4 (raised, 0)",
            Notes = "Command 0 min, 1 max, 2 average, 3 sample standard deviation, 4 total over the REAL array the first argument starts (a module / global array); Mode 1 skips the trend invalid / gated values and gives the invalid marker (both halves 0xFFFFBBBB, a NaN) when none are left. An element past the array reads 0."
        )]
        private static Value TableMath(SimHost h, CicodeTask t, ArgList a)
        {
            Value tab = a.Get(0);
            long size = A.Short(A.I(a.Get(1)));
            long cmd = A.I(a.Get(2)) & 0xFFFF;
            bool skip = (A.I(a.Get(3)) & 1) != 0;
            if (size < 2 || cmd > 4)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.R(0.0);
            }
            var vals = new double[size];
            for (int i = 0; i < size; i++)
            {
                if (tab.O is CodeVarRef cr)
                    vals[i] = A.R(cr.Slot.Get(cr.Element + i, out _));
                else
                    vals[i] = i == 0 ? A.R(tab) : 0.0;
            }
            bool any = false;
            double r;
            switch (cmd)
            {
                case 0:
                    r = 1.2e38;
                    foreach (double v in vals)
                        if (!(skip && Marker(v)))
                        {
                            any = true;
                            r = v < r ? v : r;
                        }
                    break;
                case 1:
                    r = -1.2e38;
                    foreach (double v in vals)
                        if (!(skip && Marker(v)))
                        {
                            any = true;
                            r = v > r ? v : r;
                        }
                    break;
                case 2:
                {
                    double sum = 0;
                    long n = size;
                    foreach (double v in vals)
                    {
                        if (skip && Marker(v))
                        {
                            n--;
                            continue;
                        }
                        any = true;
                        sum += v;
                    }
                    r = n == 0 ? 0.0 : sum / n;
                    break;
                }
                case 3:
                {
                    double sum = 0,
                        sq = 0;
                    long n = size;
                    foreach (double v in vals)
                    {
                        if (skip && Marker(v))
                        {
                            n--;
                            continue;
                        }
                        any = true;
                        sum += v;
                        sq += v * v;
                    }
                    if (skip && n < 2)
                    {
                        r = 0.0;
                        break;
                    }
                    double var = (sq - sum * sum / n) / (n - 1);
                    r = var > 0.0 ? Math.Sqrt(var) : 0.0;
                    break;
                }
                default:
                {
                    double sum = 0;
                    foreach (double v in vals)
                        if (!(skip && Marker(v)))
                        {
                            any = true;
                            sum += v;
                        }
                    r = sum;
                    break;
                }
            }
            if (!skip)
                any = true;
            // none left: the trend invalid marker itself (the runtime stores 0xFFFFBBBB in
            // both halves of the result, a NaN; prints it as -NAN)
            if (!any)
                r = BitConverter.Int64BitsToDouble(unchecked((long)0xFFFFBBBBFFFFBBBBUL));
            return W.R(r);
        }
    }
}
