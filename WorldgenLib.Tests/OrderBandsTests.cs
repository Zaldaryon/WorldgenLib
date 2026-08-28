using System;
using Xunit;

namespace WorldgenLib.Tests
{
    public class OrderBandsTests
    {
        [Fact]
        public void BeforeVanillaRange_DoesNotOverlapVanilla()
        {
            Assert.True(OrderBands.BeforeVanillaMax < OrderBands.Vanilla);
        }

        [Fact]
        public void VanillaRange_DoesNotOverlapAfterVanilla()
        {
            Assert.True(OrderBands.Vanilla < OrderBands.AfterVanillaMin);
        }

        [Fact]
        public void AfterVanillaRange_DoesNotOverlapFinalOverride()
        {
            Assert.True(OrderBands.AfterVanillaMax < OrderBands.FinalOverrideMin);
        }

        [Fact]
        public void AllModOffsets_AreWithinAfterVanilla()
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

        [Fact]
        public void RiversAndVSRiverGen_SameOffset()
        {
            // Both are alternatives — same default order value
            Assert.Equal(OrderBands.RiversOffset, OrderBands.VSRiverGenOffset);
        }

        [Fact]
        public void WatershedsOffset_GreaterThanRiversOffset()
        {
            // Watersheds runs after rivers at the same step
            Assert.True(OrderBands.WatershedsOffset > OrderBands.RiversOffset);
        }
    }
}
