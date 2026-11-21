using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>The p-code asked for something the VM cannot do (an assignment through a value
    /// that is not a reference, a fill the memory cannot hold ...). The sweep counts these as VM
    /// errors.</summary>
    public sealed class VmError : Exception
    {
        public VmError(string message)
            : base(message) { }
    }

    /// <summary>The Cicode virtual machine.</summary>
    public sealed class CicodeVm
    {
        public Program Library;
        public IBuiltins? Builtins;
        public ITagAccess? Tags;
        public IClock Clock;
        public IAssociations? Associations;
        public readonly CodeConfig Config;
        public readonly Func0Table Table;
        private readonly int[] _ret = new int[0x47B];

        public LineHook? LineHook;

        /// <summary>The variable history recorder (a debugger attached: Stepper.Attach); null =
        /// nothing recorded.</summary>
        public Recorder? Recorder;

        /// <summary>The per-function timing (ctsim/profile); null = nothing timed.</summary>
        public FunctionProfile? Functions;
        public ErrorHook? ErrorHook;

        /// <summary>Called after every raised hardware error once the VM has decided whether it
        /// halts the task (the debugger's error report).</summary>
        public ErrorReportHook? ErrorReport;
        public Action<string>? Log;
        public Func<bool> OtherWork = () => false;
        public readonly Dictionary<string, int> NotSimulatedCounts = new Dictionary<string, int>(
            StringComparer.Ordinal
        );
        public Scheduler? Scheduler;

        private readonly Dictionary<(Program, byte[]), CodeBlock> _codes =
            new Dictionary<(Program, byte[]), CodeBlock>();
        private readonly Dictionary<(Program, long), CodeVarSlot> _slots =
            new Dictionary<(Program, long), CodeVarSlot>();
        private readonly CodeBlock _end;

        public readonly long[] GlbInt;
        public readonly double[] GlbFloat;
        public readonly long[] GlbBool;
        public readonly string[] GlbStr;

        /// <summary>The CRT rand() state (MSVC LCG), per VM.</summary>
        public long RandSeed = 1;

        public CicodeVm(
            Program? library = null,
            IBuiltins? builtins = null,
            ITagAccess? tags = null,
            IClock? clock = null,
            IAssociations? associations = null,
            CodeConfig? config = null,
            Func0Table? table = null
        )
        {
            Library = library ?? new Program();
            Builtins = builtins;
            Tags = tags;
            Clock = clock ?? new RealClock();
            Associations = associations;
            Config = (config ?? new CodeConfig()).Clamped();
            Table = table ?? Func0Table.Default;
            for (int k = 0; k < _ret.Length; k++)
                _ret[k] = Types.Void;
            foreach (var b in Table.Entries)
                if (b.Index >= 0 && b.Index < _ret.Length)
                    _ret[b.Index] = b.Ret;
            _end = new CodeBlock(new Program(), new byte[] { 0xFF }, null, "<return>");
            GlbInt = new long[Config.GlobalInt];
            GlbFloat = new double[Config.GlobalFloat];
            GlbBool = new long[Config.GlobalBool];
            GlbStr = new string[Config.GlobalStr];
            for (int k = 0; k < GlbStr.Length; k++)
                GlbStr[k] = "";
        }

        // ------------------------------------------------------------------ code / variables
        public CodeBlock CodeFor(Function func, Program? program = null)
        {
            Program prog = program ?? Library;
            var key = (prog, func.Code);
            if (!_codes.TryGetValue(key, out var c))
            {
                c = new CodeBlock(prog, func.Code, func);
                _codes[key] = c;
            }
            return c;
        }

        /// <summary>Decoded code blocks kept (page expressions, run-time call text); past it the
        /// block cache starts again, so a host that builds new code bytes per call cannot grow it.</summary>
        public int BlockCache = 8192;

        private readonly Dictionary<BlockKey, CodeBlock> _blocks = new Dictionary<BlockKey, CodeBlock>();

        /// <summary>The number of cached blocks (tests).</summary>
        public int BlockCount => _blocks.Count;

        /// <summary>The decoded form of a code block, cached by its CONTENT and program (a block
        /// rebuilt as new bytes per evaluation reuses the entry instead of adding one).</summary>
        public CodeBlock CodeForBlock(byte[] code, Program program, string label = "")
        {
            var key = new BlockKey(program, code);
            if (!_blocks.TryGetValue(key, out var c) || c.Program != program)
            {
                if (_blocks.Count >= BlockCache)
                    _blocks.Clear();
                c = new CodeBlock(program, (byte[])code.Clone(), null, label);
                _blocks[new BlockKey(program, c.Code)] = c;
            }
            return c;
        }

        private readonly struct BlockKey : IEquatable<BlockKey>
        {
            private readonly Program _p;
            private readonly byte[] _b;
            private readonly int _h;

            public BlockKey(Program p, byte[] b)
            {
                _p = p;
                _b = b;
                int h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p);
                foreach (byte x in b)
                    h = h * 31 + x;
                _h = h;
            }

            public bool Equals(BlockKey o)
            {
                if (!ReferenceEquals(_p, o._p) || _b.Length != o._b.Length || _h != o._h)
                    return false;
                if (ReferenceEquals(_b, o._b))
                    return true;
                for (int i = 0; i < _b.Length; i++)
                    if (_b[i] != o._b[i])
                        return false;
                return true;
            }

            public override bool Equals(object? obj) => obj is BlockKey k && Equals(k);

            public override int GetHashCode() => _h;
        }

        /// <summary>The slot key of an AddrCodeVar operand (a RUN DataAddress) that no
        /// variable starts at: negative, so it never meets a CODEVAR offset.</summary>
        public static long DataAddressKey(long dataAddress) => -2 - Math.Max(0, dataAddress);

        /// <summary>The storage of a module / global variable (one per library variable record).</summary>
        public CodeVarSlot Slot(Program program, CodeVar? cv, long offset = -1)
        {
            long off = cv != null ? cv.Offset : offset;
            var key = (program, off);
            if (_slots.TryGetValue(key, out var s))
                return s;
            // a library a reload replaced (ShareVariables): its storage lives with it, not here, so
            // that the old program can go once no task runs its code
            Dictionary<long, CodeVarSlot>? retired = null;
            if (_retired.TryGetValue(program, out retired) && retired.TryGetValue(off, out s))
                return s;
            if (cv == null && offset < -1)
            {
                // an AddrCodeVar DataAddress no variable starts at (DataAddressKey)
                long d = -2 - offset;
                cv = new CodeVar(offset, "?data@" + d.ToString(CultureInfo.InvariantCulture), Types.Long)
                {
                    DataAddress = d,
                };
            }
            cv ??= new CodeVar(
                offset,
                "?codevar@" + offset.ToString(CultureInfo.InvariantCulture),
                Types.Long
            );
            s = new CodeVarSlot(cv, program.RunImage);
            if (retired != null)
                retired[off] = s;
            else
                _slots[key] = s;
            return s;
        }

        /// <summary>The storage of replaced libraries, by the library (weak: it goes with the last
        /// task that runs its code).</summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            Program,
            Dictionary<long, CodeVarSlot>
        > _retired = new System.Runtime.CompilerServices.ConditionalWeakTable<
            Program,
            Dictionary<long, CodeVarSlot>
        >();

        /// <summary>Carry module and global variables over to a reloaded library.</summary>
        public int ShareVariables(Program from, Program to)
        {
            if (ReferenceEquals(from, to))
                return 0;
            // the old library's storage: what it made (its own keys, or kept for it by an earlier
            // reload)
            var mine = new Dictionary<long, CodeVarSlot>();
            if (_retired.TryGetValue(from, out var before))
                foreach (var kv in before)
                    mine[kv.Key] = kv.Value;
            var keys = new List<(Program, long)>();
            foreach (var kv in _slots)
                if (ReferenceEquals(kv.Key.Item1, from))
                {
                    mine[kv.Key.Item2] = kv.Value;
                    keys.Add(kv.Key);
                }
            foreach (var k in keys)
                _slots.Remove(k);
            var old = new Dictionary<(string, long), CodeVar>();
            foreach (var cv in from.CodeVarOrder)
            {
                var k = (cv.Name.ToLowerInvariant(), cv.UnitType);
                if (!old.ContainsKey(k))
                    old[k] = cv;
            }
            int n = 0;
            foreach (var cv in to.CodeVarOrder)
            {
                if (!old.TryGetValue((cv.Name.ToLowerInvariant(), cv.UnitType), out var o))
                    continue;
                if (o.RawType != cv.RawType || o.BitWidth != cv.BitWidth || o.UnitCount != cv.UnitCount)
                    continue;
                var nk = (to, cv.Offset);
                if (_slots.ContainsKey(nk))
                    continue;
                // one storage for both: the old one's when it exists, else the new one's (made
                // now, so that neither side can make its own later)
                if (mine.TryGetValue(o.Offset, out var s))
                    _slots[nk] = s;
                else
                    mine[o.Offset] = Slot(to, cv);
                n++;
            }
            _retired.Remove(from);
            _retired.Add(from, mine);
            var drop = new List<(Program, byte[])>();
            foreach (var k in _codes.Keys)
                if (ReferenceEquals(k.Item1, from))
                    drop.Add(k);
            foreach (var k in drop)
                _codes.Remove(k);
            // the cached blocks of other programs (page code, compiled-table code, call text)
            // resolved their FuncCallByLookup targets by name in the old library: decoded again at
            // their next use, they call the new code (an animator over this VM: its pages)
            _blocks.Clear();
            return n;
        }

        /// <summary>A module / global variable's storage by name (first match, case-insensitive).</summary>
        public CodeVarSlot? Variable(string name, Program? program = null)
        {
            Program prog = program ?? Library;
            string low = name.ToLowerInvariant();
            foreach (var cv in prog.CodeVarOrder)
                if (cv.Name.ToLowerInvariant() == low)
                    return Slot(prog, cv);
            return null;
        }

        public Function? Function(string name) => Library.Function(name);

        // ------------------------------------------------------------------ tasks
        /// <summary>A task that calls a user function with the arguments (converted to the
        /// parameter types like the compiler's Convert) and stops with its return value in
        /// task.Result.</summary>
        public CicodeTask TaskForCall(
            Function? fn,
            IReadOnlyList<Value> args,
            bool foreground = false,
            Program? program = null,
            string name = "",
            string missing = ""
        )
        {
            var t = new CicodeTask(
                this,
                name.Length > 0 ? name : (fn != null ? fn.Name : missing),
                foreground
            );
            if (fn == null)
            {
                t.State = TaskState.Halt;
                t.HaltCode = Errors.InvalidFunction;
                t.HaltReason = "function " + missing + " not found";
                return t;
            }
            for (int k = 0; k < args.Count; k++)
            {
                Value v = args[k];
                if (k < fn.Args.Length)
                    v = Conv.Convert(v, Types.SigType(fn.Args[k]), NoErr.Instance);
                t.Push(v);
            }
            t.Code = _end;
            t.Ip = 0;
            Enter(t, fn, t.Sp, program);
            return t;
        }

        public CicodeTask TaskForCall(
            string name,
            IReadOnlyList<Value> args,
            bool foreground = false,
            Program? program = null,
            string taskName = ""
        )
        {
            Function? fn = (program ?? Library).Function(name);
            return TaskForCall(fn, args, foreground, program, taskName, name);
        }

        /// <summary>A task that runs a code block (a page expression / command, a TaskNew call
        /// text) from its first byte; an expression leaves its value in task.Result.</summary>
        public CicodeTask TaskForBlock(byte[] code, Program program, bool foreground = true, string name = "")
        {
            var t = new CicodeTask(this, name, foreground);
            try
            {
                t.Code = CodeForBlock(code, program, name);
            }
            catch (DecodeError e)
            {
                t.State = TaskState.Halt;
                t.HaltReason = "invalid code: " + e.Message;
                return t;
            }
            t.Ip = t.Code.Start;
            return t;
        }

        /// <summary>Set up a call of fn whose argc arguments are on the stack (the caller's frame
        /// must already be in task.Code / task.Ip).</summary>
        private void Enter(CicodeTask t, Function fn, int argc, Program? program = null)
        {
            t.Frames.Add(
                new Frame
                {
                    Code = t.Code!,
                    Ip = t.Ip,
                    Base = t.Base,
                    Func = t.Func,
                    Line = t.Line,
                }
            );
            if (fn.NotCompiled != null)
            {
                // (not 'too many arguments': its code is a placeholder, it has no parameters)
                t.Func = fn;
                t.Base = t.Sp - argc;
                Fatal(
                    t,
                    Errors.InvalidFunction,
                    fn.Name + " did not compile: " + fn.NotCompiled,
                    opError: true,
                    unwind: false
                );
                return;
            }
            CodeBlock code = CodeFor(fn, program);
            int maxargs = code.HasEntry ? code.EntryCount - 1 : 0;
            t.Func = fn;
            t.Base = t.Sp - argc;
            Recorder?.Enter(t);
            Functions?.Enter(t, fn);
            t.Code = code;
            t.Ip = code.HasEntry && argc >= 0 && argc < code.EntryCount ? code.D.Entry![argc] : code.Start;
            if (argc < fn.MinParam)
                Fatal(t, Errors.TooFewArgs, fn.Name + ": too few arguments", opError: true, unwind: false);
            else if (argc > maxargs)
                Fatal(t, Errors.TooManyArgs, fn.Name + ": too many arguments", opError: true, unwind: false);
        }

        // ------------------------------------------------------------------ errors
        /// <summary>Raise a Cicode hardware error.</summary>
        public void RaiseError(CicodeTask task, int code, bool halt)
        {
            if (task.Unwinding)
                return;
            task.Error = code;
            if (code == 0)
                return; // code 0 only clears IsError (no error event)
            int level = task.ErrSetLevel;
            Log?.Invoke(
                (level > 0 ? "SUPPRESSED hardware alarm" : "Hardware alarm")
                    + " code "
                    + code
                    + " Task="
                    + task.Name
            );
            bool handled = ErrorHook != null && ErrorHook(task, code);
            bool halting = !handled && level == 0 && halt && Config.HaltOnError;
            if (halting)
            {
                task.State = TaskState.Halt;
                task.HaltCode = code;
                task.HaltReason = "hardware error " + code;
                task.Unwinding = true;
            }
            ErrorReport?.Invoke(task, code, halting, level > 0);
        }

        /// <summary>An error that always halts.</summary>
        public void Fatal(
            CicodeTask task,
            int code,
            string reason = "",
            bool opError = false,
            bool unwind = true
        )
        {
            if (task.Unwinding)
                return;
            if (opError)
                task.SetOpError(code);
            Log?.Invoke(("Hardware alarm code " + code + " Task=" + task.Name + " " + reason).TrimEnd());
            task.State = TaskState.Halt;
            task.HaltCode = code;
            task.HaltReason = reason.Length > 0 ? reason : "hardware error " + code;
            if (unwind)
                task.Unwinding = true;
            ErrorReport?.Invoke(task, code, true, task.ErrSetLevel > 0);
        }

        /// <summary>Halt: state Halt.</summary>
        public void Halt(CicodeTask task, string reason = "Halt()")
        {
            task.State = TaskState.Halt;
            task.HaltReason = reason;
        }

        /// <summary>End the task's slice now: a background task is re-queued, a foreground task gets 285 and halts (Sleep(0) / SleepMS(0)).</summary>
        public void Preempt(CicodeTask task)
        {
            if (task.Foreground)
            {
                Fatal(
                    task,
                    Errors.FgTooLong,
                    "foreground Cicode blocked for more than a slice",
                    unwind: false
                );
                return;
            }
            task.YieldNow = true;
        }

        // ------------------------------------------------------------------ references
        /// <summary>Write through a by-reference argument (a VARIANT holding a Ref). The
        /// referenced stack slot keeps its type (the value is converted) and its quality (a
        /// built-in writes raw data).</summary>
        public void AssignRef(in Value target, Value value)
        {
            if (!(target.O is Ref r))
                throw new VmError("an assignment through a value that is not a reference");
            AssignRef(r, value);
        }

        public void AssignRef(Ref r, Value value)
        {
            if (r is StackRef sr)
            {
                Value old = sr.Get();
                Value nv = ConvertTo(old.T, value);
                nv.T = old.T;
                nv.Q = old.Q;
                nv.Ts = old.Ts;
                nv.Vts = old.Vts;
                nv.Qts = old.Qts;
                sr.Set(nv);
                return;
            }
            r.Set(value);
        }

        private static Value ConvertTo(int t, in Value v)
        {
            if (t == Types.String)
                return Conv.ToStr(v, NoErr.Instance);
            if (Types.IsRealType(t))
                return Conv.ToReal(v, NoErr.Instance);
            if (t == Types.Object)
                return Conv.ToObject(v, NoErr.Instance);
            if (t == Types.Timestamp || t == Types.Quality)
                return Value.Int(t, v.T == t ? v.I : 0, v.Q);
            return Conv.ToInt(v, NoErr.Instance);
        }

        // ------------------------------------------------------------------ running
        /// <summary>Run a task to its end without a scheduler (a wait is an error here: use a
        /// Scheduler for code that sleeps).</summary>
        public Value? Run(CicodeTask task, int maxSlices = 1000000)
        {
            int n = 0;
            while (!task.Done)
            {
                if (task.State == TaskState.Wait || task.State == TaskState.Sleep)
                {
                    var w = task.Wait;
                    if (w != null && w.Ready(Clock))
                    {
                        Resume(task);
                        continue;
                    }
                    throw new InvalidOperationException(
                        $"task {task.Name} waits ({w?.Reason ?? ""}); run it with a Scheduler"
                    );
                }
                RunSlice(task);
                n++;
                if (n >= maxSlices)
                    throw new InvalidOperationException(
                        $"task {task.Name} did not end in {maxSlices} slices"
                    );
            }
            return task.Result;
        }

        /// <summary>Run a user function to its end and return its value (null for VOID).</summary>
        public Value? Call(string name, params object?[] args)
        {
            var vals = new Value[args.Length];
            for (int k = 0; k < args.Length; k++)
                vals[k] = ValueOf(args[k]);
            var t = TaskForCall(name, vals);
            Value? r = Run(t);
            if (t.State == TaskState.Halt)
                throw new CicodeHalted(t);
            return r;
        }

        /// <summary>Run a page expression block in the foreground; task.Result holds its value.</summary>
        public CicodeTask Evaluate(byte[] code, Program program)
        {
            var t = TaskForBlock(code, program, true);
            if (!t.Done)
                Run(t);
            return t;
        }

        /// <summary>A waiting task becomes ready: a blocked built-in's result is pushed now, as
        /// its declared return type (a VOID built-in pushes nothing; the code after the call
        /// expects exactly what a direct return pushes).</summary>
        public void Resume(CicodeTask task)
        {
            var w = task.Wait;
            task.Wait = null;
            if (task.PendingResult)
            {
                task.PendingResult = false;
                int outerBuiltin = task.CurrentBuiltin;
                ArgList outerArgs = task.CurrentArgs;
                task.CurrentBuiltin = task.PendingBuiltin;
                var pa = task.PendingArgs;
                task.CurrentArgs = pa != null ? new ArgList(pa, 0, pa.Length) : default;
                task.PendingArgs = null;
                Value? r;
                try
                {
                    r = w?.Result?.Invoke();
                }
                finally
                {
                    task.CurrentBuiltin = outerBuiltin;
                    task.CurrentArgs = outerArgs;
                    task.PendingBuiltin = -1;
                }
                int ret = task.HasPendingCall ? task.PendingRet : Types.Void;
                ulong q = task.HasPendingCall ? task.PendingQ : Quality.Good;
                ulong ts = task.HasPendingCall ? task.PendingTs : 0;
                ulong vts = task.HasPendingCall ? task.PendingVts : 0;
                ulong qts = task.HasPendingCall ? task.PendingQts : 0;
                task.HasPendingCall = false;
                bool has = WrapResult(task, ret, r ?? Value.Nothing, q, ts, vts, qts, out Value v);
                if (task.Unwinding)
                    return; // a NULL string result with the halt flag
                if (has)
                    task.Push(v);
            }
            if (task.State == TaskState.Wait || task.State == TaskState.Sleep)
                task.State = TaskState.Ready;
        }

        // ------------------------------------------------------------------ built-in calls
        internal const int BValue = 0,
            BNone = 1,
            BBlock = 2,
            BNotSim = 3,
            BHalted = 4;

        /// <summary>Func (0x05): the result gets the merged quality and the latest timestamps of all arguments (0 = now; no arguments: good, 0); a built-in
        /// declared INT returns 16 bits sign-extended; a NULL string result is 530.</summary>
        internal int CallBuiltin(CicodeTask task, int idx, ArgList args, out Value result, out string nsName)
        {
            result = default;
            nsName = "";
            ulong now = task.NowFt;
            ulong q = Quality.Good;
            ulong ts = 0,
                vts = 0,
                qts = 0;
            for (int k = 0; k < args.Count; k++)
            {
                ref readonly Value a = ref args[k];
                if (a.Q != q)
                    q = Quality.Merge(a.Q, q);
                ulong x = a.Ts != 0 ? a.Ts : now;
                ulong y = ts != 0 ? ts : now;
                ts = x > y ? x : y;
                x = a.Vts != 0 ? a.Vts : now;
                y = vts != 0 ? vts : now;
                vts = x > y ? x : y;
                x = a.Qts != 0 ? a.Qts : now;
                y = qts != 0 ? qts : now;
                qts = x > y ? x : y;
            }
            var bi = Builtins;
            if (bi == null)
            {
                nsName = Table.Name(idx);
                return BNotSim;
            }
            Value r;
            int outer = task.CurrentBuiltin;
            ArgList outerArgs = task.CurrentArgs;
            task.CurrentBuiltin = idx;
            task.CurrentArgs = args;
            try
            {
                r = bi.Call(idx, task, args);
            }
            catch (HardwareError e)
            {
                RaiseError(task, e.Code, e.Halt);
                r = e.Result;
            }
            finally
            {
                task.CurrentBuiltin = outer;
                task.CurrentArgs = outerArgs;
            }
            if (r.IsNotSimulated)
            {
                nsName = (string?)r.O ?? Table.Name(idx);
                return BNotSim;
            }
            if (task.Unwinding)
                return BHalted;
            int ret = idx >= 0 && idx < _ret.Length ? _ret[idx] : Types.Void;
            if (r.IsBlock)
            {
                task.HasPendingCall = true;
                task.PendingRet = ret;
                task.PendingBuiltin = idx;
                task.PendingArgs = args.Count > 0 ? args.ToArray() : null;
                task.PendingQ = q;
                task.PendingTs = ts;
                task.PendingVts = vts;
                task.PendingQts = qts;
                return BBlock;
            }
            return WrapResult(task, ret, r, q, ts, vts, qts, out result)
                ? BValue
                : (task.Unwinding ? BHalted : BNone);
        }

        /// <summary>A built-in's result read as an integer (its low 64 bits): a REAL truncated
        /// toward zero (0 for NaN or beyond 1e300), a STRING's decimal integer text (else 0).</summary>
        internal static long AsInt(in Value r)
        {
            if (r.IsNothing)
                return 0;
            if (Types.IsRealType(r.T))
            {
                double x = r.D;
                return !double.IsNaN(x) && Math.Abs(x) < 1e300 ? FloatText.TruncLow64(x) : 0;
            }
            if (r.T == Types.String)
                return r.S != null && Dbf.ParseInt(r.S, out long v) ? v : 0;
            if (r.T == Types.Variant && r.O != null)
                return 0;
            return r.I;
        }

        /// <summary>The number of a built-in's STRING result read as a REAL
        /// (<see cref="FloatText.TryParseNumber"/>); false when it holds none.</summary>
        internal static bool ResultReal(string s, out double v) => FloatText.TryParseNumber(s, out v);

        /// <summary>The text of a built-in's result read as a STRING: a REAL in its shortest
        /// round-trip form, a TIMESTAMP / QUALITY as its unsigned 64-bit number, an integer in
        /// decimal.</summary>
        internal static string ResultText(in Value r)
        {
            if (Types.IsRealType(r.T))
                return FloatText.Shortest(r.D);
            if (r.T == Types.String)
                return r.S ?? "";
            if (r.T == Types.Variant && r.O != null)
                return "<ref>";
            if (r.T == Types.Timestamp || r.T == Types.Quality)
                return unchecked((ulong)r.I).ToString(CultureInfo.InvariantCulture);
            return r.I.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsNone(in Value r) => r.IsNothing || (r.T == Types.String && r.S == null);

        /// <summary>The built-in's result as its declared return type. False = no value (VOID).</summary>
        private bool WrapResult(
            CicodeTask task,
            int ret,
            in Value r,
            ulong q,
            ulong ts,
            ulong vts,
            ulong qts,
            out Value result
        )
        {
            result = default;
            switch (ret)
            {
                case Types.Void:
                    return false;
                case Types.Int16:
                    result = Value.Long(Func0Table.SignExtend16(AsInt(r)), q, ts, vts, qts);
                    return true;
                case Types.Long:
                    result = Value.Long((int)AsInt(r), q, ts, vts, qts);
                    return true;
                case Types.Real:
                {
                    double x;
                    if (IsNone(r))
                        x = 0.0;
                    else if (Types.IsRealType(r.T))
                        x = r.D;
                    else if (r.T == Types.String)
                        x = ResultReal(r.S!, out double f) ? f : 0.0;
                    else if (r.T == Types.Variant && r.O != null)
                        x = 0.0;
                    else if (r.T == Types.Timestamp || r.T == Types.Quality)
                        x = FloatText.FromUInt64(unchecked((ulong)r.I)); // the unsigned 64-bit payload
                    else
                        x = r.I;
                    result = Value.Real(x, q, ts, vts, qts);
                    return true;
                }
                case Types.String:
                {
                    string s;
                    if (IsNone(r))
                    {
                        RaiseError(task, Errors.Null, true);
                        s = "";
                    }
                    else
                        s = ResultText(r);
                    if (s.Length > Types.StringMax)
                        s = s.Substring(0, Types.StringMax);
                    result = Value.Str(s, q, ts, vts, qts);
                    return true;
                }
                case Types.Object:
                    result = Value.Object(IsNone(r) ? -1 : AsInt(r), q, ts, vts, qts);
                    return true;
                case Types.Variant:
                {
                    Ref rf;
                    if (r.T == Types.Variant && r.O is Ref rr)
                        rf = rr;
                    else
                        rf = new ValueRef(r.IsNothing ? Value.Int(Types.Void, 0) : r);
                    result = Value.Variant(rf, q, ts, vts, qts);
                    return true;
                }
                case Types.Timestamp:
                case Types.Quality:
                    result = Value.Int(ret, AsInt(r), q, ts, vts, qts);
                    return true;
            }
            Value o = IsNone(r) ? Value.Int(ret, 0) : r.Retyped(ret);
            result = o.Bare(0);
            return true;
        }

        /// <summary>FuncCallByLookup's target: resolved by name on first use and cached
        /// (FUNCLOOKUP FunctionHandle, -1 until resolved).</summary>
        internal Function? ResolveCall(CodeBlock code, int index, string? name)
        {
            var calls = code.Calls;
            var res = code.Resolved;
            if (calls == null || res == null || index >= calls.Length)
            {
                int n = code.Ins.Length;
                code.Calls = calls = new Function?[n];
                code.Resolved = res = new bool[n];
            }
            if (res[index])
                return calls[index];
            Function? fn = !string.IsNullOrEmpty(name) ? Library.Function(name!) : null;
            calls[index] = fn;
            res[index] = true;
            return fn;
        }

        // ------------------------------------------------------------------ association format
        /// <summary>The printf format of a super genie association: a
        /// STRING value '%s'; the associated tag's format word + EU through FormatWordToPrintf; not
        /// associated: '%6.2f'-style (word 0x206) and false (expression error 323).</summary>
        public string AssociationFormat(CicodeTask task, string name, int t, out bool ok)
        {
            if (t == Types.String)
            {
                ok = true;
                return "%s";
            }
            var assoc = Associations;
            if (assoc is IAssociationFormat af)
            {
                if (!af.Format(name, task, out long word, out string eu))
                {
                    ok = false;
                    return Conv.FormatWordToPrintf(0x206, "", t);
                }
                ok = true;
                return Conv.FormatWordToPrintf(word, eu ?? "", t);
            }
            ok = assoc != null && assoc.Resolve(name, task, out _);
            return Conv.FormatWordToPrintf(0x206, "", t);
        }

        /// <summary>A StringFmt suffix with '?name?' tokens, association-resolved (verify the
        /// token rules).</summary>
        public string ResolveSuffix(CicodeTask task, string suffix)
        {
            var assoc = Associations;
            if (assoc == null)
                return suffix;
            var parts = suffix.Split('?');
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < parts.Length; k++)
            {
                string p = parts[k];
                if (k % 2 == 1 && k < parts.Length - 1)
                    sb.Append(assoc.Resolve(p, task, out Value v) ? Conv.ValueText(v, out _) : "");
                else
                    sb.Append(p);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ plain values
        /// <summary>A plain value as a VM value (int -&gt; INT, double -&gt; REAL, string -&gt;
        /// STRING, null -&gt; VOID).</summary>
        public static Value ValueOf(object? x)
        {
            switch (x)
            {
                case Value v:
                    return v;
                case bool b:
                    return Value.Long(b ? 1 : 0);
                case int i:
                    return Value.Long(i);
                case long l:
                    return Value.Long((int)l);
                case short s:
                    return Value.Long(s);
                case byte by:
                    return Value.Long(by);
                case sbyte sb:
                    return Value.Long(sb);
                case ushort us:
                    return Value.Long(us);
                case uint ui:
                    return Value.Long((int)ui);
                case ulong ul:
                    return Value.Long((int)ul);
                case double d:
                    return Value.Real(d);
                case float f:
                    return Value.Real(f);
                case decimal m:
                    return Value.Real((double)m);
                case null:
                    return Value.Int(Types.Void, 0);
            }
            // anything else as its invariant text (never the current culture's)
            string str = System.Convert.ToString(x, CultureInfo.InvariantCulture) ?? "";
            return Value.Str(str.Length > Types.StringMax ? str.Substring(0, Types.StringMax) : str);
        }

        // ------------------------------------------------------------------ the interpreter
        /// <summary>Run the task for one time slice. Returns its state.</summary>
        public int RunSlice(CicodeTask task)
        {
            var outer = Current;
            Current = task;
            var fp = Functions;
            if (fp != null)
            {
                if (outer != null)
                    fp.SliceEnd(outer); // (a slice inside another's: not its time)
                fp.SliceStart(task);
            }
            try
            {
                return Interpreter.RunSlice(this, task);
            }
            finally
            {
                Current = outer;
                if (fp != null)
                {
                    fp.SliceEnd(task);
                    if (outer != null)
                        fp.SliceStart(outer);
                }
            }
        }

        /// <summary>The task whose slice runs now (null between slices): a host's log line names it.</summary>
        public CicodeTask? Current { get; private set; }

        // --- helpers the interpreter shares ------------------------------------------------
        /// <summary>Load (0x02): read a tag's cached value.</summary>
        internal void Load(CicodeTask task, TagRef r, long? index)
        {
            task.LastTagRef = r;
            task.TagError = 0;
            var tags = Tags;
            Value v;
            int e;
            if (tags == null)
            {
                v = Value.Long(0, 0);
                e = Errors.TagNotFound;
            }
            else
            {
                if (index.HasValue && tags is ITagElementAccess ea)
                    v = ea.ReadElement(r, index.Value, task);
                else
                    v = tags.Read(r, task);
                e = task.TagError;
            }
            task.SetOpError(e);
            task.Push(v);
            if (task.Sp >= Config.Stack)
            {
                task.Sp--;
                Fatal(task, Errors.Stack, "Cicode stack overflow");
                return;
            }
            if (e != 0 && task.ErrSetLevel == 0 && Config.HaltOnInvalidTagData && !task.Foreground)
                Fatal(task, Errors.InvalidTagData, "invalid tag data");
        }

        /// <summary>Store (0x03): write a tag.</summary>
        internal void Store(CicodeTask task, TagRef r, Value v, long? index, bool ok)
        {
            int oe = task.OpError;
            if (oe == Errors.Range && task.ScaleCheck)
            {
                task.OpError = 0;
                RaiseError(task, Errors.Range, false);
            }
            else if (ok)
            {
                if (oe == 0)
                {
                    Value d = v.T == Types.Variant ? Value.Deref(v) : v;
                    var tags = Tags;
                    int e;
                    if (tags == null)
                        e = Errors.TagNotFound;
                    else if (index.HasValue && tags is ITagElementAccess ea)
                        e = ea.WriteElement(r, index.Value, d, task);
                    else
                        e = tags.Write(r, d, task);
                    if (e != 0 && task.ExprError == 0)
                        task.ExprError = e;
                }
                task.OpError = 0;
            }
        }

        /// <summary>StoreCodeVar (0x3d): skipped while an op error is pending; the value and its
        /// quality / timestamps go into the variable; an error clears them and raises.</summary>
        internal void StoreCodeVar(CicodeTask task, CodeVarSlot sl, long idx, Value v)
        {
            if (task.OpError == 0)
            {
                Value d = v.T == Types.Variant ? Value.Deref(v) : v;
                var rec = Recorder;
                Value old = rec != null ? sl.Get(idx, out _) : default;
                int e = sl.Set(idx, d);
                task.OpError = e;
                if (e == 0)
                {
                    sl.Q = d.Q;
                    sl.Ts = d.Ts;
                    sl.Vts = d.Vts;
                    sl.Qts = d.Qts;
                    rec?.CodeVar(task, sl, idx, old, sl.Get(idx, out _));
                }
                else
                {
                    sl.Q = 0;
                    sl.Ts = 0;
                    sl.Vts = 0;
                    sl.Qts = 0;
                    RaiseError(task, e, true);
                    if (task.Unwinding)
                        return;
                }
            }
            if (task.OpError != 0 && task.ExprError == 0)
                task.ExprError = task.OpError;
            task.OpError = 0;
        }

        /// <summary>The Bar arithmetic (case 0x37, constants 100.0 / 1.0 /
        /// 1.1920928955078125e-07): the percent clamped to 0..100, and whether it was out of range.</summary>
        public static double BarPercent(double x, double engZero, double engFull, out bool oor)
        {
            double rng = engFull - engZero;
            if (rng == 0.0)
                rng = 1.0;
            double k = 100.0 / rng;
            if (k != 1.0 || engZero != 0.0)
                x = x * k + (0.0 - k * engZero);
            const double eps = 1.1920928955078125e-07;
            oor = (0.0 - x) > eps || (x - 100.0) > eps;
            double r;
            if (0.0 <= x)
                r = x <= 100.0 ? x : 100.0;
            else
                r = 0.0;
            if (double.IsNaN(x))
                r = 0.0;
            return r;
        }

        internal Value Bar(CicodeTask task, Value v, in Ins x)
        {
            if (Types.TypeClass(v.T) != Types.ClassReal)
            {
                v = Conv.ToReal(v, task);
                if (task.Unwinding)
                    return v;
            }
            double xv = v.D;
            if (x.B == 2)
            {
                // the last tag's own range: the same arithmetic and clamp; it writes the op
                // error, 257 outside, else 0
                if (Tags is ITagScale tsc && tsc.TryScale(task.LastTagRef, task, out double z, out double f))
                {
                    double rr = BarPercent(xv, z, f, out bool oorT);
                    if (oorT)
                        task.SetOpError(Errors.Range);
                    else
                        task.OpError = 0;
                    return Value.Real(rr, v.Q, v.Ts, v.Vts, v.Qts);
                }
                return Value.Real(xv, v.Q, v.Ts, v.Vts, v.Qts);
            }
            if (x.B == 0)
                return Value.Real(xv, v.Q, v.Ts, v.Vts, v.Qts);
            var sc = (Scale)x.O!;
            double r = BarPercent(xv, sc.EngZero, sc.EngFull, out bool oor);
            if (oor)
                task.SetOpError(Errors.Range);
            else
                task.OpError = 0;
            return Value.Real(r, v.Q, v.Ts, v.Vts, v.Qts);
        }

        /// <summary>StoreStack (0x35) when the types differ.</summary>
        internal static Value StoreStackConv(CicodeTask task, in Value slot, in Value v)
        {
            int t = slot.T;
            switch (t)
            {
                case Types.Real:
                {
                    Value r = v.T == Types.Real ? v : Conv.ToReal(v, task);
                    return Value.Real(r.D, r.Q, r.Ts, r.Vts, r.Qts);
                }
                case Types.Long:
                {
                    if (Types.TypeClass(v.T) == Types.ClassInt)
                        return Value.Long((int)v.I, v.Q, v.Ts, v.Vts, v.Qts);
                    Value r = Conv.ToInt(v, task);
                    return Value.Long(r.I, r.Q, r.Ts, r.Vts, r.Qts);
                }
                case Types.String:
                {
                    Value r = v.T == Types.String ? v : Conv.ToStr(v, task);
                    string s = r.S ?? "";
                    if (s.Length > Types.StringMax)
                        s = s.Substring(0, Types.StringMax);
                    return Value.Str(s, r.Q, r.Ts, r.Vts, r.Qts);
                }
                case Types.Object:
                {
                    Value r = v.T == Types.Object ? v : Conv.ToObject(v, task);
                    return Value.Object(r.I, r.Q, r.Ts, r.Vts, r.Qts);
                }
                case Types.Timestamp:
                case Types.Quality:
                {
                    Value d = v.T == Types.Variant ? Value.Deref(v) : v;
                    if (d.T == t)
                        return Value.Int(t, d.I, d.Q, d.Ts, d.Vts, d.Qts);
                    task.RaiseError(Errors.TypeMismatch, true);
                    return Value.Int(t, 0, 0);
                }
            }
            return slot.Bare(0);
        }
    }
}
