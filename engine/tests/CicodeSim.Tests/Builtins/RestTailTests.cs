using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>XML documents, REAL tables, cluster activation, kernel queues and more.</summary>
    [Collection("World")]
    public sealed class RestTailTests : DataTestBase
    {
        private Sim MakeClusters() =>
            MakeData(() =>
            {
                Dbf(
                    Path.Combine(MainDir, "cluster.dbf"),
                    new[] { "NAME" },
                    new[] { "Cluster1" },
                    new[] { "Cluster2" }
                );
                Dbf(
                    Path.Combine(MainDir, "almsrv.dbf"),
                    new[] { "NAME", "CLUSTER", "MODE" },
                    new[] { "AlarmServer1", "Cluster1", "Primary" },
                    new[] { "AlarmServer2", "Cluster2", "Primary" }
                );
            });

        private static string File(Sim s, string name)
        {
            byte[]? b = s.Host.Sandbox.ReadAll(s.Host.Sandbox.Full(name));
            Assert.NotNull(b);
            return Encoding.UTF8.GetString(b!);
        }

        // ------------------------------------------------------------------ XML
        [Fact]
        public void XmlCreateBuildSaveAndOpen()
        {
            var s = MakeData();
            long doc = Call(s, "XMLCreate", "shapes", "").I;
            Assert.Equal(0, doc);
            long root = Call(s, "XMLGetRoot", doc).I;
            Assert.Equal(0, root);
            Assert.Equal(root, Call(s, "XMLGetRoot", doc).I); // the same handle every time
            long poly = Call(s, "XMLNodeAddChild", doc, root, "polygon", "").I;
            Assert.Equal(1, poly);
            Assert.Equal(0, Call(s, "XMLSetAttribute", doc, poly, "sides", "5").I);
            Assert.Equal(0, Call(s, "XMLSetAttribute", doc, poly, "SIDES", "6").I); // found without case
            Assert.Equal(0, Call(s, "XMLSetAttribute", doc, poly, "colour", "red").I);
            Assert.Equal(0, Call(s, "XMLNodeSetValue", doc, poly, "Pentagon").I);
            Assert.Equal(2, Call(s, "XMLGetAttributeCount", doc, poly).I);
            Assert.Equal("sides", Call(s, "XMLGetAttributeName", doc, poly, 0).S);
            Assert.Equal("6", Call(s, "XMLGetAttributeValue", doc, poly, 0).S);
            Assert.Equal("red", Call(s, "XMLGetAttribute", doc, poly, "Colour").S);
            Assert.Equal("Pentagon", Call(s, "XMLNodeGetValue", doc, poly).S);
            Assert.Equal("polygon", Call(s, "XMLNodeGetName", doc, poly).S);
            Assert.Equal(1, Call(s, "XMLGetChildCount", doc, root).I);
            Assert.Equal(poly, Call(s, "XMLGetChild", doc, root, 0).I);
            Assert.Equal(root, Call(s, "XMLGetParent", doc, poly).I);
            Assert.Equal(0, Call(s, "XMLSave", doc, "shapes.xml").I);
            Assert.Equal(
                "<?xml version=\"1.0\"?>\r\n<shapes>\r\n  <polygon sides=\"6\" colour=\"red\">Pentagon</polygon>\r\n</shapes>",
                File(s, "shapes.xml")
            );
            Assert.Equal(0, Call(s, "XMLClose", doc).I);
            var r = Call(s, "XMLClose", doc);
            Assert.Equal(274, r.I);
            Assert.Equal(274, r.Error);
            // reopened: handles continue in turn (1, not 0), nodes from 0 again
            long d2 = Call(s, "XMLOpen", "shapes.xml").I;
            Assert.Equal(1, d2);
            long n = Call(s, "XMLNodeFind", d2, "/shapes/polygon[@sides='6']").I;
            Assert.Equal(0, n);
            Assert.Equal("Pentagon", Call(s, "XMLNodeGetValue", d2, n).S);
            // the root's parent is the document node, which has none
            long top = Call(s, "XMLGetParent", d2, Call(s, "XMLGetRoot", d2).I).I;
            Assert.Equal("#document", Call(s, "XMLNodeGetName", d2, top).S);
            r = Call(s, "XMLGetParent", d2, top);
            Assert.Equal(-1, r.I);
            Assert.Equal(274, r.Error);
            // a second root on the document node: 545
            r = Call(s, "XMLNodeAddChild", d2, top, "other", "");
            Assert.Equal(-1, r.I);
            Assert.Equal(545, r.Error);
        }

        [Fact]
        public void XmlErrorsAndQuietFailures()
        {
            var s = MakeData();
            var r = Call(s, "XMLOpen", "missing.xml");
            Assert.Equal(-1, r.I);
            Assert.Equal(261, r.Error);
            var f = s.Host.Sandbox.Open(s.Host.Sandbox.Full("bad.xml"), false, true, true, true, 0)!;
            byte[] bad = Encoding.ASCII.GetBytes("<a><b></a>");
            s.Host.Sandbox.Write(f, bad, bad.Length, 0);
            r = Call(s, "XMLOpen", "bad.xml");
            Assert.Equal(-1, r.I);
            Assert.Equal(543, r.Error);
            r = Call(s, "XMLCreate", "1bad", ""); // XmlDocument.CreateElement checks no name
            Assert.Equal(0, r.I);
            Assert.Equal(0, r.Error);
            Assert.Equal(0, Call(s, "XMLClose", 0).I);
            r = Call(s, "XMLCreate", "", "");
            Assert.Equal(274, r.Error);
            long doc = Call(s, "XMLCreate", "root", "urn:x").I;
            Assert.True(doc >= 0);
            // an unknown document: -1 / 0 without an error, as the runtime
            r = Call(s, "XMLGetRoot", 999);
            Assert.Equal(-1, r.I);
            Assert.Equal(0, r.Error);
            r = Call(s, "XMLNodeAddChild", doc, 77, "x", "");
            Assert.Equal(-1, r.I);
            Assert.Equal(0, r.Error);
            Assert.Equal(0, Call(s, "XMLSave", 999, "x.xml").I);
            r = Call(s, "XMLNodeRemove", 999, 0);
            Assert.Equal(274, r.I);
            Assert.Equal(0, Call(s, "XMLNodeRemove", doc, 77).I); // unknown node of a known document
            // XPath
            r = Call(s, "XMLNodeFind", doc, "/root/");
            Assert.Equal(-1, r.I);
            Assert.Equal(544, r.Error);
            r = Call(s, "XMLNodeFind", doc, "/nothing");
            Assert.Equal(-1, r.I);
            Assert.Equal(0, r.Error);
            // names, values, attributes
            long root = Call(s, "XMLGetRoot", doc).I;
            long c = Call(s, "XMLNodeAddChild", doc, root, "c", "").I;
            r = Call(s, "XMLNodeGetValue", doc, root); // an element child: no value
            Assert.Equal("", r.S);
            Assert.Equal(274, r.Error);
            Assert.Equal("", Call(s, "XMLNodeGetValue", doc, c).S); // empty: '' and no error
            Assert.Equal(0, Call(s, "XMLNodeGetValue", doc, c).Error);
            r = Call(s, "XMLGetAttribute", doc, c, "none");
            Assert.Equal(274, r.Error);
            r = Call(s, "XMLGetAttributeName", doc, c, 0);
            Assert.Equal(274, r.Error);
            r = Call(s, "XMLSetAttribute", doc, c, "", "1");
            Assert.Equal(274, r.I); // an empty name: ArgumentException
            Assert.Equal(0, Call(s, "XMLGetAttributeCount", doc, root).I); // the namespace is no attribute node
            // a value too long for the 256-byte buffer comes back ''
            XmlDocs.Of(s.Host).Document((int)doc)!.DocumentElement!.FirstChild!.InnerText = new string(
                'x',
                300
            ); // only a file can hold one
            Assert.Equal("", Call(s, "XMLNodeGetValue", doc, c).S);
            Assert.Contains(s.Log, x => x.Contains("256-byte buffer"));
            // removal frees the handles of the removed node and its children with handles
            long gc = Call(s, "XMLNodeAddChild", doc, c, "g", "").I;
            Assert.Equal(0, Call(s, "XMLNodeRemove", doc, c).I);
            r = Call(s, "XMLNodeGetName", doc, gc);
            Assert.Equal(274, r.Error);
            Assert.Equal(0, Call(s, "XMLGetChildCount", doc, root).I);
            // the root removed: no root, a save of no root element is 543
            Assert.Equal(0, Call(s, "XMLNodeRemove", doc, root).I);
            Assert.Equal(-1, Call(s, "XMLGetRoot", doc).I);
            r = Call(s, "XMLSave", doc, "noroot.xml");
            Assert.Equal(543, r.I);
            r = Call(s, "XMLSave", doc, "nofolder\\x.xml");
            Assert.Equal(543, r.I);
        }

        [Fact]
        public void XmlHandleManagerHandsOutHandlesInTurn()
        {
            var h = new XmlDocs.Handles<string>(3);
            Assert.Equal(0, h.Add("a"));
            Assert.Equal(1, h.Add("b"));
            Assert.Equal(2, h.Add("c"));
            Assert.Equal(-1, h.Add("d")); // full
            Assert.Equal("b", h.Remove(1));
            Assert.Equal(1, h.Add("e")); // the free one after the last given (2): 0 taken, 1
            Assert.Equal(-1, h.Add("f"));
            h.RemoveAll();
            Assert.Equal(0, h.Add("g"));
        }

        // ------------------------------------------------------------------ REAL tables
        [Fact]
        public void TableLookUpAndShift()
        {
            var s = Make(pb =>
            {
                pb.Pools.CodeVar("Levels", Types.Real, 5, new List<double> { 10, 15, 50, 100, 200 });
                pb.Function("find", "2", Types.Long)
                    .Begin()
                    .AddrVar("Levels", 6)
                    .ConstInt(5)
                    .LoadStack(0)
                    .Func("TableLookUp", 3)
                    .RetValue()
                    .End();
                pb.Function("shift", "4", Types.Long)
                    .Begin()
                    .AddrVar("Levels", 6)
                    .ConstInt(5)
                    .LoadStack(0)
                    .Func("TableShift", 3)
                    .RetValue()
                    .End();
                pb.Function("at", "4", Types.Real)
                    .Begin()
                    .LoadStack(0)
                    .Index(5)
                    .LoadVar("Levels")
                    .RetValue()
                    .End();
            });
            Assert.Equal(2, s.Result("find", 50.0).I); // the help's example
            Assert.Equal(-1, s.Result("find", 45.0).I);
            Assert.Equal(2, s.Result("find", 50.0009).I);
            Assert.Equal(-1, s.Result("find", 50.0011).I);
            Assert.Equal(0, s.Result("shift", 2).I);
            double[] want = { 50, 100, 200, 0, 0 };
            for (int i = 0; i < 5; i++)
                Assert.Equal(want[i], s.Result("at", i).D);
            Assert.Equal(0, s.Result("shift", -1).I);
            double[] want2 = { 0, 50, 100, 200, 0 };
            for (int i = 0; i < 5; i++)
                Assert.Equal(want2[i], s.Result("at", i).D);
            Assert.Equal(0, s.Result("shift", 9).I);
            for (int i = 0; i < 5; i++)
                Assert.Equal(0.0, s.Result("at", i).D);
        }

        // ------------------------------------------------------------------ clusters
        [Fact]
        public void ClusterActivationFollowsInIsActiveAndStatus()
        {
            var s = MakeClusters();
            Assert.Equal(1, Call(s, "ClusterIsActive", "Cluster1").I);
            Assert.Equal(406, Call(s, "ClusterActivate", "cluster1").I); // active already
            Assert.Equal(403, Call(s, "ClusterActivate", "Nowhere").I);
            Assert.Equal(406, Call(s, "ClusterSwapActive", "Cluster1", "Cluster2").I);
            Assert.Equal(0, Call(s, "ClusterDeactivate", "Cluster1").I);
            Assert.Equal(0, Call(s, "ClusterDeactivate", "Cluster1").I); // inactive already: still 0
            Assert.Equal(0, Call(s, "ClusterIsActive", "Cluster1").I);
            Assert.Equal(0, Call(s, "ClusterStatus", "Cluster1", 1).I);
            Assert.Equal(2, Call(s, "ClusterStatus", "Cluster2", 1).I);
            Assert.Equal(0, Call(s, "ClusterSwapActive", "Cluster1", "Cluster2").I);
            Assert.Equal(1, Call(s, "ClusterIsActive", "Cluster1").I);
            Assert.Equal(0, Call(s, "ClusterIsActive", "Cluster2").I);
            Assert.Equal(0, Call(s, "ClusterDeactivate", "Cluster1").I);
            Assert.Equal(405, Call(s, "ClusterSwapActive", "Cluster1", "Cluster2").I);
            Assert.Equal(403, Call(s, "ClusterSwapActive", "Cluster1", "").I);
            Assert.Equal(0, Call(s, "ClusterActivate", "Cluster2").I);
        }

        // ------------------------------------------------------------------ kernel, menus, logging
        [Fact]
        public void KernelQueuesMenusAndLogging()
        {
            var s = MakeData(build: pb =>
            {
                pb.Function("Cmd", "", Types.Long).ConstInt(0).RetValue().End();
                pb.Function("Yes", "", Types.Long).ConstInt(1).RetValue().End();
                pb.Function("Arg", "4", Types.Long).Begin().LoadStack(0).RetValue().End();
            });
            long q = Call(s, "QueOpen", "MyQueue", 1).I;
            Assert.Equal(0, Call(s, "QueWrite", q, 7, "x").I);
            Assert.Equal(0, Call(s, "QueWrite", q, 8, "y").I);
            Assert.Equal(2, Call(s, "KernelQueueLength", "myqueue").I);
            var r = Call(s, "KernelQueueLength", "None");
            Assert.Equal(-1, r.I);
            Assert.Equal(274, r.Error);
            r = Call(s, "KernelTableItemCount", "CiCode");
            Assert.Equal(-1, r.I);
            Assert.Equal(274, r.Error);
            Assert.Equal(447, Call(s, "KernelTableInfo", "Task", "x", "Mode").Error);
            // menus: HiddenWhen / DisabledWhen on a node MenuNodeAddChild made
            long root = Call(s, "MenuGetGenericNode", 1).I;
            long node = Call(s, "MenuNodeAddChild", root, "Item", "Cmd", "", "", 0).I;
            Assert.Equal(0, Call(s, "MenuNodeIsHidden", node).I);
            Assert.Equal(0, Call(s, "MenuNodeSetHiddenWhen", node, "Yes", "").I);
            Assert.Equal(1, Call(s, "MenuNodeIsHidden", node).I);
            Assert.Equal(274, Call(s, "MenuNodeSetHiddenWhen", node, "Yes", "").I); // only once
            Assert.Equal(274, Call(s, "MenuNodeSetDisabledWhen", node, "NoSuchFn", "").I);
            Assert.Equal(0, Call(s, "MenuNodeSetDisabledWhen", node, "Arg", "0", 2).I);
            Assert.Equal(0, Call(s, "MenuNodeIsDisabled", node).I);
            Assert.Equal("2", Call(s, "MenuNodeGetProperty", node, 4).S);
            Assert.Equal(269, Call(s, "MenuNodeSetHiddenWhen", 9999, "Yes", "").I);
            Assert.Equal(274, Call(s, "MenuNodeSetHiddenWhen", root, "Yes", "").I); // not made by MenuNodeAddChild
            Assert.Equal(-1, Call(s, "MenuNodeGetCurr").I);
            Call(s, "MenuReload");
            Assert.Equal(-1, Call(s, "MenuGetGenericNode", 0).I); // the added generic root is gone
            // logging parameters
            Assert.Equal(0, Call(s, "SetLogging", "Debug", "DriverTrace", "1", 0).I);
            Assert.Equal("1", Call(s, "GetLogging", "debug", "drivertrace").S);
            Assert.Equal(274, Call(s, "SetLogging", "Debug", "Nothing", "1", 0).I);
            r = Call(s, "GetLogging", "Debug", "Nothing");
            Assert.Equal("", r.S);
            Assert.Equal(274, r.Error);
            Assert.Equal(0, Call(s, "SetLogging", "General", "Verbose", "2", 1).I);
            Assert.Equal("2", Call(s, "ParameterGet", "General", "Verbose", "").S); // persisted to the in-memory ini
        }

        // ------------------------------------------------------------------ tests, overrides, conversions
        [Fact]
        public void TestFunctionsOverridesAndConversions()
        {
            var s = MakeData();
            Assert.Equal(42, Call(s, "TestVariantINT", 42).I);
            Assert.Equal(1.5, Call(s, "TestVariantREAL", 1.5).D);
            Assert.Equal("abc", Call(s, "TestVariantSTRING", "abc").S);
            Assert.Equal((double)(float)0.1, Call(s, "TestDbl2Flt", 0.1).D);
            Assert.Equal("", Call(s, "TestStringNULL").S);
            Assert.Equal(0, Call(s, "PointNew", "x", 1, 1, 1, 1, 1).I);
            Assert.Equal("", Call(s, "FtpFileFind", 0, "*").S);
            // the Override element's quality
            Assert.Equal(0, Call(s, "TagSetOverrideBad", "Pump1_Run", 0, "").I);
            Assert.Equal(0UL, s.W.Tags.Get("Pump1_Run", "override").Q & 0xC0);
            var r = Call(s, "TagSetOverrideUncertain", "Pump1_Run");
            Assert.True(r.Blocked); // Synch defaults to TRUE
            Assert.Equal(0x40UL, s.W.Tags.Get("Pump1_Run", "override").Q & 0xC0);
            Assert.Equal(0, Call(s, "TagSetOverrideGood", "Plant.Tank1.Run", 0, "").I); // Equipment.Item
            Assert.Equal(0xC0UL, s.W.Tags.Get("Pump1_Run", "override").Q & 0xC0);
            r = Call(s, "TagSetOverrideGood", "NoSuchTag", 0, "");
            Assert.Equal(424, r.I);
            Assert.Equal(424, r.Error);
            Assert.Equal(282, CallFg(s, "TagSetOverrideGood", "Pump1_Run", 1, "").I);
            // VariableToStr
            Assert.Equal("12", Call(s, "VariableToStr", 12, 0).S);
            Assert.Equal("1.5", Call(s, "VariableToStr", 1.5, 1).S);
            // Debug, the RDB path, the restart project
            Assert.Equal(0, Call(s, "Debug", "drvdebug", "10", "", "").I);
            Assert.Equal(274, Call(s, "Debug", "other", "10", "", "").I);
            Assert.Equal(1, Call(s, "rdbSetPath", "c:\\a\\;C:\\A;;c:\\b").I);
            Assert.Equal("c:\\a;c:\\b", Call(s, "rdbGetPath").S);
            Assert.Equal("", Call(s, "ProjectRestartGet").S);
            Assert.Equal(0, Call(s, "ProjectRestartSet", "Other").I);
            Assert.Equal("Other", Call(s, "ProjectRestartGet").S);
            Assert.Equal(0, Call(s, "ProjectSet", "D:\\Projects\\X").I);
            Assert.Equal("D:\\Projects\\X", Call(s, "ParameterGet", "CtEdit", "Run", "").S);
            Assert.Equal(-1, Call(s, "_RunTest", "nothing").I);
            Assert.Equal(-1, Call(s, "RdbOpenSub", 99, "X").I);
        }

        [Fact]
        public void TimeToFmtStrTokens()
        {
            var s = MakeData(); // UTC: 2026-03-03 14:05:09, a Tuesday
            const long t = 1772546709;
            Assert.Equal("2026-03-03 14:05:09", Call(s, "TimeToFmtStr", t, "YYYY-MM-DD HH24:MI:SS").S);
            Assert.Equal(
                "Tuesday Mar 03rd, 2,026 02:05 P.M.",
                Call(s, "TimeToFmtStr", t, "Day Mon DDTH, Y,YYY HH:MI P.M.").S
            );
            Assert.Equal(
                "061 3 50709 026 26 6 Q AD A.D. PM March Tue",
                Call(s, "TimeToFmtStr", t, "DDD D SSSS YYY YY Y \"Q\" BC B.C. am month dy").S
            );
            Assert.Equal("11th 02nd", Call(s, "TimeToFmtStr", 1772546709 + 8 * 86400, "DDTH \"\"HH12TH").S);
        }

        [Fact]
        public void OutsideWorldStubsAnswerAsWithoutTheThing()
        {
            var s = MakeData();
            Assert.Equal(313, Call(s, "MailLogon", "me", "", 0).I);
            var o = Ref(Value.Str("old"));
            Assert.Equal(
                313,
                Call(s, "MailRead", o, Ref(Value.Str("x")), Ref(Value.Str("x")), Ref(Value.Str("x")), 0).I
            );
            Assert.Equal("", Held(o).S);
            Assert.Equal(-1, Call(s, "LineOpen", "COM1", 1, "", "").I);
            var r = Call(s, "FuzzyOpen", "x.ftr");
            Assert.Equal(-1, r.I);
            Assert.Equal(274, r.Error);
            Assert.Equal(274, Call(s, "SendKeys", "Notepad", "abc").I);
            Assert.Equal(282, CallFg(s, "SOEArchive", "c:\\x", "").I);
            Assert.True(Call(s, "SOEMount", "c:\\x", "").Blocked);
            Assert.Equal("", Call(s, "PasswordEncrypt", "secret").S);
        }
    }
}
