using System;
using System.IO;
using System.Linq;
using CicodeSim.Tests.Vm;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;
using static CicodeSim.Tests.World.WorldFixture;
using Quality = CicodeSim.World.Quality;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.World
{
    /// <summary>The VM against the World: the VM's Load / Store / Index / Bar go to
    /// TagDb on the VM's own task, simulated time moves for Sleep / SleepMS, and real compiled
    /// Example / ExampleSA functions (R2, read only) read and write the world's tags.</summary>
    [Collection("World")]
    public sealed class WorldVmTests : IDisposable
    {
        private readonly TempDir _tmp = new TempDir();

        public void Dispose() => _tmp.Dispose();

        /// <summary>A number value's payload (an INT tag reads as LONG, a scaled or REAL one as REAL).</summary>
        private static double N(Value v) => Types.IsRealType(v.T) ? v.D : v.I;

        private (WorldT, WorldRun) Synthetic(Action<ProgramBuilder> build)
        {
            var w = MakeWorld(_tmp.Path);
            var pb = new ProgramBuilder();
            build(pb);
            return (w, new WorldRun(w, pb.Build()));
        }

        [Fact]
        public void LoadAndStoreGoThroughTagDbOnTheVmTask()
        {
            var (w, run) = Synthetic(pb =>
            {
                pb.Function("Bump").Load("Count_Long").ConstInt(5).Op("Add").Store("Count_Long").Ret().End();
                pb.Function("ReadNope", "", Types.Real).Load("NoSuchTag").RetValue().End();
            });
            w.Tags.Set("Count_Long", 37);
            var r = run.Call("Bump");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(42L, w.Tags.Get("Count_Long").I);
            Assert.Single(r.Changes);
            Assert.Equal("Count_Long", r.Changes[0].Name);
            Assert.Contains("Count_Long", w.Tags.Used);
            r = run.Call("ReadNope");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(TagDb.ErrTagNotFound, r.Task.TagError);
            Assert.Equal(TagDb.ErrTagNotFound, r.Task.Error);
            Assert.Equal(Quality.QualityTagNotFound, r.Task.Result!.Value.Q);
        }

        [Fact]
        public void IndexedLoadAndStoreUseTheElementAccess()
        {
            var (w, run) = Synthetic(pb =>
            {
                pb.Function("Put").ConstReal(2.5).ConstInt(4).Index(10).Store("Arr_Real").Ret().End();
                pb.Function("Get", "", Types.Real).ConstInt(4).Index(10).Load("Arr_Real").RetValue().End();
                pb.Function("Past", "", Types.Real).ConstInt(12).Index(10).Load("Arr_Real").RetValue().End();
            });
            Assert.Equal("stop", run.Call("Put").Outcome);
            Assert.Equal(2.5, w.Tags.Get("Arr_Real", index: 4).D);
            Assert.Equal(0.0, w.Tags.Get("Arr_Real", index: 3).D);
            Assert.Equal(2.5, run.Call("Get").Task.Result!.Value.D);
            var past = run.Call("Past").Task; // a 1-dimension index is passed unchecked
            Assert.Equal(TagDb.ErrArrayOverrun, past.TagError);
            Assert.Equal(TagDb.ErrArrayOverrun, past.Error);
        }

        [Fact]
        public void BarWithoutScaleUsesTheTagsEngineeringRange()
        {
            // Bar 0x37, operand -1: the last tag's own scale; Tank1_Temp is 0..150
            var (w, run) = Synthetic(pb =>
                pb.Function("Pct", "", Types.Real).Load("Tank1_Temp").Bar(0xFFFFFFFF).RetValue().End()
            );
            w.Tags.Set("Tank1_Temp", 75.0);
            var t = run.Call("Pct").Task;
            Assert.Equal(50.0, t.Result!.Value.D);
            Assert.Equal(0, t.Error);
            w.Tags.Set("Tank1_Temp", 160.0);
            t = run.Call("Pct").Task;
            Assert.Equal(100.0, t.Result!.Value.D); // clamped, op error 257
            Assert.Equal(257, t.Error);
            Assert.True(
                w.Tags.TryScale(Ref("Tank1_Level"), null!, out double z, out double f)
                    && z == 0.0
                    && f == 100.0
            );
            Assert.False(w.Tags.TryScale(Ref("Name_Str"), null!, out _, out _));
            Assert.False(w.Tags.TryScale(Ref("NoSuchTag"), null!, out _, out _));
            Assert.False(w.Tags.TryScale(null, null!, out _, out _));
        }

        [Fact]
        public void StoreHonoursTheTasksScaleCheck()
        {
            // the VM task's ScaleCheck reaches TagDb's write: Tank1_Temp is 0..150, so 200 is
            // refused (257, the expression error) until CodeSetMode(1, 0) turns the check off
            var (w, run) = Synthetic(pb =>
            {
                pb.Function("Hot").ConstReal(200.0).Store("Tank1_Temp").Ret().End();
                pb.Function("HotUnchecked")
                    .ConstInt(1)
                    .ConstInt(0)
                    .Func("CodeSetMode", 2)
                    .Op("Pop")
                    .ConstReal(200.0)
                    .Store("Tank1_Temp")
                    .Ret()
                    .End();
            });
            w.Tags.Set("Tank1_Temp", 75.0);
            var r = run.Call("Hot");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(TagDb.ErrOutOfRange, r.Task.ExprError);
            Assert.Equal(75.0, w.Tags.Get("Tank1_Temp").D);
            Assert.Empty(r.Changes);
            r = run.Call("HotUnchecked");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(0, r.Task.ExprError);
            Assert.Equal(200.0, w.Tags.Get("Tank1_Temp").D);
        }

        [Fact]
        public void SleepMovesSimulatedTimeAndLateWritesArrive()
        {
            // SleepMS(300) costs no real time: the run advances the SimClock to the wake-up; a
            // write without WriteLocal (CodeSetMode(0, 0)) arrives after the world's write delay
            var (w, run) = Synthetic(pb =>
                pb.Function("Slow")
                    .ConstInt(0)
                    .ConstInt(0)
                    .Func("CodeSetMode", 2)
                    .Op("Pop")
                    .ConstInt(300)
                    .Func("SleepMS", 1)
                    .Op("Pop")
                    .ConstReal(7.5)
                    .Store("Motor_Speed")
                    .Ret()
                    .End()
            );
            var r = run.Call("Slow");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(0.3 + w.Tags.WriteDelay, r.SimSeconds, 9);
            Assert.Equal(7.5, w.Tags.Get("Motor_Speed").D);
            Assert.Equal(0, w.Tags.PendingWrites());
        }

        [Fact]
        public void TimeBudgetEndsAWaitingLoop()
        {
            var (w, run) = Synthetic(pb =>
            {
                var f = pb.Function("Loop");
                f.Label("top")
                    .Load("Count_Long")
                    .ConstInt(1)
                    .Op("Add")
                    .Store("Count_Long")
                    .ConstInt(1)
                    .Func("Sleep", 1)
                    .Op("Pop")
                    .Branch("top")
                    .End();
            });
            var r = run.Call("Loop", maxSeconds: 5.0);
            Assert.Equal("waits", r.Outcome);
            Assert.Equal(6L, w.Tags.Get("Count_Long").I); // at t = 0, 1, ..., 5
            Assert.Equal(5.0, r.SimSeconds, 9);
        }

        [Fact]
        public void OtherLiveTasksWakeAtTheirOwnTimes()
        {
            // a loop an earlier call left running (SleepMS(100) per step) keeps its own pace while
            // a later call sleeps: the clock goes to the NEXT wake-up of any task, not straight to
            // the caller's
            var (w, run) = Synthetic(pb =>
            {
                var f = pb.Function("Ticker");
                f.Label("top")
                    .Load("Count_Long")
                    .ConstInt(1)
                    .Op("Add")
                    .Store("Count_Long")
                    .ConstInt(100)
                    .Func("SleepMS", 1)
                    .Op("Pop")
                    .Branch("top")
                    .End();
                pb.Function("Nap").ConstInt(1).Func("Sleep", 1).Op("Pop").Ret().End();
            });
            w.Tags.Set("Count_Long", 0);
            var r = run.Call("Ticker", maxSeconds: 0.0);
            Assert.Equal("waits", r.Outcome);
            Assert.Equal(1L, w.Tags.Get("Count_Long").I);
            r = run.Call("Nap");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(1.0, r.SimSeconds, 6);
            Assert.Equal(11L, w.Tags.Get("Count_Long").I); // t = 0, 0.1, ..., 1.0
        }

        // -- real compiled functions (R2 corpus, read only) ------------------------------------

        private static WorldRun? Real(string project)
        {
            string dir = Path.Combine(Corpus.User, project);
            if (!File.Exists(Path.Combine(dir, "_LIBRARY.RDB")))
                return null;
            WorldT.ClearCaches();
            return WorldRun.ForProject(dir, start: DefaultStart);
        }

        [CorpusFact]
        public void ExampleStockPileSimReadsAndWritesTags()
        {
            // Example\Simulatr.ci: IF DiskFilter THEN STOCKPILE = STOCKPILE + 2; IF STOCKPILE = 100 THEN STOCKPILE = 0
            var run = Real("Example");
            if (run == null)
                return;
            var tags = run.World.Tags;
            tags.Set("DiskFilter", 1);
            tags.Set("Stockpile", 10);
            Assert.Equal("stop", run.Call("StockPileSim").Outcome);
            Assert.Equal(12.0, N(tags.Get("Stockpile")));
            tags.Set("Stockpile", 98);
            run.Call("StockPileSim");
            Assert.Equal(0.0, N(tags.Get("Stockpile")));
            tags.Set("DiskFilter", 0);
            tags.Set("Stockpile", 40);
            var r = run.Call("StockPileSim");
            Assert.Equal(40.0, N(tags.Get("Stockpile")));
            Assert.Empty(r.Changes);
            Assert.Equal(new[] { "DiskFilter", "STOCKPILE" }, tags.Used.Take(2));
        }

        [CorpusFact]
        public void ExampleWaterTankLevelRunsOnSimulatedTime()
        {
            // WHILE TRUE: below 8000 the low switch fills the tank by 550 a second; Sleep(1)
            var run = Real("Example");
            if (run == null)
                return;
            var tags = run.World.Tags;
            tags.Set("Water_Level", 5000);
            tags.Set("Valve_5_CMD", 0);
            var r = run.Call("WaterTankLevel", maxSeconds: 5.0);
            Assert.Equal("waits", r.Outcome);
            Assert.Equal(5.0, r.SimSeconds, 9);
            Assert.Equal(5000 + 6 * 550, N(tags.Get("Water_Level")));
            Assert.Equal(1.0, N(tags.Get("Water_Tank_Lo_Switch")));
            run.Vm.Halt(r.Task, "test"); // else the first loop keeps running too
            tags.Set("Valve_5_CMD", 1); // drains 600 a second while it fills 550
            r = run.Call("WaterTankLevel", maxSeconds: 2.0);
            Assert.Equal(8300 - 3 * 50, N(tags.Get("Water_Level")));
        }

        [CorpusFact]
        public void ExampleSaCapFillWritesEquipmentItems()
        {
            // ExampleSA\Bottling.ci ProdLine_SimulateCapFill: Equip.Item references
            // (Company.TopMilk.Bottling.CNV02.Position ...) and SleepMS(100) per step
            var run = Real("ExampleSA");
            if (run == null)
                return;
            var tags = run.World.Tags;
            tags.Set("Company.TopMilk.Bottling.CNV02.Position", 95);
            var r = run.Call("ProdLine_SimulateCapFill");
            // the loop's 5 steps: Running 1, Stopped 0, Position + 1 each (what follows the loop,
            // TagWrite after the not simulated SemOpen of EnterCriticalSection, may change later)
            Assert.True(r.SimSeconds >= 0.5 - 1e-9);
            Assert.True(r.Changes.Count >= 15);
            for (int k = 0; k < 5; k++)
            {
                Assert.Equal(
                    ("TopMilk_CNV02_Running", 1.0),
                    (r.Changes[3 * k].Name, r.Changes[3 * k].Value.Double)
                );
                Assert.Equal(
                    ("TopMilk_CNV02_Stopped", 0.0),
                    (r.Changes[3 * k + 1].Name, r.Changes[3 * k + 1].Value.Double)
                );
                Assert.Equal(
                    ("TopMilk_CNV02_Position", 96.0 + k),
                    (r.Changes[3 * k + 2].Name, r.Changes[3 * k + 2].Value.Double)
                );
            }
            if (r.Changes.Count == 15)
            {
                Assert.Equal(100.0, N(tags.Get("Company.TopMilk.Bottling.CNV02.Position")));
                Assert.Equal(1.0, N(tags.Get("TopMilk_CNV02_Running")));
                Assert.Equal(0.0, N(tags.Get("TopMilk_CNV02_Stopped")));
            }
            Assert.False(r.Outcome.StartsWith("halt", StringComparison.Ordinal));
        }
    }
}
