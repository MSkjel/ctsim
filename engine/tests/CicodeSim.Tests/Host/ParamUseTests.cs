using System.Linq;
using System.Threading;
using CicodeSim.Host;
using CicodeSim.Sim;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>ctsim/params: the parameters Cicode read, where from and who read them;
    /// setParam changes what the next read gets (R2 Example, read only).</summary>
    [Collection("ctsim server")]
    public sealed class ParamUseTests
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
            Assert.True(done.Wait(30000), text);
            return o!;
        }

        [ProjectFact]
        public void ReadsAreListedAndSetParamChangesTheNext()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                Assert.Equal("dflt", Run(sim, "ParameterGet(\"CtgeditTest\", \"Nothing\", \"dflt\")").Result);
                var p = sim.Invoke(() => sim.ParamsJson())["params"]
                    .Items.Single(x => x["name"].Str == "Nothing");
                Assert.Equal("default", p["source"].Str);
                Assert.Equal(1, p["reads"].Long);
                sim.Invoke(() => sim.SetParam("CtgeditTest", "Nothing", "42"));
                Assert.Equal("42", Run(sim, "ParameterGet(\"CtgeditTest\", \"Nothing\", \"dflt\")").Result);
                p = sim.Invoke(() => sim.ParamsJson())["params"]
                    .Items.Single(x => x["name"].Str == "Nothing");
                Assert.Equal("simulator", p["source"].Str);
                Assert.Equal(2, p["reads"].Long);
            }
        }
    }
}
