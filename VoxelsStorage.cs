using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace AutomaticChiselling
{
    /// <summary>
    /// Flat voxel data for a single 16x16x16 block.
    /// </summary>
    public class VoxelBlockData
    {
        public bool[,,] Voxels = new bool[16, 16, 16];
        public byte[,,] MaterialIndex = new byte[16, 16, 16];
        public int VoxelCount;

        public VoxelBlockData Clone()
        {
            var clone = new VoxelBlockData();
            clone.VoxelCount = VoxelCount;
            Array.Copy(Voxels, clone.Voxels, Voxels.Length);
            Array.Copy(MaterialIndex, clone.MaterialIndex, MaterialIndex.Length);
            return clone;
        }
    }

    /// <summary>
    /// Raw voxel with position and color index.
    /// </summary>
    public struct RawVoxel
    {
        public Vec3i Position;
        public byte ColorIndex;

        public RawVoxel(Vec3i pos, byte colorIndex)
        {
            Position = pos;
            ColorIndex = colorIndex;
        }
    }

    public class VoxelsStorage
    {
        private Dictionary<BlockPos, VoxelBlockData> voxelBlocks = new Dictionary<BlockPos, VoxelBlockData>();

        private Vec3i VoxelsOffset = new Vec3i(0, 0, 0);
        private Vec3i BlocksOffset = new Vec3i(0, 0, 0);
        private Vec3i MinPos = new Vec3i(0, 0, 0);
        private Vec3i MaxPos = new Vec3i(0, 0, 0);
        private ModelAllign Allign = ModelAllign.Center;
        private Vec3i BlockCorrection = new Vec3i(0, 0, 0);
        private int rotationY = 0;
        private int rotationX = 0;
        private int rotationZ = 0;

        private string voxFileName = "";
        private VoxFile voxFile;
        private List<RawVoxel> rawVoxels = new List<RawVoxel>();
        private List<RawVoxel> originalRawVoxels; // backup for generated models (no voxFile)

        // Color palette from .vox file (up to 256 colors)
        public byte[][] Palette { get; private set; } = new byte[256][];

        /// <summary>
        /// Canonical palette indices actually used by this model's voxels (after RGB dedup).
        /// Colors with the same RGB are merged to the first occurring index. Size 1 → mono, >1 → colored.
        /// </summary>
        public List<byte> UsedPaletteIndices { get; private set; } = new List<byte>();

        /// <summary>True if the model uses more than one distinct RGB color.</summary>
        public bool IsColored => UsedPaletteIndices.Count > 1;

        /// <summary>Material mapping (palette idx → block AssetLocation). Null if unassigned.</summary>
        public MaterialMapping MaterialMapping { get; set; }

        /// <summary>
        /// VS BlockEntityChisel limit — max distinct materials PER BLOCK (not per model).
        /// Checked at chisel time per 16x16x16 block, not at load time.
        /// </summary>
        public const int MaxMaterialsPerBlock = 16;

        /// <summary>
        /// Number of interior (unreachable from outside) voxels filled by optimization.
        /// </summary>
        public int InteriorVoxelsFilled { get; private set; } = 0;

        /// <summary>
        /// Original block count before interior fill (matches highlighted blocks).
        /// </summary>
        public int OriginalBlockCount { get; private set; } = 0;

        /// <summary>
        /// For generated models: generator name + params so we can re-create this storage.
        /// Null for models loaded from .vox files.
        /// </summary>
        public string GeneratorName { get; private set; }
        public Dictionary<string, object> GeneratorParams { get; private set; }

        public VoxelsStorage(string filename)
        {
            if (!LoadFromFile(filename))
            {
                return;
            }
            voxFileName = filename;
            LoadRawVoxels();
            UpdateModel();
        }

        /// <summary>
        /// Creates a VoxelsStorage from a list of raw voxels (for generated shapes).
        /// Optional palette gives per-color RGBA; if null a default gray is used.
        /// </summary>
        public static VoxelsStorage FromRawVoxels(List<RawVoxel> voxels, string name,
            string generatorName = null, Dictionary<string, object> generatorParams = null,
            byte[][] palette = null)
        {
            var storage = new VoxelsStorage();
            storage.voxFileName = name;
            storage.GeneratorName = generatorName;
            storage.GeneratorParams = generatorParams != null
                ? new Dictionary<string, object>(generatorParams) : null;
            storage.rawVoxels = voxels;
            storage.originalRawVoxels = voxels.Select(v =>
                new RawVoxel(new Vec3i(v.Position.X, v.Position.Y, v.Position.Z), v.ColorIndex)).ToList();

            storage.Palette = new byte[256][];
            for (int i = 0; i < 256; i++)
            {
                if (palette != null && i < palette.Length && palette[i] != null)
                    storage.Palette[i] = palette[i];
                else
                    storage.Palette[i] = new byte[] { 180, 180, 180, 255 };
            }

            storage.UpdateModelFromRawVoxels();
            return storage;
        }

        // Private constructor for FromRawVoxels
        private VoxelsStorage() { }

        private bool LoadFromFile(string filename)
        {
            string basePath = Path.Combine(ModPaths.Models, filename) + ".vox";
            if (!File.Exists(basePath))
                return false;

            try
            {
                voxFile = VoxFile.Read(basePath);
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        private void LoadRawVoxels()
        {
            rawVoxels = new List<RawVoxel>();

            // Extract palette
            if (voxFile.Palette != null)
            {
                Palette = new byte[256][];
                for (int i = 0; i < 256; i++)
                {
                    var c = voxFile.Palette[i];
                    Palette[i] = new byte[] { c.R, c.G, c.B, c.A };
                }
            }
            else
            {
                // No palette in file — fill with white so Palette[idx] never crashes
                Palette = new byte[256][];
                for (int i = 0; i < 256; i++)
                    Palette[i] = new byte[] { 255, 255, 255, 255 };
            }

            foreach (var model in voxFile.Models)
            {
                // MagicaVoxel: Y-up, VS: Z-up. Convert: (X, Z, -Y)
                foreach (var v in model.Voxels)
                {
                    Vec3i voxel = new Vec3i(v.X, v.Z, -v.Y);
                    rawVoxels.Add(new RawVoxel(voxel, v.ColorIndex));
                }
            }
        }

        private void ApplyAlignModel()
        {
            if (rawVoxels == null || rawVoxels.Count == 0)
            {
                return;
            }

            int minX = rawVoxels[0].Position.X;
            int minY = rawVoxels[0].Position.Y;
            int minZ = rawVoxels[0].Position.Z;
            int maxX = minX, maxY = minY, maxZ = minZ;

            for (int i = 1; i < rawVoxels.Count; i++)
            {
                var p = rawVoxels[i].Position;
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            // Normalize to (0,0,0) origin
            for (int i = 0; i < rawVoxels.Count; i++)
            {
                var rv = rawVoxels[i];
                rv.Position = new Vec3i(rv.Position.X - minX, rv.Position.Y - minY, rv.Position.Z - minZ);
                rawVoxels[i] = rv;
            }

            int sizeX = maxX - minX + 1;
            int sizeY = maxY - minY + 1;
            int sizeZ = maxZ - minZ + 1;

            int blockSizeX = (int)Math.Ceiling(sizeX / 16f);
            int blockSizeY = (int)Math.Ceiling(sizeY / 16f);
            int blockSizeZ = (int)Math.Ceiling(sizeZ / 16f);

            // ALWAYS center voxels within their block grid on ALL 3 axes.
            // This ensures rotation doesn't shift the model within blocks.
            // Alignment (Center/NE/NW/etc.) only affects BlockCorrection (block-level offset).
            int correctX = (blockSizeX * 16 - sizeX) / 2;
            int correctY = (blockSizeY * 16 - sizeY) / 2;
            int correctZ = (blockSizeZ * 16 - sizeZ) / 2;

            // BlockCorrection: how many blocks to shift for alignment relative to anchor
            switch (Allign)
            {
                case ModelAllign.Southeast:
                    BlockCorrection = new Vec3i(0, 0, 0);
                    break;

                case ModelAllign.Northeast:
                    BlockCorrection = new Vec3i(blockSizeX, 0, 0);
                    break;

                case ModelAllign.Southwest:
                    BlockCorrection = new Vec3i(0, 0, blockSizeZ);
                    break;

                case ModelAllign.Northwest:
                    BlockCorrection = new Vec3i(blockSizeX, 0, blockSizeZ);
                    break;

                default: // Center
                    BlockCorrection = new Vec3i(blockSizeX / 2, 0, blockSizeZ / 2);
                    break;
            }

            if (correctX != 0 || correctY != 0 || correctZ != 0)
            {
                for (int i = 0; i < rawVoxels.Count; i++)
                {
                    var rv = rawVoxels[i];
                    rv.Position = new Vec3i(
                        rv.Position.X + correctX,
                        rv.Position.Y + correctY,
                        rv.Position.Z + correctZ
                    );
                    rawVoxels[i] = rv;
                }
            }
        }

        private void ApplyVoxelsOffset()
        {
            if (VoxelsOffset.X == 0 && VoxelsOffset.Y == 0 && VoxelsOffset.Z == 0)
                return;

            for (int i = 0; i < rawVoxels.Count; i++)
            {
                var rv = rawVoxels[i];
                rv.Position = new Vec3i(
                    rv.Position.X + VoxelsOffset.X,
                    rv.Position.Y + VoxelsOffset.Y,
                    rv.Position.Z + VoxelsOffset.Z
                );
                rawVoxels[i] = rv;
            }
        }

        private void ConvertToBlockData()
        {
            voxelBlocks = new Dictionary<BlockPos, VoxelBlockData>();

            if (rawVoxels == null || rawVoxels.Count == 0)
                return;

            foreach (var rv in rawVoxels)
            {
                var pos = rv.Position;

                // Handle negative coordinates correctly with floor division
                int blockX = pos.X >= 0 ? pos.X / 16 : (pos.X - 15) / 16;
                int blockY = pos.Y >= 0 ? pos.Y / 16 : (pos.Y - 15) / 16;
                int blockZ = pos.Z >= 0 ? pos.Z / 16 : (pos.Z - 15) / 16;

                int localX = pos.X - blockX * 16;
                int localY = pos.Y - blockY * 16;
                int localZ = pos.Z - blockZ * 16;

                var blockPos = new BlockPos(
                    blockX - BlockCorrection.X,
                    blockY - BlockCorrection.Y,
                    blockZ - BlockCorrection.Z,
                    0
                );

                if (!voxelBlocks.TryGetValue(blockPos, out var blockData))
                {
                    blockData = new VoxelBlockData();
                    voxelBlocks[blockPos] = blockData;
                }

                if (!blockData.Voxels[localX, localY, localZ])
                {
                    blockData.Voxels[localX, localY, localZ] = true;
                    blockData.MaterialIndex[localX, localY, localZ] = rv.ColorIndex;
                    blockData.VoxelCount++;
                }
            }
        }

        private void ApplyBlockOffset()
        {
            if (BlocksOffset.X == 0 && BlocksOffset.Y == 0 && BlocksOffset.Z == 0)
                return;

            var newDict = new Dictionary<BlockPos, VoxelBlockData>();
            foreach (var kvp in voxelBlocks)
            {
                var newPos = kvp.Key.Copy();
                newPos.Add(BlocksOffset.X, BlocksOffset.Y, BlocksOffset.Z);
                newDict[newPos] = kvp.Value;
            }
            voxelBlocks = newDict;
        }

        private void FindMinMaxPos()
        {
            if (voxelBlocks == null || voxelBlocks.Count == 0)
            {
                MinPos = new Vec3i(0, 0, 0);
                MaxPos = new Vec3i(0, 0, 0);
                return;
            }

            var first = voxelBlocks.Keys.First();
            int minX = first.X, minY = first.Y, minZ = first.Z;
            int maxX = first.X, maxY = first.Y, maxZ = first.Z;

            foreach (var key in voxelBlocks.Keys)
            {
                if (key.X < minX) minX = key.X;
                if (key.Y < minY) minY = key.Y;
                if (key.Z < minZ) minZ = key.Z;
                if (key.X > maxX) maxX = key.X;
                if (key.Y > maxY) maxY = key.Y;
                if (key.Z > maxZ) maxZ = key.Z;
            }

            MinPos = new Vec3i(minX, minY, minZ);
            MaxPos = new Vec3i(maxX, maxY, maxZ);
        }

        public void SetBlockVoxelsOffsetAndAllign(Vec3i offsetBlocks, Vec3i offsetVoxels, ModelAllign allign)
        {
            BlocksOffset = offsetBlocks;
            VoxelsOffset = offsetVoxels;
            Allign = allign;
            UpdateModel();
        }

        // --- Y axis rotation (horizontal, Left/Right arrows) ---

        /// <summary>
        /// These only set the angle. Call SetBlockVoxelsOffsetAndAllign() after to rebuild.
        /// </summary>
        public void RotateY_CW()  { rotationY = (rotationY + 90) % 360; }
        public void RotateY_CCW() { rotationY = (rotationY + 270) % 360; }
        public void RotateX_CW()  { rotationX = (rotationX + 90) % 360; }
        public void RotateX_CCW() { rotationX = (rotationX + 270) % 360; }
        public void RotateZ_CW()  { rotationZ = (rotationZ + 90) % 360; }
        public void RotateZ_CCW() { rotationZ = (rotationZ + 270) % 360; }

        public int GetRotationY() => rotationY;
        public int GetRotationX() => rotationX;
        public int GetRotationZ() => rotationZ;

        private void ApplyRotation()
        {
            if (rawVoxels == null || rawVoxels.Count == 0)
                return;
            if (rotationY == 0 && rotationX == 0 && rotationZ == 0)
                return;

            int stepsY = rotationY / 90;
            int stepsX = rotationX / 90;
            int stepsZ = rotationZ / 90;

            for (int i = 0; i < rawVoxels.Count; i++)
            {
                var rv = rawVoxels[i];
                int x = rv.Position.X;
                int y = rv.Position.Y;
                int z = rv.Position.Z;

                // Apply Y rotation first (horizontal)
                for (int s = 0; s < stepsY; s++)
                {
                    // 90° CW around Y (looking down): (x, z) -> (z, -x)
                    int tmp = x;
                    x = z;
                    z = -tmp;
                }

                // Then X rotation (vertical tilt forward/back)
                for (int s = 0; s < stepsX; s++)
                {
                    // 90° CW around X (looking from right): (y, z) -> (z, -y)
                    int tmp = y;
                    y = z;
                    z = -tmp;
                }

                // Then Z rotation (roll left/right around camera's forward axis)
                for (int s = 0; s < stepsZ; s++)
                {
                    // 90° CW around Z (looking along +Z): (x, y) -> (y, -x)
                    int tmp = x;
                    x = y;
                    y = -tmp;
                }

                rv.Position = new Vec3i(x, y, z);
                rawVoxels[i] = rv;
            }
        }

        private void UpdateModel()
        {
            // Re-load raw voxels fresh (they get modified in place by transforms)
            if (voxFile != null)
            {
                LoadRawVoxels();
            }
            else if (originalRawVoxels != null)
            {
                // Generated model — restore from saved copy
                rawVoxels = originalRawVoxels.Select(v =>
                    new RawVoxel(new Vec3i(v.Position.X, v.Position.Y, v.Position.Z), v.ColorIndex)).ToList();
            }
            UpdateModelFromRawVoxels();
        }

        private void UpdateModelFromRawVoxels()
        {
            ApplyRotation();
            ApplyAlignModel();
            ApplyVoxelsOffset();
            ConvertToBlockData();
            ApplyBlockOffset();
            FindMinMaxPos();
            OriginalBlockCount = voxelBlocks?.Count ?? 0;
            FillUnreachableInterior();
            ComputeUsedPaletteIndices();
        }

        /// <summary>
        /// Find distinct palette indices used by voxels, then merge duplicates by RGB
        /// (multiple palette slots with the same color → first occurrence wins).
        /// Rewrites voxelBlocks MaterialIndex in-place to use canonical indices.
        /// </summary>
        private void ComputeUsedPaletteIndices()
        {
            var used = new HashSet<byte>();
            if (rawVoxels != null)
                foreach (var rv in rawVoxels) used.Add(rv.ColorIndex);

            // Build RGB → canonical (first index with that RGB)
            var rgbToCanonical = new Dictionary<int, byte>();
            var remap = new Dictionary<byte, byte>();
            var sorted = new List<byte>(used);
            sorted.Sort();

            foreach (byte idx in sorted)
            {
                if (Palette == null || idx >= Palette.Length || Palette[idx] == null)
                {
                    remap[idx] = idx;
                    continue;
                }
                var c = Palette[idx];
                int key = (c[0] << 16) | (c[1] << 8) | c[2];
                if (!rgbToCanonical.TryGetValue(key, out byte canonical))
                {
                    rgbToCanonical[key] = idx;
                    remap[idx] = idx;
                }
                else
                {
                    remap[idx] = canonical;
                }
            }

            // Rewrite voxelBlocks MaterialIndex to canonical form
            if (voxelBlocks != null)
            {
                foreach (var bd in voxelBlocks.Values)
                {
                    for (int x = 0; x < 16; x++)
                        for (int y = 0; y < 16; y++)
                            for (int z = 0; z < 16; z++)
                                if (bd.Voxels[x, y, z])
                                {
                                    byte orig = bd.MaterialIndex[x, y, z];
                                    if (remap.TryGetValue(orig, out byte canon) && canon != orig)
                                        bd.MaterialIndex[x, y, z] = canon;
                                }
                }
            }

            // Final canonical list: sorted unique values from remap
            var canonSet = new HashSet<byte>(remap.Values);
            var canonList = new List<byte>(canonSet);
            canonList.Sort();
            UsedPaletteIndices = canonList;
        }

        /// <summary>
        /// Flood-fill from the exterior of the model's bounding box to find all air voxels
        /// reachable from outside. Any air voxel NOT reachable is an interior cavity —
        /// fill it as solid to avoid wasting chisel operations on invisible geometry.
        /// Also adds fully-interior empty blocks as full solid blocks.
        /// </summary>
        private void FillUnreachableInterior()
        {
            InteriorVoxelsFilled = 0;
            if (voxelBlocks == null || voxelBlocks.Count == 0) return;

            int spanX = MaxPos.X - MinPos.X + 1;
            int spanY = MaxPos.Y - MinPos.Y + 1;
            int spanZ = MaxPos.Z - MinPos.Z + 1;

            int gx = spanX * 16 + 2; // +2 for 1-voxel air padding on each side
            int gy = spanY * 16 + 2;
            int gz = spanZ * 16 + 2;

            // Safety: skip optimization for very large models (>20M voxels)
            long totalCells = (long)gx * gy * gz;
            if (totalCells > 20_000_000) return;

            // Build global voxel grid: true = solid (model voxel)
            bool[,,] solid = new bool[gx, gy, gz];

            foreach (var kvp in voxelBlocks)
            {
                var bp = kvp.Key;
                var bd = kvp.Value;
                int baseX = (bp.X - MinPos.X) * 16 + 1;
                int baseY = (bp.Y - MinPos.Y) * 16 + 1;
                int baseZ = (bp.Z - MinPos.Z) * 16 + 1;

                for (int x = 0; x < 16; x++)
                    for (int y = 0; y < 16; y++)
                        for (int z = 0; z < 16; z++)
                            if (bd.Voxels[x, y, z])
                                solid[baseX + x, baseY + y, baseZ + z] = true;
            }

            // Flood-fill from corner (0,0,0) — padding guarantees it's air and connected to exterior
            bool[,,] reachable = new bool[gx, gy, gz];
            var queue = new Queue<(int, int, int)>();
            queue.Enqueue((0, 0, 0));
            reachable[0, 0, 0] = true;

            int[] dx = { 1, -1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, 1, -1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                var (cx, cy, cz) = queue.Dequeue();
                for (int d = 0; d < 6; d++)
                {
                    int nx = cx + dx[d], ny = cy + dy[d], nz = cz + dz[d];
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= gx || ny >= gy || nz >= gz) continue;
                    if (reachable[nx, ny, nz] || solid[nx, ny, nz]) continue;
                    reachable[nx, ny, nz] = true;
                    queue.Enqueue((nx, ny, nz));
                }
            }

            // Fill unreachable air in existing model blocks
            foreach (var kvp in voxelBlocks)
            {
                var bp = kvp.Key;
                var bd = kvp.Value;
                int baseX = (bp.X - MinPos.X) * 16 + 1;
                int baseY = (bp.Y - MinPos.Y) * 16 + 1;
                int baseZ = (bp.Z - MinPos.Z) * 16 + 1;

                for (int x = 0; x < 16; x++)
                    for (int y = 0; y < 16; y++)
                        for (int z = 0; z < 16; z++)
                        {
                            if (!bd.Voxels[x, y, z] && !reachable[baseX + x, baseY + y, baseZ + z])
                            {
                                bd.Voxels[x, y, z] = true;
                                bd.VoxelCount++;
                                InteriorVoxelsFilled++;
                            }
                        }
            }

            // Check for entirely interior empty blocks (not in voxelBlocks but fully unreachable)
            for (int bx = MinPos.X; bx <= MaxPos.X; bx++)
                for (int by = MinPos.Y; by <= MaxPos.Y; by++)
                    for (int bz = MinPos.Z; bz <= MaxPos.Z; bz++)
                    {
                        var bp = new BlockPos(bx, by, bz, 0);
                        if (voxelBlocks.ContainsKey(bp)) continue;

                        int baseX = (bx - MinPos.X) * 16 + 1;
                        int baseY = (by - MinPos.Y) * 16 + 1;
                        int baseZ = (bz - MinPos.Z) * 16 + 1;

                        // Check if ANY voxel in this block is reachable from outside
                        bool anyReachable = false;
                        for (int x = 0; x < 16 && !anyReachable; x++)
                            for (int y = 0; y < 16 && !anyReachable; y++)
                                for (int z = 0; z < 16 && !anyReachable; z++)
                                    if (reachable[baseX + x, baseY + y, baseZ + z])
                                        anyReachable = true;

                        if (!anyReachable)
                        {
                            // Entire block is interior — add as full solid block
                            var bd = new VoxelBlockData();
                            for (int x = 0; x < 16; x++)
                                for (int y = 0; y < 16; y++)
                                    for (int z = 0; z < 16; z++)
                                        bd.Voxels[x, y, z] = true;
                            bd.VoxelCount = 4096;
                            voxelBlocks[bp] = bd;
                            InteriorVoxelsFilled += 4096;
                        }
                    }
        }

        public Dictionary<BlockPos, VoxelBlockData> GetVoxelBlocks()
        {
            // Return a deep clone
            var clone = new Dictionary<BlockPos, VoxelBlockData>();
            foreach (var kvp in voxelBlocks)
            {
                clone[kvp.Key] = kvp.Value.Clone();
            }
            return clone;
        }

        public List<BlockPos> GetDimensionsBlocksList()
        {
            if (voxelBlocks == null || voxelBlocks.Count == 0)
                return new List<BlockPos>();

            var list = new List<BlockPos>();
            for (int x = MinPos.X; x <= MaxPos.X; x++)
            {
                for (int y = MinPos.Y; y <= MaxPos.Y; y++)
                {
                    for (int z = MinPos.Z; z <= MaxPos.Z; z++)
                    {
                        list.Add(new BlockPos(x, y, z, 0));
                    }
                }
            }
            return list;
        }

        public List<BlockPos> GetModelHighlightList()
        {
            return new List<BlockPos>(voxelBlocks.Keys);
        }

        public List<BlockPos> GetDimensionsHighlightList()
        {
            if (MinPos == null || MaxPos == null)
                return new List<BlockPos>();

            var list = new List<BlockPos>();
            list.Add(new BlockPos(MinPos, 0));
            list.Add(new BlockPos(MaxPos.Clone().Add(1, 1, 1), 0));
            return list;
        }

        public string GetFileName()
        {
            return voxFileName;
        }

        public Vec3i GetBlocksOffset() => BlocksOffset;
        public Vec3i GetVoxelsOffset() => VoxelsOffset;
        public ModelAllign GetAlignment() => Allign;

        public int GetTotalVoxelCount()
        {
            if (voxelBlocks == null) return 0;
            int count = 0;
            foreach (var bd in voxelBlocks.Values)
                count += bd.VoxelCount;
            return count;
        }

        public int GetBlockCount()
        {
            return voxelBlocks?.Count ?? 0;
        }

        /// <summary>
        /// Returns model dimensions in voxels: (width, height, depth).
        /// Computed from raw voxel bounds before block conversion.
        /// </summary>
        public Vec3i GetModelDimensions()
        {
            if (rawVoxels == null || rawVoxels.Count == 0)
                return new Vec3i(0, 0, 0);

            // Collect raw voxel positions — from voxFile or originalRawVoxels (for generated)
            var tempVoxels = new List<RawVoxel>();
            if (voxFile != null)
            {
                foreach (var model in voxFile.Models)
                    foreach (var v in model.Voxels)
                        tempVoxels.Add(new RawVoxel(new Vec3i(v.X, v.Z, -v.Y), v.ColorIndex));
            }
            else if (originalRawVoxels != null)
            {
                // Generated model — use saved copy of raw voxels
                foreach (var v in originalRawVoxels)
                    tempVoxels.Add(new RawVoxel(new Vec3i(v.Position.X, v.Position.Y, v.Position.Z), v.ColorIndex));
            }

            if (tempVoxels.Count == 0) return new Vec3i(0, 0, 0);

            int minX = tempVoxels[0].Position.X, maxX = minX;
            int minY = tempVoxels[0].Position.Y, maxY = minY;
            int minZ = tempVoxels[0].Position.Z, maxZ = minZ;

            for (int i = 1; i < tempVoxels.Count; i++)
            {
                var p = tempVoxels[i].Position;
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }

            return new Vec3i(maxX - minX + 1, maxY - minY + 1, maxZ - minZ + 1);
        }

        /// <summary>
        /// Returns the number of blocks in the model's bounding box.
        /// </summary>
        public int GetBoundingBoxBlockCount()
        {
            if (voxelBlocks == null || voxelBlocks.Count == 0) return 0;
            int spanX = MaxPos.X - MinPos.X + 1;
            int spanY = MaxPos.Y - MinPos.Y + 1;
            int spanZ = MaxPos.Z - MinPos.Z + 1;
            return spanX * spanY * spanZ;
        }
    }
}
