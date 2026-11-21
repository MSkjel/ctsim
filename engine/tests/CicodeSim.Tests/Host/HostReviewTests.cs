using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>Inputs that could hang or crash the server or break a client, one test
    /// each.</summary>
    [Collection("ctsim server")]
    public sealed class HostReviewTests
    {
        // ------------------------------------------------------------------ no server needed
        [Fact]
        public void TheTagFilterWildcardMatchesLikeARegexAndNeverBacktracksExponentially()
        {
            var rnd = new Random(7);
            const string abc = "ab*?";
            for (int n = 0; n < 4000; n++)
            {
                string p = new string(
                    Enumerable.Range(0, rnd.Next(0, 7)).Select(_ => abc[rnd.Next(abc.Length)]).ToArray()
                );
                string s = new string(
                    Enumerable.Range(0, rnd.Next(0, 9)).Select(_ => "ab"[rnd.Next(2)]).ToArray()
                );
                bool want = Regex.IsMatch(
                    s,
                    "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                    RegexOptions.Singleline
                );
                Assert.True(
                    want == TagListing.Filter.Wild(p, 0, s, 0),
                    "pattern '" + p + "' text '" + s + "'"
                );
            }
            // a pattern that would take exponential time to backtrack
            var sw = Stopwatch.StartNew();
            string name = new string('a', 60);
            Assert.False(TagListing.Filter.Wild("*a*a*a*a*a*a*a*a*a*a*a*a*b", 0, name, 0));
            Assert.True(sw.ElapsedMilliseconds < 1000, sw.ElapsedMilliseconds + " ms");
        }

        [ProjectFact]
        public void ACompiledPageOfAnotherVersionOfThePageIsNotRun()
        {
            // a restored project's .RDB can be newer than its .ctg and still be of another
            // version of the page: the engine runs the model's rows when the objects differ
            string rdb = Path.Combine(SimCorpus.Example, "Mixer.RDB");
            var cp = CicodeSim.Page.CompiledPage.Load(rdb);
            var model = new CicodeSim.Page.PageModel();
            foreach (var kv in cp.Tables)
            {
                if (!kv.Key.StartsWith("PG", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rows = new List<Dictionary<string, string>>();
                foreach (var r in kv.Value)
                    if (r.TryGetValue("AN", out var an) && an != null)
                        rows.Add(
                            new Dictionary<string, string>
                            {
                                ["AN"] = Convert.ToString(
                                    an,
                                    System.Globalization.CultureInfo.InvariantCulture
                                )!,
                            }
                        );
                model.Tables[kv.Key.ToLowerInvariant()] = rows;
            }
            var notes = new List<string>();
            Assert.True(SimServer.RdbMatches(rdb, model, notes));
            Assert.Empty(notes);
            // one object less (the page was edited since that compile): not run
            var t = model.Tables.First(kv => kv.Value.Count > 1);
            t.Value.RemoveAt(0);
            Assert.False(SimServer.RdbMatches(rdb, model, notes));
            Assert.Contains("another version of the page", notes.Single());
            // an .RDB that does not read: not run
            Assert.False(
                SimServer.RdbMatches(Path.Combine(Path.GetTempPath(), "no-such-page.RDB"), model, notes)
            );
        }

        [Fact]
        public void ARegularExpressionFilterFindsTheNameOrEquipmentItemWithoutCase()
        {
            // found anywhere, anchored with ^ $, without case; the wildcards are
            // plain regex there
            var f = new TagListing.Filter(@"^tank\d_(level|temp)$", true);
            Assert.True(f.Match("Tank1_Level", "", ""));
            Assert.True(f.Match("TANK2_TEMP", "", ""));
            Assert.False(f.Match("Tank1_LevelHigh", "", ""));
            Assert.True(new TagListing.Filter(@"t20.\.lt", true).Match("X_9", "WTP.T201", "LT"));
            Assert.True(new TagListing.Filter("a.c", true).Match("xABCx", "", ""));
            Assert.False(new TagListing.Filter("a*", false).Match("xa", "", "")); // (a wildcard: the whole name)
            Assert.True(new TagListing.Filter("a*", true).Match("xa", "", "")); // (a regex: found anywhere)
            Assert.True(new TagListing.Filter("", true).Empty);
            var bad = Assert.ThrowsAny<ArgumentException>(() => new TagListing.Filter("(tank", true));
            Assert.NotEmpty(bad.Message);
            // a pattern that backtracks without end on one name fails instead of stalling
            var slow = new TagListing.Filter("^(a+)+$", true);
            Assert.Throws<RegexMatchTimeoutException>(() => slow.Match(new string('a', 40) + "!", "", ""));
        }

        [Fact]
        public void WatchExpressionsAreBoundedAndNeverThrowOutOfTheParser()
        {
            var ev = new WatchEval();
            // a deep nesting fails the evaluation (a stack overflow would end the process)
            string deep = new string('(', 100_000) + "1" + new string(')', 100_000);
            var e = Assert.Throws<EvalException>(() => ev.Evaluate(deep));
            Assert.Contains("nested too deeply", e.Message);
            Assert.Throws<EvalException>(() =>
                ev.Evaluate(string.Concat(Enumerable.Repeat("NOT ", 50_000)) + "1")
            );
            Assert.Equal(7, ev.Evaluate("((((3 + 4))))").I);
            // net48's double.Parse throws OverflowException on 1e999: the engine's strtod gives inf
            Assert.True(double.IsPositiveInfinity(ev.Evaluate("1e999").D));
            Assert.Equal(1.5, ev.Evaluate("0.15e1").D);
            // only ASCII digits are digits
            Assert.Throws<EvalException>(() => ev.Evaluate("٣"));
        }

        [Fact]
        public void TheWireIsStrictJson()
        {
            var m = JsonValue.NewObject();
            m.Set("type", "event");
            var b = JsonValue.NewObject();
            b.Set("nan", JsonValue.Of(double.NaN));
            b.Set("inf", JsonValue.Of(double.PositiveInfinity));
            var arr = JsonValue.NewArray();
            arr.Add(JsonValue.Of(double.NegativeInfinity));
            arr.Add(JsonValue.Of(1.5));
            b.Set("list", arr);
            m.Set("body", b);
            string wire = MessageConnection.WithSeq(m, 3);
            Assert.Equal(
                "{\"seq\":3,\"type\":\"event\",\"body\":{\"nan\":\"NaN\",\"inf\":\"Infinity\",\"list\":[\"-Infinity\",1.5]}}",
                wire
            );
            // the engine's own files keep the numbers
            Assert.Equal("{\"nan\":NaN,\"inf\":Infinity,\"list\":[-Infinity,1.5]}", b.Dump());
        }

        private sealed class SlowStream : Stream
        {
            public readonly MemoryStream Written = new MemoryStream();

            public override void Write(byte[] buffer, int offset, int count)
            {
                Thread.Sleep(300);
                lock (Written)
                    Written.Write(buffer, offset, count);
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => 0;
                set => throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>An input that has nothing until it is disposed (then the end of stream).</summary>
        private sealed class QuietInput : Stream
        {
            private readonly ManualResetEventSlim _gone = new ManualResetEventSlim(false);

            public override int Read(byte[] buffer, int offset, int count)
            {
                _gone.Wait();
                return 0;
            }

            protected override void Dispose(bool disposing)
            {
                _gone.Set();
                base.Dispose(disposing);
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override void Flush() { }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => 0;
                set => throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        [Fact]
        public void CloseThenWaitFinishedWritesTheQueuedMessagesFirst()
        {
            // ctsim/shutdown answers, then the process ends: it must wait for the writer, or the
            // response dies with it
            var output = new SlowStream();
            var conn = new MessageConnection(new QuietInput(), output, "slow");
            conn.Start();
            var m = JsonValue.NewObject();
            m.Set("type", "response");
            m.Set("command", "ctsim/shutdown");
            conn.Send(m);
            conn.Close();
            Assert.True(conn.WaitFinished(5000));
            string text;
            lock (output.Written)
                text = Encoding.UTF8.GetString(output.Written.ToArray());
            Assert.Contains("\"command\":\"ctsim/shutdown\"", text);
        }

        // ------------------------------------------------------------------ on ExampleSA
        [ProjectFact]
        public void InvokeNeverWaitsForEverOnAnEndingSimulation()
        {
            for (int round = 0; round < 6; round++)
            {
                var sim = SimTestUtil.Start(SimCorpus.ExampleSA);
                int answered = 0,
                    refused = 0;
                var callers = new List<Thread>();
                for (int k = 0; k < 4; k++)
                {
                    var t = new Thread(() =>
                    {
                        while (true)
                        {
                            try
                            {
                                sim.Invoke(() => 1);
                                Interlocked.Increment(ref answered);
                            }
                            catch (ObjectDisposedException)
                            {
                                Interlocked.Increment(ref refused);
                                return;
                            }
                        }
                    })
                    {
                        IsBackground = true,
                    };
                    t.Start();
                    callers.Add(t);
                }
                Thread.Sleep(30 + 20 * round);
                sim.Dispose();
                foreach (var t in callers)
                    Assert.True(
                        t.Join(5000),
                        "an Invoke still waits after the simulation ended (round " + round + ")"
                    );
                Assert.Equal(4, refused);
                Assert.True(answered > 0);
                Assert.Throws<ObjectDisposedException>(() => sim.Post(() => { }));
            }
        }

        [ProjectFact]
        public void AFaultInsideASliceEndsThatTaskAndItsCallIsAnswered()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var ev = new SimEvents(sim);
                // the VM's log hook throws inside RaiseError, out of the slice (a built-in bug looks
                // the same)
                var keep = sim.Vm.Log;
                sim.Invoke(() => sim.Vm.Log = _ => throw new InvalidOperationException("boom"));
                var done = new ManualResetEventSlim(false);
                CallOutcome? outcome = null;
                sim.Invoke(() => sim.StartCall("GetTagVal(\"NoSuchTagXyz\")", o =>
                    {
                        outcome = o;
                        done.Set();
                    }));
                Assert.True(done.Wait(10000), "the call of a faulted task was never answered");
                Assert.Equal("halt", outcome!.State);
                Assert.Contains("boom", outcome.HaltReason);
                sim.Invoke(() => sim.Vm.Log = keep);
                Assert.DoesNotContain(sim.Invoke(() => sim.TaskList()), t => t.Handle == outcome.Task);
                sim.Events.Flush();
                lock (ev.Logs)
                    Assert.Contains(ev.Logs, l =>
                        l.Text.StartsWith(
                            "the VM failed: InvalidOperationException: boom",
                            StringComparison.Ordinal
                        )
                    );
                // the simulation runs on
                var ok = new ManualResetEventSlim(false);
                sim.Invoke(() => sim.StartCall("IntToStr(5)", o => ok.Set()));
                Assert.True(ok.Wait(10000));
            }
        }

        [ProjectFact]
        public void AFailingEventHandlerDoesNotTakeTheOthersEventsOrLoop()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                sim.Events.Flush(); // the start's notes are delivered before the handlers come
                int calls = 0;
                var seen = new List<LogEntry>();
                sim.Log += e =>
                {
                    Interlocked.Increment(ref calls);
                    throw new InvalidOperationException("handler bug");
                };
                sim.Log += e =>
                {
                    lock (seen)
                        seen.Add(e);
                };
                sim.Note("info", "hello");
                sim.Events.Flush();
                Thread.Sleep(200);
                sim.Events.Flush();
                lock (seen)
                {
                    Assert.Contains(seen, e => e.Text == "hello"); // the second handler still got it
                    Assert.Single(
                        seen.FindAll(e =>
                            e.Text.StartsWith("an event handler failed", StringComparison.Ordinal)
                        )
                    );
                }
                Assert.Equal(2, calls); // 'hello' and the one report of the failure: no endless loop
            }
        }

        [ProjectFact]
        public void DebugTargetEvaluateSurvivesHostileExpressions()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var r = sim.Debugger.Evaluate(new string('(', 50_000) + "1" + new string(')', 50_000), null);
                Assert.Contains("nested too deeply", r.ErrorText);
                r = sim.Debugger.Evaluate("1e999 + 1", null);
                Assert.Equal("REAL", r.Type);
                Assert.Equal("INF", r.Result);
                // the simulation is alive
                Assert.Equal(1, sim.Invoke(() => 1));
            }
        }

        [ProjectFact]
        public void ASubscribedTagExtensionGetsItsOwnValueInEveryBatch()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            using (var server = new SimServer(sim, "rev-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                string disc = Path.Combine(
                    Path.GetTempPath(),
                    "ctsim-review-" + Guid.NewGuid().ToString("N")
                );
                try
                {
                    server.Listen(disc);
                    using (var c = Connect(server.Listener!.Address))
                    using (var d = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "review")));
                        d.Ok("ctsim/hello", Args(("client", "review 2")));
                        var sub = c.Ok(
                            "ctsim/subscribe",
                            Args(("names", Names("WTP_T201_LT.Q", "WTP_T201_LT", "wtp_t201_lt.T")))
                        );
                        Assert.Equal("good", sub["tags"].Items[0]["value"].Str);
                        d.Ok("ctsim/subscribe", Args(("names", Names("WTP_T201_LT.Q"))));
                        Assert.Equal(2, sim.Invoke(() => sim.WatchedCount)); // two names, one counted twice
                        c.Ok(
                            "ctsim/setTag",
                            Args(("name", "WTP_T201_LT"), ("value", 43), ("quality", "bad"))
                        );
                        var e = c.WaitEvent("ctsim/tagsChanged");
                        var byName = e["tags"].Items.ToDictionary(t => t["name"].Str, t => t);
                        // the .Q name carries the quality, not the Field value
                        Assert.Equal("bad", byName["WTP_T201_LT.Q"]["value"].Str);
                        Assert.Equal("WTP_T201_LT", byName["WTP_T201_LT.Q"]["tag"].Str);
                        Assert.Equal(43, byName["WTP_T201_LT"]["value"].Double);
                        Assert.Equal("2026-10-01T08:00:00.000Z", byName["wtp_t201_lt.T"]["value"].Str);
                        Assert.Equal(3, e["tags"].Count);
                        Assert.Equal(
                            "bad",
                            d.WaitEvent("ctsim/tagsChanged")["tags"].Items.Single()["value"].Str
                        );
                        // the watches go with the subscriptions and the clients
                        c.Ok("ctsim/unsubscribe", Args(("names", Names("wtp_t201_lt.T"))));
                        c.Ok("ctsim/unsubscribe");
                        SimTestUtil.Until(sim, () => sim.WatchedCount == 1);
                    }
                    SimTestUtil.Until(
                        sim,
                        () => sim.WatchedCount == 0,
                        what: "the closed client's watch was given back"
                    );
                }
                finally
                {
                    server.Dispose();
                    try
                    {
                        Directory.Delete(disc, true);
                    }
                    catch (Exception) { }
                }
            }
        }

        [ProjectFact]
        public void AClockStepWithABusyTaskMovesTimeAndStaysShort()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                // a task that never sleeps (WHILE TRUE DO END): the runtime time-slices it; a
                // step must still move time and not hold the simulation thread for its budget
                var busy = sim.Invoke(() =>
                {
                    var pools = new Pools();
                    var cb = new CodeBuilder(pools);
                    cb.Label("top").Line(1).Branch("top").End();
                    var t = sim.Vm.TaskForBlock(
                        cb.Code(),
                        pools.Fill(new CicodeSim.Compiled.Program()),
                        false,
                        "busy"
                    );
                    sim.Scheduler.Add(t);
                    return t;
                });
                var sw = Stopwatch.StartNew();
                long moved = sim.Invoke(() => sim.StepClock(3000));
                long ms = sw.ElapsedMilliseconds;
                Assert.Equal(3000, moved);
                Assert.True(ms < 10_000, "the step held the simulation thread " + ms + " ms");
                Assert.True(busy.Slices > 1, "the busy task ran " + busy.Slices + " slices");
                sim.Invoke(() =>
                {
                    busy.State = TaskState.Halt;
                    busy.HaltReason = "test";
                });
            }
        }

        [ProjectFact]
        public void ACallThatCannotGetAThreadIsAnsweredAndNonFiniteValuesTravel()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            using (var server = new SimServer(sim, "rev-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                string disc = Path.Combine(
                    Path.GetTempPath(),
                    "ctsim-review-" + Guid.NewGuid().ToString("N")
                );
                try
                {
                    server.Listen(disc);
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "review")));
                        // a REAL that is not finite: strict JSON on the wire (a string), the
                        // client's JSON parser does not fail on the frame
                        var inf = c.Ok("ctsim/call", Args(("expr", "Pow(10, 400)")));
                        Assert.Equal("INF", inf["result"].Str);
                        Assert.Equal("Infinity", inf["value"].Str);
                        string raw;
                        lock (c.Received)
                            raw = string.Join("\n", c.Received.ConvertAll(x => x.Dump()));
                        Assert.DoesNotContain(":Infinity", raw);
                        // out of Cicode threads: the call is refused with an answer
                        sim.Invoke(() => sim.Vm.Config.Threads = 1);
                        var first = c.Ok("ctsim/call", Args(("expr", "Sleep(100)"), ("wait", false)));
                        Assert.Equal("running", first["state"].Str);
                        var second = c.Request("ctsim/call", Args(("expr", "IntToStr(1)")), 10000);
                        Assert.False(second["success"].Bool);
                        Assert.Equal(Errors.Threads, second["body"]["error"].Long);
                    }
                }
                finally
                {
                    server.Dispose();
                    try
                    {
                        Directory.Delete(disc, true);
                    }
                    catch (Exception) { }
                }
            }
        }
    }
}
