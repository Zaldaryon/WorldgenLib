using System;
using System.Collections;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace WorldgenLib
{
    // ════════════════════════════════════════════════════════════════════
    //  ChunkContext — per-chunk state, passed to steps 0, 1, 10
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-chunk context passed to hooks at steps 0, 1, and 10.
    /// Contains region map samples, config values, and mutable taper map.
    /// </summary>
    public sealed class ChunkContext
    {
        // ── Read-only: chunk coordinates ──

        public int ChunkX { get; }
        public int ChunkZ { get; }
        public int RegionX { get; internal set; }
        public int RegionZ { get; internal set; }

        // ── Read-only: region map corner samples ──

        public int ClimateUpLeft { get; }
        public int ClimateUpRight { get; }
        public int ClimateBotLeft { get; }
        public int ClimateBotRight { get; }

        public int OceanUpLeft { get; }
        public int OceanUpRight { get; }
        public int OceanBotLeft { get; }
        public int OceanBotRight { get; }

        public int UpheavalUpLeft { get; }
        public int UpheavalUpRight { get; }
        public int UpheavalBotLeft { get; }
        public int UpheavalBotRight { get; }

        // ── Read-only: config ──

        public int SeaLevel { get; }
        public int MapSizeY { get; }
        public float OceanicityFactor { get; }
        public int TaperThreshold { get; }
        public double GeoUpheavalAmplitude { get; }

        // ── Read-only: resolved worldgen block IDs ──

        /// <summary>Resolved rock block ID used by the terrain pass.</summary>
        public int RockBlockId { get; }

        /// <summary>Resolved fresh-water block ID.</summary>
        public int FreshWaterBlockId { get; }

        /// <summary>Resolved salt-water block ID.</summary>
        public int SaltWaterBlockId { get; }

        /// <summary>Resolved lake-ice block ID.</summary>
        public int LakeIceBlockId { get; }

        // ── Mutable: border taper (step 0 writes, step 7 reads) ──

        public GenTerraHost.WeightedTaper[] TaperMap { get; set; }

        /// <summary>
        /// Per-chunk state shared by hooks. Keys must be namespaced by the
        /// owning mod (for example, <c>strata:elevation</c>).
        /// </summary>
        public IDictionary<string, object?> CustomData { get; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);

        // ── Read-only: noise instances ──

        public SimplexNoise Distort2dX { get; }
        public SimplexNoise Distort2dZ { get; internal set; } = null!;
        public NewNormalizedSimplexFractalNoise TerrainNoise { get; internal set; } = null!;
        public NormalizedSimplexNoise GeoUpheavalNoise { get; internal set; } = null!;

        // ── Landform data ──

        public float[][] TerrainYThresholds { get; }
        public LandformsWorldProperty Landforms { get; }
        public LerpedWeightedIndex2DMap LandLerpMap { get; }
        public IntDataMap2D LandformMap { get; internal set; } = null!;
        public int RegionMapSize { get; internal set; }
        public int ChunkSize => GlobalConstants.ChunkSize;
        public float ChunkPixelSize { get; }
        public float BaseX { get; }
        public float BaseZ { get; }

        /// <summary>Vertical chunks produced for this terrain request.</summary>
        public IServerChunk[] Chunks { get; }

        /// <summary>Map chunk produced for this terrain request.</summary>
        public IMapChunk MapChunk { get; }

        /// <summary>Region containing the maps used by this terrain request.</summary>
        public IMapRegion MapRegion => MapChunk.MapRegion;

        /// <summary>The original request, available to a finalization hook.</summary>
        public IChunkColumnGenerateRequest Request { get; }

        private readonly object _customDataSync = new();

        /// <summary>
        /// Creates a ChunkContext with all region-level data needed by hooks.
        /// Parameters are grouped: coordinates, region map corners, config, noise, landforms.
        /// </summary>
        public ChunkContext(
            int chunkX, int chunkZ,
            int climateUpLeft, int climateUpRight, int climateBotLeft, int climateBotRight,
            int oceanUpLeft, int oceanUpRight, int oceanBotLeft, int oceanBotRight,
            int upheavalUpLeft, int upheavalUpRight, int upheavalBotLeft, int upheavalBotRight,
            int seaLevel, int mapSizeY, float oceanicityFactor, int taperThreshold,
            double geoUpheavalAmplitude,
            GenTerraHost.WeightedTaper[] taperMap,
            SimplexNoise distort2dX,
            float[][] terrainYThresholds, LandformsWorldProperty landforms,
            LerpedWeightedIndex2DMap landLerpMap,
            float chunkPixelSize, float baseX, float baseZ,
            IServerChunk[]? chunks = null, IMapChunk? mapChunk = null,
            IChunkColumnGenerateRequest? request = null,
            int rockBlockId = 0, int freshWaterBlockId = 0,
            int saltWaterBlockId = 0, int lakeIceBlockId = 0)
        {
            ChunkX = chunkX; ChunkZ = chunkZ;
            ClimateUpLeft = climateUpLeft; ClimateUpRight = climateUpRight;
            ClimateBotLeft = climateBotLeft; ClimateBotRight = climateBotRight;
            OceanUpLeft = oceanUpLeft; OceanUpRight = oceanUpRight;
            OceanBotLeft = oceanBotLeft; OceanBotRight = oceanBotRight;
            UpheavalUpLeft = upheavalUpLeft; UpheavalUpRight = upheavalUpRight;
            UpheavalBotLeft = upheavalBotLeft; UpheavalBotRight = upheavalBotRight;
            SeaLevel = seaLevel; MapSizeY = mapSizeY;
            OceanicityFactor = oceanicityFactor; TaperThreshold = taperThreshold;
            GeoUpheavalAmplitude = geoUpheavalAmplitude;
            TaperMap = taperMap; Distort2dX = distort2dX;
            TerrainYThresholds = terrainYThresholds; Landforms = landforms;
            LandLerpMap = landLerpMap;
            ChunkPixelSize = chunkPixelSize; BaseX = baseX; BaseZ = baseZ;
            RockBlockId = rockBlockId; FreshWaterBlockId = freshWaterBlockId;
            SaltWaterBlockId = saltWaterBlockId; LakeIceBlockId = lakeIceBlockId;
            Chunks = chunks ?? Array.Empty<IServerChunk>();
            MapChunk = mapChunk!;
            Request = request!;
        }

        /// <summary>
        /// Gets a typed per-request value, creating it once if absent. The
        /// collection is safe to initialize from parallel column hooks; the
        /// value itself remains owned by the consumer and must be partitioned
        /// or synchronized by that consumer.
        /// </summary>
        public T GetOrCreateCustomData<T>(string key, Func<T> factory)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A custom-data key is required.", nameof(key));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            lock (_customDataSync)
            {
                if (CustomData.TryGetValue(key, out object? existing))
                {
                    if (existing is T typed) return typed;
                    throw new InvalidOperationException(
                        $"Custom-data key '{key}' is already owned by a different type.");
                }

                T created = factory();
                CustomData.Add(key, created);
                return created;
            }
        }

        /// <summary>Try to read typed per-request state without creating it.</summary>
        public bool TryGetCustomData<T>(string key, out T? value)
        {
            lock (_customDataSync)
            {
                if (CustomData.TryGetValue(key, out object? existing) && existing is T typed)
                {
                    value = typed;
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>Write generic moddata to the first generated chunk.</summary>
        public void SetChunkModdata<T>(string key, T data)
        {
            if (Chunks.Length == 0 || Chunks[0] == null)
                throw new InvalidOperationException("No generated chunk is available in this context.");
            Chunks[0].SetModdata(key, data);
        }

        /// <summary>Write generic moddata to the generated map chunk.</summary>
        public void SetMapChunkModdata<T>(string key, T data)
        {
            if (MapChunk == null)
                throw new InvalidOperationException("No generated map chunk is available in this context.");
            MapChunk.SetModdata(key, data);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  ColumnContext — per-column state inside Parallel.For (steps 2–7)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-column context inside the Parallel.For loop.
    /// Mutable fields are applied by the host after each hook returns.
    /// </summary>
    public ref struct ColumnContext
    {
        // ── Read-only: world position ──

        public int WorldX { get; }
        public int WorldZ { get; }
        public int LocalX { get; }
        public int LocalZ { get; }

        // ── Read-only: landform weights (step 2, set by host) ──

        private Span<float> _landformWeights;

        /// <summary>Per-landform weights. The span is mutable by hooks.</summary>
        public Span<float> LandformWeights => _landformWeights;

        // ── Read-only: octave amplitudes/thresholds (step 2, set by host) ──

        private Span<double> _octaveAmplitudes;
        private Span<double> _octaveThresholds;
        private readonly LandformsWorldProperty _landforms;

        /// <summary>Interpolated octave amplitudes, mutable by step 2 hooks.</summary>
        public Span<double> OctaveAmplitudes => _octaveAmplitudes;

        /// <summary>Interpolated octave thresholds, mutable by step 2 hooks.</summary>
        public Span<double> OctaveThresholds => _octaveThresholds;

        /// <summary>
        /// Rebuild the octave arrays from the current landform weights. This
        /// is the safe way for a Step 2 hook to blend in a custom variant
        /// (for example a river landform) after changing
        /// <see cref="LandformWeights"/>.
        /// </summary>
        public void RecalculateOctaves()
        {
            _octaveAmplitudes.Clear();
            _octaveThresholds.Clear();

            int count = Math.Min(_landformWeights.Length, _landforms.LandFormsByIndex.Length);
            for (int landformIndex = 0; landformIndex < count; landformIndex++)
            {
                float weight = _landformWeights[landformIndex];
                if (weight == 0) continue;

                LandformVariant landform = _landforms.LandFormsByIndex[landformIndex];
                int octaveCount = Math.Min(_octaveAmplitudes.Length, landform.TerrainOctaves.Length);
                for (int octave = 0; octave < octaveCount; octave++)
                {
                    _octaveAmplitudes[octave] += landform.TerrainOctaves[octave] * weight;
                    _octaveThresholds[octave] += landform.TerrainOctaveThresholds[octave] * weight;
                }
            }
        }

        // ── Read-only: column-level derived values (step 4, set by host) ──

        public float UpheavalStrength { get; internal set; }
        public float Oceanicity { get; internal set; }

        // ── Mutable: vertical distortion (step 4, modifiable by hooks) ──

        public float DistY { get; set; }

        // ── Mutable: water type (step 5, modifiable by hooks) ──

        public int WaterBlockId { get; set; }

        // ── Read-only: noise bounds (step 6, set by host) ──

        public double NoiseBoundMin { get; internal set; }
        public double NoiseBoundMax { get; internal set; }

        // ── Read-only: column noise (step 6, set by host) ──

        public NewNormalizedSimplexFractalNoise.ColumnNoise ColumnNoise { get; internal set; }

        // ── Read-only: block solidity (step 7, set by host) ──

        public BitArray ColumnBlockSolidities { get; internal set; }

        public ColumnContext(int worldX, int worldZ, int localX, int localZ,
            Span<float> landformWeights, Span<double> octaveAmplitudes,
            Span<double> octaveThresholds, BitArray columnBlockSolidities,
            LandformsWorldProperty landforms)
        {
            WorldX = worldX; WorldZ = worldZ;
            LocalX = localX; LocalZ = localZ;
            _landformWeights = landformWeights;
            _octaveAmplitudes = octaveAmplitudes;
            _octaveThresholds = octaveThresholds;
            ColumnBlockSolidities = columnBlockSolidities;
            _landforms = landforms ?? throw new ArgumentNullException(nameof(landforms));
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  ColumnCarvingContext — step 10 (post-placement) state
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-column context for step 10 hooks. Full block accessor available.
    /// </summary>
    public ref struct ColumnCarvingContext
    {
        private readonly IChunkBlocks _blockData;

        public int WorldX { get; }
        public int WorldZ { get; }
        public int LocalX { get; }
        public int LocalZ { get; }
        public int SeaLevel { get; }
        public int MapSizeY { get; }
        public int WaterBlockId { get; }

        /// <summary>All vertical chunks in this generated column.</summary>
        public IServerChunk[] Chunks { get; }

        /// <summary>The map chunk associated with this column.</summary>
        public IMapChunk MapChunk { get; }

        /// <summary>
        /// Read/write access to the bottom vertical chunk for compatibility.
        /// For higher Y values, use <see cref="GetBlockDataAtY"/> and pass a
        /// chunk-local Y to <see cref="ChunkIndex3d"/>.
        /// </summary>
        public IChunkBlocks BlockData => _blockData;

        /// <summary>Terrain heightmap. Mutable.</summary>
        public ushort[] TerrainHeightMap { get; internal set; } = Array.Empty<ushort>();

        /// <summary>Rain heightmap. Mutable.</summary>
        public ushort[] RainHeightMap { get; internal set; } = Array.Empty<ushort>();

        /// <summary>Column solidity results from steps 7–9.</summary>
        public BitArray ColumnBlockSolidities { get; internal set; } = new BitArray(0);

        /// <summary>Build an index from local X/Y/Z coordinates in one vertical chunk.</summary>
        public int ChunkIndex3d(int x, int y, int z)
        {
            return (y * GlobalConstants.ChunkSize + z) * GlobalConstants.ChunkSize + x;
        }

        /// <summary>Get the chunk storage containing a global Y coordinate.</summary>
        public IChunkBlocks GetBlockDataAtY(int y)
        {
            if (y < 0 || y >= MapSizeY)
                throw new ArgumentOutOfRangeException(nameof(y));
            return Chunks[y / GlobalConstants.ChunkSize].Data;
        }

        /// <summary>Set a fluid block using local X/Z and global Y.</summary>
        public void SetFluid(int x, int y, int z, int blockId)
        {
            if ((uint)x >= GlobalConstants.ChunkSize)
                throw new ArgumentOutOfRangeException(nameof(x),
                    "Fluid placement expects local X/Z coordinates within the chunk.");
            if ((uint)z >= GlobalConstants.ChunkSize)
                throw new ArgumentOutOfRangeException(nameof(z),
                    "Fluid placement expects local X/Z coordinates within the chunk.");
            GetBlockDataAtY(y).SetFluid(
                ChunkIndex3d(x, y % GlobalConstants.ChunkSize, z), blockId);
        }

        /// <summary>Write permanently stored data to the generated chunk column.</summary>
        public void SetChunkModdata(string key, byte[] data)
        {
            if (Chunks.Length == 0 || Chunks[0] == null)
                throw new InvalidOperationException("No generated chunk is available in this context.");
            Chunks[0].SetModdata(key, data);
        }

        /// <summary>Write generic moddata to the generated chunk column.</summary>
        public void SetChunkModdata<T>(string key, T data)
        {
            if (Chunks.Length == 0 || Chunks[0] == null)
                throw new InvalidOperationException("No generated chunk is available in this context.");
            Chunks[0].SetModdata(key, data);
        }

        /// <summary>Write permanently stored data to the generated map chunk.</summary>
        public void SetMapChunkModdata(string key, byte[] data)
        {
            if (MapChunk == null)
                throw new InvalidOperationException("No generated map chunk is available in this context.");
            MapChunk.SetModdata(key, data);
        }

        /// <summary>Write generic moddata to the generated map chunk.</summary>
        public void SetMapChunkModdata<T>(string key, T data)
        {
            if (MapChunk == null)
                throw new InvalidOperationException("No generated map chunk is available in this context.");
            MapChunk.SetModdata(key, data);
        }

        public ColumnCarvingContext(int worldX, int worldZ, int seaLevel, int mapSizeY, int waterBlockId)
        {
            WorldX = worldX; WorldZ = worldZ;
            LocalX = FloorMod(worldX, GlobalConstants.ChunkSize);
            LocalZ = FloorMod(worldZ, GlobalConstants.ChunkSize);
            SeaLevel = seaLevel; MapSizeY = mapSizeY;
            WaterBlockId = waterBlockId;
            Chunks = Array.Empty<IServerChunk>();
            MapChunk = null!;
            _blockData = null!;
        }

        internal ColumnCarvingContext(int worldX, int worldZ, int seaLevel, int mapSizeY,
            int waterBlockId, IServerChunk[] chunks, IMapChunk mapChunk,
            ushort[] terrainHeightMap, ushort[] rainHeightMap, BitArray solidities)
        {
            WorldX = worldX; WorldZ = worldZ;
            LocalX = FloorMod(worldX, GlobalConstants.ChunkSize);
            LocalZ = FloorMod(worldZ, GlobalConstants.ChunkSize);
            SeaLevel = seaLevel; MapSizeY = mapSizeY;
            WaterBlockId = waterBlockId;
            Chunks = chunks;
            MapChunk = mapChunk;
            _blockData = chunks[0].Data;
            TerrainHeightMap = terrainHeightMap;
            RainHeightMap = rainHeightMap;
            ColumnBlockSolidities = solidities;
        }

        private static int FloorMod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Delegate types for hook registration
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Hook delegate for step 0 (BorderTaperPrepare) — per-chunk.</summary>
    public delegate void BorderTaperHook(ChunkContext ctx);

    /// <summary>
    /// Terminal terrain hook. Return true when the consumer generated the
    /// complete column and WorldgenLib must skip its vanilla-compatible pass.
    /// Returning false leaves the pass to the next handler or to WorldgenLib.
    /// </summary>
    public delegate bool TerrainGenerationHook(IChunkColumnGenerateRequest request);

    /// <summary>Hook delegate for step 2 (BuildCornerOctaves) — per-column.</summary>
    public delegate void BuildOctavesHook(ChunkContext chunk, ref ColumnContext col);

    /// <summary>Hook delegate for step 4 (VerticalDistortion) — per-column.</summary>
    public delegate void VerticalDistortionHook(ChunkContext chunk, ref ColumnContext col);

    /// <summary>Hook delegate for step 5 (WaterColumnSelect) — per-column.</summary>
    public delegate void WaterSelectHook(ChunkContext chunk, ref ColumnContext col);

    /// <summary>
    /// Hook delegate for step 7 (PerVoxelThreshold) — per-column, per-Y.
    /// Returns the modified threshold.
    /// </summary>
    public delegate double ThresholdHook(ChunkContext chunk, ref ColumnContext col, int posY, double threshold);

    /// <summary>Hook delegate for step 10 (PostPlacementColumn) — per-column.</summary>
    public delegate void PostPlacementHook(ChunkContext chunk, ref ColumnCarvingContext col);

    /// <summary>
    /// Hook after all columns in a terrain request have completed placement and
    /// Step 10. This is the safe boundary for chunk-wide arrays such as river
    /// flow vectors and map data that must be written exactly once per request.
    /// </summary>
    public delegate void TerrainFinalizeHook(ChunkContext chunk);

    // ════════════════════════════════════════════════════════════════════
    //  RegionContext — per-region (GenMaps) state
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-region context passed to GenMaps hooks at steps 1–9 and RegionFinalize.
    /// Contains the map region, noise sizes, and mutable map data.
    /// </summary>
    public sealed class RegionContext
    {
        public int RegionX { get; }
        public int RegionZ { get; }
        public IMapRegion MapRegion { get; }
        public ICoreServerAPI? ServerApi { get; }

        /// <summary>Parameters supplied by the worldgen request, when any.</summary>
        public ITreeAttribute? ChunkGenParams { get; internal set; }

        /// <summary>
        /// Map produced by the current step. A hook may replace this
        /// reference; the host writes it back to the corresponding region
        /// property after the chain completes.
        /// </summary>
        public IntDataMap2D? CurrentMap { get; set; }

        private readonly System.Func<MapGeneratorStep, MapLayerBase>? _getMapGenerator;

        private readonly Action<ForceLandform>? _forceLandform;
        private readonly Action<ForceClimate>? _forceClimate;
        private readonly Action<int, int>? _requireLand;

        // ── Read-only: noise sizes ──
        public int NoiseSizeGeoProv { get; }
        public int NoiseSizeClimate { get; }
        public int NoiseSizeForest { get; }
        public int NoiseSizeUpheavel { get; }
        public int NoiseSizeOcean { get; }
        public int NoiseSizeBeach { get; }
        public int NoiseSizeShrubs { get; }
        public int NoiseSizeLandform { get; }

        public RegionContext(int regionX, int regionZ, IMapRegion mapRegion,
            int noiseSizeGeoProv, int noiseSizeClimate, int noiseSizeForest,
            int noiseSizeUpheavel, int noiseSizeOcean, int noiseSizeBeach,
            int noiseSizeShrubs, int noiseSizeLandform,
            Action<ForceLandform>? forceLandform = null,
            Action<ForceClimate>? forceClimate = null,
            Action<int, int>? requireLand = null,
            ICoreServerAPI? serverApi = null,
            System.Func<MapGeneratorStep, MapLayerBase>? getMapGenerator = null)
        {
            RegionX = regionX; RegionZ = regionZ; MapRegion = mapRegion;
            ServerApi = serverApi;
            NoiseSizeGeoProv = noiseSizeGeoProv; NoiseSizeClimate = noiseSizeClimate;
            NoiseSizeForest = noiseSizeForest; NoiseSizeUpheavel = noiseSizeUpheavel;
            NoiseSizeOcean = noiseSizeOcean; NoiseSizeBeach = noiseSizeBeach;
            NoiseSizeShrubs = noiseSizeShrubs; NoiseSizeLandform = noiseSizeLandform;
            _forceLandform = forceLandform;
            _forceClimate = forceClimate;
            _requireLand = requireLand;
            _getMapGenerator = getMapGenerator;
        }

        /// <summary>Queue a forced landform.</summary>
        public void ForceLandformAt(ForceLandform landform)
            => (_forceLandform ?? throw new InvalidOperationException(
                "ForceLandformAt is unavailable in this context."))(landform);

        /// <summary>Queue a forced climate value.</summary>
        public void ForceClimateAt(ForceClimate climate)
            => (_forceClimate ?? throw new InvalidOperationException(
                "ForceClimateAt is unavailable in this context."))(climate);

        /// <summary>Require land at an ocean map coordinate.</summary>
        public void RequireLandAt(int x, int z)
            => (_requireLand ?? throw new InvalidOperationException(
                "RequireLandAt is unavailable in this context."))(x, z);

        /// <summary>Get a persistent custom map registered for this region.</summary>
        public IntDataMap2D GetMap(RegionMapSlot slot)
            => slot.GetMap(MapRegion, RegionX, RegionZ);

        /// <summary>
        /// Gets the generator currently assembled for a map stage. This is
        /// available to a full-region adapter after worldgen initialization.
        /// </summary>
        public MapLayerBase GetMapGenerator(MapGeneratorStep step)
            => (_getMapGenerator ?? throw new InvalidOperationException(
                "GetMapGenerator is unavailable in this context."))(step);
    }

    // ════════════════════════════════════════════════════════════════════
    //  GenMaps hook delegate types
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Hook delegate for GenMaps map steps — per-region.</summary>
    public delegate void MapsStepHook(RegionContext ctx);

    /// <summary>Hook delegate for GenMaps RegionFinalize — after all maps, before DirtyForSaving.</summary>
    public delegate void RegionFinalizeHook(RegionContext ctx);
}
