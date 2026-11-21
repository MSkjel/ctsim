using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Page;
using CicodeSim.Tests.World;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;
using Xunit;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.Page
{
    /// <summary>The animator over the synthetic install (WorldFixture): pages built in memory,
    /// the runtime's order of events, the shown / disabled rules, inputs and the classes.</summary>
    [Collection("World")]
    public class AnimatorTests : IDisposable
    {
        private readonly TempDir _dir = new TempDir();
        private readonly WorldT _world;

        public AnimatorTests()
        {
            _world = WorldFixture.MakeWorld(_dir.Path);
        }

        public void Dispose() => _dir.Dispose();

        private Animator Make(Program? lib = null) => new Animator(_world, lib ?? new Program());

        private string Str(string tag) => _world.Tags.Get(tag).S ?? "";

        private double Num(string tag)
        {
            Value v = _world.Tags.Get(tag);
            return Types.IsRealType(v.T) ? v.D : v.I;
        }

        /// <summary>LocStr = LocStr + text.</summary>
        private static void Append(CodeBuilder c, string text) =>
            c.Load("LocStr").ConstStr(text).Op("Add").Store("LocStr");

        // ------------------------------------------------------------------ events
        [Fact]
        public void PageEventsRunInTheRuntimeOrder()
        {
            var lb = new ProgramBuilder();
            void Fn(string name, string text, bool ret)
            {
                var f = lb.Function(name, "", ret ? Types.Long : Types.Void);
                f.Begin();
                Append(f, text);
                if (ret)
                    f.ConstInt(0).RetValue();
                else
                    f.Ret();
                f.End();
            }
            Fn("_PageStart", "S", false);
            Fn("_PageStop", "P", false);
            Fn("Ev8", "8", true);
            Fn("Ev4", "4", true);
            Fn("Ev5", "5", true);
            Fn("Ev9", "9", true);
            Fn("Bg", "B", true);
            var lib = lb.Build();
            var a = Make(lib);
            foreach (var (type, fn) in new[] { (8, "Ev8"), (4, "Ev4"), (5, "Ev5"), (9, "Ev9") })
                a.Host!.Events[type] = lib.Function(fn)!.Index;
            var p = new PageFixture();
            p.Row(
                "Control",
                ("ENTRY_FUNC", p.Command(c => Append(c, "E"))),
                ("EXIT_FUNC", p.Command(c => Append(c, "X"))),
                ("READY_FUNC", p.Command(c => Append(c, "R"))),
                ("BGND_FUNC", p.Code(c => c.Call("Bg", 0)))
            );
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            // _PageStart, OnEvent 8, OnEvent 4, then the first scan inside the open (BGND) before
            // the entry task has run a statement (verify)
            Assert.Equal("S84B", Str("LocStr"));
            a.RunFor(0.0);
            Assert.Equal("S84BE", Str("LocStr"));
            // scan 2 at 0.25 s; READY_FUNC starts at the end of scan 2 ([Page]ReadyCicodeDelay 2)
            a.RunScans(1);
            Assert.Equal("S84BEBR", Str("LocStr"));
            a.RunScans(1);
            Assert.Equal("S84BEBRB", Str("LocStr"));
            // close: _PageStop, OnEvent 5, OnEvent 9, EXIT_FUNC synchronously
            a.Close(0);
            Assert.Equal("S84BEBRBP59X", Str("LocStr"));
            var evs = a.Events.ConvertAll(e => e.Event);
            Assert.Equal(new[] { "open", "entry", "taskStart", "shown", "taskStart", "exit" }, evs.ToArray());
            Assert.Equal(0.25, a.Events.Find(e => e.Event == "shown")!.T, 9);
        }

        // ------------------------------------------------------------------ shown / disabled
        private Animator? _shared;

        private (Animator a, PageRun r) Group(
            int groupStyle,
            int childStyle,
            int childDisMode,
            int childArea,
            bool groupDis,
            bool childDis,
            bool groupHidden = false
        )
        {
            _world.Tags.Set("Valve_A", groupDis ? 1 : 0);
            _world.Tags.Set("Valve_B", childDis ? 1 : 0);
            var a = _shared ??= Make();
            a.Stats.Clear();
            var p = new PageFixture();
            p.Dyn(
                1,
                0x08,
                -1,
                ("DISABLED", p.Load("Valve_A")),
                ("VISIBLE", p.Const(groupHidden ? 1 : 0)),
                ("GREY_STYLE", (long)groupStyle)
            );
            p.Dyn(
                2,
                0x0F,
                1,
                ("DISABLED", p.Load("Valve_B")),
                ("GREY_STYLE", (long)childStyle),
                ("DISMODE", (long)childDisMode),
                ("AREA", (long)childArea),
                ("MVX", p.ConstReal(50)),
                ("MVXMAXPT", 100L)
            );
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            return (a, a.Run(0)!);
        }

        [Fact]
        public void ShownAndDisabledFollowTheBinaryRules()
        {
            // child disabled, style GRAY_HIDE: hidden while disabled
            var (_, r) = Group(5, 4, 0, 0, false, true);
            var s = r.State.Objects[2];
            Assert.False(s.Shown);
            Assert.True(s.Disabled);
            // child disabled, GRAY_SUNK: shown, disabled, style 1
            (_, r) = Group(5, 1, 0, 0, false, true);
            s = r.State.Objects[2];
            Assert.True(s.Shown);
            Assert.True(s.Disabled);
            Assert.Equal(1, s.DisableStyle);
            // GREY_STYLE 0 (no key) renders like GRAY_SUNK (any other value: mode 4)
            (_, r) = Group(5, 0, 0, 0, false, true);
            Assert.Equal(1, r.State.Objects[2].DisableStyle);
            // a disabled group imposes its style: the group's GRAY_HIDE hides the child
            (_, r) = Group(4, 5, 0, 0, true, false);
            s = r.State.Objects[2];
            Assert.False(s.Shown);
            Assert.True(s.Disabled);
            // an enabled group: the child's own style
            (_, r) = Group(4, 5, 0, 0, false, true);
            s = r.State.Objects[2];
            Assert.True(s.Shown);
            Assert.Equal(5, s.DisableStyle);
            // no area access: not drawn, unless DISMODE 1 (then drawn disabled)
            (_, r) = Group(5, 5, 0, 5, false, false);
            Assert.False(r.State.Objects[2].Shown);
            Assert.False(r.State.Objects[2].Disabled);
            (_, r) = Group(5, 5, 1, 5, false, false);
            Assert.True(r.State.Objects[2].Shown);
            Assert.True(r.State.Objects[2].Disabled);
            // a hidden group hides its members, which then evaluate nothing but VISIBLE / DISABLED
            var (a, r2) = Group(5, 5, 0, 0, false, false, groupHidden: true);
            Assert.False(r2.State.Objects[2].Shown);
            Assert.Null(r2.State.Objects[2].Move);
            Assert.Equal(0, a.StatsOf("movement").Evaluations);
            // shown: the movement runs
            (a, r2) = Group(5, 5, 0, 0, false, false);
            Assert.Equal(new[] { 50.0, 0.0 }, r2.State.Objects[2].Move);
        }

        // ------------------------------------------------------------------ inputs
        [Theory]
        [InlineData(500, 2.0, 2)] // 1.002, 1.503 (next 2.004)
        [InlineData(100, 2.0, 10)] // 1.010 + k * 0.101 up to 1.919
        [InlineData(199, 2.0, 5)] // 1.2, 1.4, 1.6, 1.8, 2.0
        public void TouchDownUpAndRepeatTiming(int rate, double hold, int repeats)
        {
            var a = Make();
            var p = new PageFixture();
            byte[] Add(long n) => p.Command(c => c.Load("LocInt").ConstInt(n).Op("Add").Store("LocInt"));
            p.Row(
                "PGTOUCH",
                ("AN", 3L),
                ("ACTION", 1L),
                ("RPT_RATE", (long)rate),
                ("CMD", Add(10000)),
                ("DOWN_CMD", Add(100)),
                ("REPEAT_CMD", Add(1))
            );
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            _world.Tags.Set("LocInt", 0);
            Assert.True(a.Touch(0, 3, Animator.TouchKind.Down));
            a.RunFor(hold);
            Assert.True(a.Touch(0, 3, Animator.TouchKind.Up));
            a.RunFor(1.0);
            Assert.Equal(10100 + repeats, (int)Num("LocInt"));
        }

        [Fact]
        public void ARepeatWaitsWhileDisabledAndEndsOnTheRelease()
        {
            var a = Make();
            var p = new PageFixture();
            byte[] Add(long n) => p.Command(c => c.Load("LocInt").ConstInt(n).Op("Add").Store("LocInt"));
            // the press disables its own object (DISABLED = Valve_A): no input while disabled
            p.Dyn(3, 0x0F, -1, ("DISABLED", p.Load("Valve_A")));
            p.Row(
                "PGTOUCH",
                ("AN", 3L),
                ("ACTION", 1L),
                ("RPT_RATE", 500L),
                ("CMD", Add(10000)),
                (
                    "DOWN_CMD",
                    p.Command(c =>
                        c.ConstInt(1).Store("Valve_A").Load("LocInt").ConstInt(100).Op("Add").Store("LocInt")
                    )
                ),
                ("REPEAT_CMD", Add(1))
            );
            _world.Tags.Set("Valve_A", 0);
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            _world.Tags.Set("LocInt", 0);
            Assert.True(a.Touch(0, 3, Animator.TouchKind.Down));
            a.RunFor(2.0);
            // repeats due at 1.002 and 1.503 found the object disabled
            Assert.Equal(100, (int)Num("LocInt"));
            // the release reaches the pressed object (mouse capture): the repeat ends, the
            // disabled object runs no CMD
            Assert.False(a.Touch(0, 3, Animator.TouchKind.Up));
            _world.Tags.Set("Valve_A", 0);
            a.RunFor(2.0);
            Assert.Equal(100, (int)Num("LocInt"));
        }

        [Fact]
        public void OneRepeatTimerPerProcess()
        {
            var a = Make();
            var p = new PageFixture();
            byte[] Add(long n) => p.Command(c => c.Load("LocInt").ConstInt(n).Op("Add").Store("LocInt"));
            p.Row("PGTOUCH", ("AN", 3L), ("ACTION", 1L), ("RPT_RATE", 500L), ("REPEAT_CMD", Add(1)));
            p.Row("PGTOUCH", ("AN", 4L), ("ACTION", 1L), ("RPT_RATE", 500L), ("REPEAT_CMD", Add(100)));
            // a record for another button (KEY_RBUTTON 2) takes no left button event
            p.Row("PGTOUCH", ("AN", 5L), ("ACTION", 2L), ("DOWN_CMD", Add(10000)));
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            _world.Tags.Set("LocInt", 0);
            a.Touch(0, 3, Animator.TouchKind.Down);
            a.RunFor(1.6); // AN 3 repeats at 1.002, 1.503
            Assert.Equal(2, (int)Num("LocInt"));
            // a press on AN 4 while AN 3 repeats: one timer, AN 4's, repeating from its next tick
            a.Touch(0, 4, Animator.TouchKind.Down);
            a.RunFor(0.6); // 0.501 later: AN 4 once, AN 3 never
            Assert.Equal(102, (int)Num("LocInt"));
            Assert.False(a.Touch(0, 5, Animator.TouchKind.Down));
            a.Touch(0, 4, Animator.TouchKind.Up);
            a.RunFor(2.0);
            Assert.Equal(102, (int)Num("LocInt"));
        }

        [Fact]
        public void KeySetSeqAddsACommandThatCallsItsFunction()
        {
            var lb = new ProgramBuilder();
            var f = lb.Function("OnScale", "", Types.Void);
            f.Begin();
            f.ConstInt(0).Convert(4).Func("GetGlbStr", 1).Store("LocStr");
            f.Ret();
            f.End();
            var lib = lb.Build();
            var a = Make(lib);
            a.Keys.Add("ENTER", 0x0D);
            var p = new PageFixture();
            // the page's entry adds the command, as trend.ci's KeySetSeq("######## ENTER", ...)
            p.Row(
                "Control",
                (
                    "ENTRY_FUNC",
                    p.Command(c =>
                        c.ConstStr("### ENTER")
                            .ConstInt(0)
                            .ConstStr("OnScale")
                            .Func("KeySetSeq", 3)
                            .Store("LocInt")
                    )
                )
            );
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            a.RunFor(0.1);
            Assert.Single(a.Display.Windows[0].RuntimeKeys);
            foreach (string k in new[] { "4", "2" })
                Assert.False(a.Key(0, k));
            Assert.True(a.Key(0, "ENTER"));
            a.RunFor(0.1);
            Assert.Equal("42", Str("LocStr"));
            // another page shown: the command is gone
            Assert.Equal(0, a.Open("TestPage", 0, compiled: new PageFixture().Build()));
            Assert.False(a.Key(0, "7"));
            Assert.False(a.Key(0, "ENTER"));
        }

        [Fact]
        public void KeyboardSequencesPassTheirDataInGlobalStrings()
        {
            var a = Make();
            a.Keys.Add("ENTER", 0x0D);
            var p = new PageFixture();
            // LocStr = GetGlbStr(0) + "|" + GetGlbStr(1) (Arg1 / Arg2 are these labels, Include labels.dbf)
            p.Row(
                "PGKEY",
                ("AN", 0L),
                ("KEY_SEQ", "##,## ENTER"),
                (
                    "CMD",
                    p.Command(c =>
                        c.ConstInt(0)
                            .Convert(4)
                            .Func("GetGlbStr", 1)
                            .ConstStr("|")
                            .Op("Add")
                            .ConstInt(1)
                            .Convert(4)
                            .Func("GetGlbStr", 1)
                            .Op("Add")
                            .Store("LocStr")
                    )
                )
            );
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            foreach (string k in new[] { "1", "2", ",", "3" })
                Assert.False(a.Key(0, k));
            Assert.True(a.Key(0, "ENTER"));
            a.RunFor(0.1);
            Assert.Equal("12|3", Str("LocStr"));
            // a line no sequence starts with is dropped (a data character would be a prefix)
            Assert.False(a.Key(0, "ENTER"));
            Assert.Empty(a.Display.Windows[0].KeyBuffer);
        }

        [Fact]
        public void SliderDragWritesThePercentThroughTheWriteCode()
        {
            var a = Make();
            var p = new PageFixture();
            long read = p.Load("Tank1_Level", percent: true);
            long write = p.Code(c => c.ConstInt(0).Convert(4).Func("GetGlbStr", 1).Store("Tank1_Level"));
            p.Percent("Tank1_Level");
            p.Dyn(
                5,
                0x0F,
                -1,
                ("MVXSL", CompiledPage.Slider(read, write)),
                ("MVXSLMODE", 4097L),
                ("MVXMINPT", 0L),
                ("MVXMAXPT", 200L)
            );
            _world.Tags.Set("Tank1_Level", 25);
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            // the display reads the tag as a percent of its range: 25 % of 0..200 px
            Assert.Equal(new[] { 50.0, 0.0 }, a.Run(0)!.State.Objects[5].Move);
            // a drag to +100 px = 50 %: written at once (continuous, SLMODE 0x1000)
            Assert.True(a.Drag(0, 5, 100, null, null, release: false));
            Assert.Equal(50.0, Num("Tank1_Level"), 6);
            Assert.True(a.Drag(0, 5, 160, null, null, release: true));
            Assert.Equal(80.0, Num("Tank1_Level"), 6);
            // Arg1 is the percent as sprintf "%e"
            Assert.Equal("8.000000e+01", a.Vm.GlbStr[0]);
            a.RunScans(1);
            Assert.Equal(new[] { 160.0, 0.0 }, a.Run(0)!.State.Objects[5].Move);
        }

        [Fact]
        public void MouseOverColoursAndTheExtentIncludesItsEdge()
        {
            var a = Make();
            var p = new PageFixture();
            p.Dyn(
                6,
                0x0F,
                -1,
                ("COLMODE", 0L),
                ("COL_A", p.Code(c => c.ConstInt(-1).Convert(4).Func("DspGetMouseOver", 1)))
            );
            var d = Draw.Obj(6, 0x0F, new Rect(10, 10, 50, 30));
            d.Colour = new ColourStates
            {
                State0 = ColourPair.Steady(0xFFFFFF),
                MaxOn = ColourPair.Steady(0xFF0000),
            };
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(d), compiled: p.Build()));
            var r = a.Run(0)!;
            Assert.Equal(0xFFFFFFL, r.State.Objects[6].FillColour);
            a.Mouse(0, 20, 20);
            a.RunScans(1);
            Assert.Equal(0xFF0000L, r.State.Objects[6].FillColour);
            a.Mouse(0, 50, 30); // the right / bottom pixel is inside
            a.RunScans(1);
            Assert.Equal(0xFF0000L, r.State.Objects[6].FillColour);
            a.Mouse(0, 51, 30);
            a.RunScans(1);
            Assert.Equal(0xFFFFFFL, r.State.Objects[6].FillColour);
        }

        // ------------------------------------------------------------------ associations, built-ins
        [Fact]
        public void AssociationsResolveTagReferencesAndUnresolvedShowAsc()
        {
            var a = Make();
            _world.Tags.Set("Tank1_Level", 42);
            PageFixture P()
            {
                var p = new PageFixture("!Pop");
                p.Dyn(
                    7,
                    0x05,
                    -1,
                    ("TXTMODE", 3L),
                    ("TXT_STR", p.Code(c => c.Load("?Equip?.Level").StringFmt(0x105)))
                );
                // an element of an associated array (an Index-prefixed Load)
                p.Dyn(
                    8,
                    0x05,
                    -1,
                    ("TXTMODE", 3L),
                    ("TXT_STR", p.Code(c => c.ConstInt(1).Index(4).Load("?Arr?").StringFmt(0x105)))
                );
                return p;
            }
            // an equipment name is no tag: the store's check leaves 289 and keeps the association
            // (live: the ExampleSA faceplates' Ass of __EquipmentRef returned 289)
            Assert.Equal(289, a.Display.Ass(-2, "Equip", "Plant.Tank1", 0));
            Assert.Equal(0, a.Open("!Pop", 0, compiled: P().Build()));
            Assert.Equal(" 42.0", a.Run(0)!.State.Objects[7].Text);
            // the next page has no association: 323 Super Genie not associated
            Assert.Equal(0, a.Open("!Pop", 0, compiled: P().Build()));
            var s = a.Run(0)!.State.Objects[7];
            Assert.Equal("#ASC", s.Text);
            Assert.Equal("#ASC", s.Error);
            Assert.Equal("#ASC", a.Run(0)!.State.Objects[8].Text);
            // a page's declared default is used when the caller gave none
            var m = new PageModel();
            m.Drawing.Associations.Add(new PageAssociation { Name = "Equip", Default = "Plant.Tank1" });
            Assert.Equal(0, a.Open("!Pop", 0, m, compiled: P().Build()));
            Assert.Equal(" 42.0", a.Run(0)!.State.Objects[7].Text);
        }

        [Fact]
        public void AssMetadataStoresEveryEntryAndAnEmptyValueDeletes()
        {
            var a = Make();
            var p = new PageFixture();
            // a Cicode object runs AssMetadata(-2, 20) every scan
            p.Row(
                "PGADV",
                ("AN", 21L),
                ("CMD", p.Code(c => c.ConstInt(-2).Convert(4).ConstInt(20).Convert(4).Func("AssMetadata", 2)))
            );
            var d = Draw.Obj(20, 0x08);
            d.Metadata.Add(new KeyValuePair<string, string>("Equip", "Plant.Tank1"));
            d.Metadata.Add(new KeyValuePair<string, string>("Blank", ""));
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(d), compiled: p.Build()));
            a.Display.Ass(-2, "Blank", "Plant.Tank1", 0);
            a.Display.Ass(-2, "Keep", "Plant.Tank1", 0);
            // Ass with an empty tag returns before the store: the entry stays
            Assert.Equal(0, a.Display.Ass(-2, "Keep", "", 0));
            a.RunScans(1);
            Assert.Contains(a.Display.Pending, e => e.Arg == "Equip" && e.Tag == "Plant.Tank1");
            Assert.Contains(a.Display.Pending, e => e.Arg == "Keep");
            // AssMetadata stores through: an empty value deletes the entry
            Assert.DoesNotContain(a.Display.Pending, e => e.Arg == "Blank");
        }

        [Fact]
        public void AssMetadataMakesMinusThreeTheCurrentWindow()
        {
            // 'cmp bx, -3' then the current window, as Ass does
            var a = Make();
            var p = new PageFixture();
            p.Row(
                "PGADV",
                ("AN", 21L),
                ("CMD", p.Code(c => c.ConstInt(-3).Convert(4).ConstInt(20).Convert(4).Func("AssMetadata", 2)))
            );
            var d = Draw.Obj(20, 0x08);
            d.Metadata.Add(new KeyValuePair<string, string>("Equip", "Plant.Tank1"));
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(d), compiled: p.Build()));
            a.RunScans(1);
            Assert.Contains(a.Display.Pending, e => e.Arg == "Equip" && e.Target == 0);
            Assert.DoesNotContain(a.Display.Pending, e => e.Target == -3);
        }

        [Fact]
        public void PageInfoSevenIsThePreviousDisplayCount()
        {
            // doc 1188754 'Previous display count': 0 inside the first scan (the SA priority
            // buttons' Cicode object runs IF PageInfo(7) = "0" THEN DspSym ...)
            var a = Make();
            var p = new PageFixture();
            p.Dyn(
                10,
                0x05,
                -1,
                ("TXTMODE", 4L),
                ("TXT_STR", p.Code(c => c.ConstInt(7).Convert(4).Func("PageInfo", 1)))
            );
            Assert.Equal(0, a.Open("TestPage", 0, null, compiled: p.Build()));
            var st = a.Run(0)!.State.Objects;
            Assert.Equal("0", st[10].Text);
            a.RunScans(1);
            Assert.Equal("1", st[10].Text);
            a.RunScans(2);
            Assert.Equal("3", st[10].Text);
        }

        [Fact]
        public void DisplayBuiltinsReadThePageAndItsVariables()
        {
            var a = Make();
            var p = new PageFixture("!Pop");
            long Text(int an, Action<CodeBuilder> b)
            {
                long off = p.Code(b);
                p.Dyn(an, 0x05, -1, ("TXTMODE", 4L), ("TXT_STR", off));
                return off;
            }
            Text(10, c => c.ConstInt(0).Convert(4).Func("PageInfo", 1));
            Text(11, c => c.ConstInt(2).Convert(4).Func("PageInfo", 1));
            Text(12, c => c.ConstStr("-2").ConstInt(-1).Convert(4).Func("PageGetInt", 2).Convert(7));
            Text(13, c =>
                c.ConstStr("tab")
                    .ConstStr("Height")
                    .ConstInt(7)
                    .Convert(4)
                    .Func("_LibControl_GetInt", 3)
                    .Convert(7)
            );
            Text(14, c => c.Func("DspGetAnCur", 0).Convert(7));
            Text(15, c => c.ConstInt(18).Convert(4).Func("PageInfo", 1));
            // a Cicode object registers AN 20 as control 'tab' (Library_Controls _LibControl_SetANName)
            p.Row(
                "PGADV",
                ("AN", 20L),
                (
                    "CMD",
                    p.Code(c =>
                        c.ConstStr(">ANtab")
                            .ConstInt(20)
                            .Convert(4)
                            .ConstInt(-1)
                            .Convert(4)
                            .Func("PageSetInt", 3)
                    )
                )
            );
            var d = Draw.Obj(20, 0x08);
            d.Metadata.Add(new KeyValuePair<string, string>("Height", " 33x"));
            Assert.Equal(0, a.Open("!Pop", 0, Draw.Model(d), compiled: p.Build()));
            var st = a.Run(0)!.State.Objects;
            Assert.Equal(" Pop", st[10].Text); // a leading '!' as a space
            Assert.Equal("!Pop title", st[11].Text);
            Assert.Equal("250", st[12].Text); // PageGetInt(-2) = the scan time
            Assert.Equal("7", st[13].Text); // not registered yet: the default
            Assert.Equal("14", st[14].Text);
            Assert.Equal("1", st[15].Text); // flash phase + 1
            a.RunScans(1);
            Assert.Equal("33", st[13].Text); // atol(" 33x")
            Assert.Equal(20, a.Display.Windows[0].IntVars[">ANtab"]);
        }

        [Fact]
        public void PageVariablesAreListedSentAndCleared()
        {
            var a = Make();
            var p = new PageFixture();
            // a text reads ShowMenu, a Cicode object sets almOn1 and a string variable
            p.Dyn(
                30,
                0x05,
                -1,
                ("TXTMODE", 4L),
                (
                    "TXT_STR",
                    p.Code(c =>
                        c.ConstStr("ShowMenu").ConstInt(-1).Convert(4).Func("PageGetInt", 2).Convert(7)
                    )
                )
            );
            p.Row(
                "PGADV",
                ("AN", 20L),
                (
                    "CMD",
                    p.Code(c =>
                        c.ConstStr("almOn1")
                            .ConstInt(1)
                            .Convert(4)
                            .ConstInt(-1)
                            .Convert(4)
                            .Func("PageSetInt", 3)
                            .Op("Pop")
                            .ConstStr("Mode")
                            .ConstStr("Auto")
                            .ConstInt(-1)
                            .Convert(4)
                            .Func("PageSetStr", 3)
                    )
                )
            );
            Assert.Equal(0, a.Open("TestPage", 0, null, compiled: p.Build()));
            a.RunScans(1);
            var run = a.Run(0)!;
            var w = a.Display.Windows[0];
            Assert.Contains(("ShowMenu", false), w.VarNames);
            Assert.Contains(("almOn1", false), w.VarNames);
            Assert.Contains(("Mode", true), w.VarNames);
            string json = run.State.ToJson().ToString();
            Assert.Contains("\"pageVars\"", json);
            Assert.Contains("\"name\": \"almOn1\"", json.Replace("\":\"", "\": \""));
            // a scan that changes nothing sends no pageVars; a changed value does
            var events = new List<string>();
            a.PageChanged += (st, changed) => events.Add(st.ToJson(changed).ToString());
            a.RunScans(1);
            Assert.DoesNotContain(events, e => e.Contains("pageVars"));
            w.IntVars["ShowMenu"] = 1;
            w.NoteVar("ShowMenu", false, true);
            a.RunScans(1);
            Assert.Contains(events, e => e.Contains("pageVars"));
            Assert.Equal("1", run.State.Objects[30].Text);
            // another page: the list starts again
            w.ClearVars();
            Assert.Empty(w.VarNames);
            Assert.Empty(w.IntVars);
        }

        [Fact]
        public void LibControlGetIntKeepsTheCallersErrorState()
        {
            var a = Make();
            var p = new PageFixture();
            // under ErrSet(1) 1 / 0 sets IsError 273 without halting; a successful _LibControl read
            // restores it, a failed one leaves IsError 0
            Action<CodeBuilder> Probe(string field) =>
                c =>
                    c.ConstInt(1)
                        .Convert(4)
                        .Func("ErrSet", 1)
                        .ConstInt(1)
                        .ConstInt(0)
                        .Op("Div")
                        .Op("Pop")
                        .ConstStr("tab")
                        .ConstStr(field)
                        .ConstInt(7)
                        .Convert(4)
                        .Func("_LibControl_GetInt", 3)
                        .Op("Pop")
                        .Func("IsError", 0)
                        .Convert(7);
            p.Dyn(30, 0x05, -1, ("TXTMODE", 4L), ("TXT_STR", p.Code(Probe("Height"))));
            p.Dyn(31, 0x05, -1, ("TXTMODE", 4L), ("TXT_STR", p.Code(Probe("Missing"))));
            p.Row(
                "PGADV",
                ("AN", 20L),
                (
                    "CMD",
                    p.Code(c =>
                        c.ConstStr(">ANtab")
                            .ConstInt(20)
                            .Convert(4)
                            .ConstInt(-1)
                            .Convert(4)
                            .Func("PageSetInt", 3)
                    )
                )
            );
            var d = Draw.Obj(20, 0x08);
            d.Metadata.Add(new KeyValuePair<string, string>("Height", "12"));
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(d), compiled: p.Build()));
            // the 1 / 0 leaves #DIV/0 on the text: show the values (IgnoreValueQuality 1, "No")
            a.Display.Windows[0].IgnoreValueQuality = 1;
            a.RunScans(1);
            var st = a.Run(0)!.State.Objects;
            Assert.Equal("273", st[30].Text);
            Assert.Equal("0", st[31].Text);
        }

        [Fact]
        public void LibControlGettersReportNeitherTheirOwnMissNorTheRestoredCode()
        {
            // Library_Controls' __LibControl_Process takes PageInfo's 256 in a window that closed
            // under it; every later successful _LibControl_GetInt_ByAN puts the pending code back.
            // The runtime only stores the code again (not raised, a syslog line only with
            // DEBUG 2): the error is reported once, where it was raised.
            var a = Make();
            var p = new PageFixture();
            Action<CodeBuilder> Probe(string field) =>
                c =>
                    c.ConstInt(1)
                        .Convert(4)
                        .Func("ErrSet", 1)
                        .ConstInt(1)
                        .ConstInt(0)
                        .Op("Div")
                        .Op("Pop")
                        .ConstStr("tab")
                        .ConstStr(field)
                        .ConstInt(7)
                        .Convert(4)
                        .Func("_LibControl_GetInt", 3)
                        .Op("Pop")
                        .ConstInt(20)
                        .Convert(4)
                        .ConstStr(field)
                        .ConstInt(7)
                        .Convert(4)
                        .Func("_LibControl_GetInt_ByAN", 3)
                        .Op("Pop")
                        .ConstStr("tab")
                        .ConstStr(field)
                        .ConstStr("x")
                        .Func("_LibControl_GetStr", 3)
                        .Op("Pop")
                        .Func("IsError", 0)
                        .Convert(7);
            p.Dyn(30, 0x05, -1, ("TXTMODE", 4L), ("TXT_STR", p.Code(Probe("Height"))));
            p.Dyn(31, 0x05, -1, ("TXTMODE", 4L), ("TXT_STR", p.Code(Probe("Missing"))));
            p.Row(
                "PGADV",
                ("AN", 20L),
                (
                    "CMD",
                    p.Code(c =>
                        c.ConstStr(">ANtab")
                            .ConstInt(20)
                            .Convert(4)
                            .ConstInt(-1)
                            .Convert(4)
                            .Func("PageSetInt", 3)
                    )
                )
            );
            var d = Draw.Obj(20, 0x08);
            d.Metadata.Add(new KeyValuePair<string, string>("Height", "12"));
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(d), compiled: p.Build()));
            a.Display.Windows[0].IgnoreValueQuality = 1;
            a.RunScans(1);
            var reports = new List<(int Code, string Builtin)>();
            var prev = a.Vm.ErrorReport;
            a.Vm.ErrorReport = (t, code, halting, suppressed) =>
            {
                reports.Add((code, t.CurrentBuiltin >= 0 ? a.Vm.Table.Name(t.CurrentBuiltin) : ""));
                prev?.Invoke(t, code, halting, suppressed);
            };
            a.RunScans(3);
            var st = a.Run(0)!.State.Objects;
            Assert.Equal("273", st[30].Text); // the caller's code kept
            Assert.Equal("0", st[31].Text); // a failed read leaves IsError 0
            Assert.DoesNotContain(reports, r =>
                r.Builtin.StartsWith("_LibControl_", StringComparison.Ordinal)
            );
            Assert.Contains(reports, r => r.Code == 273); // the 1 / 0 itself, where it happened
        }

        // ------------------------------------------------------------------ classes
        [Fact]
        public void ClassesUseTheDrawingData()
        {
            var a = Make();
            var p = new PageFixture();
            // flashing On / Off: the pair's halves alternate every [Animator]FlashTime
            p.Dyn(13, 0x0F, -1, ("COLMODE", 0L), ("COL_A", p.Const(1)));
            // threshold over the default thresholds: 97 % > 95
            p.Dyn(16, 0x0F, -1, ("COLMODE", 3L), ("COL_EXP", p.ConstReal(97)));
            // gradient from the static colour to COL_MAXON
            p.Dyn(18, 0x0F, -1, ("COLMODE", 4L), ("COL_EXP", p.ConstReal(50)));
            // multi-state: A = 1, B = 0, C = 1 -> state 5
            p.Dyn(
                19,
                0x0F,
                -1,
                ("COLMODE", 1L),
                ("COL_A", p.Const(1)),
                ("COL_B", p.Const(0)),
                ("COL_C", p.Const(1))
            );
            // array colour index past the end: the last entry and an error
            p.Dyn(21, 0x0F, -1, ("COLMODE", 2L), ("COL_ARR", p.Const(9)));
            // animated symbol set: frames 1..3 every [Page]AnmDelay
            p.Dyn(14, 0x0C, -1, ("TXTMODE", 3L), ("TXT_A", p.Const(1)));
            // array symbol set: past the states but below the declared count -> nothing, no error
            p.Dyn(17, 0x0C, -1, ("TXTMODE", 2L), ("SET_ARR", p.Const(5)));
            p.Dyn(22, 0x0C, -1, ("TXTMODE", 2L), ("SET_ARR", p.Const(-1)));
            // vertex: 50 % between From (0, 0) and To (0, -100)
            p.Row(
                "PGVERTEX",
                ("INDEX", 1L),
                ("AN", 15L),
                ("EXPR", p.ConstReal(50)),
                ("X1", 0L),
                ("Y1", 0L),
                ("X2", 0L),
                ("Y2", -100L)
            );
            // fill level with FILCOL
            p.Dyn(23, 0x0F, -1, ("FIL", p.ConstReal(40)), ("FILMODE", 2L));
            // display value: on / off text and an array text clamped
            p.Dyn(24, 0x05, -1, ("TXTMODE", 0L), ("TXT_A", p.Const(1)));
            p.Dyn(25, 0x05, -1, ("TXTMODE", 2L), ("TXT_A", p.Const(-4)));
            // rotation about a Custom pivot; scaling about the right edge
            p.Dyn(
                26,
                0x0F,
                -1,
                ("ROT", p.ConstReal(25)),
                ("ROTMINPC", 0L),
                ("ROTMAXPC", 360L),
                ("ROTMODE", 1L),
                ("ROTXORG", 8L),
                ("ROTYORG", 13L),
                ("SZX", p.ConstReal(50)),
                ("SZXMINPC", 0L),
                ("SZXMAXPC", 200L),
                ("SZXORG", 1L)
            );
            var m = new PageModel();
            var flash = Draw.Obj(13, 0x0F);
            flash.Colour = new ColourStates
            {
                State0 = ColourPair.Steady(0xFFFFFF),
                MaxOn = new ColourPair(0xFF0000, 0x000000),
            };
            var grad = Draw.Obj(18, 0x0F);
            grad.Colour = new ColourStates
            {
                State0 = ColourPair.Steady(0xFF0000),
                MaxOn = ColourPair.Steady(0x0000FF),
            };
            var multi = Draw.Obj(19, 0x0F);
            multi.Colour = new ColourStates();
            for (uint k = 0; k < 8; k++)
                multi.Colour.Sta.Add(ColourPair.Steady(0x100000 * k));
            var arr = Draw.Obj(21, 0x0F);
            arr.Colour = new ColourStates
            {
                Arr = new List<ColourPair>
                {
                    ColourPair.Steady(1),
                    ColourPair.Steady(2),
                    ColourPair.Steady(3),
                },
            };
            var anim = Draw.Obj(14, 0x0C);
            anim.SymbolSet = new SymbolSetInfo { States = 4 };
            var set = Draw.Obj(17, 0x0C);
            set.SymbolSet = new SymbolSetInfo { States = 3, Declared = 256 };
            var set2 = Draw.Obj(22, 0x0C);
            set2.SymbolSet = new SymbolSetInfo { States = 3, Declared = 256 };
            var fill = Draw.Obj(23, 0x0F);
            fill.Colour = new ColourStates { FilCol = ColourPair.Steady(0x123456) };
            var onoff = Draw.Obj(24, 0x05);
            onoff.Text = new TextStrings { Off = "Stopped", On = "Running" };
            var atext = Draw.Obj(25, 0x05);
            atext.Text = new TextStrings { Array = { "zero", "one" } };
            var rot = Draw.Obj(26, 0x0F, new Rect(100, 200, 141, 230));
            foreach (var o in new[] { flash, grad, multi, arr, anim, set, set2, fill, onoff, atext, rot })
                m.Drawing.Add(o);
            Assert.Equal(0, a.Open("TestPage", 0, m, compiled: p.Build()));
            var r = a.Run(0)!;
            var s = r.State.Objects;
            Assert.Equal(0xFF0000L, s[13].FillColour);
            Assert.Equal(0xFF0000L, s[16].FillColour);
            Assert.Equal(120, Colours.ToHls((uint)s[18].FillColour!.Value).H);
            Assert.Equal(0x500000L, s[19].FillColour);
            Assert.Equal(3L, s[21].FillColour);
            Assert.Null(s[21].Indication); // a hardware alarm (logged), not an indication
            Assert.Equal(1, s[14].SymbolState);
            Assert.Equal(-1, s[17].SymbolState);
            Assert.Null(s[17].Indication);
            Assert.Equal(-1, s[22].SymbolState);
            Assert.Contains(a.Logs, l => l.Contains("hardware alarm 274") && l.Contains("AN 22"));
            Assert.Equal(new[] { 0.0, 0.0 }, s[15].VertexOffsets![0]);
            Assert.Equal(new[] { 0.0, -50.0 }, s[15].VertexOffsets![1]);
            Assert.Equal(0.4, s[23].FillLevel!.Value, 12);
            Assert.Equal(2, s[23].FillLevelDirection);
            Assert.Equal(0x123456L, s[23].FillLevelColour);
            Assert.Equal("Running", s[24].Text);
            Assert.Equal("zero", s[25].Text); // < 0 -> entry 0 with an error
            Assert.Equal(90.0, s[26].Rotate!.Value, 9);
            Assert.Equal(new[] { 100.0 + 20 + 8, 200.0 + 15 + 13 }, s[26].RotateOrigin);
            Assert.Equal(new[] { 1.0, 1.0 }, s[26].Scale);
            Assert.Equal(new[] { 141.0, 200.0 + 15 }, s[26].ScaleOrigin);
            // the flash phase flips at 0.5 s: the ON / OFF halves alternate
            a.RunFor(0.5);
            Assert.Equal(0x000000L, s[13].FillColour);
            Assert.False(r.State.FlashOn);
            // the animated set advances on each [Page]AnmDelay tick, 1..3 then back to 1
            int f0 = s[14].SymbolState!.Value;
            a.RunFor(0.5);
            Assert.Equal(f0 % 3 + 1, s[14].SymbolState);
        }

        [Theory]
        [InlineData(false, null)]
        [InlineData(true, "error")]
        public void AMultiStateRangeErrorShowsOnlyWithRangeCheck(bool rangeCheck, string? indication)
        {
            // a condition's 257 is dropped unless [Page]RangeCheck
            var a = Make();
            a.Display.Config.RangeCheck = rangeCheck;
            var p = new PageFixture();
            long sc = p.Pb.Pools.Scale(0.0, 100.0);
            p.Dyn(19, 0x0F, -1, ("COLMODE", 1L), ("COL_A", p.Code(c => c.ConstReal(150.0).Bar(sc))));
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            var s = a.Run(0)!.State.Objects[19];
            Assert.Equal(Colours.StateDefaults[1], (uint)s.FillColour!.Value);
            Assert.Equal(indication, s.Indication);
        }

        [Fact]
        public void QualityAndErrorsShowOnTexts()
        {
            var a = Make();
            var p = new PageFixture();
            p.Dyn(
                40,
                0x05,
                -1,
                ("TXTMODE", 3L),
                ("TXT_STR", p.Code(c => c.Load("NoSuchTag").StringFmt(0x105)))
            );
            p.Dyn(
                41,
                0x05,
                -1,
                ("TXTMODE", 3L),
                ("TXT_STR", p.Code(c => c.Load("Tank1_Temp").StringFmt(0x105)))
            );
            p.Dyn(
                42,
                0x05,
                -1,
                ("TXTMODE", 3L),
                ("TXT_STR", p.Code(c => c.ConstInt(1).ConstInt(0).Op("Div").StringFmt(0x105)))
            );
            _world.Tags.Set("Tank1_Temp", 12.5);
            _world.Tags.SetQuality("Tank1_Temp", "bad");
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            var s = a.Run(0)!.State.Objects;
            Assert.Equal("#BAD", s[40].Text); // 424 with bad quality
            Assert.Equal("#BAD", s[41].Text); // a bad-quality read (live: Mixing_L3 tank_level f3)
            Assert.Equal("bad", s[41].Indication);
            Assert.Equal("#DIV/0", s[42].Text);
            // IgnoreValueQuality 1 (No): the values
            a.Display.Windows[0].IgnoreValueQuality = 1;
            a.RunScans(1);
            Assert.Null(s[40].Error);
            Assert.Null(s[41].Indication);
        }

        [Theory]
        [InlineData("bad")]
        [InlineData("uncertain")]
        public void ANotGoodReadWithoutAnErrorShowsOkWhenItsTextIsOff(string quality)
        {
            // doc 1118038, row 'No Error' (0): #BAD / #UNC by default, #OK (the error text of 0)
            // with ShowBadText = ShowUncertainText = 0 and ShowErrorText = 1
            var a = Make();
            var p = new PageFixture();
            p.Dyn(
                41,
                0x05,
                -1,
                ("TXTMODE", 3L),
                ("TXT_STR", p.Code(c => c.Load("Tank1_Temp").StringFmt(0x105)))
            );
            _world.Tags.Set("Tank1_Temp", 12.5);
            _world.Tags.SetQuality("Tank1_Temp", quality);
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            var s = a.Run(0)!.State.Objects;
            Assert.Equal(quality == "bad" ? "#BAD" : "#UNC", s[41].Text);
            a.Display.Config.ShowBadText = a.Display.Config.ShowUncertainText = false;
            a.RunScans(1);
            Assert.Equal("#OK", s[41].Text);
            a.Display.Config.ShowErrorText = false;
            a.RunScans(1);
            Assert.Equal(" 12.5", s[41].Text);
        }

        [Fact]
        public void KeyEchoShowsTheKeyLineOnAnOne()
        {
            var a = Make();
            a.Keys.Add("ENTER", 0x0D);
            var p = new PageFixture();
            p.Row(
                "PGKEY",
                ("AN", 0L),
                ("KEY_SEQ", "#### ENTER"),
                ("CMD", p.Command(c => c.ConstInt(0).Convert(4).Func("GetGlbStr", 1).Store("LocStr")))
            );
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(Draw.Obj(1, 0x05)), compiled: p.Build()));
            a.Key(0, "4");
            a.Key(0, "2");
            a.RunScans(1);
            Assert.Equal("42", a.Run(0)!.State.Objects[1].Text);
            a.Key(0, "ENTER");
            a.RunScans(1);
            Assert.Null(a.Run(0)!.State.Objects[1].Text);
            Assert.Equal("42", Str("LocStr"));
        }

        [Fact]
        public void WindowsHistoryAndActivation()
        {
            var a = Make();
            CompiledPage P(string name)
            {
                var p = new PageFixture(name);
                p.Row(
                    "Control",
                    ("ACT_FUNC", p.Command(c => Append(c, "A"))),
                    ("DEACT_FUNC", p.Command(c => Append(c, "D")))
                );
                return p.Build();
            }
            _world.Tags.Set("LocStr", "");
            Assert.Equal(0, a.Open("One", 0, compiled: P("One")));
            Assert.Equal(0, a.Open("Two", 0, compiled: P("Two")));
            var w = a.Display.Windows[0];
            Assert.Equal(new[] { "One", "Two" }, w.History.ToArray());
            Assert.Equal(1, w.HistoryPos);
            // back keeps the forward entry; a new page drops it
            a.Display.Catalogue = null;
            Assert.Equal(261, a.History(0, -1)); // no compiled 'One' to reopen here
            Assert.Equal(274, a.History(0, +1));
            // activation runs ACT_FUNC / DEACT_FUNC as page tasks
            a.Activate(0, true);
            a.Activate(0, true); // already active: nothing
            a.Activate(0, false);
            a.RunFor(0.1);
            Assert.Equal("AD", Str("LocStr"));
            // a second window, freed again
            int n = a.NewWindow("nowhere");
            Assert.Equal(-1, n);
            Assert.Equal(1, a.Display.OpenCount());
        }

        [Fact]
        public void AHiddenGroupsMemberTakesNoInputAndParentCyclesAreCut()
        {
            var a = Make();
            var p = new PageFixture();
            byte[] Add(long n) => p.Command(c => c.Load("LocInt").ConstInt(n).Op("Add").Store("LocInt"));
            // a group hidden by VISIBLE = Valve_A; its member (AN 41) has no animation of its own
            p.Dyn(40, 0x08, -1, ("VISIBLE", p.Load("Valve_A")));
            p.Row("PGTOUCH", ("AN", 41L), ("ACTION", 1L), ("CMD", Add(1)));
            // two objects whose model parents name each other (a malformed model)
            p.Dyn(50, 0x0F, -1, ("MVX", p.ConstReal(50)), ("MVXMAXPT", 100L));
            p.Dyn(51, 0x0F, -1, ("MVX", p.ConstReal(50)), ("MVXMAXPT", 100L));
            p.Row("PGTOUCH", ("AN", 50L), ("ACTION", 1L), ("CMD", Add(100)));
            p.Row("PGKEY", ("AN", 50L), ("KEY_SEQ", "Q"), ("CMD", Add(1000)));
            var m = Draw.Model(
                Draw.Obj(40, 0x08),
                Draw.Obj(41, 0x0F, parent: 40),
                Draw.Obj(50, 0x0F, parent: 51),
                Draw.Obj(51, 0x0F, parent: 50)
            );
            _world.Tags.Set("Valve_A", 1);
            Assert.Equal(0, a.Open("TestPage", 0, m, compiled: p.Build()));
            _world.Tags.Set("LocInt", 0);
            Assert.False(a.Touch(0, 41, Animator.TouchKind.Up));
            _world.Tags.Set("Valve_A", 0);
            a.RunScans(1);
            Assert.True(a.Touch(0, 41, Animator.TouchKind.Up));
            // the cycle is cut where it closes: the input checks and the key chain end
            Assert.True(a.Touch(0, 50, Animator.TouchKind.Up));
            a.Display.Windows[0].CommandCursor = 50;
            Assert.True(a.Key(0, "Q"));
            a.RunFor(0.1);
            Assert.Equal(1101, (int)Num("LocInt"));
        }

        [Fact]
        public void ANewAnimatorTakesOverTheWorldsDisplay()
        {
            var first = Make();
            var second = Make();
            Assert.Same(second, _world.Display.Controller);
            // one flash timer: the phase changes once per [Animator]FlashTime (two animators
            // toggling it would leave it as it was)
            int f0 = _world.Display.FlashPhase;
            second.RunFor(0.5);
            Assert.NotEqual(f0, _world.Display.FlashPhase);
            first.Dispose();
            Assert.Same(second, _world.Display.Controller);
            second.Dispose();
            Assert.Null(_world.Display.Controller);
        }

        [Fact]
        public void AFlashPhaseChangeAloneSendsAPageChanged()
        {
            // a page whose objects do not change: a viewer still needs the phase, for the
            // colours and pictures of the drawing that flash of their own
            var a = Make();
            var p = new PageFixture();
            var m = Draw.Model(Draw.Obj(10, 0x0F));
            Assert.Equal(0, a.Open("TestPage", 0, m, compiled: p.Build()));
            a.RunScans(1);
            var phases = new List<bool>();
            a.PageChanged += (st, changed) => phases.Add(st.FlashOn);
            a.RunFor(1.6);
            Assert.Contains(true, phases);
            Assert.Contains(false, phases);
            // one event per phase change, not one per scan
            Assert.True(phases.Count <= 6, string.Join(",", phases));
        }

        [Fact]
        public void AnAnimatorOverTheSimulationsVmSharesItsVariables()
        {
            var lb = new ProgramBuilder();
            lb.Pools.CodeVar("g", Types.Long, init: 0);
            var set = lb.Function("SetG", "", Types.Void);
            set.Begin();
            set.ConstInt(7).StoreVar("g");
            set.Ret();
            set.End();
            var get = lb.Function("GetG", "", Types.Long);
            get.Begin();
            get.LoadVar("g").RetValue();
            get.End();
            // about 40,000 instructions: more than the simulation's 20,000 a background slice,
            // less than a foreground slice (TimeSlicePage 500 ms)
            lb.Pools.CodeVar("n", Types.Long, init: 0);
            var spin = lb.Function("Spin", "", Types.Long);
            spin.Begin();
            spin.ConstInt(0).StoreVar("n");
            spin.Label("top")
                .LoadVar("n")
                .ConstInt(1)
                .Op("Add")
                .StoreVar("n")
                .LoadVar("n")
                .ConstInt(5000)
                .Op("Lt")
                .BrTrue("top");
            spin.LoadVar("n").RetValue();
            spin.End();
            var lib = lb.Build();
            // the simulation's VM (as Simulation makes it: World.Tags, its own SimHost)
            // runs SetG as a task; page code on the same VM sees the variable it set
            var cfg = CodeConfig.FromParams((s, k, d) => d);
            cfg.MaxSliceOps = 20000;
            var vm = new CicodeVm(
                lib,
                CicodeSim.Builtins.WorldBuiltins.Create(_world),
                _world.Tags,
                _world.Clock,
                null,
                cfg
            );
            var sched = new Scheduler(vm);
            sched.Spawn("SetG");
            sched.Run();
            var a = new Animator(_world, vm, sched);
            Assert.Same(vm, a.Vm);
            Assert.Same(a.Tags, vm.Tags);
            var p = new PageFixture();
            p.Dyn(70, 0x05, -1, ("TXTMODE", 4L), ("TXT_STR", p.Code(c => c.Call("GetG", 0).Convert(7))));
            p.Dyn(71, 0x05, -1, ("TXTMODE", 4L), ("TXT_STR", p.Code(c => c.Call("Spin", 0).Convert(7))));
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            Assert.Equal("7", a.Run(0)!.State.Objects[70].Text);
            // page code runs with the foreground count (100,000 here), the VM keeps its own
            Assert.Equal(100000, a.ForegroundOps);
            Assert.Equal("5000", a.Run(0)!.State.Objects[71].Text);
            Assert.Equal(20000, vm.Config.MaxSliceOps);
            // with the VM's own 20,000 the same expression stops (285, halted)
            long halted = a.StatsOf("display_string").Halted;
            a.ForegroundOps = 20000;
            a.RunScans(1);
            Assert.Equal(halted + 1, a.StatsOf("display_string").Halted);
            // a VM without an instruction count is refused (no hang on the simulated clock)
            var bare = new CicodeVm(
                lib,
                null,
                _world.Tags,
                _world.Clock,
                null,
                CodeConfig.FromParams((s, k, d) => d)
            );
            Assert.Throws<ArgumentException>(() => new Animator(_world, bare, new Scheduler(bare)));
        }

        [Fact]
        public void AnExplicitConfigStillBoundsForegroundCode()
        {
            // MaxSliceOps 0 (no count) and a simulated clock that stands still while code runs:
            // the animator still counts the slice, so the loop halts (285) instead of hanging
            var cfg = CodeConfig.FromParams((s, k, d) => d);
            Assert.Equal(0, cfg.MaxSliceOps);
            var a = new Animator(_world, new Program(), null, cfg);
            var p = new PageFixture();
            p.Dyn(60, 0x05, -1, ("TXTMODE", 4L), ("TXT_STR", p.Code(c => c.Label("top").Branch("top"))));
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            Assert.Equal(1, a.StatsOf("display_string").Halted);
        }

        [Fact]
        public void ABusyCommandTaskCostsTimeAndScansGoOn()
        {
            var a = Make();
            var p = new PageFixture();
            // WHILE TRUE DO LocInt = LocInt + 1 END as a touch command
            p.Row(
                "PGTOUCH",
                ("AN", 3L),
                ("ACTION", 1L),
                (
                    "CMD",
                    p.Command(c =>
                        c.Label("top").Load("LocInt").ConstInt(1).Op("Add").Store("LocInt").Branch("top")
                    )
                )
            );
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            var run = a.Run(0)!;
            long s0 = run.Scans;
            Assert.True(a.Touch(0, 3, Animator.TouchKind.Up));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            a.RunFor(1.0);
            Assert.Equal(s0 + 4, run.Scans);
            Assert.True(Num("LocInt") > 0);
            Assert.True(sw.Elapsed.TotalSeconds < 60, sw.Elapsed.ToString());
        }
    }
}
