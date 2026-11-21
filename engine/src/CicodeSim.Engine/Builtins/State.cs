using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// Built-ins that read or change the interpreter's state: errors, globals, Halt, Sleep.
    /// </summary>
    public static class State
    {
        public const int ERange = 257;
        public const int EInvalidArg = 274;

        private static Value I(long v) => Value.Long(v);

        /// <summary>The doc 1130208 title of a hardware error code ("" when unknown).</summary>
        public static string ErrorMessage(long code) => Errors.Title(code);

        private static bool Glb(CicodeTask task, int len, long idx)
        {
            if (idx >= 0 && idx < len)
                return true;
            task.RaiseError(ERange, false);
            return false;
        }

        internal static void Register(Action<string, string, BuiltinImpl> add)
        {
            add("ErrSet", "(non-zero: level + 1, capped at 1000; 0: level - 1)", (t, a) =>
            {
                long mode = A.I(a.Get(0)) & 0xFF;
                int lvl = t.ErrSetLevel;
                if (mode == 0)
                {
                    if (lvl > 0)
                        t.ErrSetLevel = lvl - 1;
                    else
                        t.Vm.Log?.Invoke("ErrSet: Error check level is 0 when ErrSet(0) is called");
                    return Value.Nothing;
                }
                if (lvl < 100)
                {
                    t.ErrSetLevel = lvl + 1;
                    if (lvl + 1 == 100)
                        t.Vm.Log?.Invoke("ErrSet: Error check level has reached 100");
                }
                else if (lvl <= 999)
                {
                    t.ErrSetLevel = lvl + 1;
                    if (lvl + 1 == 1000)
                        t.Vm.Log?.Invoke("ErrSet: Error check level has reached 1000");
                }
                return Value.Nothing;
            });

            add(
                "IsError",
                "(returns the last error and clears it); live: IsError = 0 on a fresh task",
                (t, a) =>
                {
                    int e = t.Error;
                    t.Error = 0;
                    return I(e);
                }
            );

            add("ErrSetLevel", "(sets the level, returns the old one)", (t, a) =>
            {
                int old = t.ErrSetLevel;
                t.ErrSetLevel = (int)A.Short(A.I(a.Get(0)));
                return I(old);
            });

            add("ErrTrap", "raises Error (bHalt: halting)", (t, a) =>
            {
                t.RaiseError((int)(A.I(a.Get(0)) & 0xFFFF), (A.I(a.Get(1)) & 0xFFFF) != 0);
                return I(0);
            });

            add(
                "ErrMsg",
                "(GetHardAlmMsg); text: doc 1130208 titles (verify against the runtime's message table)",
                (t, a) => Value.Str(ErrorMessage(A.I(a.Get(0))))
            );

            add(
                "CodeSetMode",
                "(0 WriteLocal, 1 ScaleCheck, 2 EchoError, 3 IgnoreCase, 4 profiler; returns the previous value; any other type: 274 without the halt flag and -1)",
                (t, a) =>
                {
                    long ty = A.Short(A.I(a.Get(0)));
                    long v = A.I(a.Get(1));
                    bool on = (v & 0xFFFF) != 0;
                    bool old;
                    switch (ty)
                    {
                        case 0:
                            old = t.WriteLocal;
                            t.WriteLocal = on;
                            break;
                        case 1:
                            old = t.ScaleCheck;
                            t.ScaleCheck = on;
                            break;
                        case 2:
                            old = t.EchoError;
                            t.EchoError = on;
                            break;
                        case 3:
                            old = t.IgnoreCaseFlag;
                            t.IgnoreCaseFlag = on;
                            break;
                        case 4:
                            old = t.Profile;
                            t.Profile = on;
                            break;
                        default:
                            t.RaiseError(EInvalidArg, false);
                            return I(-1);
                    }
                    return I(old ? 1 : 0);
                }
            );

            // --- [Global] arrays (..) -----
            add("GetGlbInt", "", (t, a) =>
            {
                long k = A.Short(A.I(a.Get(0)));
                var arr = t.Vm.GlbInt;
                return I(Glb(t, arr.Length, k) ? arr[k] : 0);
            });

            add("SetGlbInt", "", (t, a) =>
            {
                long k = A.Short(A.I(a.Get(0)));
                var arr = t.Vm.GlbInt;
                if (Glb(t, arr.Length, k))
                    arr[k] = A.I(a.Get(1));
                return Value.Nothing;
            });

            add("GetGlbFlt", "", (t, a) =>
            {
                long k = A.Short(A.I(a.Get(0)));
                var arr = t.Vm.GlbFloat;
                return Value.Real(Glb(t, arr.Length, k) ? arr[k] : 0.0);
            });

            add("SetGlbFlt", "", (t, a) =>
            {
                long k = A.Short(A.I(a.Get(0)));
                var arr = t.Vm.GlbFloat;
                if (Glb(t, arr.Length, k))
                    arr[k] = A.R(a.Get(1));
                return Value.Nothing;
            });

            add("GetGlbBool", "(the stored byte, sign-extended)", (t, a) =>
            {
                long k = A.Short(A.I(a.Get(0)));
                var arr = t.Vm.GlbBool;
                if (!Glb(t, arr.Length, k))
                    return I(0);
                long b = arr[k] & 0xFF;
                return I((b & 0x80) != 0 ? b - 0x100 : b);
            });

            add("SetGlbBool", "(stores the low byte)", (t, a) =>
            {
                long k = A.Short(A.I(a.Get(0)));
                var arr = t.Vm.GlbBool;
                if (Glb(t, arr.Length, k))
                    arr[k] = A.I(a.Get(1)) & 0xFF;
                return Value.Nothing;
            });

            add(
                "GetGlbStr",
                "(out of range: 257 without the halt flag, then a NULL string, which the call turns into 530 with the halt flag)",
                (t, a) =>
                {
                    long k = A.Short(A.I(a.Get(0)));
                    var arr = t.Vm.GlbStr;
                    return Glb(t, arr.Length, k) ? Value.Str(arr[k]) : Value.Nothing;
                }
            );

            add("SetGlbStr", "(a string of 256+ characters: 257)", (t, a) =>
            {
                long k = A.Short(A.I(a.Get(0)));
                string v = A.S(a.Get(1));
                var arr = t.Vm.GlbStr;
                if (k >= 0 && k < arr.Length && v.Length < 0x100)
                    arr[k] = v;
                else
                    t.RaiseError(ERange, false);
                return Value.Nothing;
            });

            // --- task control ----------------------------------------------------------------
            add("Halt", "-> (state Halt)", (t, a) =>
            {
                t.Vm.Halt(t, "Halt()");
                return I(0);
            });

            add(
                "Sleep",
                "(n < 0: returns 274; 0: yields; foreground: 282 + halt; else wakes at _time32 + n, whole seconds)",
                (t, a) =>
                {
                    long n = A.I(a.Get(0));
                    if (n < 0)
                        return I(EInvalidArg);
                    if (n == 0)
                    {
                        t.Vm.Preempt(t);
                        return I(0);
                    }
                    if (t.Foreground)
                    {
                        t.Vm.Fatal(t, Errors.FgBlock, "foreground Cicode cannot block", unwind: false);
                        return I(0);
                    }
                    t.Wait = new Wait(TaskState.Sleep, "Sleep(" + n + ")")
                    {
                        UntilSec = Clocks.UnixSeconds(t.Vm.Clock) + n,
                    };
                    return I(0);
                }
            );

            add(
                "SleepMS",
                "(n < 0: returns 274; 0: yields; foreground: 282 + halt; else a millisecond wait)",
                (t, a) =>
                {
                    long n = A.I(a.Get(0));
                    if (n < 0)
                        return I(EInvalidArg);
                    if (n == 0)
                    {
                        t.Vm.Preempt(t);
                        return I(0);
                    }
                    if (t.Foreground)
                    {
                        t.Vm.Fatal(t, Errors.FgBlock, "foreground Cicode cannot block", unwind: false);
                        return I(0);
                    }
                    t.Wait = new Wait(TaskState.Sleep, "SleepMS(" + n + ")")
                    {
                        UntilMs = t.Vm.Clock.MonotonicMs() + n,
                    };
                    return I(0);
                }
            );
        }
    }
}
