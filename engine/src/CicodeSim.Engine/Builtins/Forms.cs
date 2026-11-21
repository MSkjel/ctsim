using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>One field of a form: the variable, the type, the function, the instance
    /// string, the instance integer, the list.</summary>
    internal sealed class CField
    {
        public int Type,
            Col,
            Row,
            Width,
            Height,
            MaxLen;

        /// <summary>The by-reference variable OK writes (FUNC0 '8' argument).</summary>
        public Value Var;
        public bool HasVar;
        public string Text = "",
            Label = "",
            Fn = "";
        public List<string> Items = new List<string>();
        public long IData;
        public string? SData;
    }

    /// <summary>One form: shown, the dialog, the waiting task, width and height, the fields,
    /// the window, the mode.</summary>
    internal sealed class CForm
    {
        public int Handle;
        public string Title = "";
        public int Width,
            Height,
            Mode,
            Window = -1;
        public bool Active,
            Closed;
        public long Result;
        public CicodeTask? Waiter;
        public CicodeTask Creator = null!;
        public readonly List<CField> Fields = new List<CField>();
        public Dialog? Dialog;
    }

    /// <summary>Forms and message boxes.</summary>
    public static class FormBuiltins
    {
        private const int Cancelled = 299;

        // the field types that keep a variable OK writes (switch)
        private static bool KeepsVar(int type) =>
            type == 1
            || type == 2
            || type == 8
            || type == 9
            || type == 10
            || type == 11
            || type == 13
            || type == 14
            || type == 15
            || type == 18;

        private static bool IsList(int type) => type == 10 || type == 11 || type == 13 || type == 14;

        internal static int WindowOf(CicodeTask t) =>
            t.Data.TryGetValue("window", out object? w) && w is int i ? i : 0;

        private static string Local(string s) => Misc.LocalText(s);

        private static CForm? Form(SimHost h, long hd) => h.Forms.Get(A.Short(hd));

        private static CForm? Shown(SimHost h, long hd)
        {
            var f = Form(h, hd);
            return f != null && f.Active ? f : null;
        }

        private static bool FieldOk(CForm f, long field) =>
            (ushort)field < 0x8000 && (short)field < f.Fields.Count;

        // ------------------------------------------------------------------ dialogs
        private static Dialog ToDialog(CForm f)
        {
            var d = new Dialog
            {
                Kind = DialogKind.Form,
                Title = f.Title,
                Mode = f.Mode,
                Width = f.Width,
                Height = f.Height,
                Handle = f.Handle,
                Window = f.Window,
                Task = f.Waiter?.Handle ?? -1,
            };
            var fields = new List<DialogField>();
            for (int i = 0; i < f.Fields.Count; i++)
                fields.Add(FieldOf(f, i));
            d.SetFields(fields);
            return d;
        }

        private static DialogField FieldOf(CForm f, int i)
        {
            var c = f.Fields[i];
            return new DialogField
            {
                Index = i,
                Type = c.Type,
                Kind = DialogField.KindOf(c.Type),
                Col = c.Col,
                Row = c.Row,
                Width = c.Width,
                Height = c.Height,
                Label = c.Label.Trim().Length > 0 || !c.HasVar ? c.Label : RowPrompt(f, c),
                Text = c.Text,
                Items = new List<string>(c.Items),
                HasVariable = c.HasVar,
                Function = c.Fn,
                MaxLength = c.MaxLen,
            };
        }

        /// <summary>The label of an input field without one (Include's LoginForm puts the prompt
        /// in a FormPrompt of its own): the nearest text left of it on its row.</summary>
        private static string RowPrompt(CForm f, CField c)
        {
            string best = "";
            int col = -1;
            foreach (var p in f.Fields)
                if (
                    (p.Type == 3 || p.Type == 17)
                    && p.Row == c.Row
                    && p.Col < c.Col
                    && p.Col > col
                    && p.Label.Trim().Length > 0
                )
                {
                    col = p.Col;
                    best = p.Label;
                }
            return best;
        }

        private static void Changed(SimHost h, CForm f, int field)
        {
            if (f.Dialog == null)
                return;
            if (field >= 0 && field < f.Fields.Count)
                f.Dialog.SetField(field, FieldOf(f, field));
            h.Interaction.Changed(f.Dialog, field);
        }

        private static string VarText(in Value v)
        {
            if (!(v.O is Ref))
                return A.S(v);
            try
            {
                return A.S(Value.Deref(v));
            }
            catch (IndexOutOfRangeException)
            {
                return "";
            }
        }

        private static void WriteVar(CForm f, CField c)
        {
            if (!c.HasVar || !(c.Var.O is Ref r))
                return;
            if (r is StackRef sr && sr.Task.Done)
                return; // the form's task is gone: dropped
            W.Out(f.Creator, c.Var, A.Cap(c.Text));
        }

        /// <summary>Take the host's answers (SimHost.PumpDialogs).</summary>
        internal static void Pump(SimHost h)
        {
            foreach (var (hd, f) in h.Forms.Live())
            {
                var d = f.Dialog;
                if (d == null || f.Closed)
                    continue;
                if (d.TakeTyped(out var typed, out var presses))
                {
                    foreach (var kv in typed)
                        if (kv.Key >= 0 && kv.Key < f.Fields.Count)
                            f.Fields[kv.Key].Text = kv.Value;
                    foreach (int i in presses)
                    {
                        if (f.Closed || i < 0 || i >= f.Fields.Count)
                            continue;
                        var c = f.Fields[i];
                        if (c.Type == 16)
                            continue; // a disabled button
                        // FormCurr names the last button WITH a function (only
                        // when the field's function is not -1)
                        if (c.Fn.Length > 0 && c.Fn != "0")
                        {
                            h.CurrForm = hd;
                            h.CurrField = i;
                        }
                        RunButton(h, f, c);
                        if (c.Type == 5)
                            Close(h, f, true, null);
                        else if (c.Type == 6)
                            Close(h, f, false, null);
                    }
                }
                if (!f.Closed && d.TakeAnswer(out bool ok, out _, out var texts))
                    Close(h, f, ok, texts);
            }
        }

        /// <summary>A button's function as a new task with no arguments; FormCurr names the form and the field.</summary>
        private static void RunButton(SimHost h, CForm f, CField c)
        {
            if (c.Fn.Length == 0 || c.Fn == "0")
                return;
            Function? fn = CallText.Find(f.Creator, c.Fn);
            if (fn == null)
                return;
            var vm = f.Creator.Vm;
            try
            {
                var x = vm.TaskForCall(fn, Array.Empty<Value>(), false, null, fn.Name);
                if (f.Window >= 0)
                    x.Data["window"] = f.Window;
                if (vm.Scheduler != null)
                    vm.Scheduler.Add(x);
                else
                {
                    try
                    {
                        vm.Run(x, 1000);
                    }
                    catch (InvalidOperationException) { }
                }
            }
            catch (HardwareError) { }
        }

        /// <summary>The dialog closed (with result 1 = OK, else Cancel): OK
        /// copies every field's text into its variable; the waiting task gets 0 or 299.</summary>
        private static void Close(SimHost h, CForm f, bool ok, Dictionary<int, string>? texts)
        {
            if (f.Closed)
                return;
            f.Closed = true;
            f.Active = false;
            if (ok)
            {
                if (texts != null)
                    foreach (var kv in texts)
                        if (kv.Key >= 0 && kv.Key < f.Fields.Count)
                            f.Fields[kv.Key].Text = kv.Value;
                foreach (var c in f.Fields)
                    WriteVar(f, c);
            }
            h.World?.Users.NoteCommand();
            f.Result = ok ? 0 : Cancelled;
            h.Forms.Free(f.Handle);
            if (f.Dialog != null)
            {
                f.Dialog.Drop();
                h.Interaction.Closed(f.Dialog);
                h.World?.Trace?.Event(
                    "dialog",
                    null,
                    f.Waiter?.Handle,
                    ("op", "close"),
                    ("kind", "form"),
                    ("title", f.Title),
                    ("result", ok ? "ok" : "cancel")
                );
            }
        }

        private static void Show(SimHost h, Dialog d)
        {
            h.World?.Trace?.Event(
                "dialog",
                null,
                d.Task >= 0 ? (object)d.Task : null,
                ("op", "show"),
                ("kind", d.Kind.ToString().ToLowerInvariant()),
                ("title", d.Title)
            );
            if (ReferenceEquals(h.Interaction, HeadlessInteraction.Instance))
                h.Log?.Invoke(HeadlessInteraction.DialogText(d) + ": cancelled (no UI)");
            h.Interaction.Show(d);
        }

        // ------------------------------------------------------------------ forms
        [Builtin(
            "FormNew",
            Doc = "1106091",
            Effects = "creates a form of the process (the form being built)",
            Errors = "274 with the halt flag when a form is still being built less than 2 s after the last FormNew (returns -1); 274 a width outside 1..221, a height outside 1..131 or a mode of 0x200+ (returns -1)",
            Notes = "A form still being built is destroyed first (a shown one is closed as cancelled). The 2 s are the VM clock's milliseconds (the runtime's timeGetTime). The title is translated (@(...) to its native text)."
        )]
        private static Value FormNew(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            double now = t.Vm.Clock.MonotonicMs();
            if (h.CurrentForm != -1)
            {
                if ((uint)(long)Math.Floor(now - h.LastFormNew) < 0x7d1)
                {
                    t.RaiseError(Codes.InvalidArg, true);
                    return W.I(-1);
                }
                Destroy(h, h.CurrentForm);
            }
            long w = A.I(a.Get(1)),
                ht = A.I(a.Get(2)),
                mode = A.I(a.Get(3));
            int handle = -1;
            if ((ushort)(w - 1) < 0xdd && (ushort)(ht - 1) < 0x83 && (ushort)mode < 0x200)
            {
                var f = new CForm
                {
                    Title = Local(A.S(a.Get(0))),
                    Width = (short)w,
                    Height = (short)ht,
                    Mode = (int)(mode & 0x7fff),
                    Window = WindowOf(t),
                    Creator = t,
                };
                handle = h.Forms.Add(f);
                f.Handle = handle;
            }
            else
                W.Raise(t, Codes.InvalidArg);
            h.CurrentForm = handle;
            h.LastFormNew = now;
            return W.I(handle);
        }

        private static long Destroy(SimHost h, long hd)
        {
            var f = Form(h, hd);
            if (f == null)
                return Codes.InvalidArg;
            if (f.Active)
            {
                Close(h, f, false, null);
                return 0;
            }
            h.Forms.Free(f.Handle);
            h.CurrentForm = -1;
            h.LastField = -1;
            return 0;
        }

        [Builtin(
            "FormDestroy",
            Doc = "1106077",
            Effects = "closes the form without writing its variables",
            Errors = "274 no such form (returned)",
            Notes = "A shown form closes as cancelled (its FormRead(0) returns 299); a form being built is freed."
        )]
        private static Value FormDestroy(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            return W.I(Destroy(h, A.I(a.Get(0))));
        }

        [Builtin(
            "FormRead",
            Doc = "1106098",
            Blocking = true,
            Effects = "shows the form being built through the dialog host; OK writes the fields' variables",
            Errors = "274 no form being built (returned); 274 a mode other than 0 / 1 (raised and returned); 282 mode 0 in foreground code (returned, the form is not shown); 299 cancelled",
            Notes = "Mode 0 waits until the form closes: 0 for OK, 299 for Cancel; mode 1 returns 0 at once and the form stays shown until the host answers it or FormDestroy. Headless (no UI) every form is cancelled. The form being built is cleared either way."
        )]
        private static Value FormRead(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            long mode = A.Short(A.I(a.Get(0)));
            var f = Form(h, h.CurrentForm);
            if (f == null || f.Active)
            {
                h.CurrentForm = -1;
                h.LastField = -1;
                return W.I(Codes.InvalidArg);
            }
            if (mode == 0)
            {
                if (t.Foreground)
                {
                    h.CurrentForm = -1;
                    h.LastField = -1;
                    return W.I(Codes.FgBlock);
                }
                f.Waiter = t;
            }
            else if (mode != 1)
            {
                W.Raise(t, Codes.InvalidArg);
                h.CurrentForm = -1;
                h.LastField = -1;
                return W.I(Codes.InvalidArg);
            }
            f.Active = true;
            h.CurrentForm = -1;
            h.LastField = -1;
            f.Dialog = ToDialog(f);
            Show(h, f.Dialog);
            if (mode == 1)
            {
                Pump(h);
                return W.I(0);
            }
            t.Wait = new Wait(TaskState.Wait, "FormRead(" + f.Title + ")")
            {
                Poll = () =>
                {
                    Pump(h);
                    return f.Closed;
                },
                Result = () => W.I(f.Result),
            };
            return Value.Block;
        }

        [Builtin("FormActive", Doc = "1106071", Notes = "1 while the form is shown, else 0.")]
        private static Value FormActive(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            return W.I(Shown(h, A.I(a.Get(0))) != null ? 1 : 0);
        }

        [Builtin(
            "FormGetText",
            Doc = "1106083",
            Notes = "The field's current text (what the user typed so far) while the form is shown, else \"\"."
        )]
        private static Value FormGetText(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            var f = Shown(h, A.I(a.Get(0)));
            long fld = A.I(a.Get(1));
            if (f == null || !FieldOk(f, fld))
                return W.Empty;
            return W.S(A.Cap(f.Fields[(int)fld].Text));
        }

        [Builtin(
            "FormSetText",
            Doc = "1106104",
            Errors = "269 the form is not shown or no such field (returned)",
            Effects = "sets the field's text in the shown form",
            Notes = "The text is translated (@(...))."
        )]
        private static Value FormSetText(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            var f = Shown(h, A.I(a.Get(0)));
            long fld = A.I(a.Get(1));
            if (f == null || !FieldOk(f, fld))
                return W.I(Codes.BadHandle);
            f.Fields[(int)fld].Text = Local(A.S(a.Get(2)));
            Changed(h, f, (int)fld);
            return W.I(0);
        }

        [Builtin(
            "FormGetData",
            Doc = "1106081",
            Errors = "269 the form is not shown (returned)",
            Effects = "writes every field's current text into its variable"
        )]
        private static Value FormGetData(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            var f = Shown(h, A.I(a.Get(0)));
            if (f == null)
                return W.I(Codes.BadHandle);
            foreach (var c in f.Fields)
                WriteVar(f, c);
            return W.I(0);
        }

        [Builtin(
            "FormSetData",
            Doc = "1106102",
            Errors = "269 the form is not shown (returned)",
            Effects = "sets every field with a variable to the variable's value"
        )]
        private static Value FormSetData(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            var f = Shown(h, A.I(a.Get(0)));
            if (f == null)
                return W.I(Codes.BadHandle);
            for (int i = 0; i < f.Fields.Count; i++)
            {
                var c = f.Fields[i];
                if (!c.HasVar)
                    continue;
                c.Text = VarText(c.Var);
                Changed(h, f, i);
            }
            return W.I(0);
        }

        [Builtin(
            "FormCurr",
            Doc = "1106076",
            Notes = "Writes the form and the field of the last button with a function pressed (0 and 0 before any); returns 0."
        )]
        private static Value FormCurr(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            W.Out(t, a.Get(0), h.CurrForm);
            W.Out(t, a.Get(1), h.CurrField);
            return W.I(0);
        }

        private static Value GetInst(
            SimHost h,
            CicodeTask t,
            CForm? f,
            long fld,
            in Value iRef,
            in Value sRef
        )
        {
            if (f == null || fld < 0 || (short)fld >= f.Fields.Count || (ushort)fld >= 0x8000)
            {
                W.Out(t, sRef, "");
                W.Out(t, iRef, 0);
                return W.I(Codes.BadHandle);
            }
            var c = f.Fields[(int)fld];
            W.Out(t, iRef, c.IData);
            W.Out(t, sRef, c.SData ?? "");
            return W.I(0);
        }

        [Builtin(
            "FormGetInst",
            Doc = "1106082",
            Errors = "269 no such form or field (returned, iData 0, sData \"\")",
            Notes = "The integer and string FormSetInst stored with the field (a form being built or shown)."
        )]
        private static Value FormGetInst(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            return GetInst(h, t, Form(h, A.I(a.Get(0))), A.Short(A.I(a.Get(1))), a.Get(2), a.Get(3));
        }

        [Builtin(
            "FormGetCurrInst",
            Doc = "1106080",
            Errors = "269 no form or field pressed (returned, iData 0, sData \"\")",
            Notes = "FormGetInst of FormCurr's form and field (the button whose function runs)."
        )]
        private static Value FormGetCurrInst(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            return GetInst(h, t, Form(h, h.CurrForm), h.CurrField, a.Get(0), a.Get(1));
        }

        [Builtin(
            "FormSetInst",
            Doc = "1106103",
            Errors = "269 no such form or field (returned)",
            Effects = "stores an integer and a string with the field"
        )]
        private static Value FormSetInst(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            var f = Form(h, A.I(a.Get(0)));
            long fld = A.Short(A.I(a.Get(1)));
            if (f == null || fld < 0 || fld >= f.Fields.Count)
                return W.I(Codes.BadHandle);
            f.Fields[(int)fld].IData = A.I(a.Get(2));
            f.Fields[(int)fld].SData = A.S(a.Get(3));
            return W.I(0);
        }

        [Builtin(
            "FormPosition",
            Doc = "1106095",
            Errors = "274 no form being built (returned)",
            Notes = "FormPosition(X, Y, Mode): where the form being built opens; the simulator keeps no screen positions: 0."
        )]
        private static Value FormPosition(SimHost h, CicodeTask t, ArgList a) =>
            W.I(Form(h, h.CurrentForm) != null ? 0 : Codes.InvalidArg);

        [Builtin(
            "FormGoto",
            Doc = "1106084",
            Errors = "274 no such form (returned)",
            Notes = "Brings a shown form to the front: nothing to do in the simulator, 0."
        )]
        private static Value FormGoto(SimHost h, CicodeTask t, ArgList a) =>
            W.I(Form(h, A.I(a.Get(0))) != null ? 0 : Codes.InvalidArg);

        [Builtin(
            "FormWndHnd",
            Doc = "1106105",
            Notes = "A shown form's window handle: the simulator has no windows and gives 0x10000 + the form handle (guess: a stand-in, nonzero like a real handle); 0 when the form is not shown."
        )]
        private static Value FormWndHnd(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Shown(h, A.I(a.Get(0)));
            return W.I(f != null ? 0x10000 + f.Handle : 0);
        }

        // ------------------------------------------------------------------ fields
        /// <summary>a field of the form being built; its handle or -1.</summary>
        private static int AddField(
            SimHost h,
            long col,
            long row,
            long width,
            long height,
            long type,
            in Value var,
            string text,
            string fn,
            long maxLen,
            string label
        )
        {
            var f = Form(h, h.CurrentForm);
            if (f == null || f.Active)
                return -1;
            if (
                (ushort)col >= 0x8000
                || (short)col >= f.Width
                || (ushort)row >= 0x8000
                || (short)row >= f.Height
                || (ushort)width >= 0x301
                || (ushort)height >= 0x101
                || (type & 0xffff) >= 0x15
            )
                return -1;
            int ty = (int)(type & 0xffff);
            var c = new CField
            {
                Type = ty,
                Col = (short)col,
                Row = (short)row,
                Width = (ushort)width,
                Height = (ushort)height,
                Fn = fn,
                MaxLen = (int)Math.Max(0, Math.Min(255, maxLen)),
                Label = label,
            };
            c.HasVar = KeepsVar(ty) && var.O is Ref;
            if (c.HasVar)
                c.Var = var;
            c.Text = var.O is Ref && KeepsVar(ty) ? VarText(var) : text;
            f.Fields.Add(c);
            h.LastField = f.Fields.Count - 1;
            return h.LastField;
        }

        private static long Opt(in ArgList a, int k, long def) =>
            k < a.Count && !a.Get(k).IsNothing ? A.I(a.Get(k)) : def;

        [Builtin(
            "FormField",
            Doc = "1106079",
            Effects = "adds a field to the form being built",
            Notes = "-1 when no form is being built, it is shown, Col / Row lie outside the form, Width is above 768, Height above 256 or Type above 20. Types 1, 2, 8, 9, 10, 11, 13, 14, 15 and 18 keep the Buffer variable that OK writes; a button (4 / 5 / 6) runs Fn."
        )]
        private static Value FormField(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            string label = Local(A.S(a.Get(6)));
            return W.I(
                AddField(
                    h,
                    A.I(a.Get(0)),
                    A.I(a.Get(1)),
                    A.I(a.Get(2)),
                    A.I(a.Get(3)),
                    A.I(a.Get(4)),
                    a.Get(5),
                    label,
                    A.S(a.Get(7)),
                    Opt(a, 8, 0),
                    label
                )
            );
        }

        private static int Prompt(SimHost h, long col, long row, string text, long w, long ht)
        {
            if (Form(h, h.CurrentForm) == null)
                return -1;
            if ((short)w > 0 && (short)ht > 0)
                return AddField(h, col, row, w, ht, 17, Value.Nothing, text, "", 0, text);
            return AddField(h, col, row, text.Length, 1, 3, Value.Nothing, text, "", 0, text);
        }

        [Builtin(
            "FormPrompt",
            Doc = "1106096",
            Effects = "adds a text to the form being built",
            Notes = "Width and Height both above 0: a text box of that size (type 17); else one line as wide as the text (type 3)."
        )]
        private static Value FormPrompt(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            return W.I(
                Prompt(h, A.I(a.Get(0)), A.I(a.Get(1)), Local(A.S(a.Get(2))), Opt(a, 3, 0), Opt(a, 4, 0))
            );
        }

        [Builtin(
            "FormInput",
            Doc = "1106086",
            Effects = "adds a prompt and an edit field to the form being built",
            Notes = "The prompt at (Col, Row), the edit field (type 1, the variable sBuf) at Col + the prompt's length + 2; returns the edit field's handle."
        )]
        private static Value FormInput(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            long col = A.I(a.Get(0)),
                row = A.I(a.Get(1));
            string prompt = Local(A.S(a.Get(2)));
            Prompt(h, col, row, prompt, 0, 0);
            return W.I(
                AddField(
                    h,
                    col + prompt.Length + 2,
                    row,
                    A.I(a.Get(4)),
                    1,
                    1,
                    a.Get(3),
                    VarText(a.Get(3)),
                    "",
                    Opt(a, 5, 0),
                    prompt
                )
            );
        }

        [Builtin(
            "FormEdit",
            Doc = "1106078",
            Effects = "adds an edit field to the form being built",
            Notes = "Type 1 (one line); a height above 1 makes a multi-line edit (18)."
        )]
        private static Value FormEdit(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            long ht = Math.Max(1L, (long)(short)Opt(a, 5, 1));
            bool flag = Opt(a, 6, 0) != 0;
            int type = ht < 2 ? (flag ? 20 : 1) : (flag ? 19 : 18);
            return W.I(
                AddField(
                    h,
                    A.I(a.Get(0)),
                    A.I(a.Get(1)),
                    A.I(a.Get(3)),
                    ht,
                    type,
                    a.Get(2),
                    VarText(a.Get(2)),
                    "",
                    Opt(a, 4, 0),
                    ""
                )
            );
        }

        private static Value Password(SimHost h, ArgList a, int type)
        {
            long col = A.I(a.Get(0)),
                row = A.I(a.Get(1));
            string prompt = Local(A.S(a.Get(2)));
            Prompt(h, col, row, prompt, 0, 0);
            return W.I(
                AddField(
                    h,
                    col + prompt.Length,
                    row,
                    A.I(a.Get(4)),
                    1,
                    type,
                    a.Get(3),
                    VarText(a.Get(3)),
                    "",
                    0,
                    prompt
                )
            );
        }

        [Builtin(
            "FormPassword",
            Doc = "1106094",
            Effects = "adds a prompt and a password field to the form being built",
            Notes = "The password field (type 2) at Col + the prompt's length."
        )]
        private static Value FormPassword(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            return Password(h, a, 2);
        }

        [Builtin(
            "FormSecurePassword",
            Doc = "1106100",
            Effects = "adds a prompt and a secure password field to the form being built",
            Notes = "As FormPassword with type 15."
        )]
        private static Value FormSecurePassword(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            return Password(h, a, 15);
        }

        [Builtin(
            "FormButton",
            Doc = "1106073",
            Effects = "adds a button to the form being built",
            Notes = "Mode 0 normal (4), 1 OK (5), 2 Cancel (6), 3 disabled (16); as wide as the text + 1. Pressing it runs Fn as a new task (FormCurr gives the form and the button); an OK / Cancel button then closes the form."
        )]
        private static Value FormButton(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            string text = Local(A.S(a.Get(2)));
            long mode = A.Short(A.I(a.Get(4)));
            int type =
                mode == 1 ? 5
                : mode == 2 ? 6
                : mode == 3 ? 16
                : 4;
            return W.I(
                AddField(
                    h,
                    A.I(a.Get(0)),
                    A.I(a.Get(1)),
                    text.Length + 1,
                    1,
                    type,
                    Value.Nothing,
                    text,
                    A.S(a.Get(3)),
                    0,
                    text
                )
            );
        }

        [Builtin(
            "FormRadioButton",
            Doc = "1106097",
            Effects = "adds a radio button to the form being built",
            Notes = "Type 8; the variable holds \"0\" / \"1\"."
        )]
        private static Value FormRadioButton(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            string text = Local(A.S(a.Get(2)));
            return W.I(
                AddField(h, A.I(a.Get(0)), A.I(a.Get(1)), text.Length + 4, 1, 8, a.Get(3), text, "", 0, text)
            );
        }

        [Builtin(
            "FormCheckBox",
            Doc = "1106074",
            Effects = "adds a check box to the form being built",
            Notes = "Type 9; the variable holds \"0\" / \"1\"."
        )]
        private static Value FormCheckBox(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            string text = Local(A.S(a.Get(2)));
            return W.I(
                AddField(h, A.I(a.Get(0)), A.I(a.Get(1)), text.Length + 4, 1, 9, a.Get(3), text, "", 0, text)
            );
        }

        [Builtin("FormGroupBox", Doc = "1106085", Effects = "adds a group box to the form being built")]
        private static Value FormGroupBox(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            string text = VarText(a.Get(4));
            return W.I(
                AddField(
                    h,
                    A.I(a.Get(0)),
                    A.I(a.Get(1)),
                    A.I(a.Get(2)),
                    A.I(a.Get(3)),
                    7,
                    Value.Nothing,
                    text,
                    "",
                    0,
                    text
                )
            );
        }

        [Builtin(
            "FormListBox",
            Doc = "1106088",
            Effects = "adds a list box to the form being built",
            Notes = "Mode 0 type 10, else 13; OK writes the selected item. FormAddList adds its items."
        )]
        private static Value FormListBox(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            int type = Opt(a, 5, 0) == 0 ? 10 : 13;
            return W.I(
                AddField(
                    h,
                    A.I(a.Get(0)),
                    A.I(a.Get(1)),
                    A.I(a.Get(2)),
                    A.I(a.Get(3)),
                    type,
                    a.Get(4),
                    VarText(a.Get(4)),
                    "",
                    0,
                    ""
                )
            );
        }

        [Builtin(
            "FormComboBox",
            Doc = "1106075",
            Effects = "adds a combo box to the form being built",
            Notes = "Mode 0 type 11, else 14; OK writes the selected or typed text. FormAddList adds its items."
        )]
        private static Value FormComboBox(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            int type = Opt(a, 5, 0) == 0 ? 11 : 14;
            return W.I(
                AddField(
                    h,
                    A.I(a.Get(0)),
                    A.I(a.Get(1)),
                    A.I(a.Get(2)),
                    A.I(a.Get(3)),
                    type,
                    a.Get(4),
                    VarText(a.Get(4)),
                    "",
                    0,
                    ""
                )
            );
        }

        [Builtin(
            "FormAddList",
            Doc = "1106072",
            Effects = "adds an item to the last list or combo box of the form being built",
            Errors = "274 an empty text, or the last field is not a list or combo box (returned)"
        )]
        private static Value FormAddList(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            string text = A.S(a.Get(0));
            if (text.Length == 0)
                return W.I(Codes.InvalidArg);
            var f = Form(h, h.CurrentForm);
            if (
                f == null
                || h.LastField < 0
                || h.LastField >= f.Fields.Count
                || !IsList(f.Fields[h.LastField].Type)
            )
                return W.I(Codes.InvalidArg);
            f.Fields[h.LastField].Items.Add(text);
            return W.I(0);
        }

        private static CField? ShownList(SimHost h, ArgList a, out CForm? f, out long fld)
        {
            f = Shown(h, A.I(a.Get(0)));
            fld = A.Short(A.I(a.Get(1)));
            if (f == null || fld < 0 || fld >= f.Fields.Count)
                return null;
            return f.Fields[(int)fld];
        }

        [Builtin(
            "FormListAddText",
            Doc = "1106087",
            Effects = "adds an item to a list or combo box of the shown form",
            Errors = "274 an empty text; 269 the form is not shown or no such field (returned)"
        )]
        private static Value FormListAddText(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            string text = A.S(a.Get(2));
            if (text.Length == 0)
                return W.I(Codes.InvalidArg);
            var c = ShownList(h, a, out var f, out long fld);
            if (c == null)
                return W.I(Codes.BadHandle);
            if (IsList(c.Type))
            {
                c.Items.Add(text);
                Changed(h, f!, (int)fld);
            }
            return W.I(0);
        }

        [Builtin(
            "FormListSelectText",
            Doc = "1106090",
            Effects = "selects an item of a list or combo box of the shown form",
            Errors = "269 the form is not shown or no such field (returned)",
            Notes = "An item not in the list leaves the selection as it is (LB_SELECTSTRING finds nothing)."
        )]
        private static Value FormListSelectText(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            var c = ShownList(h, a, out var f, out long fld);
            if (c == null)
                return W.I(Codes.BadHandle);
            string text = A.S(a.Get(2));
            foreach (string it in c.Items)
                if (it.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                {
                    c.Text = it;
                    Changed(h, f!, (int)fld);
                    break;
                }
            return W.I(0);
        }

        [Builtin(
            "FormListDeleteText",
            Doc = "1106089",
            Effects = "removes an item from a list or combo box of the shown form",
            Errors = "274 an empty text; 269 the form is not shown or no such field (returned)",
            Notes = "The first item equal to the text (without case) goes; none: nothing changes, 0."
        )]
        private static Value FormListDeleteText(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            string text = A.S(a.Get(2));
            if (text.Length == 0)
                return W.I(Codes.InvalidArg);
            var c = ShownList(h, a, out var f, out long fld);
            if (c == null)
                return W.I(Codes.BadHandle);
            for (int i = 0; i < c.Items.Count; i++)
                if (string.Equals(c.Items[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(c.Text, c.Items[i], StringComparison.Ordinal))
                        c.Text = "";
                    c.Items.RemoveAt(i);
                    Changed(h, f!, (int)fld);
                    break;
                }
            return W.I(0);
        }

        // ------------------------------------------------------------------ message and input boxes
        /// <summary>A dialog a task waits for; Result gives (ok, text) to <paramref name="done"/>.</summary>
        private static Value Wait(SimHost h, CicodeTask t, Dialog d, Func<bool, string?, Value> done)
        {
            d.Task = t.Handle;
            d.Window = WindowOf(t);
            Show(h, d);
            t.Wait = new Wait(TaskState.Wait, d.Kind + "(" + d.Title + ")")
            {
                Poll = () => d.Answered,
                Result = () =>
                {
                    d.TakeAnswer(out bool ok, out string? text, out _);
                    d.Drop();
                    h.Interaction.Closed(d);
                    h.World?.Users.NoteCommand();
                    h.World?.Trace?.Event(
                        "dialog",
                        null,
                        t.Handle,
                        ("op", "close"),
                        ("kind", d.Kind.ToString().ToLowerInvariant()),
                        ("title", d.Title),
                        ("result", ok ? "ok" : "cancel")
                    );
                    return done(ok, text);
                },
            };
            return Value.Block;
        }

        private static string Cut254(string s) => s.Length > 254 ? s.Substring(0, 254) : s;

        [Builtin(
            "Message",
            Doc = "1188601",
            Blocking = true,
            Errors = "274 a mode outside the MB_OKCANCEL / icon / MB_DEFBUTTON2 bits (0x171, below 0x142) (raised, returns 0); 282 foreground code (raised, returns 0); 299 Cancel (returned)",
            Notes = "Shows a message box through the dialog host and waits: 0 for OK, 299 for Cancel. Mode bit 1 adds the Cancel button; a Yes / No box (mode 4) is refused by the mode check. Headless: cancelled (299)."
        )]
        private static Value Message(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            long mode = A.I(a.Get(2)) & 0xffff;
            if (mode >= 0x142 || (mode & 0x171) != mode)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            if (t.Foreground)
            {
                // CodeBlock fails:
                W.Raise(t, Codes.FgBlock);
                return W.I(0);
            }
            var d = new Dialog
            {
                Kind = DialogKind.Message,
                Title = Cut254(Local(A.S(a.Get(0)))),
                Text = Cut254(Local(A.S(a.Get(1)))),
                Mode = (int)mode,
            };
            return Wait(h, t, d, (ok, _) => W.I(ok ? 0 : Cancelled));
        }

        [Builtin(
            "Input",
            Doc = "1188594",
            Blocking = true,
            Errors = "282 foreground code (halts the task); 299 Cancel (raised in the waiting task, returns \"\")",
            Notes = "Shows a one-line input box through the dialog host and waits for the text (254 characters at most). Headless: cancelled."
        )]
        private static Value Input(SimHost h, CicodeTask t, ArgList a)
        {
            Pump(h);
            if (t.Foreground)
            {
                t.Vm.Fatal(t, Codes.FgBlock, "Foreground Cicode cannot block (Input)");
                return W.Empty;
            }
            var d = new Dialog
            {
                Kind = DialogKind.Input,
                Title = Cut254(Local(A.S(a.Get(0)))),
                Text = Cut254(Local(A.S(a.Get(1)))),
                Default = Cut254(Local(A.S(a.Get(2)))),
            };
            return Wait(h, t, d, (ok, text) =>
            {
                if (ok)
                    return W.S(A.Cap(text ?? d.Default));
                W.Raise(t, Cancelled);
                return W.Empty;
            });
        }

        private static Value Picker(
            SimHost h,
            CicodeTask t,
            DialogKind kind,
            string title,
            string file,
            string filter,
            string ext
        )
        {
            Pump(h);
            if (t.Foreground)
            {
                t.Vm.Fatal(t, Codes.FgBlock, "Foreground Cicode cannot block (" + kind + ")");
                return W.Empty;
            }
            var d = new Dialog
            {
                Kind = kind,
                Title = Local(title),
                Default = file,
                Filter = filter,
                Extension = ext,
            };
            return Wait(h, t, d, (ok, text) =>
            {
                if (ok)
                    return W.S(A.Cap(text ?? d.Default));
                W.Raise(t, Cancelled);
                return W.Empty;
            });
        }

        [Builtin(
            "FormOpenFile",
            Doc = "1106093",
            Blocking = true,
            Errors = "282 foreground code (halts the task); 299 Cancel (guess: raised, returns \"\") (verify)",
            Notes = "A file picker through the dialog host: the chosen path. Headless: cancelled."
        )]
        private static Value FormOpenFile(SimHost h, CicodeTask t, ArgList a) =>
            Picker(h, t, DialogKind.OpenFile, A.S(a.Get(0)), A.S(a.Get(1)), A.S(a.Get(2)), "");

        [Builtin(
            "FormSaveAsFile",
            Doc = "1106099",
            Blocking = true,
            Errors = "282 foreground code (halts the task); 299 Cancel (guess: raised, returns \"\") (verify)",
            Notes = "A save-as picker through the dialog host: the chosen path. Headless: cancelled."
        )]
        private static Value FormSaveAsFile(SimHost h, CicodeTask t, ArgList a) =>
            Picker(h, t, DialogKind.SaveFile, A.S(a.Get(0)), A.S(a.Get(1)), A.S(a.Get(2)), A.S(a.Get(3)));

        [Builtin(
            "FormSelectPrinter",
            Doc = "1106101",
            Blocking = true,
            Errors = "282 foreground code (halts the task); 299 Cancel (guess: raised, returns \"\") (verify)",
            Notes = "A printer picker through the dialog host: the printer's name. Headless: cancelled."
        )]
        private static Value FormSelectPrinter(SimHost h, CicodeTask t, ArgList a) =>
            Picker(h, t, DialogKind.SelectPrinter, "", "", "", "");

        [Builtin(
            "Prompt",
            Doc = "1188612",
            Effects = "writes the text on the window's prompt line (the dialog host's Prompt)",
            Notes = "The window is the task's (0 when it has none); returns 0."
        )]
        private static Value PromptFn(SimHost h, CicodeTask t, ArgList a)
        {
            string text = Local(A.S(a.Get(0)));
            h.Interaction.Prompt(WindowOf(t), text);
            h.World?.Trace?.Event(
                "print",
                null,
                t.Handle >= 0 ? (object)t.Handle : null,
                ("text", "Prompt: " + text)
            );
            return W.I(0);
        }
    }
}
