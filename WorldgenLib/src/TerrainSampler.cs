using System;
using System.Collections.Generic;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace WorldgenLib
{
    /// <summary>
    /// Samples the terrain pipeline at arbitrary world positions without placing blocks.
    /// Replaces the GenTerraSampler fork that Watersheds currently maintains.
    ///
    /// Thread-safe: uses thread-local temp data internally.
    ///
    /// Usage:
    /// <code>
    /// int height = TerrainSampler.SampleHeight(worldX, worldZ);
    /// double threshold = TerrainSampler.SampleThreshold(worldX, worldZ, posY);
    /// </code>
    /// </summary>
    public static class TerrainSampler
    {
        private static WorldgenLibMod? _mod;

        /// <summary>Initialize the sampler. Called internally by WorldgenLibMod.</summary>
        internal static void Initialize(WorldgenLibMod mod)
        {
            _mod = mod;
        }

        internal static void Reset() => _mod = null;

        /// <summary>
        /// Sample the terrain height at an arbitrary world position.
        /// Uses the same noise stack as GenTerra but does not place blocks.
        /// </summary>
        /// <param name="worldX">World X coordinate.</param>
        /// <param name="worldZ">World Z coordinate.</param>
        /// <param name="modifiers">Optional caller-supplied transforms.</param>
        /// <returns>Estimated terrain Y at this position.</returns>
        public static int SampleHeight(int worldX, int worldZ, SamplingModifiers? modifiers = null)
        {
            var host = _mod?.GenTerraHost;
            if (host == null)
                throw new InvalidOperationException(
                    "[WorldgenLib] TerrainSampler is not initialized. WorldgenLib must be loaded.");

            return host.SampleHeight(worldX, worldZ, modifiers);
        }

        /// <summary>
        /// Compatibility name used by Watersheds' GenTerraSampler. This is
        /// the base canonical terrain sample; callers can pass modifiers when
        /// they need to apply a consumer-owned river or erosion transform.
        /// </summary>
        public static int SampleTerrainHeight(int worldX, int worldZ,
            SamplingModifiers? modifiers = null)
            => SampleHeight(worldX, worldZ, modifiers);

        /// <summary>
        /// Sample many world positions using the same canonical terrain
        /// pipeline. The result is keyed by the original world coordinate,
        /// matching the batch shape used by Watersheds' slope samplers.
        /// </summary>
        public static Dictionary<(int worldX, int worldZ), int> SampleTerrainHeightsBatch(
            IEnumerable<(int worldX, int worldZ)> worldCoordinates,
            bool ignoreRivers = false)
        {
            if (worldCoordinates == null)
                throw new ArgumentNullException(nameof(worldCoordinates));

            var host = _mod?.GenTerraHost;
            if (host == null)
                throw new InvalidOperationException(
                    "[WorldgenLib] TerrainSampler is not initialized. WorldgenLib must be loaded.");

            // WorldgenLib owns no built-in river field. Keep the compatibility
            // flag explicit so a future registered river provider can honor
            // it without changing consumer call sites; the current canonical
            // base sample is already river-neutral.
            return host.SampleHeightsBatch(worldCoordinates, ignoreRivers);
        }

        /// <summary>
        /// Explicit name for the unmodified canonical sample used as the
        /// pre-consumer baseline by slope/erosion algorithms.
        /// </summary>
        public static int SampleBaseTerrainHeight(int worldX, int worldZ)
            => SampleHeight(worldX, worldZ);

        /// <summary>Batch form of <see cref="SampleBaseTerrainHeight"/>.</summary>
        public static Dictionary<(int worldX, int worldZ), int> SampleBaseTerrainHeightsBatch(
            IEnumerable<(int worldX, int worldZ)> worldCoordinates)
            => SampleTerrainHeightsBatch(worldCoordinates, ignoreRivers: true);

        /// <summary>
        /// Sample the terrain threshold at a specific Y position.
        /// Returns the noise threshold value — below this Y is solid, above is air.
        /// </summary>
        /// <param name="worldX">World X coordinate.</param>
        /// <param name="worldZ">World Z coordinate.</param>
        /// <param name="posY">Y position to sample.</param>
        /// <returns>The threshold value at this position.</returns>
        public static double SampleThreshold(int worldX, int worldZ, int posY,
            SamplingModifiers? modifiers = null)
        {
            var host = _mod?.GenTerraHost;
            if (host == null)
                throw new InvalidOperationException(
                    "[WorldgenLib] TerrainSampler is not initialized. WorldgenLib must be loaded.");

            return host.SampleThreshold(worldX, worldZ, posY, modifiers);
        }

        /// <summary>Invalidate a cached landform interpolation after map mutation.</summary>
        public static void InvalidateRegion(int regionX, int regionZ)
        {
            var host = _mod?.GenTerraHost;
            if (host == null)
                throw new InvalidOperationException(
                    "[WorldgenLib] TerrainSampler is not initialized. WorldgenLib must be loaded.");
            host.InvalidateLandformRegion(regionX, regionZ);
        }
    }

    /// <summary>
    /// Optional modifiers for terrain sampling. Apply transformations to the
    /// noise pipeline at arbitrary positions.
    /// </summary>
    public sealed class SamplingModifiers
    {
        /// <summary>Apply distY delta (for coastal erosion, tectonics).</summary>
        public float DistYDelta { get; set; }

        /// <summary>
        /// Modify the interpolated landform weights before terrain octaves are
        /// built. The array is local to this sample and may be edited in place.
        /// </summary>
        public Action<float[]>? LandformWeightTransform { get; set; }

        /// <summary>Apply threshold delta at a specific Y. Return modified threshold.</summary>
        public Func<int, double, double>? ThresholdTransform { get; set; }
    }
}
