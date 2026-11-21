using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>Message sessions inside the one simulated process.</summary>
    public static class Messages
    {
        public const int ServerSide = 0x1000;
        private const string CurrentKey = "msg.current";

        internal sealed class Session
        {
            public int Handle;
            public string Type = "",
                Name = "",
                Cluster = "";
            public Function? ClientFn;
        }

        internal sealed class State
        {
            public readonly List<Session> Sessions = new List<Session>();
            public readonly Dictionary<string, Function?> ServerFns = new Dictionary<string, Function?>(
                StringComparer.OrdinalIgnoreCase
            );
            public bool Built;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SimHost, State> States =
            new System.Runtime.CompilerServices.ConditionalWeakTable<SimHost, State>();

        /// <summary>The sessions: one a server of the unit (Servers.All), then the computer's own (mode 2 / 4).</summary>
        private static State Of(SimHost h)
        {
            var s = States.GetValue(h, _ => new State());
            if (!s.Built)
            {
                s.Built = true;
                foreach (var d in Servers.All(h))
                    s.Sessions.Add(
                        new Session
                        {
                            Handle = s.Sessions.Count,
                            Type = d.Type,
                            Name = d.Name,
                            Cluster = d.Cluster,
                        }
                    );
                s.Sessions.Add(
                    new Session
                    {
                        Handle = s.Sessions.Count,
                        Type = "Node",
                        Name = ProcessData.Of(h).ComputerName,
                    }
                );
            }
            return s;
        }

        private static Session? Get(SimHost h, long hd)
        {
            var s = Of(h);
            return hd >= 0 && hd < s.Sessions.Count ? s.Sessions[(int)hd] : null;
        }

        private static string TypeOf(string name) =>
            C.IEq(name, "Alarm") ? "Alarm"
            : C.IEq(name, "Report") ? "Report"
            : C.IEq(name, "Trend") ? "Trend"
            : "";

        /// <summary>The first session of a server type, or of a server named so, in the cluster.</summary>
        private static Session? Find(SimHost h, string name, string cluster)
        {
            string type = TypeOf(CitectText.Trim(name)),
                n = CitectText.Trim(name),
                c = CitectText.Trim(cluster);
            foreach (var x in Of(h).Sessions)
            {
                if (x.Type == "Node")
                    continue;
                if (c.Length > 0 && x.Cluster.Length > 0 && !C.IEq(x.Cluster, c))
                    continue;
                if (type.Length > 0 ? C.IEq(x.Type, type) : C.IEq(x.Name, n))
                    return x;
            }
            return null;
        }

        /// <summary>Start a post function as a task with its message (MsgRead / MsgGetCurr read it).</summary>
        private static void Post(SimHost h, CicodeTask from, Function? fn, int handle, long type, string str)
        {
            if (fn == null)
                return;
            var vm = from.Vm;
            try
            {
                var task = vm.TaskForCall(fn, Array.Empty<Value>(), false, null, fn.Name);
                task.Data[CurrentKey] = (handle, type, str);
                if (vm.Scheduler != null)
                    vm.Scheduler.Add(task);
                else
                    vm.Run(task, 1000000);
            }
            catch (HardwareError) { }
            catch (InvalidOperationException) { }
        }

        private static (int handle, long type, string str)? Current(CicodeTask t) =>
            t.Data.TryGetValue(CurrentKey, out var v) && v is ValueTuple<int, long, string> m
                ? m
                : ((int, long, string)?)null;

        [Builtin(
            "MsgOpen",
            Doc = "1193646",
            Effects = "sets the post function of the session (mode 0) or of the server side of every session of that server type (mode 1)",
            Errors = "274 a mode above 4 (raised, -1; guess on the code); 405 mode 3: no redundant server to talk to (raised, -1; guess)",
            Notes = "Mode 0: the session to the cluster's first Alarm / Report / Trend server, or to the server named (an I/O server), its handle; -1 when there is none (no error). Mode 1: the server side, handle 4096. Mode 2: the session to this computer (sName its computer name, else -1); 4: the session to the calling process. Fn is the post function's name (0: none)."
        )]
        private static Value MsgOpen(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("MsgOpen");
            string name = A.S(a.Get(0));
            long mode = A.UShort(A.I(a.Get(1)));
            string fnName = a.Count > 2 ? A.S(a.Get(2)) : "";
            string cluster = a.Count > 3 ? A.S(a.Get(3)) : "";
            var s = Of(h);
            Function? fn =
                CitectText.Trim(fnName).Length == 0 || fnName == "0" ? null : CallText.Find(t, fnName);
            if (mode > 4)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var node = s.Sessions[s.Sessions.Count - 1];
            if (mode == 4)
                return W.I(node.Handle);
            if (mode == 2)
            {
                if (!C.IEq(CitectText.Trim(name), node.Name))
                    return W.I(-1);
                node.ClientFn = fn;
                return W.I(node.Handle);
            }
            if (mode == 3)
            {
                W.Raise(t, 405);
                return W.I(-1);
            }
            var x = Find(h, name, cluster);
            if (x == null)
                return W.I(-1);
            if (mode == 1)
            {
                s.ServerFns[x.Type + "|" + x.Name] = fn;
                foreach (var y in s.Sessions)
                    if (C.IEq(y.Type, x.Type) && (TypeOf(name).Length > 0 || C.IEq(y.Name, x.Name)))
                        s.ServerFns[y.Type + "|" + y.Name] = fn;
                return W.I(ServerSide);
            }
            x.ClientFn = fn;
            return W.I(x.Handle);
        }

        [Builtin(
            "MsgClose",
            Doc = "1193644",
            Errors = "257 a bad handle (returned)",
            Notes = "4096 (the server side): the server post functions of that server type go; a session's handle: its client post function goes. The sessions stay."
        )]
        private static Value MsgClose(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("MsgClose");
            long hd = A.Short(A.I(a.Get(1)));
            var s = Of(h);
            if (hd == ServerSide)
            {
                string type = TypeOf(CitectText.Trim(A.S(a.Get(0))));
                foreach (var y in s.Sessions)
                    if (type.Length > 0 ? C.IEq(y.Type, type) : C.IEq(y.Name, CitectText.Trim(A.S(a.Get(0)))))
                        s.ServerFns.Remove(y.Type + "|" + y.Name);
                return W.I(0);
            }
            var x = Get(h, hd);
            if (x == null)
                return W.I(257);
            x.ClientFn = null;
            return W.I(0);
        }

        [Builtin(
            "MsgState",
            Doc = "1193649",
            Notes = "1 (online) for a session of the process (every one is: the servers are this process), -1 for a bad handle."
        )]
        private static Value MsgState(SimHost h, CicodeTask t, ArgList a) =>
            W.I(h.World != null && Get(h, A.Short(A.I(a.Get(0)))) != null ? 1 : -1);

        [Builtin(
            "MsgWrite",
            Doc = "1193650",
            Effects = "starts the other side's post function with the message (MsgRead gives it)",
            Errors = "269 a bad handle (returned)",
            Notes = "From a server post function (or a task running for a message) the message goes to the session's client post function, else to the server post function of the session's server; no post function there: disregarded (doc), 0."
        )]
        private static Value MsgWrite(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("MsgWrite");
            var x = Get(h, A.Short(A.I(a.Get(0))));
            if (x == null)
                return W.I(Codes.BadHandle);
            long type = A.I(a.Get(1));
            string str = A.Cap(A.S(a.Get(2)));
            bool fromServer = t.Data.TryGetValue(ServerKey, out var sv) && sv is bool b && b;
            if (fromServer)
                Post(h, t, x.ClientFn, x.Handle, type, str);
            else if (Of(h).ServerFns.TryGetValue(x.Type + "|" + x.Name, out var fn) && fn != null)
                PostServer(h, t, fn, x.Handle, type, str);
            return W.I(0);
        }

        private const string ServerKey = "msg.server";

        private static void PostServer(
            SimHost h,
            CicodeTask from,
            Function fn,
            int handle,
            long type,
            string str
        )
        {
            var vm = from.Vm;
            try
            {
                var task = vm.TaskForCall(fn, Array.Empty<Value>(), false, null, fn.Name);
                task.Data[CurrentKey] = (handle, type, str);
                task.Data[ServerKey] = true;
                if (vm.Scheduler != null)
                    vm.Scheduler.Add(task);
                else
                    vm.Run(task, 1000000);
            }
            catch (HardwareError) { }
            catch (InvalidOperationException) { }
        }

        [Builtin(
            "MsgRead",
            Doc = "1193647",
            Notes = "In a post function: Type and Str get the message, the session handle is returned (to answer with MsgWrite); elsewhere -1 and the arguments stay."
        )]
        private static Value MsgRead(SimHost h, CicodeTask t, ArgList a)
        {
            var m = Current(t);
            if (m == null)
                return W.I(-1);
            W.Out(t, a.Get(0), (long)(int)m.Value.type);
            W.Out(t, a.Get(1), m.Value.str);
            return W.I(m.Value.handle);
        }

        [Builtin(
            "MsgGetCurr",
            Doc = "1193645",
            Notes = "The session of the message (or the MsgRPC call) the task runs for; -1 for any other task."
        )]
        private static Value MsgGetCurr(SimHost h, CicodeTask t, ArgList a)
        {
            var m = Current(t);
            return W.I(m == null ? -1 : m.Value.handle);
        }

        [Builtin(
            "MsgBrdcst",
            Doc = "1193643",
            Effects = "starts the client post function of every session of that server type with the message",
            Notes = "sName 'Alarm' / 'Report' / 'Trend' or a server's name; the sessions without a post function do not get it (doc). 0."
        )]
        private static Value MsgBrdcst(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("MsgBrdcst");
            string name = CitectText.Trim(A.S(a.Get(0)));
            long type = A.I(a.Get(1));
            string str = A.Cap(A.S(a.Get(2)));
            string cluster = a.Count > 3 ? CitectText.Trim(A.S(a.Get(3))) : "";
            string kind = TypeOf(name);
            foreach (var x in Of(h).Sessions)
            {
                if (x.Type == "Node")
                    continue;
                if (cluster.Length > 0 && x.Cluster.Length > 0 && !C.IEq(x.Cluster, cluster))
                    continue;
                if (kind.Length > 0 ? C.IEq(x.Type, kind) : C.IEq(x.Name, name))
                    Post(h, t, x.ClientFn, x.Handle, type, str);
            }
            return W.I(0);
        }

        [Builtin(
            "MsgRPC",
            Doc = "1193648",
            Blocking = true,
            Errors = "513 the logged-in user has no role with Allow RPC (raised, ''); 282 a synchronous call in foreground code (raised, ''); the call text's 292 / 433 / 529 / 518 (raised)",
            Notes = "The call runs here (the session's server is this process): bAsync 0 waits for it and gives its result as a string, 1 starts it and gives ''. In the called task MsgGetCurr gives the session. A bad handle: '' and nothing raised (the runtime's check)."
        )]
        private static Value MsgRPC(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("MsgRPC");
            var x = Get(h, A.UShort(A.I(a.Get(0))));
            if (x == null)
                return W.Empty;
            string name = A.S(a.Get(1)),
                args = A.S(a.Get(2));
            long mode = a.Count > 3 ? A.Short(A.I(a.Get(3))) : 0;
            bool async = (mode & 1) != 0;
            if (!async && t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.Empty;
            }
            if (!w.Users.CanRpc())
            {
                W.Raise(t, 513);
                return W.Empty;
            }
            Function? fn = CallText.Find(t, name);
            if (fn == null)
            {
                W.Raise(t, Errors.InvalidFunction);
                return W.Empty;
            }
            var vals = CallText.Bind(fn, args, out int err, out string msg);
            if (msg.Length > 0)
                h.Note(t, msg);
            if (err != 0)
            {
                W.Raise(t, err);
                if (err != Errors.TooManyArgs)
                    return W.Empty;
            }
            CicodeTask child;
            try
            {
                child = t.Vm.TaskForCall(fn, vals, false, null, fn.Name);
            }
            catch (HardwareError e)
            {
                W.Raise(t, e.Code);
                return W.Empty;
            }
            child.Data[CurrentKey] = (x.Handle, 0L, "");
            var sched = t.Vm.Scheduler;
            if (sched == null)
            {
                t.Vm.Run(child, 1000000);
                return async ? W.Empty : Result(child, fn);
            }
            sched.Add(child);
            if (async)
                return W.Empty;
            t.Wait = new Wait(TaskState.Wait, "MsgRPC(" + fn.Name + ")")
            {
                Poll = () => child.Done,
                Result = () => Result(child, fn),
            };
            return Value.Block;
        }

        private static Value Result(CicodeTask child, Function fn)
        {
            if (child.State != TaskState.Stop || child.Result == null || fn.Result == Types.Void)
                return W.Empty;
            return Conv.ToStr(child.Result.Value, NoErr.Instance);
        }
    }
}
