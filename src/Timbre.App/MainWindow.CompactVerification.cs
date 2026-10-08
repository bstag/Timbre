using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Shapes;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private void VerifyCompactControlsUi()
    {
        var mic = endpoints.First(IsB20); DeviceList.SelectedItem = mic;
        var before = effects.Read(mic); var audioBefore = backend.Read(mic.Id);
        var sideBefore = sidetone.Read(mic);
        var profileBytes = File.ReadAllBytes(profiles.FilePath);
        var setupBytes = File.ReadAllBytes(setups.FilePath);
        var editedBefore = editedSetupPages.ToHashSet();
        LiveApplyCheck.IsChecked = true;
        MicrophoneMonitorExpander.IsExpanded = true;
        MicrophoneEqExpander.IsExpanded = false;
        FeaturesExpander.IsExpanded = false;
        var content = (FrameworkElement)Content;
        void Layout(double width, double height) {
            DetailScroll.ScrollToTop();
            content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        }
        void VerifySwitch(CheckBox control, bool off, string text) {
            control.ApplyTemplate();
            var slash = (System.Windows.Shapes.Path)control.Template.FindName("OffSlash", control);
            if (slash.Visibility != (off ? Visibility.Visible : Visibility.Collapsed) ||
                System.Windows.Automation.AutomationProperties.GetHelpText(control) != text ||
                control.ToolTip is not string hint || !hint.StartsWith((control == MuteCheck ? "Audio endpoint" : control.Content) + ": " + text + "\n"))
                throw new InvalidOperationException("Icon slash, tooltip and accessible state disagree for " + control.Name);
        }
        Layout(1040, 720);
        // The primary controls must fit in the normal content area, not only the tall screenshot render.
        var graphBottom = MicrophoneActivity.TranslatePoint(new Point(0, MicrophoneActivity.ActualHeight), DetailScroll).Y;
        var gateTop = GateEnabledCheck.TranslatePoint(new Point(), DetailScroll).Y;
        var sideBottom = SidetoneMuteCheck.TranslatePoint(new Point(0, SidetoneMuteCheck.ActualHeight), DetailScroll).Y;
        if (gateTop + .1 < graphBottom || gateTop - graphBottom > 12 || sideBottom > DetailScroll.ActualHeight ||
            MicrophoneProcessingEditors.Visibility != Visibility.Visible || GateEnabledCheck.ActualHeight <= 0 || FilterEnabledCheck.ActualHeight <= 0 || HighPassCheck.ActualHeight <= 0 || EqEnabledCheck.ActualHeight <= 0)
            throw new InvalidOperationException($"Compact controls do not fit below the graph: graph={graphBottom}, gate={gateTop}, sidetone={sideBottom}, viewport={DetailScroll.ActualHeight}.");
        LiveApplyCheck.IsChecked = false;
        foreach (var control in new[] { GateEnabledCheck, FilterEnabledCheck, HighPassCheck, EqEnabledCheck }) {
            var original = control.IsChecked;
            control.IsChecked = false; VerifySwitch(control, true, "Off");
            control.IsChecked = true; VerifySwitch(control, false, "On");
            control.IsChecked = original;
        }
        MuteCheck.IsChecked = true; VerifySwitch(MuteCheck, true, "Muted");
        MuteCheck.IsChecked = false; VerifySwitch(MuteCheck, false, "On");
        SidetoneMuteCheck.IsChecked = true; VerifySwitch(SidetoneMuteCheck, true, "Muted");
        SidetoneMuteCheck.IsChecked = false; VerifySwitch(SidetoneMuteCheck, false, "On");
        DiscardChangesClick(this, new RoutedEventArgs());
        // Use the accessible Toggle pattern so custom presentation retains standard checkbox behavior.
        GateEnabledCheck.IsChecked = true;
        var toggle = (IToggleProvider)new CheckBoxAutomationPeer(GateEnabledCheck).GetPattern(PatternInterface.Toggle);
        var storedGate = GateSlider.Value;
        toggle.Toggle();
        if (GateEnabledCheck.IsChecked != false || GateSlider.IsEnabled || GateSlider.Value != storedGate ||
            effects.Read(mic) != before || backend.Read(mic.Id) != audioBefore || sidetone.Read(mic) != sideBefore)
            throw new InvalidOperationException("Gate icon did not disable only gating as a manual draft while retaining the level.");
        ApplyEffectsClick(this, new RoutedEventArgs());
        var afterOff = effects.Read(mic);
        if (afterOff.Processing!.GateEnabled || afterOff.GatePercent != before.GatePercent || afterOff.Processing != before.Processing! with { GateEnabled = false } ||
            afterOff.Processing!.FilterEnabled != before.Processing!.FilterEnabled || afterOff.FilterLevel != before.FilterLevel || backend.Read(mic.Id) != audioBefore)
            throw new InvalidOperationException("Applying gate off altered its stored setting, another effect, or endpoint mute.");
        toggle.Toggle(); ApplyEffectsClick(this, new RoutedEventArgs());
        if (!effects.Read(mic).Processing!.GateEnabled || !GateSlider.IsEnabled || GateSlider.Value != storedGate)
            throw new InvalidOperationException("Gate icon did not recover its retained level when enabled again.");
        effects.Apply(mic, effects.Read(mic), before); LoadEffects(true);
        editedSetupPages.Clear(); editedSetupPages.UnionWith(editedBefore);
        if (!File.ReadAllBytes(profiles.FilePath).SequenceEqual(profileBytes) || !File.ReadAllBytes(setups.FilePath).SequenceEqual(setupBytes))
            throw new InvalidOperationException("Compact icon audition changed saved profiles.");
        // Narrow windows may wrap the effect strip, but sliders and switches must remain inside its width.
        Layout(860, 590);
        foreach (var control in new FrameworkElement[] { GateEnabledCheck, GateSlider, FilterEnabledCheck, HighPassCheck, EqEnabledCheck, EqPreset, SidetoneMuteCheck, SidetoneSlider }) {
            var right = control.TranslatePoint(new Point(control.ActualWidth, 0), DetailScroll).X;
            if (right > DetailScroll.ViewportWidth + 1) throw new InvalidOperationException("Compact control overflows the narrow viewport: " + control.Name);
        }
        MicrophoneEqExpander.IsExpanded = true; Layout(1040, 720); DetailScroll.ScrollToBottom(); content.UpdateLayout();
        DeviceList.SelectedItem = endpoints.First(e => e.Direction == AudioDirection.Playback);
        content.UpdateLayout();
        if (DetailScroll.VerticalOffset != 0 || MicrophoneMonitorCard.Visibility != Visibility.Collapsed || microphoneMonitor is not null)
            throw new InvalidOperationException("Device selection retained a scrolled microphone page or active analysis.");
        MicrophoneEqExpander.IsExpanded = false; DeviceList.SelectedItem = mic;
        LiveApplyCheck.IsChecked = true; Layout(1040, 1450);
    }
}
