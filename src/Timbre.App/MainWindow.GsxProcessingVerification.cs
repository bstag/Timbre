using System.IO;
using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    internal void ShowDemoGsxProcessing()
    {
        SelectDemoSoundPage(); FeaturesExpander.IsExpanded = true; GsxProcessingExpander.IsExpanded = true;
    }
    internal void ScrollDemoGsxProcessingIntoView() => GsxProcessingExpander.BringIntoView();
    private void VerifyGsxProcessingUi()
    {
        var (mic, sound) = GsxProcessingRestoreSession.SelectPair(endpoints);
        var b20 = endpoints.First(IsB20);
        var before = gsxProcessing.Read(mic, sound);
        var b20Before = effects.Read(b20);
        var audioBefore = endpoints.ToDictionary(e => e.Id, e => backend.Read(e.Id));
        var sidetoneBefore = gsxSidetone.Read(mic);
        var profilesBefore = profiles.ForDevice(mic).Concat(profiles.ForDevice(sound)).ToArray();
        DeviceList.SelectedItem = sound;
        if (GsxProcessingExpander.Visibility != Visibility.Visible || GsxProcessingExpander.IsExpanded ||
            !SaveGsxProcessingButton.IsEnabled || RestoreGsxProcessingButton.IsEnabled || RestoreGsxProcessingCheck.IsChecked == true)
            throw new InvalidOperationException("GSX processing must start unsaved, collapsed and opted out on either page.");
        LiveApplyCheck.IsChecked = false;
        SoundMode.SelectedIndex = before.Playback.SurroundEnabled ? 0 : 1;
        SaveGsxProcessingClick(this, new RoutedEventArgs());
        var saved = gsxProcessingStates.Load(mic, sound)!;
        if (!saved.Effects.Matches(before) || saved.RestoreOnConnect || !playbackPending || !RestoreGsxProcessingButton.IsEnabled)
            throw new InvalidOperationException("GSX save captured manual drafts or implicitly enabled restore.");
        DiscardChangesClick(this, new RoutedEventArgs());
        LiveApplyCheck.IsChecked = true;
        SoundMode.SelectedIndex = before.Playback.SurroundEnabled ? 0 : 1;
        playbackEqSliders[4].Value = 3;
        SaveGsxProcessingClick(this, new RoutedEventArgs());
        saved = gsxProcessingStates.Load(mic, sound)!;
        if (saved.Effects.Playback.SurroundEnabled == before.Playback.SurroundEnabled ||
            saved.Effects.Playback.Equalizer!.Band5 != 3 || playbackPending || liveQueue.HasPending)
            throw new InvalidOperationException("GSX save did not flush live processing before capture.");
        DeviceList.SelectedItem = mic;
        if (GsxProcessingExpander.Visibility != Visibility.Visible || !RestoreGsxProcessingButton.IsEnabled)
            throw new InvalidOperationException("GSX saved processing is not shared by both device pages.");
        GateSlider.Value = 73;
        FlushLiveChanges(true);
        if (gsxProcessingStates.Load(mic, sound) != saved)
            throw new InvalidOperationException("GSX auditioning replaced explicitly saved processing.");
        RestoreGsxProcessingCheck.IsChecked = true;
        CheckProcessingRestores();
        if (effects.Read(mic).GatePercent != 73)
            throw new InvalidOperationException("Enabling restore immediately changed GSX processing.");
        // An observed reconnect restores both pages, including when B20 is selected.
        DeviceList.SelectedItem = b20;
        gsxProcessingRestore.Observe([]); gsxProcessingRestore.Observe(endpoints); CheckProcessingRestores();
        if (!gsxProcessing.Read(mic, sound).Matches(saved.Effects) || GsxProcessingExpander.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("GSX paired reconnect restoration or B20 visibility isolation failed.");
        effects.Apply(mic, effects.Read(mic), saved.Effects.Microphone with { GatePercent = 41 });
        PollCurrent();
        if (effects.Read(mic).GatePercent != 41)
            throw new InvalidOperationException("GSX polling overwrote external edits after its one restore attempt.");
        DeviceList.SelectedItem = sound;
        SoundMode.SelectedIndex = saved.Effects.Playback.SurroundEnabled ? 0 : 1;
        RestoreGsxProcessingClick(this, new RoutedEventArgs());
        if (!gsxProcessing.Read(mic, sound).Matches(saved.Effects) || liveQueue.HasPending || playbackPending)
            throw new InvalidOperationException("Manual GSX restore did not restore both pages and cancel drafts.");
        if (effects.Read(b20) != b20Before || endpoints.Any(e => backend.Read(e.Id) != audioBefore[e.Id]) ||
            gsxSidetone.Read(mic) != sidetoneBefore || !profiles.ForDevice(mic).Concat(profiles.ForDevice(sound)).SequenceEqual(profilesBefore))
            throw new InvalidOperationException("GSX processing save/restore changed B20, volume, sidetone or named profiles.");
        // Corruption remains intact and blocks saving/restoration rather than resetting policy.
        var stateFile = Directory.EnumerateFiles(gsxProcessingStates.DirectoryPath, "*.json").Single();
        var original = File.ReadAllText(stateFile); File.WriteAllText(stateFile, "{}");
        LoadSavedGsxProcessing(); SaveGsxProcessingClick(this, new RoutedEventArgs());
        if (SaveGsxProcessingButton.IsEnabled || RestoreGsxProcessingButton.IsEnabled || RestoreGsxProcessingCheck.IsEnabled ||
            File.ReadAllText(stateFile) != "{}" || !gsxProcessing.Read(mic, sound).Matches(saved.Effects))
            throw new InvalidOperationException("Corrupt GSX processing was reset or changed hardware.");
        File.WriteAllText(stateFile, original);
        LoadSavedGsxProcessing(); RestoreGsxProcessingCheck.IsChecked = false;
        gsxProcessing.Apply(mic, sound, gsxProcessing.Read(mic, sound), before);
        DeviceList.SelectedItem = b20;
    }
}
