using System;
using System.IO;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Tests.World;
using Xunit;
using static CicodeSim.Tests.World.WorldFixture;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>PageRowCompiler.HasRows: a project's page rows found from an index of each
    /// section table's page names, which a change of the table renews.</summary>
    public class PageRowIndexTests
    {
        [Fact]
        public void ThePageIndexFollowsTheTables()
        {
            using (var tmp = new TempDir())
            {
                string pages = Path.Combine(tmp.Path, "pages.dbf");
                WriteDbf(pages, F(16, "NAME"), new[] { Row(("NAME", "Mix")) });
                Assert.True(PageRowCompiler.HasRows(tmp.Path, " mix "));
                Assert.False(PageRowCompiler.HasRows(tmp.Path, "Pump"));
                // a page with rows of a section only
                WriteDbf(
                    Path.Combine(tmp.Path, "PGTEXT.DBF"),
                    F(16, "PAGE", "AN"),
                    new[] { Row(("PAGE", "Pump")) }
                );
                Assert.True(PageRowCompiler.HasRows(tmp.Path, "Pump"));
                // a rename of the same length: the time tells
                var time = File.GetLastWriteTimeUtc(pages);
                WriteDbf(pages, F(16, "NAME"), new[] { Row(("NAME", "Mux")) });
                File.SetLastWriteTimeUtc(pages, time.AddSeconds(2));
                Assert.False(PageRowCompiler.HasRows(tmp.Path, "Mix"));
                Assert.True(PageRowCompiler.HasRows(tmp.Path, "Mux"));
                // no folder
                Assert.False(PageRowCompiler.HasRows(Path.Combine(tmp.Path, "none"), "Mux"));
            }
        }
    }
}
