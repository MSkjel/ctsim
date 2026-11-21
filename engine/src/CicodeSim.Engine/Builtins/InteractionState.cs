using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    public sealed partial class SimHostOptions
    {
        /// <summary>The sandbox of the File* built-ins. A simulation passes one (new SandboxOptions
        /// { Id = its id }: writes in %LOCALAPPDATA%\cicode-sim\sandbox\(id)); null = SimHost.DefaultSandbox.</summary>
        public SandboxOptions? Sandbox;

        /// <summary>The UI of forms and message boxes (null = headless: every dialog is cancelled).</summary>
        public IInteractionHost? Interaction;
    }

    /// <summary>The interaction and sandbox state of a simulated process:
    /// the sandbox of the File* built-ins with their open file and find tables, the forms, the
    /// dialog host, and the stub accounting.</summary>
    public sealed partial class SimHost
    {
        internal readonly Dictionary<string, Dictionary<string, DdeTopic>> DdeServers = new Dictionary<
            string,
            Dictionary<string, DdeTopic>
        >(StringComparer.OrdinalIgnoreCase);
        internal readonly HandleTable<DdeConversation> DdeConversations = new HandleTable<DdeConversation>();

        private Sandbox? _sandbox;
        private SandboxOptions? _sandboxOptions;

        /// <summary>The UI dialogs go to (default: headless, every dialog cancelled).</summary>
        public IInteractionHost Interaction { get; set; } = HeadlessInteraction.Instance;

        /// <summary>The virtual file system of the File* built-ins (made at its first use; its
        /// changes go to the World's trace as 'file' events).</summary>
        public Sandbox Sandbox
        {
            get
            {
                if (_sandbox == null)
                {
                    _sandbox = new Sandbox(_sandboxOptions ?? DefaultSandbox(), World);
                    _sandbox.Changed += e =>
                    {
                        var tr = World?.Trace;
                        if (tr == null)
                            return;
                        var f = new List<(string, object?)> { ("op", e.Op), ("path", e.Path) };
                        if (e.Other.Length > 0)
                            f.Add(("other", e.Other));
                        if (e.Op == "write" || e.Op == "copy")
                            f.Add(("bytes", e.Bytes));
                        tr.Event("file", null, null, f.ToArray());
                    };
                }
                return _sandbox;
            }
        }

        /// <summary>The sandbox of a host given none: in memory (tests, sweeps, one-off runs write
        /// nothing to any disk), unless CICODESIM_SANDBOX says otherwise: "folder" = a sandbox
        /// folder under %LOCALAPPDATA%\cicode-sim\sandbox, another text = that folder.</summary>
        public static SandboxOptions DefaultSandbox()
        {
            string v = (Environment.GetEnvironmentVariable("CICODESIM_SANDBOX") ?? "").Trim();
            if (v.Length == 0 || string.Equals(v, "memory", StringComparison.OrdinalIgnoreCase))
                return new SandboxOptions { InMemory = true };
            if (string.Equals(v, "folder", StringComparison.OrdinalIgnoreCase))
                return new SandboxOptions();
            return new SandboxOptions { Folder = v };
        }

        /// <summary>The sandbox of a simulation (a server): its own folder under
        /// %LOCALAPPDATA%\cicode-sim\sandbox, made at the first write, unless CICODESIM_SANDBOX says
        /// otherwise (memory, folder or a folder path).</summary>
        public static SandboxOptions SimulationSandbox() =>
            (Environment.GetEnvironmentVariable("CICODESIM_SANDBOX") ?? "").Trim().Length > 0
                ? DefaultSandbox()
                : new SandboxOptions();

        /// <summary>Use these sandbox options (before the first File* call).</summary>
        public void UseSandbox(SandboxOptions options)
        {
            _sandboxOptions = options;
            _sandbox = null;
        }

        partial void InitInteraction(SimHostOptions? options);

        partial void InitInteraction(SimHostOptions? options)
        {
            _sandboxOptions = options?.Sandbox;
            if (options?.Interaction != null)
                Interaction = options.Interaction;
        }

        // ------------------------------------------------------------------ files
        internal readonly HandleTable<OpenFile> OpenFiles = new HandleTable<OpenFile>();

        /// <summary>FileFind's sessions: five for the process, one per task.</summary>
        internal readonly FindSession?[] Finds = new FindSession?[5];

        // ------------------------------------------------------------------ forms
        internal readonly HandleTable<CForm> Forms = new HandleTable<CForm>();

        /// <summary>The form being built and its last field.</summary>
        internal int CurrentForm = -1,
            LastField = -1;

        /// <summary>The VM clock's milliseconds of the last FormNew.</summary>
        internal double LastFormNew;

        /// <summary>FormCurr: the form and field of the last button pressed (34).</summary>
        internal int CurrForm,
            CurrField;

        /// <summary>Take the dialog host's answers since the last call.</summary>
        public void PumpDialogs()
        {
            FormBuiltins.Pump(this);
            OutsideAsk.PumpAnswers(this); // (a permission question nobody waits for)
        }

        // ------------------------------------------------------------------ stubs
        /// <summary>The last SQL call failed (SQLErrMsg's text).</summary>
        internal bool SqlFailed;

        /// <summary>A stub ran: it counts in the VM's not-simulated table (the ctsim/notSimulated
        /// event) and goes to the trace as a 'stub' event.</summary>
        internal void NoteStub(CicodeTask t, string name, ArgList a, Value result)
        {
            var counts = t.Vm.NotSimulatedCounts;
            counts.TryGetValue(name, out int n);
            counts[name] = n + 1;
            var tr = World?.Trace;
            if (tr == null)
                return;
            var args = JsonValue.NewArray();
            for (int i = 0; i < a.Count; i++)
                args.Add(global::CicodeSim.World.TraceWriter.Jsonable(Value.Deref(a.Get(i))));
            object? ret = result.IsNothing || result.IsBlock ? null : (object)result;
            tr.Event(
                "stub",
                null,
                t.Handle >= 0 ? (object)t.Handle : null,
                ("fn", name),
                ("args", args),
                ("returned", ret)
            );
        }
    }

    /// <summary>One FileOpen handle.</summary>
    internal sealed class OpenFile
    {
        public SandboxFile F = null!;
        public bool Exclusive;
    }

    /// <summary>One FileFind session: the matches of its pattern still to give.</summary>
    internal sealed class FindSession
    {
        public CicodeTask Task = null!;
        public string Drive = "",
            Dir = "";
        public List<Found> Left = new List<Found>();
        public int Next;
        public int Mask;
        public bool Single;
    }
}
