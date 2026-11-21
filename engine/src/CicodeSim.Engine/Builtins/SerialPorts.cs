using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    internal sealed class SerialPort
    {
        public readonly string Name;
        public readonly string Peer;
        public readonly bool Shared;
        public readonly Queue<char> Received = new Queue<char>();
        public bool Closed;

        public SerialPort(string name, string peer, bool shared)
        {
            Name = name;
            Peer = peer;
            Shared = shared;
        }
    }

    public sealed partial class SimHost
    {
        internal readonly HandleTable<SerialPort> SerialPorts = new HandleTable<SerialPort>();
    }

    internal static class SerialPorts
    {
        private static SerialPort? Find(SimHost h, string name)
        {
            foreach (var (_, port) in h.SerialPorts.Live())
                if (C.IEq(port.Name, name))
                    return port;
            return null;
        }

        private static bool Configured(SimHost h, string name)
        {
            if (h.World == null)
                return false;
            foreach (var (_, row) in h.World.Unit.Rows("ports.dbf"))
                if (C.IEq(row.Get("NAME"), name))
                    foreach (var (_, board) in h.World.Unit.Rows("boards.dbf"))
                        if (
                            C.IEq(row.Get("BOARD"), board.Get("NAME"))
                            && C.IEq(row.Get("SERVER"), board.Get("SERVER"))
                        )
                            return true;
            return false;
        }

        [Builtin(
            "ComOpen",
            Doc = "1073044",
            Notes = "Opens a project port or an endpoint configured in [SimulatedSerial]. The parameter value names its receiving endpoint. Returns -1 if the port is missing or already open."
        )]
        private static Value Open(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            long mode = A.I(a.Get(1));
            if (name.Length == 0 || (mode != 0 && mode != 1) || Find(h, name) != null)
                return W.I(-1);
            string peer = Params.Get(h, "SimulatedSerial", name) ?? "";
            if (peer.Length == 0 && !Configured(h, name))
                return W.I(-1);
            return W.I(h.SerialPorts.Add(new SerialPort(name, peer, mode == 1)));
        }

        [Builtin("ComClose", Doc = "1073043", Errors = "269 invalid port handle")]
        private static Value Close(SimHost h, CicodeTask t, ArgList a)
        {
            long handle = A.I(a.Get(0));
            var port = h.SerialPorts.Get(handle);
            if (port == null)
                return W.I(Codes.BadHandle);
            port.Closed = true;
            h.SerialPorts.Free(handle);
            return W.I(0);
        }

        [Builtin(
            "ComReset",
            Doc = "1073046",
            Effects = "clears the receive buffer",
            Errors = "269 invalid port handle"
        )]
        private static Value Reset(SimHost h, CicodeTask t, ArgList a)
        {
            var port = h.SerialPorts.Get(A.I(a.Get(0)));
            if (port == null)
                return W.I(Codes.BadHandle);
            port.Received.Clear();
            return W.I(0);
        }

        [Builtin(
            "ComRead",
            Doc = "1073045",
            Blocking = true,
            Notes = "Reads up to 128 characters. A negative timeout waits until input or closure. Both the data and length arguments are updated."
        )]
        private static Value Read(SimHost h, CicodeTask t, ArgList a) => Transfer(h, t, a, false);

        [Builtin(
            "ComWrite",
            Doc = "1073047",
            Blocking = true,
            Notes = "Sends up to 128 characters to the configured simulated peer and resets the length argument."
        )]
        private static Value Write(SimHost h, CicodeTask t, ArgList a) => Transfer(h, t, a, true);

        private static Value Transfer(SimHost h, CicodeTask t, ArgList a, bool write)
        {
            var port = h.SerialPorts.Get(A.I(a.Get(0)));
            if (port == null)
                return W.I(Codes.BadHandle);
            Value buffer = a.Get(1),
                count = a.Get(2);
            long length = A.I(count),
                timeout = A.I(a.Get(3));
            if (length < 0 || length > 128)
                return W.I(Codes.OutOfRange);
            bool timed = timeout > 0 && !port.Shared;
            bool available() => write || length == 0 || port.Received.Count > 0;
            if (t.Foreground && timeout != 0 && (timed || !available()))
                return W.I(Codes.FgBlock);
            if (write)
            {
                string data = A.Buffer(buffer);
                var peer = Find(h, port.Peer);
                for (int i = 0; i < length; i++)
                    peer?.Received.Enqueue(i < data.Length ? data[i] : '\0');
                W.Out(t, count, 0);
            }
            Value Finish(bool expired)
            {
                if (port.Closed)
                    return W.I(Codes.OutOfRange);
                if (!write)
                {
                    var chars = new char[Math.Min((int)length, port.Received.Count)];
                    for (int i = 0; i < chars.Length; i++)
                        chars[i] = port.Received.Dequeue();
                    W.Out(t, buffer, new string(chars));
                    W.Out(t, count, chars.Length);
                }
                if (expired)
                    t.RaiseError(7, false);
                return W.I(expired ? 7 : 0);
            }
            if (timeout == 0 || (!timed && available()))
                return Finish(false);
            var clock = t.Vm.Clock;
            double deadline = timeout > 0 ? clock.MonotonicMs() + timeout * 1000.0 : double.PositiveInfinity;
            t.Wait = new Wait(TaskState.Wait, write ? "ComWrite" : "ComRead")
            {
                Poll = () => port.Closed || (!timed && available()) || clock.MonotonicMs() >= deadline,
                Result = () => Finish(timed || !available()),
            };
            return Value.Block;
        }
    }
}
