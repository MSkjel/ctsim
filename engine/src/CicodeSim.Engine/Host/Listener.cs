using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace CicodeSim.Host
{
    /// <summary>Where a server listens: a named pipe, a Unix socket, or TCP on localhost.</summary>
    public abstract class Listener : IDisposable
    {
        public const string Pipe = "pipe",
            Unix = "unix",
            Tcp = "tcp";

        /// <summary>pipe, unix or tcp.</summary>
        public abstract string Transport { get; }

        /// <summary>What a client connects to: the full pipe path, the socket path or host:port.</summary>
        public abstract string Address { get; }

        /// <summary>Accept clients on a background thread; each gets its duplex stream and a name.</summary>
        public abstract void Start(Action<Stream, string> onClient);

        public abstract void Dispose();

        /// <summary>An accept or a client callback failed (raised on the accept thread; the
        /// listener goes on).</summary>
        public event Action<Exception>? Error;

        protected void Report(Exception e)
        {
            try
            {
                Error?.Invoke(e);
            }
            catch (Exception) { }
        }

        /// <summary>Runs the client callback; a failure is reported and the stream closed.</summary>
        protected void Hand(Action<Stream, string> onClient, Stream s, string name)
        {
            try
            {
                onClient(s, name);
            }
            catch (Exception e)
            {
                Report(e);
                try
                {
                    s.Dispose();
                }
                catch (Exception) { }
            }
        }

        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>The platform's listener for server <paramref name="id"/>.</summary>
        public static Listener Open(string id)
        {
            if (IsWindows)
                return new PipeListener("ctsim." + id);
#if NET
            try
            {
                return new UnixListener(Path.Combine(Path.GetTempPath(), "ctsim." + id + ".sock"));
            }
            catch (Exception e)
                when (e is SocketException || e is PlatformNotSupportedException || e is IOException) { }
#endif
            return new TcpListenerHost();
        }

        /// <summary>Connect to a server's address (tests, clients written in .NET). A timeout is
        /// TimeoutException on net10.0 but can be IOException ("semaphore timeout") on net48
        /// when the pipe exists and every instance is busy.</summary>
        public static Stream Connect(string transport, string address, int timeoutMs = 5000)
        {
            switch (transport)
            {
                case Pipe:
                {
                    string name = address.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
                        ? address.Substring(9)
                        : address;
                    var c = new NamedPipeClientStream(
                        ".",
                        name,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous
                    );
                    try
                    {
                        c.Connect(timeoutMs);
                    }
                    catch (Exception)
                    {
                        c.Dispose();
                        throw;
                    }
                    return c;
                }
#if NET
                case Unix:
                {
                    var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        s.Connect(new UnixDomainSocketEndPoint(address));
                    }
                    catch (Exception)
                    {
                        s.Dispose();
                        throw;
                    }
                    return new NetworkStream(s, ownsSocket: true);
                }
#endif
                case Tcp:
                {
                    int colon = address.LastIndexOf(':');
                    var c = new TcpClient();
                    try
                    {
                        c.Connect(
                            address.Substring(0, colon),
                            int.Parse(
                                address.Substring(colon + 1),
                                NumberStyles.None,
                                CultureInfo.InvariantCulture
                            )
                        );
                    }
                    catch (Exception)
                    {
                        c.Dispose();
                        throw;
                    }
                    c.NoDelay = true;
                    return c.GetStream();
                }
            }
            throw new ArgumentException("unknown transport " + transport);
        }

        /// <summary>The pause after a failed accept, so a persistent failure does not spin.</summary>
        protected const int RetryMs = 100;
    }

    /// <summary>A Windows named pipe server.</summary>
    public sealed class PipeListener : Listener
    {
        private readonly string _name;
        private volatile bool _stop;
        private Thread? _thread;
        private NamedPipeServerStream? _waiting;
        private int _n;

        public PipeListener(string name)
        {
            _name = name;
            // fail now (not on the accept thread) when the name is taken
            _waiting = Create(first: true);
        }

        public override string Transport => Pipe;

        public override string Address => @"\\.\pipe\" + _name;

        public string Name => _name;

        private NamedPipeServerStream Create(bool first)
        {
            try
            {
                return PipeAcl.CreateServer(_name, first);
            }
            catch (UnauthorizedAccessException e) when (first)
            {
                // net10.0 FirstPipeInstance, or another user's pipe of that name
                throw new IOException("the pipe name is in use: " + Address, e);
            }
        }

        public override void Start(Action<Stream, string> onClient)
        {
            _thread = new Thread(() =>
            {
                while (!_stop)
                {
                    NamedPipeServerStream server;
                    try
                    {
                        server = _waiting ?? Create(first: false);
                    }
                    catch (Exception e)
                    {
                        if (_stop)
                            break;
                        Report(e);
                        Thread.Sleep(RetryMs);
                        continue;
                    }
                    _waiting = server;
                    try
                    {
                        server.WaitForConnection();
                    }
                    catch (Exception e)
                    {
                        // IOException: a client that connected and left at once
                        try
                        {
                            server.Dispose();
                        }
                        catch (Exception) { }
                        _waiting = null;
                        if (_stop)
                            break;
                        if (!(e is IOException))
                        {
                            Report(e);
                            Thread.Sleep(RetryMs);
                        }
                        continue;
                    }
                    _waiting = null;
                    if (_stop)
                    {
                        server.Dispose();
                        break;
                    }
                    int n = Interlocked.Increment(ref _n);
                    Hand(onClient, server, "pipe#" + n.ToString(CultureInfo.InvariantCulture));
                }
            })
            {
                IsBackground = true,
                Name = "ctsim-accept",
            };
            _thread.Start();
        }

        /// <summary>The SIDs the waiting pipe instance allows (tests).</summary>
        internal IReadOnlyList<string> AllowedSids() =>
            PipeAcl.AllowedSids(_waiting ?? throw new InvalidOperationException("no waiting instance"));

        public override void Dispose()
        {
            if (_stop)
                return;
            _stop = true;
            // wake the accept thread: connect once (WaitForConnection has no cancellation on net48)
            try
            {
                using var c = new NamedPipeClientStream(".", _name, PipeDirection.InOut);
                c.Connect(200);
            }
            catch (Exception) { }
            try
            {
                _waiting?.Dispose();
            }
            catch (Exception) { }
            _thread?.Join(1000);
        }
    }

    /// <summary>Named pipe creation with an access list of the current user only (Windows).
    /// PipeListener is only made on Windows (Listener.Open), hence the CA1416 pragma.</summary>
    internal static class PipeAcl
    {
#pragma warning disable CA1416
        private static PipeSecurity CurrentUserOnly()
        {
            var sec = new PipeSecurity();
            var me = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("no user SID");
            sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            return sec;
        }

        public static NamedPipeServerStream CreateServer(string name, bool first)
        {
#if NET
            var opts = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
            return NamedPipeServerStreamAcl.Create(
                name,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                opts,
                0,
                0,
                CurrentUserOnly()
            );
#else
            // net48 has no FirstPipeInstance (the constructor refuses the flag): ask whether a
            // pipe of that name exists first (WaitNamedPipe neither connects nor takes an
            // instance; File.Exists on the pipe path would connect and spend one)
            if (first && Exists(@"\\.\pipe\" + name))
                throw new UnauthorizedAccessException("the pipe name is in use");
            return new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                CurrentUserOnly()
            );
#endif
        }

        public static IReadOnlyList<string> AllowedSids(NamedPipeServerStream s)
        {
#if NET
            var sec = PipesAclExtensions.GetAccessControl(s);
#else
            var sec = s.GetAccessControl();
#endif
            var r = new List<string>();
            foreach (AuthorizationRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule is PipeAccessRule p && p.AccessControlType == AccessControlType.Allow)
                    r.Add(p.IdentityReference.Value);
            return r;
        }
#pragma warning restore CA1416

#if !NET
        [DllImport(
            "kernel32.dll",
            EntryPoint = "WaitNamedPipeW",
            CharSet = CharSet.Unicode,
            SetLastError = true
        )]
        private static extern bool WaitNamedPipe(string name, uint timeoutMs);

        private const int ErrorFileNotFound = 2;

        /// <summary>A pipe of that name exists (an instance free or not).</summary>
        private static bool Exists(string path) =>
            WaitNamedPipe(path, 1) || Marshal.GetLastWin32Error() != ErrorFileNotFound;
#endif
    }

#if NET
    /// <summary>A Unix domain socket server (net10.0 off Windows).</summary>
    public sealed class UnixListener : Listener
    {
        private readonly string _path;
        private readonly Socket _sock;
        private volatile bool _stop;
        private int _n;

        public UnixListener(string path)
        {
            _path = path;
            if (File.Exists(path))
            {
                // a live server's socket is refused like a taken pipe name; a stale file is removed
                bool live = false;
                try
                {
                    using var probe = new Socket(
                        AddressFamily.Unix,
                        SocketType.Stream,
                        ProtocolType.Unspecified
                    );
                    probe.Connect(new UnixDomainSocketEndPoint(path));
                    live = true;
                }
                catch (SocketException) { }
                if (live)
                    throw new IOException("the socket is in use: " + path);
                File.Delete(path);
            }
            _sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                _sock.Bind(new UnixDomainSocketEndPoint(path));
                _sock.Listen(16);
            }
            catch (Exception)
            {
                _sock.Dispose();
                throw;
            }
        }

        public override string Transport => Unix;

        public override string Address => _path;

        public override void Start(Action<Stream, string> onClient)
        {
            new Thread(() =>
            {
                while (!_stop)
                {
                    Socket c;
                    try
                    {
                        c = _sock.Accept();
                    }
                    catch (Exception e)
                    {
                        if (_stop)
                            break;
                        Report(e);
                        Thread.Sleep(RetryMs);
                        continue;
                    }
                    int n = Interlocked.Increment(ref _n);
                    Hand(
                        onClient,
                        new NetworkStream(c, ownsSocket: true),
                        "unix#" + n.ToString(CultureInfo.InvariantCulture)
                    );
                }
            })
            {
                IsBackground = true,
                Name = "ctsim-accept",
            }.Start();
        }

        public override void Dispose()
        {
            if (_stop)
                return;
            _stop = true;
            try
            {
                _sock.Dispose();
            }
            catch (Exception) { }
            try
            {
                File.Delete(_path);
            }
            catch (Exception) { }
        }
    }
