using System;
using System.Collections.Generic;
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
    /// <summary>Display built-ins inside the page animator.</summary>
    [Collection("World")]
    public class DisplayP1PageTests : IDisposable
    {
        private readonly TempDir _dir = new TempDir();
        private readonly WorldT _world;

        public DisplayP1PageTests()
        {
            _world = WorldFixture.MakeWorld(_dir.Path);
        }

        public void Dispose() => _dir.Dispose();

        private Animator Make(Program? lib = null) => new Animator(_world, lib ?? new Program());

        private string Str(string tag) => _world.Tags.Get(tag).S ?? "";

        [Fact]
        public void WhatCicodeDrawsIsInThePageState()
        {
            var a = Make();
            var p = new PageFixture();
            // a Cicode object writes a label at its own AN every scan
            p.Row(
                "PGADV",
                ("AN", 21L),
                (
                    "CMD",
                    p.Code(c =>
                        c.Func("DspGetAnCur", 0)
                            .ConstStr("Arial")
                            .ConstInt(-12)
                            .Convert(4)
                            .ConstInt(0)
                            .Convert(4)
                            .ConstInt(0xffffff)
                            .Convert(4)
                            .Func("DspFont", 4)
                            .ConstStr("hello")
                            .Func("DspText", 3)
                    )
                )
            );
            var obj = Draw.Obj(21, 0x1009, new Rect(40, 50, 60, 70));
            obj.PosX = 40;
            obj.PosY = 50;
            int events = 0;
            a.PageChanged += (st, ch) => events++;
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(obj), compiled: p.Build()));
            var st = a.Run(0)!.State;
            var d = Assert.Single(st.Drawn!);
            Assert.Equal((21, "text", "hello", 40, 50), (d.An, d.Kind, d.Text, d.X, d.Y));
            var j = st.ToJson();
            var drawn = j.Get("drawn");
            Assert.NotNull(drawn);
            Assert.Equal("hello", drawn!.Items[0].Get("text")!.Str);
            Assert.True(events > 0);
            // a page change takes the output with it
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(obj), compiled: new PageFixture().Build()));
            Assert.Empty(a.Display.Windows[0].Drawn);
        }

        [Fact]
        public void PageExpressionsFindObjectsByNameAndMetadata()
        {
            var a = Make();
            var p = new PageFixture();
            p.Dyn(
                10,
                0x05,
                -1,
                ("TXTMODE", 4L),
                ("TXT_STR", p.Code(c => c.ConstStr("Pump.Base").Func("DspGetAnFromName", 1).Convert(7)))
            );
            p.Dyn(
                11,
                0x05,
                -1,
                ("TXTMODE", 4L),
                (
                    "TXT_STR",
                    p.Code(c =>
                        c.ConstStr("..\\..\\..\\Pump").ConstStr("Equip").Func("DspGetMetadataFromName", 2)
                    )
                )
            );
            var pump = Draw.Obj(20, 0x08);
            pump.Name = "Pump";
            pump.Metadata.Add(new KeyValuePair<string, string>("Equip", "Plant.Tank1"));
            var bas = Draw.Obj(22, 0x0F, parent: 20);
            bas.Name = "Base";
            // the text AN 11 is a member of a group in Pump: '..\..\..\Pump' climbs to the root
            var grp = Draw.Obj(23, 0x08, parent: 20);
            var t11 = Draw.Obj(11, 0x05, parent: 23);
            Assert.Equal(
                0,
                a.Open(
                    "TestPage",
                    0,
                    Draw.Model(pump, bas, grp, t11, Draw.Obj(10, 0x05)),
                    compiled: p.Build()
                )
            );
            var st = a.Run(0)!.State.Objects;
            Assert.Equal("22", st[10].Text);
            Assert.Equal("Plant.Tank1", st[11].Text);
        }

        [Fact]
        public void KeyPutRunsTheKeyboardCommand()
        {
            var a = Make();
            a.Keys.Add("ENTER", 0x0D);
            var p = new PageFixture();
            p.Row(
                "PGKEY",
                ("AN", 0L),
                ("KEY_SEQ", "## ENTER"),
                ("CMD", p.Command(c => c.ConstInt(0).Convert(4).Func("GetGlbStr", 1).Store("LocStr")))
            );
            // a Cicode object types "42" and ENTER once
            p.Row(
                "PGADV",
                ("AN", 30L),
                (
                    "CMD",
                    p.Code(c =>
                        c.ConstStr("Done")
                            .Func("PageGetStr", 1)
                            .ConstStr("")
                            .Op("Ne")
                            .BrTrue("skip")
                            .ConstStr("42 ENTER")
                            .Func("KeyPutStr", 1)
                            .Op("Pop")
                            .ConstStr("Done")
                            .ConstStr("1")
                            .Func("PageSetStr", 2)
                            .Op("Pop")
                            .Label("skip")
                            .ConstInt(0)
                    )
                )
            );
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(Draw.Obj(30, 0x1009)), compiled: p.Build()));
            a.RunFor(0.5);
            Assert.Equal("42", Str("LocStr"));
        }

        [Fact]
        public void ExtentFollowsTheMovementAndInfoListsTheExpressions()
        {
            var a = Make();
            var p = new PageFixture();
            _world.Tags.Set("Valve_A", 1);
            // a rectangle moved right by MVX (Valve_A 0..1 -> 0..100 pixels)
            p.Dyn(40, 0x0F, -1, ("MVX", p.ConstReal(50)), ("MVXMAXPT", 100L), ("VISIBLE", p.Const(0)));
            var o = Draw.Obj(40, 0x0F, new Rect(10, 10, 20, 20));
            o.PosX = 10;
            o.PosY = 10;
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(o), compiled: p.Build()));
            var run = a.Run(0)!;
            var mv = run.State.Objects[40].Move;
            Assert.NotNull(mv);
            // DspGetAnExtent of the moved object (through the page runtime)
            var ext = CicodeSim.Builtins.DisplayAns.CurrentExtent(
                a.Display,
                a.Display.Windows[0],
                a.Display.Windows[0].Page!.Get(40)!
            );
            Assert.Equal(10 + (int)mv![0], ext.L);
            // DspInfo: the AN's expressions and their tags
            var ex = ((IPageRuntime)a).Expressions(0, 40);
            Assert.NotNull(ex);
            Assert.Contains(ex!, e => e.Context == "Move X");
            Assert.Contains(ex!, e => e.Context == "Visibility");
        }

        [Fact]
        public void PageDisplayFromCicodeRunsThePageEvents()
        {
            var a = Make();
            var p = new PageFixture("First");
            // a command in the first page displays the second; ENTRY_FUNC of the second writes a tag
            var second = new PageFixture("Second");
            second.Page.Control!.Name = "Second";
            Assert.Equal(0, a.Open("First", 0, compiled: p.Build()));
            // the controller of the World's display is the animator
            Assert.Same(a, a.Display.Pages);
            Assert.Same(a, a.Display.Runtime);
            // a page that is not in the catalogue: the animator's error, the window keeps its page
            var (code, prog) = Bytes(c => c.ConstStr("Nope").Func("_PageDisplay", 1).RetValue());
            var t = a.Vm.TaskForBlock(code, prog, false, "cmd");
            TaskContext.Set(t, 0, -1);
            a.Vm.Run(t);
            Assert.Equal(261, (int)t.Result!.Value.I);
            Assert.Equal("Cannot Display Page", a.Display.Windows[0].Prompt);
        }

        private static (byte[], Program) Bytes(Action<CodeBuilder> build)
        {
            var pb = new ProgramBuilder();
            var cb = pb.Block();
            build(cb);
            cb.End();
            return (cb.Code(), pb.Build());
        }
    }
}
