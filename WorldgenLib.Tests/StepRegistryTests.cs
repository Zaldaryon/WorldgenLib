using System;
using System.Linq;
using Xunit;

namespace WorldgenLib.Tests
{
    public class StepRegistryTests
    {
        [Fact]
        public void HasAllGenMapsSteps()
        {
            var registry = new StepRegistry();
            string[] expected = {
                "maps:geoprovince", "maps:climate", "maps:forest",
                "maps:upheavel", "maps:ocean", "maps:beach",
                "maps:shrub", "maps:biome", "maps:landform",
                "maps:region-finalize"
            };
            foreach (var id in expected)
                Assert.True(registry.Steps.ContainsKey(id), $"Missing maps step: {id}");
        }

        [Fact]
        public void HasAllGenTerraSteps()
        {
            var registry = new StepRegistry();
            string[] expected = {
                "terra:step0-border-taper", "terra:step1-load-region",
                "terra:step2-build-octaves", "terra:step3-h-distortion",
                "terra:step4-v-distortion", "terra:step5-water-select",
                "terra:step6-noise-setup", "terra:step7-threshold",
                "terra:step8-solidity", "terra:step9-place-blocks",
                "terra:step10-post-place", "terra:terrain-finalize"
            };
            foreach (var id in expected)
                Assert.True(registry.Steps.ContainsKey(id), $"Missing terra step: {id}");
        }

        [Fact]
        public void HasPostProcessStep()
        {
            var registry = new StepRegistry();
            Assert.True(registry.Steps.ContainsKey("postprocess:cleanup-floating"));
        }

        [Fact]
        public void HasBlockLayersSteps()
        {
            var registry = new StepRegistry();
            Assert.True(registry.Steps.ContainsKey("blocklayers:raise-modifier"));
            Assert.True(registry.Steps.ContainsKey("blocklayers:sea-level-filter"));
        }

        [Fact]
        public void RemapTable_Works()
        {
            var registry = new StepRegistry();
            registry.AddRemap("old:step-id", "terra:step7-threshold");

            Assert.Equal("terra:step7-threshold", registry.ResolveStepId("old:step-id"));
            // Old step is marked inactive if it existed
        }

        [Fact]
        public void ResolveStepId_Unknown_ReturnsNull()
        {
            var registry = new StepRegistry();
            Assert.Null(registry.ResolveStepId("nonexistent:step"));
        }

        [Fact]
        public void ResolveStepId_Known_ReturnsSame()
        {
            var registry = new StepRegistry();
            Assert.Equal("terra:step7-threshold", registry.ResolveStepId("terra:step7-threshold"));
        }

        [Fact]
        public void CurrentVersion_IsPositive()
        {
            Assert.True(StepRegistry.CurrentVersion > 0);
        }

        [Fact]
        public void ProductVersion_IsSet()
        {
            Assert.False(string.IsNullOrEmpty(StepRegistry.ProductVersion));
        }

        [Fact]
        public void GetVersionReport_ContainsInfo()
        {
            var registry = new StepRegistry();
            var report = registry.GetVersionReport();
            Assert.Contains("WorldgenLib", report);
            Assert.Contains("Steps registered:", report);
        }

        [Fact]
        public void StepCount_MatchesExpected()
        {
            var registry = new StepRegistry();
            // 10 maps + 12 terrain boundaries + 1 postprocess + 2 blocklayers = 25
            Assert.Equal(25, registry.Steps.Count);
        }
    }
}
