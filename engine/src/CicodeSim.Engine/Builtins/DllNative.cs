using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using CicodeSim.Values;

namespace CicodeSim.Builtins
{
    /// <summary>A value that crosses to a library: nothing, an integer, a real, a string or
    /// the handle of an object the library side keeps.</summary>
    public struct DllValue
    {
        public const int Null = 0,
            Int = 1,
            Real = 2,
            Str = 3,
            Obj = 4;

        public int Kind;
        public long I;
        public double D;
        public string? S;

        public static DllValue OfInt(long v) => new DllValue { Kind = Int, I = v };

        public static DllValue OfReal(double v) => new DllValue { Kind = Real, D = v };

        public static DllValue OfStr(string? v) => new DllValue { Kind = Str, S = v ?? "" };

        public static DllValue OfObj(int handle) => new DllValue { Kind = Obj, I = handle };

        public JsonValue ToJson()
        {
            var a = JsonValue.NewArray();
            switch (Kind)
            {
                case Int:
                    a.Add(JsonValue.Of("i"));
                    a.Add(JsonValue.Of(I));
                    break;
                case Real:
                    a.Add(JsonValue.Of("r"));
                    a.Add(JsonValue.Of(D.ToString("R", CultureInfo.InvariantCulture)));
                    break;
                case Str:
                    a.Add(JsonValue.Of("s"));
                    a.Add(JsonValue.Of(S ?? ""));
                    break;
                case Obj:
                    a.Add(JsonValue.Of("o"));
                    a.Add(JsonValue.Of(I));
                    break;
                default:
                    a.Add(JsonValue.Of("n"));
                    break;
            }
            return a;
        }

        public static DllValue FromJson(JsonValue? v)
        {
            if (v == null || v.Kind != JsonKind.Array || v.Count == 0)
                return default;
            string k = v.Items[0].Str;
            JsonValue? x = v.Count > 1 ? v.Items[1] : null;
            switch (k)
            {
                case "i":
                    return OfInt(x != null && x.IsNumber ? (long)x.ToNumber() : 0);
                case "r":
                    return OfReal(
                        x != null
                        && double.TryParse(
                            x.Str,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double d
                        )
                            ? d
                            : 0.0
                    );
                case "s":
                    return OfStr(x?.Str);
                case "o":
                    return OfObj(x != null && x.IsNumber ? (int)x.ToNumber() : -1);
                default:
                    return default;
            }
        }
    }

    /// <summary>What loads libraries and calls into them for a simulation: in this process
    /// (<see cref="DllLocalRunner"/>) or in a helper process (<see cref="DllChildRunner"/>:
    /// a library that crashes or hangs takes the helper with it, not the simulator).</summary>
    public interface IDllRunner : IDisposable
    {
        /// <summary>Load a library and find a function: its id (0 or more), or -1 and why.</summary>
        int Load(string path, string name, out string error);
        void Free(int id);

        /// <summary>Call a function: <paramref name="types"/> = the return type letter, then one
        /// per argument (DLLOpen: A H I short, J long, B double, C string, D counted string).</summary>
        bool Call(int id, string types, IReadOnlyList<DllValue> args, out DllValue result, out string error);

        int ClassCreate(string path, string className, IReadOnlyList<DllValue> args, out string error);
        bool ClassIsValid(int id);
        bool ClassDispose(int id, out string error);
        bool ClassGet(int id, string property, out DllValue value, out string error);
        bool ClassSet(int id, string property, DllValue value, out string error);
        bool ClassCall(
            int id,
            string method,
            IReadOnlyList<DllValue> args,
            out DllValue value,
            out string error
        );
    }

    /// <summary>Runs libraries in this process.</summary>
    public sealed class DllLocalRunner : IDllRunner
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

        [DllImport("msvcrt", CallingConvention = CallingConvention.Cdecl)]
        private static extern void _fpreset();

        private sealed class Fn
        {
            public IntPtr Module,
                Address;
        }

        private readonly Dictionary<int, Fn> _fns = new Dictionary<int, Fn>();
        private readonly Dictionary<int, object> _objects = new Dictionary<int, object>();
        private readonly Dictionary<int, List<int>> _children = new Dictionary<int, List<int>>();
        private int _nextFn,
            _nextObject = 1;

