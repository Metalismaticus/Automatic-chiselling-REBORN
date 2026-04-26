using System;
using Cairo;
using Vintagestory.API.Client;

namespace AutomaticChiselling
{
    public enum ChiselHudState
    {
        Hidden,
        ModelLoaded,  // model loaded, waiting for position/start
        Chiseling,    // actively chiseling
        Paused        // paused
    }

    public class ProgressHud : HudElement
    {
        private float progress = 0f;
        private int remainingSeconds = -1;
        private long lastUpdateMs = 0;
        private ChiselHudState state = ChiselHudState.Hidden;

        public Action OnStartClicked;
        public Action OnPauseClicked;
        public Action OnStopClicked;

        public override string ToggleKeyCombinationCode => null;
        public override bool Focusable => false;

        public ProgressHud(ICoreClientAPI capi) : base(capi) { }

        public void ShowModelLoaded()
        {
            progress = 0f;
            remainingSeconds = -1;
            state = ChiselHudState.ModelLoaded;
            ComposeHud();
            TryOpen();
        }

        public void ShowChiseling()
        {
            progress = 0f;
            state = ChiselHudState.Chiseling;
            ComposeHud();
            lastUpdateMs = capi.ElapsedMilliseconds;
            if (!IsOpened()) TryOpen();
        }

        public void ShowPaused()
        {
            state = ChiselHudState.Paused;
            ComposeHud();
            lastUpdateMs = capi.ElapsedMilliseconds;
        }

        public void Hide()
        {
            state = ChiselHudState.Hidden;
            progress = 0f;
            remainingSeconds = -1;
            lastUpdateMs = 0;
            TryClose();
        }

        // Set while the user is holding down a mouse button anywhere over the
        // HUD. Used to defer the periodic ComposeHud() — recomposing between
        // mousedown and mouseup tears down the buttons and drops the click,
        // which is what made Start/Pause/Stop "not always" register.
        private bool mouseHeld = false;
        private bool deferredRecompose = false;

        public override void OnMouseDown(MouseEvent args)
        {
            mouseHeld = true;
            base.OnMouseDown(args);
        }

        public override void OnMouseUp(MouseEvent args)
        {
            base.OnMouseUp(args);
            mouseHeld = false;
            if (deferredRecompose)
            {
                deferredRecompose = false;
                ComposeHud();
            }
        }

        public void UpdateProgress(float percent, int remainingSec = -1)
        {
            progress = Math.Clamp(percent, 0f, 100f);
            remainingSeconds = remainingSec;

            // Redraw at ~4Hz; always force the final 100% redraw so the bar
            // doesn't get stuck visibly short of full just before completion.
            long now = capi.ElapsedMilliseconds;
            if (now - lastUpdateMs < 250 && progress < 100f) return;
            lastUpdateMs = now;

            // While a mouse button is held, defer the recompose: rebuilding
            // buttons mid-click loses the press. The latest progress/ETA is
            // already stored above, so the deferred call on mouseup will
            // render the current values.
            if (mouseHeld) { deferredRecompose = true; return; }

            ComposeHud();
        }

        private static string FormatTime(int seconds)
        {
            if (seconds < 0) return null;
            if (seconds < 60)   return $"{seconds}s";
            if (seconds < 3600) return $"{seconds / 60}m {seconds % 60:D2}s";
            int h = seconds / 3600, m = (seconds % 3600) / 60;
            return m > 0 ? $"{h}h {m:D2}m" : $"{h}h";
        }

        private string BuildEtaText()
        {
            switch (state)
            {
                case ChiselHudState.Paused:    return "PAUSED";
                case ChiselHudState.Chiseling: return FormatTime(remainingSeconds) ?? "...";
                case ChiselHudState.ModelLoaded: return "Ready";
                default: return "";
            }
        }

        private void ComposeHud()
        {
            if (state == ChiselHudState.Hidden) return;

            try
            {
                // --- Layout (single row): [ETA] [progress bar] [Start/Pause] [Stop] ---
                const double H        = 28;
                const double BAR_W    = 250;
                const double BTN1_W   = 64;
                const double BTN_GAP  = 6;
                const double BTN2_W   = 54;
                const double GAP      = 10;
                const double PAD      = 12;   // symmetric padding on ALL four sides

                // Symmetric side groups → progress bar lands on the panel's
                // geometric horizontal centre.
                const double SIDE_W = BTN1_W + BTN_GAP + BTN2_W; // 124

                // Children are placed at (0,0) origin: WithFixedPadding(PAD) below
                // wraps the row with PAD on every side, so the content sits at
                // the panel's geometric centre vertically as well — no manual
                // top/bottom magic numbers, no asymmetric drift.
                double etaX  = 0;
                double barX  = etaX + SIDE_W + GAP;
                double btn1X = barX  + BAR_W  + GAP;
                double btn2X = btn1X + BTN1_W + BTN_GAP;

                var dialogBounds = ElementStdBounds.AutosizedMainDialog
                    .WithAlignment(EnumDialogArea.CenterTop)
                    .WithFixedOffset(0, 60);

                var etaBounds  = ElementBounds.Fixed(etaX,  0, SIDE_W, H);
                var barBounds  = ElementBounds.Fixed(barX,  0, BAR_W,  H);
                var btn1Bounds = ElementBounds.Fixed(btn1X, 0, BTN1_W, H);
                var btn2Bounds = ElementBounds.Fixed(btn2X, 0, BTN2_W, H);

                var bgBounds = ElementBounds.Fill.WithFixedPadding(PAD);
                bgBounds.BothSizing = ElementSizing.FitToChildren;
                bgBounds.WithChildren(etaBounds, barBounds, btn1Bounds, btn2Bounds);

                string btnText = (state == ChiselHudState.Chiseling) ? "Pause" : "Start";

                // No AddShadedDialogBG — the panel frame is intentionally omitted;
                // only the elements themselves render (ETA text, progress bar,
                // buttons). bgBounds still drives FitToChildren layout maths so
                // the row stays centred and the dialog has the right hit area.
                Composers["autochisel_hud"] = capi.Gui
                    .CreateCompo("autochisel_hud", dialogBounds)
                    .BeginChildElements(bgBounds)
                    .AddStaticCustomDraw(etaBounds, DrawEtaText)
                    .AddStaticCustomDraw(barBounds, DrawProgressBar)
                    .AddSmallButton(btnText, OnStartPauseBtn, btn1Bounds, EnumButtonStyle.Small, "btnStartPause")
                    .AddSmallButton("Stop",   OnStopBtn,      btn2Bounds, EnumButtonStyle.Small, "btnStop")
                    .EndChildElements()
                    .Compose();
            }
            catch (Exception e)
            {
                capi.Logger?.Warning("[AutoChisel] HUD compose failed: " + e);
            }
        }

