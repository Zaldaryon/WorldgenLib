using System;
using System.Reflection;
using Xunit;

namespace WorldgenLib.Tests
{
    public class GenMapsHostTests
    {
        [Fact]
        public void GenMapsHost_HasNineMapSteps()
        {
            // Verify the nine map layer fields exist on GenMapsHost
            var type = typeof(GenMapsHost);
            string[] expectedFields = {
                "geologicprovinceGen",
                "climateGen",
                "forestGen",
                "upheavelGen",
                "oceanGen",
                "beachGen",
                "bushGen",
                "flowerGen",
                "landformsGen"
            };

            foreach (var fieldName in expectedFields)
            {
                var field = type.GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(field != null, $"GenMapsHost missing field: {fieldName}");
            }
        }

        [Fact]
        public void GenMapsHost_HasNoiseSizeFields()
        {
            var type = typeof(GenMapsHost);
            string[] expectedFields = {
                "noiseSizeGeoProv",
                "noiseSizeClimate",
                "noiseSizeForest",
                "noiseSizeUpheavel",
                "noiseSizeOcean",
                "noiseSizeBeach",
                "noiseSizeShrubs",
                "noiseSizeLandform"
            };

            foreach (var fieldName in expectedFields)
            {
                var field = type.GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(field != null, $"GenMapsHost missing noise size field: {fieldName}");
                Assert.Equal(typeof(int), field!.FieldType);
            }
        }

        [Fact]
        public void GenMapsHost_HasForceCollections()
        {
            var type = typeof(GenMapsHost);
            var forceLandforms = type.GetField("forceLandforms",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var forceClimate = type.GetField("forceClimate",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var requireLandAt = type.GetField("requireLandAt",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.NotNull(forceLandforms);
            Assert.NotNull(forceClimate);
            Assert.NotNull(requireLandAt);
        }

        [Fact]
        public void GenMapsHost_HasInitWorldGenMethod()
        {
            var method = typeof(GenMapsHost).GetMethod("InitWorldGen",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
        }

        [Fact]
        public void GenMapsHost_HasOnMapRegionGenMethod()
        {
            var method = typeof(GenMapsHost).GetMethod("OnMapRegionGen",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
        }

        [Fact]
        public void GenMapsHost_HasPublicAPI()
        {
            var type = typeof(GenMapsHost);
            Assert.NotNull(type.GetMethod("ForceClimateAt"));
            Assert.NotNull(type.GetMethod("ForceLandformAt"));
            Assert.NotNull(type.GetMethod("ForceLandAt"));
            Assert.NotNull(type.GetMethod("ForceRandomLandArea"));
        }

        [Fact]
        public void GenMapsBlocker_HasHarmonyInstance()
        {
            var prop = typeof(GenMapsBlocker).GetProperty("HarmonyInstance",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prop);
        }
    }
}
