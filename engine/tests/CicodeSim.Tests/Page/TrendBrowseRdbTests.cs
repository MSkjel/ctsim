using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Page;
using CicodeSim.Tests.Vm;
using CicodeSim.Values;
using Xunit;
using Xunit.Abstractions;

namespace CicodeSim.Tests.Page
{
    /// <summary>The trend browse against the compiled trend table.</summary>
    [Collection("World")]
    public class TrendBrowseRdbTests
    {
        private readonly ITestOutputHelper _out;

        public TrendBrowseRdbTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>The fields the engine derives as the compiler does.</summary>
        private static readonly string[] Derived =
        {
            "CLUSTER",
            "NAME",
            "AREA",
            "PRIV",
            "SAMPLEPER",
            "TYPE",
            "ENG_UNITS",
            "FORMAT",
            "COMMENT",
            "STORMETHOD",
            "SPCFLAG",
            "DEADBAND",
            "EQUIPMENT",
            "ITEM",
            "HISTORIAN",
            "FILENAME",
            "FILES",
        };

        private static string FieldText(RdbFile f, FieldDesc fd, int rec)
        {
            int a = rec + fd.Offset;
            byte[] d = f.Data;
            switch (fd.Type & 0xffff)
            {
                case 1:
                    return f.Text(BitConverter.ToUInt32(d, a)) ?? "";
                case 2:
                    return Cp1252.DecodeZ(d, a, fd.Size);
                case 3:
                    return C(BitConverter.ToInt16(d, a));
                case 4:
                    return C(BitConverter.ToInt32(d, a));
                case 5:
                    return Printf.Format("%.7g", PrintfArg.Float(BitConverter.ToSingle(d, a)));
                case 7:
                case 8:
                    return C(d[a]);
                case 0x14:
                    return C(BitConverter.ToInt32(d, a));
                default:
                    return "";
            }
        }

        private static string C(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        [CorpusFact]
        public void TheBrowseFieldsAreTheCompiledTrendRecords()
        {
            foreach (string project in new[] { "Example", "ExampleSA" })
                Check(project);
        }

        private void Check(string project)
        {
            string dir = Path.Combine(Corpus.User, project);
            string rdb = Path.Combine(dir, "_TREND.RDB");
            Assert.True(File.Exists(rdb), rdb);
            var f = RdbFile.Open(rdb);
            var sec = f.Sections["TREND"];
            var byField = sec.Fields.ToDictionary(x => x.Name, StringComparer.Ordinal);
            var want = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, start) in f.RawRecords("TREND"))
            {
                var rec = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string name in Derived)
                    rec[name] = FieldText(f, byField[name], start);
                want[rec["NAME"]] = rec;
            }
            var a = Animator.ForProject(dir);
            var (h, _) = PageObjectTests.Call(a, "TrnBrowseOpen", "", string.Join(",", Derived), "");
            Assert.True(h.I >= 0);
            var bad = new Dictionary<string, int>(StringComparer.Ordinal);
            var example = new Dictionary<string, string>(StringComparer.Ordinal);
            int n = 0;
            for (
                long e = PageObjectTests.Call(a, "TrnBrowseFirst", h.I).v.I;
                e == 0;
                e = PageObjectTests.Call(a, "TrnBrowseNext", h.I).v.I
            )
            {
                string name = PageObjectTests.S(PageObjectTests.Call(a, "TrnBrowseGetField", h.I, "NAME").v);
                Assert.True(want.ContainsKey(name), project + ": " + name + " is not in _TREND.RDB");
                n++;
                foreach (string fld in Derived)
                {
                    string have = PageObjectTests.S(PageObjectTests.Call(a, "TrnBrowseGetField", h.I, fld).v);
                    if (have == want[name][fld])
                        continue;
                    bad[fld] = bad.TryGetValue(fld, out int c) ? c + 1 : 1;
                    if (!example.ContainsKey(fld))
                        example[fld] = name + ": '" + have + "', compiled '" + want[name][fld] + "'";
                }
            }
            Assert.Equal(want.Count, n);
            var sb = new StringBuilder();
            foreach (var kv in bad)
                sb.Append(kv.Key)
                    .Append(' ')
                    .Append(kv.Value)
                    .Append(" (")
                    .Append(example[kv.Key])
                    .Append(")\n");
            _out.WriteLine(
                project
                    + ": "
                    + n
                    + " trends; "
                    + (sb.Length == 0 ? "every derived field as compiled" : sb.ToString())
            );
            Assert.True(bad.Count == 0, project + ":\n" + sb);
        }
    }
}
