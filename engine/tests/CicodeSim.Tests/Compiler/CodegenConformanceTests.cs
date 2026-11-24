using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Tests.Vm;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>Runtime conformance cases on code our compiler made.</summary>
    public class CodegenConformanceTests
    {
        /// <summary>Compiled from the repo files alone (conformance.ci; FUNC0.DBF when installed,
        /// else the runtime's built-in table).</summary>
        [Fact]
        public void CompiledConformanceGivesTheLiveResults() => RunCases(null);

        /// <summary>The same with the runtime's built-in table in place of FUNC0.DBF (what a machine
        /// without the install compiles with).</summary>
        [Fact]
        public void CompiledWithTheRuntimeTableGivesTheLiveResults() =>
            RunCases(Func0Rows.FromRuntimeTable());

        private static void RunCases(CicodeSim.Compiler.Semantics.Func0Rows? func0)
        {
            string dir = RuntimeFunctionCasesTests.CasesDir();
            Assert.True(dir.Length > 0, "no conformance/runtime/functions");
            string src = CicodeSim.Compiler.Syntax.Lexer.SourceText(
                File.ReadAllBytes(Path.Combine(dir, "conformance.ci"))
            );
            var unit = Units
                .ForTexts(
                    "CiSimRT",
                    new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("conformance.ci", src),
                    },
                    func0
                )
                .Compile();
            Assert.Empty(unit.Errors());
            var img = LibraryBuilder.Build(unit);
            Assert.Empty(img.Failed);
            var (compared, bad) = RuntimeFunctionCasesTests.Replay(img.ToProgram("source:CiSimRT"));
            Assert.True(compared >= 14, "cases compared: " + compared);
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        /// <summary>The CiSimRT unit (ExampleSA + zz_conformance.ci) compiled from source equals the
        /// kept CtCmp32 library instruction for instruction, operands resolved.</summary>
        [RuntimeCasesFact]
        public void CiSimRtUnitMatchesItsCompiledLibrary()
        {
            string folder = Path.GetDirectoryName(RuntimeFunctionCasesTests.Library)!;
            var unit = Units.ForProject(folder).Compile();
            Assert.Empty(unit.Errors());
            var img = LibraryBuilder.Build(unit);
            Assert.Empty(img.Failed);
            var rep = LibraryCheck.Compare(img, RuntimeFunctionCasesTests.Library, 10);
            var why = new List<string>();
            foreach (var (i, name, what) in rep.Mismatches)
                why.Add($"#{i} {name}: {what}");
            Assert.True(
                rep.Exact == rep.Functions && rep.CountMismatch == 0,
                rep.Summary() + "\n" + string.Join("\n", why)
            );
            Assert.True(rep.FuncsymSame != false, "_FUNCSYM.DBF " + rep.FuncsymDetail);
        }
    }
}
