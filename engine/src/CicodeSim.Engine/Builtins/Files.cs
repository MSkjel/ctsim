using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>File built-ins over the simulation's sandbox.</summary>
    public static class Files
    {
        private const int CannotOpen = 262,
            CannotRead = 263,
            CannotWrite = 264,
            FileError = 293,
            FileEof = 294,
            StringTooLong = 275;

        internal static string FullName(SimHost h, string name) => h.Sandbox.Full(Misc.PathOf(h, name));

        private static ulong Now(CicodeTask t) => t.Vm.Clock.NowFiletime();

        private static OpenFile? Handle(SimHost h, long hd) => h.OpenFiles.Get((int)hd);

        /// <summary>A bad handle: raise(269, 1).</summary>
        private static OpenFile? Check(SimHost h, CicodeTask t, long hd)
        {
            var f = Handle(h, hd);
            if (f == null)
                t.RaiseError(Codes.BadHandle, true);
            return f;
        }

        // ------------------------------------------------------------------ C runtime path rules
        /// <summary>The Universal CRT's _splitpath: drive (the first two characters when the
        /// second is ':'), folder (up to the last '\' or '/'), name, extension (from the last '.'
        /// after the folder).</summary>
        public static void SplitPath(
            string path,
            out string drive,
            out string dir,
            out string fname,
            out string ext
        )
        {
            string p = path;
            drive = "";
            if (p.Length >= 2 && p[1] == ':')
            {
                drive = p.Substring(0, 2);
                p = p.Substring(2);
            }
            int lastSlash = -1,
                dot = -1;
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i] == '\\' || p[i] == '/')
                    lastSlash = i;
                else if (p[i] == '.')
                    dot = i;
            }
            dir = lastSlash >= 0 ? p.Substring(0, lastSlash + 1) : "";
            string rest = lastSlash >= 0 ? p.Substring(lastSlash + 1) : p;
            int d = dot > lastSlash ? dot - lastSlash - 1 : -1;
            if (d >= 0)
            {
                fname = rest.Substring(0, d);
                ext = rest.Substring(d);
            }
            else
            {
                fname = rest;
                ext = "";
            }
        }

        /// <summary>The Universal CRT's _makepath: the drive's first letter and ':', the folder
        /// with a '\' after it unless it ends in one, the name, the extension with a '.' before it
        /// unless it starts with one.</summary>
        public static string MakePath(string drive, string dir, string fname, string ext)
        {
            var sb = new System.Text.StringBuilder();
            if (drive.Length > 0)
            {
                sb.Append(drive[0]);
                sb.Append(':');
            }
            if (dir.Length > 0)
            {
                sb.Append(dir);
                char last = dir[dir.Length - 1];
                if (last != '\\' && last != '/')
                    sb.Append('\\');
            }
            sb.Append(fname);
            if (ext.Length > 0)
            {
                if (ext[0] != '.')
                    sb.Append('.');
                sb.Append(ext);
            }
            return sb.ToString();
        }

        /// <summary>A file some handle has open (Windows refuses to delete or rename it: _sopen
        /// does not share delete).</summary>
        private static bool IsOpen(SimHost h, string full)
        {
            string k = Sandbox.KeyOf(full);
            foreach (var (_, o) in h.OpenFiles.Live())
                if (Sandbox.KeyOf(o.F.Path) == k)
                    return true;
            return false;
        }

        /// <summary>The folder and the name pattern of a full path.</summary>
        private static void FolderAndName(string full, out string folder, out string name)
        {
            CicodeSim.Project.WinPath.Split(full, out folder, out name);
        }

        // ------------------------------------------------------------------ open / close
        [Builtin(
            "FileOpen",
            Doc = "1105370",
            Effects = "opens a file of the sandbox ('w' / 'a' create one there, a real project file opened for writing is copied into the sandbox first)",
            Errors = "262 with the halt flag: the file cannot be opened (missing for 'r' / 'r+' / no mode letter, a missing folder, a folder, open without sharing, no room) (returns -1)",
            Cases = "runtime/functions/Conf_FailingBuiltin.json",
            Notes = "Mode 'r' read, 'w' create or truncate (write only), 'a' open or create and start at the end (read and write), any other first letter read and write of an existing file; '+' after it reads and writes, 'e' then opens without sharing. Real files under the readable roots (the unit's project folders and the citect.ini folder) are read-only; nothing outside the sandbox is ever written. At most SandboxOptions.MaxOpenFiles (512) open files."
        )]
        private static Value FileOpen(SimHost h, CicodeTask t, ArgList a)
        {
            string full = FullName(h, A.S(a.Get(0)));
            string mode = A.S(a.Get(1));
            int flags;
            int k = 0;
            char c = mode.Length > 0 ? mode[0] : '\0';
            switch (c)
            {
                case 'A':
                case 'a':
                    flags = 0x800a;
                    k = 1;
                    break;
                case 'R':
                case 'r':
                    flags = 0x8000;
                    k = 1;
                    break;
                case 'W':
                case 'w':
                    flags = 0x8301;
                    k = 1;
                    break;
                default:
                    flags = 0x8002;
                    break;
            }
            c = k < mode.Length ? mode[k] : '\0';
            if (c == '+')
            {
                flags = (flags & 0xfffe) | 2;
                k++;
                c = k < mode.Length ? mode[k] : '\0';
            }
            bool exclusive = c == 'e' || c == 'E';
            var sb = h.Sandbox;
            if (h.OpenFiles.Count >= sb.MaxOpenFiles)
            {
                t.RaiseError(CannotOpen, true);
                return W.I(-1);
            }
            // a file open without sharing, or opened now without sharing, refuses a second open
            string key = Sandbox.KeyOf(full);
            foreach (var (_, o) in h.OpenFiles.Live())
                if (Sandbox.KeyOf(o.F.Path) == key && (o.Exclusive || exclusive))
                {
                    t.RaiseError(CannotOpen, true);
                    return W.I(-1);
                }
            ulong now = Now(t);
            // the open without the append flag; a write-only or append open that fails is
            // retried as create + truncate + read / write
            int first = flags & ~8;
            SandboxFile? f = Open(sb, full, first, now);
            if (f == null && (flags & 9) != 0)
                f = Open(sb, full, 0x8302, now);
            else if (f != null && (flags & 8) != 0)
                f.Position = sb.Length(f);
            if (f == null)
            {
                t.RaiseError(CannotOpen, true);
                return W.I(-1);
            }
            return W.I(h.OpenFiles.Add(new OpenFile { F = f, Exclusive = exclusive }));
        }

        private static SandboxFile? Open(Sandbox sb, string full, int flags, ulong now)
        {
            int acc = flags & 3;
            bool read = acc == 0 || acc == 2,
                write = acc == 1 || acc == 2;
            return sb.Open(full, read, write, (flags & 0x100) != 0, (flags & 0x200) != 0, now);
        }

        [Builtin(
            "FileClose",
            Doc = "1105361",
            Effects = "closes the file",
            Errors = "269 with the halt flag: a bad handle (returned)"
        )]
        private static Value FileClose(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.I(a.Get(0));
            if (Handle(h, hd) == null)
            {
                t.RaiseError(Codes.BadHandle, true);
                return W.I(Codes.BadHandle);
            }
            h.OpenFiles.Free(hd);
            return W.I(0);
        }

        // ------------------------------------------------------------------ reading
        /// <summary>read up to n bytes at the position; -1 for a bad handle
        /// (269, halt) or a file not open for reading (263, halt).</summary>
        private static int ReadBytes(SimHost h, CicodeTask t, long hd, byte[] buf, int n)
        {
            var f = Check(h, t, hd);
            if (f == null)
                return -1;
            int k = h.Sandbox.Read(f.F, buf, n);
            if (k < 0)
                t.RaiseError(CannotRead, true);
            return k;
        }

        [Builtin(
            "FileRead",
            Doc = "1105372",
            Errors = "257 a length of 255 or more (raised, \"\"); 294 at the end of the file (raised, \"\"); 269 with the halt flag: a bad handle; 263 with the halt flag: not open for reading",
            Notes = "Up to Length bytes from the position (moving it on), the text up to a NUL."
        )]
        private static Value FileRead(SimHost h, CicodeTask t, ArgList a)
        {
            long len = A.I(a.Get(1));
            if ((ushort)len >= 0xff)
            {
                W.Raise(t, Codes.OutOfRange);
                return W.Empty;
            }
            var buf = new byte[0x100];
            int n = ReadBytes(h, t, A.I(a.Get(0)), buf, (int)(ushort)len);
            if (n < 0)
                return W.Empty;
            if (n == 0)
                W.Raise(t, FileEof);
            return W.S(Cp1252.DecodeZ(buf, 0, n));
        }

        [Builtin(
            "FileReadBlock",
            Doc = "1105373",
            Errors = "274 a length of 255 or more (raised, returns -1); 269 with the halt flag: a bad handle (returns -1); 263 with the halt flag: not open for reading",
            Effects = "writes the bytes read into sBuffer",
            Notes = "Returns the number of bytes read (0 at the end of the file, no error)."
        )]
        private static Value FileReadBlock(SimHost h, CicodeTask t, ArgList a)
        {
            long len = A.I(a.Get(2));
            W.Out(t, a.Get(1), "");
            if ((ushort)len >= 0xff)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var buf = new byte[0x100];
            int n = ReadBytes(h, t, A.I(a.Get(0)), buf, (int)(ushort)len);
            if (n > 0)
                W.Out(t, a.Get(1), Cp1252.DecodeZ(buf, 0, n));
            return W.I(n);
        }

        [Builtin(
            "FileReadLn",
            Doc = "1105374",
            Errors = "294 at the end of the file (raised, \"\"); 275 a line longer than 255 characters (raised, its first 255, the next read goes on there); 269 with the halt flag: a bad handle; 263 with the halt flag: not open for reading",
            Notes = "The line without its end (CR LF, CR or LF); the text up to a NUL."
        )]
        private static Value FileReadLn(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.I(a.Get(0));
            var f = Check(h, t, hd);
            if (f == null)
                return W.Empty;
            long start = f.F.Position;
            var buf = new byte[0x102];
            int n = h.Sandbox.Read(f.F, buf, 0x102);
            if (n < 0)
            {
                t.RaiseError(CannotRead, true);
                return W.Empty;
            }
            if (n == 0)
            {
                W.Raise(t, FileEof);
                return W.Empty;
            }
            int lim = Math.Min(n, 0xff);
            int len = 0;
            while (len < lim && buf[len] != '\r' && buf[len] != '\n')
                len++;
            int skip = 0;
            if (len == n || (len == lim && buf[len] != '\r' && buf[len] != '\n'))
            {
                // 275 is raised without the halt flag, so a long line
                // does not halt
                if (len == 0xff && n != 0xff)
                    t.RaiseError(StringTooLong, false);
            }
            else if (buf[len] == '\r')
                skip = len + 1 < n && buf[len + 1] == '\n' ? 2 : 1;
            else
                skip = 1;
            f.F.Position = start + len + skip;
            return W.S(Cp1252.DecodeZ(buf, 0, len));
        }

        [Builtin(
            "FileEOF",
            Doc = "1105364",
            Errors = "269 with the halt flag: a bad handle (returns 0)",
            Notes = "1 when the position is at or past the end of the file, else 0."
        )]
        private static Value FileEOF(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Check(h, t, A.I(a.Get(0)));
            if (f == null)
                return W.I(0);
            long size = h.Sandbox.Length(f.F);
            return W.I(f.F.Position < size ? 0 : 1);
        }

        // ------------------------------------------------------------------ writing
        /// <summary>write n bytes of s (NUL-padded) at the position.</summary>
        private static int WriteBytes(SimHost h, CicodeTask t, long hd, string s, long n)
        {
            var f = Check(h, t, hd);
            if (f == null)
                return -1;
            if ((ushort)n >= 0x100)
            {
                W.Raise(t, Codes.InvalidArg);
                return -1;
            }
            var data = new byte[(ushort)n];
            byte[] sb = Cp1252.Encode(s);
            Buffer.BlockCopy(sb, 0, data, 0, Math.Min(sb.Length, data.Length));
            int k = h.Sandbox.Write(f.F, data, data.Length, Now(t));
            if (k < 0)
                t.RaiseError(CannotWrite, true);
            return k;
        }

        [Builtin(
            "FileWrite",
            Doc = "1105381",
            Effects = "writes the string at the file's position (in the sandbox)",
            Errors = "264 with the halt flag: not open for writing or the sandbox is full (returns -1); 269 with the halt flag: a bad handle (returns -1)",
            Notes = "Returns the number of bytes written."
        )]
        private static Value FileWrite(SimHost h, CicodeTask t, ArgList a)
        {
            string s = A.S(a.Get(1));
            return W.I(WriteBytes(h, t, A.I(a.Get(0)), s, s.Length));
        }

        [Builtin(
            "FileWriteLn",
            Doc = "1105383",
            Effects = "writes the string and CR LF at the file's position (in the sandbox)",
            Errors = "264 with the halt flag: not open for writing or the sandbox is full (returns -1); 269 with the halt flag: a bad handle (returns -1)",
            Notes = "Returns the bytes written with the CR LF."
        )]
        private static Value FileWriteLn(SimHost h, CicodeTask t, ArgList a)
        {
            string s = A.S(a.Get(1));
            long hd = A.I(a.Get(0));
            int n = WriteBytes(h, t, hd, s, s.Length);
            if (n == -1)
                return W.I(-1);
            return W.I(n + WriteBytes(h, t, hd, "\r\n", 2));
        }

        [Builtin(
            "FileWriteBlock",
            Doc = "1105382",
            Effects = "writes Length bytes of the buffer at the file's position (in the sandbox)",
            Errors = "274 a length of 256 or more (raised, returns -1); 264 with the halt flag: not open for writing or the sandbox is full; 269 with the halt flag: a bad handle (returns -1)",
            Notes = "Bytes past the string's end are written as NUL (guess: the runtime writes what its buffer holds there)."
        )]
        private static Value FileWriteBlock(SimHost h, CicodeTask t, ArgList a) =>
            W.I(WriteBytes(h, t, A.I(a.Get(0)), A.S(a.Get(1)), A.I(a.Get(2))));

        // ------------------------------------------------------------------ position, size, time
        [Builtin(
            "FileSeek",
            Doc = "1105377",
            Errors = "269 with the halt flag: a bad handle (returns Offset); 274 with the halt flag: a negative offset (returns it)",
            Notes = "Moves to Offset bytes from the start (past the end is allowed); returns the new position."
        )]
        private static Value FileSeek(SimHost h, CicodeTask t, ArgList a)
        {
            long off = A.I(a.Get(1));
            var f = Check(h, t, A.I(a.Get(0)));
            if (f == null)
                return W.I(off);
            if (off < 0)
            {
                t.RaiseError(Codes.InvalidArg, true);
                return W.I(off);
            }
            f.F.Position = off;
            return W.I(off);
        }

        [Builtin(
            "FileSize",
            Doc = "1105379",
            Errors = "269 with the halt flag: a bad handle (returns -1)",
            Notes = "The file's length in bytes."
        )]
        private static Value FileSize(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Check(h, t, A.I(a.Get(0)));
            if (f == null)
                return W.I(-1);
            long n = h.Sandbox.Length(f.F);
            if (n < 0)
            {
                t.RaiseError(FileError, true);
                return W.I(-1);
            }
            return W.I(n);
        }

        /// <summary>A FILETIME as the runtime returns a file time: local time in DOS precision
        /// (even seconds).</summary>
        internal static long DosTime(SimHost h, ulong ft)
        {
            DateTime utc;
            try
            {
                utc = DateTime.FromFileTimeUtc((long)ft);
            }
            catch (ArgumentException)
            {
                return 0;
            }
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, h.TimeZone);
            if (local.Year < 1980)
                return 0;
            return Time.MkTime(
                h,
                local.Year - 1900,
                local.Month - 1,
                local.Day,
                local.Hour,
                local.Minute,
                local.Second & ~1
            );
        }

        [Builtin(
            "FileGetTime",
            Doc = "1105368",
            Errors = "269 with the halt flag: a bad handle (returns 0)",
            Notes = "The last write time (local, even seconds, as Cicode time): a sandbox file's is when the simulation last wrote it (the simulated clock), a real file's its own."
        )]
        private static Value FileGetTime(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Check(h, t, A.I(a.Get(0)));
            if (f == null)
                return W.I(0);
            ulong? ft = h.Sandbox.Time(f.F);
            return W.I(ft == null ? 0 : DosTime(h, ft.Value));
        }

        [Builtin(
            "FileSetTime",
            Doc = "1105378",
            Effects = "sets the sandbox file's times",
            Errors = "274 a time outside 1980..2037 (raised, returned); 293 a real file or a file not open for writing (returned); 269 with the halt flag: a bad handle (returns 293)"
        )]
        private static Value FileSetTime(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Check(h, t, A.I(a.Get(0)));
            if (f == null)
                return W.I(FileError);
            long time = A.I(a.Get(1));
            if ((uint)(time - 315532800L) > 0x6d181fffU)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var tm = Time.Break(h, time, false);
            long local = Time.MkTime(h, tm.Year, tm.Mon, tm.MDay, tm.Hour, tm.Min, tm.Sec & ~1);
            ulong ft = (ulong)((local + 11644473600L) * 10000000L);
            return W.I(h.Sandbox.SetTime(f.F, ft) ? 0 : FileError);
        }

        // ------------------------------------------------------------------ names
        [Builtin(
            "FileExist",
            Doc = "1105365",
            Blocking = true,
            Errors = "282 with the halt flag in foreground code (returns 0)",
            Notes = "1 when the file (or a folder) exists in the sandbox or among the readable real files, else 0; the answer comes at the task's next turn (the runtime asks a thread)."
        )]
        private static Value FileExist(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground)
            {
                t.RaiseError(Codes.FgBlock, true);
                return W.I(0);
            }
            string full = FullName(h, A.S(a.Get(0)));
            var sb = h.Sandbox;
            bool yes = sb.FileExists(full) || (!Sandbox.HasWildcard(full) && sb.FolderVisible(full));
            return W.BlockOneTurn(t, "FileExist", () => W.I(yes ? 1 : 0));
        }

        [Builtin(
            "FileDelete",
            Doc = "1105363",
            Effects = "deletes sandbox files (wildcards in the name)",
            Errors = "293 with the halt flag: no file matches, or a match is a real file (read-only) or open (returns 293); a wildcard in the drive or folder raises 293 with the halt flag and returns 274",
            Notes = "Every non-folder match in name order, stopping at the first that cannot go."
        )]
        private static Value FileDelete(SimHost h, CicodeTask t, ArgList a)
        {
            string full = FullName(h, A.S(a.Get(0)));
            SplitPath(full, out string drive, out string dir, out _, out _);
            if (Sandbox.HasWildcard(drive) || Sandbox.HasWildcard(dir))
            {
                t.RaiseError(FileError, true);
                return W.I(Codes.InvalidArg);
            }
            FolderAndName(full, out string folder, out string name);
            var sb = h.Sandbox;
            var found = sb.Find(folder, name);
            bool fail = found.Count == 0;
            foreach (var f in found)
            {
                if (f.IsFolder)
                    continue;
                string p = CicodeSim.Project.WinPath.Join(folder, f.Name);
                if (IsOpen(h, p) || !sb.Delete(p))
                {
                    fail = true;
                    break;
                }
            }
            if (fail)
            {
                t.RaiseError(FileError, true);
                return W.I(FileError);
            }
            return W.I(0);
        }

        /// <summary>FileReName's wildcard name: '*' takes the rest of the old name from there,
        /// '?' its character there ('_' past its end).</summary>
        private static string Rewild(string pattern, string old)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '*')
                {
                    if (i < old.Length)
                        sb.Append(old, i, old.Length - i);
                    break;
                }
                sb.Append(c == '?' ? (i < old.Length ? old[i] : '_') : c);
            }
            return sb.ToString();
        }

        [Builtin(
            "FileReName",
            Doc = "1105375",
            Effects = "renames sandbox files (wildcards in the names)",
            Errors = "293 with the halt flag: no file matches, a match is a real file (read-only) or open, or the new name exists (returns 293); a wildcard in a drive or folder raises 293 with the halt flag and returns 274",
            Notes = "A '*' or '?' in the new name or extension takes the old name's characters (an empty new name or extension stays empty, the runtime's rule)."
        )]
        private static Value FileReName(SimHost h, CicodeTask t, ArgList a)
        {
            string from = FullName(h, A.S(a.Get(0))),
                to = FullName(h, A.S(a.Get(1)));
            SplitPath(from, out string fd, out string fdir, out _, out _);
            SplitPath(to, out string td, out string tdir, out string tname, out string text);
            if (
                Sandbox.HasWildcard(fd)
                || Sandbox.HasWildcard(fdir)
                || Sandbox.HasWildcard(td)
                || Sandbox.HasWildcard(tdir)
            )
            {
                t.RaiseError(FileError, true);
                return W.I(Codes.InvalidArg);
            }
            bool wildName = tname.Length == 0 || Sandbox.HasWildcard(tname);
            bool wildExt = text.Length == 0 || Sandbox.HasWildcard(text);
            FolderAndName(from, out string folder, out string pattern);
            var sb = h.Sandbox;
            var found = sb.Find(folder, pattern);
            bool fail = found.Count == 0;
            foreach (var f in found)
            {
                SplitPath(f.Name, out _, out _, out string on, out string oe);
                string nn = wildName ? Rewild(tname, on) : tname;
                string ne = wildExt ? Rewild(text, oe) : text;
                string target = MakePath(td, tdir, nn, ne);
                string p = CicodeSim.Project.WinPath.Join(folder, f.Name);
                if (IsOpen(h, p) || !sb.Rename(p, sb.Full(target)))
                {
                    fail = true;
                    break;
                }
            }
            if (fail)
            {
                t.RaiseError(FileError, true);
                return W.I(FileError);
            }
            return W.I(0);
        }

        /// <summary>Copy every file matching the source to the target.</summary>
        internal static int CopyFiles(SimHost h, string from, string to, bool onlyIfDifferent, ulong now)
        {
            var sb = h.Sandbox;
            FolderAndName(from, out string folder, out string pattern);
            SplitPath(to, out string td, out string tdir, out string tname, out string text);
            int copied = 0;
            foreach (var f in sb.Find(folder, pattern))
            {
                if (f.IsFolder)
                    continue;
                SplitPath(f.Name, out _, out _, out string on, out string oe);
                string nn = tname.Length == 0 || Sandbox.HasWildcard(tname) ? on : tname;
                string ne = text.Length == 0 || Sandbox.HasWildcard(text) ? oe : text;
                int r = sb.Copy(
                    CicodeSim.Project.WinPath.Join(folder, f.Name),
                    sb.Full(MakePath(td, tdir, nn, ne)),
                    onlyIfDifferent,
                    now
                );
                if (r != 0)
                    return r;
                copied++;
            }
            return copied == 0 ? Codes.FileNotFound : 0;
        }

        [Builtin(
            "FileCopy",
            Doc = "1105362",
            Blocking = true,
            Effects = "copies files into the sandbox (wildcards in the source)",
            Errors = "293 with the halt flag in foreground code without mode 2 (returns 282); 261 no source file, 262 the target cannot be made, 264 the sandbox is full (returned in mode 0 / 1)",
            Notes = "Mode 1 skips a target with the source's time, mode 2 does not wait (the result is 0 whatever happens). The copy is done at once; a waiting copy's result comes at the task's next turn. A target keeps the source's time."
        )]
        private static Value FileCopy(SimHost h, CicodeTask t, ArgList a)
        {
            long mode = A.I(a.Get(2)) & 0xffff;
            bool async = (mode & 2) != 0;
            if (!async && t.Foreground)
            {
                // the request fails with 282 and every failure is raised as 0x125 with the halt
                // flag (returns si = 282)
                t.RaiseError(FileError, true);
                return W.I(Codes.FgBlock);
            }
            string from = FullName(h, A.S(a.Get(0))),
                to = FullName(h, A.S(a.Get(1)));
            int r = CopyFiles(h, from, to, (mode & 1) != 0, Now(t));
            if (async)
                return W.I(0);
            return W.BlockOneTurn(t, "FileCopy", () => W.I(r));
        }

        [Builtin(
            "FileFind",
            Doc = "1105366",
            Errors = "274 an empty path with no search of this task, or foreground code (no task) (raised, \"\"); 271 more than five searches at once (raised, \"\")",
            Notes = "A path starts a search of this task (the sandbox's files and the readable real ones, in name order); \"\" gives the next match; \"\" when none is left (the search ends). Attributes 0 = 0x9a1; a match needs one of its attributes and no hidden / system / folder attribute it does not ask for. A path without a wildcard ends its search after the first answer. The result is the pattern's drive and folder with the match's name."
        )]
        private static Value FileFind(SimHost h, CicodeTask t, ArgList a)
        {
            string path = A.S(a.Get(0));
            long attr = A.Short(A.I(a.Get(1)));
            if (t.Foreground)
            {
                // no task handle (0xffff): raise(274, 0) and ""
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            var task = t;
            int slot = -1;
            for (int i = 0; i < h.Finds.Length; i++)
                if (h.Finds[i] != null && ReferenceEquals(h.Finds[i]!.Task, task))
                {
                    slot = i;
                    break;
                }
            FindSession? s;
            if (path.Length == 0)
            {
                if (slot < 0)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                s = h.Finds[slot]!;
            }
            else
            {
                if (slot < 0)
                {
                    // the search of a task that ended is free again (simulator: the runtime's
                    // table keeps it until its handle is reused)
                    for (int i = 0; i < h.Finds.Length && slot < 0; i++)
                        if (h.Finds[i] == null || h.Finds[i]!.Task.Done)
                            slot = i;
                    if (slot < 0)
                    {
                        W.Raise(t, Codes.NoHandles);
                        return W.Empty;
                    }
                }
                string full = FullName(h, path);
                FolderAndName(full, out string folder, out string name);
                SplitPath(full, out string drive, out string dir, out _, out _);
                s = new FindSession
                {
                    Task = task,
                    Drive = drive,
                    Dir = dir,
                    Mask = attr == 0 ? 0x9a1 : (int)attr,
                    Single = !Sandbox.HasWildcard(path),
                    Left = h.Sandbox.Find(folder, name),
                };
                h.Finds[slot] = s;
            }
            while (s.Next < s.Left.Count)
            {
                var f = s.Left[s.Next++];
                int at = f.Attributes;
                if ((s.Mask & at) != 0 && (at & ~s.Mask & 0x16) == 0)
                {
                    SplitPath(f.Name, out _, out _, out string fn, out string ext);
                    string r = MakePath(s.Drive, s.Dir, fn, ext);
                    if (s.Single)
                        h.Finds[slot] = null;
                    return W.S(A.Cap(r));
                }
            }
            h.Finds[slot] = null;
            return W.Empty;
        }

        [Builtin(
            "FileFindClose",
            Doc = "1105367",
            Errors = "370 no search of this task (raised, returned); 282 no task: foreground code (raised, returned)",
            Notes = "Ends the task's FileFind search."
        )]
        private static Value FileFindClose(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground)
            {
                // no task handle: raise(282, 0) and 282
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            for (int i = 0; i < h.Finds.Length; i++)
                if (h.Finds[i] != null && ReferenceEquals(h.Finds[i]!.Task, t))
                {
                    h.Finds[i] = null;
                    return W.I(0);
                }
            W.Raise(t, 370);
            return W.I(370);
        }

        [Builtin(
            "FileSplitPath",
            Doc = "1105380",
            Effects = "writes the drive, folder, name and extension into the four variables",
            Notes = "The path through the [NAME] path operators, split as the C runtime's _splitpath does; returns 0. No file is touched."
        )]
        private static Value FileSplitPath(SimHost h, CicodeTask t, ArgList a)
        {
            SplitPath(
                Misc.PathOf(h, A.S(a.Get(0))),
                out string drive,
                out string dir,
                out string fname,
                out string ext
            );
            W.Out(t, a.Get(1), A.Cap(drive));
            W.Out(t, a.Get(2), A.Cap(dir));
            W.Out(t, a.Get(3), A.Cap(fname));
            W.Out(t, a.Get(4), A.Cap(ext));
            return W.I(0);
        }

        [Builtin(
            "FileMakePath",
            Doc = "1105369",
            Notes = "The C runtime's _makepath: the drive's first letter and ':', the folder with a '\\' after it, the name, the extension with a '.' before it. No file is touched."
        )]
        private static Value FileMakePath(SimHost h, CicodeTask t, ArgList a) =>
            W.S(A.Cap(MakePath(A.S(a.Get(0)), A.S(a.Get(1)), A.S(a.Get(2)), A.S(a.Get(3)))));
    }
}
