using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Sim;
using CicodeSim.Tests.Golden;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>The debugger's Module scope (LibraryVars): the MODULE variables each file's
    /// functions use, a variable passed by reference included (AddrCodeVar, whose operand is the
    /// variable's RUN DataAddress, not a CODEVAR offset: row 0x3c).</summary>
    public sealed class LibraryVarsTests
    {
        [GoldenFact]
        [Trait("Golden", "light")]
        public void ModuleScopeReadsAddrCodeVarByDataAddress()
        {
            var bad = new List<string>();
            foreach (string lib in new[] { "ExampleSA", "Example" })
            {
                var prog = Rdb.LoadLibrary(Path.Combine(GoldenFiles.User, lib, "_LIBRARY.RDB"));
                var lv = LibraryVars.For(prog);
                var want = new Dictionary<string, HashSet<CodeVar>>(StringComparer.Ordinal);
                foreach (var fn in prog.Functions)
                {
                    string key = CicodeSim.Vm.SourceFiles.Key(fn.Filename);
                    foreach (var x in Opcodes.Instructions(fn.Code))
                    {
                        CodeVar? cv = null;
                        if (x.Op == Op.AddrCodeVar)
                            cv = prog.CodeVarAtData(x.Offset);
                        else if (x.Op == Op.LoadCodeVar || x.Op == Op.StoreCodeVar)
                            prog.CodeVars.TryGetValue(x.Offset, out cv);
                        if (cv == null || cv.UnitType == -1)
                            continue;
                        if (!want.TryGetValue(key, out var s))
                            want[key] = s = new HashSet<CodeVar>();
                        s.Add(cv);
                    }
                }
                if (want.Count != lv.Module.Count)
                    bad.Add($"{lib}: {lv.Module.Count} files with module variables, want {want.Count}");
                foreach (var kv in want)
                {
                    var got = lv.Module.TryGetValue(kv.Key, out var l)
                        ? new HashSet<CodeVar>(l)
                        : new HashSet<CodeVar>();
                    if (!got.SetEquals(kv.Value))
                        bad.Add($"{lib} {kv.Key}: {got.Count} variables, want {kv.Value.Count}");
                }
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
    }
}
