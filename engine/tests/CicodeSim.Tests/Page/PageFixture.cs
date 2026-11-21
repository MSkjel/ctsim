using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Page;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Tests.Page
{
    /// <summary>A compiled page built in memory: the page's pools (a ProgramBuilder), its code
    /// blocks and its table rows, as a page .RDB would give them.</summary>
    public sealed class PageFixture
    {
        public readonly ProgramBuilder Pb = new ProgramBuilder();
        public readonly CompiledPage Page;

        public PageFixture(string name = "TestPage")
        {
            Page = new CompiledPage(name)
            {
                Control = new PageRecord
                {
                    Name = name,
                    Title = name + " title",
                    NextPage = "Next",
                    PrevPage = "Prev",
                },
            };
        }

        /// <summary>A code block (End appended) in the page's CODE pool; its offset.</summary>
        public long Code(Action<CodeBuilder> build)
        {
            var cb = Pb.Block();
            build(cb);
            cb.End();
            return Page.AddCode(cb.Code());
        }

        /// <summary>An expression reading a tag (Load).</summary>
        public long Load(string tag, bool percent = false)
        {
            long off = Code(c => c.Load(tag));
            if (percent)
                foreach (var r in Pb.Pools.TagRefs.Values)
                    if (r.Tag == tag)
                        r.Percentage = 1;
            return off;
        }

        /// <summary>A tag reference by percent (TAGREF Percentage = 1).</summary>
        public void Percent(string tag)
        {
            foreach (var r in Pb.Pools.TagRefs.Values)
                if (r.Tag == tag)
                    r.Percentage = 1;
        }

        public long Const(long v) => Code(c => c.ConstInt(v));

        public long ConstReal(double v) => Code(c => c.ConstReal(v));

        /// <summary>A command: call a built-in with constant / tag arguments, value popped.</summary>
        public byte[] Command(Action<CodeBuilder> build) => CompiledPage.Command(Code(build));

        public Dictionary<string, object?> Row(string table, params (string, object?)[] fields)
        {
            var r = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in fields)
                r[k] = v;
            Page.AddRow(table, r);
            return r;
        }

        /// <summary>A PGDYNOBJ row (TYPE "0x0005" style).</summary>
        public Dictionary<string, object?> Dyn(int an, int type, int group, params (string, object?)[] fields)
        {
            var all = new List<(string, object?)>
            {
                ("AN", (long)an),
                ("TYPE", "0x" + type.ToString("X4")),
                ("GROUP", (long)group),
            };
            all.AddRange(fields);
            return Row("PGDYNOBJ", all.ToArray());
        }

        public CompiledPage Build()
        {
            Page.Program = Pb.Build();
            return Page;
        }
    }

    /// <summary>A drawing summary built in code.</summary>
    public static class Draw
    {
        public static DrawObject Obj(int an, int type, Rect? rect = null, int parent = 0) =>
            new DrawObject
            {
                An = an,
                Type = type,
                Rect = rect,
                Bounds = rect,
                Parent = parent,
            };

        public static PageModel Model(params DrawObject[] objs)
        {
            var m = new PageModel();
            foreach (var o in objs)
                m.Drawing.Add(o);
            return m;
        }
    }
}
