using System;
using System.Collections.Generic;

namespace AutomaticChiselling
{
    /// <summary>
    /// Settings that drive <see cref="Voxelizer.Voxelize"/>.
    /// </summary>
    public class VoxelizeSettings
    {
        /// <summary>Max voxels along the longest bbox axis. Other axes scale proportionally.</summary>
        public int Resolution = 64;

        /// <summary>If true, flood-fill the interior of the shell. If the mesh isn't watertight, fill may leak.</summary>
        public bool FillInterior = true;

        /// <summary>If true, flip the Y axis of input vertices. Useful for OBJ exports where +Y is down.</summary>
        public bool FlipY = false;

        /// <summary>If true, swap Y and Z of input vertices. Many OBJ exports use Y-up while our target is Z-up-like.</summary>
        public bool SwapYZ = false;

        /// <summary>How many samples per unit voxel edge when rasterizing a triangle's surface.</summary>
        public int SurfaceSamplesPerVoxel = 3;
    }

    /// <summary>
    /// Voxelizes a triangle mesh into a GeneratedShape (colored voxel grid + palette).
    /// Pipeline: bbox → per-triangle surface sampling → optional flood-fill interior →
    /// per-material palette entry. No texture sampling (v1 uses material Kd only).
    /// </summary>
    public static class Voxelizer
    {
        public static GeneratedShape Voxelize(ObjMesh mesh, VoxelizeSettings settings,
            out string status)
        {
            status = null;
            if (mesh == null || mesh.Triangles.Count == 0 || mesh.Positions.Count == 0)
            {
                status = "Empty mesh.";
                return null;
            }

            // --- 1. Transform vertices (axis flips/swaps) into working coord system ---
            var verts = new ObjVec3[mesh.Positions.Count];
            for (int i = 0; i < verts.Length; i++)
            {
                var p = mesh.Positions[i];
                if (settings.FlipY) p.Y = -p.Y;
                if (settings.SwapYZ)
                {
                    float t = p.Y; p.Y = p.Z; p.Z = t;
                }
                verts[i] = p;
            }

            // --- 2. Bounding box of the mesh ---
            float minX = verts[0].X, maxX = minX;
            float minY = verts[0].Y, maxY = minY;
            float minZ = verts[0].Z, maxZ = minZ;
            for (int i = 1; i < verts.Length; i++)
            {
                var v = verts[i];
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            }

            float extentX = maxX - minX, extentY = maxY - minY, extentZ = maxZ - minZ;
            float maxExtent = Math.Max(extentX, Math.Max(extentY, extentZ));
            if (maxExtent <= 0f)
            {
                status = "Mesh bbox is degenerate (all vertices coincident).";
                return null;
            }

            // --- 3. Grid sizing: longest axis gets `Resolution` voxels, others scale ---
            int res = Math.Max(4, Math.Min(512, settings.Resolution));
            float voxelSize = maxExtent / res;
            int sizeX = Math.Max(1, (int)Math.Ceiling(extentX / voxelSize));
            int sizeY = Math.Max(1, (int)Math.Ceiling(extentY / voxelSize));
            int sizeZ = Math.Max(1, (int)Math.Ceiling(extentZ / voxelSize));

            // Safety cap — prevents accidental 500×500×500 = 125M voxel grids
            long totalCells = (long)sizeX * sizeY * sizeZ;
            if (totalCells > 20_000_000)
            {
                status = $"Grid too large ({sizeX}×{sizeY}×{sizeZ}). Lower the resolution.";
                return null;
            }

            var shape = new GeneratedShape
            {
                Voxels = new byte[sizeX, sizeY, sizeZ],
                Palette = new byte[256][]
            };
            // Seed the palette with gray fallback for unassigned entries
            for (int i = 0; i < 256; i++)
                shape.Palette[i] = new byte[] { 180, 180, 180, 255 };

            // --- 4. Build palette from materials, map material → palette index ---
            // Index 0 = empty. Index 1..N = distinct materials. Index 255 reserved for
            // "no material" triangles (default gray).
            byte defaultIdx = 255;
            shape.Palette[defaultIdx] = new byte[] { 200, 200, 200, 255 };

            byte nextIdx = 1;
            byte[] matToPalette = new byte[mesh.Materials.Count];
            for (int i = 0; i < mesh.Materials.Count && nextIdx < defaultIdx; i++)
            {
                var m = mesh.Materials[i];
                matToPalette[i] = nextIdx;
                shape.Palette[nextIdx] = new byte[]
                {
                    (byte)Math.Clamp((int)(m.KdR * 255f), 0, 255),
                    (byte)Math.Clamp((int)(m.KdG * 255f), 0, 255),
                    (byte)Math.Clamp((int)(m.KdB * 255f), 0, 255),
                    255
                };
                nextIdx++;
            }

            // --- 5. Surface rasterization: sample each triangle densely, mark voxels ---
            int shellCount = 0;
            foreach (var tri in mesh.Triangles)
            {
                var a = verts[tri.P0];
                var b = verts[tri.P1];
                var c = verts[tri.P2];

                // Estimate how many samples we need along each edge so the sample
                // spacing ≤ voxelSize / SurfaceSamplesPerVoxel. This guarantees that
                // every voxel crossed by the triangle gets a sample point inside it.
                float ab = Distance(a, b);
                float ac = Distance(a, c);
                float bc = Distance(b, c);
                float maxEdge = Math.Max(ab, Math.Max(ac, bc));
                int steps = Math.Max(2, (int)Math.Ceiling(maxEdge / voxelSize * settings.SurfaceSamplesPerVoxel));

                byte colorIdx = tri.MaterialIdx >= 0 && tri.MaterialIdx < matToPalette.Length
                    ? matToPalette[tri.MaterialIdx]
                    : defaultIdx;

                // Barycentric sweep: u + v ≤ 1, u,v ≥ 0.
                for (int u = 0; u <= steps; u++)
                {
                    float fu = (float)u / steps;
                    for (int v = 0; v <= steps - u; v++)
                    {
                        float fv = (float)v / steps;
                        float fw = 1f - fu - fv;

                        float px = a.X * fw + b.X * fu + c.X * fv;
                        float py = a.Y * fw + b.Y * fu + c.Y * fv;
                        float pz = a.Z * fw + b.Z * fu + c.Z * fv;

                        int ix = (int)((px - minX) / voxelSize);
                        int iy = (int)((py - minY) / voxelSize);
                        int iz = (int)((pz - minZ) / voxelSize);
                        if (ix < 0) ix = 0; else if (ix >= sizeX) ix = sizeX - 1;
                        if (iy < 0) iy = 0; else if (iy >= sizeY) iy = sizeY - 1;
                        if (iz < 0) iz = 0; else if (iz >= sizeZ) iz = sizeZ - 1;

                        if (shape.Voxels[ix, iy, iz] == 0)
                        {
                            shape.Voxels[ix, iy, iz] = colorIdx;
                            shellCount++;
                        }
                    }
                }
            }

            if (shellCount == 0)
            {
                status = "No voxels were marked (grid too coarse?). Increase resolution.";
                return null;
            }

            // --- 6. Optional interior flood fill ---
            if (settings.FillInterior)
            {
                FillInterior(shape, sizeX, sizeY, sizeZ, defaultIdx);
            }

            int total = 0;
            for (int x = 0; x < sizeX; x++)
                for (int y = 0; y < sizeY; y++)
                    for (int z = 0; z < sizeZ; z++)
                        if (shape.Voxels[x, y, z] != 0) total++;

            status = $"Voxelized: {sizeX}×{sizeY}×{sizeZ}, {total:N0} solid voxels, {nextIdx - 1} materials.";
            return shape;
        }

