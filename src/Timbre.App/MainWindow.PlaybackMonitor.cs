using System.Windows;
using System.Windows.Threading;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private IMicrophoneMonitor? playbackMonitor;
    private readonly DispatcherTimer playbackMonitorTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool changingPlaybackMonitor;
    private void InitializePlaybackMonitor()
    {
        playbackMonitorTimer.Tick += (_, _) => PollPlaybackMonitor();
        PlaybackActivity.BandSelected += index => {
            if (currentPlayback is null || index is < 0 or > 8) return;
            PlaybackEqExpander.IsExpanded = true; playbackEqSliders[index].BringIntoView(); playbackEqSliders[index].Focus();
            PlaybackActivity.HighlightBand(index);
        };
        Closed += (_, _) => StopPlaybackMonitor("Stopped.");
    }
    private void PlaybackMonitorChanged(object sender, RoutedEventArgs e)
    {
        if (changingPlaybackMonitor) return;
        if (PlaybackMonitorEnabledCheck.IsChecked != true) { StopPlaybackMonitor("Stopped. EQ settings are still shown."); return; }
        if (Selected is not { Direction: AudioDirection.Playback } endpoint) { StopPlaybackMonitor("Select a sound output."); return; }
        try {
            playbackMonitor = demo ? new DemoMicrophoneMonitor() : new MicrophoneMonitor(() => WasapiMeasurementSource.OpenPlaybackLoopback(endpoint));
            PlaybackMonitorStatus.Text = demo ? "Demo activity · synthetic levels" : "Starting Windows output analysis…";
            playbackMonitorTimer.Start(); PollPlaybackMonitor();
        } catch (Exception ex) { StopPlaybackMonitor("Output view unavailable: " + ex.Message); }
    }
    private void StopPlaybackMonitor(string reason)
    {
        playbackMonitorTimer.Stop(); playbackMonitor?.Dispose(); playbackMonitor = null;
        if (PlaybackMonitorEnabledCheck is null) return;
        changingPlaybackMonitor = true;
        try { PlaybackMonitorEnabledCheck.IsChecked = false; } finally { changingPlaybackMonitor = false; }
        PlaybackActivity.SetFrame(null); PlaybackMonitorLevel.Value = -90; PlaybackMonitorLevels.Text = "No live audio values"; PlaybackMonitorStatus.Text = reason;
    }
    private void PollPlaybackMonitor()
    {
        if (playbackMonitor is null) return;
        if (playbackMonitor.Error is { } error) { StopPlaybackMonitor("Output view unavailable: " + error); return; }
        var frame = playbackMonitor.Latest;
        if (frame is null || DateTimeOffset.UtcNow - frame.CapturedAt > TimeSpan.FromSeconds(1)) {
            PlaybackActivity.SetFrame(null); PlaybackMonitorLevel.Value = -90; PlaybackMonitorLevels.Text = "Waiting for playback…"; return;
        }
        PlaybackActivity.SetFrame(frame); PlaybackMonitorLevel.Value = Math.Clamp(frame.RmsDbFs, -90, 0);
        PlaybackMonitorLevels.Text = $"RMS {frame.RmsDbFs:0.#} dBFS · peak {frame.PeakDbFs:0.#} dBFS" + (frame.Clipping ? " · clipping" : "");
        PlaybackMonitorStatus.Text = demo ? "Demo activity · synthetic levels" : "Live playback activity · no recording saved";
        if (frame.InvalidSamples > 0) PlaybackMonitorStatus.Text += " · invalid samples detected";
        if (frame.InitialDiscontinuities > 0) PlaybackMonitorStatus.Text += " · initial capture discontinuity observed";
        if (frame.TimestampErrors > 0) PlaybackMonitorStatus.Text += " · timestamp uncertainty observed";
        if (frame.Discontinuities > 0) PlaybackMonitorStatus.Text += " · stream interruptions observed";
    }
    private void UpdatePlaybackMonitorControls()
    {
        if (PlaybackActivity is null) return;
        PlaybackActivity.SetControls(currentPlayback is not null && playbackEqSliders.Count == 9 ? playbackEqSliders.Select(s => (float)s.Value).ToArray() : null,
            true, playbackPending, null, false);
    }
    internal void ShowDemoPlaybackMonitor()
    {
        SelectDemoSoundPage(); PlaybackMonitorEnabledCheck.IsChecked = true; UpdatePlaybackMonitorControls();
    }
}
