using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Datastructures;
using Vintagestory.ServerMods;

namespace WorldgenLib
{
    /// <summary>
    /// Faithful reimplementation of vanilla GenMaps server-side behavior.
    /// Generates the nine region maps (geoprovince, climate, forest, upheavel,
    /// ocean, beach, shrub, biome, landform) plus the force mechanisms and the
    /// client latitude channel.
    ///
    /// When no hooks are registered, the output is byte-identical to vanilla
    /// for the same seed and config. Consumer mods register effects at the
    /// nine map steps or at RegionFinalize.
    ///
    /// The static factory methods (GetClimateMapGen, GetOceanMapGen, etc.) are
    /// called on the vanilla GenMaps class — they are public static and do not
    /// depend on GenMaps instance state.
    /// </summary>
    public sealed class GenMapsHost
    {
        private readonly ICoreServerAPI _sapi;

        // ── Map layer generators (built in initWorldGen) ──
        // NOTE: Field names match vanilla GenMaps convention (camelCase, no underscore).
        // This is intentional for parity with the vanilla code structure.

        internal MapLayerBase geologicprovinceGen = null!;
        internal MapLayerBase climateGen = null!;
        internal MapLayerBase forestGen = null!;
        internal MapLayerBase upheavelGen = null!;
        internal MapLayerBase oceanGen = null!;
        internal MapLayerBase beachGen = null!;
        internal MapLayerBase bushGen = null!;
        internal MapLayerBase flowerGen = null!;
        internal MapLayerBase landformsGen = null!;

        // ── Noise sizes (computed from region size and map scales) ──

        internal int noiseSizeGeoProv;
        internal int noiseSizeClimate;
        internal int noiseSizeForest;
        internal int noiseSizeUpheavel;
        internal int noiseSizeOcean;
        internal int noiseSizeBeach;
        internal int noiseSizeShrubs;
        internal int noiseSizeLandform;

        // ── Force mechanisms ──

        internal readonly List<ForceLandform> forceLandforms = new();
        internal readonly List<ForceClimate> forceClimate = new();
        internal readonly List<XZ> requireLandAt = new();
        private readonly List<XZ> _pendingRequiredLand = new();
        private readonly List<ForceLandform> _pendingLandformForces = new();
        private readonly List<(int X, int Z, int Radius)> _pendingRandomLandAreas = new();

        // ── Latitude data ──

        internal LatitudeData latdata = new();

        // ── Noise for forceLandform wobble ──

        private NormalizedSimplexNoise noisegenX = null!;
        private NormalizedSimplexNoise noisegenZ = null!;
        private GetLatitudeDelegate? _previousLatitude;
        private bool _capturedPreviousLatitude;
        private float _previousUpheavelCommonness;
        private bool _capturedPreviousUpheavelCommonness;

        // ── Hook lists — one per map step. Frozen after InitWorldGen. ──

        private readonly OrderedHookList<MapsStepHook> _geoprovinceHooks = new();
        private readonly OrderedHookList<MapsStepHook> _climateHooks = new();
        private readonly OrderedHookList<MapsStepHook> _forestHooks = new();
        private readonly OrderedHookList<MapsStepHook> _upheavelHooks = new();
        private readonly OrderedHookList<MapsStepHook> _oceanHooks = new();
        private readonly OrderedHookList<MapsStepHook> _beachHooks = new();
        private readonly OrderedHookList<MapsStepHook> _shrubHooks = new();
        private readonly OrderedHookList<MapsStepHook> _biomeHooks = new();
        private readonly OrderedHookList<MapsStepHook> _landformHooks = new();
        private readonly OrderedHookList<RegionFinalizeHook> _regionFinalizeHooks = new();
        private readonly OrderedHookList<MapGeneratorHook> _geoprovinceGeneratorHooks = new();
        private readonly OrderedHookList<MapGeneratorHook> _climateGeneratorHooks = new();
        private readonly OrderedHookList<MapGeneratorHook> _forestGeneratorHooks = new();
        private readonly OrderedHookList<MapGeneratorHook> _upheavelGeneratorHooks = new();
        private readonly OrderedHookList<MapGeneratorHook> _oceanGeneratorHooks = new();
        private readonly OrderedHookList<MapGeneratorHook> _beachGeneratorHooks = new();
        private readonly OrderedHookList<MapGeneratorHook> _shrubGeneratorHooks = new();
        private readonly OrderedHookList<MapGeneratorHook> _biomeGeneratorHooks = new();
        private readonly OrderedHookList<MapGeneratorHook> _landformGeneratorHooks = new();
        private readonly OrderedHookList<MapPaddingHook> _mapPaddingHooks = new();
        private readonly OrderedHookList<MapRegionGenerationHook> _fullRegionGenerationHooks = new();
        private bool _hooksFrozen;
        private bool _initialized;
        private bool _spawnRequirementsAdded;
        private Action<int, int>? _invalidateLandformRegion;

        internal bool IsInitialized => _initialized;
        internal bool IsFrozen => _hooksFrozen;

        public GenMapsHost(ICoreServerAPI api)
        {
            _sapi = api;
        }

        /// <summary>
        /// Connect the map host to the terrain host's per-region cache. The
        /// callback is kept as a narrow dependency so map regeneration cannot
        /// leave stale landform interpolation data behind.
        /// </summary>
        internal void SetLandformRegionInvalidator(Action<int, int> callback)
        {
            _invalidateLandformRegion = callback
                ?? throw new ArgumentNullException(nameof(callback));
        }

        // ════════════════════════════════════════════════════════════════
        //  Hook registration — must happen during StartServerSide
        // ════════════════════════════════════════════════════════════════

        private void RegisterHook(OrderedHookList<MapsStepHook> list, string modId, double order, MapsStepHook hook)
        {
            if (_hooksFrozen)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Cannot register GenMaps hooks after InitWorldGen. Mod '{modId}' attempted late registration.");
            list.Register(modId, order, hook);
        }

        public void RegisterGeoprovinceHook(string modId, double order, MapsStepHook hook)
            => RegisterHook(_geoprovinceHooks, modId, order, hook);

        public void RegisterClimateHook(string modId, double order, MapsStepHook hook)
            => RegisterHook(_climateHooks, modId, order, hook);

        public void RegisterForestHook(string modId, double order, MapsStepHook hook)
            => RegisterHook(_forestHooks, modId, order, hook);

        public void RegisterUpheavelHook(string modId, double order, MapsStepHook hook)
            => RegisterHook(_upheavelHooks, modId, order, hook);

        public void RegisterOceanHook(string modId, double order, MapsStepHook hook)
            => RegisterHook(_oceanHooks, modId, order, hook);