#endif

    /// <summary>TCP on 127.0.0.1 (the fallback when no pipe or socket file can be made). Any
    /// local user can connect to it: there is no access list on a TCP port.</summary>
    public sealed class TcpListenerHost : Listener
    {
        private readonly TcpListener _l;
        private volatile bool _stop;
        private int _n;

        public TcpListenerHost()
        {
            _l = new TcpListener(IPAddress.Loopback, 0);
            _l.Start();
        }

        public override string Transport => Tcp;

        public override string Address =>
            "127.0.0.1:" + ((IPEndPoint)_l.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);

        public override void Start(Action<Stream, string> onClient)
        {
            new Thread(() =>
            {
                while (!_stop)
                {
                    TcpClient c;
                    try
                    {
                        c = _l.AcceptTcpClient();
                    }
                    catch (Exception e)
                    {
                        if (_stop)
                            break;
                        Report(e);
                        Thread.Sleep(RetryMs);
                        continue;
                    }
                    c.NoDelay = true;
                    int n = Interlocked.Increment(ref _n);
                    Hand(onClient, c.GetStream(), "tcp#" + n.ToString(CultureInfo.InvariantCulture));
                }
            })
            {
                IsBackground = true,
                Name = "ctsim-accept",
            }.Start();
        }

        public override void Dispose()
        {
            if (_stop)
                return;
            _stop = true;
            try
            {
                _l.Stop();
            }
            catch (Exception) { }
        }
    }
}
