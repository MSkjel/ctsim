using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>Hardware error codes the world built-ins use (doc 1130208 titles).</summary>
    public static class Codes
    {
        public const int OutOfRange = 257;
        public const int FileNotFound = 261;
        public const int FieldNotFound = 266;
        public const int BadHandle = 269;
        public const int NoHandles = 271;
        public const int InvalidArg = 274;
        public const int NoPrivilege = 276;
        public const int FgBlock = 282;
        public const int OutOfBuffers = 288;
        public const int NameNotFound = 289;
        public const int InvalidFunction = 292;
        public const int QueueEmpty = 296;
        public const int SemOwnerDied = 297;
        public const int SemTimeout = 298;
        public const int NotInVersion = 317;
        public const int PropertyNotFound = 348;
        public const int TagNotFound = 424;
        public const int TypeMismatch = 433;
        public const int BrowseSessionNotFound = 410;
        public const int BrowseEof = 412;
        public const int BrowseFieldNotFound = 413;
        public const int PropertyNotReady = 432;

        /// <summary>'Tag name exceed length limit' (a name of 255+ characters).</summary>
        public const int TagNameTooLong = 0x22d;
    }

    /// <summary>A task waiting on a semaphore or queue: valid while the task still sits in
    /// that wait (a suspend, kill or resume takes it out).</summary>
    internal sealed class Waiter
    {
        public readonly CicodeTask Task;
        public Wait Wait = null!;
        public bool Done;
        public Value Result;

        /// <summary>QueRead's by-reference Type and Str.</summary>
        public Value TypeRef,
            StrRef;

        public Waiter(CicodeTask task)
        {
            Task = task;
        }

        public bool Valid => !Done && !Task.Done && ReferenceEquals(Task.Wait, Wait);
    }

    /// <summary>One semaphore: name, mode, count, pending error, waiter queue, owner
    /// task.</summary>
    internal sealed class Semaphore
    {
        public string Name = "";
        public int Mode;

        /// <summary>0 = free (signaled); the owner's recursive count otherwise.</summary>
        public int Count;

        /// <summary>The error the next SemWait that takes it returns (297 after the owner died).</summary>
        public int Pending;
        public CicodeTask? Owner;
        public readonly LinkedList<Waiter> Waiters = new LinkedList<Waiter>();

        /// <summary>The owner ended holding it (the runtime does this when the task is freed:
        /// count 0, every waiter released with 297).</summary>
        public void CheckOwner()
        {
            if (Count > 0 && Owner != null && Owner.Done)
            {
                Pending = Codes.SemOwnerDied;
                Count = 0;
                foreach (var w in Waiters)
                    if (w.Valid)
                    {
                        w.Done = true;
                        w.Result = W.I(Codes.SemOwnerDied);
                    }
                Waiters.Clear();
            }
        }
    }

    /// <summary>One element of a Cicode queue.</summary>
    internal readonly struct QueItem
    {
        public readonly long Type;
        public readonly string Str;

        public QueItem(long type, string str)
        {
            Type = type;
            Str = str;
        }
    }

    /// <summary>One queue: name, mode, element queue, list of waiting readers.</summary>
    internal sealed class CicodeQueue
    {
        public string Name = "";
        public int Mode;
        public readonly LinkedList<QueItem> Items = new LinkedList<QueItem>();
        public readonly LinkedList<Waiter> Readers = new LinkedList<Waiter>();

        public bool HasReaders
        {
            get
            {
                for (var n = Readers.First; n != null; )
                {
                    var next = n.Next;
                    if (!n.Value.Valid)
                        Readers.Remove(n);
                    n = next;
                }
                return Readers.Count > 0;
            }
        }
    }

    /// <summary>Semaphores and queues, per process, served first in first out.</summary>
    public static class Sync
    {
        /// <summary>The name characters SemOpen / QueOpen keep (letters, digits,
        /// '\', '_', and '.' for queues; up to 31 characters; stops at the first other character).</summary>
        internal static string NameCopy(string s, bool dot)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                bool ok = char.IsLetterOrDigit(c) || c == '\\' || c == '_' || (dot && c == '.');
                if (!ok)
                    break;
                if (sb.Length < 31)
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static Semaphore? Sem(SimHost h, in Value v) => h.Sems.Get(A.Short(A.I(v)));

        [Builtin(
            "SemOpen",
            Doc = "1193659",
            Effects = "creates a semaphore of the process",
            Errors = "274 bad mode or a name that is not a Cicode name (returns -1)",
            Notes = "Mode 0 opens the named one (-1 when none), 1 always creates a new one, 2 opens or creates; names compare case-insensitively; a new semaphore is signaled (free)."
        )]
        private static Value SemOpen(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            long mode = A.Short(A.I(a.Get(1)));
            if (mode != 0 && mode != 1 && mode != 2)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            if (mode == 0 || mode == 2)
            {
                foreach (var (hd, s) in h.Sems.Live())
                    if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                        return W.I(hd);
                if (mode == 0)
                    return W.I(-1);
            }
            string trimmed = A.SkipSpace(name);
            string copy = NameCopy(trimmed, false);
            if (copy != name)
            {
                W.Raise(t, Codes.InvalidArg); // the copied name differs from the argument
                return W.I(-1);
            }
            return W.I(h.Sems.Add(new Semaphore { Name = copy, Mode = (int)mode }));
        }

        [Builtin(
            "SemClose",
            Doc = "1193658",
            Effects = "closes the semaphore; its waiters wake with 297",
            Errors = "269 bad handle (returned)"
        )]
        private static Value SemClose(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.Short(A.I(a.Get(0)));
            var s = h.Sems.Get(hd);
            if (s == null)
                return W.I(Codes.BadHandle);
            foreach (var w in s.Waiters)
                if (w.Valid)
                {
                    w.Done = true;
                    w.Result = W.I(Codes.SemOwnerDied); // 0x129 handed to each waiter
                }
            s.Waiters.Clear();
            h.Sems.Free(hd);
            return W.I(0);
        }

        [Builtin(
            "SemSignal",
            Doc = "1193660",
            Effects = "releases the semaphore to its first waiter",
            Errors = "269 bad handle; 276 not the owner (returned)",
            Notes = "Only the owner signals; a recursive owner releases when its count reaches 0; the first waiting task then owns it and its SemWait returns 0. Does not block (the help calls it blocking)."
        )]
        private static Value SemSignal(SimHost h, CicodeTask t, ArgList a)
        {
            var s = Sem(h, a.Get(0));
            if (s == null)
                return W.I(Codes.BadHandle);
            s.CheckOwner();
            if (s.Count == 0 || s.Owner != t)
                return W.I(Codes.NoPrivilege);
            s.Count--;
            if (s.Count > 0)
                return W.I(0);
            s.Count = 0;
            s.Owner = null;
            s.Pending = 0;
            while (s.Waiters.Count > 0)
            {
                var w = s.Waiters.First!.Value;
                s.Waiters.RemoveFirst();
                if (!w.Valid)
                    continue;
                s.Count = 1;
                s.Owner = w.Task;
                w.Done = true;
                w.Result = W.I(s.Pending);
                break;
            }
            return W.I(0);
        }

        [Builtin(
            "SemWait",
            Doc = "1193661",
            Blocking = true,
            Errors = "269 bad handle; 282 in foreground code; 298 timeout or Timeout 0 and taken; 297 owner died (returned)",
            Notes = "Timeout in seconds of the wall clock (_time32 + Timeout, checked by a timer task); -1 (any negative) waits for ever; the owner taking it again counts up (recursive)."
        )]
        private static Value SemWait(SimHost h, CicodeTask t, ArgList a)
        {
            var s = Sem(h, a.Get(0));
            if (s == null)
                return W.I(Codes.BadHandle);
            long timeout = A.I(a.Get(1));
            s.CheckOwner();
            if (s.Count == 0)
            {
                s.Count = 1;
                s.Owner = t;
                int p = s.Pending;
                s.Pending = 0;
                return W.I(p);
            }
            if (s.Owner == t)
            {
                s.Count++;
                return W.I(0);
            }
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            if (timeout == 0)
                return W.I(Codes.SemTimeout);
            var clock = t.Vm.Clock;
            long? deadline = timeout > 0 ? Clocks.UnixSeconds(clock) + timeout : (long?)null;
            var me = new Waiter(t);
            var wait = new Wait(TaskState.Wait, "SemWait(" + s.Name + ")");
            me.Wait = wait;
            s.Waiters.AddLast(me);
            wait.Poll = () =>
            {
                s.CheckOwner();
                return me.Done || (deadline != null && Clocks.UnixSeconds(clock) >= deadline.Value);
            };
            wait.Result = () =>
            {
                if (me.Done)
                    return me.Result;
                me.Done = true;
                s.Waiters.Remove(me);
                return W.I(Codes.SemTimeout);
            };
            t.Wait = wait;
            return Value.Block;
        }

        // ------------------------------------------------------------------ queues
        private static CicodeQueue? Que(SimHost h, in Value v) => h.Queues.Get(A.Short(A.I(v)));

        [Builtin(
            "QueOpen",
            Doc = "1193653",
            Effects = "creates a queue of the process",
            Errors = "274 bad mode (raised, returns -1)",
            Notes = "Mode & 3: 0 opens the named one (-1 when none), 1 always creates, 2 opens or creates; + 4 allows several blocked readers. The name keeps its leading name characters (letters, digits, '.', '\\', '_', 31 at most)."
        )]
        private static Value QueOpen(SimHost h, CicodeTask t, ArgList a)
        {
            long mode = A.I(a.Get(1)) & 0xFFFF;
            string name = NameCopy(A.SkipSpace(A.S(a.Get(0))), true);
            long m = mode & 3;
            if (m == 3)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            if (m == 0 || m == 2)
            {
                foreach (var (hd, q) in h.Queues.Live())
                    if (string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase))
                        return W.I(hd);
                if (m == 0)
                    return W.I(-1);
            }
            return W.I(h.Queues.Add(new CicodeQueue { Name = name, Mode = (int)mode }));
        }

        [Builtin(
            "QueClose",
            Doc = "1193651",
            Effects = "closes the queue; waiting readers wake with 0, Type 0 and Str ''",
            Errors = "269 bad handle (returned)"
        )]
        private static Value QueClose(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.Short(A.I(a.Get(0)));
            var q = h.Queues.Get(hd);
            if (q == null)
                return W.I(Codes.BadHandle);
            h.QueueElements -= q.Items.Count;
            q.Items.Clear();
            foreach (var r in q.Readers)
                if (r.Valid)
                {
                    r.Done = true;
                    r.Result = W.I(0);
                    W.Out(r.Task, r.TypeRef, 0);
                    W.Out(r.Task, r.StrRef, "");
                }
            q.Readers.Clear();
            h.Queues.Free(hd);
            return W.I(0);
        }

        [Builtin(
            "QueWrite",
            Doc = "1193656",
            Effects = "appends (Type, Str) to the queue, or hands it to the first waiting reader",
            Errors = "269 bad handle; 288 out of queue buffers (both raised and returned)",
            Notes = "Str is cut to 255 characters. Does not block (the help calls it blocking)."
        )]
        private static Value QueWrite(SimHost h, CicodeTask t, ArgList a)
        {
            var q = Que(h, a.Get(0));
            if (q == null)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(Codes.BadHandle);
            }
            long type = A.I(a.Get(1));
            string str = A.Cap(A.S(a.Get(2)));
            while (q.Readers.Count > 0)
            {
                var r = q.Readers.First!.Value;
                q.Readers.RemoveFirst();
                if (!r.Valid)
                    continue;
                r.Done = true;
                r.Result = W.I(0);
                W.Out(r.Task, r.TypeRef, type);
                W.Out(r.Task, r.StrRef, str);
                return W.I(0);
            }
            if (h.QueueElements >= h.QueueBuffers)
            {
                W.Raise(t, Codes.OutOfBuffers);
                return W.I(Codes.OutOfBuffers);
            }
            q.Items.AddLast(new QueItem(type, str));
            h.QueueElements++;
            return W.I(0);
        }

        [Builtin(
            "QueRead",
            Doc = "1193655",
            Blocking = true,
            Errors = "269 bad handle; 296 empty with Mode 0; 282 in foreground code, or a second blocked reader of a queue opened without mode 4 (returned)",
            Notes = "Mode 0 never blocks; any other Mode waits for the next QueWrite (first waiting reader first)."
        )]
        private static Value QueRead(SimHost h, CicodeTask t, ArgList a)
        {
            var q = Que(h, a.Get(0));
            if (q == null)
                return W.I(Codes.BadHandle);
            Value typeRef = a.Get(1),
                strRef = a.Get(2);
            long mode = A.Short(A.I(a.Get(3)));
            if (q.Items.Count > 0)
            {
                var it = q.Items.First!.Value;
                q.Items.RemoveFirst();
                h.QueueElements--;
                W.Out(t, typeRef, it.Type);
                W.Out(t, strRef, it.Str);
                return W.I(0);
            }
            if (mode == 0)
                return W.I(Codes.QueueEmpty);
            if ((q.Mode & 4) == 0 && q.HasReaders)
                return W.I(Codes.FgBlock);
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            var me = new Waiter(t) { TypeRef = typeRef, StrRef = strRef };
            var wait = new Wait(TaskState.Wait, "QueRead(" + q.Name + ")") { Poll = () => me.Done };
            wait.Result = () =>
            {
                if (me.Done)
                    return me.Result;
                // woken without an element (TaskResume): returned 0 when it
                // blocked (a QueWrite / QueClose sets the result again), Type and Str unchanged
                me.Done = true;
                q.Readers.Remove(me);
                return W.I(0);
            };
            me.Wait = wait;
            q.Readers.AddLast(me);
            t.Wait = wait;
            return Value.Block;
        }

        [Builtin("QueLength", Doc = "1193652", Errors = "274 bad handle (raised, returns 0)")]
        private static Value QueLength(SimHost h, CicodeTask t, ArgList a)
        {
            var q = Que(h, a.Get(0));
            if (q == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            return W.I(q.Items.Count);
        }

        [Builtin(
            "QuePeek",
            Doc = "1193654",
            Effects = "with Mode + 8 removes the element found",
            Errors = "269 bad handle; 296 not found (returned)",
            Notes = "Mode 1 matches Str (case-insensitive), 4 case-sensitive, 2 matches Type (with 1 / 4: both), 16 takes the element at offset Type, 0 the first; the element found is copied into Type and Str."
        )]
        private static Value QuePeek(SimHost h, CicodeTask t, ArgList a)
        {
            var q = Que(h, a.Get(0));
            if (q == null)
                return W.I(Codes.BadHandle);
            Value typeRef = a.Get(1),
                strRef = a.Get(2);
            long mode = A.I(a.Get(3));
            long wantType = A.I(typeRef);
            string wantStr = A.S(strRef);
            int k = 0;
            for (var n = q.Items.First; n != null; n = n.Next, k++)
            {
                var it = n.Value;
                bool hit;
                if ((mode & 5) != 0)
                {
                    bool strEq =
                        (mode & 4) != 0
                            ? string.CompareOrdinal(wantStr, it.Str) == 0
                            : string.Equals(wantStr, it.Str, StringComparison.OrdinalIgnoreCase);
                    hit = strEq && ((mode & 2) == 0 || wantType == it.Type);
                }
                else if ((mode & 2) != 0)
                    hit = wantType == it.Type;
                else if ((mode & 0x10) != 0)
                    hit = k == wantType;
                else
                    hit = true;
                if (!hit)
                    continue;
                W.Out(t, typeRef, it.Type);
                W.Out(t, strRef, it.Str);
                if ((mode & 8) != 0)
                {
                    q.Items.Remove(n);
                    h.QueueElements--;
                }
                return W.I(0);
            }
            return W.I(Codes.QueueEmpty);
        }
    }
}
