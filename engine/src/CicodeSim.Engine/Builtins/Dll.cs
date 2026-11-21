using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>A function of DLLOpen: one handle per library and function name, counted.</summary>
    internal sealed class DllFn
    {
        public int Handle;
        public string Lib = "",
            Path = "",
            Name = "",
            Types = "";
        public int Refs = 1;

        /// <summary>The runner's id of the loaded function.</summary>
        public int Id;
    }

    /// <summary>The libraries of one simulation.</summary>
    internal sealed class DllWorld : IDisposable
    {
        public readonly SortedDictionary<int, DllFn> Fns = new SortedDictionary<int, DllFn>();
        private IDllRunner? _runner;

        public IDllRunner Runner(SimHost h)
        {
            if (_runner != null)
                return _runner;
            _runner = Dll.RunnerFactory?.Invoke(h) ?? new DllLocalRunner();
            ApplyTimeout(h.Outside.DllTimeoutMs);
            return _runner;
        }

        /// <summary>How long a call may take (OutsideAccess.DllTimeoutMs; 0 = no limit).</summary>
        public void ApplyTimeout(int ms)
        {
            if (_runner is DllChildRunner r)
                r.TimeoutMs = ms;
        }

        public int FreeHandle()
        {
            int n = 0;
            while (Fns.ContainsKey(n))
                n++;
            return n;
        }

        public void Dispose()
        {
            Fns.Clear();
            var r = _runner;
            _runner = null;
            try
            {
                r?.Dispose();
            }
            catch (Exception) { } // swallow-ok: the simulation is ending
        }
    }

    public sealed partial class SimHost
    {
        internal readonly DllWorld DllData = new DllWorld();

        /// <summary>The simulation ends: its database connections are closed, its libraries
        /// freed (the library helper process ends).</summary>
        public void ShutdownOutside()
        {
            ShutdownSql();
            ShutdownDll();
        }

        /// <summary>Free every library of the simulation (the library helper process ends).</summary>
        public void ShutdownDll()
        {
            Outside.CancelAsked("dll"); // (the questions up: refused)
            DllData.Dispose();
        }
    }

    /// <summary>DLLOpen, DLLClose and DLLCall.</summary>
    public static class Dll
    {
        private const int NameNotFound = 289;

        /// <summary>Makes a simulation's library runner; null = in this process
        /// (<see cref="DllLocalRunner"/>). ctsim serve sets the helper process.</summary>
        public static Func<SimHost, IDllRunner>? RunnerFactory;

        private static readonly Value NullVariant = Value.Variant(null);

        private static bool Off(SimHost h) => h.Outside.Dll == OutsideMode.Off;

        private static Value Stub(SimHost h, CicodeTask t, string name, ArgList a, Value r)
        {
            h.NoteStub(t, name, a, r);
            return r;
        }

        private static Value Bad(CicodeTask t, Value result, int code)
        {
            t.RaiseError(code, false);
            return result;
        }

        private static bool TypesOk(string s)
        {
            foreach (char c in s)
                if ("ABCDHIJ".IndexOf(c) < 0)
                    return false;
            return true;
        }

        /// <summary>The full path LoadLibrary would load for ``library``.</summary>
        public static string Resolve(string library, string? binDir = null)
        {
            string lib = (library ?? "").Trim().Trim('"').Replace('/', '\\');
            if (lib.Length == 0)
                return "";
            string bin = (binDir ?? "").Trim().Trim('"');
            try
            {
                string file = Path.GetFileName(lib);
                if (!file.Contains("."))
                    lib += ".dll";
                if (
                    Path.IsPathRooted(lib) && lib.IndexOf(':') >= 0
                    || lib.StartsWith("\\\\", StringComparison.Ordinal)
                )
                    return Path.GetFullPath(lib);
                if (lib.IndexOf('\\') >= 0)
                    return Path.GetFullPath(bin.Length > 0 ? Path.Combine(bin, lib.TrimStart('\\')) : lib);
                var dirs = new List<string>();
                if (bin.Length > 0)
                    dirs.Add(bin);
                dirs.Add(Environment.SystemDirectory);
                string win = Environment.GetEnvironmentVariable("SystemRoot") ?? "";
                if (win.Length > 0)
                    dirs.Add(win);
                foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                    if (d.Trim().Length > 0)
                        dirs.Add(d.Trim().Trim('"'));
                foreach (string d in dirs)
                {
                    try
                    {
                        string p = Path.Combine(d, lib);
                        if (File.Exists(p))
                            return Path.GetFullPath(p);
                    }
                    catch (Exception) { } // swallow-ok: a PATH entry that is not a path
                }
            }
            catch (Exception) { } // swallow-ok: a name that is not a path: it stays as given (and does not load)
            return lib;
        }

        /// <summary>Whether <see cref="Resolve"/> found the file (a full path that exists).</summary>
        private static bool Found(string path)
        {
            try
            {
                return path.Length > 0 && Path.IsPathRooted(path) && File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            } // swallow-ok: a path the file system refuses
        }

        private static string NotFound(string path) =>
            Path.IsPathRooted(path)
                ? "there is no " + path
                : "no " + path + " in the runtime's folder, the Windows folders or the PATH";

        private static string BinDir() => Project.CitectIni.IniValue("CtEdit", "Bin", "");

        private static Value WhenAllowed(
            SimHost h,
            CicodeTask t,
            string fn,
            string path,
            string detail,
            Func<bool, Pending> then
        )
        {
            OutsideAsk.PumpAnswers(h);
            bool? d = h.Outside.DllDecision(path);
            if (d.HasValue)
            {
                Pending p = then(d.Value);
                return p.Poll == null ? p.Result() : OutsideAsk.Await(t, fn, p);
            }
            return OutsideAsk.Then(h, t, "dll", "load", path, detail, then);
        }

        /// <summary>The variable arguments of a VARARG built-in after its fixed ones.</summary>
        internal static List<Value> Extras(ArgList a, int fixedCount)
        {
            var l = new List<Value>();
            if (a.Count <= fixedCount)
                return l;
            Value last = Value.Deref(a.Get(a.Count - 1));
            long n = last.PayloadIsInt ? last.I : -1;
            if (n >= 0 && a.Count == fixedCount + Math.Max(n, 1) + 1)
            {
                for (int i = 0; i < n; i++)
                    l.Add(a.Get(fixedCount + i));
                return l;
            }
            for (int i = fixedCount; i < a.Count; i++)
                l.Add(a.Get(i));
            return l;
        }

        // ------------------------------------------------------------------ native functions
        [Builtin(
            "DLLOpen",
            Doc = "1105179",
            Errors = "274 an argument-type letter other than A B C D H I J (raised); 289 the library or the function is not found, or loading it was not allowed (raised)",
            Effects = "loads the library (options dll 'ask' / 'on')",
            Notes = "A deliberate deviation: ctsim/options dll. 'off' (the default): no library is loaded, every open fails as LoadLibrary does (289, -1) and counts as not simulated. 'ask': the viewer is asked before a library that is not on the whitelist is loaded (a task waits for the answer; page code cannot wait: this open is refused and the answer counts from the next one; denied, or no viewer shows dialogs: 289, -1). 'on': loads. The same library and function again give the same handle (counted). In every mode the simulator answers CTUTIL.DLL's DspPopupMenu, IsActive and GetData (Include CTUTIL.CI's popup menus) and Kernel32 GetCurrentProcessId, User32 LockWindowUpdate, WinUtil.DLL WinUtil_WaitCursor / WinUtil_NormalCursor itself (Builtins.CtUtil, Builtins.WinApi: a pseudo handle). A library named without a folder is looked for as the runtime's LoadLibrary does: the runtime's own folder ([CtEdit] Bin) first, then the Windows system folder, the Windows folder and the PATH. The library's name is kept in 260 characters, the function's in 32, the type letters in 32. ctsim.exe is a 32-bit process, as the runtime: it loads 32-bit libraries; they run in a helper process (ctsim dllhost)."
        )]
        private static Value DLLOpen(SimHost h, CicodeTask t, ArgList a)
        {
            string types = A.CUpper(A.S(a.Get(2)));
            if (!TypesOk(types))
                return Bad(t, W.I(-1), Codes.InvalidArg);
            string lib = A.S(a.Get(0)),
                name = A.SkipSpace(A.S(a.Get(1))).TrimEnd();
            if (name.Length > 32)
                name = name.Substring(0, 32);
            if (types.Length > 31)
                types = types.Substring(0, 31);
            int hd = CtUtil.Open(lib, name);
            if (hd >= 0)
                return W.I(hd);
            hd = WinApi.Open(lib, name);
            if (hd >= 0)
                return W.I(hd);
            if (Off(h))
            {
                Outside.LogNotSimulated(h, t, "DLLOpen(\"" + lib + "\", \"" + name + "\")");
                return Stub(h, t, "DLLOpen", a, Bad(t, W.I(-1), NameNotFound));
            }
            foreach (var f in h.DllData.Fns.Values)
                if (
                    string.Equals(f.Lib, lib, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
                )
                {
                    f.Refs++;
                    return W.I(f.Handle);
                }
            string path = Resolve(lib, BinDir());
            if (!Found(path))
            {
                // (not asked about: LoadLibrary would look beside ctsim.exe, a place the runtime's
                // search has not; the question would name a bare file name)
                h.Note(t, "DLLOpen(\"" + lib + "\", \"" + name + "\"): " + NotFound(path));
                return Bad(t, W.I(-1), NameNotFound);
            }
            return WhenAllowed(h, t, "DLLOpen", path, name, allowed =>
                Pending.Now(() =>
                {
                    if (!allowed)
                    {
                        h.Note(t, "DLLOpen: the simulator was not allowed to load " + path);
                        return Bad(t, W.I(-1), NameNotFound);
                    }
                    int id = h.DllData.Runner(h).Load(path, name, out string error);
                    if (id < 0)
                    {
                        h.Note(t, "DLLOpen(\"" + lib + "\", \"" + name + "\"): " + error);
                        return Bad(t, W.I(-1), NameNotFound);
                    }
                    var fn = new DllFn
                    {
                        Handle = h.DllData.FreeHandle(),
                        Lib = lib,
                        Path = path,
                        Name = name,
                        Types = types,
                        Id = id,
                    };
                    h.DllData.Fns[fn.Handle] = fn;
                    h.Note(t, "DLLOpen: loaded " + path + " (" + name + ")");
                    return W.I(fn.Handle);
                })
            );
        }

        [Builtin(
            "DLLClose",
            Doc = "1105178",
            Errors = "269 no such function handle (returned)",
            Effects = "frees the library when its last handle closes",
            Notes = "A pseudo handle of a function the simulator answers itself closes: 0. Options dll 'off': counted as not simulated."
        )]
        private static Value DLLClose(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.I(a.Get(0));
            if (CtUtil.Is(hd) || WinApi.Is(hd))
                return W.I(0);
            if (Off(h))
                return Stub(h, t, "DLLClose", a, W.I(Codes.BadHandle));
            if (hd < 0 || hd > int.MaxValue || !h.DllData.Fns.TryGetValue((int)hd, out var f))
                return W.I(Codes.BadHandle);
            if (--f.Refs <= 0)
            {
                h.DllData.Fns.Remove(f.Handle);
                h.DllData.Runner(h).Free(f.Id);
            }
            return W.I(0);
        }

        private static bool Run(SimHost h, CicodeTask t, DllFn f, List<DllValue> args, out DllValue result)
        {
            var sw = Stopwatch.StartNew();
            bool ok = h.DllData.Runner(h).Call(f.Id, f.Types, args, out result, out string error);
            sw.Stop();
            if (!ok)
                h.Note(t, "The call of " + f.Name + " in " + f.Path + " failed: " + error);
            // the runtime reports a call of more than 250 ms (hardware error 538 in its log)
            else if (sw.ElapsedMilliseconds > 250)
                h.Note(
                    t,
                    "The call of "
                        + f.Name
                        + " in "
                        + f.Path
                        + " took "
                        + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                        + " ms (the runtime logs error 538 above 250 ms)"
                );
            return ok;
        }

        [Builtin(
            "DLLCall",
            Doc = "1105176",
            Effects = "calls the library's function (options dll 'ask' / 'on')",
            Notes = "The argument text is read as Cicode call text (strings, numbers, a leading '-', names, commas), one argument per type letter of DLLOpen: a number for a string type is its text (_ltoa, _gcvt 5 digits), a string for a number type atol / atof of it, a missing argument 0 or \"\". The result as text: A H I the low 16 bits, J the 32 bits (_ltoa), B _gcvt 15 digits, C the string, D the counted string without its count. An unknown handle gives \"\" without an error, as in the runtime; a call that fails in the library (an access violation, a helper that had to be ended) gives \"\" and a log line. A pseudo handle runs the simulated function. The simulation waits for the library as the runtime does (a message box the library shows waits for its click); ctsim/options dllTimeout sets a limit, after which the helper process is ended and the call fails. Options dll 'off': \"\", counted as not simulated."
        )]
        private static Value DLLCall(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.I(a.Get(0));
            if (WinApi.Is(hd))
                return W.S(WinApi.Call(hd).ToString(CultureInfo.InvariantCulture));
            if (CtUtil.Is(hd))
                return CtUtil.Call(h, t, hd, new[] { Value.Str(a.Count > 1 ? A.S(a.Get(1)) : "") });
            if (Off(h))
                return Stub(h, t, "DLLCall", a, W.Empty);
            if (hd < 0 || hd > int.MaxValue || !h.DllData.Fns.TryGetValue((int)hd, out var f))
                return W.Empty;
            var parsed = CallText.DllArgs(a.Count > 1 ? A.S(a.Get(1)) : "", Math.Max(0, f.Types.Length - 1));
            var args = new List<DllValue>();
            for (int i = 1; i < f.Types.Length; i++)
            {
                var p = parsed[i - 1];
                switch (f.Types[i])
                {
                    case 'B':
                        args.Add(DllValue.OfReal(p.D));
                        break;
                    case 'C':
                    case 'D':
                        args.Add(DllValue.OfStr(p.Text));
                        break;
                    default:
                        args.Add(DllValue.OfInt(p.I));
                        break;
                }
            }
            if (!Run(h, t, f, args, out DllValue r))
                return W.Empty;
            switch (r.Kind)
            {
                case DllValue.Real:
                    return W.S(CallText.Gcvt(r.D, 15));
                case DllValue.Str:
                    return W.S(A.Cap(r.S ?? ""));
                default:
                    return W.S(r.I.ToString(CultureInfo.InvariantCulture));
            }
        }

        [Builtin(
            "DLLCallEx",
            Doc = "1105177",
            Errors = "269 no such function handle (raised); 274 not one argument per type letter, or an argument of another type than its letter: an integer for A H I J, a real for B, a string for C D (raised)",
            Effects = "calls the library's function (options dll 'ask' / 'on')",
            Notes = "The result by the return type letter: an integer, a real or a string; a NULL VARIANT on an error and when the call fails in the library. A pseudo handle runs the simulated function with the arguments (DspPopupMenu's request number). Options dll 'off': 269 and a NULL VARIANT, counted as not simulated."
        )]
        private static Value DLLCallEx(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.I(a.Get(0));
            if (WinApi.Is(hd))
                return W.I(WinApi.Call(hd));
            if (CtUtil.Is(hd))
            {
                var pseudo = new List<Value>();
                foreach (Value v in Extras(a, 1))
                    pseudo.Add(Value.Deref(v));
                return CtUtil.Call(h, t, hd, pseudo);
            }
            if (Off(h))
                return Stub(h, t, "DLLCallEx", a, Bad(t, NullVariant, Codes.BadHandle));
            if (hd < 0 || hd > int.MaxValue || !h.DllData.Fns.TryGetValue((int)hd, out var f))
                return Bad(t, NullVariant, Codes.BadHandle);
            var given = Extras(a, 1);
            if (given.Count != Math.Max(0, f.Types.Length - 1))
            {
                h.Note(
                    t,
                    "DLLCallEx("
                        + f.Name
                        + "): "
                        + given.Count.ToString(CultureInfo.InvariantCulture)
                        + " arguments, the function was opened with "
                        + Math.Max(0, f.Types.Length - 1).ToString(CultureInfo.InvariantCulture)
                );
                return Bad(t, NullVariant, Codes.InvalidArg);
            }
            var args = new List<DllValue>();
            for (int i = 1; i < f.Types.Length; i++)
            {
                Value v = Value.Deref(given[i - 1]);
                bool ok;
                switch (f.Types[i])
                {
                    case 'B':
                        ok = v.T == Types.Real || v.T == Types.RealValue;
                        if (ok)
                            args.Add(DllValue.OfReal(v.D));
                        break;
                    case 'C':
                    case 'D':
                        ok = v.T == Types.String;
                        if (ok)
                            args.Add(DllValue.OfStr(A.S(v)));
                        break;
                    default:
                        ok =
                            v.T == Types.Int16
                            || v.T == Types.Long
                            || v.T == Types.UInt
                            || v.T == Types.Digital
                            || v.T == Types.Void;
                        if (ok)
                            args.Add(DllValue.OfInt(v.I));
                        break;
                }
                if (!ok)
                {
                    h.Note(
                        t,
                        "DLLCallEx("
                            + f.Name
                            + "): argument "
                            + i.ToString(CultureInfo.InvariantCulture)
                            + " is "
                            + Sim.ValueFormat.TypeName(v.T)
                            + ", its type letter is "
                            + f.Types[i]
                    );
                    return Bad(t, NullVariant, Codes.InvalidArg);
                }
            }
            if (!Run(h, t, f, args, out DllValue r))
                return NullVariant;
            switch (r.Kind)
            {
                case DllValue.Real:
                    return W.R(r.D);
                case DllValue.Str:
                    return W.S(A.Cap(r.S ?? ""));
                default:
                    return W.I(r.I);
            }
        }

        [Builtin(
            "DLLCallOEM",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Notes = "No OEM DLL in the simulator: \"\"."
        )]
        private static Value DLLCallOEM(SimHost h, CicodeTask t, ArgList a) => W.Empty;

        // ------------------------------------------------------------------ .NET classes
        private static DllValue ToDll(Value v0)
        {
            Value v = Value.Deref(v0);
            if (v.T == Types.String)
                return DllValue.OfStr(A.S(v));
            if (Types.IsRealType(v.T))
                return DllValue.OfReal(v.D);
            if (v.T == Types.Variant && v.O == null)
                return default;
            return DllValue.OfInt(v.I);
        }

        private static Value FromDll(DllValue v)
        {
            switch (v.Kind)
            {
                case DllValue.Int:
                case DllValue.Obj:
                    return W.I(v.I);
                case DllValue.Real:
                    return W.R(v.D);
                case DllValue.Str:
                    return W.S(A.Cap(v.S ?? ""));
                default:
                    return NullVariant;
            }
        }

        private static List<DllValue> Rest(ArgList a, int from)
        {
            var l = new List<DllValue>();
            foreach (Value v in Extras(a, from))
                l.Add(ToDll(v));
            return l;
        }

        [Builtin(
            "DllClassCreate",
            Doc = "1188708",
            Blocking = true,
            Errors = "274 the assembly cannot be loaded, it has no such class, the class cannot be made, or loading it was not allowed (raised); 282 foreground code (returned -1)",
            Effects = "loads the .NET assembly and makes an object of the class (options dll 'ask' / 'on')",
            Notes = "The class by its name without case and without its namespace; the arguments after the class name go to its constructor. Options dll 'ask' asks before an assembly that is not on the whitelist is loaded (denied: 274, -1). 'off' (the default): nothing is loaded, 289 raised, -1, counted as not simulated. The object lives in the helper process (ctsim dllhost); ctsim.exe loads .NET Framework assemblies."
        )]
        private static Value DllClassCreate(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
            {
                if (t.Foreground)
                    return Stub(h, t, "DllClassCreate", a, W.I(-1));
                Outside.LogNotSimulated(
                    h,
                    t,
                    "DllClassCreate(\"" + A.S(a.Get(0)) + "\", \"" + A.S(a.Get(1)) + "\")"
                );
                return Stub(h, t, "DllClassCreate", a, Bad(t, W.I(-1), NameNotFound));
            }
            if (t.Foreground)
                return W.I(-1);
            string lib = A.S(a.Get(0)),
                cls = A.S(a.Get(1));
            string path = Resolve(lib, BinDir());
            if (!Found(path))
            {
                h.Note(t, "DllClassCreate(\"" + lib + "\", \"" + cls + "\"): " + NotFound(path));
                return Bad(t, W.I(-1), Codes.InvalidArg);
            }
            var args = Rest(a, 2);
            return WhenAllowed(h, t, "DllClassCreate", path, cls, allowed =>
                Pending.Now(() =>
                {
                    if (!allowed)
                    {
                        h.Note(t, "DllClassCreate: the simulator was not allowed to load " + path);
                        return Bad(t, W.I(-1), Codes.InvalidArg);
                    }
                    int id = h.DllData.Runner(h).ClassCreate(path, cls, args, out string error);
                    if (id < 0)
                    {
                        h.Note(t, "DllClassCreate(\"" + lib + "\", \"" + cls + "\"): " + error);
                        return Bad(t, W.I(-1), Codes.InvalidArg);
                    }
                    h.Note(t, "DllClassCreate: loaded " + path + " (" + cls + ")");
                    return W.I(id);
                })
            );
        }

        [Builtin(
            "DllClassIsValid",
            Doc = "1188709",
            Blocking = true,
            Errors = "282 foreground code (returned)",
            Notes = "1 for a live object handle, else 0. Options dll 'off': 0, counted as not simulated."
        )]
        private static Value DllClassIsValid(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "DllClassIsValid", a, W.I(t.Foreground ? Codes.FgBlock : 0));
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            long hd = A.I(a.Get(0));
            return W.I(hd > 0 && hd <= int.MaxValue && h.DllData.Runner(h).ClassIsValid((int)hd) ? 1 : 0);
        }

        private static bool Object(SimHost h, long hd) =>
            hd > 0 && hd <= int.MaxValue && h.DllData.Runner(h).ClassIsValid((int)hd);

        [Builtin(
            "DllClassDispose",
            Doc = "1188707",
            Blocking = true,
            Errors = "274 no such object (raised, returned); 282 foreground code (returned)",
            Effects = "frees the object and the objects its properties and methods gave",
            Notes = "Options dll 'off': 269, counted as not simulated."
        )]
        private static Value DllClassDispose(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(
                    h,
                    t,
                    "DllClassDispose",
                    a,
                    t.Foreground ? W.I(Codes.FgBlock) : Bad(t, W.I(Codes.BadHandle), Codes.BadHandle)
                );
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            long hd = A.I(a.Get(0));
            if (!Object(h, hd))
                return Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            if (!h.DllData.Runner(h).ClassDispose((int)hd, out string error))
            {
                h.Note(t, "DllClassDispose: " + error);
                return Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            }
            return W.I(0);
        }

        [Builtin(
            "DllClassSetProperty",
            Doc = "1188710",
            Blocking = true,
            Errors = "274 no such object or property, or the value does not fit (raised, returned); 282 foreground code (returned)",
            Effects = "sets the object's property",
            Notes = "Options dll 'off': 269, counted as not simulated."
        )]
        private static Value DllClassSetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(
                    h,
                    t,
                    "DllClassSetProperty",
                    a,
                    t.Foreground ? W.I(Codes.FgBlock) : Bad(t, W.I(Codes.BadHandle), Codes.BadHandle)
                );
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            long hd = A.I(a.Get(0));
            if (!Object(h, hd))
                return Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            if (!h.DllData.Runner(h).ClassSet((int)hd, A.S(a.Get(1)), ToDll(a.Get(2)), out string error))
            {
                h.Note(t, "DllClassSetProperty(" + A.S(a.Get(1)) + "): " + error);
                return Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            }
            return W.I(0);
        }

        [Builtin(
            "DllClassGetProperty",
            Doc = "1188711",
            Blocking = true,
            Errors = "274 no such object or property (raised)",
            Notes = "A number or a string; any other object (a date and time too) stays in the helper and its new object handle comes back; a NULL VARIANT on an error. Options dll 'off': a NULL VARIANT and 269, counted as not simulated."
        )]
        private static Value DllClassGetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(
                    h,
                    t,
                    "DllClassGetProperty",
                    a,
                    t.Foreground ? NullVariant : Bad(t, NullVariant, Codes.BadHandle)
                );
            if (t.Foreground)
                return NullVariant;
            long hd = A.I(a.Get(0));
            if (!Object(h, hd))
                return Bad(t, NullVariant, Codes.InvalidArg);
            if (!h.DllData.Runner(h).ClassGet((int)hd, A.S(a.Get(1)), out DllValue v, out string error))
            {
                h.Note(t, "DllClassGetProperty(" + A.S(a.Get(1)) + "): " + error);
                return Bad(t, NullVariant, Codes.InvalidArg);
            }
            return FromDll(v);
        }

        [Builtin(
            "DllClassCallMethod",
            Doc = "1188712",
            Blocking = true,
            Errors = "274 no such object or method, another number of arguments than the method takes, an argument that does not fit, or the method threw (raised)",
            Effects = "calls the object's method",
            Notes = "The first method of that name that is not generic and takes as many arguments; the arguments are converted to its parameter types. The result as DllClassGetProperty's. Options dll 'off': a NULL VARIANT and 269, counted as not simulated."
        )]
        private static Value DllClassCallMethod(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(
                    h,
                    t,
                    "DllClassCallMethod",
                    a,
                    t.Foreground ? NullVariant : Bad(t, NullVariant, Codes.BadHandle)
                );
            if (t.Foreground)
                return NullVariant;
            long hd = A.I(a.Get(0));
            if (!Object(h, hd))
                return Bad(t, NullVariant, Codes.InvalidArg);
            if (
                !h
                    .DllData.Runner(h)
                    .ClassCall((int)hd, A.S(a.Get(1)), Rest(a, 2), out DllValue v, out string error)
            )
            {
                h.Note(t, "DllClassCallMethod(" + A.S(a.Get(1)) + "): " + error);
                return Bad(t, NullVariant, Codes.InvalidArg);
            }
            return FromDll(v);
        }
    }
}
