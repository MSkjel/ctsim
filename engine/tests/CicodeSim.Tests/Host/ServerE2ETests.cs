using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;
using Xunit.Abstractions;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>End to end: the built ctsim.exe started as a server on the R2 projects, clients
    /// over its named pipe. The discovery folder is a temporary one (the user's
    /// %LOCALAPPDATA%\cicode-sim\servers is left alone).</summary>
    [Collection("ctsim server")]
    public sealed class ServerE2ETests : IDisposable
    {
        private readonly string _disc = Path.Combine(
            Path.GetTempPath(),
            "ctsim-test-" + Guid.NewGuid().ToString("N")
        );
        private readonly ITestOutputHelper _out;

        public ServerE2ETests(ITestOutputHelper output)
        {
            _out = output;
            Directory.CreateDirectory(_disc);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_disc, true);
            }
            catch (Exception) { }
        }

        private string ServeArgs(string project, string extra = "") =>
            "serve --project "
            + ServerProcess.Quote(project)
            + " --discovery "
            + ServerProcess.Quote(_disc)
            + " --start 2026-10-01T08:00:00Z --tz UTC "
            + extra;

        [ProjectFact]
        public void TwoClientsSeeTheSameTagChange()
        {
            var sw = Stopwatch.StartNew();
            using (var server = new ServerProcess(ServeArgs(SimCorpus.ExampleSA, "--clock run")))
            {
                long readyMs = sw.ElapsedMilliseconds;
                // the discovery file is there, with this process and the project
                var entry = Discovery.List(_disc).Single();
                Assert.Equal(server.Id, entry.Id);
                Assert.Equal(server.Process.Id, entry.Pid);
                Assert.Equal(SimCorpus.ExampleSA, entry.Project, ignoreCase: true);
                Assert.Equal(@"\\.\pipe\ctsim." + server.Id, entry.Address);
                using (var a = Connect(server.Pipe))
                using (var b = Connect(server.Pipe))
                {
                    var ha = a.Ok("ctsim/hello", Args(("client", "test A")));
                    b.Ok("ctsim/hello", Args(("client", "test B")));
                    Assert.Equal("ExampleSA", ha["projectName"].Str);
                    Assert.Equal("ExampleSA", ha["root"].Str);
                    Assert.Equal(SimServer.Protocol, ha["protocol"].Str);
                    Assert.Equal("run", ha["clock"]["mode"].Str);
                    var sa = a.Ok("ctsim/subscribe", Args(("names", Names("WTP_T201_LT", "NoSuchTagXyz"))));
                    var sb = b.Ok("ctsim/subscribe", Args(("names", Names("wtp_t201_lt"))));
                    Assert.Equal("WTP_T201_LT", sa["tags"].Items.Single()["name"].Str);
                    Assert.Equal("NoSuchTagXyz", sa["unknown"].Items.Single().Str);
                    Assert.Equal("WTP_T201_LT", sb["tags"].Items.Single()["tag"].Str); // the defined spelling
                    // A sets it as a device would; both get one tagsChanged with the value
                    var set = a.Ok("ctsim/setTag", Args(("name", "WTP_T201_LT"), ("value", 42.5)));
                    Assert.Equal(0, set["error"].Long);
                    Func<JsonValue, bool> has = e =>
                        e["tags"]
                            .Items.Any(t =>
                                (t.Get("tag") ?? t["name"]).Str == "WTP_T201_LT" && t["value"].Double == 42.5
                            );
                    var ea = a.WaitEvent("ctsim/tagsChanged", has);
                    var eb = b.WaitEvent("ctsim/tagsChanged", has);
                    Assert.Equal(
                        "good",
                        ea["tags"].Items.First(t => t["name"].Str == "WTP_T201_LT")["quality"].Str
                    );
                    Assert.Equal(
                        "wtp_t201_lt",
                        eb["tags"].Items.First(t => t.Get("tag")?.Str == "WTP_T201_LT")["name"].Str
                    );
                    Assert.True(ea["t"].Double >= 0);
                    // the listing: paged, grouped by equipment, with definitions and values
                    var tags = b.Ok("ctsim/tags", Args(("filter", "WTP_T201_LT"), ("groups", true)));
                    var t1 = tags["tags"].Items.Single(t => t["name"].Str == "WTP_T201_LT");
                    Assert.Equal(42.5, t1["value"].Double);
                    Assert.Equal("variable", t1["kind"].Str);
                    Assert.True(tags["groups"].Count >= 1);
                    // a regular expression, a refused one
                    var rx = b.Ok("ctsim/tags", Args(("filter", "^wtp_t20[0-9]_lt$"), ("regex", true)));
                    Assert.Contains(rx["tags"].Items, t => t["name"].Str == "WTP_T201_LT");
                    Assert.All(rx["tags"].Items, t => Assert.Matches("(?i)^wtp_t20[0-9]_lt$", t["name"].Str));
                    var nrx = b.Request("ctsim/tags", Args(("filter", "(wtp"), ("regex", true)));
                    Assert.False(nrx["success"].Bool);
                    Assert.StartsWith("not a regular expression", nrx["message"].Str);
                    var page = b.Ok("ctsim/tags", Args(("start", 10), ("count", 25)));
                    Assert.Equal(25, page["tags"].Count);
                    Assert.True(page["total"].Long > 1000);
                    // a call with literal arguments, a refused bare expression, a page request
                    var call = b.Ok("ctsim/call", Args(("expr", "IntToStr(42)")));
                    Assert.Equal("stop", call["state"].Str);
                    Assert.Equal("42", call["result"].Str);
                    var bare = b.Request("ctsim/call", Args(("expr", "1 + 2")));
                    Assert.False(bare["success"].Bool);
                    Assert.Equal(292, bare["body"]["error"].Long);
                    // the page animator: the page opens in window 0 and its states come with it
                    var op = b.Ok("ctsim/openPage", Args(("name", "WTP_INTAKE_HD1080")));
                    Assert.Equal(0, op["window"].Long);
                    Assert.Equal("rdb", op["source"].Str);
                    Assert.True(op["state"]["objects"].Count > 0);
                    Assert.True(b.Ok("ctsim/closePage", Args(("window", 0)))["closed"].Bool);
                    var usage = server.Usage();
                    _out.WriteLine(
                        $"ExampleSA serve: ready in {readyMs} ms (load {server.Ready["loadMs"].Long} ms), working set {usage.Mb:F1} MB (peak {usage.PeakMb:F1} MB), cpu {usage.CpuSeconds:F2} s"
                    );
                    // a client ends the server: the discovery file goes with it
                    a.Ok("ctsim/shutdown");
                    Assert.True(server.WaitExit(15000), "the server did not exit: " + server.Stderr);
                }
                Assert.Equal(0, server.Process.ExitCode);
                Assert.Empty(Directory.GetFiles(_disc));
            }
        }

        [ProjectFact]
        public void SetAlarmAndSetEquipmentSetTheServersStateWithTheRuntimesErrorCodes()
        {
            using (var server = new ServerProcess(ServeArgs(SimCorpus.ExampleSA, "--clock paused")))
            using (var c = Connect(server.Pipe))
            {
                c.Ok("ctsim/hello", Args(("client", "test")));
                // an OFF unacknowledged alarm, as the live alarm server keeps them (ctsim-pagecase/1)
                var a = c.Ok("ctsim/setAlarm", Args(("name", "OV08_Fault"), ("state", "off_unack")));
                Assert.Equal(0, a["error"].Long);
                Assert.Equal(
                    (0L, 0L, 256L),
                    (a["alarm"]["on"].Long, a["alarm"]["ack"].Long, a["alarm"]["state"].Long)
                );
                var on = c.Ok(
                    "ctsim/call",
                    Args(("expr", "AlarmHighestPriority(2, \"Company.TopMilk.Mixing.OV08\")"))
                );
                Assert.Equal((2 << 8) | 1, on["value"].Long); // OFF unacknowledged, priority 1
                var bad = c.Request("ctsim/setAlarm", Args(("name", "NoSuchAlarmXyz"), ("state", "on")));
                Assert.False(bad["success"].Bool);
                Assert.Equal(424, bad["body"]["error"].Long);
                var word = c.Request("ctsim/setAlarm", Args(("name", "OV08_Fault"), ("state", "maybe")));
                Assert.False(word["success"].Bool);
                Assert.Contains("not an alarm state", word["message"].Str);
                // the equipment's runtime fields; nothing changes on a refused request
                var e = c.Ok(
                    "ctsim/setEquipment",
                    Args(
                        ("name", "Company.TopMilk.Mixing"),
                        ("fields", Args(("STATE", "Active"), ("mode", "0")))
                    )
                );
                Assert.Equal("Active", e["fields"]["STATE"].Str);
                var get = c.Ok(
                    "ctsim/call",
                    Args(("expr", "EquipGetProperty(\"Company.TopMilk.Mixing\", \"State\", 1, \"\")"))
                );
                Assert.Equal("Active", get["result"].Str);
                var noEq = c.Request(
                    "ctsim/setEquipment",
                    Args(("name", "Company.Nope"), ("fields", Args(("STATE", "x"))))
                );
                Assert.Equal(289, noEq["body"]["error"].Long);
                var noField = c.Request(
                    "ctsim/setEquipment",
                    Args(
                        ("name", "Company.TopMilk.Mixing"),
                        ("fields", Args(("STATE", "Off"), ("TYPE", "x")))
                    )
                );
                Assert.Equal(274, noField["body"]["error"].Long);
                get = c.Ok(
                    "ctsim/call",
                    Args(("expr", "EquipGetProperty(\"Company.TopMilk.Mixing\", \"State\", 1, \"\")"))
                );
                Assert.Equal("Active", get["result"].Str);
                c.Ok("ctsim/shutdown");
                Assert.True(server.WaitExit(15000), "the server did not exit: " + server.Stderr);
            }
        }

        [ProjectFact]
        public void ARealExampleFunctionRunsThroughTheProtocol()
        {
            using (var server = new ServerProcess(ServeArgs(SimCorpus.Example, "--clock paused")))
            using (var c = Connect(server.Pipe))
            {
                c.Ok("ctsim/hello", Args(("client", "test")));
                c.Ok("ctsim/subscribe", Args(("names", Names("STOCKPILE", "Water_Level"))));
                c.Ok("ctsim/setTag", Args(("name", "DiskFilter"), ("value", 1)));
                c.Ok("ctsim/setTag", Args(("name", "STOCKPILE"), ("value", 10)));
                // Simulatr.ci StockPileSim(): IF DiskFilter THEN STOCKPILE = STOCKPILE + 2
                var r = c.Ok("ctsim/call", Args(("expr", "StockPileSim()")));
                Assert.Equal("stop", r["state"].Str);
                Assert.Equal(0, r["error"].Long);
                c.WaitEvent("ctsim/tagsChanged", e =>
                    e["tags"].Items.Any(t => t["name"].Str == "STOCKPILE" && t["value"].Double == 12)
                );
                var t = c.Ok("ctsim/tags", Args(("names", Names("STOCKPILE"))));
                Assert.Equal(12, t["tags"].Items.Single()["value"].Double);
                // WaterTankLevel() loops for ever with Sleep(1): started, not waited for
                var w = c.Ok("ctsim/call", Args(("expr", "WaterTankLevel()"), ("wait", false)));
                Assert.Equal("running", w["state"].Str);
                long task = w["task"].Long;
                var started = c.WaitEvent("ctsim/taskStarted", e => e["task"].Long == task);
                Assert.Equal("WaterTankLevel", started["function"].Str);
                var tasks = c.Ok("ctsim/tasks");
                Assert.Contains(tasks["tasks"].Items, x => x["task"].Long == task);
                // 3 s of simulated time: Water_Level < 8000 -> + 550 a second
                var step = c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 3)));
                Assert.Equal(3000, step["moved"].Long);
                Assert.Equal("paused", step["mode"].Str);
                var ch = c.WaitEvent("ctsim/tagsChanged", e =>
                    e["tags"].Items.Any(x => x["name"].Str == "Water_Level" && x["value"].Double >= 1650)
                );
                Assert.NotNull(ch);
                var clock = c.Ok("ctsim/clock");
                Assert.Equal(3000, clock["ms"].Long);
                Assert.Equal(3.0, clock["t"].Double);
                var re = c.Ok("ctsim/reload", Args(("file", Path.Combine(SimCorpus.Example, "Simulatr.ci"))));
                Assert.True(re["accepted"].Bool);
                // the source compiler compiles the saved file and swaps the program: the
                // first reload compiles the unit (the compiler starts), the next ones the file alone
                Assert.True(re["compiled"].Bool, re.Dump());
                var re2 = c.Ok(
                    "ctsim/reload",
                    Args(("file", Path.Combine(SimCorpus.Example, "Simulatr.ci")))
                );
                Assert.True(re2["compiled"].Bool && re2["incremental"].Bool, re2.Dump());
                c.Ok("ctsim/shutdown");
                Assert.True(server.WaitExit(15000), "the server did not exit: " + server.Stderr);
            }
        }

        [ProjectFact]
        public void DapModeKeepsStdoutForTheStdioSession()
        {
            var psi = new ProcessStartInfo(
                ServerProcess.Exe,
                "dap --project "
                    + ServerProcess.Quote(SimCorpus.ExampleSA)
                    + " --discovery "
                    + ServerProcess.Quote(_disc)
                    + " --log"
            )
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi)!)
            {
                var err = new System.Text.StringBuilder();
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                        lock (err)
                            err.AppendLine(e.Data);
                };
                p.BeginErrorReadLine();
                try
                {
                    var c = new ProtocolClient(p.StandardOutput.BaseStream, p.StandardInput.BaseStream);
                    var init = c.Request(
                        "initialize",
                        Args(("clientID", "test"), ("adapterID", "cicode-sim"))
                    );
                    Assert.True(init["success"].Bool);
                    c.WaitEvent("initialized");
                    // a viewer joins the same simulation through the pipe of the discovery file
                    var entry = Discovery.List(_disc).Single();
                    Assert.Equal("dap", entry.Mode);
                    using (var v = Connect(entry.Address))
                    {
                        var h = v.Ok("ctsim/hello", Args(("client", "viewer")));
                        Assert.True(h["debugger"].Bool);
                        // a second debugger is refused
                        var second = v.Request("initialize");
                        Assert.False(second["success"].Bool);
                    }
                    // ctsim/* inside the debugger's own session
                    var info = c.Request("ctsim/tags", Args(("filter", "WTP_T201_LT")));
                    Assert.True(info["success"].Bool);
                    var unknown = c.Request("setBreakpoints");
                    Assert.False(unknown["success"].Bool);
                    var disc = c.Request("disconnect");
                    Assert.True(disc["success"].Bool);
                    Assert.True(p.WaitForExit(15000), "ctsim dap did not exit: " + err);
                    Assert.Equal(0, p.ExitCode);
                    // nothing but DAP frames went to stdout
                    lock (c.Received)
                        Assert.All(c.Received, m => Assert.True(m.Get("type") != null));
                    Assert.Empty(Directory.GetFiles(_disc));
                }
                finally
                {
                    if (!p.HasExited)
                        p.Kill();
                }
            }
        }
    }
}
