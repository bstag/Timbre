using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using EposControl.Core;

namespace EposControl.App;

public partial class MainWindow
{
    public sealed class SetupEndpointChoice : INotifyPropertyChanged
    {
        public AudioEndpoint? Endpoint { get; }
        public SetupDeviceProfile? SavedDevice { get; }
        public string Label => Endpoint?.Label ?? SavedDevice!.DeviceName + " (disconnected)";
        public string PageLabel => ((Endpoint?.Direction ?? SavedDevice!.Settings.Direction) == AudioDirection.Microphone ? "Microphone" : "Sound") + (Endpoint is null ? " (disconnected)" : "");
        public string Key => Endpoint is { } endpoint ? SetupKey(endpoint) : SetupKey(SavedDevice!.Settings);
        private bool included;
        public bool IsIncluded {
            get => included;
            set { if (included == value) return; included = value; PropertyChanged?.Invoke(this, new(nameof(IsIncluded))); InclusionChanged?.Invoke(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal event Action? InclusionChanged;
        public SetupEndpointChoice(AudioEndpoint? endpoint, SetupDeviceProfile? saved, bool included) { Endpoint = endpoint; SavedDevice = saved; this.included = included; }
    }
    public sealed class SetupDeviceChoice : INotifyPropertyChanged
    {
        public string Label { get; }
        public IReadOnlyList<SetupEndpointChoice> Pages { get; }
        public bool? IsIncluded {
            get => Pages.All(p => p.IsIncluded) ? true : Pages.Any(p => p.IsIncluded) ? null : false;
            set { if (value is not { } include) return; foreach (var page in Pages) page.IsIncluded = include; }
        }
        public string SelectionInfo => IsIncluded == true ? "All listed pages included" : IsIncluded == false ? "Excluded" : "Selected pages only";
        public event PropertyChangedEventHandler? PropertyChanged;
        public SetupDeviceChoice(string label, IReadOnlyList<SetupEndpointChoice> pages)
        {
            Label = label; Pages = pages;
            foreach (var page in pages) page.PropertyChanged += (_, _) => {
                PropertyChanged?.Invoke(this, new(nameof(IsIncluded)));
                PropertyChanged?.Invoke(this, new(nameof(SelectionInfo)));
            };
        }
    }
    private IReadOnlyList<SetupDeviceChoice> setupDeviceChoices = [];
    private static string SetupPhysicalKey(SetupEndpointChoice choice)
    {
        var identity = choice.Endpoint is { } endpoint ? ProfileStore.DeviceIdentity(endpoint) : choice.SavedDevice!.Settings.DeviceIdentity;
        var direction = choice.Endpoint?.Direction ?? choice.SavedDevice!.Settings.Direction;
        var suffix = "|" + direction;
        // Only canonical USB identities establish physical grouping for disconnected pages.
        return identity.StartsWith("usb-v1:", StringComparison.Ordinal) && identity.EndsWith(suffix, StringComparison.Ordinal)
            ? identity[..^suffix.Length] : "page:" + choice.Key;
    }
    private IReadOnlyList<SetupDeviceChoice> GroupSetupChoices(IReadOnlyList<SetupEndpointChoice> choices) => choices.GroupBy(SetupPhysicalKey).Select(group => {
        var pages = group.ToArray();
        var usb = pages.Select(p => p.Endpoint?.Usb).FirstOrDefault(u => u is not null);
        if (usb is null && group.Key.StartsWith("usb-v1:", StringComparison.Ordinal)) {
            var parts = group.Key.Split(':', 4);
            if (parts.Length == 4) usb = new(parts[1], parts[2], parts[3]);
        }
        var label = catalog.Find(usb)?.Model ?? pages.FirstOrDefault(p => p.Endpoint is not null)?.Endpoint?.AdapterName ?? pages[0].SavedDevice!.DeviceName;
        if (pages.All(p => p.Endpoint is null)) label += " (disconnected)";
        return new SetupDeviceChoice(label, pages);
    }).ToArray();
    private readonly HashSet<string> editedSetupPages = [];
    private bool syncingSetupInclusion;
    private static string SetupKey(AudioEndpoint endpoint) => ProfileStore.DeviceIdentity(endpoint) + "|" + endpoint.Direction;
    private static string SetupKey(AudioProfile profile) => profile.DeviceIdentity + "|" + profile.Direction;
    private void RefreshSetupChoices()
    {
        RebuildSetupChoices(false);
    }
    private void RebuildSetupChoices(bool savedSelection)
    {
        var previous = setupChoices.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().IsIncluded);
        var selected = SetupList.SelectedItem as SetupProfile;
        var choices = endpoints.Select(endpoint => {
            var saved = selected?.Devices.FirstOrDefault(d => ProfileStore.BelongsTo(endpoint, d.Settings));
            return new SetupEndpointChoice(endpoint, saved, !savedSelection && previous.TryGetValue(SetupKey(endpoint), out var included) ? included : saved is not null);
        }).ToList();
        foreach (var saved in selected?.Devices ?? []) {
            if (endpoints.Any(e => ProfileStore.BelongsTo(e, saved.Settings))) continue;
            choices.Add(new(null, saved, savedSelection || !previous.TryGetValue(SetupKey(saved.Settings), out var included) || included));
        }
        setupChoices = choices;
        foreach (var choice in setupChoices) choice.InclusionChanged += SetupInclusionChanged;
        setupDeviceChoices = GroupSetupChoices(setupChoices);
        SetupDevices.ItemsSource = setupDeviceChoices;
        SetupInclusionChanged();
    }
    private void LoadSetups(string? selectName = null)
    {
        selectName ??= (SetupList.SelectedItem as SetupProfile)?.Name;
        SetupList.ItemsSource = setups.Load();
        SetupList.SelectedItem = SetupList.Items.Cast<SetupProfile>().FirstOrDefault(s => s.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase));
        ShowSetupSummary();
    }
    private void SetupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SetupSummary is null) return;
        if (SetupList.SelectedItem is SetupProfile) RebuildSetupChoices(true);
        ShowSetupSummary();
    }
    private bool SetupMembershipChanged(SetupProfile setup) => !setup.Devices.Select(d => SetupKey(d.Settings)).ToHashSet().SetEquals(setupChoices.Where(c => c.IsIncluded).Select(c => c.Key));
    private void SetupInclusionChanged()
    {
        if (SelectedSetupIncludeCheck is null) return;
        syncingSetupInclusion = true;
        try {
            SelectedSetupIncludeCheck.IsEnabled = Selected is not null;
            SelectedSetupIncludeCheck.IsChecked = Selected is { } endpoint && setupChoices.Any(c => c.Endpoint?.Id == endpoint.Id && c.IsIncluded);
            var group = SelectedSetupDeviceChoice;
            SelectedSetupDeviceIncludeCheck.IsEnabled = group is not null;
            SelectedSetupDeviceIncludeCheck.IsChecked = group?.IsIncluded ?? (group is null ? false : null);
        } finally { syncingSetupInclusion = false; }
        ShowSetupSummary();
    }
    private SetupDeviceChoice? SelectedSetupDeviceChoice => Selected is { } endpoint
        ? setupDeviceChoices.FirstOrDefault(g => g.Pages.Any(p => p.Endpoint?.Id == endpoint.Id)) : null;
    private void SelectedSetupDeviceIncludeChanged(object sender, RoutedEventArgs e)
    {
        if (syncingSetupInclusion || SelectedSetupDeviceChoice is not { } group) return;
        group.IsIncluded = SelectedSetupDeviceIncludeCheck.IsChecked == true;
    }
    private void SelectedSetupIncludeChanged(object sender, RoutedEventArgs e)
    {
        if (syncingSetupInclusion || Selected is not { } endpoint) return;
        if (setupChoices.FirstOrDefault(c => c.Endpoint?.Id == endpoint.Id) is { } choice) choice.IsIncluded = SelectedSetupIncludeCheck.IsChecked == true;
    }
    private void MarkSetupPageEdited(AudioEndpoint endpoint) => editedSetupPages.Add(SetupKey(endpoint));
    private async void SelectEditedSetupPagesClick(object sender, RoutedEventArgs e)
    {
        if (LiveApplyCheck.IsChecked == true && !FlushLiveChanges(true)) { Status.Text += " Included pages were not changed."; return; }
        if (LiveApplyCheck.IsChecked == true && !await FlushGsxSidetoneAsync()) { Status.Text += " Included pages were not changed."; return; }
        foreach (var choice in setupChoices) choice.IsIncluded = editedSetupPages.Contains(choice.Key);
        SetupInclusionChanged();
        Status.Text = "Selected " + setupChoices.Count(c => c.IsIncluded) + " pages with direct adjustments applied in this app session. Save to update the setup.";
    }
    private void ShowSetupSummary()
    {
        if (SetupSummary is null) return;
        var selected = SetupList.SelectedItem as SetupProfile;
        var included = setupChoices.Where(c => c.IsIncluded).ToArray();
        SaveSelectedSetupButton.IsEnabled = selected is not null && included.Length != 0;
        SaveSetupAsButton.IsEnabled = included.Length != 0;
        ApplySetupButton.IsEnabled = selected is not null && !SetupMembershipChanged(selected);
        SetupSummary.Text = "Included on save: " + (included.Length == 0 ? "none. Check the pages you want." : string.Join("; ", included.Select(c => c.Label)) + ".") +
            (selected is not null && SetupMembershipChanged(selected) ? " Inclusion changed. Save before applying." : "");
    }
    private void ReloadSetupsClick(object sender, RoutedEventArgs e)
    {
        try { LoadSetups(); Status.Text = "Reloaded saved setups."; }
        catch (Exception ex) { Error("Cannot read saved setups", ex); }
    }
    private async void SaveSetupClick(object sender, RoutedEventArgs e)
    {
        try {
            if (!setupChoices.Any(c => c.IsIncluded)) throw new InvalidOperationException("Choose at least one device page to include.");
            if (LiveApplyCheck.IsChecked == true && !FlushLiveChanges(true)) { Status.Text += " Setup was not saved."; return; }
            if (LiveApplyCheck.IsChecked == true && !await FlushGsxSidetoneAsync()) { Status.Text += " Setup was not saved."; return; }
            var setup = await CaptureIncludedSetupAsync(SetupName.Text.Trim());
            setups.Save(setup); LoadSetups(setup.Name);
            Status.Text = "Saved current settings for " + setup.Devices.Count + " device pages as “" + setup.Name + "”.";
        } catch (Exception ex) { Error("Cannot save setup", ex); }
    }
    private async void SaveSelectedSetupClick(object sender, RoutedEventArgs e)
    {
        if (SetupList.SelectedItem is not SetupProfile setup) { Status.Text = "Choose a setup to save to."; return; }
        try {
            if (!setupChoices.Any(c => c.IsIncluded)) throw new InvalidOperationException("Choose at least one device page to include.");
            if (LiveApplyCheck.IsChecked == true && !FlushLiveChanges(true)) { Status.Text += " Setup was not saved."; return; }
            if (LiveApplyCheck.IsChecked == true && !await FlushGsxSidetoneAsync()) { Status.Text += " Setup was not saved."; return; }
            setups.UpdateSelected(setup, await CaptureIncludedSetupAsync(setup.Name)); LoadSetups(setup.Name);
            Status.Text = "Saved checked device pages to “" + setup.Name + "”. Unchecked pages are excluded.";
        } catch (Exception ex) { Error("Cannot save to selected setup", ex); }
    }
    private SetupProfile CaptureIncludedSetup(string name)
    {
        var choices = setupChoices.Where(c => c.IsIncluded).ToArray();
        return setupControls.Capture(name, choices.Where(c => c.Endpoint is not null).Select(c => c.Endpoint!).ToArray(),
            choices.Where(c => c.Endpoint is null).Select(c => c.SavedDevice!).ToArray(), SetupList.SelectedItem as SetupProfile);
    }
    private Task<SetupProfile> CaptureIncludedSetupAsync(string name)
    {
        var choices = setupChoices.Where(c => c.IsIncluded).ToArray();
        var connected = choices.Where(c => c.Endpoint is not null).Select(c => c.Endpoint!).ToArray();
        var retained = choices.Where(c => c.Endpoint is null).Select(c => c.SavedDevice!).ToArray();
        var previous = SetupList.SelectedItem as SetupProfile;
        return connected.Any(GsxSidetoneProtocol.Supports) ? RunUsbWork(() => setupControls.Capture(name, connected, retained, previous))
            : Task.FromResult(setupControls.Capture(name, connected, retained, previous));
    }
    private async void ApplySetupClick(object sender, RoutedEventArgs e)
    {
        if (SetupList.SelectedItem is not SetupProfile setup) { Status.Text = "Choose a setup first."; return; }
        if (SetupMembershipChanged(setup)) { Status.Text = "Save inclusion changes before applying this setup."; return; }
        CancelLiveChanges();
        try {
            var results = setup.Devices.Any(d => d.Settings.GsxSidetone is not null) ? await RunUsbWork(() => setupControls.Apply(setup)) : setupControls.Apply(setup);
            var persistenceErrors = new List<string>();
            foreach (var result in results.Where(r => r.Status == SetupApplyStatus.Applied && r.Endpoint is { } endpoint && IsB20(endpoint) && r.Device.Settings.Effects is not null)) {
                try {
                    var current = effects.Read(result.Endpoint!);
                    if (!ApoMicrophoneCodec.Matches(current, result.Device.Settings.Effects!)) throw new InvalidDataException("Processing changed after setup application.");
                    processingStates.Remember(result.Endpoint!, current);
                } catch (Exception ex) { persistenceErrors.Add("B20 processing restore state was not saved: " + ex.Message); }
            }
            ReloadSelectedAfterSetup();
            SetupResult.Text = string.Join("\n", results.Select(r => r.Device.DeviceName + ": " + r.Message).Concat(persistenceErrors));
            var applied = results.Count(r => r.Status == SetupApplyStatus.Applied);
            var missing = results.Count(r => r.Status == SetupApplyStatus.Missing);
            Status.Text = "“" + setup.Name + "”: " + applied + " applied" + (missing > 0 ? ", " + missing + " disconnected" : "") +
                (results.Any(r => r.Status is SetupApplyStatus.Failed or SetupApplyStatus.NotApplied) ?
                    (applied > 0 ? ". Partially applied; see setup results." : ". Apply failed; see setup results.") : ".") +
                (persistenceErrors.Count > 0 ? " Restore-state save failed; see setup results." : "");
        } catch (Exception ex) {
            ReloadSelectedAfterSetup(); SetupResult.Text = ex.Message; Error("Cannot apply setup", ex);
        }
    }
    private void ReloadSelectedAfterSetup()
    {
        if (Selected is not { } endpoint) return;
        try { ShowState(backend.Read(endpoint.Id), true); } catch (Exception ex) { Error("Cannot reload device level", ex); }
        LoadEffects(true); LoadSidetone(true); LoadPlayback(true); LoadSavedProcessing(); UpdateDraftInfo();
        gsxSidetonePending = false; LoadGsxSidetone(true);
    }
    internal void ShowDemoSetupProfiles()
    {
        if (!demo) throw new InvalidOperationException("Setup preview requires demo devices.");
        SetupProfilesExpander.IsExpanded = true;
        SetupDevicesExpander.IsExpanded = true;
    }
}
