using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>Where a debugger session comes from.</summary>
    internal static partial class DapSessions
    {
        static partial void CreateSession(DapContext context, ref IDapSession? session);

        public static IDapSession Create(DapContext context)
        {
            IDapSession? s = null;
            CreateSession(context, ref s);
            return s ?? new MinimalDapSession(context);
        }
    }

    /// <summary>The stand-in debugger session: initialize (capabilities: none, then the
    /// initialized event), configurationDone, threads, disconnect, ctsim/* requests through the
    /// viewer protocol; every other request fails with a message.</summary>
    internal sealed class MinimalDapSession : IDapSession
    {
        private readonly DapContext _ctx;
        private int _closed;

        public MinimalDapSession(DapContext ctx)
        {
            _ctx = ctx;
        }

        private void Respond(JsonValue req, bool ok, JsonValue? body = null, string? message = null)
        {
            var r = JsonValue.NewObject();
            r.Set("type", "response");
            r.Set("request_seq", req.Get("seq") ?? JsonValue.Of(0));
            r.Set("success", JsonValue.Of(ok));
            r.Set("command", req.Get("command")?.Str ?? "");
            if (message != null)
                r.Set("message", message);
            r.Set("body", body ?? JsonValue.NewObject());
            _ctx.Sink.Send(r);
        }

        public void OnMessage(JsonValue msg)
        {
            if (msg.Get("type")?.Str != "request")
                return;
            string cmd = msg.Get("command")?.Str ?? "";
            switch (cmd)
            {
                case "initialize":
                {
                    var caps = JsonValue.NewObject();
                    caps.Set("supportsConfigurationDoneRequest", JsonValue.True);
                    Respond(msg, true, caps);
                    _ctx.Sink.Send(SimServer.Event("initialized", JsonValue.NewObject()));
                    return;
                }
                case "configurationDone":
                    Respond(msg, true);
                    return;
                case "threads":
                {
                    var arr = JsonValue.NewArray();
                    foreach (var t in _ctx.Debugger.Tasks())
                    {
                        var o = JsonValue.NewObject();
                        o.Set("id", JsonValue.Of(t.Id));
                        o.Set("name", t.Name);
                        arr.Add(o);
                    }
                    var b = JsonValue.NewObject();
                    b.Set("threads", arr);
                    Respond(msg, true, b);
                    return;
                }
                case "disconnect":
                    Respond(msg, true);
                    _ctx.Debugger.Detach();
                    _ctx.Sink.Close();
                    return;
            }
            if (_ctx.HandleCustom(msg))
                return;
            Respond(
                msg,
                false,
                null,
                "not implemented by the minimal debugger session (the extension's DAP layer in engine/src/ctsim/Dap/ implements "
                    + cmd
                    + ")"
            );
        }

        public void OnClosed() => System.Threading.Interlocked.Exchange(ref _closed, 1);
    }
}
