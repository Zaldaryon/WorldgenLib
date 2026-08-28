using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace WorldgenLib.Tests
{
    public class ParityHarnessTests : IDisposable
    {
        private readonly string _testDir;

        public ParityHarnessTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"worldgenlib-parity-test-{Guid.NewGuid():N}");
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }

        [Fact]
        public void SnapshotRegion_CreatesFiles()
        {
            var harness = new ParityHarness(_testDir);
            var maps = new Dictionary<string, int[]>
            {
                ["climate"] = new[] { 1, 2, 3, 4 },
                ["ocean"] = new[] { 0, 0, 1, 1 }
            };

            harness.SnapshotRegion(42, 5, 10, maps);

            Assert.True(File.Exists(Path.Combine(_testDir, "seed42", "r5_10", "climate.json")));
            Assert.True(File.Exists(Path.Combine(_testDir, "seed42", "r5_10", "ocean.json")));
        }

        [Fact]
        public void SnapshotChunk_CreatesFiles()
        {
            var harness = new ParityHarness(_testDir);
            var blocks = new int[4, 4, 4];
            var terrainHeight = new ushort[16];
            var rainHeight = new ushort[16];

            harness.SnapshotChunk(42, 3, 7, blocks, terrainHeight, rainHeight);

            var dir = Path.Combine(_testDir, "seed42", "c3_7");
            Assert.True(File.Exists(Path.Combine(dir, "blocks.json")));
            Assert.True(File.Exists(Path.Combine(dir, "terrainHeightMap.json")));
            Assert.True(File.Exists(Path.Combine(dir, "rainHeightMap.json")));
        }

        [Fact]
        public void DiffAgainstVanilla_Identical_NoDifferences()
        {
            var harness = new ParityHarness(_testDir);
            var maps = new Dictionary<string, int[]> { ["climate"] = new[] { 1, 2 } };
            harness.SnapshotRegion(1, 0, 0, maps);

            // Use the same directory as "expected" — should be identical.
            var diffs = harness.DiffAgainstVanilla(_testDir);
            Assert.Empty(diffs);
        }

        [Fact]
        public void DiffAgainstVanilla_Different_DetectsDifferences()
        {
            var actualDir = Path.Combine(_testDir, "actual");
            var expectedDir = Path.Combine(_testDir, "expected");
            Directory.CreateDirectory(actualDir);
            Directory.CreateDirectory(expectedDir);

            // Write different values.
            File.WriteAllText(Path.Combine(actualDir, "data.json"), "{\"value\":1}");
            File.WriteAllText(Path.Combine(expectedDir, "data.json"), "{\"value\":2}");

            var harness = new ParityHarness(actualDir);
            var diffs = harness.DiffAgainstVanilla(expectedDir);
            Assert.Single(diffs);
            Assert.Contains("Data mismatch", diffs[0]);
        }

        [Fact]
        public void DiffAgainstVanilla_MissingFile_DetectsDifference()
        {
            var actualDir = Path.Combine(_testDir, "actual");
            var expectedDir = Path.Combine(_testDir, "expected");
            Directory.CreateDirectory(actualDir);
            Directory.CreateDirectory(expectedDir);

            File.WriteAllText(Path.Combine(expectedDir, "data.json"), "{}");

            var harness = new ParityHarness(actualDir);
            var diffs = harness.DiffAgainstVanilla(expectedDir);
            Assert.Single(diffs);
            Assert.Contains("Missing in WorldgenLib", diffs[0]);
        }

        [Fact]
        public void TestSeeds_HasAtLeast5Entries()
        {
            Assert.True(ParityHarness.TestSeeds.Length >= 5);
        }
    }
}
