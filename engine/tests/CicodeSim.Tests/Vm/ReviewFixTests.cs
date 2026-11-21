using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>Cases where net48 and net10.0 can differ; each expected value is the settled one
    /// (correctly rounded numbers, the Universal CRT's maths).</summary>
    public class ReviewFixTests
    {
        private static ulong Bits(double x) => unchecked((ulong)BitConverter.DoubleToInt64Bits(x));

        /// <summary>Run name(args) through the Func opcode with ErrSet(1) first.</summary>
        private static (Value Result, CicodeTask Task) Call(string name, Registry reg, params object[] args)
        {
            var pb = new ProgramBuilder();
            var b = pb.Block();
            b.ConstInt(1).Func("ErrSet", 1);
            foreach (object a in args)
            {
                if (a is string s)
                    b.ConstStr(s).Convert(Types.String);
                else if (a is double d)
                    b.ConstReal(d).Convert(Types.Real);
                else
                    b.ConstInt(System.Convert.ToInt64(a, CultureInfo.InvariantCulture)).Convert(Types.Long);
            }
            b.Func(name, args.Length).End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var m = new CicodeVm(prog, reg, null, new ManualClock());
            var task = m.TaskForBlock(code, prog, false);
            m.Run(task);
            Assert.True(task.State == TaskState.Stop, task.StateName + " " + task.HaltReason);
            return (task.Result ?? default, task);
        }

        // ------------------------------------------------------------------ TIMESTAMP text
        [Theory]
        [InlineData(2650467743999999990UL, "9999-12-31 23:59:59.999")]
        [InlineData(2650467743999999999UL, "9999-12-31 23:59:59.999")]
        [InlineData(2650467744000000000UL, "")]
        [InlineData(2929709466567259522UL, "")]
        [InlineData(3104887852799999990UL, "")]
        [InlineData(18446744073709551615UL, "")]
        [InlineData(133000000000012345UL, "2022-06-18 04:26:40.001")]
        [InlineData(1UL, "1601-01-01 00:00:00.000")]
        public void TimestampTextPastYear9999IsEmpty(ulong ft, string want)
        {
            // 1601-01-01 + whole microseconds; past year 9999 ''
            Assert.Equal(want, Conv.DefaultTimestampToStr(ft));
        }

        [Fact]
        public void FarFutureTimestampToStringRunsInTheVm()
        {
            var t = VmTestUtil.Block(b =>
                b.ConstSub(Types.Timestamp, BitConverter.GetBytes(2929709466567259522UL))
                    .Convert(Types.String)
            );
            Assert.Equal(TaskState.Stop, t.State);
            Assert.Equal("", t.Result!.Value.S);
        }

        // ------------------------------------------------------------------ CRT maths (net48)
        [Theory]
        [InlineData(24476.0, 0x3fc2ebc78fe1da92UL, 0xbfefa601252a2228UL, 0xbfc32195549b1f0bUL)]
        [InlineData(34168732.0, 0xbfe2509d1a151b4bUL, 0xbfea3d924880e153UL, 0x3fe655b5aee2771fUL)]
        [InlineData(502794841.0, 0x3fee05999534e377UL, 0xbfd6272044da2e65UL, 0xc005aef6d1b4f3cfUL)]
        [InlineData(999999999.5, 0x3fb3cb1fcc865abeUL, 0x3fefe77a1c40324bUL, 0x3fb3da569e40e26dUL)]
        [InlineData(-987654321.123, 0xbfefac1db6469fc5UL, 0xbfc2454b78170b6cUL, 0x401bbc7037de9a4eUL)]
        [InlineData(100000000.1, 0x3fec80ab6bee37f2UL, 0xbfdd17cfea33d76cUL, 0xbfff59c15713af09UL)]
        [InlineData(123456.789, 0xbfeff50e60ab53f9UL, 0x3faa74d27c41b22aUL, 0xc03353a85fe8d6d5UL)]
        public void TrigIsTheUcrts(double x, ulong sin, ulong cos, ulong tan)
        {
            // sin / cos / tan are the UCRT's; .NET Framework's Math.Sin differs for
            // about half of all arguments
            var reg = Registry.Default();
            Assert.Equal(sin, Bits(Call("Sin", reg, x).Result.D));
            Assert.Equal(cos, Bits(Call("Cos", reg, x).Result.D));
            Assert.Equal(tan, Bits(Call("Tan", reg, x).Result.D));
            Assert.Equal(sin, Bits(CMath.Sin(x)));
            Assert.Equal(cos, Bits(CMath.Cos(x)));
            Assert.Equal(tan, Bits(CMath.Tan(x)));
        }

        [Fact]
        public void OtherCrtFunctionsAreTheUcrts()
        {
            Assert.Equal(0x3fd380159e14f6ffUL, Bits(CMath.Asin(0.3)));
            Assert.Equal(0x3ff441f5ecbeef59UL, Bits(CMath.Acos(0.3)));
            Assert.Equal(0x3ff921f7d57b40f2UL, Bits(CMath.Atan(0.3e6)));
            Assert.Equal(0x400b7e47790da685UL, Bits(CMath.Exp(1.2345)));
            Assert.Equal(0x00c4ff475c68ca02UL, Bits(CMath.Exp(-700.5)));
            Assert.Equal(0x3fcaf71abe30f48fUL, Bits(CMath.Log(1.2345)));
            Assert.Equal(0x4022d79559791e31UL, Bits(CMath.Log(12345.678)));
            Assert.Equal(0x3fb76bf5d9c18ff5UL, Bits(CMath.Log10(1.2345)));
            Assert.Equal(0x3ff028a79ddfb81bUL, Bits(CMath.Pow(1.0000001, 98765.4321)));
            Assert.Equal(0xc1da727064cdd0e0UL, Bits(CMath.Pow(-3.5, 17.0)));
            Assert.Equal(0x3f0d3ff1ad305d66UL, Bits(CMath.Pow(77.7, -2.25)));
#if NETFRAMEWORK
            Assert.True(CMath.UsesUcrt, "net48 on Windows calls ucrtbase.dll");
#endif
        }

        // ------------------------------------------------------------------ unsigned 64-bit ints
        [Fact]
        public void SprintfOfATimestampOrQualityIsUnsigned()
        {
            // the payload is unsigned: 2^64 - 1 as a double
            Assert.Equal("1.844674e+19", Crt.Sprintf("%e", PrintfArg.UInt64(ulong.MaxValue)));
            Assert.Equal("0x1.0000000000000p+63", Crt.Sprintf("%a", PrintfArg.UInt64(1UL << 63)));
            Assert.Equal("-1.000000e+00", Crt.Sprintf("%e", PrintfArg.Int(-1)));
            // the low 32 bits for the int conversions
            Assert.Equal("-1", Crt.Sprintf("%d", PrintfArg.UInt64(ulong.MaxValue)));
            Assert.Equal("ffffffff", Crt.Sprintf("%x", PrintfArg.UInt64(ulong.MaxValue)));
        }

        [Theory]
        [InlineData(Types.Quality, 0xFFFFFFFFFFFFFFFFUL, "%10.4G", " 1.845E+19")]
        [InlineData(Types.Quality, 0x8000000000000000UL, "%.0e", "9e+18")]
        [InlineData(Types.Timestamp, 0xFFFFFFFFFFFFFFFFUL, "%E", "1.844674E+19")]
        [InlineData(Types.Timestamp, 0x8000000000000000UL, "%A", "0x1.0000000000000p+63")]
        [InlineData(Types.Quality, 0xd58d668ac6dcd00bUL, "%.0e", "2e+19")]
        public void StringOpcodeFormatsTheUnsignedPayload(int t, ulong v, string fmt, string want)
        {
            var r = VmTestUtil.ValueOf(b =>
                b.ConstSub(t, BitConverter.GetBytes(v)).ConstStr(fmt).Op("String")
            );
            Assert.Equal(want, r.S);
        }

        [Fact]
        public void BuiltinResultOfAQualityPayloadIsUnsigned()
        {
            // the result as text / as a REAL of the unsigned payload
            var reg = Registry.Default(
                new Dictionary<string, BuiltinImpl>
                {
                    ["IntToStr"] = (t, a) => Value.Int(Types.Quality, -1),
                    ["IntToReal"] = (t, a) =>
                        Value.Int(Types.Timestamp, unchecked((long)0x8000000000000000UL)),
                }
            );
            Assert.Equal("18446744073709551615", Call("IntToStr", reg, 5).Result.S);
            Assert.Equal(9223372036854775808.0, Call("IntToReal", reg, 5).Result.D);
        }

        [Theory]
        [InlineData(0xd37e4ea402632bc1UL, "0x1.a6fc9d4804c65p+63", "15239704655389992960.00")]
        [InlineData(0xd101c24c0680eb8cUL, "0x1.a20384980d01dp+63", "15060532260686063616.00")]
        [InlineData(0xbed87c0fc7d38404UL, "0x1.7db0f81f8fa71p+63", "13751877869394692096.00")]
        [InlineData(0xfffffffffffffc00UL, "0x1.0000000000000p+64", "18446744073709551616.00")]
        [InlineData(0xfffffffffffffbffUL, "0x1.fffffffffffffp+63", "18446744073709549568.00")]
        [InlineData(0x8000000000000401UL, "0x1.0000000000001p+63", "9223372036854777856.00")]
        [InlineData(0x8000000000000400UL, "0x1.0000000000000p+63", "9223372036854775808.00")]
        [InlineData(0x8000000000000c00UL, "0x1.0000000000002p+63", "9223372036854779904.00")]
        [InlineData(0x7fffffffffffffffUL, "0x1.0000000000000p+63", "9223372036854775808.00")]
        public void UnsignedToDoubleRoundsOnceOnBothFrameworks(ulong v, string hex, string fixed2)
        {
            // the nearest double, half to even, rounded once; .NET Framework's (double)ulong rounds
            // twice above 2^63 (the last digit of every second case here)
            Assert.Equal(hex, Crt.HexFloat(FloatText.FromUInt64(v)));
            Assert.Equal(hex, Crt.Sprintf("%a", PrintfArg.UInt64(v)));
            Assert.Equal(fixed2, Crt.Sprintf("%.2f", PrintfArg.UInt64(v)));
        }

        // ------------------------------------------------------------------ host values
        [Fact]
        public void ValueOfOtherIntegerTypesIsAnInt()
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(Value.Long(-1294967296), CicodeVm.ValueOf(3000000000u));
                Assert.Equal(Value.Long(-1), CicodeVm.ValueOf(ulong.MaxValue));
                Assert.Equal(Value.Long(65535), CicodeVm.ValueOf((ushort)65535));
                Assert.Equal(Value.Long(-5), CicodeVm.ValueOf((sbyte)-5));
                Assert.Equal(Value.Real(2.5), CicodeVm.ValueOf(2.5m));
                // any other object: its invariant text, never "1,5"
                Assert.Equal("1.5", CicodeVm.ValueOf(new Wrapped()).S);
            }
            finally
            {
                CultureInfo.CurrentCulture = old;
            }
        }

        private sealed class Wrapped : IFormattable
        {
            public string ToString(string? format, IFormatProvider? provider) => 1.5.ToString(provider);

            public override string ToString() => ToString(null, CultureInfo.CurrentCulture);
        }

        // ------------------------------------------------------------------ [Code] parameters
        [Theory]
        [InlineData("Stack", "4294967296", 1024)] // the whole number clamped, never wrapped to 0
        [InlineData("Stack", "-4294967296", 8)]
        [InlineData("Stack", "18446744073709551616", 1024)]
        [InlineData("Stack", " 100 ", 100)]
        [InlineData("Stack", "1_000", 256)] // no integer: the default
        [InlineData("TimeSlice", "99999999999999999999", 5000)]
        [InlineData("Threads", "1e3", 128)] // no integer: the default
        public void CodeParametersClampTheWholeNumber(string key, string text, int want)
        {
            var c = CodeConfig.FromParams((sec, k, d) => k == key ? text : d);
            int got =
                key == "Stack" ? c.Stack
                : key == "TimeSlice" ? c.TimeSlice
                : c.Threads;
            Assert.Equal(want, got);
        }

        [Fact]
        public void CodeParameterFlagsTestTheWholeNumber()
        {
            // the whole number 4294967296 is nonzero (its low 32 bits are 0)
            var c = CodeConfig.FromParams(
                (sec, k, d) =>
                    k == "FullTimeSlice" || k == "HaltOnError"
                        ? (k == "HaltOnError" ? "8589934592" : "4294967296")
                        : d
            );
            Assert.True(c.FullTimeSlice);
            Assert.True(c.HaltOnError);
        }
    }
}
