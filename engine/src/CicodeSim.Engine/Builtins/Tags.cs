using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using WQuality = CicodeSim.World.Quality;

namespace CicodeSim.Builtins
{
    /// <summary>A tag subscription (TagSubscribe, TagGetValue's managed ones): the resolved tag,
    /// its scale mode and the public functions called with the handle when the value changes.</summary>
    internal sealed class Subscription
    {
        public Resolved Res = null!;
        public string Name = "";
        public string Cluster = "";

        /// <summary>0 Eng, 1 Raw, 2 Percent.</summary>
        public int Mode;
        public int PollMs = 250;
        public bool Lightweight;
        public readonly List<string> Callbacks = new List<string>();
        public CicodeVm? Vm;
    }

    /// <summary>Tag built-ins over the World's TagDb.</summary>
    public static class Tags
    {
        internal static TagDb? Db(SimHost h) => h.World?.Tags;

        internal static Value NoWorld(string name) => Value.NotSimulated(name, -1);

        internal static int Flags(CicodeTask t) =>
            (t.WriteLocal ? TaskFlags.WriteLocal : 0)
            | (t.ScaleCheck ? TaskFlags.ScaleCheck : 0)
            | TaskFlags.EchoError
            | TaskFlags.IgnoreCase;

        /// <summary>a name of 255+ characters raises 'Tag name exceed length limit'.</summary>
        internal static bool TooLong(CicodeTask t, string name)
        {
            if (name.Length > 254)
            {
                W.Raise(t, Codes.TagNameTooLong);
                return true;
            }
            return false;
        }

        internal static Resolved Resolve(TagDb db, string name, string cluster = "") =>
            db.Resolve(name, "", "", cluster);

        /// <summary>The hardware error a bad quality stands for (guess: tag not found 424, waiting 423, a DS error code when the quality has one, else
        /// 281 '#COM').</summary>
        internal static int QualityError(ulong q)
        {
            if (WQuality.IsGood(q))
                return 0;
            if (q == WQuality.QualityTagNotFound)
                return Codes.TagNotFound;
            if (q == WQuality.QualityWaiting)
                return 423;
            long e = WQuality.ErrorCode(q);
            if (e > 0 && e < 0x10000)
                return (int)e;
            return 281;
        }

