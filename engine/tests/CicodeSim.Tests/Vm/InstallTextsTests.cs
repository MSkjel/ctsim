using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Tests.World;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>The texts that are AVEVA's come from the install on the computer
    /// (Compiled/InstallTexts): the compiler's messages from ERRSTR.DBF, the hardware error titles
    /// from the help's error table; without them, none.</summary>
    public class InstallTextsTests
    {
        [Fact]
        public void TheCompilersMessagesAreReadFromErrstr()
        {
            using (var dir = new TempDir())
            {
                string path = Path.Combine(dir.Path, "ERRSTR.DBF");
                WorldFixture.WriteDbf(
                    path,
                    WorldFixture.F(64, "E_CODE", "E_STRING"),
                    new[]
                    {
                        WorldFixture.Row(("E_CODE", "1004"), ("E_STRING", "Wrong argument count ")),
                        WorldFixture.Row(("E_CODE", "1004"), ("E_STRING", "a second row")),
                        WorldFixture.Row(("E_CODE", "x"), ("E_STRING", "no code")),
                        WorldFixture.Row(("E_CODE", " 2041"), ("E_STRING", "Unknown name")),
                    }
                );
                var m = InstallTexts.CompilerMessages(path);
                Assert.Equal(2, m.Count);
                Assert.Equal("Wrong argument count", m[1004]); // (the first row of a code)
                Assert.Equal("Unknown name", m[2041]);
            }
        }

        [Fact]
        public void TheHardwareErrorTitlesAreReadFromTheHelpTable()
        {
            using (var dir = new TempDir())
            {
                string path = Path.Combine(dir.Path, InstallTexts.ErrorTablePage);
                File.WriteAllText(
                    path,
                    "<html><body><table>"
                        + "<tr><th>Error No.</th><th>Error title</th><th>Description</th></tr>"
                        + "<tr style=\"x\"><td><p>424</p></td><td><p>Tag  not\n found</p></td><td>d</td></tr>"
                        + "<tr><td>275</td><td>Divide &amp; conquer</td></tr>"
                        + "<tr><td>only one cell</td></tr>"
                        + "</table></body></html>"
                );
                var t = InstallTexts.RuntimeErrorTitles(path);
                Assert.Equal(2, t.Count);
                Assert.Equal("Tag not found", t[424]);
                Assert.Equal("Divide & conquer", t[275]);
            }
        }

        [Fact]
        public void WithoutTheInstallThereAreNoTexts()
        {
            using (var dir = new TempDir())
            {
                Assert.Empty(InstallTexts.CompilerMessages(Path.Combine(dir.Path, "ERRSTR.DBF")));
                Assert.Empty(InstallTexts.RuntimeErrorTitles(Path.Combine(dir.Path, "none.html")));
            }
        }
    }
}
