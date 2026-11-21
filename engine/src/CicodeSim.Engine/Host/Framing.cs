using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>A broken DAP frame (bad header, missing or invalid Content-Length, a body over
    /// the limit). The connection cannot be resynchronised after it and is closed.</summary>
    public sealed class FramingException : IOException
    {
        public FramingException(string message)
            : base(message) { }
    }

    /// <summary>DAP base protocol framing: Content-Length headers, then UTF-8 JSON.</summary>
    public static class DapFraming
    {
        /// <summary>The largest body a peer may send (a tag list page or a full page state is a
        /// few MB at most).</summary>
        public const int DefaultMaxBody = 64 << 20;

        /// <summary>The longest header line accepted.</summary>
        public const int MaxHeaderLine = 8192;

        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>One frame: the header then the body.</summary>
        public static byte[] Encode(string json)
        {
            byte[] body = Utf8.GetBytes(json);
            byte[] head = Encoding.ASCII.GetBytes(
                "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n"
            );
            var r = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, r, 0, head.Length);
            Buffer.BlockCopy(body, 0, r, head.Length, body.Length);
            return r;
        }

        public static byte[] Encode(JsonValue message) => Encode(message.Dump());
    }

    /// <summary>A push parser of DAP frames: feed it bytes as they arrive (any split, several
    /// frames in one chunk, a frame over many chunks) and it returns each complete body.</summary>
    public sealed class DapParser
    {
        private readonly int _maxBody;
        private readonly MemoryStream _header = new MemoryStream();
        private byte[]? _body;
        private int _bodyFill;
        private int _contentLength = -1;
        private bool _sawCr;

        public DapParser(int maxBody = DapFraming.DefaultMaxBody)
        {
            _maxBody = maxBody;
        }

        /// <summary>True between two frames (nothing of the next one read yet).</summary>
        public bool AtFrameStart => _body == null && _header.Length == 0 && _contentLength < 0;

        /// <summary>Feed <paramref name="count"/> bytes; every completed body is added to
        /// <paramref name="bodies"/>. Throws FramingException on a broken header.</summary>
        public void Feed(byte[] data, int offset, int count, List<string> bodies)
        {
            int i = offset,
                end = offset + count;
            while (i < end)
            {
                if (_body != null)
                {
                    int n = Math.Min(end - i, _body.Length - _bodyFill);
                    Buffer.BlockCopy(data, i, _body, _bodyFill, n);
                    _bodyFill += n;
                    i += n;
                    if (_bodyFill == _body.Length)
                    {
                        bodies.Add(DapFraming.Utf8.GetString(_body));
                        _body = null;
                        _bodyFill = 0;
                        _contentLength = -1;
                    }
                    continue;
                }
                byte b = data[i++];
                if (b == (byte)'\n')
                {
                    // a header line ends (CR LF; a bare LF is accepted too)
                    _sawCr = false;
                    string line = Encoding.ASCII.GetString(_header.GetBuffer(), 0, (int)_header.Length);
                    _header.SetLength(0);
                    if (line.Length == 0)
                    {
                        if (_contentLength < 0)
                            throw new FramingException("DAP header without Content-Length");
                        if (_contentLength == 0)
                        {
                            bodies.Add("");
                            _contentLength = -1;
                        }
                        else
                        {
                            _body = new byte[_contentLength];
                            _bodyFill = 0;
                        }
                        continue;
                    }
                    int colon = line.IndexOf(':');
                    if (colon <= 0)
                        throw new FramingException("bad DAP header line: " + Clip(line));
                    string name = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        if (
                            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int len)
                            || len < 0
                        )
                            throw new FramingException("bad Content-Length: " + Clip(value));
                        if (len > _maxBody)
                            throw new FramingException(
                                "DAP body of "
                                    + len.ToString(CultureInfo.InvariantCulture)
                                    + " bytes is over the limit of "
                                    + _maxBody.ToString(CultureInfo.InvariantCulture)
                            );
                        _contentLength = len;
                    }
                    continue;
                }
                if (_sawCr)
                {
                    // a CR that does not end the line belongs to it
                    _header.WriteByte((byte)'\r');
                    _sawCr = false;
                }
                if (b == (byte)'\r')
                {
                    _sawCr = true;
                    continue;
                }
                if (_header.Length >= DapFraming.MaxHeaderLine)
                    throw new FramingException("DAP header line too long");
                _header.WriteByte(b);
            }
        }

        private static string Clip(string s) => s.Length > 60 ? s.Substring(0, 60) + "..." : s;
    }

    /// <summary>Reads DAP frames from a stream (blocking).</summary>
    public sealed class DapReader
    {
        private readonly Stream _s;
        private readonly DapParser _parser;
        private readonly byte[] _buf = new byte[65536];
        private readonly Queue<string> _ready = new Queue<string>();
        private readonly List<string> _tmp = new List<string>();

        public DapReader(Stream s, int maxBody = DapFraming.DefaultMaxBody)
        {
            _s = s;
            _parser = new DapParser(maxBody);
        }

        /// <summary>The next body; null at the end of the stream (between frames). A stream that
        /// ends inside a frame is a FramingException.</summary>
        public string? Read()
        {
            while (_ready.Count == 0)
            {
                int n = _s.Read(_buf, 0, _buf.Length);
                if (n <= 0)
                {
                    if (_parser.AtFrameStart)
                        return null;
                    throw new FramingException("the stream ended inside a DAP frame");
                }
                _tmp.Clear();
                _parser.Feed(_buf, 0, n, _tmp);
                foreach (var b in _tmp)
                    _ready.Enqueue(b);
            }
            return _ready.Dequeue();
        }
    }
}