        private static int? Index(Resolved r, long offset)
        {
            if (offset == 0)
                return r.Index;
            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (r.Index ?? 0) + offset));
        }

        private static string Text(in Value v) => Conv.ToStr(v, NoErr.Instance).S ?? "";

        /// <summary>The read TagRead / TagReadEx complete with (at the task's next turn).</summary>
        private static Value ReadNow(
            TagDb db,
            CicodeTask t,
            string name,
            long offset,
            string cluster,
            bool ex
        )
        {
            Resolved r = Resolve(db, name, cluster);
            if (!r.Found)
            {
                W.Raise(t, Codes.TagNotFound);
                return ex ? Value.Long(0, WQuality.QualityTagNotFound) : W.Empty;
            }
            Value v = db.ReadResolved(r, Index(r, offset), false, "cicode", out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return ex ? v : W.Empty;
            }
            if (ex)
                return v;
            int qe = QualityError(v.Q);
            if (qe != 0 && r.Item != "v")
            {
                W.Raise(t, qe);
                return W.Empty;
            }
            return W.S(Text(v));
        }

        [Builtin(
            "TagRead",
            TagArg = 0,
            Doc = "1192745",
            Blocking = true,
            Errors = "557 name of 255+ characters; 282 in foreground code; 424 tag not found; the quality's error when not Good (all raised, '' returned)",
            Notes = "Completes at the task's next turn with the World's value as a string (INT decimal, REAL %.15G, verify against a runtime); 'Tag.v' reads a bad-quality value too; nOffset adds to an index."
        )]
        private static Value TagRead(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return NoWorld("TagRead");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.Empty;
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.Empty;
            }
            long off = A.I(a.Get(1));
            string cl = A.S(a.Get(2));
            return W.BlockOneTurn(t, "TagRead(" + name + ")", () => ReadNow(db, t, name, off, cl, false));
        }

        [Builtin(
            "TagReadEx",
            TagArg = 0,
            Doc = "1192746",
            Blocking = true,
            Errors = "557 name of 255+ characters; 282 in foreground code; 424 tag not found (raised)",
            Notes = "As TagRead, the value with its own type, quality and timestamps (a VARIANT)."
        )]
        private static Value TagReadEx(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return NoWorld("TagReadEx");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return Value.Long(0);
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return Value.Long(0);
            }
            long off = A.I(a.Get(1));
            string cl = A.S(a.Get(2));
            return W.BlockOneTurn(t, "TagReadEx(" + name + ")", () => ReadNow(db, t, name, off, cl, true));
        }

        /// <summary>The write TagWrite makes: the item checks, then the World's.</summary>
        internal static int WriteName(
            TagDb db,
            CicodeTask t,
            string name,
            Value v,
            long offset,
            string cluster
        )
        {
            Resolved r = Resolve(db, name, cluster);
            if (!r.Found)
                return Codes.TagNotFound;
            if (r.Item.Length > 0 && r.Item != "v")
                return Codes.InvalidArg;
            return db.WriteResolved(r, TagDb.WVal.Of(v), Flags(t), Index(r, offset), "cicode");
        }

        [Builtin(
            "TagWrite",
            TagArg = 0,
            Doc = "1192756",
            Blocking = true,
            Effects = "writes the tag (asynchronous unless bSynch)",
            Errors = "557 name of 255+ characters; 424 tag not found; 274 a value the tag cannot take; 257 out of range with ScaleCheck; 282 bSynch in foreground code (raised and returned)",
            Notes = "sValue is converted to the tag's type; bSynch 1 blocks until the write is done (one turn)."
        )]
        private static Value TagWrite(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return NoWorld("TagWrite");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.I(Codes.TagNameTooLong);
            string value = A.S(a.Get(1));
            long off = A.I(a.Get(2));
            bool synch = A.I(a.Get(3)) != 0;
            string cl = A.S(a.Get(4));
            if (synch && t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            int code = WriteName(db, t, name, Value.Str(value), off, cl);
            if (code != 0)
            {
                W.Raise(t, code);
                return W.I(code);
            }
            if (synch)
                return W.BlockOneTurn(t, "TagWrite(" + name + ")", () => W.I(0));
            return W.I(0);
        }

        [Builtin(
            "TagWriteIntArray",
            TagArg = 0,
            Doc = "1192757",
            Blocking = true,
            Effects = "writes nLength elements of the tag from the array",
            Errors = "424 tag not found; 259 past the tag's array (returned)",
            Notes = "Writes nValueArray elements one by one from nOffset (verify: the array argument is the first element passed by reference)."
        )]
        private static Value TagWriteIntArray(SimHost h, CicodeTask t, ArgList a) =>
            WriteArray(h, t, a, "TagWriteIntArray");

        [Builtin(
            "TagWriteRealArray",
            TagArg = 0,
            Doc = "1192758",
            Blocking = true,
            Effects = "writes nLength elements of the tag from the array",
            Errors = "424 tag not found; 259 past the tag's array (returned)",
            Notes = "As TagWriteIntArray (verify)."
        )]
        private static Value TagWriteRealArray(SimHost h, CicodeTask t, ArgList a) =>
            WriteArray(h, t, a, "TagWriteRealArray");

        private static Value WriteArray(SimHost h, CicodeTask t, ArgList a, string fn)
        {
            var db = Db(h);
            if (db == null)
                return NoWorld(fn);
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.I(Codes.TagNameTooLong);
            long n = A.I(a.Get(1));
            Value arr = a.Get(2);
            long off = A.I(a.Get(3));
            string cl = A.S(a.Get(5));
            int code = 0;
            for (long i = 0; i < n && code == 0; i++)
            {
                Value v;
                if (arr.O is CodeVarRef cr)
                {
                    v = cr.Slot.Get(cr.Element + i, out int e);
                    if (e != 0)
                        break;
                }
                else if (i == 0)
                    v = arr.T == Types.Variant ? Value.Deref(arr) : arr;
                else
                    break;
                code = WriteName(db, t, name, v, off + i, cl);
            }
            if (code != 0)
                W.Raise(t, code);
            return W.I(code);
        }

        // ------------------------------------------------------------------ properties
        /// <summary>[Client]TagReadCachedMode (default 3) for a CachedMode of -1.</summary>
        private static long CachedMode(SimHost h, long mode)
        {
            if (mode != -1)
                return mode;
            string? s = h.World?.Param("Client", "TagReadCachedMode");
            if (string.IsNullOrEmpty(s))
                return 3;
            return Conv.StrToInt(s, out _);
        }

        private static Value PropertyNow(
            SimHost h,
            TagDb db,
            CicodeTask t,
            string name,
            string prop,
            string cluster,
            Func<TagDef, string?> get,
            string fn
        )
        {
            Resolved r = Resolve(db, name, cluster);
            if (!r.Found || r.Tag == null)
            {
                W.Raise(t, Codes.TagNotFound);
                return W.Empty;
            }
            string? v = get(r.Tag);
            if (v == null)
            {
                W.Raise(t, Codes.PropertyNotFound);
                return W.Empty;
            }
            return W.S(A.Cap(v));
        }

        /// <summary><paramref name="f"/> with the task's ErrSet level raised by one: its errors are
        /// raised suppressed (TagInfo / TagInfoEx), never halting or reported.</summary>
        internal static Value Suppressed(CicodeTask t, Func<Value> f)
        {
            t.ErrSetLevel++;
            try
            {
                return f();
            }
            finally
            {
                if (t.ErrSetLevel > 0)
                    t.ErrSetLevel--;
            }
        }

        internal static Value PropertyCall(
            SimHost h,
            CicodeTask t,
            string name,
            string prop,
            long mode,
            string cluster,
            Func<TagDef, string?> get,
            string fn,
            bool suppressed = false
        )
        {
            if (suppressed)
                return Suppressed(t, () => PropertyCallCore(h, t, name, prop, mode, cluster, get, fn, true));
            return PropertyCallCore(h, t, name, prop, mode, cluster, get, fn, false);
        }

        private static Value PropertyCallCore(
            SimHost h,
            CicodeTask t,
            string name,
            string prop,
            long mode,
            string cluster,
            Func<TagDef, string?> get,
            string fn,
            bool suppressed
        )
        {
            var db = Db(h)!;
            mode = CachedMode(h, mode);
            if (name.Length == 0 || mode < 0 || mode > 3)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            if (mode == 0)
            {
                if (t.Foreground)
                {
                    W.Raise(t, Codes.FgBlock);
                    return W.Empty;
                }
                // the answer comes at the task's next turn: TagInfo's suppression covers it too
                return W.BlockOneTurn(t, fn + "(" + name + ")", () =>
                    suppressed
                        ? Suppressed(t, () => PropertyNow(h, db, t, name, prop, cluster, get, fn))
                        : PropertyNow(h, db, t, name, prop, cluster, get, fn)
                );
            }
            return PropertyNow(h, db, t, name, prop, cluster, get, fn);
        }

        [Builtin(
            "TagGetProperty",
            TagArg = 0,
            Doc = "1192738",
            Errors = "557 name of 255+ characters; 274 empty name or bad mode; 424 tag not found; 348 unknown property; 282 mode 0 in foreground code (raised, '' returned)",
            Notes = "Properties of the tag definition (variable.dbf / locvar.dbf, TagDef.Property, case-sensitive names); CachedMode 0 blocks one turn, 1..3 answer at once; -1 = [Client]TagReadCachedMode (default 3)."
        )]
        private static Value TagGetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("TagGetProperty");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.Empty;
            string prop = A.S(a.Get(1));
            long mode = a.Count > 2 ? A.I(a.Get(2)) : -1;
            return PropertyCall(
                h,
                t,
                name,
                prop,
                mode,
                A.S(a.Get(3)),
                d => d.Property(prop),
                "TagGetProperty"
            );
        }

        /// <summary>TagInfo's sType: a number (the help's table) or a TagGetProperty name.</summary>
        private static Func<TagDef, string?> InfoGetter(string type)
        {
            string s = type.Trim();
            if (s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '-'))
            {
                int k = (int)Conv.StrToInt(s, out _);
                return d => d.Info(k);
            }
            return d => d.Property(s);
        }

        [Builtin(
            "TagInfo",
            TagArg = 0,
            Doc = "1192741",
            Errors = "424 tag not found; 348 unknown type (raised suppressed: never halts)",
            Notes = "sType is the type number as text (0 name .. 33 Custom8; 9, 12, 13, 19, 21, 24 need the I/O configuration and give 348) or a property name; errors are raised as under ErrSet(1)."
        )]
        private static Value TagInfo(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("TagInfo");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.Empty;
            if (name.Length == 0)
                return W.Empty;
            long mode = a.Count > 3 ? A.I(a.Get(3)) : -1;
            return PropertyCall(
                h,
                t,
                name,
                "",
                mode,
                A.S(a.Get(2)),
                InfoGetter(A.S(a.Get(1))),
                "TagInfo",
                suppressed: true
            );
        }

        [Builtin(
            "TagInfoEx",
            TagArg = 0,
            Doc = "1192742",
            Errors = "424 tag not found; 274 unknown type (raised suppressed)",
            Notes = "As TagInfo with the type as a number (0..33 without 9 and 19)."
        )]
        private static Value TagInfoEx(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("TagInfoEx");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.Empty;
            long k = A.Short(A.I(a.Get(1)));
            if (name.Length == 0)
                return W.Empty;
            if (k < 0 || k > 0x21 || k == 9 || k == 0x13 || k == 0x17)
                return Suppressed(t, () =>
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                });
            long mode = a.Count > 3 ? A.I(a.Get(3)) : -1;
            return PropertyCall(
                h,
                t,
                name,
                "",
                mode,
                A.S(a.Get(2)),
                d => d.Info((int)k),
                "TagInfoEx",
                suppressed: true
            );
        }

        /// <summary>The tag's value at a percentage of its engineering range, in its own format
        /// (and units).</summary>
        internal static string ScaleText(TagDef d, double pct, bool eu)
        {
            var (ez, ef) = d.EngRange();
            double v = ez + (ef - ez) * pct / 100.0;
            long word = TagTypes.FormatWord(d.DisplayFormat);
            if (eu)
                word |= 0x40000;
            else
                word &= ~0x40000L;
            int type = d.Type == Types.String ? Types.Real : Types.Real;
            return Conv.FormatValue(Value.RealOf(type, v), word, d.EngUnits, NoErr.Instance).S ?? "";
        }

        [Builtin(
            "TagGetScale",
            TagArg = 0,
            Doc = "1192739",
            Errors = "557 name of 255+ characters; 424 tag not found; 274 bad mode; 282 mode 0 in foreground code (raised, '' returned)",
            Notes = "EngZero + (EngFull - EngZero) * Percent / 100 printed with the tag's format (and ' ' + units with EngUnits 1) (verify the REAL format)."
        )]
        private static Value TagGetScale(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("TagGetScale");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.Empty;
            double pct = A.I(a.Get(1));
            bool eu = A.I(a.Get(2)) != 0;
            long mode = a.Count > 3 ? A.I(a.Get(3)) : -1;
            return PropertyCall(
                h,
                t,
                name,
                "",
                mode,
                A.S(a.Get(4)),
                d => ScaleText(d, pct, eu),
                "TagGetScale"
            );
        }

        [Builtin(
            "TagScaleStr",
            TagArg = 0,
            Doc = "1192754",
            Errors = "as TagGetScale",
            Notes = "TagGetScale with the arguments (sTag, Percent, EngUnits, ClusterName, CachedMode)."
        )]
        private static Value TagScaleStr(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("TagScaleStr");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.Empty;
            double pct = A.I(a.Get(1));
            bool eu = A.I(a.Get(2)) != 0;
            long mode = a.Count > 4 ? A.I(a.Get(4)) : -1;
            return PropertyCall(
                h,
                t,
                name,
                "",
                mode,
                A.S(a.Get(3)),
                d => ScaleText(d, pct, eu),
                "TagScaleStr"
            );
        }

        [Builtin(
            "TagRDBReload",
            Status = BuiltinStatus.Stub,
            Returns = "1",
            Doc = "1192744",
            Notes = "The simulator's tag database is the project's tables; nothing to reload: returns 1."
        )]
        private static Value TagRDBReload(SimHost h, CicodeTask t, ArgList a) => W.I(1);

        [Builtin(
            "ReRead",
            Status = BuiltinStatus.Stub,
            Doc = "1193657",
            Notes = "Deprecated; does nothing in the runtime either."
        )]
        private static Value ReRead(SimHost h, CicodeTask t, ArgList a) => Value.Nothing;

        [Builtin(
            "TagResolve",
            TagArg = 0,
            Doc = "1192747",
            Errors = "557 name of 255+ characters; 424 tag not found (returns -1)",
            Notes = "A handle of the resolved tag (the World resolves every tag at once; the handle only counts)."
        )]
        private static Value TagResolve(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return NoWorld("TagResolve");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.I(-1);
            var r = Resolve(db, name);
            if (!r.Found)
            {
                W.Raise(t, Codes.TagNotFound);
                return W.I(-1);
            }
            return W.I(h.Resolves.Add(r.Name));
        }

        [Builtin("TagUnresolve", Doc = "1192755", Errors = "269 bad handle (returned)")]
        private static Value TagUnresolve(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.I(a.Get(0));
            if (h.Resolves.Get(hd) == null)
                return W.I(Codes.BadHandle);
            h.Resolves.Free(hd);
            return W.I(0);
        }

        [Builtin(
            "VariableQuality",
            Doc = "1189274",
            Notes = "The argument's quality (a NULL argument: uncertain 0x40)."
        )]
        private static Value VariableQuality(SimHost h, CicodeTask t, ArgList a)
        {
            if (a.Count == 0 || a.Get(0).IsNothing)
                return Value.Int(Types.Quality, 0x40);
            Value v = a.Get(0);
            Value d = v.T == Types.Variant && v.O is Ref ? Value.Deref(v) : v;
            return Value.Int(Types.Quality, unchecked((long)d.Q));
        }

        [Builtin(
            "VariableTimestamp",
            Doc = "1193775",
            Errors = "274 unknown type (returns 0)",
            Notes = "Type 0 the element's timestamp, 1 its quality timestamp, 2 its value timestamp (a constant / local: 0)."
        )]
        private static Value VariableTimestamp(SimHost h, CicodeTask t, ArgList a)
        {
            if (a.Count == 0 || a.Get(0).IsNothing)
                return Value.Int(Types.Timestamp, 0);
            Value v = a.Get(0);
            Value d = v.T == Types.Variant && v.O is Ref ? Value.Deref(v) : v;
            switch (A.I(a.Get(1)))
            {
                case 0:
                    return Value.Int(Types.Timestamp, unchecked((long)d.Ts));
                case 1:
                    return Value.Int(Types.Timestamp, unchecked((long)d.Qts));
                case 2:
                    return Value.Int(Types.Timestamp, unchecked((long)d.Vts));
            }
            W.Raise(t, Codes.InvalidArg);
            return Value.Int(Types.Timestamp, 0);
        }

        // ------------------------------------------------------------------ subscriptions
        private static void Hook(SimHost h)
        {
            if (h.SubscriptionHook != null || h.World == null)
                return;
            h.SubscriptionHook = h.World.Tags.Subscribe(ch => OnChange(h, ch));
        }

        /// <summary>A value change of a subscribed tag: each callback runs as a new task with the
        /// subscription handle (the runtime builds the call text '%d' and queues it).</summary>
        private static void OnChange(SimHost h, TagChange ch)
        {
            if (ch.Element != "field" && ch.Element != "override" && ch.Element.Length > 0)
                return;
            foreach (var (hd, s) in h.Subscriptions.Live())
            {
                if (s.Callbacks.Count == 0 || s.Vm?.Scheduler == null)
                    continue;
                if (!string.Equals(s.Res.Name, ch.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (string cb in s.Callbacks.ToArray())
                {
                    var fn = s.Vm.Library.Function(cb);
                    if (fn == null)
                        continue;
                    try
                    {
                        s.Vm.Scheduler.Add(
                            s.Vm.TaskForCall(fn, new[] { Value.Long(hd) }, false, null, fn.Name)
                        );
                    }
                    catch (HardwareError) { }
                }
            }
        }

        private static int ScaleMode(string s)
        {
            if (string.Equals(s, "Eng", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (string.Equals(s, "Raw", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(s, "Percent", StringComparison.OrdinalIgnoreCase))
                return 2;
            return -1;
        }

        [Builtin(
            "TagSubscribe",
            TagArg = 0,
            Doc = "1192753",
            Effects = "subscribes the tag; callbacks run as new tasks on each change",
            Errors = "557 name of 255+ characters; 274 bad scale mode, poll time or callback; 424 tag not found (raised, -1)",
            Notes = "A callback is a public function name, called with the handle on every value change of the tag (no first call at subscribe: verify)."
        )]
        private static Value TagSubscribe(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return NoWorld("TagSubscribe");
            string name = A.S(a.Get(0));
            if (TooLong(t, name))
                return W.I(-1);
            long poll = a.Count > 1 ? A.I(a.Get(1)) : 250;
            int mode = ScaleMode(a.Count > 2 ? A.S(a.Get(2)) : "Eng");
            string cb = A.S(a.Get(4));
            if (mode < 0 || poll < 1)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var r = Resolve(db, name);
            if (!r.Found)
            {
                W.Raise(t, Codes.TagNotFound);
                return W.I(-1);
            }
            var s = new Subscription
            {
                Res = r,
                Name = name,
                Mode = mode,
                PollMs = (int)poll,
                Lightweight = a.Count <= 5 || A.I(a.Get(5)) != 0,
                Vm = t.Vm,
            };
            int hd = h.Subscriptions.Add(s);
            if (cb.Length > 0)
            {
                var fn = t.Vm.Library.Function(cb);
                if (fn == null || fn.Private)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(hd);
                }
                s.Callbacks.Add(cb);
                Hook(h);
            }
            return W.I(hd);
        }

        [Builtin("TagUnsubscribe", Doc = "1192755", Errors = "269 bad handle (returned, verify)")]
        private static Value TagUnsubscribe(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.I(a.Get(0));
            if (h.Subscriptions.Get(hd) == null)
                return W.I(Codes.BadHandle);
            h.Subscriptions.Free(hd);
            return W.I(0);
        }

        [Builtin(
            "SubscriptionAddCallback",
            Doc = "1192719",
            Errors = "274 bad handle or not a public function (returned)"
        )]
        private static Value SubscriptionAddCallback(SimHost h, CicodeTask t, ArgList a)
        {
            var s = h.Subscriptions.Get(A.I(a.Get(0)));
            string cb = A.S(a.Get(1));
            var fn = cb.Length > 0 ? t.Vm.Library.Function(cb) : null;
            if (s == null || fn == null || fn.Private)
                return W.I(Codes.InvalidArg);
            s.Callbacks.Add(cb);
            s.Vm = t.Vm;
            Hook(h);
            return W.I(0);
        }

        [Builtin(
            "SubscriptionRemoveCallback",
            Doc = "1192726",
            Errors = "274 bad handle or callback (returned)"
        )]
        private static Value SubscriptionRemoveCallback(SimHost h, CicodeTask t, ArgList a)
        {
            var s = h.Subscriptions.Get(A.I(a.Get(0)));
            string cb = A.S(a.Get(1));
            if (s == null || cb.Length == 0)
                return W.I(Codes.InvalidArg);
            int k = s.Callbacks.FindIndex(x => string.Equals(x, cb, StringComparison.OrdinalIgnoreCase));
            if (k < 0)
                return W.I(Codes.InvalidArg);
            s.Callbacks.RemoveAt(k);
            return W.I(0);
        }

        /// <summary>The subscribed value in the subscription's scale mode.</summary>
        private static Value SubValue(SimHost h, Subscription s, long offset, out int err)
        {
            var db = Db(h)!;
            Value v = db.ReadResolved(
                s.Res,
                offset != 0 ? (int?)((s.Res.Index ?? 0) + offset) : s.Res.Index,
                s.Lightweight,
                "cicode",
                out err
            );
            if (err != 0 || s.Mode == 0 || s.Res.Tag == null || v.T == Types.String)
                return v;
            TagDef d = s.Res.Tag;
            double x = Types.IsRealType(v.T) ? v.D : v.I;
            if (s.Mode == 1)
                return Value.Real(d.Scaled ? d.ToRaw(x) : x, v.Q, v.Ts, v.Vts, v.Qts);
            var (ez, ef) = d.EngRange();
            double p = ef != ez ? (x - ez) * 100.0 / (ef - ez) : 0.0;
            return Value.Real(p, v.Q, v.Ts, v.Vts, v.Qts);
        }

        private static Subscription? Sub(SimHost h, CicodeTask t, in Value hv, bool raise)
        {
            var s = h.Subscriptions.Get(A.I(hv));
            if (s == null && raise)
                W.Raise(t, Codes.BadHandle);
            return s;
        }

        [Builtin(
            "SubscriptionGetValue",
            Doc = "1192725",
            Errors = "269 bad handle; the tag read's error (raised, 0 / '' returned)",
            Notes = "The current value in the subscription's scale mode, quality good and no timestamps (the runtime resets them)."
        )]
        private static Value SubscriptionGetValue(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("SubscriptionGetValue");
            var s = Sub(h, t, a.Get(0), true);
            if (s == null)
                return W.Empty;
            Value v = SubValue(h, s, A.I(a.Get(1)), out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return W.Empty;
            }
            return v.WithQ(Value.QualityGood, 0, 0, 0);
        }

        [Builtin(
            "SubscriptionGetTag",
            Doc = "1192723",
            Errors = "269 bad handle; the tag read's error (raised)",
            Notes = "The value with its quality and timestamps."
        )]
        private static Value SubscriptionGetTag(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("SubscriptionGetTag");
            var s = Sub(h, t, a.Get(0), true);
            if (s == null)
                return W.Empty;
            Value v = SubValue(h, s, A.I(a.Get(1)), out int err);
            if (err != 0)
                W.Raise(t, err);
            return v;
        }

        [Builtin("SubscriptionGetQuality", Doc = "1192722", Errors = "269 bad handle (raised, returns 0)")]
        private static Value SubscriptionGetQuality(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("SubscriptionGetQuality");
            var s = Sub(h, t, a.Get(0), true);
            if (s == null)
                return Value.Int(Types.Quality, 0);
            Value v = SubValue(h, s, 0, out _);
            return Value.Int(Types.Quality, unchecked((long)v.Q));
        }

        [Builtin(
            "SubscriptionGetTimestamp",
            Doc = "1192724",
            Errors = "269 bad handle; 274 unknown attribute (raised, returns 0)",
            Notes = "'Timestamp', 'QualityTimestamp' or 'ValueTimestamp' (case-insensitive)."
        )]
        private static Value SubscriptionGetTimestamp(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("SubscriptionGetTimestamp");
            string at = a.Count > 1 ? A.S(a.Get(1)) : "Timestamp";
            var s = Sub(h, t, a.Get(0), false);
            if (s == null)
            {
                W.Raise(t, Codes.BadHandle);
                return Value.Int(Types.Timestamp, 0);
            }
            Value v = SubValue(h, s, 0, out _);
            if (string.Equals(at, "Timestamp", StringComparison.OrdinalIgnoreCase))
                return Value.Int(Types.Timestamp, unchecked((long)v.Ts));
            if (string.Equals(at, "QualityTimestamp", StringComparison.OrdinalIgnoreCase))
                return Value.Int(Types.Timestamp, unchecked((long)v.Qts));
            if (string.Equals(at, "ValueTimestamp", StringComparison.OrdinalIgnoreCase))
                return Value.Int(Types.Timestamp, unchecked((long)v.Vts));
            W.Raise(t, Codes.InvalidArg);
            return Value.Int(Types.Timestamp, 0);
        }

        private static string FullName(Subscription s) =>
            s.Res.Tag != null && s.Res.Tag.Cluster.Length > 0
                ? s.Res.Tag.Cluster + "." + s.Res.Name
                : s.Res.Name;

        [Builtin(
            "SubscriptionGetInfo",
            Doc = "1192721",
            Errors = "269 bad handle; 274 unknown attribute (raised, '' returned)",
            Notes = "TagName, ClusterName, FullName or ElementName."
        )]
        private static Value SubscriptionGetInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var s = Sub(h, t, a.Get(0), true);
            if (s == null)
                return W.Empty;
            string at = A.S(a.Get(1));
            if (string.Equals(at, "TagName", StringComparison.OrdinalIgnoreCase))
                return W.S(s.Res.Name);
            if (string.Equals(at, "ClusterName", StringComparison.OrdinalIgnoreCase))
                return W.S(s.Res.Tag?.Cluster ?? "");
            if (string.Equals(at, "FullName", StringComparison.OrdinalIgnoreCase))
                return W.S(FullName(s));
            if (string.Equals(at, "ElementName", StringComparison.OrdinalIgnoreCase))
                return W.S(s.Res.Element);
            W.Raise(t, Codes.InvalidArg);
            return W.Empty;
        }

        [Builtin(
            "SubscriptionGetAttribute",
            Doc = "1192720",
            Errors = "269 bad handle; 274 unknown attribute (raised, '' returned)",
            Notes = "TagName, ClusterName, FullName, Value (as text), ValueQuality (the quality number), ValueTimestamp (seconds since 1970), ValueTimestampMS (its milliseconds)."
        )]
        private static Value SubscriptionGetAttribute(SimHost h, CicodeTask t, ArgList a)
        {
            if (Db(h) == null)
                return NoWorld("SubscriptionGetAttribute");
            var s = Sub(h, t, a.Get(0), true);
            if (s == null)
                return W.Empty;
            string at = A.S(a.Get(1));
            if (string.Equals(at, "TagName", StringComparison.OrdinalIgnoreCase))
                return W.S(s.Res.Name);
            if (string.Equals(at, "ClusterName", StringComparison.OrdinalIgnoreCase))
                return W.S(s.Res.Tag?.Cluster ?? "");
            if (string.Equals(at, "FullName", StringComparison.OrdinalIgnoreCase))
                return W.S(FullName(s));
            Value v = SubValue(h, s, A.I(a.Get(2)), out int err);
            if (string.Equals(at, "Value", StringComparison.OrdinalIgnoreCase))
            {
                if (err != 0)
                {
                    W.Raise(t, err);
                    return W.Empty;
                }
                return W.S(Text(v));
            }
            if (string.Equals(at, "ValueQuality", StringComparison.OrdinalIgnoreCase))
                return W.S(unchecked((long)v.Q).ToString(CultureInfo.InvariantCulture));
            if (
                string.Equals(at, "ValueTimestamp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(at, "ValueTimestampMS", StringComparison.OrdinalIgnoreCase)
            )
            {
                long ft = unchecked((long)v.Ts);
                long ms = (ft - 116444736000000000L) / 10000;
                if (string.Equals(at, "ValueTimestamp", StringComparison.OrdinalIgnoreCase))
                    return W.S((ft == 0 ? 0 : ms / 1000).ToString(CultureInfo.InvariantCulture));
                return W.S(
                    (ft == 0 ? 0 : ((ms % 1000) + 1000) % 1000).ToString(CultureInfo.InvariantCulture)
                );
            }
            W.Raise(t, Codes.InvalidArg);
            return W.Empty;
        }

        [Builtin(
            "TagGetValue",
            TagArg = 0,
            Doc = "1192740",
            Errors = "557 name of 255+ characters; 274 brackets in the name, bad scale mode, poll time or item; 424 tag not found (raised, 0 returned)",
            Notes = "Non-blocking: the World's value at once (a managed subscription has it). Item 0 the value with its quality and timestamps, 1 the value (quality good, no timestamps), 2 value timestamp, 3 quality, 4 quality timestamp, 5 timestamp."
        )]
        private static Value TagGetValue(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return NoWorld("TagGetValue");
            string name = A.S(a.Get(0));
            long item = A.I(a.Get(1));
            // a failed call returns the item's type with 0 (also for the
            // quality / timestamp items): a QUALITY argument of QualityIsGood then converts
            Value Fail() =>
                item == 3 ? Value.Int(Types.Quality, 0)
                : item == 2 || item == 4 || item == 5 ? Value.Int(Types.Timestamp, 0)
                : W.I(0);
            if (TooLong(t, name))
                return Fail();
            long index = A.I(a.Get(2));
            long poll = a.Count > 3 ? A.I(a.Get(3)) : 250;
            long mode = A.I(a.Get(4));
            if (
                name.IndexOfAny(new[] { '[', ']' }) >= 0
                || (mode & 0xFFFF) >= 3
                || poll < 1
                || item < 0
                || item > 5
            )
            {
                W.Raise(t, Codes.InvalidArg);
                return Fail();
            }
            var r = Resolve(db, name);
            if (!r.Found)
            {
                W.Raise(t, Codes.TagNotFound);
                return Fail();
            }
            var s = new Subscription
            {
                Res = r,
                Name = name,
                Mode = (int)mode,
                Lightweight = false,
            };
            Value v = SubValue(h, s, index, out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return item == 0 || item == 1 ? v : Fail();
            }
            switch (item)
            {
                case 0:
                    return v;
                case 1:
                    return v.WithQ(Value.QualityGood, 0, 0, 0);
                case 2:
                    return Value.Int(Types.Timestamp, unchecked((long)v.Vts));
                case 3:
                    return Value.Int(Types.Quality, unchecked((long)v.Q));
                case 4:
                    return Value.Int(Types.Timestamp, unchecked((long)v.Qts));
                default:
                    return Value.Int(Types.Timestamp, unchecked((long)v.Ts));
            }
        }
    }
}
