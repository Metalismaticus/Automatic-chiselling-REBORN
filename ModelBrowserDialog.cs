using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using SysPath = System.IO.Path;
using SysIO = System.IO;

namespace AutomaticChiselling
{
    public class ModelInfo
    {
        public string FileName;
        public long FileSize;
        public bool StatsLoaded;
        public int TotalVoxels;
        public int BlockCount;
        public int ChiselOps;
        public float ChiselsNeeded;
        public int SizeX, SizeY, SizeZ;
        public int InteriorFilled;
        public string TimeEstimateSP;
        public LoadedTexture PreviewTexture;

        // Palette info (for colored models)
        public bool IsColored;
        public List<byte> UsedPaletteIndices = new List<byte>();
        public byte[][] Palette;
        public MaterialMapping MaterialMapping = new MaterialMapping();
    }

    public class ModelBrowserDialog : GuiDialog
    {
        private const int CHISEL_DURABILITY = 6000;
        private const float OPS_PER_SEC_SP = 120f;

        private const int DLG_W = 820;
        private const int INSET_H = 550;
        private const int ROW_H = 140;
        private const int TAB_H = 30;

        private List<ModelInfo> models = new List<ModelInfo>();
        private Action<string, MaterialMapping> onModelSelected;
        private Action<VoxelsStorage> onGeneratedModel;
        private int nextStatsIndex = 0;
        private long bgTickId = 0;
        private bool needsRecompose = false;
        private long lastRecomposeMs = 0;
        private const long RECOMPOSE_THROTTLE_MS = 400;
        private ElementBounds insetClipBounds;

        // Vertical offset of first model row (above which lives the loading banner, if any)
        private double rowsStartYOffset = 0;

        // Generators tab inset (used for positioning the preview texture in OnRenderGUI)
        private ElementBounds genInsetBounds;

        // Preview box bounds for Generators tab — properly parented to insetBounds
        // so the texture renders exactly inside the dark frame.
        private ElementBounds genPreviewBoxBounds;

        // Persisted material mappings keyed by model filename. Loaded from disk at
        // construction and re-saved whenever the user edits a mapping. Survives across
        // repeated Z presses AND game restarts.
        private Dictionary<string, MaterialMapping> sessionMaterialMappings
            = new Dictionary<string, MaterialMapping>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Loads persisted mappings from disk into sessionMaterialMappings.</summary>
        private void LoadPersistedMappings()
        {
            try
            {
                string path = ModPaths.MappingsFile;
                if (!SysIO.File.Exists(path)) return;
                string json = SysIO.File.ReadAllText(path);
                var loaded = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    Dictionary<string, MaterialMapping>>(json);
                if (loaded == null) return;
                sessionMaterialMappings.Clear();
                foreach (var kv in loaded)
                    if (kv.Value != null)
                        sessionMaterialMappings[kv.Key] = kv.Value;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[AutoChisel] Failed to load material mappings: " + e.Message);
            }
        }

        /// <summary>Serializes sessionMaterialMappings to disk.</summary>
        private void SavePersistedMappings()
        {
            try
            {
                string path = ModPaths.MappingsFile;
                SysIO.Directory.CreateDirectory(SysPath.GetDirectoryName(path));
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    sessionMaterialMappings, Newtonsoft.Json.Formatting.Indented);
                SysIO.File.WriteAllText(path, json);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[AutoChisel] Failed to save material mappings: " + e.Message);
            }
        }

        // Current tab: 0 = Models, 1 = Generators, 2 = Import, 3 = Settings
        private int currentTab = 0;

        // Import tab state
        private List<string> importObjFiles = new List<string>();
        private int importSelectedIdx = -1;
        private readonly VoxelizeSettings importSettings = new VoxelizeSettings
        {
            Resolution = 64, FillInterior = true, FlipY = false, SwapYZ = false
        };
        private GeneratedShape importPreviewShape;
        private LoadedTexture importPreviewTexture;
        private string importStatus = "";
        private bool importSaveAsVox = true;
        private ElementBounds importPreviewBoxBounds;
        private ElementBounds importInsetBounds;
        private string importLastBuiltFor = ""; // "path|res|fill|flipY|swapYZ" — skip voxelization if unchanged

        // 3D preview camera (yaw/pitch in radians). Default to a three-quarter iso view.
        private double importPreviewYaw = 30 * Math.PI / 180.0;
        private double importPreviewPitch = 20 * Math.PI / 180.0;
        private string importTextureKey = ""; // cache: textures depend on voxelized shape AND camera angles

        // Mouse-drag rotation state (Import tab only)
        private bool importDragging = false;
        private double importDragStartX, importDragStartY;
        private double importDragStartYaw, importDragStartPitch;
        private long importLastDragRebuildMs = 0;

        // 3D preview camera for Generators tab (same iso defaults as Import).
        private double genPreviewYaw = 30 * Math.PI / 180.0;
        private double genPreviewPitch = 20 * Math.PI / 180.0;
        private VoxelsStorage cachedGeneratorStorage; // last generated storage, used for camera-only re-renders
        private bool genDragging = false;
        private double genDragStartX, genDragStartY;
        private double genDragStartYaw, genDragStartPitch;
        private long genLastDragRebuildMs = 0;

        // Generator state
        private List<IShapeGenerator> generators = new List<IShapeGenerator>();
        private int selectedGenerator = 0;
        private Dictionary<string, object> generatorParams = new Dictionary<string, object>();
        private LoadedTexture generatorPreview;
        private bool saveGeneratedToVox = false;

        // Pre-computed stats for current generator settings
        private class GenStats
        {
            public int TotalVoxels, BlockCount, ChiselOps;
            public float ChiselsNeeded;
            public int SizeX, SizeY, SizeZ;
            public string TimeEstimate;
        }
        private GenStats genStats;

        public override string ToggleKeyCombinationCode => "autochisel_browser";

        public ModelBrowserDialog(ICoreClientAPI capi, Action<string, MaterialMapping> onSelected,
            Action<VoxelsStorage> onGenerated) : base(capi)
        {
            onModelSelected = onSelected;
            onGeneratedModel = onGenerated;
            LoadGenerators();
            LoadPersistedMappings();
        }

        private void LoadGenerators()
        {
            generators.Clear();
            generators.AddRange(BuiltinGenerators.GetAll());
            generators.AddRange(ScriptLoader.LoadUserGenerators(capi));
            ResetGeneratorParams();
        }

        private void ResetGeneratorParams()
        {
            generatorParams.Clear();
            if (generators.Count > 0 && selectedGenerator < generators.Count)
            {
                foreach (var p in generators[selectedGenerator].Parameters)
                    generatorParams[p.Id] = p.Default;
            }
        }

        /// <summary>Safely reads an int param (for UI rendering of sliders).</summary>
        private int ParamInt(string id, int fallback)
        {
            if (!generatorParams.TryGetValue(id, out var v) || v == null) return fallback;
            if (v is int i) return i;
            if (v is bool b) return b ? 1 : 0;
            int.TryParse(v.ToString(), out int parsed);
            return parsed;
        }

        private bool ParamBool(string id, bool fallback)
        {
            if (!generatorParams.TryGetValue(id, out var v) || v == null) return fallback;
            if (v is bool b) return b;
            if (v is int i) return i != 0;
            bool.TryParse(v.ToString(), out bool parsed);
            return parsed;
        }

        private string ParamString(string id, string fallback)
        {
            if (!generatorParams.TryGetValue(id, out var v) || v == null) return fallback;
            return v.ToString();
        }

        // ================================================================
        // Model scanning + background stats (same as before)
        // ================================================================

        private void ScanFileList()
        {
            foreach (var m in models) m.PreviewTexture?.Dispose();
            models.Clear();
            nextStatsIndex = 0;
            foreach (var file in ModPaths.GetAllVoxFiles())
            {
                try
                {
                    models.Add(new ModelInfo
                    {
                        FileName = SysPath.GetFileNameWithoutExtension(file),
                        FileSize = new SysIO.FileInfo(file).Length,
                        StatsLoaded = false, TimeEstimateSP = "..."
                    });
                }
                catch { }
            }
        }

        private void BackgroundComputeTick(float dt)
        {
            if (nextStatsIndex >= models.Count)
            {
                capi.World.UnregisterGameTickListener(bgTickId);
                bgTickId = 0;
                return;
            }
            var m = models[nextStatsIndex];
            try
            {
                var storage = new VoxelsStorage(m.FileName);
                if (storage.GetBlockCount() > 0)
                {
                    int ops = ChiselConveyor.CountChiselOperations(storage);
                    var dims = storage.GetModelDimensions();
                    m.TotalVoxels = storage.GetTotalVoxelCount();
                    m.BlockCount = storage.OriginalBlockCount;
                    m.ChiselOps = ops;
                    m.ChiselsNeeded = (float)Math.Ceiling(ops / (double)CHISEL_DURABILITY * 10) / 10f;
                    m.SizeX = dims.X; m.SizeY = dims.Y; m.SizeZ = dims.Z;
                    m.InteriorFilled = storage.InteriorVoxelsFilled;
                    m.TimeEstimateSP = FormatTime(ChiselConveyor.EstimateSeconds(capi, ops));
                    m.PreviewTexture = ModelPreview.CreatePreviewTexture(capi, storage);
                    m.IsColored = storage.IsColored;
                    m.UsedPaletteIndices = new List<byte>(storage.UsedPaletteIndices);
                    m.Palette = storage.Palette;
                    // Restore any color assignments the player already made this session.
                    if (sessionMaterialMappings.TryGetValue(m.FileName, out var cached))
                        m.MaterialMapping = cached.Clone();
                    m.StatsLoaded = true;
                }
            }
            catch { m.StatsLoaded = true; }
            nextStatsIndex++;
            needsRecompose = true;
        }