        public void RegisterBeachHook(string modId, double order, MapsStepHook hook)
            => RegisterHook(_beachHooks, modId, order, hook);

        public void RegisterShrubHook(string modId, double order, MapsStepHook hook)
            => RegisterHook(_shrubHooks, modId, order, hook);

        public void RegisterBiomeHook(string modId, double order, MapsStepHook hook)
            => RegisterHook(_biomeHooks, modId, order, hook);

        public void RegisterLandformHook(string modId, double order, MapsStepHook hook)
            => RegisterHook(_landformHooks, modId, order, hook);

        public void RegisterRegionFinalizeHook(string modId, double order, RegionFinalizeHook hook)
        {
            if (_hooksFrozen)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Cannot register GenMaps hooks after InitWorldGen. Mod '{modId}' attempted late registration.");
            _regionFinalizeHooks.Register(modId, order, hook);
        }

        /// <summary>Register a wrapper/replacement for a vanilla map generator.</summary>
        public void RegisterMapGenerator(MapGeneratorStep step, string modId,
            double order, MapGeneratorHook hook)
        {
            if (_hooksFrozen)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Cannot register map generator hooks after InitWorldGen. Mod '{modId}' attempted late registration.");
            GetGeneratorHooks(step).Register(modId, order, hook);
        }

        /// <summary>Register a safe replacement for one map stage's padding constant.</summary>
        public void RegisterMapPadding(string modId, double order, MapPaddingHook hook)
        {
            if (_hooksFrozen)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Cannot register GenMaps hooks after InitWorldGen. Mod '{modId}' attempted late registration.");
            _mapPaddingHooks.Register(modId, order, hook);
        }

        /// <summary>
        /// Register a terminal complete-region generator for migrations that
        /// need custom map stages beyond the nine vanilla maps. Returning
        /// true owns the region; returning false composes with the next hook
        /// and then with WorldgenLib's standard pass.
        /// </summary>
        public void RegisterFullRegionGeneration(string modId, double order,
            MapRegionGenerationHook hook)
        {
            if (_hooksFrozen)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Cannot register GenMaps hooks after InitWorldGen. Mod '{modId}' attempted late registration.");
            _fullRegionGenerationHooks.Register(modId, order, hook);
        }

        /// <summary>Freeze all hook lists. Called once after all StartServerSide have run.</summary>
        public void FreezeHooks()
        {
            _geoprovinceHooks.Freeze();
            _climateHooks.Freeze();
            _forestHooks.Freeze();
            _upheavelHooks.Freeze();
            _oceanHooks.Freeze();
            _beachHooks.Freeze();
            _shrubHooks.Freeze();
            _biomeHooks.Freeze();
            _landformHooks.Freeze();
            _regionFinalizeHooks.Freeze();
            _geoprovinceGeneratorHooks.Freeze();
            _climateGeneratorHooks.Freeze();
            _forestGeneratorHooks.Freeze();
            _upheavelGeneratorHooks.Freeze();
            _oceanGeneratorHooks.Freeze();
            _beachGeneratorHooks.Freeze();
            _shrubGeneratorHooks.Freeze();
            _biomeGeneratorHooks.Freeze();
            _landformGeneratorHooks.Freeze();
            _mapPaddingHooks.Freeze();
            _fullRegionGenerationHooks.Freeze();
            _hooksFrozen = true;
        }

        /// <summary>Get a diagnostic report of all registered GenMaps hooks.</summary>
        public IReadOnlyList<(string Step, double Order, string ModId)> GetHookReport()
        {
            var report = new List<(string, double, string)>();
            void AddFrom<T>(string step, OrderedHookList<T> list) where T : class
            {
                foreach (var entry in list.GetRegistrationReport())
                    report.Add((step, entry.Order, entry.ModId));
            }
            AddFrom("Geoprovince", _geoprovinceHooks);
            AddFrom("Climate", _climateHooks);
            AddFrom("Forest", _forestHooks);
            AddFrom("Upheavel", _upheavelHooks);
            AddFrom("Ocean", _oceanHooks);
            AddFrom("Beach", _beachHooks);
            AddFrom("Shrub", _shrubHooks);
            AddFrom("Biome", _biomeHooks);
            AddFrom("Landform", _landformHooks);
            foreach (var entry in _regionFinalizeHooks.GetRegistrationReport())
                report.Add(("RegionFinalize", entry.Order, entry.ModId));
            AddFrom("GeoprovinceGenerator", _geoprovinceGeneratorHooks);
            AddFrom("ClimateGenerator", _climateGeneratorHooks);
            AddFrom("ForestGenerator", _forestGeneratorHooks);
            AddFrom("UpheavelGenerator", _upheavelGeneratorHooks);
            AddFrom("OceanGenerator", _oceanGeneratorHooks);
            AddFrom("BeachGenerator", _beachGeneratorHooks);
            AddFrom("ShrubGenerator", _shrubGeneratorHooks);
            AddFrom("BiomeGenerator", _biomeGeneratorHooks);
            AddFrom("LandformGenerator", _landformGeneratorHooks);
            foreach (var entry in _mapPaddingHooks.GetRegistrationReport())
                report.Add(("MapPadding", entry.Order, entry.ModId));
            foreach (var entry in _fullRegionGenerationHooks.GetRegistrationReport())
                report.Add(("FullRegionGeneration", entry.Order, entry.ModId));
            return report;
        }

        // ════════════════════════════════════════════════════════════════
        //  initWorldGen — builds map layer chains from vanilla factories
        // ════════════════════════════════════════════════════════════════

        private OrderedHookList<MapGeneratorHook> GetGeneratorHooks(MapGeneratorStep step)
        {
            return step switch
            {
                MapGeneratorStep.Geoprovince => _geoprovinceGeneratorHooks,
                MapGeneratorStep.Climate => _climateGeneratorHooks,
                MapGeneratorStep.Forest => _forestGeneratorHooks,
                MapGeneratorStep.Upheavel => _upheavelGeneratorHooks,
                MapGeneratorStep.Ocean => _oceanGeneratorHooks,
                MapGeneratorStep.Beach => _beachGeneratorHooks,
                MapGeneratorStep.Shrub => _shrubGeneratorHooks,
                MapGeneratorStep.Biome => _biomeGeneratorHooks,
                MapGeneratorStep.Landform => _landformGeneratorHooks,
                _ => throw new ArgumentOutOfRangeException(nameof(step))
            };
        }

