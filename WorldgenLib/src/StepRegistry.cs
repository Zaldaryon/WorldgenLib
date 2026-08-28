using System;
using System.Collections.Generic;

namespace WorldgenLib
{
    /// <summary>
    /// Versioned step registry. Each step has a stable string ID, a
    /// human-readable name, and a version at which it was introduced.
    /// When Vintage Story restructures the vanilla code, the remap table
    /// maps old step IDs to new ones so consumer delegates keep applying.
    /// A removed step degrades to a logged no-op.
    /// </summary>
    public sealed class StepRegistry
    {
        /// <summary>Current step list version. Bump when steps are added, removed, or reordered.</summary>
        public const int CurrentVersion = 2;

        /// <summary>Product version of WorldgenLib.</summary>
        public const string ProductVersion = "0.1.0";

        private readonly Dictionary<string, StepDefinition> _steps = new();
        private readonly Dictionary<string, string> _remapTable = new();

        /// <summary>
        /// Metadata for a single pipeline step. Immutable after registration.
        /// </summary>
        public sealed class StepDefinition
        {
            /// <summary>Stable step ID (e.g. "terra:step7-threshold").</summary>
            public string Id { get; init; } = "";

            /// <summary>Human-readable name (e.g. "PerVoxelThreshold").</summary>
            public string Name { get; init; } = "";

            /// <summary>Host class that owns this step (e.g. "GenTerraHost").</summary>
            public string Host { get; init; } = "";

            /// <summary>WorldgenLib version when this step was introduced.</summary>
            public int IntroducedVersion { get; init; }

            /// <summary>Whether this step is active (false if remapped to another step).</summary>
            public bool IsActive { get; set; } = true;
        }

        public IReadOnlyDictionary<string, StepDefinition> Steps => _steps;
        public IReadOnlyDictionary<string, string> RemapTable => _remapTable;

        public StepRegistry()
        {
            // ── GenMapsHost steps ──
            Register("maps:geoprovince", "Geoprovince", "GenMapsHost", 1);
            Register("maps:climate", "Climate", "GenMapsHost", 1);
            Register("maps:forest", "Forest", "GenMapsHost", 1);
            Register("maps:upheavel", "Upheavel", "GenMapsHost", 1);
            Register("maps:ocean", "Ocean", "GenMapsHost", 1);
            Register("maps:beach", "Beach", "GenMapsHost", 1);
            Register("maps:shrub", "Shrub", "GenMapsHost", 1);
            Register("maps:biome", "Biome", "GenMapsHost", 1);
            Register("maps:landform", "Landform", "GenMapsHost", 1);
            Register("maps:region-finalize", "RegionFinalize", "GenMapsHost", 1);

            // ── GenTerraHost steps ──
            Register("terra:step0-border-taper", "BorderTaperPrepare", "GenTerraHost", 1);
            Register("terra:step1-load-region", "LoadRegionInputs", "GenTerraHost", 1);
            Register("terra:step2-build-octaves", "BuildCornerOctaves", "GenTerraHost", 1);
            Register("terra:step3-h-distortion", "HorizontalDistortion", "GenTerraHost", 1);
            Register("terra:step4-v-distortion", "VerticalDistortion", "GenTerraHost", 1);
            Register("terra:step5-water-select", "WaterColumnSelect", "GenTerraHost", 1);
            Register("terra:step6-noise-setup", "ColumnNoiseSetup", "GenTerraHost", 1);
            Register("terra:step7-threshold", "PerVoxelThreshold", "GenTerraHost", 1);
            Register("terra:step8-solidity", "SolidityResolve", "GenTerraHost", 1);
            Register("terra:step9-place-blocks", "PlaceBlocks", "GenTerraHost", 1);
            Register("terra:step10-post-place", "PostPlacementColumn", "GenTerraHost", 1);
            Register("terra:terrain-finalize", "TerrainFinalize", "GenTerraHost", 2);

            // ── GenTerraPostProcessHost steps ──
            Register("postprocess:cleanup-floating", "CleanupFloatingNodes", "GenTerraPostProcessHost", 1);

            // ── GenBlockLayersHost steps ──
            Register("blocklayers:raise-modifier", "RaiseModifier", "GenBlockLayersHost", 1);
            Register("blocklayers:sea-level-filter", "SeaLevelRiseFilter", "GenBlockLayersHost", 1);
        }

        private void Register(string id, string name, string host, int version)
        {
            _steps[id] = new StepDefinition
            {
                Id = id,
                Name = name,
                Host = host,
                IntroducedVersion = version
            };
        }

        /// <summary>
        /// Add a remap entry: when a step with oldId is encountered,
        /// redirect delegates to newId.
        /// </summary>
        public void AddRemap(string oldId, string newId)
        {
            _remapTable[oldId] = newId;

            // Mark the old step as inactive
            if (_steps.TryGetValue(oldId, out var oldStep))
                oldStep.IsActive = false;
        }

        /// <summary>
        /// Resolve a step ID, applying remaps if needed.
        /// Returns the current step ID, or null if the step was removed
        /// and has no remap.
        /// </summary>
        public string? ResolveStepId(string requestedId)
        {
            if (string.IsNullOrWhiteSpace(requestedId))
            {
                Console.WriteLine("[WorldgenLib] WARN: Empty step ID — no remap defined.");
                return null;
            }
            if (_remapTable.TryGetValue(requestedId, out var remapped))
                return remapped;

            if (_steps.ContainsKey(requestedId))
                return requestedId;

            // Unknown step — warn and return null
            Console.WriteLine($"[WorldgenLib] WARN: Unknown step ID '{requestedId}' — no remap defined.");
            return null;
        }

        /// <summary>
        /// Get the startup report section for step versions.
        /// </summary>
        public string GetVersionReport()
        {
            var lines = new List<string>
            {
                $"WorldgenLib v{ProductVersion} — Step list v{CurrentVersion}",
                $"Steps registered: {_steps.Count}",
                $"Remap entries: {_remapTable.Count}",
                ""
            };

            foreach (var step in _steps.Values)
            {
                var status = step.IsActive ? "active" : "REMAP";
                lines.Add($"  [{status}] {step.Id} → {step.Name} ({step.Host}, v{step.IntroducedVersion})");
            }

            if (_remapTable.Count > 0)
            {
                lines.Add("");
                lines.Add("Remap table:");
                foreach (var (oldId, newId) in _remapTable)
                    lines.Add($"  {oldId} → {newId}");
            }

            return string.Join("\n", lines);
        }
    }
}
