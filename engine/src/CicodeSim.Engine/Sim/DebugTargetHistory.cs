using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Sim
{
    /// <summary>Variable history and time travel.</summary>
    public sealed partial class DebugTarget
    {
        private TaskHistory? Hist(CicodeTask t) => _sim.Vm.Recorder?.Of(t);

        private static string NameOf(in HistoryEvent e) =>
            e.Kind == HistoryKind.Local ? Recorder.LocalName(e.Func, e.Slot) : e.Var?.Cv.Name ?? "";

        private VariableChange Change(CicodeTask t, in HistoryEvent e) =>
            new VariableChange
            {
                Seq = e.Seq,
                Task = t.Handle,
                CallId = e.CallId,
                Function = e.Func?.Name ?? "",
                Scope =
                    e.Kind == HistoryKind.Local ? "local"
                    : e.Var != null && e.Var.Cv.UnitType == -1 ? "global"
                    : "module",
                Name = NameOf(e),
                Index = e.Index,
                Old = e.Entry ? "" : ValueFormat.Display(e.Old),
                New = ValueFormat.Display(e.New),
                Type = ValueFormat.TypeName(e.New.T),
                Entry = e.Entry,
                Line = e.Line,
                Time = e.Ms / 1000.0,
            };

        public IReadOnlyList<VariableChange> History(
            int task,
            string? name = null,
            long callId = -1,
            int max = 500
        ) =>
            _sim.InvokeDebug(() =>
            {
                var t = FindTask(task);
                var h = Hist(t);
                var r = new List<VariableChange>();
                if (h == null)
                    return (IReadOnlyList<VariableChange>)r;
                for (int i = h.Count - 1; i >= 0 && r.Count < Math.Max(1, max); i--)
                {
                    ref var e = ref h[i];
                    if (e.Kind != HistoryKind.Local && e.Kind != HistoryKind.CodeVar)
                        continue;
                    if (callId >= 0 && e.CallId != callId)
                        continue;
                    if (name != null && !string.Equals(name, NameOf(e), StringComparison.OrdinalIgnoreCase))
                        continue;
                    r.Add(Change(t, e));
                }
                r.Reverse();
                return (IReadOnlyList<VariableChange>)r;
            });

        public HistoryInfo HistoryState(int task) =>
            _sim.InvokeDebug(() =>
            {
                var t = FindTask(task);
                var h = Hist(t);
                if (h == null)
                    return new HistoryInfo();
                return new HistoryInfo
                {
                    First = h.FirstSeq,
                    Last = h.LastSeq,
                    Cursor = h.Cursor,
                    Events = h.Count,
                    Truncated = h.Truncated,
                };
            });

        // ------------------------------------------------------------------ moving the view
        private TaskHistory StoppedHist(CicodeTask t)
        {
            if (!t.Pause)
                throw new ArgumentException(
                    "task " + t.Handle.ToString(CultureInfo.InvariantCulture) + " is not stopped"
                );
            return Hist(t) ?? throw new ArgumentException("no history was recorded for this task");
        }

        /// <summary>The index of the view's place: the cursor's event, or live the last Line
        /// event (where the task stopped).</summary>
        private static int Here(TaskHistory h) => h.Cursor >= 0 ? h.IndexAtOrBefore(h.Cursor) : LastLine(h);

        private HistoryMove Move(CicodeTask t, TaskHistory h, bool atStart)
        {
            Bump(t); // (the frame and variable handles of the old view go)
            var m = new HistoryMove
            {
                Cursor = h.Cursor,
                AtStart = atStart,
                Live = h.Cursor < 0,
            };
            var frames = ViewFrames(t, h);
            if (frames.Count > 0)
            {
                m.Line = frames[0].Info.Line;
                m.Function = frames[0].Info.Name;
                m.File = frames[0].Info.File;
            }
            return m;
        }

        public HistoryMove StepBack(int task) =>
            _sim.InvokeDebug(() =>
            {
                var t = FindTask(task);
                var h = StoppedHist(t);
                int i = Here(h);
                for (int k = i - 1; k >= 0; k--)
                    if (h[k].Kind == HistoryKind.Line)
                    {
                        h.Cursor = h[k].Seq;
                        bool first = true;
                        for (int j = k - 1; j >= 0 && first; j--)
                            if (h[j].Kind == HistoryKind.Line)
                                first = false;
                        return Move(t, h, first);
                    }
                // nothing earlier: at the start of the recording
                if (i >= 0)
                    h.Cursor = h[i].Seq;
                return Move(t, h, true);
            });

        public HistoryMove StepForward(int task) =>
            _sim.InvokeDebug(() =>
            {
                var t = FindTask(task);
                var h = StoppedHist(t);
                if (h.Cursor < 0)
                    return Move(t, h, false);
                int i = Here(h),
                    last = LastLine(h);
                for (int k = i + 1; k < h.Count; k++)
                    if (h[k].Kind == HistoryKind.Line)
                    {
                        // the stop's own line is now: live again
                        h.Cursor = k >= last ? -1 : h[k].Seq;
                        return Move(t, h, false);
                    }
                h.Cursor = -1;
                return Move(t, h, false);
            });

        /// <summary>The index of the last Line event (where the task stopped).</summary>
        private static int LastLine(TaskHistory h)
        {
            for (int i = h.Count - 1; i >= 0; i--)
                if (h[i].Kind == HistoryKind.Line)
                    return i;
            return h.Count - 1;
        }

        public HistoryMove ReverseContinue(int task) =>
            _sim.InvokeDebug(() =>
            {
                var t = FindTask(task);
                var h = StoppedHist(t);
                int i = Here(h);
                for (int k = i - 1; k >= 0; k--)
                {
                    ref var e = ref h[k];
                    bool hit =
                        e.Kind == HistoryKind.Line && e.Func != null && S.HasBreakpointAt(e.Func, e.Line)
                        || (e.Kind == HistoryKind.Local || e.Kind == HistoryKind.CodeVar)
                            && S.MatchData(e) != 0;
                    if (hit)
                    {
                        h.Cursor = e.Seq;
                        return Move(t, h, false);
                    }
                }
                // the start: the first line marker (the call's entry values sit just before it)
                int first = FirstLine(h);
                if (first >= 0)
                    h.Cursor = h[first].Seq;
                return Move(t, h, true);
            });

        /// <summary>The index of the first Line event, -1 when none.</summary>
        private static int FirstLine(TaskHistory h)
        {
            for (int i = 0; i < h.Count; i++)
                if (h[i].Kind == HistoryKind.Line)
                    return i;
            return -1;
        }

        public void Live(int task) =>
            _sim.InvokeDebug(() =>
            {
                var t = FindTask(task);
                var h = Hist(t);
                if (h != null && h.Cursor >= 0)
                {
                    h.Cursor = -1;
                    Bump(t);
                }
            });

        /// <summary>A task's view goes live (a Continue or a Step of it).</summary>
        private void GoLive(CicodeTask t)
        {
            var h = Hist(t);
            if (h != null)
                h.Cursor = -1;
        }

        public IReadOnlyList<BreakpointResult> SetDataBreakpoints(
            IReadOnlyList<DataBreakpointRequest> list
        ) =>
            _sim.InvokeDebug(() =>
            {
                S.Attach();
                return (IReadOnlyList<BreakpointResult>)S.SetDataBreakpoints(list);
            });

        // ------------------------------------------------------------------ the rebuilt view
        private sealed class ViewFrame
        {
            public FrameInfo Info = null!;
            public long CallId;

            /// <summary>The frame is one of the task's now (its stack holds the values).</summary>
            public bool LiveFrame;
        }

        /// <summary>The task's frames (top first) with their call ids: live, or as of the
        /// cursor (the calls that returned since come back, the ones started since go).</summary>
        private List<ViewFrame> ViewFrames(CicodeTask t, TaskHistory? h)
        {
            var live = DebugInspect.Frames(t);
            var r = new List<ViewFrame>();
            // the live calls, bottom first, parallel to h.Calls (the frames of the stub code skipped)
            var liveCalls = new List<(long Id, FrameInfo? Info)>();
            if (h != null)
            {
                var byDepth = new List<FrameInfo>(live);
                byDepth.Reverse(); // bottom first
                int off = h.Calls.Count - byDepth.Count;
                for (int j = 0; j < h.Calls.Count; j++)
                    liveCalls.Add(
                        (h.Calls[j], j - off >= 0 && j - off < byDepth.Count ? byDepth[j - off] : null)
                    );
            }
            if (h == null || h.Cursor < 0)
            {
                for (int d = 0; d < live.Count; d++)
                {
                    long id = h != null && h.Calls.Count - 1 - d >= 0 ? h.Calls[h.Calls.Count - 1 - d] : 0;
                    r.Add(
                        new ViewFrame
                        {
                            Info = live[d],
                            CallId = id,
                            LiveFrame = true,
                        }
                    );
                }
                return r;
            }
            long c = h.Cursor;
            var stack = new List<(long Id, Function? Func)>();
            foreach (var (id, info) in liveCalls)
                stack.Add((id, info?.Func));
            for (int i = h.Count - 1; i >= 0; i--)
            {
                ref var e = ref h[i];
                if (e.Seq <= c)
                    break;
                if (e.Kind == HistoryKind.Leave)
                    stack.Add((e.CallId, e.Func));
                else if (
                    e.Kind == HistoryKind.Enter
                    && stack.Count > 0
                    && stack[stack.Count - 1].Id == e.CallId
                )
                    stack.RemoveAt(stack.Count - 1);
            }
            int ci = h.IndexAtOrBefore(c);
            for (int k = stack.Count - 1; k >= 0; k--)
            {
                var (id, fn) = stack[k];
                int line = 0;
                for (int i = ci; i >= 0; i--)
                    if (
                        h[i].CallId == id
                        && (
                            h[i].Kind == HistoryKind.Line
                            || h[i].Kind == HistoryKind.Local
                            || h[i].Kind == HistoryKind.CodeVar
                        )
                    )
                    {
                        line = h[i].Line;
                        break;
                    }
                if (line == 0)
                    // before the call's first line marker (its entry): that line
                    for (int i = ci + 1; i < h.Count; i++)
                        if (h[i].CallId == id && h[i].Kind == HistoryKind.Line)
                        {
                            line = h[i].Line;
                            break;
                        }
                FrameInfo? liveInfo = null;
                foreach (var (lid, info) in liveCalls)
                    if (lid == id)
                        liveInfo = info;
                var fi = new FrameInfo
                {
                    Func = fn ?? liveInfo?.Func,
                    Name = (fn ?? liveInfo?.Func)?.Name ?? liveInfo?.Name ?? "",
                    File = (fn ?? liveInfo?.Func)?.Filename ?? liveInfo?.File ?? "",
                    Line = line != 0 ? line : liveInfo?.Line ?? 0,
                    Base = liveInfo?.Base ?? -1,
                    Pc = 0,
                    Depth = stack.Count - 1 - k,
                };
                r.Add(
                    new ViewFrame
                    {
                        Info = fi,
                        CallId = id,
                        LiveFrame = liveInfo != null,
                    }
                );
            }
            return r;
        }

        /// <summary>A call's locals as of cursor <paramref name="c"/> (name, slot, value).</summary>
        private List<(string Name, int Slot, Value Value)> ViewLocals(
            CicodeTask t,
            TaskHistory h,
            long c,
            long callId,
            FrameInfo frame,
            bool liveFrame
        )
        {
            var vals = new Dictionary<int, Value>();
            var present = new HashSet<int>();
            if (liveFrame)
            {
                foreach (var (_, slot, v) in DebugInspect.Locals(t, frame))
                {
                    vals[slot] = v;
                    present.Add(slot);
                }
                for (int i = h.Count - 1; i >= 0; i--)
                {
                    ref var e = ref h[i];
                    if (e.Seq <= c)
                        break;
                    if (e.Kind != HistoryKind.Local || e.CallId != callId)
                        continue;
                    if (e.Entry)
                        present.Remove(e.Slot);
                    else
                        vals[e.Slot] = e.Old;
                }
            }
            else
            {
                for (int i = 0; i < h.Count; i++)
                {
                    ref var e = ref h[i];
                    if (e.Seq > c)
                        break;
                    if (e.Kind != HistoryKind.Local || e.CallId != callId)
                        continue;
                    vals[e.Slot] = e.New;
                    present.Add(e.Slot);
                }
            }
            var r = new List<(string Name, int Slot, Value Value)>();
            if (frame.Func == null)
                return r;
            foreach (var (slot, name) in frame.Func.Locals)
                if (present.Contains(slot) && vals.TryGetValue(slot, out var v))
                    r.Add((name, slot, v));
            r.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return r;
        }

        /// <summary>A module / global variable's element as of cursor <paramref name="c"/> (this
        /// task's later writes undone; other tasks' writes are not in its history).</summary>
        private static Value ViewCodeVar(TaskHistory h, long c, CodeVarSlot slot, long index)
        {
            Value v = slot.Get(index, out _);
            for (int i = h.Count - 1; i >= 0; i--)
            {
                ref var e = ref h[i];
                if (e.Seq <= c)
                    break;
                if (e.Kind == HistoryKind.CodeVar && ReferenceEquals(e.Var, slot) && e.Index == index)
                    v = e.Old;
            }
            return v;
        }

        /// <summary>Evaluate in a view of the past: a variable's name only (a local of the
        /// frame, a module or global variable), as of then; anything else is refused.</summary>
        private EvaluateResult EvaluateInView(FrameH f, string expr)
        {
            var h = Hist(f.Task) ?? throw NotLive();
            foreach (var (name, _, v) in ViewLocals(f.Task, h, f.View, f.CallId, f.Frame, f.LiveFrame))
                if (string.Equals(name, expr, StringComparison.OrdinalIgnoreCase))
                    return new EvaluateResult
                    {
                        Result = ValueFormat.Display(v),
                        Type = ValueFormat.TypeName(v.T),
                    };
            var lib = LibraryVars.For(_sim.Library);
            if (lib.ByName.TryGetValue(expr, out var cvs) && cvs.Count > 0 && lib.DimsOf(cvs[0]).Length == 0)
            {
                var slot = _sim.Vm.Slot(_sim.Library, cvs[0]);
                Value v = ViewCodeVar(h, f.View, slot, 0);
                return new EvaluateResult
                {
                    Result = ValueFormat.Display(v),
                    Type = ValueFormat.TypeName(v.T),
                };
            }
            throw NotLive();
        }

        /// <summary>A refusal while a task's view is not live.</summary>
        private static ArgumentException NotLive() =>
            new ArgumentException(
                "This shows the task as it was earlier: go back to now (Step Forward to the end, Continue or Step) first"
            );
    }
}
