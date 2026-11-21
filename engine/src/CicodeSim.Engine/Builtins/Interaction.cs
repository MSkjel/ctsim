using System;
using System.Collections.Generic;
using CicodeSim.Values;

namespace CicodeSim.Builtins
{
    /// <summary>What a dialog is.</summary>
    public enum DialogKind
    {
        /// <summary>A form (FormNew ... FormRead).</summary>
        Form,

        /// <summary>Message(sTitle, sPrompt, Mode): OK, or OK and Cancel (Mode bit 1).</summary>
        Message,

        /// <summary>Input(sTitle, sPrompt, sDefault): one line of text.</summary>
        Input,

        /// <summary>FormOpenFile: a file to open.</summary>
        OpenFile,

        /// <summary>FormSaveAsFile: a file to save as.</summary>
        SaveFile,

        /// <summary>FormSelectPrinter: a printer.</summary>
        SelectPrinter,

        /// <summary>A popup menu (Include CTUTIL.CI DspPopupMenu, the CTUTIL.DLL menu): the
        /// answer's text is the chosen item's number as DspPopupMenu returns it ("0" none).</summary>
        PopupMenu,

        /// <summary>The simulator's own question (OutsideAccess, mode Ask): may the code load a
        /// library, connect to a database or change it? OK's text is 'once', 'session' or
        /// 'always'; Cancel denies.</summary>
        Permission,
    }

    /// <summary>One item of a popup menu (DspPopupMenu's CSV items: '!' disabled, '~' checked,
    /// '[bitmap]' an icon, '&gt;n' a link to menu n, an empty item a separator).</summary>
    public sealed class MenuItem
    {
        public string Label = "";
        public bool Enabled = true,
            Checked,
            Separator;

        /// <summary>The number DspPopupMenu returns for it (menu * 100 + its position among the
        /// menu's items, separators and links not counted); 0 for a separator or a link.</summary>
        public int Id;

        /// <summary>The bitmap named in '[...]' ("" none).</summary>
        public string Bitmap = "";

        /// <summary>A link's submenu, null for an item.</summary>
        public List<MenuItem>? Items;
    }

    /// <summary>One field of a form (FormField types, doc 1106079 and the field adders).</summary>
    public sealed class DialogField
    {
        /// <summary>The field handle (its index in the form).</summary>
        public int Index;

        /// <summary>FormField's type code.</summary>
        public int Type;

        /// <summary>A word for the type (edit, password, text, button, ok, cancel, group, radio,
        /// check, list, combo, disabled, textbox, multiline).</summary>
        public string Kind = "";
        public int Col,
            Row,
            Width,
            Height;

        /// <summary>The prompt left of an input field, or the caption of a button / box.</summary>
        public string Label = "";

        /// <summary>The field's text now (an edit's value, "0" / "1" for a check or radio box,
        /// the selected item of a list).</summary>
        public string Text = "";

        /// <summary>The items of a list or combo box.</summary>
        public List<string> Items = new List<string>();

        /// <summary>OK writes this field's text into a Cicode variable.</summary>
        public bool HasVariable;

        /// <summary>The callback function of a button ("" = none).</summary>
        public string Function = "";

        /// <summary>The most characters an edit takes (0 = 255).</summary>
        public int MaxLength;

        /// <summary>A copy (the host's view never changes the engine's).</summary>
        public DialogField Clone()
        {
            var f = (DialogField)MemberwiseClone();
            f.Items = new List<string>(Items);
            return f;
        }

        public static string KindOf(int type)
        {
            switch (type)
            {
                case 1:
                case 20:
                    return "edit";
                case 2:
                    return "password";
                case 3:
                    return "text";
                case 4:
                    return "button";
                case 5:
                    return "ok";
                case 6:
                    return "cancel";
                case 7:
                    return "group";
                case 8:
                    return "radio";
                case 9:
                    return "check";
                case 10:
                case 13:
                    return "list";
                case 11:
                case 14:
                    return "combo";
                case 15:
                    return "password";
                case 16:
                    return "disabled";
                case 17:
                    return "textbox";
                case 18:
                case 19:
                    return "multiline";
                default:
                    return "none";
            }
        }
    }

