using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>Server and process control for the one simulated process.</summary>
    public static class ServerProcess
    {
        private const int ServerNotFound = 418,
            AccessDenied = 513;

        /// <summary>a server by name ('Cluster.Server' or the cluster argument), null = none.</summary>
        private static Servers.ServerDef? Server(SimHost h, string name, string cluster)
        {
            string n = CitectText.Trim(name),
                c = CitectText.Trim(cluster);
            int dot = n.IndexOf('.');
            if (c.Length == 0 && dot > 0 && Servers.Clusters(h).Exists(x => C.IEq(x, n.Substring(0, dot))))
            {
                c = n.Substring(0, dot);
                n = n.Substring(dot + 1);
            }
            return Servers
                .All(h)
                .Find(x =>
                    C.IEq(x.Name, n) && (c.Length == 0 || x.Cluster.Length == 0 || C.IEq(x.Cluster, c))
                );
        }

        private static Value Run(SimHost h, CicodeTask t, string builtin, params Value[] args)
        {
            var b = Func0Table.Default.Lookup(builtin)!;
            return h.Registry!.Call(b.Index, t, ArgList.Of(args));
        }

        [Builtin(
            "ServerRPC",
            Doc = "1193662",
            Blocking = true,
            Errors = "418 no such server (raised, ''); 513 the logged-in user has no role with Allow RPC, or the default user (raised, ''); 282 iMode 0 in foreground code (raised, ''); the TaskCall / TaskNew errors of the call (292, 433, 529, 518)",
            Notes = "The simulated process is every server: iMode 0 runs the function as TaskCall does (waits for it, its result as a string), 1 starts it as TaskNew does and returns ''. sArg is the call's argument text (as TaskNew reads it)."
        )]
        private static Value ServerRPC(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("ServerRPC");
            string server = A.S(a.Get(0)),
                name = A.S(a.Get(1)),
                args = A.S(a.Get(2));
            long mode = a.Count > 3 ? A.Short(A.I(a.Get(3))) : 0;
            string cluster = a.Count > 4 ? A.S(a.Get(4)) : "";
            if (Server(h, server, cluster) == null)
            {
                W.Raise(t, ServerNotFound);
                return W.Empty;
            }
            if (mode == 0 && t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.Empty;
            }
            if (!w.Users.CanRpc())
            {
                W.Raise(t, AccessDenied);
                return W.Empty;
            }
            if (mode == 0)
                return Run(h, t, "TaskCall", Value.Str(name), Value.Str(args));
            Run(h, t, "TaskNew", Value.Str(name), Value.Str(args), Value.Long(0));
            return W.Empty;
        }

        [Builtin(
            "ServerRestart",
            Doc = "1192420",
            Blocking = true,
            Errors = "513 [Shutdown]NetworkStart is not 1 (returned); 418 no such server (raised and returned); 282 in foreground code (raised and returned)",
            Effects = "the server is offline for the restart's seconds (World.ServerStates.RestartSeconds): ServerIsOnline answers 0 meanwhile",
            Notes = "The runtime asks the server to restart. With [Shutdown]NetworkStart 1 and the server known, the simulation takes it offline for a few simulated seconds (a monitoring page sees it go and come back) and answers 0 after one turn."
        )]
        private static Value ServerRestart(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("ServerRestart");
            if (!World.Users.IsTrue(Params.Get(h, "Shutdown", "NetworkStart")))
                return W.I(AccessDenied);
            if (Server(h, A.S(a.Get(0)), a.Count > 1 ? A.S(a.Get(1)) : "") == null)
            {
                W.Raise(t, ServerNotFound);
                return W.I(ServerNotFound);
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            var w = h.World;
            string name = A.S(a.Get(0));
            World.ServerStates.Of(w).Restart(name, w.Clock.Now());
            h.Note(
                t,
                "ServerRestart("
                    + name
                    + "): offline for "
                    + World.ServerStates.RestartSeconds
                    + " simulated seconds"
            );
            return W.BlockOneTurn(t, "ServerRestart", () => W.I(0));
        }

        [Builtin(
            "ServerReload",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1192419",
            Blocking = true,
            Errors = "418 no such server (raised and returned); 282 in foreground code (raised and returned)",
            Notes = "Nothing to reload in the simulator (the World is the compiled project): the request is logged and 0 answered after one turn (bSync or not: the runtime always waits for the answer)."
        )]
        private static Value ServerReload(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("ServerReload");
            if (Server(h, A.S(a.Get(0)), a.Count > 1 ? A.S(a.Get(1)) : "") == null)
            {
                W.Raise(t, ServerNotFound);
                return W.I(ServerNotFound);
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            h.Note(t, "ServerReload(" + A.S(a.Get(0)) + ") (not simulated)");
            return W.BlockOneTurn(t, "ServerReload", () => W.I(0));
        }

        [Builtin(
            "ServerDumpKernel",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188614",
            Blocking = true,
            Errors = "418 no such server (raised and returned); 282 in foreground code (raised and returned)",
            Notes = "No kernel and no KERNEL.DAT in the simulator: logged, 0 after one turn."
        )]
        private static Value ServerDumpKernel(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("ServerDumpKernel");
            if (Server(h, A.S(a.Get(2)), a.Count > 3 ? A.S(a.Get(3)) : "") == null)
            {
                W.Raise(t, ServerNotFound);
                return W.I(ServerNotFound);
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            return W.BlockOneTurn(t, "ServerDumpKernel", () => W.I(0));
        }

        [Builtin(
            "ServerControl",
            Status = BuiltinStatus.Stub,
            Returns = "274",
            Doc = "",
            Errors = "274 anything but ('alarm', 0) (raised and returned)",
            Notes = "Undocumented. ('alarm', 0, bPrimary) calls the alarm server's AlmSetPrimary (no redundancy in the simulator: nothing changes) and gives 0; every other server type or mode raises and returns 274, as the runtime."
        )]
        private static Value ServerControl(SimHost h, CicodeTask t, ArgList a)
        {
            if (C.IEq(A.S(a.Get(0)), "alarm") && A.Short(A.I(a.Get(1))) == 0)
                return W.I(0);
            W.Raise(t, Codes.InvalidArg);
            return W.I(Codes.InvalidArg);
        }

        [Builtin(
            "ProcessRestart",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188606",
            Notes = "The runtime asks the Runtime Manager to restart the process; a simulation is never restarted: logged, 0."
        )]
        private static Value ProcessRestart(SimHost h, CicodeTask t, ArgList a)
        {
            h.Note(t, "ProcessRestart (not simulated)");
            return W.I(0);
        }

        [Builtin(
            "DumpKernel",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188586",
            Notes = "No kernel and no KERNEL.DAT in the simulator: 0."
        )]
        private static Value DumpKernel(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "SwitchConfig",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188621",
            Notes = "Never starts or focuses a program (1 Graphics Builder, 3 Studio, 4 Cicode Editor): logged, 0."
        )]
        private static Value SwitchConfig(SimHost h, CicodeTask t, ArgList a)
        {
            h.Note(t, "SwitchConfig(" + C.Inv(A.Short(A.I(a.Get(0)))) + ") (not simulated)");
            return W.I(0);
        }

        [Builtin(
            "DisplayRuntimeManager",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188585",
            Notes = "Never starts the Runtime Manager: logged, 0."
        )]
        private static Value DisplayRuntimeManager(SimHost h, CicodeTask t, ArgList a)
        {
            h.Note(t, "DisplayRuntimeManager (not simulated)");
            return W.I(0);
        }

        [Builtin(
            "ResetScreenProfile",
            Doc = "1192246",
            Notes = "No screen profile in the simulator (one simulated screen): 293, the runtime's answer without a profile (0), nothing raised."
        )]
        private static Value ResetScreenProfile(SimHost h, CicodeTask t, ArgList a) => W.I(293);

        [Builtin(
            "AnByName",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1030951",
            Errors = "360 no ActiveX object of that name on the page (raised, 0; guess on the code)",
            Notes = "No ActiveX object exists on any page in the simulator: with a page on the task's window and a name, raised and 0; else 0."
        )]
        private static Value AnByName(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            var d = DisplayBuiltins.Of(h);
            if (name.Length == 0 || d.OpenWindow(d.WindowOf(t))?.Page == null)
                return W.I(0);
            W.Raise(t, 360);
            return W.I(0);
        }

        [Builtin(
            "DspTrendInfo",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1104739",
            Errors = "274 no such trend definition (raised, 0; guess on the code)",
            Notes = "The trend definitions of the graphics libraries (V3 trend objects) are not read: every name is unknown, as failing: raised, 0."
        )]
        private static Value DspTrendInfo(SimHost h, CicodeTask t, ArgList a)
        {
            W.Raise(t, Codes.InvalidArg);
            return W.I(0);
        }

        [Builtin(
            "__ObjectMasterAN",
            Doc = "",
            Notes = "Undocumented: a process table of 16-bit values (table, index 0..749, the runtime's 0x2ee entries a table): Value 0 or less reads the entry; another value stores it and gives it back. An index of 750 or more -1."
        )]
        private static Value ObjectMasterAN(SimHost h, CicodeTask t, ArgList a)
        {
            long table = A.Short(A.I(a.Get(0))),
                index = A.UShort(A.I(a.Get(1))),
                value = A.Short(A.I(a.Get(2)));
            if (index >= 0x2ee)
                return W.I(-1);
            var x = ProcessExtras.Of(h).MasterAn;
            var key = (table, index);
            if (value < 1)
                return W.I(x.TryGetValue(key, out long v) ? A.Short(v) : 0);
            x[key] = value;
            return W.I(value);
        }
    }
}
