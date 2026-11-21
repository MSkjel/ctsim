using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>Connections and listeners: several clients on one pipe, message order, a
    /// failing handler, closing, the pipe's access list.</summary>
    public class TransportTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private static string NewId() => "test." + Guid.NewGuid().ToString("N");

        private static JsonValue Msg(string type, string key, object? value)
        {
            var m = JsonValue.NewObject();
            m.Set("type", type);
            m.Set(key, value);
            return m;
        }

        /// <summary>A server connection per accepted client, collected.</summary>
        private sealed class Server : IDisposable
        {
            public readonly Listener L;
            public readonly BlockingCollection<MessageConnection> Accepted =
                new BlockingCollection<MessageConnection>();
            public Action<MessageConnection, JsonValue>? OnMessage;

            public Server(Listener l)
            {
                L = l;
                l.Start(
                    (stream, name) =>
                    {
                        var c = new MessageConnection(stream, name);
                        c.Message += (cc, m) => OnMessage?.Invoke(cc, m);
                        c.Start();
                        Accepted.Add(c);
                    }
                );
            }

            public MessageConnection Next()
            {
                Assert.True(Accepted.TryTake(out var c, Wait), "no client accepted");
                return c!;
            }

            public void Dispose()
            {
                L.Dispose();
                foreach (var c in Accepted)
                    c.Dispose();
            }
        }

        private static JsonValue ReadMsg(DapReader r)
        {
            string? b = r.Read();
            Assert.NotNull(b);
            return JsonValue.Parse(b!);
        }

        private static void Write(Stream s, string json)
        {
            byte[] f = DapFraming.Encode(json);
            s.Write(f, 0, f.Length);
            s.Flush();
        }

        [Fact]
        public void SeveralClientsOnOnePipeEachGetTheirOwnAnswers()
        {
            using var srv = new Server(new PipeListener("ctsim." + NewId()));
            srv.OnMessage = (c, m) => c.Send(Msg("response", "echo", m["n"].Long * 10));
            var clients = new List<Stream>();
            var conns = new List<MessageConnection>();
            for (int i = 0; i < 4; i++)
            {
                clients.Add(Listener.Connect(srv.L.Transport, srv.L.Address));
                conns.Add(srv.Next());
            }
            Assert.Equal(4, conns.Select(c => c.Name).Distinct().Count());
            for (int round = 0; round < 3; round++)
            for (int i = 0; i < clients.Count; i++)
                Write(
                    clients[i],
                    "{\"type\":\"request\",\"n\":"
                        + (i * 100 + round).ToString(CultureInfo.InvariantCulture)
                        + "}"
                );
            for (int i = 0; i < clients.Count; i++)
            {
                var r = new DapReader(clients[i]);
                for (int round = 0; round < 3; round++)
                {
                    var m = ReadMsg(r);
                    Assert.Equal((i * 100 + round) * 10L, m["echo"].Long);
                    Assert.Equal(round + 1L, m["seq"].Long);
                    Assert.Equal("seq", m.Pairs[0].Key);
                }
            }
            foreach (var c in clients)
                c.Dispose();
        }

        [Fact]
        public void SeqFollowsSendOrderAcrossThreads()
        {
            using var srv = new Server(new PipeListener("ctsim." + NewId()));
            using var client = Listener.Connect(srv.L.Transport, srv.L.Address);
            var conn = srv.Next();
            const int perThread = 500,
                threads = 4;
            var ts = Enumerable
                .Range(0, threads)
                .Select(t => new Thread(() =>
                {
                    for (int i = 0; i < perThread; i++)
                        conn.Send(Msg("event", "k", t * 100000 + i));
                }))
                .ToList();
            ts.ForEach(t => t.Start());
            ts.ForEach(t => t.Join());
            var r = new DapReader(client);
            var lastPerThread = new long[threads];
            for (int i = 0; i < threads; i++)
                lastPerThread[i] = -1;
            for (int n = 1; n <= perThread * threads; n++)
            {
                var m = ReadMsg(r);
                Assert.Equal((long)n, m["seq"].Long);
                long k = m["k"].Long;
                int t = (int)(k / 100000);
                Assert.True(k % 100000 > lastPerThread[t], "a thread's messages out of order");
                lastPerThread[t] = k % 100000;
            }
        }

        [Fact]
        public void AHandlerThatThrowsDoesNotEndTheConnectionOrTheProcess()
        {
            // an exception escaping the reader thread terminates a .NET process (both targets)
            using var srv = new Server(new PipeListener("ctsim." + NewId()));
            srv.OnMessage = (c, m) =>
            {
                if (m["n"].Long == 1)
                    throw new InvalidOperationException("handler bug");
                c.Send(Msg("response", "ok", m["n"].Long));
            };
            using var client = Listener.Connect(srv.L.Transport, srv.L.Address);
            var conn = srv.Next();
            Write(client, "{\"seq\":7,\"type\":\"request\",\"command\":\"ctsim/call\",\"n\":1}");
            Write(client, "{\"type\":\"request\",\"n\":2}");
            var r = new DapReader(client);
            // the request gets its failed response, then the log event
            var fail = ReadMsg(r);
            Assert.Equal("response", fail["type"].Str);
            Assert.Equal(7L, fail["request_seq"].Long);
            Assert.False(fail["success"].Bool);
            Assert.Equal("ctsim/call", fail["command"].Str);
            Assert.Contains("handler bug", fail["message"].Str);
            var log = ReadMsg(r);
            Assert.Equal("ctsim/log", log["event"].Str);
            Assert.Equal("error", log["body"]["level"].Str);
            Assert.Contains("handler bug", log["body"]["text"].Str);
            Assert.Equal(2L, ReadMsg(r)["ok"].Long);
            Assert.False(conn.IsClosed);
        }

        [Fact]
        public void BadJsonAndNonObjectMessagesAreReportedNotDispatched()
        {
            using var srv = new Server(new PipeListener("ctsim." + NewId()));
            var got = new BlockingCollection<JsonValue>();
            srv.OnMessage = (c, m) => got.Add(m);
            using var client = Listener.Connect(srv.L.Transport, srv.L.Address);
            srv.Next();
            Write(client, "{\"type\":");
            Write(client, "[1,2]");
            Write(client, "42");
            Write(client, "{\"type\":\"request\",\"n\":3}");
            var r = new DapReader(client);
            for (int i = 0; i < 3; i++)
            {
                var log = ReadMsg(r);
                Assert.Equal("ctsim/log", log["event"].Str);
                Assert.Equal((long)(i + 1), log["seq"].Long);
            }
            Assert.True(got.TryTake(out var m, Wait));
            Assert.Equal(3L, m!["n"].Long);
            Assert.False(got.TryTake(out _, 200));
        }

        [Fact]
        public void CloseWritesQueuedMessagesFirst()
        {
            using var srv = new Server(new PipeListener("ctsim." + NewId()));
            using var client = Listener.Connect(srv.L.Transport, srv.L.Address);
            var conn = srv.Next();
            // the order is the point here, not the drain time: a loaded machine (the whole suite
            // on both targets, a virus scanner) can keep the reader from its first read for the
            // default 5 s, after which Close drops the connection
            conn.DrainTimeout = TimeSpan.FromSeconds(60);
            var closed = new ManualResetEventSlim();
            string? reason = "unset";
            conn.Closed += (c, why) =>
            {
                reason = why;
                closed.Set();
            };
            for (int i = 0; i < 200; i++)
                conn.Send(Msg("event", "i", i));
            conn.Close();
            conn.Send(Msg("event", "i", 999)); // after Close: dropped
            var r = new DapReader(client);
            for (int i = 0; i < 200; i++)
                Assert.Equal((long)i, ReadMsg(r)["i"].Long);
            Assert.Null(r.Read());
            Assert.True(closed.Wait(Wait));
            Assert.Null(reason);
        }

        [Fact]
        public void CloseFinishesEvenWhenTheClientNeverReads()
        {
            // a client that stops reading must not keep the connection's threads and handles
            // forever: Close drains for DrainTimeout, then drops the connection
            using var srv = new Server(new PipeListener("ctsim." + NewId()));
            using var client = Listener.Connect(srv.L.Transport, srv.L.Address);
            var conn = srv.Next();
            conn.DrainTimeout = TimeSpan.FromMilliseconds(300);
            var closed = new ManualResetEventSlim();
            conn.Closed += (c, why) => closed.Set();
            string big = new string('x', 1 << 20);
            for (int i = 0; i < 8; i++)
                conn.Send(Msg("event", "blob", big)); // far more than the pipe buffer
            conn.Close();
            // the drain timer is a thread-pool timer: on a loaded machine it can fire seconds late
            Assert.True(closed.Wait(TimeSpan.FromSeconds(60)), "Close never finished");
        }

        [Fact]
        public void ASlowClientIsDroppedAndClosedRunsOutsideTheSendLock()
        {
            using var srv = new Server(new PipeListener("ctsim." + NewId()));
            using var client = Listener.Connect(srv.L.Transport, srv.L.Address);
            var conn = srv.Next();
            conn.MaxQueuedBytes = 4 << 20;
            // a Closed handler that takes the registry lock another sender holds while sending:
            // Closed is not raised under the connection's send lock
            var registry = new object();
            var closed = new ManualResetEventSlim();
            string? reason = null;
            conn.Closed += (c, why) =>
            {
                lock (registry)
                {
                    reason = why;
                }
                closed.Set();
            };
            var holder = new Thread(() =>
            {
                lock (registry)
                {
                    Thread.Sleep(300);
                    conn.Send(Msg("event", "late", 1));
                }
            });
            string big = new string('y', 1 << 20);
            holder.Start();
            var flood = new Thread(() =>
            {
                for (int i = 0; i < 64 && !conn.IsClosed; i++)
                    conn.Send(Msg("event", "blob", big));
            });
            flood.Start();
            Assert.True(flood.Join(Wait), "the sender hung");
            Assert.True(holder.Join(Wait), "deadlock between Closed and Send");
            Assert.True(closed.Wait(Wait));
            Assert.Contains("does not read", reason);
        }

        [Fact]
        public void TheListenerSurvivesAFailingClientCallback()
        {
            var id = "ctsim." + NewId();
            using var l = new PipeListener(id);
            var errors = new BlockingCollection<Exception>();
            l.Error += e => errors.Add(e);
            int n = 0;
            var ok = new BlockingCollection<Stream>();
            l.Start(
                (s, name) =>
                {
                    if (Interlocked.Increment(ref n) == 1)
                        throw new InvalidOperationException("callback bug");
                    ok.Add(s);
                }
            );
            // kept open until the server saw it: a client that leaves before the accept thread
            // reaches WaitForConnection is dropped quietly (IOException), never handed on
            using (var c1 = Listener.Connect(l.Transport, l.Address))
            {
                Assert.True(errors.TryTake(out var e, Wait));
                Assert.Contains("callback bug", e!.Message);
            }
            using var c2 = Listener.Connect(l.Transport, l.Address);
            Assert.True(ok.TryTake(out var s2, Wait));
            s2!.Dispose();
        }

        [Fact]
        public void APipeNameInUseIsRefusedAndDisposeStopsListening()
        {
            var id = "ctsim." + NewId();
            var l = new PipeListener(id);
            l.Start((s, name) => s.Dispose());
            Assert.ThrowsAny<IOException>(() => new PipeListener(id).Dispose());
            l.Dispose();
            l.Dispose();
            Assert.ThrowsAny<Exception>(() =>
                Listener.Connect(Listener.Pipe, @"\\.\pipe\" + id, 300).Dispose()
            );
            // the name is free again
            using var again = new PipeListener(id);
        }

        [Fact]
        public void ThePipeIsOnlyOpenToTheCurrentUser()
        {
            if (!Listener.IsWindows)
                return;
            using var l = new PipeListener("ctsim." + NewId());
            var sids = l.AllowedSids();
#pragma warning disable CA1416 // Windows only (checked above)
            string me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
#pragma warning restore CA1416
            // the default access list also lets Everyone and Anonymous read the pipe
            Assert.Equal(new[] { me }, sids);
        }

        [Fact]
        public void TcpFallbackServesSeveralClients()
        {
            using var srv = new Server(new TcpListenerHost());
            Assert.StartsWith("127.0.0.1:", srv.L.Address);
            srv.OnMessage = (c, m) => c.Send(Msg("response", "n", m["n"].Long));
            using var a = Listener.Connect(srv.L.Transport, srv.L.Address);
            using var b = Listener.Connect(srv.L.Transport, srv.L.Address);
            srv.Next();
            srv.Next();
            Write(a, "{\"n\":1}");
            Write(b, "{\"n\":2}");
            Assert.Equal(1L, ReadMsg(new DapReader(a))["n"].Long);
            Assert.Equal(2L, ReadMsg(new DapReader(b))["n"].Long);
        }

        [Fact]
        public void AnInboundMessageOverTheLimitClosesTheConnection()
        {
            var accepted = new BlockingCollection<MessageConnection>();
            var reasons = new BlockingCollection<string?>();
            var got = new BlockingCollection<JsonValue>();
            using var l = new PipeListener("ctsim." + NewId());
            l.Start(
                (s, name) =>
                {
                    var c = new MessageConnection(s, name) { MaxInboundBody = 1 << 20 };
                    c.Message += (cc, m) => got.Add(m);
                    c.Closed += (cc, why) => reasons.Add(why);
                    c.Start();
                    accepted.Add(c);
                }
            );
            using var client = Listener.Connect(l.Transport, l.Address);
            Assert.True(accepted.TryTake(out var conn, Wait));
            Write(client, "{\"s\":\"" + new string('a', (1 << 20) - 8) + "\"}"); // exactly 1 MB: fine
            Assert.True(got.TryTake(out var m, Wait));
            Assert.Equal((1 << 20) - 8, m!["s"].Str.Length);
            byte[] head = System.Text.Encoding.ASCII.GetBytes("Content-Length: 1048577\r\n\r\n");
            client.Write(head, 0, head.Length);
            client.Flush();
            Assert.True(reasons.TryTake(out var why, Wait));
            Assert.Contains("over the limit", why);
            Assert.True(conn!.IsClosed);
            Assert.Equal(16 << 20, new MessageConnection(new MemoryStream(), "x").MaxInboundBody);
        }

        [Fact]
        public void SendRefusesANonObject()
        {
            using var srv = new Server(new PipeListener("ctsim." + NewId()));
            using var client = Listener.Connect(srv.L.Transport, srv.L.Address);
            var conn = srv.Next();
            Assert.Throws<ArgumentException>(() => conn.Send(JsonValue.Parse("[1]")));
        }
    }
}
