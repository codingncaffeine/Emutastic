using System;
using System.Collections.Generic;

namespace Emutastic.Services
{
    /// <summary>
    /// Decides which SDL device each of the four player ports reads, so that a controller is
    /// never read by two players at once and a disconnect never shifts the remaining players.
    ///
    /// One table per session: a game window's four <see cref="ControllerManager"/>s share one,
    /// and the library window's single manager (EmuTV, the TV chord, the Preferences capture
    /// panel) has its own. Resolution follows three rules — the same three the Linux port
    /// applies in SdlInput.ResolvePorts, so a config file behaves the same in both apps:
    ///
    ///   1. A port whose configuration binds a device (InputConfiguration.ControllerDeviceId)
    ///      reads THAT device — or nothing while it is absent. It is deliberately not handed
    ///      some other pad: silently giving player 1 player 2's controller is the confusion an
    ///      explicit binding exists to remove.
    ///   2. An unbound port keeps the device it already has for as long as that device stays
    ///      attached and no binding claims it.
    ///   3. An unbound port with no device takes the next unclaimed device in SDL enumeration
    ///      order.
    ///
    /// Rule 2 is what makes couch play survive a disconnect: a pad dropping out frees only the
    /// port that owned it. Rule 3 is what the old "player N polls XInput slot N" default could
    /// not do once bindings existed — with P1 bound to a Retrolink, an unbound P2 sat on XInput
    /// slot 1 and never saw the Xbox pad in slot 0. And because every port now reads through
    /// SDL, a pad bound to one player can no longer be read a second time by another player
    /// through its XInput slot: SDL enumeration order and XInput slot order are independent
    /// (they drift apart after a replug, since XInput re-uses the lowest free slot while SDL
    /// appends), so that double read happened whenever the two disagreed.
    ///
    /// XInput slots remain the read path only while SDL3 is unavailable — see
    /// ControllerManager.PollController.
    ///
    /// THREADING
    /// ---------
    /// Called from each player's 60 Hz poll timer thread. Resolution runs under a lock, once
    /// per published snapshot set (SdlJoystickHub publishes a new set every 16 ms) or when a
    /// binding changes, so the four callers always share one answer computed from one view of
    /// the devices.
    /// </summary>
    public sealed class ControllerPortTable
    {
        public const int PortCount = 4;

        private readonly object    _gate     = new();
        private readonly string?[] _bound    = new string?[PortCount];   // from configuration; null = unbound
        private readonly string?[] _assigned = new string?[PortCount];   // what each port reads right now
        private readonly string    _label;
        private SdlJoystickHub.SnapshotSet? _resolvedFor;
        private bool _bindingsChanged = true;

        /// <param name="label">Names this table in controller-diag.log ("game", "library", …).</param>
        public ControllerPortTable(string label)
        {
            _label = label;
        }

        /// <summary>Record a port's configured binding. Null or blank = unbound.</summary>
        public void SetBinding(int port, string? deviceId)
        {
            if (port < 0 || port >= PortCount) return;
            string? id = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
            lock (_gate)
            {
                if (string.Equals(_bound[port], id, StringComparison.Ordinal)) return;
                _bound[port] = id;
                _bindingsChanged = true;
            }
        }

        /// <summary>The device id a port is bound to by configuration, or null when unbound.</summary>
        public string? BoundId(int port)
        {
            if (port < 0 || port >= PortCount) return null;
            lock (_gate) return _bound[port];
        }

        /// <summary>
        /// The device id <paramref name="port"/> reads given <paramref name="set"/>, or null
        /// when it reads nothing (its bound pad is absent, or no unclaimed pad is left).
        /// </summary>
        internal string? DeviceFor(int port, SdlJoystickHub.SnapshotSet set)
        {
            if (port < 0 || port >= PortCount) return null;
            lock (_gate)
            {
                if (_bindingsChanged || !ReferenceEquals(set, _resolvedFor)) Resolve(set);
                return _assigned[port];
            }
        }

        // Caller holds _gate.
        private void Resolve(SdlJoystickHub.SnapshotSet set)
        {
            _resolvedFor     = set;
            _bindingsChanged = false;

            // "Attached" means enumerated AND still connected. A pad that just dropped out is
            // still in the set (Connected == false) until the next 1 s reconcile closes it;
            // treating it as absent here frees its port at once instead of a second later.
            var attached = new List<string>(set.Order.Length);
            foreach (var id in set.Order)
                if (set.ById.TryGetValue(id, out var s) && s.Connected) attached.Add(id);
            var attachedSet = new HashSet<string>(attached, StringComparer.Ordinal);
            var claimed     = new HashSet<string>(StringComparer.Ordinal);

            var before = (string?[])_assigned.Clone();

            // 1. Bound ports claim their device — or read nothing while it is absent.
            for (int p = 0; p < PortCount; p++)
            {
                string? bound = _bound[p];
                if (bound == null) continue;
                _assigned[p] = attachedSet.Contains(bound) ? bound : null;
                if (_assigned[p] != null) claimed.Add(bound);
            }

            // 2. Unbound ports keep what they have, if still attached and not claimed by a binding.
            for (int p = 0; p < PortCount; p++)
            {
                if (_bound[p] != null) continue;
                string? cur = _assigned[p];
                if (cur != null && (!attachedSet.Contains(cur) || claimed.Contains(cur)))
                    _assigned[p] = cur = null;
                if (cur != null) claimed.Add(cur);
            }

            // 3. Unbound, empty ports take the next unclaimed device in enumeration order.
            foreach (var id in attached)
            {
                if (claimed.Contains(id)) continue;
                int free = -1;
                for (int p = 0; p < PortCount; p++)
                    if (_bound[p] == null && _assigned[p] == null) { free = p; break; }
                if (free < 0) break;
                _assigned[free] = id;
                claimed.Add(id);
            }

            for (int p = 0; p < PortCount; p++)
            {
                if (string.Equals(before[p], _assigned[p], StringComparison.Ordinal)) continue;
                string source = _bound[p] != null ? $"bound '{_bound[p]}'" : "default (enumeration order)";
                ControllerManager.CtrlLog(_assigned[p] != null
                    ? $"[ports:{_label}] P{p + 1} <- '{_assigned[p]}'  [{source}]"
                    : $"[ports:{_label}] P{p + 1} <- (none)  [{source}{(_bound[p] != null ? " — NOT ATTACHED" : "")}]");
            }
        }
    }
}