    /// <summary>
    /// A dialog the Cicode code shows (form, message box, input box, file or printer picker).
    /// </summary>
    public sealed class Dialog
    {
        private static int _ids;
        private readonly object _lock = new object();
        private bool _done,
            _ok;
        private string? _text;
        private Dictionary<int, string>? _texts;
        private readonly Queue<int> _presses = new Queue<int>();
        private readonly Dictionary<int, string> _typed = new Dictionary<int, string>();

        public int Id { get; } = System.Threading.Interlocked.Increment(ref _ids);
        public DialogKind Kind { get; internal set; }
        public string Title { get; internal set; } = "";

        /// <summary>The prompt of a message or input box.</summary>
        public string Text { get; internal set; } = "";

        /// <summary>Input's default text; a file picker's file name.</summary>
        public string Default { get; internal set; } = "";

        /// <summary>A file picker's filter; a save picker's default extension in Extension.</summary>
        public string Filter { get; internal set; } = "";
        public string Extension { get; internal set; } = "";

        /// <summary>FormNew's / Message's mode.</summary>
        public int Mode { get; internal set; }

        /// <summary>A form's size in columns and rows.</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }

        /// <summary>The form handle (-1 for the other kinds).</summary>
        public int Handle { get; internal set; } = -1;

        /// <summary>The window the code ran in (-1 = none).</summary>
        public int Window { get; internal set; } = -1;

        /// <summary>A popup menu's position in the window's page pixels (-1 = at the pointer).</summary>
        public int X { get; internal set; } = -1;
        public int Y { get; internal set; } = -1;

        public World.Display.FontInfo? MenuFont { get; internal set; }

        /// <summary>A popup menu's items (menu 0 with its linked submenus).</summary>
        public IReadOnlyList<MenuItem> Menu { get; internal set; } = Array.Empty<MenuItem>();

        /// <summary>A permission question: 'dll' or 'sql'; 'load', 'connect' or 'write'; the
        /// library's path or the database's identity; the statement or the function asked for.</summary>
        public string Access { get; internal set; } = "";
        public string Action { get; internal set; } = "";
        public string Target { get; internal set; } = "";
        public string Detail { get; internal set; } = "";

        /// <summary>A permission question's answer was applied (several calls may wait for one),
        /// and what it allowed (the later takers get the same).</summary>
        internal bool Taken,
            Allowed;

        /// <summary>The task that waits for the answer (-1 = none: a FormRead(1) form).</summary>
        public int Task { get; internal set; } = -1;

        /// <summary>A copy of the form's fields when it was shown (or last changed by the code).
        /// Each change makes a new list, so a host may read it on its own thread while the code
        /// changes a field (a list it holds never changes under it).</summary>
        public IReadOnlyList<DialogField> Fields
        {
            get
            {
                lock (_lock)
                    return _fields;
            }
        }
        private DialogField[] _fields = Array.Empty<DialogField>();

        internal void SetFields(List<DialogField> fields)
        {
            var a = fields.ToArray();
            lock (_lock)
                _fields = a;
        }

        internal void SetField(int i, DialogField f)
        {
            lock (_lock)
            {
                if (i < 0 || i >= _fields.Length)
                    return;
                var a = (DialogField[])_fields.Clone();
                a[i] = f;
                _fields = a;
            }
        }

        /// <summary>Message: Mode bit 1 gives an OK and a Cancel button (else OK alone).</summary>
        public bool HasCancel => Kind != DialogKind.Message || (Mode & 1) != 0;

        /// <summary>OK with one text (Input, a file picker, a printer; ignored by a form and a message).</summary>
        public void Ok(string? text = null)
        {
            lock (_lock)
            {
                if (_done)
                    return;
                _done = true;
                _ok = true;
                _text = text;
            }
        }

        /// <summary>OK of a form with the texts the user entered by field handle (fields not
        /// named keep their text).</summary>
        public void Ok(IDictionary<int, string> texts)
        {
            lock (_lock)
            {
                if (_done)
                    return;
                _done = true;
                _ok = true;
                _texts = new Dictionary<int, string>(texts);
            }
        }

        /// <summary>Cancel (the Cancel button, Esc, closing the window).</summary>
        public void Cancel()
        {
            lock (_lock)
            {
                if (_done)
                    return;
                _done = true;
                _ok = false;
            }
        }

