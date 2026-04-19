# Automatic Chiselling (AutoChisel)

**Version:** 2.0.0 &nbsp;•&nbsp; **Side:** Client &nbsp;•&nbsp; **Game:** Vintage Story 1.21+

A client-side Vintage Story mod that **automatically chisels 3D voxel models** from `.vox` files or from built-in / scripted shape generators. Pick a model, aim at the world, and the mod drives your chisel for you — breaking blocks that are in the way, placing the material blocks, and carving voxel-by-voxel until the model matches what you saw in the hologram preview.

---

## Differences from the original mod

The original Automatic Chiselling mod was a minimal, command-driven tool: it could load a single `.vox` from the `worldedit` folder and run a mono-color chisel sequence against it, with start position picked by looking at a block. Everything was done through `.vox load`, `.vox selpos`, `.vox start`, etc.

Version 2.0 is a full rewrite around a GUI-first workflow. Key differences:

| Area | Original | v2.0 (this mod) |
|---|---|---|
| **UI** | Chat commands only (`.vox load`, `.vox start`, …) | Model Browser dialog opened with **Z**; all commands removed |
| **Model source** | `worldedit/*.vox` | Dedicated `autochisel/models/*.vox` folder + built-in generators + user C# scripts |
| **Colors** | Mono only — the chisel block was whatever you were holding | Full **multi-color chisel**: map each palette index to any block; runtime assigns materials on the chisel block via the vanilla AddMaterial flow |
| **Color persistence** | None | Per-model palette mappings saved to `autochisel/material_mappings.json` and restored across game restarts |
| **Preview** | Block-grid highlights in the world | Translucent 3D **hologram** with custom animated gradient shader; optional block-grid mode |
| **Progress** | Start-over on stop | Progress is saved to `autochisel/progress/` and survives game restarts; **auto-resumes** after world load if an unfinished run is found |
| **HUD** | Chat messages only | Progress HUD with bar, ETA, pause/stop buttons |
| **Generators** | None | Dedicated **Generators** tab with 10 built-in shapes (Sphere, Cube, Dome, Arch, Cylinder, Cone, Roof, Column, Tunnel, Wall) |
| **User scripts** | None | Drop a `.cs` file into `autochisel/scripts/` — compiled live via Roslyn, appears in the Generators tab on next dialog open |
| **QR generator** | None | Built-in QR-code / text-stamp generator (configurable depth, backing, alignment, error-correction) |
| **Rotation** | None | Rotate model around **Y** (arrow ←/→), tilt around **X** (↑/↓), roll around **Z** (Shift+←/→) |
| **Material inventory** | Player must hold correct block | Mod auto-detects and switches to any mapped block in hotbar/backpack; prompts in chat if a block is missing |
| **Throughput** | Fixed pace | Adaptive: speeds up after sustained success, backs off when server lag is detected; uses hierarchical brush sizes (8→4→2→1) |
| **Partial blocks** | Re-chiseled from scratch | `CheckAndFixPartialBlock` reads the existing voxel state and only emits the diff |
| **File structure** | `worldedit/` (shared with Worldedit mod) | Private `autochisel/{models,scripts,progress}/` root, clean separation |

---

## Usage guide

### Quick start (mono-color)

1. Put any `.vox` file into `autochisel/models/`.
2. In-game, press **Z** to open the **AutoChisel Browser**.
3. Click a model in the list — the dialog closes and a translucent hologram of the model appears in the world anchored to wherever you aim.
4. Aim where you want the **south-west / bottom corner** of the model to be and **left-click** to confirm the position.
5. Hold a Chisel in the active hotbar slot and hold the base block (e.g. Granite Bricks) to be carved.
6. Click **Start** on the HUD (or right-click the hologram). The mod begins:
   - Phase 1 — break any blocks sitting where the model needs to be.
   - Phase 2 — place chisel blocks and carve the voxels.

The HUD shows progress %, remaining-time estimate, and **Pause** / **Stop** buttons.

### Multi-color models

1. Open the browser (Z). A `.vox` model with more than one palette colour has a **palette strip** below its card and an **Assign Materials** button.
2. Click **Assign Materials** — a dialog lists each used colour swatch. For each, choose which in-game block should represent it (Granite, Andesite, Oak Planks, …).
3. Unassigned colours fall through to the base block you're holding when the chisel starts.
4. Assignments are saved immediately to `material_mappings.json` and restored automatically the next time you open this model (even after a game restart).
5. Start chiseling as normal. The mod will:
   - Pick up each mapped block from your inventory once per chisel block.
   - Call the vanilla `AddMaterial` flow so the block entity has the correct `MaterialIds`.
   - Send per-voxel chisel packets with the right `materialIdx`.
   - Prompt you in chat if any required block is missing.

### Hologram preview

- Right-clicking with an **empty hand** when a model is loaded toggles the hologram.
- The hologram uses a custom GLSL shader with an animated cyan↔violet vertical gradient to make the preview unmistakable.
- If the shader fails to compile on your GPU, the mod automatically falls back to the standard Vintage Story shader path.

