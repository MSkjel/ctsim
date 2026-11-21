using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Semantics
{
    /// <summary>One compiler message: code, text and source position.</summary>
    public sealed class Diagnostic
    {
        public readonly int Code;

        /// <summary>The source path (or a DBF path for label / locvar / include messages).</summary>
        public readonly string File;

        /// <summary>1-based; 0 = no position.</summary>
        public readonly int Line;
        public readonly int Col;

        /// <summary>The token text the compiler shows in braces.</summary>
        public readonly string Context;
        public readonly string Project;

        /// <summary>Offset in the source text (-1 = none).</summary>
        public readonly int Pos;

        /// <summary>Index of the token it is reported at (-1 = not known): orders messages inside
        /// a label expansion, whose tokens all carry the position of the label's use.</summary>
        public readonly int Seq;

        public Diagnostic(
            int code,
            string file = "",
            int line = 0,
            int col = 0,
            string context = "",
            string project = "",
            int pos = -1,
            int seq = -1
        )
        {
            Code = code;
            File = file ?? "";
            Line = line;
            Col = col;
            Context = context ?? "";
            Project = project ?? "";
            Pos = pos;
            Seq = seq;
        }

        /// <summary>'W1007' / 'E2041' / 'F4016'.</summary>
        public string Name => CodeName(Code);

        public string Message => Messages.Text(Code);

        public bool IsError => Code >= 2000;

        public bool IsWarning => Code < 2000;

        public static string CodeName(int code) =>
            (
                code < 2000 ? "W"
                : code < 4000 ? "E"
                : "F"
            ) + code.ToString(CultureInfo.InvariantCulture);

        public override string ToString()
        {
            string where = Line != 0 ? File + ":" + Line.ToString(CultureInfo.InvariantCulture) : File;
            string ctx = Context.Length > 0 ? " {" + Context + "}" : "";
            return Name + " " + Message + " [" + where + "]" + ctx;
        }

        public static Diagnostic? FirstError(IEnumerable<Diagnostic> diags)
        {
            foreach (var d in diags)
                if (d.IsError)
                    return d;
            return null;
        }
    }

    /// <summary>The compiler's message texts, from the install's Bin\ERRSTR.DBF ("" without it:
    /// InstallTexts).</summary>
    public static class Messages
    {
        private static Dictionary<int, string>? _texts;
        private static readonly object Lock = new object();

        public static string Text(int code)
        {
            var t = _texts;
            if (t == null)
            {
                lock (Lock)
                {
                    t = _texts ??= Load();
                }
            }
            return t.TryGetValue(code, out string? s) ? s : "";
        }

        private static Dictionary<int, string> Load() => CicodeSim.Compiled.InstallTexts.CompilerMessages();
    }
}
