using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Parameters, environment, ini files, version, maps and arrays.</summary>
    [Collection("World")]
    public sealed class DataParamsMapsTests : DataTestBase
    {
        private string Ini(string text)
        {
            string p = Path.Combine(Tmp.Path, "citect.ini");
            File.WriteAllText(p, text, Encoding.ASCII);
            return p;
        }

        // ------------------------------------------------------------------ parameters
        [Fact]
        public void ParameterGetLooksUpComputerSectionIniThenParamDbfThenDefault()
        {
            var s = MakeData();
            var d = ProcessData.Of(s.Host);
            d.ComputerName = "SCADA1";
            string ini = Ini(
                "[Alarm]\r\nScanTime = 750\r\nEmpty=\r\n[Page]\r\nWindows=4\r\n[\\\\SCADA1\\Page]\r\nWindows = 9\r\n[\\\\OTHER\\Alarm]\r\nScanTime=1\r\n"
            );
            byte[] before = File.ReadAllBytes(ini);
            d.Ini.CitectIniPath = ini;
            Assert.Equal("9", Call(s, "ParameterGet", "Page", "Windows", "x").S); // the computer's section first
            Assert.Equal("750", Call(s, "ParameterGet", "ALARM", "scantime", "x").S); // citect.ini beats param.dbf (500)
            Assert.Equal("60", Call(s, "ParameterGet", "Alarm", "Period", "x").S); // param.dbf of the unit
            Assert.Equal("dflt", Call(s, "ParameterGet", "Alarm", "Nothing", "dflt").S); // default last
            Assert.Equal("dflt", Call(s, "ParameterGet", "Alarm", "Empty", "dflt").S); // an empty ini value falls through
            // ParameterPut: the process's copy only, read first; an empty value falls through again
            Assert.Equal(0, Call(s, "ParameterPut", "Alarm", "Period", "99").I);
            Assert.Equal("99", Call(s, "ParameterGet", "Alarm", "Period", "x").S);
            Call(s, "ParameterPut", "Alarm", "Period", "");
            Assert.Equal("60", Call(s, "ParameterGet", "Alarm", "Period", "x").S);
            Assert.Equal(before, File.ReadAllBytes(ini)); // never the real file
            // an ini value of 256+ characters does not fit the result buffer: not found, and the
            // parameter records of that section are not read either (the runtime returns 0)
            d.Ini.CitectIniPath = Ini("[Alarm]\r\nScanTime=" + new string('v', 256) + "\r\n");
            Assert.Equal("d", Call(s, "ParameterGet", "Alarm", "ScanTime", "d").S); // not param.dbf's 500
            Assert.Equal(
                new string('v', 255),
                Call(s, "ParameterGet", "Alarm", "Nothing", new string('v', 255)).S
            );
        }

        [Fact]
        public void ParameterGetReadsTheCompiledParamTableFirstMatch()
        {
            if (!HaveR2)
                return;
            // ExampleSA's _PARAM.RDB: record order, first match without case
            var w = new CicodeSim.World.World(Path.Combine(R2, "ExampleSA"), root: "ExampleSA");
            var run = new WorldRun(w, new ProgramBuilder().Build());
            var h = WorldBuiltins.HostOf(run.Vm.Builtins)!;
            var d = ProcessData.Of(h);
            d.Ini.CitectIniPath = Ini("");
            d.ComputerName = "NOSUCHPC";
            var sim = new Sim
            {
                W = w,
                Run = run,
                Host = h,
            };
            Assert.Equal("512", Call(sim, "ParameterGet", "code", "STACK", "").S);
            Assert.Equal("ClientStartup", Call(sim, "ParameterGet", "Client", "StartupCode", "").S);
            Assert.Equal("Log State", Call(sim, "ParameterGet", "AlarmHeading", "LogState", "").S);
            Assert.Equal("nope", Call(sim, "ParameterGet", "AlarmHeading", "NoSuch", "nope").S);
        }

        [Fact]
        public void GetEnvIsTheSimulatedEnvironment()
        {
            var s = MakeData();
            var d = ProcessData.Of(s.Host);
            d.ComputerName = "PC7";
            Assert.Equal("PC7", Call(s, "GetEnv", "COMPUTERNAME").S); // follows the simulated name
            Assert.Equal("PC7", Call(s, "GetEnv", "USERDOMAIN").S);
            d.Environment["COMPUTERNAME"] = "SET";
            d.ComputerName = "PC8";
            Assert.Equal("SET", Call(s, "GetEnv", "COMPUTERNAME").S); // a value the host set stays
            Assert.Equal("x86", Call(s, "GetEnv", "processor_architecture").S);
            Assert.Equal(@"C:\Temp", Call(s, "GetEnv", "TEMP").S);
            Assert.Equal("", Call(s, "GetEnv", "NO_SUCH_VARIABLE").S);
            d.Environment["Long"] = new string('e', 300);
            Assert.Equal(255, Call(s, "GetEnv", "LONG").S.Length);
            Assert.Equal(0, Call(s, "GetEnv", "x").Error);
        }

        [Fact]
        public void IniFilesReadTheReadableFoldersAndWriteTheCopy()
        {
            var s = MakeData();
            string file = Path.Combine(MainDir, "USER.INI");
            File.WriteAllText(file, "[Startup]\r\nPage = \"menu\" \r\n Name=Op\r\n[Other]\r\nX=1\r\n");
            byte[] before = File.ReadAllBytes(file);
            Assert.Equal("menu", Call(s, "WndGetFileProfile", "startup", "page", "d", file).S); // quotes dropped
            Assert.Equal("Op", Call(s, "WndGetFileProfile", "Startup", "Name", "d", "[RUN]:\\USER.INI").S);
            Assert.Equal("d", Call(s, "WndGetFileProfile", "Startup", "None", "d  ", file).S); // default, blanks cut
            Assert.Equal("d", Call(s, "WndGetFileProfile", "Startup", "Page", "d", "USER.INI").S); // the Windows folder: nothing
            string outside = Path.Combine(Tmp.Path, "outside.ini");
            File.WriteAllText(outside, "[A]\r\nB=1\r\n");
            Assert.Equal("no", Call(s, "WndGetFileProfile", "A", "B", "no", outside).S); // outside the sandbox
            Assert.Equal(0, Call(s, "WndPutFileProfile", "Startup", "Page", "alarms", file).I);
            Assert.Equal("alarms", Call(s, "WndGetFileProfile", "Startup", "Page", "d", file).S);
            Assert.Equal(0, Call(s, "WndPutFileProfile", "New", "K", "v", outside).I);
            Assert.Equal("v", Call(s, "WndGetFileProfile", "new", "k", "d", outside).S); // the copy reads back
            Assert.Equal(Codes.InvalidArg, Call(s, "WndPutFileProfile", "", "K", "v", file).I);
            Assert.Equal(before, File.ReadAllBytes(file));
            // the same file by another spelling; device names and a path out of the folder
            Assert.Equal(
                "Op",
                Call(
                    s,
                    "WndGetFileProfile",
                    "Startup",
                    "Name",
                    "d",
                    Path.Combine(MainDir, ".", "x", "..", "USER.INI")
                ).S
            );
            foreach (string dev in new[] { "CON", "NUL", "COM1", "AUX" })
                Assert.Equal("d", Call(s, "WndGetFileProfile", "A", "B", "d", Path.Combine(MainDir, dev)).S);
            Assert.Equal(
                "no",
                Call(
                    s,
                    "WndGetFileProfile",
                    "A",
                    "B",
                    "no",
                    Path.Combine(MainDir, "..", "..", "..", "outside.ini")
                ).S
            );
            // a file above IniFiles.MaxFileBytes is not read (a sparse length: nothing is written)
            string big = Path.Combine(MainDir, "BIG.INI");
            using (var fs = new FileStream(big, FileMode.Create))
            {
                byte[] head = Encoding.ASCII.GetBytes("[A]\r\nB=1\r\n");
                fs.Write(head, 0, head.Length);
                fs.SetLength(IniFiles.MaxFileBytes + 1);
            }
            Assert.Equal("d", Call(s, "WndGetFileProfile", "A", "B", "d", big).S);
            File.Delete(big);
        }

        // ------------------------------------------------------------------ product, version
        [Fact]
        public void ProductInfoAndVersionTexts()
        {
            var s = MakeData();
            Assert.Equal("Plant SCADA", Call(s, "ProductInfo", 0).S);
            Assert.Equal("AVEVA", Call(s, "ProductInfo", 1).S);
            Assert.Equal("8", Call(s, "ProductInfo", 2).S);
            Assert.Equal("50", Call(s, "ProductInfo", 3).S);
            Assert.Equal("8.50.0.368", Call(s, "ProductInfo", 4).S);
            Assert.Equal("", Call(s, "ProductInfo", 5).S);
            Assert.Equal("8", Call(s, "Version", 0).S);
            Assert.Equal("50", Call(s, "Version", 1).S);
            Assert.Equal("0", Call(s, "Version", 2).S);
            Assert.Equal("8.50.0.368", Call(s, "Version", 3).S);
            Assert.Equal("", Call(s, "Version", 4).S);
        }

        [Fact]
        public void CitectInfoGroups()
        {
            var s = MakeData();
            var r = Call(s, "CitectInfo", "General", "", "7");
            Assert.Equal(0, r.I);
            Assert.Equal(Codes.InvalidArg, r.Error);
            Assert.Equal(0, Call(s, "CitectInfo", "General", "", "6").I); // the clock has not run
            s.W.RunFor(90);
            Assert.Equal(90, Call(s, "CitectInfo", "General", "", "6").I);
            r = Call(s, "CitectInfo", "Queue", "NoQueue", "0");
            Assert.Equal(-1, r.I);
            Assert.Equal(Codes.InvalidArg, r.Error);
            Assert.Equal(1L << 20, Call(s, "CitectInfo", "Memory", "KB", "0").I);
            Assert.Equal(0, Call(s, "CitectInfo", "Bogus", "", "0").I);
        }

        // ------------------------------------------------------------------ maps
        [Fact]
        public void MapOpenNamesModesAndErrors()
        {
            var s = MakeData();
            var r = Call(s, "MapOpen");
            Assert.Matches("^\\.Map[0-9A-F]{16}$", r.S); // '.Map' + 16 upper-case hex digits
            string m = r.S;
            Assert.Equal(1, Call(s, "MapExists", m).I);
            Assert.NotEqual(m, Call(s, "MapOpen").S);
            r = Call(s, "MapOpen", m, 0, "");
            Assert.Equal("", r.S);
            Assert.Equal(271, r.Error); // mode 0 and it exists
            Assert.Equal(m, Call(s, "MapOpen", m, 1, "").S); // mode 1 opens it
            r = Call(s, "MapOpen", "Nope", 1, "");
            Assert.Equal(536, r.Error); // mode 1, missing
            r = Call(s, "MapOpen", "", 1, "");
            Assert.Equal(Codes.InvalidArg, r.Error); // empty name with mode 1
            r = Call(s, "MapOpen", "X", 3, "");
            Assert.Equal(Codes.InvalidArg, r.Error); // mode above 2
            r = Call(s, "MapOpen", "X", 0, "NoSuchFunction");
            Assert.Equal(Codes.InvalidArg, r.Error); // the callback must be a public function
            Assert.Equal("Named", Call(s, "MapOpen", "Named", 2, "").S); // mode 2 creates
            Assert.Equal("Named", Call(s, "MapOpen", "Named", 2, "").S); // ... and opens
            Assert.Equal(0, Call(s, "MapExists", "named").I); // names are case-sensitive
            r = Call(s, "MapExists", "");
            Assert.Equal(Codes.InvalidArg, r.Error);
            Assert.Equal(0, Call(s, "MapClose", m).I);
            Assert.Equal(0, Call(s, "MapExists", m).I);
            Assert.Equal(536, Call(s, "MapClose", m).I);
            r = Call(s, "MapClose", "");
            Assert.Equal(Codes.InvalidArg, r.I);
            Assert.Equal(Codes.InvalidArg, r.Error);
        }

        [Fact]
        public void MapKeysAreCaseSensitiveInByteOrder()
        {
            var s = MakeData();
            string m = Call(s, "MapOpen").S;
            Assert.Equal(0, Call(s, "MapValueSet", m, "somekey", "somevalue").I);
            Assert.Equal(0, Call(s, "MapValueSet", m, "SomeKey", "SomeValue").I);
            Assert.Equal(0, Call(s, "MapValueSet", m, "Zeta", 5).I);
            Assert.Equal(3, Call(s, "MapKeyCount", m).I);
            Assert.Equal("SomeKey", Call(s, "MapKeyFirst", m).S); // doc 1106304
            Assert.Equal("Zeta", Call(s, "MapKeyNext", m, "SomeKey").S);
            Assert.Equal("somekey", Call(s, "MapKeyNext", m, "Zeta").S); // byte order: 'Z' < 's'
            Assert.Equal("", Call(s, "MapKeyNext", m, "somekey").S);
            Assert.Equal("", Call(s, "MapKeyNext", m, "notthere").S); // a key not in the map
            Assert.Equal(1, Call(s, "MapKeyExists", m, "SomeKey").I);
            Assert.Equal(0, Call(s, "MapKeyExists", m, "SOMEKEY").I);
            Assert.Equal(0, Call(s, "MapKeyExists", "nomap", "SomeKey").I);
            // set modes
            Assert.Equal(271, Call(s, "MapValueSet", m, "Zeta", 6, 0).I); // mode 0, the key exists
            Assert.Equal(536, Call(s, "MapValueSet", m, "New", 6, 1).I); // mode 1, missing
            Assert.Equal(0, Call(s, "MapValueSet", m, "Zeta", 6, 1).I);
            Assert.Equal(536, Call(s, "MapValueSet", "nomap", "K", 1).I);
            var r = Call(s, "MapValueSet", m, "", 1);
            Assert.Equal(Codes.InvalidArg, r.I);
            Assert.Equal(Codes.InvalidArg, r.Error);
            Assert.Equal(Codes.InvalidArg, Call(s, "MapValueSet", m, "K", 1, 3).I);
            // delete, clear
            Assert.Equal(0, Call(s, "MapKeyDelete", m, "Zeta").I);
            Assert.Equal(536, Call(s, "MapKeyDelete", m, "Zeta").I);
            Assert.Equal(0, Call(s, "MapClear", m).I);
            Assert.Equal(0, Call(s, "MapKeyCount", m).I);
            Assert.Equal("", Call(s, "MapKeyFirst", m).S);
            Assert.Equal(-1, Call(s, "MapKeyCount", "nomap").I);
            Assert.Equal(536, Call(s, "MapClear", "nomap").I);
        }

        [Fact]
        public void MapValuesKeepQualityAndTimestamps()
        {
            var s = MakeData();
            string m = Call(s, "MapOpen").S;
            var v = Value.Real(12.5, 0x40, 133000000000000000UL, 133000000000000001UL, 133000000000000002UL);
            Assert.Equal(0, Call(s, "MapValueSet", m, "K", Ref(v)).I); // a VARIANT argument is dereferenced
            var r = Call(s, "MapValueGet", m, "K");
            Value got = Value.Deref(r.V);
            Assert.Equal(12.5, got.D);
            Assert.Equal(0x40UL, got.Q);
            Assert.Equal(133000000000000001UL, got.Vts);
            Assert.Equal(0, Call(s, "MapValueSetQuality", m, "K", Value.Int(Types.Quality, 0xC0)).I);
            Assert.Equal(0xC0UL, Value.Deref(Call(s, "MapValueGet", m, "K").V).Q);
            Assert.Equal(536, Call(s, "MapValueSetQuality", m, "Nope", Value.Int(Types.Quality, 0xC0)).I);
            // a missing key: a reference to nothing, no error
            r = Call(s, "MapValueGet", m, "Nope");
            Assert.True(r.V.IsNothing);
            Assert.Equal(0, r.Error);
            r = Call(s, "MapValueGet", "", "K");
            Assert.Equal(Codes.InvalidArg, r.Error);
        }

        [Fact]
        public void MapValueGetOfAMissingKeyConvertsToEmptyInCicode()
        {
            // STRING f() RETURN MapValueGet(MapOpen(), "x") : '' without a hardware error
            var s = MakeData(build: pb =>
                pb.Function("f", "", Types.String)
                    .Func("MapOpen", 0)
                    .ConstStr("x")
                    .Func("MapValueGet", 2)
                    .Convert(Types.String)
                    .RetValue()
                    .End()
            );
            var r = s.Call("f");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal("", r.Task.Result!.Value.S);
            Assert.Equal(0, r.Task.Error);
        }

        [Fact]
        public void MapCloseRunsTheCallbackWithTheMapStillOpen()
        {
            // OnClose(STRING m): writes MapKeyCount(m) to Count_Long
            var s = MakeData(build: pb =>
                pb.Function("OnClose", "7")
                    .Begin()
                    .LoadStack(0)
                    .Func("MapKeyCount", 1)
                    .Store("Count_Long")
                    .Ret()
                    .End()
            );
            string m = Call(s, "MapOpen", "CB", 0, "OnClose").S;
            Assert.Equal("CB", m);
            Call(s, "MapValueSet", m, "a", 1);
            Call(s, "MapValueSet", m, "b", 2);
            Assert.Equal(0, Call(s, "MapClose", m).I);
            Assert.Equal(2, s.W.Tags.Get("Count_Long").I);
            Assert.Equal(0, Call(s, "MapExists", m).I);
        }

        // ------------------------------------------------------------------ arrays
        [Fact]
        public void ArrayCreateLimits()
        {
            var s = MakeData();
            var r = Call(s, "ArrayCreate", "A", 0);
            Assert.Equal(-1, r.I);
            Assert.Equal(Codes.OutOfRange, r.Error); // x 0
            r = Call(s, "ArrayCreate", "A", 32766);
            Assert.Equal(Codes.OutOfRange, r.Error); // above 32765
            Assert.True(Call(s, "ArrayCreate", "A", 32765).I >= 0);
            r = Call(s, "ArrayCreate", "A", 1024, 2048, 2); // 4,194,304 items
            Assert.Equal(-1, r.I);
            Assert.Equal(Arrays.OutOfMemory, r.Error); // 'too many items': 272
            long ok = Call(s, "ArrayCreate", "B", 1024, 2048, 1).I; // 2,097,152 items: the limit
            Assert.True(ok >= 0);
            r = Call(s, "ArrayCreate", "C", 32765, 32765, 32765); // the C int product wraps negative
            Assert.Equal(-1, r.I);
            Assert.Equal(Arrays.OutOfMemory, r.Error);
            Assert.Equal(1024, Call(s, "ArrayGetInfo", ok, 0).I);
            Assert.Equal(2048, Call(s, "ArrayGetInfo", ok, 1).I);
            Assert.Equal(1, Call(s, "ArrayGetInfo", ok, 2).I); // z -1 -> 1
            Assert.Equal(-1, Call(s, "ArrayGetInfo", ok, 3).I);
            r = Call(s, "ArrayGetInfo", 999, 0);
            Assert.Equal(-1, r.I);
            Assert.Equal(Codes.BadHandle, r.Error);
        }

        [Fact]
        public void ArrayItemsIndexDirtyAndErrors()
        {
            var s = MakeData();
            long h = Call(s, "ArrayCreate", "Grid", 3, 4, 5).I;
            // fill every item with its (x, y, z) and read it back: the index ((y*X + x)*Z + z)
            for (int x = 0; x < 3; x++)
            for (int y = 0; y < 4; y++)
            for (int z = 0; z < 5; z++)
                Assert.Equal(0, Call(s, "ArraySetString", h, x + "," + y + "," + z, x, y, z).I);
            for (int x = 0; x < 3; x++)
            for (int y = 0; y < 4; y++)
            for (int z = 0; z < 5; z++)
                Assert.Equal(x + "," + y + "," + z, Call(s, "ArrayGetString", h, x, y, z).S);
            var r = Call(s, "ArraySetString", h, "v", 3, 0, 0);
            Assert.Equal(-1, r.I);
            Assert.Equal(Codes.OutOfRange, r.Error); // set out of range: 257
            r = Call(s, "ArrayGetString", h, 0, 4, 0);
            Assert.Equal("", r.S);
            Assert.Equal(Codes.OutOfRange, r.Error); // get out of range: 257
            Assert.Equal(Codes.OutOfRange, Call(s, "ArrayGetInt", h, 3).Error);
            Assert.Equal(Codes.BadHandle, Call(s, "ArrayGetString", 77, 0).Error);
            r = Call(s, "ArraySetString", 77, "v", 0);
            Assert.Equal(0, r.I);
            Assert.Equal(Codes.BadHandle, r.Error);
            // y, z of -1 are 0; ints through '%ld' and atol
            long h2 = Call(s, "ArrayCreate", "Ints", 4).I;
            Assert.Equal(0, Call(s, "ArraySetInt", h2, -12345, 2, -1, -1).I);
            Assert.Equal("-12345", Call(s, "ArrayGetString", h2, 2).S);
            Assert.Equal(-12345, Call(s, "ArrayGetInt", h2, 2, -1, -1).I);
            Assert.Equal("", Call(s, "ArrayGetString", h2, 1).S); // never set: ''
            // dirty: set by a change, not by the same text again
            Assert.Equal(1, Call(s, "ArrayIsDirty", h2).I); // new arrays are dirty
            Assert.Equal(0, Call(s, "ArraySetIsDirty", h2, 0).I);
            Assert.Equal(0, Call(s, "ArrayIsDirty", h2).I);
            Call(s, "ArraySetInt", h2, -12345, 2);
            Assert.Equal(0, Call(s, "ArrayIsDirty", h2).I);
            Call(s, "ArraySetInt", h2, 7, 2);
            Assert.Equal(1, Call(s, "ArrayIsDirty", h2).I);
            Assert.Equal(0, Call(s, "ArrayIsDirty", 999).I);
            // a value longer than 255 characters keeps 255
            Call(s, "ArraySetString", h2, new string('x', 300), 0);
            Assert.Equal(255, Call(s, "ArrayGetString", h2, 0).S.Length);
        }

        [Fact]
        public void ArrayMapCopySwapDestroy()
        {
            var s = MakeData();
            long a = Call(s, "ArrayCreate", "", 2).I;
            Assert.Equal(1, Call(s, "ArrayExists", a).I);
            string map = Call(s, "ArrayGetMapName", a).S;
            Assert.Matches("^\\.Map[0-9A-F]{16}$", map);
            Assert.Equal(1, Call(s, "MapExists", map).I); // the hidden map
            Call(s, "ArraySetString", a, "first", 0);
            long b = Call(s, "ArrayCopy", a).I;
            Assert.NotEqual(a, b);
            Assert.Equal("first", Call(s, "ArrayGetString", b, 0).S);
            Assert.NotEqual(map, Call(s, "ArrayGetMapName", b).S);
            long c = Call(s, "ArrayCreate", "Other", 5).I;
            Assert.Equal(0, Call(s, "ArraySwap", a, c).I);
            Assert.Equal(5, Call(s, "ArrayGetInfo", a, 0).I);
            Assert.Equal("first", Call(s, "ArrayGetString", c, 0).S);
            Assert.Equal(map, Call(s, "ArrayGetMapName", c).S);
            Assert.Equal(0, Call(s, "ArrayDestroy", c).I);
            Assert.Equal(0, Call(s, "ArrayExists", c).I);
            Assert.Equal(0, Call(s, "MapExists", map).I); // its map closed too
            Assert.Equal(0, Call(s, "ArrayDestroy", 999).I); // a bad handle: 0
            var r = Call(s, "ArrayGetMapName", 999);
            Assert.Equal(Codes.BadHandle, r.Error);
            r = Call(s, "ArrayCopy", 999);
            Assert.Equal(-1, r.I);
            Assert.Equal(Codes.BadHandle, r.Error);
            // the lowest free handle is reused
            Assert.Equal(c, Call(s, "ArrayCreate", "Again", 1).I);
        }
    }
}
