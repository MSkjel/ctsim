using System.Linq;
using System.Threading;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>ctsim/profile's functions: each Cicode and built-in function's
    /// calls, total and self time and callers; a task's wait between its slices is not timed.</summary>
    [Collection("World")]
    public sealed class FunctionProfileTests : BuiltinTestBase
    {
        [Fact]
        public void EachFunctionIsTimedWithItsCallersAndAWaitIsNotTimed()
        {
            var s = Make(pb =>
            {
                // Worker(INT n): Count_Long = n; Sleep(1); Count_Long = Inner(n)
                pb.Function("Inner", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .ConstInt(1)
                    .Op("Add")
                    .RetValue()
                    .End();
                pb.Function("Worker", "4")
                    .Begin()
                    .LoadStack(0)
                    .Store("Count_Long")
                    .ConstInt(1)
                    .Func("Sleep", 1)
                    .Op("Pop")
                    .LoadStack(0)
                    .Call("Inner", 1)
                    .Store("Count_Long")
                    .Ret()
                    .End();
                Ret(pb, "Start", "TaskNew", "Worker", "41", 0);
            });
            var fp = new FunctionProfile();
            s.Run.Vm.Functions = fp;
            Assert.True(s.Result("Start").I > 0);
            s.Run.Scheduler.Run(); // Worker runs to its Sleep
            // a call that has not returned (asleep): in the tree with what it called so far
            var open = fp.TreeReport(minShare: 0).Items.Single(r => r["name"].Str == "(task Worker)");
            var ow = open["children"].Items.Single(c => c["name"].Str == "Worker");
            Assert.Equal(0, ow["calls"].Long);
            Assert.True(
                ow["ms"].ToNumber() + 1e-9
                    >= ow["children"].Items.Single(c => c["name"].Str == "Sleep")["ms"].ToNumber()
            );
            Assert.True(open["ms"].ToNumber() + 1e-9 >= ow["ms"].ToNumber());
            Thread.Sleep(300); // real time while it sleeps: not its time
            s.W.Clock.Advance(2);
            s.Run.Scheduler.Run();
            Assert.Equal(42, s.W.Tags.Get("Count_Long").I);
            var rows = fp.Report(50, out int n).Items;
            Assert.Equal(rows.Count, n);
            JsonValue Row(string name) => rows.Single(r => r["name"].Str == name);
            string Caller(JsonValue r) => r["callers"].Items[0]["name"].Str;

            var w = Row("Worker");
            Assert.False(w["builtin"].Bool);
            Assert.Equal(1, w["calls"].Long);
            Assert.True(w["ms"].ToNumber() < 150, "the sleep was timed: " + w["ms"].ToNumber());
            Assert.Equal("(task Worker)", Caller(w));
            // Inner's time is in Worker's total, not in its self time
            var inner = Row("Inner");
            Assert.Equal("Worker", Caller(inner));
            Assert.True(w["selfMs"].ToNumber() <= w["ms"].ToNumber());
            Assert.True(w["ms"].ToNumber() + 1e-9 >= inner["ms"].ToNumber());
            var sl = Row("Sleep");
            Assert.True(sl["builtin"].Bool);
            Assert.Equal("Worker", Caller(sl));
            Assert.Equal("Start", Caller(Row("TaskNew")));
            Assert.Equal("(task Start)", Caller(Row("Start"))); // (the test runs it as a task)
            // the costliest self time first
            for (int i = 1; i < rows.Count; i++)
                Assert.True(rows[i - 1]["selfMs"].ToNumber() >= rows[i]["selfMs"].ToNumber());
            // the call tree: one root per origin, each call under its caller
            var tree = fp.TreeReport(minShare: 0).Items;
            JsonValue Root(string name) => tree.Single(r => r["name"].Str == name);
            JsonValue Kid(JsonValue n, string name) => n["children"].Items.Single(c => c["name"].Str == name);
            var wr = Root("(task Worker)");
            Assert.Equal("origin", wr["kind"].Str);
            var wn = Kid(wr, "Worker");
            Assert.Equal("cicode", wn["kind"].Str);
            Assert.Equal(1, wn["calls"].Long);
            Assert.Equal("builtin", Kid(wn, "Sleep")["kind"].Str);
            var inn = Kid(wn, "Inner");
            Assert.True(wn["ms"].ToNumber() + 1e-9 >= inn["ms"].ToNumber());
            Assert.True(wr["ms"].ToNumber() + 1e-9 >= wn["ms"].ToNumber());
            Assert.Equal("TaskNew", Kid(Kid(Root("(task Start)"), "Start"), "TaskNew")["name"].Str);
            // a node's own time plus its children's totals is its total (no '(other)' here)
            double kids = wn["children"].Items.Sum(c => c["ms"].ToNumber());
            Assert.True(System.Math.Abs(wn["selfMs"].ToNumber() + kids - wn["ms"].ToNumber()) < 0.05);
            fp.Reset();
            Assert.Equal(0, fp.Report(50, out _).Count);
            Assert.Equal(0, fp.TreeReport().Count);
        }
    }
}
