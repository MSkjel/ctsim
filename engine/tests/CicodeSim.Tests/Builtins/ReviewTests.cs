using System;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Built-in edge cases: each test pins one rule.</summary>
    [Collection("World")]
    public sealed class ReviewTests : BuiltinTestBase
    {
        [Fact]
        public void RecursiveEventHandlersHaltWith295InsteadOfOverflowingTheHost()
        {
            // a handler that raises its own event again: the runtime runs handlers nested on the
            // caller's Cicode stack, so the nest ends in a stack overflow (295), never in a crash
            // of the simulating process
            var s = Make(pb =>
            {
                pb.Function("Rec", "", Types.Long)
                    .ConstInt(0)
                    .ConstInt(5)
                    .Func("CallEvent", 2)
                    .Op("Pop")
                    .ConstInt(1)
                    .RetValue()
                    .End();
                pb.Function("Chain", "", Types.Long)
                    .ConstInt(6)
                    .Func("GetEvent", 1)
                    .Func("ChainEvent", 1)
                    .Op("Pop")
                    .ConstInt(1)
                    .RetValue()
                    .End();
                Ret(pb, "on", "OnEvent", 5, "Rec");
                Ret(pb, "on6", "OnEvent", 6, "Chain");
                Ret(pb, "call", "CallEvent", 0, 5);
                Ret(pb, "call6", "CallEvent", 0, 6);
            });
            Assert.Equal(0, s.Result("on").I);
            var r = s.Call("call");
            Assert.Equal("halt 295", r.Outcome);
            Assert.Equal(0, s.Result("on6").I);
            r = s.Call("call6");
            Assert.Equal("halt 295", r.Outcome);
        }

        [Fact]
        public void TagInfoInCachedMode0RaisesSuppressedAtItsAnswerToo()
        {
            // CachedMode 0 answers at the task's next turn; the 424 raised then is still under
            // TagInfo's ErrSet: the debugger's error hook sees level 1, not 0
            var s = Make(pb =>
            {
                Ret(pb, "now", "TagInfo", "NoSuch", "1", "", 3);
                Ret(pb, "later", "TagInfo", "NoSuch", "1", "", 0);
                Ret(pb, "laterEx", "TagInfoEx", "NoSuch", 1, "", 0);
                Ret(pb, "badEx", "TagInfoEx", "Tank1_Level", 9, "", 3);
            });
            var levels = new System.Collections.Generic.List<int>();
            s.Run.Vm.ErrorHook = (task, code) =>
            {
                levels.Add(task.ErrSetLevel);
                return false;
            };
            foreach (string fn in new[] { "now", "later", "laterEx", "badEx" })
            {
                levels.Clear();
                var r = s.Call(fn);
                Assert.Equal("stop", r.Outcome);
                Assert.Equal(fn == "badEx" ? Codes.InvalidArg : Codes.TagNotFound, r.Task.Error);
                Assert.Equal(new[] { 1 }, levels.ToArray());
                Assert.Equal(0, r.Task.ErrSetLevel);
            }
        }

        [Fact]
        public void TimestampTextUsesTheSimulatedZoneNeverThisComputers()
        {
            // 'U' and 'z' take the simulated zone (--tz), never the zone of the computer the
            // simulator runs on
            var s = Make(pb =>
            {
                pb.Function("u13", "4", Types.String)
                    .Begin()
                    .Func("TimestampCurrent", 0)
                    .ConstInt(13)
                    .LoadStack(0)
                    .Func("TimestampToStr", 3)
                    .RetValue()
                    .End();
                pb.Function("fmt", "74", Types.String)
                    .Begin()
                    .Func("TimestampCurrent", 0)
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("TimestampFormat", 3)
                    .RetValue()
                    .End();
            });
            s.Host.TimeZone = TimeZoneInfo.CreateCustomTimeZone(
                "plus5",
                TimeSpan.FromHours(5),
                "plus5",
                "plus5"
            );
            // 2026-10-01 08:00:00 UTC = 13:00 in the simulated zone
            // (the machine running the test is not at +05:00, or the zone would not show)
            Assert.Equal("Thursday, 1 October 2026 08:00:00 AM", S(s.Result("u13", 0)));
            Assert.Equal("Thursday, 1 October 2026 08:00:00 AM", S(s.Result("u13", 1)));
            Assert.Equal("Thursday, 1 October 2026 08:00:00 AM", S(s.Result("fmt", "U", 0)));
            Assert.Equal("13:00 +05:00", S(s.Result("fmt", "HH:mm zzz", 0)));
            Assert.Equal("13:00 +5", S(s.Result("fmt", "HH:mm z", 0)));
            Assert.Equal("08:00 +00:00", S(s.Result("fmt", "HH:mm zzz", 1)));
            Assert.Equal("2026-10-01T13:00:00.0000000+05:00", S(s.Result("fmt", "o", 0)));
            Assert.Equal("2026-10-01T08:00:00.0000000Z", S(s.Result("fmt", "o", 1)));
            Assert.Equal("13:00+05:00", S(s.Result("fmt", "HH:mmK", 0)));
            Assert.Equal("08:00Z", S(s.Result("fmt", "HH:mmK", 1)));
            Assert.Equal("2026 z K", S(s.Result("fmt", "yyyy 'z' \\K", 0))); // quoted / escaped: literal
            Assert.Equal("13:00", S(s.Result("fmt", "HH:mm", 0)));
        }

        [Fact]
        public void TaskNewWithBadArgumentTextLogsTheKernelMessage()
        {
            // live (conformance/runtime/ctapi/tasknew_int_param_non_numeric.json):
            // TaskNew("AddZeroFront", "^"abc^"", 0) = -1, no task, no hardware error, and the kernel
            // shows 'Function AddZeroFront, parameter 1, number type (4) parameter expected, value=abc'
            var s = Make(pb =>
            {
                pb.Function("AddZeroFront", "4", Types.String).Begin().ConstStr("x").RetValue().End();
                pb.Function("Two", "44").Begin().Ret().End();
                Ret(pb, "bad", "TaskNew", "AddZeroFront", "\"abc\"", 0);
                Ret(pb, "badLong", "TaskNew", "AddZeroFront", "\"abcdefghijklm\"", 0);
                Ret(pb, "few", "TaskNew", "Two", "1", 0);
                Ret(pb, "ok", "TaskNew", "AddZeroFront", "5", 0);
            });
            var r = s.Call("bad");
            Assert.Equal(-1, r.Task.Result!.Value.I);
            Assert.Equal(0, r.Task.Error);
            Assert.Contains(
                "Function AddZeroFront, parameter 1, number type (4) parameter expected, value=abc",
                s.Log
            );
            s.Call("badLong");
            Assert.Contains(
                "Function AddZeroFront, parameter 1, number type (4) parameter expected, value=abcdefghij...",
                s.Log
            );
            Assert.Equal(-1, s.Result("few").I);
            Assert.Contains("Not enough arguments for function Two", s.Log);
            int n = s.Log.Count;
            Assert.True(s.Result("ok").I > 0);
            Assert.Equal(n, s.Log.Count);
        }

        [Fact]
        public void TagBrowseFieldsReadAloneEqualTheDefinitions()
        {
            // the browse reads a raw column on its own; every field of every record is the text
            // the tag's definition gives
            var s = Make(pb => Ret(pb, "open", "TagBrowseOpen", "", "", "", ""));
            var db = s.W.Tags;
            var recs = Equipment.TagRecords.Of(s.Host, db);
            Assert.True(recs.Count >= 13);
            for (int i = 0; i < recs.Count; i++)
            {
                string name = recs.Field(i, "TAG")!;
                var d = db.Find(name)!;
                foreach (string f in Equipment.TagFields)
                    Assert.Equal(Equipment.TagField(db, name, d, f), recs.Field(i, f));
                Assert.Null(recs.Field(i, "NOPE"));
            }
        }

        [Fact]
        public void TagBrowseByNameOrEquipmentReadsNoDefinitionColumns()
        {
            // a browse of every tag filtered / sorted by name or equipment (and its GetFields of
            // those) never reads the definition columns: on a big unit they take over a gigabyte
            var s = Make(pb =>
            {
                Ret(pb, "open", "TagBrowseOpen", "TAG=*", "TAG,EQUIPMENT,ITEM,CLUSTER", "TAG:A", "");
                Ret(pb, "openEq", "TagBrowseOpen", "EQUIPMENT=Plant.Tank1", "TAG,ITEM", "ITEM:D", "");
                pb.Function("get", "47", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("TagBrowseGetField", 2)
                    .RetValue()
                    .End();
                pb.Function("next", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TagBrowseNext", 1)
                    .RetValue()
                    .End();
                pb.Function("n", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TagBrowseNumRecords", 1)
                    .RetValue()
                    .End();
            });
            long h = s.Result("open").I;
            Assert.True(s.Result("n", h).I >= 13);
            while (s.Result("next", h).I == 0)
            {
                S(s.Result("get", h, "EQUIPMENT"));
                S(s.Result("get", h, "CLUSTER"));
            }
            long he = s.Result("openEq").I;
            Assert.Equal(3, s.Result("n", he).I);
            Assert.Equal(0, s.Result("next", he).I);
            Assert.Equal("Temp", S(s.Result("get", he, "ITEM"))); // ITEM:D
            var defs = typeof(CicodeSim.World.VarTable).GetField(
                "_defs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!;
            foreach (var vt in s.W.Tags.Tables())
                Assert.Null(defs.GetValue(vt));
        }

        [Fact]
        public void BrowseSortIsTheSameOnEveryFrameworkForMixedKeys()
        {
            // numeric where both keys are numbers, text otherwise: not a total order
            // ("2" < "10" < "1a" < "2"); the merge sort gives one order whatever the framework
            var keys = new[] { "10", "2", "1a", "b", "", "-3", "x1", "1e2", "9" };
            var m = new System.Collections.Generic.List<int>();
            for (int i = 0; i < keys.Length; i++)
                m.Add(i);
            var order = Browse.Sorted(m, (i, f) => keys[i], Browse.SortSpec("K", Array.Empty<string>()));
            var got = Array.ConvertAll(order, i => keys[i]);
            Assert.Equal(new[] { "", "-3", "1a", "2", "9", "10", "1e2", "b", "x1" }, got);
        }

        [Fact]
        public void TaskHandlesStayInTheSixteenBitRangeOfTaskNew()
        {
            // TaskNew returns a 16-bit INT: handles wrap to 1 and skip the live tasks
            var s = Make(pb =>
            {
                pb.Function("Quick").Begin().Ret().End();
                pb.Function("Slow").Begin().ConstInt(100).Func("Sleep", 1).Op("Pop").Ret().End();
                Ret(pb, "start", "TaskNew", "Slow", "", 0);
                pb.Function("kill", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TaskKill", 1)
                    .RetValue()
                    .End();
            });
            var sched = s.Run.Scheduler;
            long first = s.Result("start").I; // a live task holding a low handle
            Assert.True(first > 0 && first < 10);
            var quick = s.Run.Library.Function("Quick")!;
            int max = 0;
            for (int i = 0; i < 40000; i++)
            {
                var t = sched.Spawn(quick);
                max = Math.Max(max, t.Handle);
                Assert.NotEqual(first, t.Handle); // a live task's handle is never given again
                sched.Run();
            }
            Assert.Equal(Scheduler.MaxHandle, max);
            long h = s.Result("start").I;
            Assert.True(
                h > 0 && h <= Scheduler.MaxHandle,
                h.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            Assert.Equal(0, s.Result("kill", h).I);
            Assert.Equal(0, s.Result("kill", first).I);
        }

        [Fact]
        public void StrToTimestampUsesTheSimulatedDayAndTheTextsOffset()
        {
            // a time alone is on the simulated day, not this computer's today; an offset is honoured
            var s = Make(
                pb =>
                    pb.Function("ts", "74", Types.Long)
                        .Begin()
                        .LoadStack(0)
                        .ConstInt(0)
                        .LoadStack(1)
                        .Func("StrToTimestamp", 3)
                        .ConstInt(1)
                        .Func("TimestampToTimeInt", 2)
                        .RetValue()
                        .End(),
                start: "2020-03-04T08:00:00Z"
            );
            s.Host.TimeZone = TimeZoneInfo.CreateCustomTimeZone(
                "plus5",
                TimeSpan.FromHours(5),
                "plus5",
                "plus5"
            );
            long Unix(int y, int mo, int d, int hh, int mm) =>
                new DateTimeOffset(y, mo, d, hh, mm, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            // 08:00 UTC = 13:00 on 2020-03-04 in the simulated zone
            Assert.Equal(Unix(2020, 3, 4, 9, 30), s.Result("ts", "02:30 PM", 0).I); // local 14:30 = 09:30 UTC
            Assert.Equal(Unix(2020, 3, 4, 14, 30), s.Result("ts", "14:30", 1).I); // UTC day
            Assert.Equal(Unix(2026, 10, 1, 8, 0), s.Result("ts", "2026-10-01T08:00:00Z", 0).I);
            Assert.Equal(Unix(2026, 10, 1, 6, 0), s.Result("ts", "2026-10-01T08:00:00+02:00", 0).I);
            Assert.Equal(Unix(2026, 10, 1, 6, 0), s.Result("ts", "2026-10-01T08:00:00+02:00", 1).I);
            Assert.Equal(Unix(2026, 10, 1, 3, 0), s.Result("ts", "2026-10-01 08:00:00", 0).I); // no offset: local
            Assert.Equal(Unix(2026, 10, 1, 8, 0), s.Result("ts", "2026-10-01 08:00:00", 1).I);
        }

        [Fact]
        public void EveryWorldBuiltinSurvivesHostileArguments()
        {
            // extreme numbers, NaN / infinities, 300-character and format-like strings, invalid
            // timestamps: a built-in raises a hardware error or returns, never a .NET exception
            int[] ints = { 0, 1, -1, 2, 7, 8, 15, 16, 255, 256, 32767, -32768, int.MaxValue, int.MinValue };
            double[] reals = { 0.0, -1.0, 1e300, -1e300, double.NaN, double.PositiveInfinity, 0.5 };
            string[] strs =
            {
                "",
                "x",
                "abc def",
                "{a,5,R}{b}",
                "^",
                "\"",
                "Tank1_Level",
                "TAG=*;EQUIPMENT<>NOT a|b OR ITEM>3",
                new string('a', 300),
                "1:2:3",
                "2026-10-01T08:00:00Z",
                "*",
                "Plant.Tank1",
                "@(x",
                "[RUN]:x",
                "zzz K o U",
            };
            ulong[] stamps = { 0, ulong.MaxValue, 1, 133000000000000000UL, 0x7FFFFFFFFFFFFFFFUL };
            var rnd = new Random(20261001);
            var table = CicodeSim.Compiled.Func0Table.Default;
            var calls = new System.Collections.Generic.List<(string Fn, string Name)>();
            var s = Make(pb =>
            {
                int k = 0;
                foreach (var (info, _) in WorldBuiltins.Declared())
                {
                    var b = table.Lookup(info.Name)!;
                    for (int pass = 0; pass < 30; pass++)
                    {
                        int n = pass % 2 == 0 ? b.MinArgs : b.NArgs;
                        var args = new System.Collections.Generic.List<object>();
                        var text = new System.Text.StringBuilder(b.Name + "(");
                        for (int i = 0; i < n; i++)
                        {
                            int ty = Types.SigType(b.Args[i]);
                            object v;
                            if (ty == Types.String || ty == Types.Variant || ty == Types.Tag)
                                v = strs[rnd.Next(strs.Length)];
                            else if (Types.IsRealType(ty))
                                v = reals[rnd.Next(reals.Length)];
                            else if (ty == Types.Quality || ty == Types.Timestamp)
                                v = Value.Int(ty, unchecked((long)stamps[rnd.Next(stamps.Length)]));
                            else
                                v = ints[rnd.Next(ints.Length)];
                            args.Add(v);
                            text.Append(i > 0 ? "," : "")
                                .Append(v is string sv && sv.Length > 20 ? "a*300" : v.ToString());
                        }
                        string fn = "fuzz" + k++;
                        Ret(pb, fn, b.Name, args.ToArray());
                        calls.Add((fn, text.Append(')').ToString()));
                    }
                }
            });
            var failed = new System.Collections.Generic.List<string>();
            foreach (var (fn, name) in calls)
            {
                try
                {
                    var r = s.Run.Call(fn, null, maxSeconds: 2, maxSlices: 2000);
                    foreach (var x in s.Run.Scheduler.Live())
                        s.Run.Vm.Halt(x, "fuzz"); // nothing it started runs on
                    s.Run.Scheduler.Step();
                }
                catch (Exception e)
                {
                    failed.Add(name + ": " + e.GetType().Name + " " + e.Message);
                }
            }
            Assert.True(failed.Count == 0, string.Join("\n", failed));
        }
    }
}
