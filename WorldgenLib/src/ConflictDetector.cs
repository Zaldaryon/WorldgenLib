using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Vintagestory.ServerMods;

namespace WorldgenLib
{
    /// <summary>
    /// Detects legacy full world-generation takeovers without mistaking an
    /// ordinary additive worldgen delegate for a conflict. Reports are
    /// advisory unless the foreign delegate/patch clearly owns the same
    /// vanilla pass as WorldgenLib.
    /// </summary>
    public static class ConflictDetector
    {
        private static readonly object Sync = new();
        private static readonly List<ConflictReport> _reports = new();

        public sealed class ConflictReport
        {
            public string OffendingModId { get; init; } = "";
            public string Mechanism { get; init; } = "";
            public string Detail { get; init; } = "";
            public bool IsBlocking { get; init; }
            public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
        }

        public static IReadOnlyList<ConflictReport> Reports
        {
            get { lock (Sync) return _reports.ToArray(); }
        }

        /// <summary>True for any advisory or blocking finding.</summary>
        public static bool HasConflicts
        {
            get { lock (Sync) return _reports.Count != 0; }
        }

        /// <summary>
        /// True only when a foreign system appears to replace the same
        /// generation callback. Advisory factory/transpiler findings do not
        /// disable WorldgenLib's ordinary hooks.
        /// </summary>
        public static bool HasBlockingConflicts
        {
            get { lock (Sync) return _reports.Any(report => report.IsBlocking); }
        }

        public static bool Detect(ICoreServerAPI api)
        {
            lock (Sync) _reports.Clear();

            try
            {
                DetectDelegates(api);
                DetectHarmonyPatches();
            }
            catch (Exception ex)
            {
                // A detector failure must not leave two generators active with
                // an unknown conflict state. The seam prefixes observe this
                // blocking report and let the vanilla/foreign path continue.
                Report(
                    "unknown",
                    "conflict detection failure",
                    "Worldgen compatibility could not be determined: " + ex.Message,
                    isBlocking: true);
                api.Logger.Error("[WorldgenLib] Conflict detection failed closed: {0}", ex.Message);
            }

            ConflictReport[] reports = Reports.ToArray();
            foreach (ConflictReport report in reports)
            {
                if (report.IsBlocking)
                {
                    api.Logger.Error(
                        "[WorldgenLib] BLOCKING WORLDGEN CONFLICT: '{0}' uses {1}. {2}",
                        report.OffendingModId, report.Mechanism, report.Detail);
                }
                else
                {
                    api.Logger.Warning(
                        "[WorldgenLib] Worldgen compatibility advisory: '{0}' uses {1}. {2}",
                        report.OffendingModId, report.Mechanism, report.Detail);
                }
            }

            if (!HasConflicts)
                api.Logger.Notification("[WorldgenLib] No conflicting worldgen generators detected.");
            else if (!HasBlockingConflicts)
                api.Logger.Notification("[WorldgenLib] No blocking worldgen takeover detected; advisory findings were recorded.");

            return HasBlockingConflicts;
        }

        private static void DetectDelegates(ICoreServerAPI api)
        {
            if (api.World is not ServerMain serverMain)
            {
                Report("unknown", "worldgen owner",
                    "ServerMain was not available; the worldgen delegate layout could not be inspected.",
                    isBlocking: true);
                return;
            }

            object? manager = GetMemberValue(serverMain, "ModEventManager");
            if (manager == null)
            {
                Report("unknown", "worldgen owner",
                    "ServerMain.ModEventManager was unavailable; delegate inspection failed closed.",
                    isBlocking: true);
                return;
            }

            object? handlers = GetMemberValue(manager, "WorldgenHandlers");
            if (handlers == null)
            {
                Report("unknown", "worldgen layout",
                    "ModEventManager.WorldgenHandlers was unavailable; delegate inspection failed closed.",
                    isBlocking: true);
                return;
            }

            object? standard = GetDictionaryValue(handlers, "standard");
            if (standard == null)
            {
                Report("unknown", "worldgen layout",
                    "The standard worldgen handler was unavailable; delegate inspection failed closed.",
                    isBlocking: true);
                return;
            }

            InspectRequiredDelegateMember(standard, "OnInitWorldGen", "OnInitWorldGen",
                typeof(GenTerra));

            object? chunkHandlers = GetMemberValue(standard, "OnChunkColumnGen");
            if (chunkHandlers is not Array array
                || array.Length <= (int)EnumWorldGenPass.Terrain)
            {
                Report("unknown", "worldgen layout",
                    "The standard terrain handler collection was unavailable or incomplete; " +
                    "delegate inspection failed closed.", isBlocking: true);
            }
            else
            {
                InspectRequiredDelegateValue(
                    array.GetValue((int)EnumWorldGenPass.Terrain),
                    "OnChunkColumnGen[Terrain]", requiredVanillaType: typeof(GenTerra));
            }

            InspectRequiredDelegateMember(standard, "OnMapRegionGen", "OnMapRegionGen",
                typeof(GenMaps));
        }

