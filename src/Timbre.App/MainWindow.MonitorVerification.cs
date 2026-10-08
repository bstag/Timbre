using System.IO;
using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private void VerifyMicrophoneMonitorUi()
    {
        if (!demo) throw new InvalidOperationException("Monitor UI checks require demo adapters.");
        var microphone = endpoints.First(IsB20); DeviceList.SelectedItem = microphone;
        var before = setupControls.Capture("Before analysis", endpoints);
        var deviceProfiles = File.ReadAllBytes(profiles.FilePath); var setupProfiles = File.ReadAllBytes(setups.FilePath);
        var edited = editedSetupPages.ToHashSet();
        if (MonitorEnabledCheck.IsChecked == true || microphoneMonitor is not null || MicrophoneActivity.Frame is not null)
            throw new InvalidOperationException("Microphone analysis unexpectedly started during startup or other UI checks.");
        MicrophoneMonitorExpander.IsExpanded = true;
        if (microphoneMonitor is not null) throw new InvalidOperationException("Expanding a view opened capture without enabling live activity.");
        MonitorEnabledCheck.IsChecked = true; PollMicrophoneMonitor();
        if (MicrophoneActivity.Frame is not { BandRmsDbFs.Count: 9 } frame || frame.RmsDbFs != -27 || MonitorLevel.Value != -27 || !MonitorStatus.Text.Contains("synthetic"))
            throw new InvalidOperationException("Demo microphone activity did not render the live summary and meter.");
        var equalizer = effects.Read(microphone).Processing!.Equalizer.Levels();
        if (MicrophoneActivity.Equalizer is null || !MicrophoneActivity.Equalizer.SequenceEqual(equalizer))
            throw new InvalidOperationException("EQ settings preview did not follow the selected microphone's controls.");
        GateGuideEnter(GateSlider, new RoutedEventArgs());
        if (GateEnabledCheck.IsChecked == true && MicrophoneActivity.GateGuidePercent != (float)GateSlider.Value)
            throw new InvalidOperationException("Noise-gate hover did not show its current position guide.");
        gateGuideVisible = false; UpdateMonitorControls();
        if (MicrophoneActivity.GateGuidePercent is not null) throw new InvalidOperationException("Gate guide remained after leaving the control.");
        SelectMonitorEqBand(4);
        if (!MicrophoneMonitorExpander.IsExpanded || !MicrophoneEqExpander.IsExpanded)
            throw new InvalidOperationException("Selecting a graph band did not reveal the EQ controls.");
        MicrophoneMonitorExpander.IsExpanded = false;
        if (MonitorEnabledCheck.IsChecked == true || microphoneMonitor is not null || MicrophoneActivity.Frame is not null)
            throw new InvalidOperationException("Collapsing the microphone view left capture active or retained stale levels.");
        MicrophoneMonitorExpander.IsExpanded = true; MonitorEnabledCheck.IsChecked = true;
        DeviceList.SelectedItem = endpoints.First(e => e.Direction == AudioDirection.Playback);
        if (microphoneMonitor is not null || MicrophoneActivity.Frame is not null || MicrophoneMonitorCard.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Device selection leaked microphone capture or live values onto playback.");
        DeviceList.SelectedItem = microphone;
        if (MonitorEnabledCheck.IsChecked == true || microphoneMonitor is not null)
            throw new InvalidOperationException("Returning to a microphone automatically restarted capture.");
        microphoneMonitor = new FrozenMonitor(frame with { CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-3) }, null);
        PollMicrophoneMonitor();
        if (MicrophoneActivity.Frame is not null || !MonitorLevels.Text.Contains("Waiting")) throw new InvalidOperationException("Stale monitor data was presented as live audio.");
        StopMicrophoneMonitor("Stopped.");
        microphoneMonitor = new FrozenMonitor(null, "test disconnected capture"); PollMicrophoneMonitor();
        if (microphoneMonitor is not null || MicrophoneActivity.Frame is not null || !MonitorStatus.Text.Contains("test disconnected capture"))
            throw new InvalidOperationException("Capture error did not stop analysis and report unavailability.");
        var after = setupControls.Capture("Before analysis", endpoints);
        if (!before.Devices.SequenceEqual(after.Devices) || !File.ReadAllBytes(profiles.FilePath).SequenceEqual(deviceProfiles) ||
            !File.ReadAllBytes(setups.FilePath).SequenceEqual(setupProfiles) || !editedSetupPages.SetEquals(edited))
            throw new InvalidOperationException("Microphone analysis, guide hover or graph navigation changed settings, profiles or edit tracking.");
        MicrophoneMonitorExpander.IsExpanded = true; MicrophoneProcessingExpander.IsExpanded = false; MicrophoneEqExpander.IsExpanded = false;
    }
    private sealed class FrozenMonitor(MicrophoneMonitorFrame? frame, string? error) : IMicrophoneMonitor
    {
        public MicrophoneMonitorFrame? Latest => frame;
        public string? Error => error;
        public void Dispose() { }
    }
}
