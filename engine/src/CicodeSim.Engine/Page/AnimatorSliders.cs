using System;
using CicodeSim.World.Display;
using CProgram = CicodeSim.Compiled.Program;

namespace CicodeSim.Page
{
    /// <summary>The page objects' sliders for DspGetSlider / DspSetSlider.</summary>
    public sealed partial class Animator : ISliders
    {
        bool ISliders.Slider(Window w, int an, out byte[] read, out byte[]? write, out CProgram? program)
        {
            read = Array.Empty<byte>();
            write = null;
            program = null;
            if (!(w.Page?.Runtime is PageRun run) || run.Window != w || run.Closed)
                return false;
            var o = run.Object(an);
            if (o == null)
                return false;
            var sl =
                o.SliderX ? o.MvxSl
                : o.SliderY ? o.MvySl
                : o.SliderRot ? o.RotSl
                : (null, null);
            if (sl.Read == null)
                return false;
            read = sl.Read;
            write = sl.Write;
            program = o.Program ?? run.Compiled.Program;
            return true;
        }
    }
}
