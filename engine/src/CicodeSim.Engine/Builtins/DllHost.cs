using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CicodeSim.Values;

namespace CicodeSim.Builtins
{
    /// <summary>The DLL helper process: one JSON request per line in, one answer per line out.</summary>
    public static class DllServer
    {
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool SetStdHandle(int which, IntPtr handle);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string name,
            uint access,
            uint share,
            IntPtr security,
            uint disposition,
            uint flags,
            IntPtr template
        );

        /// <summary>The <c>ctsim dllhost</c> command.</summary>
        public static int Main()
        {
            Stream input = Console.OpenStandardInput();
            Stream output = Console.OpenStandardOutput();
            try
            {
                // what a library prints must not land in the answers: its standard output is nowhere
                IntPtr nul = CreateFileW(
                    "NUL",
                    0x40000000 /* write */
                    ,
                    3,
                    IntPtr.Zero,
                    3 /* open existing */
                    ,
                    0,
                    IntPtr.Zero
                );
                if (nul != IntPtr.Zero && nul != new IntPtr(-1))
                    SetStdHandle(-11, nul);
            }
            catch (Exception) { } // swallow-ok: not Windows, or no console handles: the answers still go to the stream opened above
            Run(input, output);
            return 0;
        }

        private static List<DllValue> Values(JsonValue? a)
        {
            var l = new List<DllValue>();
            if (a != null && a.Kind == JsonKind.Array)
                foreach (JsonValue v in a.Items)
                    l.Add(DllValue.FromJson(v));
            return l;
        }

        private static int Int(JsonValue q, string key) =>
            q.Get(key) is JsonValue v && v.IsNumber ? (int)v.ToNumber() : -1;

        private static string Str(JsonValue q, string key) =>
            q.Get(key) is JsonValue v && v.Kind == JsonKind.String ? v.Str : "";

