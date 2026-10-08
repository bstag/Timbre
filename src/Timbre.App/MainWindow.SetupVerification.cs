using System.IO;
using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private void VerifySetupDemoUi()
    {
        if (!demo) throw new InvalidOperationException("Setup UI checks require demo adapters.");
        if (SetupProfilesExpander.IsExpanded || setups.Load().Count != 0 || setupChoices.Any(c => c.IsIncluded) || SelectedSetupIncludeCheck.IsChecked == true || SaveSetupAsButton.IsEnabled)
            throw new InvalidOperationException("New setups must start with no automatically included pages.");
        editedSetupPages.Clear(); PollCurrent();
        if (editedSetupPages.Count != 0) throw new InvalidOperationException("Reading device state marked pages as edited.");
        var original = setupControls.Capture("Starting", endpoints);
        var deviceProfileBytes = File.ReadAllBytes(profiles.FilePath);
        var mic = endpoints.First(IsB20); var output = endpoints.First(e => e.Direction == AudioDirection.Playback);
        try {
            DeviceList.SelectedItem = mic; SelectedSetupIncludeCheck.IsChecked = true;
            DeviceList.SelectedItem = output;
            if (SelectedSetupIncludeCheck.IsChecked == true) throw new InvalidOperationException("Microphone inclusion leaked into the sound page.");
            SelectedSetupIncludeCheck.IsChecked = true;
            DeviceList.SelectedItem = mic;
            if (SelectedSetupIncludeCheck.IsChecked != true || setupChoices.Count(c => c.IsIncluded) != 2)
                throw new InvalidOperationException("Device-page inclusion checkbox did not synchronize with the setup selection.");
            SetupName.Text = "Gaming"; SaveSetupClick(this, new RoutedEventArgs());
            var gaming = setups.Load().Single();
            if (gaming.Devices.Count != 2 || (SetupList.SelectedItem as SetupProfile)?.Name != "Gaming" || !ApplySetupButton.IsEnabled || !SaveSelectedSetupButton.IsEnabled)
                throw new InvalidOperationException("Save as setup did not create/select the chosen device snapshots.");
            var connectedEndpoints = endpoints;
            var audioBeforeGhostChoice = backend.Read(mic.Id);
            try {
                endpoints = endpoints.Where(e => e.Id != mic.Id).ToArray(); RefreshSetupChoices();
                var ghost = setupChoices.Single(c => c.Endpoint is null);
                if (!ghost.IsIncluded || !ghost.Label.Contains("disconnected") || ghost.SavedDevice is null) throw new InvalidOperationException("Disconnected included page was not shown with its saved checkbox.");
                ghost.IsIncluded = false; RefreshSetupChoices();
                if (setupChoices.Single(c => c.Endpoint is null).IsIncluded) throw new InvalidOperationException("Refresh re-included an unchecked disconnected page.");
                endpoints = connectedEndpoints; RefreshSetupChoices();
                if (setupChoices.Single(c => c.Endpoint?.Id == mic.Id).IsIncluded || backend.Read(mic.Id) != audioBeforeGhostChoice)
                    throw new InvalidOperationException("Reconnect lost an explicit exclusion or changed hardware.");
            } finally { endpoints = connectedEndpoints; RebuildSetupChoices(true); }
            DeviceList.SelectedItem = mic; GateSlider.Value = 73; EqPreset.SelectedIndex = 1;
            SaveSelectedSetupClick(this, new RoutedEventArgs());
            gaming = setups.Load().Single();
            if (liveQueue.HasPending || gaming.Devices.Single(d => d.Settings.Direction == AudioDirection.Microphone).Settings.Effects?.GatePercent != 73)
                throw new InvalidOperationException("Saving to a setup did not finish and capture live microphone changes.");
            DeviceList.SelectedItem = output; SoundMode.SelectedIndex = 1; playbackEqSliders[0].Value = -2;
            SetupName.Text = "Streaming"; SaveSetupClick(this, new RoutedEventArgs());
            var streaming = setups.Load().Single(s => s.Name == "Streaming");
            if (streaming.Devices.Single(d => d.Settings.Direction == AudioDirection.Playback).Settings.Playback?.SurroundEnabled != true ||
                streaming.Devices.Single(d => d.Settings.Direction == AudioDirection.Playback).Settings.Playback?.Equalizer?.Band1 != -2 || liveQueue.HasPending)
                throw new InvalidOperationException("Saving a new setup did not finish and capture live sound edits.");
            if (!setups.Load().Single(s => s.Name == "Gaming").Devices.SequenceEqual(gaming.Devices))
                throw new InvalidOperationException("Auditioning or saving another setup changed Gaming.");
            LiveApplyCheck.IsChecked = false;
            var audioBeforeDraft = backend.Read(output.Id);
            LevelSlider.Value = 22; SetupName.Text = "Meeting"; SaveSetupClick(this, new RoutedEventArgs());
            if (setups.Load().Single(s => s.Name == "Meeting").Devices.Single(d => d.Settings.Direction == AudioDirection.Playback).Settings.Level != audioBeforeDraft.Level || backend.Read(output.Id) != audioBeforeDraft)
                throw new InvalidOperationException("Unapplied manual drafts were saved or applied by setup capture.");
            DiscardChangesClick(this, new RoutedEventArgs()); LiveApplyCheck.IsChecked = true;
            var unrelated = endpoints.Single(e => e.Direction == AudioDirection.Microphone && !IsB20(e));
            var unrelatedBefore = backend.Read(unrelated.Id);
            SetupList.SelectedItem = SetupList.Items.Cast<SetupProfile>().Single(s => s.Name == "Gaming");
            playbackEqSliders[1].Value = 5;
            ApplySetupClick(this, new RoutedEventArgs()); FlushLiveChanges(true);
            foreach (var device in gaming.Devices) {
                var endpoint = endpoints.Single(e => ProfileStore.BelongsTo(e, device.Settings));
                var current = backend.Read(endpoint.Id);
                if (current.Level != device.Settings.Level || current.Muted != device.Settings.Muted) throw new InvalidOperationException("Setup did not restore its endpoint state.");
                if (device.Settings.Effects is { } desired && !ApoMicrophoneCodec.Matches(effects.Read(endpoint), desired)) throw new InvalidOperationException("Setup did not restore its microphone settings.");
                if (device.Settings.Playback is { } sound && !ApoPlaybackCodec.Matches(playback.Read(endpoint), sound)) throw new InvalidOperationException("Queued sound edits overrode the loaded setup.");
            }
            if (backend.Read(unrelated.Id) != unrelatedBefore || liveQueue.HasPending || !Status.Text.Contains("2 applied"))
                throw new InvalidOperationException("Setup load affected an excluded device or left queued work.");
            if (!File.ReadAllBytes(profiles.FilePath).SequenceEqual(deviceProfileBytes))
                throw new InvalidOperationException("Setup operations modified named device profiles.");
            if (!ApoMicrophoneCodec.Matches(processingStates.Load(mic)!.Effects, gaming.Devices.Single(d => d.Settings.Direction == AudioDirection.Microphone).Settings.Effects!))
                throw new InvalidOperationException("Applying a setup did not remember B20 processing for restore.");
            setupChoices.Single(c => c.Endpoint?.Id == unrelated.Id).IsIncluded = true;
            SetupName.Text = "Full desk"; SaveSetupClick(this, new RoutedEventArgs());
            if (setups.Load().Single(s => s.Name == "Full desk").Devices.Count != 3) throw new InvalidOperationException("An additional selected device page was not saved.");
            foreach (var choice in setupChoices) choice.IsIncluded = false;
            var beforeEmptySave = File.ReadAllBytes(setups.FilePath);
            SaveSelectedSetupClick(this, new RoutedEventArgs());
            if (!File.ReadAllBytes(setups.FilePath).SequenceEqual(beforeEmptySave) || SaveSetupAsButton.IsEnabled || SaveSelectedSetupButton.IsEnabled)
                throw new InvalidOperationException("An empty inclusion selection overwrote a setup.");
            setupChoices.Single(c => c.Endpoint?.Id == mic.Id).IsIncluded = true;
            setupChoices.Single(c => c.Endpoint?.Id == output.Id).IsIncluded = true;
            var beforeMembershipApply = backend.Read(unrelated.Id);
            ApplySetupClick(this, new RoutedEventArgs());
            if (ApplySetupButton.IsEnabled || !Status.Text.StartsWith("Save inclusion changes") || backend.Read(unrelated.Id) != beforeMembershipApply)
                throw new InvalidOperationException("Unsaved checkbox changes allowed application of the old membership.");
            SaveSelectedSetupClick(this, new RoutedEventArgs());
            var full = setups.Load().Single(s => s.Name == "Full desk");
            if (full.Devices.Count != 2 || full.Devices.Any(d => ProfileStore.BelongsTo(unrelated, d.Settings)) || !ApplySetupButton.IsEnabled)
                throw new InvalidOperationException("Save to selected did not remove the unchecked page.");
            ApplySetupClick(this, new RoutedEventArgs());
            if (backend.Read(unrelated.Id) != beforeMembershipApply) throw new InvalidOperationException("Applying the updated setup changed an excluded device.");
            setups.Save(full with { Devices = full.Devices.Select((d, i) => i == 0 ? d with { Settings = d.Settings with { Level = .321f } } : d).ToArray() });
            SaveSelectedSetupClick(this, new RoutedEventArgs());
            if (!Status.Text.StartsWith("Cannot save to selected setup") || setups.Load().Single(s => s.Name == "Full desk").Devices[0].Settings.Level != .321f)
                throw new InvalidOperationException("A stale selected setup overwrote a newer save.");
            ReloadSetupsClick(this, new RoutedEventArgs());
            if ((SetupList.SelectedItem as SetupProfile)?.Devices[0].Settings.Level != .321f) throw new InvalidOperationException("Reload setups did not refresh its selected snapshot.");
            editedSetupPages.Clear(); ApplySetupClick(this, new RoutedEventArgs());
            if (editedSetupPages.Count != 0) throw new InvalidOperationException("Applying saved settings marked device pages as direct edits.");
            DeviceList.SelectedItem = mic; GateSlider.Value = 34;
            SelectEditedSetupPagesClick(this, new RoutedEventArgs());
            if (liveQueue.HasPending || setupChoices.Count(c => c.IsIncluded) != 1 || !setupChoices.Single(c => c.Endpoint?.Id == mic.Id).IsIncluded || ApplySetupButton.IsEnabled)
                throw new InvalidOperationException("Select edited pages did not flush a pending successful change and select only its page.");
            SetupName.Text = "Mic only"; SaveSetupClick(this, new RoutedEventArgs());
            if (setups.Load().Single(s => s.Name == "Mic only").Devices.Count != 1) throw new InvalidOperationException("Saving edited pages included untouched pages.");
            DeviceList.SelectedItem = output; LiveApplyCheck.IsChecked = false; LevelSlider.Value = 19;
            SelectEditedSetupPagesClick(this, new RoutedEventArgs());
            if (setupChoices.Any(c => c.IsIncluded && c.Endpoint?.Id == output.Id)) throw new InvalidOperationException("An unapplied manual draft was marked as an edited page.");
            DiscardChangesClick(this, new RoutedEventArgs()); LiveApplyCheck.IsChecked = true;
            var bytes = File.ReadAllBytes(setups.FilePath);
            playbackEqSliders[0].Value = 3;
            var newer = playback.Read(output) with { SurroundEnabled = !playback.Read(output).SurroundEnabled };
            playback.Apply(output, playback.Read(output), newer);
            SaveSelectedSetupClick(this, new RoutedEventArgs());
            if (!File.ReadAllBytes(setups.FilePath).SequenceEqual(bytes) || !Status.Text.EndsWith("Setup was not saved."))
                throw new InvalidOperationException("Failed live changes did not block setup saving.");
            if (editedSetupPages.Contains(SetupKey(output))) throw new InvalidOperationException("A failed live update marked the output page as successfully edited.");
        }
        finally {
            CancelLiveChanges(); setupControls.Apply(original); ReloadSelectedAfterSetup();
            LoadSetups("Gaming"); SetupName.Text = "Meeting"; SetupResult.Text = "";
            DeviceList.SelectedItem = mic; SetupProfilesExpander.IsExpanded = false;
        }
    }
}
