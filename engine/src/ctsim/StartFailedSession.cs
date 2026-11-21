using System;
using System.IO;
using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>ctsim dap when the simulation can't start.</summary>
    internal static class StartFailedSession
    {
        public static int Run(Stream input, Stream output, string reason)
        {
            var reader = new DapReader(input);
            long seq = 0;
            void Send(JsonValue m)
            {
                m.Set("seq", JsonValue.Of(++seq));
                byte[] f = DapFraming.Encode(m);
                output.Write(f, 0, f.Length);
                output.Flush();
            }
            void Respond(JsonValue req, bool ok, JsonValue? body = null, string? message = null)
            {
                var r = JsonValue.NewObject();
                r.Set("type", "response");
                var rs = req.Get("seq");
                r.Set("request_seq", rs != null && rs.Kind == JsonKind.Int ? rs : JsonValue.Of(0));
                r.Set("success", JsonValue.Of(ok));
                r.Set("command", req.Get("command")?.Str ?? "");
                if (message != null)
                    r.Set("message", message);
                r.Set("body", body ?? JsonValue.NewObject());
                Send(r);
            }
            void Refuse(JsonValue req)
            {
                var error = JsonValue.NewObject();
                error.Set("id", JsonValue.Of(1));
                error.Set("format", reason);
                error.Set("showUser", JsonValue.True);
                var body = JsonValue.NewObject();
                body.Set("error", error);
                Respond(req, false, body, reason);
            }
            void Event(string name, JsonValue body)
            {
                var e = JsonValue.NewObject();
                e.Set("type", "event");
                e.Set("event", name);
                e.Set("body", body);
                Send(e);
            }
            try
            {
                while (true)
                {
                    string? text = reader.Read();
                    if (text == null)
                        break;
                    JsonValue msg;
                    try
                    {
                        msg = JsonValue.Parse(text);
                    }
                    catch (FormatException)
                    {
                        continue;
                    }
                    if (msg.Kind != JsonKind.Object || msg.Get("type")?.Str != "request")
                        continue;
                    switch (msg.Get("command")?.Str ?? "")
                    {
                        case "initialize":
                        {
                            var caps = JsonValue.NewObject();
                            caps.Set("supportsTerminateRequest", JsonValue.True);
                            Respond(msg, true, caps);
                            break;
                        }
                        case "launch":
                        case "attach":
                        {
                            var o = JsonValue.NewObject();
                            o.Set("category", "stderr");
                            o.Set("output", reason + "\n");
                            Event("output", o);
                            Refuse(msg);
                            break;
                        }
                        case "terminate":
                            Respond(msg, true);
                            Event("terminated", JsonValue.NewObject());
                            break;
                        case "disconnect":
                            Respond(msg, true);
                            return 1;
                        default:
                            Refuse(msg);
                            break;
                    }
                }
            }
            catch (Exception e)
                when (e is IOException || e is FramingException || e is ObjectDisposedException)
            {
                // the client went away
            }
            return 1;
        }
    }
}