        /// <summary>Serve requests until the input ends.</summary>
        public static void Run(Stream input, Stream output)
        {
            var reader = new StreamReader(input, new UTF8Encoding(false));
            var writer = new StreamWriter(output, new UTF8Encoding(false))
            {
                NewLine = "\n",
                AutoFlush = true,
            };
            using (var runner = new DllLocalRunner())
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0)
                        continue;
                    var r = JsonValue.NewObject();
                    try
                    {
                        JsonValue q = JsonValue.Parse(line);
                        string error = "";
                        bool ok = true;
                        DllValue value = default;
                        bool hasValue = false;
                        switch (Str(q, "op"))
                        {
                            case "ping":
                                r.Set("bits", JsonValue.Of(IntPtr.Size * 8));
                                break;
                            case "load":
                            {
                                int id = runner.Load(Str(q, "path"), Str(q, "name"), out error);
                                ok = id >= 0;
                                r.Set("id", JsonValue.Of(id));
                                break;
                            }
                            case "free":
                                runner.Free(Int(q, "id"));
                                break;
                            case "call":
                                ok = runner.Call(
                                    Int(q, "id"),
                                    Str(q, "types"),
                                    Values(q.Get("args")),
                                    out value,
                                    out error
                                );
                                hasValue = ok;
                                break;
                            case "classCreate":
                            {
                                int id = runner.ClassCreate(
                                    Str(q, "path"),
                                    Str(q, "class"),
                                    Values(q.Get("args")),
                                    out error
                                );
                                ok = id >= 0;
                                r.Set("id", JsonValue.Of(id));
                                break;
                            }
                            case "classIsValid":
                                r.Set("valid", JsonValue.Of(runner.ClassIsValid(Int(q, "id"))));
                                break;
                            case "classDispose":
                                ok = runner.ClassDispose(Int(q, "id"), out error);
                                break;
                            case "classGet":
                                ok = runner.ClassGet(Int(q, "id"), Str(q, "name"), out value, out error);
                                hasValue = ok;
                                break;
                            case "classSet":
                                ok = runner.ClassSet(
                                    Int(q, "id"),
                                    Str(q, "name"),
                                    DllValue.FromJson(q.Get("value")),
                                    out error
                                );
                                break;
                            case "classCall":
                                ok = runner.ClassCall(
                                    Int(q, "id"),
                                    Str(q, "name"),
                                    Values(q.Get("args")),
                                    out value,
                                    out error
                                );
                                hasValue = ok;
                                break;
                            default:
                                ok = false;
                                error = "unknown request";
                                break;
                        }
                        r.Set("ok", JsonValue.Of(ok));
                        if (!ok)
                            r.Set("error", JsonValue.Of(error));
                        if (hasValue)
                            r.Set("value", value.ToJson());
                    }
                    catch (Exception ex)
                    {
                        r = JsonValue.NewObject();
                        r.Set("ok", JsonValue.False);
                        r.Set("error", JsonValue.Of(ex.GetType().Name + ": " + ex.Message));
                    }
                    writer.WriteLine(r.DumpStrict());
                }
            }
        }
    }

    /// <summary>Runs libraries in a helper process, so a crash takes only the helper down.</summary>
    public sealed class DllChildRunner : IDllRunner
    {
        private readonly string _exe;
        private readonly string[] _args;
        private readonly object _lock = new object();
        private Process? _proc;
        private StreamWriter? _in;
        private StreamReader? _out;
        private int _generation;

        private sealed class Fn
        {
            public string Path = "",
                Name = "";
            public int Generation = -1,
                ChildId = -1;
        }

        private readonly Dictionary<int, Fn> _fns = new Dictionary<int, Fn>();
        private readonly Dictionary<int, (int Generation, int ChildId)> _objects =
            new Dictionary<int, (int, int)>();
        private int _nextFn,
            _nextObject = 1;

        /// <summary>How long a request may take before the helper is ended: milliseconds, 0 = as
        /// long as it takes (the runtime waits for a library too).</summary>
        public int TimeoutMs;

        /// <summary>Called with a line when the helper starts, dies or is ended.</summary>
        public Action<string>? Log;

        public DllChildRunner(string exe, params string[] args)
        {
            _exe = exe;
            _args = args;
        }

        /// <summary>The runner over this process's own executable with the <c>dllhost</c>
        /// command (ctsim.exe, or dotnet with the host's assembly).</summary>
        public static DllChildRunner ForThisProcess(string hostAssembly)
        {
            string exe = "";
            try
            {
                exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            }
            catch (Exception) { } // swallow-ok: no main module to ask (the helper then fails to start, with a log line)
            string name = Path.GetFileNameWithoutExtension(exe);
            if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
                return new DllChildRunner(exe, hostAssembly, "dllhost");
            return new DllChildRunner(exe, "dllhost");
        }

        private static string Quote(string a) =>
            a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 && a.Length > 0
                ? a
                : "\"" + a.Replace("\"", "\\\"") + "\"";

        private bool Alive => _proc != null && !_proc.HasExited;

        private bool Start(out string error)
        {
            error = "";
            if (Alive)
                return true;
            Stop();
            try
            {
                var sb = new StringBuilder();
                foreach (string a in _args)
                    sb.Append(sb.Length > 0 ? " " : "").Append(Quote(a));
                var si = new ProcessStartInfo(_exe, sb.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                };
                _proc = Process.Start(si);
                if (_proc == null)
                {
                    error = "the library helper did not start";
                    return false;
                }
                _in = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false))
                {
                    NewLine = "\n",
                    AutoFlush = true,
                };
                _out = _proc.StandardOutput;
                _generation++;
                Log?.Invoke(
                    "The library helper started (process "
                        + _proc.Id.ToString(CultureInfo.InvariantCulture)
                        + ")"
                );
                return true;
            }
            catch (Exception ex)
            {
                error = "the library helper did not start: " + ex.Message;
                Stop();
                return false;
            }
        }

        private void Stop()
        {
            var p = _proc;
            _proc = null;
            _in = null;
            _out = null;
            if (p == null)
                return;
            try
            {
                if (!p.HasExited)
                    p.Kill();
            }
            catch (Exception) { } // swallow-ok: the helper ended by itself meanwhile
            try
            {
                p.Dispose();
            }
            catch (Exception) { } // swallow-ok: a process object that is gone
        }

        private JsonValue? Send(JsonValue request, out string error)
        {
            lock (_lock)
            {
                if (!Start(out error))
                    return null;
                var proc = _proc;
                try
                {
                    _in!.WriteLine(request.DumpStrict());
                    var read = _out!.ReadLineAsync();
                    if (!read.Wait(TimeoutMs > 0 ? TimeoutMs : System.Threading.Timeout.Infinite))
                    {
                        error =
                            "the library did not answer within "
                            + (TimeoutMs / 1000).ToString(CultureInfo.InvariantCulture)
                            + " s: the library helper was ended";
                        Log?.Invoke(
                            "The library helper was ended: no answer within "
                                + (TimeoutMs / 1000).ToString(CultureInfo.InvariantCulture)
                                + " s"
                        );
                        Stop();
                        return null;
                    }
                    string? line = read.Result;
                    if (line == null)
                    {
                        int code = 0;
                        try
                        {
                            proc!.WaitForExit(2000);
                            code = proc.ExitCode;
                        }
                        catch (Exception) { } // swallow-ok: the exit code is only for the message
                        error =
                            "the library helper ended (the library crashed it; exit code 0x"
                            + code.ToString("X8", CultureInfo.InvariantCulture)
                            + ")";
                        Log?.Invoke(
                            "The library helper ended while a library ran (exit code 0x"
                                + code.ToString("X8", CultureInfo.InvariantCulture)
                                + ")"
                        );
                        Stop();
                        return null;
                    }
                    JsonValue r = JsonValue.Parse(line);
                    if (r.Get("ok") is JsonValue ok && ok.Kind == JsonKind.Bool && ok.Bool)
                        return r;
                    error = r.Get("error")?.Str ?? "the request failed";
                    return null;
                }
                catch (Exception ex)
                {
                    error = "the library helper failed: " + (ex.InnerException ?? ex).Message;
                    Stop();
                    return null;
                }
            }
        }

        private static JsonValue Request(string op)
        {
            var q = JsonValue.NewObject();
            q.Set("op", JsonValue.Of(op));
            return q;
        }

        private static JsonValue Values(IReadOnlyList<DllValue> args)
        {
            var a = JsonValue.NewArray();
            foreach (var v in args)
                a.Add(v.ToJson());
            return a;
        }

        private bool Ensure(Fn f, out string error)
        {
            error = "";
            lock (_lock)
            {
                if (Alive && f.Generation == _generation)
                    return true;
                var q = Request("load");
                q.Set("path", JsonValue.Of(f.Path));
                q.Set("name", JsonValue.Of(f.Name));
                JsonValue? r = Send(q, out error);
                if (r == null)
                    return false;
                f.ChildId = (int)(r.Get("id")?.ToNumber() ?? -1);
                f.Generation = _generation;
                return true;
            }
        }

        public int Load(string path, string name, out string error)
        {
            var f = new Fn { Path = path, Name = name };
            if (!Ensure(f, out error))
                return -1;
            lock (_lock)
            {
                int id = _nextFn++;
                _fns[id] = f;
                return id;
            }
        }

        public void Free(int id)
        {
            Fn? f;
            lock (_lock)
            {
                if (!_fns.TryGetValue(id, out f))
                    return;
                _fns.Remove(id);
                if (!Alive || f.Generation != _generation)
                    return;
            }
            var q = Request("free");
            q.Set("id", JsonValue.Of(f.ChildId));
            Send(q, out _);
        }

        public bool Call(
            int id,
            string types,
            IReadOnlyList<DllValue> args,
            out DllValue result,
            out string error
        )
        {
            result = default;
            Fn? f;
            lock (_lock)
                _fns.TryGetValue(id, out f);
            if (f == null)
            {
                error = "no such function";
                return false;
            }
            if (!Ensure(f, out error))
                return false;
            var q = Request("call");
            q.Set("id", JsonValue.Of(f.ChildId));
            q.Set("types", JsonValue.Of(types));
            q.Set("args", Values(args));
            JsonValue? r = Send(q, out error);
            if (r == null)
                return false;
            result = DllValue.FromJson(r.Get("value"));
            return true;
        }

        // ---- .NET classes: an object lives in the helper that made it
        private bool Child(int id, out int childId)
        {
            lock (_lock)
            {
                if (_objects.TryGetValue(id, out var o) && Alive && o.Generation == _generation)
                {
                    childId = o.ChildId;
                    return true;
                }
                childId = -1;
                return false;
            }
        }

        private DllValue Adopt(DllValue v)
        {
            if (v.Kind != DllValue.Obj)
                return v;
            lock (_lock)
            {
                int id = _nextObject++;
                _objects[id] = (_generation, (int)v.I);
                return DllValue.OfObj(id);
            }
        }

        private List<DllValue> ToChild(IReadOnlyList<DllValue> args)
        {
            var l = new List<DllValue>();
            foreach (var v in args)
                l.Add(v.Kind == DllValue.Obj ? DllValue.OfObj(Child((int)v.I, out int c) ? c : -1) : v);
            return l;
        }

        public int ClassCreate(string path, string className, IReadOnlyList<DllValue> args, out string error)
        {
            var q = Request("classCreate");
            q.Set("path", JsonValue.Of(path));
            q.Set("class", JsonValue.Of(className));
            q.Set("args", Values(ToChild(args)));
            JsonValue? r = Send(q, out error);
            if (r == null)
                return -1;
            return (int)Adopt(DllValue.OfObj((int)(r.Get("id")?.ToNumber() ?? -1))).I;
        }

        public bool ClassIsValid(int id)
        {
            if (!Child(id, out int c))
                return false;
            var q = Request("classIsValid");
            q.Set("id", JsonValue.Of(c));
            JsonValue? r = Send(q, out _);
            return r?.Get("valid") is JsonValue v && v.Kind == JsonKind.Bool && v.Bool;
        }

        public bool ClassDispose(int id, out string error)
        {
            if (!Child(id, out int c))
            {
                error = "no such object";
                return false;
            }
            lock (_lock)
                _objects.Remove(id);
            var q = Request("classDispose");
            q.Set("id", JsonValue.Of(c));
            return Send(q, out error) != null;
        }

        public bool ClassGet(int id, string property, out DllValue value, out string error)
        {
            value = default;
            if (!Child(id, out int c))
            {
                error = "no such object";
                return false;
            }
            var q = Request("classGet");
            q.Set("id", JsonValue.Of(c));
            q.Set("name", JsonValue.Of(property));
            JsonValue? r = Send(q, out error);
            if (r == null)
                return false;
            value = Adopt(DllValue.FromJson(r.Get("value")));
            return true;
        }

        public bool ClassSet(int id, string property, DllValue value, out string error)
        {
            if (!Child(id, out int c))
            {
                error = "no such object";
                return false;
            }
            var q = Request("classSet");
            q.Set("id", JsonValue.Of(c));
            q.Set("name", JsonValue.Of(property));
            q.Set("value", ToChild(new[] { value })[0].ToJson());
            return Send(q, out error) != null;
        }

        public bool ClassCall(
            int id,
            string method,
            IReadOnlyList<DllValue> args,
            out DllValue value,
            out string error
        )
        {
            value = default;
            if (!Child(id, out int c))
            {
                error = "no such object";
                return false;
            }
            var q = Request("classCall");
            q.Set("id", JsonValue.Of(c));
            q.Set("name", JsonValue.Of(method));
            q.Set("args", Values(ToChild(args)));
            JsonValue? r = Send(q, out error);
            if (r == null)
                return false;
            value = Adopt(DllValue.FromJson(r.Get("value")));
            return true;
        }

        public void Dispose()
        {
            // the process first, outside the lock: a call that waits for a library with no time
            // limit holds the lock until the helper is gone (its pipe closes, the read ends)
            Stop();
            lock (_lock)
            {
                try
                {
                    _in?.Dispose();
                }
                catch (Exception) { } // swallow-ok: the helper's pipe is already closed
                Stop();
                _fns.Clear();
                _objects.Clear();
            }
        }
    }
}
