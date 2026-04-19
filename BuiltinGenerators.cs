using System;
using System.Collections.Generic;

namespace AutomaticChiselling
{
    public static class BuiltinGenerators
    {
        public static IShapeGenerator[] GetAll() => new IShapeGenerator[]
        {
            new WallGenerator(),
            new CubeGenerator(),
            new SphereGenerator(),
            new DomeGenerator(),
            new CylinderGenerator(),
            new ConeGenerator(),
            new ArchGenerator(),
            new RoofGenerator(),
            new ColumnGenerator(),
            new TunnelGenerator()
        };
    }

    // ================================================================
    // Wall
    // ================================================================
    public class WallGenerator : IShapeGenerator
    {
        public string Name => "Wall";
        public string Description => "Flat wall";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "width", Label = "Width", Default = 32, Min = 1, Max = 256 },
            new ShapeParameter { Id = "height", Label = "Height", Default = 16, Min = 1, Max = 256 },
            new ShapeParameter { Id = "depth", Label = "Depth", Default = 1, Min = 1, Max = 64 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int w = p.GetInt("width"), h = p.GetInt("height"), d = p.GetInt("depth");
            var v = new bool[w, h, d];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    for (int z = 0; z < d; z++)
                        v[x, y, z] = true;
            return GeneratedShape.Mono(v);
        }
    }

    // ================================================================
    // Cube
    // ================================================================
    public class CubeGenerator : IShapeGenerator
    {
        public string Name => "Cube";
        public string Description => "Box / hollow box";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "width", Label = "Width", Default = 32, Min = 1, Max = 256 },
            new ShapeParameter { Id = "height", Label = "Height", Default = 32, Min = 1, Max = 256 },
            new ShapeParameter { Id = "depth", Label = "Depth", Default = 32, Min = 1, Max = 256 },
            new ShapeParameter { Id = "hollow", Label = "Hollow", Default = false, Type = ParameterType.Checkbox },
            new ShapeParameter { Id = "shell", Label = "Shell thickness", Default = 2, Min = 1, Max = 32 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int w = p.GetInt("width"), h = p.GetInt("height"), d = p.GetInt("depth");
            bool hollow = p.GetBool("hollow");
            int shell = p.GetInt("shell", 2);
            var v = new bool[w, h, d];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    for (int z = 0; z < d; z++)
                    {
                        if (hollow && x >= shell && x < w - shell &&
                            y >= shell && y < h - shell &&
                            z >= shell && z < d - shell)
                            continue;
                        v[x, y, z] = true;
                    }
            return GeneratedShape.Mono(v);
        }
    }

    // ================================================================
    // Sphere
    // ================================================================
    public class SphereGenerator : IShapeGenerator
    {
        public string Name => "Sphere";
        public string Description => "Full or hollow sphere";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "radius", Label = "Radius", Default = 16, Min = 2, Max = 128 },
            new ShapeParameter { Id = "hollow", Label = "Hollow", Default = false, Type = ParameterType.Checkbox },
            new ShapeParameter { Id = "shell", Label = "Shell thickness", Default = 2, Min = 1, Max = 16 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int r = p.GetInt("radius", 16);
            bool hollow = p.GetBool("hollow");
            int shell = p.GetInt("shell", 2);
            int size = r * 2;
            var v = new bool[size, size, size];
            float cr = r - 0.5f;
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                    for (int z = 0; z < size; z++)
                    {
                        float dx = x - cr, dy = y - cr, dz = z - cr;
                        float dist = dx * dx + dy * dy + dz * dz;
                        float rr = r * r;
                        if (dist <= rr)
                        {
                            if (hollow)
                            {
                                float innerR = (r - shell);
                                if (dist >= innerR * innerR)
                                    v[x, y, z] = true;
                            }
                            else
                                v[x, y, z] = true;
                        }
                    }
            return GeneratedShape.Mono(v);
        }
    }

    // ================================================================
    // Dome
    // ================================================================
    public class DomeGenerator : IShapeGenerator
    {
        public string Name => "Dome";
        public string Description => "Half sphere (dome)";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "radius", Label = "Radius", Default = 16, Min = 2, Max = 128 },
            new ShapeParameter { Id = "hollow", Label = "Hollow", Default = true, Type = ParameterType.Checkbox },
            new ShapeParameter { Id = "shell", Label = "Shell thickness", Default = 2, Min = 1, Max = 16 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int r = p.GetInt("radius", 16);
            bool hollow = p.GetBool("hollow", true);
            int shell = p.GetInt("shell", 2);
            int size = r * 2;
            var v = new bool[size, r, size];
            float cr = r - 0.5f;
            for (int x = 0; x < size; x++)
                for (int y = 0; y < r; y++)
                    for (int z = 0; z < size; z++)
                    {
                        float dx = x - cr, dy = y, dz = z - cr;
                        float dist = dx * dx + dy * dy + dz * dz;
                        float rr = r * r;
                        if (dist <= rr)
                        {
                            if (hollow)
                            {
                                float innerR = (r - shell);
                                if (dist >= innerR * innerR)
                                    v[x, y, z] = true;
                            }
                            else
                                v[x, y, z] = true;
                        }
                    }
            return GeneratedShape.Mono(v);
        }
    }

    // ================================================================
    // Cylinder
    // ================================================================
    public class CylinderGenerator : IShapeGenerator
    {
        public string Name => "Cylinder";
        public string Description => "Vertical cylinder";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "radius", Label = "Radius", Default = 12, Min = 2, Max = 128 },
            new ShapeParameter { Id = "height", Label = "Height", Default = 32, Min = 1, Max = 256 },
            new ShapeParameter { Id = "hollow", Label = "Hollow", Default = false, Type = ParameterType.Checkbox },
            new ShapeParameter { Id = "shell", Label = "Shell thickness", Default = 2, Min = 1, Max = 16 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int r = p.GetInt("radius", 12), h = p.GetInt("height", 32);
            bool hollow = p.GetBool("hollow");
            int shell = p.GetInt("shell", 2);
            int diam = r * 2;
            var v = new bool[diam, h, diam];
            float cr = r - 0.5f;
            for (int x = 0; x < diam; x++)
                for (int y = 0; y < h; y++)
                    for (int z = 0; z < diam; z++)
                    {
                        float dx = x - cr, dz = z - cr;
                        float dist2d = dx * dx + dz * dz;
                        if (dist2d <= r * r)
                        {
                            if (hollow && dist2d < (r - shell) * (r - shell))
                                continue;
                            v[x, y, z] = true;
                        }
                    }
            return GeneratedShape.Mono(v);
        }
    }

    // ================================================================
    // Cone
    // ================================================================
    public class ConeGenerator : IShapeGenerator
    {
        public string Name => "Cone";
        public string Description => "Cone / hollow cone";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "radius", Label = "Base radius", Default = 12, Min = 2, Max = 128 },
            new ShapeParameter { Id = "height", Label = "Height", Default = 24, Min = 2, Max = 256 },
            new ShapeParameter { Id = "hollow", Label = "Hollow", Default = false, Type = ParameterType.Checkbox },
            new ShapeParameter { Id = "shell", Label = "Shell thickness", Default = 2, Min = 1, Max = 16 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int r = p.GetInt("radius", 12), h = p.GetInt("height", 24);
            bool hollow = p.GetBool("hollow");
            int shell = p.GetInt("shell", 2);
            int diam = r * 2;
            var v = new bool[diam, h, diam];
            float cr = r - 0.5f;
            for (int y = 0; y < h; y++)
            {
                float layerR = r * (1f - (float)y / h);
                float innerR = Math.Max(0, layerR - shell);
                for (int x = 0; x < diam; x++)
                    for (int z = 0; z < diam; z++)
                    {
                        float dx = x - cr, dz = z - cr;
                        float dist = (float)Math.Sqrt(dx * dx + dz * dz);
                        if (dist <= layerR)
                        {
                            if (hollow && dist < innerR) continue;
                            v[x, y, z] = true;
                        }
                    }
            }
            return GeneratedShape.Mono(v);
        }
    }

    // ================================================================
    // Arch
    // ================================================================
    public class ArchGenerator : IShapeGenerator
    {
        public string Name => "Arch";
        public string Description => "Arch (rectangular, rounded, or circular)";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "width", Label = "Width", Default = 16, Min = 3, Max = 128 },
            new ShapeParameter { Id = "height", Label = "Height", Default = 24, Min = 3, Max = 128 },
            new ShapeParameter { Id = "depth", Label = "Depth", Default = 4, Min = 1, Max = 64 },
            new ShapeParameter { Id = "archType", Label = "Arch shape (0-2)", Default = 1, Min = 0, Max = 2 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int w = p.GetInt("width", 16), h = p.GetInt("height", 24), d = p.GetInt("depth", 4);
            int archType = p.GetInt("archType", 1);
            var v = new bool[w, h, d];
            float halfW = w / 2f;
            int archStartY = archType == 0 ? h - 1 : h - (int)halfW;

            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    for (int z = 0; z < d; z++)
                    {
                        if ((x < d || x >= w - d) && y < archStartY)
                        {
                            v[x, y, z] = true;
                            continue;
                        }
                        if (y >= archStartY)
                        {
                            float dx = x - (halfW - 0.5f);
                            float dy = y - archStartY;
                            float archR = halfW;
                            if (archType == 0)
                            {
                                if (x < d || x >= w - d || y >= h - d)
                                    v[x, y, z] = true;
                            }
                            else
                            {
                                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                                if (dist <= archR && dist >= archR - d)
                                    v[x, y, z] = true;
                            }
                        }
                    }
            return GeneratedShape.Mono(v);
        }
    }

    // ================================================================
    // Roof
    // ================================================================
    public class RoofGenerator : IShapeGenerator
    {
        public string Name => "Roof";
        public string Description => "Triangular roof";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "width", Label = "Width", Default = 20, Min = 4, Max = 128 },
            new ShapeParameter { Id = "height", Label = "Height", Default = 10, Min = 2, Max = 64 },
            new ShapeParameter { Id = "depth", Label = "Depth", Default = 30, Min = 1, Max = 256 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int w = p.GetInt("width", 20), h = p.GetInt("height", 10), d = p.GetInt("depth", 30);
            var v = new bool[w, h, d];
            for (int y = 0; y < h; y++)
            {
                int inset = (int)((float)y / h * (w / 2f));
                for (int x = inset; x < w - inset; x++)
                    for (int z = 0; z < d; z++)
                        v[x, y, z] = true;
            }
            return GeneratedShape.Mono(v);
        }
    }

    // ================================================================
    // Column
    // ================================================================
    public class ColumnGenerator : IShapeGenerator
    {
        public string Name => "Column";
        public string Description => "Round column with base and capital";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "radius", Label = "Radius", Default = 4, Min = 2, Max = 32 },
            new ShapeParameter { Id = "height", Label = "Height", Default = 32, Min = 4, Max = 256 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int r = p.GetInt("radius", 4), h = p.GetInt("height", 32);
            int diam = (r + 2) * 2;
            var v = new bool[diam, h, diam];
            float cr = diam / 2f - 0.5f;
            for (int y = 0; y < h; y++)
            {
                float layerR = (y < 2 || y >= h - 2) ? r + 1.5f : r;
                for (int x = 0; x < diam; x++)
                    for (int z = 0; z < diam; z++)
                    {
                        float dx = x - cr, dz = z - cr;
                        if (dx * dx + dz * dz <= layerR * layerR)
                            v[x, y, z] = true;
                    }
            }
            return GeneratedShape.Mono(v);
        }
    }

    // ================================================================
    // Tunnel
    // ================================================================
    public class TunnelGenerator : IShapeGenerator
    {
        public string Name => "Tunnel";
        public string Description => "Tunnel with arch ceiling";
        public ShapeParameter[] Parameters => new[]
        {
            new ShapeParameter { Id = "width", Label = "Width", Default = 12, Min = 4, Max = 64 },
            new ShapeParameter { Id = "height", Label = "Height", Default = 12, Min = 4, Max = 64 },
            new ShapeParameter { Id = "depth", Label = "Length", Default = 32, Min = 1, Max = 256 },
            new ShapeParameter { Id = "shell", Label = "Wall thickness", Default = 2, Min = 1, Max = 8 }
        };
        public GeneratedShape Generate(Dictionary<string, object> p)
        {
            int w = p.GetInt("width", 12), h = p.GetInt("height", 12), d = p.GetInt("depth", 32);
            int shell = p.GetInt("shell", 2);
            var v = new bool[w, h, d];
            float halfW = w / 2f;
            int archStartY = h / 2;

            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    for (int z = 0; z < d; z++)
                    {
                        bool isWall = false;
                        if (x < shell || x >= w - shell) isWall = true;
                        if (y >= archStartY)
                        {
                            float dx = x - (halfW - 0.5f);
                            float dy = y - archStartY;
                            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                            float outerR = halfW;
                            float innerR = halfW - shell;
                            if (dist <= outerR && dist >= innerR) isWall = true;
                            if (dist > outerR) isWall = false;
                        }
                        if (y < shell) isWall = true;
                        if (isWall) v[x, y, z] = true;
                    }
            return GeneratedShape.Mono(v);
        }
    }
}
