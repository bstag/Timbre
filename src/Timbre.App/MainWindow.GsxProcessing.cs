using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private readonly GsxProcessingStateStore gsxProcessingStates;
    private readonly GsxProcessingRestoreSession gsxProcessingRestore;
    private readonly IGsxProcessingBackend gsxProcessing;

    private (AudioEndpoint Microphone, AudioEndpoint Playback) SelectedGsxPair()
    {
        var pair = GsxProcessingRestoreSession.SelectPair(endpoints);
        if (Selected?.Id != pair.Microphone.Id && Selected?.Id != pair.Playback.Id)
            throw new InvalidOperationException("Select this GSX microphone or sound page first.");
        return pair;
    }
    private void LoadSavedGsxProcessing()
    {
        GsxProcessingExpander.Visibility = Selected is { } endpoint && GsxProcessingRestoreSession.IsGsx(endpoint)
            ? Visibility.Visible : Visibility.Collapsed;
        if (GsxProcessingExpander.Visibility != Visibility.Visible) return;
        var wasLoading = loading; loading = true;
        try {
            var (mic, sound) = SelectedGsxPair();
            var saved = gsxProcessingStates.Load(mic, sound);
            // Do not offer operations on an absent/partial processor interface.
            gsxProcessing.Read(mic, sound);
            SaveGsxProcessingButton.IsEnabled = true;
            RestoreGsxProcessingButton.IsEnabled = saved is not null;
            RestoreGsxProcessingCheck.IsEnabled = saved is not null;
            RestoreGsxProcessingCheck.IsChecked = saved?.RestoreOnConnect == true;
            SavedGsxProcessingInfo.Text = saved is null ? "No saved processing yet. Save the applied settings for this device."
                : $"Device processing saved {saved.SavedAtUtc.ToLocalTime():g}. Live adjustments do not replace this save.";
        } catch (Exception ex) {
            SaveGsxProcessingButton.IsEnabled = RestoreGsxProcessingButton.IsEnabled = RestoreGsxProcessingCheck.IsEnabled = false;
            RestoreGsxProcessingCheck.IsChecked = false;
            SavedGsxProcessingInfo.Text = "Saved processing unavailable: " + ex.Message;
        } finally { loading = wasLoading; }
    }
    private void SaveGsxProcessingClick(object sender, RoutedEventArgs e)
    {
        if (usbWorkBusy) return;
        try {
            if (LiveApplyCheck.IsChecked == true && !FlushLiveChanges(true)) return;
            var (mic, sound) = SelectedGsxPair();
            gsxProcessingStates.Remember(mic, sound, gsxProcessing.Read(mic, sound));
            Status.Text = "Saved current GSX microphone and sound processing. Volume, mute and sidetone are separate.";
        } catch (Exception ex) { Error("Cannot save GSX processing", ex); }
        LoadSavedGsxProcessing();
    }
    private void RestoreGsxProcessingChanged(object sender, RoutedEventArgs e)
    {
        if (loading || usbWorkBusy) return;
        try {
            var (mic, sound) = SelectedGsxPair();
            gsxProcessingStates.SetRestoreOnConnect(mic, sound, RestoreGsxProcessingCheck.IsChecked == true);
            Status.Text = RestoreGsxProcessingCheck.IsChecked == true
                ? "Saved GSX microphone and sound processing will restore on the next observed connection or Timbre launch."
                : "Automatic GSX processing restore is off.";
        } catch (Exception ex) { Error("Cannot save GSX restore preference", ex); LoadSavedGsxProcessing(); }
    }
    private void RestoreGsxProcessingClick(object sender, RoutedEventArgs e)
    {
        if (usbWorkBusy) return;
        CancelLiveChanges();
        try {
            var (mic, sound) = SelectedGsxPair();
            gsxProcessingStates.Restore(mic, sound, backend, gsxProcessing);
            Status.Text = "Restored saved GSX microphone and sound processing.";
        } catch (Exception ex) { Error("Cannot restore GSX processing", ex); }
        LoadEffects(true); LoadPlayback(true); LoadSavedGsxProcessing(); UpdateDraftInfo();
    }
    private void CheckGsxProcessingRestore()
    {
        if (usbWorkBusy) return;
        (AudioEndpoint Microphone, AudioEndpoint Playback) pair;
        try { pair = GsxProcessingRestoreSession.SelectPair(endpoints); } catch { return; }
        if ((Selected?.Id == pair.Microphone.Id || Selected?.Id == pair.Playback.Id) && (effectsPending || playbackPending)) return;
        // Wait for both existing pages without consuming the connection attempt.
        try { gsxProcessing.Read(pair.Microphone, pair.Playback); } catch { return; }
        try {
            if (gsxProcessingRestore.RestoreIfEnabled(pair.Microphone, pair.Playback, backend, gsxProcessing) is null) return;
            if (Selected?.Id == pair.Microphone.Id || Selected?.Id == pair.Playback.Id) {
                LoadEffects(true); LoadPlayback(true); LoadSavedGsxProcessing();
            }
            Status.Text = "Restored saved GSX microphone and sound processing.";
        } catch (Exception ex) { Error("Automatic GSX processing restore failed; use Restore saved processing to retry", ex); }
    }
}
