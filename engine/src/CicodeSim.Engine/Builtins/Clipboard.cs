using System;
using System.Text;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// Clipboard built-ins over a sandbox clipboard. The Windows clipboard is never touched.
    /// </summary>
    public static class Clipboard
    {
        private static ProcessExtras X(SimHost h) => ProcessExtras.Of(h);

        [Builtin(
            "ClipSetMode",
            Doc = "1069872",
            Effects = "sets the formats ClipWriteLn / ClipCopy put on the clipboard (1 text, 2 CSV, both added)",
            Notes = "Returns the mode it was given: the runtime stores the argument and returns with it still in ax (the help says the previous mode). The mode starts at 1."
        )]
        private static Value ClipSetMode(SimHost h, CicodeTask t, ArgList a)
        {
            long m = A.Short(A.I(a.Get(0)));
            X(h).ClipMode = (int)m;
            return W.I(m);
        }

        /// <summary>a line goes into the block being written (with CR LF); "" ends
        /// the block, which replaces the clipboard in the formats of the mode (none: the clipboard
        /// is emptied).</summary>
        private static long WriteLn(SimHost h, string text)
        {
            var x = X(h);
            x.ClipWriting ??= new StringBuilder();
            if (text.Length > 0)
            {
                x.ClipWriting.Append(text).Append("\r\n");
                return 0;
            }
            string block = x.ClipWriting.ToString();
            x.ClipWriting = null;
            x.ClipText = (x.ClipMode & 1) != 0 ? block : null;
            x.ClipCsv = (x.ClipMode & 2) != 0 ? block : null;
            x.ClipReading = null;
            return 0;
        }

        [Builtin(
            "ClipWriteLn",
            Doc = "1069873",
            Effects = "a text: added with CR LF to the block being written; '': the block replaces the sandbox clipboard",
            Notes = "The block goes in as text and / or CSV by ClipSetMode's mode (bit 1 text, bit 2 CSV; the clipboard is emptied first, so a mode with neither leaves it empty). 0 (the runtime's 256 when the clipboard cannot be opened, 272 out of memory, do not occur)."
        )]
        private static Value ClipWriteLn(SimHost h, CicodeTask t, ArgList a) =>
            W.I(WriteLn(h, A.S(a.Get(0))));

        [Builtin(
            "ClipCopy",
            Doc = "1069869",
            Effects = "puts the text and CR LF on the sandbox clipboard (as ClipWriteLn(text) then ClipWriteLn(''))",
            Notes = "The runtime is ClipWriteLn(sText) then ClipWriteLn(\"\"): the clipboard holds the text with CR LF, after any lines a ClipWriteLn block had written. 0."
        )]
        private static Value ClipCopy(SimHost h, CicodeTask t, ArgList a)
        {
            long r = WriteLn(h, A.S(a.Get(0)));
            if (r == 0)
                r = WriteLn(h, "");
            return W.I(r);
        }

        [Builtin(
            "ClipPaste",
            Doc = "1069870",
            Notes = "The clipboard's text (CF_TEXT only, not CSV), its first 255 characters; '' when there is none. It does not move ClipReadLn's position."
        )]
        private static Value ClipPaste(SimHost h, CicodeTask t, ArgList a)
        {
            string s = X(h).ClipText ?? "";
            int nul = s.IndexOf('\0');
            if (nul >= 0)
                s = s.Substring(0, nul);
            return W.S(s.Length > 255 ? s.Substring(0, 255) : s);
        }

        [Builtin(
            "ClipReadLn",
            Doc = "1069871",
            Notes = "Copies the clipboard (text, else CSV) at the first read, then gives it line by line: up to and with the next LF, at most 255 characters, cut at the first CR or LF after any it starts with (strtok: an empty line between two lines reads as its CR LF, as in the runtime); '' at the end, which drops the copy (the next read starts again from the clipboard)."
        )]
        private static Value ClipReadLn(SimHost h, CicodeTask t, ArgList a)
        {
            var x = X(h);
            if (x.ClipReading == null)
            {
                string? src = x.ClipText ?? x.ClipCsv;
                if (src == null)
                    return W.Empty;
                int nul = src.IndexOf('\0');
                x.ClipReading = nul >= 0 ? src.Substring(0, nul) : src;
                x.ClipReadPos = 0;
            }
            string buf = x.ClipReading;
            int p = x.ClipReadPos;
            // up to and with '\n', at most 255 characters
            var line = new StringBuilder();
            if (p < buf.Length)
            {
                int n = 0;
                while (n < 0xFF)
                {
                    char c = buf[p++];
                    line.Append(c);
                    if (c == '\n')
                        break;
                    n++;
                    if (p >= buf.Length)
                        break;
                }
            }
            x.ClipReadPos = p;
            string s = line.ToString();
            // strtok(buf, "\r\n"): after any leading CR / LF the first one ends the token; the
            // buffer is returned from its start, so a line that is only CR / LF stays as it is
            int k = 0;
            while (k < s.Length && (s[k] == '\r' || s[k] == '\n'))
                k++;
            if (k < s.Length)
            {
                int e = s.IndexOfAny(new[] { '\r', '\n' }, k);
                if (e >= 0)
                    s = s.Substring(0, e);
            }
            if (s.Length == 0)
            {
                x.ClipReading = null;
                x.ClipReadPos = 0;
            }
            return W.S(s);
        }
    }
}
