using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Compiled tables, menus, users, groups, clusters, servers and I/O devices.</summary>
    [Collection("World")]
    public sealed partial class DataTablesTests : DataTestBase
    {
        private static readonly string[] MenuFields =
        {
            "PAGE",
            "LEVEL1",
            "LEVEL2",
            "LEVEL3",
            "SYMBOL",
            "PRIVILEGE",
            "AREA",
            "ORDER",
            "COMMENT",
            "TARGETPAGE",
            "CUSTOM1",
        };

        private void Menu() =>
            Dbf(
                Path.Combine(MainDir, "pagemenu.dbf"),
                MenuFields,
                new[] { "Overview", "Plant", "", "", "", "", "", "2", "", "", "" },
                new[]
                {
                    "Overview",
                    "Plant",
                    "Tank1",
                    "",
                    "tank_sym",
                    "3",
                    "5",
                    "",
                    "first tank",
                    "Tank1Page",
                    "c1",
                },
                new[] { "Overview", "Plant", "Pumps", "", "", "", "", "1", "", "", "" },
                new[] { "Overview", "Alarms", "", "", "", "", "", "1", "", "AlarmPage", "" },
                new[] { "", "Help", "", "", "", "", "", "", "", "", "" }
            );

        // ------------------------------------------------------------------ menus
        [Fact]
        public void MenuTreeFromPagemenuDbf()
        {
            var s = MakeData(Menu);
            long root = Call(s, "MenuGetPageNode", "overview").I; // pages compare without case
            Assert.True(root > 0);
            Assert.Equal(-1, Call(s, "MenuGetPageNode", "NoPage").I);
            long created = Call(s, "MenuGetPageNode", "NoPage", 1).I;
            Assert.True(created > 0);
            Assert.Equal(created, Call(s, "MenuGetPageNode", "NoPage").I);
            Assert.Equal(0xffff, Call(s, "MenuNodeGetDepth", root).I); // the root's ushort -1
            // children by ORDER: Alarms (1) before Plant (2)
            long first = Call(s, "MenuGetFirstChild", root).I;
            Assert.Equal("Alarms", Call(s, "MenuNodeGetProperty", first, 0).S);
            Assert.Equal("AlarmPage", Call(s, "MenuNodeGetProperty", first, 13).S);
            long plant = Call(s, "MenuGetNextChild", first).I;
            Assert.Equal("Plant", Call(s, "MenuNodeGetProperty", plant, 0).S);
            Assert.Equal(-1, Call(s, "MenuGetNextChild", plant).I);
            Assert.Equal(first, Call(s, "MenuGetPrevChild", plant).I);
            Assert.Equal(-1, Call(s, "MenuGetPrevChild", first).I);
            Assert.Equal(root, Call(s, "MenuGetParent", plant).I);
            Assert.Equal(-1, Call(s, "MenuGetParent", root).I);
            // Pumps (ORDER 1) before Tank1 (none)
            long pumps = Call(s, "MenuGetFirstChild", plant).I;
            Assert.Equal("Pumps", Call(s, "MenuNodeGetProperty", pumps, 0).S);
            long tank = Call(s, "MenuGetChild", plant, "TANK1").I;
            Assert.Equal(tank, Call(s, "MenuGetNodeByPath", root, "Plant.Tank1").I);
            Assert.Equal(-1, Call(s, "MenuGetNodeByPath", root, "Plant.Nope").I);
            Assert.Equal(1, Call(s, "MenuNodeGetDepth", tank).I); // the depth of its parent Plant (0)
            Assert.Equal(0, Call(s, "MenuNodeGetDepth", plant).I);
            Assert.Equal("tank_sym", Call(s, "MenuNodeGetProperty", tank, 1).S);
            Assert.Equal("3", Call(s, "MenuNodeGetProperty", tank, 2).S);
            Assert.Equal("5", Call(s, "MenuNodeGetProperty", tank, 3).S);
            Assert.Equal("first tank", Call(s, "MenuNodeGetProperty", tank, 7).S);
            Assert.Equal("Overview", Call(s, "MenuNodeGetProperty", tank, 12).S);
            Assert.Equal("c1", Call(s, "MenuNodeGetProperty", tank, 101).S);
            Assert.Equal("Tank1Page", Call(s, "MenuNodeGetTargetPage", tank).S);
            Assert.Equal("", Call(s, "MenuNodeGetProperty", 9999, 0).S);
            // set
            Assert.Equal(0, Call(s, "MenuNodeSetProperty", tank, 7, "changed").I);
            Assert.Equal("changed", Call(s, "MenuNodeGetProperty", tank, 7).S);
            Assert.Equal(-1, Call(s, "MenuNodeSetProperty", tank, 12, "x").I);
            Assert.Equal(-1, Call(s, "MenuNodeSetProperty", 9999, 7, "x").I);
            Assert.Equal(0, Call(s, "MenuNodeSetExpanded", tank, 1).I);
            Assert.Equal(1, Call(s, "MenuNodeGetExpanded", tank).I);
            // the generic menu: rows without a PAGE
            long gen = Call(s, "MenuGetGenericNode").I;
            Assert.Equal("Help", Call(s, "MenuNodeGetProperty", Call(s, "MenuGetFirstChild", gen).I, 0).S);
            // a copy has new handles
            long copy = Call(s, "MenuGetPageNode", "Overview", 3).I;
            Assert.NotEqual(root, copy);
            Assert.Equal("Alarms", Call(s, "MenuNodeGetProperty", Call(s, "MenuGetFirstChild", copy).I, 0).S);
            // no compiled code here: no command, not hidden, not disabled
            Assert.Equal(0, Call(s, "MenuNodeHasCommand", tank).I);
            Assert.Equal(0, Call(s, "MenuNodeIsHidden", tank).I);
            Assert.Equal(0, Call(s, "MenuNodeRunCommand", tank).I);
            Assert.Equal(Codes.BadHandle, Call(s, "MenuNodeRunCommand", 9999).I);
            var r = Call(s, "__Tabmenu_GetMenuConfigDisabled", 9999);
            Assert.Equal(Codes.InvalidArg, r.Error);
            Assert.Equal(-1, Call(s, "MenuGetWindowNode", 0).I);
        }

        [Fact]
        public void MenuNodeGetTargetPageWithANegativeHandleIsTheRunningNode()
        {
            // FUNC0.DBF OPTARGS '(-2)': Navigation_ShowTargetPage's MenuNodeGetTargetPage() passes -2
            var s = MakeData(Menu);
            long root = Call(s, "MenuGetPageNode", "Overview").I;
            long tank = Call(s, "MenuGetNodeByPath", root, "Plant.Tank1").I;
            var b = Func0Table.Default.Lookup("MenuNodeGetTargetPage")!;
            string In(object? node, params object[] args)
            {
                var t = new CicodeTask(s.Run.Vm, "menu command", false);
                if (node != null)
                    t.Data["menunode"] = node;
                var vals = new List<Value>();
                foreach (object a in args)
                    vals.Add(CicodeVm.ValueOf(a));
                var r = s.Run.Vm.Builtins!.Call(b.Index, t, ArgList.Of(vals.ToArray()));
                return r.S ?? "";
            }
            Assert.Equal("Tank1Page", In((int)tank, -2));
            Assert.Equal("Tank1Page", In((int)tank, -1));
            Assert.Equal("Tank1Page", In((int)tank));
            Assert.Equal("", In(null, -2)); // no running command
            Assert.Equal("Tank1Page", In(null, tank)); // an explicit handle
        }

        [Fact]
        public void MenuTreeOfExampleSAFromItsCompiledTable()
        {
            if (!HaveR2)
                return;
            // _PAGEMEN.RDB of ExampleSA (46 records, the COMMAND / HIDDEN / DISABLED code)
            var w = new CicodeSim.World.World(Path.Combine(R2, "ExampleSA"), root: "ExampleSA");
            var lib = Rdb.LoadLibrary(Path.Combine(R2, "ExampleSA", "_LIBRARY.RDB"));
            var run = new WorldRun(w, lib);
            var sim = new Sim
            {
                W = w,
                Run = run,
                Host = WorldBuiltins.HostOf(run.Vm.Builtins)!,
            };
            var f = RdbFile.Open(Path.Combine(R2, "ExampleSA", "_PAGEMEN.RDB"));
            var recs = f.Records("PAGEMEN");
            var pages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in recs)
                pages.Add((string?)rec["PAGE"] ?? "");
            var commands = new HashSet<long>();
            foreach (var b in Rdb.PageFromRdb(f).Blocks)
                if (b.Field == "COMMAND")
                    commands.Add(b.Record);
            // a path given twice keeps the later row's fields
            var lastOfPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in recs)
            {
                var ps = new List<string> { (string?)rec["PAGE"] ?? "" };
                foreach (string lv in new[] { "LEVEL1", "LEVEL2", "LEVEL3", "LEVEL4", "LEVEL5", "LEVEL6" })
                {
                    string x = ((string?)rec[lv] ?? "").Trim();
                    if (x.Length == 0)
                        break;
                    ps.Add(x);
                }
                lastOfPath[string.Join("|", ps)] = (long)rec["_index"]!;
            }
            int found = 0;
            foreach (string p in pages)
            {
                long root = Call(sim, "MenuGetPageNode", p).I;
                Assert.True(root > 0, p);
                found++;
                // every record's path is in the tree
                foreach (var rec in recs)
                {
                    if (!string.Equals((string?)rec["PAGE"] ?? "", p, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parts = new List<string>();
                    foreach (
                        string lv in new[] { "LEVEL1", "LEVEL2", "LEVEL3", "LEVEL4", "LEVEL5", "LEVEL6" }
                    )
                    {
                        string x = ((string?)rec[lv] ?? "").Trim();
                        if (x.Length == 0)
                            break;
                        parts.Add(x);
                    }
                    long n = Call(sim, "MenuGetNodeByPath", root, string.Join(".", parts)).I;
                    Assert.True(n > 0, p + ": " + string.Join(".", parts));
                    long last = lastOfPath[p + "|" + string.Join("|", parts)];
                    if (last != (long)rec["_index"]!)
                        continue;
                    Assert.Equal(commands.Contains(last) ? 1 : 0, Call(sim, "MenuNodeHasCommand", n).I);
                    Assert.Equal(0, Call(sim, "MenuNodeIsHidden", n).I); // HIDDEN: Const 0 (corpus)
                    // record 24: DISABLED = UserInfo(0) = "0" (nobody logged in): 1
                    Assert.Equal(last == 24 ? 1 : 0, Call(sim, "MenuNodeIsDisabled", n).I);
                    Assert.Equal(
                        ((string?)rec["TARGETPAGE"] ?? "").Trim(),
                        Call(sim, "MenuNodeGetTargetPage", n).S
                    );
                }
            }
            Assert.True(found > 0);
        }

        // ------------------------------------------------------------------ compiled tables

        [Fact]
        public void RdbCursorFindAndErrors()
        {
            if (!HaveR2)
                return;
            var w = new CicodeSim.World.World(Path.Combine(R2, "ExampleSA"), root: "ExampleSA");
            var run = new WorldRun(w, new ProgramBuilder().Build());
            var sim = new Sim
            {
                W = w,
                Run = run,
                Host = WorldBuiltins.HostOf(run.Vm.Builtins)!,
            };
            Assert.Equal(-1, Call(sim, "RdbOpen", "_NOSUCH").I);
            long h = Call(sim, "RdbOpen", "_VARIABL").I; // the section 'Variabl'
            Assert.True(h >= 0);
            Assert.Equal(1, Call(sim, "RdbEOF", h).I); // before First
            long hit = Call(sim, "RdbFind", h, "TAG", "wtp_t201_lt").I; // the field Tag, without case
            Assert.True(hit >= 0);
            Assert.Equal("WTP_T201_LT", Call(sim, "RdbGet", h, "tag").S.ToUpperInvariant());
            Assert.Matches("^-?[0-9]+[.][0-9]{6}$", Call(sim, "RdbGet", h, "Deadband").S); // a f32 field: '%f'
            Assert.Equal(-1, Call(sim, "RdbFind", h, "TAG", "wtp_t201_lt").I); // from the next record on
            Assert.Equal(-1, Call(sim, "RdbFirstRec", 999).I);
            Assert.Equal(Codes.InvalidArg, Call(sim, "RdbClose", 999).I);
            Assert.Equal("", Call(sim, "RdbGet", 999, "NAME").S);
            long last = Call(sim, "RdbLastRec", h).I;
            Assert.Equal(Call(sim, "RdbNoRec", h).I - 1, last);
            Assert.Equal(last - 1, Call(sim, "RdbPrevRec", h).I);
            Assert.Equal(3, Call(sim, "RdbPosRec", h, 3).I);
            Assert.Equal(0, Call(sim, "RdbSet", h, "COMMENT", "changed").I);
            Assert.Equal("changed", Call(sim, "RdbGet", h, "COMMENT").S);
        }

        // ------------------------------------------------------------------ users and groups
        [Fact]
        public void UserInfoGetPrivLoginLogout()
        {
            var s = MakeData();
            Assert.Equal("0", Call(s, "UserInfo", 0).S); // the default user
            Assert.Equal(0, Call(s, "GetPriv", 1, 0).I);
            Assert.Equal(1, Call(s, "GetPriv", 0, 0).I); // area 0 is viewable
            var r = Call(s, "GetPriv", 9, 0);
            Assert.Equal(0, r.I);
            Assert.Equal(Codes.InvalidArg, r.Error);
            Assert.Equal(Codes.InvalidArg, Call(s, "GetPriv", 1, 256).Error);
            Assert.Equal(Codes.InvalidArg, Call(s, "GetPriv", -1, 0).Error);
            Assert.Equal(449, Call(s, "Logout").I); // nobody logged in
            Assert.Equal("", Call(s, "FullName").S);
            r = Call(s, "Login", "Nobody", "x");
            Assert.Equal(Codes.InvalidArg, r.I);
            Assert.Equal(Codes.InvalidArg, r.Error);
            Assert.Equal(0, Call(s, "Login", "op", "pw").I);
            Assert.Equal("1", Call(s, "UserInfo", 0).S);
            Assert.Equal("Op", Call(s, "UserInfo", 1).S);
            Assert.Equal("Operator", Call(s, "UserInfo", 2).S);
            Assert.Equal("Operator", Call(s, "FullName").S);
            Assert.Equal("", Call(s, "UserInfo", 7).S);
            Assert.Equal(1, Call(s, "GetPriv", 1, 5).I); // role Op: PRIV_1 = 5
            Assert.Equal(0, Call(s, "GetPriv", 1, 6).I);
            Assert.Equal(0, Call(s, "Login", "OP", "again").I); // the same user: 0
            r = Call(s, "Login", "Engineer", "pw", 1); // bSync: one turn
            Assert.True(r.Blocked);
            Assert.Equal(0, r.I);
            r = CallFg(s, "Login", "Op", "pw", 1);
            Assert.Equal(Codes.FgBlock, r.Error);
            Assert.Equal(Codes.FgBlock, r.I);
            // the foreground call logged Op in before it refused to wait (the login, then CodeBlock)
            Assert.Equal("Op", Call(s, "Name").S);
            Assert.Equal(1, Call(s, "AreaCheck", 5).I); // role Op: privileges in area 5
            Assert.Equal(0, Call(s, "AreaCheck", 200).I);
            Assert.Equal(0, Call(s, "Logout").I);
            Assert.Equal("0", Call(s, "UserInfo", 0).S);
            Assert.Equal("", Call(s, "Name").S);
            Assert.Equal(1, Call(s, "AreaCheck", 0).I);
            Assert.Equal(0, Call(s, "AreaCheck", 6).I);
            Assert.Equal(0, Call(s, "AreaCheck", 300).I);
        }

        [Fact]
        public void MenuNodesAddedAndRemoved()
        {
            // AddedCmd(STRING x): writes x to Name_Str
            var s = MakeData(Menu, pb =>
                pb.Function("AddedCmd", "7").Begin().LoadStack(0).Store("Name_Str").Ret().End()
            );
            long root = Call(s, "MenuGetPageNode", "Overview").I;
            long plant = Call(s, "MenuGetChild", root, "Plant").I;
            long added = Call(s, "MenuNodeAddChild", plant, "Mixer", "AddedCmd", "\"run\"", "mix_sym", 0).I;
            Assert.True(added > 0);
            Assert.Equal(added, Call(s, "MenuGetFirstChild", plant).I); // ORDER 0 first
            Assert.Equal("mix_sym", Call(s, "MenuNodeGetProperty", added, 1).S);
            Assert.Equal(1, Call(s, "MenuNodeHasCommand", added).I);
            var r = Call(s, "MenuNodeRunCommand", added);
            Assert.Equal(0, r.I);
            s.Run.Scheduler.Run();
            Assert.Equal("run", s.W.Tags.Get("Name_Str").S);
            Assert.Equal(-1, Call(s, "MenuNodeAddChild", 9999, "x", "", "", "", 0).I);
            Assert.Equal(0, Call(s, "MenuNodeRemove", plant).I);
            Assert.Equal(-1, Call(s, "MenuGetChild", root, "Plant").I);
            Assert.Equal("", Call(s, "MenuNodeGetProperty", added, 0).S); // gone with its parent
            Assert.Equal(-1, Call(s, "MenuNodeRemove", plant).I);
        }

        [Fact]
        public void GroupsOpenCreateAndMath()
        {
            var s = MakeData(() =>
                Dbf(
                    Path.Combine(MainDir, "groups.dbf"),
                    new[] { "NAME", "ASS1", "ASS2", "ASS3" },
                    new[] { "Plantwide", "1..10", "20", "Section1" },
                    new[] { "Section1", "25", "30..31", "" }
                )
            );
            long area = Call(s, "GetArea").I;
            Assert.Equal(0x4000, area);
            Assert.Equal("0", Call(s, "GrpToStr", area).S); // the default user views area 0
            long pw = Call(s, "GrpOpen", "plantwide", 0).I;
            Assert.True((pw & 0x4000) != 0);
            Assert.Equal("1..10,20,25,30..31", Call(s, "GrpToStr", pw).S);
            Assert.Equal("plantwide", Call(s, "GrpName", pw).S); // the name as opened
            Assert.Equal(1, Call(s, "GrpFirst", pw).I);
            Assert.Equal(20, Call(s, "GrpNext", pw, 10).I);
            Assert.Equal(-1, Call(s, "GrpNext", pw, 31).I);
            Assert.Equal(1, Call(s, "GrpIn", pw, 25).I);
            Assert.Equal(0, Call(s, "GrpIn", pw, 26).I);
            Assert.Equal(pw, Call(s, "GrpOpen", "PLANTWIDE", 2).I); // an open group by name
            Assert.Equal(-1, Call(s, "GrpOpen", "Plantwide", 1).I); // create: it exists
            Assert.Equal(-1, Call(s, "GrpOpen", "Nope", 0).I);
            long g = Call(s, "GrpOpen", "Mine", 1).I;
            Assert.True(g > 0);
            Assert.Equal("", Call(s, "GrpToStr", g).S);
            Assert.Equal(-1, Call(s, "GrpFirst", g).I);
            Assert.Equal(0, Call(s, "StrToGrp", g, "3..5, 7").I);
            Assert.Equal("3..5,7", Call(s, "GrpToStr", g).S);
            Assert.Equal(0, Call(s, "GrpInsert", g, 9).I);
            Assert.Equal(Codes.OutOfRange, Call(s, "GrpInsert", g, 16376).I);
            Assert.Equal(0, Call(s, "GrpDelete", g, 4).I);
            Assert.Equal("3,5,7,9", Call(s, "GrpToStr", g).S);
            long sec = Call(s, "GrpOpen", "Section1", 0).I;
            Assert.Equal(1, Call(s, "GrpIn", pw, sec).I); // a group in a group
            Assert.Equal(0, Call(s, "GrpIn", g, sec).I);
            Assert.Equal(0, Call(s, "GrpMath", g, pw, sec, 1).I); // Plantwide - Section1
            Assert.Equal("1..10,20", Call(s, "GrpToStr", g).S);
            Assert.Equal(0, Call(s, "GrpMath", g, pw, sec, 2).I); // AND
            Assert.Equal("25,30..31", Call(s, "GrpToStr", g).S);
            Assert.Equal(Codes.BadHandle, Call(s, "GrpMath", g, pw, sec, 5).I);
            Assert.Equal("17", Call(s, "GrpToStr", 17).S); // not a group handle: the number
            Assert.Equal(0, Call(s, "GrpClose", g).I);
            Assert.Equal(Codes.BadHandle, Call(s, "GrpClose", g).I);
            Assert.Equal(Codes.BadHandle, Call(s, "GrpInsert", g, 1).I);
        }

        // ------------------------------------------------------------------ clusters, servers
        private void Servers()
        {
            Dbf(
                Path.Combine(MainDir, "cluster.dbf"),
                new[] { "NAME", "COMMENT" },
                new[] { "Cluster1", "" },
                new[] { "Cluster2", "" }
            );
            var srv = new[] { "NAME", "MODE", "CLUSTER", "NETADDR", "PORT", "COMMENT" };
            Dbf(
                Path.Combine(MainDir, "almsrv.dbf"),
                srv,
                new[] { "AlarmServer1", "Primary", "Cluster1", "localhost", "", "alarms" }
            );
            Dbf(
                Path.Combine(MainDir, "trnsrv.dbf"),
                srv,
                new[] { "TrendServer1", "Primary", "Cluster1", "localhost", "", "" }
            );
            Dbf(
                Path.Combine(MainDir, "repsrv.dbf"),
                srv,
                new[] { "ReportServer1", "Primary", "Cluster2", "localhost", "", "" }
            );
            Dbf(
                Path.Combine(MainDir, "units.dbf"),
                new[] { "SERVER", "NAME", "NUMBER", "ADDR", "PROTOCOL", "PORT", "MODE", "COMMENT" },
                new[] { "IOServer1", "PLC1", "3", "1.2", "MODBUS", "Port1", "Primary", "the plc" },
                new[] { "IOServer1", "Internal", "1", "", "CICODE", "", "Primary", "" }
            );
            File.WriteAllText(
                Path.Combine(MainDir, "ServerConfiguration.json"),
                "{\"Clusters\":[{\"Name\":\"Cluster1\",\"Servers\":[{\"Name\":\"IOServer1\",\"HostName\":\"localhost\",\"Port\":2082,\"ServiceType\":\"IO\"}]}]}"
            );
        }

        [Fact]
        public void ClustersAndServers()
        {
            var s = MakeData(Servers);
            Assert.Equal("Cluster1", Call(s, "ClusterFirst").S);
            Assert.Equal("Cluster2", Call(s, "ClusterNext", "CLUSTER1").S);
            Assert.Equal("", Call(s, "ClusterNext", "Cluster2").S);
            Assert.Equal(2, Call(s, "ClusterStatus", "Cluster1", 1).I);
            Assert.Equal(-1, Call(s, "ClusterStatus", "Cluster1", 4).I); // no report server there
            Assert.Equal(2, Call(s, "ClusterStatus", "Cluster2", 4).I);
            Assert.Equal(-2, Call(s, "ClusterStatus", "Nope", 1).I);
            var r = Call(s, "ClusterStatus", "Cluster1", 3);
            Assert.Equal(Codes.InvalidArg, r.Error);
            Assert.Equal(1 | 2 | 8, Call(s, "ClusterServerTypes", "Cluster1").I);
            Assert.Equal(1, Call(s, "ClusterIsActive", "Cluster2").I);
            Assert.Equal(Codes.InvalidArg, Call(s, "ClusterIsActive", "Nope").Error);
            Assert.Equal(
                "IOServer.Cluster1.IOServer1,Alarm.Cluster1.AlarmServer1,Trend.Cluster1.TrendServer1,Report.Cluster2.ReportServer1,Client",
                Call(s, "ServiceGetList").S
            );
            Assert.Equal("1", Call(s, "ServerInfo", "Alarm", 0).S);
            Assert.Equal("0", Call(s, "ServerInfo", "Report", 2).S);
            Assert.Equal(Codes.InvalidArg, Call(s, "ServerInfo", "Bogus", 0).Error);
            r = Call(s, "ServerInfoEx", "Server", 0, "Trend");
            Assert.True(r.Blocked);
            Assert.Equal("TrendServer1", r.S);
            Assert.Equal(Codes.FgBlock, CallFg(s, "ServerInfoEx", "Server", 0, "Trend").Error);
            Assert.Equal(1, Call(s, "ServerIsOnline", "alarmserver1", "", 1).I);
            r = Call(s, "ServerIsOnline", "Nope", "", 1);
            Assert.Equal(0, r.I);
            Assert.Equal(418, r.Error);
            // the server browse, by name
            r = Call(s, "ServerBrowseOpen", "TYPE=Alarm|Report", "", "");
            Assert.True(r.Blocked);
            long hs = r.I;
            Assert.Equal(2, Call(s, "ServerBrowseNumRecords", hs).I);
            Assert.Equal(0, Call(s, "ServerBrowseFirst", hs).I);
            Assert.Equal("AlarmServer1", Call(s, "ServerBrowseGetField", hs, "NAME").S);
            Assert.Equal("alarms", Call(s, "ServerBrowseGetField", hs, "comment").S);
            Assert.Equal(0, Call(s, "ServerBrowseNext", hs).I);
            Assert.Equal("Cluster2", Call(s, "ServerBrowseGetField", hs, "CLUSTER").S);
            Assert.Equal(0, Call(s, "ServerBrowseClose", hs).I);
            Assert.Equal(Codes.InvalidArg, Call(s, "ServerBrowseOpen", "BOGUS=1", "", "").Error);
        }

        [Fact]
        public void IODeviceInfoByNameAndNumber()
        {
            var s = MakeData(Servers);
            Assert.Equal("PLC1", Call(s, "IODeviceInfo", "plc1", 0).S);
            Assert.Equal("MODBUS", Call(s, "IODeviceInfo", "PLC1", 1).S);
            Assert.Equal("1.2", Call(s, "IODeviceInfo", "3", 2).S); // by number
            Assert.Equal("1", Call(s, "IODeviceInfo", "PLC1,P", 3).S); // running
            Assert.Equal("3", Call(s, "IODeviceInfo", "PLC1", 11).S);
            Assert.Equal("IOServer1", Call(s, "IODeviceInfo", "PLC1", 12).S);
            Assert.Equal("the plc", Call(s, "IODeviceInfo", "PLC1", 15).S);
            Assert.Equal("0", Call(s, "IODeviceInfo", "PLC1", 4).S);
            Assert.Equal("", Call(s, "IODeviceInfo", "Nope", 0).S);
            Assert.Equal("0", Call(s, "IODeviceInfo", "Nope", 3).S); // the status word of no unit
            Assert.Equal("", Call(s, "IODeviceInfo", "  ", 3).S); // an empty name: nothing
            Assert.Equal("1.2", Call(s, "IODeviceInfo", "3", 2).S); // again: from the process's index
            Assert.Equal(0, Call(s, "KerCmd", "Main", "page").I);
            Assert.Contains(s.Log, l => l.Contains("KerCmd"));
        }
    }
}