        /// <summary>A form button was pressed (its callback runs as a new task; an OK / Cancel
        /// button also closes the form).</summary>
        public void Press(int field)
        {
            lock (_lock)
            {
                if (!_done)
                    _presses.Enqueue(field);
            }
        }

        /// <summary>The user typed into a field of a shown form (FormGetText reads it).</summary>
        public void SetText(int field, string text)
        {
            lock (_lock)
            {
                if (!_done)
                    _typed[field] = text;
            }
        }

        public bool Answered
        {
            get
            {
                lock (_lock)
                    return _done;
            }
        }

        internal bool TakeTyped(out Dictionary<int, string> typed, out List<int> presses)
        {
            lock (_lock)
            {
                typed = new Dictionary<int, string>(_typed);
                _typed.Clear();
                presses = new List<int>(_presses);
                _presses.Clear();
                return typed.Count > 0 || presses.Count > 0;
            }
        }

        internal bool TakeAnswer(out bool ok, out string? text, out Dictionary<int, string>? texts)
        {
            lock (_lock)
            {
                ok = _ok;
                text = _text;
                texts = _texts;
                return _done;
            }
        }

        /// <summary>The engine closed it (FormDestroy, a new form): no answer is taken any more.</summary>
        internal void Drop()
        {
            lock (_lock)
                _done = true;
        }
    }

    /// <summary>The UI for a simulation's dialogs. Called on the simulation's thread.</summary>
    public interface IInteractionHost
    {
        /// <summary>Show a dialog; answer it now or later (Dialog.Ok / Cancel / Press).</summary>
        void Show(Dialog dialog);

        /// <summary>The code changed a shown form's field (FormSetText, FormSetData, a list).</summary>
        void Changed(Dialog dialog, int field);

        /// <summary>The code closed a dialog, or it was answered (no more answers are taken).</summary>
        void Closed(Dialog dialog);

        /// <summary>Prompt(sMessage): the prompt line of a window.</summary>
        void Prompt(int window, string text);
    }

    /// <summary>No UI: every dialog is cancelled at once (a form's FormRead returns 299 and writes
    /// nothing, Input gives "" with 299), so an unattended run never writes a variable from a
    /// form. Each is logged.</summary>
    public class HeadlessInteraction : IInteractionHost
    {
        public static readonly HeadlessInteraction Instance = new HeadlessInteraction();

        /// <summary>Called with a line per dialog it cancelled.</summary>
        public Action<string>? Log;

        public virtual void Show(Dialog dialog)
        {
            Log?.Invoke(DialogText(dialog) + ": cancelled (no UI)");
            dialog.Cancel();
        }

        public virtual void Changed(Dialog dialog, int field) { }

        public virtual void Closed(Dialog dialog) { }

        public virtual void Prompt(int window, string text) { }

        public static string DialogText(Dialog d)
        {
            string k =
                d.Kind == DialogKind.Form ? "form"
                : d.Kind == DialogKind.Message ? "message box"
                : d.Kind == DialogKind.Input ? "input box"
                : d.Kind == DialogKind.SelectPrinter ? "printer picker"
                : d.Kind == DialogKind.PopupMenu ? "popup menu"
                : d.Kind == DialogKind.Permission ? "permission question"
                : "file picker";
            return k + " '" + d.Title + "'" + (d.Text.Length > 0 ? " (" + d.Text + ")" : "");
        }
    }

    /// <summary>Answers dialogs from a scenario's ``forms`` list.</summary>
    public sealed class ScenarioInteraction : IInteractionHost
    {
        private sealed class Answer
        {
            public string Title = "*",
                Kind = "",
                Result = "ok",
                Text = "";
            public bool HasText,
                Repeat,
                Used;
            public List<(string Name, string Value)> Fields = new List<(string, string)>();
            public List<string> Press = new List<string>();
        }

        private readonly List<Answer> _answers = new List<Answer>();

        /// <summary>The host of dialogs no entry answers (default: cancel them).</summary>
        public IInteractionHost Fallback = HeadlessInteraction.Instance;

        /// <summary>Called with a line per answer.</summary>
        public Action<string>? Log;

        /// <summary>The prompt line texts, by window.</summary>
        public readonly Dictionary<int, string> Prompts = new Dictionary<int, string>();

