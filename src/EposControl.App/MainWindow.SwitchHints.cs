using System.Windows.Automation;

namespace EposControl.App;

public partial class MainWindow
{
    private void InitializeSwitchHints()
    {
        foreach (var control in new[] { GateEnabledCheck, FilterEnabledCheck, HighPassCheck, EqEnabledCheck, MuteCheck, SidetoneMuteCheck, ReverbEnabledCheck }) {
            var instructions = control.ToolTip as string ?? "";
            var mute = control == MuteCheck || control == SidetoneMuteCheck;
            var label = control == MuteCheck ? "Audio endpoint" : control.Content?.ToString() ?? control.Name;
            void UpdateHint() {
                var state = !control.IsEnabled ? "Unavailable" : mute
                    ? control.IsChecked == true ? "Muted" : "On"
                    : control.IsChecked == true ? "On" : "Off";
                control.ToolTip = $"{label}: {state}\n{instructions}";
                AutomationProperties.SetHelpText(control, state);
            }
            control.Checked += (_, _) => UpdateHint();
            control.Unchecked += (_, _) => UpdateHint();
            control.IsEnabledChanged += (_, _) => UpdateHint();
            UpdateHint();
        }
    }
}
