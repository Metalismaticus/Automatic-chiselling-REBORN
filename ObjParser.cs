using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AutomaticChiselling
{
    /// <summary>
    /// Minimal Wavefront OBJ + MTL parser. Supports the subset we need for
    /// voxelization: vertices, UVs, triangulated faces, materials with Kd
    /// diffuse color and map_Kd diffuse texture.
    ///
    /// Unsupported (intentional, safe to ignore): smoothing groups, vertex
    /// normals (we compute per-face normals on the fly), object/group names
    /// beyond usemtl boundaries, free-form curves, per-vertex colors.
    /// </summary>
    public class ObjMesh
    {
        public List<ObjVec3> Positions = new List<ObjVec3>();
        public List<ObjVec2> UVs = new List<ObjVec2>();
        public List<ObjTriangle> Triangles = new List<ObjTriangle>();
        public List<ObjMaterial> Materials = new List<ObjMaterial>();

        /// <summary>Lookup material by name (case-insensitive).</summary>
        public ObjMaterial GetMaterial(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var m in Materials)
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                    return m;
            return null;
        }
    }

    public struct ObjVec3
    {
        public float X, Y, Z;
        public ObjVec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public struct ObjVec2
    {
        public float X, Y;
        public ObjVec2(float x, float y) { X = x; Y = y; }
    }

    /// <summary>
    /// Triangle indices into ObjMesh.Positions / ObjMesh.UVs, plus which material
    /// was active at the time of the face declaration.
    /// </summary>
    public class ObjTriangle
    {
        public int P0, P1, P2;          // position indices (0-based)
        public int UV0, UV1, UV2;       // UV indices (0-based, -1 if no UV)
        public int MaterialIdx;         // index into ObjMesh.Materials (-1 = none)
    }

    public class ObjMaterial
    {
        public string Name;
        public float KdR = 0.7f, KdG = 0.7f, KdB = 0.7f;  // diffuse color
        public string MapKdPath;                           // absolute path to diffuse texture (if any)
    }

    public static class ObjParser
    {
        /// <summary>
        /// Parses an .obj file (and any referenced .mtl) into an ObjMesh.
        /// All indices in ObjTriangle are 0-based.
        /// Throws IOException if the file is missing; returns null on parse error
        /// with a non-empty <paramref name="error"/> string.
        /// </summary>
        public static ObjMesh Parse(string objPath, out string error)
        {
            error = null;
            if (!File.Exists(objPath))
            {
                error = "File not found: " + objPath;
                return null;
            }

            var mesh = new ObjMesh();
            int currentMaterialIdx = -1;
            string objDir = Path.GetDirectoryName(objPath) ?? ".";

            try
            {
                using var reader = new StreamReader(objPath);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    switch (parts[0])
                    {
                        case "v":
                            if (parts.Length >= 4)
                            {
                                mesh.Positions.Add(new ObjVec3(
                                    ParseFloat(parts[1]),
                                    ParseFloat(parts[2]),
                                    ParseFloat(parts[3])));
                            }
                            break;

                        case "vt":
                            if (parts.Length >= 3)
                            {
                                mesh.UVs.Add(new ObjVec2(
                                    ParseFloat(parts[1]),
                                    ParseFloat(parts[2])));
                            }
                            break;

                        case "f":
                            // Face — may be n-gon; triangulate as a fan around vertex 1.
                            // Format of each part: v | v/vt | v//vn | v/vt/vn
                            if (parts.Length < 4) break;
                            int fanCount = parts.Length - 3;
                            for (int k = 0; k < fanCount; k++)
                            {
                                var t = new ObjTriangle
                                {
                                    MaterialIdx = currentMaterialIdx,
                                    UV0 = -1, UV1 = -1, UV2 = -1
                                };
                                ParseFaceVertex(parts[1],      mesh, out t.P0, out t.UV0);
                                ParseFaceVertex(parts[2 + k],  mesh, out t.P1, out t.UV1);
                                ParseFaceVertex(parts[3 + k],  mesh, out t.P2, out t.UV2);
                                mesh.Triangles.Add(t);
                            }
                            break;

                        case "mtllib":
                            if (parts.Length >= 2)
                            {
                                string mtlPath = Path.Combine(objDir, parts[1]);
                                if (File.Exists(mtlPath))
                                    LoadMtl(mtlPath, mesh);
                            }
                            break;

                        case "usemtl":
                            if (parts.Length >= 2)
                            {
                                string name = parts[1];
                                currentMaterialIdx = -1;
                                for (int i = 0; i < mesh.Materials.Count; i++)
                                    if (string.Equals(mesh.Materials[i].Name, name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        currentMaterialIdx = i;
                                        break;
                                    }
                            }
                            break;

                        // All other directives (o, g, s, vn, l, …) are silently skipped.
                    }
                }
            }
            catch (Exception e)
            {
                error = "Parse error: " + e.Message;
                return null;
            }

            return mesh;
        }

        private static void ParseFaceVertex(string token, ObjMesh mesh, out int posIdx, out int uvIdx)
        {
            posIdx = 0;
            uvIdx = -1;
            // Token is like "10", "10/3", "10//5", "10/3/5". Split on '/' and take
            // up to first two meaningful entries.
            var segs = token.Split('/');
            if (segs.Length >= 1 && segs[0].Length > 0)
                posIdx = NormalizeIndex(int.Parse(segs[0], CultureInfo.InvariantCulture), mesh.Positions.Count);
            if (segs.Length >= 2 && segs[1].Length > 0)
                uvIdx = NormalizeIndex(int.Parse(segs[1], CultureInfo.InvariantCulture), mesh.UVs.Count);
        }

        /// <summary>
        /// OBJ indices are 1-based and can be negative (relative-from-end).
        /// Convert to 0-based.
        /// </summary>
        private static int NormalizeIndex(int raw, int count)
        {
            if (raw > 0) return raw - 1;
            if (raw < 0) return count + raw; // e.g. -1 → last
            return 0;
        }

        private static float ParseFloat(string s)
        {
            // OBJ always uses '.' as decimal. Don't let system locale change this.
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        private static void LoadMtl(string mtlPath, ObjMesh mesh)
        {
            string mtlDir = Path.GetDirectoryName(mtlPath) ?? ".";
            ObjMaterial current = null;

            using var reader = new StreamReader(mtlPath);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                switch (parts[0])
                {
                    case "newmtl":
                        current = new ObjMaterial { Name = parts.Length >= 2 ? parts[1] : "" };
                        mesh.Materials.Add(current);
                        break;

                    case "Kd":
                        if (current != null && parts.Length >= 4)
                        {
                            current.KdR = ParseFloat(parts[1]);
                            current.KdG = ParseFloat(parts[2]);
                            current.KdB = ParseFloat(parts[3]);
                        }
                        break;

                    case "map_Kd":
                        if (current != null && parts.Length >= 2)
                        {
                            // Texture path may be relative to .mtl, relative to .obj,
                            // or absolute. Try .mtl dir first.
                            string texPath = parts[parts.Length - 1]; // last token (handle spaces in path poorly but better than nothing)
                            string candidate = Path.IsPathRooted(texPath)
                                ? texPath
                                : Path.Combine(mtlDir, texPath);
                            if (File.Exists(candidate))
                                current.MapKdPath = candidate;
                            else
                                current.MapKdPath = texPath; // store raw, caller may retry
                        }
                        break;

                    // Ns, Ka, Ks, Ke, Ni, d, illum, map_Bump, map_Ks, … — ignored.
                }
            }
        }
    }
}
