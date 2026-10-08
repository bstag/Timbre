using System.Windows;
using EposControl.Core;

namespace EposControl.App;

public partial class MainWindow
{
    private void VerifyReverbUi()
    {
        var sound = endpoints.Single(e => e.Direction == AudioDirection.Playback && e.Usb?.ProductId == "0098");
        DeviceList.SelectedItem = sound; LiveApplyCheck.IsChecked = false;
        var before = playback.Read(sound); var audioBefore = backend.Read(sound.Id);
        playback.Apply(sound, before, before with { SurroundEnabled = true, Reverb = new(true, .376f) }); LoadPlayback(true);
        var seeded = playback.Read(sound);
        ReverbSlider.Value = 62; PollCurrent();
        if (playback.Read(sound) != seeded || ReverbSlider.Value != 62 || !playbackPending)
            throw new InvalidOperationException("Polling lost a manual reverb draft or wrote it early.");
        DiscardChangesClick(this, new RoutedEventArgs());
        if (playback.Read(sound) != seeded || playbackPending || playbackReverbLevelEdited)
            throw new InvalidOperationException("Discard failed to restore reverb without writes.");
        ReverbEnabledCheck.IsChecked = false; ApplyPlaybackClick(this, new RoutedEventArgs());
        if (playback.Read(sound).Reverb != new PlaybackReverb(false, .376f) || ReverbSlider.IsEnabled)
            throw new InvalidOperationException("Reverb icon failed to retain the precise stored amount.");
        LiveApplyCheck.IsChecked = true; ReverbEnabledCheck.IsChecked = true; ReverbSlider.Value = 57;
        if (!FlushLiveChanges(true) || playback.Read(sound).Reverb != new PlaybackReverb(true, .57f))
            throw new InvalidOperationException("Live reverb changes failed.");
        var profile = CaptureProfile(sound, "Reverb UI check");
        ReverbSlider.Value = 13; FlushLiveChanges(true);
        ProfileStore.Apply(backend, sound, profile, effects, sidetone, playback); LoadPlayback(true);
        if (playback.Read(sound).Reverb != profile.Playback!.Reverb || ReverbSlider.Value != 57)
            throw new InvalidOperationException("Profile failed to restore reverb.");
        SoundMode.SelectedIndex = 0; FlushLiveChanges(true);
        if (ReverbEnabledCheck.IsEnabled || ReverbSlider.IsEnabled || playback.Read(sound).Reverb != profile.Playback.Reverb)
            throw new InvalidOperationException("Stereo availability changed stored reverb settings.");
        if (backend.Read(sound.Id) != audioBefore) throw new InvalidOperationException("Reverb controls changed output level or mute.");
        playback.Apply(sound, playback.Read(sound), before); LoadPlayback(true);
        DeviceList.SelectedItem = endpoints.First(e => e.Direction == AudioDirection.Microphone);
        if (PlaybackCard.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Reverb leaked into microphone controls.");
    }
}
