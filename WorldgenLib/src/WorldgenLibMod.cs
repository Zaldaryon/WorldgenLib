using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace WorldgenLib
{
    /// <summary>
    /// WorldgenLib mod entry point. Registers as a ModSystem and installs the
    /// worldgen hosts (GenMapsHost, GenTerraHost, GenTerraPostProcessHost,
    /// GenBlockLayersHost) when no conflicting full-replacement generator
    /// is detected. The hosts form one cooperative canonical pass; a
    /// foreign takeover is detected before that pass is initialized.
    /// </summary>
    public class WorldgenLibMod : ModSystem
    {
        private GenMapsHost? _genMapsHost;
        private GenTerraHost? _genTerraHost;
        private GenTerraPostProcessHost? _genTerraPostProcessHost;
        private GenBlockLayersHost? _genBlockLayersHost;
        private bool _worldGenInitialized;
        private bool _startupReported;
        private bool _enabled;

        /// <summary>GenMaps host. Available after AssetsFinalize.</summary>
        internal GenMapsHost? GenMapsHost => _genMapsHost;

        /// <summary>GenTerra host. Available after AssetsFinalize.</summary>
        internal GenTerraHost? GenTerraHost => _genTerraHost;

        /// <summary>GenTerraPostProcess host. Available after StartServerSide.</summary>
        internal GenTerraPostProcessHost? GenTerraPostProcessHost => _genTerraPostProcessHost;

        /// <summary>GenBlockLayers host. Available after StartServerSide.</summary>
        internal GenBlockLayersHost? GenBlockLayersHost => _genBlockLayersHost;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }

        public override double ExecuteOrder() => -1000;

        public override void AssetsFinalize(ICoreAPI coreApi)
        {
            if (coreApi is not ICoreServerAPI api) return;

            // Apply the narrow runtime seams before any StartServerSide runs.
            // Vanilla systems remain responsible for their own initialization;
            // only the duplicate generation callbacks are intercepted.
            GenMapsBlocker.Patch();
            GenTerraBlocker.Patch();
            GenBlockLayersPatch.Patch();

            if (!GenMapsBlocker.IsPatched || !GenTerraBlocker.IsPatched
                || !GenBlockLayersPatch.IsPatched
                || !GenBlockLayersPatch.TransformationAvailable)
            {
                api.Logger.Error(
                    "[WorldgenLib] Required worldgen seam is unavailable; WorldgenLib is disabled to avoid duplicate or missing generation.");
                GenBlockLayersPatch.Unpatch();
                GenMapsBlocker.Unpatch();
                GenTerraBlocker.Unpatch();
                _enabled = false;
                return;
            }

            _enabled = true;

            LandformRegistry.Initialize(api);

            // Initialize GenTerraHost sea level (same logic as vanilla GenTerra.AssetsFinalize)
            _genTerraHost = new GenTerraHost(api);
            _genTerraHost.AssetsFinalize();

            // Expose API for consumer mods
            WorldgenLibAPI.Initialize(this);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (!_enabled) return;

            _apiForDiagnostics = api;

            // ── GenMapsHost ──
            _genMapsHost = new GenMapsHost(api);
            if (_genTerraHost == null)
                throw new InvalidOperationException(
                    "[WorldgenLib] GenTerraHost is unavailable during server startup.");
            _genMapsHost.SetLandformRegionInvalidator(_genTerraHost.InvalidateLandformRegion);
            _genBlockLayersHost = new GenBlockLayersHost(api);

            // GenMaps may be silenced by a foreign full replacement (for
            // example Watersheds). Register an independent init callback so
            // WorldgenLib still has a deterministic lifecycle boundary. When
            // vanilla GenMaps is present, its own init callback and the
            // patched postfix refresh the host after vanilla globals settle.
            api.Event.InitWorldGenerator(InitializeWorldGen, "standard");
            api.Event.InitWorldGenerator(InitializeWorldGen, "superflat");

            api.Event.MapRegionGeneration(OnMapRegionGen, "standard");
            api.Event.MapRegionGeneration(OnMapRegionGen, "superflat");

            // Send latitude data to joining players
            api.Event.PlayerJoin += (plr) =>
            {
                if (_genMapsHost != null)
                    api.Network.GetChannel("latitudedata").SendPacket(_genMapsHost.latdata, plr);
            };

            // ── GenTerraHost ──

            api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.Terrain, "standard");

            // ── GenTerraPostProcessHost ──
            _genTerraPostProcessHost = new GenTerraPostProcessHost(api);
            _genTerraPostProcessHost.StartServerSide();

            // Initialization and freezing happen from the callback above (or
            // the vanilla GenMaps postfix refresh). At this point every
            // StartServerSide consumer has had the chance to register its
            // adapters.
        }

        public override void Dispose()
        {
            _genMapsHost?.Dispose();
            _genTerraHost?.Dispose();
            _genTerraPostProcessHost?.Dispose();
            GenBlockLayersPatch.Unpatch();
            GenMapsBlocker.Unpatch();
            GenTerraBlocker.Unpatch();
            WorldgenLibAPI.Reset();
            ConflictDetector.Reset();
            TerrainSampler.Reset();
            LandformRegistry.Reset();
            RegionMapRegistry.Reset();
            _genMapsHost = null;
            _genTerraHost = null;
            _genTerraPostProcessHost = null;
            _genBlockLayersHost = null;
            _worldGenInitialized = false;
            _startupReported = false;
            _enabled = false;
        }

        private void OnMapRegionGen(IMapRegion mapRegion, int regionX, int regionZ, ITreeAttribute chunkGenParams)
        {
            if (ConflictDetector.HasBlockingConflicts) return;
            _genMapsHost?.OnMapRegionGen(mapRegion, regionX, regionZ, chunkGenParams);
        }

        private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
        {
            if (ConflictDetector.HasBlockingConflicts) return;
            _genTerraHost?.OnChunkColumnGen(request);
        }

        private void InitializeWorldGen()
        {
            if (!_enabled || _genMapsHost == null || _genTerraHost == null) return;

            if (_worldGenInitialized)
                return;

            bool blocksGeneration = ConflictDetector.Detect(
                _apiForDiagnostics ?? throw new InvalidOperationException(
                    "[WorldgenLib] Server API is unavailable during worldgen initialization."));
            if (blocksGeneration)
            {
                // Leave all vanilla/foreign callbacks enabled. The seam
                // prefixes observe this report and yield to the original
                // pipeline, so WorldgenLib never creates a second generator.
                _worldGenInitialized = true;
                StartupReport.Print(_apiForDiagnostics, _genMapsHost, _genTerraHost,
                    _genTerraPostProcessHost, _genBlockLayersHost);
                _startupReported = true;
                return;
            }

            // Vanilla GenMaps may reload NoiseLandforms during its own init.
            // Rebind immediately before constructing the host so every index
            // used by the terrain pass is canonical.
            LandformRegistry.RebindCurrent();
            // The vanilla init callback can receive story-structure force
            // requests before this host is initialized. Preserve the queue on
            // the first pass; the host replays it once its map scales exist.
            _genMapsHost.InitWorldGen(preserveRequestedForces: true);
            _genTerraHost.InitWorldGen();

            _genMapsHost.FreezeHooks();
            _genTerraHost.FreezeHooks();
            _genBlockLayersHost?.FreezeHooks();
            _genTerraPostProcessHost?.FreezeHooks();
            LandformRegistry.Freeze();
            RegionMapRegistry.Freeze();
            TerrainSampler.Initialize(this);

            _worldGenInitialized = true;

            if (!_startupReported)
            {
                StartupReport.Print(_apiForDiagnostics, _genMapsHost, _genTerraHost,
                    _genTerraPostProcessHost, _genBlockLayersHost);
                _startupReported = true;
            }
        }

        private ICoreServerAPI? _apiForDiagnostics;

        internal static void NotifyVanillaGenMapsInitialized()
        {
            WorldgenLibAPI.Instance?.HandleVanillaGenMapsInitialized();
        }

        internal static bool TryForwardForceClimate(ForceClimate climate)
        {
            GenMapsHost? host = WorldgenLibAPI.Instance?.GenMapsHost;
            if (host == null) return false;
            host.ForceClimateAt(climate);
            return true;
        }

        internal static bool TryForwardForceLandform(ForceLandform landform)
        {
            GenMapsHost? host = WorldgenLibAPI.Instance?.GenMapsHost;
            if (host == null) return false;
            host.ForceLandformAt(landform);
            return true;
        }

        internal static bool TryForwardForceRandomLandArea(int positionX, int positionZ, int radius)
        {
            GenMapsHost? host = WorldgenLibAPI.Instance?.GenMapsHost;
            if (host == null) return false;
            host.ForceRandomLandArea(positionX, positionZ, radius);
            return true;
        }

        internal void HandleVanillaGenMapsInitialized()
        {
            if (!_enabled || _genMapsHost == null || _genTerraHost == null
                || ConflictDetector.HasBlockingConflicts)
                return;

            // WorldgenLib's own InitWorldGenerator callback is the fallback for
            // a foreign mod that suppresses GenMaps.StartServerSide. If the
            // native GenMaps callback also ran, refresh after it so landform
            // and other vanilla static state are the final inputs.
            if (!_worldGenInitialized)
            {
                InitializeWorldGen();
                return;
            }

            LandformRegistry.RebindCurrent();
            _genMapsHost.InitWorldGen(preserveRequestedForces: true);
            _genTerraHost.InitWorldGen();
        }

        internal static bool TryHandleBlockLayers(IChunkColumnGenerateRequest request)
        {
            GenBlockLayersHost? host = WorldgenLibAPI.TryGetBlockLayersHost();
            return host != null && host.TryRunFullGeneration(request);
        }

        internal static bool CanHandleMapGeneration
            => WorldgenLibAPI.Instance?.IsMapGenerationReady == true;

        internal static bool CanHandleTerrainGeneration
            => WorldgenLibAPI.Instance?.IsTerrainGenerationReady == true;

        internal static bool CanHandlePostProcess
            => WorldgenLibAPI.Instance?.IsPostProcessReady == true;

        internal bool IsMapGenerationReady
            => _worldGenInitialized && _genMapsHost?.IsInitialized == true;

        internal bool IsTerrainGenerationReady
            => _worldGenInitialized && _genTerraHost?.IsInitialized == true;

        internal bool IsPostProcessReady
            => _worldGenInitialized && _genTerraPostProcessHost?.IsRegistered == true;
    }
}
