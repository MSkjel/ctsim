using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.World;
using TagRef = CicodeSim.Compiled.TagRef;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.World
{
    /// <summary>The world tests share process-wide caches (tables, scopes): they run one at a time.</summary>
    [Xunit.CollectionDefinition("World", DisableParallelization = true)]
    public sealed class WorldCollection { }

    /// <summary>A temporary folder.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cicodesim-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception) { }
        }
    }

    public static class WorldFixture
    {
        /// <summary>A dBASE III table of char fields [(name, length)] with rows (dicts).</summary>
        public static void WriteDbf(
            string path,
            (string name, int len)[] fields,
            IEnumerable<Dictionary<string, string>> rows
        )
        {
            var list = rows.ToList();
            int hlen = 32 + 32 * fields.Length + 1;
            int rlen = 1 + fields.Sum(f => f.len);
            var ms = new MemoryStream();
            var d = DateTime.Today;
            ms.WriteByte(3);
            ms.WriteByte((byte)(d.Year - 1900));
            ms.WriteByte((byte)d.Month);
            ms.WriteByte((byte)d.Day);
            ms.Write(BitConverter.GetBytes((uint)list.Count), 0, 4);
            ms.Write(BitConverter.GetBytes((ushort)hlen), 0, 2);
            ms.Write(BitConverter.GetBytes((ushort)rlen), 0, 2);
            ms.Write(new byte[20], 0, 20);
            foreach (var (name, len) in fields)
            {
                var desc = new byte[32];
                byte[] nb = TextCodec.Encode(name);
                Array.Copy(nb, desc, Math.Min(11, nb.Length));
                desc[11] = (byte)'C';
                desc[16] = (byte)len;
                ms.Write(desc, 0, 32);
            }
            ms.WriteByte(0x0D);
            foreach (var row in list)
            {
                var rec = Enumerable.Repeat((byte)' ', rlen).ToArray();
                var up = row.ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value);
                int off = 1;
                foreach (var (name, len) in fields)
                {
                    if (up.TryGetValue(name.ToUpperInvariant(), out string? v) && !string.IsNullOrEmpty(v))
                    {
                        byte[] b = TextCodec.Encode(v, strict: true);
                        Array.Copy(b, 0, rec, off, Math.Min(len, b.Length));
                    }
                    off += len;
                }
                ms.Write(rec, 0, rec.Length);
            }
            ms.WriteByte(0x1A);
            File.WriteAllBytes(path, ms.ToArray());
        }

        public static (string, int)[] F(int n, params string[] names) => names.Select(x => (x, n)).ToArray();

        public static Dictionary<string, string> Row(params (string k, string v)[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in kv)
                d[k] = v;
            return d;
        }

        public static readonly (string, int)[] Variable = F(
            40,
            "NAME",
            "TYPE",
            "UNIT",
            "ADDR",
            "RAW_ZERO",
            "RAW_FULL",
            "ENG_ZERO",
            "ENG_FULL",
            "ENG_UNITS",
            "FORMAT",
            "COMMENT",
            "DEADBAND",
            "CLUSTER",
            "EQUIP",
            "ITEM",
            "CUSTOM1"
        );
        public static readonly (string, int)[] Locvar = F(
            40,
            "NAME",
            "TYPE",
            "ASIZE",
            "ZERO",
            "FULL",
            "COMMENT",
            "ENG_UNITS",
            "FORMAT"
        );
        public static readonly (string, int)[] Equip = F(
            60,
            "NAME",
            "CLUSTER",
            "TYPE",
            "AREA",
            "COMMENT",
            "PAGE",
            "TAGPREFIX",
            "SCHEDULED",
            "DEFSTATE"
        );
        public static readonly (string, int)[] Digalm = F(
            40,
            "TAG",
            "NAME",
            "DESC",
            "VAR_A",
            "VAR_B",
            "CATEGORY",
            "PRIV",
            "AREA",
            "COMMENT",
            "DELAY",
            "CLUSTER",
            "EQUIP",
            "ITEM",
            "CUSTOM1"
        );
        public static readonly (string, int)[] Anaalm = F(
            30,
            "TAG",
            "NAME",
            "VAR",
            "SETPOINT",
            "HIGHHIGH",
            "HIGH",
            "LOW",
            "LOWLOW",
            "DEVIATION",
            "RATE",
            "DEADBAND",
            "FORMAT",
            "CATEGORY",
            "AREA",
            "COMMENT",
            "HHDELAY",
            "HDELAY",
            "LDELAY",
            "LLDELAY",
            "DEVDELAY",
            "CLUSTER",
            "EQUIP",
            "ITEM"
        );
        public static readonly (string, int)[] Advalm = F(
            60,
            "TAG",
            "NAME",
            "DESC",
            "EXPR",
            "CATEGORY",
            "AREA",
            "DELAY",
            "CLUSTER",
            "EQUIP",
            "ITEM"
        );
        public static readonly (string, int)[] Argdig = F(
            30,
            new[] { "TAG", "NAME", "DESC", "VAR_A", "VAR_B", "VAR_C" }
                .Concat(Enumerable.Range(0, 8).Select(i => $"ALMDESC{i}"))
                .Concat(Enumerable.Range(0, 8).Select(i => $"ALMSTAT{i}"))
                .Concat(new[] { "CATEGORY", "AREA", "ONFUNC", "OFFFUNC" })
                .ToArray()
        );
        public static readonly (string, int)[] Category = F(
            40,
            "CATEGORY",
            "PRIORITY",
            "ACTION",
            "OFF_ACTION",
            "ACK_ACTION"
        );
        public static readonly (string, int)[] Labels = F(40, "NAME", "EXPR", "COMMENT");
        public static readonly (string, int)[] UsersT = F(40, "NAME", "FULL_NAME", "ROLES", "TYPE");
        public static readonly (string, int)[] Roles = F(
            30,
            new[] { "NAME", "ACCESS", "PRIV_G" }
                .Concat(Enumerable.Range(1, 8).Select(i => $"PRIV_{i}"))
                .ToArray()
        );
        public static readonly (string, int)[] Param = F(40, "SECTION", "NAME", "DATA");
        public static readonly (string, int)[] Names = F(40, "NAME");
        public static readonly (string, int)[] Master = { ("NAME", 64), ("PATH", 200) };

        public static Dictionary<string, string> Var(
            string name,
            string type = "",
            params (string k, string v)[] kw
        )
        {
            var r = Row(("NAME", name), ("TYPE", type));
            foreach (var (k, v) in kw)
                r[k.ToUpperInvariant()] = v;
            return r;
        }

        /// <summary>Build the install under root; returns Main's folder.</summary>
        public static string MakeInstall(string root)
        {
            string user = Path.Combine(root, "inst", "User");
            var dirs = new Dictionary<string, string>();
            foreach (string p in new[] { "Main", "Lib", "Include" })
            {
                dirs[p] = Path.Combine(user, p);
                Directory.CreateDirectory(dirs[p]);
            }
            WriteDbf(
                Path.Combine(user, "MASTER.DBF"),
                Master,
                dirs.Select(kv => Row(("NAME", kv.Key), ("PATH", kv.Value)))
            );
            WriteDbf(Path.Combine(dirs["Main"], "include.dbf"), Names, new[] { Row(("NAME", "Lib")) });
            string main = dirs["Main"],
                lib = dirs["Lib"];
            WriteDbf(
                Path.Combine(main, "variable.dbf"),
                Variable,
                new[]
                {
                    Var("Pump1_Run", "DIGITAL", ("equip", "Plant.Tank1"), ("item", "Run")),
                    Var("Valve_A", "DIGITAL"),
                    Var("Valve_B", "DIGITAL"),
                    Var(
                        "Tank1_Level",
                        "INT",
                        ("raw_zero", "0"),
                        ("raw_full", "32000"),
                        ("eng_zero", "0"),
                        ("eng_full", "100"),
                        ("eng_units", "%"),
                        ("format", "###.#EU"),
                        ("equip", "Plant.Tank1"),
                        ("item", "Level"),
                        ("custom1", "levelsensor")
                    ),
                    Var(
                        "Tank1_Temp",
                        "REAL",
                        ("eng_zero", "0"),
                        ("eng_full", "150"),
                        ("equip", "Plant.Tank1"),
                        ("item", "Temp")
                    ),
                    Var("Motor_Speed", "Real"),
                    Var("Name_Str", "STRING"),
                    Var("Arr_Real", "REAL", ("addr", "R1[10]")),
                    Var("Count_Long", "LONG"),
                    Var("Byte_Val", "BYTE"),
                    Var("Untyped"),
                    Var(
                        "Tank2_Level",
                        "REAL",
                        ("eng_zero", "0"),
                        ("eng_full", "100"),
                        ("equip", "Plant.Tank2"),
                        ("item", "Level")
                    ),
                }
            );
            WriteDbf(
                Path.Combine(lib, "variable.dbf"),
                Variable,
                new[] { Var("Lib_Flag", "DIGITAL", ("cluster", "C1")) }
            );
            WriteDbf(
                Path.Combine(lib, "locvar.dbf"),
                Locvar,
                new[]
                {
                    Row(("NAME", "LocInt"), ("TYPE", "INT")),
                    Row(("NAME", "LocStr"), ("TYPE", "STRING")),
                    Row(("NAME", "LocArr"), ("TYPE", "INT"), ("ASIZE", "4,3")),
                    Row(("NAME", "LocUntyped"), ("TYPE", ""), ("ZERO", "0"), ("FULL", "10")),
                }
            );
            WriteDbf(
                Path.Combine(main, "equip.dbf"),
                Equip,
                new[]
                {
                    Row(("NAME", "Plant")),
                    Row(("NAME", "Plant.Tank1"), ("TYPE", "Tank"), ("PAGE", "Overview")),
                    Row(
                        ("NAME", "Plant.Tank2"),
                        ("TYPE", "Tank"),
                        ("PAGE", "Detail"),
                        ("SCHEDULED", "True"),
                        ("DEFSTATE", "Idle")
                    ),
                }
            );
            WriteDbf(
                Path.Combine(main, "digalm.dbf"),
                Digalm,
                new[]
                {
                    Row(
                        ("TAG", "ALM_PUMP"),
                        ("NAME", "Pump"),
                        ("VAR_A", "Pump1_Run"),
                        ("CATEGORY", "1"),
                        ("EQUIP", "Plant.Tank1"),
                        ("ITEM", "PumpAlarm"),
                        ("CUSTOM1", "east")
                    ),
                    Row(
                        ("TAG", "ALM_NOTPUMP"),
                        ("NAME", "Pump stopped"),
                        ("VAR_A", "NOT Pump1_Run"),
                        ("CATEGORY", "2"),
                        ("DELAY", "00:00:05")
                    ),
                    Row(
                        ("TAG", "ALM_AND"),
                        ("VAR_A", "Pump1_Run"),
                        ("VAR_B", "Lib_Flag"),
                        ("CATEGORY", "_CAT_LBL"),
                        ("AREA", "5"),
                        ("PRIV", "_PRIV_ACK")
                    ),
                }
            );
            WriteDbf(
                Path.Combine(main, "anaalm.dbf"),
                Anaalm,
                new[]
                {
                    Row(
                        ("TAG", "ALM_TEMP"),
                        ("VAR", "Tank1_Temp"),
                        ("HIGHHIGH", "120"),
                        ("HIGH", "100"),
                        ("LOW", "20"),
                        ("LOWLOW", "10"),
                        ("DEADBAND", "5"),
                        ("HDELAY", "00:00:02"),
                        ("CATEGORY", "1"),
                        ("EQUIP", "Plant.Tank2"),
                        ("ITEM", "TempAlarm")
                    ),
                    Row(("TAG", "ALM_RATE"), ("VAR", "Motor_Speed"), ("RATE", "60"), ("CATEGORY", "1")),
                    Row(
                        ("TAG", "ALM_DEV"),
                        ("VAR", "Plant.Tank2.Level"),
                        ("SETPOINT", "50"),
                        ("DEVIATION", "10"),
                        ("CATEGORY", "2")
                    ),
                }
            );
            WriteDbf(
                Path.Combine(main, "advalm.dbf"),
                Advalm,
                new[]
                {
                    Row(("TAG", "ADV_SIMPLE"), ("EXPR", "Pump1_Run"), ("CATEGORY", "3")),
                    Row(("TAG", "ADV_NOT"), ("EXPR", "NOT Valve_A"), ("CATEGORY", "3")),
                    Row(("TAG", "ADV_EXPR"), ("EXPR", "Tank1_Level > 80 AND Pump1_Run"), ("CATEGORY", "3")),
                }
            );
            string[] stat = { "0", "1", "0", "0", "1", "0", "0", "1" };
            var row = Row(
                ("TAG", "ALM_MULTI"),
                ("VAR_A", "Valve_A"),
                ("VAR_B", "Valve_B"),
                ("VAR_C", "Pump1_Run"),
                ("CATEGORY", "2"),
                ("ONFUNC", "OnMulti()"),
                ("OFFFUNC", "OffMulti()")
            );
            for (int i = 0; i < 8; i++)
            {
                row[$"ALMSTAT{i}"] = stat[i];
                row[$"ALMDESC{i}"] = $"S{i}";
            }
            WriteDbf(Path.Combine(main, "argdig.dbf"), Argdig, new[] { row });
            WriteDbf(
                Path.Combine(lib, "category.dbf"),
                Category,
                new[]
                {
                    Row(("CATEGORY", "1"), ("PRIORITY", "1"), ("ACTION", "Beep(1)")),
                    Row(("CATEGORY", "2"), ("PRIORITY", "2")),
                    Row(("CATEGORY", "_CAT_LBL"), ("PRIORITY", "3"), ("ACK_ACTION", "Acked()")),
                }
            );
            WriteDbf(
                Path.Combine(lib, "labels.dbf"),
                Labels,
                new[]
                {
                    Row(("NAME", "_CAT_LBL"), ("EXPR", "3")),
                    Row(("NAME", "_PRIV_ACK"), ("EXPR", "2")),
                    Row(("NAME", "TRUE"), ("EXPR", "1")),
                }
            );
            WriteDbf(
                Path.Combine(main, "users.dbf"),
                UsersT,
                new[]
                {
                    Row(("NAME", "Engineer"), ("FULL_NAME", "Systems Engineer"), ("ROLES", "Eng")),
                    Row(("NAME", "Op"), ("FULL_NAME", "Operator"), ("ROLES", "Op")),
                }
            );
            WriteDbf(
                Path.Combine(main, "roles.dbf"),
                Roles,
                new[]
                {
                    Row(("NAME", "Eng"), ("PRIV_G", "1..8")),
                    Row(("NAME", "Op"), ("ACCESS", "6"), ("PRIV_1", "5"), ("PRIV_3", "5,7")),
                }
            );
            WriteDbf(
                Path.Combine(main, "param.dbf"),
                Param,
                new[]
                {
                    Row(("SECTION", "Alarm"), ("NAME", "ScanTime"), ("DATA", "500")),
                    Row(("SECTION", "Alarm"), ("NAME", "Period"), ("DATA", "60")),
                }
            );
            return main;
        }

        public const string DefaultStart = "2026-10-01T08:00:00Z";

        public static WorldT MakeWorld(string root, object? start = null)
        {
            WorldT.ClearCaches();
            return new WorldT(MakeInstall(root), start: start ?? DefaultStart);
        }

        public static SimpleTask Task(int flags = 0x0F) => new SimpleTask(flags);

        public static TagRef Ref(
            string tag,
            string element = "",
            string item = "",
            string cluster = "",
            int lightweight = 0
        ) => WorldValues.Ref(tag, element, item, cluster, lightweight);

        public static Value Real(double v) => Value.Real(v);

        public static Value Long(long v) => Value.Long(v);

        public static Value Str(string v) => Value.Str(v);

        public const string R2 = @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";
    }
}