        private MapLayerBase BuildGenerator(MapGeneratorStep step, MapLayerBase current,
            long seed, int mapScale, NoiseClimate? climateNoise, double landcover,
            double oceanScale, double landformScale, bool requiresSpawnOffset)
        {
            var context = new MapGeneratorContext(step, seed, _sapi, mapScale,
                landcover, oceanScale, landformScale, requiresSpawnOffset,
                requireLandAt, climateNoise);

            foreach (var (order, modId, hook) in GetGeneratorHooks(step).Enumerate())
            {
                try
                {
                    MapLayerBase? replacement = hook(context, current);
                    if (replacement == null)
                    {
                        GetGeneratorHooks(step).Disable(modId);
                        _sapi.Logger.Warning(
                            "[WorldgenLib] Map generator hook '{0}' returned null at {1} and was disabled.",
                            modId, step);
                    }
                    else current = replacement;
                }
                catch (Exception ex)
                {
                    GetGeneratorHooks(step).Disable(modId);
                    _sapi.Logger.Warning(
                        "[WorldgenLib] Map generator hook '{0}' at {1} was disabled after exception: {2}",
                        modId, step, ex.Message);
                }
            }
            return current;
        }

        /// <summary>
        /// Return the generator currently used for a map step. Consumers that
        /// need to wrap a vanilla chain should use RegisterMapGenerator during
        /// startup; this accessor is for diagnostics and terminal adapters.
        /// </summary>
        public MapLayerBase GetMapGenerator(MapGeneratorStep step)
        {
            if (!_initialized)
                throw new InvalidOperationException("[WorldgenLib] GenMapsHost is not initialized.");

            return step switch
            {
                MapGeneratorStep.Geoprovince => geologicprovinceGen,
                MapGeneratorStep.Climate => climateGen,
                MapGeneratorStep.Forest => forestGen,
                MapGeneratorStep.Upheavel => upheavelGen,
                MapGeneratorStep.Ocean => oceanGen,
                MapGeneratorStep.Beach => beachGen,
                MapGeneratorStep.Shrub => bushGen,
                MapGeneratorStep.Biome => flowerGen,
                MapGeneratorStep.Landform => landformsGen,
                _ => throw new ArgumentOutOfRangeException(nameof(step))
            };
        }

        private void RunMapHooks(OrderedHookList<MapsStepHook> hooks,
            RegionContext context, IntDataMap2D map, Action<IntDataMap2D> assign)
        {
            context.CurrentMap = map;
            foreach (var (order, modId, hook) in hooks.Enumerate())
            {
                try { hook(context); }
                catch (Exception ex)
                {
                    hooks.Disable(modId);
                    _sapi.Logger.Warning(
                        "[WorldgenLib] GenMaps hook '{0}' was disabled after exception: {1}",
                        modId, ex.Message);
                }
            }

            if (context.CurrentMap != null)
                assign(context.CurrentMap);
        }

        private int ResolvePadding(MapGeneratorStep step, int vanillaPadding)
        {
            int padding = vanillaPadding;
            foreach (var (order, modId, hook) in _mapPaddingHooks.Enumerate())
            {
                try
                {
                    int candidate = hook(step, padding);
                    if (candidate < 0)
                    {
                        _mapPaddingHooks.Disable(modId);
                        _sapi.Logger.Warning(
                            "[WorldgenLib] Map padding hook '{0}' returned {1} for {2} and was disabled.",
                            modId, candidate, step);
                    }
                    else padding = candidate;
                }
                catch (Exception ex)
                {
                    _mapPaddingHooks.Disable(modId);
                    _sapi.Logger.Warning(
                        "[WorldgenLib] Map padding hook '{0}' at {1} was disabled after exception: {2}",
                        modId, step, ex.Message);
                }
            }
            return padding;
        }