        static string FormatTime(int s)
        {
            if (s < 60) return $"~{Math.Max(1, s)} sec";
            if (s < 3600) return $"~{s / 60} min";
            int h = s / 3600, m2 = (s % 3600) / 60;
            return m2 > 0 ? $"~{h}h {m2}m" : $"~{h}h";
        }

        // ================================================================
        // GUI Setup — tabs + content
        // ================================================================

        private void SetupDialog()
        {
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            // Tab buttons above inset
            var tabBounds = ElementBounds.Fixed(0, GuiStyle.TitleBarHeight, DLG_W, TAB_H);
            double contentTop = GuiStyle.TitleBarHeight + TAB_H + 5;

            // Inset area for content
            var insetBounds = ElementBounds.Fixed(0, contentTop, DLG_W, INSET_H);
            var scrollbarBounds = insetBounds.RightCopy().WithFixedWidth(20);

            insetClipBounds = insetBounds.ForkContainingChild(
                GuiStyle.HalfPadding, GuiStyle.HalfPadding,
                GuiStyle.HalfPadding, GuiStyle.HalfPadding);
            var containerBounds = insetBounds.ForkContainingChild(
                GuiStyle.HalfPadding, GuiStyle.HalfPadding,
                GuiStyle.HalfPadding, GuiStyle.HalfPadding);

            // Open Folder button at bottom
            var folderBtnBounds = ElementBounds.Fixed(0, contentTop + INSET_H + 8, 140, 25);

            var bgBounds = ElementBounds.Fill
                .WithFixedPadding(GuiStyle.ElementToDialogPadding)
                .WithSizing(ElementSizing.FitToChildren)
                .WithChildren(tabBounds, insetBounds, scrollbarBounds, folderBtnBounds);

            var comp = capi.Gui.CreateCompo("modelbrowser", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("AutoChisel Browser", () => TryClose())
                .BeginChildElements();

            // Tab buttons
            var tab1Bounds = ElementBounds.Fixed(5,   GuiStyle.TitleBarHeight + 2, 120, TAB_H - 4);
            var tab2Bounds = ElementBounds.Fixed(130, GuiStyle.TitleBarHeight + 2, 120, TAB_H - 4);
            var tab3Bounds = ElementBounds.Fixed(255, GuiStyle.TitleBarHeight + 2, 120, TAB_H - 4);
            var tab4Bounds = ElementBounds.Fixed(380, GuiStyle.TitleBarHeight + 2, 120, TAB_H - 4);
            comp.AddSmallButton("Models", () => { SwitchTab(0); return true; },
                tab1Bounds, currentTab == 0 ? EnumButtonStyle.Normal : EnumButtonStyle.Small, "tabModels");
            comp.AddSmallButton("Generators", () => { SwitchTab(1); return true; },
                tab2Bounds, currentTab == 1 ? EnumButtonStyle.Normal : EnumButtonStyle.Small, "tabGenerators");
            comp.AddSmallButton("Import OBJ", () => { SwitchTab(2); return true; },
                tab3Bounds, currentTab == 2 ? EnumButtonStyle.Normal : EnumButtonStyle.Small, "tabImport");
            comp.AddSmallButton("Settings", () => { SwitchTab(3); return true; },
                tab4Bounds, currentTab == 3 ? EnumButtonStyle.Normal : EnumButtonStyle.Small, "tabSettings");

            // Content
            comp.AddInset(insetBounds, 3);

            // Open Folder button — on both tabs
            comp.AddSmallButton("Open Folder", OnOpenFolder,
                folderBtnBounds, EnumButtonStyle.Small, "btnOpenFolder");

            if (currentTab == 0)
            {
                // --- MODELS TAB ---
                int loadedCount = 0;
                foreach (var mm in models) if (mm.StatsLoaded) loadedCount++;
                bool showBanner = models.Count > 0 && loadedCount < models.Count;
                const int BANNER_H = 40;

                float scrollTotalH = Math.Max(models.Count, 1) * ROW_H + (showBanner ? BANNER_H : 0);

                comp.BeginClip(insetClipBounds)
                    .AddContainer(containerBounds, "scroll-content")
                    .EndClip()
                    .AddVerticalScrollbar(OnNewScrollbarValue, scrollbarBounds, "scrollbar")
                    .EndChildElements();

                // Add rows to container BEFORE Compose (VS DemoScrollingGui pattern)
                var scrollArea = comp.GetContainer("scroll-content");
                double rowStartY = 0;
                rowsStartYOffset = 0;

                if (models.Count == 0)
                {
                    var emptyBounds = ElementBounds.Fixed(0, 0, DLG_W - 25, ROW_H);
                    scrollArea.Add(new GuiElementStaticText(capi,
                        "No .vox models found.", EnumTextOrientation.Center,
                        emptyBounds, CairoFont.WhiteSmallText()));
                }
                else
                {
                    // Loading banner at the top — visible while background stats are loading
                    if (showBanner)
                    {
                        var bannerBounds = ElementBounds.Fixed(0, 0, DLG_W - 25, BANNER_H - 4);
                        float progressPct = loadedCount / (float)models.Count;
                        int capturedLoaded = loadedCount;
                        int capturedTotal = models.Count;
                        scrollArea.Add(new GuiElementCustomDraw(capi, bannerBounds, (ctx, surface, b) =>
                        {
                            // Background
                            ctx.SetSourceRGBA(0.22, 0.2, 0.14, 0.9);
                            RoundRect(ctx, b.drawX + 2, b.drawY + 2, b.InnerWidth - 4, b.InnerHeight - 4, 4);
                            ctx.Fill();
                            ctx.SetSourceRGBA(0.5, 0.42, 0.22, 0.5);
                            RoundRect(ctx, b.drawX + 2, b.drawY + 2, b.InnerWidth - 4, b.InnerHeight - 4, 4);
                            ctx.LineWidth = 1; ctx.Stroke();
                            // Progress bar
                            double barY = b.drawY + b.InnerHeight - 8;
                            double barMaxW = b.InnerWidth - 12;
                            ctx.SetSourceRGBA(0.3, 0.3, 0.3, 0.6);
                            RoundRect(ctx, b.drawX + 6, barY, barMaxW, 4, 2); ctx.Fill();
                            ctx.SetSourceRGBA(0.55, 0.85, 0.55, 0.95);
                            RoundRect(ctx, b.drawX + 6, barY, barMaxW * progressPct, 4, 2); ctx.Fill();
                            // Text
                            ctx.SetSourceRGBA(0.95, 0.88, 0.7, 1);
                            ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
                            ctx.SetFontSize(GuiElement.scaled(13));
                            string label = $"Loading model stats...  {capturedLoaded} / {capturedTotal}";
                            var ext = ctx.TextExtents(label);
                            ctx.MoveTo(b.drawX + (b.InnerWidth - ext.Width) / 2, b.drawY + 18);
                            ctx.ShowText(label);
                        }));
                        rowStartY = BANNER_H;
                        rowsStartYOffset = BANNER_H;
                    }

                    var rowBounds = ElementBounds.Fixed(0, rowStartY, DLG_W - 25, ROW_H);
                    for (int i = 0; i < models.Count; i++)
                        AddModelRow(scrollArea, models[i], i, ref rowBounds);
                }

                // Single Compose
                SingleComposer = comp.Compose();
                SingleComposer.GetScrollbar("scrollbar")?.SetHeights((float)INSET_H, scrollTotalH);
                OnNewScrollbarValue(0);
            }
            else if (currentTab == 3)
            {
                // --- SETTINGS TAB ---
                BuildSettingsTab(comp, insetBounds);
                SingleComposer = comp.Compose();
            }
            else if (currentTab == 2)
            {
                // --- IMPORT TAB (OBJ → voxel) ---
                BuildImportTab(comp, insetBounds);
                SingleComposer = comp.Compose();
            }
            else
            {
                // --- GENERATORS TAB ---
                // Same structure as Models tab: clip + container + scrollbar — consistent UX.
                comp.BeginClip(insetClipBounds)
                    .AddContainer(containerBounds, "gen-content")
                    .EndClip()
                    .AddVerticalScrollbar(OnGenScrollChanged, scrollbarBounds, "gen-scrollbar");

                // Preview box — properly parented to insetBounds so it always stays inside
                // the inset's visible area (ForkContainingChild sets parent + enforces margins).
                // Layout: 340x340 box at right side of inset, 10px from top/right, centered-ish.
                const int PV_SIZE = 330;
                int rightMargin = 20;
                int topMargin = 10;
                int leftMargin = DLG_W - PV_SIZE - rightMargin;
                int bottomMargin = INSET_H - topMargin - PV_SIZE;
                genPreviewBoxBounds = insetBounds.ForkContainingChild(
                    leftMargin, topMargin, rightMargin, bottomMargin);

                comp.AddStaticCustomDraw(genPreviewBoxBounds, (ctx, s, b) =>
                {
                    ctx.SetSourceRGBA(0.08, 0.08, 0.1, 0.85);
                    RoundRect(ctx, b.drawX, b.drawY, b.InnerWidth, b.InnerHeight, 4);
                    ctx.Fill();
                    ctx.SetSourceRGBA(0.3, 0.32, 0.35, 0.4);
                    RoundRect(ctx, b.drawX, b.drawY, b.InnerWidth, b.InnerHeight, 4);
                    ctx.LineWidth = 1; ctx.Stroke();
                });

                comp.EndChildElements();

                UpdateGeneratorPreview();
                genInsetBounds = insetBounds;

                var genArea = comp.GetContainer("gen-content");
                int contentH = BuildGeneratorUI(genArea);

                SingleComposer = comp.Compose();
                ApplyPendingTextInputValues();

                SingleComposer.GetScrollbar("gen-scrollbar")?.SetHeights((float)INSET_H, contentH);
                OnGenScrollChanged(0);
            }
        }

        private void OnGenScrollChanged(float value)
        {
            var container = SingleComposer?.GetContainer("gen-content");
            if (container == null) return;
            container.Bounds.fixedY = 3 - value;
            container.Bounds.CalcWorldBounds();
        }

        // ================================================================
        // Models tab — row builder (same pattern as before)
        // ================================================================

        private void AddModelRow(GuiElementContainer scrollArea, ModelInfo m, int idx, ref ElementBounds rowBounds)
        {
            var cardBounds = rowBounds.FlatCopy();
            var previewTex = m.PreviewTexture;
            scrollArea.Add(new GuiElementCustomDraw(capi, cardBounds, (ctx, surface, b) =>
            {
                ctx.SetSourceRGBA(0.18, 0.18, 0.2, 0.7);
                RoundRect(ctx, b.drawX + 2, b.drawY + 1, b.InnerWidth - 4, b.InnerHeight - 3, 3);
                ctx.Fill();
                ctx.SetSourceRGBA(0.4, 0.42, 0.45, 0.4);
                RoundRect(ctx, b.drawX + 2, b.drawY + 1, b.InnerWidth - 4, b.InnerHeight - 3, 3);
                ctx.LineWidth = 1; ctx.Stroke();

                double pvX = b.drawX + 6, pvY = b.drawY + 6, pvS = b.InnerHeight - 12;
                ctx.SetSourceRGBA(0.1, 0.1, 0.12, 0.9);
                RoundRect(ctx, pvX, pvY, pvS, pvS, 3); ctx.Fill();
                if (previewTex == null || previewTex.TextureId == 0)
                {
                    ctx.SetSourceRGBA(0.35, 0.35, 0.4, 0.7);
                    ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
                    ctx.SetFontSize(GuiElement.scaled(10));
                    var ext = ctx.TextExtents(".vox");
                    ctx.MoveTo(pvX + (pvS - ext.Width) / 2, pvY + (pvS + ext.Height) / 2);
                    ctx.ShowText(".vox");
                }
            }));

            // Layout (symmetric gaps). Card width is DLG_W-25 (scrollbar takes 25px on right).
            //   preview  [128]  ←26→  text  ←26→  buttons  [85]  ←20→ card edge
            //   x=6..134       160..664           690..775            775..795
            // Buttons live at x=DLG_W-130 (=690), 85 wide, ending at DLG_W-45 (=775).
            // Card right edge is DLG_W-25 (=795), so button → edge gap = 20px.
            double tx = 160, tw = DLG_W - tx - 156;

            // Line 1 — filename (bold, white). Truncated so timestamp-style
            // file names don't run under the Load button.
            scrollArea.Add(new GuiElementStaticText(capi, $"{TruncateName(m.FileName)}.vox",
                EnumTextOrientation.Left, ElementBounds.Fixed(tx, rowBounds.fixedY + 10, tw, 22),
                CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold)));

            if (m.StatsLoaded)
            {
                // Line 2 — dimensions (blue)
                scrollArea.Add(new GuiElementStaticText(capi,
                    $"{m.SizeX} × {m.SizeZ} × {m.SizeY}  voxels",
                    EnumTextOrientation.Left, ElementBounds.Fixed(tx, rowBounds.fixedY + 36, tw, 16),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.55, 0.78, 0.98, 1 })));