        /// <summary>From the scenario's <c>forms</c> value (a JSON array of answers), or from a
        /// whole scenario object holding one.</summary>
        public static ScenarioInteraction FromJson(JsonValue? json)
        {
            var s = new ScenarioInteraction();
            if (json == null)
                return s;
            if (json.Kind == JsonKind.Object && json.Get("forms") != null)
                json = json.Get("forms");
            if (json == null || json.Kind != JsonKind.Array)
                return s;
            foreach (var e in json.Items)
            {
                if (e.Kind != JsonKind.Object)
                    continue;
                var a = new Answer();
                var t = e.Get("title");
                if (t != null)
                    a.Title = t.Text;
                var k = e.Get("kind");
                if (k != null && k.Kind == JsonKind.String)
                    a.Kind = k.Str;
                var r = e.Get("result");
                if (r != null && r.Kind == JsonKind.String)
                    a.Result = r.Str;
                var tx = e.Get("text");
                if (tx != null)
                {
                    a.HasText = true;
                    a.Text = tx.Text;
                }
                var rep = e.Get("repeat");
                a.Repeat = rep != null && rep.IsSet;
                var f = e.Get("fields");
                if (f != null && f.Kind == JsonKind.Object)
                    foreach (var kv in f.Pairs)
                        a.Fields.Add((kv.Key, kv.Value.Text));
                var p = e.Get("press");
                if (p != null && p.Kind == JsonKind.Array)
                    foreach (var x in p.Items)
                        a.Press.Add(x.Text);
                else if (p != null && p.Kind == JsonKind.String)
                    a.Press.Add(p.Str);
                s._answers.Add(a);
            }
            return s;
        }

        public int Count => _answers.Count;

        private static string KindWord(DialogKind k) =>
            k == DialogKind.Form ? "form"
            : k == DialogKind.Message ? "message"
            : k == DialogKind.Input ? "input"
            : k == DialogKind.OpenFile ? "openFile"
            : k == DialogKind.SaveFile ? "saveFile"
            : k == DialogKind.PopupMenu ? "popupMenu"
            : k == DialogKind.Permission ? "permission"
            : "printer";

        public void Show(Dialog dialog)
        {
            foreach (var a in _answers)
            {
                if (a.Used && !a.Repeat)
                    continue;
                if (
                    a.Kind.Length > 0
                    && !string.Equals(a.Kind, KindWord(dialog.Kind), StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                if (
                    a.Title != "*"
                    && !string.Equals(a.Title.Trim(), dialog.Title.Trim(), StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                a.Used = true;
                Log?.Invoke(HeadlessInteraction.DialogText(dialog) + ": " + a.Result + " (scenario)");
                foreach (string name in a.Press)
                {
                    int f = FieldOf(dialog, name);
                    if (f >= 0)
                        dialog.Press(f);
                }
                if (string.Equals(a.Result, "cancel", StringComparison.OrdinalIgnoreCase))
                {
                    dialog.Cancel();
                    return;
                }
                if (string.Equals(a.Result, "open", StringComparison.OrdinalIgnoreCase))
                    return; // stays shown (a FormRead(1) form)
                if (dialog.Kind == DialogKind.Form)
                {
                    var texts = new Dictionary<int, string>();
                    foreach (var (name, value) in a.Fields)
                    {
                        int f = FieldOf(dialog, name);
                        if (f >= 0)
                            texts[f] = value;
                    }
                    dialog.Ok(texts);
                }
                else
                    dialog.Ok(a.HasText ? a.Text : dialog.Default);
                return;
            }
            Fallback.Show(dialog);
        }

        private static int FieldOf(Dialog d, string name)
        {
            if (
                name.StartsWith("#", StringComparison.Ordinal)
                && int.TryParse(
                    name.Substring(1),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int h
                )
            )
                return h >= 0 && h < d.Fields.Count ? h : -1;
            string n = name.Trim();
            foreach (var f in d.Fields)
                if (f.HasVariable || f.Type == 4 || f.Type == 5 || f.Type == 6)
                    if (string.Equals(f.Label.Trim(), n, StringComparison.OrdinalIgnoreCase))
                        return f.Index;
            return -1;
        }

        public void Changed(Dialog dialog, int field) { }

        public void Closed(Dialog dialog) => Fallback.Closed(dialog);

        public void Prompt(int window, string text)
        {
            Prompts[window] = text;
            Fallback.Prompt(window, text);
        }
    }
}
