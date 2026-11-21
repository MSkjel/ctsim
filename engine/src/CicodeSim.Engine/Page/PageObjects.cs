using System;
using System.Collections.Generic;
using CicodeSim.World.Display;

namespace CicodeSim.Page
{
    /// <summary>The result of one foreground evaluation (the value cell
    /// zeroed before the run, the final stack value when the code ends or halts with one, the
    /// error = task).</summary>
    public struct EvalResult
    {
        public long I;
        public double D;
        public string S;
        public int Error;
        public int Flags;
        public ulong Quality;

        /// <summary>The built-in that stopped the code as not simulated, "" when none.</summary>
        public string NotSimulated;

        /// <summary>The task halted (any reason).</summary>
        public bool Halted;
        public string HaltReason;

        /// <summary>The halt code of a halted task (CicodeTask.HaltCode, 0 for Halt() and the
        /// not-simulated stops).</summary>
        public int HaltCode;
    }

    /// <summary>The animation classes and state of one animated object.</summary>
    public sealed class AnimObject
    {
        public int An;
        public int GroupAn;
        public int Type;

        /// <summary>PGDYNOBJ, PGBUTTON, PGNUM ... (the table the row came from).</summary>
        public string Table = "";
        public Dictionary<string, object?> Row = new Dictionary<string, object?>();
        public AnimObject? Parent;
        public DrawObject? Drawing;

        /// <summary>The pools the object's code refers to when they are not the page's: a member
        /// of a genie DspSym placed (its own compiled rows, PageRun.PlaceSymbol); null = the page's.</summary>
        public CicodeSim.Compiled.Program? Program;

        // the compiled code of the fields (null = no expression)
        public byte[]? Vis,
            Dis,
            Szx,
            Szy,
            Rot,
            Mvx,
            Mvy,
            ColArr,
            ColExp,
            TxtStr,
            SetArr,
            Fil;
        public byte[]?[] Col = new byte[5][];
        public byte[]?[] Txt = new byte[5][];
        public (byte[]? Read, byte[]? Write) RotSl,
            MvxSl,
            MvySl;
        public int SzxMin,
            SzxMax = 100,
            SzxOrg,
            SzxMode,
            SzyMin,
            SzyMax = 100,
            SzyOrg,
            SzyMode;
        public int RotMin,
            RotMax = 360,
            RotXOrg,
            RotYOrg,
            RotMode,
            RotSlMode;
        public int MvxMin,
            MvxMax,
            MvxSlMode,
            MvyMin,
            MvyMax,
            MvySlMode;
        public int ColMode,
            TxtMode,
            FilMin,
            FilMax = 100,
            FilMode,
            DisMode,
            GreyStyle,
            Priv,
            Area;
        public string Comment = "",
            Desc = "";

        /// <summary>PGBUTTON TEXT (a button's caption).</summary>
        public string ButtonText = "";
        public int RptRate;

        /// <summary>The vertex rows of this AN (PGVERTEX), by INDEX.</summary>
        public List<VertexRow> Vertices = new List<VertexRow>();

        /// <summary>The alarm indicator of this AN (PGALMIND), null when none.</summary>
        public AlarmIndicator? Indicator;

        /// <summary>A legacy V3/V4 row (PGNUM, PGSTR, PGANSYM, PGSYM, PGTEXT).</summary>
        public LegacyRow? Legacy;

        // the run-time state
        public bool OwnShown = true,
            OwnDisabled,
            AreaOk = true,
            PrivOk = true;
        public bool Shown = true,
            Disabled;

        /// <summary>The effective disable style / DISMODE (the group's when the group is disabled).</summary>
        public int EffStyle,
            EffDisMode;
        public ColourPair? ColourSel;
        public ColourPair? FilColSel;
        public int Frame;
        public bool AnimTick;
        public long AnimValue;

        /// <summary>The scan's indication flags and first error code.</summary>
        public int Flags,
            ErrCode;
        public ObjectState State;
        public ObjectState? Sent;
        public bool SliderActiveX,
            SliderActiveY,
            SliderActiveRot;

        public AnimObject(int an)
        {
            An = an;
            State = new ObjectState(an);
        }

        public bool IsSymbolSet => Type == 0x0C;

        /// <summary>A text object: the record type that takes a display value (text 5, the V3/V4
        /// string / text classes 0x1005 / 0x1006, number 0x1004).</summary>
        public bool IsText => Type == 0x05 || Type == 0x1004 || Type == 0x1005 || Type == 0x1006;

        public bool HasAnimation =>
            Vis != null
            || Dis != null
            || Szx != null
            || Szy != null
            || Rot != null
            || Mvx != null
            || Mvy != null
            || ColArr != null
            || ColExp != null
            || TxtStr != null
            || SetArr != null
            || Fil != null
            || AnyOf(Col)
            || AnyOf(Txt)
            || RotSl.Read != null
            || MvxSl.Read != null
            || MvySl.Read != null
            || Vertices.Count > 0
            || Legacy != null;

        public static bool AnyOf(byte[]?[] a)
        {
            foreach (var x in a)
                if (x != null)
                    return true;
            return false;
        }

