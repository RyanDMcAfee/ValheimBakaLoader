using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using ValheimBakaLoader.Forms;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Every live server session in the whole application, not just the ones belonging to one
    /// window.
    /// <para>
    /// BakaLoader opens one window per auto-start profile, and each window keeps its own
    /// registry of the sessions it created. That is the right shape for everything a window
    /// does to its own servers, and exactly the wrong shape for the one question the self
    /// update has to ask: updating means replacing this process, and every window goes down
    /// with it. A window whose own profile is stopped used to answer "nothing is running" while
    /// the window next to it had a world full of vikings, and the update went ahead on that
    /// answer. This registry is where the app as a whole keeps that answer.
    /// </para>
    /// </summary>
    public interface IServerSessionRegistry
    {
        /// <summary>Adds a session. Registering the same session twice is a no-op.</summary>
        void Register(ServerSession session);

        /// <summary>Removes a session. Unregistering one that was never added is a no-op.</summary>
        void Unregister(ServerSession session);

        /// <summary>
        /// A snapshot of every session registered right now. A snapshot rather than a live view,
        /// so a window closing on another thread cannot throw halfway through a caller's scan.
        /// </summary>
        IReadOnlyCollection<ServerSession> All { get; }
    }

    /// <inheritdoc />
    public class ServerSessionRegistry : IServerSessionRegistry
    {
        // A set with reference identity: two profiles in two windows may carry the same name,
        // and both of their servers are real. ConcurrentDictionary rather than a lock because
        // windows register from the UI thread while the close guard reads from wherever the
        // shutdown happens to be.
        private readonly ConcurrentDictionary<ServerSession, byte> Live =
            new(ReferenceEqualityComparer.Instance);

        public void Register(ServerSession session)
        {
            if (session == null) return;
            Live[session] = 0;
        }

        public void Unregister(ServerSession session)
        {
            if (session == null) return;
            Live.TryRemove(session, out _);
        }

        public IReadOnlyCollection<ServerSession> All => Live.Keys.ToList();

        /// <summary>
        /// Reference identity for the session set. ServerSession does not override Equals, so
        /// the default comparer already does this, but saying it here means a value-equality
        /// override added later cannot quietly collapse two real sessions into one entry.
        /// </summary>
        private sealed class ReferenceEqualityComparer : IEqualityComparer<ServerSession>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            public bool Equals(ServerSession x, ServerSession y) => ReferenceEquals(x, y);

            public int GetHashCode(ServerSession obj)
                => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