                // Line 3 — chisel-steel + blocks (gold)
                scrollArea.Add(new GuiElementStaticText(capi,
                    $"{m.ChiselsNeeded:F1} chisel-steel   ·   {m.BlockCount:N0} blocks",
                    EnumTextOrientation.Left, ElementBounds.Fixed(tx, rowBounds.fixedY + 58, tw, 16),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.97, 0.85, 0.45, 1 })));

                // Line 4 — time + ops (green)
                string opsLine = $"{m.TimeEstimateSP}   ·   {m.ChiselOps:N0} ops   ·   {m.TotalVoxels:N0} voxels";
                if (m.InteriorFilled > 0) opsLine += $"   (−{m.InteriorFilled:N0} interior)";
                scrollArea.Add(new GuiElementStaticText(capi, opsLine,
                    EnumTextOrientation.Left, ElementBounds.Fixed(tx, rowBounds.fixedY + 80, tw, 16),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.55, 0.9, 0.55, 1 })));

                // Line 5 — palette swatches + counter (only for colored models)
                if (m.IsColored && m.UsedPaletteIndices != null && m.UsedPaletteIndices.Count > 0)
                {
                    int assigned = m.MaterialMapping?.AssignedCount ?? 0;
                    int total = m.UsedPaletteIndices.Count;

                    var paletteCopy = m.Palette;
                    var usedCopy = new List<byte>(m.UsedPaletteIndices);

                    // Swatches on the left
                    int swatchCount = Math.Min(usedCopy.Count, 16);
                    double swatchSize = 14;
                    double gap = 3;
                    double swatchesWidth = swatchCount * (swatchSize + gap);

                    var paletteBounds = ElementBounds.Fixed(tx, rowBounds.fixedY + 108, swatchesWidth, swatchSize);
                    scrollArea.Add(new GuiElementCustomDraw(capi, paletteBounds, (ctx, surface, b) =>
                    {
                        for (int i = 0; i < swatchCount; i++)
                        {
                            byte pIdx = usedCopy[i];
                            double sx = b.drawX + i * (swatchSize + gap);
                            double sy = b.drawY;
                            if (paletteCopy != null && pIdx < paletteCopy.Length && paletteCopy[pIdx] != null)
                            {
                                var c = paletteCopy[pIdx];
                                ctx.SetSourceRGBA(c[0] / 255.0, c[1] / 255.0, c[2] / 255.0, 1);
                            }
                            else ctx.SetSourceRGBA(0.5, 0.5, 0.5, 1);
                            RoundRect(ctx, sx, sy, swatchSize, swatchSize, 2);
                            ctx.Fill();
                            ctx.SetSourceRGBA(0.0, 0.0, 0.0, 0.9);
                            RoundRect(ctx, sx, sy, swatchSize, swatchSize, 2);
                            ctx.LineWidth = 1; ctx.Stroke();
                        }
                    }));

                    // Counter label to the right of swatches
                    double labelX = tx + swatchesWidth + 10;
                    string counter = $"{assigned} / {total} assigned";
                    var counterColor = assigned == total && total > 0
                        ? new double[] { 0.55, 0.9, 0.65, 1 }   // green if all assigned
                        : new double[] { 0.75, 0.72, 0.85, 1 }; // muted purple otherwise
                    scrollArea.Add(new GuiElementStaticText(capi, counter,
                        EnumTextOrientation.Left, ElementBounds.Fixed(labelX, rowBounds.fixedY + 108, 200, 16),
                        CairoFont.WhiteDetailText().WithColor(counterColor)));
                }
            }
            else
            {
                scrollArea.Add(new GuiElementStaticText(capi,
                    $"Loading stats… ({m.FileSize / 1024} KB)", EnumTextOrientation.Left,
                    ElementBounds.Fixed(tx, rowBounds.fixedY + 50, tw, 16),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.5, 0.5, 0.5, 1 })));
            }

            // Right-side buttons — sit inside the card with a 25px margin from card edge.
            // For colored models: Load (top) + Colors (bottom), centered as a pair.
            // For mono-color models: only Load, vertically centered in the card.
            // While stats are loading, no buttons are shown — prevents clicking on rows whose
            // data isn't ready, and avoids the jitter from repeated recomposes.
            int capturedIdx = idx;
            double btnX = DLG_W - 130;   // Card right edge is DLG_W-25, button width 85, margin 20 → 25+20+85=130
            double btnW = 85, btnH = 32;

            if (m.StatsLoaded)
            {
                if (m.IsColored)
                {
                    // Two buttons stacked, centered vertically (ROW_H=140, total height 32+12+32=76 → top at y=32)
                    double topY = (ROW_H - (btnH * 2 + 12)) / 2.0;
                    scrollArea.Add(new GuiElementTextButton(capi, "Load", CairoFont.ButtonText(),
                        CairoFont.ButtonPressedText(), () => { OnSelectModel(capturedIdx); return true; },
                        ElementBounds.Fixed(btnX, rowBounds.fixedY + topY, btnW, btnH), EnumButtonStyle.Small));
                    scrollArea.Add(new GuiElementTextButton(capi, "Colors", CairoFont.ButtonText(),
                        CairoFont.ButtonPressedText(), () => { OpenMaterialAssignment(capturedIdx); return true; },
                        ElementBounds.Fixed(btnX, rowBounds.fixedY + topY + btnH + 12, btnW, btnH), EnumButtonStyle.Small));
                }
                else
                {
                    // Single button centered
                    double centerY = (ROW_H - btnH) / 2.0;
                    scrollArea.Add(new GuiElementTextButton(capi, "Load", CairoFont.ButtonText(),
                        CairoFont.ButtonPressedText(), () => { OnSelectModel(capturedIdx); return true; },
                        ElementBounds.Fixed(btnX, rowBounds.fixedY + centerY, btnW, btnH), EnumButtonStyle.Small));
                }
            }
            else
            {
                // Subtle "pending" label where Load button would go — keeps the column alignment
                double centerY = (ROW_H - 20) / 2.0;
                scrollArea.Add(new GuiElementStaticText(capi, "—", EnumTextOrientation.Center,
                    ElementBounds.Fixed(btnX, rowBounds.fixedY + centerY, btnW, 20),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.4, 0.4, 0.45, 0.8 })));
            }

            rowBounds = rowBounds.BelowCopy(fixedDeltaY: 0);
        }

        // ================================================================
        // Generators tab — UI builder
        // ================================================================

        // TextInput references — SetValue must run after Compose.
        private readonly List<(GuiElementTextInput input, string value)> pendingTextInputValues
            = new List<(GuiElementTextInput, string)>();

        /// <summary>
        /// Populate the generator UI inside a scroll container. Returns the total pixel height
        /// of the content (for scrollbar SetHeights).
        /// </summary>
        private int BuildGeneratorUI(GuiElementContainer area)
        {
            if (generators.Count == 0)
            {
                area.Add(new GuiElementStaticText(capi, "No generators available.",
                    EnumTextOrientation.Center, ElementBounds.Fixed(0, 20, DLG_W - 30, 25),
                    CairoFont.WhiteSmallText()));
                return 50;
            }

            const double L = 15;      // left margin
            const double R = 440;     // right column (preview)
            const double pvW = 340;
            const double ROW = 38;
            double y = 15;

            var gen = generators[selectedGenerator];
            pendingTextInputValues.Clear();

            // ---- Shape: < Name > ----
            area.Add(new GuiElementTextButton(capi, "<",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { selectedGenerator = (selectedGenerator - 1 + generators.Count) % generators.Count; ResetGeneratorParams(); SetupDialog(); return true; },
                ElementBounds.Fixed(L, y, 32, 28), EnumButtonStyle.Small));

            area.Add(new GuiElementStaticText(capi, gen.Name,
                EnumTextOrientation.Center, ElementBounds.Fixed(L + 42, y + 5, 150, 22),
                CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold)));

            area.Add(new GuiElementTextButton(capi, ">",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { selectedGenerator = (selectedGenerator + 1) % generators.Count; ResetGeneratorParams(); SetupDialog(); return true; },
                ElementBounds.Fixed(L + 202, y, 32, 28), EnumButtonStyle.Small));
            y += 40;

            // ---- Description ----
            area.Add(new GuiElementStaticText(capi, gen.Description,
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y, 400, 18),
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.6, 0.7, 0.8, 0.8 })));
            y += 28;

            // ---- Parameters ----
            foreach (var param in gen.Parameters)
            {
                string pId = param.Id;

                area.Add(new GuiElementStaticText(capi, param.Label,
                    EnumTextOrientation.Left, ElementBounds.Fixed(L, y + 6, 170, 20),
                    CairoFont.WhiteSmallishText()));

                if (param.Type == ParameterType.Checkbox)
                {
                    bool val = ParamBool(pId, param.Default is bool bd && bd);
                    area.Add(new GuiElementTextButton(capi, val ? "ON" : "OFF",
                        CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                        () => { generatorParams[pId] = !ParamBool(pId, false); SetupDialog(); return true; },
                        ElementBounds.Fixed(L + 180, y, 55, 28), EnumButtonStyle.Small));
                }
                else if (param.Type == ParameterType.Dropdown)
                {
                    string cur = ParamString(pId, "");
                    string[] vals = param.DropdownValues ?? new[] { "(none)" };
                    int idx = Array.IndexOf(vals, cur);
                    if (idx < 0) { idx = 0; if (vals.Length > 0) generatorParams[pId] = vals[0]; }

                    int ic = idx, lc = vals.Length;
                    string[] vc = vals;
                    area.Add(new GuiElementTextButton(capi, "<",
                        CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                        () => { generatorParams[pId] = vc[(ic - 1 + lc) % lc]; SetupDialog(); return true; },
                        ElementBounds.Fixed(L + 180, y, 28, 28), EnumButtonStyle.Small));
                    area.Add(new GuiElementStaticText(capi, vals[idx],
                        EnumTextOrientation.Center, ElementBounds.Fixed(L + 212, y + 6, 120, 20),
                        CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold)));
                    area.Add(new GuiElementTextButton(capi, ">",
                        CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                        () => { generatorParams[pId] = vc[(ic + 1) % lc]; SetupDialog(); return true; },
                        ElementBounds.Fixed(L + 336, y, 28, 28), EnumButtonStyle.Small));
                }
                else if (param.Type == ParameterType.TextInput)
                {
                    // Can't use inline GuiElementTextInput inside a scroll container —
                    // composer focus routing doesn't work. Use a modal popup instead:
                    // show current value as a clickable button, clicking opens
                    // GuiDialogTextInput where the user can paste/edit freely.
                    string current = ParamString(pId, param.Default as string ?? "");
                    string display = current;
                    if (string.IsNullOrEmpty(display)) display = "(click to edit)";
                    // Narrower than before — the 280px button used to overlap the
                    // preview box on the right. 235px fits cleanly with ~10px gap.
                    if (display.Length > 22) display = display.Substring(0, 21) + "…";

                    string capturedPId = pId;
                    string capturedLabel = param.Label;
                    area.Add(new GuiElementTextButton(capi, display,
                        CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                        () => { OpenTextInputPopup(capturedPId, capturedLabel); return true; },
                        ElementBounds.Fixed(L + 180, y, 235, 28), EnumButtonStyle.Small));
                }
                else // Slider
                {
                    int val = ParamInt(pId, param.Default is int di ? di : param.Min);
                    int pMin = param.Min, pMax = param.Max;
                    area.Add(new GuiElementTextButton(capi, "-",
                        CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                        () => { generatorParams[pId] = Math.Max(pMin, ParamInt(pId, pMin) - 1); SetupDialog(); return true; },
                        ElementBounds.Fixed(L + 180, y, 32, 28), EnumButtonStyle.Small));
                    area.Add(new GuiElementStaticText(capi, val.ToString(),
                        EnumTextOrientation.Center, ElementBounds.Fixed(L + 216, y + 6, 48, 20),
                        CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold)));
                    area.Add(new GuiElementTextButton(capi, "+",
                        CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                        () => { generatorParams[pId] = Math.Min(pMax, ParamInt(pId, pMin) + 1); SetupDialog(); return true; },
                        ElementBounds.Fixed(L + 268, y, 32, 28), EnumButtonStyle.Small));
                }
                y += ROW;
            }

            // ---- Separator before action area ----
            y += 8;
            area.Add(new GuiElementCustomDraw(capi, ElementBounds.Fixed(L, y, 400, 1), (ctx, s, b) =>
            {
                ctx.SetSourceRGBA(0.4, 0.4, 0.45, 0.35);
                ctx.MoveTo(b.drawX, b.drawY); ctx.LineTo(b.drawX + b.InnerWidth, b.drawY);
                ctx.LineWidth = 1; ctx.Stroke();
            }));
            y += 10;

            // ---- Save-to-.vox checkbox ----
            area.Add(new GuiElementStaticText(capi, "Save as .vox",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y + 6, 160, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, saveGeneratedToVox ? "ON" : "OFF",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { saveGeneratedToVox = !saveGeneratedToVox; SetupDialog(); return true; },
                ElementBounds.Fixed(L + 180, y, 55, 28), EnumButtonStyle.Small));
            y += 40;

            // ---- Generate button ----
            area.Add(new GuiElementTextButton(capi,
                saveGeneratedToVox ? "Generate · Save · Load" : "Generate & Load",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { OnGenerate(); return true; },
                ElementBounds.Fixed(L, y, 240, 34), EnumButtonStyle.Normal));
            y += 50;

            // ---- Separator line ----
            area.Add(new GuiElementCustomDraw(capi, ElementBounds.Fixed(L, y, 400, 1), (ctx, s, b) =>
            {
                ctx.SetSourceRGBA(0.4, 0.4, 0.45, 0.4);
                ctx.MoveTo(b.drawX, b.drawY); ctx.LineTo(b.drawX + b.InnerWidth, b.drawY);
                ctx.LineWidth = 1; ctx.Stroke();
            }));
            y += 12;

            // ---- Stats ----
            if (genStats != null)
            {
                area.Add(new GuiElementStaticText(capi,
                    $"{genStats.SizeX} x {genStats.SizeZ} x {genStats.SizeY}  voxels",
                    EnumTextOrientation.Left, ElementBounds.Fixed(L, y, 400, 18),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.6, 0.78, 0.95, 1 })));
                y += 22;
                area.Add(new GuiElementStaticText(capi,
                    $"{genStats.ChiselsNeeded:F1} chisel-steel          {genStats.BlockCount} blocks",
                    EnumTextOrientation.Left, ElementBounds.Fixed(L, y, 400, 18),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.95, 0.87, 0.5, 1 })));
                y += 22;
                area.Add(new GuiElementStaticText(capi,
                    $"{genStats.TimeEstimate}          {genStats.ChiselOps:N0} ops",
                    EnumTextOrientation.Left, ElementBounds.Fixed(L, y, 400, 18),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.5, 0.88, 0.5, 1 })));
                y += 22;
                area.Add(new GuiElementStaticText(capi,
                    $"{genStats.TotalVoxels:N0} voxels total",
                    EnumTextOrientation.Left, ElementBounds.Fixed(L, y, 400, 16),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.5, 0.55, 0.6, 0.8 })));
                y += 22;
            }
            else
            {
                area.Add(new GuiElementStaticText(capi, "Computing stats...",
                    EnumTextOrientation.Left, ElementBounds.Fixed(L, y, 400, 18),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.5, 0.5, 0.5, 0.7 })));
                y += 22;
            }

            // Preview box is added at composer level in SetupDialog (stays fixed when scrolling).

            return (int)(y + 10);
        }

        /// <summary>
        /// Opens a modal popup for editing a long string parameter. The popup
        /// has its own composer with a proper interactive TextInput, so keyboard
        /// focus / paste / edit work reliably even though our main dialog has a
        /// scroll container that normally swallows keyboard events.
        /// </summary>
        private void OpenTextInputPopup(string paramId, string label)
        {
            string current = ParamString(paramId, "");
            var popup = new TextInputPopup(capi, label, current);
            popup.OnSave = text =>
            {
                generatorParams[paramId] = text ?? "";
                UpdateGeneratorPreview();
                needsRecompose = true;
            };
            popup.TryOpen();
        }

        /// <summary>
        /// After SingleComposer.Compose() runs, set initial values on TextInputs.
        /// </summary>
        private void ApplyPendingTextInputValues()
        {
            foreach (var (input, value) in pendingTextInputValues)
            {
                try { input.SetValue(value); } catch { }
            }
            pendingTextInputValues.Clear();
        }

        // ================================================================
        // Actions
        // ================================================================

        private void SwitchTab(int tab)
        {
            currentTab = tab;
            SetupDialog();
        }

        /// <summary>
        /// Renders the Settings tab — separate SP / MP speed caps + adaptive toggle.
        /// Uses a child container placed inside the inset so layout is consistent
        /// with how Generators tab builds its UI.
        /// </summary>
        /// <summary>
        /// Renders the Import tab — scan obj_imports/ folder, pick a .obj, tune
        /// voxelization params, preview the result, then generate & load like any
        /// other model. Kd diffuse colors drive the palette; textures are ignored
        /// in v1 for cross-platform compatibility (no System.Drawing.Common).
        /// </summary>
        private void BuildImportTab(GuiComposer comp, ElementBounds insetBounds)
        {
            importInsetBounds = insetBounds;
            RescanObjFolder();

            // --- Preview box on the RIGHT side of the inset (like Generators tab) ---
            const int PV_SIZE = 330;
            int rightMargin = 20;
            int topMargin = 10;
            int leftMargin = DLG_W - PV_SIZE - rightMargin;
            int bottomMargin = INSET_H - topMargin - PV_SIZE;
            importPreviewBoxBounds = insetBounds.ForkContainingChild(
                leftMargin, topMargin, rightMargin, bottomMargin);

            comp.AddStaticCustomDraw(importPreviewBoxBounds, (ctx, s, b) =>
            {
                ctx.SetSourceRGBA(0.08, 0.08, 0.1, 0.85);
                RoundRect(ctx, b.drawX, b.drawY, b.InnerWidth, b.InnerHeight, 4);
                ctx.Fill();
                ctx.SetSourceRGBA(0.3, 0.32, 0.35, 0.4);
                RoundRect(ctx, b.drawX, b.drawY, b.InnerWidth, b.InnerHeight, 4);
                ctx.LineWidth = 1; ctx.Stroke();
            });

            // --- Controls container on the LEFT side ---
            // leftPad=20; rightPad reserves the preview column + a 16px gap.
            var area2 = insetBounds.ForkContainingChild(20, 12, PV_SIZE + rightMargin + 16, 12);
            comp.AddContainer(area2, "import-content");
            comp.EndChildElements();
            var area = comp.GetContainer("import-content");

            // Clamp file selection BEFORE firing the preview rebuild — otherwise
            // on the first tab open selectedIdx == -1 and the rebuild is a no-op,
            // so the preview box sits empty until the user toggles something.
            if (importObjFiles.Count > 0 &&
                (importSelectedIdx < 0 || importSelectedIdx >= importObjFiles.Count))
            {
                importSelectedIdx = 0;
            }

            // Eagerly rebuild the preview so it appears as soon as the tab opens or
            // a settings toggle is flipped. Voxelization at res≈64 on modest meshes
            // is sub-second; the key-signature check below skips redundant work.
            RebuildImportPreviewIfNeeded();

            // Layout mirrors the Generators tab (L=15 margin, labelW=170) so rows
            // end well before the preview column and nothing wraps onto the picture.
            const double L = 15;
            const double ROW = 38;
            const double labelW = 170;
            const double ctrlX = L + labelW + 10; // same offset Generators uses
            // Max width of multi-line text (description, status) in the left column.
            const double CTRL_W = 400;
            double y = 0;

            // --- Header ---
            area.Add(new GuiElementStaticText(capi, "Import .obj → voxel",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y, 400, 22),
                CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold)));
            y += 26;

            area.Add(new GuiElementStaticText(capi,
                "Drop .obj + .mtl files into autochisel/obj_imports/. Colors come from the material Kd values. Textures (map_Kd) are not sampled in this version.",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y, CTRL_W, 52),
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.65, 0.75, 0.85, 0.85 })));
            y += 58;

            // --- File selector ---
            if (importObjFiles.Count == 0)
            {
                area.Add(new GuiElementStaticText(capi,
                    "No .obj files found in autochisel/obj_imports/.",
                    EnumTextOrientation.Left, ElementBounds.Fixed(L, y, CTRL_W, 20),
                    CairoFont.WhiteSmallishText().WithColor(new double[] { 1.0, 0.75, 0.5, 1.0 })));
                y += 28;
                area.Add(new GuiElementTextButton(capi, "Open Folder",
                    CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                    () => { OpenObjFolder(); return true; },
                    ElementBounds.Fixed(L, y, 140, 28), EnumButtonStyle.Small));
                return;
            }

            if (importSelectedIdx < 0 || importSelectedIdx >= importObjFiles.Count)
                importSelectedIdx = 0;
            string selName = System.IO.Path.GetFileName(importObjFiles[importSelectedIdx]);

            area.Add(new GuiElementStaticText(capi, "File",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            // Filename row: ["<"] [centered name, truncated if long] [">"]
            // Strip ".obj" then suffix-truncate to keep the row from wrapping
            // into the next field. The 170-wide bounds comfortably fits the
            // 20-char + "..." form at every GUI scale.
            string displayName = selName;
            if (!string.IsNullOrEmpty(displayName)
                && displayName.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                displayName = displayName.Substring(0, displayName.Length - 4);
            displayName = TruncateName(displayName);

            area.Add(new GuiElementTextButton(capi, "<",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => {
                    importSelectedIdx = (importSelectedIdx - 1 + importObjFiles.Count) % importObjFiles.Count;
                    InvalidateImportPreview(); SetupDialog(); return true;
                },
                ElementBounds.Fixed(ctrlX, y, 32, 28), EnumButtonStyle.Small));
            area.Add(new GuiElementStaticText(capi, displayName ?? "",
                EnumTextOrientation.Center, ElementBounds.Fixed(ctrlX + 36, y + 6, 170, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, ">",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => {
                    importSelectedIdx = (importSelectedIdx + 1) % importObjFiles.Count;
                    InvalidateImportPreview(); SetupDialog(); return true;
                },
                ElementBounds.Fixed(ctrlX + 210, y, 32, 28), EnumButtonStyle.Small));
            y += ROW;

            // --- Resolution slider ---
            area.Add(new GuiElementStaticText(capi, "Resolution (voxels)",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, "-",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => {
                    importSettings.Resolution = Math.Max(8, importSettings.Resolution - 8);
                    InvalidateImportPreview(); SetupDialog(); return true;
                },
                ElementBounds.Fixed(ctrlX, y, 32, 28), EnumButtonStyle.Small));
            area.Add(new GuiElementStaticText(capi, importSettings.Resolution.ToString(),
                EnumTextOrientation.Center, ElementBounds.Fixed(ctrlX + 36, y + 6, 48, 20),
                CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold)));
            area.Add(new GuiElementTextButton(capi, "+",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => {
                    importSettings.Resolution = Math.Min(256, importSettings.Resolution + 8);
                    InvalidateImportPreview(); SetupDialog(); return true;
                },
                ElementBounds.Fixed(ctrlX + 88, y, 32, 28), EnumButtonStyle.Small));
            y += ROW;

            // --- Fill interior toggle ---
            area.Add(new GuiElementStaticText(capi, "Fill interior",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, importSettings.FillInterior ? "ON" : "OFF",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => {
                    importSettings.FillInterior = !importSettings.FillInterior;
                    InvalidateImportPreview(); SetupDialog(); return true;
                },
                ElementBounds.Fixed(ctrlX, y, 55, 28), EnumButtonStyle.Small));
            y += ROW;

            // --- Flip Y toggle ---
            area.Add(new GuiElementStaticText(capi, "Flip Y axis",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, importSettings.FlipY ? "ON" : "OFF",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => {
                    importSettings.FlipY = !importSettings.FlipY;
                    InvalidateImportPreview(); SetupDialog(); return true;
                },
                ElementBounds.Fixed(ctrlX, y, 55, 28), EnumButtonStyle.Small));
            y += ROW;

            // --- Swap Y/Z toggle ---
            area.Add(new GuiElementStaticText(capi, "Swap Y ↔ Z axes",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, importSettings.SwapYZ ? "ON" : "OFF",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => {
                    importSettings.SwapYZ = !importSettings.SwapYZ;
                    InvalidateImportPreview(); SetupDialog(); return true;
                },
                ElementBounds.Fixed(ctrlX, y, 55, 28), EnumButtonStyle.Small));
            y += ROW + 4;

            // --- View angle hint + reset button ---
            // The user rotates the 3D preview by dragging INSIDE the preview box
            // (handled in OnMouseDown/Move/Up below). Button just snaps back to iso.
            area.Add(new GuiElementStaticText(capi, "View angle",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, "Reset angle",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => {
                    importPreviewYaw = 30 * Math.PI / 180.0;
                    importPreviewPitch = 20 * Math.PI / 180.0;
                    RebuildImportTextureOnly(); SetupDialog(); return true;
                },
                ElementBounds.Fixed(ctrlX, y, 120, 28), EnumButtonStyle.Small));
            y += ROW;

            area.Add(new GuiElementStaticText(capi, "Drag inside the preview → rotate camera.",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y, CTRL_W, 18),
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.6, 0.7, 0.85, 0.8 })));
            y += 22;

            // --- Status / last voxelization result ---
            if (!string.IsNullOrEmpty(importStatus))
            {
                area.Add(new GuiElementStaticText(capi, importStatus,
                    EnumTextOrientation.Left, ElementBounds.Fixed(L, y, CTRL_W, 20),
                    CairoFont.WhiteDetailText().WithColor(new double[] { 0.8, 0.9, 0.6, 0.9 })));
                y += 24;
            }

            // --- Save as .vox toggle ---
            area.Add(new GuiElementStaticText(capi, "Save as .vox",
                EnumTextOrientation.Left, ElementBounds.Fixed(L, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, importSaveAsVox ? "ON" : "OFF",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { importSaveAsVox = !importSaveAsVox; SetupDialog(); return true; },
                ElementBounds.Fixed(ctrlX, y, 55, 28), EnumButtonStyle.Small));
            y += ROW;

            // --- Action buttons ---
            area.Add(new GuiElementTextButton(capi,
                importSaveAsVox ? "Voxelize · Save · Load" : "Voxelize & Load",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { OnImportGenerate(); return true; },
                ElementBounds.Fixed(L, y, 220, 32), EnumButtonStyle.Normal));
        }

        /// <summary>Scans autochisel/obj_imports/ for .obj files (flat, no recursion).</summary>
        private void RescanObjFolder()
        {
            importObjFiles.Clear();
            try
            {
                if (SysIO.Directory.Exists(ModPaths.ObjImports))
                {
                    var files = SysIO.Directory.GetFiles(ModPaths.ObjImports, "*.obj");
                    Array.Sort(files);
                    importObjFiles.AddRange(files);
                }
            }
            catch { }
        }

        private void InvalidateImportPreview()
        {
            importPreviewShape = null;
            importPreviewTexture?.Dispose();
            importPreviewTexture = null;
            importStatus = "";
            // NOTE: we deliberately do NOT reset importLastBuiltFor here — the
            // RebuildImportPreviewIfNeeded caller manages that (it needs to
            // compare keys before calling us, and overwrite the key after).
        }

        /// <summary>
        /// Voxelizes (or re-voxelizes) the currently-selected .obj with the current
        /// import settings, IF the last-built signature differs. Cache key covers
        /// path + all settings so we don't redo work on every SetupDialog() tick.
        /// </summary>
        private void RebuildImportPreviewIfNeeded()
        {
            if (importSelectedIdx < 0 || importSelectedIdx >= importObjFiles.Count)
            {
                InvalidateImportPreview();
                importLastBuiltFor = "";
                return;
            }

            string path = importObjFiles[importSelectedIdx];
            string key = $"{path}|{importSettings.Resolution}|{importSettings.FillInterior}|{importSettings.FlipY}|{importSettings.SwapYZ}";
            if (key == importLastBuiltFor && importPreviewTexture != null) return;

            InvalidateImportPreview();
            importLastBuiltFor = key;

            var mesh = ObjParser.Parse(path, out string err);
            if (mesh == null) { importStatus = "OBJ parse failed: " + err; return; }

            var shape = Voxelizer.Voxelize(mesh, importSettings, out string voxelStatus);
            if (shape == null)
            {
                importStatus = (voxelStatus ?? "Voxelization failed.")
                    + $"  (mesh: {mesh.Triangles.Count:N0} tris)";
                return;
            }

            importPreviewShape = shape;
            importStatus = voxelStatus + $"  ·  mesh {mesh.Triangles.Count:N0} tris / {mesh.Materials.Count} mtl";

            // Build a VoxelsStorage and render a 3D preview with current yaw/pitch.
            RebuildImportTextureOnly();
        }

        /// <summary>
        /// Re-renders just the 3D preview texture from the already-voxelized
        /// importPreviewShape, using the current yaw/pitch. Cheap compared to
        /// a full voxelize — used when only the camera angle changed.
        /// </summary>
        private void RebuildImportTextureOnly()
        {
            importPreviewTexture?.Dispose();
            importPreviewTexture = null;
            if (importPreviewShape == null) return;

            string baseName = importSelectedIdx >= 0 && importSelectedIdx < importObjFiles.Count
                ? System.IO.Path.GetFileNameWithoutExtension(importObjFiles[importSelectedIdx])
                : "preview";
            var storage = VoxelArrayConverter.FromShape(importPreviewShape, baseName);
            if (storage != null && storage.GetBlockCount() > 0)
            {
                importPreviewTexture = ModelPreview.CreatePreviewTexture3D(
                    capi, storage, 300, importPreviewYaw, importPreviewPitch);
            }
        }

        private void OnImportGenerate()
        {
            if (importSelectedIdx < 0 || importSelectedIdx >= importObjFiles.Count) return;

            if (importPreviewShape == null)
            {
                RebuildImportPreviewIfNeeded();
                if (importPreviewShape == null) { SetupDialog(); return; }
            }

            string path = importObjFiles[importSelectedIdx];
            string baseName = System.IO.Path.GetFileNameWithoutExtension(path);
            var storage = VoxelArrayConverter.FromShape(importPreviewShape, baseName,
                "obj_import", new Dictionary<string, object>
                {
                    { "source",       System.IO.Path.GetFileName(path) },
                    { "resolution",   importSettings.Resolution },
                    { "fillInterior", importSettings.FillInterior },
                    { "flipY",        importSettings.FlipY },
                    { "swapYZ",       importSettings.SwapYZ }
                });
            if (storage == null || storage.GetBlockCount() == 0)
            {
                importStatus = "Generated shape is empty.";
                SetupDialog();
                return;
            }

            if (importSaveAsVox)
            {
                try
                {
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string fname = $"{SanitizeFilename(baseName)}-{stamp}.vox";
                    string outPath = SysPath.Combine(ModPaths.Models, fname);
                    VoxFileWriter.Write(outPath, importPreviewShape);
                    capi.ShowChatMessage($"[AutoChisel] Saved model: {fname}");
                }
                catch (Exception e)
                {
                    capi.ShowChatMessage($"[AutoChisel] Save failed: {e.Message}");
                }
            }

            TryClose();
            onGeneratedModel?.Invoke(storage);
        }

        private void OpenObjFolder()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", ModPaths.ObjImports); }
            catch { }
        }

        private void BuildSettingsTab(GuiComposer comp, ElementBounds insetBounds)
        {
            var s = ModSettings.Instance;
            bool isSP = capi.IsSinglePlayer;

            // Child container pinned inside the inset. All element bounds below
            // are relative to this container's top-left corner — no FixedUnder gymnastics.
            // insetBounds's AddInset visual frame was already added by SetupDialog.
            var settingsArea = insetBounds.ForkContainingChild(20, 12, 20, 12);
            comp.AddContainer(settingsArea, "settings-content");
            comp.EndChildElements();

            var area = comp.GetContainer("settings-content");

            const double ROW = 38;
            const double labelW = 220;
            const double numX = labelW;
            const double valX = numX + 36;
            const double plusX = valX + 52;
            double y = 0;

            // === Header ===
            area.Add(new GuiElementStaticText(capi, "Chisel speed",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(0, y, 420, 22),
                CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold)));
            y += 24;

            area.Add(new GuiElementStaticText(capi,
                isSP
                    ? "Running in SINGLEPLAYER. Editing values for the SP track below."
                    : "Running in MULTIPLAYER. Editing values for the MP track below.",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(0, y, 560, 18),
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.75, 0.85, 1.0, 0.9 })));
            y += 22;

            area.Add(new GuiElementStaticText(capi,
                "Ops per tick = how many chisel packets the mod sends per tick. Higher = faster, but can lag or desync on a busy server.",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(0, y, 560, 36),
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.65, 0.75, 0.85, 0.85 })));
            y += 40;

            // === SP row ===
            DrawSpeedRow(area, ref y, labelW, numX, valX, plusX, ROW, "Singleplayer",
                () => s.MaxOpsPerTickSP,
                v => {
                    s.MaxOpsPerTickSP = Math.Max(1, Math.Min(64, v));
                    if (s.InitialOpsPerTickSP > s.MaxOpsPerTickSP) s.InitialOpsPerTickSP = s.MaxOpsPerTickSP;
                },
                () => s.InitialOpsPerTickSP,
                v => s.InitialOpsPerTickSP = Math.Max(1, Math.Min(s.MaxOpsPerTickSP, v)),
                highlight: isSP);

            y += 6;

            // === MP row ===
            DrawSpeedRow(area, ref y, labelW, numX, valX, plusX, ROW, "Multiplayer",
                () => s.MaxOpsPerTickMP,
                v => {
                    s.MaxOpsPerTickMP = Math.Max(1, Math.Min(64, v));
                    if (s.InitialOpsPerTickMP > s.MaxOpsPerTickMP) s.InitialOpsPerTickMP = s.MaxOpsPerTickMP;
                },
                () => s.InitialOpsPerTickMP,
                v => s.InitialOpsPerTickMP = Math.Max(1, Math.Min(s.MaxOpsPerTickMP, v)),
                highlight: !isSP);

            y += 10;

            // === Adaptive speed toggle ===
            area.Add(new GuiElementStaticText(capi, "Adaptive speed",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(0, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, s.AdaptiveSpeed ? "ON" : "OFF",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => {
                    s.AdaptiveSpeed = !s.AdaptiveSpeed;
                    s.Save(capi); SetupDialog(); return true;
                },
                ElementBounds.Fixed(numX, y, 55, 28), EnumButtonStyle.Small));
            y += ROW;

            // === Explanation / footnote ===
            area.Add(new GuiElementStaticText(capi,
                s.AdaptiveSpeed
                    ? "• Adaptive ON: speed starts at \"Initial\" and ramps up by 1 every 20 successful ticks until it reaches Max. MP lag halves the rate."
                    : "• Adaptive OFF: speed stays locked at \"Initial\". Use this for slow servers or when you need a predictable rate.",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(0, y, 560, 50),
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.6, 0.7, 0.85, 0.85 })));
            y += 56;

            // === Apply hint ===
            area.Add(new GuiElementStaticText(capi,
                "Changes apply on the next Start / Resume. Current run keeps its speed.",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(0, y, 560, 20),
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.75, 0.75, 0.55, 0.85 })));
        }

        /// <summary>
        /// Renders one pair of [- Max +] / [- Initial +] controls for either the
        /// SP or MP speed track. highlight=true means this track is the one
        /// currently active (used for a subtle visual hint).
        /// </summary>
        private void DrawSpeedRow(GuiElementContainer area, ref double y,
            double labelW, double numX, double valX, double plusX, double ROW,
            string trackName,
            Func<int> getMax, Action<int> setMax,
            Func<int> getInitial, Action<int> setInitial,
            bool highlight)
        {
            // Track label
            var font = CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold);
            if (highlight) font = font.WithColor(new double[] { 0.85, 1.0, 0.75, 1.0 });
            area.Add(new GuiElementStaticText(capi, trackName + (highlight ? "  ← active" : ""),
                EnumTextOrientation.Left,
                ElementBounds.Fixed(0, y, 300, 20),
                font));
            y += 22;

            // Max ops
            area.Add(new GuiElementStaticText(capi, "  Max ops per tick",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(0, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, "-",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { setMax(getMax() - 1); ModSettings.Instance.Save(capi); SetupDialog(); return true; },
                ElementBounds.Fixed(numX, y, 32, 28), EnumButtonStyle.Small));
            area.Add(new GuiElementStaticText(capi, getMax().ToString(),
                EnumTextOrientation.Center,
                ElementBounds.Fixed(valX - 4, y + 6, 48, 20),
                CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold)));
            area.Add(new GuiElementTextButton(capi, "+",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { setMax(getMax() + 1); ModSettings.Instance.Save(capi); SetupDialog(); return true; },
                ElementBounds.Fixed(plusX, y, 32, 28), EnumButtonStyle.Small));
            y += ROW;

            // Initial ops
            area.Add(new GuiElementStaticText(capi, "  Initial ops per tick",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(0, y + 6, labelW, 20),
                CairoFont.WhiteSmallishText()));
            area.Add(new GuiElementTextButton(capi, "-",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { setInitial(getInitial() - 1); ModSettings.Instance.Save(capi); SetupDialog(); return true; },
                ElementBounds.Fixed(numX, y, 32, 28), EnumButtonStyle.Small));
            area.Add(new GuiElementStaticText(capi, getInitial().ToString(),
                EnumTextOrientation.Center,
                ElementBounds.Fixed(valX - 4, y + 6, 48, 20),
                CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold)));
            area.Add(new GuiElementTextButton(capi, "+",
                CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                () => { setInitial(getInitial() + 1); ModSettings.Instance.Save(capi); SetupDialog(); return true; },
                ElementBounds.Fixed(plusX, y, 32, 28), EnumButtonStyle.Small));
            y += ROW;
        }

        private bool OnOpenFolder()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", ModPaths.Root); }
            catch { }
            return true;
        }

        private void UpdateGeneratorPreview()
        {
            generatorPreview?.Dispose();
            generatorPreview = null;
            genStats = null;
            cachedGeneratorStorage = null;

            if (selectedGenerator < 0 || selectedGenerator >= generators.Count) return;
            var gen = generators[selectedGenerator];

            try
            {
                var shape = gen.Generate(generatorParams);
                if (shape == null || shape.Voxels == null) return;
                var storage = VoxelArrayConverter.FromShape(shape, gen.Name);
                if (storage == null || storage.GetBlockCount() == 0) return;

                cachedGeneratorStorage = storage;
                generatorPreview = ModelPreview.CreatePreviewTexture3D(
                    capi, storage, 280, genPreviewYaw, genPreviewPitch);

                int ops = ChiselConveyor.CountChiselOperations(storage);
                var dims = storage.GetModelDimensions();
                genStats = new GenStats
                {
                    TotalVoxels = storage.GetTotalVoxelCount(),
                    BlockCount = storage.OriginalBlockCount,
                    ChiselOps = ops,
                    ChiselsNeeded = (float)Math.Ceiling(ops / (double)CHISEL_DURABILITY * 10) / 10f,
                    SizeX = dims.X, SizeY = dims.Y, SizeZ = dims.Z,
                    TimeEstimate = FormatTime(ChiselConveyor.EstimateSeconds(capi, ops))
                };
            }
            catch { }
        }

        /// <summary>
        /// Re-renders the Generators preview texture from the cached storage with
        /// the current yaw/pitch. Does NOT regenerate the shape — used while the
        /// user drags inside the preview box so rotation stays buttery.
        /// </summary>
        private void RebuildGeneratorTextureOnly()
        {
            if (cachedGeneratorStorage == null) return;
            generatorPreview?.Dispose();
            generatorPreview = ModelPreview.CreatePreviewTexture3D(
                capi, cachedGeneratorStorage, 280, genPreviewYaw, genPreviewPitch);
        }

        private void OnGenerate()
        {
            if (selectedGenerator < 0 || selectedGenerator >= generators.Count) return;
            var gen = generators[selectedGenerator];

            try
            {
                var shape = gen.Generate(generatorParams);
                if (shape == null || shape.Voxels == null)
                {
                    capi.ShowChatMessage("Generator returned empty result.");
                    return;
                }

                var storage = VoxelArrayConverter.FromShape(shape, gen.Name, gen.Name, generatorParams);
                if (storage == null || storage.GetBlockCount() == 0)
                {
                    capi.ShowChatMessage("Generated shape is empty.");
                    return;
                }

                // Optionally persist as .vox so it shows up in Models tab next time.
                if (saveGeneratedToVox)
                {
                    try
                    {
                        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        string fname = $"{SanitizeFilename(gen.Name)}-{stamp}.vox";
                        string path = SysPath.Combine(ModPaths.Models, fname);
                        VoxFileWriter.Write(path, shape);
                        capi.ShowChatMessage($"[AutoChisel] Saved model: {fname}");
                    }
                    catch (Exception e)
                    {
                        capi.ShowChatMessage($"[AutoChisel] Save failed: {e.Message}");
                    }
                }

                TryClose();
                onGeneratedModel?.Invoke(storage);
            }
            catch (Exception e)
            {
                capi.ShowChatMessage($"Generator error: {e.Message}");
            }
        }

        private static string SanitizeFilename(string s)
        {
            if (string.IsNullOrEmpty(s)) return "generated";
            var invalid = SysIO.Path.GetInvalidFileNameChars();
            var arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
                if (Array.IndexOf(invalid, arr[i]) >= 0 || arr[i] == ' ')
                    arr[i] = '_';
            return new string(arr);
        }

        private void OnNewScrollbarValue(float value)
        {
            var container = SingleComposer?.GetContainer("scroll-content");
            if (container == null) return;
            container.Bounds.fixedY = 3 - value;
            container.Bounds.CalcWorldBounds();
        }

        private bool OnSelectModel(int index)
        {
            if (index < 0 || index >= models.Count) return false;
            var mi = models[index];
            TryClose();
            onModelSelected?.Invoke(mi.FileName, mi.MaterialMapping);
            return true;
        }

        private void OpenMaterialAssignment(int index)
        {
            if (index < 0 || index >= models.Count) return;
            var mi = models[index];
            if (!mi.IsColored || mi.UsedPaletteIndices == null || mi.Palette == null) return;

            // Build a fresh VoxelsStorage for the dialog's 3D preview. Load failures
            // are non-fatal — the dialog still works without the preview panel.
            VoxelsStorage previewStorage = null;
            try
            {
                var s = new VoxelsStorage(mi.FileName);
                if (s.GetBlockCount() > 0) previewStorage = s;
            }
            catch { }

            var dlg = new MaterialAssignmentDialog(capi, mi.FileName, mi.UsedPaletteIndices,
                mi.Palette, mi.MaterialMapping, updated =>
                {
                    mi.MaterialMapping = updated;
                    // Persist across sessions — written to autochisel/material_mappings.json.
                    sessionMaterialMappings[mi.FileName] = updated.Clone();
                    SavePersistedMappings();
                    needsRecompose = true; // refresh count in label
                }, previewStorage);
            dlg.TryOpen();
        }

        // ================================================================
        // Render (preview textures for Models tab)
        // ================================================================

        public override void OnRenderGUI(float deltaTime)
        {
            // Throttle recompose — every stats-load triggers a full rebuild, which
            // transiently drops click handlers. Coalesce into one recompose per ~400ms.
            // Exception: recompose immediately when the background pass has finished
            // so the final "fully loaded" state appears right away.
            if (needsRecompose)
            {
                long now = capi.ElapsedMilliseconds;
                bool allLoaded = (bgTickId == 0); // ticker unregisters when done
                if (allLoaded || now - lastRecomposeMs >= RECOMPOSE_THROTTLE_MS)
                {
                    needsRecompose = false;
                    lastRecomposeMs = now;
                    SetupDialog();
                }
            }
            base.OnRenderGUI(deltaTime);

            // Render generator preview on Generators tab.
            // Anchor to genInsetBounds (NOT the scrolling gen-content container) so the
            // texture stays fixed when user scrolls the parameter list below it.
            if (currentTab == 1 && generatorPreview != null && generatorPreview.TextureId != 0
                && genPreviewBoxBounds != null)
            {
                // Anchor directly to the preview box bounds so texture stays inside frame
                // regardless of dialog size / GUI scale.
                double inset = GuiElement.scaled(5);
                double pvX = genPreviewBoxBounds.absX + inset;
                double pvY = genPreviewBoxBounds.absY + inset;
                double pvW = genPreviewBoxBounds.OuterWidth - inset * 2;
                double pvH = genPreviewBoxBounds.OuterHeight - inset * 2;
                capi.Render.Render2DTexturePremultipliedAlpha(
                    generatorPreview.TextureId,
                    (float)pvX, (float)pvY,
                    (float)pvW, (float)pvH, 50f);
            }

            // Render Import-tab preview texture inside its dark frame.
            if (currentTab == 2 && importPreviewTexture != null && importPreviewTexture.TextureId != 0
                && importPreviewBoxBounds != null)
            {
                double inset = GuiElement.scaled(5);
                double pvX = importPreviewBoxBounds.absX + inset;
                double pvY = importPreviewBoxBounds.absY + inset;
                double pvW = importPreviewBoxBounds.OuterWidth - inset * 2;
                double pvH = importPreviewBoxBounds.OuterHeight - inset * 2;
                capi.Render.Render2DTexturePremultipliedAlpha(
                    importPreviewTexture.TextureId,
                    (float)pvX, (float)pvY,
                    (float)pvW, (float)pvH, 50f);
            }

            if (currentTab != 0 || !IsOpened() || SingleComposer == null) return;

            var scrollContent = SingleComposer.GetContainer("scroll-content");
            if (scrollContent == null || insetClipBounds == null) return;

            double clipX = insetClipBounds.absX, clipY = insetClipBounds.absY;
            double clipW = insetClipBounds.OuterWidth, clipH = insetClipBounds.OuterHeight;

            capi.Render.GlScissor((int)clipX, (int)(capi.Render.FrameHeight - clipY - clipH),
                (int)clipW, (int)clipH);
            capi.Render.GlScissorFlag(true);

            for (int i = 0; i < models.Count; i++)
            {
                var m = models[i];
                if (m.PreviewTexture == null || m.PreviewTexture.TextureId == 0) continue;
                // Account for the banner offset (loading indicator sits above rows)
                double rowY = scrollContent.Bounds.absY
                              + GuiElement.scaled(rowsStartYOffset)
                              + i * GuiElement.scaled(ROW_H);
                double pvSize = GuiElement.scaled(ROW_H - 14);
                double pvX = scrollContent.Bounds.absX + GuiElement.scaled(7);
                double pvY = rowY + GuiElement.scaled(7);
                if (pvY + pvSize < clipY || pvY > clipY + clipH) continue;
                capi.Render.Render2DTexturePremultipliedAlpha(
                    m.PreviewTexture.TextureId, (float)pvX, (float)pvY,
                    (float)pvSize, (float)pvSize, 50f);
            }

            capi.Render.GlScissorFlag(false);
        }

        // ================================================================
        // Lifecycle
        // ================================================================

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            // Rescan scripts folder on every open so newly-added generator .cs files show up
            // without restarting the game.
            LoadGenerators();
            ScanFileList();
            SetupDialog();
            if (bgTickId != 0) capi.World.UnregisterGameTickListener(bgTickId);
            bgTickId = capi.World.RegisterGameTickListener(BackgroundComputeTick, 50);
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            if (bgTickId != 0)
            {
                capi.World.UnregisterGameTickListener(bgTickId);
                bgTickId = 0;
            }
        }


        // ================================================================
        // Mouse interaction — drag inside the Import preview box rotates the
        // camera around the voxelized shape. Only regenerates the texture
        // (cheap) not the whole voxelization (expensive).
        // ================================================================

        public override void OnMouseDown(MouseEvent args)
        {
            if (currentTab == 2 && importPreviewBoxBounds != null && IsInside(importPreviewBoxBounds, args.X, args.Y))
            {
                importDragging = true;
                importDragStartX = args.X;
                importDragStartY = args.Y;
                importDragStartYaw = importPreviewYaw;
                importDragStartPitch = importPreviewPitch;
                args.Handled = true;
                return;
            }
            if (currentTab == 1 && genPreviewBoxBounds != null && IsInside(genPreviewBoxBounds, args.X, args.Y))
            {
                genDragging = true;
                genDragStartX = args.X;
                genDragStartY = args.Y;
                genDragStartYaw = genPreviewYaw;
                genDragStartPitch = genPreviewPitch;
                args.Handled = true;
                return;
            }
            base.OnMouseDown(args);
        }

        public override void OnMouseMove(MouseEvent args)
        {
            if (importDragging)
            {
                double dx = args.X - importDragStartX;
                double dy = args.Y - importDragStartY;
                // ~0.6° per pixel feels natural — a full spin is ~600px of drag.
                importPreviewYaw   = importDragStartYaw   + dx * (Math.PI / 300.0);
                importPreviewPitch = importDragStartPitch - dy * (Math.PI / 300.0);
                ClampPitch(ref importPreviewPitch);

                // Throttle texture rebuild to ~30 fps so heavy meshes don't lag the UI.
                long now = capi.ElapsedMilliseconds;
                if (now - importLastDragRebuildMs >= 33)
                {
                    importLastDragRebuildMs = now;
                    RebuildImportTextureOnly();
                }
                args.Handled = true;
                return;
            }
            if (genDragging)
            {
                double dx = args.X - genDragStartX;
                double dy = args.Y - genDragStartY;
                genPreviewYaw   = genDragStartYaw   + dx * (Math.PI / 300.0);
                genPreviewPitch = genDragStartPitch - dy * (Math.PI / 300.0);
                ClampPitch(ref genPreviewPitch);

                long now = capi.ElapsedMilliseconds;
                if (now - genLastDragRebuildMs >= 33)
                {
                    genLastDragRebuildMs = now;
                    RebuildGeneratorTextureOnly();
                }
                args.Handled = true;
                return;
            }
            base.OnMouseMove(args);
        }

        public override void OnMouseUp(MouseEvent args)
        {
            if (importDragging)
            {
                importDragging = false;
                RebuildImportTextureOnly();
                args.Handled = true;
                return;
            }
            if (genDragging)
            {
                genDragging = false;
                RebuildGeneratorTextureOnly();
                args.Handled = true;
                return;
            }
            base.OnMouseUp(args);
        }

        private static void ClampPitch(ref double p)
        {
            if (p >  Math.PI / 2 - 0.05) p =  Math.PI / 2 - 0.05;
            if (p < -Math.PI / 2 + 0.05) p = -Math.PI / 2 + 0.05;
        }

        private static bool IsInside(ElementBounds b, double x, double y)
        {
            return x >= b.absX && x < b.absX + b.OuterWidth
                && y >= b.absY && y < b.absY + b.OuterHeight;
        }

        /// <summary>
        /// Trim long file names so they don't overflow their text bounds and
        /// run under adjacent buttons. Keeps the first <paramref name="maxChars"/>
        /// characters and appends "...".
        /// </summary>
        private static string TruncateName(string name, int maxChars = 15)
        {
            if (string.IsNullOrEmpty(name) || name.Length <= maxChars) return name;
            if (maxChars <= 3) return new string('.', maxChars);
            return name.Substring(0, maxChars) + "...";
        }

        private static void RoundRect(Context ctx, double x, double y, double w, double h, double r)
        {
            ctx.NewPath();
            ctx.Arc(x + w - r, y + r, r, -Math.PI / 2, 0);
            ctx.Arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
            ctx.Arc(x + r, y + h - r, r, Math.PI / 2, Math.PI);
            ctx.Arc(x + r, y + r, r, Math.PI, 3 * Math.PI / 2);
            ctx.ClosePath();
        }

        public override void Dispose()
        {
            if (bgTickId != 0) capi.World.UnregisterGameTickListener(bgTickId);
            foreach (var m in models) m.PreviewTexture?.Dispose();
            generatorPreview?.Dispose();
            base.Dispose();
        }
    }
}
