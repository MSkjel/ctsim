using System.Collections.Generic;
using System.Threading;
using CicodeSim.Sim;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>The simulated user (ctsim/user: full access, not logged in, a project user) and
    /// the simulator-only locks on tags and page variables (PROTOCOL.md ctsim/lockTag,
    /// setPageVar {locked}). Read only: the project is only read.</summary>
    [Collection("ctsim server")]
    public sealed class UserLockTests
    {
        private static CallOutcome Run(Simulation sim, string text)
        {
            CallOutcome? o = null;
            var done = new ManualResetEventSlim(false);
            sim.Invoke(() => sim.StartCall(text, r =>
                {
                    o = r;
                    done.Set();
                }));
            Assert.True(done.Wait(10000), text);
            return o!;
        }

        [ProjectFact]
        public void FullAccessNoneAndAProjectUser()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var seen = new List<string>();
                sim.UserChanged += u =>
                {
                    lock (seen)
                        seen.Add(u["mode"].Str);
                };
                Assert.Equal("none", sim.Invoke(() => sim.UserJson())["mode"].Str);
                Assert.Equal("0", Run(sim, "GetPriv(3, 0)").Result);
                var full = sim.Invoke(() => sim.SetUser(JsonValue.Parse("{\"mode\": \"full\"}")));
                Assert.Equal("full", full["mode"].Str);
                Assert.Equal("1", Run(sim, "GetPriv(3, 0)").Result);
                Assert.Equal("1", Run(sim, "GetPriv(8, 200)").Result);
                Assert.True(sim.Invoke(() => sim.World.Users.CanView(17)));
                sim.Invoke(() => sim.SetUser(JsonValue.Parse("{\"mode\": \"none\"}")));
                Assert.Equal("0", Run(sim, "GetPriv(1, 0)").Result);
                // a project user: users.dbf of the unit, that user's roles
                var users = sim.Invoke(() => sim.UsersJson())["users"];
                Assert.True(users.Items.Count > 0);
                string name = users.Items[0]["name"].Str;
                var u = sim.Invoke(() =>
                    sim.SetUser(JsonValue.Parse("{\"mode\": \"user\", \"name\": \"" + name + "\"}"))
                );
                Assert.Equal(("user", name), (u["mode"].Str, u["name"].Str));
                Assert.Throws<System.ArgumentException>(() =>
                    sim.Invoke(() =>
                        sim.SetUser(JsonValue.Parse("{\"mode\": \"user\", \"name\": \"nobody-here\"}"))
                    )
                );
                SimTestUtil.Until(sim, () =>
                {
                    lock (seen)
                        return seen.Count >= 3;
                });
                lock (seen)
                    Assert.Equal(new[] { "full", "none", "user" }, seen.GetRange(0, 3).ToArray());
            }
        }

        [ProjectFact]
        public void ALockedTagKeepsTheUsersValueAgainstCicode()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                string tag = sim.Invoke(() => sim.World.Tags.Tables()[0].Definition(0).Name);
                var logs = new List<string>();
                sim.Log += e =>
                {
                    lock (logs)
                        logs.Add(e.Text);
                };
                Assert.Equal(0, sim.Invoke(() => sim.World.Tags.Set(tag, JsonValue.Of(5), by: "user")));
                Assert.Equal(0, sim.Invoke(() => sim.World.Tags.SetLock(tag, true)));
                Assert.True(sim.Invoke(() => sim.World.Tags.IsLocked(tag)));
                Run(sim, "TagWrite(\"" + tag + "\", 9)");
                SimTestUtil.Until(sim, () => sim.World.Clock.NowMs >= 0);
                sim.Invoke(() => sim.World.Clock.Advance(1.0));
                Assert.Equal("5", Run(sim, "TagRead(\"" + tag + "\")").Result);
                SimTestUtil.Until(sim, () =>
                {
                    lock (logs)
                        return logs.Exists(t => t.Contains(tag + " is locked"));
                });
                // the user's own write still goes in
                Assert.Equal(0, sim.Invoke(() => sim.World.Tags.Set(tag, JsonValue.Of(7), by: "user")));
                Assert.Equal("7", Run(sim, "TagRead(\"" + tag + "\")").Result);
                Assert.Contains(tag, sim.Invoke(() => sim.World.Tags.LockedNames()));
                Assert.Equal(0, sim.Invoke(() => sim.World.Tags.SetLock(tag, false)));
                Run(sim, "TagWrite(\"" + tag + "\", 9)");
                sim.Invoke(() => sim.World.Clock.Advance(1.0));
                Assert.Equal("9", Run(sim, "TagRead(\"" + tag + "\")").Result);
                Assert.NotEqual(0, sim.Invoke(() => sim.World.Tags.SetLock("no_such_tag_here", true)));
            }
        }
    }
}
