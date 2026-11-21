using System;
using System.IO;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Host;
using CicodeSim.Tests.Golden;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>ctsim-dev compile writes _LIBRARY.RDB and _FUNCSYM.DBF to its out folder and refuses a
    /// project's folder (CtCmp32's own files are there).</summary>
    public class CompileCommandTests
    {
        [GoldenFact]
        [Trait("Golden", "light")]
        public void CompileNeverWritesIntoAProjectFolder()
        {
            string dir = CodegenReloadTests.NewProject(
                ("a.ci", CodegenReloadTests.A),
                ("b.ci", CodegenReloadTests.B)
            );
            string outDir = Path.Combine(Path.GetDirectoryName(dir)!, "out");
            try
            {
                Assert.Equal(2, CompilerCommands.Dispatch(new[] { "compile", dir, "--out", dir }));
                Assert.False(File.Exists(Path.Combine(dir, "_LIBRARY.RDB")));
                Assert.False(File.Exists(Path.Combine(dir, "_FUNCSYM.DBF")));
                Assert.Equal(0, CompilerCommands.Dispatch(new[] { "compile", dir, "--out", outDir }));
                Assert.True(File.Exists(Path.Combine(outDir, "_LIBRARY.RDB")));
                Assert.True(File.Exists(Path.Combine(outDir, "_FUNCSYM.DBF")));
                // a folder that holds a project's files is refused too (only asked: a test never
                // writes near a live project)
                var unit = Units.ForProject(dir);
                Assert.Equal(
                    "Example",
                    CompilerCommands.ProjectFolderAt(Path.Combine(GoldenFiles.User, "Example"), unit)
                );
                Assert.Null(CompilerCommands.ProjectFolderAt(outDir, unit));
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }
    }
}
