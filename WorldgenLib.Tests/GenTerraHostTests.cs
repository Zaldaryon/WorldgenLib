using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace WorldgenLib.Tests
{
    public class GenTerraHostTests
    {
        [Fact]
        public void GenTerraHost_HasDistortionConstants()
        {
            var type = typeof(GenTerraHost);

            // Check for private constants via reflection
            var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Static);
            var fieldNames = new System.Collections.Generic.HashSet<string>();
            foreach (var f in fields) fieldNames.Add(f.Name);

            Assert.Contains("TerrainDistortionMultiplier", fieldNames);
            Assert.Contains("TerrainDistortionThreshold", fieldNames);
            Assert.Contains("GeoDistortionMultiplier", fieldNames);
            Assert.Contains("GeoDistortionThreshold", fieldNames);
            Assert.Contains("MaxDistortionAmount", fieldNames);
        }

        [Fact]
        public void GenTerraHost_HasStepMethods()
        {
            var type = typeof(GenTerraHost);
            string[] expectedMethods = {
                "Step0_BorderTaperPrepare",
                "InitWorldGen",
                "OnChunkColumnGen",
                "AssetsFinalize"
            };

            foreach (var name in expectedMethods)
            {
                var method = type.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.NotNull(method);
            }
        }

        [Fact]
        public void GenTerraHost_HasHelperMethods()
        {
            TestAssemblyLoader.EnsureVSEssentialsLoaded();
            var type = typeof(GenTerraHost);
            string[] expectedMethods = {
                "GetOrLoadLerpedLandformMap",
                "GetInterpolatedOctaves",
                "StartSampleDisplacedYThreshold",
                "ContinueSampleDisplacedYThreshold",
                "ComputeOceanAndUpheavalDistY",
                "ComputeGeoUpheavalTaper",
                "NewDistortionNoise",
                "ApplyIsotropicDistortionThreshold",
                "ChunkIndex3d",
                "ChunkIndex2d"
            };

            foreach (var name in expectedMethods)
            {
                bool exists = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(method => method.Name == name);
                Assert.True(exists, $"GenTerraHost missing helper method: {name}");
            }
        }

        [Fact]
        public void GenTerraHost_HasNoiseFieldsAndPerRequestScratch()
        {
            var type = typeof(GenTerraHost);
            string[] expectedFields = {
                "_terrainNoise",
                "_distort2dx",
                "_distort2dz",
                "_geoUpheavalNoise"
            };

            foreach (var name in expectedFields)
            {
                var field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(field != null, $"GenTerraHost missing field: {name}");
            }

            var scratchType = type.GetNestedType("GenerationScratch",
                BindingFlags.NonPublic);
            Assert.NotNull(scratchType);
            foreach (var name in new[] {
                "TaperMap", "ColumnResults", "LayerFullySolid",
                "LayerFullyEmpty", "BorderIndicesByCardinal"
            })
            {
                Assert.NotNull(scratchType!.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
            }

            // These buffers are request-local. Keeping them on the host would
            // make concurrent terrain requests overwrite one another.
            foreach (var name in new[] {
                "_taperMap", "_columnResults", "_layerFullySolid",
                "_layerFullyEmpty", "_borderIndicesByCardinal"
            })
            {
                Assert.Null(type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
            }
        }

        [Fact]
        public void GenTerraBlocker_HasHarmonyPatches()
        {
            var type = typeof(GenTerraBlocker);
            Assert.NotNull(type.GetProperty("HarmonyInstance",
                BindingFlags.Static | BindingFlags.NonPublic));
        }
    }
}
