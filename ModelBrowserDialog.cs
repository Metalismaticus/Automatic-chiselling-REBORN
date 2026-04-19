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

        // Current tab: 0 = Models, 1 = Generators
        private int currentTab = 0;

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
                    m.TimeEstimateSP = FormatTime((int)(ops / OPS_PER_SEC_SP));
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
            var tab1Bounds = ElementBounds.Fixed(5, GuiStyle.TitleBarHeight + 2, 120, TAB_H - 4);
            var tab2Bounds = ElementBounds.Fixed(130, GuiStyle.TitleBarHeight + 2, 120, TAB_H - 4);
            comp.AddSmallButton("Models", () => { SwitchTab(0); return true; },
                tab1Bounds, currentTab == 0 ? EnumButtonStyle.Normal : EnumButtonStyle.Small, "tabModels");
            comp.AddSmallButton("Generators", () => { SwitchTab(1); return true; },
                tab2Bounds, currentTab == 1 ? EnumButtonStyle.Normal : EnumButtonStyle.Small, "tabGenerators");

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

            // Line 1 — filename (bold, white)
            scrollArea.Add(new GuiElementStaticText(capi, $"{m.FileName}.vox",
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
                    if (display.Length > 32) display = display.Substring(0, 32) + "…";

                    string capturedPId = pId;
                    string capturedLabel = param.Label;
                    area.Add(new GuiElementTextButton(capi, display,
                        CairoFont.ButtonText(), CairoFont.ButtonPressedText(),
                        () => { OpenTextInputPopup(capturedPId, capturedLabel); return true; },
                        ElementBounds.Fixed(L + 180, y, 280, 28), EnumButtonStyle.Small));
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

            if (selectedGenerator < 0 || selectedGenerator >= generators.Count) return;
            var gen = generators[selectedGenerator];

            try
            {
                var shape = gen.Generate(generatorParams);
                if (shape == null || shape.Voxels == null) return;
                var storage = VoxelArrayConverter.FromShape(shape, gen.Name);
                if (storage == null || storage.GetBlockCount() == 0) return;

                generatorPreview = ModelPreview.CreatePreviewTexture(capi, storage, 280);

                int ops = ChiselConveyor.CountChiselOperations(storage);
                var dims = storage.GetModelDimensions();
                genStats = new GenStats
                {
                    TotalVoxels = storage.GetTotalVoxelCount(),
                    BlockCount = storage.OriginalBlockCount,
                    ChiselOps = ops,
                    ChiselsNeeded = (float)Math.Ceiling(ops / (double)CHISEL_DURABILITY * 10) / 10f,
                    SizeX = dims.X, SizeY = dims.Y, SizeZ = dims.Z,
                    TimeEstimate = FormatTime((int)(ops / OPS_PER_SEC_SP))
                };
            }
            catch { }
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

            var dlg = new MaterialAssignmentDialog(capi, mi.FileName, mi.UsedPaletteIndices,
                mi.Palette, mi.MaterialMapping, updated =>
                {
                    mi.MaterialMapping = updated;
                    // Persist across sessions — written to autochisel/material_mappings.json.
                    sessionMaterialMappings[mi.FileName] = updated.Clone();
                    SavePersistedMappings();
                    needsRecompose = true; // refresh count in label
                });
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
