using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EposControl.Core;

namespace EposControl.App;

public partial class MainWindow
{
    private IMicrophoneMonitor? microphoneMonitor;
    private readonly DispatcherTimer monitorTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool changingMonitor, gateGuideVisible;
    private void InitializeMonitor()
    {
        monitorTimer.Tick += (_, _) => PollMicrophoneMonitor();
        MicrophoneActivity.BandSelected += SelectMonitorEqBand;
        Closed += (_, _) => StopMicrophoneMonitor("Stopped.");
    }
    private void MonitorChanged(object sender, RoutedEventArgs e)
    {
        if (changingMonitor) return;
        if (MonitorEnabledCheck.IsChecked != true) { StopMicrophoneMonitor("Stopped. EQ settings are still shown."); return; }
        if (Selected is not { Direction: AudioDirection.Microphone } endpoint) { StopMicrophoneMonitor("Select a microphone."); return; }
        try {
            microphoneMonitor = demo ? new DemoMicrophoneMonitor() : new MicrophoneMonitor(() => new WasapiMeasurementSource(endpoint));
            MonitorStatus.Text = demo ? "Demo activity · synthetic levels" : "Starting live microphone analysis…";
            monitorTimer.Start(); PollMicrophoneMonitor();
        } catch (Exception ex) { StopMicrophoneMonitor("Microphone view unavailable: " + ex.Message); }
    }
    private void StopMicrophoneMonitor(string reason)
    {
        monitorTimer.Stop(); microphoneMonitor?.Dispose(); microphoneMonitor = null;
        if (MonitorEnabledCheck is null) return;
        changingMonitor = true; try { MonitorEnabledCheck.IsChecked = false; } finally { changingMonitor = false; }
        MicrophoneActivity.SetFrame(null); MonitorLevel.Value = -90; MonitorLevels.Text = "No live audio values"; MonitorStatus.Text = reason;
    }
    private void MonitorCollapsed(object sender, RoutedEventArgs e) => StopMicrophoneMonitor("Stopped. Expand and enable the view to analyze again.");
    private void PollMicrophoneMonitor()
    {
        if (microphoneMonitor is null) return;
        if (microphoneMonitor.Error is { } error) { StopMicrophoneMonitor("Microphone view unavailable: " + error); return; }
        var frame = microphoneMonitor.Latest;
        if (frame is null || DateTimeOffset.UtcNow - frame.CapturedAt > TimeSpan.FromSeconds(1)) {
            MicrophoneActivity.SetFrame(null); MonitorLevel.Value = -90; MonitorLevels.Text = "Waiting for audio…"; return;
        }
        MicrophoneActivity.SetFrame(frame); MonitorLevel.Value = Math.Clamp(frame.RmsDbFs, -90, 0);
        MonitorLevels.Text = $"RMS {frame.RmsDbFs:0.#} dBFS · peak {frame.PeakDbFs:0.#} dBFS" + (frame.Clipping ? " · clipping" : "");
        MonitorStatus.Text = demo ? "Demo activity · synthetic levels" : "Live Windows microphone capture · no recording or playback";
        if (frame.InvalidSamples > 0) MonitorStatus.Text += " · invalid samples detected";
        if (frame.InitialDiscontinuities > 0) MonitorStatus.Text += " · initial capture discontinuity observed";
        if (frame.TimestampErrors > 0) MonitorStatus.Text += " · timestamp uncertainty observed";
        if (frame.Discontinuities > 0) MonitorStatus.Text += " · stream interruptions observed";
    }
    private void UpdateMonitorControls()
    {
        if (MicrophoneActivity is null) return;
        var microphone = Selected?.Direction == AudioDirection.Microphone;
        MicrophoneMonitorCard.Visibility = microphone ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneActivity.SetControls(microphone && currentEffects is not null && eqSliders.Count == 9 ? eqSliders.Select(s => (float)s.Value).ToArray() : null,
            EqEnabledCheck.IsChecked == true, effectsPending,
            microphone && currentEffects is not null && GateEnabledCheck.IsChecked == true ? (float)GateSlider.Value : null, gateGuideVisible);
    }
    private void GateGuideEnter(object sender, RoutedEventArgs e) { gateGuideVisible = true; UpdateMonitorControls(); }
    private void GateGuideLeave(object sender, RoutedEventArgs e)
    {
        gateGuideVisible = GateSlider.IsMouseOver || GateSlider.IsKeyboardFocusWithin || GateEnabledCheck.IsMouseOver || GateEnabledCheck.IsKeyboardFocusWithin;
        UpdateMonitorControls();
    }
    private void SelectMonitorEqBand(int index)
    {
        if (currentEffects is null || index is < 0 or > 8) return;
        MicrophoneMonitorExpander.IsExpanded = true; MicrophoneEqExpander.IsExpanded = true;
        eqSliders[index].BringIntoView();
        if (EqEnabledCheck.IsChecked == true) eqSliders[index].Focus();
        else Status.Text = "Enable microphone EQ to adjust this band.";
        MicrophoneActivity.HighlightBand(index);
    }
    internal void ShowDemoMicrophoneMonitor()
    {
        if (!demo) throw new InvalidOperationException("Monitor preview requires demo devices.");
        DeviceList.SelectedItem = endpoints.First(IsB20); MicrophoneMonitorExpander.IsExpanded = true; MonitorEnabledCheck.IsChecked = true;
        gateGuideVisible = true; UpdateMonitorControls();
    }
}
