using System.Windows;
using System.Windows.Automation;
using EposControl.Core;

namespace EposControl.App;

public partial class MainWindow
{
    private void VerifyGsxSidetoneUi()
    {
        var closedDuringUpdate = false;
        EventHandler detectClose = (_, _) => closedDuringUpdate = true;
        Closed += detectClose; usbWorkBusy = true;
        try { Close(); }
        finally { usbWorkBusy = false; Closed -= detectClose; }
        if (closedDuringUpdate) throw new InvalidOperationException("Closing interrupted a pending USB transaction.");
        var mic = endpoints.Single(GsxSidetoneProtocol.Supports); DeviceList.SelectedItem = mic;
        var before = gsxSidetone.Read(mic); var levelBefore = backend.Read(mic.Id);
        if (GsxSidetoneCard.Visibility != Visibility.Visible || SidetoneCard.Visibility != Visibility.Collapsed ||
            AutomationProperties.GetName(GsxSidetoneSlider) != "GSX sidetone level percent" || currentGsxSidetone != before)
            throw new InvalidOperationException("GSX sidetone routing, units or initial read failed.");
        LiveApplyCheck.IsChecked = false; GsxSidetoneSlider.Value = 50; PollCurrent();
        if (GsxSidetoneSlider.Value != 50 || gsxSidetone.Read(mic) != before || !gsxSidetonePending)
            throw new InvalidOperationException("Polling changed an unapplied GSX draft.");
        DiscardChangesClick(this, new RoutedEventArgs());
        if (gsxSidetonePending || gsxSidetone.Read(mic) != before || GsxSidetoneSlider.Value != before.Settings.Percent)
            throw new InvalidOperationException("Discard did not preserve the exact GSX hardware value.");
        LiveApplyCheck.IsChecked = true; GsxSidetoneSlider.Value = 50;
        if (!FlushGsxSidetoneAsync().GetAwaiter().GetResult() || gsxSidetone.Read(mic).Settings != GsxSidetoneSettings.FromPercent(50))
            throw new InvalidOperationException("Live GSX sidetone failed.");
        ProfileName.Text = "GSX sidetone check"; SaveProfileClick(this, new RoutedEventArgs());
        var saved = profiles.ForDevice(mic).Single(p => p.Name == ProfileName.Text);
        if (saved.GsxSidetone != gsxSidetone.Read(mic).Settings || saved.Sidetone is not null)
            throw new InvalidOperationException("GSX profile did not capture raw state separately from B20.");
        GsxSidetoneSlider.Value = 75; FlushGsxSidetoneAsync().GetAwaiter().GetResult();
        if (profiles.ForDevice(mic).Single(p => p.Name == saved.Name) != saved) throw new InvalidOperationException("GSX audition rewrote a saved profile.");
        ApplyProfileClick(this, new RoutedEventArgs());
        if (gsxSidetone.Read(mic).Settings != saved.GsxSidetone || gsxSidetonePending)
            throw new InvalidOperationException("GSX profile application failed.");
        GsxSidetoneSlider.Value = 25;
        DeviceList.SelectedItem = endpoints.First(IsB20);
        if (!FlushGsxSidetoneAsync().GetAwaiter().GetResult() || gsxSidetone.Read(mic).Settings != saved.GsxSidetone || GsxSidetoneCard.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Device navigation failed to cancel the queued GSX edit.");
        DeviceList.SelectedItem = mic; LiveApplyCheck.IsChecked = false; GsxSidetoneSlider.Value = 20;
        gsxSidetone.Apply(mic, gsxSidetone.Read(mic), new(250));
        if (FlushGsxSidetoneAsync().GetAwaiter().GetResult() || gsxSidetone.Read(mic).Settings.RawValue != 250 || gsxSidetonePending)
            throw new InvalidOperationException("Stale GSX draft overwrote an external edit or retained retries.");
        gsxSidetone.Apply(mic, gsxSidetone.Read(mic), before.Settings); LoadGsxSidetone(true);
        if (backend.Read(mic.Id) != levelBefore) throw new InvalidOperationException("GSX sidetone changed microphone volume or mute.");
        DeviceList.SelectedItem = endpoints.First(IsB20); LiveApplyCheck.IsChecked = true;
    }
}
