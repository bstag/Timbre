using System.IO;
using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private void VerifyPlaybackMonitorUi()
    {
        var sound = endpoints.Single(e => e.Direction == AudioDirection.Playback && e.Usb?.ProductId == "0098");
        var b20 = endpoints.First(IsB20); var before = playback.Read(sound); var audioBefore = backend.Read(sound.Id);
        var profileBytes = File.ReadAllBytes(profiles.FilePath); var setupBytes = File.ReadAllBytes(setups.FilePath);
        DeviceList.SelectedItem = sound;
        if (PlaybackMonitorEnabledCheck.IsChecked == true || playbackMonitor is not null || PlaybackActivity.Frame is not null || PlaybackActivity.Equalizer is null)
            throw new InvalidOperationException("Sound selection started capture or omitted its stored EQ curve.");
        PlaybackMonitorEnabledCheck.IsChecked = true; PollPlaybackMonitor();
        if (PlaybackActivity.Frame is null || PlaybackActivity.SourceLabel != "Playback" || !PlaybackMonitorLevels.Text.StartsWith("RMS ") ||
            !PlaybackMonitorStatus.Text.Contains("synthetic")) throw new InvalidOperationException("Demo playback activity was not displayed or labeled.");
        LiveApplyCheck.IsChecked = false; playbackEqSliders[2].Value = 1.25; UpdatePlaybackMonitorControls();
        if (PlaybackActivity.Equalizer![2] != 1.25f || playback.Read(sound) != before)
            throw new InvalidOperationException("Playback curve preview failed or wrote a manual draft.");
        DiscardChangesClick(this, new RoutedEventArgs());
        if (!PlaybackActivity.Equalizer!.SequenceEqual(before.Equalizer!.Levels()))
            throw new InvalidOperationException("Discard did not recover the playback curve.");
        PlaybackMonitorEnabledCheck.IsChecked = false;
        if (playbackMonitor is not null || PlaybackActivity.Frame is not null || PlaybackMonitorLevel.Value != -90)
            throw new InvalidOperationException("Playback activity retained stale values after stopping.");
        PlaybackMonitorEnabledCheck.IsChecked = true; DeviceList.SelectedItem = b20;
        if (playbackMonitor is not null || PlaybackMonitorEnabledCheck.IsChecked == true || PlaybackActivity.Frame is not null)
            throw new InvalidOperationException("Microphone navigation retained output capture.");
        DeviceList.SelectedItem = sound;
        if (playbackMonitor is not null || PlaybackMonitorEnabledCheck.IsChecked == true || playback.Read(sound) != before ||
            backend.Read(sound.Id) != audioBefore || !File.ReadAllBytes(profiles.FilePath).SequenceEqual(profileBytes) ||
            !File.ReadAllBytes(setups.FilePath).SequenceEqual(setupBytes)) throw new InvalidOperationException("Playback analysis changed profiles/settings or restarted itself.");
        DeviceList.SelectedItem = b20; LiveApplyCheck.IsChecked = true;
    }
}
