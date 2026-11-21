using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Host.Dap
{
    /// <summary>Variable history, time travel (stepBack, reverseContinue, stepping forward in a
    /// past view) and data breakpoints (the engine's recording, revision 17). A variable with
    /// recorded changes gets children, one per change ('3: line 18' = '0 -> 12.5 (3.20 s)');
    /// cicode/history gives a frame's changes for the client's inline values.</summary>
    internal sealed partial class CicodeDapSession
    {
        /// <summary>References of the history children: above the engine's, below ReturnValueRef.</summary>
        private const int HistoryRefBase = 0x40000000;
        /// <summary>Changes read at once (the engine's ring per task).</summary>
        private const int HistoryMax = 20000;

        /// <summary>What a variables reference of the engine lists: a frame's locals, the module or
        /// global variables, or an array's elements.</summary>
        private sealed class VarContext
        {
            public int Task;
            public long CallId;
            public string Function = "";
            public bool Local;
            /// <summary>The array whose elements the reference lists, null = a scope.</summary>
            public string? Array;
        }

        private sealed class HistoryRef
        {
            public int Task;
            public long CallId;
            public bool Local;
            public string Name = "";
            public long Index = -1;
        }

        // the current stop's views (under _lock); a stop, a continue or a move drops them
        private readonly Dictionary<long, DebugFrame> _frameInfo = new Dictionary<long, DebugFrame>();
        private readonly Dictionary<int, VarContext> _varContext = new Dictionary<int, VarContext>();
        private readonly Dictionary<int, HistoryRef> _historyRefs = new Dictionary<int, HistoryRef>();
        private int _nextHistoryRef = HistoryRefBase;

        private void DropViews()
        {
            lock (_lock)
            {
                _frameInfo.Clear();
                _varContext.Clear();
                _historyRefs.Clear();
                _nextHistoryRef = HistoryRefBase;
            }
        }

        private void RememberFrame(DebugFrame f)
        {
            lock (_lock)
                _frameInfo[f.Id] = f;
        }

        private DebugFrame? FrameInfo(long id)
        {
            lock (_lock)
                return _frameInfo.TryGetValue(id, out var f) ? f : null;
        }

        /// <summary>A scope of a frame: Locals are the call's, Module and Globals the task's.</summary>
        private void RememberScope(long frameId, DebugScope s)
        {
            var f = FrameInfo(frameId);
            if (f == null || s.VariablesReference == 0)
                return;
            bool local = s.Name == "Locals";
            if (!local && s.Name != "Module" && s.Name != "Globals")
                return;
            lock (_lock)
                _varContext[s.VariablesReference] = new VarContext
                {
                    Task = f.Task, CallId = local ? f.CallId : -1, Function = f.Function, Local = local,
                };
        }

        private VarContext? ContextOf(int vref)
        {
            lock (_lock)
                return _varContext.TryGetValue(vref, out var c) ? c : null;
        }

        private HistoryRef? HistoryRefOf(int vref)
        {
            lock (_lock)
                return _historyRefs.TryGetValue(vref, out var h) ? h : null;
        }

        /// <summary>The task's recorded changes as its view sees them (up to the cursor in a past
        /// view), of call <paramref name="callId"/> (-1 all), locals or the others.</summary>
        private List<VariableChange> Changes(int task, long callId, bool? local, string? name = null)
        {
            var list = new List<VariableChange>();
            IReadOnlyList<VariableChange> all;
            long cursor;
            try
            {
                all = _dbg.History(task, name, callId, HistoryMax);
                cursor = _dbg.HistoryState(task).Cursor;
            }
            catch (ArgumentException)
            {
                return list;
            }
            foreach (var c in all)
            {
                if (cursor >= 0 && c.Seq > cursor)
                    continue;
                if (local.HasValue && (c.Scope == "local") != local.Value)
                    continue;
                list.Add(c);
            }
            return list;
        }

        private static string Key(string name, long index) =>
            name.ToUpperInvariant() + (index >= 0 ? "[" + index.ToString(CultureInfo.InvariantCulture) + "]" : "");

        /// <summary>The variables of an engine reference with their history: a scalar with changes
        /// gets children (its history), an array's elements likewise.</summary>
        private void AddHistory(int vref, List<(DebugVariable v, JsonValue o)> vars)
        {
            var ctx = ContextOf(vref);
            if (ctx == null)
                return;
            var changes = Changes(ctx.Task, ctx.CallId, ctx.Local, ctx.Array);
            var counts = new Dictionary<string, int>();
            foreach (var c in changes)
            {
                string k = Key(c.Name, ctx.Array != null ? c.Index : -1);
                if (ctx.Array == null && c.Index >= 0)
                    continue;
                counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            foreach (var (v, o) in vars)
            {
                if (v.VariablesReference != 0)
                {
                    // an array: its elements get their history
                    if (ctx.Array == null)
                        lock (_lock)
                            _varContext[v.VariablesReference] = new VarContext
                            {
                                Task = ctx.Task, CallId = ctx.CallId, Function = ctx.Function, Local = ctx.Local, Array = v.Name,
                            };
                    continue;
                }
                string name;
                long index = -1;
                if (ctx.Array != null)
                {
                    if (!TryIndex(v.Name, out index))
                        continue;
                    name = ctx.Array;
                }
                else
                    name = v.Name;
                if (!counts.TryGetValue(Key(name, index), out int count))
                    continue;
                int id;
                lock (_lock)
                {
                    id = _nextHistoryRef++;
                    _historyRefs[id] = new HistoryRef { Task = ctx.Task, CallId = ctx.CallId, Local = ctx.Local, Name = name, Index = index };
                }
                o.Set("variablesReference", JsonValue.Of(id));
                o.Set("namedVariables", JsonValue.Of(count));
            }
        }

        /// <summary>'[3]' = element 3.</summary>
        private static bool TryIndex(string name, out long index)
        {
            index = -1;
            return name.Length > 2 && name[0] == '[' && name[name.Length - 1] == ']' &&
                   long.TryParse(name.Substring(1, name.Length - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
        }

        /// <summary>A variable's history as children, oldest first.</summary>
        private JsonValue HistoryChildren(HistoryRef h)
        {
            var arr = JsonValue.NewArray();
            int k = 0;
            foreach (var c in Changes(h.Task, h.CallId, h.Local, h.Name))
            {
                if (c.Index != h.Index && h.Index >= 0)
                    continue;
                if (h.Index < 0 && c.Index >= 0)
                    continue;
                k++;
                var o = JsonValue.NewObject();
                o.Set("name", Num(k) + ": line " + Num(c.Line));
                o.Set("value", (c.Entry ? c.New + " (at entry)" : c.Old + " -> " + c.New) + "   " + Seconds(c.Time));
                if (c.Type.Length > 0)
                    o.Set("type", c.Type);
                o.Set("variablesReference", JsonValue.Of(0));
                var hint = JsonValue.NewObject();
                var attrs = JsonValue.NewArray();
                attrs.Add(JsonValue.Of("readOnly"));
                hint.Set("attributes", attrs);
                o.Set("presentationHint", hint);
                arr.Add(o);
            }
            return arr;
        }

        private static string Seconds(double t) => t.ToString("0.00", CultureInfo.InvariantCulture) + " s";

        // ------------------------------------------------------------------ time travel
        private enum Travel { Back, Forward, ReverseContinue }

        private void Move(JsonValue req, JsonValue a, Travel how)
        {
            int task = Int(a, "threadId", 0);
            HistoryMove m = how == Travel.Back ? _dbg.StepBack(task)
                : how == Travel.Forward ? _dbg.StepForward(task)
                : _dbg.ReverseContinue(task);
            Respond(req, true);
            Moved(task, m);
        }

        /// <summary>The view moved: the client asks for the frames again (a stop).</summary>
        private void Moved(int task, HistoryMove? m)
        {
            DropViews();
            if (m != null && m.AtStart)
                Output("console", "Thread " + Num(task) + ": the start of its recorded history" +
                                  (_dbg.HistoryState(task).Truncated ? " (older changes were dropped)" : "") + "\n");
            var b = JsonValue.NewObject();
            b.Set("reason", "step");
            b.Set("threadId", JsonValue.Of(task));
            b.Set("allThreadsStopped", JsonValue.False);
            if (m != null && !m.Live)
                b.Set("description", "Paused (history)");
            Event("stopped", b);
        }

        /// <summary>A step of a task whose view is in the past: next / stepIn go one line forward
        /// in the recording, stepOut back to now. False when the view is live.</summary>
        private bool StepInPast(JsonValue req, int task, StepKind kind)
        {
            long cursor;
            try
            {
                cursor = _dbg.HistoryState(task).Cursor;
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (cursor < 0)
                return false;
            HistoryMove? m = null;
            if (kind == StepKind.Out)
                _dbg.Live(task);
            else
                m = _dbg.StepForward(task);
            Respond(req, true);
            Moved(task, m);
            return true;
        }

        // ------------------------------------------------------------------ data breakpoints
        private void DataBreakpointInfo(JsonValue req, JsonValue a)
        {
            string name = Str(a, "name");
            var b = JsonValue.NewObject();
            string? fn = null, var = null;
            long index = -1;
            var vrefV = a.Get("variablesReference");
            if (vrefV != null && vrefV.IsNumber && (int)vrefV.Long != 0)
            {
                var ctx = ContextOf((int)vrefV.Long);
                if (ctx != null)
                {
                    fn = ctx.Local ? ctx.Function : null;
                    if (ctx.Array != null)
                    {
                        if (TryIndex(name, out index))
                            var = ctx.Array;
                    }
                    else
                        var = name;
                }
            }
            else if (name.Length > 0 && name.IndexOfAny(new[] { ' ', '(', '+', '-', '*', '/', '=' }) < 0)
            {
                // a name typed in (a watch): a module or global variable
                var = name;
            }
            if (var == null)
            {
                b.Set("dataId", JsonValue.Null);
                b.Set("description", "only Cicode variables (locals, module and global variables, array elements) can be watched");
                Respond(req, true, b);
                return;
            }
            var id = JsonValue.NewObject();
            id.Set("n", var);
            if (fn != null)
                id.Set("f", fn);
            if (index >= 0)
                id.Set("i", JsonValue.Of(index));
            b.Set("dataId", id.Dump());
            b.Set("description", (fn != null ? fn + "." : "") + var + (index >= 0 ? "[" + index.ToString(CultureInfo.InvariantCulture) + "]" : ""));
            var types = JsonValue.NewArray();
            types.Add(JsonValue.Of("write"));
            b.Set("accessTypes", types);
            b.Set("canPersist", JsonValue.False);
            Respond(req, true, b);
        }

        private void SetDataBreakpoints(JsonValue req, JsonValue a)
        {
            var list = new List<DataBreakpointRequest>();
            if (a.Get("breakpoints") is JsonValue bps && bps.Kind == JsonKind.Array)
                foreach (var bp in bps.Items)
                {
                    var r = new DataBreakpointRequest();
                    try
                    {
                        var id = JsonValue.Parse(Str(bp, "dataId"));
                        r.Name = Str(id, "n");
                        r.Function = id.Get("f") is JsonValue f && f.Kind == JsonKind.String ? f.Str : null;
                        r.Index = id.Get("i") is JsonValue i && i.IsNumber ? i.Long : -1;
                    }
                    catch (Exception e) when (e is FormatException || e is ArgumentException || e is InvalidOperationException)
                    {
                        // not one of ours: Name stays empty, the engine answers unverified
                    }
                    r.Condition = NonEmpty(Str(bp, "condition"));
                    r.HitCondition = NonEmpty(Str(bp, "hitCondition"));
                    list.Add(r);
                }
            var results = _dbg.SetDataBreakpoints(list);
            var arr = JsonValue.NewArray();
            foreach (var res in results)
            {
                var o = JsonValue.NewObject();
                o.Set("id", JsonValue.Of(res.Id));
                o.Set("verified", JsonValue.Of(res.Verified));
                if (res.Message != null)
                    o.Set("message", res.Message);
                arr.Add(o);
            }
            var b = JsonValue.NewObject();
            b.Set("breakpoints", arr);
            Respond(req, true, b);
        }

        // ------------------------------------------------------------------ cicode/history
        /// <summary>cicode/history {frameId}: the changes of that frame's call as its view sees
        /// them, for the client's inline values: {cursor, function, changes: [{line, name, index,
        /// value, old, entry, seq, time}]}.</summary>
        private void HistoryRequest(JsonValue req, JsonValue a)
        {
            var f = FrameInfo(Int(a, "frameId", 0));
            if (f == null)
                throw new ArgumentException("no frame " + Num(Int(a, "frameId", 0)) + " (ask stackTrace first)");
            var arr = JsonValue.NewArray();
            if (f.CallId != 0)
                foreach (var c in Changes(f.Task, f.CallId, null))
                {
                    var o = JsonValue.NewObject();
                    o.Set("line", JsonValue.Of(c.Line));
                    o.Set("name", c.Name);
                    o.Set("index", JsonValue.Of(c.Index));
                    o.Set("value", c.New);
                    o.Set("old", c.Old);
                    o.Set("entry", JsonValue.Of(c.Entry));
                    o.Set("seq", JsonValue.Of(c.Seq));
                    o.Set("time", JsonValue.Of(c.Time));
                    arr.Add(o);
                }
            var b = JsonValue.NewObject();
            long cursor = -1;
            try { cursor = _dbg.HistoryState(f.Task).Cursor; } catch (ArgumentException) { }
            b.Set("cursor", JsonValue.Of(cursor));
            b.Set("function", f.Function);
            b.Set("changes", arr);
            Respond(req, true, b);
        }
    }
}
