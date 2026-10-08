using System.Windows;
using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Threading;
using EposControl.Core;

namespace EposControl.App;

public partial class MainWindow
{
    private readonly IGsxSidetoneBackend gsxSidetone;
    private readonly IUsbWorkRunner usbRunner;
    private int gsxEditEpoch, gsxEditRevision;
    private GsxSidetoneState? currentGsxSidetone;
    private bool gsxSidetonePending, gsxReadBusy, usbWorkBusy;
    private int gsxGeneration;
    private CancellationTokenSource gsxReadCancellation = new();
    private readonly DispatcherTimer gsxLiveTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly LiveApplyQueue gsxLiveQueue = new(TimeSpan.FromMilliseconds(150));
    private Task<bool>? gsxApplyTask;
    private void DeviceUpdateClosing(object? sender, CancelEventArgs e)
    {
        if (!usbWorkBusy) return;
        e.Cancel = true;
        Status.Text = "Finishing the device operation. Close again when it completes.";
    }
    private void ResetGsxSidetone()
    {
        gsxGeneration++; gsxReadCancellation.Cancel(); gsxReadCancellation.Dispose(); gsxReadCancellation = new();
        gsxLiveTimer.Stop(); gsxLiveQueue.Cancel(); gsxReadBusy = false; gsxSidetonePending = false; currentGsxSidetone = null;
        GsxSidetoneCard.Visibility = Visibility.Collapsed;
    }
    private async void LoadGsxSidetone(bool updateControls, bool preserveAcceptedLevel = false)
    {
        if (Selected is not { } endpoint || !GsxSidetoneProtocol.Supports(endpoint)) { GsxSidetoneCard.Visibility = Visibility.Collapsed; return; }
        GsxSidetoneCard.Visibility = Visibility.Visible;
        if (gsxReadBusy || usbWorkBusy) return;
        var generation = gsxGeneration; var cancellation = gsxReadCancellation.Token; gsxReadBusy = true;
        if (currentGsxSidetone is null) { GsxSidetoneSlider.IsEnabled = false; GsxSidetoneStatus.Text = "Reading sidetone…"; GsxSidetoneStatus.Visibility = Visibility.Visible; }
        try {
            var state = demo ? gsxSidetone.Read(endpoint, cancellation) : await Task.Run(() => gsxSidetone.Read(endpoint, cancellation), cancellation);
            if (generation != gsxGeneration || Selected?.Id != endpoint.Id) return;
            GsxSidetoneInfo.Text = $"Current GSX sidetone: {state.Settings.Percent:0.#}%";
            GsxSidetoneSlider.IsEnabled = true; ApplyGsxSidetoneButton.IsEnabled = true; GsxSidetoneStatus.Visibility = Visibility.Collapsed;
            if (updateControls && !gsxSidetonePending || currentGsxSidetone is null)
                ShowGsxSidetone(state, preserveAcceptedLevel && currentGsxSidetone == state);
            ShowFeatures(endpoint);
        } catch (OperationCanceledException) { }
        catch (Exception ex) {
            if (generation != gsxGeneration) return;
            currentGsxSidetone = null; GsxSidetoneSlider.IsEnabled = false; ApplyGsxSidetoneButton.IsEnabled = false;
            gsxSidetonePending = false; gsxLiveTimer.Stop(); GsxSidetoneStatus.Text = "Sidetone unavailable. " + ex.Message; GsxSidetoneStatus.Visibility = Visibility.Visible;
        } finally { if (generation == gsxGeneration) gsxReadBusy = false; UpdateDraftInfo(); }
    }
    private void ShowGsxSidetone(GsxSidetoneState state, bool preserveRequestedLevel = false)
    {
        loading = true;
        try {
            currentGsxSidetone = state; gsxSidetonePending = false;
            if (!preserveRequestedLevel) GsxSidetoneSlider.Value = state.Settings.Percent;
        }
        finally { loading = false; }
        GsxSidetoneValue.Text = $"{GsxSidetoneSlider.Value:0.#}%";
        AutomationProperties.SetHelpText(GsxSidetoneSlider, $"Selected monitoring level {GsxSidetoneValue.Text}; hardware step {state.Settings.Percent:0.#}%.");
    }
    private void GsxSidetoneLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GsxSidetoneValue is not null) GsxSidetoneValue.Text = $"{e.NewValue:0.#}%";
        if (loading || currentGsxSidetone is null) return;
        gsxEditRevision++;
        gsxSidetonePending = true;
        if (LiveApplyCheck.IsChecked == true && CurrentTarget is { } target) {
            gsxLiveQueue.Schedule(target, ControlArea.Sidetone, liveClock.Elapsed);
            if (!gsxLiveTimer.IsEnabled) gsxLiveTimer.Start();
        }
        UpdateDraftInfo();
    }
    private async void ApplyGsxSidetoneClick(object sender, RoutedEventArgs e) => await FlushGsxSidetoneAsync();
    private async void GsxLiveTick(object? sender, EventArgs e)
    {
        if (gsxApplyTask is not null) return;
        if (gsxLiveQueue.Take(CurrentTarget, liveClock.Elapsed).Count != 0) await FlushGsxSidetoneAsync();
        else if (!gsxLiveQueue.HasPending) gsxLiveTimer.Stop();
    }
    private async Task<bool> FlushGsxSidetoneAsync()
    {
        if (gsxApplyTask is not null) return await gsxApplyTask;
        gsxLiveTimer.Stop(); gsxLiveQueue.Cancel();
        if (!gsxSidetonePending) return true;
        gsxApplyTask = DrainGsxSidetoneAsync(CurrentTarget, gsxEditEpoch);
        try { return await gsxApplyTask; } finally { gsxApplyTask = null; }
    }
    private async Task<bool> DrainGsxSidetoneAsync(ControlTarget? target, int epoch)
    {
        while (true) {
            if (epoch != gsxEditEpoch || CurrentTarget != target) return false;
            if (!await ApplyGsxSidetoneAsync()) { gsxLiveTimer.Stop(); gsxLiveQueue.Cancel(); return false; }
            if (!gsxSidetonePending || LiveApplyCheck.IsChecked != true) return true;
            // Edits made during the transaction remain drafts until the next bounded quiet interval.
            while (gsxLiveQueue.HasPending) {
                if (epoch != gsxEditEpoch || CurrentTarget != target) return false;
                if (gsxLiveQueue.Take(target, liveClock.Elapsed).Count != 0) break;
                await Task.Delay(25);
            }
            if (epoch != gsxEditEpoch || CurrentTarget != target) return false;
            if (!gsxSidetonePending) return true;
        }
    }
    private async Task<bool> ApplyGsxSidetoneAsync()
    {
        if (Selected is not { } endpoint || currentGsxSidetone is not { } expected) { Status.Text = "GSX sidetone is unavailable."; return false; }
        var desired = GsxSidetoneSettings.FromPercent(GsxSidetoneSlider.Value);
        var revision = gsxEditRevision;
        try {
            var result = await RunUsbWork(() => gsxSidetone.Apply(endpoint, expected, desired), allowSidetoneEdits: true);
            if (Selected?.Id != endpoint.Id) return false;
            // Keep the user's position: GSX hardware has fewer steps than the percentage slider.
            if (revision == gsxEditRevision) ShowGsxSidetone(result, preserveRequestedLevel: true);
            else currentGsxSidetone = result;
            GsxSidetoneInfo.Text = $"Current GSX sidetone: {result.Settings.Percent:0.#}%";
            if (result.Settings != expected.Settings) MarkSetupPageEdited(endpoint);
            Status.Text = "Applied GSX sidetone."; return true;
        } catch (Exception ex) {
            gsxSidetonePending = false; Error("GSX sidetone was not applied", ex); LoadGsxSidetone(true); return false;
        } finally { UpdateDraftInfo(); }
    }
    private async Task<T> RunUsbWork<T>(Func<T> work, bool allowSidetoneEdits = false)
    {
        if (usbWorkBusy) throw new InvalidOperationException("A device update is still in progress.");
        usbWorkBusy = true; gsxGeneration++; gsxReadCancellation.Cancel(); gsxReadCancellation.Dispose(); gsxReadCancellation = new(); gsxReadBusy = false;
        FrameworkElement[] guarded = allowSidetoneEdits
            ? [DevicePageNavigation, DraftControls, VolumeControls, PlaybackCard, MicrophoneProcessingEditors, SidetoneCard,
                ApplyGsxSidetoneButton, ProfilesExpander, FeaturesExpander, DeviceList, SetupProfilesExpander, RefreshDevicesButton]
            : [DetailPanel, DeviceList, SetupProfilesExpander, RefreshDevicesButton];
        var enabled = guarded.Select(control => control.IsEnabled).ToArray();
        foreach (var control in guarded) control.IsEnabled = false;
        try { return await usbRunner.Run(work); }
        finally { usbWorkBusy = false; for (var i = 0; i < guarded.Length; i++) guarded[i].IsEnabled = enabled[i]; }
    }
    private Task<GsxSidetoneSettings?> CaptureGsxSidetoneAsync(AudioEndpoint endpoint) => GsxSidetoneProtocol.Supports(endpoint)
        ? RunUsbWork<GsxSidetoneSettings?>(() => gsxSidetone.Read(endpoint).Settings) : Task.FromResult<GsxSidetoneSettings?>(null);
}
