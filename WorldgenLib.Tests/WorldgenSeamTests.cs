using System;
using System.Linq;
using Xunit;

namespace WorldgenLib.Tests
{
    public class WorldgenSeamTests
    {
        [Fact]
        public void GenMapsHost_ExposesMigrationSeams()
        {
            Assert.NotNull(typeof(GenMapsHost).GetMethod("RegisterMapGenerator"));
            Assert.NotNull(typeof(GenMapsHost).GetMethod("RegisterMapPadding"));
            Assert.NotNull(typeof(GenMapsHost).GetMethod("RegisterFullRegionGeneration"));
            Assert.NotNull(typeof(GenMapsHost).GetMethod("GetMapGenerator"));
            Assert.NotNull(typeof(GenMapsHost).GetMethod("RequireLandAt"));
            Assert.NotNull(typeof(WorldgenLibAPI).GetMethod("ForceRandomLandArea"));
            Assert.NotNull(typeof(WorldgenLibAPI).GetMethod("RequireLandAt"));
        }

        [Fact]
        public void GenTerraAndBlockLayers_ExposeTerminalSeams()
        {
            Assert.NotNull(typeof(GenTerraHost).GetMethod("RegisterFullGeneration"));
            Assert.NotNull(typeof(GenBlockLayersHost).GetMethod("RegisterFullGeneration"));
            Assert.NotNull(typeof(GenBlockLayersHost).GetMethod("RegisterRaiseModifier"));
            Assert.NotNull(typeof(GenBlockLayersHost).GetMethod("RegisterSeaLevelRiseFilter"));
        }

        [Fact]
        public void GenBlockLayersPatch_TransformsReferenceVintageStoryMethod()
        {
            // Load the reference DLL explicitly because production references
            // are Private=false and are intentionally not copied to testhost.
            TestAssemblyLoader.EnsureVSEssentialsLoaded();
            GenBlockLayersPatch.Unpatch();
            GenBlockLayersPatch.Patch();
            try
            {
                Assert.True(GenBlockLayersPatch.IsPatched);
                Assert.True(GenBlockLayersPatch.TransformationAvailable,
                    "The 1.22.7 GenBlockLayers raise pattern must remain supported.");
            }
            finally
            {
                GenBlockLayersPatch.Unpatch();
            }
        }

        [Fact]
        public void ConflictDetector_ManualReportsAreAdvisoryByDefault()
        {
            const string modId = "test-advisory-unique";
            ConflictDetector.Report(modId, "test", "manual finding");
            var report = ConflictDetector.Reports.Single(item => item.OffendingModId == modId);
            Assert.False(report.IsBlocking);
        }

        [Fact]
        public void TerrainSampler_ExposesWatershedsCompatibleBatchBoundaries()
        {
            Assert.NotNull(typeof(TerrainSampler).GetMethod("SampleTerrainHeight"));
            Assert.NotNull(typeof(TerrainSampler).GetMethod("SampleTerrainHeightsBatch"));
            Assert.NotNull(typeof(TerrainSampler).GetMethod("SampleBaseTerrainHeight"));
            Assert.NotNull(typeof(TerrainSampler).GetMethod("SampleBaseTerrainHeightsBatch"));
        }

        [Fact]
        public void ColumnCarvingContext_UsesFloorModuloForNegativeWorldCoordinates()
        {
            TestAssemblyLoader.EnsureVSEssentialsLoaded();
            var context = new ColumnCarvingContext(-1, -33, 32, 64, 2);
            Assert.Equal(31, context.LocalX);
            Assert.Equal(31, context.LocalZ);
        }
    }
}
