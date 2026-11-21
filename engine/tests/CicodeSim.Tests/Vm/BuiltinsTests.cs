using System;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>The pure and interpreter-state built-ins, called through the VM's Func opcode.</summary>
    public class BuiltinsTests
    {
        /// <summary>Run name(args) as compiled code would: constants, Convert to the
        /// signature's types, Func. Returns (result, task).</summary>
        private static (Value Result, CicodeTask Task) Call(string name, object[] args, bool errset = true)
        {
            var info = Func0Table.Default.Lookup(name)!;
            var pb = new ProgramBuilder();
            var b = pb.Block();
            if (errset)
                b.ConstInt(1).Func("ErrSet", 1);
            for (int k = 0; k < args.Length; k++)
            {
                object a = args[k];
                if (a is string s)
                    b.ConstStr(s);
                else if (a is double d)
                    b.ConstReal(d);
                else
                    b.ConstInt(Convert.ToInt64(a));
                int t = k < info.Args.Length ? Types.SigType(info.Args[k]) : Types.Long;
                if (t == Types.Int16 || t == Types.Long)
                    b.Convert(Types.Long);
                else if (t == Types.Real || t == Types.String)
                    b.Convert(t);
            }
            b.Func(name, args.Length).End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var m = new CicodeVm(prog, Registry.Default(), null, new ManualClock());
            var task = m.TaskForBlock(code, prog, false);
            m.Run(task);
            return (task.Result ?? default, task);
        }

        private static long Vi(string name, params object[] args) => Call(name, args).Result.I;

        private static string Vs(string name, params object[] args) => Call(name, args).Result.S!;

        private static double Vr(string name, params object[] args) => Call(name, args).Result.D;

        private static int Err(string name, params object[] args) => Call(name, args).Task.Error;

        // ------------------------------------------------------------------ live facts
        [Fact]
        public void LiveRecordedResults()
        {
            // header (the live recording's smoke runs)
            Assert.Equal(12, Vi("StrToInt", "12abc"));
            Assert.Equal(0, Vi("StrToInt", "abc"));
            Assert.Equal(0.0, Vr("StrToReal", "abc"));
            Assert.Equal(2147483647, Vi("StrToInt", "2147483648"));
            Assert.Equal("    1.5000", Vs("RealToStr", 1.5, 10, 4));
            Assert.Equal(0, Vi("IsError"));
        }

        // ------------------------------------------------------------------ strings
        [Fact]
        public void StrLeftRightMid()
        {
            Assert.Equal("abc", Vs("StrLeft", "abcdef", 3));
            Assert.True(Vs("StrLeft", "abc", 10) == "abc" && Err("StrLeft", "abc", 10) == 0);
            Assert.True(Vs("StrLeft", "abc", -1) == "" && Err("StrLeft", "abc", -1) == 257);
            Assert.Equal("ef", Vs("StrRight", "abcdef", 2));
            Assert.Equal("abc", Vs("StrRight", "abc", 10));
            Assert.True(Vs("StrMid", "abcdef", 2, 3) == "cde" && Err("StrMid", "abcdef", 2, 3) == 0);
            // past the end: the part that exists, and 257 without the halt flag
            var (r, t) = Call("StrMid", new object[] { "abc", 1, 10 }, errset: false);
            Assert.True(r.S == "bc" && t.Error == 257 && t.State == TaskState.Stop);
            Assert.Equal("", Vs("StrMid", "abc", 5, 1));
        }

        [Fact]
        public void StrSearchAndLength()
        {
            Assert.Equal(4, Vi("StrSearch", 0, "hello world", "o"));
            Assert.Equal(7, Vi("StrSearch", 5, "hello world", "o"));
            Assert.Equal(-1, Vi("StrSearch", 0, "abc", "z")); // 0xffff, sign-extended
            Assert.Equal(-1, Vi("StrSearch", 3, "abc", "")); // offset not < length
            Assert.Equal(3, Vi("StrLength", "abc"));
        }

        [Fact]
        public void StrToIntErrorsWithoutHalt()
        {
            var (r, t) = Call("StrToInt", new object[] { "x1" }, errset: false);
            Assert.True(r.I == 0 && t.Error == 274 && t.State == TaskState.Stop);
        }

        [Fact]
        public void StrToRealRaises274()
        {
            var (r, t) = Call("StrToReal", new object[] { "1.5x" }, errset: false);
            Assert.True(r.D == 1.5 && t.Error == 274 && t.State == TaskState.Stop);
        }

        [Fact]
        public void CaseTrimPadFill()
        {
            Assert.Equal("ABC\u00C6", Vs("StrUpper", "abc\u00C6")); // C locale: ASCII only
            Assert.Equal("abc", Vs("StrLower", "ABC"));
            Assert.Equal("a b", Vs("StrTrim", "  a b \t"));
            Assert.Equal("ab---", Vs("StrPad", "ab", "-", 5));
            Assert.Equal("xyxab", Vs("StrPad", "ab", "xy", -5));
            Assert.Equal("abc", Vs("StrPad", "abcdef", " ", 3));
            Assert.Equal("ababa", Vs("StrFill", "ab", 5));
            Assert.Equal("", Vs("StrFill", "x", 300));
        }

        [Fact]
        public void CharFunctions()
        {
            Assert.Equal("A", Vs("CharToStr", 65));
            Assert.Equal(65, Vi("StrToChar", "Abc"));
            Assert.Equal(0, Vi("StrToChar", ""));
            Assert.Equal("00ff", Vs("HexToStr", 255, 4));
            Assert.Equal("ffffffff", Vs("HexToStr", -1, 0));
            Assert.Equal("abc", Vs("StrClean", "a\tb\u0001c"));
        }

        [Fact]
        public void StrReplaceAndTerminators()
        {
            Assert.Equal("a+b+c", Vs("StrReplace", "a-b-c", "-", "+"));
            Assert.Equal("dog concat", Vs("StrReplace", "cat concat", "cat", "dog", " "));
            Assert.Equal("abc", Vs("StrReplace", "abc", "", "x"));
        }

        [Fact]
        public void StrToLines()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("Lines", "7", Types.String).Begin();
            f.StackVar(Types.Long);
            f.LoadStack(0)
                .ConstInt(10)
                .Convert(Types.Long)
                .AddrStack(1)
                .Func("StrToLines", 3)
                .ConstStr("|")
                .Op("Add")
                .LoadStack(1)
                .Convert(Types.String)
                .Op("Add")
                .RetValue()
                .End();
            var m = new CicodeVm(pb.Build(), Registry.Default(), null, new ManualClock());
            Assert.Equal("the quick\nbrown fox|2", m.Call("Lines", "the quick brown fox")!.Value.S);
        }

        [Fact]
        public void RealToStrRules()
        {
            Assert.Equal("  1,25", Vs("RealToStr", 1.25, 6, 2, ","));
            Assert.Equal("", Vs("RealToStr", 1.25, 30, 2)); // width > 29: 257
            Assert.Equal(" 2.5", Vs("RealToStr", 2.5, 4, -1)); // negative places: as printed
        }

        [Fact]
        public void LibControlStringArrays()
        {
            const string data = "#3  1 22333";
            Assert.Equal(3, Vi("_LibControl_StrArray_GetLen", data));
            Assert.Equal(22, Vi("_LibControl_StrArray_GetInt", data, 2, 0));
            Assert.Equal(-1, Vi("_LibControl_StrArray_GetInt", data, 9, -1));
            Assert.Equal("#3  1  7333", Vs("_LibControl_StrArray_SetInt", data, 2, 7));
            Assert.Equal("#20000 5", Vs("_LibControl_StrArray_SetInt", "#2", 3, 5));
            Assert.Equal("#2aabb0000", Vs("_LibControl_StrArray_SetLen", "#2aabb", 4, 0));
            Assert.Equal("#2aa", Vs("_LibControl_StrArray_SetLen", "#2aabbcc", 1, 1));
            Assert.Equal("Foo(1)", Vs("_LibControl_StrReplace", "Foo(#A)", "#a", "1"));
            Assert.Equal("F(#x,2)", Vs("_LibControl_SetCallbackArg", "F(#x,#y)", "y", "2"));
        }

        // ------------------------------------------------------------------ maths
        [Fact]
        public void RoundTruncatesNegatives()
        {
            // trunc, + 1 when the rest >= 0.5; a negative rest never is
            Assert.Equal(3.0, Vr("Round", 2.5, 0));
            Assert.Equal(-2.0, Vr("Round", -2.5, 0));
            Assert.Equal(-2.0, Vr("Round", -2.6, 0));
            Assert.Equal(2.35, Vr("Round", 2.346, 2));
            Assert.Equal(1200.0, Vr("Round", 1234.0, -2));
        }

        [Fact]
        public void MathsDomains()
        {
            Assert.Equal(3.0, Vr("Sqrt", 9.0));
            var (r, t) = Call("Sqrt", new object[] { -1.0 }, errset: false);
            Assert.True(r.D == 0.0 && t.Error == 274 && t.State == TaskState.Stop);
            Assert.True(Vr("Exp", 100.0) == 0.0 && Err("Exp", 100.0) == 274);
            Assert.True(Vr("Ln", 0.0) == 0.0 && Err("Ln", 0.0) == 274);
            Assert.Equal(0.0, Vr("ArcSin", 2.0));
            Assert.Equal(120, Vi("Fact", 5));
            Assert.Equal(unchecked((int)6227020800L), Vi("Fact", 13));
            Assert.True(Vr("Max", 1.0, 2.0) == 2.0 && Vr("Min", 1.0, 2.0) == 1.0);
            Assert.Equal(3.5, Vr("Abs", -3.5));
            Assert.Equal(-1.0, Vr("Sign", -0.1));
            Assert.Equal(3.141592653589793, Vr("Pi"));
            Assert.True(Vi("HighWord", -1) == 0xFFFF && Vi("LowByte", 0x1234) == 0x34);
        }

        [Fact]
        public void QualityFunctions()
        {
            Assert.Equal(0xC0, Vi("QualityCreate", 3, 0, 0, 0, 0, 0, 0));
            long q = Vi("QualityCreate", 1, 2, 1, 5, 1, 1, 9);
            Assert.Equal((9L << 32) | 0x30000 | (5 << 8) | 0x40 | (2 << 2) | 1, q);
        }

        // ------------------------------------------------------------------ state
        [Fact]
        public void ErrSetIsErrorLevels()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("Probe", "", Types.Long);
            f.ConstInt(1).Func("ErrSet", 1).ConstReal(-1.0).Func("Sqrt", 1).Op("Pop");
            f.Func("IsError", 0); // 274, then cleared
            f.Func("IsError", 0);
            f.Op("Add");
            f.ConstInt(0).Func("ErrSet", 1); // back to level 0
            f.ConstInt(5).Func("ErrSetLevel", 1); // returns the old level, 0
            f.Op("Add").RetValue().End();
            var m = new CicodeVm(pb.Build(), Registry.Default(), null, new ManualClock());
            Assert.Equal(274, m.Call("Probe")!.Value.I);
        }

        [Fact]
        public void CodeSetModeReturnsPrevious()
        {
            Assert.Equal(1, Vi("CodeSetMode", 3, 0));
            var (_, t) = Call("CodeSetMode", new object[] { 3, 0 });
            Assert.False(t.IgnoreCaseFlag);
        }

        [Fact]
        public void GlobalArrays()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("G", "", Types.String);
            f.ConstInt(3).Convert(Types.Long).ConstStr("hi").Func("SetGlbStr", 2);
            f.ConstInt(3).Convert(Types.Long).Func("GetGlbStr", 1).RetValue().End();
            var m = new CicodeVm(pb.Build(), Registry.Default(), null, new ManualClock());
            Assert.Equal("hi", m.Call("G")!.Value.S);
            Assert.True(Vi("GetGlbInt", 9999) == 0 && Err("GetGlbInt", 9999) == 257);
        }

        [Fact]
        public void HaltBuiltin()
        {
            var (_, t) = Call("Halt", new object[0]);
            Assert.True(t.State == TaskState.Halt && t.HaltReason == "Halt()");
        }

        [Fact]
        public void RegistryNamesAndNotSimulated()
        {
            var reg = Registry.Default();
            Assert.True(reg.IsImplemented("StrMid") && !reg.IsImplemented("TagRead"));
            var r = reg.Call(reg.Table.Lookup("TagRead")!.Index, null!, ArgList.Of());
            Assert.True(r.IsNotSimulated && ((string)r.O!).Contains("TagRead"));
        }

        [InstallTextsFact]
        public void ErrMsgTitles()
        {
            // (the title is the install's help: InstallTexts)
            Assert.Equal("Divide by zero", Vs("ErrMsg", 273).Trim());
        }

        [Fact]
        public void ErrMsgOfAnUnknownCodeIsEmpty() => Assert.Equal("", Vs("ErrMsg", 99999));

        // ------------------------------------------------------------------ edge cases
        [Fact]
        public void ErrTrapCode0OnlyClearsTheError()
        {
            // the runtime stores code 0 and returns before the error event and the halt:
            // ErrTrap(0, 1) does not halt and IsError() then reports 0
            var pb = new ProgramBuilder();
            var b = pb.Block();
            foreach (var (code, halt) in new[] { (273, 0), (0, 1) })
                b.ConstInt(code)
                    .Convert(Types.Long)
                    .ConstInt(halt)
                    .Convert(Types.Long)
                    .Func("ErrTrap", 2)
                    .Op("Pop");
            b.Func("IsError", 0).End();
            byte[] c = b.Code();
            var prog = pb.Build();
            var m = new CicodeVm(prog, Registry.Default(), null, new ManualClock());
            var t = m.TaskForBlock(c, prog, false);
            m.Run(t);
            Assert.True(t.State == TaskState.Stop && t.Result!.Value.I == 0);
        }

        [Fact]
        public void CodeSetModeUnknownTypeIs274AndMinus1()
        {
            var (r, t) = Call("CodeSetMode", new object[] { 7, 1 }, errset: false);
            Assert.True(t.State == TaskState.Stop && r.I == -1 && t.Error == 274);
            (r, t) = Call("CodeSetMode", new object[] { -1, 1 }, errset: false);
            Assert.True(r.I == -1 && t.Error == 274);
        }

        [Fact]
        public void GetGlbStrOutOfRangeReturnsNull()
        {
            // the runtime raises 257 (no halt) and returns a NULL string, which the call turns
            // into 530 with the halt flag
            var (r, t) = Call("GetGlbStr", new object[] { 9999 }, errset: false);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 530);
            (r, t) = Call("GetGlbStr", new object[] { 9999 });
            Assert.True(t.State == TaskState.Stop && r.S == "" && t.Error == 530);
        }

        [Fact]
        public void StrFormatClampsWidthAndDecimals()
        {
            // the 16-bit width is clamped to 1..20 and the decimals to 0..10
            Assert.Equal(
                Printf.Format("%20.10f", PrintfArg.Float(3.14159)),
                Vs("StrFormat", 3.14159, 30, 12, "")
            );
            Assert.Equal("3 m", Vs("StrFormat", 3.14159, -5, -2, "m"));
            Assert.Equal("   2.5", Vs("StrFormat", 2.5, 6, 1, ""));
            Assert.Equal("   2.5", Vs("StrFormat", 2.5, 0x10006, 1, "")); // 16 bits
        }
    }
}
