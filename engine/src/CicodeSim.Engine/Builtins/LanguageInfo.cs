using System;
using System.Collections.Generic;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>The display client's language (GetLanguage).</summary>
    public static class LanguageInfo
    {
        /// <summary>The language / country codes of the Windows locale names the projects use
        /// (the compiled _lan&lt;code&gt;.RDB files name them: _lande-DE ...;
        /// 'Norwegian' compiles to _lannb). Other names give '' (guess: the runtime asks Windows).</summary>
        private static readonly Dictionary<string, string> LocaleCodes = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["English"] = "en",
            ["English(United States)"] = "en-US",
            ["English(United Kingdom)"] = "en-GB",
            ["English(Australia)"] = "en-AU",
            ["German(Germany)"] = "de-DE",
            ["French(France)"] = "fr-FR",
            ["Spanish(Spain)"] = "es-ES",
            ["Italian(Italy)"] = "it-IT",
            ["Japanese(Japan)"] = "ja-JP",
            ["Korean(Korea)"] = "ko-KR",
            ["Chinese(Simplified, PRC)"] = "zh-CN",
            ["Russian(Russia)"] = "ru-RU",
            ["Portuguese(Brazil)"] = "pt-BR",
            ["Hebrew(Israel)"] = "he-IL",
            ["Norwegian"] = "nb",
        };

        private static string CodeOf(string name) =>
            LocaleCodes.TryGetValue(Norm(name), out string? c) ? c : "";

        /// <summary>'German (Germany)' and 'German(Germany)' alike (the project tables write no blank).</summary>
        private static string Norm(string name) => CitectText.Trim(name).Replace(" (", "(");

        /// <summary>The languages the unit defines (lang.dbf's Language column, every project).</summary>
        internal static List<string> Defined(SimHost h)
        {
            var o = new List<string>();
            var w = h.World;
            if (w == null)
                return o;
            foreach (var (_, r) in w.Unit.Rows("lang.dbf"))
            {
                int k = r.Header.IndexOfLoose("LANGUAGE");
                if (k < 0)
                    continue;
                string n = CitectText.Trim(r[k]);
                if (
                    n.Length > 0
                    && !o.Exists(x => string.Equals(Norm(x), Norm(n), StringComparison.OrdinalIgnoreCase))
                )
                    o.Add(n);
            }
            return o;
        }

        private static void Ensure(SimHost h)
        {
            var x = ProcessExtras.Of(h);
            if (x.LanguageName != null)
                return;
            Load(h);
        }

        /// <summary>The language at start: [Language]LocalLanguage, else English.</summary>
        private static void Load(SimHost h)
        {
            var x = ProcessExtras.Of(h);
            string? v = Params.Get(h, "Language", "LocalLanguage");
            string name = v != null && CitectText.Trim(v).Length > 0 ? CitectText.Trim(v) : "English";
            x.LanguageName = C.Cut(name, 253);
            x.LanguageCode = CodeOf(name);
            var lang = DisplayBuiltins.Of(h).Language; // what the windows draw
            lang.Name = name;
            lang.CaseSensitive = CaseSensitive(h);
        }

        /// <summary>[Language]CaseSensitive. Default 0: '@(native)' matches its row without case.</summary>
        private static bool CaseSensitive(SimHost h)
        {
            string v = CitectText.Trim(Params.Get(h, "Language", "CaseSensitive") ?? "");
            return long.TryParse(v, out long n)
                ? n != 0
                : v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Use a language the project defines (or English); false when it is not one.</summary>
        internal static bool Select(SimHost h, string name)
        {
            string n = CitectText.Trim(name);
            string? hit = null;
            if (string.Equals(Norm(n), "English", StringComparison.OrdinalIgnoreCase))
                hit = "English";
            else
                foreach (string d in Defined(h))
                    if (string.Equals(Norm(d), Norm(n), StringComparison.OrdinalIgnoreCase))
                    {
                        hit = d;
                        break;
                    }
            if (hit == null)
                return false;
            var x = ProcessExtras.Of(h);
            x.LanguageName = C.Cut(hit, 253);
            x.LanguageCode = CodeOf(hit);
            var lang = DisplayBuiltins.Of(h).Language;
            lang.Name = hit;
            lang.CaseSensitive = CaseSensitive(h);
            return true;
        }

        [Builtin(
            "GetLanguage",
            Doc = "1192338",
            Errors = "274 a type other than 0 / 1 (raised, '')",
            Notes = "0 the current language's name, 1 its language / country code ('en-US'). The language is [Language]LocalLanguage at start, else 'English' (code 'en': the neutral English the compiler writes as _lanen.RDB; verify the texts live), then the language a UserLogin names (Login does not change it in the simulator). Codes of the Windows locale names the R2 projects use; another name gives code ''."
        )]
        private static Value GetLanguage(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.UShort(A.I(a.Get(0)));
            if (type >= 2)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            Ensure(h);
            var x = ProcessExtras.Of(h);
            return W.S(type == 0 ? x.LanguageName! : x.LanguageCode!);
        }

        [Builtin(
            "SetLanguage",
            Doc = "",
            Effects = "the language becomes [Language]LocalLanguage again",
            Notes = "Undocumented. The runtime ignores its arguments and loads the language of the [Language] parameters again: here the current language becomes [Language]LocalLanguage (else English). Returns 0 (the runtime's function returns nothing: guess)."
        )]
        private static Value SetLanguage(SimHost h, CicodeTask t, ArgList a)
        {
            Load(h);
            return W.I(0);
        }
    }
}
