using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>Load (0x02) / Store (0x03) of a TAGREF. A read returns the subscription cache and
    /// never blocks.</summary>
    public interface ITagAccess
    {
        /// <summary>The tag's value with its quality and timestamps. A tag that does not exist
        /// returns a value with bad quality and sets task.TagError (424 Tag not found).</summary>
        Value Read(TagRef r, CicodeTask task);

        /// <summary>Write the value as the code produced it; the host converts it to the tag's
        /// type. Returns 0 or the hardware error code.</summary>
        int Write(TagRef r, Value value, CicodeTask task);
    }

    /// <summary>Optional: array tag elements (Index prefix). index -1 = out of bounds
    /// (the runtime passes -1 after a bounds error).</summary>
    public interface ITagElementAccess
    {
        Value ReadElement(TagRef r, long index, CicodeTask task);
        int WriteElement(TagRef r, long index, Value value, CicodeTask task);
    }

    /// <summary>Optional: the tag's engineering range for a Bar with no SCALE record (operand
    /// -1); false = leave the value as it is.</summary>
    public interface ITagScale
    {
        bool TryScale(TagRef? r, CicodeTask task, out double engZero, out double engFull);
    }

    /// <summary>Func (0x05): a call of built-in number index (FUNC0 OP_CODE) with arguments already converted to the signature's types (by-reference arguments are
    /// VARIANT references the callee assigns through the VM's AssignRef).</summary>
    public interface IBuiltins
    {
        /// <summary>Call a built-in. Returns a value, Value.Nothing or Value.Block.</summary>
        Value Call(int index, CicodeTask task, ArgList args);

        string Name(int index);
    }

    /// <summary>Simulated time. NowFiletime = 100 ns units since 1601 (what TIMESTAMP holds),
    /// MonotonicMs drives time slices and SleepMS.</summary>
    public interface IClock
    {
        ulong NowFiletime();
        double MonotonicMs();
    }

    /// <summary>Optional: both clock values in one call, exactly NowFiletime() then
    /// MonotonicMs() (the interpreter's slice check reads them together after every built-in
    /// call).</summary>
    public interface IClockReader
    {
        void Read(out ulong filetime, out double monotonicMs);
    }

    /// <summary>AssFmt (0x42) and the '?name?' super genie associations of the window the code
    /// runs in.</summary>
    public interface IAssociations
    {
        /// <summary>false = not associated (error 323).</summary>
        bool Resolve(string name, CicodeTask task, out Value value);
    }

    /// <summary>Optional: the associated tag's display format for AssFmt: (format word,
    /// engineering-units text) as the runtime's format conversion takes them; false when not
    /// associated.</summary>
    public interface IAssociationFormat
    {
        bool Format(string name, CicodeTask task, out long word, out string eu);
    }

    /// <summary>Called at every Line (0x34) marker and after a return into a caller (with the
    /// caller's line; task.AtReturn is set then): the debugger's breakpoints and stepping. Set
    /// task.Pause to stop a background task there.</summary>
    public delegate void LineHook(CicodeTask task, int line);

    /// <summary>Called on every raised hardware error; true = handled (the task does not halt).</summary>
    public delegate bool ErrorHook(CicodeTask task, int code);

    /// <summary>Called after a raised hardware error once the VM decided: halting = the task
    /// halts now (state Halt), suppressed = the task's ErrSet level is above 0 (the runtime
    /// logs 'SUPPRESSED hardware alarm' and reports nothing to a debugger).</summary>
    public delegate void ErrorReportHook(CicodeTask task, int code, bool halting, bool suppressed);

    /// <summary>The arguments of a built-in call: a view of the values the caller pushed (they
    /// stay valid until the built-in pushes onto the same task's stack).</summary>
    public readonly struct ArgList
    {
        private readonly Value[] _a;
        private readonly int _off;
        public readonly int Count;

        public ArgList(Value[] a, int off, int count)
        {
            _a = a;
            _off = off;
            Count = count;
        }

        public static ArgList Of(params Value[] values) => new ArgList(values, 0, values.Length);

        public ref readonly Value this[int k] => ref _a[_off + k];

        /// <summary>The argument, or Value.Nothing past the end.</summary>
        public ref readonly Value Get(int k)
        {
            if (k < Count)
                return ref _a[_off + k];
            return ref Value.Nothing;
        }

        public Value[] ToArray()
        {
            var r = new Value[Count];
            System.Array.Copy(_a, _off, r, 0, Count);
            return r;
        }
    }
}
