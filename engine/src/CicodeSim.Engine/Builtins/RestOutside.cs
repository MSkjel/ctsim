using System;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>Functions that reach outside the runtime: mail, phone lines, fuzzy logic, graphs.</summary>
    public static class RestOutside
    {
        // ------------------------------------------------------------------ mail
        private const int MailNotAvailable = 0x139;

        private const string NoMapi =
            "Never sends or reads mail: the simulated computer has no MAPI (the runtime loads MAPI32.DLL only with win.ini [MAIL]MAPI=1), so the runtime's answer 313 without it.";

        [Builtin("MailLogon", Status = BuiltinStatus.Stub, Returns = "313", Doc = "1106281", Notes = NoMapi)]
        [Builtin("MailLogoff", Status = BuiltinStatus.Stub, Returns = "313", Doc = "1106280", Notes = NoMapi)]
        [Builtin("MailSend", Status = BuiltinStatus.Stub, Returns = "313", Doc = "1106283", Notes = NoMapi)]
        private static Value MailNone(SimHost h, CicodeTask t, ArgList a) => W.I(MailNotAvailable);

        [Builtin(
            "MailRead",
            Status = BuiltinStatus.Stub,
            Returns = "313",
            Doc = "1106282",
            Notes = NoMapi + " The four output strings are emptied first, as the runtime does."
        )]
        private static Value MailRead(SimHost h, CicodeTask t, ArgList a)
        {
            for (int i = 0; i < 4 && i < a.Count; i++)
                W.Out(t, a.Get(i), "");
            return W.I(MailNotAvailable);
        }

        [Builtin(
            "MailError",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1106279",
            Notes = "The last MAPI error; no MAPI call is ever made here: 0."
        )]
        private static Value MailError(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        // ------------------------------------------------------------------ the I/O server's driver exports
        private const string NoDriver =
            "The call goes to the I/O server's driver DLL export of the same name; no I/O server runs in the simulator and no driver exports it: the runtime's answer without the export.";

        [Builtin(
            "LineOpen",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "",
            Notes = NoDriver + " -1 (0xFFFF)."
        )]
        [Builtin(
            "LineClose",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "",
            Notes = NoDriver + " -1 (0xFFFF)."
        )]
        [Builtin(
            "LineAnswer",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "",
            Notes = NoDriver + " -1 (0xFFFF)."
        )]
        [Builtin(
            "LineMakeCall",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "",
            Notes = NoDriver + " -1 (0xFFFF)."
        )]
        [Builtin(
            "LineDrop",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "",
            Notes = NoDriver + " -1 (0xFFFF)."
        )]
        private static Value LineNone(SimHost h, CicodeTask t, ArgList a) => W.I(-1);

        [Builtin(
            "LineInfo",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Doc = "",
            Notes = NoDriver + " ''."
        )]
        [Builtin(
            "DriverInfo",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Doc = "1106222",
            Notes = NoDriver + " ''."
        )]
        private static Value DriverNone(SimHost h, CicodeTask t, ArgList a) => W.Empty;

        // ------------------------------------------------------------------ fuzzy logic
        private const string NoFtr =
            "fuzzyTECH (FTRUN32.dll) is not run by the simulator: no .ftr file opens, so every handle is bad; each failure raises 274 without the halt flag, as the runtime's ftr* failure path.";

        [Builtin(
            "FuzzyOpen",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "1106181",
            Errors = "274 (raised, -1)",
            Notes = NoFtr
        )]
        private static Value FuzzyOpen(SimHost h, CicodeTask t, ArgList a)
        {
            W.Raise(t, Codes.InvalidArg);
            return W.I(-1);
        }

        [Builtin(
            "FuzzyClose",
            Status = BuiltinStatus.Stub,
            Returns = "274",
            Doc = "1106178",
            Errors = "274 (raised and returned)",
            Notes = NoFtr
        )]
        [Builtin(
            "FuzzySetShellValue",
            Status = BuiltinStatus.Stub,
            Returns = "274",
            Doc = "1106183",
            Errors = "274 (raised and returned)",
            Notes = NoFtr
        )]
        [Builtin(
            "FuzzySetCodeValue",
            Status = BuiltinStatus.Stub,
            Returns = "274",
            Doc = "1106182",
            Errors = "274 (raised and returned)",
            Notes = NoFtr
        )]
        [Builtin(
            "FuzzyTrace",
            Status = BuiltinStatus.Stub,
            Returns = "274",
            Doc = "1106184",
            Errors = "274 (raised and returned)",
            Notes = NoFtr
        )]
        private static Value FuzzyFail(SimHost h, CicodeTask t, ArgList a)
        {
            W.Raise(t, Codes.InvalidArg);
            return W.I(Codes.InvalidArg);
        }

        [Builtin(
            "FuzzyGetShellValue",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1106180",
            Errors = "274 (raised, 0; the flag argument is not written)",
            Notes = NoFtr
        )]
        [Builtin(
            "FuzzyGetCodeValue",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1106179",
            Errors = "274 (raised, 0; the flag argument is not written)",
            Notes = NoFtr
        )]
        private static Value FuzzyGet(SimHost h, CicodeTask t, ArgList a)
        {
            W.Raise(t, Codes.InvalidArg);
            return W.I(0);
        }

        // ------------------------------------------------------------------ keystrokes, archives, errors
        [Builtin(
            "SendKeys",
            Status = BuiltinStatus.Stub,
            Returns = "274",
            Doc = "1106274",
            Notes = "Never sends keystrokes to another program: no window of the simulated desktop is a Windows window, so FindWindowA finds none and the runtime returns 274 (not raised)."
        )]
        private static Value SendKeys(SimHost h, CicodeTask t, ArgList a)
        {
            h.Note(t, "SendKeys(\"" + A.S(a.Get(0)) + "\") (not simulated)");
            return W.I(Codes.InvalidArg);
        }

        private static Value Soe(SimHost h, CicodeTask t, string what)
        {
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            h.Note(t, what + " (not simulated)");
            return W.BlockOneTurn(t, what, () => W.I(0));
        }

        [Builtin(
            "SOEArchive",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1192396",
            Blocking = true,
            Errors = "282 in foreground code (returned, not raised)",
            Notes = "The alarm server would archive its SOE events to sPath; the simulator writes no archive: logged, 0 after one turn (the server's answer on success; guess)."
        )]
        private static Value SOEArchive(SimHost h, CicodeTask t, ArgList a) =>
            Soe(h, t, "SOEArchive(" + A.S(a.Get(0)) + ")");

        [Builtin(
            "SOEMount",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1192399",
            Blocking = true,
            Errors = "282 in foreground code (returned, not raised)",
            Notes = "The alarm server would mount an SOE archive; nothing is mounted here: logged, 0 after one turn (guess)."
        )]
        private static Value SOEMount(SimHost h, CicodeTask t, ArgList a) =>
            Soe(h, t, "SOEMount(" + A.S(a.Get(0)) + ")");

        [Builtin(
            "SOEDismount",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1192397",
            Blocking = true,
            Errors = "282 in foreground code (returned, not raised)",
            Notes = "Dismounts the archive SOEMount mounted; nothing is mounted here: logged, 0 after one turn (guess)."
        )]
        private static Value SOEDismount(SimHost h, CicodeTask t, ArgList a) => Soe(h, t, "SOEDismount");

        [Builtin(
            "ErrDrv",
            Status = BuiltinStatus.Stub,
            Doc = "1105315",
            Notes = "The runtime looks the driver error up in the installed protocols' proterr.dbf; the simulator reads no driver databases: '#Error not in database', the runtime's text for an error it does not find."
        )]
        private static Value ErrDrv(SimHost h, CicodeTask t, ArgList a) => W.S("#Error not in database");

        [Builtin(
            "PasswordEncrypt",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Doc = "",
            Notes = "Undocumented: the legacy reversible user-password cipher. Deliberately not simulated: ''."
        )]
        [Builtin(
            "PasswordDecrypt",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Doc = "",
            Notes = "Undocumented: reverses the legacy reversible user-password cipher. Deliberately not simulated: ''."
        )]
        private static Value PasswordCipher(SimHost h, CicodeTask t, ArgList a) => W.Empty;
    }
}
