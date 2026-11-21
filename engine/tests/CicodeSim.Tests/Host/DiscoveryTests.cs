using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CicodeSim.Host;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>The discovery folder: atomic entries, stale ones ignored and cleaned.</summary>
    public sealed class DiscoveryTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(),
            "ctsim-disc-" + Guid.NewGuid().ToString("N")
        );

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (Exception) { }
        }

        private static ServerEntry Me(string id, string project = @"C:\p\Proj")
        {
            long start;
            using (var p = Process.GetCurrentProcess())
                start = p.StartTime.ToUniversalTime().ToFileTimeUtc();
            return new ServerEntry
            {
                Id = id,
                Address = @"\\.\pipe\ctsim." + id,
                Pid = Discovery.CurrentPid,
                ProcessStart = start,
                Project = project,
                ProjectName = "Proj",
                Root = "Proj",
                Started = "2026-10-01T08:00:00.000Z",
            };
        }

        [Fact]
        public void EntriesAreWrittenAtomicallyListedAndRemoved()
        {
            var e = Me(Discovery.NewId());
            string path = Discovery.Write(e, _dir);
            Assert.Equal(Path.Combine(_dir, e.Id + ".json"), path);
            Assert.Single(Directory.GetFiles(_dir)); // no temporary file left
            var got = Discovery.List(_dir).Single();
            Assert.Equal(e.Id, got.Id);
            Assert.Equal(e.Pid, got.Pid);
            Assert.Equal(e.ProcessStart, got.ProcessStart);
            Assert.Equal(e.Address, got.Address);
            Assert.Equal("Proj", got.ProjectName);
            Assert.Single(Discovery.ForProject(@"c:\P\proj\", _dir));
            Assert.Empty(Discovery.ForProject(@"C:\p\Other", _dir));
            e.Mode = "dap";
            Discovery.Write(e, _dir); // replaces in place
            Assert.Equal("dap", Discovery.List(_dir).Single().Mode);
            Discovery.Remove(e.Id, _dir);
            Assert.Empty(Discovery.List(_dir));
            Assert.Matches("^[0-9]+-[0-9a-f]{8}$", Discovery.NewId());
        }

        [Fact]
        public void StaleEntriesAreIgnoredAndCleaned()
        {
            // a dead process
            int dead;
            using (
                var p = Process.Start(
                    new ProcessStartInfo("cmd.exe", "/c exit")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    }
                )!
            )
            {
                p.WaitForExit();
                dead = p.Id;
            }
            var gone = Me("dead-1");
            gone.Pid = dead;
            Discovery.Write(gone, _dir);
            // this pid, but another start time: a reused pid
            var reused = Me("reused-1");
            reused.ProcessStart -= 10 * Discovery.StartTolerance;
            Discovery.Write(reused, _dir);
            // a pid this user cannot open (Windows: 4 is the System process; HasExited is denied)
            var system = Me("system-1");
            system.Pid = 4;
            Discovery.Write(system, _dir);
            // the live one
            var live = Me("live-1");
            Discovery.Write(live, _dir);
            // a file that is no entry (old) and a fresh one (maybe being written by another program)
            File.WriteAllText(Path.Combine(_dir, "junk.json"), "{not json");
            File.SetLastWriteTimeUtc(Path.Combine(_dir, "junk.json"), DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllText(Path.Combine(_dir, "fresh.json"), "{}");
            var list = Discovery.List(_dir);
            Assert.Equal(new[] { "live-1" }, list.Select(x => x.Id).ToArray());
            var left = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "fresh.json", "live-1.json" }, left);
            // listing without cleaning keeps the files
            Discovery.Write(gone, _dir);
            Assert.Single(Discovery.List(_dir, clean: false));
            Assert.True(File.Exists(Path.Combine(_dir, "dead-1.json")));
        }

        [Fact]
        public void AnUnknownStartTimeCountsAsAlive()
        {
            var e = Me("nostart-1");
            e.ProcessStart = 0;
            Discovery.Write(e, _dir);
            Assert.Single(Discovery.List(_dir));
            Assert.Null(ServerEntry.FromJson(Values.JsonValue.Parse("{\"format\": \"other\"}")));
        }

        [Fact]
        public void AnEntryCarriesTheBuildsRevisionCommitAndTime()
        {
            var e = Me("rev-1");
            Discovery.Write(e, _dir);
            var back = Discovery.List(_dir).Single();
            Assert.Equal(EngineInfo.ProtocolRevision, back.ProtocolRevision);
            Assert.Equal(EngineInfo.Commit, back.Commit);
            Assert.Equal(EngineInfo.BuildTime, back.BuildTime);
            Assert.True(EngineInfo.ProtocolRevision >= 5);
            Assert.NotEqual("", EngineInfo.BuildTime);
            // an entry without the field: revision 0
            var old = ServerEntry.FromJson(
                Values.JsonValue.Parse(
                    "{\"format\": \"" + ServerEntry.Format + "\", \"id\": \"x\", \"pid\": 1}"
                )
            );
            Assert.NotNull(old);
            Assert.Equal(0, old!.ProtocolRevision);
        }
    }
}
