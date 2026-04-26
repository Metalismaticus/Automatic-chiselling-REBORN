using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.MathTools;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.API.Config;
using Newtonsoft.Json;

namespace AutomaticChiselling
{
    /// <summary>
    /// A single chisel operation — matches ChiselWiz approach.
    /// </summary>
    public struct ChiselOperation
    {
        public BlockPos Block;
        public int X, Y, Z;    // Voxel coordinates (0-15)
        public int BrushSize;   // 1, 2, 4, or 8
        public bool IsRemove;   // true = remove, false = add
        public byte PaletteIdx; // Canonical palette index for ADD ops (which color to place). 0 for REMOVE.

        public ChiselOperation(BlockPos block, int x, int y, int z, int brushSize, bool isRemove = true, byte paletteIdx = 0)
        {
            Block = block;
            X = x; Y = y; Z = z;
            BrushSize = brushSize;
            IsRemove = isRemove;
            PaletteIdx = paletteIdx;
        }

        public int ToolMode => BrushSize switch
        {
            8 => 3,
            4 => 2,
            2 => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Serializable progress state for saving/loading.
    /// </summary>
    public class ChiselProgress
    {
        public string ModelFileName;
        public int StartX, StartY, StartZ;
        public int OffsetVoxX, OffsetVoxY, OffsetVoxZ;
        public string Alignment;
        public int CurrentIndex;
        public int TotalOperations;

        // For generated models (non-vox): re-run generator to reproduce
        public string GeneratorName;
        public Dictionary<string, object> GeneratorParams;

        // Material assignments (palette idx → block code). Saved for colored models.
        public Dictionary<byte, string> MaterialAssignments;
    }

    /// <summary>
    /// Brush sizes from largest to smallest (ChiselWiz: VoxelConstants.BrushSizes)
    /// </summary>
    public static class VoxelConstants
    {
        public const int BlockSize = 16;
        public static readonly int[] BrushSizes = { 8, 4, 2, 1 };
    }

    public class ChiselConveyor
    {
        private ICoreClientAPI capi;
        private VoxelsStorage myVox;

        private List<ChiselOperation> operationQueue;
        private List<ChiselOperation> deferredOps;    // last-voxel removes (deferred to end)
        private List<BlockPos> blocksToBreak;
        private int currentIndex = 0;
        private int breakIndex = 0;
        private bool isBreakingPhase = true;

        private long tickerID;
        private bool activeChiseling = false;
        private int lastReportedProgress = -1;
        private long lastReminder = 0;
        private BlockPos lastChiseledBlock = null;
        private long lastSentMs = 0;
        private const long SERVER_LAG_THRESHOLD_MS = 500;

        // Adaptive speed
        private int opsPerTick = 1;
        private int maxOpsPerTick = 8;
        private int successStreak = 0;
        private bool adaptiveSpeed = true;

        /// <summary>
        /// Reads ModSettings and applies them to the conveyor's speed state.
        /// Picks the SP or MP track based on the client's current connection.
        /// Called at StartConveyor and ResumeFromIndex so live changes take effect
        /// at the next run.
        /// </summary>
        private void ApplySpeedSettings()
        {
            var s = ModSettings.Instance;
            adaptiveSpeed = s.AdaptiveSpeed;
            bool sp = capi.IsSinglePlayer;
            maxOpsPerTick = sp ? s.MaxOpsPerTickSP : s.MaxOpsPerTickMP;
            int initial  = sp ? s.InitialOpsPerTickSP : s.InitialOpsPerTickMP;
            opsPerTick = Math.Min(initial, maxOpsPerTick);
        }

        // Caches
        private HashSet<BlockPos> confirmedChiselBlocks = new HashSet<BlockPos>();
        private int lastToolMode = -1;

        // Multi-color state — computed at StartConveyor based on MaterialMapping.
        // Maps palette index → materialIdx (position in BlockEntityChisel.MaterialIds).
        private Dictionary<byte, byte> paletteToMaterialIdx = new Dictionary<byte, byte>();
        // Which chisel blocks have had all non-base materials added.
        private HashSet<BlockPos> materialsInitialized = new HashSet<BlockPos>();

        // Time estimation
        private long sessionStartTimeMs = 0;
        private int sessionStartOps = 0;

        // Progress tracking — always sum both phases for smooth bar
        public int TotalOperations => (blocksToBreak?.Count ?? 0) + (operationQueue?.Count ?? 0);
        public int CompletedOperations => breakIndex + (isBreakingPhase ? 0 : currentIndex);
        public float ProgressPercent => TotalOperations > 0 ? (float)CompletedOperations / TotalOperations * 100f : 0f;
        public bool IsActive => activeChiseling;
        public string ModelName => myVox?.GetFileName() ?? "";

        // Events for HUD
        public event Action<float> OnProgressChanged;
        public event Action OnCompleted;
        // Events for HUD

        public ChiselConveyor(ICoreClientAPI clientApi, VoxelsStorage storage)
        {
            capi = clientApi;
            myVox = storage;
        }

        /// <summary>
        /// Returns the expected ops/sec given the current connection (SP/MP) and
        /// the user's speed settings. Used for PRE-RUN estimates in the Model
        /// Browser. Actual runtime ETA uses measured throughput, not this value.
        ///
        /// Adaptive ON: averages Initial and Max since the rate ramps linearly
        /// during the run. Adaptive OFF: fixed at Initial.
        /// </summary>
        public static float EstimateOpsPerSec(ICoreClientAPI capi)
        {
            var s = ModSettings.Instance;
            bool sp = capi.IsSinglePlayer;
            int ticksPerSec = sp ? 40 : 10;          // matches RegisterGameTickListener(25ms / 100ms)
            int initial = sp ? s.InitialOpsPerTickSP : s.InitialOpsPerTickMP;
            int max     = sp ? s.MaxOpsPerTickSP     : s.MaxOpsPerTickMP;
            float effective = s.AdaptiveSpeed ? (initial + max) * 0.5f : initial;
            if (effective < 1f) effective = 1f;
            return effective * ticksPerSec;
        }

        /// <summary>
        /// Pre-run time estimate in seconds for the given op count, using the
        /// current settings and connection mode.
        /// </summary>
        public static int EstimateSeconds(ICoreClientAPI capi, int ops)
        {
            if (ops <= 0) return 0;
            float opsPerSec = EstimateOpsPerSec(capi);
            return (int)Math.Ceiling(ops / opsPerSec);
        }

        /// <summary>
        /// Estimates remaining time in seconds based on current session throughput.
        /// Returns -1 if not enough data to estimate.
        /// </summary>
        public int GetEstimatedRemainingSeconds()
        {
            if (!activeChiseling || TotalOperations == 0) return -1;
            int completedThisSession = CompletedOperations - sessionStartOps;
            if (completedThisSession < 20) return -1; // need some data points

            long elapsedMs = capi.ElapsedMilliseconds - sessionStartTimeMs;
            if (elapsedMs <= 0) return -1;

            double msPerOp = (double)elapsedMs / completedThisSession;
            int remaining = TotalOperations - CompletedOperations;
            return (int)(msPerOp * remaining / 1000.0);
        }

        /// <summary>
        /// Pre-calculates the number of chisel operations for a model (no world state needed).
        /// Used to show estimates before chiseling starts.
        /// </summary>
        public static int CountChiselOperations(VoxelsStorage storage)
        {
            var voxelBlocks = storage.GetVoxelBlocks();
            var ops = new List<ChiselOperation>();
            var deferred = new List<ChiselOperation>();

            // baseIdx is a synthetic sentinel for "unmapped palette colors" — voxels of
            // any unmapped color will fall through to MaterialIds[0] (whatever the player
            // placed in world). We pick one that's guaranteed NOT in mapped so the coloring
            // pass treats all mapped colors as needing REMOVE+ADD.
            byte baseIdx = 255;
            HashSet<byte> mapped = null;
            if (storage.MaterialMapping != null && !storage.MaterialMapping.IsEmpty)
            {
                mapped = new HashSet<byte>(storage.MaterialMapping.Assignments.Keys);
                while (mapped.Contains(baseIdx) && baseIdx > 0) baseIdx--;
            }

            foreach (var kvp in voxelBlocks)
            {
                ComputeBlockOps(kvp.Key, kvp.Value, ops, deferred, baseIdx, mapped);
            }

            return ops.Count + deferred.Count;
        }

        public bool ChisellingActive() => activeChiseling;

        public void StartConveyor()
        {
            BuildOperationQueue();
            activeChiseling = true;
            isBreakingPhase = blocksToBreak.Count > 0;
            breakIndex = 0;
            currentIndex = 0;
            lastReportedProgress = -1;
            lastChiseledBlock = null;
            lastSentMs = 0;

            // Speed init — prefer user settings, fall back to SP/MP defaults.
            ApplySpeedSettings();
            successStreak = 0;
            confirmedChiselBlocks.Clear();
            materialsInitialized.Clear();
            lastToolMode = -1;

            BuildPaletteToMaterialIdxMap();

            sessionStartTimeMs = capi.ElapsedMilliseconds;
            sessionStartOps = 0;

            int tickMs = capi.IsSinglePlayer ? 25 : 100;
            tickerID = capi.World.RegisterGameTickListener(ProcessTick, tickMs);


            SaveProgress();
        }

        /// <summary>
        /// Builds the palette-index → material-index map based on the model's used colors
        /// and the player's chosen MaterialMapping.
        ///
        /// MaterialIds[0] is reserved for whatever block the player placed in the world
        /// (becomes the fallback for unmapped palette colors). Mapped colors get matIdx 1,
        /// 2, 3, ... — even if a mapped color is "first" in the palette. This way, the
        /// player's color selection is always honored, and only unmapped colors fall
        /// through to the placed base block.
        /// </summary>
        private void BuildPaletteToMaterialIdxMap()
        {
            paletteToMaterialIdx.Clear();
            if (myVox?.MaterialMapping == null || myVox.MaterialMapping.IsEmpty) return;
            if (myVox.UsedPaletteIndices == null) return;

            byte matIdx = 1;  // 0 reserved for base (placed block)
            foreach (byte paletteIdx in myVox.UsedPaletteIndices)
            {
                if (!myVox.MaterialMapping.Assignments.ContainsKey(paletteIdx)) continue;
                paletteToMaterialIdx[paletteIdx] = matIdx++;
                if (matIdx >= VoxelsStorage.MaxMaterialsPerBlock) break;
            }
        }

        public void ResumeFromIndex(int savedIndex)
        {
            BuildOperationQueue();
            activeChiseling = true;

            // Start from beginning — FilterCompletedOperations will skip
            // all operations that are already done by checking real block state.
            // This is more reliable than trusting savedIndex which can drift
            // when CheckAndFixPartialBlock modifies the queue.
            isBreakingPhase = blocksToBreak.Count > 0;
            breakIndex = 0;
            currentIndex = 0;

            lastReportedProgress = -1;
            lastChiseledBlock = null;
            lastSentMs = 0;

            // Filter out operations that are already done in the world (crash recovery)
            int filtered = FilterCompletedOperations();
            if (filtered > 0)
            {
                capi.ShowChatMessage($"Skipped {filtered} already-completed operations.");
            }

            // Speed init — prefer user settings, fall back to SP/MP defaults.
            ApplySpeedSettings();
            successStreak = 0;
            confirmedChiselBlocks.Clear();
            materialsInitialized.Clear();
            lastToolMode = -1;

            BuildPaletteToMaterialIdxMap();

            sessionStartTimeMs = capi.ElapsedMilliseconds;
            sessionStartOps = CompletedOperations;

            int tickMs = capi.IsSinglePlayer ? 25 : 100;
            tickerID = capi.World.RegisterGameTickListener(ProcessTick, tickMs);

        }

        public void PauseConveyor()
        {
            capi.World.UnregisterGameTickListener(tickerID);
            SaveProgress();
        }

        public void ResumeConveyor()
        {
            int tickMs = capi.IsSinglePlayer ? 25 : 100;
            tickerID = capi.World.RegisterGameTickListener(ProcessTick, tickMs);
        }

        public void StopConveyor()
        {
            activeChiseling = false;
            capi.World.UnregisterGameTickListener(tickerID);
            DeleteProgressFile();
        }

        // ====================================================================
        // ChiselWiz-style algorithm: Hierarchical brush sweep (8 → 4 → 2 → 1)
        // ====================================================================

        private void BuildOperationQueue()
        {
            operationQueue = new List<ChiselOperation>();
            deferredOps = new List<ChiselOperation>();
            blocksToBreak = new List<BlockPos>();

            var voxelBlocks = myVox.GetVoxelBlocks();
            var requiredSet = new HashSet<BlockPos>(voxelBlocks.Keys);

            // Phase 1: Find blocks to break (in bounding box but NOT in model)
            var dimensionBlocks = myVox.GetDimensionsBlocksList();
            foreach (var pos in dimensionBlocks)
            {
                if (!requiredSet.Contains(pos))
                {
                    // Only break if there's actually a block there
                    var block = capi.World.BlockAccessor.GetBlock(pos);
                    if (block != null && block.BlockId != 0)
                    {
                        blocksToBreak.Add(pos);
                    }
                }
            }

            // Sort blocks to break bottom-to-top (layer by layer visual effect)
            blocksToBreak.Sort((a, b) =>
            {
                int cmp = a.Y.CompareTo(b.Y);
                if (cmp != 0) return cmp;
                cmp = a.X.CompareTo(b.X);
                if (cmp != 0) return cmp;
                return a.Z.CompareTo(b.Z);
            });

            // Phase 2: For each model block, compute chisel operations
            // Sorted by Y (bottom-up), X, Z — preserves within-block brush order (8→4→2→1)
            var sortedBlocks = voxelBlocks
                .OrderBy(kvp => kvp.Key.Y)
                .ThenBy(kvp => kvp.Key.X)
                .ThenBy(kvp => kvp.Key.Z);

            // Same sentinel approach as CountChiselOperations — baseIdx is NEVER a
            // real mapped palette color, so the coloring pass generates REMOVE+ADD
            // pairs for ALL mapped colors. Unmapped voxels stay as base (placed block).
            byte baseIdx = 255;
            HashSet<byte> mapped = null;
            if (myVox.MaterialMapping != null && !myVox.MaterialMapping.IsEmpty)
            {
                mapped = new HashSet<byte>(myVox.MaterialMapping.Assignments.Keys);
                while (mapped.Contains(baseIdx) && baseIdx > 0) baseIdx--;
            }

            foreach (var kvp in sortedBlocks)
            {
                ComputeBlockOps(kvp.Key, kvp.Value, operationQueue, deferredOps, baseIdx, mapped);
            }

            // Append deferred operations (single-voxel removes delayed to preserve block existence)
            operationQueue.AddRange(deferredOps);
            deferredOps = null;
        }

        /// <summary>
        /// Checks actual world state and removes operations that are already completed.
        /// Useful for crash recovery when progress file is behind the real world state.
        /// Uses BlockEntityMicroBlock.ConvertToVoxels() to read current voxel data.
        /// Returns the number of filtered (skipped) operations.
        /// </summary>
        private int FilterCompletedOperations()
        {
            if (operationQueue == null || operationQueue.Count == 0) return 0;

            // Collect unique blocks in the remaining operation range
            var blockVoxelCache = new Dictionary<BlockPos, BoolArray16x16x16>();
            int startIdx = isBreakingPhase ? 0 : currentIndex;

            for (int i = startIdx; i < operationQueue.Count; i++)
            {
                var bp = operationQueue[i].Block;
                if (blockVoxelCache.ContainsKey(bp)) continue;

                var be = capi.World.BlockAccessor.GetBlockEntity(bp);
                if (be is BlockEntityMicroBlock microBlock)
                {
                    microBlock.ConvertToVoxels(out BoolArray16x16x16 voxels, out _);
                    blockVoxelCache[bp] = voxels;
                }
            }

            if (blockVoxelCache.Count == 0) return 0;

            // Filter operations: skip removes where target voxels are already empty
            var newQueue = new List<ChiselOperation>(operationQueue.Count);
            int filtered = 0;

            for (int i = 0; i < operationQueue.Count; i++)
            {
                // Keep all operations before current index (already counted as done)
                if (!isBreakingPhase && i < currentIndex)
                {
                    newQueue.Add(operationQueue[i]);
                    continue;
                }

                var op = operationQueue[i];

                if (!op.IsRemove || !blockVoxelCache.TryGetValue(op.Block, out var currentVoxels))
                {
                    newQueue.Add(op);
                    continue;
                }

                // Check if ALL voxels in the brush region are already empty
                bool allEmpty = true;
                for (int dx = 0; dx < op.BrushSize && allEmpty; dx++)
                    for (int dy = 0; dy < op.BrushSize && allEmpty; dy++)
                        for (int dz = 0; dz < op.BrushSize && allEmpty; dz++)
                        {
                            int vx = op.X + dx, vy = op.Y + dy, vz = op.Z + dz;
                            if (vx < 16 && vy < 16 && vz < 16 && currentVoxels[vx, vy, vz])
                                allEmpty = false;
                        }

                if (allEmpty)
                    filtered++;
                else
                    newQueue.Add(op);
            }

            if (filtered > 0)
            {
                operationQueue = newQueue;
                // Adjust currentIndex since we removed entries before it might shift
                // But we preserved all entries before currentIndex, so it stays valid
            }

            // Also filter blocksToBreak — skip already-broken blocks
            if (isBreakingPhase && blocksToBreak != null)
            {
                var newBreaks = new List<BlockPos>();
                int breakFiltered = 0;
                for (int i = 0; i < blocksToBreak.Count; i++)
                {
                    if (i < breakIndex)
                    {
                        newBreaks.Add(blocksToBreak[i]);
                        continue;
                    }
                    var block = capi.World.BlockAccessor.GetBlock(blocksToBreak[i]);
                    if (block != null && block.BlockId != 0)
                        newBreaks.Add(blocksToBreak[i]);
                    else
                        breakFiltered++;
                }
                if (breakFiltered > 0)
                {
                    blocksToBreak = newBreaks;
                    filtered += breakFiltered;
                }
            }

            return filtered;
        }

        /// <summary>
        /// ChiselWiz-style: hierarchical brush sweep for a single block.
        /// Start from brush size 8 down to 1. For each region, check if ALL voxels
        /// in that region need to be removed. If yes, use the big brush. Otherwise,
        /// subdivide to smaller brushes.
        ///
        /// Key insight from ChiselWiz: defer the removal of the last voxel in a block
        /// so the block entity doesn't disappear mid-operation.
        /// </summary>
        /// <summary>
        /// Threshold: only use remove-and-rebuild in blocks that are mostly solid.
        /// Thin-walled blocks (domes, shells) have low goal counts — R&amp;R would
        /// generate too many add operations, causing visible holes if any fail.
        /// At 75%+ fill, the block is massive enough that a few missing voxels
        /// from failed adds are invisible inside the solid volume.
        /// </summary>
        private const int REBUILD_MIN_GOAL_VOXELS = 4096 * 3 / 4; // 3072

        private static void ComputeBlockOps(BlockPos blockPos, VoxelBlockData goalData,
            List<ChiselOperation> ops, List<ChiselOperation> deferred,
            byte baseIdx = 0, HashSet<byte> mappedIndices = null)
        {
            // Working copy: tracks what's been "processed" so far
            // Start with all 4096 voxels filled (solid block), goal = model voxels
            bool[,,] currentState = new bool[16, 16, 16];
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                        currentState[x, y, z] = true; // solid block

            // Count how many voxels the goal has (we need to keep these)
            int goalVoxelCount = goalData.VoxelCount;
            if (goalVoxelCount == 4096)
            {
                // Full block — add coloring if needed
                AppendColoringOps(blockPos, goalData, ops, baseIdx, mappedIndices);
                return;
            }
            if (goalVoxelCount == 0) return;     // Should not happen (filtered earlier)

            // Determine if this block is solid enough for remove-and-rebuild
            bool allowRebuild = goalVoxelCount >= REBUILD_MIN_GOAL_VOXELS;

            // Track remaining solid voxels in working state
            int remainingSolid = 4096;

            // Hierarchical sweep: 8 → 4 → 2 → 1
            foreach (int brushSize in VoxelConstants.BrushSizes)
            {
                int step = brushSize;
                for (int x = 0; x < 16; x += step)
                    for (int y = 0; y < 16; y += step)
                        for (int z = 0; z < 16; z += step)
                        {
                            ProcessSubBlock(blockPos, goalData, currentState,
                                x, y, z, brushSize, ref remainingSolid, ops, deferred,
                                allowRebuild);
                        }
            }

            // After REMOVE-based carving, add coloring ops for voxels that should be a specific color
            AppendColoringOps(blockPos, goalData, ops, baseIdx, mappedIndices);
        }

        /// <summary>
        /// For each voxel with a mapped non-base color, append a REMOVE+ADD pair
        /// to replace the default (base) material with the assigned block color.
        /// Respects the VS per-block limit of 16 distinct materials:
        /// at most MaxMaterialsPerBlock−1 extra colors are added per block, rest fall back to base.
        ///
        /// DISABLED at runtime until material-registration flow is implemented.
        /// Without pre-registering materials on BlockEntityChisel, server ignores ADD ops with
        /// unknown materialIdx, leaving REMOVE-only effect that can destroy the chisel block.
        /// </summary>
        private static void AppendColoringOps(BlockPos blockPos, VoxelBlockData goalData,
            List<ChiselOperation> ops, byte baseIdx, HashSet<byte> mappedIndices)
        {
            if (mappedIndices == null || mappedIndices.Count == 0) return;

            // Collect distinct non-base colors actually used in this block
            var blockColors = new HashSet<byte>();
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                    {
                        if (!goalData.Voxels[x, y, z]) continue;
                        byte tgt = goalData.MaterialIndex[x, y, z];
                        if (tgt != baseIdx && mappedIndices.Contains(tgt))
                            blockColors.Add(tgt);
                    }

            // Per-block VS limit: base + (MaxMaterialsPerBlock-1) extras = 16 slots total.
            // If exceeded, drop the least-used colors.
            var allowedColors = blockColors;
            if (blockColors.Count >= VoxelsStorage.MaxMaterialsPerBlock)
            {
                // Count voxels per color, keep top (Max-1)
                var counts = new Dictionary<byte, int>();
                for (int x = 0; x < 16; x++)
                    for (int y = 0; y < 16; y++)
                        for (int z = 0; z < 16; z++)
                        {
                            if (!goalData.Voxels[x, y, z]) continue;
                            byte tgt = goalData.MaterialIndex[x, y, z];
                            if (blockColors.Contains(tgt))
                            {
                                if (!counts.ContainsKey(tgt)) counts[tgt] = 0;
                                counts[tgt]++;
                            }
                        }
                var sorted = new List<KeyValuePair<byte, int>>(counts);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                allowedColors = new HashSet<byte>();
                for (int i = 0; i < sorted.Count && allowedColors.Count < VoxelsStorage.MaxMaterialsPerBlock - 1; i++)
                    allowedColors.Add(sorted[i].Key);
            }

            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                    {
                        if (!goalData.Voxels[x, y, z]) continue;
                        byte tgt = goalData.MaterialIndex[x, y, z];
                        if (tgt == baseIdx) continue;
                        if (!allowedColors.Contains(tgt)) continue;
                        // REMOVE then ADD with the colored material
                        ops.Add(new ChiselOperation(blockPos, x, y, z, 1, true, 0));
                        ops.Add(new ChiselOperation(blockPos, x, y, z, 1, false, tgt));
                    }
        }

        /// <summary>
        /// Process a sub-block region. Decides whether to remove it with the current brush.
        /// Mirrors ChiselWiz's ProcessSubBlock logic.
        /// </summary>
        private static void ProcessSubBlock(BlockPos blockPos, VoxelBlockData goalData,
            bool[,,] currentState, int sx, int sy, int sz, int brushSize,
            ref int remainingSolid, List<ChiselOperation> ops, List<ChiselOperation> deferred,
            bool allowRebuild)
        {
            int volume = brushSize * brushSize * brushSize;

            // Count how many voxels in this region are currently solid
            int currentCount = 0;
            // Count how many of those should be KEPT (exist in goal)
            int goalCount = 0;

            for (int dx = 0; dx < brushSize; dx++)
                for (int dy = 0; dy < brushSize; dy++)
                    for (int dz = 0; dz < brushSize; dz++)
                    {
                        int x = sx + dx, y = sy + dy, z = sz + dz;
                        if (currentState[x, y, z]) currentCount++;
                        if (goalData.Voxels[x, y, z]) goalCount++;
                    }

            if (currentCount == 0) return; // Already empty, skip

            // If goal wants ALL voxels in this region removed (goalCount == 0),
            // AND current has voxels here, we can remove the whole region at once
            if (goalCount == 0 && currentCount == volume)
            {
                // Check: would removing this leave the block completely empty?
                if (remainingSolid - volume <= 0 && brushSize == 1)
                {
                    // Defer single-voxel last removal to end
                    deferred.Add(new ChiselOperation(blockPos, sx, sy, sz, 1));
                    return;
                }

                if (remainingSolid - volume <= 0 && brushSize > 1)
                {
                    return; // Don't remove with big brush — subdivide to handle last-voxel
                }

                ops.Add(new ChiselOperation(blockPos, sx, sy, sz, brushSize));

                // Update working state
                for (int dx = 0; dx < brushSize; dx++)
                    for (int dy = 0; dy < brushSize; dy++)
                        for (int dz = 0; dz < brushSize; dz++)
                            currentState[sx + dx, sy + dy, sz + dz] = false;

                remainingSolid -= currentCount;
                return;
            }

            // ============================================================
            // Remove-and-rebuild optimization:
            // If few goal voxels exist in this region, it's cheaper to remove
            // the entire region with a big brush and add back individuals.
            // Cost: 1 remove + goalCount adds vs many subdivision operations.
            //
            // GUARDED by allowRebuild (block must be ≥75% solid):
            //   - Thin walls (domes/shells): skip R&R entirely → no holes
            //   - Massive blocks with small cutouts: use R&R → big speedup
            //     Even if a few adds fail, they're invisible inside the mass.
            //
            // Safety: block must keep ≥1 voxel after remove (before adds)
            // to prevent the server from destroying the block entity.
            // ============================================================
            if (allowRebuild && goalCount > 0 && brushSize > 1 && currentCount == volume
                && goalCount < volume / 3 && remainingSolid - volume > 0)
            {
                // Step 1: Remove entire region with big brush
                ops.Add(new ChiselOperation(blockPos, sx, sy, sz, brushSize, true));

                // Step 2: Add back goal voxels individually (brush size 1)
                for (int dx = 0; dx < brushSize; dx++)
                    for (int dy = 0; dy < brushSize; dy++)
                        for (int dz = 0; dz < brushSize; dz++)
                            if (goalData.Voxels[sx + dx, sy + dy, sz + dz])
                                ops.Add(new ChiselOperation(blockPos, sx + dx, sy + dy, sz + dz, 1, false));

                // Update working state to match goal
                for (int dx = 0; dx < brushSize; dx++)
                    for (int dy = 0; dy < brushSize; dy++)
                        for (int dz = 0; dz < brushSize; dz++)
                            currentState[sx + dx, sy + dy, sz + dz] = goalData.Voxels[sx + dx, sy + dy, sz + dz];

                remainingSolid -= (volume - goalCount);
                return;
            }

            // For brush size 1: handle individual voxels directly
            if (brushSize == 1)
            {
                if (goalCount == 0 && currentCount > 0)
                {
                    // This voxel should be removed
                    if (remainingSolid <= 1)
                    {
                        // Last voxel — defer
                        deferred.Add(new ChiselOperation(blockPos, sx, sy, sz, 1));
                        return;
                    }

                    ops.Add(new ChiselOperation(blockPos, sx, sy, sz, 1));
                    currentState[sx, sy, sz] = false;
                    remainingSolid--;
                }
                // If goalCount > 0, this voxel should stay — do nothing
            }
            // For larger brushes: if not all voxels need removing, skip and let
            // smaller brushes handle it in the next sweep iteration
        }

        // ====================================================================
        // Main tick handler — batched with adaptive speed
        // ====================================================================

        private void ProcessTick(float dt)
        {
            if (!activeChiseling) return;

            if (!ChiselDetector())
            {
                ChiselReminder();
                return;
            }

            // Server lag protection (ChiselWiz style)
            long now = capi.ElapsedMilliseconds;
            if (adaptiveSpeed && !capi.IsSinglePlayer && lastSentMs > 0
                && (now - lastSentMs) > SERVER_LAG_THRESHOLD_MS)
            {
                // Lag detected — reduce speed (only when adaptive is enabled)
                opsPerTick = Math.Max(1, opsPerTick / 2);
                successStreak = 0;
                return;
            }

            int processed = 0;

            // Phase 1: Break unwanted blocks (batch)
            while (isBreakingPhase && processed < opsPerTick)
            {
                if (breakIndex >= blocksToBreak.Count)
                {
                    isBreakingPhase = false;
                    break;
                }

                // Skip if block is already air (nothing to break)
                var breakPos = blocksToBreak[breakIndex];
                var breakBlock = capi.World.BlockAccessor.GetBlock(breakPos);
                if (breakBlock == null || breakBlock.BlockId == 0)
                {
                    breakIndex++;
                    continue; // already air, skip without counting as processed
                }

                BreakBlock(breakPos);
                breakIndex++;
                processed++;
            }

            // Phase 2: Chisel operations (batch)
            while (!isBreakingPhase && processed < opsPerTick && currentIndex < operationQueue.Count)
            {
                var op = operationQueue[currentIndex];

                // Block switch — ensure chisel block exists + check state
                if (lastChiseledBlock == null || !lastChiseledBlock.Equals(op.Block))
                {
                    // Check if block exists at all
                    var block = capi.World.BlockAccessor.GetBlock(op.Block);
                    if (block == null || block.BlockId == 0)
                    {
                        var skipBlock = op.Block;
                        while (currentIndex < operationQueue.Count
                            && operationQueue[currentIndex].Block.Equals(skipBlock))
                        {
                            currentIndex++;
                        }
                        continue;
                    }

                    EnsureChiselBlock(op.Block);
                    lastChiseledBlock = op.Block;
                    lastToolMode = -1;

                    // For colored models, add all mapped materials to this new chisel block.
                    // If blocks are missing from inventory, this returns false — we bail
                    // the tick, the player sees a chat request, and we retry next tick.
                    if (!EnsureMaterialsForBlock(op.Block))
                    {
                        // Reset so we retry EnsureChiselBlock + EnsureMaterialsForBlock next tick
                        lastChiseledBlock = null;
                        break;
                    }

                    // Optimize operations for partially chiseled blocks — ONLY for mono-color.
                    // For colored chisel: CheckAndFix only compares voxel presence (bool),
                    // ignoring material colors. If block is fresh (just created, all solid base)
                    // it would report "matches goal" and wipe out our coloring REMOVE+ADD ops.
                    // Skip to preserve the original op queue's coloring pairs.
                    if (paletteToMaterialIdx.Count <= 1)
                    {
                        CheckAndFixPartialBlock(op.Block);
                    }
                }

                // Re-check bounds (CheckAndFixPartialBlock may have inserted ops)
                if (currentIndex >= operationQueue.Count) break;
                op = operationQueue[currentIndex];

                EnsureToolMode(op.ToolMode, op.Block);
                byte matIdx;
                if (op.IsRemove)
                {
                    matIdx = 1;
                }
                else
                {
                    // ADD op — look up materialIdx from the palette→material map.
                    // Default to 0 (base material) for voxels with unmapped colors.
                    if (!paletteToMaterialIdx.TryGetValue(op.PaletteIdx, out matIdx))
                        matIdx = 0;
                }
                SendChiselPacket(op.Block, op.X, op.Y, op.Z, op.IsRemove, matIdx);
                currentIndex++;
                processed++;
            }

            // Check completion
            if (!isBreakingPhase && currentIndex >= operationQueue.Count)
            {
                capi.ShowChatMessage("Chiseling completed!");
                FinishChiseling();
                return;
            }

            lastSentMs = now;
            ReportProgress();

            // Adaptive speed: increase after sustained success (skipped when user
            // disabled adaptive mode — speed stays locked at InitialOpsPerTick).
            successStreak++;
            if (adaptiveSpeed && successStreak % 20 == 0 && opsPerTick < maxOpsPerTick)
            {
                opsPerTick++;
            }
        }

        /// <summary>
        /// If a block is already partially chiseled, compute the DIFF between
        /// current state and goal, then generate efficient operations:
        /// - Missing voxels (goal=solid, current=empty) → hierarchical ADD (8→4→2→1)
        /// - Extra voxels (goal=empty, current=solid) → hierarchical REMOVE (8→4→2→1)
        /// - Already correct → skip
        /// Replaces the pre-computed operations for this block with an optimal set.
        /// </summary>
        private void CheckAndFixPartialBlock(BlockPos pos)
        {
            var be = capi.World.BlockAccessor.GetBlockEntity(pos);
            if (!(be is BlockEntityMicroBlock microBlock)) return;

            microBlock.ConvertToVoxels(out BoolArray16x16x16 currentVoxels, out _);

            // Count — if full block, original operations are already optimal
            int currentCount = 0;
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                        if (currentVoxels[x, y, z]) currentCount++;
            if (currentCount == 4096) return;

            // Get goal for this block
            var goalBlocks = myVox.GetVoxelBlocks();
            if (!goalBlocks.TryGetValue(pos, out var goalData)) return;

            // Build diff maps:
            // needAdd[x,y,z] = goal is solid, current is empty → must add
            // needRemove[x,y,z] = goal is empty, current is solid → must remove
            bool[,,] needAdd = new bool[16, 16, 16];
            bool[,,] needRemove = new bool[16, 16, 16];
            int addCount = 0, removeCount = 0;

            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                    {
                        bool cur = currentVoxels[x, y, z];
                        bool goal = goalData.Voxels[x, y, z];
                        if (goal && !cur) { needAdd[x, y, z] = true; addCount++; }
                        if (!goal && cur) { needRemove[x, y, z] = true; removeCount++; }
                    }

            if (addCount == 0 && removeCount == 0) return; // already matches goal

            // Generate efficient operations using hierarchical brush sweep
            var newOps = new List<ChiselOperation>();

            // Phase A: ADD missing voxels — hierarchical 8→4→2→1
            if (addCount > 0)
                GenerateHierarchicalOps(pos, needAdd, addCount, false, newOps);

            // Phase B: REMOVE extra voxels — hierarchical 8→4→2→1
            if (removeCount > 0)
                GenerateHierarchicalOps(pos, needRemove, removeCount, true, newOps);

            // VERIFY: simulate operations locally to guarantee correctness
            newOps = VerifyOrFallback(currentVoxels, goalData, newOps, pos);

            // Replace this block's operations in queue
            int idx = currentIndex;
            while (idx < operationQueue.Count && operationQueue[idx].Block.Equals(pos))
                idx++;

            operationQueue.RemoveRange(currentIndex, idx - currentIndex);
            operationQueue.InsertRange(currentIndex, newOps);
        }

        /// <summary>
        /// Simulates operations on a local copy and verifies result matches goal.
        /// If simulation fails, falls back to safe brush-1 operations.
        /// </summary>
        private static List<ChiselOperation> VerifyOrFallback(
            BoolArray16x16x16 currentVoxels, VoxelBlockData goalData,
            List<ChiselOperation> ops, BlockPos pos)
        {
            // Simulate on copy
            bool[,,] simState = new bool[16, 16, 16];
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                        simState[x, y, z] = currentVoxels[x, y, z];

            foreach (var op in ops)
            {
                for (int dx = 0; dx < op.BrushSize; dx++)
                    for (int dy = 0; dy < op.BrushSize; dy++)
                        for (int dz = 0; dz < op.BrushSize; dz++)
                        {
                            int vx = op.X + dx, vy = op.Y + dy, vz = op.Z + dz;
                            if (vx < 16 && vy < 16 && vz < 16)
                                simState[vx, vy, vz] = !op.IsRemove; // add=true, remove=false
                        }
            }

            // Compare simulated result with goal
            bool match = true;
            for (int x = 0; x < 16 && match; x++)
                for (int y = 0; y < 16 && match; y++)
                    for (int z = 0; z < 16 && match; z++)
                        if (simState[x, y, z] != goalData.Voxels[x, y, z])
                            match = false;

            if (match) return ops; // Hierarchical ops are correct

            // Real fallback: brush=1 per-voxel. Slower but guarantees every voxel
            // that needs changing actually gets its own op. This is the safety net
            // for rare cases where the hierarchical pass leaves something behind
            // (e.g., edge voxels or partial-block interactions with existing chisel data).
            var fallbackOps = new List<ChiselOperation>();

            // Phase A: ADD missing voxels, one at a time
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                    {
                        bool cur = currentVoxels[x, y, z];
                        bool goal = goalData.Voxels[x, y, z];
                        if (goal && !cur)
                            fallbackOps.Add(new ChiselOperation(pos, x, y, z, 1, false));
                    }

            // Phase B: REMOVE extra voxels, one at a time
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                    {
                        bool cur = currentVoxels[x, y, z];
                        bool goal = goalData.Voxels[x, y, z];
                        if (!goal && cur)
                            fallbackOps.Add(new ChiselOperation(pos, x, y, z, 1, true));
                    }

            return fallbackOps;
        }

        /// <summary>
        /// Generates hierarchical brush operations (8→4→2→1) for a set of target voxels.
        /// Works for both ADD and REMOVE — same algorithm, different isRemove flag.
        /// Only uses big brush if ALL voxels in the region are targets.
        /// </summary>
        private static void GenerateHierarchicalOps(BlockPos blockPos, bool[,,] targets,
            int totalTargets, bool isRemove, List<ChiselOperation> ops)
        {
            // Track which targets have been handled
            bool[,,] handled = new bool[16, 16, 16];

            foreach (int brushSize in VoxelConstants.BrushSizes)
            {
                int vol = brushSize * brushSize * brushSize;

                for (int sx = 0; sx < 16; sx += brushSize)
                    for (int sy = 0; sy < 16; sy += brushSize)
                        for (int sz = 0; sz < 16; sz += brushSize)
                        {
                            // Check if ALL voxels in this region are targets.
                            // Brush applies to entire region — any non-target = can't use.
                            bool allTargets = true;
                            for (int dx = 0; dx < brushSize && allTargets; dx++)
                                for (int dy = 0; dy < brushSize && allTargets; dy++)
                                    for (int dz = 0; dz < brushSize && allTargets; dz++)
                                        if (!targets[sx + dx, sy + dy, sz + dz])
                                            allTargets = false;

                            if (!allTargets) continue;

                            // Check at least one unhandled voxel (avoid duplicate ops)
                            bool hasUnhandled = false;
                            for (int dx = 0; dx < brushSize && !hasUnhandled; dx++)
                                for (int dy = 0; dy < brushSize && !hasUnhandled; dy++)
                                    for (int dz = 0; dz < brushSize && !hasUnhandled; dz++)
                                        if (!handled[sx + dx, sy + dy, sz + dz])
                                            hasUnhandled = true;

                            if (!hasUnhandled) continue;

                            ops.Add(new ChiselOperation(blockPos, sx, sy, sz, brushSize, isRemove));
                            for (int dx = 0; dx < brushSize; dx++)
                                for (int dy = 0; dy < brushSize; dy++)
                                    for (int dz = 0; dz < brushSize; dz++)
                                        handled[sx + dx, sy + dy, sz + dz] = true;
                        }
            }

            // Brush 1 fallback: individual voxels not covered by bigger brushes
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                        if (targets[x, y, z] && !handled[x, y, z])
                            ops.Add(new ChiselOperation(blockPos, x, y, z, 1, isRemove));
        }

        /// <summary>
        /// Full stop: unregister tick, clean up, fire event.
        /// </summary>
        private void FinishChiseling()
        {
            activeChiseling = false;
            capi.World.UnregisterGameTickListener(tickerID);
            DeleteProgressFile();
            OnCompleted?.Invoke();
        }

        private void ReportProgress()
        {
            float progress = ProgressPercent;
            int pInt = (int)progress;
            if (pInt != lastReportedProgress)
            {
                lastReportedProgress = pInt;
                OnProgressChanged?.Invoke(progress);
            }
            if (CompletedOperations % 100 == 0)
                SaveProgress();
        }

        // ====================================================================
        // Network — matches ChiselWiz packet format
        // ====================================================================

        private void EnsureChiselBlock(BlockPos pos)
        {
            // Cache: skip if already confirmed as chisel block
            if (confirmedChiselBlocks.Contains(pos)) return;

            var blockEntity = capi.World.BlockAccessor.GetBlockEntity(pos);
            if (blockEntity is BlockEntityChisel)
            {
                confirmedChiselBlocks.Add(pos);
                return;
            }

            // Chisel stays in hand — SendHandInteraction with chisel on a chisellable
            // block converts it to a BlockEntityChisel using the block's own material
            // (that becomes MaterialIds[0]). For colored chiseling, the user is
            // expected to place the BASE material block in world at all model
            // positions — those blocks become MaterialIds[0] when converted.
            // Additional colors are added on top via EnsureMaterialsForBlock.
            var block = capi.World.BlockAccessor.GetBlock(pos);
            if (block != null && block.Id != 0)
            {
                var blockSel = new BlockSelection(pos, BlockFacing.NORTH, block);
                (capi.World as ClientMain)?.SendHandInteraction(2, blockSel, null,
                    EnumHandInteract.HeldItemInteract, EnumHandInteractNw.StartHeldItemUse, true,
                    EnumItemUseCancelReason.ReleasedMouse);
            }
            confirmedChiselBlocks.Add(pos);
        }

        private void EnsureToolMode(int toolMode, BlockPos pos)
        {
            // Fast cache check — avoid GetToolMode + BlockSelection allocation
            if (toolMode == lastToolMode) return;

            var slot = capi.World.Player.InventoryManager?.ActiveHotbarSlot;
            if (slot?.Itemstack?.Collectible == null) return;

            var block = capi.World.BlockAccessor.GetBlock(pos);
            var blockSel = new BlockSelection(pos, BlockFacing.NORTH, block);
            SetToolMode(toolMode, blockSel);
            lastToolMode = toolMode;
        }

        /// <summary>
        /// Send chisel packet — matches ChiselWiz SingleChiselOperation format.
        /// materialIdx: 0 = first material (used for add), 1 = default (used for remove).
        /// </summary>
        private void SendChiselPacket(BlockPos pos, int x, int y, int z, bool isRemove, byte materialIdx = 1)
        {
            // CRITICAL: server's ChiselMode.Apply places ADD voxels at
            //     newPos = voxelPos + (ChiselSize × facing.Normali)
            // (verified via IL decompilation). So if we want an ADD at (x, y, z), we must
            // offset the packet's voxelPos in the opposite direction of the chosen facing.
            //
            // For brush=1 (ChiselSize=1) we need the packet's (packetX, packetY, packetZ)
            // + facing.Normali = (x, y, z). For REMOVE ops the facing is ignored —
            // voxelPos is used directly — so we just pass (x, y, z) unchanged.
            int facing;
            int packetX = x, packetY = y, packetZ = z;
            if (isRemove)
            {
                facing = BlockFacing.indexWEST; // ignored by server for REMOVE
            }
            else
            {
                // Pick the Y axis: most common case (y > 0) uses facing=UP with packetY=y-1.
                // For y == 0 (bottom voxel of the chisel block) use facing=DOWN with packetY=1.
                if (y > 0)
                {
                    facing = BlockFacing.indexUP;
                    packetY = y - 1;
                }
                else
                {
                    facing = BlockFacing.indexDOWN;
                    packetY = 1;
                }
            }

            byte[] data;
            using (var ms = new MemoryStream())
            {
                var writer = new BinaryWriter(ms);
                writer.Write(packetX);
                writer.Write(packetY);
                writer.Write(packetZ);
                writer.Write(isRemove);
                // Server reads facing as Int16 (2 bytes), not Int32 — critical alignment.
                writer.Write((short)facing);
                writer.Write(materialIdx);
                data = ms.ToArray();
            }
            capi.Network.SendBlockEntityPacket(pos, 1010, data);
        }

        private bool BreakBlock(BlockPos pos)
        {
            var block = capi.World.BlockAccessor.GetBlock(pos);
            if (block != null && block.BlockId != 0)
            {
                var bs = new BlockSelection();
                bs.Position = pos;
                bs.Face = BlockFacing.NORTH;
                bs.HitPosition = pos.ToVec3d();
                bs.DidOffset = false;
                bs.SelectionBoxIndex = 0;
                (capi.World as ClientMain)?.SendHandInteraction(2, bs, null,
                    EnumHandInteract.HeldItemAttack, EnumHandInteractNw.CancelHeldItemUse, true,
                    EnumItemUseCancelReason.ReleasedMouse);
            }
            return true;
        }

        private void SetToolMode(int num, BlockSelection blockSel)
        {
            var slot = capi.World.Player.InventoryManager?.ActiveHotbarSlot;
            var obj = slot?.Itemstack?.Collectible;
            if (obj == null) return;

            // VS's ItemChisel.SetToolMode can NRE inside AddMaterial if the target
            // chisel block's MaterialIds aren't populated yet (freshly created BE,
            // data not yet arrived from server, etc.). We MUST NOT let this throw,
            // because it aborts the tick before Packet_ToolMode is sent — meaning
            // the server never gets the brush-size update, all subsequent packets
            // execute with brush=1 (default), and the model comes out as scattered dots.
            try
            {
                obj.SetToolMode(slot, capi.World.Player, blockSel, num);
            }
            catch (Exception ex)
            {
                capi.Logger.VerboseDebug("[AutoChisel] ItemChisel.SetToolMode client-side threw (ignored): " + ex.Message);
            }

            // Always send the server-side packet — this is what actually applies the tool mode.
            var packet = new Packet_Client();
            packet.Id = 27;
            var toolMode = new Packet_ToolMode();
            toolMode.Mode = num;
            toolMode.X = blockSel?.Position?.X ?? 0;
            toolMode.Y = blockSel?.Position?.Y ?? 0;
            toolMode.Z = blockSel?.Position?.Z ?? 0;
            packet.ToolMode = toolMode;
            capi.Network.SendPacketClient(packet);
            slot.MarkDirty();
        }

        private bool ChiselDetector()
        {
            var slot = capi.World.Player.InventoryManager?.ActiveHotbarSlot;
            return slot?.Itemstack?.Collectible is ItemChisel;
        }

        private void ChiselReminder()
        {
            if (capi.ElapsedMilliseconds - lastReminder > 3000)
            {
                capi.ShowChatMessage("Take the chisel in your hand!");
                lastReminder = capi.ElapsedMilliseconds;
            }
        }

        /// <summary>Finds a slot (hotbar or backpack) containing the given block; null if none.</summary>
        private ItemSlot FindInventorySlotWithBlock(AssetLocation loc)
        {
            if (loc == null) return null;
            var invMgr = capi.World.Player.InventoryManager;
            if (invMgr == null) return null;

            ItemSlot Scan(IInventory inv)
            {
                if (inv == null) return null;
                foreach (var slot in inv)
                {
                    var st = slot?.Itemstack;
                    if (st?.Block != null && st.Block.Code != null
                        && st.Block.Code.Equals(loc) && st.StackSize > 0)
                        return slot;
                }
                return null;
            }

            return Scan(invMgr.GetOwnInventory(GlobalConstants.hotBarInvClassName))
                ?? Scan(invMgr.GetOwnInventory(GlobalConstants.backpackInvClassName));
        }

        /// <summary>Finds the first hotbar slot containing an ItemChisel, or -1.</summary>
        private int FindChiselHotbarSlot()
        {
            var hotbar = capi.World.Player.InventoryManager?
                .GetOwnInventory(GlobalConstants.hotBarInvClassName);
            if (hotbar == null) return -1;
            for (int i = 0; i < hotbar.Count; i++)
            {
                if (hotbar[i]?.Itemstack?.Collectible is ItemChisel) return i;
            }
            return -1;
        }

        /// <summary>Switches the active hotbar slot to one holding the given block. Returns false if not found in hotbar.</summary>
        private bool SwitchHotbarToBlock(AssetLocation loc)
        {
            var invMgr = capi.World.Player.InventoryManager;
            var hotbar = invMgr?.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            if (hotbar == null) return false;
            for (int i = 0; i < hotbar.Count; i++)
            {
                var st = hotbar[i]?.Itemstack;
                if (st?.Block != null && st.Block.Code != null
                    && st.Block.Code.Equals(loc) && st.StackSize > 0)
                {
                    invMgr.ActiveHotbarSlotNumber = i;
                    return true;
                }
            }
            return false;
        }

        private AssetLocation lastRequestedMaterial;
        private long lastMaterialRequestMs;

        private void RequestMaterialInChat(AssetLocation loc)
        {
            if (loc == null) return;
            if (loc.Equals(lastRequestedMaterial)
                && capi.ElapsedMilliseconds - lastMaterialRequestMs < 5000) return;
            lastRequestedMaterial = loc;
            lastMaterialRequestMs = capi.ElapsedMilliseconds;

            var block = capi.World.GetBlock(loc);
            string displayName = block?.GetPlacedBlockName(capi.World, null) ?? loc.ToString();
            capi.ShowChatMessage($"[AutoChisel] Need '{displayName}' in inventory to continue. Add to hotbar or backpack.");
        }

        // ============================================================================
        // Multi-color: add extra materials to a freshly-created BlockEntityChisel
        // using the standard VS AddMaterial mechanism (pick block to mouse slot,
        // then send Packet_ToolMode with mode=-1 to trigger server-side AddMaterial).
        // Based on the ChiselWiz approach.
        // ============================================================================

        /// <summary>
        /// For a just-placed chisel block at <paramref name="pos"/>, add all mapped palette
        /// materials (other than the base one, which was placed via EnsureChiselBlock).
        /// Returns true if all materials are successfully set up, false if we need to wait
        /// (e.g., block missing from inventory — caller should retry next tick).
        /// </summary>
        private bool EnsureMaterialsForBlock(BlockPos pos)
        {
            if (materialsInitialized.Contains(pos)) return true;
            // Mono-color: nothing to add, base material from EnsureChiselBlock is enough
            if (paletteToMaterialIdx == null || paletteToMaterialIdx.Count <= 1)
            {
                materialsInitialized.Add(pos);
                return true;
            }

            var be = capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityChisel;
            if (be == null) return false;  // BE not synced yet, retry next tick

            var mouseSlot = capi.World.Player.InventoryManager?.MouseItemSlot;
            if (mouseSlot == null || mouseSlot.Itemstack != null) return false;

            int chiselSlot = FindChiselHotbarSlot();
            if (chiselSlot < 0)
            {
                // Only chat this occasionally — it's actionable for the player
                if (capi.ElapsedMilliseconds - lastChiselReminderMs > 5000)
                {
                    capi.ShowChatMessage("[AutoChisel] Put a chisel in hotbar for colored chiseling.");
                    lastChiselReminderMs = capi.ElapsedMilliseconds;
                }
                return false;
            }

            // Iterate in materialIdx order. Skip materialIdx 0 (base — placed during
            // EnsureChiselBlock, already in MaterialIds[0]).
            bool addedAny = false;
            // Iterate in strict matIdx order. Server's AddMaterial appends to MaterialIds,
            // so the Nth AddMaterial call creates MaterialIds[base+N]. Our paletteToMaterialIdx
            // MUST match that ordering — sort by value explicitly (Dictionary iteration order
            // isn't contractually guaranteed, although in practice it's insertion order in .NET).
            foreach (var kvp in paletteToMaterialIdx.OrderBy(k => k.Value))
            {
                byte paletteIdx = kvp.Key;
                if (!myVox.MaterialMapping.TryGet(paletteIdx, out AssetLocation blockLoc)) continue;

                if (ChiselBlockHasMaterial(pos, blockLoc)) continue;

                var materialSlot = FindInventorySlotWithBlock(blockLoc);
                if (materialSlot == null)
                {
                    RequestMaterialInChat(blockLoc);
                    return false;
                }

                if (!SendAddMaterial(pos, materialSlot, chiselSlot))
                {
                    return false;
                }
                addedAny = true;
            }

            // Batched: rebuild mesh only once after all materials are added
            if (addedAny) be.MarkDirty(true);

            materialsInitialized.Add(pos);
            return true;
        }

        /// <summary>Quick check — does the chisel block at pos already have this material in its MaterialIds?</summary>
        private bool ChiselBlockHasMaterial(BlockPos pos, AssetLocation blockLoc)
        {
            var be = capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityChisel;
            if (be?.BlockIds == null) return false;
            int targetId = capi.World.GetBlock(blockLoc)?.Id ?? 0;
            if (targetId == 0) return false;
            foreach (int id in be.BlockIds) if (id == targetId) return true;
            return false;
        }

        /// <summary>
        /// Full ChiselWiz-style AddMaterial flow:
        /// 1. Validate the block is a legal chisel material
        /// 2. Call BlockEntityChisel.AddMaterial(block, ref isFull, true) LOCALLY (client-side)
        ///    — this updates MaterialIds on the client immediately. Found via reflection:
        ///      `public int AddMaterial(Block addblock, ref bool isFull, bool compareToPickBlock)`.
        /// 3. MarkDirty(true) to rebuild the mesh
        /// 4. Pick up the WHOLE stack into mouse slot (for server-side consumption)
        /// 5. Switch active hotbar to chisel
        /// 6. Send Packet_ToolMode { Mode = -1, pos } — server calls its own AddMaterial
        /// 7. Return remainder from mouse slot back to inventory
        /// </summary>
        private bool SendAddMaterial(BlockPos pos, ItemSlot materialSlot, int chiselSlot)
        {
            var be = capi.World.BlockAccessor.GetBlockEntity(pos) as BlockEntityChisel;
            if (be == null) return false;

            var block = materialSlot.Itemstack?.Block;
            if (block == null || block.Id == 0) return false;
            int qty = materialSlot.Itemstack.StackSize;
            if (qty <= 0) return false;

            // Skip if already in MaterialIds
            if (be.BlockIds != null)
                foreach (int id in be.BlockIds)
                    if (id == block.Id) return true;

            // === Step 1: local client-side AddMaterial (no MarkDirty here — caller batches) ===
            // Use the 3-arg overload with compareToPickBlock=FALSE. The 1-arg and 3-arg-with-true
            // variants access pickblock attributes that can be null on freshly-created chisel
            // blocks (NRE in ArrayExtensions.Contains). We already validated via
            // IsValidChiselingMaterial, so the strict pickblock check is redundant here.
            int newMatIdx = -1;
            try
            {
                newMatIdx = be.AddMaterial(block, out bool _, false);
            }
            catch
            {
                // Even with false, some edge blocks may throw — server-side sync handles it.
            }

            // === Step 2: server-side sync via toolmode=-1 packet ===
            // Use inventory.ActivateSlot(slotId, mouseSlot, ref op) — this simulates the
            // standard player-click on the slot: moves items AND returns the packet needed
            // to sync with the server. (TryPutInto only does client-side move + returns int quantity.)
            var invMgr = capi.World.Player.InventoryManager;
            var mouseSlot = invMgr.MouseItemSlot;
            var sourceInv = materialSlot.Inventory;
            int sourceSlotId = sourceInv.GetSlotId(materialSlot);
            if (sourceSlotId < 0) return false;

            var pickupOp = new ItemStackMoveOperation(capi.World,
                EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, qty);
            object pickupPkt = sourceInv.ActivateSlot(sourceSlotId, mouseSlot, ref pickupOp);

            if (mouseSlot.Itemstack == null) return false;

            if (pickupPkt is Packet_Client pc1)
                capi.Network.SendPacketClient(pc1);

            invMgr.ActiveHotbarSlotNumber = chiselSlot;
            SendToolModePacket(pos, -1);

            // Return remainder from mouse slot to the source inventory slot
            if (mouseSlot.Itemstack != null)
            {
                var returnOp = new ItemStackMoveOperation(capi.World,
                    EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge,
                    mouseSlot.Itemstack.StackSize);
                // Return via same ActivateSlot mechanic (click on source with mouse full).
                object returnPkt = sourceInv.ActivateSlot(sourceSlotId, mouseSlot, ref returnOp);
                if (returnPkt is Packet_Client pc2)
                    capi.Network.SendPacketClient(pc2);
            }

            return true;
        }

        // Log-only diagnostic — goes to client-main.log, NOT to chat.
        private void Diag(string msg)
        {
            capi.Logger.VerboseDebug("[AutoChisel] " + msg);
        }

        private long lastChiselReminderMs;

        /// <summary>
        /// Sends the raw Packet_ToolMode with the given mode and target position.
        /// mode = -1 is the VS-native trigger to add the mouse-slot block as a new material.
        /// </summary>
        private void SendToolModePacket(BlockPos pos, int mode)
        {
            var packet = new Packet_Client();
            packet.Id = 27;
            var tm = new Packet_ToolMode();
            tm.Mode = mode;
            tm.X = pos.X;
            tm.Y = pos.Y;
            tm.Z = pos.Z;
            packet.ToolMode = tm;
            capi.Network.SendPacketClient(packet);
        }

        // ====================================================================
        // Progress save/load
        // ====================================================================

        private string GetProgressFilePath()
        {
            string basePath = ModPaths.Progress;
            return Path.Combine(basePath, myVox.GetFileName() + "_progress.json");
        }

        private void SaveProgress()
        {
            try
            {
                string path = GetProgressFilePath();
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var progress = new ChiselProgress
                {
                    ModelFileName = myVox.GetFileName(),
                    StartX = myVox.GetBlocksOffset().X,
                    StartY = myVox.GetBlocksOffset().Y,
                    StartZ = myVox.GetBlocksOffset().Z,
                    OffsetVoxX = myVox.GetVoxelsOffset().X,
                    OffsetVoxY = myVox.GetVoxelsOffset().Y,
                    OffsetVoxZ = myVox.GetVoxelsOffset().Z,
                    Alignment = myVox.GetAlignment().ToString(),
                    CurrentIndex = CompletedOperations,
                    TotalOperations = TotalOperations,
                    GeneratorName = myVox.GeneratorName,
                    GeneratorParams = myVox.GeneratorParams,
                    MaterialAssignments = myVox.MaterialMapping != null && !myVox.MaterialMapping.IsEmpty
                        ? new Dictionary<byte, string>(myVox.MaterialMapping.Assignments)
                        : null
                };
                string json = JsonConvert.SerializeObject(progress, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception e)
            {
                capi.Logger.Warning($"[AutoChisel] SaveProgress FAILED: {e.Message}");
            }
        }

        private void DeleteProgressFile()
        {
            try
            {
                var path = GetProgressFilePath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Deletes the progress file for a specific model by name. Public so
        /// HUD Stop callback can clear it even when no live ChiselConveyor
        /// instance exists (e.g. after auto-restore, before user presses Start).
        /// </summary>
        public static void DeleteProgressFile(string modelFileName)
        {
            if (string.IsNullOrEmpty(modelFileName)) return;
            try
            {
                string path = Path.Combine(ModPaths.Progress, modelFileName + "_progress.json");
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Deletes ALL progress files in the progress folder. Used by Stop
        /// when we don't know (or can't determine) which model's progress
        /// is stale, to prevent zombie "Resume?" prompts.
        /// </summary>
        public static void DeleteAllProgressFiles()
        {
            try
            {
                if (!Directory.Exists(ModPaths.Progress)) return;
                foreach (var f in Directory.GetFiles(ModPaths.Progress, "*_progress.json"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch (Exception) { }
        }

        public static ChiselProgress LoadProgress(string filename)
        {
            try
            {
                string basePath = ModPaths.Progress;
                string path = Path.Combine(basePath, filename + "_progress.json");
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<ChiselProgress>(json);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Scans worldedit folder for any *_progress.json file and returns the first one found.
        /// Used for auto-resume when no model is loaded.
        /// </summary>
        public static ChiselProgress FindProgressFile()
        {
            try
            {
                string basePath = ModPaths.Progress;
                GamePaths.EnsurePathExists(basePath);
                if (!Directory.Exists(basePath)) return null;

                foreach (var file in Directory.GetFiles(basePath, "*_progress.json"))
                {
                    string json = File.ReadAllText(file);
                    var progress = JsonConvert.DeserializeObject<ChiselProgress>(json);
                    if (progress != null && !string.IsNullOrEmpty(progress.ModelFileName))
                        return progress;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
