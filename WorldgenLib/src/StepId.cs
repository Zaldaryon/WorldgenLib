namespace WorldgenLib
{
    /// <summary>
    /// Stable step ID constants for the WorldgenLib worldgen pipeline.
    /// Use these when registering hooks to ensure forward compatibility.
    ///
    /// Steps without hook points (1, 3, 6, 8, 9) are listed for documentation
    /// but cannot have hooks registered on them.
    /// </summary>
    public static class StepId
    {
        // ════════════════════════════════════════════════════════════════
        //  GenTerraHost steps (with hook points)
        // ════════════════════════════════════════════════════════════════

        /// <summary>Step 0 — BorderTaperPrepare. Per-chunk. Mutable: TaperMap.</summary>
        public const string BorderTaper = "terra:step0-border-taper";

        /// <summary>Step 2 — BuildCornerOctaves. Per-column. Mutable: LandformWeights, Octaves.</summary>
        public const string BuildOctaves = "terra:step2-build-octaves";

        /// <summary>Step 4 — VerticalDistortion. Per-column. Mutable: DistY.</summary>
        public const string VerticalDistortion = "terra:step4-v-distortion";

        /// <summary>Step 5 — WaterColumnSelect. Per-column. Mutable: WaterBlockId.</summary>
        public const string WaterSelect = "terra:step5-water-select";

        /// <summary>Step 7 — PerVoxelThreshold. Per-column, per-Y. Return: modified threshold.</summary>
        public const string Threshold = "terra:step7-threshold";

        /// <summary>Step 10 — PostPlacementColumn. Per-column. Full block accessor.</summary>
        public const string PostPlacement = "terra:step10-post-place";

        /// <summary>
        /// Terrain finalization — once per generated column after placement
        /// and Step 10. Suitable for chunk-wide moddata persistence.
        /// </summary>
        public const string TerrainFinalize = "terra:terrain-finalize";

        // ════════════════════════════════════════════════════════════════
        //  GenTerraHost steps (no hook points — documentation only)
        // ════════════════════════════════════════════════════════════════

        /// <summary>Step 1 — LoadRegionInputs. No hooks: pure data read.</summary>
        public const string LoadRegion = "terra:step1-load-region";

        /// <summary>Step 3 — HorizontalDistortion. No hooks: noise-based.</summary>
        public const string HorizontalDistortion = "terra:step3-h-distortion";

        /// <summary>Step 6 — ColumnNoiseSetup. No hooks: internal noise state.</summary>
        public const string NoiseSetup = "terra:step6-noise-setup";

        /// <summary>Step 8 — SolidityResolve. No hooks: derived from threshold.</summary>
        public const string SolidityResolve = "terra:step8-solidity";

        /// <summary>Step 9 — PlaceBlocks. No hooks: bulk block placement.</summary>
        public const string PlaceBlocks = "terra:step9-place-blocks";

        // ════════════════════════════════════════════════════════════════
        //  GenMapsHost steps
        // ════════════════════════════════════════════════════════════════

        /// <summary>GenMaps Step 1 — Geoprovince map generation.</summary>
        public const string MapsGeoprovince = "maps:geoprovince";

        /// <summary>GenMaps Step 2 — Climate map generation.</summary>
        public const string MapsClimate = "maps:climate";

        /// <summary>GenMaps Step 3 — Forest map generation (depends on Climate).</summary>
        public const string MapsForest = "maps:forest";

        /// <summary>GenMaps Step 4 — Upheaval map generation.</summary>
        public const string MapsUpheavel = "maps:upheavel";

        /// <summary>GenMaps Step 5 — Ocean map generation.</summary>
        public const string MapsOcean = "maps:ocean";

        /// <summary>GenMaps Step 6 — Beach map generation.</summary>
        public const string MapsBeach = "maps:beach";

        /// <summary>GenMaps Step 7 — Shrub map generation (depends on Climate).</summary>
        public const string MapsShrub = "maps:shrub";

        /// <summary>GenMaps Step 8 — Biome map generation (depends on Climate).</summary>
        public const string MapsBiome = "maps:biome";

        /// <summary>GenMaps Step 9 — Landform map generation.</summary>
        public const string MapsLandform = "maps:landform";

        /// <summary>GenMaps RegionFinalize — after all maps, before DirtyForSaving.</summary>
        public const string MapsRegionFinalize = "maps:region-finalize";

        // ════════════════════════════════════════════════════════════════
        //  GenTerraPostProcessHost
        // ════════════════════════════════════════════════════════════════

        /// <summary>PostProcess — floating island cleanup (BFS flood, ≤40 blocks).</summary>
        public const string PostProcessCleanup = "postprocess:cleanup-floating";

        // ════════════════════════════════════════════════════════════════
        //  GenBlockLayersHost
        // ════════════════════════════════════════════════════════════════

        /// <summary>BlockLayers — terrain raise modifier at the vanilla final clamp.</summary>
        public const string BlockLayersRaise = "blocklayers:raise-modifier";

        /// <summary>BlockLayers — sea-level-rise filter at the vanilla final clamp.</summary>
        public const string BlockLayersSeaLevel = "blocklayers:sea-level-filter";
    }
}