        /// <summary>
        /// Flood-fills "outside" from the 6 faces of the grid, then marks every
        /// unvisited empty cell as interior (painted with <paramref name="interiorColor"/>).
        /// If the shell has holes, outside leaks in — the result is then just the shell,
        /// which is fine for non-watertight meshes.
        /// </summary>
        private static void FillInterior(GeneratedShape shape, int sx, int sy, int sz, byte interiorColor)
        {
            // outside[x,y,z] = true if reachable from any border empty cell.
            var outside = new bool[sx, sy, sz];
            var stack = new Stack<(int x, int y, int z)>();

            void Seed(int x, int y, int z)
            {
                if (x < 0 || x >= sx || y < 0 || y >= sy || z < 0 || z >= sz) return;
                if (shape.Voxels[x, y, z] != 0) return;
                if (outside[x, y, z]) return;
                outside[x, y, z] = true;
                stack.Push((x, y, z));
            }

            for (int x = 0; x < sx; x++)
                for (int y = 0; y < sy; y++) { Seed(x, y, 0); Seed(x, y, sz - 1); }
            for (int x = 0; x < sx; x++)
                for (int z = 0; z < sz; z++) { Seed(x, 0, z); Seed(x, sy - 1, z); }
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++) { Seed(0, y, z); Seed(sx - 1, y, z); }

            while (stack.Count > 0)
            {
                var (x, y, z) = stack.Pop();
                Seed(x + 1, y, z); Seed(x - 1, y, z);
                Seed(x, y + 1, z); Seed(x, y - 1, z);
                Seed(x, y, z + 1); Seed(x, y, z - 1);
            }

            for (int x = 0; x < sx; x++)
                for (int y = 0; y < sy; y++)
                    for (int z = 0; z < sz; z++)
                        if (shape.Voxels[x, y, z] == 0 && !outside[x, y, z])
                            shape.Voxels[x, y, z] = interiorColor;
        }

        private static float Distance(ObjVec3 a, ObjVec3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
