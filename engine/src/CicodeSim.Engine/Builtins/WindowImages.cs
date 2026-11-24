using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>A picture of a window asked of a viewer (WinFile, WinPrint, WinCopy): the
    /// viewer draws the window's page at the scale, the engine recolours it.</summary>
    public sealed class WindowImageRequest
    {
        /// <summary>The id the viewer answers with (set by the host that sends it).</summary>
        public int Id;
        public int Window;
        public string Page = "";

        /// <summary>The built-in that asks (WinFile, WinPrint or WinCopy).</summary>
        public string Function = "";
        public double XScale = 1,
            YScale = 1;

        /// <summary>WinCopy: the viewer puts the finished picture on the clipboard.</summary>
        public bool Clipboard;

        /// <summary>The recolouring applied to the viewer's picture.</summary>
        public bool SwapBlackWhite;
        public IReadOnlyList<ColourSwap> Map = Array.Empty<ColourSwap>();

        private readonly object _lock = new object();
        private bool _done;
        private BmpImage? _image;

        /// <summary>Answered (with a picture or none) or cancelled.</summary>
        public bool Done
        {
            get
            {
                lock (_lock)
                    return _done;
            }
        }

        /// <summary>The recoloured picture, null when the viewer had none or nobody answered.</summary>
        public BmpImage? Image
        {
            get
            {
                lock (_lock)
                    return _image;
            }
        }

        /// <summary>The viewer's picture (null = none): recoloured and kept. False when the
        /// request was already answered or cancelled (the first answer counts).</summary>
        public bool Answer(BmpImage? image)
        {
            image?.Recolour(SwapBlackWhite, Map);
            lock (_lock)
            {
                if (_done)
                    return false;
                _done = true;
                _image = image;
                return true;
            }
        }

        /// <summary>No answer is taken any more.</summary>
        public void Cancel() => Answer(null);

        /// <summary>The ctsim/imageWanted body.</summary>
        public JsonValue ToJson()
        {
            var o = JsonValue.NewObject();
            o.Set("id", JsonValue.Of(Id));
            o.Set("window", JsonValue.Of(Window));
            o.Set("page", Page);
            o.Set("function", Function);
            o.Set("xScale", JsonValue.Of(XScale));
            o.Set("yScale", JsonValue.Of(YScale));
            o.Set("clipboard", JsonValue.Of(Clipboard));
            return o;
        }
    }

    /// <summary>The viewers that draw windows for WinFile, WinPrint and WinCopy.</summary>
    public interface IWindowImageHost
    {
        /// <summary>Send the request to a viewer that draws windows (it gets its id): false
        /// when none is connected.</summary>
        bool Ask(WindowImageRequest request);

        /// <summary>The request is over (answered, cancelled or timed out).</summary>
        void Done(WindowImageRequest request);
    }

    public sealed partial class SimHost
    {
        /// <summary>The viewers that draw windows (null: none, WinFile / WinPrint / WinCopy
        /// write nothing).</summary>
        public IWindowImageHost? WindowImages { get; set; }

        /// <summary>Simulated ms a window's picture is waited for.</summary>
        public double WindowImageWaitMs = 30000;
    }

    /// <summary>WinFile, WinPrint, WinCopy and WinPrintFile: the window's picture from the
    /// viewer, recoloured, as a .bmp file, a printout or on the clipboard.</summary>
    public static class WindowImages
    {
        private static double Real(ArgList a, int k, double dflt) => k < a.Count ? A.R(a.Get(k)) : dflt;

        private static long Int(ArgList a, int k, long dflt) => k < a.Count ? A.I(a.Get(k)) : dflt;

        private static string Str(ArgList a, int k) => k < a.Count ? A.S(a.Get(k)) : "";

        /// <summary>The swaps of a map file (sMap), read through the sandbox as the File
        /// functions name files; none when the name is empty or the file can't be read
        /// (noted).</summary>
        private static IReadOnlyList<ColourSwap> Map(SimHost h, CicodeTask t, string fn, string name)
        {
            if (name.Trim().Length == 0)
                return Array.Empty<ColourSwap>();
            string full = Files.FullName(h, name);
            byte[]? data = h.Sandbox.ReadAll(full);
            if (data == null)
            {
                h.Note(t, fn + ": the colour map " + full + " cannot be read; no colours are mapped");
                return Array.Empty<ColourSwap>();
            }
            return ColourSwap.Parse(Cp1252.Decode(data));
        }

        /// <summary>Ask the viewer for the task's window's picture and wait for it (blocking);
        /// <paramref name="done"/> takes the picture (null: no viewer, no answer, or none)
        /// when the task wakes.</summary>
        private static Value Ask(
            SimHost h,
            CicodeTask t,
            string fn,
            double xs,
            double ys,
            bool swap,
            string map,
            bool clipboard,
            Func<BmpImage?, Value> done
        )
        {
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(256);
            var req = new WindowImageRequest
            {
                Window = w.Number,
                Page = w.Page?.Name ?? "",
                Function = fn,
                XScale = xs,
                YScale = ys,
                Clipboard = clipboard,
                SwapBlackWhite = swap,
                Map = Map(h, t, fn, map),
            };
            var host = h.WindowImages;
            if (host == null || !host.Ask(req))
            {
                h.Note(t, fn + ": no viewer draws windows; nothing was done");
                return W.BlockOneTurn(t, fn, () => done(null));
            }
            var clock = t.Vm.Clock;
            double until = clock.MonotonicMs() + h.WindowImageWaitMs;
            t.Wait = new Wait(TaskState.Wait, fn + "(window " + W.Inv(req.Window) + ")")
            {
                Poll = () => req.Done || clock.MonotonicMs() >= until,
                Result = () =>
                {
                    bool late = !req.Done;
                    req.Cancel();
                    host.Done(req);
                    if (late)
                        h.Note(
                            t,
                            fn + ": the viewer sent no picture of window " + W.Inv(req.Window) + " in time"
                        );
                    return done(req.Image);
                },
            };
            return Value.Block;
        }

        /// <summary>Write a picture into the sandbox: 0, else 262.</summary>
        private static int Write(SimHost h, CicodeTask t, string fn, string full, BmpImage img)
        {
            if (h.Sandbox.WriteOutput(full, img.ToBmp(), t.Vm.Clock.NowFiletime()))
            {
                h.Note(t, fn + ": the picture is in " + full);
                return 0;
            }
            h.Note(t, fn + ": the picture could not be written to " + full);
            return 262;
        }

        [Builtin(
            "WinFile",
            Doc = "1193975",
            Blocking = true,
            Effects = "writes the window's picture to the sandbox as a .bmp file",
            Errors = "274 a width or height scale that is not above 0 (raised with the halt flag, 0 returned) (guess: WinCopy's check); 282 foreground code (raised and returned) (guess); 256 the task's window is not open (returned) (guess); 262 the file cannot be written (returned) (guess)",
            Notes = "Waits for the viewer that draws windows (ctsim/imageWanted): it draws the window's page at the scale, the engine recolours it (bSwapBlackWhite: black and white swapped; sMap: the map file's colours, first match wins, a mapped colour is not swapped) and writes it at sFile, named as the File functions name files ([data]: and the other path operators; a bare name goes in the simulator's current folder, the run folder, where the runtime uses its bin folder) as a 24-bit uncompressed .bmp (guess: the runtime's 'compressed .bmp' format is not known). No viewer, or no picture in 30 s of simulated time: nothing is written, 0 returned. A map file that cannot be read maps nothing (guess)."
        )]
        private static Value WinFile(SimHost h, CicodeTask t, ArgList a)
        {
            string name = Str(a, 0);
            double x = Real(a, 1, 1),
                y = Real(a, 2, 1);
            if (!(x > 0.0 && y > 0.0))
            {
                t.RaiseError(Codes.InvalidArg, true);
                return W.I(0);
            }
            string full = Files.FullName(h, name);
            return Ask(h, t, "WinFile", x, y, Int(a, 3, 0) != 0, Str(a, 4), false, img =>
                W.I(img == null ? 0 : Write(h, t, "WinFile", full, img))
            );
        }

        [Builtin(
            "WinPrint",
            Doc = "1193995",
            Blocking = true,
            Effects = "writes the window's picture to the sandbox as the port's printout",
            Errors = "274 a negative scale (raised with the halt flag, 0 returned) (guess); 282 foreground code (raised and returned) (guess); 256 the task's window is not open (returned) (guess); 262 the printout cannot be written (returned) (guess)",
            Notes = "No printer: the window's picture, drawn by the viewer and recoloured as WinFile does (bSwapBlackWhite defaults to 1 here), goes to the sandbox as Printers\\(port)-(n).bmp, n counting that port's printouts (plots, rich text and pictures) from 1. A scale of 0 (fit to the page) draws at 1 (guess: there is no paper). No viewer, or no picture in 30 s of simulated time: nothing is printed, 0 returned."
        )]
        private static Value WinPrint(SimHost h, CicodeTask t, ArgList a)
        {
            string port = Str(a, 0);
            double x = Real(a, 1, 0),
                y = Real(a, 2, 0);
            if (x < 0.0 || y < 0.0)
            {
                t.RaiseError(Codes.InvalidArg, true);
                return W.I(0);
            }
            return Ask(
                h,
                t,
                "WinPrint",
                x == 0.0 ? 1.0 : x,
                y == 0.0 ? 1.0 : y,
                Int(a, 3, 1) != 0,
                Str(a, 4),
                false,
                img => W.I(img == null ? 0 : Write(h, t, "WinPrint", Plots.NextPrintout(h, port, "bmp"), img))
            );
        }

        [Builtin(
            "WinCopy",
            Doc = "1193974",
            Blocking = true,
            Effects = "puts the window's picture on the viewer's clipboard",
            Errors = "274 a width or height scale that is not above 0 (raised with the halt flag, 0 returned); 282 foreground code (raised and returned) (guess); 256 the task's window is not open (returned) (guess)",
            Notes = "Waits for the viewer that draws windows (ctsim/imageWanted with clipboard): it draws the window's page at the scale, the engine recolours it as WinFile does and the viewer puts the result on the system clipboard. The scale check is the runtime's ('0.0 < x && 0.0 < y', else; before the raise). No viewer, or no picture in 30 s of simulated time: nothing is copied, 0 returned."
        )]
        private static Value WinCopy(SimHost h, CicodeTask t, ArgList a)
        {
            double x = Real(a, 0, 1),
                y = Real(a, 1, 1);
            if (!(x > 0.0 && y > 0.0))
            {
                t.RaiseError(Codes.InvalidArg, true);
                return W.I(0);
            }
            return Ask(h, t, "WinCopy", x, y, Int(a, 2, 0) != 0, Str(a, 3), true, img => W.I(0));
        }

        [Builtin(
            "WinPrintFile",
            Doc = "1193996",
            Effects = "writes the file to the sandbox as the port's printout",
            Errors = "261 the file does not exist (returned) (guess); 265 the file is not a bitmap the simulator reads (returned) (guess); 262 the printout cannot be written (returned) (guess)",
            Notes = "No printer: the file (a .bmp WinFile wrote, read through the sandbox) is recoloured (bSwapBlackWhite, default 1; fromColor to toColor, 0xRRGGBB, when both are given, taking precedence over the swap) and goes to the sandbox as Printers\\(port)-(n).bmp, as WinPrint. The scales are not applied (guess). Needs no viewer."
        )]
        private static Value WinPrintFile(SimHost h, CicodeTask t, ArgList a)
        {
            string full = Files.FullName(h, Str(a, 0));
            string port = Str(a, 1);
            bool swap = Int(a, 4, 1) != 0;
            long from = Int(a, 5, -1),
                to = Int(a, 6, -1);
            byte[]? data = h.Sandbox.ReadAll(full);
            if (data == null)
            {
                h.Note(t, "WinPrintFile: " + full + " does not exist");
                return W.I(Codes.FileNotFound);
            }
            var img = BmpImage.Read(data);
            if (img == null)
            {
                h.Note(t, "WinPrintFile: " + full + " is not a bitmap the simulator reads");
                return W.I(265);
            }
            var map = new List<ColourSwap>();
            if (from != -1 && to != -1)
                map.Add(new ColourSwap { From = (int)(from & 0xFFFFFF), To = (int)(to & 0xFFFFFF) });
            img.Recolour(swap, map);
            return W.I(Write(h, t, "WinPrintFile", Plots.NextPrintout(h, port, "bmp"), img));
        }
    }
}
