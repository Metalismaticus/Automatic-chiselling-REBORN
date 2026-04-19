using System;
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
        private string modelName = "";
        private int chiselOps = 0;
        private int remainingSeconds = -1;
        private long lastUpdateMs = 0;
        private ChiselHudState state = ChiselHudState.Hidden;

        // Callbacks for buttons
        public Action OnStartClicked;
        public Action OnPauseClicked;
        public Action OnStopClicked;

        public override string ToggleKeyCombinationCode => null;
        public override bool Focusable => false;

        public ProgressHud(ICoreClientAPI capi) : base(capi) { }

        /// <summary>
        /// Show HUD with model info (called when model is loaded).
        /// </summary>
        public void ShowModelLoaded(string name, int ops)
        {
            modelName = name ?? "";
            chiselOps = ops;
            progress = 0f;
            remainingSeconds = -1;
            state = ChiselHudState.ModelLoaded;
            ComposeHud();
            TryOpen();
        }

        /// <summary>
        /// Switch to chiseling state (called when chiseling starts).
        /// </summary>
        public void ShowChiseling(string name)
        {
            modelName = name ?? "";
            progress = 0f;
            state = ChiselHudState.Chiseling;
            ComposeHud();
            if (!IsOpened()) TryOpen();
        }

        /// <summary>
        /// Switch to paused state.
        /// </summary>
        public void ShowPaused()
        {
            state = ChiselHudState.Paused;
            ComposeHud();
        }

        /// <summary>
        /// Hide everything.
        /// </summary>
        public void Hide()
        {
            state = ChiselHudState.Hidden;
            TryClose();
        }

        /// <summary>
        /// Update progress bar during chiseling.
        /// </summary>
        public void UpdateProgress(float percent, int remainingSec = -1)
        {
            progress = Math.Clamp(percent, 0f, 100f);
            remainingSeconds = remainingSec;

            long now = capi.ElapsedMilliseconds;
            if (now - lastUpdateMs < 500) return;
            lastUpdateMs = now;

            ComposeHud();
        }

        private static string FormatTime(int seconds)
        {
            if (seconds < 0) return null;
            if (seconds < 60) return $"~{seconds} sec";
            if (seconds < 3600) return $"~{seconds / 60} min";
            int h = seconds / 3600, m = (seconds % 3600) / 60;
            return m > 0 ? $"~{h}h {m}m" : $"~{h}h";
        }

        private void ComposeHud()
        {
            if (state == ChiselHudState.Hidden) return;

            try
            {
                // Build status text
                string text;
                switch (state)
                {
                    case ChiselHudState.ModelLoaded:
                        text = $"{modelName}   |   {chiselOps:N0} ops";
                        break;
                    case ChiselHudState.Chiseling:
                        int pct = (int)progress;
                        string timeStr = FormatTime(remainingSeconds);
                        text = timeStr != null
                            ? $"{modelName}  {pct}%  |  {timeStr}"
                            : $"{modelName}  {pct}%";
                        break;
                    case ChiselHudState.Paused:
                        text = $"{modelName}  {(int)progress}%  PAUSED";
                        break;
                    default:
                        text = modelName;
                        break;
                }

                var dialogBounds = ElementStdBounds.AutosizedMainDialog
                    .WithAlignment(EnumDialogArea.CenterTop)
                    .WithFixedOffset(0, 10);

                // Text + two buttons side by side
                var textBounds = ElementBounds.Fixed(10, 10, 350, 22);

                // Start/Pause button
                string btnText = (state == ChiselHudState.Chiseling) ? "Pause" : "Start";
                var btn1Bounds = ElementBounds.Fixed(370, 8, 55, 22);

                // Stop button
                var btn2Bounds = ElementBounds.Fixed(430, 8, 45, 22);

                var bgBounds = ElementBounds.Fill
                    .WithFixedPadding(GuiStyle.ElementToDialogPadding);
                bgBounds.BothSizing = ElementSizing.FitToChildren;
                bgBounds.WithChildren(textBounds, btn1Bounds, btn2Bounds);

                Composers["autochisel_hud"] = capi.Gui
                    .CreateCompo("autochisel_hud", dialogBounds)
                    .AddShadedDialogBG(bgBounds)
                    .AddDynamicText(text, CairoFont.WhiteSmallishText(), textBounds, "statusText")
                    .AddSmallButton(btnText, OnStartPauseBtn, btn1Bounds, EnumButtonStyle.Small, "btnStartPause")
                    .AddSmallButton("Stop", OnStopBtn, btn2Bounds, EnumButtonStyle.Small, "btnStop")
                    .Compose();
            }
            catch (Exception) { }
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
