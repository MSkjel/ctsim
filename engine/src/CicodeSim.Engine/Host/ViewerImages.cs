using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Builtins;
using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>The window pictures WinFile, WinPrint and WinCopy ask the viewers for
    /// (ctsim/imageWanted, answered with ctsim/windowImage).</summary>
    public sealed class ViewerImages : IWindowImageHost
    {
        private readonly Func<bool> _hasViewer;
        private readonly Action<string, JsonValue> _send;
        private readonly object _lock = new object();
        private readonly Dictionary<int, WindowImageRequest> _open =
            new Dictionary<int, WindowImageRequest>();
        private int _next;

        /// <param name="hasViewer">a viewer that draws windows is connected</param>
        /// <param name="send">sends an event (name, body) to those viewers</param>
        public ViewerImages(Func<bool> hasViewer, Action<string, JsonValue> send)
        {
            _hasViewer = hasViewer;
            _send = send;
        }

        public bool Ask(WindowImageRequest request)
        {
            if (!_hasViewer())
                return false;
            lock (_lock)
            {
                request.Id = ++_next;
                _open[request.Id] = request;
            }
            _send("ctsim/imageWanted", request.ToJson());
            return true;
        }

        public void Done(WindowImageRequest request)
        {
            lock (_lock)
                _open.Remove(request.Id);
        }

        /// <summary>The requests waiting for a picture (their ids).</summary>
        public List<int> Open()
        {
            lock (_lock)
                return new List<int>(_open.Keys);
        }

        /// <summary>The last viewer that draws windows went: every waiting request ends
        /// without a picture.</summary>
        public void CancelAll()
        {
            List<WindowImageRequest> open;
            lock (_lock)
            {
                open = new List<WindowImageRequest>(_open.Values);
                _open.Clear();
            }
            foreach (var r in open)
                r.Cancel();
        }

        /// <summary>ctsim/windowImage {id, bmp?, none?}: a viewer's picture (a base64 Windows
        /// bitmap file) or none. Body {id, accepted, width?, height?, bmp?}: the recoloured
        /// picture's size, and for a clipboard request the picture itself (a 24-bit .bmp,
        /// base64). Throws ArgumentException for a missing id, a request that is not waiting,
        /// or a picture that isn't a bitmap.</summary>
        public JsonValue Answer(JsonValue a)
        {
            var idv = a.Get("id");
            if (idv == null || !idv.IsNumber)
                throw new ArgumentException("windowImage needs the request's id");
            int id = (int)idv.Long;
            WindowImageRequest? r;
            lock (_lock)
                _open.TryGetValue(id, out r);
            if (r == null || r.Done)
                throw new ArgumentException(
                    "no window picture " + id.ToString(CultureInfo.InvariantCulture) + " is waited for"
                );
            BmpImage? img = null;
            var nv = a.Get("none");
            bool none = nv != null && nv.Kind == JsonKind.Bool && nv.Bool;
            if (!none)
            {
                var bv = a.Get("bmp");
                if (bv == null || bv.Kind != JsonKind.String)
                    throw new ArgumentException("windowImage needs bmp (base64) or none: true");
                byte[] data;
                try
                {
                    data = Convert.FromBase64String(bv.Str);
                }
                catch (FormatException)
                {
                    throw new ArgumentException("windowImage: bmp is not base64");
                }
                img = BmpImage.Read(data)
                    ?? throw new ArgumentException(
                        "windowImage: bmp is not a 24- or 32-bit uncompressed Windows bitmap"
                    );
            }
            bool accepted = r.Answer(img);
            Done(r);
            var b = JsonValue.NewObject();
            b.Set("id", JsonValue.Of(id));
            b.Set("accepted", JsonValue.Of(accepted));
            if (accepted && img != null)
            {
                b.Set("width", JsonValue.Of(img.Width));
                b.Set("height", JsonValue.Of(img.Height));
                if (r.Clipboard)
                    b.Set("bmp", Convert.ToBase64String(img.ToBmp()));
            }
            return b;
        }
    }
}
