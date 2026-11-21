using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    internal sealed class DdeTopic
    {
        public readonly Dictionary<string, string> Items = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        );
        public readonly bool Published;

        public DdeTopic(bool published)
        {
            Published = published;
        }
    }

    internal sealed class DdeConversation
    {
        public readonly string Application;
        public readonly string Document;
        public readonly DdeTopic Topic;
        public readonly List<string> Pending = new List<string>();
        public readonly Dictionary<string, StringReader> Readers = new Dictionary<string, StringReader>(
            StringComparer.OrdinalIgnoreCase
        );
        public long Mode = 1;
        public int Error;

        public DdeConversation(string application, string document, DdeTopic topic)
        {
            Application = application;
            Document = document;
            Topic = topic;
        }
    }

    internal static class Dde
    {
        private const int NoServer = 289,
            NotProcessed = 0x4009,
            InvalidParameter = 0x4006;

        private static DdeTopic? FindTopic(SimHost h, string app, string doc)
        {
            if (app.Length == 0 || doc.Length == 0)
                return null;
            bool published = string.Equals(app, "Citect", StringComparison.OrdinalIgnoreCase);
            if (published)
            {
                if (
                    !string.Equals(doc, "Data", StringComparison.OrdinalIgnoreCase)
                    || Params.Get(h, "DDE", "Enable") != "1"
                )
                    return null;
            }
            else if (Params.Get(h, "SimulatedDDE", app) != "1")
                return null;
            if (!h.DdeServers.TryGetValue(app, out var topics))
                h.DdeServers[app] = topics = new Dictionary<string, DdeTopic>(
                    StringComparer.OrdinalIgnoreCase
                );
            if (!topics.TryGetValue(doc, out var topic))
                topics[doc] = topic = new DdeTopic(published);
            return topic;
        }

        [Builtin(
            "DDEhInitiate",
            Doc = "1073075",
            Notes = "Opens Citect|Data or an in-memory application enabled by [SimulatedDDE]. Returns -1 for an unavailable service."
        )]
        private static Value Open(SimHost h, CicodeTask t, ArgList a)
        {
            string app = A.S(a.Get(0)),
                doc = A.S(a.Get(1));
            var topic = FindTopic(h, app, doc);
            return W.I(topic == null ? -1 : h.DdeConversations.Add(new DdeConversation(app, doc, topic)));
        }

        [Builtin("DDEhTerminate", Doc = "1073080", Errors = "269 invalid handle (returned)")]
        private static Value Close(SimHost h, CicodeTask t, ArgList a)
        {
            long id = A.I(a.Get(0));
            if (h.DdeConversations.Get(id) == null)
                return W.I(Codes.BadHandle);
            h.DdeConversations.Free(id);
            return W.I(0);
        }

        [Builtin("DDEhGetLastError", Doc = "1073074")]
        private static Value LastError(SimHost h, CicodeTask t, ArgList a) =>
            W.I(h.DdeConversations.Get(A.I(a.Get(0)))?.Error ?? 0);

        [Builtin(
            "DDEPost",
            Doc = "1073082",
            Effects = "updates an item on the simulation's Citect|Data topic",
            Notes = "Requires [DDE]Enable=1."
        )]
        private static Value Post(SimHost h, CicodeTask t, ArgList a)
        {
            var topic = FindTopic(h, "Citect", "Data");
            string item = A.S(a.Get(0)),
                value = A.S(a.Get(1));
            if (topic == null || item.Length == 0)
                return W.Empty;
            topic.Items[item] = value;
            return W.S(value);
        }

        [Builtin("DDERead", Doc = "1073083", Errors = "289 unavailable service (raised)")]
        private static Value Read(SimHost h, CicodeTask t, ArgList a) => Transfer(h, t, a, false);

        [Builtin("DDEWrite", Doc = "1073084", Errors = "289 unavailable service (raised)")]
        private static Value Write(SimHost h, CicodeTask t, ArgList a) => Transfer(h, t, a, true);

        private static Value Transfer(SimHost h, CicodeTask t, ArgList a, bool write)
        {
            var topic = FindTopic(h, A.S(a.Get(0)), A.S(a.Get(1)));
            string item = A.S(a.Get(2));
            if (topic == null)
            {
                t.RaiseError(NoServer, false);
                return W.Empty;
            }
            if (item.Length == 0)
                return W.Empty;
            if (!write)
                return topic.Items.TryGetValue(item, out var value) ? W.S(value) : W.Empty;
            if (topic.Published)
                return W.Empty;
            return W.S(topic.Items[item] = A.S(a.Get(3)));
        }

        [Builtin("DDEhRequest", Doc = "1073078")]
        private static Value Request(SimHost h, CicodeTask t, ArgList a)
        {
            var c = h.DdeConversations.Get(A.I(a.Get(0)));
            if (c == null)
                return W.Empty;
            bool found = c.Topic.Items.TryGetValue(A.S(a.Get(1)), out var value);
            c.Error = found ? 0 : NotProcessed;
            return found ? W.S(value!) : W.Empty;
        }

        [Builtin("DDEhPoke", Doc = "1073076")]
        private static Value Poke(SimHost h, CicodeTask t, ArgList a)
        {
            var c = h.DdeConversations.Get(A.I(a.Get(0)));
            return c == null ? W.I(Codes.BadHandle) : Store(h, c, A.S(a.Get(1)), A.S(a.Get(2)));
        }

        private static Value Store(SimHost h, DdeConversation c, string item, string value)
        {
            if (c.Topic.Published || item.Length == 0)
                return W.I(c.Error = NotProcessed);
            c.Topic.Items[item] = value;
            h.World?.Trace?.Event(
                "dde",
                null,
                null,
                ("application", c.Application),
                ("topic", c.Document),
                ("item", item),
                ("format", c.Mode == 2 ? "csv" : "text"),
                ("value", value)
            );
            c.Readers.Remove(item);
            return W.I(c.Error = 0);
        }

        [Builtin("DDEhSetMode", Doc = "1073079")]
        private static Value SetMode(SimHost h, CicodeTask t, ArgList a)
        {
            var c = h.DdeConversations.Get(A.I(a.Get(0)));
            if (c == null)
                return W.I(Codes.BadHandle);
            long mode = A.I(a.Get(1));
            if (mode != 1 && mode != 2)
                return W.I(c.Error = InvalidParameter);
            c.Mode = mode;
            return W.I(c.Error = 0);
        }

        [Builtin(
            "DDEhWriteLn",
            Doc = "1073081",
            Notes = "Buffers lines until an empty line commits them to the specified topic item."
        )]
        private static Value WriteLine(SimHost h, CicodeTask t, ArgList a)
        {
            var c = h.DdeConversations.Get(A.I(a.Get(0)));
            if (c == null)
                return W.I(Codes.BadHandle);
            string line = A.S(a.Get(2));
            if (line.Length > 0)
            {
                c.Pending.Add(line);
                return W.I(c.Error = 0);
            }
            Value result = Store(h, c, A.S(a.Get(1)), string.Join("\r\n", c.Pending));
            c.Pending.Clear();
            return result;
        }

        [Builtin("DDEhReadLn", Doc = "1073077")]
        private static Value ReadLine(SimHost h, CicodeTask t, ArgList a)
        {
            var c = h.DdeConversations.Get(A.I(a.Get(0)));
            if (c == null)
                return W.Empty;
            string item = A.S(a.Get(1));
            if (!c.Readers.TryGetValue(item, out var reader))
            {
                if (!c.Topic.Items.TryGetValue(item, out var value))
                {
                    c.Error = NotProcessed;
                    return W.Empty;
                }
                c.Readers[item] = reader = new StringReader(value);
            }
            c.Error = 0;
            string? line = reader.ReadLine();
            if (line == null)
                c.Readers.Remove(item);
            return W.S(line ?? "");
        }

        [Builtin(
            "DDEExec",
            Doc = "1073072",
            Notes = "Records a command addressed to an enabled simulated DDE application."
        )]
        private static Value Exec(SimHost h, CicodeTask t, ArgList a)
        {
            string app = A.S(a.Get(0));
            if (FindTopic(h, app, "System") == null)
            {
                t.RaiseError(NoServer, false);
                return W.I(NoServer);
            }
            h.Note(t, "DDE " + app + "|System: " + A.S(a.Get(1)));
            return W.I(1);
        }

        [Builtin(
            "DDEhExecute",
            Doc = "1073073",
            Notes = "Records the command on the simulated conversation."
        )]
        private static Value Execute(SimHost h, CicodeTask t, ArgList a)
        {
            var c = h.DdeConversations.Get(A.I(a.Get(0)));
            if (c == null)
                return W.I(Codes.BadHandle);
            if (c.Topic.Published)
                return W.I(c.Error = NotProcessed);
            h.Note(t, "DDE " + c.Application + "|" + c.Document + ": " + A.S(a.Get(1)));
            return W.I(c.Error = 0);
        }
    }
}
