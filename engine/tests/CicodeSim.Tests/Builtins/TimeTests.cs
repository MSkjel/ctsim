using System;
using System.Collections.Generic;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Time built-ins.</summary>
    [Collection("World")]
    public sealed class TimeTests : BuiltinTestBase
    {
        private const long T0 = 1790841600; // 2026-10-01 08:00:00 UTC, a Thursday

        private Sim Prog(params (string Fn, string Builtin, object[] Args)[] calls) =>
            Make(pb =>
            {
                foreach (var (f, b, args) in calls)
                    Ret(pb, f, b, args);
            });

        private static (string, string, object[]) C(string fn, string b, params object[] args) =>
            (fn, b, args);

        private static void EveryCulture(Action a)
        {
            a();
            InCulture("nb-NO", a);
            InCulture("tr-TR", a);
            InCulture("ar-SA", a);
        }

        [Fact]
        public void TimeToStrFormats()
        {
            var s = Prog(
                C("f0", "TimeToStr", T0, 0, 0),
                C("f1", "TimeToStr", T0, 1, 0),
                C("f2", "TimeToStr", T0, 2, 0),
                C("f3", "TimeToStr", T0, 3, 0),
                C("f4", "TimeToStr", T0, 4, 0),
                C("f5", "TimeToStr", 3725, 5, 0),
                C("f6", "TimeToStr", 3725123, 6, 0),
                C("f7", "TimeToStr", 3725, 7, 0),
                C("f8", "TimeToStr", 90061, 8, 0),
                C("f9", "TimeToStr", T0, 9, 0),
                C("f10", "TimeToStr", T0, 10, 0),
                C("f11", "TimeToStr", 45296, 11, 0),
                C("f11b", "TimeToStr", 46800, 11, 0),
                C("pm", "TimeToStr", T0 + 5 * 3600 + 300, 0, 0),
                C("am12", "TimeToStr", T0 - 8 * 3600 + 1800, 0, 0),
                C("noon", "TimeToStr", T0 + 4 * 3600, 0, 0),
                C("tod", "TimeToStr", 100, 0, 0),
                C("neg", "TimeToStr", -5, 0, 0),
                C("bad", "TimeToStr", T0, 12, 0)
            );
            EveryCulture(() =>
            {
                Assert.Equal("08:00 AM", S(s.Result("f0")));
                Assert.Equal("08:00:00 AM", S(s.Result("f1")));
                Assert.Equal("01-10-26", S(s.Result("f2")));
                Assert.Equal("Thursday, 1 October 2026", S(s.Result("f3")));
                Assert.Equal("Thu Oct 01 2026 08:00:00 AM", S(s.Result("f4")));
                Assert.Equal("01:02:05", S(s.Result("f5")));
                Assert.Equal("01:02:05.123", S(s.Result("f6")));
                Assert.Equal("01:02", S(s.Result("f7")));
                Assert.Equal("  1 Days  1 Hours  1 min  1 sec", S(s.Result("f8")));
                Assert.Equal("01-10-2026", S(s.Result("f9")));
                Assert.Equal("2026-10-01 08:00:00", S(s.Result("f10")));
                Assert.Equal("12:34:56 AM", S(s.Result("f11"))); // the runtime labels noon AM in format 11
                Assert.Equal("01:00:00 PM", S(s.Result("f11b")));
                Assert.Equal("01:05 PM", S(s.Result("pm")));
                Assert.Equal("12:30 AM", S(s.Result("am12")));
                Assert.Equal("12:00 PM", S(s.Result("noon")));
            });
            var r = s.Call("tod");
            Assert.Equal("", S(r.Task.Result!.Value));
            Assert.Equal(0, r.Task.Error); // a time of day for a date format: '' only
            r = s.Call("neg");
            Assert.Equal("", S(r.Task.Result!.Value));
            Assert.Equal(Codes.InvalidArg, r.Task.Error);
            Assert.Equal(Codes.InvalidArg, s.Call("bad").Task.Error);
        }

        [Fact]
        public void TimeToStrFollowsTheLocaleAndZone()
        {
            var s = Prog(
                C("f1", "TimeToStr", T0, 1, 0),
                C("f1u", "TimeToStr", T0, 1, 1),
                C("f2", "TimeToStr", T0, 2, 0),
                C("ti1", "TimeInfo", 1),
                C("di1", "DateInfo", 1, 0),
                C("di2", "DateInfo", 2, 0)
            );
            s.Host.TimeZone = TimeZoneInfo.CreateCustomTimeZone(
                "plus2",
                TimeSpan.FromHours(2),
                "plus2",
                "plus2"
            );
            s.Host.Locale = new TimeLocale
            {
                Hour24 = true,
                TimeSep = ".",
                DateSep = ".",
                ShortDate = "dd.MM.yyyy",
                DateOrder = 1,
            };
            Assert.Equal("10.00.00", S(s.Result("f1")));
            Assert.Equal("08.00.00", S(s.Result("f1u"))); // UTC
            Assert.Equal("01.10.2026", S(s.Result("f2")));
            Assert.Equal("1", S(s.Result("ti1")));
            Assert.Equal("1", S(s.Result("di1")));
            Assert.Equal(".", S(s.Result("di2")));
        }

        [Fact]
        public void StrToTimeDatePeriod()
        {
            var s = Prog(
                C("t1", "StrToTime", "08:30"),
                C("t2", "StrToTime", "8:30:15 PM"),
                C("t3", "StrToTime", "12:00 AM"),
                C("t4", "StrToTime", "25:00"),
                C("t5", "StrToTime", "abc"),
                C("t6", "StrToTime", " 13 pm"),
                C("d1", "StrToDate", "01/10/26"),
                C("d2", "StrToDate", "1/Oct/2026"),
                C("d3", "StrToDate", "31/02/26"),
                C("d4", "StrToDate", "29/02/24"),
                C("d5", "StrToDate", "1/1/79"),
                C("d6", "StrToDate", "01/10/1999"),
                C("p1", "StrToPeriod", "90"),
                C("p2", "StrToPeriod", "1:30"),
                C("p3", "StrToPeriod", "1:02:03"),
                C("p4", "StrToPeriod", "1:60"),
                C("p5", "StrToPeriod", "x")
            );
            EveryCulture(() =>
            {
                Assert.Equal(30600, I(s.Result("t1")));
                Assert.Equal(73815, I(s.Result("t2")));
                Assert.Equal(0, I(s.Result("t3")));
                Assert.Equal(46800, I(s.Result("t6"))); // PM keeps an hour above 11
                Assert.Equal(T0 - 8 * 3600, I(s.Result("d1"))); // local midnight
                Assert.Equal(T0 - 8 * 3600, I(s.Result("d2"))); // a short month name
                Assert.Equal(-1, I(s.Result("d3")));
                Assert.Equal(1709164800, I(s.Result("d4"))); // 2024-02-29
                Assert.Equal(-1, I(s.Result("d5"))); // 2079: past 2037
                Assert.Equal(938736000, I(s.Result("d6")));
                Assert.Equal(90, I(s.Result("p1")));
                Assert.Equal(90, I(s.Result("p2"))); // m:s
                Assert.Equal(3723, I(s.Result("p3")));
            });
            var r = s.Call("t4");
            Assert.Equal(-1, I(r.Task.Result!.Value));
            Assert.Equal(Codes.InvalidArg, r.Task.Error);
            Assert.Equal(-1, I(s.Result("t5")));
            r = s.Call("d3");
            Assert.Equal(0, r.Task.Error); // StrToDate raises nothing
            Assert.Equal(-1, I(s.Result("p4")));
            Assert.Equal(Codes.InvalidArg, s.Call("p5").Task.Error);
        }

        [Fact]
        public void TimeSubPartsAndDateInfo()
        {
            var calls = new List<(string, string, object[])>();
            for (int p = 0; p <= 11; p++)
                calls.Add(C("p" + p, "_TimeSub", T0, p));
            calls.Add(C("di6", "DateInfo", 6, 1));
            calls.Add(C("di7", "DateInfo", 7, 2));
            calls.Add(C("di8", "DateInfo", 8, 12));
            calls.Add(C("di9", "DateInfo", 9, 1));
            calls.Add(C("di9x", "DateInfo", 9, 13));
            calls.Add(C("di5", "DateInfo", 5, 0));
            calls.Add(C("ti3", "TimeInfo", 3));
            calls.Add(C("ti4", "TimeInfo", 4));
            calls.Add(C("add", "DateAdd", T0, 60));
            calls.Add(C("sub", "DateSub", T0, 60));
            var s = Prog(calls.ToArray());
            long[] want = { 8, 0, 0, 1, 5, 10, 26, T0 - 8 * 3600, 273, 2026, 31 };
            for (int p = 0; p <= 10; p++)
                Assert.Equal(want[p], I(s.Result("p" + p)));
            Assert.Equal(Codes.InvalidArg, s.Call("p11").Task.Error);
            Assert.Equal("Sun", S(s.Result("di6")));
            Assert.Equal("Monday", S(s.Result("di7")));
            Assert.Equal("Dec", S(s.Result("di8")));
            Assert.Equal("January", S(s.Result("di9")));
            Assert.Equal("", S(s.Result("di9x")));
            Assert.Equal("dd-mm-yyyy", S(s.Result("di5")));
            Assert.Equal("AM", S(s.Result("ti3")));
            Assert.Equal("PM", S(s.Result("ti4")));
            Assert.Equal(T0 + 60, I(s.Result("add")));
            Assert.Equal(T0 - 60, I(s.Result("sub")));
        }

        [Fact]
        public void TimeCurrentAndSysTimeFollowTheSimulatedClock()
        {
            var s = Make(pb =>
            {
                Ret(pb, "now", "TimeCurrent");
                Ret(pb, "sys", "SysTime");
                // Delta(): x = SysTime(); SleepMS(1500); RETURN SysTimeDelta(x)
                pb.Function("Delta", "", Types.Long)
                    .StackVar(Types.Long)
                    .Func("SysTime", 0)
                    .StoreStack(0)
                    .ConstInt(1500)
                    .Func("SleepMS", 1)
                    .Op("Pop")
                    .AddrStack(0)
                    .Func("SysTimeDelta", 1)
                    .RetValue()
                    .End();
            });
            Assert.Equal(T0, I(s.Result("now")));
            Assert.Equal(1000000, I(s.Result("sys"))); // SimClock's monotonic origin, ms
            Assert.Equal(1500, I(s.Result("Delta")));
            s.W.Clock.Advance(3600);
            Assert.Equal(T0 + 3601, I(s.Result("now"))); // + the 1.5 s the Delta run slept, floored
        }

        [Fact]
        public void TimeSetChangesWallTimeAndPreservesElapsedTime()
        {
            var s = Make(pb =>
            {
                Ret(pb, "set", "TimeSet", 1600000000);
                Ret(pb, "bad", "TimeSet", 1);
                Ret(pb, "now", "TimeCurrent");
            });
            s.W.Clock.Advance(12.375);
            Assert.Equal(0, I(s.Result("set")));
            Assert.Equal(1600000000, I(s.Result("now")));
            Assert.Equal(12375, s.W.Clock.NowMs);
            Assert.Equal(Codes.InvalidArg, I(s.Result("bad")));
            s.W.Clock.Advance(1);
            Assert.Equal(1600000001, I(s.Result("now")));
        }

        [Fact]
        public void TimestampFunctions()
        {
            var s = Make(pb =>
            {
                Ret(pb, "cur", "TimestampCurrent");
                Ret(pb, "create", "TimestampCreate", 2009, 6, 29, 11, 2, 10, 468, 1);
                Ret(pb, "createBad", "TimestampCreate", 2009, 13, 29, 11, 2, 10, 0, 1);
                // fmt(): TimestampFormat(TimestampCreate(2009,6,29,11,2,10,468,1), "dd/MM/yyyy HH:mm:ss.fff", 1)
                var f = pb.Function("fmt", "", Types.String);
                foreach (object a in new object[] { 2009, 6, 29, 11, 2, 10, 468, 1 })
                    Push(f, a);
                f.Func("TimestampCreate", 8)
                    .ConstStr("dd/MM/yyyy HH:mm:ss.fff")
                    .ConstInt(1)
                    .Func("TimestampFormat", 3)
                    .RetValue()
                    .End();
                // diff(part, cum): TimestampDifference(Create(09:01:30), Create(09:00:00), part, cum)
                var d = pb.Function("diff", "44", Types.Long).Begin();
                foreach (object a in new object[] { 2008, 11, 28, 9, 1, 30, 0, 1 })
                    Push(d, a);
                d.Func("TimestampCreate", 8);
                foreach (object a in new object[] { 2008, 11, 28, 9, 0, 0, 0, 1 })
                    Push(d, a);
                d.Func("TimestampCreate", 8)
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("TimestampDifference", 4)
                    .RetValue()
                    .End();
                // part(p): TimestampGetPart(TimestampAdd(TimestampCurrent(), 1, 0), p, 1)
                pb.Function("part", "4", Types.Long)
                    .Begin()
                    .Func("TimestampCurrent", 0)
                    .ConstInt(1)
                    .ConstInt(0)
                    .Func("TimestampAdd", 3)
                    .LoadStack(0)
                    .ConstInt(1)
                    .Func("TimestampGetPart", 3)
                    .RetValue()
                    .End();
                pb.Function("toInt", "", Types.Long)
                    .Func("TimestampCurrent", 0)
                    .ConstInt(1)
                    .Func("TimestampToTimeInt", 2)
                    .RetValue()
                    .End();
                pb.Function("fromInt", "", Types.Long)
                    .ConstInt(T0)
                    .ConstInt(500)
                    .ConstInt(1)
                    .Func("TimeIntToTimestamp", 3)
                    .ConstInt(6)
                    .ConstInt(1)
                    .Func("TimestampGetPart", 3)
                    .RetValue()
                    .End();
                pb.Function("str11", "", Types.String)
                    .Func("TimestampCurrent", 0)
                    .ConstInt(11)
                    .ConstInt(1)
                    .Func("TimestampToStr", 3)
                    .RetValue()
                    .End();
                pb.Function("str15", "", Types.String)
                    .Func("TimestampCurrent", 0)
                    .ConstInt(15)
                    .ConstInt(1)
                    .Func("TimestampToStr", 3)
                    .RetValue()
                    .End();
                pb.Function("back", "", Types.Long)
                    .Func("TimestampCurrent", 0)
                    .ConstInt(15)
                    .ConstInt(1)
                    .Func("TimestampToStr", 3)
                    .ConstInt(15)
                    .ConstInt(0)
                    .Func("StrToTimestamp", 3)
                    .ConstInt(1)
                    .Func("TimestampToTimeInt", 2)
                    .RetValue()
                    .End();
                // sub(p): TimestampGetPart(TimestampSub(TimestampCurrent(), 1, 3), p, 1): one hour back
                pb.Function("sub", "4", Types.Long)
                    .Begin()
                    .Func("TimestampCurrent", 0)
                    .ConstInt(1)
                    .ConstInt(3)
                    .Func("TimestampSub", 3)
                    .LoadStack(0)
                    .ConstInt(1)
                    .Func("TimestampGetPart", 3)
                    .RetValue()
                    .End();
                Ret(pb, "off", "TimeUTCOffset", T0);
                pb.Function("ole", "", Types.Long)
                    .ConstInt(T0)
                    .ConstInt(0)
                    .Func("TimeToOLEDate", 2)
                    .ConstInt(0)
                    .Func("OLEDateToTime", 2)
                    .RetValue()
                    .End();
            });
            ulong ft0 = (ulong)DateTimeOffset.FromUnixTimeSeconds(T0).UtcDateTime.ToFileTimeUtc();
            Assert.Equal(ft0, (ulong)s.Result("cur").I);
            Assert.Equal(Types.Timestamp, s.Result("cur").T);
            Assert.Equal(
                (ulong)new DateTime(2009, 6, 29, 11, 2, 10, 468, DateTimeKind.Utc).ToFileTimeUtc(),
                (ulong)s.Result("create").I
            );
            var r = s.Call("createBad");
            Assert.Equal(0, r.Task.Result!.Value.I); // INVALID_TIMESTAMP
            Assert.Equal(Codes.InvalidArg, r.Task.Error);
            EveryCulture(() =>
            {
                Assert.Equal("29/06/2009 11:02:10.468", S(s.Result("fmt")));
                Assert.Equal("2026-10-01T08:00:00", S(s.Result("str11")));
            });
            Assert.Equal(90, I(s.Result("diff", 5, 1))); // the help's example: cumulative
            Assert.Equal(30, I(s.Result("diff", 5, 0))); // non-cumulative
            Assert.Equal(90000, I(s.Result("diff", 6, 1)));
            Assert.Equal(1, I(s.Result("diff", 4, 1)));
            Assert.Equal(2027, I(s.Result("part", 0))); // + 1 year
            Assert.Equal(8, I(s.Result("part", 3)));
            Assert.Equal(8 * 3600 * 1000, I(s.Result("part", 7)));
            Assert.Equal(7, I(s.Result("sub", 3))); // 08:00 - 1 hour
            Assert.Equal(1, I(s.Result("sub", 2))); // same day
            Assert.Equal(T0, I(s.Result("toInt")));
            Assert.Equal(500, I(s.Result("fromInt")));
            Assert.Equal(
                ft0.ToString(System.Globalization.CultureInfo.InvariantCulture),
                S(s.Result("str15"))
            );
            Assert.Equal(T0, I(s.Result("back")));
            Assert.Equal(0, I(s.Result("off"))); // UTC
            Assert.Equal(T0, I(s.Result("ole")));
            s.Host.TimeZone = TimeZoneInfo.CreateCustomTimeZone(
                "plus2",
                TimeSpan.FromHours(2),
                "plus2",
                "plus2"
            );
            Assert.Equal(7200, I(s.Result("off")));
        }

        [Fact]
        public void WaveFollowsTheMillisecondCounter()
        {
            var s = Make(pb =>
            {
                Ret(pb, "sq", "_Wave", 1, 10, 0, 100, 0);
                Ret(pb, "tri", "_Wave", 2, 10, 0, 100, 0);
                Ret(pb, "saw", "_Wave", 3, 10, 0, 100, 0);
                Ret(pb, "sine", "_Wave", 0, 10, 0, 100, 0);
            });
            // SysTime starts at 1,000,000 ms: 1000 s = 100 whole periods of 10 s: x = 0
            Assert.Equal(100, I(s.Result("sq")));
            Assert.Equal(0, I(s.Result("tri")));
            Assert.Equal(0, I(s.Result("saw")));
            Assert.Equal(50, I(s.Result("sine")));
            s.W.Clock.Advance(7.5); // x = 0.75
            Assert.Equal(0, I(s.Result("sq")));
            Assert.Equal(50, I(s.Result("tri")));
            Assert.Equal(75, I(s.Result("saw")));
            Assert.Equal(0, I(s.Result("sine")));
        }
    }
}
