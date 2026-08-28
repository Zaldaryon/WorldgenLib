using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.ServerMods;

namespace WorldgenLib
{
    /// <summary>
    /// Keeps vanilla GenMaps alive for its initialization and public force API,
    /// but suppresses its private region-generation delegate because the
    /// ordered WorldgenLib host owns the single cooperative map-generation pass.
    ///
    /// WorldgenLib also registers its own InitWorldGenerator callback. The
    /// postfix below is still needed when vanilla GenMaps is present: its
    /// callback can run after WorldgenLib's fallback callback, so the canonical
    /// host is rebound once vanilla has finished initializing its globals.
    /// </summary>
    internal static class GenMapsBlocker
    {
        internal static Harmony HarmonyInstance { get; } = new("worldgenlib.genmaps.blocker");
        internal static bool IsPatched { get; private set; }

        internal static void Patch()
        {
            if (IsPatched) return;
            try
            {
                Type? genMapsType = FindType("Vintagestory.ServerMods.GenMaps");
                if (genMapsType == null)
                {
                    Console.WriteLine("[WorldgenLib] WARN: GenMaps type not found; map seam is unavailable.");
                    return;
                }

                var mapRegionMethod = genMapsType.GetMethod("OnMapRegionGen",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (mapRegionMethod == null)
                {
                    Console.WriteLine("[WorldgenLib] WARN: GenMaps.OnMapRegionGen not found; map seam is unavailable.");
                    return;
                }

                HarmonyInstance.Patch(mapRegionMethod,
                    prefix: new HarmonyMethod(typeof(GenMapsBlocker), nameof(ShouldSkipVanillaMapRegionGeneration)));

                var initMethod = genMapsType.GetMethod("initWorldGen",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (initMethod == null)
                    throw new MissingMethodException(
                        "Vintagestory.ServerMods.GenMaps.initWorldGen");

                HarmonyInstance.Patch(initMethod,
                    postfix: new HarmonyMethod(typeof(GenMapsBlocker), nameof(AfterVanillaInitWorldGen)));

                bool forceMethodsFound = true;
                forceMethodsFound &= PatchForce(genMapsType, "ForceClimateAt", nameof(BeforeVanillaForceClimate));
                forceMethodsFound &= PatchForce(genMapsType, "ForceLandformAt", nameof(BeforeVanillaForceLandform));
                forceMethodsFound &= PatchForce(genMapsType, "ForceRandomLandArea", nameof(BeforeVanillaForceRandomLandArea));
                if (!forceMethodsFound)
                    throw new MissingMethodException(
                        "One or more GenMaps force methods are unavailable.");

                IsPatched = true;
                Console.WriteLine("[WorldgenLib] GenMaps map-generation seam applied");
            }
            catch (Exception ex)
            {
                HarmonyInstance.UnpatchAll(HarmonyInstance.Id);
                IsPatched = false;
                Console.WriteLine($"[WorldgenLib] WARN: Failed to apply GenMaps seam: {ex.Message}");
            }
        }

        internal static void Unpatch()
        {
            if (!IsPatched) return;
            HarmonyInstance.UnpatchAll(HarmonyInstance.Id);
            IsPatched = false;
        }

        private static bool PatchForce(Type type, string methodName, string prefixName)
        {
            var method = type.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Console.WriteLine($"[WorldgenLib] WARN: GenMaps.{methodName} not found; forwarding is unavailable.");
                return false;
            }

            HarmonyInstance.Patch(method,
                prefix: new HarmonyMethod(typeof(GenMapsBlocker), prefixName));
            return true;
        }

        private static Type? FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        private static bool ShouldSkipVanillaMapRegionGeneration()
            => ShouldRunVanillaMapRegionGeneration(
                ConflictDetector.HasBlockingConflicts,
                WorldgenLibMod.CanHandleMapGeneration);

        /// <summary>
        /// Harmony prefixes return true to continue the original method.
        /// Keep this truth table explicit so a blocking finding or an
        /// unready host always leaves the vanilla/foreign path enabled.
        /// </summary>
        internal static bool ShouldRunVanillaMapRegionGeneration(
            bool hasBlockingConflict, bool worldgenLibReady)
            => hasBlockingConflict || !worldgenLibReady;

        internal static void AfterVanillaInitWorldGen()
            => WorldgenLibMod.NotifyVanillaGenMapsInitialized();

        /// <summary>
        /// Forward force requests to the cooperative host while allowing the
        /// original GenMaps method to run as well. The native method owns
        /// state consumed by its own InitWorldGen (notably requireLandAt), so
        /// suppressing it would make MapLayerOceans fail before the canonical
        /// host can take over region generation.
        /// </summary>
        internal static bool BeforeVanillaForceClimate(ForceClimate climate)
        {
            ForwardForce(() => WorldgenLibMod.TryForwardForceClimate(climate));
            return true;
        }

        internal static bool BeforeVanillaForceLandform(ForceLandform landform)
        {
            ForwardForce(() => WorldgenLibMod.TryForwardForceLandform(landform));
            return true;
        }

        internal static bool BeforeVanillaForceRandomLandArea(int positionX, int positionZ, int radius)
        {
            ForwardForce(() => WorldgenLibMod.TryForwardForceRandomLandArea(positionX, positionZ, radius));
            return true;
        }

        private static void ForwardForce(Func<bool> forward)
        {
            if (ConflictDetector.HasBlockingConflicts)
                return;

            try
            {
                forward();
            }
            catch (Exception ex)
            {
                // Never turn a forwarding problem into a broken vanilla
                // worldgen call. The original method is still allowed to run.
                Console.WriteLine($"[WorldgenLib] WARN: Force request forwarding failed: {ex.Message}");
            }
        }
    }
}
