using System.Collections.Generic;
using System.Text;
using CicodeSim.Compiler.Codegen;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>Code the runtime cannot hold is not compiled (listed as not compiled, its body an
    /// End) instead of written with a wrong jump or length: a jump beyond the i16 the runtime
    /// reads, a function longer than the CODE pool's u16 length word.</summary>
    public class CodegenLimitsTests
    {
        private static string Statements(int n)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < n; k++)
                sb.Append("\ti = i + 1;\r\n");
            return sb.ToString();
        }

        [Fact]
        public void TooBigCodeIsNotCompiled()
        {
            // 14 bytes a statement: a loop body of 2,500 (35 KB) jumps too far; 5,000 statements
            // without a jump (70 KB) are too long for the length word
            string src =
                "INT FUNCTION Small()\r\n\tRETURN 1;\r\nEND\r\n"
                + "INT FUNCTION FarJump()\r\n\tINT i = 0;\r\n\tWHILE i < 1 DO\r\n"
                + Statements(2500)
                + "\tEND\r\n\tRETURN i;\r\nEND\r\n"
                + "INT FUNCTION Long()\r\n\tINT i = 0;\r\n"
                + Statements(5000)
                + "\tRETURN i;\r\nEND\r\n"
                + "INT FUNCTION Near()\r\n\tINT i = 0;\r\n\tWHILE i < 1 DO\r\n"
                + Statements(2000)
                + "\tEND\r\n\tRETURN i;\r\nEND\r\n";
            var unit = Units
                .ForTexts(
                    "P",
                    new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("big.ci", src) }
                )
                .Compile();
            Assert.Empty(unit.Errors());
            var img = LibraryBuilder.Build(unit);
            var failed = new HashSet<string>();
            foreach (var (name, why) in img.Failed)
                failed.Add(name);
            Assert.Equal(new HashSet<string> { "FarJump", "Long" }, failed);
            var prog = img.ToProgram();
            Assert.Equal(new byte[] { 0xFF }, prog.Function("FarJump")!.Code);
            Assert.Equal(new byte[] { 0xFF }, prog.Function("Long")!.Code);
            Assert.True(prog.Function("Near")!.Code.Length > 28000);
            Assert.Equal("1", CodegenReloadTests.Run(prog, "Small"));
            Assert.Equal("2000", CodegenReloadTests.Run(prog, "Near"));
            // the written file reads back
            var back = CicodeSim.Compiled.Rdb.LibraryFromRdb(
                CicodeSim.Compiled.RdbFile.FromBytes(RdbWriter.Write(img, RdbWriter.Stamp.Now()), "big")
            );
            Assert.Equal(prog.Function("Near")!.Code, back.Function("Near")!.Code);
        }
    }
}
