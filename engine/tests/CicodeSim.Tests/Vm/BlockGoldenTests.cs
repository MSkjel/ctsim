using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>2,000 random p-code blocks, frozen.</summary>
    public class BlockGoldenTests
    {
        [Fact]
        public void BlocksGiveTheirFrozenResults()
        {
            string p = CicodeSim.Tests.Golden.GoldenFiles.ReadPath("vm_block_cases.json");
            string raw = File.ReadAllText(p);
            var data = (Dictionary<string, object?>)Json.Parse(raw)!;
            var reg = Registry.Default();
            var bad = new List<string>();
            var now = new List<string?>();
            int n = 0,
                differ = 0;
            foreach (var o in (List<object?>)data["cases"]!)
            {
                var c = (Dictionary<string, object?>)o!;
                string want = (string)c["want"]!;
                string got = BlockCases.Run(c, reg);
                n++;
                now.Add(want != got ? got : null);
                if (want != got)
                {
                    differ++;
                    if (bad.Count < 10)
                        bad.Add($"{c["kind"]} {c["code"]}\n  want {want}\n  got  {got}");
                }
            }
            if (CicodeSim.Tests.Golden.GoldenFiles.Writing && differ > 0)
            {
                File.WriteAllText(p, Rewrite(raw, now), new UTF8Encoding(false));
                Assert.Fail($"CICODESIM_GOLDEN_WRITE=1: rewrote {differ} want texts of {p} (not compared)");
            }
            Assert.Equal(2000, n);
            Assert.True(bad.Count == 0, differ + " differ:\n" + string.Join("\n", bad));
        }

        /// <summary>The file with the i-th `"want":"..."` string replaced by now[i] where set (the
        /// result texts are ASCII JSON, so only '\' and '"' are escaped in the file).</summary>
        private static string Rewrite(string raw, List<string?> now)
        {
            const string key = "\"want\":\"";
            var sb = new StringBuilder(raw.Length + 4096);
            int pos = 0,
                k = 0;
            while (true)
            {
                int at = raw.IndexOf(key, pos, System.StringComparison.Ordinal);
                if (at < 0)
                    break;
                int s = at + key.Length;
                int e = s;
                while (raw[e] != '"')
                    e += raw[e] == '\\' ? 2 : 1;
                sb.Append(raw, pos, s - pos);
                if (k < now.Count && now[k] != null)
                    sb.Append(now[k]!.Replace("\\", "\\\\").Replace("\"", "\\\""));
                else
                    sb.Append(raw, s, e - s);
                pos = e;
                k++;
            }
            sb.Append(raw, pos, raw.Length - pos);
            return sb.ToString();
        }
    }
}