        private static void InspectRequiredDelegateMember(object owner, string memberName,
            string mechanism, Type requiredVanillaType)
        {
            object? value = GetMemberValue(owner, memberName);
            InspectRequiredDelegateValue(value, mechanism, memberName, requiredVanillaType);
        }

        private static void InspectRequiredDelegateValue(object? value, string mechanism,
            string? layoutMember = null, Type? requiredVanillaType = null)
        {
            string memberName = layoutMember ?? mechanism;
            if (value == null)
            {
                Report("unknown", "worldgen layout",
                    $"The standard handler member '{memberName}' was unavailable; delegate inspection failed closed.",
                    isBlocking: true);
                return;
            }

            if (value is not IEnumerable)
            {
                Report("unknown", "worldgen layout",
                    $"The standard handler member '{memberName}' was not enumerable; delegate inspection failed closed.",
                    isBlocking: true);
                return;
            }

            bool foundRequiredVanilla = InspectDelegateCollection(
                value, mechanism, blockingWhenFullReplacement: true, requiredVanillaType);
            if (requiredVanillaType != null && !foundRequiredVanilla)
            {
                Report(
                    "unknown",
                    "vanilla delegate missing (" + mechanism + ")",
                    $"The required vanilla {requiredVanillaType.FullName} delegate was not present in {memberName}; " +
                    "the active worldgen owner could not be determined.",
                    isBlocking: true);
            }
        }

        private static bool InspectDelegateCollection(object? value, string mechanism,
            bool blockingWhenFullReplacement, Type? requiredVanillaType = null)
        {
            if (value is not IEnumerable enumerable) return false;

            bool foundRequiredVanilla = false;

            foreach (object? item in enumerable)
            {
                if (item is not Delegate del) continue;
                MethodInfo method = del.Method;
                Type? declaringType = method.DeclaringType;
                if (declaringType == requiredVanillaType)
                    foundRequiredVanilla = true;

                if (declaringType == null || IsWorldgenLib(declaringType) || IsVanilla(declaringType))
                    continue;

                bool fullReplacement = LooksLikeFullReplacement(declaringType, method);
                Report(
                    GetOwnerId(declaringType),
                    "worldgen delegate (" + mechanism + ")",
                    $"Foreign delegate {declaringType.FullName}.{method.Name} is registered in the vanilla pipeline.",
                    blockingWhenFullReplacement && fullReplacement);
            }

            return foundRequiredVanilla;
        }

