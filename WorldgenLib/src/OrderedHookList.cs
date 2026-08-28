using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace WorldgenLib
{
    /// <summary>
    /// Generic ordered-delegate registry. Each step holds a sorted list of
    /// (order, index, delegate) tuples. Frozen after initWorldGen.
    /// </summary>
    public sealed class OrderedHookList<T> where T : class
    {
        private readonly List<(double Order, int Index, string ModId, T Delegate)> _entries = new();
        private readonly ConcurrentDictionary<string, byte> _disabledModIds =
            new(StringComparer.Ordinal);
        private readonly object _sync = new();
        private HookEntry[] _snapshot = Array.Empty<HookEntry>();
        private bool _frozen;
        private int _nextIndex;

        /// <summary>
        /// Immutable ordered entry used by hot-path callers. The array is
        /// published once at Freeze and never mutated afterwards.
        /// </summary>
        public readonly struct HookEntry
        {
            public double Order { get; }
            public string ModId { get; }
            public T Handler { get; }

            internal HookEntry(double order, string modId, T handler)
            {
                Order = order;
                ModId = modId;
                Handler = handler;
            }
        }

        /// <summary>
        /// Register a delegate. Throws if frozen (after initWorldGen).
        /// </summary>
        public void Register(string modId, double order, T handler)
        {
            if (string.IsNullOrWhiteSpace(modId))
                throw new ArgumentException("A hook owner id is required.", nameof(modId));

            if (!double.IsFinite(order))
                throw new ArgumentOutOfRangeException(nameof(order), "Hook order must be finite.");

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (_sync)
            {
                if (_frozen)
                    throw new InvalidOperationException(
                        $"[WorldgenLib] Cannot register after initWorldGen. Mod '{modId}' attempted late registration.");

                _entries.Add((order, _nextIndex++, modId, handler));
            }
        }

        /// <summary>
        /// Freeze the list. After this, no more registrations are allowed.
        /// Delegates are sorted by (order, registration index) for stable order.
        /// </summary>
        public void Freeze()
        {
            lock (_sync)
            {
                if (_frozen) return;

                _entries.Sort((a, b) =>
                {
                    int cmp = a.Order.CompareTo(b.Order);
                    return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
                });
                _snapshot = _entries
                    .Select(entry => new HookEntry(entry.Order, entry.ModId, entry.Delegate))
                    .ToArray();
                _frozen = true;
            }
        }

        /// <summary>
        /// Iterate delegates in frozen order. Throws if not frozen.
        /// </summary>
        public IEnumerable<(double Order, string ModId, T Delegate)> Enumerate()
        {
            HookEntry[] entries;
            lock (_sync)
            {
                if (!_frozen)
                    throw new InvalidOperationException("[WorldgenLib] Hook list must be frozen before enumeration.");

                entries = _snapshot;
            }

            foreach (var entry in entries)
            {
                if (IsDisabled(entry.ModId)) continue;
                yield return (entry.Order, entry.ModId, entry.Handler);
            }
        }

        /// <summary>
        /// Return the frozen ordered entries without allocating. This is the
        /// path used by terrain and BlockLayers hot loops. The returned span
        /// remains valid until this list is discarded; entries are immutable.
        /// </summary>
        public ReadOnlySpan<HookEntry> Snapshot
        {
            get
            {
                lock (_sync)
                {
                    if (!_frozen)
                        throw new InvalidOperationException(
                            "[WorldgenLib] Hook list must be frozen before reading its snapshot.");
                    return _snapshot;
                }
            }
        }

        /// <summary>Check disabled state without allocating or taking a lock.</summary>
        public bool IsDisabled(string modId)
            => !string.IsNullOrWhiteSpace(modId) && _disabledModIds.ContainsKey(modId);

        /// <summary>
        /// Disable all registrations owned by a mod after an invocation failure.
        /// This implements the per-hook failure boundary without mutating the
        /// sorted delegate array used by the worldgen hot path.
        /// </summary>
        public bool Disable(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId)) return false;

            lock (_sync)
            {
                bool wasAdded = _disabledModIds.TryAdd(modId, 0);
                return wasAdded && _entries.Any(entry => entry.ModId == modId);
            }
        }

        /// <summary>Whether at least one registration has been disabled.</summary>
        public bool HasDisabledRegistrations
        {
            get { return !_disabledModIds.IsEmpty; }
        }

        /// <summary>Number of registered delegates.</summary>
        public int Count
        {
            get { lock (_sync) return _entries.Count; }
        }

        /// <summary>Whether the list is frozen.</summary>
        public bool IsFrozen
        {
            get { lock (_sync) return _frozen; }
        }

        /// <summary>List registered effects for the startup report.</summary>
        public IReadOnlyList<(double Order, string ModId)> GetRegistrationReport()
        {
            lock (_sync)
                return _entries.Select(e => (e.Order, e.ModId)).ToList();
        }
    }
}
