using System;
using Vintagestory.API.Client;

namespace AutomaticChiselling
{
    /// <summary>
    /// Simple modal text-input popup. Used to edit long string parameters
    /// (like QR generator's URL text) where inline TextInput inside a scroll
    /// container can't receive keyboard focus in VS's GUI system.
    /// Self-contained dialog — its TextInput is composer-level and works reliably.
    /// </summary>
    public class TextInputPopup : GuiDialog
    {
        private const int DLG_W = 520;
        private const int DLG_H = 170;

        private readonly string title;
        private readonly string initial;
        public Action<string> OnSave;

        public override string ToggleKeyCombinationCode => null;

        public TextInputPopup(ICoreClientAPI capi, string title, string initial) : base(capi)
        {
            this.title = title ?? "";
            this.initial = initial ?? "";
            SetupDialog();
        }

        private void SetupDialog()
        {
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);
            var bgBounds = ElementBounds.Fixed(0, 0, DLG_W, DLG_H);

            var textBounds  = ElementBounds.Fixed(15, 45,  DLG_W - 30, 32);
            var saveBounds  = ElementBounds.Fixed(DLG_W - 220, 105, 100, 32);
            var cancelBnds  = ElementBounds.Fixed(DLG_W - 115, 105, 100, 32);

            SingleComposer = capi.Gui.CreateCompo(
                    "autochisel_textinput_" + Guid.NewGuid().ToString("N"),
                    dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(title, () => TryClose())
                .AddTextInput(textBounds, _ => { }, CairoFont.TextInput(), "input")
                .AddButton("Save",   OnSaveClicked,   saveBounds, EnumButtonStyle.Normal)
                .AddButton("Cancel", OnCancelClicked, cancelBnds, EnumButtonStyle.Small)
                .Compose();

            var input = SingleComposer.GetTextInput("input");
            input?.SetValue(initial);
        }

        private bool OnSaveClicked()
        {
            var input = SingleComposer?.GetTextInput("input");
            string value = input?.GetText() ?? "";
            OnSave?.Invoke(value);
            TryClose();
            return true;
        }

        private bool OnCancelClicked()
        {
            TryClose();
            return true;
        }

        public override bool CaptureAllInputs() => true;
    }
}
