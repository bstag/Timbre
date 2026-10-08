using System.IO;
using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private void VerifyUnavailableEffectsUi()
    {
        var mic = endpoints.First(IsB20); DeviceList.SelectedItem = mic;
        var before = effects.Read(mic); var profileBytes = File.ReadAllBytes(profiles.FilePath);
        var setupBytes = File.ReadAllBytes(setups.FilePath); var edited = editedSetupPages.ToHashSet();
        var processingExpanded = MicrophoneProcessingExpander.IsExpanded; var eqExpanded = MicrophoneEqExpander.IsExpanded;
        MicrophoneProcessingExpander.IsExpanded = true; MicrophoneEqExpander.IsExpanded = true;
        // Materialize nested Expander content, as in the live B20-to-GSX navigation that exposed this bug.
        var content = (FrameworkElement)Content;
        content.Measure(new Size(1040, 1450)); content.Arrange(new Rect(0, 0, 1040, 1450)); content.UpdateLayout();
        // Reproduce a failed native read after another endpoint's controls were populated.
        currentEffects = null; UpdateEffectsEditorAvailability(); UpdateMonitorControls();
        if (MicrophoneProcessingEditors.Visibility != Visibility.Collapsed || MicrophoneProcessingEditors.IsEnabled ||
            GateEnabledCheck.IsEnabled || GateSlider.IsEnabled || FilterLevel.IsEnabled || HighPassCheck.IsEnabled ||
            EqPreset.IsEnabled || eqSliders.Any(s => s.IsEnabled) || MicrophoneActivity.Equalizer is not null)
            throw new InvalidOperationException($"Unavailable processing exposed another device's editable settings: panel={MicrophoneProcessingEditors.Visibility}/{MicrophoneProcessingEditors.IsEnabled}, gate={GateEnabledCheck.IsEnabled}/{GateSlider.IsEnabled}, filter={FilterLevel.IsEnabled}, highPass={HighPassCheck.IsEnabled}, preset={EqPreset.IsEnabled}, EQ={eqSliders.Any(s => s.IsEnabled)}, preview={MicrophoneActivity.Equalizer is not null}.");
        LoadEffects(true); UpdateMonitorControls();
        if (MicrophoneProcessingEditors.Visibility != Visibility.Visible || !MicrophoneProcessingEditors.IsEnabled ||
            !ApoMicrophoneCodec.Matches(currentEffects!, before) || MicrophoneActivity.Equalizer is null ||
            !editedSetupPages.SetEquals(edited) || !File.ReadAllBytes(profiles.FilePath).SequenceEqual(profileBytes) ||
            !File.ReadAllBytes(setups.FilePath).SequenceEqual(setupBytes))
            throw new InvalidOperationException("A successful read did not recover processing controls without changing settings or profiles.");
        MicrophoneProcessingExpander.IsExpanded = processingExpanded; MicrophoneEqExpander.IsExpanded = eqExpanded;
    }
}