        private void DrawEtaText(Context ctx, ImageSurface surface, ElementBounds bounds)
        {
            string text = BuildEtaText();
            if (string.IsNullOrEmpty(text)) return;

            ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
            ctx.SetFontSize(GuiElement.scaled(14));

            var ext = ctx.TextExtents(text);
            // Right-align: text's right edge sits at bounds' right edge (less a small inner gap).
            // Vertical-centre: same formula as the bar's "%" — independent of GUI scale.
            const double RIGHT_GAP = 4;
            double tx = bounds.drawX + bounds.InnerWidth - ext.Width - ext.XBearing - RIGHT_GAP;
            double ty = bounds.drawY + (bounds.InnerHeight - ext.Height) / 2 - ext.YBearing;

            // Subtle shadow for legibility on the dialog BG, then the white glyphs.
            ctx.SetSourceRGBA(0, 0, 0, 0.55);
            ctx.MoveTo(tx + 1, ty + 1);
            ctx.ShowText(text);
            ctx.SetSourceRGBA(1, 1, 1, 0.92);
            ctx.MoveTo(tx, ty);
            ctx.ShowText(text);
        }

        private void DrawProgressBar(Context ctx, ImageSurface surface, ElementBounds bounds)
        {
            double x = bounds.drawX;
            double y = bounds.drawY;
            double w = bounds.InnerWidth;
            double h = bounds.InnerHeight;
            double pct = Math.Clamp(progress / 100.0, 0.0, 1.0);
            double r = Math.Min(4, h / 2);

            // Track
            ctx.SetSourceRGBA(0.07, 0.07, 0.09, 0.90);
            RoundRect(ctx, x, y, w, h, r);
            ctx.Fill();

            // Fill — keep at least 2r+1 wide so the rounded corners stay visible
            // even at very low percentages, otherwise a 1% bar reads as empty.
            if (pct > 0.001)
            {
                double fillW = Math.Max(2 * r + 1, w * pct);
                if (state == ChiselHudState.Paused)
                    ctx.SetSourceRGBA(0.86, 0.62, 0.20, 0.95); // amber
                else
                    ctx.SetSourceRGBA(0.40, 0.78, 0.42, 0.95); // green
                RoundRect(ctx, x, y, fillW, h, r);
                ctx.Fill();
            }

            // Outline (inset by 0.5 for crisp 1px stroke)
            ctx.SetSourceRGBA(0.55, 0.55, 0.60, 0.55);
            RoundRect(ctx, x + 0.5, y + 0.5, w - 1, h - 1, r);
            ctx.LineWidth = 1;
            ctx.Stroke();

            // Percent text — shadowed white, centered both axes
            ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Bold);
            ctx.SetFontSize(GuiElement.scaled(13));
            string label = $"{(int)progress}%";
            var ext = ctx.TextExtents(label);
            double tx = x + (w - ext.Width) / 2 - ext.XBearing;
            double ty = y + (h - ext.Height) / 2 - ext.YBearing;
            ctx.SetSourceRGBA(0, 0, 0, 0.55);
            ctx.MoveTo(tx + 1, ty + 1);
            ctx.ShowText(label);
            ctx.SetSourceRGBA(1, 1, 1, 0.95);
            ctx.MoveTo(tx, ty);
            ctx.ShowText(label);
        }

        private static void RoundRect(Context ctx, double x, double y, double w, double h, double r)
        {
            if (w < 2 * r) r = w / 2;
            if (h < 2 * r) r = h / 2;
            ctx.NewPath();
            ctx.Arc(x + w - r, y + r,     r, -Math.PI / 2, 0);
            ctx.Arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
            ctx.Arc(x + r,     y + h - r, r, Math.PI / 2, Math.PI);
            ctx.Arc(x + r,     y + r,     r, Math.PI, 3 * Math.PI / 2);
            ctx.ClosePath();
        }

        private bool OnStartPauseBtn()
        {
            if (state == ChiselHudState.Chiseling)
                OnPauseClicked?.Invoke();
            else
                OnStartClicked?.Invoke();
            return true;
        }

        private bool OnStopBtn()
        {
            OnStopClicked?.Invoke();
            return true;
        }

        public override void OnRenderGUI(float deltaTime)
        {
            if (state == ChiselHudState.Hidden) return;
            base.OnRenderGUI(deltaTime);
        }

        public override bool ShouldReceiveMouseEvents() => state != ChiselHudState.Hidden;
        public override bool ShouldReceiveKeyboardEvents() => false;
    }
}
