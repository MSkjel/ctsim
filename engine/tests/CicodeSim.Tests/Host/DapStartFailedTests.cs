using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CicodeSim.Values;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>A fact that runs the built ctsim.exe (next to the test assembly).</summary>
    public sealed class CtsimExeFactAttribute : FactAttribute
    {
        public CtsimExeFactAttribute()
        {
            if (!File.Exists(ServerProcess.Exe))
                Skip = "ctsim.exe is not next to the test assembly";
        }
    }

    /// <summary>ctsim dap with a project that cannot load: the stdio session stays up,
    /// answers initialize and fails launch / attach with the load error (StartFailedSession),
    /// so the client can say why; the process ends with exit code 1.</summary>
    [Collection("ctsim server")]
    public sealed class DapStartFailedTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(
            Path.GetTempPath(),
            "ctsim-startfail-" + Guid.NewGuid().ToString("N")
        );
        private string Disc => Path.Combine(_tmp, "servers");

        public DapStartFailedTests()
        {
            Directory.CreateDirectory(Disc);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tmp, true);
            }
            catch (Exception) { }
        }

        private sealed class DapProcess : IDisposable
        {
            public readonly Process P;
            public readonly ProtocolClient C;
            public readonly StringBuilder Err = new StringBuilder();

            public DapProcess(string project, string disc)
            {
                var psi = new ProcessStartInfo(
                    ServerProcess.Exe,
                    "dap --project "
                        + ServerProcess.Quote(project)
                        + " --discovery "
                        + ServerProcess.Quote(disc)
                )
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                P = Process.Start(psi)!;
                P.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                        lock (Err)
                            Err.AppendLine(e.Data);
                };
                P.BeginErrorReadLine();
                C = new ProtocolClient(P.StandardOutput.BaseStream, P.StandardInput.BaseStream);
            }

            public string Stderr
            {
                get
                {
                    lock (Err)
                        return Err.ToString();
                }
            }

            public void Dispose()
            {
                try
                {
                    if (!P.HasExited)
                    {
                        P.Kill();
                        P.WaitForExit(5000);
                    }
                }
                catch (Exception) { }
                P.Dispose();
            }
        }

        private static void AssertRefused(JsonValue r, string reason)
        {
            Assert.False(r["success"].Bool, r.Dump());
            Assert.Equal(reason, r["message"].Str);
            var error = r["body"]["error"];
            Assert.Equal(reason, error["format"].Str);
            Assert.True(error["showUser"].Bool);
        }

        [CtsimExeFact]
        public void LaunchFailsWithTheLoadErrorAndDisconnectEndsTheProcess()
        {
            // a project whose compiled library is no RDB (a folder without _LIBRARY.RDB loads: its
            // code, if any, compiles from source)
            string project = Path.Combine(_tmp, "NoProject");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "_LIBRARY.RDB"), "not an RDB");
            using (var d = new DapProcess(project, Disc))
            {
                var c = d.C;
                var init = c.Request("initialize", Args(("clientID", "test"), ("adapterID", "cicode-sim")));
                Assert.True(init["success"].Bool, init.Dump() + " " + d.Stderr);
                var launch = c.Request("launch", Args(("function", "Main"), ("project", project)));
                string reason = launch["message"].Str;
                Assert.StartsWith("ctsim cannot load the project: ", reason);
                AssertRefused(launch, reason);
                // the reason also went to the debug console, before the response
                var output = c.WaitEvent("output");
                Assert.Equal("stderr", output["category"].Str);
                Assert.Equal(reason + "\n", output["output"].Str);
                int ev,
                    resp;
                lock (c.Received)
                {
                    ev = c.Received.FindIndex(m => m.Get("type")?.Str == "event");
                    resp = c.Received.FindIndex(m =>
                        m.Get("type")?.Str == "response" && m["command"].Str == "launch"
                    );
                }
                Assert.True(ev >= 0 && ev < resp, "output " + ev + ", launch response " + resp);
                // the same text as on stderr, after its 'ctsim dap: ' prefix (the pipe's reader
                // may get it after the response)
                string onStderr = "ctsim dap: " + reason.Substring("ctsim cannot load the project: ".Length);
                var waited = Stopwatch.StartNew();
                while (!d.Stderr.Contains(onStderr) && waited.ElapsedMilliseconds < 10000)
                    System.Threading.Thread.Sleep(20);
                Assert.Contains(onStderr, d.Stderr);
                // attach and any other request: the same reason
                AssertRefused(c.Request("attach", Args(("server", "auto"))), reason);
                AssertRefused(c.Request("threads"), reason);
                Assert.True(c.Request("disconnect")["success"].Bool);
                Assert.True(d.P.WaitForExit(15000), "ctsim dap did not exit: " + d.Stderr);
                Assert.Equal(1, d.P.ExitCode);
                // nothing but DAP frames went to stdout, no initialized event (no configuration),
                // no server, no discovery file
                lock (c.Received)
                {
                    Assert.All(c.Received, m => Assert.True(m.Get("type") != null));
                    Assert.DoesNotContain(c.Received, m => m.Get("event")?.Str == "initialized");
                }
                Assert.Empty(Directory.GetFiles(Disc));
            }
        }

        [CtsimExeFact]
        public void AMissingFolderFailsAttachAndTheEndOfStdinEndsTheProcess()
        {
            string project = Path.Combine(_tmp, "NoSuchFolder");
            using (var d = new DapProcess(project, Disc))
            {
                var c = d.C;
                Assert.True(
                    c.Request("initialize", Args(("clientID", "test"), ("adapterID", "cicode-sim")))[
                        "success"
                    ].Bool,
                    d.Stderr
                );
                var attach = c.Request("attach", Args(("server", "auto"), ("project", project)));
                Assert.StartsWith("ctsim cannot load the project: ", attach["message"].Str);
                AssertRefused(attach, attach["message"].Str);
                Assert.True(c.Request("terminate")["success"].Bool);
                c.WaitEvent("terminated");
                // the client goes without a disconnect
                d.P.StandardInput.Close();
                Assert.True(d.P.WaitForExit(15000), "ctsim dap did not exit: " + d.Stderr);
                Assert.Equal(1, d.P.ExitCode);
            }
        }
    }
}
