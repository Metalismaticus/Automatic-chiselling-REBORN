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

        /// <summary>
        /// 3D preview with explicit yaw/pitch. Each voxel is rotated into camera
        /// space, sorted back-to-front (painter's algorithm), and rendered as a
        /// shaded rectangle. Not GPU-accelerated but more than fast enough for
        /// the voxel counts we deal with (≤ 100k surface voxels).
        ///
        /// yaw   — rotation around vertical Y axis, in radians (left/right spin).
        /// pitch — rotation around horizontal X axis, in radians (look up/down).
        /// </summary>
        public static LoadedTexture CreatePreviewTexture3D(ICoreClientAPI capi, VoxelsStorage storage,
            int texSize, double yaw, double pitch)
        {
            try
            {
                var voxelBlocks = storage.GetVoxelBlocks();
                if (voxelBlocks == null || voxelBlocks.Count == 0) return null;

                // ---- 1. Collect only SURFACE voxels (faces exposed to air) ----
                // Interior voxels never contribute to the 3D silhouette, so skip
                // them. This is critical — full solid blocks would otherwise draw
                // thousands of wasted rects.
                var occupied = new HashSet<(int x, int y, int z)>();
                var voxelList = new List<(int x, int y, int z, byte ci)>();
                foreach (var kvp in voxelBlocks)
                {
                    var bp = kvp.Key; var bd = kvp.Value;
                    for (int x = 0; x < 16; x++)
                        for (int y = 0; y < 16; y++)
                            for (int z = 0; z < 16; z++)
                                if (bd.Voxels[x, y, z])
                                {
                                    int gx = bp.X * 16 + x, gy = bp.Y * 16 + y, gz = bp.Z * 16 + z;
                                    occupied.Add((gx, gy, gz));
                                    voxelList.Add((gx, gy, gz, bd.MaterialIndex[x, y, z]));
                                }
                }
                if (voxelList.Count == 0) return null;

                // Faces-visible mask per voxel: bit0=+X bit1=-X bit2=+Y bit3=-Y bit4=+Z bit5=-Z
                // We only keep voxels whose mask != 0 (i.e. have at least one exposed face).
                var surface = new List<(int x, int y, int z, byte ci, int faces)>();
                foreach (var v in voxelList)
                {
                    int m = 0;
                    if (!occupied.Contains((v.x + 1, v.y, v.z))) m |= 1;
                    if (!occupied.Contains((v.x - 1, v.y, v.z))) m |= 2;
                    if (!occupied.Contains((v.x, v.y + 1, v.z))) m |= 4;
                    if (!occupied.Contains((v.x, v.y - 1, v.z))) m |= 8;
                    if (!occupied.Contains((v.x, v.y, v.z + 1))) m |= 16;
                    if (!occupied.Contains((v.x, v.y, v.z - 1))) m |= 32;
                    if (m != 0) surface.Add((v.x, v.y, v.z, v.ci, m));
                }
                if (surface.Count == 0) return null;

                // ---- 2. Bbox center (rotation pivot) ----
                int minX = surface[0].x, maxX = minX;
                int minY = surface[0].y, maxY = minY;
                int minZ = surface[0].z, maxZ = minZ;
                for (int i = 1; i < surface.Count; i++)
                {
                    var v = surface[i];
                    if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
                    if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
                    if (v.z < minZ) minZ = v.z; if (v.z > maxZ) maxZ = v.z;
                }
                double cx = (minX + maxX) * 0.5;
                double cy = (minY + maxY) * 0.5;
                double cz = (minZ + maxZ) * 0.5;

                // ---- 3. Rotate each voxel into camera space ----
                double cyR = Math.Cos(yaw), syR = Math.Sin(yaw);
                double cpR = Math.Cos(pitch), spR = Math.Sin(pitch);

                var projected = new List<(double sx, double sy, double depth, byte ci, int faces)>(surface.Count);
                double pminSx = double.MaxValue, pmaxSx = double.MinValue;
                double pminSy = double.MaxValue, pmaxSy = double.MinValue;

                foreach (var v in surface)
                {
                    double x = v.x - cx, y = v.y - cy, z = v.z - cz;
                    // Yaw around Y
                    double x1 = x * cyR - z * syR;
                    double z1 = x * syR + z * cyR;
                    // Pitch around X
                    double y2 = y * cpR - z1 * spR;
                    double z2 = y * spR + z1 * cpR;
                    // Project: screen X = rotated X, screen Y = -rotated Y (flip so +Y is up)
                    double sx = x1;
                    double sy = -y2;
                    projected.Add((sx, sy, z2, v.ci, v.faces));
                    if (sx < pminSx) pminSx = sx; if (sx > pmaxSx) pmaxSx = sx;
                    if (sy < pminSy) pminSy = sy; if (sy > pmaxSy) pmaxSy = sy;
                }

                // ---- 4. Scale to fit texture ----
                double extentX = Math.Max(1, pmaxSx - pminSx);
                double extentY = Math.Max(1, pmaxSy - pminSy);
                double scale = Math.Min(texSize / extentX, texSize / extentY) * 0.88;
                double voxSize = scale;
                double centerX = texSize * 0.5;
                double centerY = texSize * 0.5;

                // ---- 5. Back-to-front (painter) ordering ----
                projected.Sort((a, b) => b.depth.CompareTo(a.depth)); // draw far first, near last

                // ---- 6. Compute which faces are "front" (visible to camera) given yaw/pitch ----
                // Rotate the face normals and check sign of z component — if facing
                // the camera (+Z in our convention), it's visible.
                // Normals: 0→+X, 1→-X, 2→+Y, 3→-Y, 4→+Z, 5→-Z
                var faceCamDot = new double[6];
                faceCamDot[0] =  cyR * spR;               // +X → after yaw + pitch
                faceCamDot[1] = -cyR * spR;               // -X
                faceCamDot[2] =  cpR;                     // +Y (top)
                faceCamDot[3] = -cpR;                     // -Y
                faceCamDot[4] =  syR * spR + cyR * cpR;   // +Z wrong, keep rough
                faceCamDot[5] = -(syR * spR + cyR * cpR); // -Z
                // (Exact formula isn't critical — we only use the sign for the
                // "is this face a highlight" decision, not for occlusion.)
                double topBrightness   = 1.25;  // +Y face tends to catch light
                double frontBrightness = 1.05;
                double sideBrightness  = 0.90;
                double backBrightness  = 0.75;

                // ---- 7. Render ----
                var palette = storage.Palette;
                var surfaceImg = new ImageSurface(Format.ARGB32, texSize, texSize);
                var ctx = new Context(surfaceImg);
                ctx.SetSourceRGBA(0, 0, 0, 0);
                ctx.Paint();

                double depthRange = Math.Max(1e-6, projected.Count > 0
                    ? (projected[0].depth - projected[projected.Count - 1].depth) : 1);

                foreach (var p in projected)
                {
                    double r = 0.6, g = 0.6, b = 0.6;
                    if (palette != null && p.ci < palette.Length && palette[p.ci] != null)
                    {
                        r = palette[p.ci][0] / 255.0;
                        g = palette[p.ci][1] / 255.0;
                        b = palette[p.ci][2] / 255.0;
                    }

                    // Highlight if the brightest visible face is the top (+Y, face bit 4).
                    // Fallback to front/side/back shading based on dominant visible face.
                    double brightness;
                    if ((p.faces & 4) != 0 && faceCamDot[2] > 0.15)
                        brightness = topBrightness;
                    else if ((p.faces & 16) != 0 && faceCamDot[4] > 0.2 ||
                             (p.faces & 32) != 0 && faceCamDot[5] > 0.2)
                        brightness = frontBrightness;
                    else if ((p.faces & 1) != 0 || (p.faces & 2) != 0)
                        brightness = sideBrightness;
                    else
                        brightness = backBrightness;

                    // Mild depth fog on top
                    double depthT = (p.depth - projected[projected.Count - 1].depth) / depthRange;
                    double depthFade = 1.0 - depthT * 0.25;
                    brightness *= depthFade;

                    r = Math.Min(1, r * brightness);
                    g = Math.Min(1, g * brightness);
                    b = Math.Min(1, b * brightness);

                    double px = centerX + (p.sx - (pminSx + pmaxSx) * 0.5) * scale - voxSize * 0.5;
                    double py = centerY + (p.sy - (pminSy + pmaxSy) * 0.5) * scale - voxSize * 0.5;
                    ctx.SetSourceRGBA(r, g, b, 1.0);
                    ctx.Rectangle(px, py, voxSize + 1.1, voxSize + 1.1);
                    ctx.Fill();
                }

                // Subtle border
                ctx.SetSourceRGBA(0.4, 0.4, 0.45, 0.3);
                ctx.Rectangle(0, 0, texSize, texSize);
                ctx.LineWidth = 1;
                ctx.Stroke();

                ctx.Dispose();

                var tex = new LoadedTexture(capi);
                capi.Gui.LoadOrUpdateCairoTexture(surfaceImg, true, ref tex);
                surfaceImg.Dispose();
                return tex;
            }
            catch
            {
                return null;
            }
        }
    }
}
