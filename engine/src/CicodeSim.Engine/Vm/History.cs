using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>What one history event is.</summary>
    public enum HistoryKind : byte
    {
        /// <summary>A call of Func started (its CallId is new).</summary>
        Enter,

        /// <summary>The call CallId returned.</summary>
        Leave,

        /// <summary>A Line marker ran (the call's line).</summary>
        Line,

        /// <summary>A local of the call was written (Slot); Entry = its value when the call
        /// started (a parameter, a local's initial value).</summary>
        Local,

        /// <summary>A module or global variable was written (Var, Index).</summary>
        CodeVar,
    }

    /// <summary>One event of a task's history (see <see cref="Recorder"/>).</summary>
    public struct HistoryEvent
    {
        public long Seq;
        public HistoryKind Kind;
        public long CallId;
        public Function? Func;
        public int Slot;
        public CodeVarSlot? Var;
        public long Index;
        public Value Old;
        public Value New;
        public bool Entry;
        public int Line;
        public long Ms;
    }

    /// <summary>A task's history: a ring of events (the oldest dropped first, Truncated then) and
    /// the call ids of its frames now (bottom first), parallel to the task's frames. The ring
    /// starts small and doubles up to its capacity (most tasks record a few events).</summary>
    public sealed class TaskHistory
    {
        private const int FirstSize = 64;
        private HistoryEvent[] _ring;
        private readonly int _capacity;
        private int _start,
            _count;
        public bool Truncated;

        /// <summary>The call ids of the task's calls now: [0] the task's code, the last the
        /// current function's.</summary>
        public readonly List<long> Calls = new List<long>();

        /// <summary>The current call has not run a Line marker yet: its entry values are taken at
        /// the first one (the parameters, the locals as declared).</summary>
        public bool EntryPending;

        /// <summary>The debugger's view of this task (DebugTarget's time travel): a Seq, -1 = live.</summary>
        public long Cursor = -1;

        public TaskHistory(int capacity)
        {
            _capacity = Math.Max(16, capacity);
            _ring = new HistoryEvent[Math.Min(_capacity, FirstSize)];
        }

        public int Count => _count;

        public ref HistoryEvent this[int i]
        {
            get
            {
                int k = _start + i;
                if (k >= _ring.Length)
                    k -= _ring.Length;
                return ref _ring[k];
            }
        }

        public long FirstSeq => _count > 0 ? this[0].Seq : 0;

        public long LastSeq => _count > 0 ? this[_count - 1].Seq : 0;

        public void Add(in HistoryEvent e) => Push() = e;

        /// <summary>The slot of a new last event (the caller fills every field): the ring grows,
        /// or drops its oldest event once it is full.</summary>
        public ref HistoryEvent Push()
        {
            if (_count == _ring.Length)
            {
                if (_ring.Length < _capacity)
                    Grow();
                else
                {
                    int s = _start;
                    _start = s + 1 == _ring.Length ? 0 : s + 1;
                    Truncated = true;
                    return ref _ring[s];
                }
            }
            int k = _start + _count++;
            if (k >= _ring.Length)
                k -= _ring.Length;
            return ref _ring[k];
        }

        private void Grow()
        {
            var bigger = new HistoryEvent[Math.Min(_capacity, _ring.Length * 2)];
            for (int i = 0; i < _count; i++)
                bigger[i] = this[i];
            _ring = bigger;
            _start = 0;
        }

        /// <summary>The position of the last event with Seq at or before <paramref name="seq"/>,
        /// -1 when none.</summary>
        public int IndexAtOrBefore(long seq)
        {
            int lo = 0,
                hi = _count - 1,
                best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (this[mid].Seq <= seq)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                    hi = mid - 1;
            }
            return best;
        }
    }

    /// <summary>A variable was written: the task, the event (read only: it is the history's own slot).</summary>
    public delegate void WrittenHandler(CicodeTask task, in HistoryEvent e);

    /// <summary>Records what tasks do while a debugger is attached.</summary>
    public sealed class Recorder
    {
        public const int DefaultCapacity = 20000;
        public int Capacity = DefaultCapacity;
        private long _seq,
            _call;
        private readonly Func<long> _nowMs;

        /// <summary>A variable was written (data breakpoints; null while there are none).</summary>
        public WrittenHandler? Written;

        public Recorder(Func<long> nowMs)
        {
            _nowMs = nowMs;
        }

        /// <summary>A task's history, null when it has none.</summary>
        public TaskHistory? Of(CicodeTask t) => ReferenceEquals(t.HistOwner, this) ? t.Hist : null;

        private TaskHistory For(CicodeTask t)
        {
            if (ReferenceEquals(t.HistOwner, this))
                return t.Hist!;
            var h = new TaskHistory(Capacity);
            // the calls the task is in now (it ran before the recording started)
            for (int k = 0; k <= t.Frames.Count; k++)
                h.Calls.Add(++_call);
            h.EntryPending = true;
            t.HistOwner = this;
            t.Hist = h;
            return h;
        }

        /// <summary>A new event in the history, filled in place: the fields every kind has, the
        /// others cleared (the slot may hold an old event). Old and New are read only on a
        /// variable's event, which sets both: here they only let go of what they held.</summary>
        private ref HistoryEvent Next(TaskHistory h, HistoryKind kind, Function? fn, int line)
        {
            ref var e = ref h.Push();
            e.Seq = ++_seq;
            e.Kind = kind;
            e.CallId = h.Calls.Count > 0 ? h.Calls[h.Calls.Count - 1] : 0;
            e.Func = fn;
            e.Slot = -1;
            e.Var = null;
            e.Index = -1;
            e.Old.O = null;
            e.New.O = null;
            e.Entry = false;
            e.Line = line;
            e.Ms = _nowMs();
            return ref e;
        }

        public void Line(CicodeTask t, int line)
        {
            var h = For(t);
            if (h.EntryPending)
            {
                h.EntryPending = false;
                if (t.Func != null)
                    foreach (var (slot, _) in t.Func.Locals)
                    {
                        int i = t.Base + slot;
                        if (i < 0 || i >= t.Sp)
                            continue;
                        ref var ev = ref Next(h, HistoryKind.Local, t.Func, line);
                        ev.Slot = slot;
                        ev.Old = default;
                        ev.New = t.Stack[i];
                        ev.Entry = true;
                    }
            }
            Next(h, HistoryKind.Line, t.Func, line);
        }

        public void Enter(CicodeTask t)
        {
            // (a task new to the history: For counts its frames, the new call's among them)
            bool known = ReferenceEquals(t.HistOwner, this);
            var h = For(t);
            if (known)
                h.Calls.Add(++_call);
            h.EntryPending = true;
            Next(h, HistoryKind.Enter, t.Func, t.Line);
        }

        public void Leave(CicodeTask t, Function? fn)
        {
            // (a task new to the history: the returning call was not among its frames any more)
            bool known = ReferenceEquals(t.HistOwner, this);
            var h = For(t);
            if (!known)
                h.Calls.Add(++_call);
            Next(h, HistoryKind.Leave, fn, t.Line);
            if (h.Calls.Count > 1)
                h.Calls.RemoveAt(h.Calls.Count - 1);
            h.EntryPending = false;
        }

        public void Local(CicodeTask t, int slot, in Value old, in Value now)
        {
            ref var e = ref Next(For(t), HistoryKind.Local, t.Func, t.Line);
            e.Slot = slot;
            e.Old = old;
            e.New = now;
            Written?.Invoke(t, in e);
        }

        public void CodeVar(CicodeTask t, CodeVarSlot var, long index, in Value old, in Value now)
        {
            ref var e = ref Next(For(t), HistoryKind.CodeVar, t.Func, t.Line);
            e.Var = var;
            e.Index = index;
            e.Old = old;
            e.New = now;
            Written?.Invoke(t, in e);
        }

        /// <summary>A local's name in its function ('' when it has none).</summary>
        public static string LocalName(Function? fn, int slot)
        {
            if (fn == null)
                return "";
            foreach (var (s, n) in fn.Locals)
                if (s == slot)
                    return n;
            return "";
        }
    }
}
