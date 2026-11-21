using System;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>The older alarm functions (by record, at the
    /// cursor, the index-based summary), server and process control with ServerRPC, the
    /// message sessions and the 'not supported' table entries.</summary>
    [Collection("World")]
    public sealed class RestMoreTests : DataTestBase
    {
        private Sim MakeServers(Action<ProgramBuilder>? build = null) =>
            MakeData(
                () =>
                {
                    Dbf(Path.Combine(MainDir, "cluster.dbf"), new[] { "NAME" }, new[] { "Cluster1" });
                    Dbf(
                        Path.Combine(MainDir, "almsrv.dbf"),
                        new[] { "NAME", "CLUSTER", "MODE" },
                        new[] { "AlarmServer1", "Cluster1", "Primary" }
                    );
                    Dbf(
                        Path.Combine(MainDir, "repsrv.dbf"),
                        new[] { "NAME", "CLUSTER", "MODE" },
                        new[] { "ReportServer1", "Cluster1", "Primary" }
                    );
                    Dbf(
                        Path.Combine(MainDir, "users.dbf"),
                        new[] { "NAME", "FULL_NAME", "ROLES" },
                        new[] { "Rpc", "Rpc user", "Rpc" },
                        new[] { "Plain", "Plain user", "Plain" }
                    );
                    Dbf(
                        Path.Combine(MainDir, "roles.dbf"),
                        new[] { "NAME", "PRIV_G", "ALLOW_RPC" },
                        new[] { "Rpc", "1..8", "TRUE" },
                        new[] { "Plain", "1..8", "FALSE" }
                    );
                },
                build
            );

        // ------------------------------------------------------------------ alarms by record
        [Fact]
        public void AlarmRecordActionsAndLimits()
        {
            var s = MakeData();
            s.W.Tags.Set("Pump1_Run", 1);
            s.W.RunFor(1);
            var db = s.W.Alarms;
            long rec = Call(s, "AlarmFirstTagRec", "ALM_PUMP", "", "").I;
            Assert.True(rec >= 0);
            var a = db.Get("ALM_PUMP")!;
            Assert.False(db.PeekState(a)!.Ack);
            var r = Call(s, "AlarmAckRec", rec);
            Assert.True(r.Blocked);
            Assert.Equal(0, r.I);
            Assert.True(db.PeekState(a)!.Ack);
            Assert.Equal(0, Call(s, "AlarmDisableRec", rec, "", 0, "maintenance").I);
            Assert.True(db.PeekState(a)!.Disabled);
            r = Call(s, "AlarmDisableRec", rec, "", 1000, ""); // an end time in the past
            Assert.Equal(274, r.I);
            Assert.Equal(274, r.Error);
            Assert.Equal(0, Call(s, "AlarmEnableRec", rec, "", 1).I);
            Assert.False(db.PeekState(a)!.Disabled);
            Assert.Equal(274, Call(s, "AlarmAckRec", 9999).I);
            Assert.Equal(282, CallFg(s, "AlarmAckRec", rec).Error);
            // thresholds and delays of the analog alarm
            long temp = Call(s, "AlarmFirstTagRec", "ALM_TEMP", "", "").I;
            Assert.Equal(0, Call(s, "AlarmSetThresholdRec", temp, 0, "130").I);
            Assert.Equal(130.0, Call(s, "AlarmGetThresholdRec", temp, 0).D);
            Assert.Equal(274, Call(s, "AlarmSetThresholdRec", temp, 7, "1").I);
            Assert.Equal(274, Call(s, "AlarmSetThresholdRec", temp, 1, "abc").I);
            Assert.Equal(0, Call(s, "AlarmSetDelayRec", temp, 1, "7").I);
            Assert.Equal(7, Call(s, "AlarmGetDelayRec", temp, 1).I);
            Assert.Equal(274, Call(s, "AlarmSetDelayRec", temp, 1, "-3").I);
            s.W.Tags.Set("Tank1_Temp", 125.0); // above the old HH of 120, below the new 130
            s.W.RunFor(5);
            Assert.NotEqual("HIGHHIGH", db.PeekState(db.Get("ALM_TEMP")!)?.Sub?.ToUpperInvariant());
        }

        [Fact]
        public void TheIndexedSummaryApi()
        {
            var s = MakeData();
            AlarmEventLog.Attach(s.W);
            Assert.Equal(-1, Call(s, "AlarmSumFirst").I);
            s.W.Tags.Set("Pump1_Run", 1);
            s.W.RunFor(1);
            var log = AlarmEventLog.Of(s.W)!;
            long first = Call(s, "AlarmSumFirst").I;
            Assert.True(first > 0);
            // First / Next walk the whole summary, oldest first, to Last
            long idx = first,
                last = first;
            int n = 0;
            while (idx != -1 && n < 100)
            {
                n++;
                last = idx;
                idx = Call(s, "AlarmSumNext", idx).I;
            }
            Assert.Equal(log.Summary.Count, n);
            Assert.Equal(last, Call(s, "AlarmSumLast").I);
            first = log.Summary.First(e => e.Def.Tag == "ALM_PUMP").Seq;
            Assert.Equal("ALM_PUMP", Call(s, "AlarmSumGet", first, "Tag").S);
            Assert.Equal(0, Call(s, "AlarmSumSet", first, "Comment", "seen").I);
            Assert.Equal("seen", Call(s, "AlarmSumGet", first, "Comment").S);
            Assert.Equal(0, Call(s, "AlarmSumType", first).I); // a digital alarm
            long app = Call(s, "AlarmSumAppend", "ALM_PUMP").I;
            Assert.True(app > last);
            Assert.Equal(app, Call(s, "AlarmSumNext", last).I);
            Assert.Equal(last, Call(s, "AlarmSumPrev", app).I);
            long ev = Call(s, "AlarmSumAppend", "*Shift change").I;
            Assert.Equal("*Shift change", Call(s, "AlarmSumGet", ev, "Tag").S);
            Assert.Equal(5, Call(s, "AlarmSumType", ev).I); // a user event
            Assert.Equal(-1, Call(s, "AlarmSumAppend", "NO_SUCH_ALARM").I);
            long split = Call(s, "AlarmSumSplit", first, "", 0).I;
            Assert.True(split > app);
            Assert.Equal(-1, Call(s, "AlarmSumSplit", first, "", 0).I); // it has an OFF time now
            Assert.Equal(0, Call(s, "AlarmSumCommit", first).I);
            Assert.Equal(274, Call(s, "AlarmSumCommit", first).I); // not twice
            Assert.Equal(0, Call(s, "AlarmSumDelete", app).I);
            Assert.Equal(split, Call(s, "AlarmSumNext", last).I);
            long on = (long)Math.Floor(s.W.Clock.StartUnix + log.Summary.First(e => e.Seq == first).OnT);
            long rec = Call(s, "AlarmFirstTagRec", "ALM_PUMP", "", "").I;
            Assert.Equal(first, Call(s, "AlarmSumFind", rec, on).I);
        }

        [Fact]
        public void NotSupportedEntriesGiveZeroAndColoursSwap()
        {
            var s = MakeData();
            foreach (
                string n in new[]
                {
                    "AlarmSaveType",
                    "AlarmSetPriority",
                    "AlarmSetPriorityRec",
                    "ClusterSetName",
                    "ClusterGetName",
                }
            )
            {
                var r = Call(s, n, 1, 2, 3);
                Assert.Equal(0, r.I);
                Assert.Equal(0, r.Error);
            }
            Assert.Equal(0x332211, Call(s, "CitectColourToPackedRGB", 0x112233).I);
            Assert.Equal(0x332211, Call(s, "PackedRGBToCitectColour", 0x7F112233).I); // the top byte dropped
        }

        // ------------------------------------------------------------------ servers
        [Fact]
        public void ServerRpcRunsTheCallHere()
        {
            var s = MakeServers(pb =>
            {
                pb.Function("Echo", "7", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .ConstStr("!")
                    .Op("Add")
                    .RetValue()
                    .End();
                Ret(pb, "Rpc", "ServerRPC", "AlarmServer1", "Echo", "\"hi\"", 0);
                Ret(pb, "RpcAsync", "ServerRPC", "AlarmServer1", "Echo", "\"hi\"", 1);
                Ret(pb, "RpcNowhere", "ServerRPC", "Nowhere", "Echo", "\"hi\"", 0);
            });
            var r = s.Call("Rpc");
            Assert.Equal(513, r.Task.Error); // the default user
            s.W.Users.Login("Plain");
            Assert.Equal(513, s.Call("Rpc").Task.Error); // no Allow RPC
            s.W.Users.Login("Rpc");
            Assert.Equal("hi!", S(s.Result("Rpc")));
            Assert.Equal("", S(s.Result("RpcAsync")));
            Assert.Equal(418, s.Call("RpcNowhere").Task.Error);
        }

        [Fact]
        public void ARestartTakesTheServerOfflineForItsSeconds()
        {
            var s = MakeServers();
            Call(s, "ParameterPut", "Shutdown", "NetworkStart", "1");
            Assert.Equal(1, Call(s, "ServerIsOnline", "AlarmServer1", "", 1).I);
            Assert.Equal(0, Call(s, "ServerRestart", "AlarmServer1").I);
            Assert.Equal(0, Call(s, "ServerIsOnline", "alarmserver1", "", 1).I); // (restarting)
            Assert.Equal(1, Call(s, "ServerIsOnline", "ReportServer1", "", 1).I); // (the others answer)
            s.W.RunFor(CicodeSim.World.ServerStates.RestartSeconds + 1);
            Assert.Equal(1, Call(s, "ServerIsOnline", "AlarmServer1", "", 1).I);
            // a scenario's setServer: offline until it is brought back, a restart does not end it
            CicodeSim.World.ServerStates.Of(s.W).SetOnline("ReportServer1", false);
            Call(s, "ServerRestart", "ReportServer1");
            s.W.RunFor(CicodeSim.World.ServerStates.RestartSeconds + 1);
            Assert.Equal(0, Call(s, "ServerIsOnline", "ReportServer1", "", 1).I);
            CicodeSim.World.ServerStates.Of(s.W).SetOnline("ReportServer1", true);
            Assert.Equal(1, Call(s, "ServerIsOnline", "ReportServer1", "", 1).I);
        }

        [Fact]
        public void ServerAndProcessStubs()
        {
            var s = MakeServers();
            Assert.Equal(513, Call(s, "ServerRestart", "AlarmServer1").I); // [Shutdown]NetworkStart not set
            Assert.Equal(0, Call(s, "ServerReload", "AlarmServer1", "Cluster1", 0).I);
            var r = Call(s, "ServerReload", "Nowhere");
            Assert.Equal(418, r.I);
            Assert.Equal(418, r.Error);
            Assert.Equal(0, Call(s, "ServerControl", "alarm", 0, 1).I);
            Assert.Equal(274, Call(s, "ServerControl", "trend", 0, 1).I);
            Assert.Equal(0, Call(s, "ProcessRestart").I);
            Assert.Equal(293, Call(s, "ResetScreenProfile").I);
            Assert.Equal(0, Call(s, "SwitchConfig", 1).I);
            Assert.Equal(0, Call(s, "__ObjectMasterAN", 2, 10, 0).I);
            Assert.Equal(55, Call(s, "__ObjectMasterAN", 2, 10, 55).I);
            Assert.Equal(55, Call(s, "__ObjectMasterAN", 2, 10, 0).I);
            Assert.Equal(-1, Call(s, "__ObjectMasterAN", 2, 750, 0).I);
            Assert.True(s.Run.Vm.NotSimulatedCounts.ContainsKey("ProcessRestart"));
        }

        // ------------------------------------------------------------------ messages
        [Fact]
        public void MessageSessionsDeliverToThePostFunctions()
        {
            var s = MakeServers(pb =>
            {
                // the server's post function: MsgRead(Type, Str); TagWrite("Name_Str", Str); TagWrite("Count_Long", Type)
                pb.Function("SrvPost", "", Types.Long)
                    .Begin()
                    .StackVar(Types.Long, Types.String)
                    .AddrStack(0)
                    .AddrStack(1)
                    .Func("MsgRead", 2)
                    .Op("Pop")
                    .ConstStr("Name_Str")
                    .LoadStack(1)
                    .Func("TagWrite", 2)
                    .Op("Pop")
                    .ConstStr("Count_Long")
                    .LoadStack(0)
                    .Convert(Types.String)
                    .Func("TagWrite", 2)
                    .Op("Pop")
                    .ConstInt(0)
                    .RetValue()
                    .End();
                Ret(pb, "SrvOpen", "MsgOpen", "Alarm", 1, "SrvPost");
                pb.Function("CliSend", "", Types.Long)
                    .Begin()
                    .StackVar(Types.Long)
                    .ConstStr("Alarm")
                    .ConstInt(0)
                    .ConstStr("")
                    .Func("MsgOpen", 3)
                    .StoreStack(0)
                    .LoadStack(0)
                    .ConstInt(7)
                    .ConstStr("ping")
                    .Func("MsgWrite", 3)
                    .Op("Pop")
                    .LoadStack(0)
                    .RetValue()
                    .End();
                pb.Function("Echo", "7", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .ConstStr("!")
                    .Op("Add")
                    .RetValue()
                    .End();
                pb.Function("CliRpc", "", Types.String)
                    .Begin()
                    .StackVar(Types.Long)
                    .ConstStr("Alarm")
                    .ConstInt(0)
                    .ConstStr("")
                    .Func("MsgOpen", 3)
                    .StoreStack(0)
                    .LoadStack(0)
                    .ConstStr("Echo")
                    .ConstStr("\"x\"")
                    .ConstInt(0)
                    .Func("MsgRPC", 4)
                    .RetValue()
                    .End();
            });
            Assert.Equal(Messages.ServerSide, s.Result("SrvOpen").I);
            long h = s.Result("CliSend").I;
            Assert.True(h >= 0);
            s.Run.Scheduler.Run();
            Assert.Equal("ping", S(s.W.Tags.Get("Name_Str")));
            Assert.Equal(7, s.W.Tags.Get("Count_Long").I);
            Assert.Equal(1, Call(s, "MsgState", h).I);
            Assert.Equal(-1, Call(s, "MsgState", 999).I);
            Assert.Equal(-1, Call(s, "MsgRead", Ref(Value.Long(0)), Ref(Value.Str(""))).I); // not in a post function
            Assert.Equal(-1, Call(s, "MsgGetCurr").I);
            Assert.Equal(0, Call(s, "MsgClose", "Alarm", h).I);
            Assert.Equal(257, Call(s, "MsgClose", "Alarm", 999).I);
            s.W.Users.Login("Rpc");
            Assert.Equal("x!", S(s.Result("CliRpc")));
            Assert.Equal(-1, Call(s, "MsgOpen", "NoServer", 0, "").I);
        }
    }
}
