using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CicodeSim.Builtins;
using CicodeSim.Tests.World;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>A synthetic World (WorldFixture: Main includes Lib; tags, equipment, alarms)
    /// with a program assembled for the test, run by WorldRun with the World's built-ins; the
    /// simulated computer's zone is UTC.</summary>
    public abstract class BuiltinTestBase : IDisposable
    {
        protected readonly TempDir Tmp = new TempDir();

        public void Dispose() => Tmp.Dispose();

        protected sealed class Sim
        {
            public WorldT W = null!;
            public WorldRun Run = null!;
            public SimHost Host = null!;
            public readonly List<string> Log = new List<string>();

            public RunResult Call(string fn, params object[] args)
            {
                var vals = new List<Value>();
                foreach (object a in args)
                    vals.Add(CicodeVm.ValueOf(a));
                return Run.Call(fn, vals);
            }

            /// <summary>The value a function returned (the task must have stopped).</summary>
            public Value Result(string fn, params object[] args)
            {
                var r = Call(fn, args);
                Assert.True(r.Task.State == TaskState.Stop, r.Outcome + " " + r.Task.HaltReason);
                return r.Task.Result ?? Value.Nothing;
            }

            /// <summary>Run a function as a foreground task to its end (no scheduler).</summary>
            public CicodeTask Foreground(string fn)
            {
                var t = Run.Vm.TaskForCall(fn, Array.Empty<Value>(), foreground: true);
                Run.Vm.Run(t);
                return t;
            }
        }

        protected Sim Make(Action<ProgramBuilder> build, string? start = null)
        {
            var w = WorldFixture.MakeWorld(Tmp.Path, start);
            w.Start();
            var pb = new ProgramBuilder();
            build(pb);
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

        /// <summary>Push a constant (int / long, double, string).</summary>
        protected static CodeBuilder Push(CodeBuilder b, object a)
        {
            switch (a)
            {
                case int i:
                    return b.ConstInt(i);
                case long l:
                    return b.ConstInt(l);
                case double d:
                    return b.ConstReal(d);
                case string s:
                    return b.ConstStr(s);
                case Value v when v.T == Types.Timestamp || v.T == Types.Quality:
                    return b.ConstU64(v.T, unchecked((ulong)v.I));
            }
            throw new ArgumentException("constant " + a);
        }

        /// <summary>A function <paramref name="fn"/>() that returns builtin(args...).</summary>
        protected static void Ret(ProgramBuilder pb, string fn, string builtin, params object[] args)
        {
            var f = pb.Function(fn, "", Types.String);
            foreach (object a in args)
                Push(f, a);
            f.Func(builtin, args.Length).RetValue().End();
        }

        /// <summary>A function that calls a built-in and drops its result.</summary>
        protected static void Do(ProgramBuilder pb, string fn, string builtin, params object[] args)
        {
            var f = pb.Function(fn);
            foreach (object a in args)
                Push(f, a);
            f.Func(builtin, args.Length).Op("Pop").Ret().End();
        }

        protected static string S(in Value v) =>
            v.T == Types.String ? v.S ?? "" : Conv.ToStr(v, NoErr.Instance).S ?? "";

        protected static long I(in Value v) => v.I;

        protected static void InCulture(string name, Action a)
        {
            var old = CultureInfo.CurrentCulture;
            var oldUi = CultureInfo.CurrentUICulture;
            try
            {
                var c = new CultureInfo(name);
                CultureInfo.CurrentCulture = c;
                CultureInfo.CurrentUICulture = c;
                a();
            }
            finally
            {
                CultureInfo.CurrentCulture = old;
                CultureInfo.CurrentUICulture = oldUi;
            }
        }
    }
}
