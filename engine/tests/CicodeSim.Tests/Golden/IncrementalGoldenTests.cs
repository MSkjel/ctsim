using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Sim;
using Xunit;

namespace CicodeSim.Tests.Golden
{
    /// <summary>Every file edited and recompiled matches a full compile.</summary>
    public class IncrementalGoldenTests
    {
        [GoldenFact(heavy: true)]
        [Trait("Golden", "heavy")]
        public void EveryEditedFileCompilesAloneAsTheWholeUnitWould()
        {
            var bad = new List<string>();
            int n = 0;
            foreach (string p in new[] { "ExampleSA", "Example" })
            {
                var (c, b) = CicodeSim.Tests.Compiler.CodegenIncrementalTests.EditEveryFile(p, 1);
                n += c;
                foreach (string s in b)
                    bad.Add(p + ": " + s);
            }
            Assert.True(n >= 450, "edits compared: " + n);
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        /// <summary>Disk reloads of a copied project equal a full compile.</summary>
        [GoldenTheory(heavy: true)]
        [Trait("Golden", "heavy")]
        [InlineData("ExampleSA")]
        [InlineData("Example")]
        public void DiskReloadsOfACopiedProjectEqualAFullCompile(string project)
        {
            string dir = CopyCode(project);
            try
            {
                var files = new List<string>(Directory.GetFiles(dir, "*.ci"));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                Assert.True(files.Count >= 3, project);
                using (
                    var sim = new Simulation(
                        new SimulationOptions
                        {
                            ProjectDir = dir,
                            Compile = LiveProgram.All,
                            StartAlarms = false,
                            ClockMode = "paused",
                            Start = "2026-10-01T08:00:00Z",
                            TimeZone = TimeZoneInfo.Utc,
                        }
                    )
                )
                {
                    sim.Start();
                    var stamp = RdbWriter.Stamp.Of(new byte[0]);
                    void Reload(string what, string file, bool? incremental, bool compiled = true)
                    {
                        var r = sim.Invoke(() => sim.Reload(file, null));
                        Assert.True(compiled == r["compiled"].Bool, what + ": " + r.Dump());
                        if (!compiled)
                            return;
                        if (incremental != null)
                            Assert.True(incremental.Value == r["incremental"].Bool, what + ": " + r.Dump());
                        byte[] live = sim.Invoke(() => RdbWriter.Write(sim.Live.Source!.Image(), stamp));
                        byte[] whole = RdbWriter.Write(SourceLibrary.Open(dir).Image(), stamp);
                        Assert.True(
                            System.Linq.Enumerable.SequenceEqual(live, whole),
                            what + ": the running program differs from a full compile"
                        );
                    }
                    string f1 = files.Find(f => Array.IndexOf(File.ReadAllBytes(f), (byte)'"') >= 0)!;
                    var b1 = File.ReadAllBytes(f1);
                    File.WriteAllBytes(f1, Insert(b1, Array.IndexOf(b1, (byte)'"') + 1, "Zz"));
                    Reload("a string changed in " + Path.GetFileName(f1), f1, true);
                    string f2 = files[files.Count / 2];
                    File.WriteAllBytes(f2, Insert(File.ReadAllBytes(f2), 0, "\r\n"));
                    Reload("a blank line first in " + Path.GetFileName(f2), f2, true);
                    string f3 = files[files.Count - 1];
                    var b3 = File.ReadAllBytes(f3);
                    File.WriteAllBytes(
                        f3,
                        Insert(b3, b3.Length, "\r\nINT FUNCTION ZzReviewD()\r\n\tRETURN 42;\r\nEND\r\n")
                    );
                    Reload("a new function in " + Path.GetFileName(f3), f3, false);
                    Assert.Equal("42", Call(sim, "ZzReviewD()"));
                    string f4 = files[0];
                    var b4 = File.ReadAllBytes(f4);
                    File.WriteAllBytes(
                        f4,
                        Insert(b4, b4.Length, "\r\nINT FUNCTION ZzBroken()\r\n\tRETURN 1 +;\r\nEND\r\n")
                    );
                    Reload("an error in " + Path.GetFileName(f4), f4, null, false);
                    File.WriteAllBytes(f4, b4);
                    Reload("the error fixed in " + Path.GetFileName(f4), f4, null);
                    string f5 = files[files.Count / 3];
                    File.WriteAllBytes(
                        f5,
                        Insert(File.ReadAllBytes(f5), 0, "MODULE INT mZzReviewD = 5;\r\n")
                    );
                    Reload("a module variable in " + Path.GetFileName(f5), f5, false);
                    var b6 = File.ReadAllBytes(f3);
                    int k = Encoding.ASCII.GetString(b6).LastIndexOf("RETURN 42;", StringComparison.Ordinal);
                    b6[k + 8] = (byte)'3';
                    File.WriteAllBytes(f3, b6);
                    Reload("the new function's body", f3, true);
                    Assert.Equal("43", Call(sim, "ZzReviewD()"));
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(Path.GetDirectoryName(dir)!, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>The project's .ci files, tables (not the page tables) and compiled tables in a
        /// new temporary folder of the same name (about 20 to 30 MB).</summary>
        private static string CopyCode(string project)
        {
            string src = Path.Combine(GoldenFiles.User, project);
            string dst = Path.Combine(
                Path.GetTempPath(),
                "ctsim-test-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                project
            );
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (
                    n.EndsWith(".ci")
                    || (n.EndsWith(".dbf") && !n.StartsWith("pg"))
                    || (n.StartsWith("_") && n.EndsWith(".rdb"))
                )
                    File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            }
            return dst;
        }

        private static byte[] Insert(byte[] b, int at, string ascii)
        {
            byte[] x = Encoding.ASCII.GetBytes(ascii);
            var r = new byte[b.Length + x.Length];
            Array.Copy(b, 0, r, 0, at);
            Array.Copy(x, 0, r, at, x.Length);
            Array.Copy(b, at, r, at + x.Length, b.Length - at);
            return r;
        }

        private static string Call(Simulation sim, string text)
        {
            CallOutcome? o = null;
            var done = new ManualResetEventSlim(false);
            sim.Invoke(() => sim.StartCall(text, r =>
                {
                    o = r;
                    done.Set();
                }));
            Assert.True(done.Wait(20000), text);
            return o!.Result;
        }
    }
}
