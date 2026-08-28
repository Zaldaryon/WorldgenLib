using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace WorldgenLib
{
    /// <summary>
    /// Registry for persistent, bounded custom maps attached to an
    /// <see cref="IMapRegion"/>. A slot is registered during mod startup and
    /// its data is serialized into the region's permanent moddata when the
    /// host finishes a region.
    /// </summary>
    public static class RegionMapRegistry
    {
        private const int MaxBytesPerRegion = 4 * 1024 * 1024;
        private static readonly object Sync = new();
        private static readonly List<RegionMapSlot> _slots = new();
        private static readonly Dictionary<string, RegionMapSlot> _byCode =
            new(StringComparer.OrdinalIgnoreCase);
        private static int _declaredBytes;
        private static bool _frozen;

        /// <summary>
        /// Register a bounded map slot. The declared storage budget is
        /// charged against a 4 MiB per-region aggregate before registration
        /// succeeds, preventing an accidental unbounded save format.
        /// </summary>
        public static RegionMapSlot Register(string modId, string mapCode,
            int innerSize, int padding, int formatVersion)
        {
            if (string.IsNullOrWhiteSpace(modId))
                throw new ArgumentException("A map owner id is required.", nameof(modId));
            if (string.IsNullOrWhiteSpace(mapCode))
                throw new ArgumentException("A map code is required.", nameof(mapCode));
            if (innerSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(innerSize), "Inner size must be positive.");
            if (padding < 0)
                throw new ArgumentOutOfRangeException(nameof(padding), "Padding must be non-negative.");
            if (formatVersion < 0)
                throw new ArgumentOutOfRangeException(nameof(formatVersion), "Format version cannot be negative.");

            string canonicalCode = mapCode.Trim().ToLowerInvariant();
            int totalSize;
            int storageBytes;
            try
            {
                totalSize = checked(innerSize + checked(2 * padding));
                storageBytes = checked(16 + checked(totalSize * totalSize * sizeof(int)));
            }
            catch (OverflowException ex)
            {
                throw new ArgumentOutOfRangeException(nameof(innerSize),
                    "Region map dimensions overflow the supported save format: " + ex.Message);
            }

            lock (Sync)
            {
                if (_frozen)
                    throw new InvalidOperationException(
                        $"[WorldgenLib] Cannot register region maps after initWorldGen. Mod '{modId}' attempted late registration.");

                if (_byCode.ContainsKey(canonicalCode))
                    throw new ArgumentException(
                        $"[WorldgenLib] Region map '{canonicalCode}' is already registered.", nameof(mapCode));
                if (_declaredBytes > MaxBytesPerRegion - storageBytes)
                    throw new InvalidOperationException(
                        $"[WorldgenLib] Region map budget exceeded by '{canonicalCode}'. " +
                        $"Declared={storageBytes} bytes, remaining={MaxBytesPerRegion - _declaredBytes} bytes.");

                var slot = new RegionMapSlot(modId.Trim(), canonicalCode,
                    innerSize, padding, formatVersion, storageBytes);
                _slots.Add(slot);
                _byCode[canonicalCode] = slot;
                _declaredBytes += storageBytes;
                return slot;
            }
        }

        public static RegionMapSlot? GetSlot(string mapCode)
        {
            if (string.IsNullOrWhiteSpace(mapCode)) return null;
            lock (Sync)
                return _byCode.TryGetValue(mapCode.Trim(), out var slot) ? slot : null;
        }

        public static IReadOnlyList<RegionMapSlot> All
        {
            get { lock (Sync) return _slots.ToArray(); }
        }

        internal static void Freeze()
        {
            lock (Sync) _frozen = true;
        }

        public static bool IsFrozen
        {
            get { lock (Sync) return _frozen; }
        }

        /// <summary>Flush all maps loaded for this region to permanent moddata.</summary>
        internal static void FlushRegion(IMapRegion region)
        {
            if (region == null) throw new ArgumentNullException(nameof(region));
            RegionMapSlot[] slots;
            lock (Sync) slots = _slots.ToArray();
            foreach (var slot in slots) slot.Flush(region);
        }

        internal static void Reset()
        {
            lock (Sync)
            {
                _slots.Clear();
                _byCode.Clear();
                _declaredBytes = 0;
                _frozen = false;
            }
        }
    }

    /// <summary>A named, dimensioned custom map with persistent region access.</summary>
    public sealed class RegionMapSlot
    {
        private const int Magic = 0x544C4D31; // TLM1
        private readonly object _sync = new();
        private readonly Dictionary<(int X, int Z), IntDataMap2D> _legacyMaps = new();
        private readonly ConditionalWeakTable<IMapRegion, RegionState> _regions = new();
        private readonly string _persistenceKey;
        private readonly int _storageBytes;

        public string ModId { get; }
        public string MapCode { get; }
        public int InnerSize { get; }
        public int Padding { get; }
        public int FormatVersion { get; }
        public int TotalSize { get; }

        internal RegionMapSlot(string modId, string mapCode, int innerSize,
            int padding, int formatVersion, int storageBytes)
        {
            ModId = modId;
            MapCode = mapCode;
            InnerSize = innerSize;
            Padding = padding;
            FormatVersion = formatVersion;
            TotalSize = checked(innerSize + 2 * padding);
            _storageBytes = storageBytes;
            _persistenceKey = "worldgenlib:region-map:" + MapCode;
        }

        /// <summary>Compatibility access for pure in-memory callers and tests.</summary>
        public IntDataMap2D GetMap(int regionX, int regionZ)
        {
            lock (_sync)
            {
                var key = (regionX, regionZ);
                if (!_legacyMaps.TryGetValue(key, out var map))
                {
                    map = CreateMap();
                    _legacyMaps[key] = map;
                }
                return map;
            }
        }

        /// <summary>
        /// Get or load this slot's map for a real map region. The returned
        /// instance is retained until the region is unloaded and is serialized
        /// on the next region flush.
        /// </summary>
        public IntDataMap2D GetMap(IMapRegion region, int regionX, int regionZ)
        {
            if (region == null) throw new ArgumentNullException(nameof(region));
            var state = _regions.GetValue(region, LoadState);
            lock (state.Sync)
            {
                if (state.Map == null)
                {
                    state.Map = Decode(region.GetModdata(_persistenceKey)) ?? CreateMap();
                    region.ModMaps[MapCode] = state.Map;
                }
                return state.Map;
            }
        }

        /// <summary>Replace a real region map and mark the region for saving.</summary>
        public void SetMap(IMapRegion region, int regionX, int regionZ, int[] data)
        {
            if (region == null) throw new ArgumentNullException(nameof(region));
            if (data == null) throw new ArgumentNullException(nameof(data));
            var map = GetMap(region, regionX, regionZ);
            int expected = checked(TotalSize * TotalSize);
            if (data.Length != expected)
                throw new ArgumentException(
                    $"[WorldgenLib] Map data length {data.Length} does not match expected {expected}.", nameof(data));
            lock (_regions.GetValue(region, LoadState).Sync)
            {
                map.Data = (int[])data.Clone();
                region.ModMaps[MapCode] = map;
                region.DirtyForSaving = true;
            }
        }

        public void SetMap(int regionX, int regionZ, int[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var map = GetMap(regionX, regionZ);
            if (data.Length != map.Data.Length)
                throw new ArgumentException(
                    $"[WorldgenLib] Map data length {data.Length} does not match expected {map.Data.Length}.", nameof(data));
            lock (_sync) map.Data = (int[])data.Clone();
        }

        public bool HasMap(int regionX, int regionZ)
        {
            lock (_sync) return _legacyMaps.ContainsKey((regionX, regionZ));
        }

        public int AllocatedCount
        {
            get { lock (_sync) return _legacyMaps.Count; }
        }

        internal void Flush(IMapRegion region)
        {
            if (!_regions.TryGetValue(region, out var state)) return;
            lock (state.Sync)
            {
                if (state.Map == null) return;
                region.SetModdata(_persistenceKey, Encode(state.Map));
                region.ModMaps[MapCode] = state.Map;
            }
        }

        private IntDataMap2D CreateMap()
        {
            return new IntDataMap2D
            {
                Size = TotalSize,
                TopLeftPadding = Padding,
                BottomRightPadding = Padding,
                Data = new int[checked(TotalSize * TotalSize)]
            };
        }

        private RegionState LoadState(IMapRegion region) => new();

        private byte[] Encode(IntDataMap2D map)
        {
            int dataLength = checked(TotalSize * TotalSize);
            if (map.Data == null || map.Data.Length != dataLength)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Map '{MapCode}' has invalid data length during save.");

            var bytes = new byte[_storageBytes];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), Magic);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), TotalSize);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), dataLength);
            for (int i = 0; i < dataLength; i++)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16 + i * 4, 4), map.Data[i]);
            return bytes;
        }

        private IntDataMap2D? Decode(byte[]? bytes)
        {
            int dataLength = checked(TotalSize * TotalSize);
            if (bytes == null || bytes.Length != _storageBytes || bytes.Length < 16)
                return null;
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)) != Magic
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)) != FormatVersion
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)) != TotalSize
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4)) != dataLength)
                return null;

            var data = new int[dataLength];
            for (int i = 0; i < dataLength; i++)
                data[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16 + i * 4, 4));
            return new IntDataMap2D
            {
                Size = TotalSize,
                TopLeftPadding = Padding,
                BottomRightPadding = Padding,
                Data = data
            };
        }

        private sealed class RegionState
        {
            internal readonly object Sync = new();
            internal IntDataMap2D? Map;
        }
    }
}
