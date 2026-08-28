using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace WorldgenLib.Tests
{
    /// <summary>
    /// Behavioral tests for pure logic components that don't require
    /// Vintage Story APIs. These test actual behavior, not just structure.
    /// </summary>
    public class BehavioralTests
    {
        // ══════════════════════════════════════════════════════════════
        //  OrderedHookList — behavioral tests
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void OrderedHookList_EmptyList_HasZeroCount()
        {
            var list = new OrderedHookList<Action>();
            list.Freeze();
            Assert.Equal(0, list.Count);
        }

        [Fact]
        public void OrderedHookList_SingleHook_InvokedOnce()
        {
            var list = new OrderedHookList<Action>();
            int callCount = 0;
            list.Register("mod-a", 0, () => callCount++);
            list.Freeze();

            foreach (var (_, _, hook) in list.Enumerate())
                hook();

            Assert.Equal(1, callCount);
        }

        [Fact]
        public void OrderedHookList_MultipleHooks_InvokedInOrder()
        {
            var list = new OrderedHookList<string>();
            list.Register("c", 30, "c");
            list.Register("a", 10, "a");
            list.Register("b", 20, "b");
            list.Freeze();

            var results = list.Enumerate().Select(e => e.Delegate).ToList();
            Assert.Equal(new[] { "a", "b", "c" }, results);
        }

        [Fact]
        public void OrderedHookList_SameOrder_StableByRegistrationIndex()
        {
            var list = new OrderedHookList<string>();
            list.Register("mod-1", 5, "first");
            list.Register("mod-2", 5, "second");
            list.Register("mod-3", 5, "third");
            list.Freeze();

            var results = list.Enumerate().Select(e => e.Delegate).ToList();
            Assert.Equal(new[] { "first", "second", "third" }, results);
        }

        [Fact]
        public void OrderedHookList_NegativeOrder_BeforeZero()
        {
            var list = new OrderedHookList<string>();
            list.Register("before", -50, "before");
            list.Register("vanilla", 0, "vanilla");
            list.Register("after", 50, "after");
            list.Freeze();

            var results = list.Enumerate().Select(e => e.Delegate).ToList();
            Assert.Equal(new[] { "before", "vanilla", "after" }, results);
        }

        [Fact]
        public void OrderedHookList_ExceptionInOneHook_DoesNotStopOthers()
        {
            var list = new OrderedHookList<Action>();
            var invoked = new List<string>();
            list.Register("good-1", 0, () => invoked.Add("good-1"));
            list.Register("bad", 1, () => throw new InvalidOperationException("boom"));
            list.Register("good-2", 2, () => invoked.Add("good-2"));
            list.Freeze();

            foreach (var (_, _, hook) in list.Enumerate())
            {
                try { hook(); }
                catch (Exception) { }
            }

            Assert.Equal(new[] { "good-1", "good-2" }, invoked);
        }

        [Fact]
        public void OrderedHookList_ModId_TrackedInReport()
        {
            var list = new OrderedHookList<Action>();
            list.Register("river-mod", 50, () => { });
            list.Register("erosion-mod", 60, () => { });
            list.Freeze();

            var report = list.GetRegistrationReport();
            Assert.Equal(2, report.Count);
            Assert.Contains(report, e => e.ModId == "river-mod");
            Assert.Contains(report, e => e.ModId == "erosion-mod");
        }

        // ══════════════════════════════════════════════════════════════
        //  StepRegistry — behavioral tests
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void StepRegistry_ResolveStepId_KnownStep_ReturnsSame()
        {
            var registry = new StepRegistry();
            Assert.Equal("terra:step7-threshold", registry.ResolveStepId("terra:step7-threshold"));
        }

        [Fact]
        public void StepRegistry_ResolveStepId_RemappedStep_ReturnsNew()
        {
            var registry = new StepRegistry();
            registry.AddRemap("legacy:step", "terra:step7-threshold");
            Assert.Equal("terra:step7-threshold", registry.ResolveStepId("legacy:step"));
        }

        [Fact]
        public void StepRegistry_ResolveStepId_UnknownStep_ReturnsNull()
        {
            var registry = new StepRegistry();
            Assert.Null(registry.ResolveStepId("nonexistent:step"));
        }

        [Fact]
        public void StepRegistry_AddRemap_MarksOldStepInactive()
        {
            var registry = new StepRegistry();
            // "maps:climate" exists initially
            Assert.True(registry.Steps["maps:climate"].IsActive);

            registry.AddRemap("maps:climate", "terra:step7-threshold");
            // Old step should be inactive after remap
            Assert.False(registry.Steps["maps:climate"].IsActive);
        }

        [Fact]
        public void StepRegistry_GetVersionReport_ContainsAllSteps()
        {
            var registry = new StepRegistry();
            var report = registry.GetVersionReport();
            Assert.Contains("terra:step7-threshold", report);
            Assert.Contains("maps:climate", report);
            Assert.Contains("25", report); // total step count
        }

        [Fact]
        public void StepRegistry_TotalStepCount_Is25()
        {
            var registry = new StepRegistry();
            Assert.Equal(25, registry.Steps.Count);
        }

        // ══════════════════════════════════════════════════════════════
        //  OrderBands — behavioral tests
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void OrderBands_BandsAreNonOverlapping()
        {
            // BeforeVanilla < Vanilla < AfterVanilla < FinalOverride
            Assert.True(OrderBands.BeforeVanillaMax < OrderBands.Vanilla);
            Assert.True(OrderBands.Vanilla < OrderBands.AfterVanillaMin);
            Assert.True(OrderBands.AfterVanillaMax < OrderBands.FinalOverrideMin);
        }

        [Fact]
        public void OrderBands_AllRecommendedMods_FitInAfterVanilla()
        {
            double[] offsets = {
                OrderBands.RiversOffset,
                OrderBands.VSRiverGenOffset,
                OrderBands.WatershedsOffset,
                OrderBands.TerraPretyOffset,
                OrderBands.TerraPretyCarveOffset,
                OrderBands.TerraPretyBlockLayersOffset
            };

            foreach (var offset in offsets)
            {
                Assert.InRange(offset, OrderBands.AfterVanillaMin, OrderBands.AfterVanillaMax);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  ConflictDetector — behavioral tests
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void ConflictDetector_Report_IncrementsCount()
        {
            const string modId = "test-mod-increment-unique";
            ConflictDetector.Report(modId, "mechanism", "detail");
            Assert.Contains(ConflictDetector.Reports, report => report.OffendingModId == modId);
        }

        [Fact]
        public void ConflictDetector_Report_StoresCorrectValues()
        {
            const string modId = "my-mod-values-unique";
            ConflictDetector.Report(modId, "harmony-patch", "patched GenTerra");
            var report = ConflictDetector.Reports.Single(item => item.OffendingModId == modId);
            Assert.Equal(modId, report.OffendingModId);
            Assert.Equal("harmony-patch", report.Mechanism);
            Assert.Equal("patched GenTerra", report.Detail);
        }

        [Fact]
        public void ConflictDetector_ConflictReport_HasTimestamp()
        {
            const string modId = "timestamp-mod-unique";
            ConflictDetector.Report(modId, "mech", "detail");
            var report = ConflictDetector.Reports.Single(item => item.OffendingModId == modId);
            Assert.True(report.DetectedAt <= DateTime.UtcNow);
            Assert.True(report.DetectedAt > DateTime.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void RegionMapSlot_EncodeDecode_RoundTripsAllValues()
        {
            TestAssemblyLoader.EnsureVSEssentialsLoaded();
            RegionMapRegistry.Reset();
            try
            {
                var slot = RegionMapRegistry.Register(
                    "test-region-map", "test:roundtrip", innerSize: 3,
                    padding: 1, formatVersion: 7);
                MethodInfo getMap = slot.GetType().GetMethod(
                    "GetMap", new[] { typeof(int), typeof(int) })!;
                object map = getMap.Invoke(slot, new object[] { 0, 0 })!;
                Type mapType = map.GetType();
                FieldInfo dataField = mapType.GetField("Data")!;
                int[] data = Enumerable.Range(0, ((int[])dataField.GetValue(map)!).Length)
                    .Select(index => index * 17 - 9)
                    .ToArray();
                dataField.SetValue(map, data);

                MethodInfo encode = slot.GetType().GetMethod(
                    "Encode", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo decode = slot.GetType().GetMethod(
                    "Decode", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var bytes = (byte[])encode.Invoke(slot, new object[] { map })!;
                object decoded = decode.Invoke(slot, new object?[] { bytes })!;
                Type decodedType = decoded.GetType();

                Assert.Equal(mapType.GetField("Size")!.GetValue(map),
                    decodedType.GetField("Size")!.GetValue(decoded));
                Assert.Equal(mapType.GetField("TopLeftPadding")!.GetValue(map),
                    decodedType.GetField("TopLeftPadding")!.GetValue(decoded));
                Assert.Equal(mapType.GetField("BottomRightPadding")!.GetValue(map),
                    decodedType.GetField("BottomRightPadding")!.GetValue(decoded));
                Assert.Equal(data,
                    decodedType.GetField("Data")!.GetValue(decoded));
            }
            finally
            {
                RegionMapRegistry.Reset();
            }
        }

        [Fact]
        public void ConflictDetector_BlockingStateGatesCanonicalSeams()
        {
            ConflictDetector.Reset();
            try
            {
                // Harmony prefix truth table: true continues vanilla;
                // false suppresses it only once WorldgenLib is ready and no
                // blocking takeover has been detected.
                Assert.True(GenMapsBlocker.ShouldRunVanillaMapRegionGeneration(false, false));
                Assert.False(GenMapsBlocker.ShouldRunVanillaMapRegionGeneration(false, true));
                Assert.True(GenMapsBlocker.ShouldRunVanillaMapRegionGeneration(true, true));
                Assert.True(GenTerraBlocker.ShouldRunVanillaHandler(false, false));
                Assert.False(GenTerraBlocker.ShouldRunVanillaHandler(false, true));
                Assert.True(GenTerraBlocker.ShouldRunVanillaHandler(true, true));

                ConflictDetector.Report("foreign-worldgen", "replacement", "test", isBlocking: true);

                Assert.True(ConflictDetector.HasBlockingConflicts);
            }
            finally
            {
                ConflictDetector.Reset();
            }
        }

        [Fact]
        public void GenMapsForcePrefix_LeavesNativeMethodEnabled()
        {
            // The native force method must retain its own state because
            // GenMaps.InitWorldGen consumes requireLandAt before WorldgenLib's
            // region callback takes over.
            Assert.True(GenMapsBlocker.BeforeVanillaForceRandomLandArea(0, 0, 128));
        }
    }
}
