using System.ComponentModel;
using System.IO;
using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    internal static readonly string[] SupportScenarios = [
        "background support starts off and preserves pending drafts",
        "support startup pauses edits and excludes guided helper launches",
        "running support enables controls without automatic replay",
        "support stop preserves applied changes and refuses pending drafts",
        "support recovery failure releases guards without reporting success",
        "window close requests recovery and waits for verified completion"
    ];
    internal void ShowDemoSupportSession() => SupportExpander.IsExpanded = true;
    private void VerifySupportSessionUi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "timbre-support-ui-" + Guid.NewGuid().ToString("N"));
        var session = new DemoSupportSession(); var pilot = new DemoGsxHelperPilot(); var audio = new DemoAudioBackend();
        var window = new MainWindow(audio, catalog, new ProfileStore(Path.Combine(directory, "profiles.json")),
            new ProcessingStateStore(Path.Combine(directory, "processing")), new SetupProfileStore(Path.Combine(directory, "setups.json")),
            true, new InlineUsbWorkRunner(), pilot, session);
        try {
            if (window.SupportExpander.IsExpanded || window.SupportActive || session.Starts != 0 || window.StopSupportButton.IsEnabled || !window.StartSupportButton.IsEnabled)
                throw new InvalidOperationException("Support started implicitly or exposed incorrect startup controls.");
            window.LiveApplyCheck.IsChecked = false; window.LevelSlider.Value = 43;
            window.StartSupportClick(window, new RoutedEventArgs());
            if (session.Starts != 0 || !window.pending || !window.DetailPanel.IsEnabled) throw new InvalidOperationException("Support discarded pending drafts or started with them.");
            window.DiscardChangesClick(window, new RoutedEventArgs());
            window.StartSupportClick(window, new RoutedEventArgs());
            if (session.Starts != 1 || !window.SupportPaused || window.DetailPanel.IsEnabled || window.StartSupportButton.IsEnabled)
                throw new InvalidOperationException("Support startup did not guard competing edits.");
            window.RunGsxHelperClick(window, new RoutedEventArgs());
            if (pilot.Starts != 0) throw new InvalidOperationException("A guided helper launched during background startup.");
            session.Status = new("Running", "Demo support running"); window.PollCurrent();
            if (window.SupportPaused || !window.DetailPanel.IsEnabled || !window.DeviceList.IsEnabled || !window.StopSupportButton.IsEnabled || window.StartSupportButton.IsEnabled)
                throw new InvalidOperationException("Running support did not enable ordinary controls.");
            window.RunGsxHelperClick(window, new RoutedEventArgs()); if (pilot.Starts != 0) throw new InvalidOperationException("A guided helper launched during active support.");
            window.LevelSlider.Value = 45; window.StopSupportClick(window, new RoutedEventArgs());
            if (window.supportStatus.State != "Running" || !window.pending) throw new InvalidOperationException("Stopping support implicitly applied/discarded drafts.");
            window.DiscardChangesClick(window, new RoutedEventArgs()); window.LiveApplyCheck.IsChecked = true;
            window.LevelSlider.Value = 55; if (!window.FlushLiveChanges(true)) throw new InvalidOperationException("Demo support could not apply an ordinary live adjustment.");
            var applied = audio.Read(window.Selected!.Id);
            window.StopSupportClick(window, new RoutedEventArgs());
            if (!window.SupportPaused || window.DetailPanel.IsEnabled || audio.Read(window.Selected.Id) != applied) throw new InvalidOperationException("Support stop lost applied changes or failed to pause recovery edits.");
            session.Status = new("Failed", "Demo recovery failed"); window.PollCurrent();
            if (window.SupportActive || !window.DetailPanel.IsEnabled || !window.SupportInfo.Text.Contains("failed")) throw new InvalidOperationException("Support failure was hidden or left controls guarded.");
            window.StartSupportClick(window, new RoutedEventArgs()); session.Status = new("Running", "Demo support running"); window.PollCurrent();
            var closing = new CancelEventArgs(); window.SupportClosing(window, closing);
            if (!closing.Cancel || !window.closeAfterSupport || window.supportStatus.State != "Recovering") throw new InvalidOperationException("Closing bypassed support recovery.");
            session.Status = new("Stopped", "Demo latest settings preserved"); window.PollCurrent();
            if (window.SupportActive || window.closeAfterSupport || audio.Read(window.Selected.Id) != applied || Directory.Exists(directory))
                throw new InvalidOperationException("Support shutdown changed applied settings or wrote profiles/restoration state.");
        } finally { session.Status = new("Stopped", "Demo cleanup"); if (window.supportStatus.Active) window.PollSupportSession(); window.Close(); }
    }
}
