using System;
using System.Collections.Generic;
using System.Text;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Host.Dap
{
    /// <summary>threads, stackTrace, scopes, variables, evaluate, setVariable, exceptionInfo.</summary>
    internal sealed partial class CicodeDapSession
    {
        /// <summary>The name of the "(return value)" variable and its scope.</summary>
        public const string ReturnValueName = "(return value)";
        public const string ReturnValueScope = "Return value";

        private void Threads(JsonValue req)
        {
            var tasks = new List<DebugTaskInfo>(_dbg.Tasks());
            tasks.Sort((x, y) => x.Id.CompareTo(y.Id));
            var arr = JsonValue.NewArray();
            foreach (var t in tasks)
            {
                var o = JsonValue.NewObject();
                o.Set("id", JsonValue.Of(t.Id));
                o.Set("name", "Cicode thread " + Num(t.Id) + (t.Name.Length > 0 ? " (" + t.Name + ")" : ""));
                arr.Add(o);
            }
            var b = JsonValue.NewObject();
            b.Set("threads", arr);
            Respond(req, true, b);
        }

        private void StackTrace(JsonValue req, JsonValue a)
        {
            int task = Int(a, "threadId", 0);
            var frames = _dbg.StackTrace(task);
            // stopped on a hardware error: the top frame shows the failing statement, as the
            // real adapter pins it (the task goes on from the next one; exceptionInfo says so)
            int pinAt = 0, pinLine = 0;
            string pinFile = "";
            lock (_lock)
                if (_errors.TryGetValue(task, out var err) && err.Stopped && err.Pin)
                {
                    pinAt = err.PauseLine;
                    pinLine = err.Line;
                    pinFile = err.File;
                }
            int start = Math.Max(0, Int(a, "startFrame", 0));
            int levels = Int(a, "levels", 0);
            int end = levels > 0 ? (int)Math.Min((long)start + levels, frames.Count) : frames.Count;
            var arr = JsonValue.NewArray();
            for (int i = start; i < end; i++)
            {
                var f = frames[i];
                RememberFrame(f);
                string file = f.File;
                int line = f.Line;
                if (i == 0 && pinAt > 0 && f.Line == pinAt)
                {
                    file = pinFile;
                    line = pinLine;
                }
                var o = JsonValue.NewObject();
                o.Set("id", JsonValue.Of(f.Id));
                o.Set("name", f.Function.Length > 0 ? f.Function : "(call text)");
                o.Set("line", JsonValue.Of(line));
                o.Set("column", JsonValue.Of(1));
                if (file.Length > 0)
                    o.Set("source", Source(LocalPath(file)));
                else
                    o.Set("presentationHint", "subtle");
                arr.Add(o);
            }
            var b = JsonValue.NewObject();
            b.Set("stackFrames", arr);
            b.Set("totalFrames", JsonValue.Of(frames.Count));
            Respond(req, true, b);
        }

        private void Scopes(JsonValue req, JsonValue a)
        {
            int frameId = Int(a, "frameId", 0);
            var scopes = _dbg.Scopes(frameId);
            var arr = JsonValue.NewArray();
            foreach (var s in scopes)
            {
                RememberScope(frameId, s);
                var o = JsonValue.NewObject();
                o.Set("name", s.Name);
                if (s.Name == "Locals")
                    o.Set("presentationHint", "locals");
                o.Set("variablesReference", JsonValue.Of(s.VariablesReference));
                o.Set("namedVariables", JsonValue.Of(s.NamedVariables));
                o.Set("indexedVariables", JsonValue.Of(s.IndexedVariables));
                o.Set("expensive", JsonValue.Of(s.Expensive));
                arr.Add(o);
            }
            if (ReturnValue(out _, out _))
            {
                var o = JsonValue.NewObject();
                o.Set("name", ReturnValueScope);
                o.Set("presentationHint", "returnValue");
                o.Set("variablesReference", JsonValue.Of(ReturnValueRef));
                o.Set("namedVariables", JsonValue.Of(1));
                o.Set("indexedVariables", JsonValue.Of(0));
                o.Set("expensive", JsonValue.False);
                arr.Add(o);
            }
            var b = JsonValue.NewObject();
            b.Set("scopes", arr);
            Respond(req, true, b);
        }

        /// <summary>The launched call's return value, kept while a keepRunning session lives.</summary>
        private bool ReturnValue(out string value, out string type)
        {
            lock (_lock)
            {
                value = _returnValue ?? "";
                type = _returnType;
                return _returnValue != null;
            }
        }

        private void Variables(JsonValue req, JsonValue a)
        {
            int vref = Int(a, "variablesReference", 0);
            var arr = JsonValue.NewArray();
            if (vref == ReturnValueRef)
            {
                if (!ReturnValue(out string value, out string type))
                    throw new ArgumentException("no return value yet");
                arr.Add(VariableJson(new DebugVariable { Name = ReturnValueName, Value = value, Type = type, ReadOnly = true }));
            }
            else if (HistoryRefOf(vref) is HistoryRef h)
                arr = HistoryChildren(h);
            else
            {
                string filter = Str(a, "filter");
                var vars = new List<(DebugVariable, JsonValue)>();
                if (filter == "named")
                {
                    // the children of an array are all indexed ('[3]'); everything else is named
                    foreach (var v in _dbg.Variables(vref))
                        if (!v.Name.StartsWith("[", StringComparison.Ordinal))
                            vars.Add((v, VariableJson(v)));
                }
                else
                {
                    foreach (var v in _dbg.Variables(vref, Math.Max(0, Int(a, "start", 0)), Math.Max(0, Int(a, "count", 0))))
                        vars.Add((v, VariableJson(v)));
                }
                AddHistory(vref, vars);
                foreach (var (_, o) in vars)
                    arr.Add(o);
            }
            var b = JsonValue.NewObject();
            b.Set("variables", arr);
            Respond(req, true, b);
        }

        private static JsonValue VariableJson(DebugVariable v)
        {
            var o = JsonValue.NewObject();
            o.Set("name", v.Name);
            o.Set("value", v.Value);
            if (v.Type.Length > 0)
                o.Set("type", v.Type);
            o.Set("variablesReference", JsonValue.Of(v.VariablesReference));
            if (v.NamedVariables > 0)
                o.Set("namedVariables", JsonValue.Of(v.NamedVariables));
            if (v.IndexedVariables > 0)
                o.Set("indexedVariables", JsonValue.Of(v.IndexedVariables));
            if (v.EvaluateName.Length > 0)
                o.Set("evaluateName", v.EvaluateName);
            if (v.ReadOnly)
            {
                var hint = JsonValue.NewObject();
                var attrs = JsonValue.NewArray();
                attrs.Add(JsonValue.Of("readOnly"));
                hint.Set("attributes", attrs);
                o.Set("presentationHint", hint);
            }
            return o;
        }

        private void Evaluate(JsonValue req, JsonValue a)
        {
            string expr = Str(a, "expression");
            var b = JsonValue.NewObject();
            if (expr.Trim() == ReturnValueName)
            {
                if (!ReturnValue(out string value, out string type))
                    throw new ArgumentException("no return value yet: " + ReturnValueName + " is the launched function's, once it has returned");
                b.Set("result", value);
                b.Set("type", type);
                b.Set("variablesReference", JsonValue.Of(0));
                Respond(req, true, b);
                return;
            }
            EvaluateContext ctx;
            switch (Str(a, "context", "repl"))
            {
                case "watch":
                case "variables":
                    ctx = EvaluateContext.Watch;
                    break;
                case "hover":
                    ctx = EvaluateContext.Hover;
                    break;
                case "clipboard":
                    ctx = EvaluateContext.Clipboard;
                    break;
                default:
                    ctx = EvaluateContext.Repl;
                    break;
            }
            int frame = Int(a, "frameId", 0);
            var r = _dbg.Evaluate(expr, frame != 0 ? (int?)frame : null, ctx);
            if (r.Error != 0 || r.ErrorText.Length > 0)
            {
                Fail(req, r.ErrorText.Length > 0 ? r.ErrorText : "error " + Num(r.Error) + " " + Errors.Title(r.Error));
                return;
            }
            b.Set("result", r.Result);
            if (r.Type.Length > 0)
                b.Set("type", r.Type);
            b.Set("variablesReference", JsonValue.Of(r.VariablesReference));
            if (r.NamedVariables > 0)
                b.Set("namedVariables", JsonValue.Of(r.NamedVariables));
            if (r.IndexedVariables > 0)
                b.Set("indexedVariables", JsonValue.Of(r.IndexedVariables));
            Respond(req, true, b);
        }

        private void SetVariable(JsonValue req, JsonValue a)
        {
            int vref = Int(a, "variablesReference", 0);
            if (vref == ReturnValueRef)
                throw new ArgumentException("the return value cannot be changed");
            var v = _dbg.SetVariable(vref, Str(a, "name"), Str(a, "value"));
            var b = JsonValue.NewObject();
            b.Set("value", v.Value);
            if (v.Type.Length > 0)
                b.Set("type", v.Type);
            b.Set("variablesReference", JsonValue.Of(v.VariablesReference));
            if (v.NamedVariables > 0)
                b.Set("namedVariables", JsonValue.Of(v.NamedVariables));
            if (v.IndexedVariables > 0)
                b.Set("indexedVariables", JsonValue.Of(v.IndexedVariables));
            Respond(req, true, b);
        }

        /// <summary>The real adapter's shape (Handlers.cs OnExceptionInfo).</summary>
        private void ExceptionInfo(JsonValue req, JsonValue a)
        {
            int task = Int(a, "threadId", 0);
            ErrorInfo? e;
            lock (_lock)
            {
                if (!_errors.TryGetValue(task, out e))
                    foreach (var kv in _errors)
                        if (kv.Value.Stopped)
                        {
                            // exceptionInfo without a usable threadId: any error stop will do
                            e = kv.Value;
                            break;
                        }
            }
            if (e == null)
                throw new ArgumentException("No Cicode hardware error on this thread.");
            var desc = new StringBuilder(e.Text.Length > 0 ? e.Text : "Cicode hardware error");
            if (e.ErrPage.Length > 0)
                desc.Append(" (raised by ").Append(e.ErrPage).Append(')');
            if (e.ErrDesc.Length > 0)
                desc.Append(" in ").Append(e.ErrDesc);
            var trace = new StringBuilder();
            if (e.File.Length > 0)
                trace.Append("at ").Append(LocalPath(e.File)).Append(':').Append(Num(e.Line)).Append('\n');
            if (e.PauseLine > 0 && e.PauseLine != e.Line)
                trace.Append("The task is suspended before line ").Append(Num(e.PauseLine)).Append("; stepping continues from there.\n");
            var details = JsonValue.NewObject();
            details.Set("message", e.Announce.Length > 0 ? e.Announce : desc.ToString());
            details.Set("typeName", "Cicode hardware error");
            details.Set("stackTrace", trace.ToString().TrimEnd());
            var b = JsonValue.NewObject();
            b.Set("exceptionId", "Hardware error " + Num(e.Code));
            b.Set("description", desc.ToString());
            b.Set("breakMode", "always");
            b.Set("details", details);
            Respond(req, true, b);
        }
    }
}
