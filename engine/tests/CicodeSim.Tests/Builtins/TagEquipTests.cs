using System;
using System.Collections.Generic;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;
using WQuality = CicodeSim.World.Quality;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Tag, subscription, browse and equipment built-ins over the World's TagDb /
    /// EquipDb (WorldFixture: Tank1_Level INT 0..32000 = 0..100 %, Plant.Tank1 ...).</summary>
    [Collection("World")]
    public sealed class TagEquipTests : BuiltinTestBase
    {
        private static (string, string, object[]) C(string fn, string b, params object[] args) =>
            (fn, b, args);

        private Sim Prog(params (string Fn, string Builtin, object[] Args)[] calls) =>
            Make(pb =>
            {
                foreach (var (f, b, args) in calls)
                    Ret(pb, f, b, args);
            });

        [Fact]
        public void TagReadBlocksOneTurnAndReturnsTheValueText()
        {
            var s = Prog(
                C("lvl", "TagRead", "Tank1_Level", 0, ""),
                C("item", "TagRead", "Plant.Tank1.Level", 0, ""),
                C("none", "TagRead", "NoSuchTag", 0, ""),
                C("arr", "TagRead", "Arr_Real[2]", 1, ""),
                C("str", "TagRead", "Name_Str", 0, "")
            );
            s.W.Tags.Set("Tank1_Level", 50.0);
            s.W.Tags.Set("Name_Str", "hello");
            s.W.Tags.Set("Arr_Real", 2.5, index: 3);
            var r = s.Call("lvl");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal("50", S(r.Task.Result!.Value));
            Assert.Equal(2, r.Slices); // it waited one turn
            Assert.Equal("50", S(s.Result("item"))); // Equip.Item
            Assert.Equal("2.5", S(s.Result("arr"))); // [2] + offset 1
            Assert.Equal("hello", S(s.Result("str")));
            r = s.Call("none");
            Assert.Equal("", S(r.Task.Result!.Value));
            Assert.Equal(Codes.TagNotFound, r.Task.Error);
            var fg = s.Foreground("lvl");
            Assert.Equal(Codes.FgBlock, fg.Error); // refused in foreground, ''
            Assert.Equal("", fg.Result!.Value.S);
            s.W.Tags.SetQuality("Tank1_Level", "bad");
            r = s.Call("lvl");
            Assert.Equal("", S(r.Task.Result!.Value)); // not Good: '' + an error
            Assert.NotEqual(0, r.Task.Error);
        }

        [Fact]
        public void TagWriteConvertsScaleChecksAndSynchBlocks()
        {
            var s = Prog(
                C("w", "TagWrite", "Tank1_Level", "75", 0, 0, ""),
                C("high", "TagWrite", "Tank1_Level", "150", 0, 0, ""),
                C("none", "TagWrite", "NoSuchTag", "1", 0, 0, ""),
                C("sync", "TagWrite", "Motor_Speed", "12.5", 0, 1, ""),
                C("arr", "TagWrite", "Arr_Real[1]", "9", 2, 0, ""),
                C("bad", "TagWrite", "Tank1_Level", "abc", 0, 0, "")
            );
            var r = s.Call("w");
            Assert.Equal(0, I(r.Task.Result!.Value));
            Assert.Equal(75.0, s.W.Tags.Get("Tank1_Level").D);
            Assert.Single(r.Changes);
            r = s.Call("high");
            Assert.Equal(Codes.OutOfRange, I(r.Task.Result!.Value)); // ScaleCheck: 257, nothing written
            Assert.Equal(Codes.OutOfRange, r.Task.Error);
            Assert.Equal(75.0, s.W.Tags.Get("Tank1_Level").D);
            Assert.Equal(Codes.TagNotFound, I(s.Result("none")));
            r = s.Call("sync");
            Assert.Equal(0, I(r.Task.Result!.Value));
            Assert.Equal(2, r.Slices); // bSynch: waited for the write
            Assert.Equal(12.5, s.W.Tags.Get("Motor_Speed").D);
            Assert.Equal(0, I(s.Result("arr")));
            Assert.Equal(9.0, s.W.Tags.Get("Arr_Real", index: 3).D);
            Assert.Equal(Errors.TypeMismatch, I(s.Result("bad")));
            Assert.Equal(Codes.FgBlock, s.Foreground("sync").Error);
        }

        [Fact]
        public void TagReadExKeepsQualityAndType()
        {
            var s = Make(pb =>
            {
                // q(): VariableQuality(TagReadEx("Tank1_Level"))
                pb.Function("q", "", Types.Quality)
                    .ConstStr("Tank1_Level")
                    .ConstInt(0)
                    .ConstStr("")
                    .Func("TagReadEx", 3)
                    .Func("VariableQuality", 1)
                    .RetValue()
                    .End();
                Ret(pb, "v", "TagReadEx", "Tank1_Level", 0, "");
            });
            s.W.Tags.Set("Tank1_Level", 25.0);
            Value v = s.Result("v");
            Assert.Equal(25.0, Value.Deref(v).D);
            Assert.Equal((long)WQuality.QualityGood, s.Result("q").I);
            s.W.Tags.SetQuality("Tank1_Level", "uncertain");
            Assert.Equal((long)WQuality.QualityUncertain, s.Result("q").I);
        }

        [Fact]
        public void TagGetPropertyTagInfoAndScale()
        {
            var s = Prog(
                C("hi", "TagGetProperty", "Tank1_Level", "EngUnitsHigh", -1, ""),
                C("units", "TagGetProperty", "Tank1_Level", "Units", -1, ""),
                C("c1", "TagGetProperty", "Plant.Tank1.Level", "Custom1", 1, ""),
                C("eq", "TagGetProperty", "Tank1_Level", "Equipment", 2, ""),
                C("bogus", "TagGetProperty", "Tank1_Level", "Bogus", -1, ""),
                C("none", "TagGetProperty", "NoSuch", "Units", -1, ""),
                C("block", "TagGetProperty", "Tank1_Level", "Units", 0, ""),
                C("badmode", "TagGetProperty", "Tank1_Level", "Units", 7, ""),
                C("i1", "TagInfo", "Tank1_Level", "1", "", -1),
                C("i5", "TagInfo", "Tank1_Level", "5", "", -1),
                C("inone", "TagInfo", "NoSuch", "1", "", -1),
                C("ix4", "TagInfoEx", "Tank1_Level", 4, "", -1),
                C("scale", "TagGetScale", "Tank1_Level", 50, 1, -1, ""),
                C("scale0", "TagGetScale", "Tank1_Level", 100, 0, -1, "")
            );
            Assert.Equal("100", S(s.Result("hi")));
            Assert.Equal("%", S(s.Result("units")));
            Assert.Equal("levelsensor", S(s.Result("c1")));
            Assert.Equal("Plant.Tank1", S(s.Result("eq")));
            var r = s.Call("bogus");
            Assert.Equal("", S(r.Task.Result!.Value));
            Assert.Equal(Codes.PropertyNotFound, r.Task.Error);
            Assert.Equal(Codes.TagNotFound, s.Call("none").Task.Error);
            r = s.Call("block");
            Assert.Equal("%", S(r.Task.Result!.Value));
            Assert.Equal(2, r.Slices); // mode 0: blocks (the server)
            Assert.Equal(Codes.FgBlock, s.Foreground("block").Error);
            Assert.Equal(Codes.InvalidArg, s.Call("badmode").Task.Error);
            Assert.Equal("%", S(s.Result("i1")));
            Assert.Equal("100", S(s.Result("i5")));
            r = s.Call("inone");
            Assert.Equal("stop", r.Outcome); // suppressed: never halts
            Assert.Equal(Codes.TagNotFound, r.Task.Error);
            Assert.Equal("0", S(s.Result("ix4")));
            Assert.Equal(" 50.0 %", S(s.Result("scale"))); // '###.#EU': %5.1f + ' %'
            Assert.Equal("100.0", S(s.Result("scale0")));
        }

        [Fact]
        public void SubscriptionsCallBackOnChanges()
        {
            var s = Make(pb =>
            {
                Ret(pb, "sub", "TagSubscribe", "Tank1_Level", 250, "Eng", -1.0, "OnChange", 1, 0);
                Ret(pb, "subPct", "TagSubscribe", "Tank1_Level", 250, "Percent", -1.0, "", 1, 0);
                Ret(pb, "subRaw", "TagSubscribe", "Tank1_Level", 250, "Raw", -1.0, "", 1, 0);
                Ret(pb, "subBad", "TagSubscribe", "Tank1_Level", 250, "Bogus", -1.0, "", 1, 0);
                Ret(pb, "subNone", "TagSubscribe", "NoSuch", 250, "Eng", -1.0, "", 1, 0);
                // OnChange(INT h): Count_Long = Count_Long + 1; Name_Str = SubscriptionGetInfo(h, "TagName")
                pb.Function("OnChange", "4")
                    .Begin()
                    .Load("Count_Long")
                    .ConstInt(1)
                    .Op("Add")
                    .Store("Count_Long")
                    .LoadStack(0)
                    .ConstStr("TagName")
                    .Func("SubscriptionGetInfo", 2)
                    .Store("Name_Str")
                    .Ret()
                    .End();
                pb.Function("val", "4", Types.Variant)
                    .Begin()
                    .LoadStack(0)
                    .ConstInt(0)
                    .Func("SubscriptionGetValue", 2)
                    .RetValue()
                    .End();
                pb.Function("attr", "47", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .ConstInt(0)
                    .Func("SubscriptionGetAttribute", 3)
                    .RetValue()
                    .End();
                pb.Function("unsub", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TagUnsubscribe", 1)
                    .RetValue()
                    .End();
                Ret(pb, "gv", "TagGetValue", "Tank1_Level", 0, 0, 250, 0, -1.0, 1, 30);
                Ret(pb, "gvq", "TagGetValue", "Tank1_Level", 3, 0, 250, 0, -1.0, 1, 30);
                Ret(pb, "gvbad", "TagGetValue", "Arr_Real[1]", 0, 0, 250, 0, -1.0, 1, 30);
            });
            s.W.Tags.Set("Count_Long", 0);
            s.W.Tags.Set("Tank1_Level", 40.0);
            long h = s.Result("sub").I;
            Assert.True(h >= 0);
            s.W.Tags.Set("Tank1_Level", 60.0);
            s.Run.Scheduler.Run();
            Assert.Equal(1, s.W.Tags.Get("Count_Long").I); // the callback ran with the handle
            Assert.Equal("Tank1_Level", s.W.Tags.Get("Name_Str").S);
            Assert.Equal(60.0, Value.Deref(s.Result("val", h)).D);
            Assert.Equal("60", S(s.Result("attr", h, "Value")));
            Assert.Equal("Tank1_Level", S(s.Result("attr", h, "FullName")));
            Assert.Equal(60.0, Value.Deref(s.Result("val", s.Result("subPct").I)).D);
            Assert.Equal(19200.0, Value.Deref(s.Result("val", s.Result("subRaw").I)).D);
            Assert.Equal(-1, s.Result("subBad").I);
            Assert.Equal(-1, s.Result("subNone").I);
            Assert.Equal(0, s.Result("unsub", h).I);
            Assert.Equal(Codes.BadHandle, s.Result("unsub", h).I);
            s.W.Tags.Set("Tank1_Level", 61.0);
            s.Run.Scheduler.Run();
            Assert.Equal(1, s.W.Tags.Get("Count_Long").I); // unsubscribed: no more calls
            Assert.Equal(61.0, Value.Deref(s.Result("gv")).D);
            Assert.Equal((long)WQuality.QualityGood, Value.Deref(s.Result("gvq")).I);
            Assert.Equal(Codes.InvalidArg, s.Call("gvbad").Task.Error); // no brackets in TagGetValue
        }

        [Fact]
        public void TagBrowseSessions()
        {
            var s = Make(pb =>
            {
                Ret(pb, "open", "TagBrowseOpen", "TAG=Tank*", "TAG,ENG_FULL", "TAG:D", "");
                Ret(pb, "openBad", "TagBrowseOpen", "NOPE=1", "", "", "");
                Ret(pb, "openNum", "TagBrowseOpen", "ENG_UNITS=%; RAW_FULL>=32000", "", "", "");
                pb.Function("n", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TagBrowseNumRecords", 1)
                    .RetValue()
                    .End();
                pb.Function("first", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TagBrowseFirst", 1)
                    .RetValue()
                    .End();
                pb.Function("next", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TagBrowseNext", 1)
                    .RetValue()
                    .End();
                pb.Function("get", "47", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("TagBrowseGetField", 2)
                    .RetValue()
                    .End();
                pb.Function("close", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TagBrowseClose", 1)
                    .RetValue()
                    .End();
            });
            long h = s.Result("open").I;
            Assert.Equal(3, s.Result("n", h).I);
            Assert.Equal(0, s.Result("first", h).I);
            Assert.Equal("Tank2_Level", S(s.Result("get", h, "TAG"))); // TAG:D
            Assert.Equal("100", S(s.Result("get", h, "ENG_FULL")));
            var r = s.Call("get", h, "COMMENT"); // not a field of the session
            Assert.Equal(Codes.BrowseFieldNotFound, r.Task.Error);
            Assert.Equal(0, s.Result("next", h).I);
            Assert.Equal("Tank1_Temp", S(s.Result("get", h, "TAG")));
            Assert.Equal(0, s.Result("next", h).I);
            Assert.Equal(Codes.BrowseEof, s.Result("next", h).I);
            Assert.Equal(0, s.Result("close", h).I);
            Assert.Equal(Codes.BrowseSessionNotFound, s.Result("first", h).I);
            Assert.Equal(-1, s.Result("openBad").I);
            long hn = s.Result("openNum").I;
            Assert.Equal(1, s.Result("n", hn).I); // Tank1_Level
            Assert.Equal(Codes.FgBlock, s.Foreground("open").Error);
        }

        [Fact]
        public void EquipmentPropertiesAndBrowse()
        {
            var s = Make(pb =>
            {
                Ret(pb, "page", "EquipGetProperty", "Plant.Tank1", "Page", 2, "");
                Ret(pb, "parent", "EquipGetProperty", "Plant.Tank1", "Parent", 3, "");
                Ret(pb, "bogus", "EquipGetProperty", "Plant.Tank1", "Bogus", 3, "");
                Ret(pb, "none", "EquipGetProperty", "Plant.Nope", "Page", 3, "");
                Ret(pb, "block", "EquipGetProperty", "Plant.Tank2", "Page", 0, "");
                Ret(pb, "mode", "EquipGetProperty", "Plant.Tank2", "MODE", 3, "");
                Ret(pb, "mode2", "EquipGetProperty", "Plant.Tank2", "MODE", 2, "");
                Ret(pb, "setMode", "EquipSetProperty", "Plant.Tank2", "MODE", "2", "");
                Ret(pb, "setPage", "EquipSetProperty", "Plant.Tank2", "PAGE", "x", "");
                Ret(pb, "open", "EquipBrowseOpen", "NAME=Plant.*", "NAME,PAGE", "");
                Ret(pb, "openAll", "EquipBrowseOpen", "", "", "");
                pb.Function("n", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("EquipBrowseNumRecords", 1)
                    .RetValue()
                    .End();
                pb.Function("first", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("EquipBrowseFirst", 1)
                    .RetValue()
                    .End();
                pb.Function("next", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("EquipBrowseNext", 1)
                    .RetValue()
                    .End();
                pb.Function("get", "47", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("EquipBrowseGetField", 2)
                    .RetValue()
                    .End();
            });
            Assert.Equal("Overview", S(s.Result("page")));
            Assert.Equal("Plant", S(s.Result("parent")));
            Assert.Equal(Codes.PropertyNotFound, s.Call("bogus").Task.Error);
            Assert.Equal(Codes.NameNotFound, s.Call("none").Task.Error);
            var r = s.Call("block");
            Assert.Equal("Detail", S(r.Task.Result!.Value));
            Assert.Equal(2, r.Slices);
            Assert.Equal("0", S(s.Result("mode")));
            Assert.Equal(Codes.PropertyNotFound, s.Call("mode2").Task.Error); // a runtime field locally: 348
            Assert.Equal(0, s.Result("setMode").I);
            Assert.Equal("2", S(s.Result("mode")));
            Assert.Equal(Codes.InvalidArg, s.Result("setPage").I);
            long h = s.Result("open").I;
            Assert.Equal(2, s.Result("n", h).I);
            Assert.Equal(0, s.Result("first", h).I);
            Assert.Equal("Plant.Tank1", S(s.Result("get", h, "NAME")));
            Assert.Equal("Overview", S(s.Result("get", h, "page")));
            Assert.Equal(0, s.Result("next", h).I);
            Assert.Equal("Detail", S(s.Result("get", h, "PAGE")));
            Assert.Equal(Codes.BrowseEof, s.Result("next", h).I);
            Assert.Equal(3, s.Result("n", s.Result("openAll").I).I);
        }

        private static (string, int)[] Fields(params string[] names)
        {
            var f = new (string, int)[names.Length];
            for (int i = 0; i < names.Length; i++)
                f[i] = (names[i], 40);
            return f;
        }

        /// <summary>A browse family's session functions as h-taking test functions.</summary>
        private static void BrowseFns(ProgramBuilder pb, string p, string kind)
        {
            pb.Function(p + "n", "4", Types.Long)
                .Begin()
                .LoadStack(0)
                .Func(kind + "BrowseNumRecords", 1)
                .RetValue()
                .End();
            pb.Function(p + "first", "4", Types.Long)
                .Begin()
                .LoadStack(0)
                .Func(kind + "BrowseFirst", 1)
                .RetValue()
                .End();
            pb.Function(p + "next", "4", Types.Long)
                .Begin()
                .LoadStack(0)
                .Func(kind + "BrowseNext", 1)
                .RetValue()
                .End();
            pb.Function(p + "prev", "4", Types.Long)
                .Begin()
                .LoadStack(0)
                .Func(kind + "BrowsePrev", 1)
                .RetValue()
                .End();
            pb.Function(p + "get", "47", Types.String)
                .Begin()
                .LoadStack(0)
                .LoadStack(1)
                .Func(kind + "BrowseGetField", 2)
                .RetValue()
                .End();
            pb.Function(p + "close", "4", Types.Long)
                .Begin()
                .LoadStack(0)
                .Func(kind + "BrowseClose", 1)
                .RetValue()
                .End();
        }

        [Fact]
        public void EquipmentReferenceAndStateBrowsesAndPrev()
        {
            var s = Make(pb =>
            {
                Ret(pb, "ropen", "EquipRefBrowseOpen", "Plant.Tank1", "REFEQUIP,REFITEM,ORDER", "");
                Ret(pb, "ropenBad", "EquipRefBrowseOpen", "NOPE=1", "", "");
                Ret(pb, "sopen", "EquipStateBrowseOpen", "Plant.Tank2", "NAME,DESC");
                Ret(pb, "eopen", "EquipBrowseOpen", "NAME=Plant.*", "NAME", "");
                Ret(pb, "topen", "TagBrowseOpen", "TAG=Tank1*", "TAG", "TAG", "");
                BrowseFns(pb, "r", "EquipRef");
                BrowseFns(pb, "s", "EquipState");
                BrowseFns(pb, "e", "Equip");
                BrowseFns(pb, "t", "Tag");
                Ret(pb, "check", "EquipCheckUpdate", "");
            });
            string main = System.IO.Path.Combine(Tmp.Path, "inst", "User", "Main");
            CicodeSim.Tests.World.WorldFixture.WriteDbf(
                System.IO.Path.Combine(main, "eqref.dbf"),
                Fields(
                    "EQUIP",
                    "REFEQUIP",
                    "REFITEM",
                    "ORDER",
                    "COMMENT",
                    "CLUSTER",
                    "REFCLUST",
                    "REFCAT",
                    "ASSOC"
                ),
                new[]
                {
                    CicodeSim.Tests.World.WorldFixture.Row(
                        ("EQUIP", "Plant.Tank1"),
                        ("REFEQUIP", "Plant.Tank2"),
                        ("REFITEM", "Level"),
                        ("ORDER", "2")
                    ),
                    CicodeSim.Tests.World.WorldFixture.Row(
                        ("EQUIP", "Plant.Tank1"),
                        ("REFEQUIP", "Plant"),
                        ("REFITEM", "Mode"),
                        ("ORDER", "1")
                    ),
                    CicodeSim.Tests.World.WorldFixture.Row(
                        ("EQUIP", "Plant.Tank2"),
                        ("REFEQUIP", "Plant.Tank1"),
                        ("REFITEM", "Level"),
                        ("ORDER", "1")
                    ),
                }
            );
            CicodeSim.Tests.World.WorldFixture.WriteDbf(
                System.IO.Path.Combine(main, "eqstate.dbf"),
                Fields(
                    "NAME",
                    "CLUSTER",
                    "EQUIPMENT",
                    "ENTRY",
                    "DELAY",
                    "PRESTATE",
                    "REPEAT",
                    "PERIOD",
                    "DESC",
                    "PRIORITY",
                    "DRMODE"
                ),
                new[]
                {
                    CicodeSim.Tests.World.WorldFixture.Row(
                        ("NAME", "Running"),
                        ("EQUIPMENT", "Plant.Tank2"),
                        ("DESC", "Pump on")
                    ),
                    CicodeSim.Tests.World.WorldFixture.Row(
                        ("NAME", "Idle"),
                        ("EQUIPMENT", "Plant.Tank2"),
                        ("DESC", "Pump off")
                    ),
                    CicodeSim.Tests.World.WorldFixture.Row(("NAME", "Idle"), ("EQUIPMENT", "Plant.Tank1")),
                }
            );

            // references: EQUIP, then ORDER (numbers compare as numbers)
            long h = s.Result("ropen").I;
            Assert.Equal(2, s.Result("rn", h).I);
            Assert.Equal(0, s.Result("rfirst", h).I);
            Assert.Equal("Mode", S(s.Result("rget", h, "REFITEM")));
            Assert.Equal(0, s.Result("rnext", h).I);
            Assert.Equal("Plant.Tank2", S(s.Result("rget", h, "REFEQUIP")));
            Assert.Equal(Codes.BrowseEof, s.Result("rnext", h).I);
            Assert.Equal(0, s.Result("rprev", h).I); // back from past the end
            Assert.Equal("Level", S(s.Result("rget", h, "REFITEM")));
            Assert.Equal(Codes.BrowseFieldNotFound, s.Call("rget", h, "COMMENT").Task.Error);
            Assert.Equal(-1, s.Result("ropenBad").I);
            Assert.Equal(Codes.BrowseSessionNotFound, s.Result("sfirst", h).I); // another kind's handle
            Assert.Equal(0, s.Result("rclose", h).I);
            Assert.Equal(Codes.BrowseSessionNotFound, s.Result("rclose", h).I);

            // states: EQUIPMENT, then NAME
            long hs = s.Result("sopen").I;
            Assert.Equal(2, s.Result("sn", hs).I);
            Assert.Equal(0, s.Result("sfirst", hs).I);
            Assert.Equal("Idle", S(s.Result("sget", hs, "NAME")));
            Assert.Equal("Pump off", S(s.Result("sget", hs, "DESC")));
            Assert.Equal(Codes.BrowseEof, s.Result("sprev", hs).I);
            Assert.Equal(0, s.Result("sclose", hs).I);

            // equipment and tags walk backwards too
            long he = s.Result("eopen").I;
            Assert.Equal(0, s.Result("efirst", he).I);
            Assert.Equal("Plant.Tank1", S(s.Result("eget", he, "NAME")));
            Assert.Equal(0, s.Result("enext", he).I);
            Assert.Equal(0, s.Result("eprev", he).I);
            Assert.Equal("Plant.Tank1", S(s.Result("eget", he, "NAME")));
            Assert.Equal(0, s.Result("eclose", he).I);
            Assert.Equal(Codes.BrowseSessionNotFound, s.Result("enext", he).I);
            long ht = s.Result("topen").I;
            Assert.Equal(0, s.Result("tfirst", ht).I);
            Assert.Equal(0, s.Result("tnext", ht).I);
            Assert.Equal(0, s.Result("tprev", ht).I);
            Assert.Equal(Codes.BrowseEof, s.Result("tprev", ht).I);
            Assert.Equal(0, s.Result("check").I);
        }

        [Fact]
        public void SubscriptionGettersAndCallbackLists()
        {
            var s = Make(pb =>
            {
                Ret(pb, "sub", "TagSubscribe", "Tank1_Level", 250, "Eng", -1.0, "", 0, 0); // not lightweight: timestamps
                pb.Function("q", "4", Types.Quality)
                    .Begin()
                    .LoadStack(0)
                    .Func("SubscriptionGetQuality", 1)
                    .RetValue()
                    .End();
                pb.Function("ts", "47", Types.Timestamp)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("SubscriptionGetTimestamp", 2)
                    .RetValue()
                    .End();
                pb.Function("tag", "4", Types.Variant)
                    .Begin()
                    .LoadStack(0)
                    .ConstInt(0)
                    .Func("SubscriptionGetTag", 2)
                    .RetValue()
                    .End();
                pb.Function("add", "47", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("SubscriptionAddCallback", 2)
                    .RetValue()
                    .End();
                pb.Function("remove", "47", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("SubscriptionRemoveCallback", 2)
                    .RetValue()
                    .End();
                pb.Function("Bump", "4")
                    .Begin()
                    .Load("Count_Long")
                    .ConstInt(1)
                    .Op("Add")
                    .Store("Count_Long")
                    .Ret()
                    .End();
                Ret(pb, "scale", "TagScaleStr", "Tank1_Level", 25, 1, "", -1);
                Ret(pb, "scaleNone", "TagScaleStr", "NoSuch", 25, 1, "", -1);
                Ret(pb, "param", "EquipGetParameter", "", "Plant.Nope", "X");
            });
            s.W.Tags.Set("Count_Long", 0);
            s.W.Tags.Set("Tank1_Level", 30.0);
            long h = s.Result("sub").I;
            Assert.Equal((long)WQuality.QualityGood, s.Result("q", h).I);
            Assert.Equal((long)s.W.Clock.NowFiletime(), s.Result("ts", h, "ValueTimestamp").I);
            Assert.Equal(Codes.InvalidArg, s.Call("ts", h, "Bogus").Task.Error);
            var r = s.Call("q", 999);
            Assert.Equal(Codes.BadHandle, r.Task.Error);
            Value tag = Value.Deref(s.Result("tag", h));
            Assert.Equal(30.0, tag.D);
            Assert.Equal(WQuality.QualityGood, tag.Q);
            Assert.Equal(0, s.Result("add", h, "Bump").I);
            Assert.Equal(Codes.InvalidArg, s.Result("add", h, "NoSuchFn").I);
            s.W.Tags.Set("Tank1_Level", 31.0);
            s.Run.Scheduler.Run();
            Assert.Equal(1, s.W.Tags.Get("Count_Long").I);
            Assert.Equal(0, s.Result("remove", h, "bump").I); // case-insensitive
            Assert.Equal(Codes.InvalidArg, s.Result("remove", h, "Bump").I);
            s.W.Tags.Set("Tank1_Level", 32.0);
            s.Run.Scheduler.Run();
            Assert.Equal(1, s.W.Tags.Get("Count_Long").I);
            Assert.Equal(" 25.0 %", S(s.Result("scale")));
            Assert.Equal(Codes.TagNotFound, s.Call("scaleNone").Task.Error);
            Assert.Equal(Codes.NameNotFound, s.Call("param").Task.Error);
            Assert.Equal(Codes.FgBlock, s.Foreground("param").Error);
        }

        [Fact]
        public void VariableQualityTimestampAndResolve()
        {
            var s = Make(pb =>
            {
                pb.Function("q", "", Types.Quality)
                    .Load("Tank1_Level")
                    .Func("VariableQuality", 1)
                    .RetValue()
                    .End();
                pb.Function("t", "", Types.Timestamp)
                    .Load("Tank1_Level")
                    .ConstInt(0)
                    .Func("VariableTimestamp", 2)
                    .RetValue()
                    .End();
                pb.Function("tc", "", Types.Timestamp)
                    .ConstInt(5)
                    .ConstInt(0)
                    .Func("VariableTimestamp", 2)
                    .RetValue()
                    .End();
                Ret(pb, "res", "TagResolve", "Tank1_Level");
                Ret(pb, "resNone", "TagResolve", "NoSuch");
                Ret(pb, "unres", "TagUnresolve", 0);
                Ret(pb, "reload", "TagRDBReload");
            });
            s.W.Tags.Set("Tank1_Level", 10.0);
            Assert.Equal((long)WQuality.QualityGood, s.Result("q").I);
            Assert.Equal((long)s.W.Clock.NowFiletime(), s.Result("t").I); // the write's time
            Assert.Equal(0, s.Result("tc").I); // a constant: no timestamp
            Assert.Equal(0, s.Result("res").I);
            Assert.Equal(-1, s.Result("resNone").I);
            Assert.Equal(0, s.Result("unres").I);
            Assert.Equal(Codes.BadHandle, s.Result("unres").I);
            Assert.Equal(1, s.Result("reload").I);
        }

        [Fact]
        public void WithoutAWorldTagFunctionsAreNotSimulated()
        {
            var pb = new ProgramBuilder();
            Ret(pb, "r", "TagRead", "X", 0, "");
            Ret(pb, "now", "TimeCurrent");
            var prog = pb.Build();
            var vm = new CicodeVm(
                prog,
                WorldBuiltins.Create((CicodeSim.World.World?)null),
                null,
                new ManualClock(),
                null
            );
            var t = vm.TaskForCall("r", Array.Empty<Value>());
            vm.Run(t);
            Assert.Contains("not simulated", t.HaltReason);
            t = vm.TaskForCall("now", Array.Empty<Value>());
            vm.Run(t);
            Assert.Equal(TaskState.Stop, t.State); // the clock's time needs no World
        }
    }
}
