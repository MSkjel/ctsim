using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>Where a session writes its messages: thread-safe, sent in call order.</summary>
    public interface IMessageSink
    {
        /// <summary>Queue a message. Its "seq" is set here.</summary>
        void Send(JsonValue message);

        /// <summary>Close the connection (queued messages are written first).</summary>
        void Close();

        string Name { get; }

        bool IsClosed { get; }
    }

    /// <summary>
    /// One client connection: DAP frames over a stream pair, with reader and writer threads.
    /// </summary>
    public sealed class MessageConnection : IMessageSink, IDisposable
    {
        private readonly Stream _in;
        private readonly Stream _out;
        private readonly BlockingCollection<byte[]> _queue = new BlockingCollection<byte[]>();
        private readonly object _sendLock = new object();
        private long _queuedBytes;
        private long _seq;
        private Thread? _reader,
            _writer;
        private int _closed;
        private int _finished;
        private string? _closeReason;
        private Timer? _drainTimer;
        private readonly ManualResetEventSlim _finishedEvent = new ManualResetEventSlim(false);

        /// <summary>Outbound bytes allowed to wait for a slow client.</summary>
        public long MaxQueuedBytes { get; set; } = 256L << 20;

        /// <summary>The largest message a client may send.</summary>
        public int MaxInboundBody { get; set; } = 16 << 20;

        /// <summary>How long Close waits for the queued messages to be written before it drops
        /// the connection anyway (a client that stopped reading).</summary>
        public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public string Name { get; }

        /// <summary>A message arrived (raised on the reader thread, in arrival order).</summary>
        public event Action<MessageConnection, JsonValue>? Message;

        /// <summary>The connection closed (once; the reason, null for a normal end).</summary>
        public event Action<MessageConnection, string?>? Closed;

        public MessageConnection(Stream input, Stream output, string name)
        {
            _in = input;
            _out = output;
            Name = name;
        }

        public MessageConnection(Stream duplex, string name)
            : this(duplex, duplex, name) { }

        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        /// <summary>Wait until the connection has ended and its queued messages are written.</summary>
        public bool WaitFinished(int timeoutMs) => _finishedEvent.Wait(timeoutMs);

        public void Start()
        {
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "ctsim-write " + Name };
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ctsim-read " + Name };
            _writer.Start();
            _reader.Start();
        }

        public void Send(JsonValue message)
        {
            if (message.Kind != JsonKind.Object)
                throw new ArgumentException(
                    "a DAP message is a JSON object, not " + message.Kind,
                    nameof(message)
                );
            if (IsClosed)
                return;
            string? overflow = null;
            lock (_sendLock)
            {
                if (IsClosed)
                    return;
                byte[] frame = DapFraming.Encode(WithSeq(message, ++_seq));
                if (Interlocked.Add(ref _queuedBytes, frame.Length) > MaxQueuedBytes)
                    overflow =
                        "the client does not read its messages (over "
                        + MaxQueuedBytes.ToString(CultureInfo.InvariantCulture)
                        + " bytes queued)";
                else
                {
                    try
                    {
                        _queue.Add(frame);
                    }
                    catch (InvalidOperationException)
                    {
                        // completed (or disposed): the connection is closing
                    }
                }
            }
            // outside the lock: Shutdown may raise Closed, whose handlers may take their own locks
            if (overflow != null)
                Shutdown(overflow);
        }

        /// <summary>The message's JSON with "seq" first (the message itself is not changed: one
        /// event object can go to many connections at once).</summary>
        internal static string WithSeq(JsonValue message, long seq)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"seq\":").Append(seq.ToString(CultureInfo.InvariantCulture));
            foreach (var kv in message.Pairs)
            {
                if (kv.Key == "seq")
                    continue;
                sb.Append(',');
                JsonValue.WriteJsonString(sb, kv.Key);
                sb.Append(':');
                kv.Value.WriteStrict(sb); // RFC 8259: no bare NaN / Infinity on the wire
            }
            return sb.Append('}').ToString();
        }

        public void Close() => Shutdown(null, drain: true);

        public void Dispose() => Shutdown(null);

        private void SendLog(string text)
        {
            var ev = JsonValue.NewObject();
            ev.Set("type", "event");
            ev.Set("event", "ctsim/log");
            var b = JsonValue.NewObject();
            b.Set("level", "error");
            b.Set("text", text);
            b.Set("kind", "internal");
            ev.Set("body", b);
            Send(ev);
        }

        /// <summary>The DAP error response to <paramref name="request"/>.</summary>
        private void SendFailure(JsonValue request, string text)
        {
            var r = JsonValue.NewObject();
            r.Set("type", "response");
            var seq = request.Get("seq");
            r.Set("request_seq", seq != null && seq.Kind == JsonKind.Int ? seq : JsonValue.Of(0));
            r.Set("success", false);
            r.Set("command", request.Get("command")?.Str ?? "");
            r.Set("message", text);
            Send(r);
        }

        private void ReadLoop()
        {
            string? reason = null;
            try
            {
                var reader = new DapReader(_in, MaxInboundBody);
                while (true)
                {
                    string? body = reader.Read();
                    if (body == null || IsClosed)
                        break;
                    JsonValue msg;
                    try
                    {
                        msg = JsonValue.Parse(body);
                    }
                    catch (FormatException e)
                    {
                        // a frame with bad JSON: tell the client, read on (the framing is intact)
                        SendLog("invalid message: " + e.Message);
                        continue;
                    }
                    if (msg.Kind != JsonKind.Object)
                    {
                        SendLog("a message must be a JSON object, not a JSON " + msg.KindName);
                        continue;
                    }
                    try
                    {
                        Message?.Invoke(this, msg);
                    }
                    catch (Exception e)
                    {
                        // a handler bug ends that message, not the connection or the process;
                        // a request gets its failed response (DAP: success false), so the
                        // client does not wait for one forever
                        string text =
                            "the server failed on a message: " + e.GetType().Name + ": " + e.Message;
                        if (msg.Get("type")?.Str == "request")
                            SendFailure(msg, text);
                        SendLog(text);
                    }
                }
            }
            catch (FramingException e)
            {
                reason = e.Message;
            }
            catch (IOException e)
            {
                reason = IsClosed ? null : e.Message;
            }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException)
            {
                // a pending overlapped read when the stream was disposed
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + ": " + e.Message;
            }
            Shutdown(reason, drain: true);
        }

        private void WriteLoop()
        {
            try
            {
                foreach (var frame in _queue.GetConsumingEnumerable())
                {
                    _out.Write(frame, 0, frame.Length);
                    Interlocked.Add(ref _queuedBytes, -frame.Length);
                    if (_queue.Count == 0)
                        _out.Flush();
                }
                _out.Flush();
            }
            catch (Exception)
            {
                // IOException (the client left), ObjectDisposedException, and on an overlapped
                // pipe OperationCanceledException: Dispose cancels a write the client does not
                // take (net48 and net10.0 both); any of them ends the connection
            }
            Finish();
        }

        private void Shutdown(string? reason, bool drain = false)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            _closeReason = reason;
            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException) { }
            if (!drain || _writer == null)
            {
                Finish();
                return;
            }
            // the writer drains the queue and calls Finish; a client that does not read gets
            // DrainTimeout, then the streams are disposed (which also ends a blocked write)
            var t = new Timer(_ => Finish(), null, Timeout.Infinite, Timeout.Infinite);
            Interlocked.Exchange(ref _drainTimer, t);
            if (Volatile.Read(ref _finished) != 0)
                t.Dispose();
            else
                t.Change(DrainTimeout, Timeout.InfiniteTimeSpan);
        }

        private void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
                return;
            Volatile.Write(ref _closed, 1);
            try
            {
                _queue.CompleteAdding();
            }
            catch (Exception) { }
            Interlocked.Exchange(ref _drainTimer, null)?.Dispose();
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
            _finishedEvent.Set(); // before Closed: a handler may wait for this connection
            try
            {
                Closed?.Invoke(this, _closeReason);
            }
            catch (Exception)
            {
                // a handler bug must not end the thread (and with it the process)
            }
        }
    }
}
