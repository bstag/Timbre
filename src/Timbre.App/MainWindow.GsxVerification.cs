using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private void VerifyGsxMicrophoneUi()
    {
        var b20 = endpoints.First(IsB20);
        var gsx = endpoints.Single(e => e.Direction == AudioDirection.Microphone && e.Usb?.ProductId == "0098");
        var sound = endpoints.Single(e => e.Direction == AudioDirection.Playback && e.Usb?.ProductId == "0098");
        var before = effects.Read(gsx); var b20Before = effects.Read(b20); var soundBefore = playback.Read(sound);
        DeviceList.SelectedItem = gsx;
        if (currentEffects is null || MicrophoneProcessingEditors.Visibility != Visibility.Visible ||
            HighPassCheck.Visibility != Visibility.Collapsed || HighPassCheck.IsEnabled || currentSidetone is not null)
            throw new InvalidOperationException("GSX did not expose only its validated microphone controls.");
        LiveApplyCheck.IsChecked = true;
        GateEnabledCheck.IsChecked = true; FilterEnabledCheck.IsChecked = true; EqEnabledCheck.IsChecked = true;
        GateSlider.Value = 51; FilterLevel.SelectedIndex = 1; EqPreset.SelectedIndex = 1;
        if (!FlushLiveChanges(true)) throw new InvalidOperationException("GSX live processing apply failed.");
        var applied = effects.Read(gsx);
        if (applied.GatePercent != 51 || applied.FilterLevel != 1 || applied.Processing!.Equalizer != MicrophoneEqPresets.Warm ||
            applied.Processing.HighPassEnabled != before.Processing!.HighPassEnabled ||
            MicrophoneActivity.Equalizer is null || !MicrophoneActivity.Equalizer.SequenceEqual(MicrophoneEqPresets.Warm.Levels()) ||
            effects.Read(b20) != b20Before || playback.Read(sound) != soundBefore)
            throw new InvalidOperationException("GSX live controls or combined EQ preview changed another page or an unsupported field.");
        var profile = CaptureProfile(gsx, "GSX microphone check");
        if (profile.Effects != applied || profile.Playback is not null || profile.Sidetone is not null)
            throw new InvalidOperationException("GSX microphone profile captured unavailable or other-page settings.");
        effects.Apply(gsx, applied, before); LoadEffects(true);
        DeviceList.SelectedItem = b20;
        if (HighPassCheck.Visibility != Visibility.Visible || !HighPassCheck.IsEnabled)
            throw new InvalidOperationException("B20 high-pass availability did not recover after GSX selection.");
    }
}
