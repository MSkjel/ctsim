using System;
using System.IO;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Vm;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>Task events are rate-limited per second (thousands a second would freeze a
    /// viewer).</summary>
    [Collection("ctsim server")]
    public sealed class TaskEventRateTests
    {
        /// <summary>A second sends TaskEventsPerSecond task events one by one; the rest of it
        /// (tasks that did not halt) come as one ctsim/log line when the next second starts; a
        /// halted task is always sent.</summary>
        [ProjectFact]
        public void ABusySecondSendsItsTaskEventsAsOneCountLine()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (
                var server = new SimServer(sim, "taskrate-" + Guid.NewGuid().ToString("N").Substring(0, 8))
            )
            {
                long now = 5000;
                server.TaskClock = () => now;
                server.Listen(
                    Path.Combine(Path.GetTempPath(), "ctsim-taskrate-" + Guid.NewGuid().ToString("N"))
                );
                using (var starts = Connect(server.Listener!.Address))
                using (var exits = Connect(server.Listener!.Address))
                using (var logs = Connect(server.Listener!.Address))
                {
                    foreach (var c in new[] { starts, exits, logs })
                        c.Ok("ctsim/hello", Args(("client", "taskrate"), ("events", Names("task"))));
                    for (int k = 0; k < 1000; k++)
                        server.TaskEvent(
                            "ctsim/taskStarted",
                            new TaskSummary { Handle = k + 1, Function = "_LibAlmTable_GetData" }
                        );
                    server.TaskEvent(
                        "ctsim/taskExited",
                        new TaskSummary
                        {
                            Handle = 7,
                            Function = "Broken",
                            State = TaskState.Halt,
                            HaltCode = 269,
                            HaltReason = "Bad handle",
                        }
                    );
                    now += 1000;
                    server.TaskEvent(
                        "ctsim/taskStarted",
                        new TaskSummary { Handle = 2000, Function = "Next" }
                    );

                    int started = 0;
                    while (true)
                    {
                        var e = starts.WaitEvent("ctsim/taskStarted");
                        if (e["task"].Long == 2000)
                            break;
                        started++;
                    }
                    Assert.Equal(SimServer.TaskEventsPerSecond, started);
                    var halted = exits.WaitEvent("ctsim/taskExited");
                    Assert.Equal(269, halted["haltCode"].Long);
                    var note = logs.WaitEvent("ctsim/log");
                    Assert.Contains("900 more task events in one second", note["text"].Str);
                    Assert.Contains("_LibAlmTable_GetData 900", note["text"].Str);
                    Assert.Equal("task", note["kind"].Str);
                }
            }
        }
    }
}
