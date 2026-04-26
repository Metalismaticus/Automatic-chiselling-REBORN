using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace AutomaticChiselling
{
    public enum ModelAllign
    {
        Center,
        Northeast,
        Northwest,
        Southeast,
        Southwest
    }

    public class AutomaticChiselling : ModSystem
    {
        private ICoreClientAPI capi;
        private VoxelsStorage storage;
        private ChiselConveyor conveyor;
        private ProgressHud progressHud;
        private ModelBrowserDialog browserDialog;
        private HologramRenderer hologram;
        private bool previewMode = false;
        private ModelAllign allign = ModelAllign.Center;
        private Vec3i blocksOffset = new Vec3i(0, 0, 0);
        private Vec3i voxelsOffset = new Vec3i(0, 0, 0);

        static int hLMDID = 500;
        static int hLMBID = 501;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            Patchs.PatchAll();

            progressHud = new ProgressHud(capi);
            progressHud.OnStartClicked = OnHudStart;
            progressHud.OnPauseClicked = OnHudPause;
            progressHud.OnStopClicked = OnHudStop;
            browserDialog = new ModelBrowserDialog(capi, OnModelSelectedFromBrowser, OnGeneratedModel);
            hologram = new HologramRenderer(capi);

            // Register the custom hologram shader. Must happen after the renderer is constructed
            // so we can hand the compiled program to it. If compilation fails, the renderer
            // falls back to the standard shader path.
            capi.Event.ReloadShader += () =>
            {
                var prog = HologramShader.Register(capi);
                hologram.SetShader(prog);
                return prog != null;
            };

            // RMB empty hand → position/confirm model
            capi.Input.InWorldAction += OnInWorldAction;

            // Auto-restore after world is fully loaded
            capi.Event.LevelFinalize += TryAutoRestore;

            // Register hotkey for model browser
            capi.Input.RegisterHotKey("autochisel_browser", "AutoChisel: Open Model Browser", GlKeys.Z, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler("autochisel_browser", OnToggleBrowser);

            // Register hotkeys for rotation (arrow keys)
            capi.Input.RegisterHotKey("autochisel_rotatecw", "AutoChisel: Rotate Y-axis CW", GlKeys.Right, HotkeyType.GUIOrOtherControls);
            capi.Input.RegisterHotKey("autochisel_rotateccw", "AutoChisel: Rotate Y-axis CCW", GlKeys.Left, HotkeyType.GUIOrOtherControls);
            capi.Input.RegisterHotKey("autochisel_tiltup", "AutoChisel: Tilt X-axis Up", GlKeys.Up, HotkeyType.GUIOrOtherControls);
            capi.Input.RegisterHotKey("autochisel_tiltdown", "AutoChisel: Tilt X-axis Down", GlKeys.Down, HotkeyType.GUIOrOtherControls);
            // Z-axis roll (Shift+Left/Right = rotate around camera-forward axis)
            capi.Input.RegisterHotKey("autochisel_rollcw",  "AutoChisel: Roll Z-axis CW",  GlKeys.Right, HotkeyType.GUIOrOtherControls, shiftPressed: true);
            capi.Input.RegisterHotKey("autochisel_rollccw", "AutoChisel: Roll Z-axis CCW", GlKeys.Left,  HotkeyType.GUIOrOtherControls, shiftPressed: true);
            capi.Input.SetHotKeyHandler("autochisel_rotatecw", OnRotateCW);
            capi.Input.SetHotKeyHandler("autochisel_rotateccw", OnRotateCCW);
            capi.Input.SetHotKeyHandler("autochisel_tiltup", OnTiltUp);
            capi.Input.SetHotKeyHandler("autochisel_tiltdown", OnTiltDown);
            capi.Input.SetHotKeyHandler("autochisel_rollcw",  OnRollCW);
            capi.Input.SetHotKeyHandler("autochisel_rollccw", OnRollCCW);
        }

        // --- RMB = show hologram, LMB = confirm → show grid ---

        private void OnInWorldAction(EnumEntityAction action, bool on, ref EnumHandling handling)
        {
            if (!on) return;
            if (storage == null) return;
            if (conveyor != null && conveyor.ChisellingActive()) return;

            var slot = capi.World.Player.InventoryManager.ActiveHotbarSlot;
            if (slot == null || !slot.Empty) return;

            var blockSel = capi.World.Player.Entity.BlockSelection;
            if (blockSel == null) return;

            if (action == EnumEntityAction.RightMouseDown)
            {
                // ПКМ = поставить/переместить голограмму (без схемы)
                ShowHologramAt(blockSel.Position);
                handling = EnumHandling.PreventDefault;
            }
            else if (action == EnumEntityAction.LeftMouseDown && previewMode)
            {
                // ЛКМ в режиме голограммы = подтвердить → убрать голограмму, показать схему
                ConfirmPlacement();
                handling = EnumHandling.PreventDefault;
            }
        }

        private void ShowHologramAt(BlockPos pos)
        {
            blocksOffset = pos.ToVec3i();
            storage.SetBlockVoxelsOffsetAndAllign(blocksOffset, voxelsOffset, allign);

            // Show ONLY hologram, no grid highlights
            ClearHighlights();
            hologram.SetModel(storage);
            hologram.SetVisible(true);
            previewMode = true;

            capi.ShowChatMessage("Hologram placed. Rotate with arrows. LMB to confirm.");
        }

        private void ConfirmPlacement()
        {
            // Hide hologram, show block grid
            hologram.SetVisible(false);
            previewMode = false;

            HiLightModelBlocks(storage.GetModelHighlightList());
            HiLightModelDimension(storage.GetDimensionsHighlightList());

            capi.ShowChatMessage("Position confirmed. Use .vox start to begin chiseling.");
        }

        // --- Auto-restore from previous session ---

        /// <summary>
        /// Restores VoxelsStorage from progress file.
        /// For .vox models: loads from file. For generated: re-runs the generator.
        /// </summary>
        private VoxelsStorage RestoreStorage(ChiselProgress progress)
        {
            VoxelsStorage storage;

            // Generated model — re-run generator with saved params
            if (!string.IsNullOrEmpty(progress.GeneratorName) && progress.GeneratorParams != null)
            {
                var gen = BuiltinGenerators.GetAll()
                    .FirstOrDefault(g => g.Name == progress.GeneratorName);
                if (gen == null) return null;

                try
                {
                    var shape = gen.Generate(progress.GeneratorParams);
                    storage = VoxelArrayConverter.FromShape(shape, gen.Name,
                        gen.Name, progress.GeneratorParams);
                }
                catch { return null; }
            }
            else
            {
                // Regular .vox file
                storage = new VoxelsStorage(progress.ModelFileName);
                if (storage.GetBlockCount() == 0) return null;
            }

            // Restore material mapping if present in progress file
            if (storage != null && progress.MaterialAssignments != null && progress.MaterialAssignments.Count > 0)
            {
                var mapping = new MaterialMapping
                {
                    Assignments = new Dictionary<byte, string>(progress.MaterialAssignments)
                };
                storage.MaterialMapping = mapping;
            }

            return storage;
        }

        private void TryAutoRestore()
        {
            try
            {
                var progress = ChiselConveyor.FindProgressFile();
                if (progress == null) return;

                // Restore either from .vox file or by re-running the generator
                storage = RestoreStorage(progress);
                if (storage == null || storage.GetBlockCount() == 0) { storage = null; return; }

                // Restore position and alignment
                blocksOffset = new Vec3i(progress.StartX, progress.StartY, progress.StartZ);
                voxelsOffset = new Vec3i(progress.OffsetVoxX, progress.OffsetVoxY, progress.OffsetVoxZ);
                if (Enum.TryParse<ModelAllign>(progress.Alignment, out var savedAlign))
                    allign = savedAlign;

                storage.SetBlockVoxelsOffsetAndAllign(blocksOffset, voxelsOffset, allign);

                // Show paused HUD with progress
                float pct = progress.TotalOperations > 0
                    ? (float)progress.CurrentIndex / progress.TotalOperations * 100f : 0f;

                progressHud.ShowModelLoaded();
                progressHud.UpdateProgress(pct);
                progressHud.ShowPaused();

                // Show grid at saved position
                HiLightModelBlocks(storage.GetModelHighlightList());
                HiLightModelDimension(storage.GetDimensionsHighlightList());

                capi.ShowChatMessage($"Restored: {progress.ModelFileName} ({pct:F0}% done). Press Start to continue.");
            }
            catch (Exception) { }
        }

        // --- HUD button handlers ---

        private void OnHudStart()
        {
            if (storage == null) { capi.ShowChatMessage("No model loaded."); return; }

            if (conveyor != null && conveyor.ChisellingActive())
            {
                // Resume from pause (same session)
                conveyor.ResumeConveyor();
                progressHud.ShowChiseling();
                capi.ShowChatMessage("Resumed.");
                return;
            }

            // Check for saved progress (resume from previous session)
            var progress = ChiselConveyor.LoadProgress(storage.GetFileName());
            if (progress != null)
            {
                // Restore position
                blocksOffset = new Vec3i(progress.StartX, progress.StartY, progress.StartZ);
                voxelsOffset = new Vec3i(progress.OffsetVoxX, progress.OffsetVoxY, progress.OffsetVoxZ);
                if (Enum.TryParse<ModelAllign>(progress.Alignment, out var savedAlign))
                    allign = savedAlign;
                storage.SetBlockVoxelsOffsetAndAllign(blocksOffset, voxelsOffset, allign);

                CleanupConveyor();
                conveyor = new ChiselConveyor(capi, storage);
                conveyor.OnProgressChanged += OnProgressChanged;
                conveyor.OnCompleted += OnChiselingCompleted;
                conveyor.ResumeFromIndex(progress.CurrentIndex);

                hologram.SetVisible(false);
                previewMode = false;
                progressHud.ShowChiseling();
                capi.ShowChatMessage($"Resumed from {(int)(progress.TotalOperations > 0 ? (float)progress.CurrentIndex / progress.TotalOperations * 100f : 0)}%");
                return;
            }

            // Start new chiseling
            CleanupConveyor();
            conveyor = new ChiselConveyor(capi, storage);
            conveyor.OnProgressChanged += OnProgressChanged;
            conveyor.OnCompleted += OnChiselingCompleted;
            conveyor.StartConveyor();

            if (conveyor.TotalOperations == 0)
            {
                CleanupConveyor();
                capi.ShowChatMessage("Nothing to do — model is already complete.");
                return;
            }

            hologram.SetVisible(false);
            previewMode = false;
            progressHud.ShowChiseling();
        }

        private void OnHudPause()
        {
            if (conveyor == null || !conveyor.ChisellingActive()) return;
            conveyor.PauseConveyor();
            progressHud.ShowPaused();

            capi.ShowChatMessage("Paused. Progress saved.");
        }

        private void OnHudStop()
        {
            if (conveyor != null && conveyor.ChisellingActive())
            {
                // Active conveyor path: StopConveyor() also deletes the progress file.
                conveyor.StopConveyor();
                CleanupConveyor();
            }
            else if (storage != null)
            {
                // Auto-restored (post game-restart) path: no live conveyor exists,
                // but the progress JSON still sits on disk — delete it here or
                // TryAutoRestore will keep offering the same paused run every load.
                ChiselConveyor.DeleteProgressFile(storage.GetFileName());
            }

            storage = null;
            hologram.SetVisible(false);
            previewMode = false;
            progressHud.Hide();
            ClearHighlights();
            capi.ShowChatMessage("Stopped.");
        }

        private bool OnToggleBrowser(KeyCombination kc)
        {
            // Defensive: on servers the browserDialog may not have been fully initialized
            // at first world load — log so we can see if this path is reached.
            if (browserDialog == null)
            {
                capi?.ShowChatMessage("[AutoChisel] Browser dialog not initialized. Reconnect to world.");
                capi?.Logger.Warning("[AutoChisel] OnToggleBrowser: browserDialog is null");
                return true;
            }

            if (conveyor != null && conveyor.ChisellingActive())
            {
                capi.ShowChatMessage("Cannot browse while chiseling is active.");
                return true;
            }

            try
            {
                if (browserDialog.IsOpened())
                    browserDialog.TryClose();
                else
                    browserDialog.TryOpen();
            }
            catch (Exception e)
            {
                capi.Logger.Error("[AutoChisel] Browser toggle failed: " + e);
                capi.ShowChatMessage("[AutoChisel] Cannot open browser: " + e.Message);
            }
            return true;
        }

        private void OnGeneratedModel(VoxelsStorage generatedStorage)
        {
            if (conveyor != null && conveyor.ChisellingActive()) return;

            storage = generatedStorage;
            voxelsOffset = new Vec3i(0, 0, 0);
            previewMode = false;
            hologram.SetVisible(false);

            int blocks = storage.OriginalBlockCount;
            int voxels = storage.GetTotalVoxelCount();
            int chiselOps = ChiselConveyor.CountChiselOperations(storage);

            progressHud.ShowModelLoaded();
            capi.ShowChatMessage($"Generated: {storage.GetFileName()} ({blocks} blocks, {voxels} voxels, {chiselOps:N0} ops)");
        }

        private void OnModelSelectedFromBrowser(string filename, MaterialMapping mapping)
        {
            if (conveyor != null && conveyor.ChisellingActive()) return;

            try
            {
                storage = new VoxelsStorage(filename);
                if (storage.GetBlockCount() == 0)
                {
                    capi.ShowChatMessage($"Model '{filename}' is empty or failed to load.");
                    storage = null;
                    return;
                }
            }
            catch (Exception e)
            {
                capi.ShowChatMessage("Error loading model: " + e.Message);
                storage = null;
                return;
            }

            // Attach material mapping (may be empty → mono-color fallback)
            storage.MaterialMapping = mapping != null ? mapping.Clone() : new MaterialMapping();

            voxelsOffset = new Vec3i(0, 0, 0);
            previewMode = false;
            hologram.SetVisible(false);

            int blocks = storage.OriginalBlockCount;
            int voxels = storage.GetTotalVoxelCount();
            int chiselOps = ChiselConveyor.CountChiselOperations(storage);
            int interiorFilled = storage.InteriorVoxelsFilled;

            progressHud.ShowModelLoaded();

            string colorTag = "";
            if (storage.IsColored)
            {
                int assigned = storage.MaterialMapping.AssignedCount;
                int total = storage.UsedPaletteIndices.Count;
                colorTag = $" [colors: {assigned}/{total}]";
            }
            capi.ShowChatMessage($"Model loaded: {filename} ({blocks} blocks, {voxels} voxels, {chiselOps:N0} ops){colorTag}");
        }

        public override void Dispose()
        {
            // On game exit: SAVE progress, don't delete it (so we can resume next session)
            if (conveyor != null && conveyor.ChisellingActive())
            {
                conveyor.PauseConveyor(); // saves progress + unregisters tick
            }
            progressHud?.Hide();
            browserDialog?.Dispose();
            hologram?.Dispose();
            capi.Input.InWorldAction -= OnInWorldAction;
            capi.Event.LevelFinalize -= TryAutoRestore;
            Patchs.UnpatchAll();
            base.Dispose();
        }

        private void DoTransform(string action)
        {
            if (storage == null) return;
            if (conveyor != null && conveyor.ChisellingActive()) return;

            switch (action)
            {
                case "rotateCW": storage.RotateY_CW(); break;
                case "rotateCCW": storage.RotateY_CCW(); break;
                case "tiltUp": storage.RotateX_CW(); break;
                case "tiltDown": storage.RotateX_CCW(); break;
                case "rollCW": storage.RotateZ_CW(); break;
                case "rollCCW": storage.RotateZ_CCW(); break;
            }

            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (storage == null) return;
            storage.SetBlockVoxelsOffsetAndAllign(blocksOffset, voxelsOffset, allign);

            if (previewMode)
            {
                // Only hologram, no grid
                hologram.SetModel(storage);
            }
            else
            {
                // Only grid, no hologram
                HiLightModelBlocks(storage.GetModelHighlightList());
                HiLightModelDimension(storage.GetDimensionsHighlightList());
            }
        }

        // --- Hotkey handlers ---

        private bool OnRotateCW(KeyCombination kc) => DoHotkeyTransform("rotateCW");
        private bool OnRotateCCW(KeyCombination kc) => DoHotkeyTransform("rotateCCW");
        private bool OnTiltUp(KeyCombination kc) => DoHotkeyTransform("tiltUp");
        private bool OnTiltDown(KeyCombination kc) => DoHotkeyTransform("tiltDown");
        private bool OnRollCW(KeyCombination kc) => DoHotkeyTransform("rollCW");
        private bool OnRollCCW(KeyCombination kc) => DoHotkeyTransform("rollCCW");

        private bool DoHotkeyTransform(string action)
        {
            if (storage == null || (conveyor != null && conveyor.ChisellingActive()))
                return false;

            DoTransform(action);
            capi.ShowChatMessage($"Model: Y={storage.GetRotationY()}° X={storage.GetRotationX()}° Z={storage.GetRotationZ()}°");
            return true;
        }

        // --- Event handlers ---

        private void OnProgressChanged(float percent)
        {
            int remaining = conveyor?.GetEstimatedRemainingSeconds() ?? -1;
            progressHud.UpdateProgress(percent, remaining);
        }

        private void CleanupConveyor()
        {
            if (conveyor != null)
            {
                conveyor.OnProgressChanged -= OnProgressChanged;
                conveyor.OnCompleted -= OnChiselingCompleted;
                conveyor = null;
            }
        }

        private void OnChiselingCompleted()
        {
            progressHud.Hide();
            ClearHighlights();
            CleanupConveyor();
            hologram.SetVisible(false);
            previewMode = false;
            storage = null; // full reset — no more RMB hologram after completion
        }

        // --- Highlighting ---

        private void HiLightModelBlocks(List<BlockPos> blockPosList)
        {
            var colors = new List<int> { ColorUtil.ToRgba(100, 0, 250, 0) };
            capi.World.HighlightBlocks(capi.World.Player, hLMBID, blockPosList, colors,
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
        }

        private void HiLightModelDimension(List<BlockPos> blockPosList)
        {
            var colors = new List<int> { ColorUtil.ToRgba(100, 0, 0, 250) };
            capi.World.HighlightBlocks(capi.World.Player, hLMDID, blockPosList, colors,
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
        }

        private void ClearHighlights()
        {
            capi.World.HighlightBlocks(capi.World.Player, hLMDID, new List<BlockPos>(),
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
            capi.World.HighlightBlocks(capi.World.Player, hLMBID, new List<BlockPos>(),
                EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
        }
    }
}
