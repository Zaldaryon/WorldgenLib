using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace WorldgenLib
{
    /// <summary>
    /// Public API facade for WorldgenLib. Consumer mods call these static methods
    /// to register hooks on the worldgen pipeline.
    ///
    /// Usage from a consumer mod's StartServerSide:
    /// <code>
    /// WorldgenLibAPI.RegisterStep7("my-mod", OrderBands.AfterVanillaMin + 50, MyThresholdHook);
    /// </code>
    ///
    /// Registration must happen during StartServerSide, before InitWorldGen freezes
    /// the hook lists. After freezing, attempts to register throw InvalidOperationException.
    /// </summary>
    public static class WorldgenLibAPI
    {
        private static WorldgenLibMod? _modInstance;
        private static StepRegistry _stepRegistry = new();

        /// <summary>
        /// Initialize the API with the mod instance. Called internally by WorldgenLibMod
        /// during AssetsFinalize. Do not call directly.
        /// </summary>
        internal static void Initialize(WorldgenLibMod mod)
        {
            _modInstance = mod;
        }

        /// <summary>
        /// Get the WorldgenLib mod instance. Returns null if WorldgenLib is not loaded.
        /// Consumer mods should check for null and log a warning if WorldgenLib is missing.
        /// </summary>
        public static WorldgenLibMod? Instance => _modInstance;

        /// <summary>
        /// Check if WorldgenLib is loaded and initialized. Use this before registering hooks.
        /// Returns true if WorldgenLib is available, false if not loaded.
        /// </summary>
        public static bool IsLoaded => _modInstance != null;

        // ════════════════════════════════════════════════════════════════
        //  GenTerraHost hook registration
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Register a hook at Step 0 (BorderTaperPrepare). Per-chunk.
        /// Runs before the main generate loop. TaperMap is mutable here.
        /// </summary>
        /// <param name="modId">Your mod's identifier.</param>
        /// <param name="order">Execution order within the step (use OrderBands constants).</param>
        /// <param name="hook">The hook delegate.</param>
        public static void RegisterStep0(string modId, double order, BorderTaperHook hook)
            => GetHost().RegisterStep0(modId, order, hook);

        /// <summary>
        /// Register a hook at Step 2 (BuildCornerOctaves). Per-column, inside Parallel.For.
        /// Modify LandformWeights, OctaveAmplitudes, OctaveThresholds.
        /// </summary>
        public static void RegisterStep2(string modId, double order, BuildOctavesHook hook)
            => GetHost().RegisterStep2(modId, order, hook);

        /// <summary>
        /// Register a hook at Step 4 (VerticalDistortion). Per-column, inside Parallel.For.
        /// Modify DistY to add vertical displacement.
        /// </summary>
        public static void RegisterStep4(string modId, double order, VerticalDistortionHook hook)
            => GetHost().RegisterStep4(modId, order, hook);

        /// <summary>
        /// Register a hook at Step 5 (WaterColumnSelect). Per-column, inside Parallel.For.
        /// Modify WaterBlockId to change water type (salt → fresh, etc.).
        /// </summary>
        public static void RegisterStep5(string modId, double order, WaterSelectHook hook)
            => GetHost().RegisterStep5(modId, order, hook);

        /// <summary>
        /// Register a hook at Step 7 (PerVoxelThreshold). Per-column, per-Y, inside Parallel.For.
        /// Return the modified threshold. This is the hottest hook — called ~256K times per chunk.
        /// </summary>
        public static void RegisterStep7(string modId, double order, ThresholdHook hook)
            => GetHost().RegisterStep7(modId, order, hook);

        /// <summary>
        /// Register a hook at Step 10 (PostPlacementColumn). Per-column, after block placement.
        /// Full block accessor available for carving, water placement, etc.
        /// </summary>
        public static void RegisterStep10(string modId, double order, PostPlacementHook hook)
            => GetHost().RegisterStep10(modId, order, hook);

        /// <summary>
        /// Register a hook after all terrain columns and Step 10 hooks in a
        /// request have completed. Use it to persist chunk-wide arrays exactly once.
        /// </summary>
        public static void RegisterTerrainFinalize(string modId, double order,
            TerrainFinalizeHook hook)
            => GetHost().RegisterTerrainFinalize(modId, order, hook);

        // ════════════════════════════════════════════════════════════════
        //  GenTerraPostProcessHost hook registration
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Register an opt-out hook for GenTerraPostProcess. Return true to skip
        /// post-processing for this chunk (floating island deletion, etc.).
        /// </summary>
        public static void RegisterPostProcessOptOut(string modId, double order,
            GenTerraPostProcessHost.ChunkPostProcessHook hook)
            => GetPostProcessHost().RegisterOptOut(modId, order, hook);

        /// <summary>
        /// Register a cleanup rule hook for GenTerraPostProcess. Return false to
        /// prevent deletion of a specific floating node.
        /// </summary>
        public static void RegisterCleanupRule(string modId, double order,
            GenTerraPostProcessHost.CleanupRuleHook hook)
            => GetPostProcessHost().RegisterCleanupRule(modId, order, hook);

        // ════════════════════════════════════════════════════════════════
        //  GenMapsHost hook registration
        // ════════════════════════════════════════════════════════════════

        /// <summary>Register a hook at GenMaps Step 1 (Geoprovince). Per-region.</summary>
        public static void RegisterMapsGeoprovince(string modId, double order, MapsStepHook hook)
            => GetMapsHost().RegisterGeoprovinceHook(modId, order, hook);

        /// <summary>Register a hook at GenMaps Step 2 (Climate). Per-region.</summary>
        public static void RegisterMapsClimate(string modId, double order, MapsStepHook hook)
            => GetMapsHost().RegisterClimateHook(modId, order, hook);

        /// <summary>Register a hook at GenMaps Step 3 (Forest). Per-region.</summary>
        public static void RegisterMapsForest(string modId, double order, MapsStepHook hook)
            => GetMapsHost().RegisterForestHook(modId, order, hook);

        /// <summary>Register a hook at GenMaps Step 4 (Upheavel). Per-region.</summary>
        public static void RegisterMapsUpheavel(string modId, double order, MapsStepHook hook)
            => GetMapsHost().RegisterUpheavelHook(modId, order, hook);

        /// <summary>Register a hook at GenMaps Step 5 (Ocean). Per-region.</summary>
        public static void RegisterMapsOcean(string modId, double order, MapsStepHook hook)
            => GetMapsHost().RegisterOceanHook(modId, order, hook);

        /// <summary>Register a hook at GenMaps Step 6 (Beach). Per-region.</summary>
        public static void RegisterMapsBeach(string modId, double order, MapsStepHook hook)
            => GetMapsHost().RegisterBeachHook(modId, order, hook);

        /// <summary>Register a hook at GenMaps Step 7 (Shrub). Per-region.</summary>
        public static void RegisterMapsShrub(string modId, double order, MapsStepHook hook)
            => GetMapsHost().RegisterShrubHook(modId, order, hook);

        /// <summary>Register a hook at GenMaps Step 8 (Biome). Per-region.</summary>
        public static void RegisterMapsBiome(string modId, double order, MapsStepHook hook)
            => GetMapsHost().RegisterBiomeHook(modId, order, hook);

        /// <summary>Register a hook at GenMaps Step 9 (Landform). Per-region.</summary>
        public static void RegisterMapsLandform(string modId, double order, MapsStepHook hook)
            => GetMapsHost().RegisterLandformHook(modId, order, hook);

        /// <summary>Register a hook at GenMaps RegionFinalize. After all maps, before DirtyForSaving.</summary>
        public static void RegisterMapsRegionFinalize(string modId, double order, RegionFinalizeHook hook)
            => GetMapsHost().RegisterRegionFinalizeHook(modId, order, hook);

        /// <summary>Register a wrapper around one vanilla map-layer generator.</summary>
        public static void RegisterMapGenerator(MapGeneratorStep step, string modId,
            double order, MapGeneratorHook hook)
            => GetMapsHost().RegisterMapGenerator(step, modId, order, hook);

        /// <summary>
        /// Register a replacement for a map stage's padding constant. This
        /// covers mods that previously rewrote a local GenMaps padding value.
        /// </summary>
        public static void RegisterMapPadding(string modId, double order, MapPaddingHook hook)
            => GetMapsHost().RegisterMapPadding(modId, order, hook);

        /// <summary>
        /// Register a terminal map-region adapter for consumers whose complete
        /// region pass cannot be decomposed into the nine vanilla maps.
        /// </summary>
        public static void RegisterFullMapRegionGeneration(string modId, double order,
            MapRegionGenerationHook hook)
            => GetMapsHost().RegisterFullRegionGeneration(modId, order, hook);

        /// <summary>Get the assembled generator for a map stage after initialization.</summary>
        public static MapLayerBase GetMapGenerator(MapGeneratorStep step)
            => GetMapsHost().GetMapGenerator(step);

        /// <summary>
        /// Force a landform at a world position with a radius. Applied during region generation.
        /// </summary>
        public static void ForceLandformAt(ForceLandform landform)
            => GetMapsHost().ForceLandformAt(landform);

        /// <summary>
        /// Force a climate override at a world position with a radius.
        /// </summary>
        public static void ForceClimateAt(ForceClimate climate)
            => GetMapsHost().ForceClimateAt(climate);

        /// <summary>
        /// Require a map coordinate to be generated as land. Calls made before
        /// worldgen initialization are queued by the canonical map host.
        /// </summary>
        public static void RequireLandAt(int mapX, int mapZ)
            => GetMapsHost().RequireLandAt(mapX, mapZ);

        /// <summary>
        /// Force a natural-looking land area around a world position. This is
        /// the public equivalent of GenMaps.ForceRandomLandArea.
        /// </summary>
        public static void ForceRandomLandArea(int positionX, int positionZ, int radius)
            => GetMapsHost().ForceRandomLandArea(positionX, positionZ, radius);

        // ════════════════════════════════════════════════════════════════
        //  GenBlockLayersHost hook registration
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Register a hook that modifies terrain raise per-column.
        /// Called during BlockLayers.OnChunkColumnGeneration after vanilla raise computation.
        /// Return the modified raise value.
        /// </summary>
        /// <param name="modId">Your mod's identifier.</param>
        /// <param name="order">Execution order within the step.</param>
        /// <param name="modifier">Delegate: (localX, localZ, currentRaise, mapChunk) → modifiedRaise.</param>
        public static void RegisterBlockLayersRaiseModifier(string modId, double order,
            System.Func<int, int, float, IMapChunk, float> modifier)
            => GetBlockLayersHost().RegisterRaiseModifier(modId, order, modifier);

        /// <summary>
        /// Register a hook that can disable sea-level-rise for a column.
        /// Return true to keep the raise, false to zero it.
        /// </summary>
        /// <param name="modId">Your mod's identifier.</param>
        /// <param name="order">Execution order within the step.</param>
        /// <param name="filter">Delegate: (localX, localZ, mapChunk) → true to keep raise.</param>
        public static void RegisterBlockLayersSeaLevelFilter(string modId, double order,
            System.Func<int, int, IMapChunk, bool> filter)
            => GetBlockLayersHost().RegisterSeaLevelRiseFilter(modId, order, filter);

        /// <summary>
        /// Register a terminal BlockLayers adapter. Return true only after the
        /// complete pass has been generated by the consumer.
        /// </summary>
        public static void RegisterFullBlockLayersGeneration(string modId, double order,
            BlockLayersGenerationHook hook)
            => GetBlockLayersHost().RegisterFullGeneration(modId, order, hook);

        /// <summary>
        /// Register a terminal terrain-column adapter for a full replacement
        /// that cannot be expressed through the atomic hooks.
        /// </summary>
        public static void RegisterFullTerrainGeneration(string modId, double order,
            TerrainGenerationHook hook)
            => GetHost().RegisterFullGeneration(modId, order, hook);

        // ════════════════════════════════════════════════════════════════
        //  Noise Access
        // ════════════════════════════════════════════════════════════════

        /// <summary>Terrain noise instance. Available after initWorldGen.</summary>
        public static NewNormalizedSimplexFractalNoise TerrainNoise => GetHost().TerrainNoise;

        /// <summary>Distortion noise for XZ warp.</summary>
        public static SimplexNoise Distort2dX => GetHost().Distort2dX;

        /// <summary>Distortion noise for Z warp.</summary>
        public static SimplexNoise Distort2dZ => GetHost().Distort2dZ;

        /// <summary>Geo upheaval noise for large-scale lifts.</summary>
        public static NormalizedSimplexNoise GeoUpheavalNoise => GetHost().GeoUpheavalNoise;

        /// <summary>Noise scale factor.</summary>
        public static float NoiseScale => GetHost().NoiseScale;

        /// <summary>Number of terrain noise octaves.</summary>
        public static int TerrainGenOctaves => GetHost().TerrainGenOctaves;

        // ════════════════════════════════════════════════════════════════
        //  Query API
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get a diagnostic report of all registered hooks across all steps.
        /// Useful for startup logging and debugging.
        /// </summary>
        public static IReadOnlyList<(string Step, double Order, string ModId)> GetHookReport()
        {
            var report = new List<(string, double, string)>();
            report.AddRange(GetHost().GetHookReport());
            report.AddRange(GetMapsHost().GetHookReport());
            if (_modInstance?.GenTerraPostProcessHost != null)
                report.AddRange(_modInstance.GenTerraPostProcessHost.GetHookReport());
            report.AddRange(GetBlockLayersHost().GetHookReport());
            return report;
        }

        /// <summary>
        /// Get the step registry with all 25 registered step IDs.
        /// </summary>
        public static StepRegistry GetStepRegistry() => _stepRegistry;

        // ════════════════════════════════════════════════════════════════
        //  Internal helpers
        // ════════════════════════════════════════════════════════════════

        private static GenTerraHost GetHost()
        {
            if (_modInstance?.GenTerraHost == null)
                throw new InvalidOperationException(
                    "[WorldgenLib] WorldgenLib is not initialized. Register hooks during StartServerSide, " +
                    "after WorldgenLibMod has loaded.");
            return _modInstance.GenTerraHost;
        }

        private static GenTerraPostProcessHost GetPostProcessHost()
        {
            if (_modInstance?.GenTerraPostProcessHost == null)
                throw new InvalidOperationException(
                    "[WorldgenLib] WorldgenLib is not initialized. Register hooks during StartServerSide.");
            return _modInstance.GenTerraPostProcessHost;
        }

        private static GenMapsHost GetMapsHost()
        {
            if (_modInstance?.GenMapsHost == null)
                throw new InvalidOperationException(
                    "[WorldgenLib] WorldgenLib is not initialized. Call during StartServerSide.");
            return _modInstance.GenMapsHost;
        }

        private static GenBlockLayersHost GetBlockLayersHost()
        {
            if (_modInstance?.GenBlockLayersHost == null)
                throw new InvalidOperationException(
                    "[WorldgenLib] WorldgenLib is not initialized. Call during StartServerSide.");
            return _modInstance.GenBlockLayersHost;
        }

        internal static GenBlockLayersHost? TryGetBlockLayersHost()
            => _modInstance?.GenBlockLayersHost;

        internal static void Reset()
        {
            _modInstance = null;
            _stepRegistry = new StepRegistry();
        }
    }
}
