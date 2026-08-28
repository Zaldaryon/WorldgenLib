using System;
using System.Reflection;
using HarmonyLib;

namespace WorldgenLib
{
    /// <summary>
    /// Preserves vanilla GenTerra and GenTerraPostProcess initialization, but
    /// prevents their duplicate terrain handlers from running beside the
    /// WorldgenLib seams. This leaves vanilla state available to unrelated mods.
    /// </summary>
    internal static class GenTerraBlocker
    {
        internal static Harmony HarmonyInstance { get; } = new("worldgenlib.genterra.blocker");
        internal static bool IsPatched { get; private set; }

        internal static void Patch()
        {
            if (IsPatched) return;
            try
            {
                bool allFound = true;
                allFound &= PatchHandler(
                    "Vintagestory.ServerMods.GenTerra", "OnChunkColumnGen",
                    "GenTerra terrain handler",
                    nameof(SkipVanillaTerrainHandler));
                allFound &= PatchHandler(
                    "Vintagestory.ServerMods.GenTerraPostProcess", "OnChunkColumnGen",
                    "GenTerraPostProcess handler",
                    nameof(SkipVanillaPostProcessHandler));
                if (!allFound)
                {
                    HarmonyInstance.UnpatchAll(HarmonyInstance.Id);
                    IsPatched = false;
                    return;
                }

                IsPatched = true;
            }
            catch (Exception ex)
            {
                HarmonyInstance.UnpatchAll(HarmonyInstance.Id);
                IsPatched = false;
                Console.WriteLine($"[WorldgenLib] WARN: Failed to apply terrain seams: {ex.Message}");
            }
        }

        internal static void Unpatch()
        {
            if (!IsPatched) return;
            HarmonyInstance.UnpatchAll(HarmonyInstance.Id);
            IsPatched = false;
        }

        private static bool PatchHandler(string typeName, string methodName, string description,
            string prefixName)
        {
            Type? type = FindType(typeName);
            if (type == null)
            {
                Console.WriteLine($"[WorldgenLib] WARN: {typeName} not found; {description} remains active.");
                return false;
            }

            var method = type.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null)
            {
                Console.WriteLine($"[WorldgenLib] WARN: {typeName}.{methodName} not found; {description} remains active.");
                return false;
            }

            HarmonyInstance.Patch(method,
                prefix: new HarmonyMethod(typeof(GenTerraBlocker), prefixName));
            Console.WriteLine($"[WorldgenLib] {description} seam applied");
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

        private static bool SkipVanillaTerrainHandler()
            => ShouldRunVanillaHandler(
                ConflictDetector.HasBlockingConflicts,
                WorldgenLibMod.CanHandleTerrainGeneration);

        private static bool SkipVanillaPostProcessHandler()
            => ShouldRunVanillaHandler(
                ConflictDetector.HasBlockingConflicts,
                WorldgenLibMod.CanHandlePostProcess);

        /// <summary>Harmony true means that the original callback must run.</summary>
        internal static bool ShouldRunVanillaHandler(
            bool hasBlockingConflict, bool worldgenLibReady)
            => hasBlockingConflict || !worldgenLibReady;
    }
}
