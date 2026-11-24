using System;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>ctsim/imageWanted and ctsim/windowImage on the Example project: a viewer that
    /// draws windows answers WinFile and WinCopy; without one they return 0 and write nothing.</summary>
    [Collection("ctsim server")]
    public sealed class WindowImageProtocolTests
    {
        private static (SimServer server, string disc) Serve(Simulation sim)
        {
            var server = new SimServer(sim, "images-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string disc = Path.Combine(Path.GetTempPath(), "ctsim-images-" + Guid.NewGuid().ToString("N"));
            server.Listen(disc);
            return (server, disc);
        }

        private static void Done(SimServer server, string disc)
        {
            server.Dispose();
            try
            {
                Directory.Delete(disc, true);
            }
            catch (Exception) { }
        }

        private static string Bmp()
        {
            var p = new BmpImage(4, 3);
            p[1, 1] = 0xFFFFFF;
            return Convert.ToBase64String(p.ToBmp());
        }

        [ProjectFact]
        public void AViewerDrawsTheWindowForWinFileAndWinCopy()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        var h = c.Ok("ctsim/hello", Args(("client", "images"), ("images", true)));
                        Assert.True(h["capabilities"]["windowImages"].Bool);
                        Assert.True(h["protocolRevision"].Long >= 30);
                        c.Ok("ctsim/openPage", Args(("name", "Mixer")));
                        var call = c.Ok(
                            "ctsim/call",
                            Args(("expr", "WinFile(\"[RUN]:shot.bmp\", 0.5, 0.25, 1)"), ("wait", false))
                        );
                        var want = c.WaitEvent("ctsim/imageWanted");
                        Assert.Equal(
                            (0L, "Mixer", "WinFile"),
                            (want["window"].Long, want["page"].Str, want["function"].Str)
                        );
                        Assert.Equal(
                            (0.5, 0.25, false),
                            (want["xScale"].Double, want["yScale"].Double, want["clipboard"].Bool)
                        );
                        var ans = c.Ok("ctsim/windowImage", Args(("id", want["id"].Long), ("bmp", Bmp())));
                        Assert.True(ans["accepted"].Bool);
                        Assert.Equal((4L, 3L), (ans["width"].Long, ans["height"].Long));
                        Assert.Null(ans.Get("bmp"));
                        // a second answer fails: the request is over
                        Assert.False(
                            c.Request("ctsim/windowImage", Args(("id", want["id"].Long), ("none", true)))[
                                "success"
                            ].Bool
                        );
                        byte[]? file = null;
                        SimTestUtil.Until(
                            sim,
                            () =>
                                (file = sim.Host.Sandbox.ReadAll(Files.FullName(sim.Host, "[RUN]:shot.bmp")))
                                != null,
                            what: "the picture's file"
                        );
                        var back = BmpImage.Read(file!)!;
                        Assert.Equal((0xFFFFFF, 0), (back[0, 0], back[1, 1])); // swapped
                        // WinCopy: the recoloured picture comes back for the clipboard
                        c.Ok("ctsim/call", Args(("expr", "WinCopy(1, 1, 0)"), ("wait", false)));
                        want = c.WaitEvent("ctsim/imageWanted", e => e["function"].Str == "WinCopy");
                        Assert.True(want["clipboard"].Bool);
                        ans = c.Ok("ctsim/windowImage", Args(("id", want["id"].Long), ("bmp", Bmp())));
                        var clip = BmpImage.Read(Convert.FromBase64String(ans["bmp"].Str))!;
                        Assert.Equal((0, 0xFFFFFF), (clip[0, 0], clip[1, 1]));
                        Assert.True(call["task"].Long > 0);
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        [ProjectFact]
        public void WithoutAViewerThatDrawsNothingIsWritten()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "plain")));
                        c.Ok("ctsim/openPage", Args(("name", "Mixer")));
                        var r = c.Ok("ctsim/call", Args(("expr", "WinFile(\"[RUN]:none.bmp\")")));
                        Assert.Equal("0", r["result"].Str);
                        Assert.Null(
                            sim.Invoke(() =>
                                sim.Host.Sandbox.ReadAll(Files.FullName(sim.Host, "[RUN]:none.bmp"))
                            )
                        );
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }
    }
}