        public void InitWorldGen(bool preserveRequestedForces = false)
        {
            // Generator hooks are applied while the vanilla map chains are
            // being built below. Freeze before the first enumeration so the
            // build is deterministic and late registration cannot race it.
            // WorldgenLibMod uses its own first-initialization flag to freeze
            // the other hosts after this method returns.
            if (!_hooksFrozen)
                FreezeHooks();

            if (!preserveRequestedForces)
            {
                requireLandAt.Clear();
                forceLandforms.Clear();
                forceClimate.Clear();
                _pendingLandformForces.Clear();
                _pendingRandomLandAreas.Clear();
                _pendingRequiredLand.Clear();
                _spawnRequirementsAdded = false;
            }

            long seed = _sapi.WorldManager.Seed;
            int regionSize = _sapi.WorldManager.RegionSize;

            noiseSizeOcean = regionSize / TerraGenConfig.oceanMapScale;
            noiseSizeUpheavel = regionSize / TerraGenConfig.climateMapScale;
            noiseSizeClimate = regionSize / TerraGenConfig.climateMapScale;
            noiseSizeForest = regionSize / TerraGenConfig.forestMapScale;
            noiseSizeShrubs = regionSize / TerraGenConfig.shrubMapScale;
            noiseSizeGeoProv = regionSize / TerraGenConfig.geoProvMapScale;
            noiseSizeLandform = regionSize / TerraGenConfig.landformMapScale;
            noiseSizeBeach = regionSize / TerraGenConfig.beachMapScale;

            // ── World configuration ──

            ITreeAttribute worldConfig = _sapi.WorldManager.SaveGame.WorldConfiguration;
            string climate = worldConfig.GetString("worldClimate", "realistic");
            float tempModifier = worldConfig.GetString("globalTemperature", "1").ToFloat(1);
            float rainModifier = worldConfig.GetString("globalPrecipitation", "1").ToFloat(1);
            latdata.polarEquatorDistance = worldConfig.GetString("polarEquatorDistance", "50000").ToInt(50000);
            // Vanilla exposes this as a static consumed by the terrain pipeline.
            // Capture it so unloading/restarting WorldgenLib does not leak the
            // previous world's value into the next server session.
            if (!_capturedPreviousUpheavelCommonness)
            {
                _previousUpheavelCommonness = GenMaps.upheavelCommonness;
                _capturedPreviousUpheavelCommonness = true;
            }
            GenMaps.upheavelCommonness = worldConfig.GetString("upheavelCommonness", "0.3").ToFloat(0.3f);
            float landcover = worldConfig.GetString("landcover", "1").ToFloat(1f);
            float oceanscale = worldConfig.GetString("oceanscale", "1").ToFloat(1f);
            float landformScale = worldConfig.GetString("landformScale", "1.0").ToFloat(1.0f);

            // ── Climate noise ──

            NoiseClimate noiseClimate;
            switch (climate)
            {
                case "realistic":
                    int spawnMinTemp = 6;
                    int spawnMaxTemp = 14;

                    string startingClimate = worldConfig.GetString("startingClimate");
                    switch (startingClimate)
                    {
                        case "hot":
                            spawnMinTemp = 28; spawnMaxTemp = 32; break;
                        case "warm":
                            spawnMinTemp = 19; spawnMaxTemp = 23; break;
                        case "cool":
                            spawnMinTemp = -5; spawnMaxTemp = 1; break;
                        case "icy":
                            spawnMinTemp = -15; spawnMaxTemp = -10; break;
                    }

                    var realisticClimate = new NoiseClimateRealistic(
                        seed,
                        (double)_sapi.WorldManager.MapSizeZ / TerraGenConfig.climateMapScale / TerraGenConfig.climateMapSubScale,
                        latdata.polarEquatorDistance,
                        spawnMinTemp, spawnMaxTemp
                    );
                    realisticClimate.GeologicActivityStrength =
                        worldConfig.GetString("geologicActivity").ToFloat(0.05f);
                    noiseClimate = realisticClimate;

                    latdata.isRealisticClimate = true;
                    latdata.ZOffset = realisticClimate.ZOffset;
                    break;

                default:
                    noiseClimate = new NoiseClimatePatchy(seed);
                    break;
            }

            noiseClimate.rainMul = rainModifier;
            noiseClimate.tempMul = tempModifier;

            // ── Spawn land requirement ──

            bool requiresSpawnOffset = GameVersion.IsLowerVersionThan(
                _sapi.WorldManager.SaveGame.CreatedGameVersion, "1.20.0-pre.14");

            if (!_spawnRequirementsAdded && requiresSpawnOffset)
            {
                int centerRegX = _sapi.WorldManager.MapSizeX / _sapi.WorldManager.RegionSize / 2;
                int centerRegZ = _sapi.WorldManager.MapSizeZ / _sapi.WorldManager.RegionSize / 2;
                requireLandAt.Add(new XZ(centerRegX * noiseSizeOcean, centerRegZ * noiseSizeOcean));
                _spawnRequirementsAdded = true;
            }
            else if (!_spawnRequirementsAdded)
            {
                var chunkSize = _sapi.WorldManager.ChunkSize;
                var radius = 4 * chunkSize;
                var spawnPosX = (_sapi.WorldManager.MapSizeX + chunkSize) / 2;
                var spawnPosZ = (_sapi.WorldManager.MapSizeZ + chunkSize) / 2;
                ForceRandomLandArea(spawnPosX, spawnPosZ, radius);
                _spawnRequirementsAdded = true;
            }

            // A consumer may request a required-land coordinate from its
            // startup/init callback before this host has map scales. Replay
            // those requests before constructing the ocean generator.
            foreach (XZ requiredLand in _pendingRequiredLand)
                requireLandAt.Add(requiredLand);
            _pendingRequiredLand.Clear();

            // MapLayerOceans reads the first required-land coordinate in its
            // constructor even for current worlds where spawn offset is
            // disabled. Expand queued post-startup requests before creating
            // that layer, rather than replaying them after the constructor.
            foreach (var area in _pendingRandomLandAreas)
                AddRandomLandArea(area.X, area.Z, area.Radius);
            _pendingRandomLandAreas.Clear();

            // ── Build map layer chains (reuse vanilla public static factories) ──

            climateGen = BuildGenerator(MapGeneratorStep.Climate,
                GenMaps.GetClimateMapGen(seed + 1, noiseClimate), seed + 1,
                TerraGenConfig.climateMapScale, climateNoise: noiseClimate,
                landcover: landcover, oceanScale: oceanscale, landformScale: landformScale,
                requiresSpawnOffset: requiresSpawnOffset);
            upheavelGen = BuildGenerator(MapGeneratorStep.Upheavel,
                GenMaps.GetGeoUpheavelMapGen(seed + 873, TerraGenConfig.geoUpheavelMapScale),
                seed + 873, TerraGenConfig.geoUpheavelMapScale,
                climateNoise: noiseClimate, landcover: landcover,
                oceanScale: oceanscale, landformScale: landformScale,
                requiresSpawnOffset: requiresSpawnOffset);
            oceanGen = BuildGenerator(MapGeneratorStep.Ocean,
                GenMaps.GetOceanMapGen(seed + 1873, landcover,
                    TerraGenConfig.oceanMapScale, oceanscale, requireLandAt, requiresSpawnOffset),
                seed + 1873, TerraGenConfig.oceanMapScale, climateNoise: noiseClimate,
                landcover: landcover, oceanScale: oceanscale, landformScale: landformScale,
                requiresSpawnOffset: requiresSpawnOffset);
            forestGen = BuildGenerator(MapGeneratorStep.Forest,
                GenMaps.GetForestMapGen(seed + 2, TerraGenConfig.forestMapScale), seed + 2,
                TerraGenConfig.forestMapScale, climateNoise: noiseClimate, landcover: landcover,
                oceanScale: oceanscale, landformScale: landformScale,
                requiresSpawnOffset: requiresSpawnOffset);
            bushGen = BuildGenerator(MapGeneratorStep.Shrub,
                GenMaps.GetForestMapGen(seed + 109, TerraGenConfig.shrubMapScale), seed + 109,
                TerraGenConfig.shrubMapScale, climateNoise: noiseClimate, landcover: landcover,
                oceanScale: oceanscale, landformScale: landformScale,
                requiresSpawnOffset: requiresSpawnOffset);
            flowerGen = BuildGenerator(MapGeneratorStep.Biome,
                GenMaps.GetForestMapGen(seed + 223, TerraGenConfig.forestMapScale), seed + 223,
                TerraGenConfig.forestMapScale, climateNoise: noiseClimate, landcover: landcover,
                oceanScale: oceanscale, landformScale: landformScale,
                requiresSpawnOffset: requiresSpawnOffset);
            beachGen = BuildGenerator(MapGeneratorStep.Beach,
                GenMaps.GetBeachMapGen(seed + 2273, TerraGenConfig.beachMapScale), seed + 2273,
                TerraGenConfig.beachMapScale, climateNoise: noiseClimate, landcover: landcover,
                oceanScale: oceanscale, landformScale: landformScale,
                requiresSpawnOffset: requiresSpawnOffset);
            geologicprovinceGen = BuildGenerator(MapGeneratorStep.Geoprovince,
                GenMaps.GetGeologicProvinceMapGen(seed + 3, _sapi), seed + 3,
                TerraGenConfig.geoProvMapScale, climateNoise: noiseClimate, landcover: landcover,
                oceanScale: oceanscale, landformScale: landformScale,
                requiresSpawnOffset: requiresSpawnOffset);
            landformsGen = BuildGenerator(MapGeneratorStep.Landform,
                GenMaps.GetLandformMapGen(seed + 4, noiseClimate, _sapi, landformScale), seed + 4,
                TerraGenConfig.landformMapScale, climateNoise: noiseClimate, landcover: landcover,
                oceanScale: oceanscale, landformScale: landformScale,
                requiresSpawnOffset: requiresSpawnOffset);

            // ── Server-side latitude ──

            if (!_capturedPreviousLatitude)
            {
                _previousLatitude = _sapi.World.Calendar.OnGetLatitude;
                _capturedPreviousLatitude = true;
            }
            _sapi.World.Calendar.OnGetLatitude = GetLatitude;

            // ── Noise for forceLandform wobble ──

            int woctaves = 2;
            float wscale = 2f * TerraGenConfig.landformMapScale;
            float wpersistence = 0.9f;
            noisegenX = NormalizedSimplexNoise.FromDefaultOctaves(woctaves, 1 / wscale, wpersistence, seed + 2);
            noisegenZ = NormalizedSimplexNoise.FromDefaultOctaves(woctaves, 1 / wscale, wpersistence, seed + 1231296);
            _initialized = true;

            // Story-structure systems can call ForceLandformAt while the
            // vanilla GenMaps init callback is still running. Apply those
            // requests only after map scales and noise are available.
            foreach (ForceLandform landform in _pendingLandformForces)
                ForceLandAt(landform);
            _pendingLandformForces.Clear();
        }

