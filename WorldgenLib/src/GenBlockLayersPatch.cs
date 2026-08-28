using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace WorldgenLib
{
    /// <summary>
    /// Narrow runtime seam for the private vanilla GenBlockLayers callback.
    /// The vanilla pass remains intact; WorldgenLib only adds the final raise
    /// modifier/filter and gives a terminal adapter a chance to own the pass.
    /// </summary>
    internal static class GenBlockLayersPatch
    {
        private const string HarmonyId = "worldgenlib.genblocklayers.seam";
        private static readonly Harmony HarmonyInstance = new(HarmonyId);
        private static readonly MethodInfo VanillaMin = typeof(Math).GetMethod(
            nameof(Math.Min), new[] { typeof(float), typeof(float) })!;
        private static readonly MethodInfo ApplyFinalMethod = typeof(GenBlockLayersBridge).GetMethod(
            nameof(GenBlockLayersBridge.ApplyFinal), BindingFlags.Static | BindingFlags.Public)!;
        private static readonly MethodInfo RequestChunksGetter =
            typeof(IChunkColumnGenerateRequest).GetProperty(nameof(IChunkColumnGenerateRequest.Chunks))!.GetMethod!;

        internal static bool IsPatched { get; private set; }
        internal static bool TransformationAvailable { get; private set; }

        internal static void Patch()
        {
            if (IsPatched) return;
            try
            {
                Type? type = FindType("Vintagestory.ServerMods.GenBlockLayers");
                MethodInfo? method = type?.GetMethod("OnChunkColumnGeneration",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (method == null)
                {
                    Console.WriteLine("[WorldgenLib] WARN: GenBlockLayers.OnChunkColumnGeneration not found; seam unavailable.");
                    return;
                }

                HarmonyInstance.Patch(
                    method,
                    prefix: new HarmonyMethod(typeof(GenBlockLayersPatch), nameof(Prefix)),
                    transpiler: new HarmonyMethod(typeof(GenBlockLayersPatch), nameof(Transpiler)));
                IsPatched = true;
                Console.WriteLine("[WorldgenLib] GenBlockLayers seam applied");
            }
            catch (Exception ex)
            {
                HarmonyInstance.UnpatchAll(HarmonyId);
                IsPatched = false;
                TransformationAvailable = false;
                Console.WriteLine($"[WorldgenLib] WARN: Failed to apply GenBlockLayers seam: {ex.Message}");
            }
        }

        internal static void Unpatch()
        {
            if (!IsPatched) return;
            HarmonyInstance.UnpatchAll(HarmonyId);
            IsPatched = false;
            TransformationAvailable = false;
        }

        private static bool Prefix(IChunkColumnGenerateRequest __0)
            => ConflictDetector.HasBlockingConflicts || !WorldgenLibMod.TryHandleBlockLayers(__0);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int storeIndex = -1;
            object? raiseLocal = null;

            // GenBlockLayers computes the final sea-level rise as:
            // Math.Min(float, float), conv.i4, stloc seaLevelRise.
            // Inject after that store so all consumers observe the same
            // clamped vanilla value and no adapter depends on a private field.
            for (int i = 2; i < code.Count; i++)
            {
                if (!IsStoreLocal(code[i])) continue;
                if (code[i - 1].opcode != OpCodes.Conv_I4) continue;
                if (code[i - 2].opcode != OpCodes.Call
                    || !Equals(code[i - 2].operand, VanillaMin)) continue;

                storeIndex = i;
                raiseLocal = code[i].operand;
                break;
            }

            if (storeIndex < 0 || raiseLocal == null)
            {
                Console.WriteLine("[WorldgenLib] WARN: GenBlockLayers raise pattern not found; inline BlockLayers hooks disabled.");
                TransformationAvailable = false;
                return code;
            }

            // The 1.22.x method uses locals 23 and 24 for the X/Z loop
            // counters. Prefer a structural lookup and retain that mapping as
            // the compatibility fallback for the known reference IL.
            object xLocal = 23;
            object zLocal = 24;
            TryFindCoordinateLocals(code, storeIndex, ref xLocal, ref zLocal);

            code.InsertRange(storeIndex + 1, new[]
            {
                LoadLocal(xLocal),
                LoadLocal(zLocal),
                LoadLocal(raiseLocal),
                // The request is a method argument in the reference API;
                // load Chunks from it instead of relying on a private local
                // number that can drift between Vintage Story builds.
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Callvirt, RequestChunksGetter),
                new CodeInstruction(OpCodes.Call, ApplyFinalMethod),
                StoreLocal(raiseLocal)
            });

            TransformationAvailable = true;
            return code;
        }

        private static CodeInstruction StoreLocal(object local)
        {
            if (local is OpCode opcode)
            {
                return new CodeInstruction(opcode == OpCodes.Ldloc_0 ? OpCodes.Stloc_0
                    : opcode == OpCodes.Ldloc_1 ? OpCodes.Stloc_1
                    : opcode == OpCodes.Ldloc_2 ? OpCodes.Stloc_2
                    : OpCodes.Stloc_3);
            }

            return new CodeInstruction(OpCodes.Stloc, local);
        }

        private static void TryFindCoordinateLocals(List<CodeInstruction> code,
            int raiseStoreIndex, ref object xLocal, ref object zLocal)
        {
            // Locate the call to PutLayers in the same loop. Its argument
            // sequence is stable across 1.22.x and carries x then z directly
            // before the other column inputs.
            for (int i = raiseStoreIndex + 1; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call && code[i].opcode != OpCodes.Callvirt)
                    continue;
                if (code[i].operand is not MethodInfo method || method.Name != "PutLayers")
                    continue;

                int parameterCount = method.GetParameters().Length;
                var locals = new List<object>();
                for (int j = i - 1; j >= Math.Max(0, i - 32) && locals.Count < parameterCount; j--)
                {
                    if (IsLoadLocal(code[j])) locals.Add(code[j].operand ?? code[j].opcode);
                }

                // Reverse traversal sees the final argument first. PutLayers'
                // x and z are formal parameters 1 and 2, so calculate their
                // positions from the reflected signature instead of relying
                // on a particular count of trailing arguments.
                int xIndex = parameterCount - 2;
                int zIndex = parameterCount - 3;
                if (locals.Count == parameterCount
                    && locals[xIndex] is not null && locals[zIndex] is not null)
                {
                    zLocal = locals[zIndex];
                    xLocal = locals[xIndex];
                }
                return;
            }
        }

        private static bool IsStoreLocal(CodeInstruction instruction)
            => instruction.opcode == OpCodes.Stloc
                || instruction.opcode == OpCodes.Stloc_S
                || instruction.opcode == OpCodes.Stloc_0
                || instruction.opcode == OpCodes.Stloc_1
                || instruction.opcode == OpCodes.Stloc_2
                || instruction.opcode == OpCodes.Stloc_3;

        private static bool IsLoadLocal(CodeInstruction instruction)
            => instruction.opcode == OpCodes.Ldloc
                || instruction.opcode == OpCodes.Ldloc_S
                || instruction.opcode == OpCodes.Ldloc_0
                || instruction.opcode == OpCodes.Ldloc_1
                || instruction.opcode == OpCodes.Ldloc_2
                || instruction.opcode == OpCodes.Ldloc_3;

        private static CodeInstruction LoadLocal(object local)
        {
            if (local is OpCode opcode)
                return new CodeInstruction(opcode);
            return new CodeInstruction(OpCodes.Ldloc, local);
        }

        private static Type? FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }
    }

    /// <summary>Bridge methods called from the generated vanilla IL.</summary>
    public static class GenBlockLayersBridge
    {
        public static int ApplyFinal(int localX, int localZ, int currentRaise,
            IServerChunk[] chunks)
        {
            if (ConflictDetector.HasBlockingConflicts
                || chunks == null || chunks.Length == 0 || chunks[0] == null)
                return currentRaise;

            GenBlockLayersHost? host = WorldgenLibAPI.TryGetBlockLayersHost();
            if (host == null || !host.HasInlineHooks)
                return currentRaise;

            IMapChunk mapChunk = chunks[0].MapChunk;
            float raise = host.ApplyRaiseModifiers(localX, localZ, currentRaise, mapChunk);
            if (!host.ApplySeaLevelFilters(localX, localZ, mapChunk)) return 0;
            if (!float.IsFinite(raise) || raise <= 0) return 0;
            return (int)raise;
        }
    }
}
