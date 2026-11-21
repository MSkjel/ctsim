using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>One debugger hands over to the next.</summary>
    [Collection("ctsim server")]
    public sealed class DebuggerHandoverTests
    {
        private static string Simulatr => Path.Combine(SimCorpus.Example, "Simulatr.ci");

        /// <summary>A debugger session whose OnClosed waits for the test (the old session is
        /// closing for as long as the test likes).</summary>
        private sealed class GatedClose : IDapSession
        {
            private readonly IDapSession _inner;
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);

            public GatedClose(IDapSession inner)
            {
                _inner = inner;
            }

            public void OnMessage(JsonValue message) => _inner.OnMessage(message);

            public void OnClosed()
            {
                Entered.Set();
                Gate.Wait(20000);
                _inner.OnClosed();
            }
        }

        private static JsonValue Init(ProtocolClient c) =>
            c.Request(
                "initialize",
                Args(
                    ("clientID", "test"),
                    ("adapterID", "cicode-sim"),
                    ("linesStartAt1", true),
                    ("columnsStartAt1", true)
                )
            );

        private static JsonValue Breakpoints(int line)
        {
            var bps = JsonValue.NewArray();
            bps.Add(Args(("line", line)));
            return Args(("source", Args(("path", Simulatr))), ("breakpoints", bps));
        }

        private static List<JsonValue> Events(ProtocolClient c)
        {
            lock (c.Received)
                return c.Received.Where(m => m.Get("type")?.Str == "event").ToList();
        }

        private static void Handover(bool disconnect)
        {
            string disc = Path.Combine(Path.GetTempPath(), "ctsim-handover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(disc);
            GatedClose? first = null;
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim))
            {
                server.DapFactory = ctx =>
                {
                    var s = DapSessions.Create(ctx);
                    if (first != null)
                        return s;
                    return first = new GatedClose(s);
                };
                server.Listen(disc);
                try
                {
                    // the first session: a launched call stopped on its entry
                    var a = Connect(server.Listener!.Transport, server.Listener.Address);
                    Assert.True(Init(a)["success"].Bool);
                    a.WaitEvent("initialized");
                    a.Ok("launch", Args(("function", "Temp_Profile_Sim"), ("stopOnEntry", true)));
                    a.Ok("configurationDone");
                    long oldTask = a.WaitEvent("stopped", s => s["reason"].Str == "entry")["threadId"].Long;

                    // it goes (a disconnect, or a client that vanished); the server is letting
                    // go of it while its session's OnClosed waits
                    if (disconnect)
                        a.Ok("disconnect");
                    a.Dispose();
                    Assert.True(first!.Entered.Wait(15000), "the first session's OnClosed did not run");
                    Assert.False(server.DebuggerAttached);

                    // the next session asks to attach meanwhile: it waits for the handover
                    var b = Connect(server.Listener.Transport, server.Listener.Address);
                    var init = Task.Run(() => Init(b));
                    bool early = init.Wait(500);
                    JsonValue? bp = null;
                    if (early)
                        bp = b.Ok("setBreakpoints", Breakpoints(273)); // what a client would do at once
                    first.Gate.Set();
                    Assert.True(init.Result["success"].Bool, init.Result.Dump());
                    Assert.False(early, "the new debugger attached while the old one was still closing");
                    b.WaitEvent("initialized");
                    bp ??= b.Ok("setBreakpoints", Breakpoints(273));
                    var set = bp["breakpoints"].Items.Single();
                    Assert.True(set["verified"].Bool, set.Dump());
                    Assert.Equal(274, set["line"].Long);
                    Assert.True(server.DebuggerAttached);

                    // the old call ended before the new session subscribed
                    SimTestUtil.Until(
                        sim,
                        () =>
                            sim
                                .Debugger.Tasks()
                                .All(t =>
                                    t.Id != oldTask
                                    || t.State == "Halt"
                                    || t.State == "Stop"
                                    || t.State == "Dead"
                                ),
                        what: "the old call's end"
                    );

                    // the new session's breakpoint holds
                    b.Ok("launch", Args(("function", "Temp_Profile_Sim")));
                    b.Ok("configurationDone");
                    var stop = b.WaitEvent("stopped");
                    Assert.Equal("breakpoint", stop["reason"].Str);
                    long newTask = stop["threadId"].Long;
                    Assert.NotEqual(oldTask, newTask);
                    var frame = b.Ok("stackTrace", Args(("threadId", newTask)))["stackFrames"].Items[0];
                    Assert.Equal(274, frame["line"].Long);

                    // and nothing of the old session's call reached it
                    foreach (var e in Events(b))
                    {
                        var body = e.Get("body");
                        var tid = body?.Get("threadId");
                        Assert.False(
                            tid != null && tid.Kind == JsonKind.Int && tid.Long == oldTask,
                            "an event of the old call: " + e.Dump()
                        );
                        if (e["event"].Str == "output")
                        {
                            string text = body!["output"].Str;
                            Assert.DoesNotContain("terminated by the debugger", text);
                            Assert.DoesNotContain("halted", text);
                        }
                    }
                    b.Ok("disconnect");
                    b.Dispose();
                }
                finally
                {
                    first?.Gate.Set();
                    try
                    {
                        Directory.Delete(disc, true);
                    }
                    catch (Exception) { }
                }
            }
        }

        [ProjectFact]
        public void ANewDebuggerWaitsForAClosingOneAndKeepsItsBreakpoints() => Handover(disconnect: false);

        [ProjectFact]
        public void ASessionThatDetachedOnDisconnectIsNotDetachedAgain() => Handover(disconnect: true);

        /// <summary>A sink whose Close waits first: the time a disconnect takes between its answer
        /// and the closed connection when the simulation is busy (the kill and the detach run on
        /// its thread).</summary>
        private sealed class SlowClose : CicodeSim.Host.IMessageSink
        {
            private readonly CicodeSim.Host.IMessageSink _inner;
            public readonly ManualResetEventSlim Closing = new ManualResetEventSlim(false);

            public SlowClose(CicodeSim.Host.IMessageSink inner)
            {
                _inner = inner;
            }

            public void Send(JsonValue message) => _inner.Send(message);

            public void Close()
            {
                Closing.Set();
                Thread.Sleep(600);
                _inner.Close();
            }

            public string Name => _inner.Name;
            public bool IsClosed => _inner.IsClosed;
        }

        [ProjectFact]
        public void ANewDebuggerOnTheDisconnectAnswerWaitsForTheHandover()
        {
            // a client that restarts on the disconnect's answer sends its initialize while the old
            // session still detaches and closes
            string disc = Path.Combine(Path.GetTempPath(), "ctsim-handover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(disc);
            SlowClose? first = null;
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim))
            {
                server.DapFactory = ctx =>
                {
                    if (first != null)
                        return DapSessions.Create(ctx);
                    first = new SlowClose(ctx.Sink);
                    return DapSessions.Create(new DapContext(ctx.Simulation, first, ctx.Server, ctx.Stdio));
                };
                server.Listen(disc);
                try
                {
                    var a = Connect(server.Listener!.Transport, server.Listener.Address);
                    Assert.True(Init(a)["success"].Bool);
                    a.WaitEvent("initialized");
                    a.Ok("launch", Args(("function", "Temp_Profile_Sim"), ("stopOnEntry", true)));
                    a.Ok("configurationDone");
                    long oldTask = a.WaitEvent("stopped", s => s["reason"].Str == "entry")["threadId"].Long;
                    a.Ok("disconnect");
                    Assert.False(server.DebuggerAttached);

                    // the restart: connected on the answer, before the old connection is closed
                    var b = Connect(server.Listener.Transport, server.Listener.Address);
                    var init = Init(b);
                    Assert.True(first!.Closing.IsSet);
                    a.Dispose();
                    Assert.True(init["success"].Bool, init.Dump());
                    b.WaitEvent("initialized");
                    var set = b.Ok("setBreakpoints", Breakpoints(273))["breakpoints"].Items.Single();
                    Assert.True(set["verified"].Bool, set.Dump());
                    Assert.True(server.DebuggerAttached);
                    b.Ok("launch", Args(("function", "Temp_Profile_Sim")));
                    b.Ok("configurationDone");
                    var stop = b.WaitEvent("stopped");
                    Assert.Equal("breakpoint", stop["reason"].Str);
                    long newTask = stop["threadId"].Long;
                    Assert.NotEqual(oldTask, newTask);
                    Assert.Equal(
                        274,
                        b.Ok("stackTrace", Args(("threadId", newTask)))["stackFrames"].Items[0]["line"].Long
                    );
                    foreach (var e in Events(b))
                    {
                        var tid = e.Get("body")?.Get("threadId");
                        Assert.False(
                            tid != null && tid.Kind == JsonKind.Int && tid.Long == oldTask,
                            "an event of the old call: " + e.Dump()
                        );
                    }
                    b.Ok("disconnect");
                    b.Dispose();
                }
                finally
                {
                    try
                    {
                        Directory.Delete(disc, true);
                    }
                    catch (Exception) { }
                }
            }
        }

        [ProjectFact]
        public void AConnectionThatClosesWhileItsSessionIsMadeIsLetGo()
        {
            // OnClosed can run before its session is made: that session must not keep the target
            // attached (a stopped task with nobody to continue it), and a debugger that takes over
            // in between keeps its breakpoints
            string disc = Path.Combine(Path.GetTempPath(), "ctsim-handover-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(disc);
            int made = 0;
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim))
            {
                server.DapFactory = ctx =>
                {
                    if (Interlocked.Increment(ref made) == 1)
                        ((MessageConnection)ctx.Sink).Dispose(); // Closed runs here, on this thread
                    return DapSessions.Create(ctx);
                };
                server.Listen(disc);
                try
                {
                    var a = Connect(server.Listener!.Transport, server.Listener.Address);
                    a.Send(
                        Args(
                            ("seq", 1),
                            ("type", "request"),
                            ("command", "initialize"),
                            ("arguments", Args(("clientID", "test")))
                        )
                    );
                    SimTestUtil.Until(
                        sim,
                        () => Volatile.Read(ref made) == 1 && !sim.Debugger.Attached,
                        what: "the target detached"
                    );
                    Assert.False(server.DebuggerAttached);
                    a.Dispose();

                    var b = Connect(server.Listener.Transport, server.Listener.Address);
                    Assert.True(Init(b)["success"].Bool);
                    b.WaitEvent("initialized");
                    Assert.True(server.DebuggerAttached);
                    var set = b.Ok("setBreakpoints", Breakpoints(273))["breakpoints"].Items.Single();
                    Assert.True(set["verified"].Bool, set.Dump());
                    b.Ok("launch", Args(("function", "Temp_Profile_Sim")));
                    b.Ok("configurationDone");
                    Assert.Equal("breakpoint", b.WaitEvent("stopped")["reason"].Str);
                    b.Ok("disconnect");
                    b.Dispose();
                }
                finally
                {
                    try
                    {
                        Directory.Delete(disc, true);
                    }
                    catch (Exception) { }
                }
            }
        }

        [ProjectFact]
        public void DetachSessionSkipsWhenADetachRanSinceTheAttach()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var d = sim.Debugger;
                long at = d.AttachSession();
                Assert.True(d.Attached);
                // the session detaches itself (its disconnect), an in-process host attaches after it
                d.Detach();
                var bp = d.SetBreakpoints(Simulatr, new[] { new BreakpointRequest { Line = 273 } }).Single();
                Assert.True(bp.Verified, bp.Message);
                Assert.False(d.DetachSession(at));
                Assert.True(d.Attached);
                var ev = new SimEvents(sim);
                var task = sim.Invoke(() => sim.StartCall("Temp_Profile_Sim()", null));
                var stop = ev.Wait<StoppedEventArgs>();
                Assert.Equal(StopReason.Breakpoint, stop.Reason);
                Assert.Equal(task.Handle, stop.Task);
                // a session that did not detach is detached when its connection closes
                long again = d.AttachSession();
                Assert.True(d.DetachSession(again));
                Assert.False(d.Attached);
                Assert.False(d.DetachSession(again));
            }
        }
    }
}
