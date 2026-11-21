using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CicodeSim.World
{
    /// <summary>
    /// Which of the project's servers are offline in the simulation: taken down by a scenario's
    /// <c>setServer</c> step (until it brings them back), or for the few simulated seconds a
    /// ServerRestart takes. A server not named here is online. Names without case.
    /// </summary>
    public sealed class ServerStates
    {
        private static readonly ConditionalWeakTable<World, ServerStates> All =
            new ConditionalWeakTable<World, ServerStates>();

        /// <summary>How long a ServerRestart keeps a server offline (simulated seconds).</summary>
        public const double RestartSeconds = 5.0;

        // the simulated time each offline server comes back (PositiveInfinity: when told)
        private readonly Dictionary<string, double> _until = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase
        );

        public static ServerStates Of(World w) => All.GetValue(w, _ => new ServerStates());

        /// <summary>Take a server offline (until <see cref="SetOnline"/> brings it back) or bring
        /// it back.</summary>
        public void SetOnline(string name, bool online)
        {
            name = name.Trim();
            if (online)
                _until.Remove(name);
            else
                _until[name] = double.PositiveInfinity;
        }

        /// <summary>A restart: offline from <paramref name="now"/> for
        /// <see cref="RestartSeconds"/> (a server taken offline stays so).</summary>
        public void Restart(string name, double now)
        {
            name = name.Trim();
            if (!_until.TryGetValue(name, out double u) || !double.IsPositiveInfinity(u))
                _until[name] = now + RestartSeconds;
        }

        /// <summary>Whether the server answers at simulated time <paramref name="now"/>.</summary>
        public bool IsOnline(string name, double now)
        {
            name = name.Trim();
            if (!_until.TryGetValue(name, out double u))
                return true;
            if (now >= u)
            {
                _until.Remove(name);
                return true;
            }
            return false;
        }
    }
}
