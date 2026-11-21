using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>What the simulation may do outside itself, per kind (ctsim/options <c>dll</c>,
    /// <c>sql</c>): nothing (the stubs: a library that does not load, a database that cannot be
    /// reached), after asking the viewer, or everything.</summary>
    public enum OutsideMode
    {
        /// <summary>Not simulated: every call answers as the runtime does without the thing.</summary>
        Off,

        /// <summary>Ask the viewer before a library is loaded, a database is connected to or a
        /// statement that can change a database runs; what is on the whitelist is not asked about.</summary>
        Ask,

        /// <summary>Load every library, connect to every database, run every statement.</summary>
        On,
    }

    /// <summary>
    /// Access to DLLs and SQL databases. Off by default, so a simulation can't reach the real
    /// plant.
    /// </summary>
    public sealed class OutsideAccess
    {
        private readonly object _lock = new object();
        private OutsideMode _dll = OutsideMode.Off,
            _sql = OutsideMode.Off;
        private readonly HashSet<string> _dllAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _sqlAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dllSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _sqlSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _sqlWriteSession = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        );

        // 'once' answers of a question no task waited for (foreground code): the next call takes one
        private readonly Dictionary<string, int> _once = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase
        );

        // the question shown for a key (one dialog per library / database / action at a time)
        private readonly Dictionary<string, Dialog> _asked = new Dictionary<string, Dialog>(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>The modes or a whitelist changed (an 'always' answer): the server tells the viewers.</summary>
        public Action? Changed;

        public OutsideMode Dll
        {
            get
            {
                lock (_lock)
                    return _dll;
            }
            set
            {
                lock (_lock)
                    _dll = value;
            }
        }

        public OutsideMode Sql
        {
            get
            {
                lock (_lock)
                    return _sql;
            }
            set
            {
                lock (_lock)
                    _sql = value;
            }
        }

        public static string Word(OutsideMode m) =>
            m == OutsideMode.On ? "on"
            : m == OutsideMode.Ask ? "ask"
            : "off";

        public static bool TryParse(string? s, out OutsideMode m)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "off":
                    m = OutsideMode.Off;
                    return true;
                case "ask":
                    m = OutsideMode.Ask;
                    return true;
                case "on":
                    m = OutsideMode.On;
                    return true;
                default:
                    m = OutsideMode.Off;
                    return false;
            }
        }

        public List<string> DllAllowed()
        {
            lock (_lock)
            {
                var l = new List<string>(_dllAllowed);
                l.Sort(StringComparer.OrdinalIgnoreCase);
                return l;
            }
        }

        public List<string> SqlAllowed()
        {
            lock (_lock)
            {
                var l = new List<string>(_sqlAllowed);
                l.Sort(StringComparer.OrdinalIgnoreCase);
                return l;
            }
        }

        /// <summary>Replace a whitelist; true when it changed.</summary>
        public bool SetAllowed(bool dll, IEnumerable<string> items)
        {
            var n = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in items)
                if (!string.IsNullOrWhiteSpace(s))
                    n.Add(s.Trim());
            lock (_lock)
            {
                var set = dll ? _dllAllowed : _sqlAllowed;
                if (set.SetEquals(n))
                    return false;
                set.Clear();
                set.UnionWith(n);
                return true;
            }
        }

        private static string Key(string access, string action, string target) =>
            access + "\0" + action + "\0" + target;

        /// <summary>Whether a library may be loaded: true, false, or null = ask.</summary>
        public bool? DllDecision(string path)
        {
            lock (_lock)
            {
                if (_dll == OutsideMode.Off)
                    return false;
                if (_dll == OutsideMode.On || _dllAllowed.Contains(path) || _dllSession.Contains(path))
                    return true;
                return TakeOnce(Key("dll", "load", path)) ? true : (bool?)null;
            }
        }

        /// <summary>Whether a database may be connected to: true, false, or null = ask.</summary>
        public bool? SqlConnectDecision(string database)
        {
            lock (_lock)
            {
                if (_sql == OutsideMode.Off)
                    return false;
                if (
                    _sql == OutsideMode.On
                    || _sqlAllowed.Contains(database)
                    || _sqlSession.Contains(database)
                    || _sqlWriteSession.Contains(database)
                )
                    return true;
                return TakeOnce(Key("sql", "connect", database)) ? true : (bool?)null;
            }
        }

        /// <summary>Whether a statement that can change the database may run: true, false, or
        /// null = ask.</summary>
        public bool? SqlWriteDecision(string database)
        {
            lock (_lock)
            {
                if (_sql == OutsideMode.Off)
                    return false;
                if (
                    _sql == OutsideMode.On
                    || _sqlAllowed.Contains(database)
                    || _sqlWriteSession.Contains(database)
                )
                    return true;
                return TakeOnce(Key("sql", "write", database)) ? true : (bool?)null;
            }
        }

        private bool TakeOnce(string key)
        {
            if (!_once.TryGetValue(key, out int n) || n <= 0)
                return false;
            if (n == 1)
                _once.Remove(key);
            else
                _once[key] = n - 1;
            return true;
        }

        /// <summary>Apply an answer ('once', 'session', 'always'; anything else = deny): true =
        /// allowed. <paramref name="waited"/>: a task waits for it (a 'once' answer is this call's);
        /// false: the call was refused meanwhile (foreground code), 'once' allows the next one.</summary>
        public bool Apply(string access, string action, string target, string? choice, bool waited)
        {
            bool changed = false,
                allowed;
            lock (_lock)
            {
                _asked.Remove(Key(access, action, target));
                // the kind was turned off while the question was up: the answer denies (nothing
                // is loaded or connected to in mode off, whatever the viewer clicked meanwhile)
                if ((access == "dll" ? _dll : _sql) == OutsideMode.Off)
                    choice = "deny";
                switch ((choice ?? "").Trim().ToLowerInvariant())
                {
                    case "once":
                        allowed = true;
                        if (!waited)
                        {
                            string k = Key(access, action, target);
                            _once.TryGetValue(k, out int n);
                            _once[k] = n + 1;
                        }
                        break;
                    case "session":
                        allowed = true;
                        if (access == "dll")
                            _dllSession.Add(target);
                        else if (action == "write")
                            _sqlWriteSession.Add(target);
                        else
                            _sqlSession.Add(target);
                        break;
                    case "always":
                        allowed = true;
                        changed = (access == "dll" ? _dllAllowed : _sqlAllowed).Add(target);
                        break;
                    default:
                        allowed = false;
                        break;
                }
            }
            if (changed)
                Changed?.Invoke();
            return allowed;
        }

        /// <summary>The question shown for this key (null: none), so one library or database is
        /// asked about once at a time however many calls wait.</summary>
        internal Dialog? Asked(string access, string action, string target)
        {
            lock (_lock)
                return _asked.TryGetValue(Key(access, action, target), out var d) && !d.Answered ? d : null;
        }

        internal void NoteAsked(string access, string action, string target, Dialog d)
        {
            lock (_lock)
                _asked[Key(access, action, target)] = d;
        }

        /// <summary>The questions of a kind still up (its mode was turned off): cancelled, so the
        /// tasks waiting on them go on refused and the viewers close them.</summary>
        internal void CancelAsked(string access)
        {
            var gone = new List<Dialog>();
            lock (_lock)
            {
                foreach (var kv in _asked)
                    if (kv.Key.StartsWith(access + "\0", StringComparison.Ordinal) && !kv.Value.Answered)
                        gone.Add(kv.Value);
            }
            foreach (var d in gone)
                d.Cancel();
        }

        /// <summary>How long a DLL call may take before the library helper process is ended and
        /// the call fails: milliseconds, 0 = as long as it takes (the runtime waits too; the
        /// simulation stands still meanwhile). ctsim/options dllTimeout (seconds).</summary>
        public int DllTimeoutMs
        {
            get
            {
                lock (_lock)
                    return _dllTimeoutMs;
            }
            set
            {
                lock (_lock)
                    _dllTimeoutMs = Math.Max(0, value);
            }
        }

        private int _dllTimeoutMs;
    }

    /// <summary>What a blocked built-in waits for after its permission question: nothing more
    /// (<see cref="Poll"/> null) or an operation running on another thread.</summary>
    internal struct Pending
    {
        /// <summary>True once <see cref="Result"/> can be taken (null: at once).</summary>
        public Func<bool>? Poll;

        /// <summary>The built-in's result (on the simulation's thread).</summary>
        public Func<Value> Result;

        public static Pending Now(Func<Value> result) => new Pending { Result = result };

        public static Pending Now(Value v) => new Pending { Result = () => v };
    }

    /// <summary>The permission question of mode Ask: a dialog of kind Permission through the
    /// dialog host (a viewer that shows dialogs: ctsim/dialog kind 'permission'; headless:
    /// denied at once).</summary>
    internal static class OutsideAsk
    {
        private static Dialog Show(
            SimHost h,
            CicodeTask t,
            string access,
            string action,
            string target,
            string detail
        )
        {
            var known = h.Outside.Asked(access, action, target);
            if (known != null)
                return known;
            string title,
                text;
            if (access == "dll")
            {
                title = "Load a library?";
                text =
                    "The page's code wants to load " + target + " and call its functions on this computer.";
            }
            else if (action == "write")
            {
                title = "Change the database?";
                text =
                    "The page's code wants to run a statement that can change the database " + target + ".";
            }
            else
            {
                title = "Connect to the database?";
                text = "The page's code wants to connect to the database " + target + ".";
            }
            var d = new Dialog
            {
                Kind = DialogKind.Permission,
                Title = title,
                Text = text,
                Access = access,
                Action = action,
                Target = target,
                Detail = detail,
                Task = t.Handle,
                Window = FormBuiltins.WindowOf(t),
            };
            h.Outside.NoteAsked(access, action, target, d);
            h.Interaction.Show(d);
            return d;
        }

        private static bool Take(
            SimHost h,
            Dialog d,
            string access,
            string action,
            string target,
            bool waited
        )
        {
            h.OutsideLoose.RemoveAll(x => x.Dialog == d);
            // one answer is applied once, however many calls took it (a second waiter, page
            // code that asked again meanwhile): a 'once' answer is one call, not one per taker
            if (d.Taken)
                return d.Allowed;
            d.TakeAnswer(out bool ok, out string? choice, out _);
            d.Taken = true;
            d.Allowed = h.Outside.Apply(
                access,
                action,
                target,
                ok ? (string.IsNullOrEmpty(choice) ? "once" : choice) : "deny",
                waited
            );
            d.Drop();
            h.Interaction.Closed(d);
            return d.Allowed;
        }

        /// <summary>Ask for permission, then continue with ``then``.</summary>
        public static Value Then(
            SimHost h,
            CicodeTask t,
            string access,
            string action,
            string target,
            string detail,
            Func<bool, Pending> then
        )
        {
            var d = Show(h, t, access, action, target, detail);
            if (t.Foreground)
            {
                if (d.Answered)
                    return then(Take(h, d, access, action, target, waited: true)).Result();
                // (taken when answered: PumpAnswers, from the dialog pump and the next outside
                // call; the question asked again each scan is listed once)
                if (!h.OutsideLoose.Exists(x => x.Dialog == d))
                    h.OutsideLoose.Add((d, access, action, target, null));
                h.Note(
                    t,
                    "Asked whether to "
                        + (
                            access == "dll" ? "load "
                            : action == "write" ? "change "
                            : "connect to "
                        )
                        + target
                        + ": page code cannot wait, so this call is refused and the answer counts from the next call"
                );
                return then(false).Result();
            }
            // listed with its waiter: an answer that comes after the task ended (TaskKill) is
            // still applied (an 'always' reaches the whitelist, the viewer's dialog closes)
            if (!h.OutsideLoose.Exists(x => x.Dialog == d && x.Waiter == t))
                h.OutsideLoose.Add((d, access, action, target, t));
            bool answered = false;
            Pending next = default;
            Exception? failed = null;
            t.Wait = new Wait(TaskState.Wait, "permission(" + target + ")")
            {
                Poll = () =>
                {
                    if (!answered)
                    {
                        if (!d.Answered)
                            return false;
                        answered = true;
                        try
                        {
                            next = then(Take(h, d, access, action, target, waited: true));
                        }
                        catch (Exception e)
                        {
                            // (thrown again from Result, on this task: the poll runs before the
                            // scheduler names the task it steps)
                            failed = e;
                            return true;
                        }
                    }
                    return next.Poll == null || next.Poll();
                },
                // TaskResume ends the wait early: the call is refused then (a blocked built-in
                // returns what it has); the question stays up for the next call
                Result = () =>
                    failed != null ? throw failed
                    : answered ? next.Result!()
                    : then(false).Result(),
            };
            return Value.Block;
        }

        /// <summary>Apply the answers nobody took: the questions of page code (no task waits),
        /// and those whose waiting task ended meanwhile. On the simulation's thread (the dialog
        /// pump after a viewer's answer, every outside call).</summary>
        public static void PumpAnswers(SimHost h)
        {
            var l = h.OutsideLoose;
            for (int i = l.Count - 1; i >= 0; i--)
            {
                var (d, access, action, target, waiter) = l[i];
                if (!d.Answered)
                    continue;
                if (
                    waiter != null
                    && !waiter.Done
                    && waiter.Wait != null
                    && waiter.Wait.Reason == "permission(" + target + ")"
                )
                    continue; // its task takes it at its next turn
                l.RemoveAt(i);
                Take(h, d, access, action, target, waited: waiter != null);
            }
        }

        /// <summary>Wait for <paramref name="p"/> (a blocking built-in: at least one turn).</summary>
        public static Value Await(CicodeTask t, string reason, Pending p)
        {
            t.Wait = new Wait(TaskState.Wait, reason) { Poll = p.Poll, Result = () => p.Result() };
            return Value.Block;
        }
    }

    public sealed partial class SimHost
    {
        /// <summary>The simulation's access to DLLs and SQL databases (off by default).</summary>
        public OutsideAccess Outside { get; } = new OutsideAccess();

        /// <summary>The permission questions up: those of foreground code (Waiter null: no task
        /// waits for them) and those a background task waits for (PumpAnswers applies the
        /// answer when the task is gone before it comes).</summary>
        internal readonly List<(
            Dialog Dialog,
            string Access,
            string Action,
            string Target,
            CicodeTask? Waiter
        )> OutsideLoose = new List<(Dialog, string, string, string, CicodeTask?)>();
    }
}
