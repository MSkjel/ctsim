using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>The display built-ins on the synthetic World.</summary>
    [Collection("World")]
    public class DisplayClientTests : DataTestBase
    {
        private sealed class FakeHost : IDisplayHost
        {
            public readonly Dictionary<string, (PageRecord Rec, PageDrawing? Drawing)> Pages = new Dictionary<
                string,
                (PageRecord, PageDrawing?)
            >(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Outputs = new List<string>();

            public PageRecord? FindPage(string name, string cluster) =>
                Pages.TryGetValue(name, out var p) ? p.Rec : null;

            public PageDrawing? LoadDrawing(PageRecord page) =>
                Pages.TryGetValue(page.Name, out var p) ? p.Drawing : null;

            public void Shown(int window, PageInstance page) { }

            public void DisplayOutput(int window, int an, string kind, string text) =>
                Outputs.Add(window + ":" + an + ":" + kind + ":" + text);

            public void WindowChanged(Window w) { }

            public void Add(
                string name,
                string title,
                string next = "",
                string prev = "",
                int areas = 0,
                PageDrawing? d = null
            ) =>
                Pages[name] = (
                    new PageRecord
                    {
                        Name = name,
                        Title = title,
                        NextPage = next,
                        PrevPage = prev,
                        Areas = areas,
                    },
                    d
                );
        }

        private static DrawObject Obj(int an, int type, string name, Rect rect, int parent = 0) =>
            new DrawObject
            {
                An = an,
                Type = type,
                Name = name,
                Rect = rect,
                Bounds = rect,
                Parent = parent,
                PosX = rect.L,
                PosY = rect.T,
            };

        /// <summary>Page Main: group Pump1 (AN 1) holding Base (2) and group Inner (3) with
        /// Background (4); a text Label (5) at the top level; a web content object Web (6).</summary>
        private static PageDrawing MainDrawing()
        {
            var d = new PageDrawing
            {
                Page = "Main",
                Width = 800,
                Height = 600,
            };
            var pump = Obj(1, 0x08, "Pump1", new Rect(100, 100, 200, 200));
            pump.Metadata.Add(new KeyValuePair<string, string>("Equipment", "P1"));
            pump.Metadata.Add(new KeyValuePair<string, string>("Mode", "2"));
            d.Add(pump);
            d.Add(Obj(2, 0x0F, "Base", new Rect(100, 100, 150, 150), 1));
            d.Add(Obj(3, 0x08, "Inner", new Rect(150, 150, 200, 200), 1));
            d.Add(Obj(4, 0x0F, "Background", new Rect(160, 160, 190, 190), 3));
            var label = Obj(5, 0x05, "Label", new Rect(300, 300, 400, 320));
            label.Tip = "configured tip";
            d.Add(label);
            d.Add(Obj(6, 0x13, "Web", new Rect(500, 100, 700, 300)));
            d.Env.Add(new KeyValuePair<string, string>("Area", "North"));
            return d;
        }

        private Sim MakeDisplay(out FakeHost host, Action? tables = null)
        {
            var s = MakeData(tables);
            host = new FakeHost();
            host.Add("Main", "Main Title", next: "Second", d: MainDrawing());
            host.Add(
                "Second",
                "Second Title",
                prev: "Main",
                d: new PageDrawing
                {
                    Page = "Second",
                    Width = 640,
                    Height = 480,
                }
            );
            host.Add("Third", "Third Title");
            host.Add("!Pop", "Popup");
            int area = 1;
            while (area < 255 && s.W.Users.CanView(area))
                area++;
            host.Add("Fenced", "Fenced", areas: area);
            s.W.Display.Host = host;
            return s;
        }

        /// <summary>A built-in called by a task of the World's VM running in a window with an AN.</summary>
        private static Res CallAt(Sim s, int window, int an, string name, params object[] args)
        {
            var b = Func0Table.Default.Lookup(name);
            Assert.True(b != null, name);
            var t = new CicodeTask(s.Run.Vm, "test", false);
            TaskContext.Set(t, window, an);
            var vals = new List<Value>();
            foreach (object a in args)
                vals.Add(a is Value v ? v : CicodeVm.ValueOf(a));
            var r = s.Run.Vm.Builtins!.Call(b!.Index, t, ArgList.Of(vals.ToArray()));
            Assert.False(r.IsNotSimulated, name + " is not simulated");
            return new Res { V = r, Task = t };
        }

        private static Res C(Sim s, string name, params object[] args) => CallAt(s, 0, -1, name, args);

        private static Value R(long v = 0) => Ref(Value.Long(v));

        private static long Hi(Value r) => A.I(Held(r));

        // ------------------------------------------------------------------ windows
        /// <summary>A .ctg file image: the .ctg container with one 'page' block whose head
        /// carries the size (format word <paramref name="word2"/>).</summary>
        private static byte[] CtgImage(int word2, int width, int height)
        {
            var o = new byte[0x10 + 8 * 42 + 0x34];
            BitConverter.GetBytes((ushort)8).CopyTo(o, 0);
            BitConverter.GetBytes((ushort)word2).CopyTo(o, 2);
            BitConverter.GetBytes((uint)8).CopyTo(o, 0x0C);
            int blk = 0x10 + 8 * 42;
            BitConverter.GetBytes((ushort)0x0101).CopyTo(o, 0x10);
            System.Text.Encoding.ASCII.GetBytes("page").CopyTo(o, 0x12);
            BitConverter.GetBytes((uint)blk).CopyTo(o, 0x10 + 2 + 32);
            BitConverter.GetBytes((uint)0x34).CopyTo(o, 0x10 + 6 + 32);
            BitConverter.GetBytes((uint)width).CopyTo(o, blk + 0x14);
            BitConverter.GetBytes((uint)height).CopyTo(o, blk + 0x18);
            return o;
        }

        [Fact]
        public void PageHeadSizeOfTheCurrentAndTheOldForms()
        {
            // the current form, the old form word2 9 (the same head without the GUID) and a
            // form below 9 (the Graphics Builder does not open it: no size)
            string dir = Path.Combine(Path.GetTempPath(), "ctsim_head_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string p = Path.Combine(dir, "now.ctg");
                File.WriteAllBytes(p, CtgImage(0x0C, 800, 600));
                Assert.True(CicodeSim.Builtins.CtgHead.Read(p, out int w, out int h));
                Assert.Equal((800, 600), (w, h));
                p = Path.Combine(dir, "old.ctg");
                File.WriteAllBytes(p, CtgImage(9, 634, 443));
                Assert.True(CicodeSim.Builtins.CtgHead.Read(p, out w, out h));
                Assert.Equal((634, 443), (w, h));
                p = Path.Combine(dir, "older.ctg");
                File.WriteAllBytes(p, CtgImage(4, 640, 460));
                CicodeSim.Builtins.CtgHead.Read(p, out w, out h);
                Assert.Equal((0, 0), (w, h));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void WindowsSelectGotoAndTheTaskWindow()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "WinNumber").I);
            var nw = C(s, "WinNew", "Second");
            Assert.Equal(1, nw.I);
            Assert.Equal(1, TaskContext.Window(nw.Task)); // the new window is the task's
            Assert.Equal("Second", CallAt(s, 1, -1, "PageInfo", 0).S);
            // WinSelect: the previous window, the new one selected; a window not open: unchanged
            var sel = CallAt(s, 1, -1, "WinSelect", 0);
            Assert.Equal(1, sel.I);
            Assert.Equal(0, TaskContext.Window(sel.Task));
            var bad = CallAt(s, 1, -1, "WinSelect", 9);
            Assert.Equal(1, bad.I);
            Assert.Equal(1, TaskContext.Window(bad.Task));
            // WinGoto: focus + the task's window; 880 (and its prompt line message) for a closed window
            Assert.Equal(0, C(s, "WinGoto", 1).I);
            Assert.Equal(1, C(s, "WinGetFocus").I);
            var g = C(s, "WinGoto", 7);
            Assert.Equal(880, g.I);
            Assert.Equal(0, g.Error);
            Assert.Equal("Invalid Window", s.W.Display.Windows[0].Prompt);
            Assert.Equal(1, C(s, "WinNext").I); // the next open window
            Assert.Equal(1, C(s, "WinPrev").I);
        }

        [Fact]
        public void WinNewAtModesParentsAndChildren()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            // mode 1 page child, mode 2 window child of the task's window; 0 none
            Assert.Equal(1, C(s, "WinNewAt", "Second", 10, 20, 1).I);
            Assert.Equal(2, C(s, "WinNewAt", "Third", 0, 0, 2).I);
            Assert.Equal(3, C(s, "WinNewAt", "Third", 0, 0, 0).I);
            Assert.Equal("0", CallAt(s, 1, -1, "PageInfo", 8).S);
            Assert.Equal("1", CallAt(s, 1, -1, "PageInfo", 11).S);
            Assert.Equal("10", CallAt(s, 1, -1, "PageInfo", 14).S);
            Assert.Equal("20", CallAt(s, 1, -1, "PageInfo", 15).S);
            Assert.Equal("-1", CallAt(s, 3, -1, "PageInfo", 8).S);
            Assert.Equal("1", C(s, "PageInfo", 9).S); // first child: creation order
            Assert.Equal("2", CallAt(s, 1, -1, "PageInfo", 10).S); // next child of window 0
            Assert.Equal(1, C(s, "WinGetFirstChild", 0).I);
            Assert.Equal(2, C(s, "WinGetNextChild", 1).I);
            Assert.Equal(-1, C(s, "WinGetNextChild", 2).I);
            Assert.Equal(0, CallAt(s, 2, -1, "WinGetParent", -1, 0).I);
            Assert.Equal(-1, CallAt(s, 3, -1, "WinGetParent").I);
            // 128 = unique: a window showing the page refuses a second one
            Assert.Equal(-1, C(s, "WinNewAt", "Second", 0, 0, 128).I);
            Assert.Equal("Cannot open this window twice", s.W.Display.Windows[0].Prompt);
            Assert.Equal(-1, C(s, "WinNewAt", "Second", 0, 0, -5).I); // a negative mode
            Assert.Equal(-1, C(s, "WinNewAt", "Nope", 0, 0, 0).I);
            Assert.Equal("Cannot Open Window Nope", s.W.Display.Windows[0].Prompt);
        }

        [Fact]
        public void TooManyWindows()
        {
            var s = MakeDisplay(out _);
            int max = s.W.Display.Windows.Length;
            for (int i = 1; i < max; i++)
                Assert.Equal(i, C(s, "WinNew", "Second").I);
            Assert.Equal(-1, C(s, "WinNew", "Second").I);
            Assert.Equal("Too many windows", s.W.Display.Windows[0].Prompt);
        }

        [Fact]
        public void WinFreeRefusesTheLastWindowAndFocusesTheParent()
        {
            var s = MakeDisplay(out _);
            var f = C(s, "WinFree");
            Assert.Equal(320, f.I); // the only window
            Assert.Equal(0, f.Error);
            Assert.Equal("Cannot free Parent window", s.W.Display.Windows[0].Prompt);
            Assert.Equal(1, C(s, "WinNewAt", "Second", 0, 0, 2).I);
            Assert.Equal(320, C(s, "WinFree").I); // window 0 and its child = all
            Assert.Equal(2, C(s, "WinNew", "Third").I);
            var w = CallAt(s, 1, -1, "WinFree");
            Assert.Equal(0, w.I);
            Assert.Equal(0, TaskContext.Window(w.Task)); // the parent gets the focus
            Assert.False(s.W.Display.Windows[1].Open);
            var nw = CallAt(s, 5, -1, "WinFree");
            Assert.Equal(256, nw.I); // not open: raised
            Assert.Equal(256, nw.Error);
            Assert.Equal(0, C(s, "WinFreeEx", 2).I);
            Assert.False(s.W.Display.Windows[2].Open);
        }

        [Fact]
        public void WindowNamesTitlesPositionAndStyle()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(1, C(s, "WinNew", "Second").I);
            Assert.Equal(1, CallAt(s, 1, -1, "WinSetName", "Trend").I);
            Assert.Equal(1, C(s, "WinNumber", "Trend").I);
            Assert.Equal(-1, C(s, "WinNumber", "trend").I); // case-sensitive (std::map)
            Assert.Equal("Trend", C(s, "WinGetName", 1).S);
            Assert.Equal("Second Title", CallAt(s, 1, -1, "GetWinTitle").S);
            Assert.Equal(0, CallAt(s, 1, -1, "_WinTitle", "Hello").I);
            Assert.Equal("Hello", CallAt(s, 1, -1, "GetWinTitle").S);
            Assert.Equal(0, CallAt(s, 1, -1, "WinMove", 5, 6, 300, 200).I);
            Assert.Equal("5", CallAt(s, 1, -1, "PageInfo", 14).S);
            Assert.Equal(0, CallAt(s, 1, -1, "WinPos", 7, 8).I);
            Assert.Equal("8", CallAt(s, 1, -1, "PageInfo", 15).S);
            Assert.Equal(0, CallAt(s, 1, -1, "WinStyle", 1, 1).I);
            Assert.Equal("16384", CallAt(s, 1, -1, "PageInfo", 11).S);
            Assert.Equal(274, CallAt(s, 1, -1, "WinStyle", 9, 1).I);
            Assert.Equal(274, CallAt(s, 1, -1, "WinMode", 12).I);
            Assert.Equal(0x10001, CallAt(s, 1, -1, "WinGetWndHnd").I);
            Assert.Equal(0x10001, C(s, "WndFind", "Hello").I);
            Assert.Equal(0, C(s, "WndFind", "hello").I);
            var mv = CallAt(s, 7, -1, "WinMove", 1, 1, 1, 1);
            Assert.Equal(256, mv.I);
            Assert.Equal(256, mv.Error);
            // freeing the window drops its name
            Assert.Equal(0, CallAt(s, 1, -1, "WinFree").I);
            Assert.Equal(-1, C(s, "WinNumber", "Trend").I);
        }

        [Fact]
        public void ScreenMetrics()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(1920, C(s, "WndInfo", 0).I);
            Assert.Equal(1080, C(s, "WndInfo", 1).I);
            Assert.Equal(1, C(s, "WndInfo", 80).I);
            Assert.Equal(1920, C(s, "WndInfo", 78).I);
            var bad = C(s, "WndInfo", 98);
            Assert.Equal(0, bad.I);
            Assert.Equal(274, bad.Error);
            Assert.Equal(274, C(s, "WndInfo", -1).Error); // unsigned: above 97
            Assert.Equal(1920, C(s, "WndMonitorInfo", 1, 2).I);
            Assert.Equal(1040, C(s, "WndMonitorInfo", 1, 7).I);
            Assert.Equal(1080, C(s, "WndMonitorInfo", 0, 3).I); // the virtual screen
            Assert.Equal(-1, C(s, "WndMonitorInfo", 2, 0).I);
            Assert.Equal(-1, C(s, "WndMonitorInfo", 1, 8).I);
            Assert.Equal(1920, C(s, "WndMonitorInfoEx", "\\\\.\\DISPLAY1", 2).I);
            Assert.Equal(-1, C(s, "WndMonitorInfoEx", "nope", 2).I);
        }

        // ------------------------------------------------------------------ pages
        [Fact]
        public void PageDisplayHistoryAndNavigation()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal("Main", C(s, "PageInfo", 0).S);
            Assert.Equal("Main Title", C(s, "PageInfo", 2).S);
            Assert.Equal("Second", C(s, "PageInfo", 5).S);
            Assert.Equal(0, C(s, "PageNext").I); // NEXT_PAGE Second
            Assert.Equal("Second", C(s, "PageInfo", 0).S);
            Assert.Equal(0, C(s, "_PageDisplay", "Third").I);
            Assert.Equal(0, C(s, "PageNext").I); // empty NEXT_PAGE: nothing
            Assert.Equal("Third", C(s, "PageInfo", 0).S);
            Assert.Equal(2, C(s, "PagePeekCurrent").I);
            Assert.Equal("Second", C(s, "PagePeekLast", 1, 0).S);
            Assert.Equal("Second Title", C(s, "PagePeekLast", 1, 1).S);
            Assert.Equal(274, C(s, "PagePeekLast", 9, 0).Error);
            Assert.Equal(274, C(s, "PagePeekLast", 0, 4).Error);
            Assert.Equal(0, C(s, "PageLast").I); // back one, no entry dropped
            Assert.Equal("Second", C(s, "PageInfo", 0).S);
            Assert.Equal(0, C(s, "PageBack").I);
            Assert.Equal("Main", C(s, "PageInfo", 0).S);
            Assert.Equal(274, C(s, "PageBack", 1).I); // nothing earlier
            Assert.Equal(274, C(s, "PageLast").I);
            Assert.Equal(0, C(s, "PageForward", 2).I);
            Assert.Equal("Third", C(s, "PageInfo", 0).S);
            Assert.Equal(274, C(s, "PageForward", 0).I);
            Assert.Equal(0, C(s, "PageRecall", 0).I);
            Assert.Equal("Main", C(s, "PageInfo", 0).S);
            Assert.Equal(274, C(s, "PageRecall", 0).I); // the current page
            // a new page drops the forward entries
            Assert.Equal(0, C(s, "_PageDisplay", "!Pop").I);
            Assert.Equal(" Pop", C(s, "PageInfo", 0).S); // a leading '!' as a space
            Assert.Equal(1, C(s, "PagePeekCurrent").I);
            Assert.Equal(274, C(s, "PageForward", 1).I);
            // _PageGoto replaces the current entry
            Assert.Equal(0, C(s, "_PageGoto", "Third").I);
            Assert.Equal(1, C(s, "PagePeekCurrent").I);
            Assert.Equal("Third", C(s, "PagePeekLast", 0, 0).S);
            // PagePushLast / PagePopLast work on the entry before the current page
            Assert.Equal(0, C(s, "PagePushLast", "Second").I);
            Assert.Equal("Second", C(s, "PagePopLast").S);
            Assert.Equal("Main", C(s, "PagePopLast").S);
            var pop = C(s, "PagePopLast");
            Assert.Equal("", pop.S);
            Assert.Equal(269, pop.Error);
            Assert.Equal(274, C(s, "PageHome").I); // no [Page]HomePage
        }

        [Fact]
        public void PageChangeErrorsAndPrompts()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(274, C(s, "_PageDisplay", "").I);
            Assert.Equal("Cannot Display Page", s.W.Display.Windows[0].Prompt);
            Assert.Equal(261, C(s, "_PageDisplay", "Nope").I);
            Assert.Equal("Cannot Display Page", s.W.Display.Windows[0].Prompt); // the runtime's text
            Assert.Contains(
                "prompt: Cannot Display Page (page 'Nope': no compiled page of that name in the project or its include chain, error 261)",
                s.Log
            );
            Assert.Equal(277, C(s, "_PageDisplay", "Fenced").I);
            Assert.Equal("Page is out of your Area", s.W.Display.Windows[0].Prompt);
            Assert.Equal(261, C(s, "_PageGoto", "Nope").I);
            Assert.Equal("Invalid Page Nope", s.W.Display.Windows[0].Prompt);
            var nw = CallAt(s, 4, -1, "_PageDisplay", "Main");
            Assert.Equal(256, nw.I); // a window that is not open
            Assert.Equal(256, nw.Error);
            Assert.Equal(1, C(s, "PageExists", "Main").I);
            Assert.Equal(0, C(s, "PageExists", "Nope").I);
            var pi = C(s, "PageInfo", 0); // no page shown yet
            Assert.Equal(256, pi.Error);
        }

        [Fact]
        public void PageVariablesAndTheListGoWithThePage()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(0, C(s, "PageSetInt", "x", 5).I);
            Assert.Equal(5, C(s, "PageGetInt", "x").I);
            // a locked page variable (the simulator's user locked it) keeps its value
            var w0 = s.W.Display.Windows[0];
            w0.LockedVars.Add(CicodeSim.World.Display.Window.VarKey("x", false));
            Assert.Equal(0, C(s, "PageSetInt", "x", 9).I);
            Assert.Equal(5, C(s, "PageGetInt", "x").I);
            Assert.Contains("\"locked\": true", w0.VarsJson().ToString().Replace("\":true", "\": true"));
            w0.LockedVars.Clear();
            Assert.Equal(0, C(s, "PageSetInt", "x", 9).I);
            Assert.Equal(9, C(s, "PageGetInt", "x").I);
            Assert.Equal(0, C(s, "_PageDisplay", "Second").I);
            Assert.Equal(0, C(s, "PageGetInt", "x").I); // cleared with the page
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(2, C(s, "PageListCount").I); // each page once
            Assert.Equal(0, C(s, "PageListCurrent").I);
            Assert.Equal("Second", C(s, "PageListInfo", 1, 0).S);
            Assert.Equal(274, C(s, "PageListInfo", 5, 0).Error);
            Assert.Equal(0, C(s, "PageListDisplay", 1).I);
            Assert.Equal("Second", C(s, "PageInfo", 0).S);
            Assert.Equal(1, C(s, "PageListCurrent").I);
            Assert.Equal(0, C(s, "PageListDelete", 1).I); // the shown page: the previous one shows
            Assert.Equal("Main", C(s, "PageInfo", 0).S);
            Assert.Equal(274, C(s, "PageListDelete", 0).I); // one page left
            Assert.Equal(800, C(s, "PageFileInfo", "Main", 0).I);
            Assert.Equal(480, C(s, "PageFileInfo", "Second", 1).I);
            var nf = C(s, "PageFileInfo", "Nope", 0);
            Assert.Equal(274, nf.Error);
            Assert.Equal("640", C(s, "PageFileInfoEx", "Second", 0).S);
            var tr = C(s, "PageTransformCoords", 0, 10, 20, R(), R(), 2);
            Assert.Equal(0, tr.I);
        }

        // ------------------------------------------------------------------ animation points
        [Fact]
        public void NamesAndTheRelativePathRules()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(1, C(s, "DspGetAnFromName", "Pump1").I);
            Assert.Equal(4, C(s, "DspGetAnFromName", "Pump1.Inner.Background").I);
            Assert.Equal(4, C(s, "DspGetAnFromName", "pump1.inner.background").I); // without case (guess)
            Assert.Equal(-1, C(s, "DspGetAnFromName", "Pump1.Base.X").I); // Base is no group
            Assert.Equal(-1, C(s, "DspGetAnFromName", "Inner").I); // not at the top level
            Assert.Equal(-1, C(s, "DspGetAnFromName", "").I);
            // relative to the task's AN: a leaf starts at its group
            Assert.Equal(4, CallAt(s, 0, 2, "DspGetAnFromName", ".Inner.Background").I);
            Assert.Equal(-1, CallAt(s, 0, 2, "DspGetAnFromName", "..\\Label").I);
            Assert.Equal(5, CallAt(s, 0, 2, "DspGetAnFromName", "..\\..\\Label").I);
            Assert.Equal(5, CallAt(s, 0, 4, "DspGetAnFromName", "..\\..\\..\\Label").I);
            Assert.Equal(-1, CallAt(s, 0, 4, "DspGetAnFromName", "..\\..\\..\\..\\Label").I); // past the root
            // a group starts at itself
            Assert.Equal(4, C(s, "DspGetAnFromNameRelative", 3, ".Background").I);
            Assert.Equal(2, C(s, "DspGetAnFromNameRelative", 3, "..\\Base").I);
            Assert.Equal(-1, C(s, "DspGetAnFromNameRelative", -1, ".Base").I); // no task AN, no cursor
            Assert.Equal("Background", C(s, "DspGetNameFromAn", 4).S);
            Assert.Equal("", C(s, "DspGetNameFromAn", 0).S);
            Assert.Equal(3, C(s, "DspGetParentAn", 4).I);
            Assert.Equal(0, C(s, "DspGetParentAn", 1).I);
            var gp = C(s, "DspGetParentAn", 99);
            Assert.Equal(0, gp.I);
            Assert.Equal(274, gp.Error);
            Assert.Equal(274, C(s, "DspAnSetName", 2, "9abc").I);
            Assert.Equal(274, C(s, "DspAnSetName", 2, "a.b").I);
            Assert.Equal(269, C(s, "DspAnSetName", 2, "inner").I); // a sibling's name
            Assert.Equal(0, C(s, "DspAnSetName", 2, "Base2").I);
            Assert.Equal(2, C(s, "DspGetAnFromName", "Pump1.Base2").I);
            Assert.Equal(1, C(s, "DspGetAnFirst").I);
            Assert.Equal(2, C(s, "DspGetAnNext", 1).I);
            Assert.Equal(-1, C(s, "DspGetAnNext", 6).I);
        }

        [Fact]
        public void AnInfoGeometryAndPoints()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal("17", C(s, "DspAnInfo", 1, 3).S); // group
            Assert.Equal("3", C(s, "DspAnInfo", 5, 3).S); // text
            Assert.Equal("30", C(s, "DspAnInfo", 6, 3).S); // web content
            Assert.Equal("3", C(s, "DspAnInfo", 5, 0).S);
            Assert.Equal("", C(s, "DspAnInfo", 77, 3).S);
            var bad = C(s, "DspAnInfo", 32000, 3);
            Assert.Equal(274, bad.Error);
            Assert.Equal("3", CallAt(s, 0, 5, "DspAnInfo", -2, 3).S); // -2 = the task's AN
            var t = R();
            var l = R();
            var b = R();
            var r = R();
            Assert.Equal(0, C(s, "DspGetAnExtent", 5, t, l, b, r).I);
            Assert.Equal((300, 300, 320, 400), (Hi(t), Hi(l), Hi(b), Hi(r))); // Top, Left, Bottom, Right
            Assert.Equal(0, C(s, "DspGetAnExtent", 77, t, l, b, r).I); // unknown: all 0, 0
            Assert.Equal((0L, 0L, 0L, 0L), (Hi(t), Hi(l), Hi(b), Hi(r)));
            var ex = C(s, "DspGetAnExtent", 0, t, l, b, r);
            Assert.Equal(274, ex.I);
            Assert.Equal(274, ex.Error);
            Assert.Equal(0, C(s, "DspGetAnRawExtent", 4, t, l, b, r).I);
            Assert.Equal((160, 160, 190, 190), (Hi(t), Hi(l), Hi(b), Hi(r)));
            var x = R();
            var y = R();
            Assert.Equal(0, C(s, "DspAnGetPos", 5, x, y).I);
            Assert.Equal((300L, 300L), (Hi(x), Hi(y)));
            Assert.Equal(274, C(s, "DspAnGetPos", 5, x, y, 2).I);
            Assert.Equal(274, C(s, "DspAnGetPos", 77, x, y).I);
            Assert.Equal(5, C(s, "DspGetNearestAn", 305, 305).I);
            Assert.Equal(1, C(s, "DspGetAnFromPoint", 110, 110).I);
            Assert.Equal(2, C(s, "DspGetAnFromPoint", 110, 110, 1).I);
            Assert.Equal(0, C(s, "DspGetAnFromPoint", 110, 110, 2).I);
            Assert.Equal(1, C(s, "DspAnInRgn", 2, 1, 4).I); // (100,100) in (100,100)-(160,160)
            Assert.Equal(0, C(s, "DspAnInRgn", 5, 1, 4).I);
            // DspAnMove moves the position (and what Cicode drew there)
            Assert.Equal(0, C(s, "DspAnMove", 5, 350, 310).I);
            Assert.Equal(0, C(s, "DspAnGetPos", 5, x, y).I);
            Assert.Equal((350L, 310L), (Hi(x), Hi(y)));
            Assert.Equal(0, C(s, "DspAnGetPos", 5, x, y, 1).I); // the configured position stays
            Assert.Equal((300L, 300L), (Hi(x), Hi(y)));
            // dynamic ANs
            Assert.Equal(31999, C(s, "DspAnNew", 10, 10).I);
            Assert.Equal(31999, C(s, "DspAnNew", 10, 10).I); // one at the point already
            Assert.Equal(31998, C(s, "DspAnNewRel", 31999, 5, 0).I);
            Assert.Equal(0, C(s, "DspAnFree", 31999).I);
            Assert.Null(s.W.Display.Windows[0].Page!.Get(31999));
            Assert.Equal(1, C(s, "DspIsVisible", 5).I);
            Assert.Equal(0, C(s, "DspShow", 5, 0).I);
            Assert.Equal(0, C(s, "DspIsVisible", 5).I);
        }

        [Fact]
        public void MetadataSetOnlyChangesAnExistingEntry()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(0, C(s, "DspAnSetMetadata", 1, "mode", "3").I); // the name without case
            Assert.Equal("3", C(s, "DspAnGetMetadata", 1, "Mode").S);
            var nw = C(s, "DspAnSetMetadata", 1, "New", "x");
            Assert.Equal(289, nw.I);
            Assert.Equal(289, nw.Error);
            Assert.Equal(289, C(s, "DspAnGetMetadata", 1, "New").Error); // never added
            Assert.Equal(269, C(s, "DspAnSetMetadata", 99, "Mode", "x").I);
            Assert.Equal(303, C(s, "DspAnSetMetadata", 0, "Mode", "x").I);
            Assert.Equal("P1", C(s, "DspGetMetadataFromName", "Pump1", "Equipment").S);
            var nf = C(s, "DspGetMetadataFromName", "Nope", "Equipment");
            Assert.Equal("", nf.S);
            Assert.Equal(274, nf.Error); // the AN -1
            Assert.Equal(
                "P1",
                CallAt(s, 0, 2, "DspGetMetadataFromNameRelative", -1, "..\\..\\Pump1", "Equipment").S
            );
            Assert.Equal(0, C(s, "DspSetMetadataFromName", "Pump1", "Equipment", "P2").I);
            Assert.Equal("P2", C(s, "DspAnGetMetadata", 1, "Equipment").S);
            Assert.Equal(303, C(s, "DspSetMetadataFromName", "Nope", "Equipment", "P2").I);
            Assert.Equal("Mode", C(s, "DspAnGetMetadataAt", 1, 1, "Name").S);
            Assert.Equal("3", C(s, "DspAnGetMetadataAt", 1, 1, "value").S);
            Assert.Equal(257, C(s, "DspAnGetMetadataAt", 1, 5, "Name").Error);
            Assert.Equal(274, C(s, "DspAnGetMetadataAt", 1, 0, "Other").Error);
            Assert.Equal(0, C(s, "DspAnSetMetadataAt", 1, 0, "Value", "P9").I);
            Assert.Equal("P9", C(s, "DspAnGetMetadata", 1, "Equipment").S);
            Assert.Equal("North", C(s, "DspGetEnv", "area").S); // _stricmp
            Assert.Equal("", C(s, "DspGetEnv", "nope").S);
        }

        [Fact]
        public void MouseTipsAndCursor()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            var x = R(7);
            var y = R(7);
            Assert.Equal(256, C(s, "DspGetMouse", x, y).I); // the pointer is not in the window
            Assert.Equal((-1L, -1L), (Hi(x), Hi(y)));
            s.W.Display.Windows[0].MouseX = 120;
            s.W.Display.Windows[0].MouseY = 130;
            Assert.Equal(0, C(s, "DspGetMouse", x, y).I);
            Assert.Equal((120L, 130L), (Hi(x), Hi(y)));
            Assert.Equal(1, C(s, "DspGetMouseOver", 2).I);
            Assert.Equal("configured tip", C(s, "DspGetTip", 5, 0).S);
            Assert.Equal(0, C(s, "DspSetTip", 5, "line1\nline2").I);
            Assert.Equal("line1", C(s, "DspGetTip", 5, 1).S);
            Assert.Equal(274, C(s, "DspSetTip", -1, "x").I);
            Assert.Equal(0, C(s, "DspSetTip", -1, "x").Error); // returned, not raised
            Assert.Equal(274, C(s, "DspSetTip", 0, "x").Error);
            Assert.Equal(1, C(s, "DspTipMode", 0).I); // the previous mode
            Assert.Equal(0, C(s, "DspTipMode", 3).I);
            Assert.Equal(274, C(s, "DspTipMode", 4).Error);
            // the command cursor: only an AN with a keyboard command or KeyAllowCursor
            Assert.Equal(1, C(s, "KeySetCursor", 5).I);
            Assert.Equal(0, C(s, "KeyAllowCursor", 5, 1).I);
            Assert.Equal(0, C(s, "KeySetCursor", 5).I);
            Assert.Equal(5, C(s, "KeyGetCursor").I);
            Assert.Equal(274, C(s, "KeySetCursor", 32001).I);
            Assert.Equal(274, C(s, "KeyMove", 9).I);
            Assert.Equal(0, C(s, "KeyAllowCursor", 2, 1).I);
            Assert.Equal(0, C(s, "KeyMove", 1).I); // left: AN 2 at x 100
            Assert.Equal(2, C(s, "KeyGetCursor").I);
        }

        [Fact]
        public void KeyCommandLine()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(0, C(s, "KeyPut", 65).I);
            Assert.Equal(0, C(s, "KeyPutStr", "bc").I); // letters upper-cased
            Assert.Equal('C', C(s, "KeyPeek", 0).I);
            Assert.Equal('A', C(s, "KeyPeek", 2).I);
            Assert.Equal(0, C(s, "KeyPeek", 3).I);
            Assert.Equal('C', C(s, "KeyGet").I);
            Assert.Equal('B', C(s, "KeyBS").I);
            Assert.Equal('A', C(s, "KeyGet").I);
            Assert.Equal(0, C(s, "KeyGet").I);
            Assert.Equal(256, CallAt(s, 9, -1, "KeyPut", 65).I);
        }

        [Fact]
        public void AssociationsChainPropertiesAndReplace()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "Ass", -2, "Equip", "'Lit'", 0).I);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal("Lit", C(s, "AssGetProperty", "Equip", "TagName").S);
            Assert.Equal("1", C(s, "AssGetProperty", "Equip", "literal").S);
            Assert.Equal("", C(s, "AssGetProperty", "Equip", "Units").S);
            var mi = C(s, "AssGetProperty", "Missing", "TagName");
            Assert.Equal(274, mi.Error);
            Assert.Equal(1, C(s, "AssChain", -2, 0, 0).I); // the window's one association
            Assert.Contains(s.W.Display.Pending, p => p.Target == -2 && p.Arg == "Equip" && p.Literal);
            Assert.Equal(274, C(s, "AssWinReplace", 3).I);
            Assert.Equal(0, C(s, "Ass", 0, "X", "'v'", 0).I);
            Assert.Equal(0, C(s, "AssWinReplace", 0).I);
            Assert.Equal("v", C(s, "AssGetProperty", "X", "TagName").S);
            Assert.Equal(274, C(s, "AssScaleStr", "X", 50, 0).Error); // a literal has no scale
        }

        [Fact]
        public void ArraysByAnAreFreedWithThePage()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            long h = C(s, "ArrayCreateByAn", 5, 3, 1, 1).I;
            Assert.True(h >= 0);
            Assert.Equal(1, C(s, "ArrayExistsByAn", 5).I);
            Assert.Equal(h, C(s, "ArrayGetArrayByAn", 5).I);
            Assert.Equal(0, C(s, "ArraySetStringByAn", 5, "a", 0).I);
            Assert.Equal("a", C(s, "ArrayGetStringByAn", 5, 0).S);
            Assert.Equal(0, C(s, "ArraySetIntByAn", 5, 42, 1).I);
            Assert.Equal(42, C(s, "ArrayGetIntByAn", 5, 1).I);
            Assert.NotEqual("", C(s, "ArrayGetMapNameByAn", 5).S);
            var nm = C(s, "ArrayGetMapNameByAn", 6);
            Assert.Equal(269, nm.Error);
            Assert.Equal(269, C(s, "ArraySetStringByAn", 6, "a", 0).Error); // the -1 handle
            Assert.Equal(0, C(s, "ArrayDestroyByAn", 5).I);
            Assert.Equal(0, C(s, "ArrayExistsByAn", 5).I);
            Assert.True(C(s, "ArrayCreateByAn", 5, 3, 1, 1).I >= 0);
            Assert.Equal(0, C(s, "_PageDisplay", "Second").I);
            Assert.Equal(0, C(s, "ArrayExistsByAn", 5).I); // freed with the page
        }

        // ------------------------------------------------------------------ display output
        [Fact]
        public void DisplayOutputIsRecordedPerWindow()
        {
            var s = MakeDisplay(out var host, () =>
                Dbf(
                    Path.Combine(MainDir, "fonts.dbf"),
                    new[] { "NAME", "TYPE", "SIZE", "FGND_COLR", "BGND_COLR" },
                    new[] { "Big", "Arial", "16", "0", "-1" }
                )
            );
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(0, C(s, "DspFontHnd", "big").I); // fonts.dbf, without case
            Assert.Equal(-1, C(s, "DspFontHnd", "Nope").I);
            int f = (int)C(s, "DspFont", "Segoe UI", -12, 0, 0xffffff).I;
            Assert.Equal(1, f);
            Assert.Equal(f, C(s, "DspFont", "Segoe UI", -12, 0, 0xffffff).I); // an equal font
            var w = s.W.Display.Windows[0];
            Assert.Equal(0, C(s, "DspStr", 5, "Big", "hello").I);
            var d = Assert.Single(w.Drawn);
            Assert.Equal(("text", "hello", 0, 300, 300), (d.Kind, d.Text, d.Font, d.X, d.Y));
            Assert.Contains("0:5:text:hello", host.Outputs);
            // DspText: a pixel length (modes 0 / 1) keeps the full text for the viewer to cut;
            // it is wider than 3 pixels, so bTooltip makes the full text the tip
            Assert.Equal(0, C(s, "DspText", 5, -1, "abcdef", 3, 0, 1, 1).I);
            Assert.Equal("abcdef", Assert.Single(w.Drawn).Text);
            Assert.Equal("abcdef", C(s, "DspGetTip", 5, 0).S);
            // mode 2: the length is characters, cut here
            Assert.Equal(0, C(s, "DspText", 5, -1, "abcdef", 3, 0, 2, 1).I);
            Assert.Equal("abc", Assert.Single(w.Drawn).Text);
            Assert.Equal("abcdef", C(s, "DspGetTip", 5, 0).S);
            var no = C(s, "DspText", 77, -1, "x");
            Assert.Equal(369, no.I);
            Assert.Equal(369, no.Error);
            Assert.Equal(274, C(s, "DspText", 5, 300, "x").Error);
            Assert.Equal(274, C(s, "DspSym", 5, "lib.sym", 0, 6).Error);
            Assert.Equal(0, CallAt(s, 0, 2, "DspSym", -2, "lib.sym").I); // -2 = the task's AN
            Assert.Equal(2, w.Drawn.Count);
            Assert.Equal("sym", w.DrawnAt(2)!.Kind);
            Assert.Equal(0, C(s, "DspBar", 2, "bars.b", 50).I);
            Assert.Equal("bar", w.DrawnAt(2)!.Kind); // the last output of the AN
            Assert.Equal(0, C(s, "DspDel", 5).I);
            Assert.Null(w.DrawnAt(5));
            Assert.Equal(0, C(s, "DspCol", 5, 2).I);
            Assert.Equal(0, C(s, "DspWebContentSetURL", 6, "http://x").I);
            Assert.Equal("http://x", C(s, "DspWebContentGetURL", 6).S);
            Assert.Equal(303, C(s, "DspWebContentSetURL", 5, "http://x").I);
            // the page change takes what Cicode drew
            Assert.Equal(0, C(s, "_PageDisplay", "Second").I);
            Assert.Empty(w.Drawn);
        }

        [Fact]
        public void RubberBandAndButtonCursorHelpers()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(0, C(s, "DspRubStart", 10, 10, 0).I);
            Assert.Equal(0, C(s, "DspRubMove", 30, 40).I);
            var x = R();
            var y = R();
            var cx = R();
            var cy = R();
            Assert.Equal(0, C(s, "DspRubEnd", x, y, cx, cy).I);
            Assert.Equal((10L, 10L, 30L, 40L), (Hi(x), Hi(y), Hi(cx), Hi(cy))); // mode 0: cx, cy absolute
            Assert.Equal(274, C(s, "DspRubEnd", x, y, cx, cy).I);
            Assert.Equal(274, C(s, "DspRubMove", 1, 1).I);
            // bit 2 orders the corners, then bit 1 makes cx, cy relative
            foreach (
                var (mode, want) in new[]
                {
                    (1, (30L, 40L, -20L, -30L)),
                    (2, (10L, 10L, 30L, 40L)),
                    (3, (10L, 10L, 20L, 30L)),
                }
            )
            {
                Assert.Equal(0, C(s, "DspRubStart", 30, 40, mode).I);
                Assert.Equal(0, C(s, "DspRubMove", 10, 10).I);
                Assert.Equal(0, C(s, "DspRubEnd", x, y, cx, cy).I);
                Assert.Equal(want, (Hi(x), Hi(y), Hi(cx), Hi(cy)));
            }
            // Include zoom.ci: mode 6 inside the clip rectangle (kept ordered, it stays after the end)
            Assert.Equal(0, C(s, "DspRubSetClip", 25, 25, 0, 0).I);
            Assert.Equal(0, C(s, "DspRubStart", 10, 10, 6).I);
            Assert.Equal(0, C(s, "DspRubMove", 30, 40).I);
            Assert.Equal(0, C(s, "DspRubEnd", x, y, cx, cy).I);
            Assert.Equal((10L, 10L, 25L, 25L), (Hi(x), Hi(y), Hi(cx), Hi(cy)));
            Assert.Equal(0, C(s, "DspRubStart", 30, 30, 6).I);
            Assert.Equal(0, C(s, "DspRubEnd", x, y, cx, cy).I);
            Assert.Equal((25L, 25L, 25L, 25L), (Hi(x), Hi(y), Hi(cx), Hi(cy)));
            Assert.Equal(0, C(s, "DspRubStart", 30, 30, 0).I); // no bit 4: not clipped
            Assert.Equal(0, C(s, "DspRubEnd", x, y, cx, cy).I);
            Assert.Equal((30L, 30L, 30L, 30L), (Hi(x), Hi(y), Hi(cx), Hi(cy)));
            // the XP button helpers: the pressed AN of the window, object ids mapped to ANs
            Assert.Equal(0, C(s, "KeyAllowCursor", 5, 1).I);
            Assert.Equal(0, C(s, "KeySetCursor", 5).I);
            Assert.Equal(1, C(s, "_DspCursor_MouseDown", 1).I);
            Assert.Equal(0, CallAt(s, 0, 5, "_DspCursor_MouseUp", 0, 3, 1).I); // maps id 3 to AN 5
            Assert.Equal(1, C(s, "_DspObjectGetCursorDown", 3).I);
            Assert.Equal(0, C(s, "_DspObjectGetCursorUp", 3).I);
            Assert.Equal(1, C(s, "_DspObjectGetCursor", 3).I);
            Assert.Equal(1, C(s, "_DspCursor_MouseUp", 1, -1, 1).I); // the press ends
            Assert.Equal(1, C(s, "_DspObjectGetCursorUp", 3).I);
            Assert.Equal(0, C(s, "PageSetInt", "_tabalm_AN7_ID", 12).I);
            Assert.Equal(12, C(s, "__TabAlarm_GetIDByAN", 7).I);
            Assert.Equal(-1, C(s, "__TabAlarm_GetIDByAN", 0).I);
            Assert.Equal(-1, C(s, "DspInfoNew", 5).I); // no compiled expressions
        }

        [Fact]
        public void ANewTaskRunsInItsCreatorsWindow()
        {
            var s = MakeData(build: pb =>
            {
                pb.Function("Child", "", Types.Long).Begin().Func("WinNumber", 0).RetValue().End();
            });
            var host = new FakeHost();
            host.Add("Second", "Second Title");
            s.W.Display.Host = host;
            Assert.Equal(1, C(s, "WinNew", "Second").I);
            var r = CallAt(s, 1, -1, "TaskNew", "Child", "", 0);
            Assert.True(r.I > 0);
            var child = s.Run.Scheduler.Tasks[(int)r.I];
            Assert.Equal(1, TaskContext.Window(child));
        }

        // ------------------------------------------------------------------ edge cases
        [Fact]
        public void ArraysByAnAreBoundInTheArrayRecord()
        {
            // the binding is the array's own (AN / window): ArrayDestroy
            // of the handle ends it, a slot reused by another array is not the AN's, and an array
            // another package binds (an alarm list's) is found by every ...ByAn function
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            long h = C(s, "ArrayCreateByAn", 5, 3, 1, 1).I;
            Assert.Equal(0, C(s, "ArrayDestroy", h).I);
            Assert.Equal(0, C(s, "ArrayExistsByAn", 5).I);
            Assert.Equal(h, C(s, "ArrayCreate", "Plain", 2, 1, 1).I); // the freed slot again
            Assert.Equal(-1, C(s, "ArrayGetArrayByAn", 5).I);
            Assert.Equal(0, C(s, "ArrayDestroy", h).I);
            // the same AN in two windows: two arrays
            Assert.Equal(1, C(s, "WinNew", "Main").I);
            long h0 = C(s, "ArrayCreateByAn", 5, 2, 1, 1).I;
            long h1 = CallAt(s, 1, -1, "ArrayCreateByAn", 5, 2, 1, 1).I;
            Assert.NotEqual(h0, h1);
            Assert.Equal(h0, C(s, "ArrayGetArrayByAn", 5).I);
            Assert.Equal(h1, CallAt(s, 1, -1, "ArrayGetArrayByAn", 5).I);
            // an array bound by another package (AlarmListCreate) through DisplayAns.BindArray
            var t = new CicodeTask(s.Run.Vm, "bind", false);
            long hl = C(s, "ArrayCreate", "List", 4, 1, 1).I;
            DisplayAns.BindArray(s.Host, t, (int)hl, 6, 0);
            Assert.Equal(hl, C(s, "ArrayGetArrayByAn", 6).I);
            Assert.Equal(1, C(s, "ArrayExistsByAn", 6).I);
            Assert.Equal(-1, CallAt(s, 1, -1, "ArrayGetArrayByAn", 6).I); // window 1 has none
            // window 0's page change frees window 0's arrays only
            Assert.Equal(0, C(s, "_PageDisplay", "Second").I);
            Assert.Equal(0, C(s, "ArrayExistsByAn", 5).I);
            Assert.Equal(0, C(s, "ArrayExistsByAn", 6).I);
            Assert.Equal(h1, CallAt(s, 1, -1, "ArrayGetArrayByAn", 5).I);
        }

        [Fact]
        public void WinSetNameIsAMapInsert()
        {
            // the runtime inserts into a std::map: a name in use keeps its window (0), a window
            // keeps every name it was given, WinGetName gives the first in name order
            var s = MakeDisplay(out _);
            Assert.Equal(1, C(s, "WinNew", "Second").I);
            Assert.Equal(1, CallAt(s, 1, -1, "WinSetName", "Trend").I);
            Assert.Equal(0, C(s, "WinSetName", "Trend").I); // in use: refused
            Assert.Equal(1, C(s, "WinNumber", "Trend").I);
            Assert.Equal(0, CallAt(s, 1, -1, "WinSetName", "Trend").I); // itself: in use too
            Assert.Equal(1, CallAt(s, 1, -1, "WinSetName", "Alarms").I);
            Assert.Equal(1, C(s, "WinNumber", "Trend").I); // the old name stays
            Assert.Equal(1, C(s, "WinNumber", "Alarms").I);
            Assert.Equal("Alarms", C(s, "WinGetName", 1).S); // name order
            Assert.Equal(1, C(s, "WinSetName", "Main").I);
            Assert.Equal("Main", C(s, "WinGetName", 0).S);
            Assert.Equal(0, C(s, "WinSetName", "X", 9).I); // not open
            Assert.Equal(0, CallAt(s, 1, -1, "WinFree").I);
            Assert.Equal(-1, C(s, "WinNumber", "Trend").I); // both go with the window
            Assert.Equal(-1, C(s, "WinNumber", "Alarms").I);
            Assert.Equal(0, C(s, "WinNumber", "Main").I);
        }

        [Fact]
        public void RaiseSitesReadInTheMachineCode()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            // DspAnGetMetadataAt: the two early exits jump to
            var m = C(s, "DspAnGetMetadataAt", 0, 0, "Name");
            Assert.Equal("", m.S);
            Assert.Equal(303, m.Error);
            Assert.Equal(303, CallAt(s, 9, -1, "DspAnGetMetadataAt", 1, 0, "Name").Error);
            Assert.Equal(269, C(s, "DspAnGetMetadataAt", 99, 0, "Name").Error);
            // DspShow: only a change of state is made; the same state gives 274, not raised
            var sh = C(s, "DspShow", 5, 1);
            Assert.Equal(274, sh.I);
            Assert.Equal(0, sh.Error);
            Assert.Equal(0, C(s, "DspShow", 5, 0).I);
            Assert.Equal(274, C(s, "DspShow", 5, 0).I);
            Assert.Equal(0, C(s, "DspShow", 5, 1).I);
            // DspFlushObj: both paths raise their result
            var f0 = C(s, "DspFlushObj", 0);
            Assert.Equal(1, f0.I);
            Assert.Equal(1, f0.Error);
            var f1 = C(s, "DspFlushObj", 2);
            Assert.Equal(274, f1.I);
            Assert.Equal(274, f1.Error);
            // WinCopy: a scale that is not above 0 raises 274 with the halt flag
            var wc = C(s, "WinCopy", 0.0, 1.0, 0, "");
            Assert.Equal(TaskState.Halt, wc.Task.State);
            Assert.Equal(274, wc.Task.HaltCode);
            Assert.Equal(0, C(s, "WinCopy", 1.0, 1.0, 0, "").Error);
        }

        [Fact]
        public void TheKeyCommandLineHoldsThirtyTwoKeys()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            for (int i = 0; i < 40; i++)
                Assert.Equal(0, C(s, "KeyPut", 'A' + (i % 26)).I);
            Assert.Equal(Window.KeyLineMax, s.W.Display.Windows[0].KeyBuffer.Count);
            Assert.Equal('A' + 31 % 26, C(s, "KeyGet").I); // the 32nd key; the rest dropped
        }

        [Fact]
        public void AssGetPropertyGivesTheTagsOwnCluster()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "Ass", -2, "Flag", "Lib_Flag", 0).I);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal("C1", C(s, "AssGetProperty", "Flag", "ClusterName").S);
            Assert.Equal("C1.Lib_Flag", C(s, "AssGetProperty", "Flag", "FullName").S);
            Assert.Equal("Lib_Flag", C(s, "AssGetProperty", "Flag", "TagName").S);
        }

        [Fact]
        public void TheSameOutputAgainChangesNothing()
        {
            var s = MakeDisplay(out var host);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            var w = s.W.Display.Windows[0];
            Assert.Equal(0, C(s, "DspText", 5, 0, "hello").I);
            long serial = w.DrawnSerial;
            int outs = host.Outputs.Count;
            Assert.Equal(0, C(s, "DspText", 5, 0, "hello").I); // a Cicode object, every scan
            Assert.Equal(serial, w.DrawnSerial);
            Assert.Equal(outs, host.Outputs.Count);
            Assert.Equal(0, C(s, "DspText", 5, 0, "world").I);
            Assert.Equal(serial + 1, w.DrawnSerial);
            Assert.Single(w.Drawn);
        }

        [Fact]
        public void AChildWindowNeedsAnOpenParent()
        {
            // a task whose window was freed opens child windows: they get no parent, so two
            // windows can never be each other's parent (the free of children would not end)
            var s = MakeDisplay(out _);
            Assert.Equal(1, C(s, "WinNew", "Second").I);
            Assert.Equal(2, C(s, "WinNew", "Third").I);
            Assert.Equal(0, C(s, "WinFreeEx", 1).I);
            Assert.Equal(0, C(s, "WinFreeEx", 2).I);
            Assert.Equal(1, CallAt(s, 2, -1, "WinNewAt", "Second", 0, 0, 1).I); // its task's window 2 is closed
            Assert.Equal("-1", CallAt(s, 1, -1, "PageInfo", 8).S);
            Assert.Equal(2, CallAt(s, 1, -1, "WinNewAt", "Third", 0, 0, 1).I);
            Assert.Equal("1", CallAt(s, 2, -1, "PageInfo", 8).S);
            Assert.Equal(0, C(s, "WinFreeEx", 1).I); // and its child
            Assert.False(s.W.Display.Windows[1].Open);
            Assert.False(s.W.Display.Windows[2].Open);
        }

        [Fact]
        public void WebContentUrlErrorsAreTheBinarys()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(303, C(s, "DspWebContentGetURL", 5).Error); // not web content
            Assert.Equal(303, CallAt(s, 9, -1, "DspWebContentGetURL", 6).Error);
            // a Cicode string holds 255 characters: only a host value can be longer
            Assert.Equal(0, C(s, "DspWebContentSetURL", 6, Value.Str(new string('u', 300))).I);
            var g = C(s, "DspWebContentGetURL", 6);
            Assert.Equal("", g.S); // it does not fit 256 bytes
            Assert.Equal(583, g.Error);
        }

        [Fact]
        public void AssChainKeepsWhatIsAlreadyPending()
        {
            var s = MakeDisplay(out _);
            Assert.Equal(0, C(s, "Ass", -2, "Equip", "'Lit'", 0).I);
            Assert.Equal(0, C(s, "Ass", -2, "Other", "'O'", 0).I);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(0, C(s, "Ass", -2, "Equip", "'Mine'", 0).I); // an Ass before the chain
            Assert.Equal(1, C(s, "AssChain", -2, 0, 0).I); // only Other is copied
            Assert.Contains(s.W.Display.Pending, p => p.Target == -2 && p.Arg == "Equip" && p.Tag == "Mine");
            Assert.DoesNotContain(s.W.Display.Pending, p =>
                p.Target == -2 && p.Arg == "Equip" && p.Tag == "Lit"
            );
            Assert.Contains(s.W.Display.Pending, p => p.Target == -2 && p.Arg == "Other");
        }

        // ------------------------------------------------------------------ what a viewer draws
        /// <summary>The fonts of fonts.dbf with label and number colours, a style in TYPE and a
        /// flash colour; a German table for the language test.</summary>
        private void DisplayTables()
        {
            Dbf(
                Path.Combine(MainDir, "labels.dbf"),
                new[] { "NAME", "EXPR" },
                new[] { "PLATE_BLUE", "0x0080AAD5" },
                new[] { "BLACK", "0x00000000" }
            );
            Dbf(
                Path.Combine(MainDir, "fonts.dbf"),
                new[] { "NAME", "TYPE", "SIZE", "FGND_COLR", "BGND_COLR", "FGND_COLF", "BGND_COLF" },
                new[] { "Big", "Arial,B,I", "-12", "PLATE_BLUE", "", "BLACK", "" },
                new[] { "Small", "Arial", "10", "0x00FF0000", "0x00FFFFFF", "", "" }
            );
            Dbf(
                Path.Combine(MainDir, "lang.dbf"),
                new[] { "LANGUAGE", "SRC_FILE" },
                new[] { "German(Germany)", "german.dbf" }
            );
            Dbf(Path.Combine(MainDir, "german.dbf"), new[] { "NATIVE", "LOCAL" }, new[] { "Menu", "Menue" });
            Dbf(
                Path.Combine(MainDir, "activex.dbf"),
                new[] { "PAGE", "AN", "OBJ_NAME", "OBJ_GUID" },
                new[] { "Main", "6", "SEARCH", "{C21CD4A3-BAA4-4473-8A16-8D22B2664E6B}" },
                new[] { "Main", "4", "", "{X}" },
                new[] { "Main", "7", "CHOICE", "{E8A1DF62-3D7C-49B6-9344-2104253063A4}" }
            );
        }

        [Fact]
        public void FontInfoLocalisationAndFullTextForAViewer()
        {
            var s = MakeDisplay(out _, DisplayTables);
            var d = s.W.Display;
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            var big = d.Font(0)!;
            Assert.Equal(
                ("Arial", 16, 12.0, true, true, false),
                (big.Face, big.Height, big.Points, big.Bold, big.Italic, big.Underline)
            );
            var fi = big.ToJson();
            Assert.Equal("[8432341,0]", fi["fg"].ToString()); // a label, the flash colour BLACK
            Assert.Equal("[-1,-1]", fi["bg"].ToString()); // no background: transparent
            Assert.True(fi["flash"].Bool);
            var small = d.Font(1)!.ToJson();
            Assert.Equal(10, small["height"].Long); // a positive SIZE is pixels
            Assert.Equal("[16711680,16711680]", small["fg"].ToString());
            Assert.False(small["flash"].Bool);
            // the text output keeps '@(...)'; the viewer's JSON is localised and carries the font
            Assert.Equal(0, C(s, "DspText", 5, 0, "@(Menu) x", 40, 2, 1).I);
            var w = d.Windows[0];
            var cmd = Assert.Single(w.Drawn);
            Assert.Equal("@(Menu) x", cmd.Text);
            var j = new DisplayJson(d);
            var o = cmd.ToJson(j);
            Assert.Equal("Menu x", o["text"].Str);
            Assert.Equal("Arial", o["fontInfo"]["face"].Str);
            Assert.Equal(40, o["args"]["length"].Long);
            Assert.Equal(1, o["args"]["trunc"].Long);
            Assert.Equal("@(Menu) x", cmd.ToJson()["text"].Str); // the raw form (CLI, logs)
            // a symbol name is no display text
            Assert.Equal(0, C(s, "DspSym", 2, "@(lib).x").I);
            Assert.Equal("@(lib).x", w.DrawnAt(2)!.ToJson(j)["text"].Str);
            // the object states: text and tool tip
            var st = new CicodeSim.Page.ObjectState(5) { Text = "@(Menu)", Tooltip = "@(Pump^) 1)" };
            var sj = st.ToJson(j);
            Assert.Equal("Menu", sj["text"].Str);
            Assert.Equal("Pump) 1", sj["tooltip"].Str);
            // another language: its table's local text, the native text without a translation
            d.Language.Name = "German(Germany)";
            Assert.Equal("Menue x", cmd.ToJson(j)["text"].Str);
            Assert.Equal("Pump) 1", st.ToJson(j)["tooltip"].Str);
            d.Language.Name = "English";
            Assert.Equal("Menu x", cmd.ToJson(j)["text"].Str);
            // the table matches without case unless [Language]CaseSensitive (default
            // 0)
            Assert.False(d.Language.CaseSensitive);
            var ini = ProcessData.Of(s.Host).Ini;
            ini.Put(IniFiles.CitectIniKey, "Language", "LocalLanguage", "German(Germany)");
            Assert.Equal(0, C(s, "SetLanguage").I); // loads the [Language] parameters again
            Assert.Equal("German(Germany)", d.Language.Name);
            Assert.Equal("Menue", d.Language.Localise("@(menu)"));
            ini.Put(IniFiles.CitectIniKey, "Language", "CaseSensitive", "1");
            Assert.Equal(0, C(s, "SetLanguage").I);
            Assert.True(d.Language.CaseSensitive);
            Assert.Equal("menu", d.Language.Localise("@(menu)"));
            Assert.Equal("Menue", d.Language.Localise("@(Menu)"));
            ini.Put(IniFiles.CitectIniKey, "Language", "CaseSensitive", "");
            ini.Put(IniFiles.CitectIniKey, "Language", "LocalLanguage", "");
            Assert.Equal(0, C(s, "SetLanguage").I);
            Assert.False(d.Language.CaseSensitive);
            Assert.Equal("English", d.Language.Name);
            // a width / justification after an unescaped ',' is dropped; '^,' is the text's
            Assert.Equal("OK and A,B", DisplayLanguage.Translate("@(OK,20,C) and @(A^,B)", null));
            Assert.Equal("@(open", DisplayLanguage.Translate("@(open", null));
        }

        [Fact]
        public void ArrayOutputCarriesItsCells()
        {
            var s = MakeDisplay(out _, DisplayTables);
            var w = s.W.Display.Windows[0];
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            long h = C(s, "ArrayCreateByAn", 5, 3, 2, 1).I;
            Assert.True(h >= 0);
            Assert.Equal(0, C(s, "ArraySetStringByAn", 5, "@(Menu)", 0, 0).I);
            Assert.Equal(0, C(s, "ArraySetStringByAn", 5, "b", 2, 1).I);
            Assert.Equal(0, C(s, "DspArrayByAn", 5).I);
            var c = Assert.Single(w.Drawn);
            Assert.Equal("array", c.Kind);
            var cells = c.ToJson(new DisplayJson(s.W.Display))["cells"];
            Assert.Equal((2L, 3L, 1L), (cells["rows"].Long, cells["cols"].Long, cells["layers"].Long));
            Assert.Equal("[[\"Menu\",\"\",\"\"],[\"\",\"\",\"b\"]]", cells["data"].ToString());
            Assert.Equal(0, cells["firstRow"].Long);
            long serial = w.DrawnSerial;
            Assert.Equal(0, C(s, "DspArrayByAn", 5).I); // nothing changed: no new output
            Assert.Equal(serial, w.DrawnSerial);
            Assert.Equal(0, C(s, "ArraySetStringByAn", 5, "c", 1, 1).I);
            Assert.Equal(0, C(s, "DspArrayByAn", 5).I);
            Assert.Equal(serial + 1, w.DrawnSerial);
            Assert.Equal("c", Assert.Single(w.Drawn).Cells!["data"].Items[1].Items[1].Str);
            // an SA ArrayView array (16 layers): its value layer, settings and columns
            Assert.Equal(0, C(s, "ArrayDestroyByAn", 5).I);
            Assert.True(C(s, "ArrayCreateByAn", 5, 2, 3, 16).I >= 0);
            Assert.Equal(0, C(s, "ArraySetIntByAn", 5, 2, 0, 0, 2).I); // the first row shown
            Assert.Equal(0, C(s, "ArraySetIntByAn", 5, 18, 0, 0, 6).I); // the row height
            Assert.Equal(0, C(s, "ArraySetIntByAn", 5, 1, 0, 0, 9).I); // the row font
            Assert.Equal(0, C(s, "ArraySetStringByAn", 5, "Name", 1, 0, 1).I);
            Assert.Equal(0, C(s, "ArraySetIntByAn", 5, 120, 1, 0, 3).I);
            Assert.Equal(0, C(s, "ArraySetStringByAn", 5, "@(Menu)", 1, 0, 4).I);
            Assert.Equal(0, C(s, "ArraySetStringByAn", 5, "Pump", 1, 1, 1).I);
            Assert.Equal(0, C(s, "DspArrayByAn", 5).I);
            var sa = Assert.Single(w.Drawn).ToJson(new DisplayJson(s.W.Display))["cells"];
            Assert.Equal("Pump", sa["data"].Items[1].Items[1].Str);
            Assert.Equal((2L, 18L), (sa["firstRow"].Long, sa["rowHeight"].Long));
            var col = sa["columns"].Items[0];
            Assert.Equal(("Name", 120L, "Menu"), (col["field"].Str, col["width"].Long, col["title"].Str));
            Assert.Equal("Arial", sa["rowFontInfo"]["face"].Str);
            Assert.False(col.Has("meta"));
            // a symbol column (type 3, the SA treeview's PriorityAndState): its rows' z 2 = meta
            Assert.Equal(0, C(s, "ArraySetIntByAn", 5, 3, 1, 0, 2).I);
            Assert.Equal(0, C(s, "ArraySetIntByAn", 5, 6, 1, 1, 2).I);
            Assert.Equal(0, C(s, "DspArrayByAn", 5).I);
            var sym = Assert.Single(w.Drawn).ToJson(new DisplayJson(s.W.Display))["cells"]["columns"].Items[
                0
            ];
            Assert.Equal("[\"6\",\"\"]", sym["meta"].ToString());
        }

        [Fact]
        public void StrTruncFontMeasuresInTheFont()
        {
            var s = MakeDisplay(out _, DisplayTables);
            var big = s.W.Display.Font(0)!;
            int w3 = TextMeasure.Width(big, "abc");
            Assert.True(w3 > 0);
            Assert.Equal(TextMeasure.Width(big, "abcd"), C(s, "StrCalcWidth", "abcd", 0).I);
            Assert.Equal("abc", C(s, "StrTruncFont", "abcdefgh", "Big", w3, 0).S);
            Assert.Equal("abcdefgh", C(s, "StrTruncFont", "abcdefgh", "Big", -1, 0).S);
            string e = C(s, "StrTruncFont", "abcdefgh", "Big", TextMeasure.Width(big, "abcdef"), 1).S;
            Assert.EndsWith("...", e);
            Assert.True(TextMeasure.Width(big, e) <= TextMeasure.Width(big, "abcdef"));
            Assert.Equal("abcd", C(s, "StrTruncFontHnd", "abcdefgh", 0, 4, 2).S); // characters
            Assert.Equal("abc", C(s, "StrTruncFontHnd", "abcdefgh", -1, w3, 0).S); // -1 = the default font
        }

        [Fact]
        public void ActiveXControlsOfThePageAreSimulatedPropertyStores()
        {
            var s = MakeDisplay(out _, DisplayTables);
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            var o = C(s, "ObjectByNameEx", 0, "search");
            Assert.Equal(0, o.Error);
            Assert.Equal(1, C(s, "ObjectIsValid", o.V).I);
            var p = C(s, "_ObjectGetProperty", o.V, "Text");
            Assert.Equal((0, ""), (p.Error, p.S)); // not written yet: ''
            Assert.Equal(0, C(s, "_ObjectSetProperty", o.V, "Text", "x").I);
            Assert.Equal("x", C(s, "_ObjectGetProperty", o.V, "text").S); // what was written (names without case)
            Assert.Equal("", C(s, "_ObjectCallMethod", o.V, "Refresh").S);
            Assert.Equal(o.I, C(s, "ObjectByNameEx", 0, "SEARCH").I); // the same control again
            // a viewer types in the control (ctsim/setObjectProperty): the code reads it
            var w = s.W.Display.Windows[0];
            Assert.True(Outside.SetControlProperty(s.Host, w, 6, "Text", "Pump"));
            Assert.Equal("Pump", C(s, "_ObjectGetProperty", o.V, "Text").S);
            Assert.False(Outside.SetControlProperty(s.Host, w, 6, "Text", "Pump")); // no change
            Assert.Null(Outside.SetControlProperty(s.Host, w, 999, "Text", "Pump")); // no control there
            var cj = Outside.ControlsJson(s.Host, w)!;
            Assert.Equal(6, cj.Items[0]["an"].Long);
            Assert.Equal("Pump", cj.Items[0]["properties"]["Text"].Str);
            // a page shown again gets new controls: the old handle is no live object
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            Assert.Equal(0, C(s, "ObjectIsValid", o.V).I);
            var o2 = C(s, "ObjectByNameEx", 0, "search");
            Assert.Equal("", C(s, "_ObjectGetProperty", o2.V, "Text").S);
            Assert.Equal(0, C(s, "ObjectByNameEx", 0, "AN4").Error); // a row without a name
            var no = C(s, "ObjectByNameEx", 0, "Missing");
            Assert.Equal(360, no.Error);
            Assert.Equal(0, C(s, "ObjectIsValid", no.V).I);
            Assert.Equal(274, C(s, "_ObjectGetProperty", no.V, "Text").Error);
        }

        [Fact]
        public void ComboItemsAndSelectionAreSharedWithTheViewer()
        {
            var s = MakeDisplay(out _, DisplayTables);
            C(s, "_PageDisplay", "Main");
            var combo = C(s, "ObjectByNameEx", 0, "CHOICE").V;
            var window = s.W.Display.Windows[0];
            Assert.Equal(-1, C(s, "_ObjectGetProperty", combo, "ListIndex").I);
            C(s, "_ObjectCallMethod", combo, "AddItem", "Pump", 1);
            C(s, "_ObjectCallMethod", combo, "AddItem", "Pump", 1);
            Assert.True(Outside.SetControlProperty(s.Host, window, 7, "ListIndex", "1"));
            C(s, "_ObjectCallMethod", combo, "AddItem", "Valve", 0, 2);
            Assert.Equal(3, C(s, "_ObjectGetProperty", combo, "ListCount").I);
            Assert.Equal(2, C(s, "_ObjectGetProperty", combo, "ListIndex").I);
            Assert.Equal("Pump", C(s, "_ObjectGetProperty", combo, "Text").S);
            Assert.Equal("Valve", Outside.ControlsJson(s.Host, window)!.Items[0]["items"].Items[0].Str);
            Assert.Null(Outside.SetControlProperty(s.Host, window, 7, "ListIndex", "3"));
            Assert.Equal(274, C(s, "_ObjectCallMethod", combo, "RemoveItem", 3, 1).Error);
            C(s, "_ObjectCallMethod", combo, "RemoveItem", 0, 1);
            Assert.Equal(1, C(s, "_ObjectGetProperty", combo, "ListIndex").I);
            C(s, "_ObjectCallMethod", combo, "RemoveItem", 1, 1);
            Assert.Equal(-1, C(s, "_ObjectGetProperty", combo, "ListIndex").I);
            // (a ListIndex that is no whole number is refused, as one out of range)
            Assert.Equal(274, C(s, "_ObjectSetProperty", combo, "ListIndex", "abc").Error);
            Assert.Null(Outside.SetControlProperty(s.Host, window, 7, "ListIndex", "x"));
            C(s, "_ObjectSetProperty", combo, "ListIndex", 0);
            Assert.Equal("Pump", C(s, "_ObjectGetProperty", combo, "Text").S);
            Assert.True(Outside.SetControlProperty(s.Host, window, 7, "Text", "Other"));
            Assert.Equal(-1, C(s, "_ObjectGetProperty", combo, "ListIndex").I);
            C(s, "_ObjectCallMethod", combo, "Clear", 0, 0);
            Assert.Equal("", C(s, "_ObjectGetProperty", combo, "Text").S);
            Assert.Empty(Outside.ControlsJson(s.Host, window)!.Items[0]["items"].Items);
        }

        private sealed class MenuHost : IInteractionHost
        {
            public readonly List<Dialog> Shown = new List<Dialog>();
            public string? Answer;

            public void Show(Dialog d)
            {
                Shown.Add(d);
                if (Answer == null)
                    d.Cancel();
                else
                    d.Ok(Answer);
            }

            public void Changed(Dialog d, int field) { }

            public void Closed(Dialog d) { }

            public void Prompt(int window, string text) { }
        }

        [Fact]
        public void CtUtilPopupMenusGoToTheDialogHost()
        {
            var s = MakeDisplay(out _);
            var font = C(s, "DspFont", "Arial,B", 18, 0, 0).I;
            C(s, "DspSetPopupMenuFont", font);
            var host = new MenuHost { Answer = "102" };
            s.Host.Interaction = host;
            Assert.Equal(0, C(s, "_PageDisplay", "Main").I);
            long menu = C(s, "DLLOpen", @"C:\Bin\CTUTIL.DLL", "DspPopupMenu", "IJCJJC").I;
            long active = C(s, "DLLOpen", @"C:\Bin\CTUTIL.DLL", "IsActive", "II").I;
            long data = C(s, "DLLOpen", @"C:\Bin\CTUTIL.DLL", "GetData", "CI").I;
            Assert.True(menu > 0 && active > 0 && data > 0);
            Assert.Equal(289, C(s, "DLLOpen", @"C:\Bin\OTHER.DLL", "F", "II").Error);
            Assert.True(C(s, "DLLCallEx", menu, 0, "A,!B,,[Trend]C>1", -1, -1, "").I >= 0);
            Assert.True(C(s, "DLLCallEx", menu, 1, "D,~E", -1, -1, "").I >= 0);
            long req = C(s, "DLLCallEx", menu, -1, "", 10, 20, "").I;
            var d = Assert.Single(host.Shown);
            Assert.Equal(DialogKind.PopupMenu, d.Kind);
            Assert.Equal("Arial", d.MenuFont!.Face);
            Assert.Equal(18, d.MenuFont.Size);
            Assert.True(d.MenuFont.Bold);
            Assert.Equal((10, 20), (d.X, d.Y));
            Assert.Equal(4, d.Menu.Count);
            Assert.Equal(("A", 1, true), (d.Menu[0].Label, d.Menu[0].Id, d.Menu[0].Enabled));
            Assert.Equal(("B", 2, false), (d.Menu[1].Label, d.Menu[1].Id, d.Menu[1].Enabled));
            Assert.True(d.Menu[2].Separator);
            Assert.Equal(("C", "Trend", 0), (d.Menu[3].Label, d.Menu[3].Bitmap, d.Menu[3].Id));
            var sub = d.Menu[3].Items!;
            Assert.Equal(("D", 101, false), (sub[0].Label, sub[0].Id, sub[0].Checked));
            Assert.Equal(("E", 102, true), (sub[1].Label, sub[1].Id, sub[1].Checked));
            string r = req.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal("0", C(s, "DLLCall", active, r).S);
            Assert.Equal("102", C(s, "DLLCall", data, r).S);
            // the menus were cleared by the show; a cancelled menu gives 0
            host.Answer = null;
            Assert.True(C(s, "DLLCallEx", menu, 0, "X", -1, -1, "").I >= 0);
            long req2 = C(s, "DLLCallEx", menu, -1, "", -1, -1, "").I;
            Assert.Single(host.Shown[1].Menu);
            Assert.Equal(
                "0",
                C(s, "DLLCall", data, req2.ToString(System.Globalization.CultureInfo.InvariantCulture)).S
            );
            Assert.Equal(0, C(s, "DLLClose", menu).I);
        }
    }
}
