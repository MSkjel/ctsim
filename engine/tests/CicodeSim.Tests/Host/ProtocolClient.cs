using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using CicodeSim.Host;
using CicodeSim.Values;

namespace CicodeSim.Tests.Host
{
    /// <summary>A test client of the ctsim/* protocol over a stream pair (a pipe, or ctsim dap's
    /// stdio): DAP framing, requests with their responses, events queued for waiting on.</summary>
    public sealed class ProtocolClient : IDisposable
    {
        private readonly Stream _in;
        private readonly Stream _out;
        private readonly object _write = new object();
        private readonly Thread _reader;
        private readonly ConcurrentDictionary<long, BlockingCollection<JsonValue>> _pending =
            new ConcurrentDictionary<long, BlockingCollection<JsonValue>>();
        private readonly BlockingCollection<JsonValue> _events = new BlockingCollection<JsonValue>();
        public readonly List<JsonValue> Received = new List<JsonValue>();
        private long _seq;
        public string? Ended;

        public ProtocolClient(Stream input, Stream output)
        {
            _in = input;
            _out = output;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "test-client" };
            _reader.Start();
        }

        public static ProtocolClient Connect(string pipe) => Connect(Listener.Pipe, pipe);

        public static ProtocolClient Connect(string transport, string address)
        {
            var s = Listener.Connect(transport, address, 10000);
            return new ProtocolClient(s, s);
        }

        private void ReadLoop()
        {
            try
            {
                var r = new DapReader(_in);
                while (true)
                {
                    string? body = r.Read();
                    if (body == null)
                        break;
                    var msg = JsonValue.Parse(body);
                    lock (Received)
                        Received.Add(msg);
                    string type = msg.Get("type")?.Str ?? "";
                    if (type == "response")
                    {
                        long rs = msg.Get("request_seq")?.Long ?? -1;
                        if (_pending.TryGetValue(rs, out var q))
                            q.Add(msg);
                    }
                    else if (type == "event")
                        _events.Add(msg);
                }
                Ended = "end of stream";
            }
            catch (Exception e)
            {
                Ended = e.GetType().Name + ": " + e.Message;
            }
        }

        public void Send(JsonValue msg)
        {
            lock (_write)
            {
                byte[] f = DapFraming.Encode(msg);
                _out.Write(f, 0, f.Length);
                try
                {
                    _out.Flush();
                }
                catch (IOException) when (_out is System.IO.Pipes.PipeStream)
                {
                    // a pipe write is not buffered: Flush only checks the pipe's state, and the
                    // server may already have answered and closed it (ctsim/shutdown on a loaded
                    // machine: 'Pipe is broken' here after the request was sent)
                }
            }
        }

        /// <summary>Send a request and wait for its response (the whole response message).</summary>
        public JsonValue Request(string command, JsonValue? args = null, int timeoutMs = 20000)
        {
            long seq = Interlocked.Increment(ref _seq);
            var q = new BlockingCollection<JsonValue>();
            _pending[seq] = q;
            var m = JsonValue.NewObject();
            m.Set("seq", JsonValue.Of(seq));
            m.Set("type", "request");
            m.Set("command", command);
            m.Set("arguments", args ?? JsonValue.NewObject());
            Send(m);
            try
            {
                if (!q.TryTake(out var resp, timeoutMs))
                    throw new TimeoutException(
                        "no response to "
                            + command
                            + " in "
                            + timeoutMs
                            + " ms"
                            + (Ended != null ? " (" + Ended + ")" : "")
                    );
                return resp;
            }
            finally
            {
                _pending.TryRemove(seq, out _);
            }
        }

        /// <summary>A request that must succeed: its body.</summary>
        public JsonValue Ok(string command, JsonValue? args = null, int timeoutMs = 20000)
        {
            var r = Request(command, args, timeoutMs);
            if (!r["success"].Bool)
                throw new InvalidOperationException(
                    command + " failed: " + (r.Get("message")?.Str ?? "") + " " + r.Dump()
                );
            return r["body"];
        }

        /// <summary>The next event named <paramref name="name"/> that matches (others are skipped).</summary>
        public JsonValue WaitEvent(string name, Func<JsonValue, bool>? match = null, int timeoutMs = 20000)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (left <= 0 || !_events.TryTake(out var e, left))
                    throw new TimeoutException(
                        "no "
                            + name
                            + " event in "
                            + timeoutMs
                            + " ms"
                            + (Ended != null ? " (" + Ended + ")" : "")
                    );
                if (e["event"].Str == name && (match == null || match(e["body"])))
                    return e["body"];
            }
        }

        public static JsonValue Args(params (string Key, object? Value)[] kv)
        {
            var o = JsonValue.NewObject();
            foreach (var (k, v) in kv)
                o.Set(k, v is JsonValue j ? j : JsonValue.From(v));
            return o;
        }

        public static JsonValue Names(params string[] names)
        {
            var a = JsonValue.NewArray();
            foreach (var n in names)
                a.Add(JsonValue.Of(n));
            return a;
        }

        public void Dispose()
        {
            try
            {
                _in.Dispose();
            }
            catch (Exception) { }
            if (!ReferenceEquals(_in, _out))
                try
                {
                    _out.Dispose();
                }
                catch (Exception) { }
        }
    }

    /// <summary>The built ctsim.exe (next to the test assembly) started as a server.</summary>
    public sealed class ServerProcess : IDisposable
    {
        public readonly Process Process;
        public readonly JsonValue Ready;
        public readonly StringBuilder Stderr = new StringBuilder();
        public string Pipe => Ready["pipe"].Str;
        public string Id => Ready["id"].Str;

        public static string Exe => Path.Combine(AppContext.BaseDirectory, "ctsim.exe");

        public ServerProcess(string arguments, int timeoutMs = 60000)
        {
            var psi = new ProcessStartInfo(Exe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            Process = Process.Start(psi) ?? throw new InvalidOperationException("ctsim.exe did not start");
            Process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    lock (Stderr)
                        Stderr.AppendLine(e.Data);
            };
            Process.BeginErrorReadLine();
            var line = Process.StandardOutput.ReadLineAsync();
            if (!line.Wait(timeoutMs) || line.Result == null)
            {
                try
                {
                    Process.Kill();
                }
                catch (Exception) { }
                throw new TimeoutException("ctsim.exe printed no ready line: " + Stderr);
            }
            Ready = JsonValue.Parse(line.Result);
        }

        public static string Quote(string s) => "\"" + s.TrimEnd('\\') + "\"";

        /// <summary>The working set of the server process (MB) and its total processor time.</summary>
        public (double Mb, double PeakMb, double CpuSeconds) Usage()
        {
            Process.Refresh();
            return (
                Process.WorkingSet64 / 1048576.0,
                Process.PeakWorkingSet64 / 1048576.0,
                Process.TotalProcessorTime.TotalSeconds
            );
        }

        public bool WaitExit(int ms) => Process.WaitForExit(ms);

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill();
                    Process.WaitForExit(5000);
                }
            }
            catch (Exception) { }
            Process.Dispose();
        }
    }
}