        /// <summary>The last condition expression present (A = 1 .. E = 5), 0 when none.</summary>
        public static int LastPresent(byte[]?[] a)
        {
            for (int k = a.Length - 1; k >= 0; k--)
                if (a[k] != null)
                    return k + 1;
            return 0;
        }

        public bool HasColour =>
            ColMode switch
            {
                0 => Col[0] != null,
                1 => AnyOf(Col),
                2 => ColArr != null,
                3 or 4 => ColExp != null,
                _ => false,
            };

        public bool HasText =>
            IsText
            && TxtMode switch
            {
                0 => Txt[0] != null,
                1 => AnyOf(Txt),
                2 => Txt[0] != null,
                3 or 4 => TxtStr != null,
                _ => false,
            };

        public bool HasSymbolSet =>
            IsSymbolSet
            && TxtMode switch
            {
                0 or 3 => Txt[0] != null,
                1 => AnyOf(Txt),
                2 => SetArr != null,
                _ => false,
            };

        public bool SliderX => (MvxSlMode & 1) != 0 && MvxSl.Read != null;
        public bool SliderY => (MvySlMode & 1) != 0 && MvySl.Read != null;
        public bool SliderRot => (RotSlMode & 1) != 0 && RotSl.Read != null;
    }

    /// <summary>One PGVERTEX row: (AN, INDEX) with its expression and the From / To offsets
    /// (X1, Y1) / (X2, Y2).</summary>
    public sealed class VertexRow
    {
        public int Index;
        public byte[]? Expr;
        public int X1,
            Y1,
            X2,
            Y2;
        public double Dx,
            Dy;

        /// <summary>A row with all four offsets 0 gets no animation.</summary>
        public bool Animated => Expr != null && (X1 != 0 || Y1 != 0 || X2 != 0 || Y2 != 0);
    }

    /// <summary>One PGALMIND row (keyed by AN).</summary>
    public sealed class AlarmIndicator
    {
        public string Expr = "";
        public int Include,
            Inside,
            Width,
            Padding,
            FlagOn,
            FlagLoc,
            EqHier;
        public long Colour;
    }

    /// <summary>A V3 / V4 row: its expression(s) and the texts / symbols of
    /// the row.</summary>
    public sealed class LegacyRow
    {
        public string Table = "";
        public byte[]? Expr,
            OffText,
            OnText;
        public string OffSymbol = "";
        public List<string> Symbols = new List<string>();

        /// <summary>PGSYM / PGTEXT: the three condition tags (TAGREF offsets).</summary>
        public long[] Vars = { -1, -1, -1 };
        public List<string> Strings = new List<string>();
        public int Mode;
    }

    /// <summary>A Cicode object (PGADV): its CMD runs every scan after the object pass.</summary>
    public sealed class CicodeObject
    {
        public int An;
        public byte[]? Cmd;

        /// <summary>The pools of the code when not the page's (a DspSym genie member).</summary>
        public CicodeSim.Compiled.Program? Program;
    }

    /// <summary>A trend of the page: a PGTREND row (its CMD and VISIBLE code) or a trend
    /// TrnNew made, over the display client's trend object (World.Display.PageTrend: pens,
    /// period, span, mode, cursor, scales).</summary>
    public sealed class TrendObject
    {
        public int An;
        public byte[]? Cmd,
            Visible;

        /// <summary>The PGTREND row's pens (PEN1..PEN8).</summary>
        public List<string> Pens = new List<string>();
        public bool Shown = true;

        /// <summary>The display client's trend object, null when the page has none at the AN.</summary>
        public PageTrend? Data;

        /// <summary>A trend TrnNew made (no PGTREND row).</summary>
        public bool Dynamic;
        public ObjectState State;
        public ObjectState? Sent;

        public TrendObject(int an)
        {
            An = an;
            State = new ObjectState(an);
        }
    }

    /// <summary>A touch command row (PGTOUCH) or a button's commands (PGBUTTON).</summary>
    public sealed class TouchCommand
    {
        public int An;
        public int Action = 1;
        public int RptRate;
        public byte[]? Up,
            Down,
            Dbl,
            Repeat;
        public string LogUp = "",
            LogDown = "",
            LogDbl = "",
            LogRepeat = "";
        public bool Button;

        /// <summary>The pools of the code when not the page's (a DspSym genie member).</summary>
        public CicodeSim.Compiled.Program? Program;

        /// <summary>Does the record take a plain left button event?</summary>
        public bool TakesLeftButton => ((Action ^ 0x0001) & 0x71ff) == 0;
    }

    /// <summary>A keyboard command row (PGKEY, AN 0 = page command) or a system command
    /// (keyboard.dbf).</summary>
    public sealed class KeyCommand
    {
        public int An;
        public string Sequence = "";
        public List<int> Codes = new List<int>();
        public byte[]? Cmd;

        /// <summary>A system command's source text (keyboard.dbf), run through the call text
        /// parser (not compiled here: listed as not simulated).</summary>
        public string Text = "";
        public int Priv,
            Area;
        public string MsgLog = "";
        public bool System;

        /// <summary>A KeySetSeq command: the function handle it calls (-1: the row's Cmd).</summary>
        public long Function = -1;

        /// <summary>The pools of the code when not the page's (a DspSym genie member).</summary>
        public CicodeSim.Compiled.Program? Program;
    }
}