        /// <summary>Release host state and restore vanilla globals for unload/reload.</summary>
        internal void Dispose()
        {
            if (_capturedPreviousLatitude && _previousLatitude != null)
                _sapi.World.Calendar.OnGetLatitude = _previousLatitude;
            if (_capturedPreviousUpheavelCommonness)
                GenMaps.upheavelCommonness = _previousUpheavelCommonness;

            forceLandforms.Clear();
            forceClimate.Clear();
            requireLandAt.Clear();
            _pendingLandformForces.Clear();
            _pendingRandomLandAreas.Clear();
            _pendingRequiredLand.Clear();
            _previousLatitude = null;
            _capturedPreviousLatitude = false;
            _previousUpheavelCommonness = 0;
            _capturedPreviousUpheavelCommonness = false;
            _initialized = false;
        }

        internal bool TryRunFullRegionGeneration(RegionContext context)
        {
            foreach (var (order, modId, hook) in _fullRegionGenerationHooks.Enumerate())
            {
                try
                {
                    if (hook(context)) return true;
                }
                catch (Exception ex)
                {
                    _fullRegionGenerationHooks.Disable(modId);
                    _sapi.Logger.Warning(
                        "[WorldgenLib] Full map-region hook '{0}' disabled after exception: {1}",
                        modId, ex.Message);
                }
            }

            return false;
        }

        // ════════════════════════════════════════════════════════════════
        //  OnMapRegionGen — nine map steps + force application
        // ════════════════════════════════════════════════════════════════

