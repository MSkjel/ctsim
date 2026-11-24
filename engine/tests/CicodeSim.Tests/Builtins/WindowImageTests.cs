using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Host;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>WinFile, WinPrint, WinCopy and WinPrintFile on the synthetic World, with a fake
    /// viewer that draws windows; the bitmap reader / writer and the colour map.</summary>
    [Collection("World")]
    public sealed class WindowImageTests : DataTestBase
    {
        private sealed class Viewer : IWindowImageHost
        {
            public bool Connected = true;
            public readonly List<WindowImageRequest> Asked = new List<WindowImageRequest>();
            public readonly List<WindowImageRequest> Over = new List<WindowImageRequest>();

            public bool Ask(WindowImageRequest request)
            {
                if (!Connected)
                    return false;
                request.Id = Asked.Count + 1;
                Asked.Add(request);
                return true;
            }

            public void Done(WindowImageRequest request) => Over.Add(request);
        }

        private const int Red = 0xFF0000,
            White = 0xFFFFFF,
            Blue = 0x2B2BFF;

        /// <summary>3 x 2: black, white, red on top; blue, black, white below.</summary>
        private static BmpImage Picture()
        {
            var p = new BmpImage(3, 2);
            p[1, 0] = White;
            p[2, 0] = Red;
            p[0, 1] = Blue;
            p[2, 1] = White;
            return p;
        }

        private static int[] Row(BmpImage p, int y)
        {
            var r = new int[p.Width];
            for (int x = 0; x < p.Width; x++)
                r[x] = p[x, y];
            return r;
        }

        private Sim Make(out Viewer viewer)
        {
            var s = MakeData();
            viewer = new Viewer();
            s.Host.WindowImages = viewer;
            return s;
        }

        /// <summary>A built-in called by a background task in window 0; a blocking call is
        /// left waiting (Res.Blocked, the task's Wait).</summary>
        private static Res Start(Sim s, string name, params object[] args)
        {
            var b = Func0Table.Default.Lookup(name);
            Assert.True(b != null, name);
            var t = new CicodeTask(s.Run.Vm, "test", false);
            TaskContext.Set(t, 0, -1);
            var vals = new List<Value>();
            foreach (object a in args)
                vals.Add(CicodeVm.ValueOf(a));
            var r = s.Run.Vm.Builtins!.Call(b!.Index, t, ArgList.Of(vals.ToArray()));
            Assert.False(r.IsNotSimulated, name + " is not simulated");
            return new Res
            {
                V = r,
                Task = t,
                Blocked = r.IsBlock,
            };
        }

        /// <summary>The waiting task's turn: its result once its wait is over.</summary>
        private static long Wake(Res r)
        {
            Assert.True(r.Blocked);
            var w = r.Task.Wait!;
            Assert.True(w.Poll == null || w.Poll(), "the task still waits");
            return A.I(w.Result!() ?? Value.Nothing);
        }

        private static byte[]? Read(Sim s, string name) =>
            s.Host.Sandbox.ReadAll(Files.FullName(s.Host, name));

        [Fact]
        public void WinFileWritesTheViewersPictureAsABitmap()
        {
            var s = Make(out var viewer);
            var r = Start(s, "WinFile", "[RUN]:shot.bmp", 0.5, 2.0, 0, "");
            Assert.True(r.Blocked);
            var req = Assert.Single(viewer.Asked);
            Assert.Equal(
                (0, "WinFile", 0.5, 2.0, false),
                (req.Window, req.Function, req.XScale, req.YScale, req.Clipboard)
            );
            Assert.False(r.Task.Wait!.Poll!()); // waits for the picture
            Assert.True(req.Answer(Picture()));
            Assert.False(req.Answer(Picture())); // the first answer counts
            Assert.Equal(0, Wake(r));
            Assert.Same(req, Assert.Single(viewer.Over));
            var back = BmpImage.Read(Read(s, "[RUN]:shot.bmp")!);
            Assert.NotNull(back);
            Assert.Equal(new[] { 0, White, Red }, Row(back!, 0));
            Assert.Equal(new[] { Blue, 0, White }, Row(back!, 1));
        }

        [Fact]
        public void TheSwapAndTheColourMapRecolourThePicture()
        {
            var s = Make(out var viewer);
            File.WriteAllBytes(
                Path.Combine(MainDir, "colours.map"),
                Encoding.ASCII.GetBytes(
                    "043 043 255 155 205 255 025 000 025\r\n"
                        + "//Change dark blue to light blue\r\n"
                        + "255 000 000 000 255 000 ! red to green\r\n"
                        + "1 2 3\r\n"
                )
            );
            var r = Start(s, "WinFile", "[RUN]:mapped.bmp", 1.0, 1.0, 1, "[RUN]:colours.map");
            var req = Assert.Single(viewer.Asked);
            Assert.Equal(2, req.Map.Count);
            req.Answer(Picture());
            Assert.Equal(0, Wake(r));
            var back = BmpImage.Read(Read(s, "[RUN]:mapped.bmp")!)!;
            Assert.Equal(new[] { White, 0, 0x00FF00 }, Row(back, 0));
            Assert.Equal(new[] { 0x9BCDFF, White, 0 }, Row(back, 1));
        }

        [Fact]
        public void ColourMapLinesAndTolerances()
        {
            var map = ColourSwap.Parse(
                "043 043 255 155 205 255 025 000 025\n000 000 000 255 255 255 //x\n10 20\n"
            );
            Assert.Equal(2, map.Count);
            Assert.Equal(
                (0x2B2BFF, 0x9BCDFF, 25, 0, 25),
                (map[0].From, map[0].To, map[0].Hue, map[0].Sat, map[0].Lum)
            );
            Assert.Equal(
                (0, 0xFFFFFF, 0, 0, 0),
                (map[1].From, map[1].To, map[1].Hue, map[1].Sat, map[1].Lum)
            );
            // the tolerances: a hue tolerance alone doubles into saturation and luminance
            Assert.Equal(
                (10, 20, 20),
                (
                    ColourSwap.Parse("1 1 1 2 2 2 10")[0].Hue,
                    ColourSwap.Parse("1 1 1 2 2 2 10")[0].Sat,
                    ColourSwap.Parse("1 1 1 2 2 2 10")[0].Lum
                )
            );
            // the same hue and saturation, a little lighter: in; another hue: out
            Assert.True(map[0].Matches(0x3434FF));
            Assert.False(map[0].Matches(0xFF2B2B));
            Assert.False(map[1].Matches(0x010101));
        }

        [Fact]
        public void NoViewerWritesNothingAndReturnsZeroAfterOneTurn()
        {
            var s = Make(out var viewer);
            viewer.Connected = false;
            var r = Start(s, "WinFile", "[RUN]:none.bmp", 1.0, 1.0, 0, "");
            Assert.True(r.Blocked);
            Assert.Null(r.Task.Wait!.Poll);
            Assert.Equal(0, Wake(r));
            Assert.Null(Read(s, "[RUN]:none.bmp"));
            s.Host.WindowImages = null;
            r = Start(s, "WinCopy", 1.0, 1.0, 0, "");
            Assert.Equal(0, Wake(r));
        }

        [Fact]
        public void NoAnswerInTimeWritesNothing()
        {
            var s = Make(out var viewer);
            var r = Start(s, "WinFile", "[RUN]:late.bmp", 1.0, 1.0, 0, "");
            var req = Assert.Single(viewer.Asked);
            Assert.False(r.Task.Wait!.Poll!());
            s.W.Clock.Advance(31);
            Assert.True(r.Task.Wait!.Poll!());
            Assert.Equal(0, Wake(r));
            Assert.True(req.Done);
            Assert.False(req.Answer(Picture())); // too late
            Assert.Null(Read(s, "[RUN]:late.bmp"));
            Assert.Contains(s.Log, l => l.Contains("no picture of window 0 in time"));
        }

        [Fact]
        public void WinPrintNamesThePortsPrintouts()
        {
            var s = Make(out var viewer);
            var r = Start(s, "WinPrint", "LPT3:", 0.0, 0.0, 1, "");
            var req = Assert.Single(viewer.Asked);
            Assert.Equal((1.0, 1.0, "WinPrint"), (req.XScale, req.YScale, req.Function)); // 0: fit, drawn at 1
            req.Answer(Picture());
            Assert.Equal(0, Wake(r));
            var p1 = BmpImage.Read(s.Host.Sandbox.ReadAll(s.Host.Sandbox.Full("Printers\\LPT3-1.bmp"))!)!;
            Assert.Equal(new[] { White, 0, Red }, Row(p1, 0)); // swapped (the default)
            r = Start(s, "WinPrint", "LPT3:", 1.0, 1.0, 0, "");
            viewer.Asked[1].Answer(Picture());
            Assert.Equal(0, Wake(r));
            var p2 = BmpImage.Read(s.Host.Sandbox.ReadAll(s.Host.Sandbox.Full("Printers\\LPT3-2.bmp"))!)!;
            Assert.Equal(new[] { 0, White, Red }, Row(p2, 0));
        }

        [Fact]
        public void WinPrintFileCopiesASavedPictureAsAPrintout()
        {
            var s = Make(out var viewer);
            var r = Start(s, "WinFile", "[RUN]:temp", 1.0, 1.0, 0, "");
            viewer.Asked[0].Answer(Picture());
            Assert.Equal(0, Wake(r));
            // no viewer needed: green to red and the default swap
            viewer.Connected = false;
            var p = Call(s, "WinPrintFile", "[RUN]:temp", "LPT1:", 0.0, 0.0, 1, 0xFF0000, 0x00FF00);
            Assert.False(p.Blocked);
            Assert.Equal(0, p.I);
            var out1 = BmpImage.Read(s.Host.Sandbox.ReadAll(s.Host.Sandbox.Full("Printers\\LPT1-1.bmp"))!)!;
            Assert.Equal(new[] { White, 0, 0x00FF00 }, Row(out1, 0));
            Assert.Equal(new[] { Blue, White, 0 }, Row(out1, 1));
            // unchanged colours with swap 0 and no colours given
            Assert.Equal(0, Call(s, "WinPrintFile", "[RUN]:temp", "LPT1:", 0.0, 0.0, 0, -1, -1).I);
            var out2 = BmpImage.Read(s.Host.Sandbox.ReadAll(s.Host.Sandbox.Full("Printers\\LPT1-2.bmp"))!)!;
            Assert.Equal(new[] { 0, White, Red }, Row(out2, 0));
            // a missing file, a file that is not a bitmap
            Assert.Equal(261, Call(s, "WinPrintFile", "[RUN]:missing", "LPT1:", 0.0, 0.0, 1, -1, -1).I);
            File.WriteAllText(Path.Combine(MainDir, "text.bmp"), "not a picture");
            Assert.Equal(265, Call(s, "WinPrintFile", "[RUN]:text.bmp", "LPT1:", 0.0, 0.0, 1, -1, -1).I);
        }

        [Fact]
        public void ScaleChecksAndForegroundCode()
        {
            var s = Make(out var viewer);
            var r = Start(s, "WinCopy", 0.0, 1.0, 0, "");
            Assert.False(r.Blocked);
            Assert.Equal(274, r.Task.Error);
            r = Start(s, "WinFile", "[RUN]:x.bmp", 1.0, -1.0, 0, "");
            Assert.False(r.Blocked);
            Assert.Equal(274, r.Task.Error);
            r = Start(s, "WinPrint", "LPT1:", -1.0, 0.0, 1, "");
            Assert.False(r.Blocked);
            Assert.Equal(274, r.Task.Error);
            Assert.Empty(viewer.Asked);
            var fg = CallFg(s, "WinCopy", 1.0, 1.0, 0, "");
            Assert.Equal(282, fg.I);
            Assert.Empty(viewer.Asked);
        }

        [Fact]
        public void WinCopyAsksForTheClipboard()
        {
            var s = Make(out var viewer);
            var r = Start(s, "WinCopy", 1.0, 1.0, 0, "");
            var req = Assert.Single(viewer.Asked);
            Assert.True(req.Clipboard);
            var j = req.ToJson();
            Assert.Equal("WinCopy", j["function"].Str);
            Assert.True(j["clipboard"].Bool);
            req.Answer(null); // the viewer had no picture
            Assert.Equal(0, Wake(r));
        }

        [Fact]
        public void BitmapsOfEveryKindTheReaderTakes()
        {
            var p = Picture();
            byte[] bmp = p.ToBmp();
            Assert.Equal(54 + 12 * 2, bmp.Length); // rows padded to 4 bytes
            Assert.Equal(new[] { 0, White, Red }, Row(BmpImage.Read(bmp)!, 0));
            // 32 bits a pixel, top-down
            var top = new byte[54 + 3 * 2 * 4];
            Array.Copy(bmp, top, 54);
            BitConverter.GetBytes(-2).CopyTo(top, 22);
            top[28] = 32;
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
            {
                int v = p[x, y];
                int o = 54 + (y * 3 + x) * 4;
                top[o] = (byte)v;
                top[o + 1] = (byte)(v >> 8);
                top[o + 2] = (byte)(v >> 16);
            }
            var back = BmpImage.Read(top)!;
            Assert.Equal(new[] { Blue, 0, White }, Row(back, 1));
            // not bitmaps
            Assert.Null(BmpImage.Read(Encoding.ASCII.GetBytes("BM short")));
            var rle = (byte[])bmp.Clone();
            rle[30] = 1;
            Assert.Null(BmpImage.Read(rle));
            var cut = new byte[bmp.Length - 4];
            Array.Copy(bmp, cut, cut.Length);
            Assert.Null(BmpImage.Read(cut));
        }

        // ------------------------------------------------------------------ the server's side
        [Fact]
        public void ViewerImagesAnswersCancelsAndRefuses()
        {
            var sent = new List<(string Name, JsonValue Body)>();
            bool connected = false;
            var vi = new ViewerImages(() => connected, (n, b) => sent.Add((n, b)));
            var req = new WindowImageRequest
            {
                Window = 2,
                Page = "Mixer",
                Function = "WinCopy",
                Clipboard = true,
                SwapBlackWhite = true,
            };
            Assert.False(vi.Ask(req));
            Assert.Empty(sent);
            connected = true;
            Assert.True(vi.Ask(req));
            var ev = Assert.Single(sent);
            Assert.Equal("ctsim/imageWanted", ev.Name);
            Assert.Equal(
                (req.Id, 2L, "Mixer"),
                ((int)ev.Body["id"].Long, ev.Body["window"].Long, ev.Body["page"].Str)
            );
            Assert.Equal(new[] { req.Id }, vi.Open());
            Assert.Throws<ArgumentException>(() => vi.Answer(Args(("id", 999), ("none", true))));
            Assert.Throws<ArgumentException>(() => vi.Answer(Args(("id", req.Id), ("bmp", "not base64!"))));
            Assert.Throws<ArgumentException>(() =>
                vi.Answer(Args(("id", req.Id), ("bmp", Convert.ToBase64String(new byte[] { 1, 2, 3 }))))
            );
            var body = vi.Answer(Args(("id", req.Id), ("bmp", Convert.ToBase64String(Picture().ToBmp()))));
            Assert.True(body["accepted"].Bool);
            Assert.Equal((3L, 2L), (body["width"].Long, body["height"].Long));
            // the clipboard gets the recoloured picture back
            var clip = BmpImage.Read(Convert.FromBase64String(body["bmp"].Str))!;
            Assert.Equal(new[] { White, 0, Red }, Row(clip, 0));
            Assert.True(req.Done);
            vi.Done(req);
            Assert.Empty(vi.Open());
            // the last viewer went: a waiting request ends without a picture
            var other = new WindowImageRequest { Function = "WinFile" };
            vi.Ask(other);
            vi.CancelAll();
            Assert.True(other.Done);
            Assert.Null(other.Image);
            Assert.Empty(vi.Open());
        }

        private static JsonValue Args(params (string Key, object Value)[] kv)
        {
            var o = JsonValue.NewObject();
            foreach (var (k, v) in kv)
                o.Set(k, v);
            return o;
        }
    }
}
