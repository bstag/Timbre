using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private readonly IGsxHelperPilot helperPilot;
    private GsxHelperPilotStatus helperPilotStatus = new(false, "No helper check has run in this app session.");
    private bool helperPilotStarting;
    private bool HelperPilotBusy => helperPilotStarting || helperPilotStatus.Active;
    private Dictionary<UIElement, bool>? helperGuard;
    private void LoadHelperPilot()
    {
        LoadSupportSession();
        GsxHelperExpander.Visibility = Selected is { } endpoint && GsxProcessingRestoreSession.IsGsx(endpoint) ? Visibility.Visible : Visibility.Collapsed;
        if (GsxHelperExpander.Visibility != Visibility.Visible) return;
        string? unavailable = null;
        try { SelectedGsxPair(); unavailable = helperPilot.UnavailableReason; }
        catch (Exception ex) { unavailable = ex.Message; }
        GsxHelperInfo.Text = HelperPilotBusy ? helperPilotStatus.Message : unavailable ?? helperPilotStatus.Message;
        RunGsxHelperButton.IsEnabled = !HelperPilotBusy && !SupportActive && !usbWorkBusy && unavailable is null;
        GsxHelperReconnectCheck.IsEnabled = !HelperPilotBusy;
        OpenGsxHelperReportsButton.IsEnabled = !demo && helperPilotStatus.ReportDirectory is { } directory && Directory.Exists(directory);
    }
    private async void RunGsxHelperClick(object sender, RoutedEventArgs e)
    {
        if (HelperPilotBusy || SupportActive || usbWorkBusy) return;
        try {
            SelectedGsxPair();
            if (pending || effectsPending || playbackPending || sidetonePending || gsxSidetonePending || liveQueue.HasPending || gsxLiveQueue.HasPending)
                throw new InvalidOperationException("Apply or discard pending edits before checking the helper.");
            StopMicrophoneMonitor("Stopped for the helper check."); StopPlaybackMonitor("Stopped for the helper check.");
            CancelLiveChanges();
            var snapshot = endpoints; var reconnect = GsxHelperReconnectCheck.IsChecked == true;
            helperPilotStarting = true; SetHelperGuard(true);
            helperPilotStatus = new(true, "Waiting for administrator approval…"); LoadHelperPilot();
            helperPilotStatus = demo ? helperPilot.Start(snapshot, reconnect) : await Task.Run(() => helperPilot.Start(snapshot, reconnect));
            Status.Text = helperPilotStatus.Message;
        } catch (Exception ex) {
            helperPilotStatus = new(false, ex.Message); Error("Helper check was not started", ex);
        } finally {
            helperPilotStarting = false;
            if (!helperPilotStatus.Active) SetHelperGuard(false);
            LoadHelperPilot();
        }
    }
    private void PollHelperPilot()
    {
        if (helperPilotStarting || !helperPilotStatus.Active) return;
        helperPilotStatus = helperPilot.Poll();
        GsxHelperInfo.Text = helperPilotStatus.Message; Status.Text = helperPilotStatus.Message;
        if (!helperPilotStatus.Active) {
            SetHelperGuard(false); LoadHelperPilot();
            RefreshDevices();
            // Restore the completed-check message after discovery's ordinary Ready text.
            Status.Text = helperPilotStatus.Message;
        }
    }
    private void SetHelperGuard(bool active)
    {
        if (active) {
            helperGuard = new UIElement[] { DetailPanel, DeviceList, SetupProfilesExpander, RefreshDevicesButton }.ToDictionary(control => control, control => control.IsEnabled);
            foreach (var control in helperGuard.Keys) control.IsEnabled = false;
        } else if (helperGuard is { } previous) {
            foreach (var entry in previous) entry.Key.IsEnabled = entry.Value;
            helperGuard = null;
        }
    }
    private void HelperPilotClosing(object? sender, CancelEventArgs e)
    {
        if (!HelperPilotBusy) return;
        e.Cancel = true; Status.Text = "The helper check is finishing recovery. Close Timbre after it completes.";
    }
    private void OpenGsxHelperReportsClick(object sender, RoutedEventArgs e)
    {
        if (demo || helperPilotStatus.ReportDirectory is not { } directory || !Directory.Exists(directory)) return;
        try { Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception ex) { Error("Cannot open helper reports", ex); }
    }
}