        public void OnMapRegionGen(IMapRegion mapRegion, int regionX, int regionZ, ITreeAttribute chunkGenParams)
        {
            if (!_initialized || geologicprovinceGen == null) return;

            // GenTerra caches a copy of the landform interpolation groups.
            // Invalidate it before replacing any region map, including when
            // a consumer takes the full-region terminal path.
            _invalidateLandformRegion?.Invoke(regionX, regionZ);

            var regionCtx = new RegionContext(regionX, regionZ, mapRegion,
                noiseSizeGeoProv, noiseSizeClimate, noiseSizeForest,
                noiseSizeUpheavel, noiseSizeOcean, noiseSizeBeach,
                noiseSizeShrubs, noiseSizeLandform,
                ForceLandformAt, ForceClimateAt, RequireLandAt,
                _sapi, GetMapGenerator)
            {
                ChunkGenParams = chunkGenParams
            };

            if (TryRunFullRegionGeneration(regionCtx))
            {
                RegionMapRegistry.FlushRegion(mapRegion);
                mapRegion.DirtyForSaving = true;
                return;
            }

            // ── Step 1: Geoprovince ──

            int pad = ResolvePadding(MapGeneratorStep.Geoprovince, TerraGenConfig.geoProvMapPadding);
            mapRegion.GeologicProvinceMap.Data = geologicprovinceGen.GenLayer(
                regionX * noiseSizeGeoProv - pad,
                regionZ * noiseSizeGeoProv - pad,
                noiseSizeGeoProv + 2 * pad,
                noiseSizeGeoProv + 2 * pad
            );
            mapRegion.GeologicProvinceMap.Size = noiseSizeGeoProv + 2 * pad;
            mapRegion.GeologicProvinceMap.TopLeftPadding = mapRegion.GeologicProvinceMap.BottomRightPadding = pad;

            RunMapHooks(_geoprovinceHooks, regionCtx, mapRegion.GeologicProvinceMap,
                map => mapRegion.GeologicProvinceMap = map);

            // ── Step 2: Climate ──

            int climatePad = ResolvePadding(MapGeneratorStep.Climate, 2);
            pad = climatePad;
            mapRegion.ClimateMap.Data = climateGen.GenLayer(
                regionX * noiseSizeClimate - pad,
                regionZ * noiseSizeClimate - pad,
                noiseSizeClimate + 2 * pad,
                noiseSizeClimate + 2 * pad
            );
            mapRegion.ClimateMap.Size = noiseSizeClimate + 2 * pad;
            mapRegion.ClimateMap.TopLeftPadding = mapRegion.ClimateMap.BottomRightPadding = pad;

            RunMapHooks(_climateHooks, regionCtx, mapRegion.ClimateMap,
                map => mapRegion.ClimateMap = map);

            // ── Step 3: Forest (depends on Climate) ──

            int forestPad = ResolvePadding(MapGeneratorStep.Forest, 1);
            mapRegion.ForestMap.Size = noiseSizeForest + forestPad;
            mapRegion.ForestMap.TopLeftPadding = 0;
            mapRegion.ForestMap.BottomRightPadding = forestPad;
            forestGen.SetInputMap(mapRegion.ClimateMap, mapRegion.ForestMap);
            mapRegion.ForestMap.Data = forestGen.GenLayer(
                regionX * noiseSizeForest, regionZ * noiseSizeForest,
                noiseSizeForest + forestPad, noiseSizeForest + forestPad
            );

            RunMapHooks(_forestHooks, regionCtx, mapRegion.ForestMap,
                map => mapRegion.ForestMap = map);

            // ── Step 4: Upheavel ──

            int upPad = ResolvePadding(MapGeneratorStep.Upheavel, 3);
            mapRegion.UpheavelMap.Size = noiseSizeUpheavel + 2 * upPad;
            mapRegion.UpheavelMap.TopLeftPadding = upPad;
            mapRegion.UpheavelMap.BottomRightPadding = upPad;
            mapRegion.UpheavelMap.Data = upheavelGen.GenLayer(
                regionX * noiseSizeUpheavel - upPad,
                regionZ * noiseSizeUpheavel - upPad,
                noiseSizeUpheavel + 2 * upPad,
                noiseSizeUpheavel + 2 * upPad
            );

            RunMapHooks(_upheavelHooks, regionCtx, mapRegion.UpheavelMap,
                map => mapRegion.UpheavelMap = map);

            // ── Step 5: Ocean ──

            int opad = ResolvePadding(MapGeneratorStep.Ocean, 5);
            mapRegion.OceanMap.Size = noiseSizeOcean + 2 * opad;
            mapRegion.OceanMap.TopLeftPadding = opad;
            mapRegion.OceanMap.BottomRightPadding = opad;
            mapRegion.OceanMap.Data = oceanGen.GenLayer(
                regionX * noiseSizeOcean - opad,
                regionZ * noiseSizeOcean - opad,
                noiseSizeOcean + 2 * opad,
                noiseSizeOcean + 2 * opad
            );

            RunMapHooks(_oceanHooks, regionCtx, mapRegion.OceanMap,
                map => mapRegion.OceanMap = map);

            // ── Step 6: Beach ──

            int beachPad = ResolvePadding(MapGeneratorStep.Beach, 1);
            mapRegion.BeachMap.Size = noiseSizeBeach + beachPad;
            mapRegion.BeachMap.TopLeftPadding = 0;
            mapRegion.BeachMap.BottomRightPadding = beachPad;
            mapRegion.BeachMap.Data = beachGen.GenLayer(
                regionX * noiseSizeBeach, regionZ * noiseSizeBeach,
                noiseSizeBeach + beachPad, noiseSizeBeach + beachPad
            );

            RunMapHooks(_beachHooks, regionCtx, mapRegion.BeachMap,
                map => mapRegion.BeachMap = map);

            // ── Step 7: Shrub (depends on Climate) ──

            int shrubPad = ResolvePadding(MapGeneratorStep.Shrub, 1);
            mapRegion.ShrubMap.Size = noiseSizeShrubs + shrubPad;
            mapRegion.ShrubMap.TopLeftPadding = 0;
            mapRegion.ShrubMap.BottomRightPadding = shrubPad;
            bushGen.SetInputMap(mapRegion.ClimateMap, mapRegion.ShrubMap);
            mapRegion.ShrubMap.Data = bushGen.GenLayer(
                regionX * noiseSizeShrubs, regionZ * noiseSizeShrubs,
                noiseSizeShrubs + shrubPad, noiseSizeShrubs + shrubPad
            );

            RunMapHooks(_shrubHooks, regionCtx, mapRegion.ShrubMap,
                map => mapRegion.ShrubMap = map);

            // ── Step 8: Biome (depends on Climate) ──

            int biomePad = ResolvePadding(MapGeneratorStep.Biome, 1);
            mapRegion.BiomeMap.Size = noiseSizeForest + biomePad;
            mapRegion.BiomeMap.TopLeftPadding = 0;
            mapRegion.BiomeMap.BottomRightPadding = biomePad;
            flowerGen.SetInputMap(mapRegion.ClimateMap, mapRegion.BiomeMap);
            mapRegion.BiomeMap.Data = flowerGen.GenLayer(
                regionX * noiseSizeForest, regionZ * noiseSizeForest,
                noiseSizeForest + biomePad, noiseSizeForest + biomePad
            );

            RunMapHooks(_biomeHooks, regionCtx, mapRegion.BiomeMap,
                map => mapRegion.BiomeMap = map);

            // ── Step 9: Landform ──

            int landformPad = ResolvePadding(MapGeneratorStep.Landform, TerraGenConfig.landformMapPadding);
            pad = landformPad;
            mapRegion.LandformMap.Data = landformsGen.GenLayer(
                regionX * noiseSizeLandform - pad,
                regionZ * noiseSizeLandform - pad,
                noiseSizeLandform + 2 * pad,
                noiseSizeLandform + 2 * pad
            );
            mapRegion.LandformMap.Size = noiseSizeLandform + 2 * pad;
            mapRegion.LandformMap.TopLeftPadding = mapRegion.LandformMap.BottomRightPadding = pad;

            RunMapHooks(_landformHooks, regionCtx, mapRegion.LandformMap,
                map => mapRegion.LandformMap = map);

            // ── Force: chunkGenParams["forceLandform"] ──

            if (chunkGenParams?.HasAttribute("forceLandform") == true)
            {
                var index = chunkGenParams.GetInt("forceLandform");
                for (int i = 0; i < mapRegion.LandformMap.Data.Length; i++)
                {
                    mapRegion.LandformMap.Data[i] = index;
                }
            }

            // ── Force: registered forceLandforms + forceNoUpheavel ──

            int regionsize = _sapi.WorldManager.RegionSize;
            foreach (var fl in forceLandforms)
            {
                ForceLandform(mapRegion, regionX, regionZ, pad, regionsize, fl);
                ForceNoUpheavel(mapRegion, regionX, regionZ, upPad, regionsize, fl);
            }

            // ── Force: registered forceClimate ──

            foreach (var fc in forceClimate)
            {
                ApplyForceClimate(mapRegion, regionX, regionZ, climatePad, regionsize, fc);
            }

            // ── RegionFinalize hooks ──

            regionCtx.CurrentMap = null;
            foreach (var (order, modId, hook) in _regionFinalizeHooks.Enumerate())
            {
                try { hook(regionCtx); }
                catch (Exception ex)
                {
                    _regionFinalizeHooks.Disable(modId);
                    _sapi.Logger.Warning(
                        "[WorldgenLib] GenMaps RegionFinalize hook '{0}' was disabled after exception: {1}",
                        modId, ex.Message);
                }
            }

            RegionMapRegistry.FlushRegion(mapRegion);
            mapRegion.DirtyForSaving = true;
        }

        // ════════════════════════════════════════════════════════════════
        //  Public API — used by story structures and consumer mods
        // ════════════════════════════════════════════════════════════════

        public void ForceClimateAt(ForceClimate climate)
        {
            if (climate == null) throw new ArgumentNullException(nameof(climate));
            forceClimate.Add(climate);
        }

        /// <summary>
        /// Require an ocean map coordinate to be generated as land. Calls made
        /// before InitWorldGen are queued and replayed before the ocean layer
        /// is assembled, matching the vanilla force lifecycle.
        /// </summary>
        public void RequireLandAt(int x, int z)
        {
            var coordinate = new XZ(x, z);
            if (_initialized) requireLandAt.Add(coordinate);
            else _pendingRequiredLand.Add(coordinate);
        }

