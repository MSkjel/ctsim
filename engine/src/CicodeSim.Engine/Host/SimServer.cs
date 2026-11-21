using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Host
{
    /// <summary>A debugger session on one connection (the DAP layer; the VS Code extension
    /// implements it in engine/src/ctsim/Dap/). The server hands it every message of its
    /// connection once the first one was a DAP 'initialize'.</summary>
    public interface IDapSession
    {
        /// <summary>A message of the session's connection (the first is the initialize
        /// request). Called on the connection's reader thread, in order.</summary>
        void OnMessage(JsonValue message);

        /// <summary>The connection closed (once). The server then detaches the debugger
        /// (breakpoints cleared, stopped tasks released) unless the session did.</summary>
        void OnClosed();
    }

    /// <summary>What a DAP session works with.</summary>
    public sealed class DapContext
    {
        public Simulation Simulation { get; }
        public IDebugTarget Debugger => Simulation.Debugger;

        /// <summary>Where the session writes its responses and events (seq is set there).</summary>
        public IMessageSink Sink { get; }
        public SimServer Server { get; }

        /// <summary>ctsim dap's stdio session (VS Code owns the process: it ends with the session).</summary>
        public bool Stdio { get; }

        internal DapContext(Simulation sim, IMessageSink sink, SimServer server, bool stdio)
        {
            Simulation = sim;
            Sink = sink;
            Server = server;
            Stdio = stdio;
        }

        /// <summary>Handle a ctsim/* request of the session as the viewer protocol does (its
        /// response and later ctsim/* events go to Sink); false when it is not one.</summary>
        public bool HandleCustom(JsonValue request) => Server.HandleCustom(Sink, request);
    }

    /// <summary>The server side of ctsim.exe: one simulation, its listener and its clients.</summary>
    public sealed partial class SimServer : IDisposable
    {
        public const string Protocol = "ctsim/0";
        public const string NotImplementedPages = "not implemented yet: the page animator is a later package";

        public Simulation Simulation { get; }
        public string Id { get; }

        /// <summary>serve or dap.</summary>
        public string Mode { get; }
        public Listener? Listener { get; private set; }
        public ServerEntry? Entry { get; private set; }
        public string? DiscoveryPath { get; private set; }

        /// <summary>Makes the debugger session of a connection that sent 'initialize'.</summary>
        public Func<DapContext, IDapSession>? DapFactory { get; set; }

        /// <summary>A client asked the server to end (ctsim/shutdown), or the stdio debugger left.</summary>
        public event Action<string>? ShutdownRequested;

        private sealed class Client
        {
            public IMessageSink Sink = null!;
            public MessageConnection? Conn;
            public string Name = "";
            public bool Viewer;
            public string ClientName = "";
            public bool Stdio;
            public IDapSession? Dap;

            /// <summary>The debugger's connection closed and the server is letting go of it
            /// (OnClosed): it no longer counts as attached, and a new debugger waits for it.</summary>
            public bool Closing;

            /// <summary>The debugger session is handling its DAP disconnect.</summary>
            public bool Disconnecting;

            /// <summary>StartDebugger has made this client the debugger and is making its session
            /// (Dap still null).</summary>
            public bool Making;

            /// <summary>DebugTarget.AttachSession's count when this debugger's session started.</summary>
            public long AttachedAt;

            /// <summary>subscription key (the tag's defined name, or alarm.property) -> the names the client used.</summary>
            public readonly Dictionary<string, HashSet<string>> Subs = new Dictionary<
                string,
                HashSet<string>
            >(StringComparer.OrdinalIgnoreCase);

            /// <summary>The subscribed names that are read on their own ('Tag.Q', 'Tag.T' ...:
            /// Simulation.IsOwnRead); each is counted once in Simulation.Watch.</summary>
            public readonly HashSet<string> Own = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>Subscribed names the project does not define: subscribed once setTag makes
            /// a simulated tag for one.</summary>
            public readonly HashSet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string>? Events;

            public bool Wants(string ev) => Viewer && (Events == null || Events.Contains(ev));

            /// <summary>hello {dialogs: true}: the viewer shows the simulation's dialogs (ctsim/dialog).</summary>
            public bool Dialogs;

            /// <summary>hello {models: true}: the viewer sends the drawing of a page Cicode
            /// displays when asked (ctsim/modelWanted).</summary>
            public bool Models;
        }

        private readonly object _lock = new object();
        private readonly List<Client> _clients = new List<Client>();
        private Client? _debugger;
        private TagListing? _listing;
        private int _disposed;

        public SimServer(Simulation sim, string? id = null, string mode = "serve")
        {
            Simulation = sim;
            Id = id ?? Discovery.NewId();
            Mode = mode;
            sim.TagsChanged += OnTagsChanged;
            sim.Log += e => Broadcast("log", "ctsim/log", e.ToJson());
            sim.NotSimulated += (n, c) =>
            {
                var b = JsonValue.NewObject();
                b.Set("function", n);
                b.Set("count", JsonValue.Of(c));
                Broadcast("notSimulated", "ctsim/notSimulated", b);
            };
            sim.TaskStarted += t => TaskEvent("ctsim/taskStarted", t);
            sim.TaskExited += t => TaskEvent("ctsim/taskExited", t);
            sim.ClockChanged += c => Broadcast("clock", "ctsim/clockChanged", c);
            sim.UserChanged += u => Broadcast("options", "ctsim/userChanged", u);
            sim.ScenarioFinished += r => Broadcast("scenario", "ctsim/scenarioFinished", r);
            sim.Pages.PageChanged += b => Broadcast("page", "ctsim/pageChanged", b);
            sim.Pages.WindowsChanged += b => Broadcast("page", "ctsim/windowsChanged", b);
            // page code the debugger stopped holds the simulation: the viewers'
            // requests wait until it goes on
            sim.DebugPaused += b => Broadcast("debug", "ctsim/debugPaused", b);
            StartAlarmEvents();
            // the drawings of pages Cicode displays, and the dialogs, from the viewers that
            // said they supply them
            sim.Pages.ModelViewers = () => HasClient(c => c.Models);
            sim.Pages.ModelWanted += b => SendTo(c => c.Models, "ctsim/modelWanted", b);
            Dialogs = new ViewerDialogs(
                sim.Host.Interaction,
                () => HasClient(c => c.Dialogs),
                (name, body) => sim.Events.Post(() => SendTo(c => c.Dialogs, name, body)),
                line => sim.Note("info", line),
                text => sim.World.Display.Language.Localise(text)
            );
            sim.Host.Interaction = Dialogs;
            // an 'always' answer of a permission question put a library or a database on its whitelist
            sim.Host.Outside.Changed = () =>
                sim.Events.Post(() => Broadcast("options", "ctsim/optionsChanged", OptionsBody()));
        }

        /// <summary>The dialogs the viewers show (ctsim/dialog, ctsim/answer).</summary>
        public ViewerDialogs Dialogs { get; }

        private bool HasClient(Func<Client, bool> which)
        {
            lock (_lock)
                return _clients.Exists(c => c.Viewer && !c.Sink.IsClosed && which(c));
        }

        /// <summary>An event to the viewers <paramref name="which"/> picks.</summary>
        private void SendTo(Func<Client, bool> which, string name, JsonValue body)
        {
            List<Client> to;
            lock (_lock)
                to = _clients.FindAll(c => c.Viewer && which(c));
            if (to.Count == 0)
                return;
            var ev = Event(name, body);
            foreach (var c in to)
                c.Sink.Send(ev);
        }

        public int ClientCount
        {
            get
            {
                lock (_lock)
                    return _clients.Count;
            }
        }

        /// <summary>A debugger session is attached: false from its DAP disconnect or the moment
        /// its connection closes (a client that went away), while the server still lets go of it.</summary>
        public bool DebuggerAttached
        {
            get
            {
                lock (_lock)
                    return _debugger != null && !IsClosing(_debugger);
            }
        }

        /// <summary>How long a new debugger waits for one whose connection is closing (its
        /// drain timeout is 5 s; then its session ends, the target detaches and its last events
        /// are delivered).</summary>
        public int DebuggerHandoverMs { get; set; } = 15000;

        private static bool IsClosing(Client c) =>
            c.Closing || c.Disconnecting || (c.Conn?.IsClosed ?? false);

        // ------------------------------------------------------------------ listening
        /// <summary>Open the listener (\\.\pipe\ctsim.&lt;id&gt;) and write the discovery file.</summary>
        public void Listen(string? discoveryFolder = null)
        {
            var l = Listener.Open(Id);
            Listener = l;
            l.Error += e =>
                Simulation.Note("warning", "the listener: " + e.GetType().Name + ": " + e.Message);
            l.Start((stream, name) => AddConnection(stream, stream, name));
            long start;
            using (var p = System.Diagnostics.Process.GetCurrentProcess())
                start = p.StartTime.ToUniversalTime().ToFileTimeUtc();
            Entry = new ServerEntry
            {
                Id = Id,
                Transport = l.Transport,
                Address = l.Address,
                Pid = Discovery.CurrentPid,
                ProcessStart = start,
                Mode = Mode,
                Project = Simulation.ProjectDir,
                ProjectName = Simulation.ProjectName,
                Root = Simulation.Root,
                RootReason = Simulation.RootReason,
                Roots = new List<string>(Simulation.RootCandidates),
                User = Simulation.UserDir,
                Install = Simulation.InstallDir,
                Version = EngineInfo.Version,
                Started = Simulation.StartedUtc.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture
                ),
            };
            _discoveryFolder = discoveryFolder;
            DiscoveryPath = Discovery.Write(Entry, discoveryFolder);
        }

        private string? _discoveryFolder;

        /// <summary>Serve a connection (a pipe client, or ctsim dap's stdin / stdout).</summary>
        public MessageConnection AddConnection(Stream input, Stream output, string name, bool stdio = false)
        {
            var conn = new MessageConnection(input, output, name);
            var c = new Client
            {
                Sink = conn,
                Conn = conn,
                Name = name,
                Stdio = stdio,
            };
            lock (_lock)
                _clients.Add(c);
            conn.Message += (cn, msg) => OnMessage(c, msg);
            conn.Closed += (cn, why) => OnClosed(c, why);
            conn.Start();
            return conn;
        }

        private void OnClosed(Client c, string? why)
        {
            IDapSession? dap;
            List<string> own;
            bool debugger,
                making;
            lock (_lock)
            {
                _clients.Remove(c);
                dap = c.Dap;
                // the debugger stays this client until it has let go: a new debugger's
                // initialize waits (StartDebugger), else this session's Detach would clear the
                // new one's breakpoints and its last events would reach the new client
                debugger = _debugger == c;
                if (debugger)
                    c.Closing = true;
                // its session is still being made (StartDebugger): StartDebugger lets go once it
                // has the session, and the debugger stays this client until then
                making = debugger && c.Making;
                own = new List<string>(c.Own);
                c.Own.Clear();
                c.Subs.Clear();
            }
            Unwatch(own);
            // the last viewer that showed dialogs went: none waits for it any more
            if (c.Dialogs && !HasClient(x => x.Dialogs))
                Dialogs.CancelAll();
            if (!making)
            {
                try
                {
                    if (dap != null)
                        LetGo(c, dap);
                }
                finally
                {
                    if (debugger)
                        Release(c);
                }
            }
            if (c.Stdio)
                ShutdownRequested?.Invoke(
                    "the debugger's stdio session ended" + (why != null ? ": " + why : "")
                );
        }

        /// <summary>A closing debugger's session ends: its OnClosed, the target detached unless
        /// the session detached itself since it attached, its last events delivered.</summary>
        private void LetGo(Client c, IDapSession dap)
        {
            try
            {
                dap.OnClosed();
            }
            catch (Exception) { }
            // a session that detached on its disconnect is not detached again
            try
            {
                Simulation.Debugger.DetachSession(c.AttachedAt);
            }
            catch (Exception) { }
            // the session's last events (its ended call's thread exit and output ...) are
            // delivered before another debugger subscribes
            try
            {
                Simulation.Events.Flush(5000);
            }
            catch (Exception) { }
        }

        /// <summary>The debugger is no longer <paramref name="c"/>: a waiting initialize may take over.</summary>
        private void Release(Client c)
        {
            lock (_lock)
            {
                if (_debugger == c)
                    _debugger = null;
                Monitor.PulseAll(_lock);
            }
        }

        private void OnMessage(Client c, JsonValue msg)
        {
            var dap = c.Dap;
            if (dap != null)
            {
                // a disconnect: the session answers before it detaches and closes, so the
                // debugger counts as closing from here (a new initialize waits for the handover
                // instead of being refused); a session that keeps its connection after it is
                // live again
                bool disconnect =
                    msg.Get("type")?.Str == "request" && msg.Get("command")?.Str == "disconnect";
                if (disconnect)
                    lock (_lock)
                        c.Disconnecting = true;
                try
                {
                    dap.OnMessage(msg);
                }
                finally
                {
                    if (disconnect && !(c.Conn?.IsClosed ?? c.Sink.IsClosed))
                        lock (_lock)
                        {
                            c.Disconnecting = false;
                            Monitor.PulseAll(_lock);
                        }
                }
                return;
            }
            string type = msg.Get("type")?.Str ?? "";
            if (type != "request")
                return;
            string cmd = msg.Get("command")?.Str ?? "";
            if (cmd.StartsWith("ctsim/", StringComparison.Ordinal))
            {
                Handle(c, msg);
                return;
            }
            if (cmd == "initialize")
            {
                StartDebugger(c, msg);
                return;
            }
            Fail(
                c.Sink,
                msg,
                "unknown request "
                    + Project.CitectText.Quote(cmd)
                    + ": send ctsim/hello (a viewer) or a DAP initialize (a debugger) first"
            );
        }

        private void StartDebugger(Client c, JsonValue msg)
        {
            var factory = DapFactory;
            if (factory == null)
            {
                Fail(c.Sink, msg, "this server has no debugger");
                return;
            }
            bool taken;
            lock (_lock)
            {
                // a debugger whose connection is closing lets go first (OnClosed: its session
                // ends, the target detaches, its last events are delivered); a live one refuses
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_debugger != null && _debugger != c && IsClosing(_debugger))
                {
                    int left = DebuggerHandoverMs - (int)sw.ElapsedMilliseconds;
                    if (left <= 0)
                        break;
                    Monitor.Wait(_lock, left);
                }
                taken = _debugger != null && _debugger != c;
                if (!taken)
                {
                    _debugger = c;
                    c.Making = true;
                }
            }
            if (taken)
            {
                Fail(c.Sink, msg, "a debugger is already attached to this simulation (one at a time)");
                return;
            }
            IDapSession s;
            bool attached = false;
            try
            {
                // the runtime's SESSION_START: the error rule applies from now
                c.AttachedAt = Simulation.Debugger.AttachSession();
                attached = true;
                s = factory(new DapContext(Simulation, c.Sink, this, c.Stdio));
            }
            catch (Exception e)
            {
                lock (_lock)
                    c.Making = false;
                // detached before it lets go: a debugger that takes over keeps its breakpoints
                if (attached)
                    try
                    {
                        Simulation.Debugger.DetachSession(c.AttachedAt);
                    }
                    catch (Exception) { }
                Release(c);
                Fail(c.Sink, msg, "the debugger session cannot start: " + e.Message);
                return;
            }
            // OnClosed reads Dap under the lock; a connection that closed while the session was
            // made left the letting go to here (else the target would stay attached with nobody
            // to continue a stopped task)
            bool gone;
            lock (_lock)
            {
                c.Dap = s;
                c.Making = false;
                gone = c.Closing;
            }
            if (gone)
            {
                try
                {
                    LetGo(c, s);
                }
                finally
                {
                    Release(c);
                }
                return;
            }
            s.OnMessage(msg);
        }

        /// <summary>A ctsim/* request of a DAP session (DapContext.HandleCustom).</summary>
        internal bool HandleCustom(IMessageSink sink, JsonValue request)
        {
            string cmd = request.Get("command")?.Str ?? "";
            if (!cmd.StartsWith("ctsim/", StringComparison.Ordinal))
                return false;
            Client? c;
            lock (_lock)
                c = _clients.Find(x => x.Sink == sink);
            if (c == null)
            {
                c = new Client { Sink = sink, Name = sink.Name };
                lock (_lock)
                    _clients.Add(c);
            }
            Handle(c, request);
            return true;
        }

        // ------------------------------------------------------------------ messages out
        private static JsonValue Response(JsonValue req, bool ok, JsonValue? body, string? message)
        {
            var r = JsonValue.NewObject();
            r.Set("type", "response");
            var seq = req.Get("seq");
            r.Set("request_seq", seq != null && seq.Kind == JsonKind.Int ? seq : JsonValue.Of(0));
            r.Set("success", JsonValue.Of(ok));
            r.Set("command", req.Get("command")?.Str ?? "");
            if (message != null)
                r.Set("message", message);
            r.Set("body", body ?? JsonValue.NewObject());
            return r;
        }

        private static void Ok(IMessageSink s, JsonValue req, JsonValue? body) =>
            s.Send(Response(req, true, body, null));

        private static void Fail(IMessageSink s, JsonValue req, string message, JsonValue? body = null) =>
            s.Send(Response(req, false, body, message));

        public static JsonValue Event(string name, JsonValue body)
        {
            var e = JsonValue.NewObject();
            e.Set("type", "event");
            e.Set("event", name);
            e.Set("body", body);
            return e;
        }

        /// <summary>Task events a second may send one by one; the rest of that second (tasks that
        /// did not halt) are counted and told in one ctsim/log line when the next second starts.
        /// An SA alarm page can run thousands of tasks a second.</summary>
        public const int TaskEventsPerSecond = 100;
        private long _taskSecond = -1;
        private int _taskSent;
        private int _taskLeft;
        private readonly Dictionary<string, int> _taskLeftBy = new Dictionary<string, int>(
            StringComparer.Ordinal
        );
        private readonly object _taskLock = new object();

        /// <summary>The clock the gate reads (milliseconds; tests replace it).</summary>
        public Func<long> TaskClock = () =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;

        internal void TaskEvent(string name, TaskSummary t)
        {
            bool halted = t.HaltCode != 0 || t.HaltReason.Length > 0;
            string? note = null;
            bool send;
            lock (_taskLock)
            {
                long sec = TaskClock() / 1000;
                if (sec != _taskSecond)
                {
                    note = TaskLeftNote();
                    _taskSecond = sec;
                    _taskSent = 0;
                }
                send = halted || _taskSent < TaskEventsPerSecond;
                if (send)
                    _taskSent++;
                else
                {
                    _taskLeft++;
                    _taskLeftBy.TryGetValue(t.Function, out var n);
                    _taskLeftBy[t.Function] = n + 1;
                }
            }
            if (note != null)
                SendTaskNote(note);
            if (send)
                Broadcast("task", name, t.ToJson());
        }

        /// <summary>The line for the task events the last busy second left out (null none);
        /// resets the count. Call under _taskLock.</summary>
        private string? TaskLeftNote()
        {
            if (_taskLeft == 0)
                return null;
            var top = new List<KeyValuePair<string, int>>(_taskLeftBy);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var names = new List<string>();
            for (int k = 0; k < top.Count && k < 3; k++)
                names.Add((top[k].Key.Length > 0 ? top[k].Key : "?") + " " + top[k].Value);
            string text = _taskLeft + " more task events in one second (" + string.Join(", ", names) + ")";
            _taskLeft = 0;
            _taskLeftBy.Clear();
            return text;
        }

        private void SendTaskNote(string text)
        {
            var e = new LogEntry
            {
                Level = "info",
                Kind = "task",
                Text = text,
            };
            Broadcast("task", "ctsim/log", e.ToJson());
        }

        /// <summary>Tell the task events a busy second left out now (a timer or a test: the
        /// next task event tells them too).</summary>
        public void FlushTaskEvents()
        {
            string? note;
            lock (_taskLock)
            {
                note = TaskLeftNote();
                _taskSecond = -1;
            }
            if (note != null)
                SendTaskNote(note);
        }

        private void Broadcast(string kind, string name, JsonValue body)
        {
            List<Client> to;
            lock (_lock)
            {
                // a client made by HandleCustom for a sink of a DAP session has no connection
                // whose Closed would remove it
                _clients.RemoveAll(c => c.Conn == null && c.Sink.IsClosed);
                to = _clients.FindAll(c => c.Wants(kind));
            }
            if (to.Count == 0)
                return;
            var ev = Event(name, body);
            foreach (var c in to)
                c.Sink.Send(ev);
        }

        private void OnTagsChanged(TagBatch batch)
        {
            var updates = batch.Updates;
            List<Client> subs;
            lock (_lock)
                subs = _clients.FindAll(c => c.Subs.Count > 0);
            if (subs.Count == 0)
                return;
            var clock = batch.Clock.Kind == JsonKind.Object ? batch.Clock : JsonValue.NewObject();
            foreach (var c in subs)
            {
                var tags = JsonValue.NewArray();
                lock (_lock)
                {
                    foreach (var u in updates)
                    {
                        if (!c.Subs.TryGetValue(u.Name, out var asked))
                            continue;
                        foreach (string a in asked)
                        {
                            // the tag's own entry for its plain names, the entry read by the
                            // name for an own-read one ('Tag.Q' gets the quality, not the value)
                            if (
                                u.Asked != null
                                    ? !string.Equals(u.Asked, a, StringComparison.Ordinal)
                                    : c.Own.Contains(a)
                            )
                                continue;
                            var j = u.ToJson();
                            if (!string.Equals(a, u.Name, StringComparison.Ordinal))
                            {
                                j.Set("tag", u.Name);
                                j.Set("name", a);
                            }
                            tags.Add(j);
                        }
                    }
                }
                if (tags.Count == 0)
                    continue;
                var b = JsonValue.NewObject();
                b.Set("t", clock.Get("t") ?? JsonValue.Of(0));
                b.Set("time", clock.Get("time") ?? JsonValue.Of(""));
                b.Set("tags", tags);
                c.Sink.Send(Event("ctsim/tagsChanged", b));
            }
        }

        // ------------------------------------------------------------------ requests
        private void Handle(Client c, JsonValue req)
        {
            string cmd = req.Get("command")?.Str ?? "";
            var a = req.Get("arguments");
            if (a == null || a.Kind != JsonKind.Object)
                a = JsonValue.NewObject();
            try
            {
                switch (cmd)
                {
                    case "ctsim/hello":
                        Hello(c, req, a);
                        return;
                    case "ctsim/load":
                        Load(c, req, a);
                        return;
                    case "ctsim/options":
                        Options(c, req, a);
                        return;
                    case "ctsim/tags":
                        Tags(c, req, a);
                        return;
                    case "ctsim/subscribe":
                        Subscribe(c, req, a);
                        return;
                    case "ctsim/unsubscribe":
                        Unsubscribe(c, req, a);
                        return;
                    case "ctsim/setTag":
                        SetTag(c, req, a);
                        return;
                    case "ctsim/setAlarm":
                        SetAlarm(c, req, a);
                        return;
                    case "ctsim/lockTag":
                        LockTag(c, req, a);
                        return;
                    case "ctsim/setTagProperty":
                        SetTagProperty(c, req, a);
                        return;
                    case "ctsim/profile":
                        Profile(c, req, a);
                        return;
                    case "ctsim/user":
                        Ok(c.Sink, req, Simulation.Invoke(() => Simulation.SetUser(a)));
                        return;
                    case "ctsim/users":
                        Ok(c.Sink, req, Simulation.Invoke(() => Simulation.UsersJson()));
                        return;
                    case "ctsim/params":
                        Ok(c.Sink, req, Simulation.Invoke(() => Simulation.ParamsJson()));
                        return;
                    case "ctsim/usedTags":
                    {
                        int w = Window(a) ?? 0;
                        int from = (int)(a.Get("from")?.Long ?? 0);
                        Ok(c.Sink, req, Simulation.Invoke(() => Simulation.UsedTagsJson(w, from)));
                        return;
                    }
                    case "ctsim/setParam":
                    {
                        string sec = a.Get("section")?.Str ?? "",
                            nm = a.Get("name")?.Str ?? "",
                            val = a.Get("value")?.Str ?? "";
                        Ok(c.Sink, req, Simulation.Invoke(() => Simulation.SetParam(sec, nm, val)));
                        return;
                    }
                    case "ctsim/setEquipment":
                        SetEquipment(c, req, a);
                        return;
                    case "ctsim/alarms":
                        Alarms(c, req, a);
                        return;
                    case "ctsim/equipment":
                        EquipmentList(c, req, a);
                        return;
                    case "ctsim/call":
                        Call(c, req, a);
                        return;
                    case "ctsim/checkExpression":
                        CheckExpression(c, req, a);
                        return;
                    case "ctsim/clock":
                        Clock(c, req, a);
                        return;
                    case "ctsim/scenario":
                        ScenarioCmd(c, req, a);
                        return;
                    case "ctsim/reload":
                        Reload(c, req, a);
                        return;
                    case "ctsim/tasks":
                        Ok(c.Sink, req, TasksBody());
                        return;
                    case "ctsim/openPage":
                        OpenPage(c, req, a);
                        return;
                    case "ctsim/navigate":
                        Navigate(c, req, a);
                        return;
                    case "ctsim/closePage":
                        ClosePage(c, req, a);
                        return;
                    case "ctsim/pageState":
                        PageState(c, req, a);
                        return;
                    case "ctsim/pageVars":
                        Ok(
                            c.Sink,
                            req,
                            Simulation.Invoke(() => Simulation.Pages.PageVarsJson(Window(a) ?? 0))
                        );
                        return;
                    case "ctsim/setPageVar":
                        Ok(
                            c.Sink,
                            req,
                            Simulation.Invoke(() => Simulation.Pages.SetPageVar(Window(a) ?? 0, a))
                        );
                        return;
                    case "ctsim/setObjectProperty":
                        Ok(
                            c.Sink,
                            req,
                            Simulation.Invoke(() => Simulation.Pages.SetObjectProperty(Window(a) ?? 0, a))
                        );
                        return;
                    case "ctsim/pageModel":
                        PageModelCmd(c, req, a);
                        return;
                    case "ctsim/input":
                        Input(c, req, a);
                        return;
                    case "ctsim/windows":
                        Ok(c.Sink, req, Simulation.Invoke(() => Simulation.Pages.WindowsJson()));
                        return;
                    case "ctsim/answer":
                        Answer(c, req, a);
                        return;
                    case "ctsim/shutdown":
                        Ok(c.Sink, req, null);
                        ShutdownRequested?.Invoke(
                            "ctsim/shutdown from " + (c.ClientName.Length > 0 ? c.ClientName : c.Name)
                        );
                        return;
                }
                Fail(c.Sink, req, "unknown request " + Project.CitectText.Quote(cmd));
            }
            catch (ArgumentException e)
            {
                Fail(c.Sink, req, e.Message);
            }
            catch (FormatException e)
            {
                Fail(c.Sink, req, e.Message);
            }
            catch (FileNotFoundException e)
            {
                Fail(c.Sink, req, e.Message);
            }
            catch (ObjectDisposedException)
            {
                Fail(c.Sink, req, "the simulation has ended");
            }
            catch (InvalidOperationException e)
            {
                Fail(c.Sink, req, e.Message);
            }
            catch (NotSupportedException e)
            {
                Fail(c.Sink, req, e.Message);
            }
            catch (CallTextException e)
            {
                var b = JsonValue.NewObject();
                b.Set("error", JsonValue.Of(e.Code));
                Fail(c.Sink, req, e.Message, b);
            }
            catch (HardwareError e)
            {
                var b = JsonValue.NewObject();
                b.Set("error", JsonValue.Of(e.Code));
                b.Set("errorText", Errors.Title(e.Code));
                Fail(
                    c.Sink,
                    req,
                    "error " + e.Code.ToString(CultureInfo.InvariantCulture) + " " + Errors.Title(e.Code),
                    b
                );
            }
        }

        private static string Str(JsonValue a, string key, string def = "") =>
            a.Get(key) is JsonValue v && v.Kind == JsonKind.String ? v.Str : def;

        private static long Int(JsonValue a, string key, long def)
        {
            var v = a.Get(key);
            if (v == null || v.IsNull)
                return def;
            if (v.Kind == JsonKind.Int && v.FitsLong)
                return v.Long;
            if (v.IsNumber)
                return (long)v.Double;
            throw new ArgumentException(key + " must be a number");
        }

        private static bool Bool(JsonValue a, string key, bool def)
        {
            var v = a.Get(key);
            if (v == null || v.IsNull)
                return def;
            if (v.Kind == JsonKind.Bool)
                return v.Bool;
            throw new ArgumentException(key + " must be true or false");
        }

        private static List<string> Names(JsonValue a, string key)
        {
            var r = new List<string>();
            var v = a.Get(key);
            if (v == null || v.IsNull)
                return r;
            if (v.Kind == JsonKind.String)
            {
                r.Add(v.Str);
                return r;
            }
            if (v.Kind != JsonKind.Array)
                throw new ArgumentException(key + " must be a list of names");
            foreach (var x in v.Items)
                if (x.Kind == JsonKind.String)
                    r.Add(x.Str);
            return r;
        }

        private JsonValue ServerInfo()
        {
            var b = Simulation.Invoke(() => Simulation.Info());
            b.Set("server", "ctsim");
            b.Set("version", EngineInfo.Version);
            b.Set("protocolRevision", JsonValue.Of(EngineInfo.ProtocolRevision));
            b.Set("commit", EngineInfo.Commit);
            b.Set("buildTime", EngineInfo.BuildTime);
            b.Set("protocol", Protocol);
            b.Set("id", Id);
            b.Set("pid", JsonValue.Of(Discovery.CurrentPid));
            b.Set("mode", Mode);
            b.Set("pipe", Listener != null && Listener.Transport == Listener.Pipe ? Listener.Address : "");
            b.Set("transport", Listener?.Transport ?? "");
            b.Set("address", Listener?.Address ?? "");
            b.Set("clock", Simulation.Invoke(() => Simulation.ClockState()));
            b.Set("debugger", JsonValue.Of(DebuggerAttached));
            b.Set("clients", JsonValue.Of(ClientCount));
            var caps = JsonValue.NewObject();
            caps.Set("pages", JsonValue.True); // the page animator
            caps.Set("compile", JsonValue.True); // the source compiler
            caps.Set("scenarios", JsonValue.True);
            caps.Set("debug", JsonValue.Of(DapFactory != null));
            caps.Set("simulateUndefined", JsonValue.True); // ctsim/options
            caps.Set("windowTags", JsonValue.True); // ctsim/tags / subscribe {window}
            caps.Set("root", JsonValue.True); // hello / load {root}, rootReason, roots
            b.Set("user", Simulation.Invoke(() => Simulation.UserJson()));
            caps.Set("pageVars", JsonValue.True); // ctsim/pageVars / setPageVar, PAGE pageVars
            caps.Set("alarms", JsonValue.True); // ctsim/alarms, ctsim/equipment, ctsim/alarmsChanged
            caps.Set("dialogs", JsonValue.True); // hello {dialogs}, ctsim/dialog, ctsim/answer
            caps.Set("models", JsonValue.True); // hello {models}, ctsim/modelWanted
            caps.Set("displayText", JsonValue.True); // localised display strings, fontInfo, array cells
            caps.Set("navigate", JsonValue.True); // load {startPage}, ctsim/navigate
            caps.Set("controls", JsonValue.True); // PAGE controls, ctsim/setObjectProperty
            caps.Set("checkExpression", JsonValue.True); // ctsim/checkExpression
            caps.Set("drawnButtons", JsonValue.True); // ctsim/input presses buttons Cicode drew
            caps.Set("alarmListLayout", JsonValue.True); // alarm_list drawHeader, rowHeight, width, height, titles
            caps.Set("trendHistory", JsonValue.True); // ctsim/options seedTrendHistory
            caps.Set("windowSerials", JsonValue.True); // PAGE serials grow per window, across page displays
            caps.Set("foregroundBreak", JsonValue.True); // ctsim/options foregroundBreak, ctsim/debugPaused
            caps.Set("tagProperties", JsonValue.True); // ctsim/setTagProperty, tag entry overrides
            caps.Set("profile", JsonValue.True); // ctsim/profile; scenario generator square
            caps.Set("profileTree", JsonValue.True); // ctsim/profile {tree: true}: the call tree
            caps.Set("history", JsonValue.True); // IDebugTarget variable history, time travel, data breakpoints
            caps.Set("outside", JsonValue.True); // ctsim/options dll / sql + whitelists, ctsim/dialog kind permission
            caps.Set("regexFilter", JsonValue.True); // ctsim/tags, alarms, equipment {regex: true}
            b.Set("capabilities", caps);
            b.Set("options", OptionsBody());
            return b;
        }

        private JsonValue OptionsBody()
        {
            var o = JsonValue.NewObject();
            o.Set("simulateUndefined", JsonValue.Of(Simulation.Invoke(() => Simulation.SimulateUndefined)));
            o.Set("seedTrendHistory", JsonValue.Of(Simulation.Invoke(() => Simulation.SeedTrendHistory)));
            o.Set("foregroundBreak", JsonValue.Of(Simulation.Invoke(() => Simulation.ForegroundBreak)));
            o.Set(
                "foregroundErrorBreak",
                JsonValue.Of(Simulation.Invoke(() => Simulation.ForegroundErrorBreak))
            );
            var outside = Simulation.Host.Outside;
            o.Set("dll", JsonValue.Of(Builtins.OutsideAccess.Word(outside.Dll)));
            o.Set("sql", JsonValue.Of(Builtins.OutsideAccess.Word(outside.Sql)));
            o.Set("dllAllowed", JsonValue.Array(outside.DllAllowed().ConvertAll(s => JsonValue.Of(s))));
            o.Set("sqlAllowed", JsonValue.Array(outside.SqlAllowed().ConvertAll(s => JsonValue.Of(s))));
            o.Set("dllTimeout", JsonValue.Of((long)(outside.DllTimeoutMs / 1000)));
            return o;
        }

        /// <summary>ctsim/options {simulateUndefined?, seedTrendHistory?, foregroundBreak?, foregroundErrorBreak?}: the server's simulator-only options (SPEC
        /// 7.4), for every client of the simulation; the body = the options now. A change goes to
        /// the viewers as ctsim/optionsChanged.</summary>
        private void Options(Client c, JsonValue req, JsonValue a)
        {
            bool changed = false;
            var su = a.Get("simulateUndefined");
            if (su != null && !su.IsNull)
            {
                if (su.Kind != JsonKind.Bool)
                    throw new ArgumentException("simulateUndefined must be true or false");
                changed = Simulation.Invoke(() =>
                {
                    bool was = Simulation.SimulateUndefined;
                    Simulation.SimulateUndefined = su.Bool;
                    return was != su.Bool;
                });
            }
            var st = a.Get("seedTrendHistory");
            if (st != null && !st.IsNull)
            {
                if (st.Kind != JsonKind.Bool)
                    throw new ArgumentException("seedTrendHistory must be true or false");
                changed |= Simulation.Invoke(() =>
                {
                    bool was = Simulation.SeedTrendHistory;
                    Simulation.SeedTrendHistory = st.Bool;
                    return was != st.Bool;
                });
            }
            var fb = a.Get("foregroundBreak");
            if (fb != null && !fb.IsNull)
            {
                if (fb.Kind != JsonKind.Bool)
                    throw new ArgumentException("foregroundBreak must be true or false");
                changed |= Simulation.Invoke(() =>
                {
                    bool was = Simulation.ForegroundBreak;
                    Simulation.ForegroundBreak = fb.Bool;
                    return was != fb.Bool;
                });
            }
            var fe = a.Get("foregroundErrorBreak");
            if (fe != null && !fe.IsNull)
            {
                if (fe.Kind != JsonKind.Bool)
                    throw new ArgumentException("foregroundErrorBreak must be true or false");
                changed |= Simulation.Invoke(() =>
                {
                    bool was = Simulation.ForegroundErrorBreak;
                    Simulation.ForegroundErrorBreak = fe.Bool;
                    return was != fe.Bool;
                });
            }
            changed |= OutsideOptions(a);
            var body = OptionsBody();
            Ok(c.Sink, req, body);
            if (changed)
                Broadcast("options", "ctsim/optionsChanged", body);
        }

        /// <summary>ctsim/options: DLL and SQL access settings.</summary>
        private bool OutsideOptions(JsonValue a)
        {
            var outside = Simulation.Host.Outside;
            // every key is read before anything changes: a bad one fails the request whole
            var modes = new Builtins.OutsideMode?[2];
            var lists = new List<string>?[2];
            foreach (bool dll in new[] { true, false })
            {
                string key = dll ? "dll" : "sql";
                var m = a.Get(key);
                if (m != null && !m.IsNull)
                {
                    if (m.Kind != JsonKind.String || !Builtins.OutsideAccess.TryParse(m.Str, out var mode))
                        throw new ArgumentException(key + " must be off, ask or on");
                    modes[dll ? 0 : 1] = mode;
                }
                var l = a.Get(key + "Allowed");
                if (l != null && !l.IsNull)
                {
                    if (l.Kind != JsonKind.Array)
                        throw new ArgumentException(key + "Allowed must be a list of texts");
                    var items = new List<string>();
                    foreach (JsonValue v in l.Items)
                    {
                        if (v.Kind != JsonKind.String)
                            throw new ArgumentException(key + "Allowed must be a list of texts");
                        items.Add(v.Str);
                    }
                    lists[dll ? 0 : 1] = items;
                }
            }
            int? timeout = null;
            var to = a.Get("dllTimeout");
            if (to != null && !to.IsNull)
            {
                if (to.Kind != JsonKind.Int || to.Long < 0 || to.Long > 86400)
                    throw new ArgumentException("dllTimeout must be whole seconds, 0 (no limit) to 86400");
                timeout = (int)to.Long;
            }
            bool changed = false;
            foreach (bool dll in new[] { true, false })
            {
                var mode = modes[dll ? 0 : 1];
                if (mode != null && (dll ? outside.Dll : outside.Sql) != mode.Value)
                {
                    changed = true;
                    if (dll)
                        outside.Dll = mode.Value;
                    else
                        outside.Sql = mode.Value;
                    Simulation.Note(
                        "info",
                        (dll ? "DLLs: " : "SQL databases: ") + Builtins.OutsideAccess.Word(mode.Value)
                    );
                    if (mode == Builtins.OutsideMode.Off)
                        Simulation.Invoke(() =>
                        {
                            if (dll)
                                Simulation.Host.ShutdownDll();
                            else
                                Simulation.Host.ShutdownSql();
                            return 0;
                        });
                }
                var items = lists[dll ? 0 : 1];
                if (items != null)
                    changed |= outside.SetAllowed(dll, items);
            }
            if (timeout != null && outside.DllTimeoutMs != timeout.Value * 1000)
            {
                changed = true;
                outside.DllTimeoutMs = timeout.Value * 1000;
                Simulation.Invoke(() =>
                {
                    Simulation.Host.DllData.ApplyTimeout(outside.DllTimeoutMs);
                    return 0;
                });
            }
            return changed;
        }

        /// <summary>A request's {root} (a compiled project's name) is the root this server runs
        /// as, or absent: a server of the same project with another root is another
        /// simulation. False = failed.</summary>
        private bool RootMatches(Client c, JsonValue req, JsonValue a)
        {
            string root = Str(a, "root").Trim();
            if (root.Length == 0 || string.Equals(root, Simulation.Root, StringComparison.OrdinalIgnoreCase))
                return true;
            Fail(
                c.Sink,
                req,
                "this server runs "
                    + Simulation.ProjectName
                    + " as part of "
                    + Simulation.Root
                    + ": start a server with --root "
                    + root
            );
            return false;
        }

        private void Hello(Client c, JsonValue req, JsonValue a)
        {
            if (!RootMatches(c, req, a))
                return;
            var ev = a.Get("events");
            HashSet<string>? events = null;
            if (ev != null && ev.Kind == JsonKind.Array)
            {
                events = new HashSet<string>(StringComparer.Ordinal);
                foreach (var x in ev.Items)
                    if (x.Kind == JsonKind.String)
                        events.Add(x.Str);
            }
            // published under the lock Broadcast reads them in (the event thread)
            var dv = a.Get("dialogs");
            var mv = a.Get("models");
            lock (_lock)
            {
                c.ClientName = Str(a, "client", c.Name);
                c.Events = events;
                c.Viewer = true;
                c.Dialogs = dv != null && dv.Kind == JsonKind.Bool && dv.Bool;
                c.Models = mv != null && mv.Kind == JsonKind.Bool && mv.Bool;
            }
            Ok(c.Sink, req, ServerInfo());
            // a viewer asks for its page's tags right after it opens the page: the tag listing
            // (seconds on a large project the first time) is built meanwhile, not then
            if (Volatile.Read(ref _listing) == null && Interlocked.Exchange(ref _listingWarm, 1) == 0)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Listing();
                    }
                    catch (Exception)
                    {
                        Interlocked.Exchange(ref _listingWarm, 0);
                    }
                });
        }

        private int _listingWarm;

        private void Load(Client c, JsonValue req, JsonValue a)
        {
            string proj = Str(a, "project");
            if (proj.Length > 0)
            {
                string want,
                    have;
                try
                {
                    want = Path.GetFullPath(proj).TrimEnd('\\', '/');
                }
                catch (Exception)
                {
                    want = proj;
                }
                have = Simulation.ProjectDir;
                bool same =
                    string.Equals(want, have, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(proj, Simulation.ProjectName, StringComparison.OrdinalIgnoreCase);
                if (!same)
                {
                    Fail(c.Sink, req, "this server simulates " + have + ": start a server for " + proj);
                    return;
                }
            }
            if (!RootMatches(c, req, a))
                return;
            var su = a.Get("simulateUndefined");
            if (su != null && !su.IsNull)
            {
                if (su.Kind != JsonKind.Bool)
                    throw new ArgumentException("simulateUndefined must be true or false");
                bool changed = Simulation.Invoke(() =>
                {
                    bool was = Simulation.SimulateUndefined;
                    Simulation.SimulateUndefined = su.Bool;
                    return was != su.Bool;
                });
                if (changed)
                    Broadcast("options", "ctsim/optionsChanged", OptionsBody());
            }
            var body = ServerInfo();
            var notes = JsonValue.NewArray();
            string startup = Str(a, "startup");
            bool auto = string.Equals(startup.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
            // the startup code runs on a fast clock until it settles
            bool fast = Bool(a, "fastStartup", true);
            Simulation.Invoke(() =>
            {
                Simulation.Boot.Fast = fast;
                return 0;
            });
            if (!auto && startup.Length > 0)
            {
                var o = Simulation.Invoke(() =>
                    Simulation.StartCall(startup.IndexOf('(') >= 0 ? startup : startup + "()", null).Handle
                );
                body.Set("startupTask", JsonValue.Of(o));
            }
            var sc = a.Get("scenario");
            if (sc != null && !sc.IsNull)
            {
                Simulation.Invoke(() =>
                {
                    if (sc.Kind == JsonKind.String)
                        Simulation.LoadScenario(null, sc.Str);
                    else
                        Simulation.LoadScenario(sc, null);
                    Simulation.RunScenario();
                });
                body.Set("scenario", "running");
            }
            // the runtime's startup page in window 0 (before the load's own page, which then
            // opens in a free window, and before 'startup auto', which runs once it started)
            if (Bool(a, "startPage", false))
                body.Set("startPage", Simulation.Invoke(() => Simulation.Boot.ShowStartupPage()));
            string pageName = Str(a, "page");
            if (pageName.Length > 0)
            {
                var pn = new List<string>();
                var open = OpenBody(pageName, null, null, null, null, pn);
                body.Set("page", open);
                if (open["error"].Long != 0)
                    notes.Add(
                        JsonValue.Of(
                            "page "
                                + pageName
                                + ": error "
                                + open["error"].Long.ToString(CultureInfo.InvariantCulture)
                                + " "
                                + (open.Get("errorText")?.Str ?? "")
                        )
                    );
            }
            if (auto)
            {
                // the runtime's start (RuntimeStartup), after the load's own page (it runs once
                // window 0 shows one): its tasks, or pending until then
                var sb = Simulation.Invoke(() => Simulation.Boot.Request());
                foreach (var kv in sb.Pairs)
                    body.Set(kv.Key, kv.Value);
            }
            body.Set("loadNotes", notes);
            Ok(c.Sink, req, body);
        }

        private TagListing Listing()
        {
            var l = Volatile.Read(ref _listing);
            if (l != null)
                return l;
            // built once: two first requests at the same time do not each build one; on the
            // request's thread, not the simulation's (a build there holds every task back). The
            // lazily made table and local lists it reads are made on the simulation thread first;
            // the name indexes are locked (VarTable.Index)
            lock (_listingLock)
            {
                l = Volatile.Read(ref _listing);
                if (l != null)
                    return l;
                var db = Simulation.Invoke(() =>
                {
                    var tags = Simulation.World.Tags;
                    tags.Tables();
                    tags.Locals();
                    return tags;
                });
                ListingBuiltOnSimThread = Simulation.IsSimThread;
                l = new TagListing(db);
                Volatile.Write(ref _listing, l);
                return l;
            }
        }

        private readonly object _listingLock = new object();

        /// <summary>Test hook: the tag listing was built on the simulation thread.</summary>
        internal bool ListingBuiltOnSimThread;

        private void Tags(Client c, JsonValue req, JsonValue a)
        {
            var names = Names(a, "names");
            string page = Str(a, "page");
            int? window = Window(a);
            var b = JsonValue.NewObject();
            if (names.Count > 0 || page.Length > 0 || window != null)
            {
                PageRefs? refs = null;
                if (page.Length > 0 || window != null)
                {
                    refs = PageRefsFor(page, window, out string why);
                    if (refs == null)
                    {
                        Fail(c.Sink, req, why);
                        return;
                    }
                }
                var listing = Listing();
                var arr = JsonValue.NewArray();
                var unknown = JsonValue.NewArray();
                int undefined = 0;
                Simulation.Invoke(() =>
                {
                    var cols = new Dictionary<World.VarTable, Dictionary<string, Func<int, string>>>();
                    var seen = new Dictionary<string, JsonValue>(StringComparer.OrdinalIgnoreCase);
                    void Add(string n, bool fromPage, string? via, string? reference)
                    {
                        if (seen.TryGetValue(n, out var had))
                        {
                            if (via != null && had.Get("via") == null)
                            {
                                had.Set("via", via);
                                had.Set("ref", reference ?? "");
                            }
                            return;
                        }
                        var e = listing.EntryFor(n, cols);
                        if (e == null)
                        {
                            var sim = Simulation.World.Tags.SimulateUndefined
                                ? Simulation.World.Tags.SimulatedTag(n)
                                : null;
                            if (sim == null)
                                unknown.Add(JsonValue.Of(n));
                            // a name asked for: only a simulated tag is an entry
                            if (!fromPage && sim == null)
                                return;
                            e = UndefinedEntry(n, sim, refs);
                            if (fromPage)
                                undefined++;
                        }
                        if (via != null)
                        {
                            e.Set("via", via);
                            e.Set("ref", reference ?? "");
                        }
                        seen[n] = e;
                        arr.Add(e);
                    }
                    foreach (string n in names)
                        Add(n, false, null, null);
                    if (refs != null)
                    {
                        foreach (string n in refs.Plain)
                            Add(n, true, null, null);
                        foreach (var (reference, via, target) in refs.Resolved)
                            Add(target, true, via, reference);
                    }
                });
                b.Set("total", JsonValue.Of(arr.Count));
                b.Set("start", JsonValue.Of(0));
                b.Set("count", JsonValue.Of(arr.Count));
                b.Set("tags", arr);
                b.Set("unknown", unknown);
                if (refs != null)
                {
                    b.Set("undefined", JsonValue.Of(undefined));
                    b.Set("page", refs.Page);
                    if (refs.Window != null)
                        b.Set("window", JsonValue.Of(refs.Window.Value));
                    var lits = JsonValue.NewArray();
                    foreach (var (reference, via, value) in refs.Literals)
                    {
                        var o = JsonValue.NewObject();
                        o.Set("ref", reference);
                        o.Set("via", via);
                        o.Set("value", value);
                        lits.Add(o);
                    }
                    b.Set("literals", lits);
                    var unres = JsonValue.NewArray();
                    foreach (var (reference, via, error) in refs.Unresolved)
                    {
                        var o = JsonValue.NewObject();
                        o.Set("ref", reference);
                        o.Set("via", via);
                        o.Set("error", JsonValue.Of(error));
                        o.Set(
                            "errorText",
                            refs.Window == null
                                ? "no window: ask with the window that shows the page"
                                : "not associated"
                        );
                        unres.Add(o);
                    }
                    b.Set("unresolved", unres);
                    b.Set(
                        "simulateUndefined",
                        JsonValue.Of(Simulation.Invoke(() => Simulation.SimulateUndefined))
                    );
                }
                Ok(c.Sink, req, b);
                return;
            }
            long start = Math.Max(0, Int(a, "start", 0));
            long count = Int(a, "count", 500);
            if (count <= 0 || count > 5000)
                count = Math.Min(5000, Math.Max(1, count));
            string filter = Str(a, "filter");
            var eqv = a.Get("equipment");
            string? equipment = eqv != null && eqv.Kind == JsonKind.String ? eqv.Str : null;
            bool groups = Bool(a, "groups", false);
            if (!FilterOf(c, req, a, filter, out var flt))
                return;
            var lst = Listing();
            try
            {
                Simulation.Invoke(() =>
                {
                    var pos = lst.Matching(flt, equipment);
                    var arr = JsonValue.NewArray();
                    var cols = new Dictionary<World.VarTable, Dictionary<string, Func<int, string>>>();
                    for (long i = start; i < pos.Count && i < start + count; i++)
                        arr.Add(lst.Entry(pos[(int)i], cols));
                    b.Set("total", JsonValue.Of(pos.Count));
                    b.Set("start", JsonValue.Of(start));
                    b.Set("count", JsonValue.Of(arr.Count));
                    b.Set("tags", arr);
                    if (groups)
                    {
                        var g = JsonValue.NewArray();
                        foreach (var (eq, n) in lst.Groups(pos))
                        {
                            var o = JsonValue.NewObject();
                            o.Set("equipment", eq);
                            o.Set("count", JsonValue.Of(n));
                            g.Add(o);
                        }
                        b.Set("groups", g);
                    }
                    b.Set("all", JsonValue.Of(lst.Count));
                });
            }
            catch (RegexMatchTimeoutException)
            {
                Fail(c.Sink, req, RegexTooSlow);
                return;
            }
            Ok(c.Sink, req, b);
        }

        private void Subscribe(Client c, JsonValue req, JsonValue a)
        {
            var names = Names(a, "names");
            string page = Str(a, "page");
            bool replace = Bool(a, "replace", false);
            int? window = Window(a);
            var b = JsonValue.NewObject();
            if (page.Length > 0 || window != null)
            {
                var refs = PageRefsFor(page, window, out string why);
                if (refs == null)
                {
                    Fail(c.Sink, req, why);
                    return;
                }
                string pname = page.Length > 0 ? page : refs.Page;
                string? rdb = Simulation.Invoke(() => PageTags.FindRdb(Simulation.World.Unit, pname));
                var pn = refs.All();
                names.AddRange(pn);
                var po = JsonValue.NewObject();
                po.Set("name", pname);
                po.Set("rdb", rdb ?? "");
                po.Set("tags", JsonValue.Of(pn.Count));
                if (refs.Window != null)
                {
                    po.Set("window", JsonValue.Of(refs.Window.Value));
                    po.Set("viaAssociations", JsonValue.Of(refs.Resolved.Count));
                    po.Set("unresolved", JsonValue.Of(refs.Unresolved.Count));
                }
                b.Set("page", po);
            }
            var values = JsonValue.NewArray();
            var unknown = JsonValue.NewArray();
            var keys = Simulation.Invoke(() =>
            {
                var r = new List<(string asked, string key, TagUpdate? u, bool own)>();
                foreach (string n in names)
                {
                    var u = Simulation.ReadTag(n, hover: true);
                    r.Add(
                        (
                            n,
                            u.Defined ? u.Name : "",
                            u.Defined ? u : null,
                            u.Defined && Simulation.IsOwnRead(n)
                        )
                    );
                }
                return r;
            });
            var watch = new List<string>();
            var unwatch = new List<string>();
            lock (_lock)
            {
                if (replace)
                {
                    unwatch.AddRange(c.Own);
                    c.Own.Clear();
                    c.Subs.Clear();
                    c.Pending.Clear();
                }
                foreach (var (asked, key, u, own) in keys)
                {
                    if (u == null)
                    {
                        unknown.Add(JsonValue.Of(asked));
                        // an undefined name: subscribed when setTag makes a simulated tag for it
                        if (World.TagDb.SimulatableName(Project.CitectText.Trim(asked)))
                            c.Pending.Add(Project.CitectText.Trim(asked));
                        continue;
                    }
                    if (!c.Subs.TryGetValue(key, out var set))
                        c.Subs[key] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(asked);
                    if (own && c.Own.Add(asked))
                        watch.Add(asked);
                    var j = u.ToJson();
                    if (!string.Equals(asked, u.Name, StringComparison.Ordinal))
                    {
                        j.Set("tag", u.Name);
                        j.Set("name", asked);
                    }
                    values.Add(j);
                }
                b.Set("subscribed", JsonValue.Of(Count(c)));
            }
            // before the response: from it on, every batch has the own-read names' entries
            Simulation.Invoke(() =>
            {
                foreach (string n in unwatch)
                    Simulation.Watch(n, false);
                foreach (string n in watch)
                    Simulation.Watch(n, true);
            });
            b.Set("tags", values);
            b.Set("unknown", unknown);
            Ok(c.Sink, req, b);
        }

        /// <summary>Give back the watches of own-read names (a client left or unsubscribed).</summary>
        private void Unwatch(List<string> names)
        {
            if (names.Count == 0)
                return;
            try
            {
                Simulation.Post(() =>
                {
                    foreach (string n in names)
                        Simulation.Watch(n, false);
                });
            }
            catch (ObjectDisposedException)
            {
                // the simulation has ended: nothing is watched any more
            }
        }

        private static int Count(Client c)
        {
            int n = 0;
            foreach (var s in c.Subs.Values)
                n += s.Count;
            return n;
        }

        private void Unsubscribe(Client c, JsonValue req, JsonValue a)
        {
            var names = Names(a, "names");
            bool all = Bool(a, "all", names.Count == 0);
            var keys = all
                ? new List<string>()
                : Simulation.Invoke(() => names.ConvertAll(n => Simulation.ReadTag(n).Name));
            var gone = new List<string>();
            var b = JsonValue.NewObject();
            lock (_lock)
            {
                if (all)
                {
                    gone.AddRange(c.Own);
                    c.Own.Clear();
                    c.Subs.Clear();
                    c.Pending.Clear();
                }
                else
                    for (int i = 0; i < names.Count; i++)
                    {
                        c.Pending.Remove(Project.CitectText.Trim(names[i]));
                        if (!c.Subs.TryGetValue(keys[i], out var set))
                            continue;
                        if (set.Remove(names[i]) && c.Own.Remove(names[i]))
                            gone.Add(names[i]);
                        if (set.Count == 0)
                            c.Subs.Remove(keys[i]);
                    }
                b.Set("subscribed", JsonValue.Of(Count(c)));
            }
            Unwatch(gone);
            Ok(c.Sink, req, b);
        }

        /// <summary>ctsim/setTagProperty: override a tag's definition field (simulator only).</summary>
        private void SetTagProperty(Client c, JsonValue req, JsonValue a)
        {
            string name = Str(a, "name");
            if (name.Length == 0)
                throw new ArgumentException("setTagProperty needs a name");
            string field = Str(a, "field");
            var v = a.Get("value");
            string? value =
                v == null || v.IsNull ? null
                : v.Kind == JsonKind.String ? v.Str
                : v.Text;
            var listing = Listing();
            var (d, entry) = Simulation.Invoke(() =>
            {
                var def = Simulation.World.Tags.SetProperty(name, field.Length > 0 ? field : null, value);
                if (def == null)
                    return ((World.TagDef?)null, (JsonValue?)null);
                string what =
                    field.Length == 0 ? "every property back to the project's"
                    : value == null
                        ? field.ToUpperInvariant()
                            + " back to the project's ("
                            + def.Configured(field.Trim().ToUpperInvariant())
                            + ")"
                    : field.ToUpperInvariant()
                        + " = '"
                        + value
                        + "' (the project's: '"
                        + def.Configured(field.Trim().ToUpperInvariant())
                        + "')";
                Simulation.Note("info", def.Name + ": " + what + " (a simulator override)");
                var cols = new Dictionary<World.VarTable, Dictionary<string, Func<int, string>>>();
                return (def, listing.EntryFor(def.Name, cols));
            });
            if (d == null)
                throw new ArgumentException("Tag not found: " + name);
            Ok(c.Sink, req, entry ?? JsonValue.NewObject());
            var b = JsonValue.NewObject();
            var names = JsonValue.NewArray();
            names.Add(JsonValue.Of(d.Name));
            b.Set("names", names);
            Broadcast("options", "ctsim/tagDefinitionsChanged", b);
        }

        /// <summary>ctsim/profile {action: "start" | "stop" | "reset" | "get" (default), top? =
        /// 100}: time the page expressions, scans and task slices; the body is the
        /// report (Sim.Profiler.Report).</summary>
        private void Profile(Client c, JsonValue req, JsonValue a)
        {
            string action = Str(a, "action").Trim().ToLowerInvariant();
            if (action.Length == 0)
                action = "get";
            if (action != "start" && action != "stop" && action != "reset" && action != "get")
                throw new ArgumentException("profile action must be start, stop, reset or get: " + action);
            var tv = a.Get("top");
            int top = tv != null && tv.IsNumber ? Math.Max(1, (int)tv.ToNumber()) : 100;
            // the call tree too: each call path with its time
            var tr = a.Get("tree");
            bool tree = tr != null && tr.Kind == JsonKind.Bool && tr.Bool;
            var body = Simulation.Invoke(() =>
            {
                if (action == "start")
                    Simulation.StartProfile();
                else if (action == "stop")
                    Simulation.StopProfile();
                else if (action == "reset")
                    Simulation.Profile.Reset();
                return Simulation.Profile.Report(top, tree);
            });
            Ok(c.Sink, req, body);
        }

        private void LockTag(Client c, JsonValue req, JsonValue a)
        {
            string name = Str(a, "name");
            if (name.Length == 0)
                throw new ArgumentException("lockTag needs a name");
            var lv = a.Get("locked");
            bool locked = lv == null || lv.Kind != JsonKind.Bool || lv.Bool;
            var (err, u) = Simulation.Invoke(() =>
                (Simulation.World.Tags.SetLock(name, locked), Simulation.ReadTag(name))
            );
            if (err != 0)
                throw new ArgumentException(Errors.Text(err) + ": " + name);
            var b = JsonValue.NewObject();
            b.Set("name", u.Name);
            b.Set("locked", JsonValue.Of(locked));
            b.Set("value", u.Value);
            Ok(c.Sink, req, b);
        }

        private void SetTag(Client c, JsonValue req, JsonValue a)
        {
            string name = Str(a, "name");
            if (name.Length == 0)
                throw new ArgumentException("setTag needs a name");
            var value = a.Get("value");
            var quality = a.Get("quality");
            string element = Str(a, "element");
            var iv = a.Get("index");
            int? index = iv != null && iv.IsNumber ? (int?)(int)iv.Double : null;
            string type = Str(a, "type");
            if (
                type.Length > 0
                && World.TagTypes.TypeCode(type) == Types.Int16
                && !string.Equals(type.Trim(), "INT", StringComparison.OrdinalIgnoreCase)
            )
                throw new ArgumentException(
                    "setTag type must be a tag type (DIGITAL, INT, LONG, REAL, STRING ...): "
                        + Project.CitectText.Quote(type)
                );
            var (err, u, made) = Simulation.Invoke(() =>
            {
                var db = Simulation.World.Tags;
                bool before = db.SimulatedTag(name) != null;
                int e = Simulation.SetTag(
                    name,
                    value,
                    quality,
                    element,
                    index,
                    type.Length > 0 ? type : null
                );
                bool now = e == 0 && db.SimulateUndefined && !before && db.SimulatedTag(name) != null;
                return (e, Simulation.ReadTag(name), now);
            });
            if (made)
            {
                // the clients that subscribed the undefined name get the simulated tag
                lock (_lock)
                    foreach (var other in _clients)
                    {
                        string asked = Project.CitectText.Trim(name);
                        var hit = new List<string>();
                        foreach (string p in other.Pending)
                            if (string.Equals(p, asked, StringComparison.OrdinalIgnoreCase))
                                hit.Add(p);
                        foreach (string p in hit)
                        {
                            other.Pending.Remove(p);
                            if (!other.Subs.TryGetValue(u.Name, out var set))
                                other.Subs[u.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(p);
                        }
                    }
            }
            var b = JsonValue.NewObject();
            b.Set("error", JsonValue.Of(err));
            if (err != 0)
            {
                b.Set("errorText", Errors.Title(err));
                Fail(
                    c.Sink,
                    req,
                    "setTag "
                        + name
                        + ": error "
                        + err.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + Errors.Title(err),
                    b
                );
                return;
            }
            b.Set("tag", u.ToJson());
            Ok(c.Sink, req, b);
        }

        /// <summary>ctsim/setAlarm: set an alarm's state on the alarm server.</summary>
        private void SetAlarm(Client c, JsonValue req, JsonValue a)
        {
            string name = Str(a, "name");
            if (name.Length == 0)
                throw new ArgumentException("setAlarm needs a name");
            var spec = a.Get("state") ?? throw new ArgumentException("setAlarm needs a state");
            var (err, alarm) = Simulation.Invoke(() =>
            {
                var w = Simulation.World;
                var def = w.Alarms.Get(name);
                if (def == null)
                    return (424, (JsonValue?)null);
                var set = CicodeSim.World.AlarmSet.FromJson(spec, w.Clock); // FormatException: a request error
                int e = w.Alarms.SetState(def.Tag, set, "viewer");
                return (e, e == 0 ? CicodeSim.World.LiveState.AlarmJson(w, def) : null);
            });
            var b = JsonValue.NewObject();
            b.Set("error", JsonValue.Of(err));
            if (err != 0)
            {
                b.Set("errorText", Errors.Title(err));
                Fail(
                    c.Sink,
                    req,
                    "setAlarm "
                        + name
                        + ": error "
                        + err.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + Errors.Title(err),
                    b
                );
                return;
            }
            b.Set("alarm", alarm ?? JsonValue.Null);
            Ok(c.Sink, req, b);
        }

        /// <summary>ctsim/setEquipment: set an equipment's runtime fields.</summary>
        private void SetEquipment(Client c, JsonValue req, JsonValue a)
        {
            string name = Str(a, "name");
            if (name.Length == 0)
                throw new ArgumentException("setEquipment needs a name");
            var fields = a.Get("fields");
            if (fields == null || fields.Kind != JsonKind.Object)
                throw new ArgumentException("setEquipment needs fields (an object)");
            var (err, why, now) = Simulation.Invoke(() =>
            {
                var w = Simulation.World;
                var e = w.Equipment.Get(name);
                if (e == null)
                    return (289, "", (JsonValue?)null);
                foreach (var f in fields.Pairs)
                {
                    if (!CicodeSim.World.EquipDb.IsRuntimeField(f.Key))
                        return (
                            274,
                            f.Key
                                + " is no runtime field ("
                                + string.Join(", ", CicodeSim.World.EquipDb.RuntimeFieldNames)
                                + ")",
                            null
                        );
                    if (f.Value.Kind == JsonKind.Object || f.Value.Kind == JsonKind.Array || f.Value.IsNull)
                        return (274, f.Key + ": a value is a text or a number", null);
                }
                foreach (var f in fields.Pairs)
                    w.Equipment.SetRuntime(e, f.Key, f.Value.Text);
                var o = JsonValue.NewObject();
                foreach (var (f, v) in w.Equipment.RuntimeOf(e))
                    o.Set(f, v);
                return (0, "", o);
            });
            var b = JsonValue.NewObject();
            b.Set("error", JsonValue.Of(err));
            if (err != 0)
            {
                b.Set("errorText", Errors.Title(err));
                Fail(
                    c.Sink,
                    req,
                    "setEquipment "
                        + name
                        + ": error "
                        + err.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + Errors.Title(err)
                        + (why.Length > 0 ? " (" + why + ")" : ""),
                    b
                );
                return;
            }
            b.Set("name", name);
            b.Set("fields", now ?? JsonValue.Null);
            Ok(c.Sink, req, b);
        }

        /// <summary>ctsim/checkExpression {text, kind? = "expression" | "command", project?}: the
        /// page field compiled as the page compiler would, nothing run: {ok,
        /// errors: [{code, name, message, column}]}. An editor checks what is typed in a sheet.</summary>
        private void CheckExpression(Client c, JsonValue req, JsonValue a)
        {
            string text = Str(a, "text");
            string kind = Str(a, "kind");
            string project = Str(a, "project");
            if (kind.Length == 0)
                kind = "expression";
            if (kind != "expression" && kind != "command")
                throw new ArgumentException("kind must be expression or command");
            var body = Simulation.Invoke(() => Simulation.CheckField(text, kind, project));
            Ok(c.Sink, req, body);
        }

        private void Call(Client c, JsonValue req, JsonValue a)
        {
            string expr = Str(a, "expr");
            bool wait = Bool(a, "wait", true);
            long timeoutMs = Int(a, "timeoutMs", 0);
            // the display window the task runs in (CtAPI ctCicode's hWin, PROTOCOL.md): absent = 0
            int window = Window(a) ?? -1;
            var sink = c.Sink;
            int answered = 0;
            void Answer(CallOutcome o)
            {
                if (Interlocked.Exchange(ref answered, 1) != 0)
                    return;
                if (o.State == "halt" && o.NotSimulated.Length == 0 && o.Error != 0)
                    Fail(
                        sink,
                        req,
                        "the call halted: error "
                            + o.Error.ToString(CultureInfo.InvariantCulture)
                            + " "
                            + o.ErrorText
                            + (
                                o.HaltReason.Length > 0
                                && o.HaltReason
                                    != "hardware error " + o.Error.ToString(CultureInfo.InvariantCulture)
                                    ? " (" + o.HaltReason + ")"
                                    : ""
                            ),
                        o.ToJson()
                    );
                else
                    Ok(sink, req, o.ToJson());
            }
            Simulation.Post(() =>
            {
                CicodeTask task;
                try
                {
                    task = Simulation.StartCall(
                        expr,
                        wait ? Answer : (Action<CallOutcome>?)null,
                        window: window
                    );
                }
                catch (CallTextException e)
                {
                    var b = JsonValue.NewObject();
                    b.Set("error", JsonValue.Of(e.Code));
                    b.Set("errorText", Errors.Title(e.Code));
                    Fail(sink, req, e.Message, b);
                    return;
                }
                catch (HardwareError e)
                {
                    // 'Out of Cicode threads' from the scheduler: answered like a refused call
                    var b = JsonValue.NewObject();
                    b.Set("error", JsonValue.Of(e.Code));
                    b.Set("errorText", Errors.Title(e.Code));
                    Fail(
                        sink,
                        req,
                        "the call cannot start: error "
                            + e.Code.ToString(CultureInfo.InvariantCulture)
                            + " "
                            + Errors.Title(e.Code),
                        b
                    );
                    return;
                }
                catch (Exception e)
                {
                    // the request must get its response (the command loop would only log it)
                    Fail(sink, req, "the call cannot start: " + e.GetType().Name + ": " + e.Message);
                    return;
                }
                if (!wait)
                {
                    Answer(Simulation.Outcome(task));
                    return;
                }
                if (timeoutMs > 0)
                {
                    var t = task;
                    System.Threading.Timer? timer = null;
                    timer = new System.Threading.Timer(
                        _ =>
                        {
                            timer?.Dispose();
                            try
                            {
                                Simulation.Post(() =>
                                {
                                    var o = Simulation.Outcome(t);
                                    o.TimedOut = !t.Done;
                                    Answer(o);
                                });
                            }
                            catch (ObjectDisposedException) { }
                        },
                        null,
                        timeoutMs,
                        Timeout.Infinite
                    );
                }
            });
        }

        private void Clock(Client c, JsonValue req, JsonValue a)
        {
            string mode = Str(a, "mode");
            var value = a.Get("value");
            JsonValue body;
            switch (mode)
            {
                case "":
                    body = Simulation.Invoke(() => Simulation.ClockState());
                    break;
                case "run":
                case "pause":
                    body = Simulation.Invoke(() =>
                    {
                        Simulation.SetClockMode(mode);
                        return Simulation.ClockState();
                    });
                    break;
                case "step":
                {
                    // answered when the step ends; the simulation (and the other clients'
                    // requests) go on meanwhile
                    double seconds = value != null && value.IsNumber ? value.Double : double.NaN;
                    var sink = c.Sink;
                    Simulation.Invoke(() =>
                    {
                        long ms = double.IsNaN(seconds)
                            ? Simulation.World.Clock.StepMs
                            : World.SimClock.ToMs(seconds);
                        if (ms < 0)
                            throw new ArgumentException("a step cannot go backwards");
                        Simulation.BeginStep(ms, moved =>
                        {
                            var s = Simulation.ClockState();
                            s.Set("moved", JsonValue.Of(moved));
                            Ok(sink, req, s);
                        });
                    });
                    return;
                }
                case "speed":
                {
                    if (value == null || !value.IsNumber)
                        throw new ArgumentException("clock speed needs a number value");
                    double sp = value.Double;
                    body = Simulation.Invoke(() =>
                    {
                        Simulation.SetSpeed(sp);
                        return Simulation.ClockState();
                    });
                    break;
                }
                default:
                    throw new ArgumentException(
                        "clock mode must be run, pause, step or speed: " + Project.CitectText.Quote(mode)
                    );
            }
            Ok(c.Sink, req, body);
        }

        private void ScenarioCmd(Client c, JsonValue req, JsonValue a)
        {
            string action = Str(a, "action");
            string path = Str(a, "path");
            var data = a.Get("scenario");
            var body = Simulation.Invoke(() =>
            {
                var b = JsonValue.NewObject();
                switch (action)
                {
                    case "load":
                    case "run":
                        if (path.Length > 0 || (data != null && data.Kind == JsonKind.Object))
                        {
                            var sc = Simulation.LoadScenario(
                                data != null && data.Kind == JsonKind.Object ? data : null,
                                path.Length > 0 ? path : null
                            );
                            b.Set("loaded", JsonValue.True);
                            b.Set("steps", JsonValue.Of(sc.Steps.Count));
                            b.Set("description", sc.Description);
                            b.Set("project", sc.Project);
                        }
                        if (action == "run")
                        {
                            Simulation.RunScenario();
                            b.Set("running", JsonValue.Of(Simulation.ScenarioRunning));
                        }
                        break;
                    case "stop":
                        b.Set("stopped", JsonValue.Of(Simulation.ScenarioRunning));
                        Simulation.StopScenario();
                        break;
                    case "save":
                    {
                        var sc = Simulation.SaveScenario();
                        b.Set("scenario", sc);
                        b.Set("tags", JsonValue.Of(sc["tags"].Count));
                        if (path.Length > 0)
                        {
                            string full = Path.GetFullPath(path);
                            // never into a project folder of the unit (the simulator writes no project file)
                            foreach (var p in Simulation.World.Unit.Search)
                                if (
                                    full.StartsWith(
                                        Path.GetFullPath(p.Dir).TrimEnd('\\', '/')
                                            + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                    throw new ArgumentException(
                                        "scenario save: "
                                            + full
                                            + " is in the project folder of "
                                            + p.Name
                                            + " (the simulator writes no project file)"
                                    );
                            string tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            File.WriteAllText(tmp, sc.ToString(), new System.Text.UTF8Encoding(false));
                            if (File.Exists(full))
                                File.Delete(full);
                            File.Move(tmp, full);
                            b.Set("saved", full);
                        }
                        break;
                    }
                    default:
                        throw new ArgumentException(
                            "scenario action must be load, run, stop or save: "
                                + Project.CitectText.Quote(action)
                        );
                }
                return b;
            });
            Ok(c.Sink, req, body);
        }

        // ------------------------------------------------------------------ pages
        /// <summary>What a page's code reads, for ctsim/tags and ctsim/subscribe with a page or a
        /// window: the plain tag names, and each '?name?' reference resolved through the
        /// associations of the window that shows the page (a tag, a literal, or unresolved).</summary>
        private sealed class PageRefs
        {
            public string Page = "";
            public string Rdb = "";
            public int? Window;
            public readonly List<string> Plain = new List<string>();

            /// <summary>(reference as written, association name, the tag it names).</summary>
            public readonly List<(string Ref, string Via, string Target)> Resolved =
                new List<(string, string, string)>();
            public readonly List<(string Ref, string Via, JsonValue Value)> Literals =
                new List<(string, string, JsonValue)>();
            public readonly List<(string Ref, string Via, int Error)> Unresolved =
                new List<(string, string, int)>();

            /// <summary>Lower-case name -> the type the page's code suggests (TagUse).</summary>
            public Dictionary<string, string> Guesses = new Dictionary<string, string>(
                StringComparer.Ordinal
            );

            /// <summary>Every tag name to subscribe: the plain ones and the resolved targets.</summary>
            public List<string> All()
            {
                var r = new List<string>(Plain);
                var seen = new HashSet<string>(Plain, StringComparer.OrdinalIgnoreCase);
                foreach (var x in Resolved)
                    if (seen.Add(x.Target))
                        r.Add(x.Target);
                return r;
            }
        }

        private static string RefText(Compiled.TagRef r)
        {
            string s = (r.Cluster.Length > 0 ? r.Cluster + ":" : "") + r.Tag;
            if (r.Element.Length > 0)
                s += "." + r.Element;
            if (r.Item.Length > 0)
                s += "." + r.Item;
            return s;
        }

        /// <summary>The page's references (<see cref="PageRefs"/>): the page a window shows
        /// (<paramref name="window"/>, the page name then checked against it), else the page's
        /// code here (compiled from rows, else its .RDB). Null with the reason when there is none.</summary>
        private PageRefs? PageRefsFor(string page, int? window, out string error)
        {
            string err = "";
            var r = Simulation.Invoke(() =>
            {
                CicodeSim.Page.CompiledPage? cp;
                var res = new PageRefs();
                if (window != null)
                {
                    var wp = Simulation.Pages.WindowPage(window.Value);
                    if (wp == null)
                    {
                        err =
                            "window "
                            + window.Value.ToString(CultureInfo.InvariantCulture)
                            + " shows no page";
                        return null;
                    }
                    string shown = wp.Value.Window.Page?.Name ?? wp.Value.Page.Name;
                    if (
                        page.Length > 0
                        && !string.Equals(
                            page.TrimStart('!'),
                            shown.TrimStart('!'),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        err =
                            "window "
                            + window.Value.ToString(CultureInfo.InvariantCulture)
                            + " shows "
                            + shown
                            + ", not "
                            + page;
                        return null;
                    }
                    cp = wp.Value.Page;
                    res.Page = shown;
                    res.Window = window.Value;
                }
                else
                {
                    cp = Simulation.Pages.CompiledFor(page);
                    if (cp == null)
                    {
                        err = "no compiled page " + page + " (no " + page + ".RDB in the project)";
                        return null;
                    }
                    res.Page = cp.Name.Length > 0 ? cp.Name : page;
                }
                res.Rdb = cp.Path;
                var prog = cp.Program;
                var tags = window != null ? Simulation.Pages.Animator?.Tags : null;
                var plain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenRef = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tr in prog.TagRefs.Values)
                {
                    if (tr.Tag.Length == 0)
                        continue;
                    bool assoc =
                        tr.Tag.IndexOf('?') >= 0
                        || tr.Element.IndexOf('?') >= 0
                        || tr.Item.IndexOf('?') >= 0
                        || tr.Cluster.IndexOf('?') >= 0;
                    if (!assoc)
                    {
                        if (plain.Add(tr.Tag))
                            res.Plain.Add(tr.Tag);
                        continue;
                    }
                    string text = RefText(tr);
                    if (!seenRef.Add(text))
                        continue;
                    string token =
                        tr.Tag.IndexOf('?') >= 0 ? tr.Tag
                        : tr.Item.IndexOf('?') >= 0 ? tr.Item
                        : tr.Element.IndexOf('?') >= 0 ? tr.Element
                        : tr.Cluster;
                    string via = CicodeSim.World.Display.DisplayState.AssName(token);
                    if (tags == null)
                    {
                        res.Unresolved.Add((text, via, CicodeSim.Page.PageTags.NotAssociated));
                        continue;
                    }
                    // the association's own spelling ('ToTW' for a reference '?TotW?')
                    var shownWin = Simulation.World.Display.OpenWindow(window!.Value);
                    var entry =
                        shownWin != null ? CicodeSim.World.Display.DisplayState.Find(shownWin, token) : null;
                    if (entry != null && entry.Arg.Length > 0)
                        via = entry.Arg;
                    var (rr, lit, e) = tags.Resolve(tr, window!.Value);
                    if (lit != null)
                    {
                        Value lv = lit.Value;
                        res.Literals.Add(
                            (text, via, lv.T == Types.String ? JsonValue.Of(lv.S) : ValueFormat.Json(lv))
                        );
                        continue;
                    }
                    if (rr == null || e != 0)
                    {
                        res.Unresolved.Add((text, via, e != 0 ? e : CicodeSim.Page.PageTags.NotAssociated));
                        continue;
                    }
                    var found = Simulation.World.Tags.Resolve(rr.Tag, "", rr.Item, rr.Cluster);
                    string target =
                        found.Found && found.Kind != "alarm" && found.Name.Length > 0
                            ? found.Name
                            : rr.Tag + (rr.Item.Length > 0 ? "." + rr.Item : "");
                    res.Resolved.Add((text, via, target));
                }
                try
                {
                    res.Guesses = TagUse.Guess(prog, TagUse.PageBlocks(cp), Simulation.Library);
                    Simulation.NoteGuesses(res.Guesses);
                }
                catch (Exception ex)
                    when (ex is IOException
                        || ex is InvalidDataException
                        || ex is ArgumentException
                        || ex is IndexOutOfRangeException
                    )
                {
                    res.Guesses = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                return res;
            });
            error = err;
            return r;
        }

        /// <summary>A tag the page reads that the project does not define:
        /// {name, defined: false, kind 'undefined' (or 'simulated' once setTag made it), type
        /// (the code's use, else REAL), typeFrom 'code' | 'default', value, quality}.</summary>
        private JsonValue UndefinedEntry(string name, World.TagDef? sim, PageRefs? refs)
        {
            var o = JsonValue.NewObject();
            o.Set("name", sim?.Name ?? name);
            o.Set("defined", JsonValue.False);
            o.Set("equipment", "");
            o.Set("item", "");
            string key = Project.CitectText.Trim(name).ToLowerInvariant();
            string? guess = null;
            bool fromCode = refs != null && refs.Guesses.TryGetValue(key, out guess);
            if (sim != null)
            {
                o.Set("kind", "simulated");
                o.Set("simulated", JsonValue.True);
                o.Set("project", World.TagDb.SimulatedProject);
                o.Set("type", sim.TypeName);
                var (value, quality, _) = Simulation.World.Tags.Peek(sim.Name);
                o.Set("value", value);
                o.Set("quality", World.Quality.Name(quality));
            }
            else
            {
                o.Set("kind", "undefined");
                o.Set("project", "");
                o.Set("type", guess ?? Sim.TagUse.Real);
                o.Set("value", JsonValue.Null);
                o.Set("quality", World.Quality.Name(World.Quality.QualityTagNotFound));
            }
            o.Set("typeFrom", fromCode ? "code" : "default");
            if (fromCode && sim == null)
                o.Set("guess", guess!);
            o.Set("comment", "");
            o.Set("engUnits", "");
            o.Set("format", "");
            o.Set("engZero", JsonValue.Null);
            o.Set("engFull", JsonValue.Null);
            o.Set("cluster", "");
            return o;
        }

        private static int? Window(JsonValue a)
        {
            var v = a.Get("window");
            if (v == null || v.IsNull)
                return null;
            if (!v.IsNumber)
                throw new ArgumentException("window must be a number");
            return (int)v.ToNumber();
        }

        /// <summary>Is the page's compiled .RDB current for this model?</summary>
        internal static bool RdbMatches(string rdb, Page.PageModel model, List<string> notes)
        {
            Page.CompiledPage cp;
            try
            {
                cp = Page.CompiledPage.Load(rdb);
            }
            catch (Exception e)
            {
                notes.Add(
                    "the compiled page "
                        + Path.GetFileName(rdb)
                        + " does not read ("
                        + e.Message
                        + "): the page's rows run"
                );
                return false;
            }
            var rdbTables = new Dictionary<string, List<Dictionary<string, object?>>>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (var kv in cp.Tables)
                rdbTables[kv.Key] = kv.Value;
            foreach (var kv in model.Tables)
            {
                var mine = new List<string>();
                foreach (var row in kv.Value)
                    if (row.TryGetValue("AN", out var an))
                        mine.Add(Project.CitectText.Trim(an ?? ""));
                rdbTables.TryGetValue(kv.Key, out var theirsRows);
                var theirs = new List<string>();
                foreach (var row in theirsRows ?? new List<Dictionary<string, object?>>())
                    if (row.TryGetValue("AN", out var an) && an != null)
                        theirs.Add(Convert.ToString(an, CultureInfo.InvariantCulture)!.Trim());
                mine.Sort(StringComparer.Ordinal);
                theirs.Sort(StringComparer.Ordinal);
                if (mine.Count != theirs.Count || !System.Linq.Enumerable.SequenceEqual(mine, theirs))
                {
                    notes.Add(
                        "the compiled page "
                            + Path.GetFileName(rdb)
                            + " is of another version of the page ("
                            + kv.Key
                            + ": "
                            + theirs.Count.ToString(CultureInfo.InvariantCulture)
                            + " objects compiled, "
                            + mine.Count.ToString(CultureInfo.InvariantCulture)
                            + " in the page): the page's rows run"
                    );
                    return false;
                }
            }
            return true;
        }

        private static bool ModelCurrent(JsonValue? mj)
        {
            if (mj == null || (mj.Get("source")?.Str ?? "") != "saved")
                return false;
            var c = mj.Get("compiled");
            return c != null
                && c.Kind == JsonKind.Object
                && c.Get("current") is JsonValue cur
                && cur.Kind == JsonKind.Bool
                && cur.Bool;
        }

        /// <summary>Open a page: which code it runs (its model's rows when the model is not
        /// current or there is no .RDB, else the .RDB, else the project's rows), compiled on this
        /// thread (the simulation runs on), then shown on the simulation thread.</summary>
        private JsonValue OpenBody(
            string name,
            int? window,
            JsonValue? mj,
            Page.PageModel? model,
            JsonValue? associations,
            List<string> notes
        )
        {
            string? rdb = Simulation.Invoke(() => Simulation.Pages.RdbOf(name));
            Page.CompiledPage? cp = null;
            string source = "rdb";
            if (
                model != null
                && model.Tables.Count > 0
                && (rdb == null || !ModelCurrent(mj) || !RdbMatches(rdb, model, notes))
            )
            {
                cp = Simulation.Pages.CompileRows(name, model, notes);
                source = "rows";
            }
            else if (rdb == null)
            {
                cp = Simulation.Pages.CompileRows(name, null, notes);
                source = cp != null ? "projectRows" : "none";
            }
            if (model != null)
                foreach (string n in model.Notes)
                    notes.Add("model: " + n);
            var keep = cp;
            return Simulation.Invoke(() =>
                Simulation.Pages.Open(name, window, model, keep, associations, source, notes)
            );
        }

        private void OpenPage(Client c, JsonValue req, JsonValue a)
        {
            string name = Str(a, "name");
            if (name.Length == 0)
                throw new ArgumentException("openPage needs a name");
            var mj = a.Get("model");
            Page.PageModel? model = null;
            if (mj != null && !mj.IsNull)
            {
                if (mj.Kind != JsonKind.Object)
                    throw new ArgumentException("model must be a ctsim-page/0 object");
                model = Page.PageModel.FromJson(mj);
            }
            var notes = new List<string>();
            var body = OpenBody(name, Window(a), mj, model, a.Get("associations"), notes);
            long err = body["error"].Long;
            if (err != 0)
            {
                Fail(
                    c.Sink,
                    req,
                    "openPage "
                        + name
                        + ": error "
                        + err.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + (body.Get("errorText")?.Str ?? ""),
                    body
                );
                return;
            }
            Ok(c.Sink, req, body);
        }

        /// <summary>ctsim/answer {id, choice?, ok?, text?, values?, press?}: a viewer's answer to a
        /// ctsim/dialog. Body {id, answered: true}; a dialog that is not shown (answered or closed
        /// meanwhile) fails.</summary>
        private void Answer(Client c, JsonValue req, JsonValue a)
        {
            if (!Dialogs.Answer(a, out string error))
            {
                Fail(c.Sink, req, error);
                return;
            }
            // a form no task waits for (FormRead(1)) takes the answer now: its variables, its
            // button functions (a waiting task takes it at its next turn)
            Simulation.Post(() => Simulation.Host.PumpDialogs());
            var b = JsonValue.NewObject();
            b.Set("id", a["id"]);
            b.Set("answered", JsonValue.True);
            Ok(c.Sink, req, b);
        }

        private void ClosePage(Client c, JsonValue req, JsonValue a)
        {
            int window = Window(a) ?? 0;
            Ok(c.Sink, req, Simulation.Invoke(() => Simulation.Pages.Close(window)));
        }

        private void PageState(Client c, JsonValue req, JsonValue a)
        {
            int window = Window(a) ?? 0;
            Ok(c.Sink, req, Simulation.Invoke(() => Simulation.Pages.StateJson(window)));
        }

        private void PageModelCmd(Client c, JsonValue req, JsonValue a)
        {
            int window = Window(a) ?? throw new ArgumentException("pageModel needs a window");
            if (Bool(a, "none", false))
            {
                // the viewer has no drawing for it: a held page starts now
                Ok(c.Sink, req, Simulation.Invoke(() => Simulation.Pages.NoModel(window)));
                return;
            }
            var mj = a.Get("model");
            if (mj == null || mj.Kind != JsonKind.Object)
                throw new ArgumentException("pageModel needs a model (a ctsim-page/0 object)");
            var model = Page.PageModel.FromJson(mj);
            Ok(c.Sink, req, Simulation.Invoke(() => Simulation.Pages.AttachModel(window, model)));
        }

        private void Input(Client c, JsonValue req, JsonValue a)
        {
            int window = Window(a) ?? 0;
            Ok(c.Sink, req, Simulation.Invoke(() => Simulation.Pages.Input(window, a)));
        }

        private void Reload(Client c, JsonValue req, JsonValue a)
        {
            string file = Str(a, "file");
            if (file.Length == 0)
                throw new ArgumentException("reload needs a file");
            var tv = a.Get("text");
            string? text = tv != null && tv.Kind == JsonKind.String ? tv.Str : null;
            Ok(c.Sink, req, Simulation.Invoke(() => Simulation.Reload(file, text)));
        }

        private JsonValue TasksBody()
        {
            var list = Simulation.Invoke(() => Simulation.TaskList());
            var arr = JsonValue.NewArray();
            foreach (var t in list)
                arr.Add(t.ToJson());
            var b = JsonValue.NewObject();
            b.Set("tasks", arr);
            return b;
        }

        // ------------------------------------------------------------------ ending
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            StopAlarmEvents();
            if (Entry != null)
                Discovery.Remove(Entry.Id, _discoveryFolder);
            try
            {
                Listener?.Dispose();
            }
            catch (Exception) { }
            List<Client> all;
            lock (_lock)
                all = new List<Client>(_clients);
            foreach (var c in all)
                try
                {
                    c.Sink.Close();
                }
                catch (Exception) { }
            // the queued messages (a ctsim/shutdown's response, the last events) are written
            // before the process may end: Close only starts the drain
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var c in all)
                if (c.Conn != null)
                    c.Conn.WaitFinished((int)Math.Max(0, CloseWaitMs - sw.ElapsedMilliseconds));
        }

        /// <summary>How long Dispose waits for the connections to write their queued messages.</summary>
        public const int CloseWaitMs = 3000;
    }
}