### Rotations

While a model is loaded and chiseling is **not** active:

| Key | Action |
|---|---|
| **→** | Rotate 90° CW around Y (horizontal spin, right) |
| **←** | Rotate 90° CCW around Y (horizontal spin, left) |
| **↑** | Tilt 90° forward around X |
| **↓** | Tilt 90° back around X |
| **Shift + →** | Roll 90° CW around Z |
| **Shift + ←** | Roll 90° CCW around Z |

The chat shows current angles: `Model: Y=90° X=0° Z=0°`. Rotations are cumulative and wrap at 360°. All three axes are independent; the mod applies them in Y → X → Z order when baking the voxel grid.

All six hotkeys can be rebound from Vintage Story's **Controls → GUI or other controls** settings (search for "AutoChisel").

### Pause / resume / auto-restore

- **Pause** stops the conveyor without losing state; progress is flushed to disk.
- **Stop** cancels the run entirely and deletes the progress file.
- If you log out mid-run, the progress file stays. The next time you load the world, the mod scans `autochisel/progress/` and offers to auto-resume if one is found.

### Generators tab

1. Press Z, click the **Generators** tab.
2. Pick a shape from the dropdown (Sphere, Cube, Dome, Arch, Cylinder, Cone, Roof, Column, Tunnel, Wall, QR Code, plus any user scripts).
3. Adjust parameters (sliders, checkboxes, text input, dropdowns — depending on the generator).
4. The preview renders live as you tweak.
5. Click **Generate & Load** — the shape is loaded as if it were a `.vox` model, a hologram appears, and you can place it and start chiseling.
6. Toggle **Save as .vox** to also write the shape to `autochisel/models/` so it reappears in the Models tab next time.

Click **Open Folder** at the bottom of the dialog to jump to the `autochisel/` root in the OS file explorer.

### User shape generators (C# scripts)

- Drop any `.cs` file into `autochisel/scripts/`. Files are compiled together into one assembly via Roslyn, so you can split code across multiple files.
- Re-open the browser (Z → close → Z) to recompile. No game restart needed.
- Compile errors land in `client-main.log`.
- See `autochisel/scripts/README.txt` — it's written by the mod on every launch and documents `IShapeGenerator`, `ShapeParameter`, `GeneratedShape`, parameter helpers, and contains three full example generators (mono cube, checkbox+dropdown, multi-color flag).

Skeleton:

```csharp
using System.Collections.Generic;
using AutomaticChiselling;

public class MyShape : IShapeGenerator
{
    public string Name => "My Shape";
    public string Description => "A custom shape";
    public ShapeParameter[] Parameters => new[] {
        new ShapeParameter { Id = "size", Label = "Size", Default = 16, Min = 4, Max = 128 }
    };

    public GeneratedShape Generate(Dictionary<string, object> p)
    {
        int s = p.GetInt("size", 16);
        var v = new bool[s, s, s];
        for (int x = 0; x < s; x++)
            for (int y = 0; y < s; y++)
                for (int z = 0; z < s; z++)
                    v[x, y, z] = true;
        return GeneratedShape.Mono(v);
    }
}
```

---

## Controls cheat sheet

| Input | Context | Action |
|---|---|---|
| **Z** | Anytime | Open / close Model Browser |
| **Left-click** | Hologram showing | Confirm start position |
| **Right-click empty hand** | Model loaded | Toggle hologram / grid preview |
| **→ / ←** | Model loaded | Rotate around Y |
| **↑ / ↓** | Model loaded | Tilt around X |
| **Shift + → / ←** | Model loaded | Roll around Z |

HUD buttons (bottom-right): **Start** • **Pause** • **Stop**.

---

## Folder layout reference

```
{VintageStoryData}/
  Mods/
    autochisel.dll          ← the mod binary
  autochisel/
    models/
      my_model.vox
      dragon.vox
    scripts/
      README.txt            ← auto-regenerated on every launch
      MyGenerator.cs        ← your custom generator
    progress/
      my_model.progress.json  ← in-progress save, deleted on Stop/Completion
    material_mappings.json  ← persistent palette → block mappings
```

---

## Known limits & tips

- **Chisel block limit:** vanilla allows max 16 distinct materials per chisel block. If a block region needs more colors, the least-used ones fall back to the base material.
- **Inventory requirements:** the mod can only carve if the required blocks are actually in your hotbar or backpack. Keep a stack of each mapped block on you.
- **Server latency:** the adaptive throttle detects lag and slows down automatically. On multiplayer you may see lower throughput than singleplayer.
- **Model size:** very large models (> 1 M voxels) work but preview and generation are slower; prefer breaking big builds into several `.vox` files.
- **Overlapping model footprint:** if the model lands on top of existing chisel blocks, Phase 1 may break them. Pick an empty area or use the hologram to check placement.

---

## Credits

- Original Automatic Chiselling — **Skif_97**
- v2.0 GUI, generators, multi-color, scripting, hologram, persistence, rotations — Community contributions


Licensed as a standalone VS mod; see repository for exact terms.
