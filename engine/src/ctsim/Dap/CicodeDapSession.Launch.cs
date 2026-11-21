using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using CicodeSim.Builtins;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Host.Dap
{
    /// <summary>launch / attach / configurationDone / terminate / disconnect: one function run
    /// as a task, or a simulation that is already running.</summary>
    internal sealed partial class CicodeDapSession
    {
        private void Launch(JsonValue req, JsonValue a)
        {
            lock (_lock)
                if (_launch != null || _attach)
                    throw new ArgumentException("this session has already been started (one launch or attach per session)");
            string fn = Str(a, "function").Trim();
            if (fn.Length == 0)
                throw new ArgumentException("launch needs 'function': the name of the Cicode function to run");
            // The program is loaded once, by the server's own compile mode (ctsim --compile, which
            // the extension sets from this same field), so a launch can only confirm it.
            string compile = Str(a, "compile", _sim.Options.Compile);
            if (!CodeSource.IsMode(compile))
                throw new ArgumentException("compile must be changed, all or none: " + Quote(compile));
            if (compile != _sim.Options.Compile)
                throw new ArgumentException("this server loaded the program with compile " + Quote(_sim.Options.Compile)
                    + ", the launch asks for " + Quote(compile) + ": start ctsim with --compile " + compile);
            string project = Str(a, "project");
            if (project.Length > 0 && !InUnit(project))
                throw new ArgumentException("this server simulates " + _sim.ProjectDir + ": start a server for " + project);
            var args = a.Get("args");
            string? sig = _sim.Invoke(() => Signature(fn));
            var l = new LaunchArgs
            {
                Function = fn,
                Call = CallOf(fn, args, sig),
                StopOnEntry = Bool(a, "stopOnEntry"),
                KeepRunning = Bool(a, "keepRunning"),
                NoDebug = Bool(a, "noDebug"),
            };
            Simulation.SplitCall(l.Call, out string name, out string argText);
            int given = args != null && args.Kind == JsonKind.Array ? args.Items.Count : 0;
            string? refused = _sim.Invoke(() => Refused(name, argText, given));
            if (refused != null)
                throw new ArgumentException(refused + LoadProblems(_sim.Invoke(TakeLoadSnapshot),
                                                                   refused.StartsWith(NoFunction, StringComparison.Ordinal) ? name : null));
            string file = Str(a, "file");
            if (file.Length > 0)
                Remember(file);
            var notes = new List<string>();
            string trace = Str(a, "trace");
            if (trace.Length > 0)
                OpenTrace(trace, notes);
            try
            {
                Prepare(a.Get("scenario"), a.Get("tags"), notes);
            }
            catch (Exception)
            {
                CloseTrace();
                throw;
            }
            lock (_lock)
            {
                _launch = l;
                _noDebug = l.NoDebug;
            }
            ForegroundBreak(a);
            if (l.NoDebug)
            {
                // Run Without Debugging: the call is a normal task that never stops; every
                // hardware error is only written (OnLog), with its place
                _dbg.Options.BreakOnError = false;
                DropBreakpoints();
            }
            Respond(req, true);
            // what came from source and what did not compile (none: nothing)
            foreach (string n in _sim.LoadNotes)
                Output("console", n + "\n");
            foreach (string n in notes)
                Output("console", n + "\n");
            MaybeStart();
        }

        /// <summary>The parameter types of a function or built-in (one Types.SigChar each), null
        /// when there is none of that name (simulation thread).</summary>
        private string? Signature(string name)
        {
            var f = _sim.Library.Function(name);
            if (f != null && !f.Private)
                return f.Args;
            return _sim.Vm.Table.Lookup(name)?.Args;
        }

        /// <summary>The call text of the function with literal arguments (CallText converts them
        /// by the parameter types, as TaskNew does).</summary>
        private static string CallOf(string fn, JsonValue? args, string? sig)
        {
            var sb = new StringBuilder(fn).Append('(');
            if (args != null && !args.IsNull)
            {
                if (args.Kind != JsonKind.Array)
                    throw new ArgumentException("args must be a list of values in parameter order");
                int i = 0;
                foreach (var v in args.Items)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(Literal(v, i, sig != null && i < sig.Length ? Types.SigType(sig[i]) : -1));
                    i++;
                }
            }
            return sb.Append(')').ToString();
        }

        /// <summary>An argument as call text; <paramref name="type"/> is its parameter's type
        /// code (-1: not known).</summary>
        private static string Literal(JsonValue v, int i, int type)
        {
            switch (v.Kind)
            {
                case JsonKind.String:
                    return ValueFormat.Quote(v.Str);
                case JsonKind.Int:
                    return Number(v.FitsLong ? v.Long.ToString(CultureInfo.InvariantCulture) : v.BigValue.ToString(CultureInfo.InvariantCulture), v.Double, i, type);
                case JsonKind.Float:
                    if (double.IsNaN(v.Double) || double.IsInfinity(v.Double))
                        throw new ArgumentException("args[" + Num(i) + "]: " + FloatText.Shortest(v.Double) + " has no Cicode literal");
                    return Number(v.Double.ToString("R", CultureInfo.InvariantCulture), v.Double, i, type);
                case JsonKind.Bool:
                    return v.Bool ? "1" : "0";
            }
            throw new ArgumentException("args[" + Num(i) + "]: " + v.KindName + " is not a Cicode value (give a number or a string)");
        }

        private static readonly char[] RealMarks = { '.', 'E', 'e' };

        /// <summary>A number as call text. Call text reads an integer literal as 32 bits (a
        /// larger one wraps), so a whole number beyond INT goes by the parameter type: its text
        /// for a STRING, a real literal for a REAL (or a parameter of no known type), refused for
        /// an integer one (as is a real one beyond INT). Otherwise CallText converts the literal
        /// as TaskNew does.</summary>
        private static string Number(string text, double d, int i, int type)
        {
            bool real = text.IndexOfAny(RealMarks) >= 0;
            if (d > -2147483649.0 && d < 2147483648.0)
                return text;
            if (type >= 0 && type != Types.String && type != Types.VarArg && !Types.IsRealType(type))
                throw new ArgumentException("args[" + Num(i) + "]: " + text + " is outside the range of the " + ValueFormat.TypeName(type) +
                                            " parameter (-2147483648 to 2147483647)");
            if (type == Types.String && !real)
                return ValueFormat.Quote(text);
            if (double.IsInfinity(d))
                throw new ArgumentException("args[" + Num(i) + "]: " + text + " is too large for a REAL");
            return real ? text : text + ".0";
        }

        /// <summary>Why the call cannot run, null when it can (simulation thread).</summary>
        private string? Refused(string name, string argText, int given)
        {
            var f = _sim.Library.Function(name);
            // a function the source compiler could not compile, with no compiled code to fall
            // back on: a call would only halt
            if (f != null && f.NotCompiled != null)
                return name + " did not compile: " + f.NotCompiled;
            if (f != null)
                return ArgsRefused(f, argText, given);
            var b = _sim.Vm.Table.Lookup(name);
            if (b != null)
            {
                // as Simulation.StartCall binds a built-in
                for (int k = 0; k < b.Args.Length; k++)
                    if (b.ByRef(k))
                        return b.Name + " takes argument " + Num(k + 1) + " by reference: a launch cannot give one";
                return ArgsRefused(new CicodeSim.Compiled.Function(b.Name, b.Args, b.Ret, Array.Empty<byte>()) { MinParam = b.MinArgs }, argText, given);
            }
            foreach (var p in _sim.Library.Functions)
                if (p.Private && p.Name.StartsWith(name + "@", StringComparison.OrdinalIgnoreCase))
                    return name + " is PRIVATE (" + p.Filename + "): the simulator runs PUBLIC functions only for now";
            return NoFunction + Quote(name) + " in " + _sim.Library.Source +
                   (_sim.Options.Compile == CodeSource.None ? ": compile the project, or start ctsim with --compile changed" : "");
        }

        private const string NoFunction = "no function ";

        /// <summary>What the source compiler did, for a refused launch (taken on the simulation
        /// thread, written up off it).</summary>
        private sealed class LoadSnapshot
        {
            public string Root = "";
            /// <summary>The unit's .ci files.</summary>
            public List<string> Files = new List<string>();
            /// <summary>Functions not compiled from source, with why (LiveProgram.Failed).</summary>
            public List<(string Name, string Why)> Failed = new List<(string, string)>();
            /// <summary>The compiler's own messages (LiveProgram.Errors).</summary>
            public List<CicodeSim.Compiler.Semantics.Diagnostic> Errors = new List<CicodeSim.Compiler.Semantics.Diagnostic>();
            /// <summary>The errors of each unit file that has any (LiveProgram.ErrorsOf).</summary>
            public List<(string File, List<CicodeSim.Compiler.Semantics.Diagnostic> Errors)> FileErrors =
                new List<(string, List<CicodeSim.Compiler.Semantics.Diagnostic>)>();
            /// <summary>The program's functions by file name.</summary>
            public Dictionary<string, List<string>> FunctionsByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Simulation thread.</summary>
        private LoadSnapshot TakeLoadSnapshot()
        {
            var s = new LoadSnapshot { Root = _sim.World.Unit.Root };
            var live = _sim.Live;
            s.Failed.AddRange(live.Failed);
            s.Errors.AddRange(live.Errors);
            try
            {
                s.Files = CicodeSim.Compiler.Codegen.LiveProgram.UnitFiles(live.Project, live.UserDir);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                // no file list: the write-up names no projects
            }
            if (live.Source != null)
                foreach (string f in s.Files)
                {
                    var errs = live.ErrorsOf(f);
                    if (errs.Count > 0)
                        s.FileErrors.Add((f, errs));
                }
            foreach (var p in _sim.Library.Functions)
            {
                string key = Path.GetFileName(p.Filename);
                if (!s.FunctionsByFile.TryGetValue(key, out var list))
                    s.FunctionsByFile[key] = list = new List<string>();
                list.Add(p.Name);
            }
            return s;
        }

        /// <summary>A refused launch's write-up: for a missing function, the projects compiled and
        /// where the source defines it; then what did not compile from source.</summary>
        private static string LoadProblems(LoadSnapshot s, string? missing)
        {
            var sb = new StringBuilder();
            if (missing != null)
                WhereDefined(s, missing, sb);
            var problems = new StringBuilder();
            // one line per reason ('the compile stops at ...' covers every function after a stop)
            var reasons = new List<string>();
            var byReason = new Dictionary<string, List<string>>();
            foreach (var (fn, why) in s.Failed)
            {
                if (!byReason.TryGetValue(why, out var fns))
                {
                    byReason[why] = fns = new List<string>();
                    reasons.Add(why);
                }
                fns.Add(fn);
            }
            foreach (string why in reasons)
            {
                var fns = byReason[why];
                problems.Append("\nnot compiled from source: ").Append(fns.Count == 1 ? fns[0] : Num(fns.Count) + " functions (" + fns[0] + ", ...)")
                        .Append(why.Length > 0 ? ": " + why : "");
            }
            // the compiler's messages that are not a file's (an include row, a label table)
            var inFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (f, _) in s.FileErrors)
                inFiles.Add(f);
            int other = 0;
            foreach (var d in s.Errors)
                if (!inFiles.Contains(d.File) && ++other <= 10)
                    problems.Append("\ncompile error: ").Append(d);
            if (other > 10)
                problems.Append("\n... ").Append(Num(other - 10)).Append(" more compile errors");
            int shownFiles = 0;
            foreach (var (f, errs) in s.FileErrors)
            {
                if (++shownFiles > 10)
                {
                    problems.Append("\n... ").Append(Num(s.FileErrors.Count - 10)).Append(" more files that do not compile");
                    break;
                }
                problems.Append("\n").Append(Path.GetFileName(f)).Append(" does not compile (").Append(Num(errs.Count)).Append(errs.Count == 1 ? " error):" : " errors):");
                for (int k = 0; k < errs.Count && k < 3; k++)
                    problems.Append("\n  ").Append(Path.GetFileName(f)).Append(':').Append(Num(errs[k].Line)).Append(": ").Append(errs[k].Name)
                            .Append(' ').Append(errs[k].Message).Append(errs[k].Context.Length > 0 ? " {" + errs[k].Context + "}" : "");
            }
            if (problems.Length > 0)
                sb.Append("\nWhat did not compile from source (its compiled code, if any, runs instead):").Append(problems);
            return sb.ToString();
        }

        /// <summary>The projects the program was compiled from, and where their source defines
        /// <paramref name="fn"/>, with what the compiler said about that file and took from it: a
        /// function missing from the program is in a project this unit does not reach, or one the
        /// compiler did not take from its file. Reads the files as the compiler does.</summary>
        private static void WhereDefined(LoadSnapshot s, string fn, StringBuilder sb)
        {
            var names = new List<string>();
            foreach (string f in s.Files)
            {
                string? dir = Path.GetFileName(Path.GetDirectoryName(f));
                if (dir != null && !names.Exists(n => string.Equals(n, dir, StringComparison.OrdinalIgnoreCase)))
                    names.Add(dir);
            }
            if (names.Count > 0)
                sb.Append("\n").Append(s.Root).Append(" compiles the .ci files of ").Append(Num(names.Count)).Append(" projects: ").Append(string.Join(", ", names));
            var header = new System.Text.RegularExpressions.Regex(@"\bFUNCTION\s+" + System.Text.RegularExpressions.Regex.Escape(fn) + @"(?![\w\\])",
                                                                  System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (string f in s.Files)
            {
                string[] lines;
                try
                {
                    lines = CicodeSim.Compiler.Syntax.Lexer.SourceText(File.ReadAllBytes(f)).Split('\n');
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    continue;
                }
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!header.IsMatch(lines[i]))
                        continue;
                    string file = Path.GetFileName(f);
                    sb.Append("\nThe source defines it at ").Append(f).Append(':').Append(Num(i + 1)).Append(": ").Append(lines[i].Trim());
                    var errs = s.FileErrors.Find(x => string.Equals(x.File, f, StringComparison.OrdinalIgnoreCase)).Errors;
                    if (errs == null || errs.Count == 0)
                        sb.Append("\n  the compiler reported nothing for ").Append(file);
                    else
                        for (int k = 0; k < errs.Count && k < 5; k++)
                            sb.Append("\n  ").Append(file).Append(':').Append(Num(errs[k].Line)).Append(": ").Append(errs[k].Name)
                              .Append(' ').Append(errs[k].Message).Append(errs[k].Context.Length > 0 ? " {" + errs[k].Context + "}" : "");
                    s.FunctionsByFile.TryGetValue(file, out var taken);
                    int n = taken?.Count ?? 0;
                    sb.Append("\n  the program has ").Append(Num(n)).Append(" functions from ").Append(file)
                      .Append(n > 0 ? ": " + string.Join(", ", taken!.GetRange(0, Math.Min(12, n))) + (n > 12 ? ", ..." : "") : "");
                }
            }
        }

        /// <summary>The halt reason tells more than the error code and text ('AddOne did not compile: ...'),
        /// not 'hardware error 259' again.</summary>
        private static bool SaysMore(CallOutcome o) =>
            o.HaltReason.Length > 0 && o.HaltReason.IndexOf(o.ErrorText, StringComparison.OrdinalIgnoreCase) < 0 &&
            o.HaltReason.IndexOf("hardware error " + Num(o.Error), StringComparison.OrdinalIgnoreCase) < 0;

        /// <summary>Why the arguments do not fit the parameters (CallText.Bind), null when they do.</summary>
        private static string? ArgsRefused(CicodeSim.Compiled.Function f, string argText, int given)
        {
            CallText.Bind(f, argText, out int err, out string msg);
            if (err == Errors.TypeMismatch || err == Errors.TooFewArgs)
                return "args: " + (msg.Length > 0 ? msg : "error " + Num(err) + " " + Errors.Title(err));
            // call text drops the rest (518, the call is still made): a launch does not
            if (err == Errors.TooManyArgs)
                return "args: " + f.Name + " takes " + Num(f.Args.Length) + " argument(s), " + Num(given) + " given";
            return null;
        }

        /// <summary>The project is this simulation's or one of its compile unit (a .ci of an
        /// included project names its own folder).</summary>
        private bool InUnit(string project)
        {
            string full;
            try { full = Path.GetFullPath(project).TrimEnd('\\', '/'); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException) { full = project; }
            if (string.Equals(full, _sim.ProjectDir, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(project, _sim.ProjectName, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var p in _sim.World.Unit.Projects)
                if (string.Equals(full, p.Dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(project, p.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>The scenario (run on the clock: its tags now, its steps relative to now),
        /// then the tags (they win over the scenario's).</summary>
        private void Prepare(JsonValue? scenario, JsonValue? tags, List<string> notes)
        {
            if (tags != null && !tags.IsNull && tags.Kind != JsonKind.Object)
                throw new ArgumentException("tags must be an object: {\"Tag\": value or {\"value\", \"quality\", \"timestamp\"}}");
            bool hasScenario = scenario != null && !scenario.IsNull;
            string? path = null;
            if (hasScenario)
            {
                if (scenario!.Kind == JsonKind.String)
                    path = Path.Combine(_sim.ProjectDir, scenario.Str);
                else if (scenario.Kind != JsonKind.Object)
                    throw new ArgumentException("scenario must be a ctsim-scenario file or object");
            }
            var errors = new List<string>();
            _sim.Invoke(() =>
            {
                if (hasScenario)
                {
                    try
                    {
                        _sim.LoadScenario(path == null ? scenario : null, path);
                        _sim.RunScenario();
                    }
                    catch (Exception e) when (e is IOException || e is FormatException || e is ArgumentException || e is InvalidOperationException)
                    {
                        errors.Add("scenario: " + e.Message);
                        return;
                    }
                }
                if (tags == null || tags.Kind != JsonKind.Object)
                    return;
                foreach (var kv in tags.Pairs)
                {
                    JsonValue value = kv.Value;
                    JsonValue? quality = null;
                    if (kv.Value.Kind == JsonKind.Object)
                    {
                        value = kv.Value.Get("value") ?? JsonValue.Null;
                        quality = kv.Value.Get("quality");
                        var ts = kv.Value.Get("timestamp");
                        if (ts != null && !ts.IsNull)
                            notes.Add("tags: the timestamp of " + kv.Key + " is not set (a write is stamped with the simulated time)");
                    }
                    int err;
                    try
                    {
                        err = _sim.SetTag(kv.Key, value, quality);
                    }
                    catch (Exception e) when (e is ArgumentException || e is FormatException)
                    {
                        errors.Add(kv.Key + ": " + e.Message);
                        continue;
                    }
                    if (err != 0)
                        errors.Add(kv.Key + ": error " + Num(err) + " " + Errors.Title(err));
                }
            });
            if (errors.Count > 0)
                throw new ArgumentException((errors[0].StartsWith("scenario: ", StringComparison.Ordinal) ? "" : "tags: ") + string.Join("; ", errors));
        }

        private void OpenTrace(string path, List<string> notes)
        {
            string full = Path.GetFullPath(Path.Combine(_sim.ProjectDir, path));
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tw = _sim.Invoke(() => _sim.World.Trace == null ? _sim.World.OpenTrace(full) : null);
            if (tw == null)
            {
                notes.Add("trace: the server already writes a trace (--trace), " + full + " is not written");
                return;
            }
            lock (_lock)
                _trace = tw;
        }

        private void CloseTrace()
        {
            CicodeSim.World.TraceWriter? tw;
            lock (_lock)
            {
                tw = _trace;
                _trace = null;
            }
            if (tw == null)
                return;
            try
            {
                _sim.InvokeDebug(() =>
                {
                    if (ReferenceEquals(_sim.World.Trace, tw))
                        _sim.World.DetachTrace();
                    tw.Close();
                });
            }
            catch (ObjectDisposedException)
            {
                // the simulation has ended: it closed its trace
            }
            catch (Exception e) when (e is IOException || e is TimeoutException || e is InvalidOperationException)
            {
                // the session ends anyway (a trace file that cannot be flushed)
            }
        }

        private void Attach(JsonValue req, JsonValue a)
        {
            // 'server' picked this server (the client's discovery); a project it does not
            // simulate would give breakpoints that never verify
            string project = Str(a, "project");
            if (project.Length > 0 && !InUnit(project))
                throw new ArgumentException("this server simulates " + _sim.ProjectDir + ": start a server for " + project);
            lock (_lock)
            {
                if (_launch != null || _attach)
                    throw new ArgumentException("this session has already been started (one launch or attach per session)");
                _attach = true;
            }
            ForegroundBreak(a);
            Respond(req, true);
            Output("console", "Attached to the simulation of " + _sim.ProjectName + " (" + _sim.ProjectDir + ")\n");
            MaybeStart();
        }

        /// <summary>launch / attach 'foregroundBreak': whether page code stops (the simulator's
        /// default) or never does (the runtime's rule, notice 343). Left out: as it is.</summary>
        private void ForegroundBreak(JsonValue a)
        {
            if (a.Get("foregroundBreak") is JsonValue v && v.Kind == JsonKind.Bool)
                _sim.InvokeDebug(() => _sim.ForegroundBreak = v.Bool);
        }

        private void ConfigurationDone(JsonValue req)
        {
            Respond(req, true);
            lock (_lock)
                _configDone = true;
            MaybeStart();
        }

        /// <summary>Once the launch (or attach) and configurationDone are both in (they come in
        /// either order), with the breakpoints in place: start the call, or report the tasks
        /// that are stopped already. Called after the request's response: nothing escapes.</summary>
        private void MaybeStart()
        {
            LaunchArgs? l;
            lock (_lock)
            {
                if (_started || !_configDone || (_launch == null && !_attach) || _terminating)
                    return;
                _started = true;
                l = _launch;
            }
            try
            {
                // this session's error rule (a previous session's late detach may have ended it)
                _sim.Debugger.Attach();
                if (l != null)
                    StartCall(l);
                else
                    ReportStopped();
            }
            catch (Exception e)
            {
                string why = e is ObjectDisposedException ? "the simulation has ended" : e.GetType().Name + ": " + e.Message;
                if (l != null)
                    CallFailed(l, why);
                else
                    Output("stderr", "attach: " + why + "\n");
            }
        }

        private void StartCall(LaunchArgs l)
        {
            try
            {
                _sim.Invoke(() =>
                {
                    // a normal task also under noDebug (the session never stops it); a NoDebug
                    // task is the debugger's own evaluation, whose errors are never logged
                    var t = _sim.StartCall(l.Call, o => _sim.Events.Post(() => CallEnded(l, o)));
                    Volatile.Write(ref _call, t);
                    if (l.StopOnEntry && !l.NoDebug)
                    {
                        // before the task's first slice: it stops at its first Line marker
                        Volatile.Write(ref _entryTask, t.Handle);
                        _dbg.Pause(t.Handle);
                    }
                });
            }
            catch (CallTextException e)
            {
                CallFailed(l, e.Message);
            }
            catch (HardwareError e)
            {
                CallFailed(l, "the call cannot start: error " + Num(e.Code) + " " + Errors.Title(e.Code));
            }
        }

        private void CallFailed(LaunchArgs l, string why)
        {
            Output("stderr", l.Function + ": " + why + "\n");
            bool end;
            lock (_lock)
            {
                _callDone = true;
                end = !l.KeepRunning || _terminating;
            }
            if (end)
            {
                Exited(1);
                SendTerminated();
            }
        }

        /// <summary>The launched call ended (event thread): its return value or why it halted,
        /// then the session ends unless it keeps running.</summary>
        private void CallEnded(LaunchArgs l, CallOutcome o)
        {
            if (Volatile.Read(ref _closed) != 0)
                return;
            string text;
            string category = "console";
            int exit = 0;
            string? value = null;
            if (o.State == "stop")
            {
                value = o.Type.Length > 0 ? Display(o) : null;
                text = l.Function + " returned " + (value ?? "(no value)");
            }
            else
            {
                category = "stderr";
                exit = 1;
                if (o.Error != 0)
                    text = l.Function + " halted: hardware error " + Num(o.Error) + " '" + o.ErrorText + "'" +
                           // the reason when it says more ('AddOne did not compile: ...')
                           (SaysMore(o) ? " (" + o.HaltReason + ")" : "");
                else
                    text = l.Function + " " + (o.State == "halt" ? "halted" : "ended") + (o.HaltReason.Length > 0 ? ": " + o.HaltReason : "");
            }
            bool end;
            string? file;
            int line;
            lock (_lock)
            {
                _callDone = true;
                if (value != null)
                {
                    _returnValue = value;
                    _returnType = o.Type;
                }
                end = !l.KeepRunning || _terminating;
                // the place of its halt log line (IsCallHaltLog)
                file = _haltFile;
                line = _haltLine;
            }
            Output(category, text + "\n", file, line);
            if (end)
            {
                Exited(exit);
                SendTerminated();
            }
        }

        /// <summary>A call's value as the Variables view shows values (a string quoted).</summary>
        private static string Display(CallOutcome o)
        {
            switch (o.Type)
            {
                case "STRING":
                    return ValueFormat.Quote(o.Value.Str);
                case "REAL":
                    return o.Value.IsNumber ? ValueFormat.Real(o.Value.Double) : o.Result;
                case "TIMESTAMP":
                case "QUALITY":
                    return o.Value.Kind == JsonKind.String ? o.Value.Str : o.Result;
            }
            return o.Result;
        }

        /// <summary>attach: the tasks a debugger stopped before this session (they are released
        /// when a session ends, so normally none).</summary>
        private void ReportStopped()
        {
            foreach (var t in _dbg.Tasks())
            {
                if (!t.Paused || t.Parked)
                    continue;
                var b = JsonValue.NewObject();
                string r = t.StopReason;
                b.Set("reason", r == "breakpoint" || r == "step" || r == "exception" || r == "entry" ? r : "pause");
                b.Set("threadId", JsonValue.Of(t.Id));
                b.Set("allThreadsStopped", JsonValue.False);
                Event("stopped", b);
            }
        }

        /// <summary>End the launched call if it still runs (it halts: 'terminated by the
        /// debugger'); true when it did.</summary>
        private bool KillCall()
        {
            var t = Volatile.Read(ref _call);
            if (t == null)
                return false;
            // InvokeDebug: also while page code the debugger stopped holds the simulation
            return _sim.InvokeDebug(() =>
            {
                if (t.Done)
                    return false;
                _sim.Vm.Halt(t, "terminated by the debugger");
                _sim.Scheduler.DropEnded();
                return true;
            });
        }

        private bool TryKill()
        {
            try
            {
                return KillCall();
            }
            catch (Exception e) when (e is ObjectDisposedException || e is TimeoutException || e is InvalidOperationException)
            {
                return false;
            }
        }

        private void Terminate(JsonValue req)
        {
            bool running;
            lock (_lock)
            {
                _terminating = true;
                running = _launch != null && _started && !_callDone;
            }
            Respond(req, true);
            // a call that still runs ends now; its end sends terminated
            if (running && TryKill())
                return;
            SendTerminated();
        }

        private void Disconnect(JsonValue req, JsonValue a)
        {
            var td = a.Get("terminateDebuggee");
            bool keep = td != null && td.Kind == JsonKind.Bool && !td.Bool;
            bool kill;
            lock (_lock)
                kill = _launch != null && !_callDone && !keep;
            Respond(req, true);
            if (kill)
                TryKill();
            Shutdown(true);
            try
            {
                _dbg.Detach();
            }
            catch (Exception e) when (e is ObjectDisposedException || e is TimeoutException || e is InvalidOperationException)
            {
                // the simulation has ended (the server detaches again when the connection closes)
            }
            // ctsim dap's stdio session: the process ends with it (SimServer)
            _ctx.Sink.Close();
        }
    }
}
