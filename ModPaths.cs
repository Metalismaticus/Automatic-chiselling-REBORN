using System.IO;
using Vintagestory.API.Config;

namespace AutomaticChiselling
{
    /// <summary>
    /// Centralized paths for the mod. Creates folders on first access.
    /// All model/progress/script files go under {DataPath}/autochisel/
    /// </summary>
    public static class ModPaths
    {
        private static string _root;

        public static string Root
        {
            get
            {
                if (_root == null)
                {
                    _root = Path.Combine(GamePaths.DataPath, "autochisel");
                    EnsureDirectories();
                }
                return _root;
            }
        }

        public static string Models => Path.Combine(Root, "models");
        public static string Scripts => Path.Combine(Root, "scripts");
        public static string Progress => Path.Combine(Root, "progress");

        /// <summary>File where per-model palette→block mappings are persisted across sessions.</summary>
        public static string MappingsFile => Path.Combine(Root, "material_mappings.json");

        /// <summary>
        /// For backward compatibility: also scan worldedit folder for .vox files.
        /// </summary>
        public static string LegacyWorldEdit => Path.Combine(GamePaths.DataPath, "worldedit");

        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(Models);
            Directory.CreateDirectory(Scripts);
            Directory.CreateDirectory(Progress);

            // Write README for scripts — always overwrite so mod updates refresh the doc.
            // (This file is reference, not user-editable.)
            string readme = Path.Combine(Scripts, "README.txt");
            {
                File.WriteAllText(readme,
@"AutoChisel Script Generators
=============================

Drop a .cs file into this folder and the mod will pick it up automatically
the next time you open the Model Browser (press Z in-game). No game restart
is needed — just re-open the browser to reload user scripts.

All .cs files in THIS folder are compiled together into ONE assembly, so you
can split a library across multiple files and reference types between them.

If a script has syntax errors the mod falls back to per-file compilation so
that one broken file doesn't hide the rest. See client-main.log for compile
errors.


COORDINATE SYSTEM
-----------------
  X → east/west (width)
  Y → UP (height)
  Z → north/south (depth)

Index voxels as Voxels[x, y, z]. In-game, the model rises along Y.


PARAMETER TYPES
---------------
  Slider     integer, shown with [- value +] buttons.
             Fields: Default (int), Min, Max.

  Checkbox   boolean on/off, shown as [ON]/[OFF].
             Fields: Default (true/false).

  TextInput  free-text string, shown as an editable box.
             Fields: Default (string), MaxLength, Placeholder.

  Dropdown   choose one of DropdownValues[].
             Fields: Default (string matching one of DropdownValues),
                     DropdownValues (string[]).

Inside Generate(), read parameters with the helpers:
  int    i = p.GetInt(""id"",    fallback);
  bool   b = p.GetBool(""id"",   fallback);
  string s = p.GetString(""id"", fallback);


RETURN VALUE
------------
Generate() returns a GeneratedShape with:
  Voxels[x,y,z]  — 0 means EMPTY, 1..255 is a palette index
  Palette[i]     — byte[4] = { R, G, B, A } for palette entry i

Shortcuts:
  GeneratedShape.Mono(bool[,,])       single color, defaults to light gray
  GeneratedShape.Mono(bool[,,], r,g,b) single color with your RGB
  GeneratedShape.Empty(sx, sy, sz)     blank canvas to fill manually

If you paint voxels with different palette indices and supply Palette entries
for them, the generated model is treated as multi-color end-to-end: the Model
Browser will show the palette, and Material Mapping lets the player assign a
different block to each color.


EXAMPLES
========

--- 1. Simple mono-color cube ---

using System.Collections.Generic;
using AutomaticChiselling;

public class MyCube : IShapeGenerator
{
    public string Name => ""My Cube"";
    public string Description => ""A solid cube"";
    public ShapeParameter[] Parameters => new[] {
        new ShapeParameter { Id = ""size"", Label = ""Size"", Default = 16, Min = 4, Max = 128 }
    };

    public GeneratedShape Generate(Dictionary<string, object> p)
    {
        int s = p.GetInt(""size"", 16);
        var v = new bool[s, s, s];
        for (int x = 0; x < s; x++)
            for (int y = 0; y < s; y++)
                for (int z = 0; z < s; z++)
                    v[x, y, z] = true;
        return GeneratedShape.Mono(v);
    }
}


--- 2. Hollow option (Checkbox) + Dropdown ---

using System.Collections.Generic;
using AutomaticChiselling;

public class MyBox : IShapeGenerator
{
    public string Name => ""My Box"";
    public string Description => ""Box with optional hollow and style"";
    public ShapeParameter[] Parameters => new[] {
        new ShapeParameter { Id = ""size"",   Label = ""Size"",   Default = 24, Min = 4, Max = 96 },
        new ShapeParameter { Id = ""hollow"", Label = ""Hollow"", Default = true,
                             Type = ParameterType.Checkbox },
        new ShapeParameter { Id = ""style"",  Label = ""Style"",  Default = ""rounded"",
                             Type = ParameterType.Dropdown,
                             DropdownValues = new[] { ""sharp"", ""rounded"" } }
    };

    public GeneratedShape Generate(Dictionary<string, object> p)
    {
        int s = p.GetInt(""size"", 24);
        bool hollow = p.GetBool(""hollow"", true);
        string style = p.GetString(""style"", ""rounded"");

        var v = new bool[s, s, s];
        for (int x = 0; x < s; x++)
            for (int y = 0; y < s; y++)
                for (int z = 0; z < s; z++)
                {
                    bool solid = true;
                    if (hollow && x > 0 && x < s-1 && y > 0 && y < s-1 && z > 0 && z < s-1)
                        solid = false;
                    if (style == ""rounded"" && (x + y + z == 0 || (x == s-1 && y == s-1 && z == s-1)))
                        solid = false;
                    if (solid) v[x, y, z] = true;
                }
        return GeneratedShape.Mono(v);
    }
}


--- 3. Multi-color shape (custom palette) ---

using System.Collections.Generic;
using AutomaticChiselling;

public class Flag : IShapeGenerator
{
    public string Name => ""Flag"";
    public string Description => ""Three-color stripes"";
    public ShapeParameter[] Parameters => new[] {
        new ShapeParameter { Id = ""w"", Label = ""Width"",  Default = 30, Min = 3, Max = 150 },
        new ShapeParameter { Id = ""h"", Label = ""Height"", Default = 20, Min = 3, Max = 100 }
    };

    public GeneratedShape Generate(Dictionary<string, object> p)
    {
        int w = p.GetInt(""w"", 30);
        int h = p.GetInt(""h"", 20);
        int d = 2;

        var shape = GeneratedShape.Empty(w, h, d);
        shape.Palette[1] = new byte[] { 255,  50,  50, 255 }; // red
        shape.Palette[2] = new byte[] { 255, 255, 255, 255 }; // white
        shape.Palette[3] = new byte[] {  50,  80, 255, 255 }; // blue

        int stripe = h / 3;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                for (int z = 0; z < d; z++)
                {
                    byte idx = (y < stripe) ? (byte)1 : (y < 2*stripe) ? (byte)2 : (byte)3;
                    shape.Voxels[x, y, z] = idx;
                }
        return shape;
    }
}


--- 4. Text-driven shape (TextInput) ---

using System.Collections.Generic;
using AutomaticChiselling;

public class NameTower : IShapeGenerator
{
    public string Name => ""Name Tower"";
    public string Description => ""Tower whose height scales with the inscription length"";
    public ShapeParameter[] Parameters => new[] {
        new ShapeParameter { Id = ""width"", Label = ""Width"", Default = 4, Min = 2, Max = 16 },
        new ShapeParameter { Id = ""inscription"", Label = ""Inscription"",
                             Default = ""hello"", Type = ParameterType.TextInput,
                             MaxLength = 32, Placeholder = ""any text"" }
    };

    public GeneratedShape Generate(Dictionary<string, object> p)
    {
        int w = p.GetInt(""width"", 4);
        string text = p.GetString(""inscription"", ""hello"");

        // 2 voxels of height per character, clamp to avoid zero / huge results
        int h = System.Math.Max(2, System.Math.Min(text.Length * 2, 128));

        var v = new bool[w, h, w];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                for (int z = 0; z < w; z++)
                    v[x, y, z] = true;
        return GeneratedShape.Mono(v);
    }
}


TIPS
----
- If a generator throws an exception, the preview silently becomes empty and
  no model is generated. Check client-main.log for the stack trace.
- Keep total voxel count reasonable. Huge grids (>1M voxels) will lag the
  preview and the later chisel run.
- The folder is scanned once per browser open. If you're iterating on a
  script, close the browser (Z) and reopen it to reload.
");
            }
        }

        /// <summary>
        /// Returns all .vox files from autochisel/models/.
        /// </summary>
        public static string[] GetAllVoxFiles()
        {
            var files = new System.Collections.Generic.List<string>();

            if (Directory.Exists(Models))
                files.AddRange(Directory.GetFiles(Models, "*.vox"));

            files.Sort();
            return files.ToArray();
        }
    }
}
