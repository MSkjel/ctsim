using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace CicodeSim.Compiled
{
    /// <summary>The texts the engine shows that are AVEVA's, read from the Plant SCADA install on
    /// this computer (the engine never ships them): the compiler's messages (Bin\ERRSTR.DBF) and
    /// the runtime's hardware error titles (the help's table of Cicode errors). Without an
    /// install a message is its code alone.</summary>
    public static class InstallTexts
    {
        public const string BinDefault = @"C:\Program Files (x86)\AVEVA Plant SCADA\Bin";
        public const string HelpDefault =
            @"C:\ProgramData\AVEVA\Product Documentation\Plant SCADA\content\en";

        /// <summary>The help page with the table of Cicode (hardware) errors.</summary>
        public const string ErrorTablePage = "1130208.html";

        /// <summary>The install's Bin folder: $CICODESIM_AVEVA_BIN, else the folder of
        /// $CICODESIM_FUNC0, else the default install's.</summary>
        public static string BinDir()
        {
            string? bin = EngineEnv.Get("AVEVA_BIN");
            if (bin != null)
                return bin;
            string? func0 = EngineEnv.Get("FUNC0");
            if (func0 != null)
                return Path.GetDirectoryName(func0) ?? BinDefault;
            return BinDefault;
        }

        /// <summary>The help's content folder: $CICODESIM_AVEVA_HELP, else the default install's.</summary>
        public static string HelpDir() => EngineEnv.Get("AVEVA_HELP") ?? HelpDefault;

        /// <summary>The compiler's message text per code (ERRSTR.DBF's E_CODE, E_STRING; the
        /// first row of a code wins; <paramref name="errstr"/>: that table, else the install's);
        /// empty without it.</summary>
        public static Dictionary<int, string> CompilerMessages(string? errstr = null)
        {
            var d = new Dictionary<int, string>();
            string path = errstr ?? Path.Combine(BinDir(), "ERRSTR.DBF");
            if (!File.Exists(path))
                return d;
            try
            {
                foreach (var r in Dbf.CitectRows(path))
                {
                    if (
                        r.TryGetValue("E_CODE", out var c)
                        && int.TryParse(
                            c.Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int code
                        )
                        && r.TryGetValue("E_STRING", out var s)
                        && !d.ContainsKey(code)
                    )
                        d[code] = s.Trim();
                }
            }
            catch (Exception e)
                when (e is IOException || e is UnauthorizedAccessException || e is FormatException)
            {
                EngineNotes.Swallowed("InstallTexts.CompilerMessages", e);
            }
            return d;
        }

        private static readonly Regex Row = new Regex(
            @"<tr[^>]*>(.*?)</tr>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase
        );
        private static readonly Regex Cell = new Regex(
            @"<t[dh][^>]*>(.*?)</t[dh]>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase
        );
        private static readonly Regex Tag = new Regex(@"<[^>]+>", RegexOptions.Singleline);
        private static readonly Regex Space = new Regex(@"\s+");

        /// <summary>The title per hardware error code (the help table's Error No. and Error
        /// title columns; <paramref name="page"/>: that page, else the install's help); empty
        /// without it.</summary>
        public static Dictionary<long, string> RuntimeErrorTitles(string? page = null)
        {
            var d = new Dictionary<long, string>();
            string path = page ?? Path.Combine(HelpDir(), ErrorTablePage);
            if (!File.Exists(path))
                return d;
            try
            {
                string html = File.ReadAllText(path);
                foreach (Match row in Row.Matches(html))
                {
                    var cells = Cell.Matches(row.Groups[1].Value);
                    if (cells.Count < 2)
                        continue;
                    string no = Text(cells[0].Groups[1].Value);
                    if (
                        long.TryParse(no, NumberStyles.Integer, CultureInfo.InvariantCulture, out long code)
                        && !d.ContainsKey(code)
                    )
                        d[code] = Text(cells[1].Groups[1].Value);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                EngineNotes.Swallowed("InstallTexts.RuntimeErrorTitles", e);
            }
            return d;
        }

        private static string Text(string cellHtml) =>
            Space.Replace(WebUtility.HtmlDecode(Tag.Replace(cellHtml, " ")), " ").Trim();
    }
}
