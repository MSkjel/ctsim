using System;
using System.Collections.Generic;
using CicodeSim.Compiler.Codegen;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>Numbers the code generator reads from text or converts give the same result on
    /// .NET Framework and .NET (Core): an INT initialiser from a REAL, the scale fields and a
    /// shortform FORMAT of a tag (no corpus case for any of the out-of-range ones).</summary>
    public class CodegenNumbersTests
    {
        [Fact]
        public void ConversionsDoNotDependOnTheFramework()
        {
            Assert.Equal(-3, LibraryBuilder.Int32Of(-3.7));
            Assert.Equal(2147483647, LibraryBuilder.Int32Of(2147483647.9));
            Assert.Equal(int.MinValue, LibraryBuilder.Int32Of(-2147483648.9));
            Assert.Equal(int.MinValue, LibraryBuilder.Int32Of(2147483648.0));
            Assert.Equal(int.MinValue, LibraryBuilder.Int32Of(-1e300));
            Assert.Equal(int.MinValue, LibraryBuilder.Int32Of(double.NaN));
            Assert.Equal(double.PositiveInfinity, TagRow.Num("1e400", 0));
            Assert.Equal(12.5, TagRow.Num(" 12.5 ", 0));
            Assert.Equal(7, TagRow.Num("12abc", 7));
            Assert.Equal(FormatWords.OfTagFormat("1e300", out _), FormatWords.OfTagFormat("1e301", out _));
            Assert.Equal(0x209u, FormatWords.OfTagFormat("6.2", out _));
        }

        /// <summary>A REAL initialiser of an INT is E2006 (so the RUN image's REAL-to-INT case is
        /// only met by a unit with errors).</summary>
        [Fact]
        public void ARealInitialiserOfAnIntIsAnError()
        {
            const string src =
                "MODULE INT mBig = 30000000000.0;\r\nINT FUNCTION Big()\r\n\tRETURN mBig;\r\nEND\r\n";
            var unit = Units
                .ForTexts(
                    "P",
                    new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("n.ci", src) }
                )
                .Compile();
            Assert.Equal("E2006", Assert.Single(unit.Errors()).Name);
        }
    }
}
