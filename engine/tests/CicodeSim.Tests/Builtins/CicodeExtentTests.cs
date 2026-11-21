using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Sim;
using CicodeSim.Tests.Host;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>What Cicode draws on an AN gives the AN its extent.</summary>
    [Collection("World")]
    public class CicodeExtentTests : DataTestBase
    {
        private sealed class Host : IDisplayHost
        {
            public PageDrawing? Drawing;

            public PageRecord? FindPage(string name, string cluster) =>
                new PageRecord { Name = name, Title = name };

            public PageDrawing? LoadDrawing(PageRecord page) => Drawing;

            public void Shown(int window, PageInstance page) { }

            public void DisplayOutput(int window, int an, string kind, string text) { }

            public void WindowChanged(Window w) { }
        }

        private static Res C(Sim s, string name, params object[] args)
        {
            var b = Func0Table.Default.Lookup(name);
            Assert.True(b != null, name);
            var t = new CicodeTask(s.Run.Vm, "test", false);
            TaskContext.Set(t, 0, -1);
            var vals = new List<Value>();
            foreach (object a in args)
                vals.Add(a is Value v ? v : CicodeVm.ValueOf(a));
            var r = s.Run.Vm.Builtins!.Call(b!.Index, t, ArgList.Of(vals.ToArray()));
            Assert.False(r.IsNotSimulated, name + " is not simulated");
            return new Res { V = r, Task = t };
        }

        private static (long T, long L, long B, long R) Extent(Sim s, int an)
        {
            Value t = Ref(Value.Long(0)),
                l = Ref(Value.Long(0)),
                b = Ref(Value.Long(0)),
                r = Ref(Value.Long(0));
            Assert.Equal(0, C(s, "DspGetAnExtent", an, t, l, b, r).I);
            return (A.I(Held(t)), A.I(Held(l)), A.I(Held(b)), A.I(Held(r)));
        }

        /// <summary>A graphics library file with one element: a bitmap-like root (tc 7, no
        /// children) whose head origin is <paramref name="ex"/>, <paramref name="ey"/>, slot 2
        /// T(-e), rect1 <paramref name="rect"/>.</summary>
        internal static void WriteLibrary(
            string path,
            string element,
            int ex,
            int ey,
            (short L, short T, short R, short B) rect,
            int? slot2X = null,
            int? slot2Y = null
        )
        {
            var body = new List<byte>();
            void U16(int v) => body.AddRange(BitConverter.GetBytes((ushort)v));
            void I16(int v) => body.AddRange(BitConverter.GetBytes((short)v));
            U16(0);
            U16(5);
            U16(0); // reserved, handle, object name ''
            body.Add(0); // slot 1: identity
            body.Add(1);
            body.AddRange(BitConverter.GetBytes((double)-(slot2X ?? ex)));
            body.AddRange(BitConverter.GetBytes((double)-(slot2Y ?? ey)));
            body.Add(0); // slot 3
            I16(ex);
            I16(ey); // anchor
            I16(rect.L);
            I16(rect.T);
            I16(rect.R);
            I16(rect.B); // rect1
            I16(rect.L);
            I16(rect.T);
            I16(rect.R);
            I16(rect.B); // rect2
            var payload = new List<byte>();
            payload.AddRange(BitConverter.GetBytes((ushort)0)); // children
            payload.AddRange(BitConverter.GetBytes((uint)7)); // type code
            payload.AddRange(BitConverter.GetBytes((uint)0)); // flags
            payload.AddRange(BitConverter.GetBytes((ushort)2)); // an string '-1'
            payload.AddRange(Encoding.ASCII.GetBytes("-1"));
            payload.AddRange(body);
            var rec = new List<byte>();
            rec.AddRange(BitConverter.GetBytes((uint)payload.Count));
            rec.AddRange(BitConverter.GetBytes((uint)0));
            rec.AddRange(payload);
            var head = new byte[0x34];
            BitConverter.GetBytes((uint)rec.Count).CopyTo(head, 0);
            BitConverter.GetBytes((uint)1).CopyTo(head, 4);
            BitConverter.GetBytes(ex).CopyTo(head, 0x0C);
            BitConverter.GetBytes(ey).CopyTo(head, 0x10);
            BitConverter.GetBytes(rect.R - rect.L).CopyTo(head, 0x14);
            BitConverter.GetBytes(rect.B - rect.T).CopyTo(head, 0x18);
            var block = new List<byte>(head);
            block.AddRange(rec);
            var file = new List<byte>();
            file.AddRange(BitConverter.GetBytes((ushort)7)); // version
            file.AddRange(BitConverter.GetBytes((ushort)0x0C)); // word2
            file.AddRange(new byte[8]); // created, modified
            file.AddRange(BitConverter.GetBytes((uint)1)); // directory slots
            int off = 0x10 + 42;
            file.AddRange(BitConverter.GetBytes((ushort)1));
            var name = new byte[32];
            Encoding.ASCII.GetBytes(element).CopyTo(name, 0);
            file.AddRange(name);
            file.AddRange(BitConverter.GetBytes((uint)off));
            file.AddRange(BitConverter.GetBytes((uint)block.Count));
            file.AddRange(block);
            File.WriteAllBytes(path, file.ToArray());
        }

        private void Fonts() =>
            Dbf(
                Path.Combine(MainDir, "fonts.dbf"),
                new[] { "NAME", "TYPE", "SIZE", "FGND_COLR", "BGND_COLR" },
                new[] { "Small", "Arial", "12", "0x00000000", "" }
            );

        private Sim Show(PageDrawing? d = null, Action? tables = null)
        {
            var s = MakeData(tables ?? Fonts);
            s.W.Display.Host = new Host
            {
                Drawing = d
                    ?? new PageDrawing
                    {
                        Page = "Main",
                        Width = 800,
                        Height = 600,
                    },
            };
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            return s;
        }

        [Fact]
        public void ASymbolGivesItsAnTheElementsRectWithTheOriginOnTheAn()
        {
            var s = Show(tables: () =>
                WriteLibrary(Path.Combine(MainDir, "icons_test.ctl"), "page", 100, 115, (100, 100, 116, 116))
            );
            int an = (int)C(s, "DspAnNew", 1150, 148).I;
            Assert.Equal((148L, 1150L, 148L, 1150L), Extent(s, an)); // a new AN: its point
            Assert.Equal(0, C(s, "DspSym", an, "icons_test.page", 1).I);
            // origin (100, 115) on (1150, 148): the 16 x 16 icon above-right of the point
            Assert.Equal((133L, 1150L, 149L, 1166L), Extent(s, an));
            // Library_Controls: DspGetAnWidth = Right - Left = the icon's width, 16
            var (t, l, b, r) = Extent(s, an);
            Assert.Equal(16, r - l);
            // DspAnMove takes the symbol and its extent along
            Assert.Equal(0, C(s, "DspAnMove", an, 1200, 200).I);
            Assert.Equal((185L, 1200L, 201L, 1216L), Extent(s, an));
            // DspDel: the AN's point again
            Assert.Equal(0, C(s, "DspDel", an).I);
            Assert.Equal((200L, 1200L, 200L, 1200L), Extent(s, an));
            // a library the simulator cannot find leaves the point
            Assert.Equal(0, C(s, "DspSym", an, "nolib.page", 1).I);
            Assert.Equal((200L, 1200L, 200L, 1200L), Extent(s, an));
            // DspSymAtSize: offset from the AN, scaled by size / 32000 about the origin
            Assert.Equal(0, C(s, "DspSymAtSize", an, "icons_test.page", 10, 0, 16000, 32000).I);
            Assert.Equal((185L, 1210L, 201L, 1218L), Extent(s, an));
        }

        [Fact]
        public void AStaleElementsSlotTwoDoesNotMoveTheExtent()
        {
            // Include Progress.ctl Spinner1_1: head origin (100, 149), rect1 (100, 100, 150, 150),
            // but slot 2 T(-687, -146) (only the class-native rect followed it): the frame is
            // rect1 about the head origin
            var s = Show(tables: () =>
                WriteLibrary(
                    Path.Combine(MainDir, "stale_test.ctl"),
                    "spin",
                    100,
                    149,
                    (100, 100, 150, 150),
                    687,
                    146
                )
            );
            int an = (int)C(s, "DspAnNew", 400, 300).I;
            Assert.Equal(0, C(s, "DspSym", an, "stale_test.spin").I);
            Assert.Equal((251L, 400L, 301L, 450L), Extent(s, an));
        }

        private static (long, long, long, long) Box(Sim s, string text, int align, int x, int y)
        {
            var d = s.W.Display;
            var r = TextMeasure.Box(d.Font(d.DefaultFont), text, align, x, y);
            return (r.T, r.L, r.B, r.R);
        }

        [Fact]
        public void TextGivesItsAnTheTextsBoxFromTheBaseline()
        {
            var s = Show();
            int an = (int)C(s, "DspAnNew", 300, 200).I;
            Assert.Equal(0, C(s, "DspText", an, -1, "abc").I);
            var e = Extent(s, an);
            Assert.Equal(Box(s, "abc", 0, 300, 200), e);
            Assert.Equal(300, e.L); // left justified: from the point
            Assert.True(e.T < 200 && e.B > 200); // the point is the baseline
            Assert.True(e.R > 300);
            // right justified: the text ends on the point; centred: C division of the width
            Assert.Equal(0, C(s, "DspText", an, -1, "abc", -1, 1).I);
            Assert.Equal(Box(s, "abc", 1, 300, 200), Extent(s, an));
            Assert.Equal(300, Extent(s, an).R);
            Assert.Equal(0, C(s, "DspText", an, -1, "abc", -1, 2).I);
            Assert.Equal(Box(s, "abc", 2, 300, 200), Extent(s, an));
            // a pixel length cuts the shown text (mode 0)
            Assert.Equal(0, C(s, "DspText", an, -1, "abcdefghij", 20, 0, 0).I);
            Assert.True(Extent(s, an).R - 300 <= 20);
            // empty text: the point (SA's 'empty tab problem')
            Assert.Equal(0, C(s, "DspText", an, -1, "").I);
            Assert.Equal((200L, 300L, 200L, 300L), Extent(s, an));
        }

        [Fact]
        public void ADrawnPageObjectKeepsItsOwnExtent()
        {
            var d = new PageDrawing
            {
                Page = "Main",
                Width = 800,
                Height = 600,
            };
            d.Add(
                new DrawObject
                {
                    An = 5,
                    Type = 0x0F,
                    Rect = new Rect(10, 10, 50, 30),
                    Bounds = new Rect(10, 10, 50, 30),
                    PosX = 10,
                    PosY = 10,
                }
            );
            d.Add(
                new DrawObject
                {
                    An = 6,
                    Type = 0x1009,
                    Rect = new Rect(100, 100, 101, 101),
                    Bounds = new Rect(100, 100, 131, 117),
                    PosX = 100,
                    PosY = 100,
                }
            );
            var s = Show(d);
            Assert.Equal(0, C(s, "DspText", 5, -1, "abc").I);
            // (a drawn shape reports the area it draws: its extent grown by a pixel each side,
            // SA NavigationLink.ci 'DspGetAnWidth returns 2 pixels larger')
            Assert.Equal((9L, 9L, 31L, 51L), Extent(s, 5));
            // a Cicode object follows its output, and returns to its own extent on DspDel
            Assert.Equal(0, C(s, "DspText", 6, -1, "abc").I);
            Assert.Equal(Box(s, "abc", 0, 100, 100), Extent(s, 6));
            Assert.Equal(0, C(s, "DspDel", 6).I);
            Assert.Equal((100L, 100L, 101L, 101L), Extent(s, 6));
        }

        [Fact]
        public void AStaticTextShownTranslatedIsMeasuredFromTheShownString()
        {
            var d = new PageDrawing
            {
                Page = "Main",
                Width = 800,
                Height = 600,
            };
            var lit = new TextFont
            {
                Face = "Arial",
                Points = 12,
                Bold = true,
                AnchorX = 416,
                AnchorY = 62,
            };
            d.Add(
                new DrawObject
                {
                    An = 7,
                    Type = 0x05,
                    Rect = new Rect(416, 47, 535, 66),
                    Bounds = new Rect(416, 47, 535, 66),
                    PosX = 416,
                    PosY = 62,
                    Text = new TextStrings
                    {
                        Off = "@(Current Filter:)",
                        Caption = "@(Current Filter:)",
                        Mode = 0,
                        Font = lit,
                    },
                }
            );
            // a caption without '@(' keeps the rect CtDraw measured
            d.Add(
                new DrawObject
                {
                    An = 8,
                    Type = 0x05,
                    Rect = new Rect(10, 10, 99, 30),
                    Bounds = new Rect(10, 10, 99, 30),
                    PosX = 10,
                    PosY = 25,
                    Text = new TextStrings
                    {
                        Off = "Plain",
                        Caption = "Plain",
                        Mode = 0,
                        Font = new TextFont
                        {
                            Face = "Arial",
                            Points = 12,
                            AnchorX = 10,
                            AnchorY = 25,
                        },
                    },
                }
            );
            var s = Show(d);
            var want = TextMeasure.Box("Arial", 16, true, false, false, false, "Current Filter:", 0, 416, 62);
            Assert.Equal(((long)want.T, (long)want.L, (long)want.B, (long)want.R), Extent(s, 7));
            Assert.True(want.R < 535);
            Assert.Equal((10L, 10L, 30L, 99L), Extent(s, 8));
        }

        [ProjectFact]
        public void UserFullNamesAreShownTranslated()
        {
            // Example's users.dbf: FULL_NAME '@(Systems Engineer)' is a display string
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var users = sim.Invoke(() => sim.UsersJson())["users"];
                Assert.True(users.Items.Count > 0);
                bool any = false;
                foreach (var u in users.Items)
                {
                    Assert.DoesNotContain("@(", u["fullName"].Str);
                    if (u["fullName"].Str == "Systems Engineer")
                        any = true;
                }
                Assert.True(any);
                string name = users.Items[0]["name"].Str;
                var me = sim.Invoke(() =>
                    sim.SetUser(JsonValue.Parse("{\"mode\": \"user\", \"name\": \"" + name + "\"}"))
                );
                Assert.DoesNotContain("@(", me["fullName"].Str);
            }
        }
    }
}
