using System;
using System.Collections.Generic;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>Named key / value maps (MapOpen .. MapKeyCount).</summary>
    public static class Maps
    {
        public const int RecordNotFound = 536;

        internal sealed class CicodeMap
        {
            public readonly string Name;
            public readonly SortedList<string, Value> Keys = new SortedList<string, Value>(
                CByteOrder.Instance
            );

            /// <summary>The close callback (a public function), null for none.</summary>
            public Function? OnClose;

            public CicodeMap(string name)
            {
                Name = name;
            }
        }

        internal sealed class Store
        {
            public readonly Dictionary<string, CicodeMap> ByName = new Dictionary<string, CicodeMap>(
                StringComparer.Ordinal
            );
        }

        private static Store Of(SimHost h) => ProcessData.Of(h).MapStore;

        /// <summary>'.Map' + 16 upper-case hex digits of 8 random bytes, again while taken
        /// (the runtime takes the bytes at random, the simulator from its seeded
        /// source).</summary>
        internal static string NewName(SimHost h)
        {
            var d = ProcessData.Of(h);
            var s = Of(h);
            const string hex = "0123456789ABCDEF";
            while (true)
            {
                var sb = new StringBuilder(".Map", 20);
                for (int i = 0; i < 2; i++)
                {
                    uint r = d.Random.Next();
                    for (int k = 0; k < 4; k++)
                    {
                        int b = (int)((r >> (8 * k)) & 0xFF);
                        sb.Append(hex[b >> 4]).Append(hex[b & 15]);
                    }
                }
                string n = sb.ToString();
                if (!s.ByName.ContainsKey(n))
                    return n;
            }
        }

        /// <summary>MapOpen("", 0, ""): a new map with a random name (ArrayCreate's hidden map).</summary>
        internal static string OpenNew(SimHost h)
        {
            string n = NewName(h);
            Of(h).ByName[n] = new CicodeMap(n);
            return n;
        }

        internal static CicodeMap? Get(SimHost h, string name) =>
            Of(h).ByName.TryGetValue(name, out var m) ? m : null;

        /// <summary>Close a map: run its close callback first (the map still there), then drop it.
        /// Without a task (a page closing frees an alarm list's array) the callback does not run.</summary>
        internal static void CloseMap(SimHost h, CicodeTask? t, CicodeMap m)
        {
            if (m.OnClose != null && t != null)
                W.RunNested(h, t, m.OnClose, new[] { Value.Str(m.Name) }, 1000);
            Of(h).ByName.Remove(m.Name);
        }

        [Builtin(
            "MapOpen",
            Doc = "1106306",
            Errors = "274 a callback that is no public function, a mode above 2, an empty name with mode 1 (raised, '' returned); 271 mode 0 and the map exists (raised); 536 mode 1 and no such map (raised)",
            Notes = "An empty name with mode 0 or 2 makes a new map named '.Map' + 16 upper-case hex digits (the random bytes from the process's seeded source, ProcessData.Random). Mode 2 opens or creates. The callback (a public function taking the map name) runs when the map closes."
        )]
        private static Value MapOpen(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            long mode = a.Count > 1 ? A.UShort(A.I(a.Get(1))) : 0;
            string cb = a.Count > 2 ? A.S(a.Get(2)) : "";
            Function? fn = null;
            if (cb.Length > 0)
            {
                fn = CallText.Find(t, cb);
                if (fn == null || fn.Private)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
            }
            if (mode > 2 || (name.Length == 0 && mode == 1))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            var s = Of(h);
            if (name.Length == 0)
                name = NewName(h);
            name = C.Cut(name);
            if (s.ByName.TryGetValue(name, out var m))
            {
                if (mode == 0)
                {
                    W.Raise(t, Codes.NoHandles);
                    return W.Empty;
                }
                return W.S(name);
            }
            if (mode == 1)
            {
                W.Raise(t, RecordNotFound);
                return W.Empty;
            }
            s.ByName[name] = new CicodeMap(name) { OnClose = fn };
            return W.S(name);
        }

        [Builtin(
            "MapClose",
            Doc = "1106299",
            Effects = "runs the map's close callback, then deletes the map",
            Errors = "274 an empty name (raised and returned); 536 no such map (returned)",
            Notes = "The callback runs nested in the caller to its end (the map still open); one that waits is left waiting and the map is deleted (verify: the runtime may delete it later)."
        )]
        private static Value MapClose(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            if (name.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var m = Get(h, name);
            if (m == null)
                return W.I(RecordNotFound);
            CloseMap(h, t, m);
            return W.I(0);
        }

        [Builtin("MapExists", Doc = "1106300", Errors = "274 an empty name (raised, 0 returned)")]
        private static Value MapExists(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            if (name.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            return W.I(Get(h, name) != null ? 1 : 0);
        }

        [Builtin(
            "MapClear",
            Doc = "1106298",
            Effects = "deletes every key of the map",
            Errors = "274 an empty name (raised and returned); 536 no such map (returned)"
        )]
        private static Value MapClear(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            if (name.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var m = Get(h, name);
            if (m == null)
                return W.I(RecordNotFound);
            m.Keys.Clear();
            return W.I(0);
        }

        [Builtin(
            "MapValueSet",
            Doc = "1106308",
            Effects = "stores the value (its quality and timestamps) under the key",
            Errors = "274 an empty map name or key, no value, a mode above 2 (raised and returned); 536 no such map, or mode 1 and no such key (returned); 271 mode 0 and the key exists (returned)",
            Notes = "A VARIANT value is dereferenced first; mode 2 (default) sets the key whether it exists or not."
        )]
        private static Value MapValueSet(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)),
                key = A.S(a.Get(1));
            long mode = a.Count > 3 ? A.UShort(A.I(a.Get(3))) : 2;
            Value v = a.Get(2);
            if (v.T == Types.Variant)
                v = Value.Deref(v);
            bool none = v.IsNothing || (v.T == Types.Variant && v.O == null) || v.T == Types.Void;
            if (name.Length == 0 || key.Length == 0 || none || mode > 2)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var m = Get(h, name);
            if (m == null)
                return W.I(RecordNotFound);
            bool exists = m.Keys.ContainsKey(key);
            if (mode == 0 && exists)
                return W.I(Codes.NoHandles);
            if (mode == 1 && !exists)
                return W.I(RecordNotFound);
            if (v.T == Types.String && v.S == null)
                v = Value.Str("", v.Q, v.Ts, v.Vts, v.Qts);
            m.Keys[key] = v;
            return W.I(0);
        }

        [Builtin(
            "MapValueGet",
            Doc = "1106307",
            Errors = "274 an empty map name or key (raised)",
            Notes = "A reference to a copy of the stored value (its quality and timestamps travel with it). A missing map or key gives a reference to nothing without a raise (no trap in UsrMapValueGet; doc 1106307 says 274 is raised: verify), which converts to '' / 0 without an error (the runtime converts a NULL reference to '')."
        )]
        private static Value MapValueGet(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)),
                key = A.S(a.Get(1));
            if (name.Length == 0 || key.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return Value.Nothing;
            }
            var m = Get(h, name);
            if (m == null || !m.Keys.TryGetValue(key, out Value v))
                return Value.Nothing;
            return Value.Variant(new ValueRef(v), v.Q, v.Ts, v.Vts, v.Qts);
        }

        [Builtin(
            "MapValueSetQuality",
            Doc = "1106309",
            Effects = "sets the stored value's quality",
            Errors = "274 an empty map name or key (raised and returned); 536 no such map or key (returned)"
        )]
        private static Value MapValueSetQuality(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)),
                key = A.S(a.Get(1));
            if (name.Length == 0 || key.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var m = Get(h, name);
            if (m == null || !m.Keys.TryGetValue(key, out Value v))
                return W.I(RecordNotFound);
            v.Q = A.U64(a.Get(2));
            m.Keys[key] = v;
            return W.I(0);
        }

        [Builtin(
            "MapKeyExists",
            Doc = "1106303",
            Errors = "274 an empty map name or key (raised, 0 returned)",
            Notes = "1 / 0; a missing map 0."
        )]
        private static Value MapKeyExists(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)),
                key = A.S(a.Get(1));
            if (name.Length == 0 || key.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            var m = Get(h, name);
            return W.I(m != null && m.Keys.ContainsKey(key) ? 1 : 0);
        }

        [Builtin(
            "MapKeyDelete",
            Doc = "1106302",
            Effects = "deletes the key",
            Errors = "274 an empty map name or key (raised and returned); 536 no such map or key (returned)"
        )]
        private static Value MapKeyDelete(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)),
                key = A.S(a.Get(1));
            if (name.Length == 0 || key.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var m = Get(h, name);
            if (m == null || !m.Keys.Remove(key))
                return W.I(RecordNotFound);
            return W.I(0);
        }

        [Builtin(
            "MapKeyFirst",
            Doc = "1106304",
            Errors = "274 an empty name (raised, '' returned)",
            Notes = "The first key in byte order (case-sensitive: 'SomeKey' before 'somekey'); '' for no keys or no such map."
        )]
        private static Value MapKeyFirst(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            if (name.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            var m = Get(h, name);
            return W.S(m != null && m.Keys.Count > 0 ? C.Cut(m.Keys.Keys[0]) : "");
        }

        [Builtin(
            "MapKeyNext",
            Doc = "1106305",
            Errors = "274 an empty map name (raised, '' returned)",
            Notes = "The key after sKeyName; '' after the last key, for a key that is not in the map, or no such map (the next key only when sKeyName is found)."
        )]
        private static Value MapKeyNext(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)),
                key = A.S(a.Get(1));
            if (name.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            var m = Get(h, name);
            if (m == null)
                return W.Empty;
            int i = m.Keys.IndexOfKey(key);
            return W.S(i >= 0 && i + 1 < m.Keys.Count ? C.Cut(m.Keys.Keys[i + 1]) : "");
        }

        [Builtin(
            "MapKeyCount",
            Doc = "1106301",
            Errors = "274 an empty name (raised, -1 returned)",
            Notes = "The number of keys; -1 for no such map (0xffffffff)."
        )]
        private static Value MapKeyCount(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            if (name.Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var m = Get(h, name);
            return W.I(m != null ? m.Keys.Count : -1);
        }
    }
}
