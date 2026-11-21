using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Tests.World;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Direct calls of the data built-ins on the synthetic World.</summary>
    public abstract class DataTestBase : BuiltinTestBase
    {
        /// <summary>The result of one built-in call made by a task of the World's VM: a call
        /// that waits one turn gives the value it computes when the task wakes.</summary>
        protected sealed class Res
        {
            public Value V;
            public CicodeTask Task = null!;
            public bool Blocked;
            public int Error => Task.Error;
            public string S => V.T == Types.String ? V.S ?? "" : Conv.ValueText(V, out _);
            public long I => A.I(V);
            public double D => A.R(V);
        }

        protected static Res Call(Sim s, string name, params object[] args) => CallIn(s, false, name, args);

        protected static Res CallFg(Sim s, string name, params object[] args) => CallIn(s, true, name, args);

        protected static Res CallIn(Sim s, bool fg, string name, params object[] args)
        {
            var b = Func0Table.Default.Lookup(name);
            Assert.True(b != null, name);
            var t = new CicodeTask(s.Run.Vm, "test", fg);
            var vals = new List<Value>();
            foreach (object a in args)
                vals.Add(a is Value v ? v : CicodeVm.ValueOf(a));
            var r = s.Run.Vm.Builtins!.Call(b!.Index, t, ArgList.Of(vals.ToArray()));
            Assert.False(r.IsNotSimulated, name + " is not simulated");
            bool blocked = false;
            if (r.IsBlock)
            {
                blocked = true;
                r = t.Wait!.Result!() ?? Value.Nothing;
            }
            return new Res
            {
                V = r,
                Task = t,
                Blocked = blocked,
            };
        }

        /// <summary>A by-reference argument (a VARIANT pointing at a value) and what it holds.</summary>
        protected static Value Ref(Value initial) => Value.Variant(new ValueRef(initial));

        protected static Value Held(Value r) => ((ValueRef)r.O!).Value;

        /// <summary>The synthetic install's Main and Lib folders (WorldFixture.MakeInstall).</summary>
        protected string MainDir => Path.Combine(Tmp.Path, "inst", "User", "Main");
        protected string LibDir => Path.Combine(Tmp.Path, "inst", "User", "Lib");

        protected static void Dbf(string path, string[] fields, params string[][] rows)
        {
            var list = new List<Dictionary<string, string>>();
            foreach (var r in rows)
            {
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < fields.Length && i < r.Length; i++)
                    d[fields[i]] = r[i];
                list.Add(d);
            }
            WorldFixture.WriteDbf(path, WorldFixture.F(60, fields), list);
        }

        /// <summary>The synthetic World with extra tables written into the install before the World is made.</summary>
        protected Sim MakeData(Action? tables = null, Action<ProgramBuilder>? build = null)
        {
            CicodeSim.World.World.ClearCaches();
            string main = WorldFixture.MakeInstall(Tmp.Path);
            tables?.Invoke();
            var w = new CicodeSim.World.World(main, start: WorldFixture.DefaultStart);
            w.Start();
            var pb = new ProgramBuilder();
            build?.Invoke(pb);
            var run = new WorldRun(w, pb.Build());
            var h = WorldBuiltins.HostOf(run.Vm.Builtins)!;
            h.TimeZone = TimeZoneInfo.Utc;
            var s = new Sim
            {
                W = w,
                Run = run,
                Host = h,
            };
            h.Log = x => s.Log.Add(x);
            return s;
        }

        protected const string R2 = WorldFixture.R2;

        protected static bool HaveR2 =>
            Directory.Exists(R2) && File.Exists(Path.Combine(R2, "ExampleSA", "_LIBRARY.RDB"));
    }
}
