using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>The sandbox and the File* built-ins.</summary>
    [Collection("World")]
    public sealed class FileTests : BuiltinTestBase
    {
        /// <summary>Real files are Windows paths (the sandbox's virtual paths are).</summary>
        private static bool Windows => Path.DirectorySeparatorChar == '\\';

        /// <summary>A real folder the sandbox may read: data.txt in it.</summary>
        private string RealDir()
        {
            string d = Path.Combine(Tmp.Path, "real");
            Directory.CreateDirectory(d);
            File.WriteAllBytes(
                Path.Combine(d, "data.txt"),
                Encoding.ASCII.GetBytes("one\r\ntwo\nthree\rfour")
            );
            File.WriteAllBytes(Path.Combine(d, "b.csv"), Encoding.ASCII.GetBytes("x,y"));
            return d;
        }

        /// <summary>The functions of the file tests: each calls one built-in with its arguments.</summary>
        private Sim Files(SandboxOptions? opt = null)
        {
            var s = Make(pb =>
            {
                pb.Function("open", "77", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FileOpen", 2)
                    .RetValue()
                    .End();
                pb.Function("close", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FileClose", 1)
                    .RetValue()
                    .End();
                pb.Function("readln", "4", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .Func("FileReadLn", 1)
                    .RetValue()
                    .End();
                pb.Function("read", "44", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FileRead", 2)
                    .RetValue()
                    .End();
                pb.Function("write", "47", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FileWrite", 2)
                    .RetValue()
                    .End();
                pb.Function("writeln", "47", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FileWriteLn", 2)
                    .RetValue()
                    .End();
                pb.Function("seek", "44", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FileSeek", 2)
                    .RetValue()
                    .End();
                pb.Function("size", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FileSize", 1)
                    .RetValue()
                    .End();
                pb.Function("eof", "4", Types.Long).Begin().LoadStack(0).Func("FileEOF", 1).RetValue().End();
                pb.Function("exist", "7", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FileExist", 1)
                    .RetValue()
                    .End();
                pb.Function("del", "7", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FileDelete", 1)
                    .RetValue()
                    .End();
                pb.Function("ren", "77", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FileReName", 2)
                    .RetValue()
                    .End();
                pb.Function("copy", "774", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .LoadStack(2)
                    .Func("FileCopy", 3)
                    .RetValue()
                    .End();
                pb.Function("find", "74", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FileFind", 2)
                    .RetValue()
                    .End();
                pb.Function("findclose", "", Types.Long).Func("FileFindClose", 0).RetValue().End();
                Ret(pb, "existFg", "FileExist", @"C:\x.txt");
                Ret(pb, "copyFg", "FileCopy", @"C:\x.txt", @"C:\y.txt", 0);
                Ret(pb, "findFg", "FileFind", @"C:\*.*", 0);
                Ret(pb, "findCloseFg", "FileFindClose");
                // findall(pattern): every FileFind answer in one task, a line each in C:\found.txt,
                // then FileFindClose's result
                pb.Function("findall", "7", Types.Long)
                    .Begin()
                    .StackVar(Types.String, Types.Long)
                    .ConstStr(@"C:\found.txt")
                    .ConstStr("w")
                    .Func("FileOpen", 2)
                    .StoreStack(2)
                    .LoadStack(0)
                    .ConstInt(0)
                    .Func("FileFind", 2)
                    .StoreStack(1)
                    .Label("loop")
                    .LoadStack(1)
                    .ConstStr("")
                    .Op("Ne")
                    .BrFalse("done")
                    .LoadStack(2)
                    .LoadStack(1)
                    .Func("FileWriteLn", 2)
                    .Op("Pop")
                    .ConstStr("")
                    .ConstInt(0)
                    .Func("FileFind", 2)
                    .StoreStack(1)
                    .Branch("loop")
                    .Label("done")
                    .LoadStack(2)
                    .Func("FileFindClose", 0)
                    .Convert(Types.String)
                    .Func("FileWriteLn", 2)
                    .Op("Pop")
                    .LoadStack(2)
                    .Func("FileClose", 1)
                    .RetValue()
                    .End();
                pb.Function("gettime", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FileGetTime", 1)
                    .RetValue()
                    .End();
                pb.Function("settime", "44", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FileSetTime", 2)
                    .RetValue()
                    .End();
                pb.Function("make", "7777", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .LoadStack(2)
                    .LoadStack(3)
                    .Func("FileMakePath", 4)
                    .RetValue()
                    .End();
                // split(path): FileSplitPath(path, d, p, n, e); RETURN d|p|n|e
                pb.Function("split", "7", Types.String)
                    .Begin()
                    .StackVar(Types.String, Types.String, Types.String, Types.String)
                    .LoadStack(0)
                    .AddrStack(1)
                    .AddrStack(2)
                    .AddrStack(3)
                    .AddrStack(4)
                    .Func("FileSplitPath", 5)
                    .Op("Pop")
                    .LoadStack(1)
                    .ConstStr("|")
                    .Op("Add")
                    .LoadStack(2)
                    .Op("Add")
                    .ConstStr("|")
                    .Op("Add")
                    .LoadStack(3)
                    .Op("Add")
                    .ConstStr("|")
                    .Op("Add")
                    .LoadStack(4)
                    .Op("Add")
                    .RetValue()
                    .End();
                // block(h, n): FileReadBlock(h, s, n) as "n:s"
                pb.Function("block", "44", Types.String)
                    .Begin()
                    .StackVar(Types.String)
                    .LoadStack(0)
                    .AddrStack(2)
                    .LoadStack(1)
                    .Func("FileReadBlock", 3)
                    .Convert(Types.String)
                    .ConstStr(":")
                    .Op("Add")
                    .LoadStack(2)
                    .Op("Add")
                    .RetValue()
                    .End();
                // quiet(name, mode): ErrSet(1); h = FileOpen(name, mode); RETURN h : IsError() (the live case's shape)
                pb.Function("quiet", "77", Types.String)
                    .Begin()
                    .StackVar(Types.Long)
                    .ConstInt(1)
                    .Func("ErrSet", 1)
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FileOpen", 2)
                    .StoreStack(2)
                    .LoadStack(2)
                    .Convert(Types.String)
                    .ConstStr(";")
                    .Op("Add")
                    .Func("IsError", 0)
                    .Convert(Types.String)
                    .Op("Add")
                    .RetValue()
                    .End();
            });
            s.Host.UseSandbox(
                opt
                    ?? new SandboxOptions
                    {
                        InMemory = true,
                        ReadRoots = new List<string> { Path.Combine(Tmp.Path, "real") },
                    }
            );
            return s;
        }

        [Fact]
        public void PathsAndPatterns()
        {
            Assert.Equal(@"C:\a\c.txt", Sandbox.Normal(@"C:/a/b/../c.txt", @"D:\x"));
            Assert.Equal(@"D:\x\y.txt", Sandbox.Normal("y.txt", @"D:\x"));
            Assert.Equal(@"D:\y.txt", Sandbox.Normal(@"\y.txt", @"D:\x"));
            Assert.Equal(@"C:\", Sandbox.Normal(@"C:\", @"D:\x"));
            Assert.Equal(@"\\srv\share\a.txt", Sandbox.Normal(@"\\srv\share\a.txt", @"D:\x"));
            Assert.True(Sandbox.Match("Report.TXT", "*.txt"));
            Assert.True(Sandbox.Match("Report", "*.*"));
            Assert.True(Sandbox.Match("Report", "Rep*."));
            Assert.False(Sandbox.Match("Report.txt", "Rep*."));
            Assert.True(Sandbox.Match("ab", "a?"));
            Assert.False(Sandbox.Match("a", "a?"));
            // _splitpath / _makepath (the Universal CRT's rules)
            CicodeSim.Builtins.Files.SplitPath(
                @"C:\dir\sub\file.tar.gz",
                out string d,
                out string p,
                out string n,
                out string e
            );
            Assert.Equal((@"C:", @"\dir\sub\", "file.tar", ".gz"), (d, p, n, e));
            CicodeSim.Builtins.Files.SplitPath(@"..\.bashrc", out d, out p, out n, out e);
            Assert.Equal(("", @"..\", "", ".bashrc"), (d, p, n, e));
            Assert.Equal(@"C:\dir\file.txt", CicodeSim.Builtins.Files.MakePath("C", @"\dir", "file", "txt"));
            Assert.Equal(
                @"C:\dir/file.txt",
                CicodeSim.Builtins.Files.MakePath("Cx", @"\dir/", "file", ".txt")
            );
        }

        [Fact]
        public void OpenOfAMissingFileHaltsOrReturnsMinus1WithErrSet()
        {
            // live: conformance/runtime/functions/Conf_FailingBuiltin.json, fileopen=-1;err=262
            var s = Files();
            Assert.Equal("-1;262", S(s.Result("quiet", @"C:\NoSuchFolderXyz\none.txt", "r")));
            var r = s.Call("open", @"C:\NoSuchFolderXyz\none.txt", "r"); // ErrSet 0: raise(262, 1) halts
            Assert.Equal(TaskState.Halt, r.Task.State);
            Assert.Equal(262, r.Task.HaltCode);
        }

        [Fact]
        public void WritesGoToTheSandboxNeverToTheRealFile()
        {
            if (!Windows)
                return;
            string real = RealDir();
            string sandbox = Path.Combine(Tmp.Path, "sandbox");
            var s = Files(
                new SandboxOptions
                {
                    Folder = sandbox,
                    ReadRoots = new List<string> { real },
                }
            );
            string data = Path.Combine(real, "data.txt");
            byte[] before = File.ReadAllBytes(data);
            // read a real file
            long h = s.Result("open", data, "r").I;
            Assert.Equal("one", S(s.Result("readln", h)));
            Assert.Equal(0, s.Result("close", h).I);
            // append to it: copied into the sandbox first
            h = s.Result("open", data, "a").I;
            Assert.Equal(7, s.Result("writeln", h, "added").I);
            Assert.Equal(0, s.Result("close", h).I);
            Assert.Equal(before, File.ReadAllBytes(data)); // the real file is untouched
            string backing = Path.Combine(sandbox, real.Substring(0, 1), real.Substring(3), "data.txt");
            Assert.True(File.Exists(backing), backing);
            Assert.Equal(
                Encoding.ASCII.GetString(before) + "added\r\n",
                Encoding.ASCII.GetString(File.ReadAllBytes(backing))
            );
            // reading now sees the sandbox copy
            h = s.Result("open", data, "r").I;
            Assert.Equal("one", S(s.Result("readln", h)));
            Assert.Equal("two", S(s.Result("readln", h)));
            Assert.Equal("three", S(s.Result("readln", h)));
            Assert.Equal("fouradded", S(s.Result("readln", h)));
            Assert.Equal(1, s.Result("eof", h).I);
            // a new file: only in the sandbox
            string fresh = Path.Combine(real, "new.log");
            long w = s.Result("open", fresh, "w").I;
            Assert.Equal(5, s.Result("write", w, "hello").I);
            Assert.False(File.Exists(fresh));
            Assert.True(
                File.Exists(Path.Combine(sandbox, real.Substring(0, 1), real.Substring(3), "new.log"))
            );
            Assert.Equal("hello", Encoding.ASCII.GetString(s.Host.Sandbox.ReadAll(fresh)!));
            // deleting a real file is refused (read-only): 293 with the halt flag
            var r = s.Call("del", Path.Combine(real, "b.csv"));
            Assert.Equal(TaskState.Halt, r.Task.State);
            Assert.Equal(293, r.Task.HaltCode);
            Assert.True(File.Exists(Path.Combine(real, "b.csv")));
            s.Host.Sandbox.Clear();
        }

        [Fact]
        public void ModesAndReadLnRules()
        {
            var s = Files(
                new SandboxOptions
                {
                    InMemory = true,
                    AnyFolder = true,
                    ReadRoots = new List<string>(),
                }
            );
            string f = @"C:\Data\log.txt";
            Assert.Equal("-1;262", S(s.Result("quiet", f, "r"))); // missing
            Assert.Equal("-1;262", S(s.Result("quiet", f, ""))); // no mode letter: read / write of an existing file
            long h = s.Result("open", f, "w").I;
            Assert.Equal(0, h);
            // 'w' is write only: a read raises 263 with the halt flag
            var bad = s.Call("readln", h);
            Assert.Equal(263, bad.Task.HaltCode);
            s.Result("write", h, "a\r\nb\rc\nd\r\n" + new string('y', 200));
            s.Result("write", h, new string('y', 100) + "\r\n");
            Assert.Equal(0, s.Result("close", h).I);
            h = s.Result("open", f, "r").I;
            Assert.Equal(312, s.Result("size", h).I);
            Assert.Equal(293, s.Call("del", f).Task.HaltCode); // open: Windows refuses to delete it
            Assert.Equal("a", S(s.Result("readln", h)));
            Assert.Equal("b", S(s.Result("readln", h)));
            Assert.Equal("c", S(s.Result("readln", h)));
            Assert.Equal("d", S(s.Result("readln", h)));
            // a line of 300: its first 255 and 275 without the halt flag
            var lng = s.Call("readln", h);
            Assert.Equal(TaskState.Stop, lng.Task.State);
            Assert.Equal(275, lng.Task.Error);
            Assert.Equal(new string('y', 255), S(lng.Task.Result!.Value));
            Assert.Equal(new string('y', 45), S(s.Result("readln", h)));
            Assert.Equal(1, s.Result("eof", h).I);
            // past the end: "" and 294 without the halt flag
            var eof = s.Call("readln", h);
            Assert.Equal(TaskState.Stop, eof.Task.State);
            Assert.Equal(294, eof.Task.Error);
            // FileSeek / FileRead
            Assert.Equal(3, s.Result("seek", h, 3).I);
            Assert.Equal("b\rc", S(s.Result("read", h, 3)));
            var big = s.Call("read", h, 255);
            Assert.Equal(257, big.Task.Error); // 255+: 257, not halting
            Assert.Equal("0:", S(s.Result("block", h, 0)));
            Assert.Equal("2:\nd", S(s.Result("block", h, 2)));
            // a negative seek: 274 with the halt flag, the offset returned
            var neg = s.Call("seek", h, -5);
            Assert.Equal(274, neg.Task.HaltCode);
            Assert.Equal(0, s.Result("close", h).I);
            // a bad handle: 269 with the halt flag
            var cl = s.Call("close", h);
            Assert.Equal(TaskState.Halt, cl.Task.State);
            Assert.Equal(269, cl.Task.HaltCode);
            // 'a' appends; 'r+' reads and writes
            h = s.Result("open", f, "a").I;
            s.Result("write", h, "Z");
            s.Result("close", h);
            Assert.EndsWith("\r\nZ", Encoding.ASCII.GetString(s.Host.Sandbox.ReadAll(f)!));
            h = s.Result("open", f, "r+").I;
            Assert.Equal(1, s.Result("write", h, "A").I);
            s.Result("seek", h, 0);
            Assert.Equal("A", S(s.Result("read", h, 1)));
            // 'e' opens without sharing: a second open fails
            long ex = s.Result("open", @"C:\Data\x.txt", "we").I;
            Assert.Equal("-1;262", S(s.Result("quiet", @"C:\Data\x.txt", "r")));
            s.Result("close", ex);
            Assert.NotEqual("-1;262", S(s.Result("quiet", @"C:\Data\x.txt", "r")));
        }

        [Fact]
        public void ExistFindCopyRenameDelete()
        {
            if (!Windows)
                return;
            string real = RealDir();
            var s = Files(
                new SandboxOptions
                {
                    InMemory = true,
                    ReadRoots = new List<string> { real },
                }
            );
            Assert.Equal(1, s.Result("exist", Path.Combine(real, "data.txt")).I);
            Assert.Equal(1, s.Result("exist", real).I); // a folder (_access)
            Assert.Equal(0, s.Result("exist", Path.Combine(real, "none.txt")).I);
            Assert.Equal(0, s.Result("exist", @"C:\Windows\win.ini").I); // not a readable root
            // FileExist blocks: in foreground 282 with the halt flag
            var fg = s.Foreground("existFg");
            Assert.Equal(TaskState.Halt, fg.State);
            Assert.Equal(282, fg.HaltCode);
            // FileCopy in foreground: the request fails with 282, raised as 293 with the halt flag
            var cfg = s.Foreground("copyFg");
            Assert.Equal(TaskState.Halt, cfg.State);
            Assert.Equal(293, cfg.HaltCode);
            // FileFind / FileFindClose need a task: foreground code has none (274 / 282, not halting)
            var ffg = s.Foreground("findFg");
            Assert.Equal(TaskState.Stop, ffg.State);
            Assert.Equal(274, ffg.Error);
            Assert.Equal("", S(ffg.Result!.Value));
            var fcfg = s.Foreground("findCloseFg");
            Assert.Equal(282, fcfg.Error);
            Assert.Equal("282", S(fcfg.Result!.Value));
            // copy a real file into the sandbox: an empty extension takes the source's
            Assert.Equal(
                0,
                s.Result("copy", Path.Combine(real, "data.txt"), Path.Combine(real, "copy"), 0).I
            );
            Assert.Equal(1, s.Result("exist", Path.Combine(real, "copy.txt")).I);
            Assert.Equal(
                261,
                s.Result("copy", Path.Combine(real, "none.*"), Path.Combine(real, "x.txt"), 0).I
            );
            Assert.Equal(0, s.Result("copy", Path.Combine(real, "*.csv"), Path.Combine(real, "*.bak"), 2).I); // async: 0
            Assert.Equal(1, s.Result("exist", Path.Combine(real, "b.bak")).I);
            // FileFind: the sandbox's and the real files in name order
            string[] Found(string pattern)
            {
                Assert.Equal(0, s.Result("findall", pattern).I);
                string txt = Encoding.ASCII.GetString(s.Host.Sandbox.ReadAll(@"C:\found.txt")!);
                return txt.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            }
            var names = Found(Path.Combine(real, "*.*")).Select(Path.GetFileName).ToList();
            // the search ended with "": FileFindClose then finds none (370)
            Assert.Equal(new[] { "b.bak", "b.csv", "copy.txt", "data.txt", "370" }, names);
            // a path without a wildcard: one answer, then the search is over
            Assert.Equal(new[] { Path.Combine(real, "b.csv"), "370" }, Found(Path.Combine(real, "b.csv")));
            Assert.Equal("", S(s.Result("find", "", 0))); // a new task has no search: 274
            Assert.Equal(370, s.Result("findclose").I);
            // rename with wildcards: only sandbox files move
            Assert.Equal(0, s.Result("ren", Path.Combine(real, "copy.txt"), Path.Combine(real, "*.old")).I);
            Assert.Equal(1, s.Result("exist", Path.Combine(real, "copy.old")).I);
            var rr = s.Call("ren", Path.Combine(real, "data.txt"), Path.Combine(real, "d2.txt"));
            Assert.Equal(293, rr.Task.HaltCode);
            Assert.True(File.Exists(Path.Combine(real, "data.txt")));
            // delete: sandbox files only
            Assert.Equal(0, s.Result("del", Path.Combine(real, "*.old")).I);
            Assert.Equal(0, s.Result("exist", Path.Combine(real, "copy.old")).I);
            var none = s.Call("del", Path.Combine(real, "nothing.*"));
            Assert.Equal(293, none.Task.HaltCode);
            Assert.DoesNotContain(Directory.EnumerateFiles(real), p =>
                p.EndsWith(".bak") || p.EndsWith(".old")
            );
        }

        [Fact]
        public void SplitMakePathAndTimes()
        {
            var s = Files(
                new SandboxOptions
                {
                    InMemory = true,
                    AnyFolder = true,
                    ReadRoots = new List<string>(),
                }
            );
            Assert.Equal(@"C:|\a\b\|name|.ext", S(s.Result("split", @"C:\a\b\name.ext")));
            Assert.Equal(@"C:\a\b\name.ext", S(s.Result("make", "C", @"\a\b", "name", "ext")));
            Assert.Equal("name", S(s.Result("make", "", "", "name", "")));
            // a sandbox file's time is the simulated clock's (even seconds, local = UTC here)
            long now = Clocks.UnixSeconds(s.Run.Vm.Clock);
            long h = s.Result("open", @"C:\t.txt", "w").I;
            s.Result("write", h, "x");
            Assert.Equal(now & ~1L, s.Result("gettime", h).I);
            Assert.Equal(274, s.Result("settime", h, 100).I); // before 1980
            Assert.Equal(0, s.Result("settime", h, 1000000001).I);
            Assert.Equal(1000000000, s.Result("gettime", h).I);
        }

        [Fact]
        public void FileChangesGoToTheTraceAndTheQuotaHolds()
        {
            var s = Files(
                new SandboxOptions
                {
                    InMemory = true,
                    AnyFolder = true,
                    ReadRoots = new List<string>(),
                    MaxBytes = 10,
                }
            );
            var sw = new StringWriter();
            s.W.OpenTrace(stream: sw);
            long h = s.Result("open", @"C:\q.txt", "w").I;
            Assert.Equal(8, s.Result("write", h, "12345678").I);
            var r = s.Call("write", h, "abcd"); // past 10 bytes: 264 with the halt flag
            Assert.Equal(264, r.Task.HaltCode);
            s.W.Trace!.Flush();
            string trace = sw.ToString();
            Assert.Contains("\"ev\":\"file\"", trace);
            Assert.Contains("\"op\":\"create\"", trace);
            Assert.Contains("\"op\":\"write\"", trace);
            Assert.Contains("q.txt", trace);
        }

        [Fact]
        public void DefaultSandboxFolderIsUnderLocalAppData()
        {
            var sb = new Sandbox(new SandboxOptions { Id = "unit-test-id" });
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            if (local.Length > 0)
                Assert.Equal(Path.Combine(local, "cicode-sim", "sandbox", "unit-test-id"), sb.Folder);
            Assert.False(Directory.Exists(sb.Folder!)); // made at the first write only
            Assert.DoesNotContain("ProgramData", sb.Folder!, StringComparison.OrdinalIgnoreCase);
        }
    }
}