        // ------------------------------------------------------------------ native functions
        public int Load(string path, string name, out string error)
        {
            error = "";
            IntPtr m;
            try
            {
                m = LoadLibraryW(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return -1;
            }
            if (m == IntPtr.Zero)
            {
                int e = Marshal.GetLastWin32Error();
                error =
                    "the library cannot be loaded (Windows error "
                    + e.ToString(CultureInfo.InvariantCulture)
                    + (
                        e == 193 ? ": it is not a " + (IntPtr.Size == 4 ? "32" : "64") + "-bit library"
                        : e == 126 ? ": the library or one it needs is not found"
                        : ""
                    )
                    + ")";
                return -1;
            }
            IntPtr f = GetProcAddress(m, name);
            if (f == IntPtr.Zero)
            {
                FreeLibrary(m);
                error = "the library has no function " + name;
                return -1;
            }
            int id = _nextFn++;
            _fns[id] = new Fn { Module = m, Address = f };
            return id;
        }

        public void Free(int id)
        {
            if (!_fns.TryGetValue(id, out var f))
                return;
            _fns.Remove(id);
            FreeLibrary(f.Module);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ThunkInt(IntPtr fn, IntPtr args, int size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate double ThunkReal(IntPtr fn, IntPtr args, int size);

        private static ThunkInt? _thunkInt;
        private static ThunkReal? _thunkReal;
        private static readonly object ThunkLock = new object();

        // int __cdecl thunk(void* fn, void* args, int size): copy the arguments, call, restore
        private static readonly byte[] ThunkCode =
        {
            0x55, // push ebp
            0x8B,
            0xEC, // mov ebp, esp
            0x56, // push esi
            0x57, // push edi
            0x8B,
            0x4D,
            0x10, // mov ecx, [ebp+16]
            0x2B,
            0xE1, // sub esp, ecx
            0x8B,
            0x75,
            0x0C, // mov esi, [ebp+12]
            0x8B,
            0xFC, // mov edi, esp
            0xFC, // cld
            0xF3,
            0xA4, // rep movsb
            0xFF,
            0x55,
            0x08, // call [ebp+8]
            0x8D,
            0x65,
            0xF8, // lea esp, [ebp-8]
            0x5F, // pop edi
            0x5E, // pop esi
            0x5D, // pop ebp
            0xC3, // ret
        };

        private static void MakeThunk()
        {
            lock (ThunkLock)
            {
                if (_thunkInt != null)
                    return;
                IntPtr mem = VirtualAlloc(
                    IntPtr.Zero,
                    (UIntPtr)4096,
                    0x3000 /* commit | reserve */
                    ,
                    0x40 /* execute read write */
                );
                if (mem == IntPtr.Zero)
                    throw new InvalidOperationException("no memory for the call thunk");
                Marshal.Copy(ThunkCode, 0, mem, ThunkCode.Length);
                _thunkReal = (ThunkReal)Marshal.GetDelegateForFunctionPointer(mem, typeof(ThunkReal));
                _thunkInt = (ThunkInt)Marshal.GetDelegateForFunctionPointer(mem, typeof(ThunkInt));
            }
        }

        private delegate object Invoker(IntPtr fn, object[] args);
        private static readonly Dictionary<string, Invoker> Invokers = new Dictionary<string, Invoker>();

        // 64-bit: one calling convention; a dynamic method with a calli of the signature
        private static Invoker Invoker64(string types)
        {
            lock (Invokers)
            {
                if (Invokers.TryGetValue(types, out var known))
                    return known;
                bool real = types.Length > 0 && types[0] == 'B';
                var pars = new Type[Math.Max(0, types.Length - 1)];
                for (int i = 1; i < types.Length; i++)
                    pars[i - 1] = types[i] == 'B' ? typeof(double) : typeof(IntPtr);
                Type ret = real ? typeof(double) : typeof(IntPtr);
                var dm = new DynamicMethod(
                    "dllcall_" + types,
                    typeof(object),
                    new[] { typeof(IntPtr), typeof(object[]) },
                    typeof(DllLocalRunner).Module,
                    true
                );
                var il = dm.GetILGenerator();
                for (int i = 0; i < pars.Length; i++)
                {
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(OpCodes.Unbox_Any, pars[i]);
                }
                il.Emit(OpCodes.Ldarg_0);
                il.EmitCalli(OpCodes.Calli, CallingConvention.StdCall, ret, pars);
                il.Emit(OpCodes.Box, ret);
                il.Emit(OpCodes.Ret);
                var inv = (Invoker)dm.CreateDelegate(typeof(Invoker));
                Invokers[types] = inv;
                return inv;
            }
        }

        private static Encoding? _ansi;

        /// <summary>The system ANSI code page, what a 'C' / 'D' string is to a library (on .NET
        /// Core GetEncoding(0) is UTF-8, not the ANSI page, so it is looked up by number).</summary>
        private static Encoding Ansi()
        {
            if (_ansi != null)
                return _ansi;
#if !NETFRAMEWORK
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            }
            catch (Exception)
            {
                _ansi = Encoding.Latin1;
            } // swallow-ok: no code pages in this build
#else
            _ansi = Encoding.Default;
#endif
            return _ansi;
        }

        public bool Call(
            int id,
            string types,
            IReadOnlyList<DllValue> args,
            out DllValue result,
            out string error
        )
        {
            result = default;
            error = "";
            if (!_fns.TryGetValue(id, out var f))
            {
                error = "no such function";
                return false;
            }
            var buffers = new List<IntPtr>();
            try
            {
                return CallGuarded(f.Address, types, args, buffers, out result, out error);
            }
            finally
            {
                foreach (IntPtr b in buffers)
                    Marshal.FreeHGlobal(b);
            }
        }

        private static IntPtr StringArg(char type, string s, List<IntPtr> buffers)
        {
            byte[] bytes = Ansi().GetBytes(s ?? "");
            int head = type == 'D' ? 1 : 0;
            // (at least 256 bytes: a library that fills a string argument, the GetWindowsDirectoryA
            // idiom, then corrupts nothing; the runtime copies strlen + 1 and would)
            IntPtr p = Marshal.AllocHGlobal(Math.Max(head + bytes.Length + 1, 256));
            buffers.Add(p);
            if (head == 1)
                Marshal.WriteByte(p, 0, (byte)(bytes.Length & 0xFF));
            Marshal.Copy(bytes, 0, p + head, bytes.Length);
            Marshal.WriteByte(p, head + bytes.Length, 0);
            return p;
        }

        private static string StringResult(char type, IntPtr p)
        {
            if (p == IntPtr.Zero)
                return "";
            if (type == 'D')
                p += 1;
            int n = 0;
            while (n < 255 && Marshal.ReadByte(p, n) != 0)
                n++;
            var bytes = new byte[n];
            Marshal.Copy(p, bytes, 0, n);
            return Ansi().GetString(bytes);
        }

#if NETFRAMEWORK
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
#endif
        private static bool CallGuarded(
            IntPtr fn,
            string types,
            IReadOnlyList<DllValue> args,
            List<IntPtr> buffers,
            out DllValue result,
            out string error
        )
        {
            result = default;
            error = "";
            char ret = types.Length > 0 ? types[0] : 'J';
            try
            {
                long eax = 0;
                double st0 = 0;
                if (IntPtr.Size == 4)
                {
                    MakeThunk();
                    int size = 0;
                    for (int i = 1; i < types.Length; i++)
                        size += types[i] == 'B' ? 8 : 4;
                    IntPtr block = Marshal.AllocHGlobal(Math.Max(size, 4));
                    buffers.Add(block);
                    int at = 0;
                    for (int i = 1; i < types.Length; i++)
                    {
                        DllValue v = i - 1 < args.Count ? args[i - 1] : default;
                        switch (types[i])
                        {
                            case 'B':
                                Marshal.WriteInt64(block, at, BitConverter.DoubleToInt64Bits(v.D));
                                at += 8;
                                break;
                            case 'C':
                            case 'D':
                                Marshal.WriteInt32(
                                    block,
                                    at,
                                    StringArg(types[i], v.S ?? "", buffers).ToInt32()
                                );
                                at += 4;
                                break;
                            case 'J':
                                Marshal.WriteInt32(block, at, unchecked((int)v.I));
                                at += 4;
                                break;
                            default:
                                Marshal.WriteInt32(block, at, unchecked((short)v.I));
                                at += 4;
                                break;
                        }
                    }
                    if (ret == 'B')
                        st0 = _thunkReal!(fn, block, size);
                    else
                        eax = _thunkInt!(fn, block, size);
                    try
                    {
                        _fpreset();
                    }
                    catch (Exception) { } // swallow-ok: no C runtime to reset the floating point state with
                }
                else
                {
                    var boxed = new object[Math.Max(0, types.Length - 1)];
                    for (int i = 1; i < types.Length; i++)
                    {
                        DllValue v = i - 1 < args.Count ? args[i - 1] : default;
                        switch (types[i])
                        {
                            case 'B':
                                boxed[i - 1] = v.D;
                                break;
                            case 'C':
                            case 'D':
                                boxed[i - 1] = StringArg(types[i], v.S ?? "", buffers);
                                break;
                            case 'J':
                                boxed[i - 1] = new IntPtr(unchecked((int)v.I));
                                break;
                            default:
                                boxed[i - 1] = new IntPtr(unchecked((short)v.I));
                                break;
                        }
                    }
                    object r = Invoker64(types)(fn, boxed);
                    if (ret == 'B')
                        st0 = (double)r;
                    else
                        eax = ((IntPtr)r).ToInt64();
                }
                switch (ret)
                {
                    case 'B':
                        result = DllValue.OfReal(st0);
                        break;
                    case 'C':
                    case 'D':
                        result = DllValue.OfStr(
                            StringResult(
                                ret,
                                IntPtr.Size == 4 ? new IntPtr(unchecked((int)eax)) : new IntPtr(eax)
                            )
                        );
                        break;
                    case 'J':
                        result = DllValue.OfInt(unchecked((int)eax));
                        break;
                    default:
                        result = DllValue.OfInt(unchecked((short)eax));
                        break;
                }
                return true;
            }
            catch (Exception ex)
            {
                // an access violation in the library (the runtime's call is inside a structured exception handler too)
                error = "the function failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ .NET classes
        private static object? ToObject(DllValue v, Dictionary<int, object> objects)
        {
            switch (v.Kind)
            {
                case DllValue.Int:
                    return unchecked((int)v.I);
                case DllValue.Real:
                    return v.D;
                case DllValue.Str:
                    return v.S ?? "";
                case DllValue.Obj:
                    return objects.TryGetValue((int)v.I, out object? o) ? o : null;
                default:
                    return null;
            }
        }

        private DllValue FromObject(object? o, int parent)
        {
            switch (o)
            {
                case null:
                    return default;
                case string s:
                    return DllValue.OfStr(s);
                case bool b:
                    return DllValue.OfInt(b ? 1 : 0);
                case char c:
                    return DllValue.OfStr(c.ToString());
                case sbyte _:
                case byte _:
                case short _:
                case ushort _:
                case int _:
                    return DllValue.OfInt(Convert.ToInt64(o, CultureInfo.InvariantCulture));
                case uint u:
                    return u <= int.MaxValue ? DllValue.OfInt(u) : DllValue.OfReal(u);
                case long l:
                    return l >= int.MinValue && l <= int.MaxValue ? DllValue.OfInt(l) : DllValue.OfReal(l);
                case ulong ul:
                    return ul <= int.MaxValue ? DllValue.OfInt((long)ul) : DllValue.OfReal(ul);
                case float _:
                case double _:
                case decimal _:
                    return DllValue.OfReal(Convert.ToDouble(o, CultureInfo.InvariantCulture));
                default:
                    if (o.GetType().IsEnum)
                        return DllValue.OfInt(Convert.ToInt64(o, CultureInfo.InvariantCulture));
                    // any other object (a date and time too) stays here: its handle goes back
                    return DllValue.OfObj(Keep(o, parent));
            }
        }

        private int Keep(object o, int parent)
        {
            int id = _nextObject++;
            _objects[id] = o;
            if (parent > 0)
            {
                if (!_children.TryGetValue(parent, out var l))
                    _children[parent] = l = new List<int>();
                l.Add(id);
            }
            return id;
        }

        private object? Convert1(DllValue v, Type want)
        {
            object? o = ToObject(v, _objects);
            if (o == null)
                return want.IsValueType ? Activator.CreateInstance(want) : null;
            if (want.IsInstanceOfType(o))
                return o;
            Type target = Nullable.GetUnderlyingType(want) ?? want;
            if (target.IsEnum)
                return Enum.ToObject(target, Convert.ToInt64(o, CultureInfo.InvariantCulture));
            return Convert.ChangeType(o, target, CultureInfo.InvariantCulture);
        }

        private static string Why(Exception ex)
        {
            Exception e =
                ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return e.GetType().Name + ": " + e.Message;
        }

        public int ClassCreate(string path, string className, IReadOnlyList<DllValue> args, out string error)
        {
            error = "";
            try
            {
                Assembly asm = Assembly.LoadFile(path);
                Type? type = null;
                foreach (Type ty in asm.GetTypes())
                    if (string.Compare(ty.Name, className, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        type = ty;
                        break;
                    }
                if (type == null)
                {
                    error = "the assembly has no class " + className;
                    return -1;
                }
                object? o;
                if (args.Count == 0)
                    o = Activator.CreateInstance(type, true);
                else
                {
                    var plain = new object?[args.Count];
                    for (int i = 0; i < args.Count; i++)
                        plain[i] = ToObject(args[i], _objects);
                    o = Activator.CreateInstance(type, plain);
                }
                if (o == null)
                {
                    error = "the class cannot be made";
                    return -1;
                }
                return Keep(o, 0);
            }
            catch (Exception ex)
            {
                error = Why(ex);
                return -1;
            }
        }

        public bool ClassIsValid(int id) => _objects.ContainsKey(id);

        public bool ClassDispose(int id, out string error)
        {
            error = "";
            if (!_objects.TryGetValue(id, out object? o))
            {
                error = "no such object";
                return false;
            }
            if (_children.TryGetValue(id, out var kids))
            {
                _children.Remove(id);
                foreach (int k in kids)
                    ClassDispose(k, out _);
            }
            _objects.Remove(id);
            try
            {
                (o as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                error = Why(ex);
            }
            return true;
        }

        public bool ClassGet(int id, string property, out DllValue value, out string error)
        {
            value = default;
            error = "";
            if (!_objects.TryGetValue(id, out object? o))
            {
                error = "no such object";
                return false;
            }
            try
            {
                PropertyInfo? p = o.GetType()
                    .GetProperty(
                        property,
                        BindingFlags.Instance
                            | BindingFlags.Static
                            | BindingFlags.Public
                            | BindingFlags.IgnoreCase
                    );
                if (p == null)
                {
                    FieldInfo? fld = o.GetType()
                        .GetField(
                            property,
                            BindingFlags.Instance
                                | BindingFlags.Static
                                | BindingFlags.Public
                                | BindingFlags.IgnoreCase
                        );
                    if (fld == null)
                    {
                        error = "the class has no property " + property;
                        return false;
                    }
                    value = FromObject(fld.GetValue(o), id);
                    return true;
                }
                value = FromObject(p.GetValue(o, null), id);
                return true;
            }
            catch (Exception ex)
            {
                error = Why(ex);
                return false;
            }
        }

        public bool ClassSet(int id, string property, DllValue value, out string error)
        {
            error = "";
            if (!_objects.TryGetValue(id, out object? o))
            {
                error = "no such object";
                return false;
            }
            try
            {
                PropertyInfo? p = o.GetType()
                    .GetProperty(
                        property,
                        BindingFlags.Instance
                            | BindingFlags.Static
                            | BindingFlags.Public
                            | BindingFlags.IgnoreCase
                    );
                if (p == null)
                {
                    FieldInfo? fld = o.GetType()
                        .GetField(
                            property,
                            BindingFlags.Instance
                                | BindingFlags.Static
                                | BindingFlags.Public
                                | BindingFlags.IgnoreCase
                        );
                    if (fld == null)
                    {
                        error = "the class has no property " + property;
                        return false;
                    }
                    fld.SetValue(o, Convert1(value, fld.FieldType));
                    return true;
                }
                p.SetValue(o, Convert1(value, p.PropertyType), null);
                return true;
            }
            catch (Exception ex)
            {
                error = Why(ex);
                return false;
            }
        }

        public bool ClassCall(
            int id,
            string method,
            IReadOnlyList<DllValue> args,
            out DllValue value,
            out string error
        )
        {
            value = default;
            error = "";
            if (!_objects.TryGetValue(id, out object? o))
            {
                error = "no such object";
                return false;
            }
            try
            {
                // the first method of that name that is not generic (as the runtime takes), here the first
                // with as many parameters when the name is overloaded
                MethodInfo? found = null,
                    named = null;
                foreach (
                    MethodInfo m in o.GetType()
                        .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                )
                {
                    if (
                        m.IsGenericMethod
                        || string.Compare(m.Name, method, StringComparison.OrdinalIgnoreCase) != 0
                    )
                        continue;
                    named = named ?? m;
                    if (m.GetParameters().Length == args.Count)
                    {
                        found = m;
                        break;
                    }
                }
                if (found == null)
                {
                    error =
                        named == null
                            ? "the class has no method " + method
                            : method
                                + " takes "
                                + named.GetParameters().Length.ToString(CultureInfo.InvariantCulture)
                                + " arguments";
                    return false;
                }
                var pars = found.GetParameters();
                var plain = new object?[pars.Length];
                for (int i = 0; i < pars.Length; i++)
                    plain[i] = Convert1(args[i], pars[i].ParameterType);
                value = FromObject(found.Invoke(o, plain), id);
                return true;
            }
            catch (Exception ex)
            {
                error = Why(ex);
                return false;
            }
        }

        public void Dispose()
        {
            foreach (int id in new List<int>(_objects.Keys))
                if (_objects.ContainsKey(id))
                    ClassDispose(id, out _);
            foreach (var f in _fns.Values)
                FreeLibrary(f.Module);
            _fns.Clear();
        }
    }
}
