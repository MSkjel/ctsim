using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>The R2 corpus the corpus tests read (read only).</summary>
    public static class Corpus
    {
        public const string User = @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";
        public static readonly string ExSA = Path.Combine(User, "ExampleSA");
        public static readonly string Lib = Path.Combine(ExSA, "_LIBRARY.RDB");
        public static readonly string Page = Path.Combine(ExSA, "WTP_INTAKE_HD1080.RDB");
        public static bool Present => !LocalData.NoInstall && File.Exists(Lib);

        private static Program? _library;

        public static Program Library => _library ??= Rdb.LoadLibrary(Lib);
    }

    /// <summary>Test data made from AVEVA's projects on a computer with the install (page models,
    /// recorded runtime cases): AVEVA's content, so it is kept out of the repository (.gitignore)
    /// and a test that reads it names it (CorpusFact / ProjectFact Needs) and skips without it.</summary>
    public static class LocalData
    {
        /// <summary>The repository's root folder (the one with LICENSE), null when not found.</summary>
        public static string? Root()
        {
            string? d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "LICENSE")))
                d = Path.GetDirectoryName(d);
            return d;
        }

        /// <summary>CICODESIM_NO_INSTALL=1: the tests act as on a computer without AVEVA's
        /// install (the install's projects count as absent; a rehearsal of CI).</summary>
        public static bool NoInstall => Environment.GetEnvironmentVariable("CICODESIM_NO_INSTALL") == "1";

        public static bool Has(string relative)
        {
            string? root = Root();
            if (root == null)
                return false;
            string p = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) || Directory.Exists(p);
        }

        public static string Missing(string relative) =>
            relative + " is not here (local data made from AVEVA's projects, not in the repository)";
    }

    /// <summary>A fact about AVEVA's texts the engine reads from the install (Compiled/InstallTexts:
    /// the compiler's messages, the hardware error titles): skipped without them.</summary>
    public sealed class InstallTextsFactAttribute : FactAttribute
    {
        public InstallTextsFactAttribute()
        {
            if (
                LocalData.NoInstall
                || InstallTexts.RuntimeErrorTitles().Count == 0
                || InstallTexts.CompilerMessages().Count == 0
            )
                Skip = "the AVEVA install's error texts are not on this computer";
        }
    }

    /// <summary>A fact that needs the R2 corpus (skipped when it is not installed).</summary>
    public sealed class CorpusFactAttribute : FactAttribute
    {
        public CorpusFactAttribute()
        {
            if (!Corpus.Present)
                Skip = "R2 ExampleSA not installed";
        }

        /// <summary>Local data the test reads (a repository-relative file or folder, see
        /// <see cref="LocalData"/>): the test skips without it.</summary>
        public string? Needs
        {
            get => _needs;
            set
            {
                _needs = value;
                if (Skip == null && value != null && !LocalData.Has(value))
                    Skip = LocalData.Missing(value);
            }
        }

        private string? _needs;
    }

    /// <summary>A fact that reads the whole corpus (skipped without it or with CTG_SKIP_HEAVY=1).</summary>
    public sealed class HeavyFactAttribute : FactAttribute
    {
        public HeavyFactAttribute()
        {
            if (
                LocalData.NoInstall
                || !Directory.Exists(Corpus.User)
                || Environment.GetEnvironmentVariable("CTG_SKIP_HEAVY") == "1"
            )
                Skip = "corpus-wide check";
        }
    }

    /// <summary>TagAccess: a dictionary of name -> value; missing tags are 424 with bad quality.</summary>
    public class FakeTags : ITagAccess
    {
        public readonly Dictionary<string, Value> Values = new Dictionary<string, Value>(
            StringComparer.Ordinal
        );
        public readonly List<(string Tag, int T, object? V, bool WriteLocal)> Writes =
            new List<(string, int, object?, bool)>();

        public FakeTags(params (string Name, object Value)[] values)
        {
            foreach (var (n, v) in values)
                Values[n.ToLowerInvariant()] = CicodeVm.ValueOf(v);
        }

        public Value Read(TagRef r, CicodeTask task)
        {
            if (!Values.TryGetValue(r.Tag.ToLowerInvariant(), out var v))
            {
                task.TagError = Errors.TagNotFound;
                return Value.Long(0, 0);
            }
            return v;
        }

        public int Write(TagRef r, Value value, CicodeTask task)
        {
            Writes.Add((r.Tag, value.T, Payload(value), task.WriteLocal));
            Values[r.Tag.ToLowerInvariant()] = value;
            return 0;
        }

        public static object? Payload(Value v) =>
            Types.IsRealType(v.T) ? v.D
            : v.T == Types.String ? (object?)v.S
            : v.I;
    }

    public static class VmTestUtil
    {
        public const ulong Now = 133000000000000000UL;

        public static CicodeVm MakeVm(
            Program prog,
            ITagAccess? tags = null,
            CodeConfig? config = null,
            IClock? clock = null
        ) => new CicodeVm(prog, Registry.Default(), tags, clock ?? new ManualClock(Now), null, config);

        /// <summary>Assemble a block with build(b), run it, return the task.</summary>
        public static CicodeTask Block(
            Action<CodeBuilder> build,
            ITagAccess? tags = null,
            CodeConfig? config = null,
            bool fg = true,
            Action<ProgramBuilder>? setup = null
        )
        {
            var pb = new ProgramBuilder();
            setup?.Invoke(pb);
            var b = pb.Block();
            build(b);
            b.End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var m = MakeVm(prog, tags, config);
            var t = m.TaskForBlock(code, prog, fg);
            m.Run(t);
            return t;
        }

        public static Value ValueOf(
            Action<CodeBuilder> build,
            ITagAccess? tags = null,
            CodeConfig? config = null,
            bool fg = true
        )
        {
            var t = Block(build, tags, config, fg);
            Assert.True(t.State == TaskState.Stop, t.StateName + " " + t.HaltReason);
            return t.Result!.Value;
        }
    }
}
