using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>Round-robin over ready tasks.</summary>
    public sealed class Scheduler
    {
        public readonly CicodeVm Vm;

        /// <summary>The live tasks by handle, in the order they were added.</summary>
        public readonly Dictionary<int, CicodeTask> Tasks = new Dictionary<int, CicodeTask>();
        private readonly List<CicodeTask> _order = new List<CicodeTask>();
        private readonly LinkedList<CicodeTask> _queue = new LinkedList<CicodeTask>();
        private int _next = 1;

        public Scheduler(CicodeVm vm)
        {
            Vm = vm;
            vm.OtherWork = () => _queue.Count > 0;
            vm.Scheduler = this;
        }

        /// <summary>The live tasks in the order they were added.</summary>
        public IReadOnlyList<CicodeTask> All => _order;

        public int QueueLength => _queue.Count;

        /// <summary>The task Step is running (its slice, or its wake-up in Wake) or ran last; null
        /// before Step has reached one. A host that caught an exception out of Step halts this
        /// task (VM.Current is already restored then).</summary>
        public CicodeTask? LastStepped { get; private set; }

        /// <summary>A live task was added (raised inside Add, after the task is queued).</summary>
        public event Action<CicodeTask>? TaskAdded;

        /// <summary>A task that ended (stopped, halted, died) was dropped from the table: raised
        /// once per added task, at the scheduler's next look at it (the slice it ended in, a
        /// Wake, or the next Add).</summary>
        public event Action<CicodeTask>? TaskEnded;

        private void Drop(CicodeTask t)
        {
            if (Tasks.TryGetValue(t.Handle, out var x) && x == t)
            {
                Tasks.Remove(t.Handle);
                _order.Remove(t);
                TaskEnded?.Invoke(t);
            }
        }

        /// <summary>Drop the ended tasks now (a host that halted a waiting task, before it
        /// lists the tasks).</summary>
        public void DropEnded() => Prune();

        /// <summary>Drop the tasks that have ended (also ones a host halted while they waited).</summary>
        private void Prune()
        {
            List<CicodeTask>? done = null;
            foreach (var t in _order)
                if (t.Done)
                    (done ??= new List<CicodeTask>()).Add(t);
            if (done != null)
                foreach (var t in done)
                    Drop(t);
        }

        /// <summary>The highest task handle (16-bit). Handles wrap around past it.</summary>
        public const int MaxHandle = 0x7FFF;

        private int NextHandle()
        {
            for (int n = 0; n < MaxHandle; n++)
            {
                int h = _next;
                _next = _next >= MaxHandle ? 1 : _next + 1;
                if (!Tasks.ContainsKey(h))
                    return h;
            }
            throw new HardwareError(Errors.Threads, false, Value.Long(-1), "Out of Cicode threads");
        }

        public CicodeTask Add(CicodeTask task)
        {
            Prune();
            if (Tasks.Count >= Vm.Config.Threads)
                throw new HardwareError(Errors.Threads, false, Value.Long(-1), "Out of Cicode threads");
            task.Handle = NextHandle();
            if (!task.Done)
            {
                Tasks[task.Handle] = task;
                _order.Add(task);
                task.State = TaskState.Ready;
                _queue.AddLast(task);
                TaskAdded?.Invoke(task);
            }
            return task;
        }

        public CicodeTask Spawn(
            Function fn,
            IReadOnlyList<Value>? args = null,
            bool foreground = false,
            string name = ""
        ) => Add(Vm.TaskForCall(fn, args ?? System.Array.Empty<Value>(), foreground, null, name));

        public CicodeTask Spawn(
            string fnName,
            IReadOnlyList<Value>? args = null,
            bool foreground = false,
            string name = ""
        ) => Add(Vm.TaskForCall(fnName, args ?? System.Array.Empty<Value>(), foreground, null, name));

        private List<CicodeTask>? _wakeList;

        /// <summary>Wake the tasks whose wait is over. Returns how many woke.</summary>
        public int Wake()
        {
            var list = _wakeList ?? new List<CicodeTask>();
            _wakeList = null; // a nested wake gets its own copy
            list.AddRange(_order);
            try
            {
                return WakeFrom(list);
            }
            finally
            {
                list.Clear();
                _wakeList = list;
            }
        }

        private int WakeFrom(List<CicodeTask> tasks)
        {
            int n = 0;
            var clock = Vm.Clock;
            List<CicodeTask>? ended = null;
            foreach (var t in tasks)
            {
                if (t.Done)
                    (ended ??= new List<CicodeTask>()).Add(t);
                else if (t.State == TaskState.Wait || t.State == TaskState.Sleep)
                {
                    var w = t.Wait;
                    if (w == null || w.Ready(clock))
                    {
                        t.Data.Remove("resume");
                        LastStepped = t; // a blocked built-in's result runs here
                        Vm.Resume(t);
                        _queue.AddLast(t);
                        n++;
                    }
                }
            }
            if (ended != null)
                foreach (var t in ended)
                    Drop(t);
            return n;
        }

        /// <summary>The profiler's hook (ctsim/profile): each slice's task and its Stopwatch
        /// ticks; null = not timed.</summary>
        public Action<CicodeTask, long>? SliceTimed;

        /// <summary>Run one slice of the next ready task; false when nothing is ready.</summary>
        public bool Step()
        {
            LastStepped = null;
            Wake();
            LastStepped = null;
            while (_queue.Count > 0)
            {
                var t = _queue.First!.Value;
                _queue.RemoveFirst();
                if (t.State != TaskState.Ready)
                    continue;
                LastStepped = t;
                var timed = SliceTimed;
                if (timed == null)
                    Vm.RunSlice(t);
                else
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        Vm.RunSlice(t);
                    }
                    finally
                    {
                        timed(t, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                    }
                }
                if (t.State == TaskState.Ready)
                    _queue.AddLast(t);
                else if (t.Done)
                    Drop(t);
                return true;
            }
            return false;
        }

        /// <summary>Run until no task is ready (waiting tasks stay). Returns the slices run.</summary>
        public long Run(long maxSteps = 10000000)
        {
            long n = 0;
            while (n < maxSteps && Step())
                n++;
            return n;
        }

        public List<CicodeTask> Waiting()
        {
            var r = new List<CicodeTask>();
            foreach (var t in _order)
                if (t.State == TaskState.Wait || t.State == TaskState.Sleep)
                    r.Add(t);
            return r;
        }

        public List<CicodeTask> Live()
        {
            var r = new List<CicodeTask>();
            foreach (var t in _order)
                if (!t.Done)
                    r.Add(t);
            return r;
        }
    }
}
