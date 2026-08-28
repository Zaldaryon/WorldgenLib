using System;
using System.Collections.Generic;
using Xunit;

namespace WorldgenLib.Tests
{
    public class GenTerraHostHookTests
    {
        [Fact]
        public void RegistrationMethods_Exist()
        {
            var type = typeof(GenTerraHost);
            Assert.NotNull(type.GetMethod("RegisterStep0"));
            Assert.NotNull(type.GetMethod("RegisterStep2"));
            Assert.NotNull(type.GetMethod("RegisterStep4"));
            Assert.NotNull(type.GetMethod("RegisterStep5"));
            Assert.NotNull(type.GetMethod("RegisterStep7"));
            Assert.NotNull(type.GetMethod("RegisterStep10"));
            Assert.NotNull(type.GetMethod("RegisterTerrainFinalize"));
            Assert.NotNull(type.GetMethod("RegisterFullGeneration"));
        }

        [Fact]
        public void FreezeHooks_MethodExists()
        {
            var method = typeof(GenTerraHost).GetMethod("FreezeHooks",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
        }

        [Fact]
        public void HookOrdering_StableByOrderThenIndex()
        {
            var list = new OrderedHookList<string>();
            list.Register("c", 10, "handler-c");
            list.Register("a", 5, "handler-a");
            list.Register("b", 10, "handler-b"); // same order as c
            list.Freeze();

            var results = new List<string>();
            foreach (var (order, modId, handler) in list.Enumerate())
                results.Add(handler);

            Assert.Equal(new[] { "handler-a", "handler-c", "handler-b" }, results);
        }

        [Fact]
        public void HookErrorIsolation_ExceptionDoesNotCrash()
        {
            var list = new OrderedHookList<Action>();
            list.Register("good-mod", 0, () => { });
            list.Register("bad-mod", 1, () => throw new InvalidOperationException("test"));
            list.Register("good-mod-2", 2, () => { });
            list.Freeze();

            int callCount = 0;
            foreach (var (order, modId, hook) in list.Enumerate())
            {
                try
                {
                    hook();
                    callCount++;
                }
                catch (Exception) { /* D15: skip */ }
            }

            Assert.Equal(2, callCount);
        }

        [Fact]
        public void DelegateTypes_Exist()
        {
            var assembly = typeof(GenTerraHost).Assembly;
            Assert.NotNull(assembly.GetType("WorldgenLib.BorderTaperHook"));
            Assert.NotNull(assembly.GetType("WorldgenLib.BuildOctavesHook"));
            Assert.NotNull(assembly.GetType("WorldgenLib.VerticalDistortionHook"));
            Assert.NotNull(assembly.GetType("WorldgenLib.WaterSelectHook"));
            Assert.NotNull(assembly.GetType("WorldgenLib.ThresholdHook"));
            Assert.NotNull(assembly.GetType("WorldgenLib.PostPlacementHook"));
            Assert.NotNull(assembly.GetType("WorldgenLib.TerrainFinalizeHook"));
            Assert.NotNull(assembly.GetType("WorldgenLib.TerrainGenerationHook"));
        }

        [Fact]
        public void ContextTypes_Exist()
        {
            TestAssemblyLoader.EnsureVSEssentialsLoaded();
            Assert.False(typeof(ChunkContext).IsAbstract);
            Assert.True(typeof(ColumnContext).IsValueType);
            Assert.True(typeof(ColumnCarvingContext).IsValueType);
            Assert.NotNull(typeof(ChunkContext).GetProperty("Chunks"));
            Assert.NotNull(typeof(ChunkContext).GetProperty("MapChunk"));
            Assert.NotNull(typeof(ColumnCarvingContext).GetMethod("GetBlockDataAtY"));
        }

        [Fact]
        public void WeightedTaper_IsPublicValueType()
        {
            // WeightedTaper must be public for ChunkContext to reference it
            var type = typeof(GenTerraHost.WeightedTaper);
            Assert.True(type.IsNestedPublic);
            Assert.True(type.IsValueType);
        }
    }
}
