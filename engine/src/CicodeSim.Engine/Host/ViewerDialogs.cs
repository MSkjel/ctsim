using System;
using System.Collections.Generic;
using System.Threading;
using CicodeSim.Builtins;
using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>The simulation's dialogs, shown by a viewer when one is connected.</summary>
    public sealed class ViewerDialogs : IInteractionHost
    {
        /// <summary>Real ms a viewer has to answer before the dialog is cancelled.</summary>
        public int TimeoutMs = 60000;

        private readonly IInteractionHost _fallback;
        private readonly Func<bool> _hasViewer;
        private readonly Action<string, JsonValue> _send;
        private readonly Action<string>? _log;
        private readonly object _lock = new object();
        private readonly Dictionary<int, (Dialog Dialog, Timer Timer)> _open =
            new Dictionary<int, (Dialog, Timer)>();

        /// <param name="fallback">the host without a viewer</param>
        /// <param name="hasViewer">a viewer that shows dialogs is connected</param>
        /// <param name="send">sends an event (name, body) to those viewers</param>
        /// <param name="log">writes a line about a dialog no viewer answered (cancelled, denied
        /// or timed out), null = not written</param>
        /// <param name="localise">the display client's localisation of a shown string ('@(native)'
        /// to the current language; called on the simulation's thread), null = as given</param>
        public ViewerDialogs(
            IInteractionHost fallback,
            Func<bool> hasViewer,
            Action<string, JsonValue> send,
            Action<string>? log = null,
            Func<string, string>? localise = null
        )
        {
            _fallback = fallback;
            _hasViewer = hasViewer;
            _send = send;
            _log = log;
            _localise = localise;
        }

        private readonly Func<string, string>? _localise;

        public IInteractionHost Fallback => _fallback;

        private static bool Shown(DialogKind k) =>
            k == DialogKind.PopupMenu
            || k == DialogKind.Message
            || k == DialogKind.Input
            || k == DialogKind.Form
            || k == DialogKind.Permission;

        public void Show(Dialog dialog)
        {
            if (!Shown(dialog.Kind) || !_hasViewer())
            {
                if (
                    ReferenceEquals(_fallback, HeadlessInteraction.Instance)
                    && dialog.Kind == DialogKind.PopupMenu
                )
                    _log?.Invoke(
                        HeadlessInteraction.DialogText(dialog) + ": cancelled (no viewer shows dialogs)"
                    );
                if (
                    ReferenceEquals(_fallback, HeadlessInteraction.Instance)
                    && dialog.Kind == DialogKind.Permission
                )
                    _log?.Invoke(
                        HeadlessInteraction.DialogText(dialog) + ": denied (no viewer shows dialogs)"
                    );
                _fallback.Show(dialog);
                return;
            }
            var timer = new Timer(_ => Expire(dialog.Id), null, Timeout.Infinite, Timeout.Infinite);
            lock (_lock)
                _open[dialog.Id] = (dialog, timer);
            timer.Change(TimeoutMs, Timeout.Infinite);
            _send("ctsim/dialog", ToJson(dialog, _localise));
        }

        private void Expire(int id)
        {
            Dialog? d = null;
            lock (_lock)
            {
                if (_open.TryGetValue(id, out var e))
                    d = e.Dialog;
            }
            if (d == null)
                return;
            _log?.Invoke(
                HeadlessInteraction.DialogText(d)
                    + ": no answer in "
                    + (TimeoutMs / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " s, cancelled"
            );
            d.Cancel();
            // closed here for the viewer: the waiting task takes the cancel at its next turn (its
            // Closed then finds it gone), and a dialog whose task is gone (a page closed under a
            // message box, a menu whose CI loop was halted) or that no task waits for (a
            // FormRead(1) form) does not stay shown
            Close(id);
        }

        public void Changed(Dialog dialog, int field)
        {
            bool ours;
            lock (_lock)
                ours = _open.ContainsKey(dialog.Id);
            if (ours)
                _send("ctsim/dialog", ToJson(dialog, _localise));
            else
                _fallback.Changed(dialog, field);
        }

        public void Closed(Dialog dialog)
        {
            if (!Close(dialog.Id))
                _fallback.Closed(dialog);
        }

        private bool Close(int id)
        {
            Timer? timer;
            lock (_lock)
            {
                if (!_open.TryGetValue(id, out var e))
                    return false;
                _open.Remove(id);
                timer = e.Timer;
            }
            timer.Dispose();
            var b = JsonValue.NewObject();
            b.Set("id", JsonValue.Of(id));
            _send("ctsim/dialogClosed", b);
            return true;
        }

        public void Prompt(int window, string text) => _fallback.Prompt(window, text);

        /// <summary>The last viewer that shows dialogs went: every dialog shown is cancelled (as
        /// the timeout would), so no code waits for a viewer that is gone.</summary>
        public void CancelAll()
        {
            List<(Dialog Dialog, Timer Timer)> open;
            lock (_lock)
                open = new List<(Dialog, Timer)>(_open.Values);
            foreach (var (d, _) in open)
            {
                d.Cancel();
                Close(d.Id);
            }
        }

        /// <summary>The dialogs shown now (their ids).</summary>
        public List<int> Open()
        {
            lock (_lock)
                return new List<int>(_open.Keys);
        }

        /// <summary>ctsim/answer: answer a shown dialog. False when no dialog has the id.</summary>
        public bool Answer(JsonValue a, out string error)
        {
            error = "";
            var idv = a.Get("id");
            if (idv == null || !idv.IsNumber)
            {
                error = "answer needs the dialog's id";
                return false;
            }
            int id = (int)idv.Long;
            Dialog? d;
            lock (_lock)
                d = _open.TryGetValue(id, out var e) ? e.Dialog : null;
            if (d == null || d.Answered)
            {
                error =
                    "no dialog "
                    + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " is shown";
                return false;
            }
            var okv = a.Get("ok");
            bool? ok = okv != null && okv.Kind == JsonKind.Bool ? okv.Bool : (bool?)null;
            var tv = a.Get("text");
            string? text = tv != null && tv.Kind == JsonKind.String ? tv.Str : null;
            switch (d.Kind)
            {
                case DialogKind.PopupMenu:
                {
                    var cv = a.Get("choice");
                    int choice = cv != null && cv.IsNumber ? (int)cv.Long : 0;
                    if (ok == false || choice <= 0 || !CtUtil.HasItem(d.Menu, choice))
                        d.Cancel();
                    else
                        d.Ok(choice.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }
                case DialogKind.Permission:
                {
                    // {permission: "once" | "session" | "always" | "deny"}; ok alone = once / deny
                    var pm = a.Get("permission");
                    string choice =
                        pm != null && pm.Kind == JsonKind.String
                            ? pm.Str.Trim().ToLowerInvariant()
                            : (ok == true ? "once" : "deny");
                    if (ok == false || (choice != "once" && choice != "session" && choice != "always"))
                        d.Cancel();
                    else
                        d.Ok(choice);
                    break;
                }
                case DialogKind.Form:
                {
                    if (ok == false)
                    {
                        d.Cancel();
                        break;
                    }
                    // a button pressed (its function runs; an OK / Cancel button closes the form)
                    var pv = a.Get("press");
                    if (pv != null && pv.IsNumber && ok == null)
                    {
                        d.Press((int)pv.Long);
                        break;
                    }
                    var texts = new Dictionary<int, string>();
                    var vv = a.Get("values");
                    if (vv != null && vv.Kind == JsonKind.Array)
                    {
                        var fields = d.Fields;
                        var items = vv.Items;
                        for (int i = 0; i < items.Count && i < fields.Count; i++)
                            if (!items[i].IsNull)
                                texts[fields[i].Index] =
                                    items[i].Kind == JsonKind.String ? items[i].Str : items[i].ToString();
                    }
                    d.Ok(texts);
                    break;
                }
                default:
                    if (ok == false)
                        d.Cancel();
                    else
                        d.Ok(text);
                    break;
            }
            return true;
        }

        private static string KindWord(DialogKind k) =>
            k switch
            {
                DialogKind.PopupMenu => "popupMenu",
                DialogKind.Message => "message",
                DialogKind.Input => "input",
                DialogKind.Permission => "permission",
                _ => "form",
            };

        /// <summary>The ctsim/dialog body for a dialog.</summary>
        public static JsonValue ToJson(Dialog d, Func<string, string>? localise = null)
        {
            Func<string, string> L = localise ?? (x => x);
            var o = JsonValue.NewObject();
            o.Set("id", JsonValue.Of(d.Id));
            o.Set("window", JsonValue.Of(d.Window));
            o.Set("kind", KindWord(d.Kind));
            o.Set("task", JsonValue.Of(d.Task));
            o.Set("title", L(d.Title));
            o.Set("text", L(d.Text));
            switch (d.Kind)
            {
                case DialogKind.PopupMenu:
                    if (d.X >= 0)
                        o.Set("x", JsonValue.Of(d.X));
                    if (d.Y >= 0)
                        o.Set("y", JsonValue.Of(d.Y));
                    if (d.MenuFont != null)
                        o.Set("font", d.MenuFont.ToJson());
                    o.Set("items", CtUtil.ToJson(d.Menu, L));
                    break;
                case DialogKind.Permission:
                    o.Set("access", d.Access);
                    o.Set("action", d.Action);
                    o.Set("target", d.Target);
                    o.Set("detail", d.Detail);
                    o.Set(
                        "choices",
                        JsonValue.Array(
                            new[]
                            {
                                JsonValue.Of("once"),
                                JsonValue.Of("session"),
                                JsonValue.Of("always"),
                                JsonValue.Of("deny"),
                            }
                        )
                    );
                    break;
                case DialogKind.Message:
                    o.Set("mode", JsonValue.Of(d.Mode));
                    o.Set(
                        "buttons",
                        JsonValue.Array(
                            d.HasCancel
                                ? new[] { JsonValue.Of("OK"), JsonValue.Of("Cancel") }
                                : new[] { JsonValue.Of("OK") }
                        )
                    );
                    break;
                case DialogKind.Input:
                {
                    var f = JsonValue.NewObject();
                    f.Set("label", L(d.Text));
                    f.Set("value", d.Default);
                    f.Set("kind", "edit");
                    f.Set("width", JsonValue.Of(254));
                    o.Set("fields", JsonValue.Array(new[] { f }));
                    o.Set("buttons", JsonValue.Array(new[] { JsonValue.Of("OK"), JsonValue.Of("Cancel") }));
                    break;
                }
                default:
                {
                    o.Set("mode", JsonValue.Of(d.Mode));
                    o.Set("size", JsonValue.Array(new[] { JsonValue.Of(d.Width), JsonValue.Of(d.Height) }));
                    var fields = JsonValue.NewArray();
                    var buttons = JsonValue.NewArray();
                    foreach (var fd in d.Fields)
                    {
                        var f = JsonValue.NewObject();
                        f.Set("index", JsonValue.Of(fd.Index));
                        f.Set("label", L(fd.Label));
                        f.Set("value", fd.Text);
                        f.Set("kind", fd.Kind);
                        f.Set("width", JsonValue.Of(fd.Width));
                        f.Set("height", JsonValue.Of(fd.Height));
                        f.Set("col", JsonValue.Of(fd.Col));
                        f.Set("row", JsonValue.Of(fd.Row));
                        if (fd.MaxLength > 0)
                            f.Set("maxLength", JsonValue.Of(fd.MaxLength));
                        if (fd.Items.Count > 0)
                        {
                            var it = JsonValue.NewArray();
                            foreach (string s in fd.Items)
                                it.Add(JsonValue.Of(s));
                            f.Set("items", it);
                        }
                        fields.Add(f);
                        if (fd.Kind == "ok" || fd.Kind == "cancel" || fd.Kind == "button")
                            buttons.Add(JsonValue.Of(L(fd.Label)));
                    }
                    o.Set("fields", fields);
                    o.Set("buttons", buttons);
                    break;
                }
            }
            return o;
        }
    }
}
