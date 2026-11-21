using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>DAP base protocol framing: Content-Length header lines, an empty line, then
    /// that many bytes of UTF-8 JSON, in any split.</summary>
    public class FramingTests
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private static List<string> FeedInChunks(
            byte[] data,
            int chunk,
            int maxBody = DapFraming.DefaultMaxBody
        )
        {
            var p = new DapParser(maxBody);
            var bodies = new List<string>();
            for (int i = 0; i < data.Length; i += chunk)
                p.Feed(data, i, Math.Min(chunk, data.Length - i), bodies);
            Assert.True(p.AtFrameStart);
            return bodies;
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

        [Fact]
        public void EveryChunkSizeGivesTheSameBodies()
        {
            var msgs = new[]
            {
                "{\"a\":1}",
                "{\"text\":\"blåbær € 😀\"}",
                "{}",
                "[" + string.Join(",", Enumerable.Range(0, 300)) + "]",
            };
            byte[] all = Concat(msgs.Select(DapFraming.Encode).ToArray());
            for (int chunk = 1; chunk <= 64; chunk++)
                Assert.Equal(msgs, FeedInChunks(all, chunk));
            Assert.Equal(msgs, FeedInChunks(all, all.Length));
        }

        [Fact]
        public void ContentLengthCountsUtf8BytesNotCharacters()
        {
            string json = "{\"s\":\"æøå€😀\"}";
            byte[] f = DapFraming.Encode(json);
            string head = Encoding.ASCII.GetString(f, 0, Array.IndexOf(f, (byte)'\r'));
            Assert.Equal(
                "Content-Length: " + Utf8.GetByteCount(json).ToString(CultureInfo.InvariantCulture),
                head
            );
            Assert.NotEqual(json.Length, Utf8.GetByteCount(json));
            Assert.Equal(new[] { json }, FeedInChunks(f, 3));
        }

        [Fact]
        public void HeadersAreCaseInsensitiveOtherHeadersIgnoredBareLfAccepted()
        {
            byte[] a = Encoding.ASCII.GetBytes(
                "content-length: 2\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{}"
            );
            byte[] b = Encoding.ASCII.GetBytes("Content-Length:3\n\n[1]");
            byte[] c = Encoding.ASCII.GetBytes("CONTENT-LENGTH:   1  \r\n\r\n7");
            Assert.Equal(new[] { "{}", "[1]", "7" }, FeedInChunks(Concat(a, b, c), 5));
        }

        [Fact]
        public void ABigBodyArrivesWhole()
        {
            var sb = new StringBuilder("{\"rows\":[");
            for (int i = 0; i < 400000; i++)
                sb.Append(i > 0 ? "," : "")
                    .Append("\"tag")
                    .Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append('"');
            string json = sb.Append("]}").ToString();
            byte[] f = DapFraming.Encode(json);
            Assert.True(f.Length > 4_000_000);
            var got = FeedInChunks(Concat(f, DapFraming.Encode("{}")), 65536);
            Assert.Equal(2, got.Count);
            Assert.Equal(json, got[0]);
            Assert.Equal(400000, JsonValue.Parse(got[0])["rows"].Count);
            // and through the blocking reader over a stream
            var r = new DapReader(new MemoryStream(Concat(f, f)));
            Assert.Equal(json, r.Read());
            Assert.Equal(json, r.Read());
            Assert.Null(r.Read());
        }

        [Fact]
        public void ZeroLengthBodyIsAnEmptyString()
        {
            Assert.Equal(
                new[] { "", "{}" },
                FeedInChunks(
                    Concat(Encoding.ASCII.GetBytes("Content-Length: 0\r\n\r\n"), DapFraming.Encode("{}")),
                    1
                )
            );
        }

        [Theory]
        [InlineData("\r\n\r\n{}")]
        [InlineData("Content-Type: x\r\n\r\n{}")]
        [InlineData("Content-Length: -1\r\n\r\n")]
        [InlineData("Content-Length: 1e3\r\n\r\n")]
        [InlineData("Content-Length: 99999999999\r\n\r\n")]
        [InlineData("Content-Length 2\r\n\r\n{}")]
        [InlineData(":2\r\n\r\n{}")]
        public void BrokenHeadersAreFramingErrors(string text)
        {
            Assert.Throws<FramingException>(() => FeedInChunks(Encoding.ASCII.GetBytes(text), 4));
        }

        [Fact]
        public void LimitsAreEnforcedBeforeAllocating()
        {
            var e = Assert.Throws<FramingException>(() =>
                FeedInChunks(Encoding.ASCII.GetBytes("Content-Length: 1001\r\n\r\n"), 7, maxBody: 1000)
            );
            Assert.Contains("over the limit", e.Message);
            Assert.Equal(
                new[] { new string('x', 1000) },
                FeedInChunks(DapFraming.Encode(new string('x', 1000)), 7, maxBody: 1000)
            );
            byte[] longLine = Encoding.ASCII.GetBytes(
                "X-Junk: " + new string('a', DapFraming.MaxHeaderLine) + "\r\n"
            );
            Assert.Throws<FramingException>(() => FeedInChunks(longLine, 100));
        }

        [Fact]
        public void AStreamEndingInsideAFrameIsAnErrorBetweenFramesItIsTheEnd()
        {
            byte[] f = DapFraming.Encode("{\"a\":1}");
            Assert.Null(new DapReader(new MemoryStream(new byte[0])).Read());
            for (int cut = 1; cut < f.Length; cut++)
            {
                var r = new DapReader(new MemoryStream(f, 0, cut));
                Assert.Throws<FramingException>(() => r.Read());
            }
        }
    }
}
