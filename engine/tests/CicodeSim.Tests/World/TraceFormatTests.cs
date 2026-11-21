using System.IO;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.World
{
    /// <summary>The text a TraceWriter writes.</summary>
    public class TraceFormatTests
    {
        private static string Q(char c) => c.ToString();

        [Fact]
        public void TraceLinesAreTheReadmesFormat()
        {
            var sw = new StringWriter { NewLine = "\n" };
            var clock = new SimClock("2026-10-01T08:00:00Z");
            var tw = new TraceWriter(
                stream: sw,
                project: "P",
                scenario: "s.json",
                engine: "test 1",
                clock: clock
            );
            string text =
                "q"
                + Q('"')
                + Q('\\')
                + Q((char)8)
                + Q((char)12)
                + Q((char)10)
                + Q((char)13)
                + Q((char)9)
                + Q((char)1)
                + Q((char)0x1f)
                + Q((char)0x7f)
                + Q((char)0xe9)
                + Q((char)0x2028);
            tw.Event(
                "tagWrite",
                0.0078125,
                null,
                ("tag", "T"),
                ("a", 0.1),
                ("b", 2.0),
                ("c", 1e16),
                ("d", 1.5e-5),
                ("e", 1e-4),
                ("f", -0.0),
                ("g", 5L),
                ("h", double.NaN),
                ("i", double.NegativeInfinity),
                ("j", text),
                ("k", 1e15),
                ("l", 9007199254740993.0),
                ("m", 123456789.125)
            );
            tw.Event("print", 2.5e-6, 3L, ("text", ""));
            tw.Event("print", 0.0000005, null);
            tw.Close();
            string[] lines = sw.ToString().Split('\n');
            Assert.Equal(5, lines.Length);
            Assert.Equal("", lines[4]); // every line ends in a newline
            // the header: format, engine, project, scenario and, with a clock, start
            Assert.Equal(
                "{\"format\":\"ctsim-trace/0\",\"engine\":\"test 1\",\"project\":\"P\",\"scenario\":\"s.json\",\"start\":1790841600.0}",
                lines[0]
            );
            // ev, t (6 decimals, half to even on the exact binary value: 0.0078125 -> 0.007812),
            // task, n, then the fields in the order set; a double in its shortest round-trip form
            // (positional for decimal exponents -4 .. 15), NaN / infinity as strings, the escapes
            string esc =
                "q"
                + "\\\""
                + "\\\\"
                + "\\b"
                + "\\f"
                + "\\n"
                + "\\r"
                + "\\t"
                + "\\u0001"
                + "\\u001f"
                + Q((char)0x7f)
                + Q((char)0xe9)
                + Q((char)0x2028);
            Assert.Equal(
                "{\"ev\":\"tagWrite\",\"t\":0.007812,\"task\":null,\"n\":1,\"tag\":\"T\",\"a\":0.1,\"b\":2.0,\"c\":1e+16,"
                    + "\"d\":1.5e-05,\"e\":0.0001,\"f\":-0.0,\"g\":5,\"h\":\"nan\",\"i\":\"-inf\",\"j\":\""
                    + esc
                    + "\","
                    + "\"k\":1000000000000000.0,\"l\":9007199254740992.0,\"m\":123456789.125}",
                lines[1]
            );
            Assert.Equal("{\"ev\":\"print\",\"t\":3e-06,\"task\":3,\"n\":2,\"text\":\"\"}", lines[2]);
            Assert.Equal("{\"ev\":\"print\",\"t\":0.0,\"task\":null,\"n\":3}", lines[3]); // 5e-7 is below 0.0000005
            Assert.Equal(3, tw.Count); // events, not the header
        }
    }
}
