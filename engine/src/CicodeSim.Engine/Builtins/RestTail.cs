using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>The long tail: REAL tables, cluster activation, kernel queues and more.</summary>
    public static class RestTail
    {
        // ------------------------------------------------------------------ REAL tables
        /// <summary>Element <paramref name="i"/> of the REAL table a reference starts (a module /
        /// global array: the following elements; a local: element 0, the rest 0).</summary>
        private static double Element(in Value table, long i)
        {
            if (table.O is CodeVarRef cr)
                return A.R(cr.Slot.Get(cr.Element + i, out _));
            return i == 0 ? A.R(table) : 0.0;
        }

        private static void Store(CicodeTask t, in Value table, long i, double v)
        {
            if (table.O is CodeVarRef cr)
                cr.Slot.Set(cr.Element + i, Value.Real(v));
            else if (i == 0)
                W.Out(t, table, Value.Real(v));
        }

        [Builtin(
            "TableLookUp",
            Doc = "1192708",
            Notes = "The offset of the first element within 0.001 of Value (-0.001 < Value - Table[i] < 0.001, both strict:), -1 when none of Size elements is. Size is counted down as a 32-bit number and tested as its low 16 bits, as the runtime. An element past a module / global array reads 0."
        )]
        private static Value TableLookUp(SimHost h, CicodeTask t, ArgList a)
        {
            Value tab = a.Get(0);
            long n = (int)A.I(a.Get(1));
            double v = A.R(a.Get(2));
            short i = 0;
            while ((short)n >= 1)
            {
                n--;
                double d = v - Element(tab, i);
                if (-0.001 < d && d < 0.001)
                    return W.I(i);
                i++;
            }
            return W.I(-1);
        }

        [Builtin(
            "TableShift",
            Doc = "1192710",
            Effects = "shifts the REAL table's first Size elements",
            Notes = "Count > 0 moves the elements Count places towards offset 0, Count < 0 towards the end; the places left behind become 0; |Count| >= Size clears all Size elements. 0. Elements past a module / global array are neither read (0) nor written; a local variable is element 0 only. A Size of 0 or less changes nothing (the runtime's memset of a negative size would fault)."
        )]
        private static Value TableShift(SimHost h, CicodeTask t, ArgList a)
        {
            Value tab = a.Get(0);
            long size = A.Short(A.I(a.Get(1))),
                count = A.Short(A.I(a.Get(2)));
            if (size <= 0 || count == 0)
                return W.I(0);
            var v = new double[size];
            for (int i = 0; i < size; i++)
                v[i] = Element(tab, i);
            var r = new double[size];
            long c = Math.Abs(count);
            if (c < size)
            {
                for (long i = 0; i < size - c; i++)
                {
                    if (count > 0)
                        r[i] = v[i + c];
                    else
                        r[i + c] = v[i];
                }
            }
            for (int i = 0; i < size; i++)
                Store(t, tab, i, r[i]);
            return W.I(0);
        }

        // ------------------------------------------------------------------ debugging
        [Builtin(
            "Debug",
            Doc = "",
            Notes = "Undocumented. Debug(\"drvdebug\", n) asks the I/O server DLL's DrvDebug to dump n bytes and gives 0 (no driver runs in the simulator: nothing is dumped); any other first argument, or n below 1, 274 returned (not raised)."
        )]
        private static Value Debug(SimHost h, CicodeTask t, ArgList a)
        {
            if (C.IEq(A.S(a.Get(0)), "drvdebug") && a.Count > 1 && C.Atol(A.S(a.Get(1))) > 0)
                return W.I(0);
            return W.I(Codes.InvalidArg);
        }

        // ------------------------------------------------------------------ clusters
        private const int ClusterNotFound = 0x193,
            ClusterInactive = 0x195,
            ClusterActive = 0x196;

        /// <summary>The cluster of that name (cluster.dbf, without case), null when none or empty.</summary>
        private static string? Cluster(SimHost h, string name)
        {
            string n = CitectText.Trim(name);
            if (n.Length == 0)
                return null;
            foreach (string c in Servers.Clusters(h))
                if (C.IEq(c, n))
                    return CitectText.Lower(c);
            return null;
        }

        /// <summary>True when the cluster is active (every cluster is at start; ClusterDeactivate
        /// makes one inactive).</summary>
        internal static bool IsActive(SimHost h, string cluster) =>
            !ProcessExtras.Of(h).InactiveClusters.Contains(CitectText.Lower(CitectText.Trim(cluster)));

        [Builtin(
            "ClusterActivate",
            Doc = "1070589",
            Effects = "makes the cluster active (ClusterIsActive 1, ClusterStatus 2)",
            Errors = "403 an empty or unknown cluster (returned); 406 the cluster is active already (returned)"
        )]
        private static Value ClusterActivate(SimHost h, CicodeTask t, ArgList a)
        {
            string? c = Cluster(h, A.S(a.Get(0)));
            if (c == null)
                return W.I(ClusterNotFound);
            return W.I(ProcessExtras.Of(h).InactiveClusters.Remove(c) ? 0 : ClusterActive);
        }

        [Builtin(
            "ClusterDeactivate",
            Doc = "1070590",
            Effects = "makes the cluster inactive (ClusterIsActive 0, ClusterStatus 0)",
            Errors = "403 an empty or unknown cluster (returned)",
            Notes = "0 for a known cluster also when it is inactive already: the runtime drops the runtime's 405."
        )]
        private static Value ClusterDeactivate(SimHost h, CicodeTask t, ArgList a)
        {
            string? c = Cluster(h, A.S(a.Get(0)));
            if (c == null)
                return W.I(ClusterNotFound);
            ProcessExtras.Of(h).InactiveClusters.Add(c);
            return W.I(0);
        }

        [Builtin(
            "ClusterSwapActive",
            Doc = "1070598",
            Effects = "deactivates the active cluster of the two and activates the other",
            Errors = "403 an empty or unknown cluster (returned); 406 both are active, 405 both are inactive (returned)"
        )]
        private static Value ClusterSwapActive(SimHost h, CicodeTask t, ArgList a)
        {
            string? x = Cluster(h, A.S(a.Get(0))),
                y = Cluster(h, A.S(a.Get(1)));
            if (x == null || y == null)
                return W.I(ClusterNotFound);
            var off = ProcessExtras.Of(h).InactiveClusters;
            bool ax = !off.Contains(x),
                ay = !off.Contains(y);
            if (ax == ay)
                return W.I(ax ? ClusterActive : ClusterInactive);
            off.Add(ax ? x : y);
            off.Remove(ax ? y : x);
            return W.I(0);
        }

        // ------------------------------------------------------------------ kernel
        [Builtin(
            "KernelQueueLength",
            Doc = "1188597",
            Errors = "274 no queue of that name (raised, -1)",
            Notes = "The number of entries of the process's queue of that name (QueOpen's queues, the name without case). The simulator has no other kernel queues."
        )]
        private static Value KernelQueueLength(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            foreach (var (_, q) in h.Queues.Live())
                if (string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase))
                    return W.I(q.Items.Count);
            W.Raise(t, Codes.InvalidArg);
            return W.I(-1);
        }

        [Builtin(
            "KernelTableItemCount",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "1188599",
            Errors = "274 no kernel table of that name (raised, -1)",
            Notes = "The simulator has no kernel tables (Bufferpool, CiCode, Task, Tran, ...): every name is unknown, as finding none."
        )]
        private static Value KernelTableItemCount(SimHost h, CicodeTask t, ArgList a)
        {
            W.Raise(t, Codes.InvalidArg);
            return W.I(-1);
        }

        [Builtin(
            "KernelTableInfo",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Doc = "1188598",
            Errors = "447 no kernel table of that name (raised, '')",
            Notes = "No kernel tables in the simulator: every table is unknown (the runtime's 0x1bf without the table; 0x198 for a table without an info function)."
        )]
        private static Value KernelTableInfo(SimHost h, CicodeTask t, ArgList a)
        {
            W.Raise(t, 447);
            return W.Empty;
        }

        // ------------------------------------------------------------------ menus
        /// <summary>HiddenWhen / DisabledWhen of a node MenuNodeAddChild made: the function and its
        /// argument text, kept as the node's '&lt;HIDDENNAME&gt;' / '&lt;HIDDENARGS&gt;' fields
        /// ('&lt;DISABLEDNAME&gt;' ...), which MenuNodeIsHidden / MenuNodeIsDisabled run.</summary>
        private static Value SetWhen(SimHost h, CicodeTask t, ArgList a, string field)
        {
            long hd = A.I(a.Get(0));
            var tree = Menus.TreeOf(h);
            if (hd <= 0 || hd > int.MaxValue || !tree.ByHandle.TryGetValue((int)hd, out var n))
                return W.I(Codes.BadHandle);
            if (!n.Fields.ContainsKey("<CMDNAME>"))
                return W.I(Codes.InvalidArg);
            if (n.Fields.TryGetValue("<" + field + "NAME>", out string? set) && set.Length > 0)
                return W.I(Codes.InvalidArg);
            string fn = CitectText.Trim(A.S(a.Get(1))),
                args = a.Count > 2 ? A.S(a.Get(2)) : "";
            Function? f = fn.Length == 0 ? null : CallText.Find(t, fn);
            if (f == null)
                return W.I(Codes.InvalidArg);
            CallText.Bind(f, args, out int err);
            if (err != 0)
                return W.I(Codes.InvalidArg);
            n.Fields["<" + field + "NAME>"] = fn;
            n.Fields["<" + field + "ARGS>"] = args;
            return W.I(0);
        }

        /// <summary>The value of a node's HiddenWhen / DisabledWhen function (MenuNodeSetHiddenWhen),
        /// null when it has none: run nested in the caller, 1 for a non-zero result.</summary>
        internal static long? WhenValue(SimHost h, CicodeTask t, Menus.Node n, string field)
        {
            if (!n.Fields.TryGetValue("<" + field + "NAME>", out string? fn) || fn.Length == 0)
                return null;
            Function? f = CallText.Find(t, fn);
            if (f == null)
                return 0;
            var args = CallText.Bind(
                f,
                n.Fields.TryGetValue("<" + field + "ARGS>", out string? s) ? s : "",
                out _
            );
            CicodeTask? x = W.RunNested(h, t, f, args, 1000);
            if (x == null || x.State != TaskState.Stop || x.Result == null)
                return 0;
            Value v = Value.Deref(x.Result.Value);
            return Types.IsRealType(v.T) ? (v.D != 0 ? 1 : 0)
                : v.T == Types.String ? (C.Atol(v.S ?? "") != 0 ? 1 : 0)
                : (v.I != 0 ? 1 : 0);
        }

        [Builtin(
            "MenuNodeSetHiddenWhen",
            Doc = "1188544",
            Effects = "sets the node's HiddenWhen function (MenuNodeIsHidden runs it)",
            Errors = "269 a bad handle (returned); 274 a node not made by MenuNodeAddChild, HiddenWhen set already, or no such function / arguments that do not bind (returned)",
            Notes = "sHiddenWhenArgs is the call's argument text (static arguments). The function's result, non-zero = hidden."
        )]
        private static Value MenuNodeSetHiddenWhen(SimHost h, CicodeTask t, ArgList a) =>
            SetWhen(h, t, a, "HIDDEN");

        [Builtin(
            "MenuNodeSetDisabledWhen",
            Doc = "1188542",
            Effects = "sets the node's DisabledWhen function (MenuNodeIsDisabled runs it) and, when iDisabledStyle is 0 or more, its DISSTYLE",
            Errors = "269 a bad handle (returned); 274 a node not made by MenuNodeAddChild, DisabledWhen set already, or no such function / arguments that do not bind (returned)"
        )]
        private static Value MenuNodeSetDisabledWhen(SimHost h, CicodeTask t, ArgList a)
        {
            Value r = SetWhen(h, t, a, "DISABLED");
            long style = a.Count > 3 ? A.Short(A.I(a.Get(3))) : -1;
            if (r.I == 0 && style >= 0)
                Menus.TreeOf(h).ByHandle[(int)A.I(a.Get(0))].Fields["DISSTYLE"] = C.Inv(style);
            return r;
        }

        [Builtin(
            "MenuNodeGetCurr",
            Doc = "1188532",
            Notes = "The node whose command the calling task runs (MenuNodeRunCommand's task), -1 for any other task."
        )]
        private static Value MenuNodeGetCurr(SimHost h, CicodeTask t, ArgList a)
        {
            if (
                t.Data.TryGetValue("menunode", out object? o)
                && o is int hd
                && Menus.TreeOf(h).ByHandle.ContainsKey(hd)
            )
                return W.I(hd);
            return W.I(-1);
        }

        [Builtin(
            "MenuReload",
            Doc = "1188546",
            Effects = "drops the process's menu tree: the next menu function reads the compiled menu configuration again (nodes added, removed or changed since are gone, handles start again)"
        )]
        private static Value MenuReload(SimHost h, CicodeTask t, ArgList a)
        {
            ProcessData.Of(h).MenuTree = null;
            return W.I(0);
        }

        // ------------------------------------------------------------------ logging parameters
        /// <summary>The parameters SetLogging / GetLogging take (doc 1188617).</summary>
        private static readonly string[] LoggingParams =
        {
            "Debug.DriverTrace",
            "Debug.SysLogSize",
            "Debug.Priority",
            "Debug.CategoryFilterMode",
            "Debug.CategoryFilter",
            "Debug.SeverityFilterMode",
            "Debug.SeverityFilter",
            "Debug.LogShutDown",
            "Debug.DebugAllTrans",
            "Debug.EnableLogging",
            "IOServer.RedundancyDebug",
            "General.Verbose",
            "General.VerboseToSysLog",
            "CtAPI.Debug",
        };

        private static string? LoggingKey(string section, string name)
        {
            string k = CitectText.Trim(section) + "." + CitectText.Trim(name);
            foreach (string p in LoggingParams)
                if (C.IEq(p, k))
                    return CitectText.Lower(p);
            return null;
        }

        [Builtin(
            "SetLogging",
            Doc = "1188617",
            Effects = "sets the process's logging parameter; Persist writes it to the in-memory citect.ini too",
            Errors = "274 not one of the logging parameters (returned)",
            Notes = "The parameters of the help topic ([Debug]DriverTrace ... [CtAPI]Debug, without case). The simulator logs nothing differently: the value is only kept (GetLogging reads it)."
        )]
        private static Value SetLogging(SimHost h, CicodeTask t, ArgList a)
        {
            string section = A.S(a.Get(0)),
                name = A.S(a.Get(1)),
                value = A.S(a.Get(2));
            bool persist = a.Count > 3 && A.Short(A.I(a.Get(3))) != 0;
            string? k = LoggingKey(section, name);
            if (k == null)
                return W.I(Codes.InvalidArg);
            ProcessExtras.Of(h).Logging[k] = value;
            if (persist)
                ProcessData.Of(h).Ini.Put(IniFiles.CitectIniKey, section, name, value);
            return W.I(0);
        }

        [Builtin(
            "GetLogging",
            Doc = "1188591",
            Errors = "274 not one of the logging parameters (raised, '')",
            Notes = "The value SetLogging gave, else the parameter's citect.ini / compiled value, else '0' (guess: the runtime prints its current setting, the default of each is not read here; verify)."
        )]
        private static Value GetLogging(SimHost h, CicodeTask t, ArgList a)
        {
            string section = A.S(a.Get(0)),
                name = A.S(a.Get(1));
            string? k = LoggingKey(section, name);
            if (k == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            if (ProcessExtras.Of(h).Logging.TryGetValue(k, out string? v))
                return W.S(A.Cap(v));
            string? p = Params.Get(h, section, name);
            return W.S(!string.IsNullOrEmpty(p) ? A.Cap(p!) : "0");
        }

        // ------------------------------------------------------------------ internal test functions
        [Builtin(
            "TestVariantINT",
            Doc = "",
            Notes = "Undocumented test function: a VARIANT holding the LONG."
        )]
        private static Value TestVariantINT(SimHost h, CicodeTask t, ArgList a) =>
            Value.Long((int)A.I(a.Get(0)));

        [Builtin(
            "TestVariantREAL",
            Doc = "",
            Notes = "Undocumented test function: a VARIANT holding the REAL."
        )]
        private static Value TestVariantREAL(SimHost h, CicodeTask t, ArgList a) => Value.Real(A.R(a.Get(0)));

        [Builtin(
            "TestVariantSTRING",
            Doc = "",
            Notes = "Undocumented test function: a VARIANT holding the STRING."
        )]
        private static Value TestVariantSTRING(SimHost h, CicodeTask t, ArgList a) =>
            Value.Str(A.S(a.Get(0)));

        [Builtin(
            "TestVariantTIMESTAMP",
            Doc = "",
            Notes = "Undocumented test function: a VARIANT holding the TIMESTAMP."
        )]
        private static Value TestVariantTIMESTAMP(SimHost h, CicodeTask t, ArgList a) =>
            Value.Int(Types.Timestamp, (long)A.U64(a.Get(0)));

        [Builtin(
            "TestVariantQUALITY",
            Doc = "",
            Notes = "Undocumented test function: a VARIANT holding the QUALITY."
        )]
        private static Value TestVariantQUALITY(SimHost h, CicodeTask t, ArgList a) =>
            Value.Int(Types.Quality, (long)A.U64(a.Get(0)));

        [Builtin(
            "TestVariantSTRINGNULL",
            Doc = "",
            Notes = "Undocumented test function: a STRING VARIANT whose text pointer is null."
        )]
        private static Value TestVariantSTRINGNULL(SimHost h, CicodeTask t, ArgList a) => Value.Str(null);

        [Builtin(
            "TestDbl2Flt",
            Doc = "",
            Notes = "Undocumented test function: the REAL rounded to a 32-bit float ((float)x) and back."
        )]
        private static Value TestDbl2Flt(SimHost h, CicodeTask t, ArgList a) =>
            Value.Real((float)A.R(a.Get(0)));

        private const string NotSupported =
            "The table entry is the runtime's 'not supported' function: nothing happens, 0.";

        [Builtin("TestVariantNULL", Doc = "", Notes = NotSupported + " A null VARIANT.")]
        private static Value TestVariantNULL(SimHost h, CicodeTask t, ArgList a) => Value.Nothing;

        [Builtin("TestStringNULL", Doc = "", Notes = NotSupported + " A null string: ''.")]
        [Builtin("PointData", Doc = "", Notes = NotSupported + " A null string: ''.")]
        [Builtin("FtpFileFind", Doc = "", Notes = NotSupported + " A null string: ''.")]
        private static Value NullString(SimHost h, CicodeTask t, ArgList a) => W.Empty;

        [Builtin("PointNew", Doc = "", Notes = NotSupported)]
        [Builtin("PointFree", Doc = "", Notes = NotSupported)]
        [Builtin("PointWrite", Doc = "", Notes = NotSupported)]
        [Builtin("PointRead", Doc = "", Notes = NotSupported)]
        [Builtin("PointStatus", Doc = "", Notes = NotSupported)]
        [Builtin("PointWriteArrayLong", Doc = "", Notes = NotSupported)]
        [Builtin("PointWriteArrayReal", Doc = "", Notes = NotSupported)]
        [Builtin("FtpOpen", Doc = "", Notes = NotSupported)]
        [Builtin("FtpClose", Doc = "", Notes = NotSupported)]
        [Builtin("FtpFileCopy", Doc = "", Notes = NotSupported)]
        [Builtin("FtpFileFindClose", Doc = "", Notes = NotSupported)]
        [Builtin("ErrHelp", Doc = "", Notes = NotSupported)]
        private static Value ReturnZero(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        // ------------------------------------------------------------------ tag override quality
        private static Value SetOverride(
            SimHost h,
            CicodeTask t,
            string tag,
            ulong quality,
            bool sync,
            string cluster,
            string fn
        )
        {
            var db = Tags.Db(h);
            if (db == null)
                return Tags.NoWorld(fn);
            if (Tags.TooLong(t, tag))
                return W.I(Codes.TagNameTooLong);
            if (sync && t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            int code = db.SetOverrideQuality(tag, quality, "cicode", cluster);
            if (code != 0)
            {
                W.Raise(t, code);
                return W.I(code);
            }
            if (sync)
                return W.BlockOneTurn(t, fn + "(" + tag + ")", () => W.I(0));
            return W.I(0);
        }

        private const string OverrideErrors =
            "557 a name of 255+ characters (raised and returned); 424 no such tag, 274 a local variable or an alarm property (raised and returned); 282 bSynch in foreground code (raised and returned)";

        [Builtin(
            "TagSetOverrideQuality",
            TagArg = 0,
            Doc = "1192751",
            Blocking = true,
            Effects = "sets the quality of the tag's Override element (its value stays; setting OverrideMode later overwrites it)",
            Errors = OverrideErrors,
            Notes = "Synch (default TRUE) waits one turn for the write. The tag may be 'Cluster.Tag' or Equipment.Item."
        )]
        private static Value TagSetOverrideQuality(SimHost h, CicodeTask t, ArgList a) =>
            SetOverride(
                h,
                t,
                A.S(a.Get(0)),
                A.U64(a.Get(1)),
                a.Count <= 2 || A.I(a.Get(2)) != 0,
                a.Count > 3 ? A.S(a.Get(3)) : "",
                "TagSetOverrideQuality"
            );

        [Builtin(
            "TagSetOverrideGood",
            TagArg = 0,
            Doc = "1192750",
            Blocking = true,
            Effects = "sets the Override element's quality to Good Non Specific (0xC0)",
            Errors = OverrideErrors
        )]
        private static Value TagSetOverrideGood(SimHost h, CicodeTask t, ArgList a) =>
            SetOverride(
                h,
                t,
                A.S(a.Get(0)),
                0xC0,
                a.Count <= 1 || A.I(a.Get(1)) != 0,
                a.Count > 2 ? A.S(a.Get(2)) : "",
                "TagSetOverrideGood"
            );

        [Builtin(
            "TagSetOverrideUncertain",
            TagArg = 0,
            Doc = "1192752",
            Blocking = true,
            Effects = "sets the Override element's quality to Uncertain Non Specific (0x40)",
            Errors = OverrideErrors
        )]
        private static Value TagSetOverrideUncertain(SimHost h, CicodeTask t, ArgList a) =>
            SetOverride(
                h,
                t,
                A.S(a.Get(0)),
                0x40,
                a.Count <= 1 || A.I(a.Get(1)) != 0,
                a.Count > 2 ? A.S(a.Get(2)) : "",
                "TagSetOverrideUncertain"
            );

        [Builtin(
            "TagSetOverrideBad",
            TagArg = 0,
            Doc = "1192749",
            Blocking = true,
            Effects = "sets the Override element's quality to Bad Non Specific (0)",
            Errors = OverrideErrors
        )]
        private static Value TagSetOverrideBad(SimHost h, CicodeTask t, ArgList a) =>
            SetOverride(
                h,
                t,
                A.S(a.Get(0)),
                0,
                a.Count <= 1 || A.I(a.Get(1)) != 0,
                a.Count > 2 ? A.S(a.Get(2)) : "",
                "TagSetOverrideBad"
            );

        // ------------------------------------------------------------------ VariableToStr
        [Builtin(
            "VariableToStr",
            Doc = "",
            Errors = "274 with bFormat, a TIMESTAMP the date conversion cannot take (raised, '')",
            Notes = "Undocumented. The value (a VARIANT read through) as text, 255 characters at most: with bFormat non-zero a TIMESTAMP is 'yyyy-MM-dd HH:mm:ss.fff' in local time and a QUALITY its quality text; otherwise, and for other types, the value's STRING conversion. A null value gives ''."
        )]
        private static Value VariableToStr(SimHost h, CicodeTask t, ArgList a)
        {
            Value v = Value.Deref(a.Get(0));
            bool fmt = a.Count > 1 && A.Short(A.I(a.Get(1))) != 0;
            if (v.IsNothing)
                return W.Empty;
            if (fmt && v.T == Types.Timestamp)
            {
                if (!Time.DateOfFt(unchecked((ulong)v.I), out DateTime u))
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                return W.S(A.Cap(Time.FormatTs(h, u, false, "yyyy-MM-dd HH:mm:ss.fff", h.Locale.DotNet())));
            }
            if (fmt && v.T == Types.Quality)
                return W.S(A.Cap(Conv.QualityToStr(unchecked((ulong)v.I))));
            return W.S(A.Cap(VmResultText(v)));
        }

        private static string VmResultText(in Value v) => Conv.ToStr(v, NoErr.Instance).S ?? "";

        // ------------------------------------------------------------------ RDB
        [Builtin(
            "rdbSetPath",
            Doc = "",
            Effects = "sets the process's compiled-database search path",
            Notes = "Undocumented. The ';'-separated folders, each without its trailing '\\', duplicates (without case) and empty entries dropped, 1027 at most (more: 0 and the path stays empty). 1 when it was taken. The simulator keeps reading the unit's compiled files: the path is only remembered (rdbGetPath)."
        )]
        private static Value RdbSetPath(SimHost h, CicodeTask t, ArgList a)
        {
            var x = ProcessExtras.Of(h);
            var list = new List<string>();
            x.RdbPath = list;
            foreach (string part in A.S(a.Get(0)).Split(';'))
            {
                string p = part.TrimEnd('\\', '/');
                if (p.Length == 0 || list.Exists(e => C.IEq(e, p)))
                    continue;
                if (list.Count > 0x402)
                    return W.I(0);
                list.Add(p);
            }
            return W.I(1);
        }

        [Builtin(
            "rdbGetPath",
            Doc = "",
            Notes = "Undocumented. The search path's folders joined by ';' (255 characters at most here: the runtime's buffer is 256 bytes). Before any rdbSetPath: the unit's root project folder (guess: the runtime sets it from the run path at start; verify)."
        )]
        private static Value RdbGetPath(SimHost h, CicodeTask t, ArgList a)
        {
            var x = ProcessExtras.Of(h);
            if (x.RdbPath == null)
                return W.S(A.Cap(h.World?.Unit.RootDir.TrimEnd('\\', '/') ?? ""));
            return W.S(A.Cap(string.Join(";", x.RdbPath)));
        }

        [Builtin(
            "RdbOpenSub",
            Doc = "",
            Notes = "Undocumented. Another table of the file an RdbOpen handle has open: a new handle on its first record position, -1 for a bad handle or a table the file lacks."
        )]
        private static Value RdbOpenSub(SimHost h, CicodeTask t, ArgList a)
        {
            var cursors = ProcessData.Of(h).RdbCursors;
            var c = cursors.Get(A.Short(A.I(a.Get(0))));
            if (c == null)
                return W.I(-1);
            string name = CitectText.Trim(A.S(a.Get(1)));
            foreach (string n in c.File.Order)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase) && !c.File.Sections[n].IsPool)
                    return W.I(
                        cursors.Add(new RdbTables.Cursor { File = c.File, Table = c.File.Sections[n] })
                    );
            return W.I(-1);
        }

        [Builtin(
            "RdbOpenPage",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "",
            Notes = "Undocumented. The runtime loads the named compiled file whole; the simulator opens compiled tables only through RdbOpen: -1."
        )]
        private static Value RdbOpenPage(SimHost h, CicodeTask t, ArgList a) => W.I(-1);

        // ------------------------------------------------------------------ project restart
        [Builtin(
            "ProjectRestartSet",
            Doc = "1188610",
            Effects = "sets the project the next restart runs (kept by the process; a simulation is never restarted)",
            Notes = "0."
        )]
        private static Value ProjectRestartSet(SimHost h, CicodeTask t, ArgList a)
        {
            ProcessExtras.Of(h).RestartProject = A.S(a.Get(0));
            return W.I(0);
        }

        [Builtin(
            "ProjectRestartGet",
            Doc = "1188609",
            Notes = "The ProjectRestartSet text; '' before any (the runtime's static buffer starts empty; verify)."
        )]
        private static Value ProjectRestartGet(SimHost h, CicodeTask t, ArgList a) =>
            W.S(A.Cap(ProcessExtras.Of(h).RestartProject));

        [Builtin(
            "ProjectSet",
            Doc = "1188611",
            Effects = "writes [CTEDIT]RUN of the in-memory citect.ini (never the real file)",
            Notes = "A name without ':' or '\\' is looked up in the User folder's MASTER.DBF (NAME without case) and its PATH used without a trailing '\\' ('' when it is not there); a path is used as given. 0."
        )]
        private static Value ProjectSet(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)),
                v = name;
            if (name.IndexOf(':') < 0 && name.IndexOf('\\') < 0)
            {
                v = "";
                string? user = CitectIni.LiveUserDir();
                if (h.World != null)
                {
                    WinPath.Split(h.World.Unit.RootDir.TrimEnd('\\', '/'), out string head, out _);
                    user = head;
                }
                if (
                    !string.IsNullOrEmpty(user)
                    && Projects.MasterProjects(user!).TryGetValue(CitectText.Lower(name), out string? p)
                )
                    v =
                        p.Length > 1 && p.EndsWith("\\", StringComparison.Ordinal)
                            ? p.Substring(0, p.Length - 1)
                            : p;
            }
            ProcessData.Of(h).Ini.Put(IniFiles.CitectIniKey, "CTEDIT", "RUN", v);
            return W.I(0);
        }

        // ------------------------------------------------------------------ TimeToFmtStr
        private static readonly string[] MonthLong =
        {
            "January",
            "February",
            "March",
            "April",
            "May",
            "June",
            "July",
            "August",
            "September",
            "October",
            "November",
            "December",
        };
        private static readonly string[] MonthShort =
        {
            "Jan",
            "Feb",
            "Mar",
            "Apr",
            "May",
            "Jun",
            "Jul",
            "Aug",
            "Sep",
            "Oct",
            "Nov",
            "Dec",
        };
        private static readonly string[] DayLong =
        {
            "Sunday",
            "Monday",
            "Tuesday",
            "Wednesday",
            "Thursday",
            "Friday",
            "Saturday",
        };
        private static readonly string[] DayShort = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        private static readonly string[] Ordinal =
        {
            "th",
            "st",
            "nd",
            "rd",
            "th",
            "th",
            "th",
            "th",
            "th",
            "th",
        };

        /// <summary>The format tokens in the order they are tested (without case).</summary>
        private static readonly (string K, int T)[] FmtTokens =
        {
            ("MM", 0xe),
            ("MONTH", 0xf),
            ("MON", 0x10),
            ("DY", 0x17),
            ("DAY", 0x16),
            ("HH12", 0x1b),
            ("HH24", 0x1c),
            ("HH", 0x1b),
            ("MI", 0x1d),
            ("SSSS", 0x1e),
            ("SS", 0x1f),
            ("DDD", 0x13),
            ("DD", 0x14),
            ("D", 0x15),
            ("AM", 0x19),
            ("PM", 0x19),
            ("A.M.", 0x1a),
            ("P.M.", 0x1a),
            ("Y,YYY", 8),
            ("YYYY", 4),
            ("YYY", 5),
            ("YY", 6),
            ("Y", 7),
            ("AD", 0xb),
            ("BC", 0xb),
            ("A.D.", 0xc),
            ("B.C.", 0xc),
            ("TH", 0x21),
        };

        /// <summary>the next format token (0 at the end; 0x20 a literal in
        /// <paramref name="lit"/>: one character, or the text up to the closing double quote).</summary>
        private static int Token(string f, ref int i, ref string lit)
        {
            if (i >= f.Length)
                return 0;
            foreach (var (k, tok) in FmtTokens)
                if (
                    i + k.Length <= f.Length
                    && string.Compare(f, i, k, 0, k.Length, StringComparison.OrdinalIgnoreCase) == 0
                )
                {
                    i += k.Length;
                    return tok;
                }
            char c = f[i++];
            if (c != '"')
            {
                lit = c.ToString();
                return 0x20;
            }
            var sb = new System.Text.StringBuilder();
            while (i < f.Length)
            {
                char x = f[i++];
                if (x == '"')
                    break;
                sb.Append(x);
            }
            lit = sb.ToString();
            return 0x20;
        }

        [Builtin(
            "TimeToFmtStr",
            Doc = "",
            Notes = "Undocumented: the local time of Time (seconds since 1970) in an Oracle-like format, the tokens matched without case in this order: MM month 01-12, MONTH / MON its name, DY / DAY the weekday's short / full name, HH12 / HH the hour 01-12, HH24 00-23, MI minutes, SSSS seconds since midnight (4 digits at least), SS seconds, DDD the day of the year counted from 000 (the runtime's tm_yday), DD the day of the month, D the weekday 1-7 (Sunday 1), AM / PM and A.M. / P.M. by the hour, Y,YYY / YYYY / YYY / YY / Y the year, AD / BC and A.D. / B.C. always 'AD' / 'A.D.', TH the ordinal suffix of the last number written (st, nd, rd, th; 11-13 th); text in double quotes and any other character are copied. Names are the English native strings; 254 characters at most."
        )]
        private static Value TimeToFmtStr(SimHost h, CicodeTask t, ArgList a)
        {
            var tm = Time.Break(h, (int)A.I(a.Get(0)), false);
            string f = A.S(a.Get(1)),
                lit = "";
            var o = new System.Text.StringBuilder();
            int i = 0,
                last = 0;
            void Num(int v, int width)
            {
                last = v;
                o.Append(
                    v.ToString(new string('0', width), System.Globalization.CultureInfo.InvariantCulture)
                );
            }
            for (int tok = Token(f, ref i, ref lit); tok != 0; tok = Token(f, ref i, ref lit))
            {
                int year = (short)(1900 + tm.Year);
                switch (tok)
                {
                    case 4:
                        Num(year, 4);
                        break;
                    case 5:
                        Num(year % 1000, 3);
                        break;
                    case 6:
                        Num(tm.Year % 100, 2);
                        break;
                    case 7:
                        last = tm.Year % 10;
                        o.Append(C.Inv(last));
                        break;
                    case 8:
                        last = year;
                        o.Append(C.Inv(year / 1000))
                            .Append(',')
                            .Append(
                                (year % 1000).ToString(
                                    "000",
                                    System.Globalization.CultureInfo.InvariantCulture
                                )
                            );
                        break;
                    case 0xb:
                        o.Append("AD");
                        break;
                    case 0xc:
                        o.Append("A.D.");
                        break;
                    case 0xe:
                        Num(tm.Mon + 1, 2);
                        break;
                    case 0xf:
                        o.Append(MonthLong[tm.Mon]);
                        break;
                    case 0x10:
                        o.Append(MonthShort[tm.Mon]);
                        break;
                    case 0x13:
                        Num(tm.YDay, 3);
                        break;
                    case 0x14:
                        Num(tm.MDay, 2);
                        break;
                    case 0x15:
                        last = tm.WDay + 1;
                        o.Append(C.Inv(last));
                        break;
                    case 0x16:
                        o.Append(DayLong[tm.WDay]);
                        break;
                    case 0x17:
                        o.Append(DayShort[tm.WDay]);
                        break;
                    case 0x19:
                        o.Append(tm.Hour >= 12 ? "PM" : "AM");
                        break;
                    case 0x1a:
                        o.Append(tm.Hour >= 12 ? "P.M." : "A.M.");
                        break;
                    case 0x1b:
                        Num(tm.Hour % 12 == 0 ? 12 : tm.Hour % 12, 2);
                        break;
                    case 0x1c:
                        Num(tm.Hour, 2);
                        break;
                    case 0x1d:
                        Num(tm.Min, 2);
                        break;
                    case 0x1e:
                        o.Append(
                            ((tm.Hour * 60 + tm.Min) * 60 + tm.Sec).ToString(
                                "0000",
                                System.Globalization.CultureInfo.InvariantCulture
                            )
                        );
                        break; // the runtime jumps past the store of the last number
                    case 0x1f:
                        Num(tm.Sec, 2);
                        break;
                    case 0x20:
                        o.Append(lit);
                        break;
                    case 0x21:
                    {
                        int k = last == 11 || last == 12 || last == 13 ? 10 : (short)last;
                        o.Append(Ordinal[Math.Abs(k % 10)]);
                        break;
                    }
                }
            }
            string r = o.ToString();
            return W.S(r.Length > 254 ? r.Substring(0, 254) : r);
        }

        // ------------------------------------------------------------------ the rest
        [Builtin(
            "MultiMonitorStart",
            Doc = "1193973",
            Notes = "Starts the screen profile's other monitors' pages when a screen profile is loaded; the simulator has none (as ResetScreenProfile): nothing happens."
        )]
        private static Value MultiMonitorStart(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "PurgeMemory",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "",
            Notes = "Undocumented: a garbage collection of the runtime's .NET host, for testing. Nothing to purge: 0."
        )]
        private static Value PurgeMemory(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        private static readonly string[] UnitTests =
        {
            "GetEffectsModeOfObject4",
            "GetEffectsModeOfObject5",
            "GetEffectsModeOfObject6",
            "GetEffectsModeOfObject7",
            "GetEffectsModeOfObject8",
            "GetEffectsModeOfObject9",
            "TestWindowCloseButton",
            "TestRemovingDuplicateSeperatorFromSystemMenu",
        };

        [Builtin(
            "_RunTest",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "",
            Notes = "Undocumented: runs one of the display client's built-in unit tests (GetEffectsModeOfObject4..9, TestWindowCloseButton, TestRemovingDuplicateSeperatorFromSystemMenu, without case). The tests check Windows window chrome and are not run: 0 for those names (verify), -1 for an empty or other name, as the runtime."
        )]
        private static Value RunTest(SimHost h, CicodeTask t, ArgList a)
        {
            string n = A.S(a.Get(0));
            foreach (string u in UnitTests)
                if (C.IEq(u, n))
                    return W.I(0);
            return W.I(-1);
        }

        [Builtin(
            "_RunTests",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "",
            Notes = "Undocumented: runs every built-in unit test and writes a summary file; neither is done here: 0."
        )]
        private static Value RunTests(SimHost h, CicodeTask t, ArgList a) => W.I(0);
    }
}
