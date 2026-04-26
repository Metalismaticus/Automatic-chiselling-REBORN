using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AutomaticChiselling
{
    /// <summary>
    /// Assign a game block to each palette color via inline dropdowns.
    /// Rows live inside a scrollable inset (same pattern as ModelBrowserDialog).
    /// </summary>
    public class MaterialAssignmentDialog : GuiDialog
    {
        private const int DLG_W = 1040;
        private const int ROW_H = 54;
        private const int HEADER_H = 72;   // title-bar + hint text area
        private const int MAX_INSET_H = 8 * ROW_H;  // max visible rows before scroll kicks in
        private const int FOOTER_H = 10;
        private const int PV_SIZE = 300;   // edge of the square 3D preview box (right column)
        private const int PV_MARGIN = 20;  // gap between row inset and preview box

        private readonly string modelName;
        private readonly List<byte> usedIndices;
        private readonly byte[][] palette;
        private readonly MaterialMapping mapping;
        private readonly Action<MaterialMapping> onUpdated;
        private readonly VoxelsStorage storage;  // may be null if caller couldn't build one

        private List<InventoryBlock> candidates = new List<InventoryBlock>();

        // --- 3D preview state (same camera conventions as ModelBrowserDialog) ---
        private ElementBounds previewBoxBounds;
        private LoadedTexture previewTexture;
        private double previewYaw = 30 * Math.PI / 180.0;
        private double previewPitch = 20 * Math.PI / 180.0;
        private bool dragging = false;
        private double dragStartX, dragStartY, dragStartYaw, dragStartPitch;
        private long lastDragRebuildMs = 0;

        public override string ToggleKeyCombinationCode => "autochisel_materials";

        private class InventoryBlock
        {
            public string Code;
            public string Name;
            public Block Block;
        }

        public MaterialAssignmentDialog(ICoreClientAPI capi, string modelName,
            List<byte> usedIndices, byte[][] palette,
            MaterialMapping mapping, Action<MaterialMapping> onUpdated,
            VoxelsStorage storage = null) : base(capi)
        {
            this.modelName = modelName;
            this.usedIndices = new List<byte>(usedIndices);
            this.palette = palette;
            this.mapping = mapping ?? new MaterialMapping();
            this.onUpdated = onUpdated;
            this.storage = storage;
            ScanInventory();
            SetupDialog();
            RebuildPreviewTexture();
        }

        /// <summary>
        /// Re-renders the 3D preview texture at the current yaw/pitch. Cheap
        /// (~20-50ms for typical models) so we can call it freely during drag.
        /// </summary>
        private void RebuildPreviewTexture()
        {
            previewTexture?.Dispose();
            previewTexture = null;
            if (storage == null || storage.GetBlockCount() == 0) return;
            previewTexture = ModelPreview.CreatePreviewTexture3D(
                capi, storage, PV_SIZE - 10, previewYaw, previewPitch);
        }

        private void ScanInventory()
        {
            candidates.Clear();
            var invMgr = capi.World.Player.InventoryManager;
            var seen = new HashSet<string>();

            void Consider(ItemStack stack)
            {
                if (stack == null) return;
                if (stack.Class != EnumItemClass.Block) return;
                var block = stack.Block;
                if (block == null || block.Code == null || block.BlockId == 0) return;
                if (block.IsLiquid()) return;
                string code = block.Code.ToString();
                if (seen.Contains(code)) return;
                seen.Add(code);
                candidates.Add(new InventoryBlock
                {
                    Code = code,
                    Name = SafeName(block),
                    Block = block
                });
            }

            var hotbar = invMgr.GetOwnInventory(GlobalConstants.hotBarInvClassName);
            if (hotbar != null) foreach (var slot in hotbar) Consider(slot?.Itemstack);

            var backpack = invMgr.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (backpack != null) foreach (var slot in backpack) Consider(slot?.Itemstack);

            foreach (var kvp in mapping.Assignments)
            {
                if (seen.Contains(kvp.Value)) continue;
                var loc = new AssetLocation(kvp.Value);
                var block = capi.World.GetBlock(loc);
                if (block == null) continue;
                seen.Add(kvp.Value);
                candidates.Add(new InventoryBlock
                {
                    Code = kvp.Value,
                    Name = SafeName(block) + "  (not in inventory)",
                    Block = block
                });
            }

            candidates.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        private string SafeName(Block block)
        {
            try
            {
                string n = block.GetPlacedBlockName(capi.World, null);
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
            catch { }
            return block.Code?.ToString() ?? "unknown";
        }

        private void SetupDialog()
        {
            int totalRows = Math.Max(1, usedIndices.Count);
            int contentH = totalRows * ROW_H;
            int insetH = Math.Min(contentH, MAX_INSET_H);
            // Dialog height must accommodate the taller of: row inset or preview box.
            int bodyH = Math.Max(insetH, PV_SIZE);
            int dlgH = HEADER_H + bodyH + FOOTER_H;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            var bgBounds = ElementBounds.Fixed(0, 0, DLG_W, dlgH);

            string[] values = new string[candidates.Count + 1];
            string[] names = new string[candidates.Count + 1];
            values[0] = "";
            names[0] = "— not assigned —";
            for (int j = 0; j < candidates.Count; j++)
            {
                values[j + 1] = candidates[j].Code;
                names[j + 1] = candidates[j].Name;
            }

            int assigned = 0;
            foreach (var b in usedIndices)
                if (mapping.Assignments.ContainsKey(b)) assigned++;

            string shortName = modelName;
            if (shortName.Length > 28) shortName = shortName.Substring(0, 28) + "…";

            string hint = candidates.Count == 0
                ? "⚠  No blocks detected in hotbar/backpack. Move some chisellable blocks to inventory and reopen."
                : $"{candidates.Count} block(s) available · {assigned}/{usedIndices.Count} colors assigned";

            // --- Layout ---
            // [ rows inset .............. | preview box ] with PV_MARGIN between them.
            // Reserve the right column for the 3D preview; rows get the rest.
            int insetY = HEADER_H;
            int previewX = DLG_W - 20 - PV_SIZE;     // 20 right-margin
            int insetW = previewX - PV_MARGIN - 10;  // 10 left-margin + gap before preview
            var insetBounds = ElementBounds.Fixed(10, insetY, insetW, insetH);
            var scrollbarBounds = insetBounds.RightCopy().WithFixedWidth(20);
            var clipBounds = insetBounds.ForkContainingChild(2, 2, 2, 2);
            var containerBounds = insetBounds.ForkContainingChild(2, 2, 2, 2);

            previewBoxBounds = ElementBounds.Fixed(previewX, insetY, PV_SIZE, PV_SIZE);

            var compo = capi.Gui.CreateCompo("autochisel_materials", dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar($"Assign Materials — {shortName}", () => TryClose())
                .AddStaticText(hint,
                    CairoFont.WhiteDetailText().WithColor(
                        candidates.Count == 0
                            ? new double[] { 0.95, 0.6, 0.4, 1 }
                            : new double[] { 0.75, 0.82, 0.9, 1 }),
                    ElementBounds.Fixed(12, 36, DLG_W - 24, 28));

            // Inset + clip + container + scrollbar
            compo.AddInset(insetBounds, 3)
                 .BeginClip(clipBounds)
                 .AddContainer(containerBounds, "rows-content")
                 .EndClip();

            // --- 3D preview frame on the right ---
            compo.AddStaticCustomDraw(previewBoxBounds, (ctx, s, b) =>
            {
                ctx.SetSourceRGBA(0.08, 0.08, 0.1, 0.85);
                ctx.Rectangle(b.drawX, b.drawY, b.InnerWidth, b.InnerHeight);
                ctx.Fill();
                ctx.SetSourceRGBA(0.3, 0.32, 0.35, 0.4);
                ctx.Rectangle(b.drawX, b.drawY, b.InnerWidth, b.InnerHeight);
                ctx.LineWidth = 1; ctx.Stroke();
            });
            // Hint below the preview
            compo.AddStaticText("Drag to rotate · preview",
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.55, 0.65, 0.8, 0.85 }),
                ElementBounds.Fixed(previewX, insetY + PV_SIZE + 2, PV_SIZE, 18));

            if (contentH > insetH)
            {
                compo.AddVerticalScrollbar(OnScrollChanged, scrollbarBounds, "rows-scrollbar");
            }

            // Row content width = container width minus scrollbar gutter (so rows don't sit under scrollbar)
            int rowW = insetW - 4 - (contentH > insetH ? 22 : 0);

            // Add rows to container as raw GuiElements (VS DemoScrollingGui pattern)
            var rowsArea = compo.GetContainer("rows-content");
            for (int i = 0; i < usedIndices.Count; i++)
            {
                AddRow(rowsArea, i, values, names, rowW);
            }

            SingleComposer = compo.Compose();

            // Configure scrollbar
            if (contentH > insetH)
            {
                SingleComposer.GetScrollbar("rows-scrollbar")?.SetHeights(insetH, contentH);
                OnScrollChanged(0);
            }
        }

        private void OnScrollChanged(float value)
        {
            var container = SingleComposer?.GetContainer("rows-content");
            if (container == null) return;
            container.Bounds.fixedY = 2 - value;
            container.Bounds.CalcWorldBounds();
        }

        /// <summary>
        /// Builds one row directly inside the scroll container using raw GuiElement constructors.
        /// Row layout (rowW = ~696 without scrollbar, ~674 with):
        ///   swatch[42] · RGB/idx[150] · arrow · dropdown · Clear[60]
        /// </summary>
        private void AddRow(GuiElementContainer rowsArea, int rowIndex,
            string[] values, string[] names, int rowW)
        {
            byte pIdx = usedIndices[rowIndex];
            double rowY = rowIndex * ROW_H;

            byte r = 180, g = 180, b = 180;
            if (palette != null && pIdx < palette.Length && palette[pIdx] != null)
            { r = palette[pIdx][0]; g = palette[pIdx][1]; b = palette[pIdx][2]; }
            double cr = r / 255.0, cg = g / 255.0, cb = b / 255.0;

            // Row card background
            var cardBounds = ElementBounds.Fixed(0, rowY, rowW, ROW_H - 4);
            rowsArea.Add(new GuiElementCustomDraw(capi, cardBounds, (ctx, surface, bb) =>
            {
                ctx.SetSourceRGBA(0.12, 0.13, 0.15, 0.7);
                ctx.Rectangle(bb.drawX, bb.drawY, bb.InnerWidth, bb.InnerHeight);
                ctx.Fill();
            }));

            // Color swatch
            var swatchBounds = ElementBounds.Fixed(8, rowY + 6, 42, 42);
            rowsArea.Add(new GuiElementCustomDraw(capi, swatchBounds, (ctx, surface, bb) =>
            {
                ctx.SetSourceRGBA(cr, cg, cb, 1);
                ctx.Rectangle(bb.drawX, bb.drawY, bb.InnerWidth, bb.InnerHeight);
                ctx.Fill();
                ctx.SetSourceRGBA(0.0, 0.0, 0.0, 1);
                ctx.Rectangle(bb.drawX, bb.drawY, bb.InnerWidth, bb.InnerHeight);
                ctx.LineWidth = 1.5; ctx.Stroke();
            }));

            // RGB + idx
            rowsArea.Add(new GuiElementStaticText(capi, $"RGB {r},{g},{b}",
                EnumTextOrientation.Left, ElementBounds.Fixed(60, rowY + 7, 150, 16),
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.9, 0.9, 0.95, 1 })));
            rowsArea.Add(new GuiElementStaticText(capi, $"palette idx {pIdx}",
                EnumTextOrientation.Left, ElementBounds.Fixed(60, rowY + 26, 150, 14),
                CairoFont.WhiteDetailText().WithColor(new double[] { 0.55, 0.6, 0.65, 1 })));

            // Arrow
            rowsArea.Add(new GuiElementStaticText(capi, "→",
                EnumTextOrientation.Left, ElementBounds.Fixed(215, rowY + 17, 20, 20),
                CairoFont.WhiteSmallishText().WithColor(new double[] { 0.7, 0.75, 0.8, 1 })));

            // Dropdown
            string currentValue = "";
            if (mapping.TryGet(pIdx, out AssetLocation curLoc))
                currentValue = curLoc.ToString();
            int selectedIdx = Array.IndexOf(values, currentValue);
            if (selectedIdx < 0) selectedIdx = 0;

            // Right-side layout: Clear button sits 20px from row edge, dropdown fills space up to it.
            //   dropdown [dropX .. clearX - 8]  ·  Clear [60]  ·  20px right margin
            double clearW = 60;
            double rightMargin = 20;
            double clearX = rowW - rightMargin - clearW;
            double dropX = 240;
            double dropW = clearX - 8 - dropX;

            // Dropdown & Clear share the same visual baseline. The button has a
            // little more intrinsic height for its bevel, so we nudge it 3px up
            // so the "Clear" label sits at the same Y as the dropdown text.
            var dropBounds = ElementBounds.Fixed(dropX, rowY + 15, dropW, 28);
            int capturedIdx = rowIndex;
            rowsArea.Add(new GuiElementDropDown(capi, values, names, selectedIdx,
                (code, selected) => OnAssignmentChanged(capturedIdx, code),
                dropBounds, CairoFont.WhiteSmallishText(), false));

            var clearBounds = ElementBounds.Fixed(clearX, rowY + 12, clearW, 28);
            rowsArea.Add(new GuiElementTextButton(capi, "Clear", CairoFont.ButtonText(),
                CairoFont.ButtonPressedText(),
                () => { ClearAssignment(capturedIdx); return true; },
                clearBounds, EnumButtonStyle.Small));
        }

        private void OnAssignmentChanged(int rowIndex, string code)
        {
            if (rowIndex < 0 || rowIndex >= usedIndices.Count) return;
            byte pIdx = usedIndices[rowIndex];
            if (string.IsNullOrEmpty(code))
                mapping.Unassign(pIdx);
            else
                mapping.Assign(pIdx, new AssetLocation(code));
            onUpdated?.Invoke(mapping);
            SetupDialog();
        }

        private void ClearAssignment(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= usedIndices.Count) return;
            byte pIdx = usedIndices[rowIndex];
            mapping.Unassign(pIdx);
            onUpdated?.Invoke(mapping);
            SetupDialog();
        }

        // ================================================================
        // 3D preview rendering + drag-to-rotate
        // ================================================================

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);
            if (previewTexture != null && previewTexture.TextureId != 0 && previewBoxBounds != null)
            {
                double inset = GuiElement.scaled(5);
                double x = previewBoxBounds.absX + inset;
                double y = previewBoxBounds.absY + inset;
                double w = previewBoxBounds.OuterWidth - inset * 2;
                double h = previewBoxBounds.OuterHeight - inset * 2;
                capi.Render.Render2DTexturePremultipliedAlpha(
                    previewTexture.TextureId, (float)x, (float)y, (float)w, (float)h, 50f);
            }
        }

        public override void OnMouseDown(MouseEvent args)
        {
            if (previewBoxBounds != null && IsInside(previewBoxBounds, args.X, args.Y))
            {
                dragging = true;
                dragStartX = args.X; dragStartY = args.Y;
                dragStartYaw = previewYaw; dragStartPitch = previewPitch;
                args.Handled = true;
                return;
            }
            base.OnMouseDown(args);
        }

        public override void OnMouseMove(MouseEvent args)
        {
            if (dragging)
            {
                double dx = args.X - dragStartX;
                double dy = args.Y - dragStartY;
                previewYaw   = dragStartYaw   + dx * (Math.PI / 300.0);
                previewPitch = dragStartPitch - dy * (Math.PI / 300.0);
                if (previewPitch >  Math.PI / 2 - 0.05) previewPitch =  Math.PI / 2 - 0.05;
                if (previewPitch < -Math.PI / 2 + 0.05) previewPitch = -Math.PI / 2 + 0.05;

                long now = capi.ElapsedMilliseconds;
                if (now - lastDragRebuildMs >= 33)
                {
                    lastDragRebuildMs = now;
                    RebuildPreviewTexture();
                }
                args.Handled = true;
                return;
            }
            base.OnMouseMove(args);
        }

        public override void OnMouseUp(MouseEvent args)
        {
            if (dragging)
            {
                dragging = false;
                RebuildPreviewTexture();
                args.Handled = true;
                return;
            }
            base.OnMouseUp(args);
        }

        public override void Dispose()
        {
            previewTexture?.Dispose();
            base.Dispose();
        }

        private static bool IsInside(ElementBounds b, double x, double y)
        {
            return x >= b.absX && x < b.absX + b.OuterWidth
                && y >= b.absY && y < b.absY + b.OuterHeight;
        }
    }
}
