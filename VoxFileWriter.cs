using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vintagestory.API.MathTools;

namespace AutomaticChiselling
{
    /// <summary>
    /// Writes a GeneratedShape to a MagicaVoxel .vox file (version 150).
    /// Chunk layout: MAIN { SIZE, XYZI, RGBA }.
    /// Model axis convention matches VoxFile reader: the .vox stores (X, Y=-Z, Z=Y)
    /// so round-tripping through the reader yields the same geometry.
    /// </summary>
    public static class VoxFileWriter
    {
        private const int VERSION = 150;

        public static void Write(string filePath, GeneratedShape shape)
        {
            if (shape == null || shape.Voxels == null)
                throw new ArgumentException("Shape is null or empty");

            int sizeX = shape.Voxels.GetLength(0);
            int sizeY = shape.Voxels.GetLength(1);
            int sizeZ = shape.Voxels.GetLength(2);

            // Collect non-empty voxels.
            // VS voxel convention (what VoxelArrayConverter creates):  (X, Y, Z) with Y = up.
            // MagicaVoxel .vox convention:                              (X, Y, Z) with Z = up.
            // VoxFile.Read converts .vox (X, Y, Z) → VS (X, Z, -Y).
            // To round-trip, we must write .vox (X, Y_vs=-Z_vox, Z_vs=Y_vox) → inverse:
            //   vox_x = vs_x,   vox_y = -vs_z + (sizeZ-1),   vox_z = vs_y
            // Bump vox_y to non-negative by adding (sizeZ-1).
            var entries = new List<byte[]>();
            for (int x = 0; x < sizeX; x++)
                for (int y = 0; y < sizeY; y++)
                    for (int z = 0; z < sizeZ; z++)
                    {
                        byte idx = shape.Voxels[x, y, z];
                        if (idx == 0) continue;
                        int voxX = x;
                        int voxY = (sizeZ - 1) - z;
                        int voxZ = y;
                        entries.Add(new byte[]
                        {
                            (byte)voxX, (byte)voxY, (byte)voxZ, idx
                        });
                    }

            // Write header + MAIN chunk with SIZE + XYZI + RGBA sub-chunks
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs, Encoding.ASCII))
            {
                // File magic + version
                bw.Write(Encoding.ASCII.GetBytes("VOX "));
                bw.Write(VERSION);

                // Build child chunks into a buffer so we can compute MAIN's child size.
                using (var childMs = new MemoryStream())
                using (var cw = new BinaryWriter(childMs))
                {
                    // SIZE chunk
                    WriteChunk(cw, "SIZE", sw =>
                    {
                        // .vox SIZE is (x, y, z) with z = up. We swap: voxSizeX=sizeX, voxSizeY=sizeZ, voxSizeZ=sizeY
                        sw.Write(sizeX);
                        sw.Write(sizeZ);
                        sw.Write(sizeY);
                    });

                    // XYZI chunk
                    WriteChunk(cw, "XYZI", sw =>
                    {
                        sw.Write(entries.Count);
                        foreach (var e in entries) sw.Write(e);
                    });

                    // RGBA chunk — always 256 entries, 4 bytes each (R, G, B, A)
                    // Spec: palette[i] corresponds to voxel color index i+1 (shifted).
                    // To keep our colorIdx bytes (1..255) mapping correctly on read,
                    // we write palette[i] at slot i (0-based) with A=255, and leave slot 255 zero.
                    WriteChunk(cw, "RGBA", sw =>
                    {
                        for (int i = 0; i < 256; i++)
                        {
                            byte[] c = null;
                            // Our palette slots are 1..255 (0 = empty). Shift to 0..254 here.
                            if (shape.Palette != null && i + 1 < shape.Palette.Length)
                                c = shape.Palette[i + 1];
                            if (c == null) c = new byte[] { 0, 0, 0, 0 };
                            sw.Write(c[0]);
                            sw.Write(c[1]);
                            sw.Write(c[2]);
                            sw.Write(c.Length >= 4 ? c[3] : (byte)255);
                        }
                    });

                    cw.Flush();
                    var childBytes = childMs.ToArray();

                    // MAIN chunk header
                    bw.Write(Encoding.ASCII.GetBytes("MAIN"));
                    bw.Write(0);                 // content bytes
                    bw.Write(childBytes.Length); // child bytes
                    bw.Write(childBytes);
                }
            }
        }

        private static void WriteChunk(BinaryWriter bw, string id, Action<BinaryWriter> writeContent)
        {
            if (id.Length != 4) throw new ArgumentException("Chunk ID must be 4 chars");
            using (var ms = new MemoryStream())
            using (var inner = new BinaryWriter(ms))
            {
                writeContent(inner);
                inner.Flush();
                var bytes = ms.ToArray();
                bw.Write(Encoding.ASCII.GetBytes(id));
                bw.Write(bytes.Length); // content bytes
                bw.Write(0);            // child bytes (none for leaf chunks)
                bw.Write(bytes);
            }
        }
    }
}
