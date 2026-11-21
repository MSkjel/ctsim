using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>Number formatting and parsing.</summary>
    public class NumberFormatTests
    {
        private static Dictionary<string, object?>? _data;

        private static Dictionary<string, object?> Data()
        {
            if (_data != null)
                return _data;
            string p = CicodeSim.Tests.Golden.GoldenFiles.ReadPath("number_format_and_parse_cases.json");
            _data = (Dictionary<string, object?>)Json.Parse(File.ReadAllText(p))!;
            return _data;
        }

        private static List<object?> Cases(string name) => (List<object?>)Data()[name]!;

        private static IEnumerable<Dictionary<string, object?>> CasesOf(string name)
        {
            foreach (var o in Cases(name))
                yield return (Dictionary<string, object?>)o!;
        }

        private static double D(string hex) =>
            BitConverter.Int64BitsToDouble((long)Convert.ToUInt64(hex, 16));

        private static string Hex(double x) => ((ulong)BitConverter.DoubleToInt64Bits(x)).ToString("x16");

        [Fact]
        public void FloatsReprAndFormats()
        {
            var bad = new List<string>();
            foreach (var c in CasesOf("floats"))
            {
                double x = D((string)c["b"]!);
                void Check(string what, string want, string got)
                {
                    if (want != got && bad.Count < 20)
                        bad.Add($"{what} {c["b"]}: want {want} got {got}");
                }
                Check("shortest", (string)c["shortest"]!, FloatText.Shortest(x));
                Check("%.15G", (string)c["g15"]!, Crt.FormatG15(x));
                Check("hex", (string)c["hex"]!, Crt.HexFloat(x));
                Check("%.2e", (string)c["e2"]!, Printf.Format("%.2e", PrintfArg.Float(x)));
                Check("%f", (string)c["f6"]!, Printf.Format("%f", PrintfArg.Float(x)));
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void StringsParse()
        {
            var bad = new List<string>();
            foreach (var c in CasesOf("strings"))
            {
                string s = (string)c["s"]!;
                double v = Crt.Strtod(s, out int used);
                double r = Conv.ParseReal(s, out int code);
                long i = Conv.StrToInt(s, out bool ok);
                string got = $"{Hex(v)} {used} {Hex(r)} {code} {i} {ok} {Crt.Atol(s)}";
                string want =
                    $"{c["strtod"]} {c["used"]} {c["real"]} {c["code"]} {c["int"]} {c["ok"]} {c["atol"]}";
                // a NaN by its bits too (the 32-bit UCRT's: "nan" 0x7FFFFFFFFFFFFFFF)
                if (want != got && bad.Count < 20)
                    bad.Add($"{Json.Quote(s)}: want {want} got {got}");
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void PrintfConversions()
        {
            var bad = new List<string>();
            foreach (var c in CasesOf("conversions"))
            {
                string fmt = (string)c["fmt"]!;
                string k = (string)c["k"]!;
                PrintfArg a =
                    k == "i" ? PrintfArg.Int((long)c["v"]!)
                    : k == "f" ? PrintfArg.Float(D((string)c["v"]!))
                    : PrintfArg.Str((string)c["v"]!);
                PrintfError e = Printf.TryFormat(fmt, a, out string got);
                if (c.TryGetValue("err", out var err))
                {
                    if (e == PrintfError.None && bad.Count < 20)
                        bad.Add($"{fmt} {c["v"]}: want {err} got {got}");
                }
                else if ((e != PrintfError.None || got != (string)c["out"]!) && bad.Count < 20)
                    bad.Add($"{fmt} {c["v"]}: want '{c["out"]}' got '{got}' ({e})");
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void CSprintfCases()
        {
            var bad = new List<string>();
            foreach (var c in CasesOf("sprintf"))
            {
                string fmt = (string)c["fmt"]!;
                string k = (string)c["k"]!;
                PrintfArg a =
                    k == "i" ? PrintfArg.Int((long)c["v"]!)
                    : k == "f" ? PrintfArg.Float(D((string)c["v"]!))
                    : PrintfArg.Str((string)c["v"]!);
                string got = Crt.Sprintf(fmt, a);
                if (got != (string)c["out"]! && !fmt.Contains("%p") && bad.Count < 20)
                    bad.Add($"{fmt} {c["v"]}: want '{c["out"]}' got '{got}'");
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void FormatValueCases()
        {
            var bad = new List<string>();
            foreach (var c in CasesOf("format"))
            {
                int t = (int)(long)c["t"]!;
                Value v;
                if (Types.IsRealType(t))
                    v = Value.RealOf(t, D((string)c["v"]!));
                else if (t == Types.String)
                    v = Value.Str((string)c["v"]!);
                else
                    v = Value.Int(t, Convert.ToInt64(c["v"]));
                string got = Conv.FormatValue(v, (long)c["w"]!, (string)c["suf"]!, NoErr.Instance).S!;
                if (got != (string)c["out"]! && bad.Count < 20)
                    bad.Add($"w=0x{(long)c["w"]!:x} suf={c["suf"]} {v}: want '{c["out"]}' got '{got}'");
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void Cp1252IsTheLosslessTable()
        {
            // 0x80..0x9f (the 5 undefined bytes are C1 code points)
            int[] want =
            {
                0x20ac,
                0x81,
                0x201a,
                0x192,
                0x201e,
                0x2026,
                0x2020,
                0x2021,
                0x2c6,
                0x2030,
                0x160,
                0x2039,
                0x152,
                0x8d,
                0x17d,
                0x8f,
                0x90,
                0x2018,
                0x2019,
                0x201c,
                0x201d,
                0x2022,
                0x2013,
                0x2014,
                0x2dc,
                0x2122,
                0x161,
                0x203a,
                0x153,
                0x9d,
                0x17e,
                0x178,
            };
            for (int k = 0; k < 32; k++)
                Assert.Equal(want[k], (int)Cp1252.Char((byte)(0x80 + k)));
            var all = new byte[256];
            for (int k = 0; k < 256; k++)
                all[k] = (byte)k;
            Assert.Equal(all, Cp1252.Encode(Cp1252.Decode(all)));
        }
    }
}
