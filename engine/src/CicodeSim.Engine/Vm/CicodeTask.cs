using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>A saved call frame: the caller's code, the instruction to return to, its frame
    /// base, function and line.</summary>
    public struct Frame
    {
        public CodeBlock Code;
        public int Ip;
        public int Base;
        public Function? Func;
        public int Line;
    }

    /// <summary>How a debugger step proceeds (the VM's debugger seam).</summary>
    public enum StepMode
    {
        None,
        Into,
        Over,
        Out,
    }

    /// <summary>One Cicode task: what the runtime keeps in its task record (0xe8 bytes;
    /// offsets noted where the interpreter uses them).</summary>
    public sealed class CicodeTask : IOpContext
    {
        public readonly CicodeVm Vm;
        public int Handle = -1;
        public string Name;
        public bool Foreground;

        public int State = TaskState.Ready;

        /// <summary>The value stack (Sp elements in use).</summary>
        public Value[] Stack;
        public int Sp;

        public readonly List<Frame> Frames = new List<Frame>();
        public CodeBlock? Code;

        /// <summary>The current instruction (index into the code's instructions).</summary>
        public int Ip;

        /// <summary>Current frame base (local slot 0).</summary>
        public int Base;

        public int Line;

        public Function? Func;

        /// <summary>Bit 0.</summary>
        public bool WriteLocal;

        /// <summary>Bit 1.</summary>
        public bool ScaleCheck;

        /// <summary>Bit 2.</summary>
        public bool EchoError;

        /// <summary>Bit 3.</summary>
        public bool IgnoreCaseFlag;
        public bool Profile;

        public int ErrSetLevel;

        /// <summary>IsError.</summary>
        public int Error;

        public int ExprError;

        public int OpError;

        /// <summary>Set by ITagAccess.Read.</summary>
        public int TagError;

        public TagRef? LastTagRef;
        public Wait? Wait;
        public bool YieldNow;

        /// <summary>A blocked built-in owes its result.</summary>
        public bool PendingResult;

        /// <summary>The blocked call's return type and merged quality / timestamps (its result
        /// is converted like a direct return when the task wakes).</summary>
        internal bool HasPendingCall;
        internal int PendingRet;

        /// <summary>The FUNC0 index of the blocked built-in (its result, and an error raised when the
        /// task wakes, are its).</summary>
        internal int PendingBuiltin = -1;

        /// <summary>A copy of the blocked built-in's arguments (an error raised when it wakes shows them).</summary>
        internal Value[]? PendingArgs;
        internal ulong PendingQ,
            PendingTs,
            PendingVts,
            PendingQts;
        public Value? Result;
        public int HaltCode;
        public string HaltReason = "";

        /// <summary>A debugger asks the task to stop at a Line.</summary>
        public bool Pause;

        /// <summary>Park even a foreground task on Pause (the Stepper sets it for a stop it
        /// makes with DebugOptions.ForegroundCanBreak; the runtime never parks foreground
        /// code).</summary>
        public bool ParkForeground;
        public ulong NowFt;
        public long Slices;
        public long Instructions;

        /// <summary>The host's per-task data (window, page ...).</summary>
        public readonly Dictionary<string, object?> Data = new Dictionary<string, object?>(
            StringComparer.Ordinal
        );

        /// <summary>A halting error was raised: the rest of the current instruction is skipped.</summary>
        public bool Unwinding;

        // --- the debugger seam ---------------------------------------------------------------
        /// <summary>The line hook runs because a call returned into this frame (not at a Line marker).</summary>
        public bool AtReturn;
        public StepMode Step;

        /// <summary>The frame depth when the step started.</summary>
        public int StepDepth;

        /// <summary>Set when the task is parked by the debugger (why: breakpoint, step, pause ...).</summary>
        public string StopReason = "";

        /// <summary>A non-halting hardware error was reported; the task stops at its next line.</summary>
        public int PendingErrorStop;

        /// <summary>A data breakpoint's id: a watched variable was written, the task stops at
        /// its next line.</summary>
        public int PendingDataStop;

        /// <summary>The recorder that holds this task's history, and the history (see
        /// <see cref="Recorder.Of"/>): kept on the task, a line's event finds it at once.</summary>
        internal Recorder? HistOwner;
        internal TaskHistory? Hist;

        /// <summary>The debugger ignores this task (a debugger evaluation: no breakpoint, step
        /// or error stop, no report).</summary>
        public bool NoDebug;

        /// <summary>The FUNC0 index of the built-in this task is calling now, -1 = none (a
        /// hardware error raised inside it names it as the error's ErrPage).</summary>
        public int CurrentBuiltin = -1;

        /// <summary>The arguments of that call (valid while CurrentBuiltin is set: an error's
        /// log line shows them).</summary>
        public ArgList CurrentArgs;

        private StackRef?[]? _refs;

        /// <summary>The slots a new task's stack starts with (it doubles as code pushes past it).</summary>
        public const int InitialStack = 48;

        public CicodeTask(CicodeVm vm, string name = "", bool foreground = false)
        {
            Vm = vm;
            Name = name;
            Foreground = foreground;
            var cfg = vm.Config;
            // the stack starts small and grows (EnsureCapacity; the overflow limit stays
            // cfg.Stack): a page scan makes a task per expression
            Stack = new Value[Math.Min(cfg.Stack + 16, InitialStack)];
            WriteLocal = cfg.WriteLocal;
            ScaleCheck = cfg.ScaleCheck;
            EchoError = cfg.EchoError;
            IgnoreCaseFlag = cfg.IgnoreCase;
        }

        /// <summary>The byte offset of the current instruction.</summary>
        public int Pc => PcOverride >= 0 ? PcOverride : Code?.PcOf(Ip) ?? 0;

        /// <summary>A halt that happened in another code than the one the task holds now (a
        /// RetValue overflow after the frame was popped) keeps its pc here.</summary>
        internal int PcOverride = -1;

        /// <summary>Call depth (saved frames).</summary>
        public int Depth => Frames.Count;

        // --- IOpContext --------------------------------------------------------------------
        public ulong Now => NowFt;
        public bool IgnoreCase => IgnoreCaseFlag;

        public void RaiseError(int code, bool halt) => Vm.RaiseError(this, code, halt);

        public void NoteExprError(int code)
        {
            if (Unwinding)
                return;
            if (ExprError == 0)
                ExprError = code;
        }

        /// <summary>Set the op error with the bottom-of-loop rule (after each instruction): a
        /// non-zero op error also sets ExprError and Error when they are clear.</summary>
        public void SetOpError(int code)
        {
            if (Unwinding)
                return;
            OpError = code;
            if (code != 0)
            {
                if (ExprError == 0)
                    ExprError = code;
                if (Error == 0)
                    Error = code;
            }
        }

        public bool Done => State == TaskState.Stop || State == TaskState.Halt || State == TaskState.Dead;

        public string StateName => TaskState.Name(State);

        public override string ToString() => $"<Task {Handle} '{Name}' {StateName}>";

        // --- stack helpers (outside the interpreter's inline paths) -------------------------
        internal void EnsureCapacity(int n)
        {
            if (n <= Stack.Length)
                return;
            int size = Math.Max(n, Stack.Length * 2);
            var a = new Value[size];
            Array.Copy(Stack, a, Sp);
            Stack = a;
        }

        public void Push(in Value v)
        {
            if (Sp >= Stack.Length)
                EnsureCapacity(Sp + 1);
            Stack[Sp++] = v;
        }

        /// <summary>The values on the stack (debugger, tests).</summary>
        public Value[] StackValues()
        {
            var r = new Value[Sp];
            Array.Copy(Stack, r, Sp);
            return r;
        }

        /// <summary>A reference to stack slot <paramref name="index"/> (AddrStack); one object per
        /// slot, reused.</summary>
        internal StackRef RefTo(int index)
        {
            var r = _refs;
            if (r == null || index >= r.Length)
            {
                var n = new StackRef?[Math.Max(index + 1, Stack.Length)];
                if (r != null)
                    Array.Copy(r, n, r.Length);
                _refs = r = n;
            }
            return r[index] ??= new StackRef(this, index);
        }
    }
}