        // Reflection cache for ForceLandform.landFormIndex (internal field)
        private static readonly FieldInfo? _landFormIndexField = typeof(ForceLandform).GetField("landFormIndex",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public void ForceLandformAt(ForceLandform landform)
        {
            if (landform == null) throw new ArgumentNullException(nameof(landform));
            int index = LandformRegistry.GetIndex(landform.LandformCode);
            if (index < 0)
                throw new ArgumentException("No landform with code " + landform.LandformCode + " found.",
                    nameof(landform));

            if (_landFormIndexField == null)
                throw new MissingFieldException(
                    typeof(ForceLandform).FullName, "landFormIndex");

            _landFormIndexField.SetValue(landform, index);
            forceLandforms.Add(landform);
            if (_initialized) ForceLandAt(landform);
            else _pendingLandformForces.Add(landform);
        }

        public void ForceLandAt(ForceLandform fl)
        {
            if (GameVersion.IsLowerVersionThan(
                _sapi.WorldManager.SaveGame.CreatedGameVersion, "1.20.0-pre.14"))
            {
                int regSize = _sapi.WorldManager.RegionSize;
                var flRadius = fl.Radius;
                int minx = ((fl.CenterPos.X - flRadius) * noiseSizeOcean) / regSize;
                int minz = ((fl.CenterPos.Z - flRadius) * noiseSizeOcean) / regSize;
                int maxx = ((fl.CenterPos.X + flRadius) * noiseSizeOcean) / regSize;
                int maxz = ((fl.CenterPos.Z + flRadius) * noiseSizeOcean) / regSize;

                for (int x = minx; x <= maxx; x++)
                {
                    for (int z = minz; z < maxz; z++)
                    {
                        RequireLandAt(x, z);
                    }
                }
            }
            else
            {
                var radius = fl.Radius + _sapi.WorldManager.ChunkSize;
                ForceRandomLandArea(fl.CenterPos.X, fl.CenterPos.Z, radius);
            }
        }

        public void ForceRandomLandArea(int positionX, int positionZ, int radius)
        {
            if (radius < 0)
                throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be non-negative.");

            if (!_initialized)
            {
                _pendingRandomLandAreas.Add((positionX, positionZ, radius));
                return;
            }

            AddRandomLandArea(positionX, positionZ, radius);
        }

        private void AddRandomLandArea(int positionX, int positionZ, int radius)
        {
            // This helper intentionally has no initialization guard. InitWorldGen
            // must expand queued areas before constructing MapLayerOceans.

            var regionSize = _sapi.WorldManager.RegionSize;
            var minx = (positionX - radius) * noiseSizeOcean / regionSize;
            var minz = (positionZ - radius) * noiseSizeOcean / regionSize;
            var maxx = (positionX + radius) * noiseSizeOcean / regionSize;
            var maxz = (positionZ + radius) * noiseSizeOcean / regionSize;

            var lcgRandom = new LCGRandom(_sapi.World.Seed);
            lcgRandom.InitPositionSeed(positionX, positionZ);
            var naturalShape = new NaturalShape(lcgRandom);
            var sizeX = maxx - minx;
            var sizeZ = maxz - minz;
            naturalShape.InitSquare(sizeX, sizeZ);
            naturalShape.Grow(sizeX * sizeZ);

            foreach (var pos in naturalShape.GetPositions())
            {
                requireLandAt.Add(new XZ(minx + pos.X, minz + pos.Y));
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Latitude — sawtooth formula for realistic climate
        // ════════════════════════════════════════════════════════════════

        internal double GetLatitude(double posZ)
        {
            if (!latdata.isRealisticClimate)
            {
                return 0.5;
            }

            double halfRange = (double)latdata.polarEquatorDistance
                / TerraGenConfig.climateMapScale
                / TerraGenConfig.climateMapSubScale;

            double A = 2;
            double P = halfRange;
            double z = posZ / TerraGenConfig.climateMapScale
                / TerraGenConfig.climateMapSubScale + latdata.ZOffset;

            double latitude = (A / P) * (P - Math.Abs(Math.Abs(z / 2 - P) % (2 * P) - P)) - 1;
            return latitude;
        }

        // ════════════════════════════════════════════════════════════════
        //  Force methods — private, identical to vanilla
        // ════════════════════════════════════════════════════════════════

        private void ForceLandform(IMapRegion mapRegion, int regionX, int regionZ, int pad, int regionsize, ForceLandform fl)
        {
            int lfmapsize = mapRegion.LandformMap.InnerSize;

            float wobbleIntensityBlocks = 80;
            float wobbleIntensityPixelslf = wobbleIntensityBlocks / regionsize * lfmapsize;

            float padRel_wobblepadlf = (float)pad / noiseSizeLandform + wobbleIntensityBlocks / regionsize;

            float minlf = -padRel_wobblepadlf;
            float maxlf = (1 + padRel_wobblepadlf);

            var flRadius = fl.Radius;
            float startX = (float)(fl.CenterPos.X - flRadius) / regionsize - regionX;
            float endX = (float)(fl.CenterPos.X + flRadius) / regionsize - regionX;
            float startZ = (float)(fl.CenterPos.Z - flRadius) / regionsize - regionZ;
            float endZ = (float)(fl.CenterPos.Z + flRadius) / regionsize - regionZ;

            if (endX >= minlf && startX <= maxlf && endZ >= minlf && startZ <= maxlf)
            {
                startX = GameMath.Clamp(startX, minlf, maxlf) * lfmapsize - pad;
                endX = GameMath.Clamp(endX, minlf, maxlf) * lfmapsize + pad;
                startZ = GameMath.Clamp(startZ, minlf, maxlf) * lfmapsize - pad;
                endZ = GameMath.Clamp(endZ, minlf, maxlf) * lfmapsize + pad;

                double radiussq = Math.Pow((double)flRadius / regionsize * lfmapsize, 2);

                double centerRegionX = (double)fl.CenterPos.X / regionsize;
                double centerRegionZ = (double)fl.CenterPos.Z / regionsize;

                double regionOffsetToCenterX = (centerRegionX - regionX) * lfmapsize;
                double regionOffsetToCenterZ = (centerRegionZ - regionZ) * lfmapsize;

                for (int x = (int)startX; x < endX; x++)
                {
                    for (int z = (int)startZ; z < endZ; z++)
                    {
                        double rsq = Math.Pow(x - regionOffsetToCenterX, 2)
                                   + Math.Pow(z - regionOffsetToCenterZ, 2);

                        if (rsq >= radiussq) continue;

                        double nx = x + regionX * lfmapsize;
                        double nz = z + regionZ * lfmapsize;

                        int offsetX = (int)(wobbleIntensityPixelslf * noisegenX.Noise(nx, nz));
                        int offsetZ = (int)(wobbleIntensityPixelslf * noisegenZ.Noise(nx, nz));

                        int finalX = x + offsetX + pad;
                        int finalZ = z + offsetZ + pad;

                        if (finalX >= 0 && finalX < mapRegion.LandformMap.Size
                            && finalZ >= 0 && finalZ < mapRegion.LandformMap.Size)
                        {
                            object? rawIndex = _landFormIndexField?.GetValue(fl);
                            int index = rawIndex is int value ? value : 0;
                            mapRegion.LandformMap.SetInt(finalX, finalZ, index);
                        }
                    }
                }
            }
        }

        private void ForceNoUpheavel(IMapRegion mapRegion, int regionX, int regionZ, int pad, int regionsize, ForceLandform fl)
        {
            var map = mapRegion.UpheavelMap;
            int uhmapsize = map.InnerSize;

            float wobbleIntensityBlocks = 80;
            float padRel_wobblepaduh = (float)pad / noiseSizeUpheavel + wobbleIntensityBlocks / regionsize;

            float minlf = -padRel_wobblepaduh;
            float maxlf = (1 + padRel_wobblepaduh);

            var rad = fl.Radius + 100;

            float startX = (float)(fl.CenterPos.X - rad) / regionsize - regionX;
            float endX = (float)(fl.CenterPos.X + rad) / regionsize - regionX;
            float startZ = (float)(fl.CenterPos.Z - rad) / regionsize - regionZ;
            float endZ = (float)(fl.CenterPos.Z + rad) / regionsize - regionZ;

            if (endX >= minlf && startX <= maxlf && endZ >= minlf && startZ <= maxlf)
            {
                double radiussq = Math.Pow((double)rad / regionsize * uhmapsize, 2);

                double centerRegionX = (double)fl.CenterPos.X / regionsize;
                double centerRegionZ = (double)fl.CenterPos.Z / regionsize;

                double regionOffsetToCenterX = (centerRegionX - regionX) * uhmapsize;
                double regionOffsetToCenterZ = (centerRegionZ - regionZ) * uhmapsize;

                startX = GameMath.Clamp(startX, minlf, maxlf) * uhmapsize - pad;
                endX = GameMath.Clamp(endX, minlf, maxlf) * uhmapsize + pad;
                startZ = GameMath.Clamp(startZ, minlf, maxlf) * uhmapsize - pad;
                endZ = GameMath.Clamp(endZ, minlf, maxlf) * uhmapsize + pad;

                for (int x = (int)startX; x < endX; x++)
                {
                    for (int z = (int)startZ; z < endZ; z++)
                    {
                        double rsq = Math.Pow(x - regionOffsetToCenterX, 2)
                                   + Math.Pow(z - regionOffsetToCenterZ, 2);

                        if (rsq >= radiussq) continue;

                        double attn = Math.Pow(1 - rsq / radiussq, 3) * 512;

                        int finalX = x + pad;
                        int finalZ = z + pad;

                        if (finalX >= 0 && finalX < map.Size
                            && finalZ >= 0 && finalZ < map.Size)
                        {
                            map.SetInt(finalX, finalZ, (int)Math.Max(0, map.GetInt(finalX, finalZ) - attn));
                        }
                    }
                }
            }
        }

        private void ApplyForceClimate(IMapRegion mapRegion, int regionX, int regionZ, int pad, int regionsize, ForceClimate fl)
        {
            var map = mapRegion.ClimateMap;
            var innerSize = map.InnerSize;

            float wobbleIntensityBlocks = 80;
            var padRel_wobblepaduh = (float)pad / noiseSizeClimate + wobbleIntensityBlocks / regionsize;

            var minlf = -padRel_wobblepaduh;
            var maxlf = (1 + padRel_wobblepaduh);
            var transitionDist = 300f;
            var rad = fl.Radius + transitionDist;

            var startX = (fl.CenterPos.X - rad) / regionsize - regionX;
            var endX = (fl.CenterPos.X + rad) / regionsize - regionX;
            var startZ = (fl.CenterPos.Z - rad) / regionsize - regionZ;
            var endZ = (fl.CenterPos.Z + rad) / regionsize - regionZ;

            if (endX >= minlf && startX <= maxlf && endZ >= minlf && startZ <= maxlf)
            {
                var radiussq = Math.Pow((double)rad / regionsize * innerSize, 2);
                var transsq = Math.Pow((double)transitionDist / regionsize * innerSize, 2);
                var startTransitionFade = Math.Sqrt(radiussq) - Math.Sqrt(transsq);

                var centerRegionX = (double)fl.CenterPos.X / regionsize;
                var centerRegionZ = (double)fl.CenterPos.Z / regionsize;

                var regionOffsetToCenterX = (centerRegionX - regionX) * innerSize;
                var regionOffsetToCenterZ = (centerRegionZ - regionZ) * innerSize;

                startX = GameMath.Clamp(startX, minlf, maxlf) * innerSize - pad;
                endX = GameMath.Clamp(endX, minlf, maxlf) * innerSize + pad;
                startZ = GameMath.Clamp(startZ, minlf, maxlf) * innerSize - pad;
                endZ = GameMath.Clamp(endZ, minlf, maxlf) * innerSize + pad;

                var forceRain = (fl.Climate >> 8) & 0xff;
                var forceTemperature = (fl.Climate >> 16) & 0xff;

                for (var x = (int)startX; x < endX; x++)
                {
                    for (var z = (int)startZ; z < endZ; z++)
                    {
                        var rsq = Math.Pow(x - regionOffsetToCenterX, 2)
                                + Math.Pow(z - regionOffsetToCenterZ, 2);
                        if (rsq >= radiussq) continue;

                        var finalX = x + pad;
                        var finalZ = z + pad;

                        if (finalX >= 0 && finalX < map.Size
                            && finalZ >= 0 && finalZ < map.Size)
                        {
                            var climate = map.GetInt(finalX, finalZ);
                            var geologicActivity = climate & 0xff;
                            var rain = (climate >> 8) & 0xff;
                            var temperature = (climate >> 16) & 0xff;

                            var mapDist = Math.Sqrt(rsq);
                            var distanceFadeStart = Math.Max(0, mapDist - startTransitionFade);
                            var lerpAmount = Math.Min(1, distanceFadeStart / startTransitionFade);

                            var newTemperature = (int)GameMath.Lerp(forceTemperature, temperature, lerpAmount);
                            var newRain = (int)GameMath.Lerp(forceRain, rain, lerpAmount);
                            var newClimate = (newTemperature << 16) + (newRain << 8) + geologicActivity;
                            map.SetInt(finalX, finalZ, newClimate);
                        }
                    }
                }
            }
        }
    }
}
