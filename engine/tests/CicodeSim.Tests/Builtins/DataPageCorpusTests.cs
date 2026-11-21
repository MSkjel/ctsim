using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Page corpus calls of the most used data built-ins.</summary>
    [Collection("World")]
    public sealed class DataPageCorpusTests : DataTestBase
    {
        private static readonly string[] Names =
        {
            "AlarmCount",
            "AlarmActive",
            "GetPriv",
            "UserInfo",
            "ProductInfo",
        };

        private static bool Calls(byte[] code, HashSet<int> idx)
        {
            int pc = 0;
            while (pc < code.Length)
            {
                int op = code[pc];
                if (op == 0xff)
                    return false;
                Instr ins;
                try
                {
                    ins = Opcodes.Decode(code, pc);
                }
                catch (Exception)
                {
                    return false;
                }
                if (ins.Op == Op.Func && idx.Contains(ins.A))
                    return true;
                if (ins.Size <= 0)
                    return false;
                pc += ins.Size;
            }
            return false;
        }

        [Theory]
        [InlineData("ExampleSA")]
        [InlineData("Example")]
        public void PageBlocksCallingTheDataBuiltinsDoNotStopAtThem(string project)
        {
            if (!HaveR2)
                return;
            string dir = Path.Combine(R2, project);
            var w = new CicodeSim.World.World(dir, root: project);
            w.Start();
            var run = new WorldRun(w, Rdb.LoadLibrary(Path.Combine(dir, "_LIBRARY.RDB")));
            var idx = new HashSet<int>();
            foreach (string n in Names)
                idx.Add(Func0Table.Default.Lookup(n)!.Index);
            int blocks = 0,
                stopsAtThem = 0;
            var bad = new List<string>();
            foreach (string rdb in Directory.GetFiles(dir, "*.RDB"))
            {
                string name = Path.GetFileName(rdb);
                if (name.StartsWith("_", StringComparison.Ordinal))
                    continue;
                PageCode pc;
                try
                {
                    pc = Rdb.LoadPage(rdb);
                }
                catch (RdbError)
                {
                    continue;
                }
                foreach (var b in pc.Blocks)
                {
                    if (!Calls(b.Code, idx))
                        continue;
                    blocks++;
                    var t = run.Vm.TaskForBlock(b.Code, pc.Program, foreground: true, name);
                    try
                    {
                        run.Vm.Run(t, 100);
                    }
                    catch (InvalidOperationException) { }
                    if (t.State == TaskState.Halt && t.HaltReason.Contains("not simulated"))
                        foreach (string n in Names)
                            if (
                                t.HaltReason.Contains(" " + n + " ")
                                || t.HaltReason.EndsWith(" " + n, StringComparison.Ordinal)
                            )
                            {
                                stopsAtThem++;
                                if (bad.Count < 5)
                                    bad.Add(
                                        name
                                            + " "
                                            + b.Table
                                            + "."
                                            + b.Record
                                            + "."
                                            + b.Field
                                            + ": "
                                            + t.HaltReason
                                    );
                            }
                }
            }
            Assert.True(blocks > 0, project + ": " + blocks + " blocks");
            Assert.True(stopsAtThem == 0, string.Join("\n", bad));
        }
    }
}
