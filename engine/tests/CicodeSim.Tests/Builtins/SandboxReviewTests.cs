using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CicodeSim.Builtins;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>No virtual path, however odd, makes the sandbox touch a
    /// file outside its folder (drive-relative segments, UNC shares named like drives, \\?\
    /// paths, alternate data streams, device names, names Win32 would trim).</summary>
    [Collection("World")]
    public sealed class SandboxReviewTests : BuiltinTestBase
    {
        private static bool Windows => Path.DirectorySeparatorChar == '\\';

        private static string[] OddNames(string tag) =>
            new[]
            {
                @"\\srv\c:\" + tag + "a.txt", // a share named like a drive
                @"C:\data\c:" + tag + "b.txt", // a drive-relative segment
                @"C:\data\d:" + tag + "b.txt",
                @"\\?\C:\" + tag + @"c\x.txt", // a \\?\ path
                @"\\.\C:\" + tag + "d.txt", // a \\.\ path
                @"C:\data\" + tag + "e.txt:stream", // an alternate data stream
                @"C:\data\NUL", // device names, in any folder
                @"C:\data\nul.txt",
                @"C:\data\Con.log",
                @"C:\data\conout$",
                @"C:\data\" + tag + "f.", // Win32 trims a trailing dot
                @"C:\data\" + tag + "f",
                @"C:\data\~41", // looks like an escaped name
                @"C:\data\A",
                @"C:\data\" + tag + "<g>.txt", // characters Windows refuses
                @"C:\da?ta\" + tag + "h.txt",
            };

        [Fact]
        public void NoPathLeavesTheSandboxFolder()
        {
            if (!Windows)
                return;
            string tag = "esc" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string folder = Path.Combine(Tmp.Path, "sb");
            string cwd = Directory.GetCurrentDirectory();
            var disk = new Sandbox(
                new SandboxOptions
                {
                    Folder = folder,
                    AnyFolder = true,
                    ReadRoots = new List<string>(),
                }
            );
            var mem = new Sandbox(
                new SandboxOptions
                {
                    InMemory = true,
                    AnyFolder = true,
                    ReadRoots = new List<string>(),
                }
            );
            try
            {
                foreach (string name in OddNames(tag))
                {
                    string full = disk.Full(name);
                    byte[] data = Encoding.ASCII.GetBytes("data of " + name);
                    var f = disk.Open(full, false, true, true, true, 0);
                    var g = mem.Open(mem.Full(name), false, true, true, true, 0);
                    // the two kinds of sandbox give the same answer
                    Assert.True((f == null) == (g == null), name);
                    if (f == null)
                        continue;
                    Assert.Equal(data.Length, disk.Write(f, data, data.Length, 0));
                    Assert.Equal(data, disk.ReadAll(full));
                }
                // every file the sandbox made is inside its folder
                foreach (string root in new[] { cwd, Path.GetPathRoot(cwd)! })
                    Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root), p =>
                        p.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0
                    );
                foreach (var (path, _) in disk.Files())
                {
                    string? b = disk.BackingPath(path);
                    Assert.NotNull(b);
                    Assert.StartsWith(
                        Path.GetFullPath(folder) + "\\",
                        Path.GetFullPath(b!),
                        StringComparison.OrdinalIgnoreCase
                    );
                    Assert.True(File.Exists(b), b);
                }
                // renames and copies go through the same rule
                string src = disk.Full(@"C:\data\" + tag + "src.txt");
                var s = disk.Open(src, false, true, true, true, 0);
                Assert.NotNull(s);
                foreach (string name in OddNames(tag + "r"))
                {
                    disk.Copy(src, disk.Full(name), false, 0);
                    disk.Rename(src, disk.Full(name));
                    src = disk.Files()
                        .Select(x => x.Path)
                        .FirstOrDefault(p => p.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? src;
                }
                foreach (string root in new[] { cwd, Path.GetPathRoot(cwd)! })
                    Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root), p =>
                        p.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0
                    );
            }
            finally
            {
                disk.Clear();
                foreach (string root in new[] { cwd, Path.GetPathRoot(cwd)! })
                foreach (
                    string p in Directory
                        .EnumerateFileSystemEntries(root)
                        .Where(p => p.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList()
                )
                {
                    try
                    {
                        if (Directory.Exists(p))
                            Directory.Delete(p, true);
                        else
                            File.Delete(p);
                    }
                    catch (Exception) { }
                }
            }
        }

        [Fact]
        public void OldSandboxesOfFinishedSimulationsArePruned()
        {
            // a simulation's sandbox is %LOCALAPPDATA%\cicode-sim\sandbox\<project>-<pid>-<n>
            string parent = Path.Combine(Tmp.Path, "sandboxes");
            DateTime old = DateTime.UtcNow - TimeSpan.FromDays(10);
            string Make(string name, bool aged)
            {
                string d = Path.Combine(parent, name, "C", "Data");
                Directory.CreateDirectory(d);
                string f = Path.Combine(d, "log.txt");
                File.WriteAllText(f, "x");
                if (aged)
                {
                    File.SetLastWriteTimeUtc(f, old);
                    foreach (string p in new[] { d, Path.GetDirectoryName(d)!, Path.Combine(parent, name) })
                        Directory.SetLastWriteTimeUtc(p, old);
                }
                return Path.Combine(parent, name);
            }
            string dead = Make("Example-999999-1", true);
            string live = Make("Example-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-1", true);
            string recent = Make("Example-999998-2", false);
            string other = Make("my-files", true);
            Assert.Equal(1, Sandbox.Prune(parent, null, Sandbox.KeepFor));
            Assert.False(Directory.Exists(dead));
            Assert.True(Directory.Exists(live));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(other));
            // a given id is one folder name
            Assert.Equal("___x", new Sandbox(new SandboxOptions { Id = @"..\x" }).Id);
        }

        [Fact]
        public void NetworkFoldersAreNeverLookedUp()
        {
            // a UNC folder outside the readable roots exists only through the sandbox: the
            // question never goes to the network (an SMB connection, a long timeout)
            var sb = new Sandbox(new SandboxOptions { InMemory = true, ReadRoots = new List<string>() });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(sb.FolderExists(@"\\cicodesim-no-such-host\share\sub"));
            Assert.Null(sb.Open(@"\\cicodesim-no-such-host\share\sub\x.txt", false, true, true, true, 0));
            Assert.False(sb.FolderExists(@"\\?\C:\Windows"));
            Assert.True(watch.ElapsedMilliseconds < 2000, watch.ElapsedMilliseconds + " ms");
            // a share root itself takes a new file (no lookup), and then its folder exists
            Assert.NotNull(sb.Open(@"\\cicodesim-no-such-host\share\x.txt", false, true, true, true, 0));
            Assert.True(sb.FolderExists(@"\\cicodesim-no-such-host\share"));
        }
    }
}
