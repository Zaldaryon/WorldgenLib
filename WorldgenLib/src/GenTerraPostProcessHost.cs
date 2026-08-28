using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace WorldgenLib
{
    /// <summary>
    /// Faithful reimplementation of vanilla GenTerraPostProcess.
    /// BFS-floods every solid block with air below and deletes floating
    /// islands of 40 blocks or fewer.
    ///
    /// Phase 5: no hooks invoked beyond opt-out. Parity with vanilla when empty.
    /// Phase 6+: opt-out hook, cleanup rule hooks.
    /// </summary>
    public sealed class GenTerraPostProcessHost
    {
        private readonly ICoreServerAPI _api;
        private IWorldGenBlockAccessor? _blockAccessor;

        // ── BFS state (instance fields, single-threaded execution) ──

        private readonly HashSet<int> _chunkVisitedNodes = new();
        private readonly List<int> _solidNodes = new(40);
        private readonly QueueOfInt _bfsQueue = new();
        private const int ArraySize = 41;
        private readonly int[] _currentVisited = new int[ArraySize * ArraySize * ArraySize];
        private int _iteration = 0;

        // ── Hook: opt-out per chunk ──

        private readonly OrderedHookList<ChunkPostProcessHook> _optOutHooks = new();
        private readonly OrderedHookList<CleanupRuleHook> _cleanupHooks = new();
        internal bool IsRegistered { get; private set; }

        /// <summary>Hook delegate for opting a chunk out of post-processing.</summary>
        public delegate bool ChunkPostProcessHook(int chunkX, int chunkZ);

        /// <summary>Hook delegate called when a floating node is found. Return true to delete it.</summary>
        public delegate bool CleanupRuleHook(int worldX, int worldY, int worldZ, int nodeSize);

        public GenTerraPostProcessHost(ICoreServerAPI api)
        {
            _api = api;
        }

        // ════════════════════════════════════════════════════════════════
        //  Registration API
        // ════════════════════════════════════════════════════════════════

        public void RegisterOptOut(string modId, double order, ChunkPostProcessHook hook)
            => _optOutHooks.Register(modId, order, hook);

        public void RegisterCleanupRule(string modId, double order, CleanupRuleHook hook)
            => _cleanupHooks.Register(modId, order, hook);

        internal void FreezeHooks()
        {
            _optOutHooks.Freeze();
            _cleanupHooks.Freeze();
        }

        /// <summary>Get a diagnostic report of post-process hooks.</summary>
        public IReadOnlyList<(string Step, double Order, string ModId)> GetHookReport()
        {
            var report = new List<(string, double, string)>();
            foreach (var entry in _optOutHooks.GetRegistrationReport())
                report.Add(("PostProcessOptOut", entry.Order, entry.ModId));
            foreach (var entry in _cleanupHooks.GetRegistrationReport())
                report.Add(("PostProcessCleanup", entry.Order, entry.ModId));
            return report;
        }

        // ════════════════════════════════════════════════════════════════
        //  StartServerSide — register for TerrainFeatures pass
        // ════════════════════════════════════════════════════════════════

        public void StartServerSide()
        {
            if (!TerraGenConfig.DoDecorationPass) return;

            _api.Event.GetWorldgenBlockAccessor(OnWorldGenBlockAccessor);
            _api.Event.ChunkColumnGeneration(OnChunkColumnGen,
                EnumWorldGenPass.TerrainFeatures, "standard");
            IsRegistered = true;
        }

        private void OnWorldGenBlockAccessor(IChunkProviderThread chunkProvider)
        {
            _blockAccessor = chunkProvider.GetBlockAccessor(true);
        }

        private IWorldGenBlockAccessor CurrentBlockAccessor
            => _blockAccessor ?? throw new InvalidOperationException(
                "[WorldgenLib] Worldgen block accessor is not initialized.");

        // ════════════════════════════════════════════════════════════════
        //  OnChunkColumnGen — BFS flood for floating islands
        // ════════════════════════════════════════════════════════════════

        private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
        {
            // Event registrations cannot be removed from the VS API. A
            // disposed mod instance may therefore still be present in the
            // delegate list during a hot reload; make that stale callback a
            // no-op instead of allowing it to mutate a later world.
            if (!IsRegistered || ConflictDetector.HasBlockingConflicts) return;

            if (_blockAccessor == null)
            {
                _api.Logger.Warning("[WorldgenLib] PostProcess skipped because the worldgen block accessor is not initialized.");
                return;
            }

            // ── Opt-out hook ──
            if (_optOutHooks.Count > 0)
            {
                foreach (var (order, modId, hook) in _optOutHooks.Enumerate())
                {
                    try
                    {
                        if (hook(request.ChunkX, request.ChunkZ)) return;
                    }
                    catch (Exception ex)
                    {
                        _optOutHooks.Disable(modId);
                        _api.Logger.Warning("[WorldgenLib] PostProcess opt-out hook '{0}' was disabled after exception: {1}", modId, ex.Message);
                    }
                }
            }

            var chunks = request.Chunks;
            int chunkX = request.ChunkX;
            int chunkZ = request.ChunkZ;

            _blockAccessor.BeginColumn();
            int seaLevel = TerraGenConfig.seaLevel - 1;
            const int chunksize = GlobalConstants.ChunkSize;
            const int chunksizeSquared = chunksize * chunksize;
            int chunkY = seaLevel / chunksize;
            int yMax = chunks[0].MapChunk.YMax;
            int cyMax = Math.Min(yMax / chunksize + 1,
                _api.World.BlockAccessor.MapSizeY / chunksize);
            _chunkVisitedNodes.Clear();

            for (int cy = chunkY; cy < cyMax; cy++)
            {
                IChunkBlocks chunkdata = chunks[cy].Data;

                int yStart = cy == 0 ? 1 : 0;
                int baseY = cy * chunksize;
                if (baseY < seaLevel)
                    yStart = seaLevel - baseY;

                int yEnd = chunksize - 1;
                if (baseY + yEnd > yMax)
                    yEnd = yMax - baseY;

                for (int baseindex3d = 0; baseindex3d < chunksizeSquared; baseindex3d++)
                {
                    int blockIdBelow;
                    int index3d = baseindex3d + (yStart - 1) * chunksizeSquared;

                    if (yStart == 0)
                        blockIdBelow = chunks[cy - 1].Data.GetBlockIdUnsafe(
                            index3d + chunksize * chunksizeSquared);
                    else
                        blockIdBelow = chunkdata.GetBlockIdUnsafe(index3d);

                    for (int y = yStart; y <= yEnd; y++)
                    {
                        index3d += chunksizeSquared;
                        int blockId = chunkdata.GetBlockIdUnsafe(index3d);
                        if (blockId != 0 && blockIdBelow == 0)
                        {
                            int x = baseindex3d % chunksize;
                            int z = baseindex3d / chunksize;
                            if (!_chunkVisitedNodes.Contains(index3d))
                                DeletePotentialFloatingBlocks(
                                    chunkX * chunksize + x,
                                    baseY + y,
                                    chunkZ * chunksize + z
                                );
                        }
                        blockIdBelow = blockId;
                    }
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  BFS flood — identical to vanilla (L97–208)
        // ════════════════════════════════════════════════════════════════

        private void DeletePotentialFloatingBlocks(int X, int Y, int Z)
        {
            int halfSize = (ArraySize - 1) / 2;
            _solidNodes.Clear();
            _bfsQueue.Clear();
            int compressedPos = halfSize << 12 | halfSize << 6 | halfSize;
            _bfsQueue.Enqueue(compressedPos);
            _solidNodes.Add(compressedPos);

            int iteration = ++_iteration;
            int visitedIndex = (halfSize * ArraySize + halfSize) * ArraySize + halfSize;
            _currentVisited[visitedIndex] = iteration;

            int baseX = X - halfSize;
            int baseY = Y - halfSize;
            int baseZ = Z - halfSize;
            BlockPos npos = new BlockPos(Dimensions.NormalWorld);
            int dx, dy, dz;

            int worldHeight = _api.World.BlockAccessor.MapSizeY;
            int curVisitedNodes = 1;

            while (_bfsQueue.Count > 0)
            {
                compressedPos = _bfsQueue.Dequeue();
                dx = compressedPos >> 12;
                dy = (compressedPos >> 6) & 0x3f;
                dz = compressedPos & 0x3f;
                npos.Set(baseX + dx, baseY + dy, baseZ + dz);

                foreach (BlockFacing facing in BlockFacing.ALLFACES)
                {
                    facing.IterateThruFacingOffsets(npos);
                    if (npos.Y >= worldHeight) continue;

                    dx = npos.X - baseX;
                    dy = npos.Y - baseY;
                    dz = npos.Z - baseZ;

                    visitedIndex = (dx * ArraySize + dy) * ArraySize + dz;
                    if (_currentVisited[visitedIndex] == iteration) continue;
                    _currentVisited[visitedIndex] = iteration;

                    int nBlock = CurrentBlockAccessor.GetBlockId(npos);
                    if (nBlock == 0) continue;

                    int newCompressedPos = dx << 12 | dy << 6 | dz;

                    if (++curVisitedNodes > 40)
                    {
                        if (!_solidNodes.Contains(newCompressedPos - 64))
                            AddToChunkVisitedNodesIfSameChunk(
                                npos.X, npos.Y, npos.Z, X, Y, Z);

                        foreach (int compPos in _solidNodes)
                        {
                            if (!_solidNodes.Contains(compPos - 64))
                            {
                                dx = compPos >> 12;
                                dy = (compPos >> 6) & 0x3f;
                                dz = compPos & 0x3f;
                                AddToChunkVisitedNodesIfSameChunk(
                                    baseX + dx, baseY + dy, baseZ + dz, X, Y, Z);
                            }
                        }
                        return;
                    }

                    _solidNodes.Add(newCompressedPos);
                    _bfsQueue.Enqueue(newCompressedPos);
                }
            }

            // ── Floating island found (≤40 blocks) ──

            // Cleanup rule hooks
            bool shouldDelete = true;
            if (_cleanupHooks.Count > 0)
            {
                foreach (var (order, modId, hook) in _cleanupHooks.Enumerate())
                {
                    try
                    {
                        if (!hook(X, Y, Z, _solidNodes.Count))
                        {
                            shouldDelete = false;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _cleanupHooks.Disable(modId);
                        _api.Logger.Warning("[WorldgenLib] PostProcess cleanup hook '{0}' was disabled after exception: {1}", modId, ex.Message);
                    }
                }
            }

            if (shouldDelete)
            {
                foreach (int compPos in _solidNodes)
                {
                    dx = compPos >> 12;
                    dy = (compPos >> 6) & 0x3f;
                    dz = compPos & 0x3f;
                    npos.Set(baseX + dx, baseY + dy, baseZ + dz);
                    CurrentBlockAccessor.SetBlock(0, npos);
                }
            }
        }

        /// <summary>
        /// Disable a callback that cannot be removed from the vanilla event
        /// list, and release the accessor/BFS state owned by this instance.
        /// </summary>
        internal void Dispose()
        {
            IsRegistered = false;
            _blockAccessor = null;
            _chunkVisitedNodes.Clear();
            _solidNodes.Clear();
            _bfsQueue.Clear();
        }

        private void AddToChunkVisitedNodesIfSameChunk(
            int nposX, int nposY, int nposZ, int origX, int origY, int origZ)
        {
            if (nposY < origY) return;
            if (nposY == origY)
            {
                if (nposZ < origZ) return;
                if (nposZ == origZ && nposX < origX) return;
            }

            const int chunksize = GlobalConstants.ChunkSize;
            const int chunkMask = ~(chunksize - 1);
            if (((nposX ^ origX) & chunkMask) != 0) return;
            if (((nposZ ^ origZ) & chunkMask) != 0) return;
            if (((nposY ^ origY) & chunkMask) != 0) return;

            const int inChunkMask = chunksize - 1;
            int index3d = ((nposY & inChunkMask) * chunksize
                         + (nposZ & inChunkMask)) * chunksize
                         + (nposX & inChunkMask);
            _chunkVisitedNodes.Add(index3d);
        }
    }
}
