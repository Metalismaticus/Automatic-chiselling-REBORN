using System.Collections.Generic;

namespace AutomaticChiselling
{
    public enum ParameterType
    {
        Slider,
        Checkbox,
        Dropdown,
        TextInput
    }

    public class ShapeParameter
    {
        public string Id;
        public string Label;
        public ParameterType Type = ParameterType.Slider;

        // Default value: int for Slider, bool (or int 0/1) for Checkbox, string for TextInput,
        // string/int for Dropdown (index into DropdownValues).
        public object Default;

        // Slider range
        public int Min = 1;
        public int Max = 256;

        // TextInput constraints
        public int MaxLength = 256;
        public string Placeholder = "";

        // Dropdown options
        public string[] DropdownValues;
    }

    /// <summary>
    /// Result of a shape generation.
    /// Voxels[x,y,z]: 0 = empty, 1..255 = palette index.
    /// Palette[i]: RGBA bytes for that index (entries 0 and unused may be null).
    /// </summary>
    public class GeneratedShape
    {
        public byte[,,] Voxels;
        public byte[][] Palette;

        /// <summary>
        /// Build a mono-color shape from a boolean voxel array. All filled voxels
        /// get palette index 1 with the given RGB color (default gray).
        /// </summary>
        public static GeneratedShape Mono(bool[,,] voxels, byte r = 180, byte g = 180, byte b = 180)
        {
            if (voxels == null) return null;
            int sx = voxels.GetLength(0);
            int sy = voxels.GetLength(1);
            int sz = voxels.GetLength(2);

            var shape = new GeneratedShape
            {
                Voxels = new byte[sx, sy, sz],
                Palette = new byte[256][]
            };
            shape.Palette[1] = new byte[] { r, g, b, 255 };

            for (int x = 0; x < sx; x++)
                for (int y = 0; y < sy; y++)
                    for (int z = 0; z < sz; z++)
                        if (voxels[x, y, z]) shape.Voxels[x, y, z] = 1;

            return shape;
        }

        /// <summary>
        /// Build an empty shape of given dimensions. Helpful as a starting canvas.
        /// </summary>
        public static GeneratedShape Empty(int sx, int sy, int sz)
        {
            return new GeneratedShape
            {
                Voxels = new byte[sx, sy, sz],
                Palette = new byte[256][]
            };
        }
    }

    /// <summary>
    /// Interface for shape generators. Implement this to create custom generators.
    /// Built-in generators and user scripts both implement this.
    /// </summary>
    public interface IShapeGenerator
    {
        string Name { get; }
        string Description { get; }
        ShapeParameter[] Parameters { get; }

        /// <summary>
        /// Generate a shape. Parameters are passed as object values — cast them
        /// based on their declared ParameterType:
        ///   Slider   → (int)p["id"]
        ///   Checkbox → (bool)p["id"]      (true = on, false = off)
        ///   Dropdown → (string)p["id"]    (selected value)
        ///   TextInput → (string)p["id"]
        /// </summary>
        GeneratedShape Generate(Dictionary<string, object> parameters);
    }

    /// <summary>
    /// Helper extensions for safe parameter access from user-written generators.
    /// </summary>
    public static class ParamExtensions
    {
        public static int GetInt(this Dictionary<string, object> p, string id, int def = 0)
        {
            if (!p.TryGetValue(id, out var v) || v == null) return def;
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is bool b) return b ? 1 : 0;
            int.TryParse(v.ToString(), out int parsed);
            return parsed;
        }

        public static bool GetBool(this Dictionary<string, object> p, string id, bool def = false)
        {
            if (!p.TryGetValue(id, out var v) || v == null) return def;
            if (v is bool b) return b;
            if (v is int i) return i != 0;
            bool.TryParse(v.ToString(), out bool parsed);
            return parsed;
        }

        public static string GetString(this Dictionary<string, object> p, string id, string def = "")
        {
            if (!p.TryGetValue(id, out var v) || v == null) return def;
            return v.ToString();
        }
    }
}
