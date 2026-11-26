using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>Marks a world-backed built-in. The fields mirror builtins/behaviour.json.</summary>
    public static class BuiltinStatus
    {
        public const string Implemented = "implemented";
        public const string Stub = "stub";
        public const string Refused = "refused";

        public static bool Known(string s) => s == Implemented || s == Stub || s == Refused;
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class BuiltinAttribute : Attribute
    {
        /// <summary>The FUNC0 name.</summary>
        public string Name { get; }

        /// <summary>BuiltinStatus.Implemented / Stub / Refused.</summary>
        public string Status { get; set; } = BuiltinStatus.Implemented;

        /// <summary>Help topic ids.</summary>
        public string Doc { get; set; } = "";

        /// <summary>The function can block (refused in foreground code).</summary>
        public bool Blocking { get; set; }

        /// <summary>World changes, '; ' separated.</summary>
        public string Effects { get; set; } = "";

        /// <summary>Hardware errors it raises or returns, '; ' separated.</summary>
        public string Errors { get; set; } = "";

        /// <summary>What a stub returns: a JSON value (builtins/README.md), for status stub.</summary>
        public string Returns { get; set; } = "";

        /// <summary>Conformance cases (paths under conformance/), '; ' separated.</summary>
        public string Cases { get; set; } = "";

        /// <summary>Anything else; '(verify)' marks a guess.</summary>
        public string Notes { get; set; } = "";

        /// <summary>The 0-based argument the function looks up as a graphics page, -1 none
        /// (ctsim/checkExpression warns about a fixed name the project has no page of).</summary>
        public int PageArg { get; set; } = -1;

        /// <summary>The 0-based argument the function looks up as a variable tag, -1 none
        /// (ctsim/checkExpression warns about a fixed name the project does not define).</summary>
        public int TagArg { get; set; } = -1;

        public BuiltinAttribute(string name)
        {
            Name = name;
        }
    }

    /// <summary>A world-backed built-in's body.</summary>
    public delegate Value HostImpl(SimHost h, CicodeTask t, ArgList a);

    /// <summary>The registry of a simulated project: the pure built-ins plus the world-backed ones.</summary>
    public static class WorldBuiltins
    {
        private static volatile List<(BuiltinAttribute Info, HostImpl Fn)>? _declared;
        private static readonly object Lock = new object();

        /// <summary>Every [Builtin] method of this assembly, in name order.</summary>
        public static IReadOnlyList<(BuiltinAttribute Info, HostImpl Fn)> Declared()
        {
            if (_declared != null)
                return _declared;
            lock (Lock)
            {
                if (_declared != null)
                    return _declared;
                var l = new List<(BuiltinAttribute, HostImpl)>();
                foreach (Type ty in typeof(WorldBuiltins).Assembly.GetTypes())
                {
                    if (ty.Namespace != "CicodeSim.Builtins")
                        continue;
                    foreach (
                        MethodInfo m in ty.GetMethods(
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                        )
                    )
                    {
                        var attrs = m.GetCustomAttributes(typeof(BuiltinAttribute), false);
                        if (attrs.Length == 0)
                            continue;
                        var fn = (HostImpl)Delegate.CreateDelegate(typeof(HostImpl), m);
                        foreach (BuiltinAttribute a in attrs)
                            l.Add((a, fn));
                    }
                }
                l.Sort(
                    (x, y) =>
                        string.CompareOrdinal(
                            x.Item1.Name.ToLowerInvariant(),
                            y.Item1.Name.ToLowerInvariant()
                        )
                );
                _declared = l;
                return l;
            }
        }

        /// <summary>The role of a built-in's argument: a page or tag name it looks up
        /// (<see cref="BuiltinAttribute.PageArg"/>, <see cref="BuiltinAttribute.TagArg"/>).</summary>
        public static Compiler.Semantics.NameRole ArgRole(string name, int arg)
        {
            var a = Info(name);
            if (a == null || arg < 0)
                return Compiler.Semantics.NameRole.None;
            if (a.PageArg == arg)
                return Compiler.Semantics.NameRole.Page;
            return a.TagArg == arg ? Compiler.Semantics.NameRole.Tag : Compiler.Semantics.NameRole.None;
        }

        /// <summary>The attribute of a world built-in, null when none.</summary>
        public static BuiltinAttribute? Info(string name)
        {
            var by = _byName;
            if (by == null)
            {
                var m = new Dictionary<string, BuiltinAttribute>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in Declared())
                    if (!m.ContainsKey(d.Info.Name))
                        m[d.Info.Name] = d.Info;
                _byName = by = m;
            }
            return by.TryGetValue(name ?? "", out var a) ? a : null;
        }

        private static volatile Dictionary<string, BuiltinAttribute>? _byName;

        /// <summary>A registry with every built-in over <paramref name="host"/>.</summary>
        public static Registry Create(
            SimHost host,
            Func0Table? table = null,
            IDictionary<string, BuiltinImpl>? overrides = null
        )
        {
            var o = new Dictionary<string, BuiltinImpl>(StringComparer.OrdinalIgnoreCase);
            // CICODESIM_EXCLUDE_BUILTINS=name,name: leave those out (bisecting a behaviour)
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (
                string x in (Environment.GetEnvironmentVariable("CICODESIM_EXCLUDE_BUILTINS") ?? "").Split(
                    ','
                )
            )
                if (x.Trim().Length > 0)
                    skip.Add(x.Trim());
            foreach (var (info, fn) in Declared())
            {
                if (skip.Contains(info.Name))
                    continue;
                HostImpl f = fn;
                string name = info.Name;
                // a stub counts as not simulated
                if (info.Status == BuiltinStatus.Stub)
                    o[name] = (t, a) =>
                    {
                        Value r = f(host, t, a);
                        host.NoteStub(t, name, a, r);
                        return r;
                    };
                else
                    o[name] = (t, a) => f(host, t, a);
            }
            if (overrides != null)
                foreach (var kv in overrides)
                    o[kv.Key] = kv.Value;
            var r = new Registry(table, o);
            host.Registry = r;
            lock (Hosts)
            {
                Hosts.Remove(r);
                Hosts.Add(r, host);
            }
            return r;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            Registry,
            SimHost
        > Hosts = new System.Runtime.CompilerServices.ConditionalWeakTable<Registry, SimHost>();

        /// <summary>The SimHost a registry made by Create runs over, null for another registry.</summary>
        public static SimHost? HostOf(IBuiltins? builtins)
        {
            if (!(builtins is Registry r))
                return null;
            lock (Hosts)
                return Hosts.TryGetValue(r, out SimHost? h) ? h : null;
        }

        /// <summary>A registry over a World (the World's tags, equipment, clock zone ...).</summary>
        public static Registry Create(World.World? world = null, SimHostOptions? options = null) =>
            Create(new SimHost(world, options));

        // ------------------------------------------------------------------ behaviour.json
        /// <summary>builtins/behaviour.json: every implemented built-in, the pure ones with their
        /// Registry source note, the world ones from their [Builtin] attributes. Keys in name
        /// order, LF line endings.</summary>
        public static string BehaviourJson()
        {
            var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var table = Func0Table.Default;
            var reg = Registry.Default();
            foreach (string name in reg.Implemented())
            {
                if (Info(name) != null)
                    continue;
                var sb = new StringBuilder();
                sb.Append("{\"status\": \"implemented\"");
                string src = Registry.Source(name);
                if (src.Length > 0)
                {
                    sb.Append(", \"notes\": ");
                    Json.WriteString(sb, src);
                }
                sb.Append(", \"registry\": \"default\"}");
                entries[Canon(table, name)] = sb.ToString();
            }
            foreach (var (a, _) in Declared())
            {
                var sb = new StringBuilder();
                sb.Append("{\"status\": ");
                Json.WriteString(sb, a.Status);
                if (a.Returns.Length > 0)
                {
                    // builtins/README.md: a stub returns `returns`, a value (a computed result is
                    // 'implemented', its rule in notes)
                    if (!IsJsonValue(a.Returns))
                        throw new InvalidOperationException(
                            a.Name + ": Returns must be a JSON value, not '" + a.Returns + "'"
                        );
                    sb.Append(", \"returns\": ").Append(a.Returns);
                }
                if (a.Blocking)
                    sb.Append(", \"blocking\": true");
                List("effects", a.Effects, sb);
                List("errors", a.Errors, sb);
                List("cases", a.Cases, sb);
                List("doc", a.Doc, sb);
                if (a.Notes.Length > 0)
                {
                    sb.Append(", \"notes\": ");
                    Json.WriteString(sb, a.Notes);
                }
                if (a.PageArg >= 0)
                    sb.Append(", \"pageArg\": ").Append(a.PageArg.ToString(CultureInfo.InvariantCulture));
                if (a.TagArg >= 0)
                    sb.Append(", \"tagArg\": ").Append(a.TagArg.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"registry\": \"world\"}");
                entries[Canon(table, a.Name)] = sb.ToString();
            }
            var o = new StringBuilder();
            o.Append("{\n  \"format\": \"ctsim-behaviour/0\",\n");
            o.Append(
                "  \"generated\": \"ctsim-dev builtins-behaviour (from the engine's registries and [Builtin] attributes)\",\n"
            );
            o.Append("  \"functions\": {");
            bool first = true;
            foreach (var kv in entries)
            {
                o.Append(first ? "\n    " : ",\n    ");
                first = false;
                Json.WriteString(o, kv.Key);
                o.Append(": ").Append(kv.Value);
            }
            o.Append("\n  }\n}\n");
            return o.ToString();
        }

        private static string Canon(Func0Table table, string name) => table.Lookup(name)?.Name ?? name;

        private static bool IsJsonValue(string text)
        {
            try
            {
                Json.Parse(text);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void List(string key, string text, StringBuilder sb)
        {
            if (text.Length == 0)
                return;
            sb.Append(", \"").Append(key).Append("\": [");
            bool first = true;
            foreach (string part in SplitItems(text))
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                Json.WriteString(sb, part);
            }
            sb.Append(']');
        }

        /// <summary>The items of an attribute list: '; ' separated, not inside parentheses
        /// ("424 no such alarm (returned; guess on the code)" is one item).</summary>
        internal static List<string> SplitItems(string text)
        {
            var items = new List<string>();
            int depth = 0,
                start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(')
                    depth++;
                else if (c == ')' && depth > 0)
                    depth--;
                else if (c == ';' && depth == 0 && i + 1 < text.Length && text[i + 1] == ' ')
                {
                    string part = text.Substring(start, i - start).Trim();
                    if (part.Length > 0)
                        items.Add(part);
                    start = i + 2;
                    i++;
                }
            }
            string last = text.Substring(start).Trim();
            if (last.Length > 0)
                items.Add(last);
            return items;
        }
    }

    /// <summary>Options of a SimHost.</summary>
    public sealed partial class SimHostOptions
    {
        /// <summary>The local time zone of the simulated computer (default: this machine's, as
        /// the runtime uses its computer's; tests and sweeps pin one).</summary>
        public TimeZoneInfo? TimeZone;

        /// <summary>The Windows [intl] settings the runtime reads at start (default: the
        /// runtime's built-in defaults, TimeLocale.Binary).</summary>
        public TimeLocale? Locale;
    }

    /// <summary>The state a simulated Cicode process keeps for its built-ins.</summary>
    public sealed partial class SimHost
    {
        public World.World? World { get; }
        public TimeZoneInfo TimeZone { get; set; }
        public TimeLocale Locale { get; set; }
        public Registry? Registry { get; internal set; }

        internal readonly HandleTable<Semaphore> Sems = new HandleTable<Semaphore>();
        internal readonly HandleTable<CicodeQueue> Queues = new HandleTable<CicodeQueue>();
        internal readonly HandleTable<FmtTemplate> Formats = new HandleTable<FmtTemplate>();
        internal readonly HandleTable<Subscription> Subscriptions = new HandleTable<Subscription>();
        internal readonly HandleTable<BrowseSession> Browses = new HandleTable<BrowseSession>();
        internal readonly HandleTable<string> Resolves = new HandleTable<string>();

        /// <summary>ErrSetHw's device status: (device type, device) to error.</summary>
        internal readonly Dictionary<(long, long), long> HwErrors = new Dictionary<(long, long), long>();

        /// <summary>OnEvent / SetEvent: the handler function handle of each event type, -1 = none.</summary>
        internal readonly long[] Events = NewEvents();

        private static long[] NewEvents()
        {
            var e = new long[256];
            for (int i = 0; i < e.Length; i++)
                e[i] = -1;
            return e;
        }

        /// <summary>The function handle that handles event <paramref name="type"/> (-1 = none):
        /// for a host that raises page / login / key events.</summary>
        public long EventHandler(int type) => type >= 0 && type < Events.Length ? Events[type] : -1;

        /// <summary>Calls running nested in a caller's built-in call (event handlers; TaskCall
        /// without a scheduler): each level is a C# call, so a deep nest is cut off at
        /// <see cref="MaxNesting"/> with 295 instead of overflowing the host's stack.</summary>
        internal int Nesting;
        public const int MaxNesting = 64;

        /// <summary>CodeTrace(-2 / -3, mode): the trace flags of new tasks.</summary>
        internal long TraceDefault;

        /// <summary>Unhooks the World tag listener the subscriptions' callbacks use.</summary>
        internal Action? SubscriptionHook;

        /// <summary>Elements in all queues ([Code]Queue buffers, doc: 32768).</summary>
        internal int QueueElements;
        public int QueueBuffers = 32768;

        /// <summary>Open browse sessions a process may hold (simulator limit; a leaked session
        /// keeps its records: 271 'No more free handles' past it).</summary>
        public const int MaxBrowses = 256;

        /// <summary>The records tag browses choose from (Equipment.TagRecords, built at the first
        /// TagBrowseOpen).</summary>
        internal Equipment.TagRecords? TagRecords;

        /// <summary>_Wave type 4's random walk (the static double).</summary>
        internal double WaveWalk;

        /// <summary>Log lines of the built-ins (TraceMsg, ErrLog, not-simulated notes ...).</summary>
        public Action<string>? Log;

        public SimHost(World.World? world = null, SimHostOptions? options = null)
        {
            World = world;
            TimeZone = options?.TimeZone ?? TimeZoneInfo.Local;
            Locale = options?.Locale ?? TimeLocale.Binary;
            // the trend history: the World's tag change log from now on
            if (world != null)
                CicodeSim.World.TagHistory.Attach(world);
            InitInteraction(options);
        }

        private readonly Dictionary<string, List<Dictionary<string, string>>> _tables = new Dictionary<
            string,
            List<Dictionary<string, string>>
        >(StringComparer.OrdinalIgnoreCase);

        /// <summary>A small project table's rows, read once (the browse sessions index into them).</summary>
        internal List<Dictionary<string, string>> TableRows(
            string table,
            Func<List<Dictionary<string, string>>> read
        )
        {
            if (!_tables.TryGetValue(table, out var l))
                _tables[table] = l = read();
            return l;
        }

        internal void Note(CicodeTask t, string text)
        {
            Log?.Invoke(text);
            t.Vm.Log?.Invoke(text);
        }
    }

    /// <summary>A handle table: the lowest free slot from 0 (as the runtime gives handles).</summary>
    internal sealed class HandleTable<T>
        where T : class
    {
        private readonly List<T?> _slots = new List<T?>();

        public int Add(T item)
        {
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i] == null)
                {
                    _slots[i] = item;
                    return i;
                }
            _slots.Add(item);
            return _slots.Count - 1;
        }

        public T? Get(long h) => h >= 0 && h < _slots.Count ? _slots[(int)h] : null;

        /// <summary>The handles in use.</summary>
        public int Count
        {
            get
            {
                int n = 0;
                foreach (var x in _slots)
                    if (x != null)
                        n++;
                return n;
            }
        }

        public void Free(long h)
        {
            if (h >= 0 && h < _slots.Count)
                _slots[(int)h] = null;
        }

        public IEnumerable<(int Handle, T Item)> Live()
        {
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i] != null)
                    yield return (i, _slots[i]!);
        }
    }

    /// <summary>Argument and result helpers of the world built-ins.</summary>
    internal static class W
    {
        public static Value I(long v) => Value.Long(v);

        public static Value S(string v) => Value.Str(v);

        public static Value R(double v) => Value.Real(v);

        public static readonly Value Empty = Value.Str("");

        public static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>The background slice refuses foreground code (CodeBlock returns -1
        /// for a task in state 6, RunExec).</summary>
        public static bool Foreground(CicodeTask t) => t.Foreground;

        /// <summary>A blocking call that completes at the task's next turn (the runtime's
        /// asynchronous request answered at once): the result is computed when the task wakes.</summary>
        public static Value BlockOneTurn(CicodeTask t, string reason, Func<Value?> result)
        {
            t.Wait = new Wait(TaskState.Wait, reason) { Result = result };
            return Value.Block;
        }

        /// <summary>Write a by-reference argument (QueRead's Type / Str ...); a value that is not
        /// a reference (call text, a test) is left alone. A waiter's stack slot that no longer
        /// exists (its task ended) is skipped too.</summary>
        public static void Out(CicodeTask t, in Value r, Value v)
        {
            if (!(r.O is Ref))
                return;
            try
            {
                t.Vm.AssignRef(r, v);
            }
            catch (IndexOutOfRangeException) { }
        }

        public static void Out(CicodeTask t, in Value r, long v) => Out(t, r, Value.Long((int)v));

        public static void Out(CicodeTask t, in Value r, string v) => Out(t, r, Value.Str(v));

        /// <summary>
        /// Run ``fn`` nested in ``t``'s call to its end; null when the nesting is too deep.
        /// </summary>
        public static CicodeTask? RunNested(
            SimHost h,
            CicodeTask t,
            Function fn,
            IReadOnlyList<Value> args,
            int maxSlices
        )
        {
            if (h.Nesting >= SimHost.MaxNesting)
            {
                t.Vm.Fatal(
                    t,
                    Errors.Stack,
                    "Cicode stack overflow (nested calls deeper than " + W.Inv(SimHost.MaxNesting) + ")"
                );
                return null;
            }
            var x = t.Vm.TaskForCall(fn, args, t.Foreground, null, fn.Name);
            h.Nesting++;
            try
            {
                t.Vm.Run(x, maxSlices);
            }
            catch (InvalidOperationException) { }
            finally
            {
                h.Nesting--;
            }
            if (x.State == TaskState.Halt && x.HaltCode == Errors.Stack)
                t.Vm.Fatal(t, Errors.Stack, x.HaltReason);
            return x;
        }

        /// <summary>A raised hardware error without the halt flag (most built-ins).</summary>
        public static void Raise(CicodeTask t, int code) => t.RaiseError(code, false);
    }
}
