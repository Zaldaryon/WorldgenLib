using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace WorldgenLib
{
    /// <summary>
    /// Canonical registry for vanilla and custom landform variants.
    ///
    /// Vanilla landforms are imported before consumer StartServerSide methods
    /// are called. Custom variants are appended to the same
    /// <see cref="LandformsWorldProperty.LandFormsByIndex"/> array, initialized
    /// with the current world height, and therefore receive the same numeric
    /// index used by the terrain host. A later vanilla landform reload is
    /// rebound automatically through <see cref="Reload"/>.
    /// </summary>
    public static class LandformRegistry
    {
        private static readonly object Sync = new();
        private static readonly List<RegisteredLandform> _all = new();
        private static readonly List<RegisteredLandform> _custom = new();
        private static readonly List<LandformVariant> _vanillaVariants = new();
        private static readonly Dictionary<string, int> _codeToIndex =
            new(StringComparer.OrdinalIgnoreCase);

        private static ICoreServerAPI? _serverApi;
        private static IWorldManagerAPI? _worldManager;
        private static LandformsWorldProperty? _landforms;
        private static LandformsWorldProperty? _previousLandforms;
        private static bool _capturedPreviousLandforms;
        private static bool _frozen;

        /// <summary>
        /// Import the current vanilla landforms. Called before consumers can
        /// register, and again after a vanilla /wgen reload if necessary.
        /// </summary>
        internal static void Initialize(ICoreServerAPI api)
        {
            lock (Sync)
            {
                _serverApi = api;
                _worldManager = api.WorldManager;
                if (!_capturedPreviousLandforms)
                {
                    _previousLandforms = NoiseLandforms.landforms;
                    _capturedPreviousLandforms = true;
                }
                NoiseLandforms.LoadLandforms(api);
                BindCurrentLandforms();
            }
        }

        /// <summary>
        /// Register a custom landform variant and return its canonical index.
        /// Registration is allowed during mod startup, before worldgen init is
        /// frozen. The variant must contain the source arrays expected by
        /// vanilla LandformVariant.Init.
        /// </summary>
        public static int Register(string code, LandformVariant variant)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("A landform code is required.", nameof(code));
            if (variant == null)
                throw new ArgumentNullException(nameof(variant));

            lock (Sync)
            {
                if (_frozen)
                    throw new InvalidOperationException(
                        $"[WorldgenLib] Cannot register landforms after initWorldGen. Attempted to register '{code}'.");

                var location = AssetLocation.Create(code);
                string canonicalCode = location.ToString();
                if (_codeToIndex.ContainsKey(canonicalCode))
                    throw new ArgumentException(
                        $"[WorldgenLib] Landform '{canonicalCode}' is already registered.", nameof(code));

                ValidateVariant(canonicalCode, variant);
                variant.Code = location;
                _custom.Add(new RegisteredLandform(canonicalCode, variant, -1));

                // Initialize runs in WorldgenLib.AssetsFinalize in production.
                // Keep registration usable in small test hosts that register
                // before a vanilla property exists; the index is rebound before
                // worldgen starts.
                if (_landforms != null && _worldManager != null)
                    BindCurrentLandforms();

                return GetIndexUnderLock(canonicalCode);
            }
        }

        /// <summary>Get a registered landform by canonical code.</summary>
        public static LandformVariant? GetByCode(string code)
        {
            lock (Sync)
            {
                return _codeToIndex.TryGetValue(Normalize(code), out int index)
                    ? _all[index].Variant
                    : null;
            }
        }

        /// <summary>Get a canonical landform index, or -1 when absent.</summary>
        public static int GetIndex(string code)
        {
            lock (Sync)
            {
                return _codeToIndex.TryGetValue(Normalize(code), out int index) ? index : -1;
            }
        }

        /// <summary>Get the interpolated Y threshold array for a landform.</summary>
        public static float[] GetThresholds(int landformIndex)
        {
            lock (Sync)
                return (float[])GetEntryUnderLock(landformIndex).Variant.TerrainYThresholds.Clone();
        }

        /// <summary>
        /// Replace a landform's interpolated thresholds. The array is used by
        /// the active terrain host, so it must have exactly MapSizeY elements.
        /// </summary>
        public static void SetThresholds(int landformIndex, float[] thresholds)
        {
            if (thresholds == null) throw new ArgumentNullException(nameof(thresholds));

            lock (Sync)
            {
                if (_frozen)
                    throw new InvalidOperationException(
                        "[WorldgenLib] Cannot change landform thresholds after initWorldGen.");
                if (_worldManager != null && thresholds.Length != _worldManager.MapSizeY)
                    throw new ArgumentException(
                        $"Expected {_worldManager.MapSizeY} thresholds, got {thresholds.Length}.",
                        nameof(thresholds));
                for (int i = 0; i < thresholds.Length; i++)
                {
                    if (!float.IsFinite(thresholds[i]))
                        throw new ArgumentException(
                            "Threshold values must all be finite.", nameof(thresholds));
                }
                GetEntryUnderLock(landformIndex).Variant.TerrainYThresholds =
                    (float[])thresholds.Clone();
            }
        }

        /// <summary>Get the terrain octave amplitudes for a landform.</summary>
        public static double[] GetTerrainOctaves(int landformIndex)
        {
            lock (Sync)
                return (double[])GetEntryUnderLock(landformIndex).Variant.TerrainOctaves.Clone();
        }

        /// <summary>Get the terrain octave thresholds for a landform.</summary>
        public static double[] GetTerrainOctaveThresholds(int landformIndex)
        {
            lock (Sync)
                return (double[])GetEntryUnderLock(landformIndex).Variant.TerrainOctaveThresholds.Clone();
        }

        /// <summary>All vanilla and custom landforms in canonical index order.</summary>
        public static IReadOnlyList<RegisteredLandform> All
        {
            get { lock (Sync) return _all.ToArray(); }
        }

        /// <summary>The active canonical vanilla/custom property.</summary>
        public static LandformsWorldProperty Landforms
        {
            get
            {
                lock (Sync)
                    return _landforms ?? throw new InvalidOperationException(
                        "[WorldgenLib] Landforms are not initialized yet.");
            }
        }

        /// <summary>Whether registration is closed for this worldgen run.</summary>
        public static bool IsFrozen
        {
            get { lock (Sync) return _frozen; }
        }

        /// <summary>Freeze registration after the InitWorldGen deadline.</summary>
        internal static void Freeze()
        {
            lock (Sync) _frozen = true;
        }

        /// <summary>Reload vanilla landforms and re-append registered custom variants.</summary>
        public static void Reload()
        {
            lock (Sync)
            {
                if (_serverApi == null)
                    throw new InvalidOperationException("[WorldgenLib] Landforms are not initialized.");
                if (_frozen)
                    throw new InvalidOperationException(
                        "[WorldgenLib] Landforms cannot be reloaded after worldgen initialization. " +
                        "Reload before the registration freeze or restart the worldgen session.");
                NoiseLandforms.LoadLandforms(_serverApi);
                BindCurrentLandforms();
            }
        }

        /// <summary>
        /// Rebind after a vanilla GenMaps initialization. This repairs the
        /// static vanilla property if GenMaps reloads landforms after WorldgenLib's
        /// own callback.
        /// </summary>
        internal static void RebindCurrent()
        {
            lock (Sync)
            {
                if (NoiseLandforms.landforms != null)
                    BindCurrentLandforms();
            }
        }

        /// <summary>Reset registry state for isolated tests.</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                if (_capturedPreviousLandforms)
                {
                    // The reference field is nullable at runtime during
                    // teardown even though the decompiled API omits that
                    // annotation. Restore the exact value captured before
                    // WorldgenLib touched the vanilla singleton.
                    NoiseLandforms.landforms = _previousLandforms!;
                }

                _all.Clear();
                _custom.Clear();
                _vanillaVariants.Clear();
                _codeToIndex.Clear();
                _landforms = null;
                _serverApi = null;
                _worldManager = null;
                _previousLandforms = null;
                _capturedPreviousLandforms = false;
                _frozen = false;
            }
        }

        private static void BindCurrentLandforms()
        {
            var vanilla = NoiseLandforms.landforms;
            if (vanilla == null || vanilla.LandFormsByIndex == null)
                throw new InvalidOperationException("[WorldgenLib] Vanilla landforms are not initialized.");

            _landforms = vanilla;

            // GenMaps and /wgen regen can replace the static array. A prior
            // WorldgenLib bind may already have appended custom entries, so
            // never treat those entries as vanilla on a subsequent bind.
            var customCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var custom in _custom) customCodes.Add(custom.Code);
            var vanillaOnly = new List<LandformVariant>(vanilla.LandFormsByIndex.Length);
            foreach (var variant in vanilla.LandFormsByIndex)
            {
                string code = Normalize(variant.Code?.ToString() ?? string.Empty);
                if (!customCodes.Contains(code)) vanillaOnly.Add(variant);
            }

            _vanillaVariants.Clear();
            _vanillaVariants.AddRange(vanillaOnly);
            int vanillaCount = _vanillaVariants.Count;
            var combined = new LandformVariant[vanillaCount + _custom.Count];
            _vanillaVariants.CopyTo(combined, 0);

            _all.Clear();
            _codeToIndex.Clear();

            for (int i = 0; i < vanillaCount; i++)
            {
                LandformVariant variant = _vanillaVariants[i];
                string code = Normalize(variant.Code?.ToString() ?? variant.Code?.Path ?? string.Empty);
                if (string.IsNullOrEmpty(code))
                    throw new InvalidOperationException($"[WorldgenLib] Vanilla landform at index {i} has no code.");
                AddCanonicalEntry(new RegisteredLandform(code, variant, i));
            }

            for (int i = 0; i < _custom.Count; i++)
            {
                RegisteredLandform custom = _custom[i];
                int index = vanillaCount + i;
                if (_worldManager == null)
                    throw new InvalidOperationException("[WorldgenLib] World manager unavailable.");
                custom.Variant.Init(_worldManager, index);
                combined[index] = custom.Variant;
                AddCanonicalEntry(new RegisteredLandform(custom.Code, custom.Variant, index));
            }

            vanilla.LandFormsByIndex = combined;
        }

        private static void AddCanonicalEntry(RegisteredLandform entry)
        {
            if (!_codeToIndex.TryAdd(entry.Code, entry.Index))
                throw new InvalidOperationException(
                    $"[WorldgenLib] Duplicate landform code '{entry.Code}' in the active world.");
            _all.Add(entry);
        }

        private static RegisteredLandform GetEntryUnderLock(int index)
        {
            if (index < 0 || index >= _all.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _all[index];
        }

        private static int GetIndexUnderLock(string code)
            => _codeToIndex.TryGetValue(code, out int index) ? index : -1;

        private static string Normalize(string code)
            => string.IsNullOrWhiteSpace(code) ? string.Empty : AssetLocation.Create(code).ToString();

        private static void ValidateVariant(string code, LandformVariant variant)
        {
            if (variant.TerrainOctaves == null || variant.TerrainOctaves.Length == 0)
                throw new ArgumentException($"Landform '{code}' has no TerrainOctaves.", nameof(variant));
            if (variant.TerrainOctaveThresholds == null || variant.TerrainOctaveThresholds.Length == 0)
                throw new ArgumentException($"Landform '{code}' has no TerrainOctaveThresholds.", nameof(variant));
            if (variant.TerrainYKeyPositions == null || variant.TerrainYKeyPositions.Length == 0
                || variant.TerrainYKeyThresholds == null
                || variant.TerrainYKeyPositions.Length != variant.TerrainYKeyThresholds.Length)
                throw new ArgumentException(
                    $"Landform '{code}' needs matching TerrainYKeyPositions and TerrainYKeyThresholds.",
                    nameof(variant));
        }

        /// <summary>A landform plus its canonical numeric index.</summary>
        public sealed class RegisteredLandform
        {
            public string Code { get; }
            public LandformVariant Variant { get; }
            public int Index { get; }

            internal RegisteredLandform(string code, LandformVariant variant, int index)
            {
                Code = code;
                Variant = variant;
                Index = index;
            }
        }
    }
}
