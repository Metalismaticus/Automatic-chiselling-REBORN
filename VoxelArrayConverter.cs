using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace AutomaticChiselling
{
    /// <summary>
    /// Converts voxel arrays (from generators) into a VoxelsStorage that the
    /// chisel conveyor, hologram renderer, etc. can use.
    /// </summary>
    public static class VoxelArrayConverter
    {
        /// <summary>
        /// Legacy helper — mono-color bool array.
        /// </summary>
        public static VoxelsStorage FromArray(bool[,,] voxels, string name = "generated",
            string generatorName = null, Dictionary<string, object> generatorParams = null)
        {
            if (voxels == null) return null;
            return FromShape(GeneratedShape.Mono(voxels), name, generatorName, generatorParams);
        }

        /// <summary>
        /// Creates a VoxelsStorage from a colored shape. Palette is propagated so
        /// the model is treated as multi-color end-to-end (browser, hologram, mapping).
        /// </summary>
        public static VoxelsStorage FromShape(GeneratedShape shape, string name = "generated",
            string generatorName = null, Dictionary<string, object> generatorParams = null)
        {
            if (shape == null || shape.Voxels == null) return null;

            int sizeX = shape.Voxels.GetLength(0);
            int sizeY = shape.Voxels.GetLength(1);
            int sizeZ = shape.Voxels.GetLength(2);
            if (sizeX == 0 || sizeY == 0 || sizeZ == 0) return null;

            var rawVoxels = new List<RawVoxel>();
            for (int x = 0; x < sizeX; x++)
                for (int y = 0; y < sizeY; y++)
                    for (int z = 0; z < sizeZ; z++)
                    {
                        byte idx = shape.Voxels[x, y, z];
                        if (idx == 0) continue;
                        rawVoxels.Add(new RawVoxel(new Vec3i(x, y, z), idx));
                    }
            if (rawVoxels.Count == 0) return null;

            // Build a full 256-slot palette — missing slots fall back to gray
            var fullPalette = new byte[256][];
            for (int i = 0; i < 256; i++)
                fullPalette[i] = new byte[] { 180, 180, 180, 255 };
            if (shape.Palette != null)
            {
                for (int i = 0; i < 256 && i < shape.Palette.Length; i++)
                    if (shape.Palette[i] != null)
                        fullPalette[i] = shape.Palette[i];
            }

            return VoxelsStorage.FromRawVoxels(rawVoxels, name, generatorName, generatorParams, fullPalette);
        }
    }
}
