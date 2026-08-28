using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace WorldgenLib
{
    /// <summary>
    /// Faithful reimplementation of vanilla GenTerra as GenTerraHost.
    /// The generate method is split into 11 named steps (0–10), each a
    /// faithful move of the vanilla code with no logic change.
    ///
    /// Phase 3: no hooks invoked. Parity with vanilla when empty.
    /// Phase 4: hook points wired at each step.
    /// </summary>
    public sealed class GenTerraHost
    {
        // ════════════════════════════════════════════════════════════════
        //  Hook lists — one per step. Frozen after InitWorldGen.
        // ════════════════════════════════════════════════════════════════

        private readonly OrderedHookList<BorderTaperHook> _step0Hooks = new();
        private readonly OrderedHookList<BuildOctavesHook> _step2Hooks = new();
        private readonly OrderedHookList<VerticalDistortionHook> _step4Hooks = new();
        private readonly OrderedHookList<WaterSelectHook> _step5Hooks = new();
        private readonly OrderedHookList<ThresholdHook> _step7Hooks = new();
        private readonly OrderedHookList<PostPlacementHook> _step10Hooks = new();
        private readonly OrderedHookList<TerrainFinalizeHook> _terrainFinalizeHooks = new();
        private readonly OrderedHookList<TerrainGenerationHook> _fullGenerationHooks = new();
        private readonly ICoreServerAPI _api;

        // ── Noise instances (exposed via TerrainSampler/WorldgenLibAPI) ──
        internal NewNormalizedSimplexFractalNoise TerrainNoise => _terrainNoise;
        internal SimplexNoise Distort2dX => _distort2dx;
        internal SimplexNoise Distort2dZ => _distort2dz;
        internal NormalizedSimplexNoise GeoUpheavalNoise => _geoUpheavalNoise;
        internal float NoiseScale => _noiseScale;
        internal int TerrainGenOctaves => _terrainGenOctaves;
        internal LandformsWorldProperty Landforms => _landforms;

        // ── Constants (L20–25) ──

        private const double TerrainDistortionMultiplier = 4.0;
        private const double TerrainDistortionThreshold = 40.0;
        private const double GeoDistortionMultiplier = 10.0;
        private const double GeoDistortionThreshold = 10.0;
        private const double MaxDistortionAmount = (55 + 40 + 30 + 10) * SimplexNoiseOctave.MAX_VALUE_2D_WARP;

        // ── Instance fields (L27–48) ──

        private int _maxThreads;
        private LandformsWorldProperty _landforms = null!;
        private float[][] _terrainYThresholds = null!;
        private readonly Dictionary<(int RegionX, int RegionZ), LerpedWeightedIndex2DMap> _landformMapByRegion = new(10);
        private readonly object _landformMapLock = new();
        private int _regionMapSize;
        private float _noiseScale;
        private int _terrainGenOctaves = 9;

        private NewNormalizedSimplexFractalNoise _terrainNoise = null!;
        private SimplexNoise _distort2dx = null!;
        private SimplexNoise _distort2dz = null!;
        private NormalizedSimplexNoise _geoUpheavalNoise = null!;

        private ThreadLocal<ThreadLocalTempData> _tempDataThreadLocal = null!;

        internal bool IsInitialized => _landforms != null;

        // ── Block IDs (loaded from GlobalConfig) ──

        private int _rockID;
        private int _waterBlockId;
        private int _saltWaterBlockId;
        private int _mantleBlockId;
        private int _lakeIceBlockId;

        // ── Structs ──

        private struct ThreadLocalTempData
        {
            public double[] LerpedAmplitudes;
            public double[] LerpedThresholds;
            public float[] LandformWeights;
        }

        /// <summary>
        /// Per-column taper data for border smoothing. Written at Step 0,
        /// read at Step 7 for chunk-border blending.
        /// </summary>
        public struct WeightedTaper
        {
            /// <summary>Interpolated terrain Y position from neighbour heightmaps.</summary>
            public float TerrainYPos;

            /// <summary>Maximum cardinal weight (0–1). Higher = stronger smoothing.</summary>
            public float Weight;
        }

        private struct ColumnResult
        {
            public BitArray ColumnBlockSolidities;
            public int WaterBlockID;
        }

        /// <summary>
        /// Scratch state owned by one terrain request. Worldgen can process
        /// multiple columns concurrently, so these buffers must not be shared
        /// by the GenTerraHost instance between requests.
        /// </summary>
        private sealed class GenerationScratch
        {
            internal readonly WeightedTaper[] TaperMap;
            internal readonly ColumnResult[] ColumnResults;
            internal readonly bool[] LayerFullySolid;
            internal readonly bool[] LayerFullyEmpty;
            internal readonly int[] BorderIndicesByCardinal;

            internal GenerationScratch(int mapSizeY, int chunkSize)
            {
                TaperMap = new WeightedTaper[chunkSize * chunkSize];
                ColumnResults = new ColumnResult[chunkSize * chunkSize];
                LayerFullySolid = new bool[mapSizeY];
                LayerFullyEmpty = new bool[mapSizeY];
                BorderIndicesByCardinal = new int[8];

                for (int i = 0; i < ColumnResults.Length; i++)
                    ColumnResults[i].ColumnBlockSolidities = new BitArray(mapSizeY);

                BorderIndicesByCardinal[Cardinal.NorthEast] = (chunkSize - 1) * chunkSize;
                BorderIndicesByCardinal[Cardinal.SouthEast] = 0;
                BorderIndicesByCardinal[Cardinal.SouthWest] = chunkSize - 1;
                BorderIndicesByCardinal[Cardinal.NorthWest] = (chunkSize - 1) * chunkSize + chunkSize - 1;
            }
        }

        private struct VectorXZ
        {
            public double X, Z;
            public static VectorXZ operator *(VectorXZ a, double b) => new VectorXZ { X = a.X * b, Z = a.Z * b };
        }

        public GenTerraHost(ICoreServerAPI api)
        {
            _api = api;
        }

        // ════════════════════════════════════════════════════════════════
        //  Hook registration API (called before InitWorldGen)
        // ════════════════════════════════════════════════════════════════

        public void RegisterStep0(string modId, double order, BorderTaperHook hook)
            => Register(_step0Hooks, modId, order, hook);

        public void RegisterStep2(string modId, double order, BuildOctavesHook hook)
            => Register(_step2Hooks, modId, order, hook);

        public void RegisterStep4(string modId, double order, VerticalDistortionHook hook)
            => Register(_step4Hooks, modId, order, hook);

        public void RegisterStep5(string modId, double order, WaterSelectHook hook)
            => Register(_step5Hooks, modId, order, hook);

        public void RegisterStep7(string modId, double order, ThresholdHook hook)
            => Register(_step7Hooks, modId, order, hook);

        public void RegisterStep10(string modId, double order, PostPlacementHook hook)
            => Register(_step10Hooks, modId, order, hook);

        /// <summary>
        /// Register a hook after all columns have completed placement and
        /// Step 10. This is the persistence boundary for chunk-wide arrays
        /// such as river flow vectors and river distances.
        /// </summary>
        public void RegisterTerrainFinalize(string modId, double order, TerrainFinalizeHook hook)
            => Register(_terrainFinalizeHooks, modId, order, hook);

        /// <summary>
        /// Register a terminal full-column generator for migrations that
        /// cannot be decomposed without changing semantics. The first hook
        /// returning true owns the complete request; false composes with the
        /// next hook and ultimately with WorldgenLib's vanilla pass.
        /// </summary>
        public void RegisterFullGeneration(string modId, double order, TerrainGenerationHook hook)
            => Register(_fullGenerationHooks, modId, order, hook);

        private void Register<T>(OrderedHookList<T> hooks, string modId, double order, T hook)
            where T : class
        {
            hooks.Register(modId, order, hook);
        }

        /// <summary>
        /// Freeze all hook lists. Called once after all registrations are complete.
        /// No more registrations allowed after this point.
        /// </summary>
        internal void FreezeHooks()
        {
            _step0Hooks.Freeze();
            _step2Hooks.Freeze();
            _step4Hooks.Freeze();
            _step5Hooks.Freeze();
            _step7Hooks.Freeze();
            _step10Hooks.Freeze();
            _terrainFinalizeHooks.Freeze();
            _fullGenerationHooks.Freeze();
        }

        /// <summary>
        /// Get a diagnostic report of all registered hooks across all steps.
        /// Used by StartupReport at server startup.
        /// </summary>
        internal IReadOnlyList<(string Step, double Order, string ModId)> GetHookReport()
        {
            var report = new List<(string, double, string)>();
            void Collect(string step, IReadOnlyList<(double Order, string ModId)> entries)
            {
                foreach (var e in entries)
                    report.Add((step, e.Order, e.ModId));
            }
            Collect("Step0", _step0Hooks.GetRegistrationReport());
            Collect("Step2", _step2Hooks.GetRegistrationReport());
            Collect("Step4", _step4Hooks.GetRegistrationReport());
            Collect("Step5", _step5Hooks.GetRegistrationReport());
            Collect("Step7", _step7Hooks.GetRegistrationReport());
            Collect("Step10", _step10Hooks.GetRegistrationReport());
            Collect("TerrainFinalize", _terrainFinalizeHooks.GetRegistrationReport());
            Collect("FullGeneration", _fullGenerationHooks.GetRegistrationReport());
            return report;
        }

        internal bool TryRunFullGeneration(IChunkColumnGenerateRequest request)
        {
            var entries = _fullGenerationHooks.Snapshot;
            foreach (var entry in entries)
            {
                if (_fullGenerationHooks.IsDisabled(entry.ModId)) continue;
                string modId = entry.ModId;
                TerrainGenerationHook hook = entry.Handler;
                try
                {
                    if (hook(request)) return true;
                }
                catch (Exception ex)
                {
                    _fullGenerationHooks.Disable(modId);
                    _api.Logger.Warning(
                        "[WorldgenLib] Full terrain hook '{0}' disabled after exception: {1}",
                        modId, ex.Message);
                }
            }

            return false;
        }

        // ════════════════════════════════════════════════════════════════
        //  AssetsFinalize — set sea level (called before StartServerSide)
        // ════════════════════════════════════════════════════════════════

        public void AssetsFinalize()
        {
            if (_api.WorldManager.SaveGame.WorldType != "standard") return;

            TerraGenConfig.seaLevel = (int)(0.4313725490196078 * _api.WorldManager.MapSizeY);
            _api.WorldManager.SetSeaLevel(TerraGenConfig.seaLevel);
            Climate.Sealevel = TerraGenConfig.seaLevel;
        }

        // ════════════════════════════════════════════════════════════════
        //  InitWorldGen — allocate noise, buffers, thread-local state
        // ════════════════════════════════════════════════════════════════

        public void InitWorldGen()
        {
            // LoadGlobalConfig equivalent — read block IDs from config
            LoadBlockIDs();

            lock (_landformMapLock) _landformMapByRegion.Clear();

            _maxThreads = Math.Clamp(
                Environment.ProcessorCount - (_api.Server.IsDedicated ? 4 : 6),
                1,
                _api.Server.Config.HostedMode ? 4 : 10
            );
            if (_api.Server.ReducedServerThreads && _maxThreads > 1) _maxThreads = 2;

            _regionMapSize = (int)Math.Ceiling(
                (double)_api.WorldManager.MapSizeX / _api.WorldManager.RegionSize
            );
            _noiseScale = Math.Max(1, _api.WorldManager.MapSizeY / 256f);
            _terrainGenOctaves = TerraGenConfig.GetTerrainOctaveCount(_api.WorldManager.MapSizeY);

            // ── Noise instances ──

            _terrainNoise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(
                _terrainGenOctaves,
                0.0005 * NewSimplexNoiseLayer.OldToNewFrequency / _noiseScale,
                0.9,
                _api.WorldManager.Seed
            );

            _distort2dx = new SimplexNoise(
                new double[] { 55, 40, 30, 10 },
                ScaleAdjustedFreqs(new double[] { 1 / 5.0, 1 / 2.50, 1 / 1.250, 1 / 0.65 }, _noiseScale),
                _api.World.Seed + 9876 + 0
            );

            _distort2dz = new SimplexNoise(
                new double[] { 55, 40, 30, 10 },
                ScaleAdjustedFreqs(new double[] { 1 / 5.0, 1 / 2.50, 1 / 1.250, 1 / 0.65 }, _noiseScale),
                _api.World.Seed + 9876 + 2
            );

            _geoUpheavalNoise = new NormalizedSimplexNoise(
                new double[] { 55, 40, 30, 15, 7, 4 },
                ScaleAdjustedFreqs(new double[] {
                    1.0 / 5.5, 1.1 / 2.75, 1.2 / 1.375,
                    1.2 / 0.715, 1.2 / 0.45, 1.2 / 0.25
                }, _noiseScale),
                _api.World.Seed + 9876 + 1
            );

            // ── Thread-local state ──

            _tempDataThreadLocal?.Dispose();
            _tempDataThreadLocal = new ThreadLocal<ThreadLocalTempData>(() => new ThreadLocalTempData
            {
                LerpedAmplitudes = new double[_terrainGenOctaves],
                LerpedThresholds = new double[_terrainGenOctaves],
                LandformWeights = new float[LandformRegistry.Landforms.LandFormsByIndex.Length]
            });

            // Landforms are bound by WorldgenLib before this callback. Keep a
            // single snapshot so every column sees the same canonical index set.
            _landforms = LandformRegistry.Landforms;
            _terrainYThresholds = new float[_landforms.LandFormsByIndex.Length][];
            for (int i = 0; i < _terrainYThresholds.Length; i++)
                _terrainYThresholds[i] = _landforms.LandFormsByIndex[i].TerrainYThresholds;
        }

        /// <summary>Release per-thread scratch state when a worldgen session ends.</summary>
        internal void Dispose()
        {
            _tempDataThreadLocal?.Dispose();
            lock (_landformMapLock) _landformMapByRegion.Clear();
            _landforms = null!;
            _terrainYThresholds = null!;
        }

        // ════════════════════════════════════════════════════════════════
        //  OnChunkColumnGen — Step 0 + lazy init + generate
        // ════════════════════════════════════════════════════════════════

        public void OnChunkColumnGen(IChunkColumnGenerateRequest request)
        {
            if (TryRunFullGeneration(request)) return;

            if (_landforms == null || _terrainYThresholds == null)
                throw new InvalidOperationException("[WorldgenLib] GenTerraHost was not initialized for worldgen.");

            Generate(request.Chunks, request.ChunkX, request.ChunkZ,
                request.RequiresChunkBorderSmoothing, request);
        }

        // ════════════════════════════════════════════════════════════════
        //  Step 0: BorderTaperPrepare (L173–270)
        // ════════════════════════════════════════════════════════════════

        private void Step0_BorderTaperPrepare(IChunkColumnGenerateRequest request,
            ChunkContext chunkCtx, GenerationScratch scratch)
        {
            bool smooth = request.RequiresChunkBorderSmoothing
                && !PreventSmoothing(request.ChunkX, request.ChunkZ);

            if (smooth)
            {
                int chunksize = GlobalConstants.ChunkSize;
                var neibHeightMaps = request.NeighbourTerrainHeight;

                // Ignore diagonals when direct cardinals are available
                if (neibHeightMaps[Cardinal.North] != null)
                {
                    neibHeightMaps[Cardinal.NorthEast] = null;
                    neibHeightMaps[Cardinal.NorthWest] = null;
                }
                if (neibHeightMaps[Cardinal.East] != null)
                {
                    neibHeightMaps[Cardinal.NorthEast] = null;
                    neibHeightMaps[Cardinal.SouthEast] = null;
                }
                if (neibHeightMaps[Cardinal.South] != null)
                {
                    neibHeightMaps[Cardinal.SouthWest] = null;
                    neibHeightMaps[Cardinal.SouthEast] = null;
                }
                if (neibHeightMaps[Cardinal.West] != null)
                {
                    neibHeightMaps[Cardinal.SouthWest] = null;
                    neibHeightMaps[Cardinal.NorthWest] = null;
                }

                for (int dx = 0; dx < chunksize; dx++)
                {
                    scratch.BorderIndicesByCardinal[Cardinal.North] = (chunksize - 1) * chunksize + dx;
                    scratch.BorderIndicesByCardinal[Cardinal.South] = dx;

                    for (int dz = 0; dz < chunksize; dz++)
                    {
                        double sumWeight = 0;
                        double ypos = 0;
                        float maxWeight = 0;

                        scratch.BorderIndicesByCardinal[Cardinal.East] = dz * chunksize;
                        scratch.BorderIndicesByCardinal[Cardinal.West] = dz * chunksize + chunksize - 1;

                        for (int i = 0; i < Cardinal.ALL.Length; i++)
                        {
                            var neibMap = neibHeightMaps[i];
                            if (neibMap == null) continue;

                            float distToEdge = 0;
                            switch (i)
                            {
                                case 0: distToEdge = (float)dz / chunksize; break;
                                case 1: distToEdge = 1 - (dx + 1f) / chunksize + (float)dz / chunksize; break;
                                case 2: distToEdge = 1 - (dx + 1f) / chunksize; break;
                                case 3: distToEdge = 1 - (dx + 1f) / chunksize + 1 - (dz + 1f) / chunksize; break;
                                case 4: distToEdge = 1 - (dz + 1f) / chunksize; break;
                                case 5: distToEdge = (float)dx / chunksize + 1 - (dz + 1f) / chunksize; break;
                                case 6: distToEdge = (float)dx / chunksize; break;
                                case 7: distToEdge = (float)dx / chunksize + (float)dz / chunksize; break;
                            }

                            float baseWeight = Math.Max(0, 1 - distToEdge);
                            float cardinalWeight = baseWeight * baseWeight;
                            var neibYPos = neibMap[scratch.BorderIndicesByCardinal[i]] + 0.5f;

                            ypos += neibYPos * Math.Max(0.0001, cardinalWeight);
                            sumWeight += cardinalWeight;
                            maxWeight = Math.Max(maxWeight, cardinalWeight);
                        }

                        scratch.TaperMap[dz * chunksize + dx] = new WeightedTaper
                        {
                            TerrainYPos = (float)(ypos / Math.Max(0.0001, sumWeight)),
                            Weight = maxWeight
                        };
                    }
                }
            }

            // ── Step 0 hooks ──
            if (_step0Hooks.Count > 0)
            {
                var entries = _step0Hooks.Snapshot;
                foreach (var entry in entries)
                {
                    if (_step0Hooks.IsDisabled(entry.ModId)) continue;
                    string modId = entry.ModId;
                    BorderTaperHook hook = entry.Handler;
                    try { hook(chunkCtx); }
                    catch (Exception ex)
                    {
                        _step0Hooks.Disable(modId);
                        _api.Logger.Warning("[WorldgenLib] Step 0 hook '{0}' disabled after exception: {1}", modId, ex.Message);
                    }
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Generate — main pipeline (L280–570)
        // ════════════════════════════════════════════════════════════════

        private void Generate(IServerChunk[] chunks, int chunkX, int chunkZ,
            bool requiresChunkBorderSmoothing, IChunkColumnGenerateRequest request)
        {
            int chunksize = GlobalConstants.ChunkSize;
            int mapsizeY = _api.WorldManager.MapSizeY;
            var scratch = new GenerationScratch(mapsizeY, chunksize);

            // ── Step 1: LoadRegionInputs (L283–330) ──
            // No hook point: data loading is pure read from region maps.

            IMapChunk mapchunk = chunks[0].MapChunk;

            int upheavalMapUpLeft = 0, upheavalMapUpRight = 0, upheavalMapBotLeft = 0, upheavalMapBotRight = 0;

            IntDataMap2D climateMap = chunks[0].MapChunk.MapRegion.ClimateMap;
            IntDataMap2D oceanMap = chunks[0].MapChunk.MapRegion.OceanMap;
            int regionChunkSize = _api.WorldManager.RegionSize / chunksize;
            float cfac = (float)climateMap.InnerSize / regionChunkSize;

            int regionX = FloorDiv(chunkX, regionChunkSize);
            int regionZ = FloorDiv(chunkZ, regionChunkSize);
            int rlX = chunkX - regionX * regionChunkSize;
            int rlZ = chunkZ - regionZ * regionChunkSize;

            int climateUpLeft = climateMap.GetUnpaddedInt((int)(rlX * cfac), (int)(rlZ * cfac));
            int climateUpRight = climateMap.GetUnpaddedInt((int)(rlX * cfac + cfac), (int)(rlZ * cfac));
            int climateBotLeft = climateMap.GetUnpaddedInt((int)(rlX * cfac), (int)(rlZ * cfac + cfac));
            int climateBotRight = climateMap.GetUnpaddedInt((int)(rlX * cfac + cfac), (int)(rlZ * cfac + cfac));

            int oceanUpLeft = 0, oceanUpRight = 0, oceanBotLeft = 0, oceanBotRight = 0;
            if (oceanMap != null && oceanMap.Data.Length > 0)
            {
                float ofac = (float)oceanMap.InnerSize / regionChunkSize;
                oceanUpLeft = oceanMap.GetUnpaddedInt((int)(rlX * ofac), (int)(rlZ * ofac));
                oceanUpRight = oceanMap.GetUnpaddedInt((int)(rlX * ofac + ofac), (int)(rlZ * ofac));
                oceanBotLeft = oceanMap.GetUnpaddedInt((int)(rlX * ofac), (int)(rlZ * ofac + ofac));
                oceanBotRight = oceanMap.GetUnpaddedInt((int)(rlX * ofac + ofac), (int)(rlZ * ofac + ofac));
            }

            IntDataMap2D upheavalMap = chunks[0].MapChunk.MapRegion.UpheavelMap;
            if (upheavalMap != null)
            {
                float ufac = (float)upheavalMap.InnerSize / regionChunkSize;
                upheavalMapUpLeft = upheavalMap.GetUnpaddedInt((int)(rlX * ufac), (int)(rlZ * ufac));
                upheavalMapUpRight = upheavalMap.GetUnpaddedInt((int)(rlX * ufac + ufac), (int)(rlZ * ufac));
                upheavalMapBotLeft = upheavalMap.GetUnpaddedInt((int)(rlX * ufac), (int)(rlZ * ufac + ufac));
                upheavalMapBotRight = upheavalMap.GetUnpaddedInt((int)(rlX * ufac + ufac), (int)(rlZ * ufac + ufac));
            }

            float oceanicityFac = _api.WorldManager.MapSizeY / 256 * 0.33333f;

            IntDataMap2D landformMap = mapchunk.MapRegion.LandformMap;
            float chunkPixelSize = landformMap.InnerSize / regionChunkSize;
            float baseX = rlX * chunkPixelSize;
            float baseZ = rlZ * chunkPixelSize;

            LerpedWeightedIndex2DMap landLerpMap = GetOrLoadLerpedLandformMap(
                mapchunk, regionX, regionZ
            );

            // ── Step 2: BuildCornerOctaves (L303–324) ──

            float[] landformWeights = _tempDataThreadLocal.Value.LandformWeights;
            GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX, baseZ, landformWeights),
                out double[] octNoiseX0, out double[] octThX0);
            GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX + chunkPixelSize, baseZ, landformWeights),
                out double[] octNoiseX1, out double[] octThX1);
            GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX, baseZ + chunkPixelSize, landformWeights),
                out double[] octNoiseX2, out double[] octThX2);
            GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX + chunkPixelSize, baseZ + chunkPixelSize, landformWeights),
                out double[] octNoiseX3, out double[] octThX3);

            float[][] terrainYThresholds = _terrainYThresholds;

            ushort[] rainheightmap = chunks[0].MapChunk.RainHeightMap;
            ushort[] terrainheightmap = chunks[0].MapChunk.WorldGenTerrainHeightMap;

            int mapsizeYm2 = _api.WorldManager.MapSizeY - 2;
            int taperThreshold = (int)(mapsizeY * 0.9f);
            double geoUpheavalAmplitude = 255;

            var chunkCtx = new ChunkContext(
                chunkX, chunkZ,
                climateUpLeft, climateUpRight, climateBotLeft, climateBotRight,
                oceanUpLeft, oceanUpRight, oceanBotLeft, oceanBotRight,
                upheavalMapUpLeft, upheavalMapUpRight, upheavalMapBotLeft, upheavalMapBotRight,
                TerraGenConfig.seaLevel, mapsizeY, oceanicityFac,
                taperThreshold, geoUpheavalAmplitude, scratch.TaperMap, _distort2dx,
                terrainYThresholds, _landforms, landLerpMap,
                chunkPixelSize, baseX, baseZ,
                chunks, mapchunk, request,
                _rockID, _waterBlockId, _saltWaterBlockId, _lakeIceBlockId)
            {
                RegionX = regionX,
                RegionZ = regionZ,
                Distort2dZ = _distort2dz,
                TerrainNoise = _terrainNoise,
                GeoUpheavalNoise = _geoUpheavalNoise,
                LandformMap = landformMap,
                RegionMapSize = _regionMapSize
            };

            Array.Clear(scratch.TaperMap, 0, scratch.TaperMap.Length);
            Step0_BorderTaperPrepare(request, chunkCtx, scratch);

            const float chunkBlockDelta = 1.0f / 32; // chunksize = 32
            float chunkPixelBlockStep = chunkPixelSize * chunkBlockDelta;
            double verticalNoiseRelativeFrequency = 0.5 / TerraGenConfig.terrainNoiseVerticalScale;

            for (int y = 0; y < scratch.LayerFullySolid.Length; y++) scratch.LayerFullySolid[y] = true;
            for (int y = 0; y < scratch.LayerFullyEmpty.Length; y++) scratch.LayerFullyEmpty[y] = true;
            scratch.LayerFullyEmpty[mapsizeY - 1] = false;

            // ── Parallel.For — Steps 2–8 per column ──

            Parallel.For(0, chunksize * chunksize,
                new ParallelOptions { MaxDegreeOfParallelism = _maxThreads },
                chunkIndex2d =>
                {
                    var step2Entries = _step2Hooks.Snapshot;
                    var step4Entries = _step4Hooks.Snapshot;
                    var step5Entries = _step5Hooks.Snapshot;
                    var step7Entries = _step7Hooks.Snapshot;
                    int lX = chunkIndex2d % chunksize;
                    int lZ = chunkIndex2d / chunksize;
                    int worldX = chunkX * chunksize + lX;
                    int worldZ = chunkZ * chunksize + lZ;
                    BitArray columnBlockSolidities = scratch.ColumnResults[chunkIndex2d].ColumnBlockSolidities;
                    columnBlockSolidities.SetAll(false);
                    double[] lerpedAmps = _tempDataThreadLocal.Value.LerpedAmplitudes;
                    double[] lerpedTh = _tempDataThreadLocal.Value.LerpedThresholds;

                    // ── Step 2 (per-column): BuildCornerOctaves ──

                    float[] columnLandformIndexedWeights = _tempDataThreadLocal.Value.LandformWeights;
                    landLerpMap.WeightsAt(
                        baseX + lX * chunkPixelBlockStep,
                        baseZ + lZ * chunkPixelBlockStep,
                        columnLandformIndexedWeights
                    );

                    for (int i = 0; i < lerpedAmps.Length; i++)
                    {
                        lerpedAmps[i] = GameMath.BiLerp(
                            octNoiseX0[i], octNoiseX1[i], octNoiseX2[i], octNoiseX3[i],
                            lX * chunkBlockDelta, lZ * chunkBlockDelta
                        );
                        lerpedTh[i] = GameMath.BiLerp(
                            octThX0[i], octThX1[i], octThX2[i], octThX3[i],
                            lX * chunkBlockDelta, lZ * chunkBlockDelta
                        );
                    }

                    var colCtx = new ColumnContext(
                        worldX, worldZ, lX, lZ,
                        columnLandformIndexedWeights,
                        lerpedAmps,
                        lerpedTh,
                        columnBlockSolidities,
                        _landforms);

                    // ── Step 2 hooks ──
                    foreach (var entry in step2Entries)
                    {
                        if (_step2Hooks.IsDisabled(entry.ModId)) continue;
                        string modId = entry.ModId;
                        BuildOctavesHook hook = entry.Handler;
                        try { hook(chunkCtx, ref colCtx); }
                        catch (Exception ex)
                        {
                            _step2Hooks.Disable(modId);
                            _api.Logger.Warning("[WorldgenLib] Step 2 hook '{0}' disabled after exception: {1}", modId, ex.Message);
                        }
                    }

                    // ── Step 3: HorizontalDistortion (L384–386) ──
                    // No hook point: noise-based, no mod-relevant parameters.

                    VectorXZ dist = NewDistortionNoise(worldX, worldZ);
                    VectorXZ distTerrain = ApplyIsotropicDistortionThreshold(
                        dist * TerrainDistortionMultiplier,
                        TerrainDistortionThreshold,
                        TerrainDistortionMultiplier * MaxDistortionAmount
                    );

                    // ── Step 4: VerticalDistortion (L388–393) ──

                    float upHeavalStrength = GameMath.BiLerp(
                        upheavalMapUpLeft, upheavalMapUpRight,
                        upheavalMapBotLeft, upheavalMapBotRight,
                        lX * chunkBlockDelta, lZ * chunkBlockDelta
                    );
                    float oceanicity = GameMath.BiLerp(
                        oceanUpLeft, oceanUpRight, oceanBotLeft, oceanBotRight,
                        lX * chunkBlockDelta, lZ * chunkBlockDelta
                    ) * oceanicityFac;

                    VectorXZ distGeo = ApplyIsotropicDistortionThreshold(
                        dist * GeoDistortionMultiplier,
                        GeoDistortionThreshold,
                        GeoDistortionMultiplier * MaxDistortionAmount
                    );

                    float baseDistY = oceanicity + ComputeOceanAndUpheavalDistY(
                        upHeavalStrength, worldX, worldZ, distGeo
                    );

                    // ── Step 4 hooks ──
                    colCtx.UpheavalStrength = upHeavalStrength;
                    colCtx.Oceanicity = oceanicity;
                    colCtx.DistY = baseDistY;
                    foreach (var entry in step4Entries)
                    {
                        if (_step4Hooks.IsDisabled(entry.ModId)) continue;
                        string modId = entry.ModId;
                        VerticalDistortionHook hook = entry.Handler;
                        try { hook(chunkCtx, ref colCtx); }
                        catch (Exception ex)
                        {
                            _step4Hooks.Disable(modId);
                            _api.Logger.Warning("[WorldgenLib] Step 4 hook '{0}' disabled after exception: {1}", modId, ex.Message);
                        }
                    }
                    if (!float.IsFinite(colCtx.UpheavalStrength))
                    {
                        _api.Logger.Warning(
                            "[WorldgenLib] Step 4 produced a non-finite upheaval strength at ({0}, {1}); original value restored.",
                            worldX, worldZ);
                        colCtx.UpheavalStrength = upHeavalStrength;
                    }
                    if (!float.IsFinite(colCtx.DistY))
                    {
                        _api.Logger.Warning(
                            "[WorldgenLib] Step 4 produced a non-finite DistY at ({0}, {1}); original value restored.",
                            worldX, worldZ);
                        colCtx.DistY = baseDistY;
                    }

                    // UpheavalStrength is an input to vanilla's DistY formula.
                    // Re-evaluate it after hooks, while preserving any explicit
                    // DistY delta supplied by a consumer.
                    float explicitDistYDelta = colCtx.DistY - baseDistY;
                    float distY = colCtx.Oceanicity + ComputeOceanAndUpheavalDistY(
                        colCtx.UpheavalStrength, worldX, worldZ, distGeo)
                        + explicitDistYDelta;
                    colCtx.DistY = distY;

                    // ── Step 5: WaterColumnSelect (L395) ──

                    scratch.ColumnResults[chunkIndex2d].WaterBlockID =
                        oceanicity > 1 ? _saltWaterBlockId : _waterBlockId;

                    // ── Step 5 hooks ──
                    colCtx.Oceanicity = oceanicity;
                    colCtx.WaterBlockId = scratch.ColumnResults[chunkIndex2d].WaterBlockID;
                    foreach (var entry in step5Entries)
                    {
                        if (_step5Hooks.IsDisabled(entry.ModId)) continue;
                        string modId = entry.ModId;
                        WaterSelectHook hook = entry.Handler;
                        try { hook(chunkCtx, ref colCtx); }
                        catch (Exception ex)
                        {
                            _step5Hooks.Disable(modId);
                            _api.Logger.Warning("[WorldgenLib] Step 5 hook '{0}' disabled after exception: {1}", modId, ex.Message);
                        }
                    }
                    scratch.ColumnResults[chunkIndex2d].WaterBlockID = colCtx.WaterBlockId;

                    // ── Step 6: ColumnNoiseSetup (L398) ──
                    // No hook point: internal noise state, exposed via step 7 context.

                    var columnNoise = _terrainNoise.ForColumn(
                        verticalNoiseRelativeFrequency, lerpedAmps, lerpedTh,
                        worldX + distTerrain.X, worldZ + distTerrain.Z
                    );
                    double noiseBoundMin = columnNoise.BoundMin;
                    double noiseBoundMax = columnNoise.BoundMax;

                    colCtx.ColumnNoise = columnNoise;
                    colCtx.NoiseBoundMin = noiseBoundMin;
                    colCtx.NoiseBoundMax = noiseBoundMax;

                    WeightedTaper wtaper = scratch.TaperMap[chunkIndex2d];

                    // ── Step 7: PerVoxelThreshold (L400–460) ──

                    float distortedPosYSlide = distY - (int)Math.Floor(distY);
                    for (int posY = 1; posY <= mapsizeYm2; posY++)
                    {
                        StartSampleDisplacedYThreshold(posY + distY, mapsizeYm2, out int distortedPosYBase);

                        double threshold = 0;
                        for (int i = 0; i < columnLandformIndexedWeights.Length; i++)
                        {
                            float weight = columnLandformIndexedWeights[i];
                            if (weight == 0) continue;
                            threshold += weight * ContinueSampleDisplacedYThreshold(
                                distortedPosYBase, distortedPosYSlide, terrainYThresholds[i]
                            );
                        }

                        ComputeGeoUpheavalTaper(posY, distY, taperThreshold, geoUpheavalAmplitude, mapsizeY, ref threshold);

                        if (requiresChunkBorderSmoothing)
                        {
                            double th = posY > wtaper.TerrainYPos ? 1 : -1;
                            var ydiff = Math.Abs(posY - wtaper.TerrainYPos);
                            var noise = ydiff > 10 ? 0
                                : _distort2dx.Noise(
                                    -(chunkX * chunksize + lX) / 10.0,
                                    posY / 10.0,
                                    -(chunkZ * chunksize + lZ) / 10.0
                                ) / Math.Max(1, ydiff / 2.0);
                            noise *= GameMath.Clamp(2 * (1 - wtaper.Weight), 0, 1) * 0.1;
                            threshold = GameMath.Lerp(threshold, th + noise, wtaper.Weight);
                        }

                        // ── Step 7 hooks ──
                        colCtx.ColumnBlockSolidities = columnBlockSolidities;
                        if (_step7Hooks.Count > 0)
                        {
                            foreach (var entry in step7Entries)
                            {
                                if (_step7Hooks.IsDisabled(entry.ModId)) continue;
                                string modId = entry.ModId;
                                ThresholdHook hook = entry.Handler;
                                try
                                {
                                    double candidate = hook(chunkCtx, ref colCtx, posY, threshold);
                                    if (double.IsFinite(candidate)) threshold = candidate;
                                    else
                                    {
                                        _step7Hooks.Disable(modId);
                                        _api.Logger.Warning("[WorldgenLib] Step 7 hook '{0}' returned a non-finite threshold and was disabled.", modId);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _step7Hooks.Disable(modId);
                                    _api.Logger.Warning("[WorldgenLib] Step 7 hook '{0}' disabled after exception: {1}", modId, ex.Message);
                                }
                            }
                        }

                        // ── Step 8: SolidityResolve (implicit in step 7) ──
                        // No hook point: determined by threshold from step 7.

                        if (threshold <= noiseBoundMin)
                        {
                            columnBlockSolidities[posY] = true;
                            scratch.LayerFullyEmpty[posY] = false;
                        }
                        else if (!(threshold < noiseBoundMax))
                        {
                            scratch.LayerFullySolid[posY] = false;
                            for (int yAbove = posY + 1; yAbove <= mapsizeYm2; yAbove++)
                                scratch.LayerFullySolid[yAbove] = false;
                            break;
                        }
                        else
                        {
                            double noiseSign = -NormalizedSimplexNoise.NoiseValueCurveInverse(threshold);
                            noiseSign = columnNoise.NoiseSign(posY, noiseSign);

                            if (noiseSign > 0)
                            {
                                columnBlockSolidities[posY] = true;
                                scratch.LayerFullyEmpty[posY] = false;
                            }
                            else
                            {
                                scratch.LayerFullySolid[posY] = false;
                            }
                        }
                    }
                });

            // ── Step 9: PlaceBlocks (L476–570) ──
            // No hook point: bulk block placement, step 10 handles per-column overrides.

            IChunkBlocks chunkBlockData = chunks[0].Data;

            chunkBlockData.SetBlockBulk(0, chunksize, chunksize, _mantleBlockId);
            int yBase = 1;
            for (; yBase < mapsizeY - 1; yBase++)
            {
                if (scratch.LayerFullySolid[yBase])
                {
                    if (yBase % chunksize == 0)
                        chunkBlockData = chunks[yBase / chunksize].Data;
                    chunkBlockData.SetBlockBulk(
                        (yBase % chunksize) * chunksize * chunksize,
                        chunksize, chunksize, _rockID
                    );
                }
                else break;
            }

            int seaLevel = TerraGenConfig.seaLevel;
            int surfaceWaterId = 0;
            int yTop = mapsizeY - 2;
            while (yTop >= yBase && scratch.LayerFullyEmpty[yTop]) yTop--;
            if (yTop < seaLevel) yTop = seaLevel;
            yTop++;

            // ── Step 10: PostPlacementColumn (L504–570) ──

            for (int lZ = 0; lZ < chunksize; lZ++)
            {
                int worldZ = chunkZ * chunksize + lZ;
                int mapIndex = ChunkIndex2d(0, lZ);
                for (int lX = 0; lX < chunksize; lX++)
                {
                    ColumnResult columnResult = scratch.ColumnResults[mapIndex];
                    int waterID = columnResult.WaterBlockID;
                    surfaceWaterId = waterID;

                    if (yBase < seaLevel && waterID != _saltWaterBlockId
                        && !columnResult.ColumnBlockSolidities[seaLevel - 1])
                    {
                        int temp = (GameMath.BiLerpRgbColor(
                            lX * chunkBlockDelta, lZ * chunkBlockDelta,
                            climateUpLeft, climateUpRight, climateBotLeft, climateBotRight
                        ) >> 16) & 0xFF;
                        float distort = (float)_distort2dx.Noise(chunkX * chunksize + lX, worldZ) / 20f;
                        float tempf = Climate.GetScaledAdjustedTemperatureFloat(temp, 0) + distort;
                        if (tempf < TerraGenConfig.WaterFreezingTempOnGen)
                            surfaceWaterId = _lakeIceBlockId;
                    }

                    terrainheightmap[mapIndex] = (ushort)(yBase - 1);
                    rainheightmap[mapIndex] = (ushort)(yBase - 1);

                    chunkBlockData = chunks[yBase / chunksize].Data;
                    for (int posY = yBase; posY < yTop; posY++)
                    {
                        int lY = posY % chunksize;

                        if (columnResult.ColumnBlockSolidities[posY])
                        {
                            terrainheightmap[mapIndex] = (ushort)posY;
                            rainheightmap[mapIndex] = (ushort)posY;
                            chunkBlockData[ChunkIndex3d(lX, lY, lZ)] = _rockID;
                        }
                        else if (posY < seaLevel)
                        {
                            int blockId;
                            if (posY == seaLevel - 1)
                            {
                                rainheightmap[mapIndex] = (ushort)posY;
                                blockId = surfaceWaterId;
                            }
                            else
                            {
                                blockId = waterID;
                            }
                            chunkBlockData.SetFluid(ChunkIndex3d(lX, lY, lZ), blockId);
                        }

                        if (lY == chunksize - 1)
                            chunkBlockData = chunks[(posY + 1) / chunksize].Data;
                    }

                    // ── Step 10 hooks ──
                    if (_step10Hooks.Count > 0)
                    {
                        var step10Entries = _step10Hooks.Snapshot;
                        var carvingCtx = new ColumnCarvingContext(
                            chunkX * chunksize + lX, worldZ,
                            seaLevel, mapsizeY, waterID, chunks, mapchunk,
                            terrainheightmap, rainheightmap,
                            columnResult.ColumnBlockSolidities);
                        foreach (var entry in step10Entries)
                        {
                            if (_step10Hooks.IsDisabled(entry.ModId)) continue;
                            string modId = entry.ModId;
                            PostPlacementHook hook = entry.Handler;
                            try { hook(chunkCtx, ref carvingCtx); }
                            catch (Exception ex)
                            {
                                _step10Hooks.Disable(modId);
                                _api.Logger.Warning("[WorldgenLib] Step 10 hook '{0}' disabled after exception: {1}", modId, ex.Message);
                            }
                        }
                    }

                    mapIndex++;
                }
            }

            // ── TerrainFinalize ──
            // Runs after all 1024 columns so consumers can persist arrays
            // assembled by their parallel Step 2/10 hooks exactly once.
            var finalizeEntries = _terrainFinalizeHooks.Snapshot;
            foreach (var entry in finalizeEntries)
            {
                if (_terrainFinalizeHooks.IsDisabled(entry.ModId)) continue;
                string modId = entry.ModId;
                TerrainFinalizeHook hook = entry.Handler;
                try { hook(chunkCtx); }
                catch (Exception ex)
                {
                    _terrainFinalizeHooks.Disable(modId);
                    _api.Logger.Warning(
                        "[WorldgenLib] Terrain finalization hook '{0}' disabled after exception: {1}",
                        modId, ex.Message);
                }
            }

            ushort ymax = 0;
            for (int i = 0; i < rainheightmap.Length; i++)
                ymax = Math.Max(ymax, rainheightmap[i]);
            chunks[0].MapChunk.YMax = ymax;
        }

        // ════════════════════════════════════════════════════════════════
        //  Helper methods
        // ════════════════════════════════════════════════════════════════

        private void LoadBlockIDs()
        {
            var worldConfig = _api.WorldManager.SaveGame.WorldConfiguration;
            string? configuredRock = worldConfig.GetString("rockBlockId");
            _rockID = GetRequiredBlockId(configuredRock == null
                ? new AssetLocation("rock-granite")
                : new AssetLocation(configuredRock));
            _waterBlockId = GetRequiredBlockId(new AssetLocation("water-still-7"));
            _saltWaterBlockId = GetRequiredBlockId(new AssetLocation("saltwater-still-7"));
            _mantleBlockId = GetRequiredBlockId(new AssetLocation("mantle"));
            _lakeIceBlockId = GetRequiredBlockId(new AssetLocation("lakeice"));
        }

        private int GetRequiredBlockId(AssetLocation location)
        {
            var block = _api.World.GetBlock(location);
            if (block == null)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Required worldgen block '{location}' was not found.");
            return block.Id;
        }

        private bool PreventSmoothing(int chunkX, int chunkZ)
        {
            BoolRef result = new BoolRef();
            _api.Event.IsTerrainHeightSmoothingPrevented(chunkX, chunkZ, result);
            return result.GetValue();
        }

        private LerpedWeightedIndex2DMap GetOrLoadLerpedLandformMap(IMapChunk mapchunk, int regionX, int regionZ)
            => GetOrLoadLerpedLandformMap(mapchunk.MapRegion, regionX, regionZ);

        private void GetInterpolatedOctaves(float[] indices, out double[] amps, out double[] thresholds)
        {
            // Allocate once per call — called 4 times per chunk (corner samples).
            // Could be pooled further if profiling shows allocation pressure.
            amps = new double[_terrainGenOctaves];
            thresholds = new double[_terrainGenOctaves];

            for (int octave = 0; octave < _terrainGenOctaves; octave++)
            {
                double amplitude = 0;
                double threshold = 0;
                int count = Math.Min(indices.Length, _landforms.LandFormsByIndex.Length);
                for (int i = 0; i < count; i++)
                {
                    float weight = indices[i];
                    if (weight == 0) continue;
                    LandformVariant l = _landforms.LandFormsByIndex[i];
                    if (octave < l.TerrainOctaves.Length)
                        amplitude += l.TerrainOctaves[octave] * weight;
                    if (octave < l.TerrainOctaveThresholds.Length)
                        threshold += l.TerrainOctaveThresholds[octave] * weight;
                }
                amps[octave] = amplitude;
                thresholds[octave] = threshold;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void StartSampleDisplacedYThreshold(float distortedPosY, int mapSizeYm2, out int yBase)
        {
            yBase = GameMath.Clamp((int)Math.Floor(distortedPosY), 0, mapSizeYm2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ContinueSampleDisplacedYThreshold(int yBase, float ySlide, float[] thresholds)
        {
            return GameMath.Lerp(thresholds[yBase], thresholds[yBase + 1], ySlide);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ComputeOceanAndUpheavalDistY(float upheavalStrength, double worldX, double worldZ, VectorXZ distGeo)
        {
            float upheavalNoiseValue = (float)_geoUpheavalNoise.Noise(
                (worldX + distGeo.X) / 400.0,
                (worldZ + distGeo.Z) / 400.0
            ) * 0.9f;
            float upheavalMultiplier = Math.Min(0, 0.5f - upheavalNoiseValue);
            return upheavalStrength * upheavalMultiplier;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ComputeGeoUpheavalTaper(double posY, double distY, double taperThreshold,
            double geoUpheavalAmplitude, double mapSizeY, ref double threshold)
        {
            const double AMPLITUDE_MODIFIER = 40.0;
            if (posY > taperThreshold && distY < -2)
            {
                double upheavalAmount = GameMath.Clamp(-distY, posY - mapSizeY, posY);
                double ceilingDelta = posY - taperThreshold;
                threshold += ceilingDelta * upheavalAmount / (AMPLITUDE_MODIFIER * geoUpheavalAmplitude);
            }
        }

        private VectorXZ NewDistortionNoise(double worldX, double worldZ)
        {
            double noiseX = worldX / 400.0;
            double noiseZ = worldZ / 400.0;
            SimplexNoise.NoiseFairWarpVector(_distort2dx, _distort2dz, noiseX, noiseZ,
                out double distX, out double distZ);
            return new VectorXZ { X = distX, Z = distZ };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private VectorXZ ApplyIsotropicDistortionThreshold(VectorXZ dist, double threshold, double maximum)
        {
            double distMagnitudeSquared = dist.X * dist.X + dist.Z * dist.Z;
            double thresholdSquared = threshold * threshold;
            if (distMagnitudeSquared <= thresholdSquared) { dist.X = dist.Z = 0; }
            else
            {
                double baseCurve = (distMagnitudeSquared - thresholdSquared) / distMagnitudeSquared;
                double maximumSquared = maximum * maximum;
                double baseCurveReciprocalAtMaximum = maximumSquared / (maximumSquared - thresholdSquared);
                double slide = baseCurve * baseCurveReciprocalAtMaximum;
                slide *= slide;
                double expectedOutputMaximum = maximum - threshold;
                double forceDown = slide * (expectedOutputMaximum / maximum);
                dist *= forceDown;
            }
            return dist;
        }

        private double[] ScaleAdjustedFreqs(double[] vs, float horizontalScale)
        {
            for (int i = 0; i < vs.Length; i++)
                vs[i] /= horizontalScale;
            return vs;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ChunkIndex3d(int x, int y, int z)
        {
            return (y * GlobalConstants.ChunkSize + z) * GlobalConstants.ChunkSize + x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ChunkIndex2d(int x, int z)
        {
            return z * GlobalConstants.ChunkSize + x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            return value % divisor < 0 ? quotient - 1 : quotient;
        }

        // ════════════════════════════════════════════════════════════════
        //  Sampling API — for TerrainSampler
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sample terrain height at an arbitrary world position using the
        /// same region maps, landform interpolation, distortion, column-noise
        /// bounds, and threshold curve as the vanilla terrain pass.
        /// </summary>
        internal int SampleHeight(int worldX, int worldZ, SamplingModifiers? modifiers)
        {
            SamplingColumn column = BuildSamplingColumn(worldX, worldZ, modifiers);
            int mapSizeYm2 = _api.WorldManager.MapSizeY - 2;
            int height = 0;

            for (int posY = 1; posY <= mapSizeYm2; posY++)
            {
                double threshold = SampleLandformThreshold(column.Weights,
                    posY + column.DistY, mapSizeYm2);
                ComputeGeoUpheavalTaper(posY, column.DistY,
                    column.TaperThreshold, column.GeoUpheavalAmplitude,
                    _api.WorldManager.MapSizeY, ref threshold);

                if (modifiers?.ThresholdTransform != null)
                    threshold = modifiers.ThresholdTransform(posY, threshold);

                if (threshold <= column.NoiseBoundMin)
                {
                    height = posY;
                    continue;
                }

                if (!(threshold < column.NoiseBoundMax)) break;

                double noiseSign = -NormalizedSimplexNoise.NoiseValueCurveInverse(threshold);
                if (column.ColumnNoise.NoiseSign(posY, noiseSign) > 0)
                    height = posY;
            }

            return height;
        }

        /// <summary>
        /// Batch terrain sampling boundary used by slope/erosion consumers.
        /// Coordinates remain keyed by world position so callers can retain
        /// their own sample metadata while using the canonical math.
        /// </summary>
        internal Dictionary<(int worldX, int worldZ), int> SampleHeightsBatch(
            IEnumerable<(int worldX, int worldZ)> worldCoordinates,
            bool ignoreRivers)
        {
            var samples = new Dictionary<(int worldX, int worldZ), int>();
            foreach (var coordinate in worldCoordinates)
            {
                // The canonical WorldgenLib host has no implicit river layer;
                // therefore ignoreRivers is intentionally a compatibility
                // switch for adapters and does not alter this base sample.
                samples[coordinate] = SampleHeight(coordinate.worldX,
                    coordinate.worldZ, modifiers: null);
            }

            return samples;
        }

        /// <summary>
        /// Sample the terrain threshold at a specific Y position. The result
        /// includes the landform interpolation, vertical map displacement,
        /// geologic upheaval taper, and optional threshold transform; it does
        /// not evaluate the 3-D noise sign.
        /// </summary>
        internal double SampleThreshold(int worldX, int worldZ, int posY,
            SamplingModifiers? modifiers = null)
        {
            int mapSizeYm2 = _api.WorldManager.MapSizeY - 2;
            if (posY < 1 || posY > mapSizeYm2)
                throw new ArgumentOutOfRangeException(nameof(posY), posY,
                    $"Terrain Y must be between 1 and {mapSizeYm2}.");

            SamplingColumn column = BuildSamplingColumn(worldX, worldZ, modifiers);
            double threshold = SampleLandformThreshold(column.Weights,
                posY + column.DistY, mapSizeYm2);
            ComputeGeoUpheavalTaper(posY, column.DistY,
                column.TaperThreshold, column.GeoUpheavalAmplitude,
                _api.WorldManager.MapSizeY, ref threshold);
            return modifiers?.ThresholdTransform == null
                ? threshold
                : modifiers.ThresholdTransform(posY, threshold);
        }

        internal void InvalidateLandformRegion(int regionX, int regionZ)
        {
            lock (_landformMapLock)
                _landformMapByRegion.Remove((regionX, regionZ));
        }

        private SamplingColumn BuildSamplingColumn(int worldX, int worldZ,
            SamplingModifiers? modifiers)
        {
            const int chunkSize = GlobalConstants.ChunkSize;
            int regionSize = _api.WorldManager.RegionSize;
            int regionChunkSize = regionSize / chunkSize;
            if (regionChunkSize <= 0)
                throw new InvalidOperationException("[WorldgenLib] RegionSize must be at least one chunk.");

            int chunkX = FloorDiv(worldX, chunkSize);
            int chunkZ = FloorDiv(worldZ, chunkSize);
            int localX = (int)((long)worldX - (long)chunkX * chunkSize);
            int localZ = (int)((long)worldZ - (long)chunkZ * chunkSize);
            int regionX = FloorDiv(chunkX, regionChunkSize);
            int regionZ = FloorDiv(chunkZ, regionChunkSize);
            int regionLocalChunkX = chunkX - regionX * regionChunkSize;
            int regionLocalChunkZ = chunkZ - regionZ * regionChunkSize;

            IMapRegion mapRegion = _api.WorldManager.GetMapRegion(regionX, regionZ);
            if (mapRegion == null)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Map region ({regionX}, {regionZ}) is not available for sampling.");

            float mapX = regionLocalChunkX + localX / (float)chunkSize;
            float mapZ = regionLocalChunkZ + localZ / (float)chunkSize;
            // Keep the same integer map-corner selection and integer
            // division as vanilla GenTerra. The public sampler must not
            // silently change the world when MapSizeY is not 256.
            float oceanicity = SampleMap(mapRegion.OceanMap, regionLocalChunkX,
                regionLocalChunkZ, localX, localZ, regionChunkSize)
                * (_api.WorldManager.MapSizeY / 256 * 0.33333f);
            float upheaval = SampleMap(mapRegion.UpheavelMap, regionLocalChunkX,
                regionLocalChunkZ, localX, localZ, regionChunkSize);

            LerpedWeightedIndex2DMap landLerpMap = GetOrLoadLerpedLandformMap(
                mapRegion, regionX, regionZ);
            float chunkPixelSize = mapRegion.LandformMap.InnerSize / (float)regionChunkSize;
            float landformX = mapX * chunkPixelSize;
            float landformZ = mapZ * chunkPixelSize;
            float[] weights = new float[_landforms.LandFormsByIndex.Length];
            landLerpMap.WeightsAt(landformX, landformZ, weights);
            modifiers?.LandformWeightTransform?.Invoke(weights);

            GetInterpolatedOctaves(weights, out double[] amplitudes,
                out double[] thresholds);
            VectorXZ distortion = NewDistortionNoise(worldX, worldZ);
            VectorXZ distTerrain = ApplyIsotropicDistortionThreshold(
                distortion * TerrainDistortionMultiplier,
                TerrainDistortionThreshold,
                TerrainDistortionMultiplier * MaxDistortionAmount);
            VectorXZ distGeo = ApplyIsotropicDistortionThreshold(
                distortion * GeoDistortionMultiplier,
                GeoDistortionThreshold,
                GeoDistortionMultiplier * MaxDistortionAmount);
            float distY = oceanicity + ComputeOceanAndUpheavalDistY(
                upheaval, worldX, worldZ, distGeo);
            if (modifiers != null) distY += modifiers.DistYDelta;

            double relativeYFrequency = 0.5 / TerraGenConfig.terrainNoiseVerticalScale;
            var columnNoise = _terrainNoise.ForColumn(relativeYFrequency,
                amplitudes, thresholds, worldX + distTerrain.X,
                worldZ + distTerrain.Z);

            return new SamplingColumn(
                weights, distY, columnNoise.BoundMin, columnNoise.BoundMax,
                columnNoise, (int)(_api.WorldManager.MapSizeY * 0.9f), 255);
        }

        private float SampleMap(IntDataMap2D? map, int regionChunkX,
            int regionChunkZ, int localX, int localZ, int regionChunkSize)
        {
            if (map == null || map.Data == null || map.Data.Length == 0) return 0;
            float scale = map.InnerSize / (float)regionChunkSize;
            int x0 = (int)(regionChunkX * scale);
            int z0 = (int)(regionChunkZ * scale);
            int x1 = (int)(regionChunkX * scale + scale);
            int z1 = (int)(regionChunkZ * scale + scale);
            return GameMath.BiLerp(
                map.GetUnpaddedInt(x0, z0), map.GetUnpaddedInt(x1, z0),
                map.GetUnpaddedInt(x0, z1), map.GetUnpaddedInt(x1, z1),
                localX / (float)GlobalConstants.ChunkSize,
                localZ / (float)GlobalConstants.ChunkSize);
        }

        private double SampleLandformThreshold(float[] weights, double displacedY,
            int mapSizeYm2)
        {
            StartSampleDisplacedYThreshold((float)displacedY, mapSizeYm2,
                out int yBase);
            float ySlide = (float)(displacedY - Math.Floor(displacedY));
            double threshold = 0;
            int count = Math.Min(weights.Length, _terrainYThresholds.Length);
            for (int i = 0; i < count; i++)
            {
                float weight = weights[i];
                if (weight == 0) continue;
                threshold += weight * ContinueSampleDisplacedYThreshold(
                    yBase, ySlide, _terrainYThresholds[i]);
            }
            return threshold;
        }

        private LerpedWeightedIndex2DMap GetOrLoadLerpedLandformMap(
            IMapRegion mapRegion, int regionX, int regionZ)
        {
            var key = (regionX, regionZ);
            lock (_landformMapLock)
            {
                if (_landformMapByRegion.TryGetValue(key, out var map))
                    return map;

                IntDataMap2D lmap = mapRegion.LandformMap;
                map = new LerpedWeightedIndex2DMap(
                    lmap.Data, lmap.Size,
                    TerraGenConfig.landFormSmoothingRadius,
                    lmap.TopLeftPadding, lmap.BottomRightPadding);
                _landformMapByRegion[key] = map;
                return map;
            }
        }

        private readonly struct SamplingColumn
        {
            public readonly float[] Weights;
            public readonly float DistY;
            public readonly double NoiseBoundMin;
            public readonly double NoiseBoundMax;
            public readonly NewNormalizedSimplexFractalNoise.ColumnNoise ColumnNoise;
            public readonly double TaperThreshold;
            public readonly double GeoUpheavalAmplitude;

            public SamplingColumn(float[] weights, float distY,
                double noiseBoundMin, double noiseBoundMax,
                NewNormalizedSimplexFractalNoise.ColumnNoise columnNoise,
                double taperThreshold, double geoUpheavalAmplitude)
            {
                Weights = weights;
                DistY = distY;
                NoiseBoundMin = noiseBoundMin;
                NoiseBoundMax = noiseBoundMax;
                ColumnNoise = columnNoise;
                TaperThreshold = taperThreshold;
                GeoUpheavalAmplitude = geoUpheavalAmplitude;
            }
        }
    }
}
