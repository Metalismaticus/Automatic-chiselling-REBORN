using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

namespace AutomaticChiselling
{
    /// <summary>
    /// Generates a 2D front-view projection of a .vox model for GUI preview.
    /// Uses depth shading to show the 3D shape as a recognizable silhouette.
    /// </summary>
    public static class ModelPreview
    {
        /// <summary>
        /// Generates a Cairo ImageSurface with a front-view projection of the model.
        /// Each pixel represents a column of voxels; color = depth-shaded model color.
        /// Returns a LoadedTexture ready for GUI rendering.
        /// </summary>
        public static LoadedTexture CreatePreviewTexture(ICoreClientAPI capi, VoxelsStorage storage, int texSize = 100)
        {
            try
            {
                var voxelBlocks = storage.GetVoxelBlocks();
                if (voxelBlocks == null || voxelBlocks.Count == 0) return null;

                // Collect all voxels from block data (works for both .vox and generated)
                var allVoxels = new List<(int x, int y, int z, byte colorIdx)>();
                foreach (var kvp in voxelBlocks)
                {
                    var bp = kvp.Key;
                    var bd = kvp.Value;
                    for (int x = 0; x < 16; x++)
                        for (int y = 0; y < 16; y++)
                            for (int z = 0; z < 16; z++)
                                if (bd.Voxels[x, y, z])
                                    allVoxels.Add((bp.X * 16 + x, bp.Y * 16 + y, bp.Z * 16 + z,
                                        bd.MaterialIndex[x, y, z]));
                }

                if (allVoxels.Count == 0) return null;

                // Find bounds
                int minX = int.MaxValue, maxX = int.MinValue;
                int minY = int.MaxValue, maxY = int.MinValue;
                int minZ = int.MaxValue, maxZ = int.MinValue;

                foreach (var (x, y, z, _) in allVoxels)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }

                int sizeX = maxX - minX + 1;
                int sizeY = maxY - minY + 1;
                int sizeZ = maxZ - minZ + 1;

                // Front projection: looking along Z axis → X is horizontal, Y is vertical
                // For each (x, y) find the frontmost voxel (min z) and its color
                var depthMap = new int[sizeX, sizeY];
                var colorMap = new byte[sizeX, sizeY];
                var hasVoxel = new bool[sizeX, sizeY];

                // Init depth to max
                for (int x = 0; x < sizeX; x++)
                    for (int y = 0; y < sizeY; y++)
                        depthMap[x, y] = sizeZ;

                foreach (var (vx, vy, vz, ci) in allVoxels)
                {
                    int lx = vx - minX;
                    int ly = vy - minY;
                    int lz = vz - minZ;

                    if (lz < depthMap[lx, ly])
                    {
                        depthMap[lx, ly] = lz;
                        colorMap[lx, ly] = ci;
                        hasVoxel[lx, ly] = true;
                    }
                }

                // Get palette
                var palette = storage.Palette;

                // Create Cairo surface
                var surface = new ImageSurface(Format.ARGB32, texSize, texSize);
                var ctx = new Context(surface);

                // Background (transparent)
                ctx.SetSourceRGBA(0, 0, 0, 0);
                ctx.Paint();

                // Scale to fit
                float scaleX = (float)texSize / sizeX;
                float scaleY = (float)texSize / sizeY;
                float scale = Math.Min(scaleX, scaleY) * 0.9f;
                float offsetX = (texSize - sizeX * scale) / 2f;
                float offsetY = (texSize - sizeY * scale) / 2f;

                // Draw voxels (Y is flipped: model Y=0 is bottom, screen Y=0 is top)
                for (int x = 0; x < sizeX; x++)
                {
                    for (int y = 0; y < sizeY; y++)
                    {
                        if (!hasVoxel[x, y]) continue;

                        int depth = depthMap[x, y];
                        byte ci = colorMap[x, y];

                        // Get color from palette
                        double r = 0.6, g = 0.6, b = 0.6;
                        if (palette != null && ci < palette.Length && palette[ci] != null)
                        {
                            r = palette[ci][0] / 255.0;
                            g = palette[ci][1] / 255.0;
                            b = palette[ci][2] / 255.0;
                        }

                        // Depth shading: closer = brighter, further = darker
                        double depthFactor = 1.0 - (double)depth / sizeZ * 0.5;
                        r *= depthFactor;
                        g *= depthFactor;
                        b *= depthFactor;

                        // Simple ambient occlusion: darken edges
                        bool hasLeft = x > 0 && hasVoxel[x - 1, y];
                        bool hasRight = x < sizeX - 1 && hasVoxel[x + 1, y];
                        bool hasUp = y > 0 && hasVoxel[x, y - 1];
                        bool hasDown = y < sizeY - 1 && hasVoxel[x, y + 1];
                        int neighbors = (hasLeft ? 1 : 0) + (hasRight ? 1 : 0) + (hasUp ? 1 : 0) + (hasDown ? 1 : 0);
                        if (neighbors < 4)
                        {
                            double edgeDarken = 0.85 + 0.15 * (neighbors / 4.0);
                            r *= edgeDarken;
                            g *= edgeDarken;
                            b *= edgeDarken;
                        }

                        ctx.SetSourceRGBA(r, g, b, 1.0);
                        double px = offsetX + x * scale;
                        double py = offsetY + (sizeY - 1 - y) * scale; // flip Y
                        ctx.Rectangle(px, py, Math.Max(scale, 1), Math.Max(scale, 1));
                        ctx.Fill();
                    }
                }

                // Subtle border
                ctx.SetSourceRGBA(0.4, 0.4, 0.45, 0.3);
                ctx.Rectangle(0, 0, texSize, texSize);
                ctx.LineWidth = 1;
                ctx.Stroke();

                ctx.Dispose();

                // Convert to texture
                var tex = new LoadedTexture(capi);
                capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref tex);
                surface.Dispose();

                return tex;
            }
            catch
            {
                return null;
            }
        }

        // Preview now uses GetVoxelBlocks() directly — no reflection needed
    }
}
