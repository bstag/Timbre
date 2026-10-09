using System.ComponentModel;
using System.IO;
using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    internal void ShowDemoGsxHelper()
    {
        SelectDemoSoundPage();
        FeaturesExpander.IsExpanded = true; GsxHelperExpander.IsExpanded = true;
    }
    internal void ScrollDemoGsxHelperIntoView() => GsxHelperExpander.BringIntoView();
    private void VerifyGsxHelperUi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "timbre-helper-ui-" + Guid.NewGuid().ToString("N"));
        var pilot = new DemoGsxHelperPilot();
        var audio = new DemoAudioBackend();
        var window = new MainWindow(audio, catalog, new ProfileStore(Path.Combine(directory, "profiles.json")),
            new ProcessingStateStore(Path.Combine(directory, "processing")), new SetupProfileStore(Path.Combine(directory, "setups.json")),
            true, new InlineUsbWorkRunner(), pilot);
        try {
            var pair = GsxProcessingRestoreSession.SelectPair(window.endpoints);
            var before = window.gsxProcessing.Read(pair.Microphone, pair.Playback);
            var states = window.endpoints.ToDictionary(endpoint => endpoint.Id, endpoint => audio.Read(endpoint.Id));
            if (window.GsxHelperExpander.Visibility != Visibility.Collapsed || pilot.Starts != 0)
                throw new InvalidOperationException("B20 selection or app startup exposed/launched a GSX helper check.");
            window.DeviceList.SelectedItem = pair.Playback;
            if (window.GsxHelperExpander.Visibility != Visibility.Visible || window.GsxHelperExpander.IsExpanded || !window.RunGsxHelperButton.IsEnabled ||
                window.GsxHelperReconnectCheck.IsChecked == true || window.OpenGsxHelperReportsButton.IsEnabled)
                throw new InvalidOperationException("Helper checks must start collapsed, idle and without physical reconnect selected.");
            window.LiveApplyCheck.IsChecked = false;
            window.SoundMode.SelectedIndex = before.Playback.SurroundEnabled ? 0 : 1;
            window.RunGsxHelperClick(window, new RoutedEventArgs());
            if (pilot.Starts != 0 || !window.playbackPending || !window.DetailPanel.IsEnabled)
                throw new InvalidOperationException("Helper check launched with pending drafts or discarded them implicitly.");
            window.DiscardChangesClick(window, new RoutedEventArgs());
            window.GsxHelperReconnectCheck.IsChecked = true;
            window.RunGsxHelperClick(window, new RoutedEventArgs());
            if (!window.HelperPilotBusy || pilot.Starts != 1 || !pilot.LastReconnect || window.DetailPanel.IsEnabled || window.DeviceList.IsEnabled ||
                window.SetupProfilesExpander.IsEnabled || window.RefreshDevicesButton.IsEnabled)
                throw new InvalidOperationException("Helper check did not pause conflicting controls and forward the explicit reconnect option.");
            var close = new CancelEventArgs(); window.HelperPilotClosing(window, close);
            if (!close.Cancel) throw new InvalidOperationException("Window closed while helper recovery was pending.");
            window.RunGsxHelperClick(window, new RoutedEventArgs());
            window.PollCurrent();
            if (pilot.Starts != 1 || !window.HelperPilotBusy || window.OpenGsxHelperReportsButton.IsEnabled || !window.gsxProcessing.Read(pair.Microphone, pair.Playback).Matches(before))
                throw new InvalidOperationException("Polling/repeated helper launch changed processing or exposed a real demo reports folder.");
            pilot.Status = new(false, "Demo helper check passed and recovered.", Passed: true);
            window.PollCurrent();
            if (window.HelperPilotBusy || !window.DetailPanel.IsEnabled || !window.DeviceList.IsEnabled || !window.SetupProfilesExpander.IsEnabled ||
                !window.RefreshDevicesButton.IsEnabled || !window.RunGsxHelperButton.IsEnabled || !window.GsxHelperInfo.Text.Contains("passed"))
                throw new InvalidOperationException("Helper completion did not release UI guards and preserve its result.");
            window.RunGsxHelperClick(window, new RoutedEventArgs());
            pilot.Status = new(false, "Demo recovery failed; review reports.", Passed: false); window.PollCurrent();
            if (window.HelperPilotBusy || !window.GsxHelperInfo.Text.Contains("failed")) throw new InvalidOperationException("Failed recovery was displayed as success or left stale guards.");
            if (!window.gsxProcessing.Read(pair.Microphone, pair.Playback).Matches(before) || states.Any(state => audio.Read(state.Key) != state.Value) ||
                window.gsxProcessingStates.Load(pair.Microphone, pair.Playback) is not null || window.profiles.ForDevice(pair.Microphone).Count != 0 ||
                window.profiles.ForDevice(pair.Playback).Count != 0)
                throw new InvalidOperationException("Demo helper workflow modified audio, saved state or device profiles.");
        } finally { window.Close(); }
    }
}
