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
    /// <summary>The remaining built-in families.</summary>
    [Collection("World")]
    public sealed class RestBuiltinsTests : DataTestBase
    {
        // ------------------------------------------------------------------ clipboard
        [Fact]
        public void TheClipboardIsTheSandboxs()
        {
            var s = MakeData();
            var x = ProcessExtras.Of(s.Host);
            Assert.Equal("", Call(s, "ClipPaste").S);
            Assert.Equal("", Call(s, "ClipReadLn").S);
            Assert.Equal(0, Call(s, "ClipCopy", "put this").I);
            Assert.Equal("put this\r\n", x.ClipText);
            Assert.Null(x.ClipCsv);
            Assert.Equal("put this\r\n", Call(s, "ClipPaste").S);
            // a block of lines, read back line by line; an empty line in it reads as its CR LF
            Call(s, "ClipWriteLn", "first");
            Assert.Equal("put this\r\n", x.ClipText); // not until the block ends
            Call(s, "ClipWriteLn", "second");
            Assert.Equal(0, Call(s, "ClipWriteLn", "").I);
            Assert.Equal("first\r\nsecond\r\n", x.ClipText);
            Assert.Equal("first", Call(s, "ClipReadLn").S);
            Assert.Equal("second", Call(s, "ClipReadLn").S);
            Assert.Equal("", Call(s, "ClipReadLn").S);
            Assert.Equal("first", Call(s, "ClipReadLn").S); // starts again
            x.ClipText = "a\r\n\r\nb";
            x.ClipReading = null;
            Assert.Equal("a", Call(s, "ClipReadLn").S);
            Assert.Equal("\r\n", Call(s, "ClipReadLn").S);
            Assert.Equal("b", Call(s, "ClipReadLn").S);
            Assert.Equal("", Call(s, "ClipReadLn").S);
            // a long line comes in pieces of 255
            x.ClipText = new string('x', 300);
            Assert.Equal(255, Call(s, "ClipReadLn").S.Length);
            Assert.Equal(45, Call(s, "ClipReadLn").S.Length);
            // CSV mode: the text format goes away, ClipReadLn reads the CSV
            Assert.Equal(2, Call(s, "ClipSetMode", 2).I); // the runtime returns what it was given
            Call(s, "ClipCopy", "100,200");
            Assert.Null(x.ClipText);
            Assert.Equal("100,200\r\n", x.ClipCsv);
            Assert.Equal("", Call(s, "ClipPaste").S);
            Assert.Equal("100,200", Call(s, "ClipReadLn").S);
            Assert.Equal(3, Call(s, "ClipSetMode", 3).I);
            Call(s, "ClipCopy", "both");
            Assert.Equal("both\r\n", x.ClipText);
            Assert.Equal("both\r\n", x.ClipCsv);
        }

        // ------------------------------------------------------------------ CtAPI
        [Fact]
        public void CapiPostAnswersOnlyAPendingQuery()
        {
            var s = MakeData();
            var x = ProcessExtras.Of(s.Host);
            var r = Call(s, "CAPIPost", "Query0", 0);
            Assert.Equal(289, r.I);
            Assert.Equal(0, r.Error); // returned, not raised
            var t = new CicodeTask(s.Run.Vm, "query", false);
            var q = x.BeginCapiQuery(t);
            var b = Func0Table.Default.Lookup("CAPIPost")!;
            var v = s.Run.Vm.Builtins!.Call(b.Index, t, ArgList.Of(Value.Str("Query1"), Value.Long(0)));
            Assert.Equal(0, v.I);
            Assert.True(q.Answered);
            Assert.Equal("Query1", q.Device);
            Assert.Same(q, x.CapiPosted.Last());
            Assert.Equal(
                289,
                s.Run.Vm.Builtins.Call(b.Index, t, ArgList.Of(Value.Str("Query1"), Value.Long(0))).I
            ); // answered once
        }

        // ------------------------------------------------------------------ language
        [Fact]
        public void GetLanguageGivesTheLocalLanguage()
        {
            var s = MakeData();
            Assert.Equal("English", Call(s, "GetLanguage", 0).S);
            Assert.Equal("en", Call(s, "GetLanguage", 1).S);
            var r = Call(s, "GetLanguage", 2);
            Assert.Equal("", r.S);
            Assert.Equal(274, r.Error);
            var d = MakeData(() =>
            {
                Dbf(
                    Path.Combine(MainDir, "param.dbf"),
                    new[] { "SECTION", "NAME", "DATA" },
                    new[] { "Language", "LocalLanguage", "German(Germany)" }
                );
                Dbf(
                    Path.Combine(MainDir, "lang.dbf"),
                    new[] { "Language", "SRC_FILE" },
                    new[] { "German(Germany)", "german.dbf" },
                    new[] { "French(France)", "french.dbf" }
                );
                Dbf(
                    Path.Combine(MainDir, "users.dbf"),
                    new[] { "NAME", "FULL_NAME", "ROLES" },
                    new[] { "Op", "Operator", "" }
                );
            });
            Assert.Equal("German(Germany)", Call(d, "GetLanguage", 0).S);
            Assert.Equal("de-DE", Call(d, "GetLanguage", 1).S);
            Assert.True(LanguageInfo.Select(d.Host, "French (France)"));
            Assert.Equal("fr-FR", Call(d, "GetLanguage", 1).S);
            Assert.Equal(0, Call(d, "SetLanguage", "x", 0).I); // reads the parameter again
            Assert.Equal("de-DE", Call(d, "GetLanguage", 1).S);
        }

        // ------------------------------------------------------------------ servers
        private Sim MakeServers() =>
            MakeData(() =>
            {
                Dbf(Path.Combine(MainDir, "cluster.dbf"), new[] { "NAME" }, new[] { "Cluster1" });
                Dbf(
                    Path.Combine(MainDir, "almsrv.dbf"),
                    new[] { "NAME", "CLUSTER", "MODE" },
                    new[] { "AlarmServer1", "Cluster1", "Primary" }
                );
                Dbf(
                    Path.Combine(MainDir, "trnsrv.dbf"),
                    new[] { "NAME", "CLUSTER", "MODE" },
                    new[] { "TrendServer1", "Cluster1", "Primary" }
                );
                Dbf(
                    Path.Combine(MainDir, "repsrv.dbf"),
                    new[] { "NAME", "CLUSTER", "MODE" },
                    new[] { "ReportServer1", "Cluster1", "Primary" }
                );
            });

        [Fact]
        public void ServerGetPropertyAnswersForTheOneProcess()
        {
            var s = MakeServers();
            var r = Call(s, "ServerGetProperty", "AlarmServer1", "SyncStatus", "Cluster1");
            Assert.True(r.Blocked);
            Assert.Equal("1", r.S); // no redundant peer
            Assert.Equal("1", Call(s, "ServerGetProperty", "Cluster1.AlarmServer1", "ServerStartState").S);
            Assert.Equal("0", Call(s, "ServerGetProperty", "AlarmServer1", "ReloadStatus").S);
            Assert.Equal("0", Call(s, "ServerGetProperty", "", "SchedulingDiskTime", "Cluster1", "Report").S);
            r = Call(s, "ServerGetProperty", "ReportServer1", "SyncStatus"); // not a report server's
            Assert.Equal("", r.S);
            Assert.Equal(274, r.Error);
            r = Call(s, "ServerGetProperty", "TrendServer1", "NoSuchProperty");
            Assert.Equal(274, r.Error);
            r = Call(s, "ServerGetProperty", "Nowhere", "SyncStatus");
            Assert.Equal(418, r.Error);
            r = Call(s, "ServerGetProperty", "", "SyncStatus", "", "IOServer");
            Assert.Equal(274, r.Error);
            r = CallFg(s, "ServerGetProperty", "AlarmServer1", "SyncStatus");
            Assert.Equal(282, r.Error);
            Assert.Equal("0", Call(s, "ServerGetProperty", "TrendServer1", "LibRDBDiskTime").S); // no _LIBRARY.RDB here
            r = Call(s, "ServerGetProperty", "", "SyncStatus", "Nowhere", "Alarm");
            Assert.Equal(256, r.Error); // no such cluster
            Assert.Equal(0, Call(s, "ClusterDeactivate", "Cluster1").I);
            r = Call(s, "ServerGetProperty", "", "SyncStatus", "Cluster1", "Alarm");
            Assert.Equal(405, r.Error); // the cluster is inactive
            Assert.Equal("", r.S);
        }

        [Fact]
        public void TheCompileTimeIsTheRdbHeaderField()
        {
            if (!HaveR2)
                return;
            string lib = Path.Combine(R2, "ExampleSA", "_LIBRARY.RDB");
            var b = File.ReadAllBytes(lib);
            Assert.Equal((long)BitConverter.ToUInt32(b, 0x106), ServerProps.CompileTime(lib));
            Assert.True(ServerProps.CompileTime(lib) > 1600000000);
        }

        // ------------------------------------------------------------------ alarm summary
        [Fact]
        public void TheAlarmSummaryBrowseWalksTheWorldsSummary()
        {
            var s = MakeData();
            AlarmEventLog.Attach(s.W);
            s.W.Tags.Set("Pump1_Run", 1);
            s.W.RunFor(1);
            s.W.Tags.Set("Pump1_Run", 0);
            s.W.RunFor(1);
            var log = AlarmEventLog.Of(s.W)!;
            Assert.True(log.Summary.Count >= 2);
            var open = Call(s, "AlmSummaryOpen", "TAG=ALM_PUMP", "");
            Assert.True(open.Blocked);
            long hd = open.I;
            Assert.True(hd >= 0);
            Assert.Equal(1, Call(s, "AlmSummaryNumRecords", hd).I);
            Assert.Equal(0, Call(s, "AlmSummaryNext", hd).I); // right after the open: the oldest
            Assert.Equal("ALM_PUMP", Call(s, "AlmSummaryGetField", hd, "Tag").S);
            Assert.Equal("OFF", Call(s, "AlmSummaryGetField", hd, "SUMSTATE").S); // ON, then OFF, not acknowledged
            var r = Call(s, "AlmSummaryGetField", hd, "NOSUCHFIELD");
            Assert.Equal(413, r.Error);
            Assert.Equal(414, Call(s, "AlmSummarySetFieldValue", hd, "Desc", "x").I);
            Assert.Equal(0, Call(s, "AlmSummarySetFieldValue", hd, "Comment", "checked").I);
            Assert.NotEqual("checked", Call(s, "AlmSummaryGetField", hd, "COMMENT").S); // not before the commit
            Assert.Equal(0, Call(s, "AlmSummaryCommit", hd).I);
            Assert.Equal("checked", Call(s, "AlmSummaryGetField", hd, "COMMENT").S);
            Assert.Equal(412, Call(s, "AlmSummaryNext", hd).I);
            Assert.Equal(0, Call(s, "AlmSummaryPrev", hd).I);
            Assert.Equal(282, CallFg(s, "AlmSummaryFirst", hd).I);
            // the whole summary, newest by Prev right after the open
            long all = Call(s, "AlmSummaryOpen", "", "TAG,SUMSTATE").I;
            int n = (int)Call(s, "AlmSummaryNumRecords", all).I;
            Assert.Equal(log.Summary.Count, n);
            Assert.Equal(0, Call(s, "AlmSummaryPrev", all).I);
            Assert.Equal(log.Summary.Last().Def.Tag, Call(s, "AlmSummaryGetField", all, "TAG").S);
            Assert.Equal(413, Call(s, "AlmSummaryGetField", all, "DESC").Error); // not in its field list
            // delete takes the entry off the World's summary
            Assert.Equal(0, Call(s, "AlmSummaryFirst", hd).I);
            Assert.Equal(0, Call(s, "AlmSummaryDelete", hd).I);
            Assert.Equal(0, Call(s, "AlmSummaryNumRecords", hd).I);
            Assert.Equal(n - 1, log.Summary.Count);
            Assert.Equal(0, Call(s, "AlmSummaryClose", hd).I);
            Assert.Equal(410, Call(s, "AlmSummaryClose", hd).I);
            Assert.Equal(-1, Call(s, "AlmSummaryOpen", "", "", "", -1, 2).I); // an AutoCloseMode the IL refuses
            Assert.Equal(-1, Call(s, "AlmSummaryOpen", "", "", "", -1, 1).I); // auto close needs a page
        }

        [Fact]
        public void AlarmQueryRecordsFollowTheDefinitionOrder()
        {
            var s = MakeData();
            s.W.Tags.Set("Pump1_Run", 1);
            s.W.RunFor(1);
            var db = s.W.Alarms;
            var all = db.All();
            long first = Call(s, "_AlarmQueryFirstRec", 0, 0, -1, 0).I; // active alarms, any category
            Assert.True(first >= 0);
            Assert.True(AlarmDb.Classify(db.PeekState(all[(int)first]), 0));
            long rec = first,
                count = 0;
            while (rec >= 0 && count < 100)
            {
                count++;
                rec = Call(s, "_AlarmQueryNextRec", rec, 0, 0, -1, 0).I;
            }
            Assert.Equal(db.Count(0), count);
            long cat1 = Call(s, "AlarmQueryFirstRec", 1, 4, -1, 0).I; // configured, category 1
            Assert.Equal(1, all[(int)cat1].Category);
            Assert.Equal(-1, Call(s, "AlarmQueryFirstRec", 0, 6, -1, 0).I); // hardware alarms have no record
            var r = CallFg(s, "AlarmQueryFirstRec", 0, 0, -1, 0);
            Assert.Equal(-1, r.I);
            Assert.Equal(282, r.Error);
        }

        [Fact]
        public void AlarmTagFromEquipmentTranslates()
        {
            var s = MakeData();
            Assert.Equal("ALM_PUMP", Call(s, "AlarmTagFromEquipment", "Plant.Tank1.PumpAlarm").S);
            Assert.Equal("ALM_PUMP", Call(s, "AlarmTagFromEquipment", "alm_pump").S);
            Assert.Equal("ALM_PUMP", Call(s, "AlarmTagFromEquipment", "Plant.Tank1").S);
            Assert.Equal("Nothing.Here", Call(s, "AlarmTagFromEquipment", "Nothing.Here").S);
        }

        [Fact]
        public void SoeEventAddJournalsAUserEvent()
        {
            var s = MakeData();
            var r = Call(s, "SOEEventAdd", Value.Int(Types.Timestamp, 1234), "Pump started", "Pump1_Run");
            Assert.True(r.Blocked);
            Assert.Equal(0, r.I);
            var e = ProcessExtras.Of(s.Host).UserEvents.Single();
            Assert.Equal("Pump started", e.Message);
            Assert.Equal("Pump1_Run", e.Tag);
            Assert.Equal(282, CallFg(s, "SOEEventAdd", Value.Int(Types.Timestamp, 1), "x").I);
        }

        [Fact]
        public void AlarmFilterEditAppendEquipmentAppendsTheEquipmentTerms()
        {
            var s = MakeData();
            Assert.Equal(0, Call(s, "AlarmFilterOpen", "F1", 1).I);
            long hd = Call(s, "AlarmFilterEditOpen", "F1").I;
            Assert.Equal(
                0,
                Call(s, "AlarmFilterEditAppendEquipment", hd, "Plant.Tank1,EQUIPMENT=Plant.Tank2", 0).I
            );
            Assert.Equal(0, Call(s, "AlarmFilterEditCommit", hd).I);
            Assert.Contains("EQUIPMENT=Plant.Tank1*", Call(s, "AlarmFilterEditFirst", hd).S);
            Assert.Equal(269, Call(s, "AlarmFilterEditAppendEquipment", 99, "x", 0).I);
        }

        // ------------------------------------------------------------------ accumulators
        [Fact]
        public void AccControlResetsTheAccumulatorTags()
        {
            var s = MakeData(() =>
                Dbf(
                    Path.Combine(MainDir, "accums.dbf"),
                    new[] { "NAME", "TRIGGER", "VALUE", "RUNNING", "STARTS", "INTEGRATE", "CLUSTER" },
                    new[]
                    {
                        "Acc_A",
                        "Pump1_Run",
                        "Motor_Speed",
                        "Tank1_Level",
                        "Count_Long",
                        "Tank1_Temp",
                        "",
                    },
                    new[] { "Other", "Pump1_Run", "1", "", "", "Tank2_Level", "" }
                )
            );
            s.W.Tags.Set("Tank1_Level", 50);
            s.W.Tags.Set("Count_Long", 7);
            s.W.Tags.Set("Tank1_Temp", 12.5);
            s.W.Tags.Set("Tank2_Level", 3.0);
            Assert.Equal(0, Call(s, "AccControl", "Acc_?", 2).I);
            Assert.Equal(0.0, s.W.Tags.Peek("Count_Long").value.Double);
            Assert.Equal(50.0, s.W.Tags.Peek("Tank1_Level").value.Double);
            Assert.Equal(0, Call(s, "AccControl", "acc*", 1).I);
            Assert.Equal(0.0, s.W.Tags.Peek("Tank1_Level").value.Double);
            Assert.Equal(0.0, s.W.Tags.Peek("Tank1_Temp").value.Double);
            Assert.Equal(3.0, s.W.Tags.Peek("Tank2_Level").value.Double); // another accumulator
            Assert.Equal(274, Call(s, "AccControl", "Acc_A", 9).I);
            Assert.Equal(282, CallFg(s, "AccControl", "Acc_A", 1).I);
            // the browse
            long hd = Call(s, "AccumBrowseOpen", "", "").I;
            Assert.Equal(2, Call(s, "AccumBrowseNumRecords", hd).I);
            Assert.Equal(0, Call(s, "AccumBrowseFirst", hd).I);
            Assert.Equal("Acc_A", Call(s, "AccumBrowseGetField", hd, "NAME").S);
            Assert.Equal("0", Call(s, "AccumBrowseGetField", hd, "STARTS").S);
            Assert.Equal(0, Call(s, "AccumBrowseClose", hd).I);
        }

        // ------------------------------------------------------------------ stubs
        [Fact]
        public void TheStubsAnswerAsTheRuntimeWithoutTheThing()
        {
            var s = MakeData();
            var r = Call(s, "DspAnCreateControlObject", 10, "MSCAL.Calendar.7", 100, 100);
            Assert.Equal(Types.Object, r.V.T);
            Assert.Equal(-1, r.V.I);
            Assert.Equal(0, r.Error); // no page: nothing raised
            Assert.True(s.Run.Vm.NotSimulatedCounts.ContainsKey("DspAnCreateControlObject"));
            Assert.Equal(274, Call(s, "ScheduleItemDelete", "Cluster1", 3).I);
            // (the simulation has its own scheduler: World.EquipScheduler)
            r = Call(
                s,
                "ScheduleItemAdd",
                "Cluster1",
                "Plant",
                "On",
                Value.Int(Types.Timestamp, 0),
                Value.Int(Types.Timestamp, 0),
                "x"
            );
            Assert.Equal(1, r.I);
            Assert.Equal(0, r.Error);
            Assert.Equal(410, Call(s, "SchdSpecialFirst", 0).I);
            Assert.Equal(289, Call(s, "IODeviceControl", "NoDevice", 1, "1").I);
        }
    }
}
