using System;
using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace WorldgenLib
{
    /// <summary>Map generator seams exposed by the vanilla GenMaps factory chain.</summary>
    public enum MapGeneratorStep
    {
        Geoprovince,
        Climate,
        Forest,
        Upheavel,
        Ocean,
        Beach,
        Shrub,
        Biome,
        Landform
    }

    /// <summary>
    /// Immutable factory information plus the current vanilla generator.
    /// A map-generator hook may return the current generator, wrap it, or
    /// replace it with a compatible generator.
    /// </summary>
    public sealed class MapGeneratorContext
    {
        public MapGeneratorStep Step { get; }
        public long Seed { get; }
        public ICoreServerAPI ServerApi { get; }
        public int MapScale { get; }
        public double Landcover { get; }
        public double OceanScale { get; }
        public double LandformScale { get; }
        public bool RequiresSpawnOffset { get; }
        public IReadOnlyList<XZ> RequiredLand { get; }
        public NoiseClimate? ClimateNoise { get; }

        internal MapGeneratorContext(MapGeneratorStep step, long seed,
            ICoreServerAPI serverApi, int mapScale, double landcover,
            double oceanScale, double landformScale, bool requiresSpawnOffset,
            IReadOnlyList<XZ> requiredLand, NoiseClimate? climateNoise)
        {
            Step = step;
            Seed = seed;
            ServerApi = serverApi;
            MapScale = mapScale;
            Landcover = landcover;
            OceanScale = oceanScale;
            LandformScale = landformScale;
            RequiresSpawnOffset = requiresSpawnOffset;
            RequiredLand = requiredLand;
            ClimateNoise = climateNoise;
        }
    }

    /// <summary>Ordered seam around one of the vanilla GenMaps factories.</summary>
    public delegate MapLayerBase MapGeneratorHook(MapGeneratorContext context, MapLayerBase current);

    /// <summary>
    /// Adjusts the padding requested for one vanilla map stage. This is a
    /// lifecycle-safe replacement for transpilers that only changed a local
    /// padding constant in GenMaps.OnMapRegionGen.
    /// </summary>
    public delegate int MapPaddingHook(MapGeneratorStep step, int vanillaPadding);

    /// <summary>
    /// Terminal map-region hook. Return true after generating the complete
    /// region to suppress WorldgenLib's default nine-step pass.
    /// </summary>
    public delegate bool MapRegionGenerationHook(RegionContext context);
}
