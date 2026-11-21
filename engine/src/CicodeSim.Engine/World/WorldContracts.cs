using System;
using System.Collections.Generic;
using CicodeSim.Values;
using TagRef = CicodeSim.Compiled.TagRef;

// The world runs on the VM's contracts: CicodeSim.Values.Value / Types,
// CicodeSim.Compiled.TagRef and CicodeSim.Vm.ITagAccess / ITagElementAccess / ITagScale / IClock.
// TagDb implements the tag contracts on the VM's CicodeTask itself (its WriteLocal / ScaleCheck /
// TagError fields read and written directly: no interface call on the Load / Store path), and
// SimClock is the VM's IClock (+ IClockReader). Hosts and tests without a VM pass a SimpleTask
// (below: a task flag word + the tag error) to TagDb's own Read / Write overloads; both
// paths share one core that takes the flag word and hands the tag error back.
// World files import the VM types with `using CicodeSim.Values; using CicodeSim.Vm;` and
// `using TagRef = CicodeSim.Compiled.TagRef;` (not `using CicodeSim.Compiled;`:
// CicodeSim.Compiled.Dbf and CicodeSim.Project.Dbf would be ambiguous). Inside namespace
// CicodeSim.World the world's own Quality wins over CicodeSim.Values.Quality;
// code in another namespace that imports both (the tests) needs an alias or a qualified name.
namespace CicodeSim.World
{
    /// <summary>task.flags bits (CodeSetMode types 0..3, [Code] defaults all 1).</summary>
    public static class TaskFlags
    {
        public const int WriteLocal = 0x01;
        public const int ScaleCheck = 0x02;
        public const int EchoError = 0x04;
        public const int IgnoreCase = 0x08;
        public const int Default = 0x0F;
    }

    /// <summary>A task for hosts and tests without a VM: the task flag word (<see cref="TaskFlags"/>)
    /// and the tag error a read / write sets (the VM's CicodeTask carries the same as fields).</summary>
    public sealed class SimpleTask
    {
        public int Flags { get; set; }
        public bool WriteLocal => (Flags & TaskFlags.WriteLocal) != 0;
        public bool ScaleCheck => (Flags & TaskFlags.ScaleCheck) != 0;
        public int TagError { get; set; }

        public SimpleTask(int flags = TaskFlags.Default)
        {
            Flags = flags;
        }
    }

    /// <summary>The value helpers the world adds to CicodeSim.Values.Value.</summary>
    public static class WorldValues
    {
        public static Value Quality(ulong q) => Value.Int(Types.Quality, unchecked((long)q));

        public static Value Timestamp(ulong ft) => Value.Int(Types.Timestamp, unchecked((long)ft));

        public static readonly Value Void = Value.Int(Types.Void, 0);

        /// <summary>The payload as a plain object: a long, a double, a string, an unsigned 64-bit
        /// quality / timestamp, or null (VOID).</summary>
        public static object? Payload(in Value v)
        {
            if (Types.IsRealType(v.T))
                return v.D;
            if (v.T == Types.String)
                return v.S ?? "";
            if (v.T == Types.Void)
                return null;
            if (v.T == Types.Quality || v.T == Types.Timestamp)
                return unchecked((ulong)v.I);
            return v.I;
        }

        /// <summary>A TagRef of a name (tests, viewers, scenarios).</summary>
        public static TagRef Ref(
            string tag,
            string element = "",
            string item = "",
            string cluster = "",
            long lightweight = 0
        ) =>
            new TagRef(0)
            {
                Tag = tag,
                Element = element,
                Item = item,
                Cluster = cluster,
                Lightweight = lightweight,
            };
    }

    /// <summary>Compares references by identity (net48 has no ReferenceEqualityComparer).</summary>
    internal sealed class RefEq<T> : IEqualityComparer<T>
        where T : class
    {
        public static readonly RefEq<T> Instance = new RefEq<T>();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
