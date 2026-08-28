using System;
using System.Collections.Generic;

namespace WorldgenLib
{
    /// <summary>
    /// Order band constants for the ordered-delegate model.
    /// Consumers pick a band and an offset inside it.
    /// </summary>
    public static class OrderBands
    {
        /// <summary>Rewrite inputs before vanilla-equivalent logic.</summary>
        public const double BeforeVanillaMin = -100;
        public const double BeforeVanillaMax = -1;

        /// <summary>Reserved for the built-in vanilla-equivalent chain.</summary>
        public const double Vanilla = 0;

        /// <summary>Normal effects (rivers, erosion, lifts).</summary>
        public const double AfterVanillaMin = 1;
        public const double AfterVanillaMax = 100;

        /// <summary>Last word (Fairlands-scale rewrites).</summary>
        public const double FinalOverrideMin = 1000;

        // ── Recommended band offsets per mod (from D9) ──

        public const double RiversOffset = 50;       // AfterVanilla + 50
        public const double VSRiverGenOffset = 50;    // AfterVanilla + 50 (alternative to Rivers)
        public const double WatershedsOffset = 60;    // AfterVanilla + 60
        public const double TerraPretyOffset = 50;    // AfterVanilla + 50
        public const double TerraPretyCarveOffset = 70; // AfterVanilla + 70 (step 10)
        public const double TerraPretyBlockLayersOffset = 60; // AfterVanilla + 60 (BlockLayers)
    }
}
