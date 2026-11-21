using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>String arrays of the process (ArrayCreate .. ArraySwap).</summary>
    public static class Arrays
    {
        public const int MaxDim = 32765;
        public const int MaxItems = 0x200000;
        public const int MaxArrays = 0xa20;

        /// <summary>'ArrayCreate: Out of memory' and 'too many items'.</summary>
        public const int OutOfMemory = 272;

        internal sealed class CicodeArray
        {
            public string Name = "";
            public int X,
                Y,
                Z;
            private bool _dirty = true;

            /// <summary>Counts the changes of the items (each dirty mark): a viewer's array
            /// output knows when its cells changed (DspArrayByAn).</summary>
            public long Serial;
            public bool Dirty
            {
                get => _dirty;
                set
                {
                    _dirty = value;
                    if (value)
                        Serial++;
                }
            }
            public string MapName = "";

            /// <summary>Set items only (index -> text): an array of two million NULL items costs nothing.</summary>
            public Dictionary<int, string> Items = new Dictionary<int, string>();
            public int Count => X * Y * Z;

            /// <summary>The AN and window the array is bound to; 0 when not bound.</summary>
            public int An,
                Window;
        }

        private static HandleTable<CicodeArray> Table(SimHost h) => ProcessData.Of(h).ArrayTable;

        private static CicodeArray? Get(SimHost h, in Value v) => Table(h).Get(A.Short(A.I(v)));

        /// <summary>The item index of (x, y, z), -1 when outside (y and z of -1 are 0;
        /// ((y * X + x) * Z + z)).</summary>
        private static int Index(CicodeArray arr, ArgList a, int first)
        {
            long x = A.Short(A.I(a.Get(first)));
            long y = a.Count > first + 1 ? A.Short(A.I(a.Get(first + 1))) : -1;
            long z = a.Count > first + 2 ? A.Short(A.I(a.Get(first + 2))) : -1;
            if (y == -1)
                y = 0;
            if (z == -1)
                z = 0;
            if (x < 0 || x >= arr.X || y < 0 || y >= arr.Y || z < 0 || z >= arr.Z)
                return -1;
            return (int)((y * arr.X + x) * arr.Z + z);
        }

        /// <summary>ArrayCreate: the handle, -1 on an error.</summary>
        internal static int Create(SimHost h, CicodeTask t, string name, long x, long y, long z)
        {
            if (y == -1 || y == 0)
                y = 1;
            if (z == -1 || z == 0)
                z = 1;
            // (ushort)(d - 1) < 0x7ffd for each dimension (257), then the int product x * y * z
            // (it wraps in C) at most 0x200000 ('too many items': 272); a
            // wrapped, negative product fails (272)
            if ((ushort)(x - 1) >= 0x7ffd || (ushort)(y - 1) >= 0x7ffd || (ushort)(z - 1) >= 0x7ffd)
            {
                W.Raise(t, Codes.OutOfRange);
                return -1;
            }
            int prod = unchecked((int)(x * y) * (int)z);
            if (prod > MaxItems)
            {
                W.Raise(t, OutOfMemory);
                return -1;
            }
            if (prod <= 0)
            {
                W.Raise(t, OutOfMemory);
                return -1;
            }
            var tab = Table(h);
            if (tab.Count >= MaxArrays)
            {
                W.Raise(t, Codes.NoHandles);
                return -1;
            }
            string n = A.CutAtNul(name).Trim(' ', '\t');
            if (n.Length > 253)
                n = n.Substring(0, 253);
            var arr = new CicodeArray
            {
                X = (int)x,
                Y = (int)y,
                Z = (int)z,
                MapName = Maps.OpenNew(h),
            };
            int hd = tab.Add(arr);
            arr.Name = n.Length > 0 ? n : "Array" + C.Inv(hd);
            return hd;
        }

        [Builtin(
            "ArrayCreate",
            Doc = "1069787",
            Errors = "257 a dimension outside 1..32765 (raised, -1); 272 more than 2,097,152 items or an item count that wraps negative (raised, -1); 271 too many arrays in use (raised, -1)",
            Notes = "y and z of -1 or 0 are 1. The name is trimmed; an empty one gets 'Array' + the handle (the format string 'Array%d'). Also opens a hidden map (ArrayGetMapName). Items start NULL (read as '')."
        )]
        private static Value ArrayCreate(SimHost h, CicodeTask t, ArgList a)
        {
            long x = A.Short(A.I(a.Get(1)));
            long y = a.Count > 2 ? A.Short(A.I(a.Get(2))) : -1;
            long z = a.Count > 3 ? A.Short(A.I(a.Get(3))) : -1;
            return W.I(Create(h, t, A.S(a.Get(0)), x, y, z));
        }

        [Builtin(
            "ArrayDestroy",
            Doc = "1069789",
            Effects = "frees the array and closes its hidden map",
            Notes = "Returns 0, also for a bad handle (always 0, no raise)."
        )]
        private static Value ArrayDestroy(SimHost h, CicodeTask t, ArgList a)
        {
            Destroy(h, t, A.Short(A.I(a.Get(0))));
            return W.I(0);
        }

        /// <summary>ArrayDestroy's work: free the handle and close the array's hidden map (its
        /// close callback runs on <paramref name="t"/>, not without one). Also for the arrays the
        /// engine makes (an AlarmListCreate list's, freed with its list or page).</summary>
        internal static void Destroy(SimHost h, CicodeTask? t, long hd)
        {
            var arr = Table(h).Get(hd);
            if (arr == null)
                return;
            Table(h).Free(hd);
            var m = Maps.Get(h, arr.MapName);
            if (m != null)
                Maps.CloseMap(h, t, m);
        }

        [Builtin("ArrayExists", Doc = "1069791", Notes = "1 for a handle in use, else 0; no error.")]
        private static Value ArrayExists(SimHost h, CicodeTask t, ArgList a) =>
            W.I(Get(h, a.Get(0)) != null ? 1 : 0);

        [Builtin(
            "ArraySetString",
            Doc = "1069806",
            Effects = "replaces the item; marks the array dirty when the text changes",
            Errors = "269 a bad handle (raised, 0); 257 an index out of range (raised, -1)",
            Notes = "y, z of -1 are 0. The same text again changes nothing (no dirty mark); an empty text makes the item NULL; at most 255 characters are kept."
        )]
        private static Value ArraySetString(SimHost h, CicodeTask t, ArgList a)
        {
            var arr = Get(h, a.Get(0));
            return W.I(Set(t, arr, A.S(a.Get(1)), a));
        }

        private static long Set(CicodeTask t, CicodeArray? arr, string value, ArgList a)
        {
            if (arr == null)
            {
                W.Raise(t, Codes.BadHandle);
                return 0;
            }
            int i = Index(arr, a, 2);
            if (i < 0)
            {
                W.Raise(t, Codes.OutOfRange);
                return -1;
            }
            if (arr.Items.TryGetValue(i, out string? old))
            {
                if (old == value)
                    return 0;
                arr.Items.Remove(i);
                arr.Dirty = true;
            }
            if (value.Length == 0)
                return 0;
            arr.Items[i] = C.Cut(value);
            arr.Dirty = true;
            return 0;
        }

        [Builtin(
            "ArraySetInt",
            Doc = "1069803",
            Effects = "as ArraySetString of the number's text",
            Errors = "269 a bad handle (raised, 0); 257 an index out of range (raised, -1)"
        )]
        private static Value ArraySetInt(SimHost h, CicodeTask t, ArgList a)
        {
            var arr = Get(h, a.Get(0));
            return W.I(Set(t, arr, C.Inv(A.I(a.Get(1))), a));
        }

        private static string GetItem(CicodeTask t, CicodeArray? arr, ArgList a)
        {
            if (arr == null)
            {
                W.Raise(t, Codes.BadHandle);
                return "";
            }
            int i = Index(arr, a, 1);
            if (i < 0)
            {
                W.Raise(t, Codes.OutOfRange);
                return "";
            }
            return arr.Items.TryGetValue(i, out string? s) ? s : "";
        }

        [Builtin(
            "ArrayGetString",
            Doc = "1069800",
            Errors = "269 a bad handle (raised, ''); 257 an index out of range (raised, '')",
            Notes = "y, z of -1 are 0; a NULL item ''."
        )]
        private static Value ArrayGetString(SimHost h, CicodeTask t, ArgList a) =>
            W.S(GetItem(t, Get(h, a.Get(0)), a));

        [Builtin(
            "ArrayGetInt",
            Doc = "1069796",
            Errors = "269 a bad handle (raised, 0); 257 an index out of range (raised, 0)"
        )]
        private static Value ArrayGetInt(SimHost h, CicodeTask t, ArgList a) =>
            W.I(C.Atol(GetItem(t, Get(h, a.Get(0)), a)));

        [Builtin(
            "ArrayGetInfo",
            Doc = "1069795",
            Errors = "269 a bad handle (raised, -1)",
            Notes = "Type 0 the x size, 1 y, 2 z, another -1 (no raise)."
        )]
        private static Value ArrayGetInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var arr = Get(h, a.Get(0));
            if (arr == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(-1);
            }
            switch (A.Short(A.I(a.Get(1))))
            {
                case 0:
                    return W.I(arr.X);
                case 1:
                    return W.I(arr.Y);
                case 2:
                    return W.I(arr.Z);
            }
            return W.I(-1);
        }

        [Builtin(
            "ArrayIsDirty",
            Doc = "1069802",
            Notes = "1 when an item changed since ArraySetIsDirty(h, 0) (a new array is dirty), 0 otherwise and for a bad handle (no error)."
        )]
        private static Value ArrayIsDirty(SimHost h, CicodeTask t, ArgList a)
        {
            var arr = Get(h, a.Get(0));
            return W.I(arr != null && arr.Dirty ? 1 : 0);
        }

        [Builtin(
            "ArraySetIsDirty",
            Doc = "1069805",
            Effects = "sets or clears the dirty mark",
            Errors = "269 a bad handle (raised)",
            Notes = "Returns 0."
        )]
        private static Value ArraySetIsDirty(SimHost h, CicodeTask t, ArgList a)
        {
            var arr = Get(h, a.Get(0));
            if (arr == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(0);
            }
            arr.Dirty = A.Short(A.I(a.Get(1))) != 0;
            return W.I(0);
        }

        [Builtin("ArrayGetMapName", Doc = "1069798", Errors = "269 a bad handle (raised, '')")]
        private static Value ArrayGetMapName(SimHost h, CicodeTask t, ArgList a)
        {
            var arr = Get(h, a.Get(0));
            if (arr == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.Empty;
            }
            return W.S(arr.MapName);
        }

        [Builtin(
            "ArrayCopy",
            Doc = "1069786",
            Effects = "a new array with the same name, sizes and items (and its own hidden map)",
            Errors = "269 a bad handle (raised, -1); the errors of ArrayCreate",
            Notes = "The copy's handle."
        )]
        private static Value ArrayCopy(SimHost h, CicodeTask t, ArgList a)
        {
            var arr = Get(h, a.Get(0));
            if (arr == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(-1);
            }
            int hd = Create(h, t, arr.Name, arr.X, arr.Y, arr.Z);
            var copy = Table(h).Get(hd);
            if (copy != null)
            {
                foreach (var kv in arr.Items)
                    copy.Items[kv.Key] = kv.Value;
            }
            return W.I(hd);
        }

        [Builtin(
            "ArraySwap",
            Doc = "1069808",
            Effects = "swaps the two arrays' whole records (names, sizes, items, maps, dirty marks)",
            Errors = "269 a bad handle (raised)",
            Notes = "Returns 0."
        )]
        private static Value ArraySwap(SimHost h, CicodeTask t, ArgList a)
        {
            long h1 = A.Short(A.I(a.Get(0))),
                h2 = A.Short(A.I(a.Get(1)));
            var tab = Table(h);
            var x = tab.Get(h1);
            var y = tab.Get(h2);
            if (x == null || y == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(0);
            }
            var tmp = new CicodeArray
            {
                Name = x.Name,
                X = x.X,
                Y = x.Y,
                Z = x.Z,
                Dirty = x.Dirty,
                MapName = x.MapName,
                Items = x.Items,
            };
            x.Name = y.Name;
            x.X = y.X;
            x.Y = y.Y;
            x.Z = y.Z;
            x.Dirty = y.Dirty;
            x.MapName = y.MapName;
            x.Items = y.Items;
            y.Name = tmp.Name;
            y.X = tmp.X;
            y.Y = tmp.Y;
            y.Z = tmp.Z;
            y.Dirty = tmp.Dirty;
            y.MapName = tmp.MapName;
            y.Items = tmp.Items;
            x.Serial++; // the items changed for a viewer either way
            y.Serial++;
            return W.I(0);
        }
    }
}