        private static void DetectHarmonyPatches()
        {
            var targets = new (Type Type, string Name, bool Blocking, string Description)[]
            {
                (typeof(GenTerra), "ShouldLoad", true, "GenTerra.ShouldLoad"),
                (typeof(GenTerra), "StartServerSide", true, "GenTerra.StartServerSide"),
                (typeof(GenTerra), "initWorldGen", true, "GenTerra.initWorldGen"),
                (typeof(GenTerra), "OnChunkColumnGen", true, "GenTerra.OnChunkColumnGen"),
                (typeof(GenTerraPostProcess), "OnChunkColumnGen", true, "GenTerraPostProcess.OnChunkColumnGen"),
                (typeof(GenMaps), "StartServerSide", true, "GenMaps.StartServerSide"),
                (typeof(GenMaps), "initWorldGen", true, "GenMaps.initWorldGen"),
                (typeof(GenMaps), "OnMapRegionGen", true, "GenMaps.OnMapRegionGen"),
                (typeof(GenBlockLayers), "OnChunkColumnGeneration", true, "GenBlockLayers.OnChunkColumnGeneration"),
                (typeof(GenMaps), "GetOceanMapGen", false, "GenMaps.GetOceanMapGen"),
                (typeof(GenMaps), "GetLandformMapGen", false, "GenMaps.GetLandformMapGen"),
                (typeof(GenMaps), "GetBeachMapGen", false, "GenMaps.GetBeachMapGen"),
                (typeof(GenMaps), "GetGeologicProvinceMapGen", false, "GenMaps.GetGeologicProvinceMapGen"),
                (typeof(GenMaps), "GetClimateMapGen", false, "GenMaps.GetClimateMapGen"),
                (typeof(GenMaps), "GetForestMapGen", false, "GenMaps.GetForestMapGen"),
                (typeof(GenMaps), "GetGeoUpheavelMapGen", false, "GenMaps.GetGeoUpheavelMapGen")
            };

            foreach (var target in targets)
            {
                MethodInfo[] methods = target.Type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == target.Name)
                    .ToArray();
                if (methods.Length == 0)
                {
                    if (target.Blocking)
                    {
                        Report(
                            "unknown",
                            "missing Harmony target",
                            $"Required worldgen target {target.Description} was not found; " +
                            "compatibility detection failed closed.",
                            isBlocking: true);
                    }
                    continue;
                }

                foreach (MethodInfo method in methods)
                {
                    MethodBase original = Harmony.GetOriginalMethod(method) ?? method;
                    var patchInfo = Harmony.GetPatchInfo(original);
                    if (patchInfo == null) continue;

                    string[] owners = patchInfo.Prefixes
                        .Concat(patchInfo.Postfixes)
                        .Concat(patchInfo.Transpilers)
                        .Concat(patchInfo.Finalizers)
                        .Select(patch => patch.owner)
                        .Where(owner => !IsWorldgenLibOwner(owner))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (owners.Length == 0) continue;

                    Report(
                        string.Join(", ", owners),
                        "Harmony patch",
                        $"Harmony patch on {target.Description} by owner(s): {string.Join(", ", owners)}.",
                        target.Blocking);
                }
            }
        }

        private static bool LooksLikeFullReplacement(Type type, MethodInfo method)
        {
            string text = (type.FullName + " " + method.Name).ToLowerInvariant();
            if (text.Contains("newgenterra") || text.Contains("watershedsgenterra")
                || text.Contains("watershedsgenmaps")
                || text.Contains("genterraprety") || text.Contains("genterrareplacement")
                || text.Contains("genmapsreplacement") || text.Contains("disablegenterra"))
                return true;

            Type? baseType = type.BaseType;
            return baseType != null && baseType != typeof(GenTerra)
                && typeof(GenTerra).IsAssignableFrom(type);
        }

        private static object? GetMemberValue(object instance, string name)
        {
            for (Type? type = instance.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo? field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(instance);

                PropertyInfo? property = type.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(instance);
            }

            return null;
        }

        private static object? GetDictionaryValue(object? value, string key)
        {
            if (value is IDictionary dictionary)
                return dictionary.Contains(key) ? dictionary[key] : null;
            if (value is not IEnumerable enumerable) return null;

            foreach (object? item in enumerable)
            {
                if (item == null) continue;
                PropertyInfo? keyProperty = item.GetType().GetProperty("Key");
                PropertyInfo? valueProperty = item.GetType().GetProperty("Value");
                if (keyProperty?.GetValue(item) as string == key)
                    return valueProperty?.GetValue(item);
            }
            return null;
        }

        private static string GetOwnerId(Type declaringType)
            => declaringType.Assembly.GetName().Name ?? declaringType.FullName ?? "unknown";

        private static bool IsWorldgenLib(Type type)
            => type.Namespace?.StartsWith("WorldgenLib", StringComparison.Ordinal) == true;

        private static bool IsVanilla(Type type)
            => type == typeof(GenTerra) || type == typeof(GenMaps)
                || type.Namespace?.StartsWith("Vintagestory.", StringComparison.Ordinal) == true;

        private static bool IsWorldgenLibOwner(string owner)
            => owner.StartsWith("worldgenlib.", StringComparison.OrdinalIgnoreCase)
                || owner.Equals("worldgenlib", StringComparison.OrdinalIgnoreCase);

        internal static void Report(string modId, string mechanism, string detail,
            bool isBlocking = false)
        {
            lock (Sync)
            {
                _reports.Add(new ConflictReport
                {
                    OffendingModId = modId ?? "unknown",
                    Mechanism = mechanism,
                    Detail = detail,
                    IsBlocking = isBlocking
                });
            }
        }

        internal static void Reset()
        {
            lock (Sync) _reports.Clear();
        }
    }
}
