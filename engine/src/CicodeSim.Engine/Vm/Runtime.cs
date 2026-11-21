using System;
using System.Collections.Generic;
using System.Diagnostics;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>Task states (name table).</summary>
    public static class TaskState
    {
        public const int Run = 0;
        public const int Wait = 1;
        public const int Sleep = 2;
        public const int Halt = 3;
        public const int Stop = 4;
        public const int Ready = 5;
        public const int RunExec = 6;
        public const int Dead = 7;

        public static readonly string[] Names =
        {
            "Run",
            "Wait",
            "Sleep",
            "Halt",
            "Stop",
            "Ready",
            "RunExec",
            "Dead",
        };

        public static string Name(int s) => s >= 0 && s < Names.Length ? Names[s] : s.ToString();
    }

    /// <summary>Error codes raised by the VM (doc 1130208 titles).</summary>
    public static class Errors
    {
        public const int General = 256;
        public const int Range = 257;
        public const int Overrun = 259;
        public const int Div0 = 273;
        public const int InvalidArg = 274;
        public const int FgBlock = 282;
        public const int FgTooLong = 285;
        public const int Threads = 286;
        public const int InvalidFunction = 292;
        public const int Stack = 295;
        public const int NotAssociated = 323;

        /// <summary>Notice 343 'Foreground Cicode cannot break' (PROTOCOL.md: a breakpoint hit in foreground code).</summary>
        public const int FgCannotBreak = 343;
        public const int TagNotFound = 424;
        public const int TypeMismatch = 433;
        public const int Conversion = 434;
        public const int InvalidTagData = 450;
        public const int TooManyArgs = 518;
        public const int TooFewArgs = 529;
        public const int Null = 530;

        private static Dictionary<long, string>? _titles;
        private static readonly object TitlesLock = new object();

        /// <summary>The title of a hardware error code, from the help's table of Cicode errors
        /// on this computer ("" when unknown or without the help: InstallTexts).</summary>
        public static string Title(long code)
        {
            var m = _titles;
            if (m == null)
            {
                lock (TitlesLock)
                {
                    if (_titles == null)
                        _titles = InstallTexts.RuntimeErrorTitles();
                    m = _titles;
                }
            }
            return m.TryGetValue(code, out var s) ? s : "";
        }

        /// <summary>A hardware error's title for a message: <see cref="Title"/>, else 'error N'
        /// (the titles come from the install's help: a message never shows an empty one).</summary>
        public static string Text(long code)
        {
            string t = Title(code);
            return t.Length > 0
                ? t
                : "error " + code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A Cicode hardware error thrown by host code (Scheduler.Add) or a built-in that
    /// prefers throwing; the VM raises it with its halt flag and uses its result.</summary>
    public sealed class HardwareError : Exception
    {
        public readonly int Code;
        public readonly bool Halt;
        public readonly Value Result;

        public HardwareError(int code, bool halt = false, Value? result = null, string message = "")
            : base(message.Length > 0 ? message : "Cicode hardware error " + code)
        {
            Code = code;
            Halt = halt;
            Result = result ?? Value.Nothing;
        }
    }

    /// <summary>VM.Call of a function whose task halted.</summary>
    public sealed class CicodeHalted : Exception
    {
        public readonly CicodeTask Task;

        public CicodeHalted(CicodeTask task)
            : base($"task {task.Name} halted: {task.HaltReason} (code {task.HaltCode}, line {task.Line})")
        {
            Task = task;
        }
    }

    /// <summary>The [Code] parameters the VM reads, and the [Global] array sizes.</summary>
    public sealed class CodeConfig
    {
        /// <summary>Value stack elements and call frames (8..1024).</summary>
        public int Stack = 256;

        /// <summary>Background slice, ms (20..5000).</summary>
        public int TimeSlice = 100;

        /// <summary>Foreground slice, ms, then error 285 (20..5000).</summary>
        public int TimeSlicePage = 500;

        /// <summary>1 = never yield before the slice ends.</summary>
        public bool FullTimeSlice;
        public bool HaltOnError = true;
        public bool HaltOnInvalidTagData;

        /// <summary>Task flag bit 0 (CodeSetMode 0).</summary>
        public bool WriteLocal = true;

        /// <summary>Bit 1 (CodeSetMode 1).</summary>
        public bool ScaleCheck = true;

        /// <summary>Bit 2 (CodeSetMode 2).</summary>
        public bool EchoError = true;

        /// <summary>Bit 3 (CodeSetMode 3).</summary>
        public bool IgnoreCase = true;

        /// <summary>Max Cicode tasks (5..512), error 286.</summary>
        public int Threads = 128;
        public int GlobalInt = 64;
        public int GlobalFloat = 64;
        public int GlobalBool = 256;
        public int GlobalStr = 32;

        /// <summary>Simulator only: instructions per slice that count as a used-up slice (0 = the
        /// clock alone decides; set it for a paused / stepped clock so loops still yield).</summary>
        public int MaxSliceOps;

        private static int Clamp(int v, int lo, int hi) =>
            v < lo ? lo
            : v > hi ? hi
            : v;

        public CodeConfig Clamped()
        {
            var c = (CodeConfig)MemberwiseClone();
            c.Stack = Clamp(c.Stack, 8, 1024);
            c.TimeSlice = Clamp(c.TimeSlice, 20, 5000);
            c.TimeSlicePage = Clamp(c.TimeSlicePage, 20, 5000);
            c.Threads = Clamp(c.Threads, 5, 512);
            c.GlobalInt = Clamp(c.GlobalInt, 64, 32000);
            c.GlobalFloat = Clamp(c.GlobalFloat, 64, 32000);
            c.GlobalBool = Clamp(c.GlobalBool, 64, 32000);
            c.GlobalStr = Clamp(c.GlobalStr, 16, 1000);
            return c;
        }

        /// <summary>From citect.ini-style parameters: get(section, key, default) -&gt; string.</summary>
        public static CodeConfig FromParams(Func<string, string, string, string> get)
        {
            // the whole number, saturated to the int range (every clamp range lies inside it),
            // never wrapped
            int Num(string sec, string key, int d)
            {
                string s = Dbf.Trim(
                    get(sec, key, d.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? ""
                );
                if (s.Length == 0)
                    return d;
                if (!Dbf.ParseBig(s, out System.Numerics.BigInteger v))
                    return d;
                return v > int.MaxValue ? int.MaxValue
                    : v < int.MinValue ? int.MinValue
                    : (int)v;
            }
            var c = new CodeConfig();
            c.Stack = Num("Code", "Stack", c.Stack);
            c.TimeSlice = Num("Code", "TimeSlice", c.TimeSlice);
            c.TimeSlicePage = Num("Code", "TimeSlicePage", c.TimeSlicePage);
            c.FullTimeSlice = Num("Code", "FullTimeSlice", 0) != 0;
            c.HaltOnError = Num("Code", "HaltOnError", 1) != 0;
            c.HaltOnInvalidTagData = Num("Code", "HaltOnInvalidTagData", 0) != 0;
            c.WriteLocal = Num("Code", "WriteLocal", 1) != 0;
            c.ScaleCheck = Num("Code", "ScaleCheck", 1) != 0;
            c.EchoError = Num("Code", "EchoError", 1) != 0;
            c.IgnoreCase = Num("Code", "IgnoreCase", 1) != 0;
            c.Threads = Num("Code", "Threads", c.Threads);
            c.GlobalInt = Num("Global", "integer", c.GlobalInt);
            c.GlobalFloat = Num("Global", "float", c.GlobalFloat);
            c.GlobalBool = Num("Global", "boolean", c.GlobalBool);
            c.GlobalStr = Num("Global", "string", c.GlobalStr);
            return c.Clamped();
        }
    }

    /// <summary>Wall clock and a monotonic millisecond counter from one performance counter read.</summary>
    public sealed class RealClock : IClock, IClockReader
    {
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static readonly long Freq = Stopwatch.Frequency;

        /// <summary>The counter / wall clock pair, replaced as one object so a clock shared by
        /// threads (a host reading the time while a VM runs) never pairs a new counter base
        /// with an old wall-clock base (a 10 s jump).</summary>
        private sealed class Base
        {
            public readonly long Ts;
            public readonly ulong Ft;

            public Base(long ts, ulong ft)
            {
                Ts = ts;
                Ft = ft;
            }
        }

        private volatile Base _base;

        /// <summary>Counter ticks between two wall-clock samples (10 s; tests set it lower).</summary>
        internal long ResyncTicks = 10 * Freq;

        public RealClock()
        {
            _base = new Base(Stopwatch.GetTimestamp(), (ulong)DateTime.UtcNow.ToFileTimeUtc());
        }

        private ulong Filetime(long ts)
        {
            Base b = _base;
            long d = ts - b.Ts;
            if (d > ResyncTicks || d < 0)
            {
                b = new Base(ts, (ulong)DateTime.UtcNow.ToFileTimeUtc());
                _base = b;
                d = 0;
            }
            // 100 ns units: d * 10^7 / Freq without overflow for d < 10 s
            return b.Ft + (ulong)(d * 10000000L / Freq);
        }

        public ulong NowFiletime() => Filetime(Stopwatch.GetTimestamp());

        public double MonotonicMs() => Stopwatch.GetTimestamp() * MsPerTick;

        public void Read(out ulong filetime, out double monotonicMs)
        {
            long ts = Stopwatch.GetTimestamp();
            filetime = Filetime(ts);
            monotonicMs = ts * MsPerTick;
        }
    }

    /// <summary>A clock that moves only when told (tests, stepped simulation). TickMs is added
    /// on every monotonic read, so busy code still sees time pass.</summary>
    public sealed class ManualClock : IClock, IClockReader
    {
        public ulong Filetime;
        public double Ms;
        public double TickMs;

        public ManualClock(ulong filetime = 133000000000000000UL, double ms = 0.0, double tickMs = 0.0)
        {
            Filetime = filetime;
            Ms = ms;
            TickMs = tickMs;
        }

        public ulong NowFiletime() => Filetime;

        public double MonotonicMs()
        {
            Ms += TickMs;
            return Ms;
        }

        public void Read(out ulong filetime, out double monotonicMs)
        {
            filetime = Filetime;
            Ms += TickMs;
            monotonicMs = Ms;
        }

        public void Advance(double ms)
        {
            Ms += ms;
            Filetime = (ulong)((long)Filetime + (long)(ms * 10000));
        }
    }

    public static class Clocks
    {
        /// <summary>1601-01-01 to 1970-01-01 in 100 ns.</summary>
        public const long EpochDiff = 116444736000000000L;

        /// <summary>_time32 of a clock (whole seconds since 1970, floor).</summary>
        public static long UnixSeconds(IClock clock)
        {
            long d = (long)clock.NowFiletime() - EpochDiff;
            long q = d / 10000000L;
            if (d % 10000000L != 0 && d < 0)
                q--;
            return q;
        }
    }

    /// <summary>Why a task does not run. UntilMs (monotonic, SleepMS), UntilSec (wall clock
    /// whole seconds, Sleep), Poll (wake when it returns true), Result (called when the task
    /// wakes; its value is pushed as the blocked built-in's result).</summary>
    public sealed class Wait
    {
        public int State = TaskState.Wait;
        public double? UntilMs;
        public long? UntilSec;
        public Func<bool>? Poll;
        public Func<Value?>? Result;
        public string Reason = "";

        public Wait(int state = TaskState.Wait, string reason = "")
        {
            State = state;
            Reason = reason;
        }

        public bool Ready(IClock clock)
        {
            if (UntilMs.HasValue && clock.MonotonicMs() < UntilMs.Value)
                return false;
            if (UntilSec.HasValue && Clocks.UnixSeconds(clock) < UntilSec.Value)
                return false;
            if (Poll != null && !Poll())
                return false;
            return true;
        }
    }

    /// <summary>AddrStack (0x3b): a reference to a stack slot of a task (it pushes type 0xd).</summary>
    public sealed class StackRef : Ref
    {
        public readonly CicodeTask Task;
        public readonly int Index;

        public StackRef(CicodeTask task, int index)
        {
            Task = task;
            Index = index;
        }

        public override Value Get()
        {
            if (Index >= Task.Sp)
                throw new IndexOutOfRangeException("list index out of range");
            return Task.Stack[Index];
        }

        public override void Set(Value value)
        {
            if (Index >= Task.Sp)
                throw new IndexOutOfRangeException("list assignment index out of range");
            Task.Stack[Index] = value;
        }
    }

    /// <summary>AddrCodeVar (0x3c): a reference to one element of a module / global variable.
    /// The runtime gives it a by-reference type (sub 4 -&gt; 5, 6 -&gt; 6, 7 -&gt; 8, 0xc -&gt; 0xe,
    /// indexed 0x15); the simulator pushes it as a VARIANT and keeps that type in BaseType.</summary>
    public sealed class CodeVarRef : Ref
    {
        public readonly CodeVarSlot Slot;
        public readonly long Element;
        private readonly int _baseType;

        public CodeVarRef(CodeVarSlot slot, long element, int baseType)
        {
            Slot = slot;
            Element = element;
            _baseType = baseType;
        }

        public override int BaseType => _baseType;

        public override Value Get() => Slot.Get(Element, out _);

        public override void Set(Value value) => Slot.Set(Element, value);
    }

    /// <summary>Storage of one module or global variable.</summary>
    public sealed class CodeVarSlot
    {
        public readonly CodeVar Cv;
        public readonly int RawType;
        public readonly int VType;

        /// <summary>The element values: ints / OBJECT / TIMESTAMP / QUALITY in I, REAL in D,
        /// STRING in S.</summary>
        public readonly long[]? Ints;
        public readonly double[]? Reals;
        public readonly string[]? Strings;
        public readonly int Count;
        public ulong Q;
        public ulong Ts,
            Vts,
            Qts;

        public static int LoadType(int rawType)
        {
            switch (rawType)
            {
                case 0:
                case 1:
                case 3:
                case 4:
                case 5:
                case 8:
                case 0xA:
                    return Types.Long;
                case 2:
                case 6:
                case 0xB:
                    return Types.Real;
                case 7:
                    return Types.String;
                case 0xC:
                    return Types.Object;
                case 0x12:
                    return Types.ULong;
                case 0x13:
                    return Types.Timestamp;
                case 0x14:
                    return Types.Quality;
            }
            return Types.Long;
        }

        public CodeVarSlot(CodeVar cv, byte[] runImage)
        {
            Cv = cv;
            RawType = cv.RawType;
            VType = LoadType(cv.RawType);
            long n = cv.UnitCount;
            if (n < 1)
                n = 1;
            if (n > 1 << 24)
                n = 1 << 24;
            Count = (int)n;
            if (VType == Types.String)
                Strings = new string[Count];
            else if (VType == Types.Real)
                Reals = new double[Count];
            else
                Ints = new long[Count];
            for (int k = 0; k < Count; k++)
                Init(runImage, k);
            Q = cv.Quality;
            Ts = cv.Timestamp;
            Vts = cv.ValueTimestamp;
            Qts = cv.QualityTimestamp;
        }

        private static bool In(byte[] img, long o, int size) => o >= 0 && o + size <= img.Length;

        private void Init(byte[] img, int k)
        {
            int rt = RawType;
            long a = Cv.DataAddress;
            // a negative offset counts from the end of the image
            long Off(long o) => o < 0 ? img.Length + o : o;
            if (rt == Types.String)
            {
                long o = a + 256L * k;
                o = Off(o);
                if (o < 0)
                    o = 0;
                if (o >= img.Length)
                {
                    Strings![k] = "";
                    return;
                }
                int max = (int)Math.Min(256, img.Length - o);
                Strings![k] = Cp1252.DecodeZ(img, (int)o, max);
                return;
            }
            if (rt == Types.Real || rt == Types.LongReal || rt == Types.RealValue)
            {
                if (Cv.BitWidth == 32)
                {
                    long o = Off(a + 4L * k);
                    Reals![k] = In(img, o, 4) ? BitConverter.ToSingle(img, (int)o) : 0.0;
                }
                else
                {
                    long o = Off(a + 8L * k);
                    Reals![k] = In(img, o, 8) ? BitConverter.ToDouble(img, (int)o) : 0.0;
                }
                return;
            }
            if (rt == Types.Timestamp || rt == Types.Quality)
            {
                long o = Off(a + 8L * k);
                Ints![k] = In(img, o, 8) ? (long)BitConverter.ToUInt64(img, (int)o) : 0;
                return;
            }
            if (rt == Types.Digital)
            {
                long o = Off(a + k / 8);
                Ints![k] = In(img, o, 1) ? (img[o] >> (k % 8)) & 1 : 0;
                return;
            }
            if (rt == Types.Int16 || Cv.BitWidth == 16)
            {
                long o = Off(a + 2L * k);
                Ints![k] = In(img, o, 2) ? BitConverter.ToInt16(img, (int)o) : DefaultInt();
                return;
            }
            if (rt == Types.ULong)
            {
                long o = Off(a + 4L * k);
                Ints![k] = In(img, o, 4) ? BitConverter.ToUInt32(img, (int)o) : DefaultInt();
                return;
            }
            long o4 = Off(a + 4L * k);
            if (Strings != null)
                Strings[k] = "";
            else if (Reals != null)
                Reals[k] = 0.0;
            else
                Ints![k] = In(img, o4, 4) ? BitConverter.ToInt32(img, (int)o4) : DefaultInt();
        }

        private long DefaultInt() => VType == Types.Object ? -1 : 0;

        private Value Make(int idx)
        {
            if (Strings != null)
                return Value.Str(Strings[idx], Q, Ts, Vts, Qts);
            if (Reals != null)
                return Value.RealOf(VType, Reals[idx], Q, Ts, Vts, Qts);
            return Value.Int(VType, Ints![idx], Q, Ts, Vts, Qts);
        }

        /// <summary>The value with the variable's quality / timestamps; an index outside the
        /// array is 0x103 (259) with the type's default.</summary>
        public Value Get(long idx, out int error)
        {
            if (idx >= 0 && idx < Count)
            {
                error = 0;
                return Make((int)idx);
            }
            error = Errors.Overrun;
            Value d = Value.Default(VType);
            return d.WithQ(Q, Ts, Vts, Qts);
        }

        /// <summary>Store an element (by the VALUE's type, the piece converting
        /// to its raw type; the compiler converts first). 0 or the error.</summary>
        public int Set(long idx, Value value)
        {
            if (!(idx >= 0 && idx < Count))
                return Errors.Overrun;
            int i = (int)idx;
            int vt = VType;
            if (vt == Types.String)
            {
                string s = Conv.ToStr(value, NoErr.Instance).S ?? "";
                Strings![i] = s.Length > Types.StringMax ? s.Substring(0, Types.StringMax) : s;
            }
            else if (vt == Types.Real)
                Reals![i] = Conv.ToReal(value, NoErr.Instance).D;
            else if (vt == Types.Timestamp || vt == Types.Quality)
                Ints![i] = value.T == Types.Timestamp || value.T == Types.Quality ? value.I : 0;
            else if (vt == Types.Object)
                Ints![i] = value.T == Types.Object ? value.I : -1;
            else if (vt == Types.ULong)
                Ints![i] = Conv.ToULong(value, NoErr.Instance).I;
            else
            {
                long x = Conv.ToInt(value, NoErr.Instance).I;
                if (RawType == Types.Int16 || Cv.BitWidth == 16)
                    x = Func0Table.SignExtend16(x);
                Ints![i] = x;
            }
            return 0;
        }

        /// <summary>The raw values (tests, debugger): long / double / string.</summary>
        public object?[] Values
        {
            get
            {
                var r = new object?[Count];
                for (int k = 0; k < Count; k++)
                    r[k] =
                        Strings != null ? Strings[k]
                        : Reals != null ? (object)Reals[k]
                        : Ints![k];
                return r;
            }
        }
    }
}
