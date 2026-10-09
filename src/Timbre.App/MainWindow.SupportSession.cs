using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private readonly ISupportSession supportSession;
    private SupportSessionStatus supportStatus = new("Idle", "Background support is off.");
    private bool supportStarting, closeAfterSupport;
    private bool SupportActive => supportStarting || supportStatus.Active;
    private bool SupportPaused => supportStarting || supportStatus.Paused;
    private Dictionary<UIElement, bool>? supportGuard;
    private void LoadSupportSession()
    {
        if (SupportInfo is null) return;
        string? unavailable = null;
        if (!SupportActive) {
            try { GsxProcessingRestoreSession.SelectPair(endpoints); unavailable = supportSession.UnavailableReason; }
            catch (Exception ex) { unavailable = ex.Message; }
        }
        SupportInfo.Text = unavailable ?? supportStatus.Message;
        StartSupportButton.IsEnabled = !SupportActive && !HelperPilotBusy && !usbWorkBusy && unavailable is null;
        StopSupportButton.IsEnabled = supportStatus.State == "Running" && !usbWorkBusy;
        OpenSupportReportsButton.IsEnabled = !demo && supportStatus.ReportDirectory is { } directory && Directory.Exists(directory);
    }
    private async void StartSupportClick(object sender, RoutedEventArgs e)
    {
        if (SupportActive || HelperPilotBusy || usbWorkBusy) return;
        try {
            if (pending || effectsPending || playbackPending || sidetonePending || gsxSidetonePending || liveQueue.HasPending || gsxLiveQueue.HasPending)
                throw new InvalidOperationException("Apply or discard pending edits before starting background support.");
            var snapshot = endpoints;
            StopMicrophoneMonitor("Stopped during support startup."); StopPlaybackMonitor("Stopped during support startup."); CancelLiveChanges();
            supportStarting = true; GuardSupport(true); LoadSupportSession(); LoadHelperPilot();
            supportStatus = demo ? supportSession.Start(snapshot) : await Task.Run(() => supportSession.Start(snapshot));
            Status.Text = supportStatus.Message;
        } catch (Exception ex) { supportStatus = new("Failed", ex.Message); Error("Background support was not started", ex); }
        finally { supportStarting = false; if (!supportStatus.Paused) GuardSupport(false); LoadSupportSession(); LoadHelperPilot(); }
    }
    private void StopSupportClick(object sender, RoutedEventArgs e)
    {
        if (supportStatus.State != "Running" || usbWorkBusy) return;
        try {
            if (pending || effectsPending || playbackPending || sidetonePending || gsxSidetonePending || liveQueue.HasPending || gsxLiveQueue.HasPending)
                throw new InvalidOperationException("Apply or discard pending edits before stopping background support.");
            RequestSupportStop();
        } catch (Exception ex) { Error("Cannot stop background support", ex); }
    }
    private void RequestSupportStop()
    {
        StopMicrophoneMonitor("Stopped for support recovery."); StopPlaybackMonitor("Stopped for support recovery."); CancelLiveChanges();
        supportStatus = supportSession.Stop(); GuardSupport(true); LoadSupportSession(); LoadHelperPilot(); Status.Text = supportStatus.Message;
    }
    private void PollSupportSession()
    {
        if (supportStarting || !supportStatus.Active) { LoadSupportSession(); return; }
        var previous = supportStatus.State;
        supportStatus = supportSession.Poll();
        GuardSupport(supportStatus.Paused);
        LoadSupportSession(); LoadHelperPilot();
        if (previous != supportStatus.State) {
            Status.Text = supportStatus.Message;
            if (!supportStatus.Paused) RefreshDevices();
            if (!supportStatus.Active) {
                Status.Text = supportStatus.Message;
                if (closeAfterSupport && supportStatus.State == "Stopped") { closeAfterSupport = false; Close(); }
                else closeAfterSupport = false;
            }
        }
    }
    private void GuardSupport(bool active)
    {
        if (active && supportGuard is null) {
            supportGuard = new UIElement[] { DetailPanel, DeviceList, SetupProfilesExpander, RefreshDevicesButton }.ToDictionary(control => control, control => control.IsEnabled);
            foreach (var control in supportGuard.Keys) control.IsEnabled = false;
        } else if (!active && supportGuard is { } guard) {
            foreach (var value in guard) value.Key.IsEnabled = value.Value;
            supportGuard = null;
        }
    }
    private void SupportClosing(object? sender, CancelEventArgs e)
    {
        if (!SupportActive) return;
        e.Cancel = true;
        if (supportStarting || usbWorkBusy) { Status.Text = "Wait for startup or the device update before closing background support."; return; }
        closeAfterSupport = true;
        if (supportStatus.State == "Running") {
            try { RequestSupportStop(); }
            catch (Exception ex) { closeAfterSupport = false; Error("Cannot request support recovery", ex); }
        }
        Status.Text = "Returning control to EPOS before closing Timbre…";
    }
    private void OpenSupportReportsClick(object sender, RoutedEventArgs e)
    {
        if (demo || supportStatus.ReportDirectory is not { } directory || !Directory.Exists(directory)) return;
        try { Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception ex) { Error("Cannot open support reports", ex); }
    }
}
