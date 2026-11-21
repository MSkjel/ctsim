using System.IO;
using System.Linq;
using CicodeSim.Sim;
using CicodeSim.Tests.Compiler;
using CicodeSim.Tests.Golden;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>ctsim/usedTags (Simulation.UsedTagsJson): the names a window's code used, with
    /// the function, file and line that first used each one, names built at run
    /// time and names no tag has included. A project compiled from source in a temporary folder.</summary>
    public class UsedTagsTests
    {
        private const string Code =
            "STRING FUNCTION Used_Base(STRING s)\r\n"
            + // 1
            "\tRETURN StrLeft(s, StrLength(s) - 4);\r\n"
            + // 2
            "END\r\n"
            + // 3
            "\r\n"
            + // 4
            "INT FUNCTION Used_Probe(STRING sTag)\r\n"
            + // 5
            "\tREAL r;\r\n"
            + // 6
            "\tr = TagGetValue(Used_Base(sTag) + \"_LimitLow\");\r\n"
            + // 7
            "\tTagGetProperty(sTag, \"FullName\");\r\n"
            + // 8
            "\tRETURN 1;\r\n"
            + // 9
            "END\r\n"; // 10

        [GoldenFact]
        public void TheFunctionFileAndLineThatFirstUsedEachName()
        {
            string dir = CodegenReloadTests.NewProject(("used.ci", Code));
            try
            {
                using (var sim = SimTestUtil.Start(dir, compile: CodeSource.All))
                {
                    var task = sim.Invoke(() => sim.StartCall("Used_Probe(\"Tank_HMI\")", null, window: 0));
                    SimTestUtil.Until(sim, () => task.Done);
                    var b = sim.Invoke(() => sim.UsedTagsJson(0, 0));
                    var names = b["names"].Items.Select(n => n.Str).ToList();
                    var by = b["by"].Items;
                    Assert.Equal(names.Count, by.Count);
                    // the name built at run time, and one no tag has (424): both count
                    int i = names.IndexOf("Tank_LimitLow");
                    Assert.True(i >= 0, string.Join(", ", names));
                    Assert.Equal("Used_Probe", by[i]["function"].Str);
                    Assert.Equal("used.ci", Path.GetFileName(by[i]["file"].Str), ignoreCase: true);
                    Assert.Equal(7, by[i]["line"].Long);
                    int j = names.IndexOf("Tank_HMI");
                    Assert.True(j >= 0, string.Join(", ", names));
                    Assert.Equal(8, by[j]["line"].Long);
                    // from: only the newer ones, by in step
                    var later = sim.Invoke(() => sim.UsedTagsJson(0, 1));
                    Assert.Equal(names.Skip(1), later["names"].Items.Select(n => n.Str));
                    Assert.Equal(names.Count - 1, later["by"].Items.Count);
                }
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }
    }
}
