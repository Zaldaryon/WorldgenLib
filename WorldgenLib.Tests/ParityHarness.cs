using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WorldgenLib.Tests
{
    /// <summary>
    /// Parity harness driver. Generates a fixed set of seeds and configs,
    /// snapshots chunk block data, both heightmaps, and the region maps
    /// for diffing against vanilla.
    ///
    /// Usage from a full Vintage Story server driver (Phase 2+):
    ///   var harness = new ParityHarness(outputDir);
    ///   harness.SnapshotRegion(seed, regionX, regionZ, regionData);
    ///   harness.SnapshotChunk(seed, chunkX, chunkZ, blocks, heightmap);
    ///   var diff = harness.DiffAgainstVanilla(expectedDir);
    ///
    /// This is the scaffolding. The actual server-side driver that
    /// calls GenMaps/GenTerra/GenTerraPostProcess directly will be
    /// implemented in Phase 2 once GenMapsHost is functional.
    /// </summary>
    public sealed class ParityHarness
    {
        private readonly string _outputDir;

        /// <summary>
        /// Standard seed set for parity testing.
        /// These seeds are chosen to exercise different terrain profiles:
        /// - Low seed: simple flat terrain
        /// - Medium seed: varied terrain
        /// - High seed: extreme terrain
        /// - Specific seeds: known to trigger edge cases (ocean, mountains, rivers)
        /// </summary>
        public static readonly int[] TestSeeds = { 1, 42, 12345, 65536, 999999 };

        public ParityHarness(string outputDir)
        {
            _outputDir = outputDir ?? throw new ArgumentNullException(nameof(outputDir));
            Directory.CreateDirectory(_outputDir);
        }

        /// <summary>
        /// Snapshot a region's map data to JSON.
        /// </summary>
        public void SnapshotRegion(int seed, int regionX, int regionZ,
            Dictionary<string, int[]> regionMaps)
        {
            var dir = Path.Combine(_outputDir, $"seed{seed}", $"r{regionX}_{regionZ}");
            Directory.CreateDirectory(dir);

            foreach (var kvp in regionMaps)
            {
                var path = Path.Combine(dir, $"{kvp.Key}.json");
                var json = JsonSerializer.Serialize(kvp.Value, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
        }

        /// <summary>
        /// Snapshot a chunk's block data and heightmaps.
        /// </summary>
        public void SnapshotChunk(int seed, int chunkX, int chunkZ,
            int[,,] blocks, ushort[] terrainHeightMap, ushort[] rainHeightMap)
        {
            var dir = Path.Combine(_outputDir, $"seed{seed}", $"c{chunkX}_{chunkZ}");
            Directory.CreateDirectory(dir);

            // Serialize blocks as a flat array (chunksize^3)
            var flatBlocks = new int[blocks.Length];
            Buffer.BlockCopy(blocks, 0, flatBlocks, 0, blocks.Length * sizeof(int));
            File.WriteAllText(Path.Combine(dir, "blocks.json"),
                JsonSerializer.Serialize(flatBlocks));

            File.WriteAllText(Path.Combine(dir, "terrainHeightMap.json"),
                JsonSerializer.Serialize(terrainHeightMap));
            File.WriteAllText(Path.Combine(dir, "rainHeightMap.json"),
                JsonSerializer.Serialize(rainHeightMap));
        }

        /// <summary>
        /// Compare a snapshot directory against expected vanilla output.
        /// Returns a list of differences (empty if identical).
        /// </summary>
        public List<string> DiffAgainstVanilla(string expectedDir)
        {
            var diffs = new List<string>();

            if (!Directory.Exists(expectedDir))
            {
                diffs.Add($"Expected directory does not exist: {expectedDir}");
                return diffs;
            }

            // Walk all files in the output and compare with expected.
            foreach (var file in Directory.GetFiles(_outputDir, "*.json", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(_outputDir, file);
                var expectedFile = Path.Combine(expectedDir, relativePath);

                if (!File.Exists(expectedFile))
                {
                    diffs.Add($"Missing in vanilla: {relativePath}");
                    continue;
                }

                var actual = File.ReadAllText(file);
                var expected = File.ReadAllText(expectedFile);

                if (actual != expected)
                {
                    diffs.Add($"Data mismatch: {relativePath}");
                }
            }

            // Check for files in expected that don't exist in actual.
            foreach (var file in Directory.GetFiles(expectedDir, "*.json", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(expectedDir, file);
                var actualFile = Path.Combine(_outputDir, relativePath);

                if (!File.Exists(actualFile))
                {
                    diffs.Add($"Missing in WorldgenLib: {relativePath}");
                }
            }

            return diffs;
        }
    }
}
