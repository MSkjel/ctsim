using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.XPath;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>The XML functions.</summary>
    public static class XmlDocs
    {
        private const int InvalidArg = 274,
            FileNotFound = 261,
            CannotWrite = 264,
            XmlError = 543,
            XPathError = 544,
            CannotAppend = 545,
            NoHandle = 546;

        /// <summary>The result buffer of the string functions (0x100 bytes).</summary>
        private const int Buffer = 0x100;

        /// <summary>The runtime's XML handles: from 0 in turn, wrapping at the maximum (the next
        /// one is the one after the last given that is free; back at the last given = none,
        /// -1).</summary>
        internal sealed class Handles<T>
            where T : class
        {
            private readonly Dictionary<int, T> _items = new Dictionary<int, T>();
            private readonly int _max;
            private int _last = -1;

            public Handles(int max)
            {
                _max = max;
            }

            public int Count => _items.Count;

            public int Add(T value)
            {
                int h = Next();
                if (h != -1)
                    _items.Add(h, value);
                return h;
            }

            public T? Find(int h) => _items.TryGetValue(h, out var v) ? v : null;

            public T? Remove(int h)
            {
                if (!_items.TryGetValue(h, out var v))
                    return null;
                _items.Remove(h);
                return v;
            }

            public void RemoveAll()
            {
                _items.Clear();
                _last = -1;
            }

            public IEnumerable<T> Values => _items.Values;

            private int Next()
            {
                int c = (_last + 1) % _max;
                if (c == _last)
                    return -1;
                do
                {
                    if (!_items.ContainsKey(c))
                    {
                        _last = c;
                        return c;
                    }
                    c = (c + 1) % _max;
                } while (c != _last);
                return -1;
            }
        }

        /// <summary>A document and its node handles
        /// (a node keeps its handle until it or an ancestor with a handle is removed).</summary>
        internal sealed class Doc
        {
            public XmlDocument? Document;
            private Handles<XmlNode>? _handles = new Handles<XmlNode>(int.MaxValue);
            private Dictionary<XmlNode, int>? _nodes = new Dictionary<XmlNode, int>();

            public Doc(XmlDocument d)
            {
                Document = d;
            }

            public int NodeCount => _handles?.Count ?? 0;

            /// <summary>AddNode: the node's handle, a new one the first time; -1 for null.</summary>
            public int AddNode(XmlNode? node)
            {
                if (node == null)
                    return -1;
                int h = FindNode(node);
                if (h != -1)
                    return h;
                h = _handles!.Add(node);
                if (h != -1)
                    _nodes![node] = h;
                return h;
            }

            public int FindNode(XmlNode node) =>
                _nodes != null && _nodes.TryGetValue(node, out int h) ? h : -1;

            public XmlNode? FindNode(int h) => _handles?.Find(h);

            /// <summary>RemoveNode: forget the handle and, recursively, those of the children that
            /// have one (a grandchild under a child without a handle keeps its own).</summary>
            public XmlNode? RemoveNode(int h)
            {
                var node = _handles?.Remove(h);
                if (node == null)
                    return null;
                _nodes!.Remove(node);
                foreach (XmlNode child in node.ChildNodes)
                {
                    int c = FindNode(child);
                    if (c != -1)
                        RemoveNode(c);
                }
                return node;
            }

            public void Destroy()
            {
                Document = null;
                _handles?.RemoveAll();
                _handles = null;
                _nodes?.Clear();
                _nodes = null;
            }
        }

        /// <summary>The process's XML documents (one manager per simulated process).</summary>
        public sealed class Manager
        {
            internal readonly Handles<Doc> Docs = new Handles<Doc>(32767);

            /// <summary>The documents open now.</summary>
            public int Count => Docs.Count;

            /// <summary>An open document by handle, null when there is none (for hosts and tests).</summary>
            public XmlDocument? Document(int hDoc) => Docs.Find(hDoc)?.Document;
        }

        private static readonly ConditionalWeakTable<SimHost, Manager> All =
            new ConditionalWeakTable<SimHost, Manager>();

        public static Manager Of(SimHost h) => All.GetValue(h, _ => new Manager());

        private static Doc? DocOf(SimHost h, long hDoc) => Of(h).Docs.Find((short)hDoc);

        private static XmlNode? NodeOf(SimHost h, long hDoc, long hNode) =>
            DocOf(h, hDoc)?.FindNode((int)hNode);

        private static Value Fail(CicodeTask t, int code, Value result)
        {
            if (code != 0)
                W.Raise(t, code);
            return result;
        }

#if NET
        private sealed class AnsiProvider : System.Text.EncodingProvider
        {
            public override System.Text.Encoding? GetEncoding(int codepage) =>
                codepage == 1252 ? Cp1252.Encoding : null;

            public override System.Text.Encoding? GetEncoding(string name) =>
                string.Equals(name, "windows-1252", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "cp1252", StringComparison.OrdinalIgnoreCase)
                    ? Cp1252.Encoding
                    : null;
        }

        private static bool _provider;
#endif

        /// <summary>.NET Framework knows the code page windows-1252 that an XML declaration may
        /// name; on .NET 8 the engine's own code page 1252 stands in for it (nothing else).</summary>
        private static void EnsureEncodings()
        {
#if NET
            if (_provider)
                return;
            _provider = true;
            System.Text.Encoding.RegisterProvider(new AnsiProvider());
#endif
        }

        /// <summary>Copy a managed string into the 256-byte result buffer.</summary>
        private static Value Text(SimHost h, CicodeTask t, string s)
        {
            int nul = s.IndexOf('\0');
            if (nul >= 0)
                s = s.Substring(0, nul);
            if (s.Length >= Buffer)
            {
                h.Note(
                    t,
                    "XML value of "
                        + C.Inv(s.Length)
                        + " characters: the runtime's 256-byte buffer does not hold it (an exception there; not simulated, \"\")"
                );
                return W.Empty;
            }
            return W.S(Cp1252.Decode(Cp1252.Encode(s)));
        }

        // ------------------------------------------------------------------ documents
        [Builtin(
            "XMLCreate",
            Doc = "1194056",
            Errors = "274 an empty root name or local part (raised, -1); 543 another XmlException of the .NET call (raised, -1)",
            Notes = "A new document: the declaration <?xml version=\"1.0\"?> and the root element (qualified name, namespace URI; XmlDocument.CreateElement checks the name no further). Handles from 0, in turn (32767): a closed handle is reused only after the others; a full table gives -1 without an error."
        )]
        private static Value XMLCreate(SimHost h, CicodeTask t, ArgList a)
        {
            string root = A.S(a.Get(0)),
                ns = a.Count > 1 ? A.S(a.Get(1)) : "";
            int code;
            int hd;
            try
            {
                var d = new XmlDocument();
                d.AppendChild(d.CreateXmlDeclaration("1.0", null, null));
                d.AppendChild(d.CreateElement(root, ns));
                try
                {
                    hd = (short)Of(h).Docs.Add(new Doc(d));
                    code = 0;
                }
                catch (Exception)
                {
                    hd = -1;
                    code = NoHandle;
                }
            }
            catch (XmlException)
            {
                hd = -1;
                code = XmlError;
            }
            catch (ArgumentException)
            {
                hd = -1;
                code = InvalidArg;
            }
            catch (Exception)
            {
                hd = -1;
                code = InvalidArg;
            }
            return Fail(t, code, W.I(hd));
        }

        [Builtin(
            "XMLOpen",
            Doc = "1194071",
            Errors = "261 no such file (raised); 543 not well-formed XML (raised); 274 a missing folder, a folder or another read error (raised); 546 no document handle left (raised)",
            Notes = "Reads the file with DTDs ignored and no external resolver. The path is used as given (no [RUN] path operators: the runtime passes it on unchanged), relative to the simulated current folder; a sandbox file wins over a real one."
        )]
        private static Value XMLOpen(SimHost h, CicodeTask t, ArgList a)
        {
            string path = A.S(a.Get(0));
            int code = 0,
                hd = -1;
            try
            {
                string full = h.Sandbox.Full(path);
                byte[]? bytes = path.Length == 0 ? null : h.Sandbox.ReadAll(full);
                if (bytes == null)
                {
                    CicodeSim.Project.WinPath.Split(full, out string folder, out _);
                    code =
                        path.Length > 0 && !h.Sandbox.FolderVisible(full) && h.Sandbox.FolderExists(folder)
                            ? FileNotFound
                            : InvalidArg;
                }
                else
                {
                    EnsureEncodings();
                    var settings = new XmlReaderSettings
                    {
                        XmlResolver = null,
                        DtdProcessing = DtdProcessing.Ignore,
                    };
                    var d = new XmlDocument();
                    using (var ms = new MemoryStream(bytes))
                    using (var r = XmlReader.Create(ms, settings))
                        d.Load(r);
                    try
                    {
                        hd = (short)Of(h).Docs.Add(new Doc(d));
                    }
                    catch (Exception)
                    {
                        hd = -1;
                        code = NoHandle;
                    }
                }
            }
            catch (XmlException)
            {
                code = XmlError;
                hd = -1;
            }
            catch (Exception)
            {
                code = InvalidArg;
                hd = -1;
            }
            return Fail(t, code, W.I(hd));
        }

        [Builtin(
            "XMLClose",
            Doc = "1194055",
            Errors = "274 no such document (raised and returned)",
            Notes = "Frees the document and every node handle of it."
        )]
        private static Value XMLClose(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Of(h).Docs.Remove((short)A.I(a.Get(0)));
            if (d == null)
                return Fail(t, InvalidArg, W.I(InvalidArg));
            d.Destroy();
            return W.I(0);
        }

        [Builtin(
            "XMLSave",
            Doc = "1194072",
            Effects = "writes the document to a sandbox file (XmlDocument.Save: indented, the declaration's encoding, else UTF-8 without a byte order mark)",
            Errors = "543 a document without a root element (raised and returned; XmlDocument.Save checks it before it opens the file); 264 the file cannot be written (a missing folder, a folder, a read-only file, a full sandbox) (raised and returned); 274 an empty path or another error (raised and returned)",
            Notes = "An unknown document handle gives 0 and writes nothing (the runtime's SaveDocument finds no container and keeps its 0). Real project files are never written: a save to one lands in the sandbox."
        )]
        private static Value XMLSave(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DocOf(h, A.I(a.Get(0)));
            string path = A.S(a.Get(1));
            if (d?.Document == null)
                return W.I(0);
            int code = 0;
            try
            {
                // XmlDocument.Save(string) refuses a document without a root element before it
                // opens the file (Save(Stream) does not check: done here)
                if (d.Document.DocumentElement == null)
                    throw new XmlException("no root element");
                if (path.Length == 0)
                    throw new ArgumentException("path");
                EnsureEncodings();
                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    d.Document.Save(ms);
                    bytes = ms.ToArray();
                }
                string full = h.Sandbox.Full(path);
                ulong now = t.Vm.Clock.NowFiletime();
                var f = h.Sandbox.Open(full, false, true, true, true, now);
                if (
                    f == null
                    || bytes.Length > 0 && h.Sandbox.Write(f, bytes, bytes.Length, now) != bytes.Length
                )
                    code = CannotWrite;
            }
            catch (XmlException)
            {
                code = XmlError;
            }
            catch (IOException)
            {
                code = CannotWrite;
            }
            catch (UnauthorizedAccessException)
            {
                code = CannotWrite;
            }
            catch (Exception)
            {
                code = InvalidArg;
            }
            return Fail(t, code, W.I(code));
        }

        // ------------------------------------------------------------------ navigation
        [Builtin(
            "XMLGetRoot",
            Doc = "1194064",
            Errors = "274 the root cannot be read (raised, -1)",
            Notes = "The handle of the document element (the same handle every time). An unknown document gives -1 without an error, as the runtime (its GetDocumentRootNode keeps code 0); so does a document whose root was removed."
        )]
        private static Value XMLGetRoot(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DocOf(h, A.I(a.Get(0)));
            if (d?.Document == null)
                return W.I(-1);
            return W.I(d.AddNode(d.Document.DocumentElement));
        }

        [Builtin(
            "XMLNodeFind",
            Doc = "1194066",
            Errors = "544 not a valid XPath expression (raised, -1); 274 another error (raised, -1)",
            Notes = "The first node the XPath 1.0 expression selects from the document (XmlNode.SelectSingleNode, no namespace manager); none, or an unknown document, -1 without an error."
        )]
        private static Value XMLNodeFind(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DocOf(h, A.I(a.Get(0)));
            string q = A.S(a.Get(1));
            if (d?.Document == null)
                return W.I(-1);
            try
            {
                var n = d.Document.SelectSingleNode(q);
                return W.I(n == null ? -1 : d.AddNode(n));
            }
            catch (XPathException)
            {
                return Fail(t, XPathError, W.I(-1));
            }
            catch (Exception)
            {
                return Fail(t, InvalidArg, W.I(-1));
            }
        }

        [Builtin(
            "XMLGetParent",
            Doc = "1194063",
            Errors = "274 no such document or node, or a node without a parent (raised, -1)",
            Notes = "The parent's handle; the root element's parent is the document node itself (name '#document'), which has none."
        )]
        private static Value XMLGetParent(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DocOf(h, A.I(a.Get(0)));
            var n = d?.FindNode((int)A.I(a.Get(1)));
            if (n?.ParentNode == null)
                return Fail(t, InvalidArg, W.I(-1));
            return W.I(d!.AddNode(n.ParentNode));
        }

        [Builtin(
            "XMLGetChildCount",
            Doc = "1194062",
            Errors = "274 no such document or node (raised, -1)",
            Notes = "Every child node counts (elements, text, comments, CDATA, processing instructions)."
        )]
        private static Value XMLGetChildCount(SimHost h, CicodeTask t, ArgList a)
        {
            var n = NodeOf(h, A.I(a.Get(0)), A.I(a.Get(1)));
            if (n == null)
                return Fail(t, InvalidArg, W.I(-1));
            return W.I(n.ChildNodes.Count);
        }

        [Builtin(
            "XMLGetChild",
            Doc = "1194061",
            Errors = "274 no such document or node, or an index outside 0 .. count - 1 (raised, -1)",
            Notes = "The handle of child nIndex (from 0, every kind of node)."
        )]
        private static Value XMLGetChild(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DocOf(h, A.I(a.Get(0)));
            var n = d?.FindNode((int)A.I(a.Get(1)));
            long i = (int)A.I(a.Get(2));
            if (n == null || i < 0 || i >= n.ChildNodes.Count)
                return Fail(t, InvalidArg, W.I(-1));
            return W.I(d!.AddNode(n.ChildNodes[(int)i]));
        }

        // ------------------------------------------------------------------ names and values
        [Builtin(
            "XMLNodeGetName",
            Doc = "1194067",
            Errors = "274 no such document or node (raised, '')",
            Notes = "XmlNode.Name: the qualified name of an element, '#text', '#comment', '#cdata-section', '#document'."
        )]
        private static Value XMLNodeGetName(SimHost h, CicodeTask t, ArgList a)
        {
            var n = NodeOf(h, A.I(a.Get(0)), A.I(a.Get(1)));
            string s = n?.Name ?? "";
            if (s.Length == 0)
                return Fail(t, InvalidArg, W.Empty);
            return Text(h, t, s);
        }

        /// <summary>A node that holds a value: an element without children or with one text child.</summary>
        private static bool HoldsValue(XmlNode? n) =>
            n != null
            && n.NodeType == XmlNodeType.Element
            && (!n.HasChildNodes || n.ChildNodes.Count == 1 && n.ChildNodes[0]!.NodeType == XmlNodeType.Text);

        [Builtin(
            "XMLNodeGetValue",
            Doc = "1194068",
            Errors = "274 no such node, or not an element with at most one text child (raised, '')",
            Notes = "The element's text (InnerText) when it has no children or one text node; an empty value is '' without an error."
        )]
        private static Value XMLNodeGetValue(SimHost h, CicodeTask t, ArgList a)
        {
            var n = NodeOf(h, A.I(a.Get(0)), A.I(a.Get(1)));
            if (!HoldsValue(n))
                return Fail(t, InvalidArg, W.Empty);
            return Text(h, t, n!.InnerText);
        }

        [Builtin(
            "XMLNodeSetValue",
            Doc = "1194070",
            Errors = "274 no such node, or not an element with at most one text child (raised and returned)",
            Notes = "Sets InnerText: the text child's value, or a new text child (also for '')."
        )]
        private static Value XMLNodeSetValue(SimHost h, CicodeTask t, ArgList a)
        {
            var n = NodeOf(h, A.I(a.Get(0)), A.I(a.Get(1)));
            if (!HoldsValue(n))
                return Fail(t, InvalidArg, W.I(InvalidArg));
            try
            {
                n!.InnerText = A.S(a.Get(2));
            }
            catch (Exception)
            {
                return Fail(t, InvalidArg, W.I(InvalidArg));
            }
            return W.I(0);
        }

        // ------------------------------------------------------------------ attributes
        [Builtin(
            "XMLGetAttributeCount",
            Doc = "1194058",
            Errors = "274 no such node or a node without attributes (not an element) (raised, -1)"
        )]
        private static Value XMLGetAttributeCount(SimHost h, CicodeTask t, ArgList a)
        {
            var n = NodeOf(h, A.I(a.Get(0)), A.I(a.Get(1)));
            if (n?.Attributes == null)
                return Fail(t, InvalidArg, W.I(-1));
            return W.I((short)n.Attributes.Count);
        }

        private static XmlAttribute? AttributeAt(XmlNode? n, long i) =>
            n?.Attributes != null && i >= 0 && i < n.Attributes.Count ? n.Attributes[(int)i] : null;

        [Builtin(
            "XMLGetAttributeName",
            Doc = "1194059",
            Errors = "274 no such node, no attributes or an index outside 0 .. count - 1 (raised, '')",
            Notes = "The qualified name of attribute nIndex (from 0, document order; xmlns declarations count)."
        )]
        private static Value XMLGetAttributeName(SimHost h, CicodeTask t, ArgList a)
        {
            var at = AttributeAt(NodeOf(h, A.I(a.Get(0)), A.I(a.Get(1))), A.Short(A.I(a.Get(2))));
            if (at == null || at.Name.Length == 0)
                return Fail(t, InvalidArg, W.Empty);
            return Text(h, t, at.Name);
        }

        [Builtin(
            "XMLGetAttributeValue",
            Doc = "1194060",
            Errors = "274 no such node, no attributes or an index outside 0 .. count - 1 (raised, '')",
            Notes = "The value of attribute nIndex (from 0); an empty value is '' without an error."
        )]
        private static Value XMLGetAttributeValue(SimHost h, CicodeTask t, ArgList a)
        {
            var at = AttributeAt(NodeOf(h, A.I(a.Get(0)), A.I(a.Get(1))), A.Short(A.I(a.Get(2))));
            if (at == null)
                return Fail(t, InvalidArg, W.Empty);
            return Text(h, t, at.Value);
        }

        /// <summary>An attribute by name: the first attribute whose name equals
        /// <paramref name="name"/> without case (String.Equals with InvariantCultureIgnoreCase).</summary>
        private static XmlAttribute? Named(XmlNode? n, string name)
        {
            if (n?.Attributes == null)
                return null;
            foreach (XmlAttribute x in n.Attributes)
                if (string.Equals(x.Name, name, StringComparison.InvariantCultureIgnoreCase))
                    return x;
            return null;
        }

        [Builtin(
            "XMLGetAttribute",
            Doc = "1194057",
            Errors = "274 no such node or no attribute of that name (raised, '')",
            Notes = "The value of the attribute named sName, the name compared without case; an empty value is '' without an error."
        )]
        private static Value XMLGetAttribute(SimHost h, CicodeTask t, ArgList a)
        {
            var at = Named(NodeOf(h, A.I(a.Get(0)), A.I(a.Get(1))), A.S(a.Get(2)));
            if (at == null)
                return Fail(t, InvalidArg, W.Empty);
            return Text(h, t, at.Value);
        }

        [Builtin(
            "XMLSetAttribute",
            Doc = "1194073",
            Errors = "274 no such node, not an element (raised and returned; for a text or other non-element node the runtime's cast throws out of the managed call: guess), or an empty name (raised and returned); 543 another XmlException of the .NET call (raised and returned)",
            Notes = "Sets the value of the attribute named sName (found without case), adding the attribute (no namespace) when the element has none of that name."
        )]
        private static Value XMLSetAttribute(SimHost h, CicodeTask t, ArgList a)
        {
            var e = NodeOf(h, A.I(a.Get(0)), A.I(a.Get(1))) as XmlElement;
            if (e == null)
                return Fail(t, InvalidArg, W.I(InvalidArg));
            string name = A.S(a.Get(2)),
                value = A.S(a.Get(3));
            try
            {
                var at = Named(e, name) ?? e.SetAttributeNode(name, "");
                if (at != null)
                    at.Value = value;
            }
            catch (ArgumentException)
            {
                return Fail(t, InvalidArg, W.I(InvalidArg));
            }
            catch (XmlException)
            {
                return Fail(t, XmlError, W.I(XmlError));
            }
            catch (Exception)
            {
                return Fail(t, InvalidArg, W.I(InvalidArg));
            }
            return W.I(0);
        }

        // ------------------------------------------------------------------ adding and removing
        [Builtin(
            "XMLNodeAddChild",
            Doc = "1194065",
            Errors = "545 the parent cannot take an element (a text node, a second root on the document node) (raised, -1); 543 not a valid element name (raised, -1); 274 an empty name or another error (raised, -1)",
            Notes = "Appends a new element (qualified name, namespace URI) as the last child and gives its handle. An unknown document or parent gives -1 without an error, as the runtime (AddChildNode keeps code 0)."
        )]
        private static Value XMLNodeAddChild(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DocOf(h, A.I(a.Get(0)));
            var p = d?.FindNode((int)A.I(a.Get(1)));
            if (p == null || d!.Document == null)
                return W.I(-1);
            string name = A.S(a.Get(2)),
                ns = a.Count > 3 ? A.S(a.Get(3)) : "";
            try
            {
                var n = d.Document.CreateNode(XmlNodeType.Element, name, ns);
                var added = n == null ? null : p.AppendChild(n);
                return W.I(added == null ? -1 : d.AddNode(added));
            }
            catch (InvalidOperationException)
            {
                return Fail(t, CannotAppend, W.I(-1));
            }
            catch (ArgumentException)
            {
                return Fail(t, InvalidArg, W.I(-1));
            }
            catch (XmlException)
            {
                return Fail(t, XmlError, W.I(-1));
            }
            catch (Exception)
            {
                return Fail(t, InvalidArg, W.I(-1));
            }
        }

        [Builtin(
            "XMLNodeRemove",
            Doc = "1194069",
            Errors = "274 no such document (raised and returned)",
            Notes = "Removes the node from its parent and frees its handle and those of its children that have one. An unknown node of a known document gives 0 and changes nothing (the runtime's RemoveNode finds no node and keeps 0)."
        )]
        private static Value XMLNodeRemove(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DocOf(h, A.I(a.Get(0)));
            if (d == null)
                return Fail(t, InvalidArg, W.I(InvalidArg));
            try
            {
                var n = d.RemoveNode((int)A.I(a.Get(1)));
                n?.ParentNode?.RemoveChild(n);
            }
            catch (Exception)
            {
                return Fail(t, InvalidArg, W.I(InvalidArg));
            }
            return W.I(0);
        }
    }
}
