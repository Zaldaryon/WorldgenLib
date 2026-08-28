using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace WorldgenLib
{
    /// <summary>
    /// Host for GenBlockLayers behavior. Provides hooks for the raise modifier
    /// and sea-level-rise filter, replacing the Harmony transpilers used by
    /// Rivers-Mod, VSRiverGen, and Terra Prety.
    ///
    /// Consumer mods register delegates during StartServerSide. After all
    /// registrations, FreezeHooks() locks the lists. During chunk generation,
    /// the host applies all registered modifiers/filters in order.
    /// </summary>
    public sealed class GenBlockLayersHost
    {
        private readonly ICoreServerAPI _api;

        /// <summary>Hook list for raise modifiers. (localX, localZ, currentRaise, mapChunk) → modifiedRaise.</summary>
        private readonly OrderedHookList<System.Func<int, int, float, IMapChunk, float>> _raiseModifiers = new();

        /// <summary>Hook list for sea-level-rise filters. (localX, localZ, mapChunk) → true to keep raise.</summary>
        private readonly OrderedHookList<System.Func<int, int, IMapChunk, bool>> _seaLevelFilters = new();
        private readonly OrderedHookList<BlockLayersGenerationHook> _fullGenerationHooks = new();

        private bool _frozen;

        public GenBlockLayersHost(ICoreServerAPI api)
        {
            _api = api;
        }

        /// <summary>
        /// Register a hook that modifies terrain raise per-column.
        /// Called during BlockLayers.OnChunkColumnGeneration after vanilla raise computation.
        /// Return the modified raise value.
        /// </summary>
        /// <param name="modId">Owning mod identifier.</param>
        /// <param name="order">Execution order within the step.</param>
        /// <param name="modifier">
        /// Delegate: (localX, localZ, currentRaise, mapChunk) → modifiedRaise.
        /// </param>
        public void RegisterRaiseModifier(string modId, double order,
            System.Func<int, int, float, IMapChunk, float> modifier)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Cannot register BlockLayers hooks after InitWorldGen. Mod '{modId}' attempted late registration.");
            EnsureInlineSeamAvailable(modId);
            _raiseModifiers.Register(modId, order, modifier);
        }

        /// <summary>
        /// Register a hook that can disable sea-level-rise for a column.
        /// Return true to keep the raise, false to zero it.
        /// </summary>
        /// <param name="modId">Owning mod identifier.</param>
        /// <param name="order">Execution order within the step.</param>
        /// <param name="filter">
        /// Delegate: (localX, localZ, mapChunk) → true to keep raise, false to zero it.
        /// </param>
        public void RegisterSeaLevelRiseFilter(string modId, double order,
            System.Func<int, int, IMapChunk, bool> filter)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Cannot register BlockLayers hooks after InitWorldGen. Mod '{modId}' attempted late registration.");
            EnsureInlineSeamAvailable(modId);
            _seaLevelFilters.Register(modId, order, filter);
        }

        /// <summary>
        /// Register a terminal replacement for the complete BlockLayers
        /// request. This is intended for migrations such as Watersheds whose
        /// stream-aware layer pass cannot be represented by a scalar raise
        /// modifier. Return true after handling the request.
        /// </summary>
        public void RegisterFullGeneration(string modId, double order,
            BlockLayersGenerationHook hook)
        {
            if (_frozen)
                throw new InvalidOperationException(
                    $"[WorldgenLib] Cannot register BlockLayers hooks after InitWorldGen. Mod '{modId}' attempted late registration.");
            _fullGenerationHooks.Register(modId, order, hook);
        }

        private static void EnsureInlineSeamAvailable(string modId)
        {
            if (!GenBlockLayersPatch.TransformationAvailable)
                throw new InvalidOperationException(
                    $"[WorldgenLib] BlockLayers inline seam is unavailable for this Vintage Story build; " +
                    $"mod '{modId}' cannot register a raise/filter hook.");
        }

        /// <summary>Freeze all hook lists. Called once after all StartServerSide have run.</summary>
        public void FreezeHooks()
        {
            _raiseModifiers.Freeze();
            _seaLevelFilters.Freeze();
            _fullGenerationHooks.Freeze();
            _frozen = true;
        }

        internal bool HasInlineHooks => _raiseModifiers.Count != 0 || _seaLevelFilters.Count != 0;

        internal bool TryRunFullGeneration(IChunkColumnGenerateRequest request)
        {
            foreach (var entry in _fullGenerationHooks.Snapshot)
            {
                if (_fullGenerationHooks.IsDisabled(entry.ModId)) continue;
                string modId = entry.ModId;
                BlockLayersGenerationHook hook = entry.Handler;
                try
                {
                    if (hook(request)) return true;
                }
                catch (Exception ex)
                {
                    _fullGenerationHooks.Disable(modId);
                    _api.Logger.Warning(
                        "[WorldgenLib] Full BlockLayers hook '{0}' disabled after exception: {1}",
                        modId, ex.Message);
                }
            }

            return false;
        }

        /// <summary>
        /// Apply all registered raise modifiers to a column's raise value.
        /// Called during BlockLayers.OnChunkColumnGeneration.
        /// </summary>
        public float ApplyRaiseModifiers(int localX, int localZ, float currentRaise, IMapChunk mapChunk)
        {
            float raise = currentRaise;
            foreach (var entry in _raiseModifiers.Snapshot)
            {
                if (_raiseModifiers.IsDisabled(entry.ModId)) continue;
                string modId = entry.ModId;
                System.Func<int, int, float, IMapChunk, float> modifier = entry.Handler;
                try
                {
                    float candidate = modifier(localX, localZ, raise, mapChunk);
                    if (float.IsFinite(candidate)) raise = candidate;
                    else
                    {
                        _raiseModifiers.Disable(modId);
                        _api.Logger.Warning(
                            "[WorldgenLib] BlockLayers raise hook '{0}' returned a non-finite value and was disabled.",
                            modId);
                    }
                }
                catch (Exception ex)
                {
                    _raiseModifiers.Disable(modId);
                    _api.Logger.Warning(
                        "[WorldgenLib] BlockLayers raise hook '{0}' disabled after exception: {1}",
                        modId, ex.Message);
                }
            }
            return raise;
        }

        /// <summary>
        /// Apply all registered sea-level-rise filters. Returns true if raise should be kept.
        /// If any filter returns false, the raise is zeroed.
        /// Called during BlockLayers.OnChunkColumnGeneration.
        /// </summary>
        public bool ApplySeaLevelFilters(int localX, int localZ, IMapChunk mapChunk)
        {
            foreach (var entry in _seaLevelFilters.Snapshot)
            {
                if (_seaLevelFilters.IsDisabled(entry.ModId)) continue;
                string modId = entry.ModId;
                System.Func<int, int, IMapChunk, bool> filter = entry.Handler;
                try
                {
                    if (!filter(localX, localZ, mapChunk)) return false;
                }
                catch (Exception ex)
                {
                    _seaLevelFilters.Disable(modId);
                    _api.Logger.Warning(
                        "[WorldgenLib] BlockLayers sea-level hook '{0}' disabled after exception: {1}",
                        modId, ex.Message);
                }
            }
            return true;
        }

        /// <summary>Get a diagnostic report of all registered hooks.</summary>
        public IReadOnlyList<(string Step, double Order, string ModId)> GetHookReport()
        {
            var report = new List<(string, double, string)>();
            foreach (var entry in _raiseModifiers.GetRegistrationReport())
                report.Add(("RaiseModifier", entry.Order, entry.ModId));
            foreach (var entry in _seaLevelFilters.GetRegistrationReport())
                report.Add(("SeaLevelFilter", entry.Order, entry.ModId));
            foreach (var entry in _fullGenerationHooks.GetRegistrationReport())
                report.Add(("FullGeneration", entry.Order, entry.ModId));
            return report;
        }
    }
}
