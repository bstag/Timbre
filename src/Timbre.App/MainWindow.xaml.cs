using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Automation;
using Timbre.Core;
using Microsoft.Win32;

namespace Timbre.App;

public partial class MainWindow : Window
{
    private readonly IAudioBackend backend;
    private readonly DeviceCatalog catalog;
    private readonly ProfileStore profiles;
    private readonly SetupProfileStore setups;
    private readonly SetupProfileController setupControls;
    private IReadOnlyList<SetupEndpointChoice> setupChoices = [];
    private readonly ProcessingStateStore processingStates;
    private readonly ProcessingRestoreSession processingRestore;
    private readonly bool demo;
    private readonly IMicrophoneEffectsBackend effects;
    private readonly ISidetoneBackend sidetone;
    private readonly IPickupPatternBackend pickupPatterns;
    private readonly IPlaybackEffectsBackend playback;
    private PlaybackEffects? currentPlayback;
    private bool playbackPending;
    private bool playbackReverbLevelEdited;
    private readonly List<Slider> playbackEqSliders = [];
    private readonly HashSet<int> playbackEqEdits = [];
    private CancellationTokenSource patternCancellation = new();
    private bool patternBusy;
    private int patternGeneration;
    private SidetoneState? currentSidetone;
    private bool sidetonePending, sidetoneLevelPending;
    private MicrophoneEffects? currentEffects;
    private bool effectsPending;
    private readonly List<Slider> eqSliders = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch liveClock = Stopwatch.StartNew();
    private readonly LiveApplyQueue liveQueue = new(TimeSpan.FromMilliseconds(150));
    private AudioState? currentAudio;
    private bool volumePending, mutePending;
    private IReadOnlyList<AudioEndpoint> endpoints = [];
    private bool loading, pending;
    private AudioEndpoint? Selected => DeviceList.SelectedItem as AudioEndpoint;

    public MainWindow(IAudioBackend backend, DeviceCatalog catalog, ProfileStore profiles, ProcessingStateStore processingStates, SetupProfileStore setups, bool demo)
        : this(backend, catalog, profiles, processingStates, setups, demo, demo ? new InlineUsbWorkRunner() : new ThreadedUsbWorkRunner()) { }
    internal MainWindow(IAudioBackend backend, DeviceCatalog catalog, ProfileStore profiles, ProcessingStateStore processingStates, SetupProfileStore setups, bool demo, IUsbWorkRunner usbRunner)
    {
        this.usbRunner = usbRunner;
        this.backend = backend; this.catalog = catalog; this.profiles = profiles; this.demo = demo;
        this.processingStates = processingStates; processingRestore = new(processingStates);
        effects = demo ? new DemoMicrophoneEffectsBackend() : WindowsApoMemory.Create(backend);
        playback = demo ? new DemoPlaybackEffectsBackend() : WindowsApoMemory.CreatePlayback(backend);
        sidetone = demo ? new DemoSidetoneBackend() : WindowsSidetone.Create(backend);
        gsxSidetone = demo ? new DemoGsxSidetoneBackend() : WindowsGsxSidetone.Create(backend);
        this.setups = setups; setupControls = new(backend, effects, sidetone, playback, gsxSidetone);
        pickupPatterns = demo ? new DemoPickupPatternBackend() : new WindowsPickupPattern(backend);
        InitializeComponent();
        BuildEqControls();
        BuildPlaybackEqControls();
        InitializeMonitor();
        InitializePlaybackMonitor();
        InitializeSwitchHints();
        LiveApplyChanged(this, new RoutedEventArgs());
        if (demo) { Title += " — Demo"; Subtitle.Text = "Demo devices · changes stay in this preview"; }
        MuteCheck.Checked += MarkPending; MuteCheck.Unchecked += MarkPending;
        timer.Tick += (_, _) => PollCurrent();
        liveTimer.Tick += (_, _) => FlushLiveChanges();
        gsxLiveTimer.Tick += GsxLiveTick;
        Closing += DeviceUpdateClosing;
        Loaded += (_, _) => timer.Start(); Closed += (_, _) => { timer.Stop(); CancelLiveChanges(); patternGeneration++; patternCancellation.Cancel(); patternCancellation.Dispose(); gsxGeneration++; gsxReadCancellation.Cancel(); gsxReadCancellation.Dispose(); };
        RefreshDevices();
        try { LoadSetups(); } catch (Exception ex) { Error("Cannot read saved setups", ex); }
    }
    private void RefreshDevices()
    {
        var previous = Selected?.Id;
        try {
            endpoints = backend.Discover();
            RefreshSetupChoices();
            processingRestore.Observe(endpoints);
            DeviceList.ItemsSource = endpoints;
            DeviceCount.Text = $"{endpoints.Count} audio endpoints · updates automatically";
            Status.Text = demo ? "Demo mode. Your Windows audio settings are untouched." : "Ready. Live changes apply as you adjust controls; profiles save when you choose.";
            DeviceList.SelectedItem = endpoints.FirstOrDefault(e => e.Id == previous) ?? endpoints.FirstOrDefault(e => e.Direction == AudioDirection.Microphone) ?? endpoints.FirstOrDefault();
            if (Selected is null) {
                DetailPanel.IsEnabled = false; ApplyButton.IsEnabled = false;
                DeviceTitle.Text = "Connect an EPOS device"; DeviceInfo.Text = "Connect a USB device, then choose Refresh devices."; FormatInfo.Text = ""; FeatureNote.Text = "";
                FeatureList.ItemsSource = null; ProfileList.ItemsSource = null;
                ResetPickupPattern(); PickupPatternInfo.Visibility = Visibility.Collapsed;
            }
        } catch (Exception ex) { Error("Cannot discover devices", ex); }
    }
    private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopMicrophoneMonitor("Stopped after device selection."); gateGuideVisible = false;
        StopPlaybackMonitor("Stopped after device selection.");
        CancelLiveChanges();
        ResetGsxSidetone();
        DetailScroll.ScrollToTop();
        SetupInclusionChanged();
        if (Selected is not { } endpoint) return;
        DetailPanel.IsEnabled = true; pending = false; volumePending = mutePending = false; currentAudio = null; effectsPending = false; currentEffects = null;
        playbackPending = false; currentPlayback = null; playbackEqEdits.Clear();
        ResetPickupPattern(); LoadPickupPattern();
        sidetonePending = false; sidetoneLevelPending = false; currentSidetone = null;
        var definition = catalog.Find(endpoint.Usb);
        DeviceTitle.Text = endpoint.Name;
        DeviceInfo.Text = endpoint.Usb is { } usb
            ? $"{endpoint.Direction} · USB {usb.VendorId}:{usb.ProductId} · {definition?.Model ?? "Additional EPOS model"}"
            : $"{endpoint.Direction} · {endpoint.AdapterName} · USB model identity unavailable";
        FormatInfo.Text = endpoint.Format is { } format
            ? $"Windows mix format: {format.Channels} channels · {format.SampleRate / 1000.0:0.#} kHz · {format.BitsPerSample} bits"
            : "Windows mix format unavailable";
        LevelTitle.Text = endpoint.Direction == AudioDirection.Microphone ? "Microphone level" : "Playback volume";
        MuteCheck.Content = endpoint.Direction == AudioDirection.Microphone ? "Mic" : "Sound";
        MuteCheck.Tag = FindResource(endpoint.Direction == AudioDirection.Microphone ? "MicIcon" : "HeadphoneIcon");
        FeatureNote.Text = endpoint.Direction == AudioDirection.Microphone
            ? "Microphone processing uses the installed EPOS processor. Available controls depend on the selected device."
            : "Sound settings apply to headphones connected to this USB device.";
        SoundPageButton.IsEnabled = RelatedEndpoint(AudioDirection.Playback) is not null;
        MicrophonePageButton.IsEnabled = RelatedEndpoint(AudioDirection.Microphone) is not null;
        ShowFeatures(endpoint);
        try { ShowState(backend.Read(endpoint.Id), true); Status.Text = "Selected " + endpoint.Name; }
        catch (Exception ex) { ApplyButton.IsEnabled = false; Error("Cannot read this endpoint", ex); }
        LoadEffects(true);
        LoadPlayback(true);
        CheckProcessingRestores(); LoadSavedProcessing();
        LoadSidetone(true);
        LoadGsxSidetone(true);
        try { LoadProfiles(); }
        catch (Exception ex) { ProfileList.ItemsSource = null; Error("Cannot read saved profiles", ex); }
        UpdateDraftInfo();
    }
    private void ShowFeatures(AudioEndpoint endpoint)
    {
        FeatureList.ItemsSource = catalog.Features(endpoint, currentEffects is not null, currentSidetone is not null || currentGsxSidetone is not null, currentPlayback is not null).Select(f => new {
            f.Name, f.Description, StatusLabel = f.State switch { FeatureState.Ready => "Available", FeatureState.PendingValidation => "In development", _ => "To be mapped" }
        }).ToArray();
    }
    private void ShowState(AudioState state, bool updateControls)
    {
        LiveInfo.Text = $"Current level: {state.Level * 100:0.#}% · {state.Decibels:0.#} dB · {(state.Muted ? "Muted" : "Unmuted")}";
        ApplyButton.IsEnabled = true;
        if (!updateControls) return;
        loading = true;
        try { currentAudio = state; LevelSlider.Value = Math.Round(state.Level * 100); MuteCheck.IsChecked = state.Muted; pending = false; volumePending = mutePending = false; }
        finally { loading = false; }
    }
    private void PollCurrent()
    {
        if (usbWorkBusy) return;
        try {
            var connected = backend.Discover();
            processingRestore.Observe(connected);
            if (!connected.OrderBy(e => e.Id).Select(e => (e.Id, e.ProfileIdentity)).SequenceEqual(endpoints.OrderBy(e => e.Id).Select(e => (e.Id, e.ProfileIdentity)))) RefreshDevices();
            CheckProcessingRestores();
        } catch (Exception ex) { Error("Cannot refresh connected devices", ex); }
        if (Selected is not { } endpoint) return;
        try { ShowState(backend.Read(endpoint.Id), !pending); }
        catch (Exception ex) { ApplyButton.IsEnabled = false; Status.Text = "Device unavailable. Refresh after reconnecting. " + ex.Message; }
        LoadEffects(!effectsPending);
        LoadPlayback(!playbackPending);
        LoadSidetone(!sidetonePending);
        LoadGsxSidetone(!gsxSidetonePending, preserveAcceptedLevel: true);
        LoadPickupPattern();
        UpdateDraftInfo();
    }
    private AudioEndpoint? RelatedEndpoint(AudioDirection direction) => Selected is { Usb: { } usb }
        ? endpoints.FirstOrDefault(e => e.Direction == direction && e.Usb is { } other &&
            usb.VendorId.Equals(other.VendorId, StringComparison.OrdinalIgnoreCase) &&
            usb.ProductId.Equals(other.ProductId, StringComparison.OrdinalIgnoreCase) &&
            usb.InstanceId.Equals(other.InstanceId, StringComparison.OrdinalIgnoreCase)) : null;
    private void SoundPageClick(object sender, RoutedEventArgs e) { if (RelatedEndpoint(AudioDirection.Playback) is { } endpoint) DeviceList.SelectedItem = endpoint; }
    private void MicrophonePageClick(object sender, RoutedEventArgs e) { if (RelatedEndpoint(AudioDirection.Microphone) is { } endpoint) DeviceList.SelectedItem = endpoint; }
    private void UpdateDraftInfo()
    {
        UpdateMonitorControls();
        UpdatePlaybackMonitorControls();
        if (DraftInfo is null) return;
        var sections = new List<string>();
        if (pending) sections.Add("volume / mute");
        if (playbackPending) sections.Add("sound mode / EQ / reverb");
        if (effectsPending) sections.Add("microphone processing");
        if (sidetonePending) sections.Add("sidetone");
        if (gsxSidetonePending) sections.Add("GSX sidetone");
        DraftInfo.Text = sections.Count == 0 ? (LiveApplyCheck.IsChecked == true ? "Live changes on · settings are up to date." : "Manual Apply · settings are up to date.")
            : (liveQueue.HasPending || gsxLiveQueue.HasPending ? "Applying: " : "Not applied: ") + string.Join(", ", sections) + ".";
        DiscardChangesButton.IsEnabled = sections.Count != 0;
    }
    private ControlTarget? CurrentTarget => Selected is { } endpoint ? new(endpoint.Id, endpoint.ProfileIdentity) : null;
    private void CancelLiveChanges() { liveTimer.Stop(); liveQueue.Cancel(); gsxLiveTimer.Stop(); gsxLiveQueue.Cancel(); gsxEditEpoch++; }
    private void Edited(ControlArea area)
    {
        if (loading) return;
        if (LiveApplyCheck.IsChecked == true && CurrentTarget is { } target) {
            liveQueue.Schedule(target, area, liveClock.Elapsed);
            if (!liveTimer.IsEnabled) liveTimer.Start();
        }
        UpdateDraftInfo();
    }
    private void LiveApplyChanged(object sender, RoutedEventArgs e)
    {
        CancelLiveChanges();
        var visibility = LiveApplyCheck?.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in new[] { ApplyButton, ApplyEffectsButton, ApplySidetoneButton, ApplyPlaybackButton, ApplyGsxSidetoneButton })
            if (button is not null) button.Visibility = visibility;
        // Enabling live changes does not apply drafts made in manual mode.
        UpdateDraftInfo();
    }
    private bool FlushLiveChanges(bool force = false)
    {
        var work = liveQueue.Take(CurrentTarget, liveClock.Elapsed, force);
        if (!liveQueue.HasPending) liveTimer.Stop();
        foreach (var area in work) {
            var success = area switch {
                ControlArea.Volume => !pending || ApplyVolume(),
                ControlArea.MicrophoneProcessing => !effectsPending || ApplyEffects(),
                ControlArea.Sidetone => !sidetonePending || ApplySidetone(),
                ControlArea.Playback => !playbackPending || ApplyPlayback(),
                _ => false
            };
            if (!success) { CancelLiveChanges(); UpdateDraftInfo(); return false; }
        }
        UpdateDraftInfo(); return true;
    }
    private void DiscardChangesClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } endpoint) return;
        CancelLiveChanges();
        try { ShowState(backend.Read(endpoint.Id), true); }
        catch (Exception ex) { Error("Cannot reload settings", ex); return; }
        LoadPlayback(true); LoadEffects(true); LoadSidetone(true); UpdateDraftInfo();
        gsxSidetonePending = false; LoadGsxSidetone(true);
        Status.Text = "Reloaded current device settings; pending edits discarded.";
    }
    private void LoadPlayback(bool updateControls)
    {
        if (Selected is not { Direction: AudioDirection.Playback, Usb: { } usb } endpoint ||
            !usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) || !usb.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase)) {
            PlaybackCard.Visibility = Visibility.Collapsed; currentPlayback = null; return;
        }
        PlaybackCard.Visibility = Visibility.Visible;
        try {
            var state = playback.Read(endpoint); ApplyPlaybackButton.IsEnabled = true; SoundMode.IsEnabled = true; PlaybackEqExpander.IsEnabled = true;
            PlaybackInfo.Text = "Current: " + (state.SurroundEnabled ? "Virtual 7.1 surround" : "Stereo (2.0)") +
                " · EQ curve: " + (state.Equalizer == PlaybackEqualizer.Flat ? "Flat" : "Custom") +
                (state.Reverb is { } reverb ? $" · Reverb: {(reverb.Enabled ? "on" : "off")} ({reverb.Level * 100:0}% stored)" : "");
            if (updateControls || currentPlayback is null) {
                loading = true;
                try {
                    currentPlayback = state; SoundMode.SelectedIndex = state.SurroundEnabled ? 1 : 0;
                    ReverbEnabledCheck.IsChecked = state.Reverb?.Enabled == true;
                    ReverbSlider.Value = (state.Reverb?.Level ?? 0) * 100;
                    playbackReverbLevelEdited = false;
                    var levels = state.Equalizer?.Levels() ?? throw new InvalidDataException("Complete playback EQ is unavailable.");
                    for (var i = 0; i < 9; i++) playbackEqSliders[i].Value = levels[i];
                    playbackEqEdits.Clear(); playbackPending = false;
                }
                finally { loading = false; }
            }
        } catch (Exception ex) {
            currentPlayback = null; ApplyPlaybackButton.IsEnabled = false; SoundMode.IsEnabled = false; PlaybackEqExpander.IsEnabled = false;
            PlaybackInfo.Text = "Sound mode unavailable: " + ex.Message;
        }
        UpdateReverbControls();
        ShowFeatures(endpoint);
    }
    private void SoundModeChanged(object sender, SelectionChangedEventArgs e) { UpdateReverbControls(); if (!loading) { playbackPending = true; Edited(ControlArea.Playback); } }
    private void UpdateReverbControls()
    {
        if (ReverbEnabledCheck is null || ReverbSlider is null) return;
        ReverbEnabledCheck.IsEnabled = currentPlayback?.Reverb is not null && SoundMode.SelectedIndex == 1;
        ReverbSlider.IsEnabled = ReverbEnabledCheck.IsEnabled && ReverbEnabledCheck.IsChecked == true;
    }
    private void ReverbToggleChanged(object sender, RoutedEventArgs e)
    {
        UpdateReverbControls();
        if (!loading) { playbackPending = true; Edited(ControlArea.Playback); }
    }
    private void ReverbAmountChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ReverbValue is not null) ReverbValue.Text = $"{e.NewValue:0}%";
        if (!loading) { playbackReverbLevelEdited = true; playbackPending = true; Edited(ControlArea.Playback); }
    }
    private void ApplyPlaybackClick(object sender, RoutedEventArgs e) { ApplyPlayback(); UpdateDraftInfo(); }
    private bool ApplyPlayback()
    {
        if (Selected is not { } endpoint || currentPlayback is null || SoundMode.SelectedIndex is < 0 or > 1) { Status.Text = "Sound controls are unavailable."; return false; }
        try {
            var result = playback.Apply(endpoint, currentPlayback, DesiredPlayback());
            if (!ApoPlaybackCodec.Matches(result, currentPlayback)) MarkSetupPageEdited(endpoint);
            LoadPlayback(true);
            Status.Text = "Applied GSX 300 sound settings.";
            return true;
        } catch (Exception ex) { LoadPlayback(true); Error("Sound mode was not applied", ex); return false; }
    }
    private PlaybackEffects DesiredPlayback()
    {
        var levels = currentPlayback!.Equalizer!.Levels();
        foreach (var i in playbackEqEdits) levels[i] = (float)playbackEqSliders[i].Value;
        var reverb = currentPlayback.Reverb is { } saved ? saved with {
            Enabled = ReverbEnabledCheck.IsChecked == true,
            Level = playbackReverbLevelEdited ? (float)(ReverbSlider.Value / 100) : saved.Level } : null;
        return new(SoundMode.SelectedIndex == 1, PlaybackEqualizer.FromLevels(levels), reverb);
    }
    private void BuildPlaybackEqControls()
    {
        for (var i = 0; i < 9; i++) {
            var index = i; var frequency = PlaybackEqualizer.Gsx300BandLabels[i];
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = frequency, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            var slider = new Slider { Minimum = -6, Maximum = 6, TickFrequency = .01, IsSnapToTickEnabled = true, Orientation = Orientation.Vertical, Height = 110, Width = 30, Margin = new Thickness(0, 8, 0, 8) };
            AutomationProperties.SetName(slider, $"Playback EQ band {i + 1} at {frequency} in decibels");
            var label = new TextBlock { Text = "0 dB", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            slider.ValueChanged += (_, e) => {
                label.Text = $"{e.NewValue:+0.##;-0.##;0} dB";
                if (!loading) { playbackEqEdits.Add(index); playbackPending = true; Edited(ControlArea.Playback); }
            };
            panel.Children.Add(slider); panel.Children.Add(label); PlaybackEqBands.Children.Add(panel); playbackEqSliders.Add(slider);
        }
    }
    private void FlatPlaybackEqClick(object sender, RoutedEventArgs e)
    {
        foreach (var slider in playbackEqSliders) slider.Value = 0;
        for (var i = 0; i < 9; i++) playbackEqEdits.Add(i);
        playbackPending = true; Edited(ControlArea.Playback);
    }
    private void ResetPickupPattern()
    {
        patternGeneration++; patternCancellation.Cancel(); patternCancellation.Dispose(); patternCancellation = new();
        patternBusy = false; PickupPatternInfo.Text = ""; PickupPatternInfo.ToolTip = null;
    }
    private async void LoadPickupPattern()
    {
        if (Selected is not { Direction: AudioDirection.Microphone, Usb: { VendorId: "1395", ProductId: "009F" } } endpoint) {
            PickupPatternInfo.Visibility = Visibility.Collapsed; return;
        }
        PickupPatternInfo.Visibility = Visibility.Visible;
        if (patternBusy) return;
        var generation = patternGeneration; var cancellation = patternCancellation.Token; patternBusy = true;
        if (PickupPatternInfo.Text.Length == 0) PickupPatternInfo.Text = "Pickup pattern: checking…";
        try {
            var state = await pickupPatterns.ReadAsync(endpoint, cancellation);
            if (generation != patternGeneration || Selected?.Id != endpoint.Id || Selected.ProfileIdentity != endpoint.ProfileIdentity) return;
            PickupPatternInfo.Text = $"Pickup pattern: {state.Label} · set with the physical switch";
            PickupPatternInfo.ToolTip = null;
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) {
            if (generation == patternGeneration) { PickupPatternInfo.Text = "Pickup pattern: unavailable"; PickupPatternInfo.ToolTip = ex.Message; }
        } finally { if (generation == patternGeneration) patternBusy = false; }
    }
    private void LoadSidetone(bool updateControls)
    {
        if (Selected is not { Direction: AudioDirection.Microphone, Usb.ProductId: "009F" } endpoint) { SidetoneCard.Visibility = Visibility.Collapsed; currentSidetone = null; return; }
        SidetoneCard.Visibility = Visibility.Visible;
        try {
            var state = sidetone.Read(endpoint); ApplySidetoneButton.IsEnabled = true;
            SidetoneInfo.Text = $"Current: L {state.Settings.LeftDb:0.##} dB · R {state.Settings.RightDb:0.##} dB · {(state.Settings.Muted ? "Muted" : "Unmuted")}";
            if (updateControls || currentSidetone is null) {
                loading = true;
                try {
                    currentSidetone = state;
                    SidetoneSlider.Minimum = state.MinimumDb;
                    SidetoneSlider.Maximum = Math.Max(state.MaximumDb, Math.Max(state.Settings.LeftDb, state.Settings.RightDb));
                    SidetoneSlider.Value = state.Settings.LeftDb; SidetoneMuteCheck.IsChecked = state.Settings.Muted;
                    sidetonePending = false; sidetoneLevelPending = false;
                } finally { loading = false; }
            }
        } catch (Exception ex) { currentSidetone = null; ApplySidetoneButton.IsEnabled = false; SidetoneInfo.Text = "Sidetone unavailable: " + ex.Message; }
        ShowFeatures(endpoint);
    }
    private void SidetoneLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SidetoneValue is not null) SidetoneValue.Text = $"{e.NewValue:+0.##;-0.##;0} dB";
        if (!loading) { sidetonePending = true; sidetoneLevelPending = true; Edited(ControlArea.Sidetone); }
    }
    private void SidetoneMuteChanged(object sender, RoutedEventArgs e) { if (!loading) { sidetonePending = true; Edited(ControlArea.Sidetone); } }
    private void ApplySidetoneClick(object sender, RoutedEventArgs e) { ApplySidetone(); UpdateDraftInfo(); }
    private bool ApplySidetone()
    {
        if (Selected is not { } endpoint || currentSidetone is null) { Status.Text = "Sidetone controls are unavailable."; return false; }
        try {
            var desired = currentSidetone.Settings with { Muted = SidetoneMuteCheck.IsChecked == true };
            if (sidetoneLevelPending) desired = desired with { LeftDb = (float)SidetoneSlider.Value, RightDb = (float)SidetoneSlider.Value };
            var result = sidetone.Apply(endpoint, currentSidetone, desired);
            if (!SidetoneBackend.Matches(result.Settings, currentSidetone.Settings)) MarkSetupPageEdited(endpoint);
            LoadSidetone(true); Status.Text = "Applied B20 sidetone.";
            return true;
        } catch (Exception ex) { LoadSidetone(true); Error("Sidetone was not applied", ex); return false; }
    }
    private void LevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LevelValue is not null) LevelValue.Text = $"{e.NewValue:0}%";
        if (!loading) { pending = true; volumePending = true; Edited(ControlArea.Volume); }
    }
    private void MarkPending(object sender, RoutedEventArgs e) { if (!loading) { pending = true; mutePending = true; Edited(ControlArea.Volume); } }
    private void LoadEffects(bool updateControls)
    {
        if (Selected is not { Direction: AudioDirection.Microphone } endpoint) { EffectsCard.Visibility = Visibility.Collapsed; currentEffects = null; UpdateEffectsEditorAvailability(); return; }
        EffectsCard.Visibility = Visibility.Visible;
        try {
            var state = effects.Read(endpoint);
            ApplyEffectsButton.IsEnabled = true;
            RestoreProcessingButton.IsEnabled = RestoreProcessingCheck.IsEnabled;
            var processing = state.Processing ?? throw new InvalidDataException("Complete microphone processing state is unavailable.");
            EffectsInfo.Text = $"Current gate: {state.GatePercent:0.#}% ({(processing.GateEnabled ? "on" : "off")}) · filter {state.FilterLevel} ({(processing.FilterEnabled ? "on" : "off")}) · high-pass {(processing.HighPassEnabled ? "on" : "off")} · EQ {(processing.EqualizerEnabled ? MicrophoneEqPresets.Name(processing.Equalizer) : "off")}";
            if (updateControls || currentEffects is null) {
                loading = true;
                try {
                    currentEffects = state; GateSlider.Value = state.GatePercent; FilterLevel.SelectedIndex = state.FilterLevel;
                    GateEnabledCheck.IsChecked = processing.GateEnabled; FilterEnabledCheck.IsChecked = processing.FilterEnabled;
                    HighPassCheck.IsChecked = processing.HighPassEnabled; EqEnabledCheck.IsChecked = processing.EqualizerEnabled;
                    SetEq(processing.Equalizer);
                    effectsPending = false;
                }
                finally { loading = false; }
            }
        } catch (Exception ex) { currentEffects = null; ApplyEffectsButton.IsEnabled = false; RestoreProcessingButton.IsEnabled = false; EffectsInfo.Text = "Processing unavailable: " + ex.Message; }
        UpdateEffectsEditorAvailability();
        ShowFeatures(endpoint);
    }
    private void UpdateEffectsEditorAvailability()
    {
        var available = currentEffects?.Processing is not null;
        MicrophoneProcessingEditors.IsEnabled = available;
        MicrophoneProcessingEditors.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        EffectsInfo.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        HighPassCheck.Visibility = Selected is { } endpoint && IsB20(endpoint) ? Visibility.Visible : Visibility.Collapsed;
        HighPassCheck.IsEnabled = available && Selected is { } selected && IsB20(selected);
    }
    private void EffectsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GateValue is not null) GateValue.Text = $"{e.NewValue:0}%";
        if (!loading) { effectsPending = true; Edited(ControlArea.MicrophoneProcessing); }
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (!loading) { effectsPending = true; Edited(ControlArea.MicrophoneProcessing); } }
    private void ProcessingToggleChanged(object sender, RoutedEventArgs e) { if (!loading) { effectsPending = true; Edited(ControlArea.MicrophoneProcessing); } }
    private void BuildEqControls()
    {
        for (var i = 0; i < 9; i++) {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var frequency = MicrophoneEqPresets.B20BandLabels[i];
            panel.Children.Add(new TextBlock { Text = frequency, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            var slider = new Slider { Minimum = -6, Maximum = 6, TickFrequency = .01, IsSnapToTickEnabled = true, Orientation = Orientation.Vertical, Height = 110, Width = 30, Margin = new Thickness(0, 8, 0, 8) };
            AutomationProperties.SetName(slider, $"Microphone EQ band {i + 1} at {frequency} in decibels");
            var label = new TextBlock { Text = "0 dB", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            slider.ValueChanged += (_, e) => {
                label.Text = $"{e.NewValue:+0.##;-0.##;0} dB";
                if (!loading) { effectsPending = true; SelectEqPreset("Custom"); Edited(ControlArea.MicrophoneProcessing); }
            };
            panel.Children.Add(slider); panel.Children.Add(label); EqBands.Children.Add(panel); eqSliders.Add(slider);
        }
    }
    private void SelectEqPreset(string name)
    {
        var wasLoading = loading; loading = true;
        try { EqPreset.SelectedIndex = name switch { "Flat" => 0, "Warm" => 1, "Clear" => 2, _ => 3 }; }
        finally { loading = wasLoading; }
    }
    private void SetEq(MicrophoneEqualizer eq)
    {
        var wasLoading = loading; loading = true;
        try { var levels = eq.Levels(); for (var i = 0; i < levels.Length; i++) eqSliders[i].Value = levels[i]; SelectEqPreset(MicrophoneEqPresets.Name(eq)); }
        finally { loading = wasLoading; }
    }
    private void EqPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || eqSliders.Count != 9) return;
        var eq = EqPreset.SelectedIndex switch { 0 => MicrophoneEqPresets.Flat, 1 => MicrophoneEqPresets.Warm, 2 => MicrophoneEqPresets.Clear, _ => null };
        if (eq is null) return;
        SetEq(eq); effectsPending = true; Edited(ControlArea.MicrophoneProcessing);
    }
    private MicrophoneEffects DesiredEffects() => new((float)GateSlider.Value, FilterLevel.SelectedIndex,
        new(FilterEnabledCheck.IsChecked == true, GateEnabledCheck.IsChecked == true,
            Selected is { } endpoint && IsB20(endpoint) ? HighPassCheck.IsChecked == true : currentEffects?.Processing?.HighPassEnabled ?? false, EqEnabledCheck.IsChecked == true,
            MicrophoneEqualizer.FromLevels(eqSliders.Select(s => (float)s.Value).ToArray())));
    private void ApplyEffectsClick(object sender, RoutedEventArgs e) { ApplyEffects(); UpdateDraftInfo(); }
    private bool ApplyEffects()
    {
        if (Selected is not { } endpoint || currentEffects is null) { Status.Text = "Microphone processing is unavailable."; return false; }
        try {
            var result = IsB20(endpoint)
                ? processingStates.ApplyAndSave(endpoint, effects, currentEffects, DesiredEffects())
                : new ProcessingApplyResult(effects.Apply(endpoint, currentEffects, DesiredEffects()), null);
            if (!ApoMicrophoneCodec.Matches(result.Effects, currentEffects)) MarkSetupPageEdited(endpoint);
            LoadEffects(true); LoadSavedProcessing();
            Status.Text = result.PersistenceError is null ? "Applied microphone processing to " + endpoint.Name + (IsB20(endpoint) ? " and saved it for restore." : "")
                : "Processing applied, but its restore state was not saved: " + result.PersistenceError;
            return true;
        } catch (Exception ex) { LoadEffects(true); Error("Processing was not applied", ex); return false; }
    }
    private void ApplyClick(object sender, RoutedEventArgs e) { ApplyVolume(); UpdateDraftInfo(); }
    private bool ApplyVolume()
    {
        if (Selected is not { } endpoint || currentAudio is null) { Status.Text = "Volume controls are unavailable."; return false; }
        try {
            var current = backend.Read(endpoint.Id);
            if (volumePending && Math.Abs(current.Level - currentAudio.Level) > .0001f || mutePending && current.Muted != currentAudio.Muted)
                throw new InvalidOperationException("Level or mute changed externally. Reload before adjusting it.");
            var result = backend.Apply(endpoint.Id, volumePending ? (float)(LevelSlider.Value / 100) : current.Level,
                mutePending ? MuteCheck.IsChecked == true : current.Muted);
            if (Math.Abs(result.Level - current.Level) > .0001f || result.Muted != current.Muted) MarkSetupPageEdited(endpoint);
            ShowState(result, true);
            Status.Text = "Applied to " + endpoint.Name; return true;
        } catch (Exception ex) { try { ShowState(backend.Read(endpoint.Id), true); } catch { ApplyButton.IsEnabled = false; }
            Error("Settings were not applied", ex); return false; }
    }
    private async void SaveProfileClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } endpoint) return;
        try {
            if (LiveApplyCheck.IsChecked == true && !FlushLiveChanges(true)) { Status.Text += " Profile was not saved."; return; }
            if (LiveApplyCheck.IsChecked == true && !await FlushGsxSidetoneAsync()) { Status.Text += " Profile was not saved."; return; }
            var name = ProfileName.Text.Trim(); var gsx = await CaptureGsxSidetoneAsync(endpoint);
            if (Selected?.Id != endpoint.Id) throw new InvalidOperationException("The device changed before its profile was saved.");
            var profile = CaptureProfile(endpoint, name, gsx);
            profiles.SaveForDevice(endpoint, profile);
            LoadProfiles(profile.Name); Status.Text = "Saved current device settings as “" + profile.Name + "”.";
        } catch (Exception ex) { Error("Cannot save profile", ex); }
    }
    private AudioProfile CaptureProfile(AudioEndpoint endpoint, string name, GsxSidetoneSettings? gsxSnapshot = null)
    {
        var state = backend.Read(endpoint.Id);
        var processing = currentEffects is not null ? effects.Read(endpoint) : null;
        var monitoring = currentSidetone is not null ? sidetone.Read(endpoint).Settings : null;
        var sound = currentPlayback is not null ? playback.Read(endpoint) : null;
        if (demo && GsxSidetoneProtocol.Supports(endpoint)) gsxSnapshot ??= gsxSidetone.Read(endpoint).Settings;
        return new(name, endpoint.Id, ProfileStore.DeviceIdentity(endpoint), endpoint.Direction, state.Level, state.Muted, processing, monitoring, sound, gsxSnapshot);
    }
    private async void SaveSelectedProfileClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } endpoint || ProfileList.SelectedItem is not AudioProfile selected) { Status.Text = "Choose a profile to save to."; return; }
        try {
            if (selected.Effects is not null && currentEffects is null || selected.Sidetone is not null && currentSidetone is null || selected.Playback is not null && currentPlayback is null || selected.GsxSidetone is not null && currentGsxSidetone is null)
                throw new InvalidOperationException("Some settings in this profile are unavailable. Reconnect or reload before saving to it.");
            if (LiveApplyCheck.IsChecked == true && !FlushLiveChanges(true)) { Status.Text += " Profile was not saved."; return; }
            if (LiveApplyCheck.IsChecked == true && !await FlushGsxSidetoneAsync()) { Status.Text += " Profile was not saved."; return; }
            var gsx = await CaptureGsxSidetoneAsync(endpoint);
            if (Selected?.Id != endpoint.Id) throw new InvalidOperationException("The device changed before its profile was saved.");
            profiles.UpdateSelected(endpoint, selected, CaptureProfile(endpoint, selected.Name, gsx));
            LoadProfiles(selected.Name); Status.Text = "Saved changes to “" + selected.Name + "”.";
        } catch (Exception ex) { Error("Cannot save to selected profile", ex); }
    }
    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SaveSelectedProfileButton is not null) SaveSelectedProfileButton.IsEnabled = ProfileList.SelectedItem is AudioProfile;
    }
    private void LoadProfiles(string? selectName = null)
    {
        if (Selected is not { } endpoint) return;
        selectName ??= (ProfileList.SelectedItem as AudioProfile)?.Name;
        ProfileList.ItemsSource = profiles.ForDevice(endpoint);
        ProfileList.SelectedItem = ProfileList.Items.Cast<AudioProfile>().FirstOrDefault(p => p.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase));
        if (ProfileList.SelectedItem is null && ProfileList.Items.Count > 0) ProfileList.SelectedIndex = 0;
        SaveSelectedProfileButton.IsEnabled = ProfileList.SelectedItem is AudioProfile;
    }
    private async void ApplyProfileClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } endpoint || ProfileList.SelectedItem is not AudioProfile profile) { Status.Text = "Choose a saved profile first."; return; }
        CancelLiveChanges();
        try {
            var result = profile.GsxSidetone is not null ? await RunUsbWork(() => ProfileStore.Apply(backend, endpoint, profile, effects, sidetone, playback, gsxSidetone))
                : ProfileStore.Apply(backend, endpoint, profile, effects, sidetone, playback);
            if (Selected?.Id != endpoint.Id) throw new InvalidOperationException("The selected device changed during profile application.");
            ShowState(result, true); LoadEffects(true); LoadSidetone(true); LoadPlayback(true); gsxSidetonePending = false; LoadGsxSidetone(true); UpdateDraftInfo();
            Status.Text = "Applied “" + profile.Name + "” to " + endpoint.Name;
            if (IsB20(endpoint) && profile.Effects is { Processing: not null } desired) {
                try {
                    if (currentEffects is null || !ApoMicrophoneCodec.Matches(currentEffects, desired)) throw new InvalidDataException("Processing changed after applying the profile.");
                    processingStates.Remember(endpoint, currentEffects); LoadSavedProcessing();
                } catch (Exception ex) { Status.Text += ". Processing restore state was not saved: " + ex.Message; }
            }
        }
        catch (Exception ex) { Error("Cannot apply profile", ex); PollCurrent(); }
    }
    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshDevices();
    private static bool IsB20(AudioEndpoint endpoint) => endpoint.Direction == AudioDirection.Microphone && endpoint.Usb is { } usb &&
        usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) && usb.ProductId.Equals("009F", StringComparison.OrdinalIgnoreCase);
    private void LoadSavedProcessing()
    {
        SavedProcessingPanel.Visibility = Selected is { } endpoint && IsB20(endpoint) ? Visibility.Visible : Visibility.Collapsed;
        if (Selected is not { } mic || !IsB20(mic)) return;
        var wasLoading = loading; loading = true;
        try {
            var saved = processingStates.Load(mic);
            RestoreProcessingButton.IsEnabled = saved is not null && currentEffects is not null;
            RestoreProcessingCheck.IsEnabled = saved is not null;
            RestoreProcessingCheck.IsChecked = saved?.RestoreOnConnect == true;
            SavedProcessingInfo.Text = saved is null ? "Apply processing to save a restore state for this B20."
                : $"Processing saved {saved.SavedAtUtc.ToLocalTime():g}. Restore includes gate, filter, high-pass and EQ.";
        } catch (Exception ex) {
            RestoreProcessingButton.IsEnabled = false; RestoreProcessingCheck.IsEnabled = false; RestoreProcessingCheck.IsChecked = false;
            SavedProcessingInfo.Text = "Saved processing unavailable: " + ex.Message;
        } finally { loading = wasLoading; }
    }
    private void RestoreProcessingChanged(object sender, RoutedEventArgs e)
    {
        if (loading || Selected is not { } endpoint || !IsB20(endpoint)) return;
        try {
            processingStates.SetRestoreOnConnect(endpoint, RestoreProcessingCheck.IsChecked == true);
            Status.Text = RestoreProcessingCheck.IsChecked == true ? "Processing will restore when this B20 next connects or the app starts."
                : "Automatic processing restore is off.";
        } catch (Exception ex) { Error("Cannot save restore preference", ex); LoadSavedProcessing(); }
    }
    private void RestoreProcessingClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } endpoint || !IsB20(endpoint)) return;
        CancelLiveChanges();
        try { processingStates.Restore(endpoint, backend, effects); LoadEffects(true); Status.Text = "Restored saved B20 processing."; }
        catch (Exception ex) { Error("Cannot restore processing", ex); LoadEffects(true); }
        LoadSavedProcessing();
    }
    private void CheckProcessingRestores()
    {
        foreach (var endpoint in endpoints.Where(IsB20)) {
            if (Selected?.Id == endpoint.Id && effectsPending) continue;
            // Do not consume the connection's attempt while its APO interface is unavailable.
            try { effects.Read(endpoint); } catch { continue; }
            try {
                if (processingRestore.RestoreIfEnabled(endpoint, backend, effects) is not null) {
                    if (Selected?.Id == endpoint.Id) { LoadEffects(true); LoadSavedProcessing(); }
                    Status.Text = "Restored saved processing for " + endpoint.Name;
                }
            } catch (Exception ex) { Error("Automatic processing restore failed; use Restore saved processing to retry", ex); }
        }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "epos-devices.json", Filter = "JSON diagnostics|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try {
            var report = new ControlDiagnostics(backend, catalog, effects, playback, sidetone).Collect(demo);
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Status.Text = "Diagnostics saved to " + dialog.FileName;
        } catch (Exception ex) { Error("Cannot export diagnostics", ex); }
    }
    private void Error(string action, Exception ex) => Status.Text = action + ": " + ex.Message;

    internal void VerifyDemoUi()
    {
        if (!demo || Selected is null) throw new InvalidOperationException("UI verification requires demo devices.");
        if (LiveApplyCheck.IsChecked != true || liveQueue.HasPending) throw new InvalidOperationException("Live changes must default on without writes at startup.");
        LiveApplyCheck.IsChecked = false;
        var demoPatterns = (DemoPickupPatternBackend)pickupPatterns;
        foreach (var pattern in Enum.GetValues<PickupPattern>()) {
            demoPatterns.SetPhysicalSwitchForDemo(Selected, pattern); LoadPickupPattern();
            if (PickupPatternInfo.Visibility != Visibility.Visible || !PickupPatternInfo.Text.Contains(new PickupPatternState(pattern, default).Label))
                throw new InvalidOperationException("Pickup-pattern status did not update from the physical switch report.");
        }
        demoPatterns.SetPhysicalSwitchForDemo(Selected, PickupPattern.Cardioid); LoadPickupPattern();
        var expectedFrequencies = new[] { "64 Hz", "125 Hz", "250 Hz", "500 Hz", "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz" };
        for (var i = 0; i < expectedFrequencies.Length; i++) {
            var panel = (StackPanel)EqBands.Children[i];
            if (((TextBlock)panel.Children[0]).Text != expectedFrequencies[i] ||
                AutomationProperties.GetName(eqSliders[i]) != $"Microphone EQ band {i + 1} at {expectedFrequencies[i]} in decibels")
                throw new InvalidOperationException($"EQ frequency label or accessibility mapping failed for band {i + 1}.");
        }
        var originalEffects = effects.Read(Selected);
        var originalSidetone = sidetone.Read(Selected);
        if (!catalog.Features(Selected, currentEffects is not null, currentSidetone is not null).Any(f => f.Name == "Sidetone" && f.State == FeatureState.Ready))
            throw new InvalidOperationException("Sidetone availability did not follow the live read.");
        var asymmetric = sidetone.Apply(Selected, originalSidetone, new(2.9765625f, -2.25f, false)); LoadSidetone(true);
        SidetoneMuteCheck.IsChecked = true; ApplySidetoneClick(this, new RoutedEventArgs());
        var mutedSide = sidetone.Read(Selected);
        if (mutedSide.Settings != asymmetric.Settings with { Muted = true }) throw new InvalidOperationException("Sidetone mute changed asymmetric channel levels.");
        sidetone.Apply(Selected, mutedSide, originalSidetone.Settings); LoadSidetone(true);
        SidetoneSlider.Value = -12; SidetoneMuteCheck.IsChecked = true; PollCurrent();
        if (sidetone.Read(Selected) != originalSidetone || SidetoneSlider.Value != -12 || SidetoneMuteCheck.IsChecked != true)
            throw new InvalidOperationException("Pending sidetone edits were applied or lost while polling.");
        ApplySidetoneClick(this, new RoutedEventArgs());
        var profileSidetone = sidetone.Read(Selected).Settings;
        if (profileSidetone != new SidetoneSettings(-12, -12, true)) throw new InvalidOperationException("Sidetone apply failed.");
        var fractional = originalEffects with { GatePercent = 49.411766f, FilterLevel = 1 };
        effects.Apply(Selected, originalEffects, fractional); LoadEffects(true);
        HighPassCheck.IsChecked = false; ApplyEffectsClick(this, new RoutedEventArgs());
        var switched = effects.Read(Selected);
        if (switched.GatePercent != fractional.GatePercent || switched.FilterLevel != fractional.FilterLevel)
            throw new InvalidOperationException("High-pass UI changed unrelated fractional settings.");
        effects.Apply(Selected, switched, originalEffects); LoadEffects(true);
        GateSlider.Value = 37; FilterLevel.SelectedIndex = 1;
        PollCurrent();
        if (effects.Read(Selected) != originalEffects || GateSlider.Value != 37 || FilterLevel.SelectedIndex != 1)
            throw new InvalidOperationException("Pending edits were applied or lost while polling.");
        LevelSlider.Value = 53; MuteCheck.IsChecked = true; ApplyClick(this, new RoutedEventArgs());
        var state = backend.Read(Selected.Id);
        if (Math.Abs(state.Level - .53f) > .001 || !state.Muted) throw new InvalidOperationException("Microphone controls failed.");
        GateSlider.Value = 37; FilterLevel.SelectedIndex = 1;
        HighPassCheck.IsChecked = false; FilterEnabledCheck.IsChecked = false;
        EqPreset.SelectedIndex = 1;
        var desired = DesiredEffects();
        ApplyEffectsClick(this, new RoutedEventArgs());
        if (!ApoMicrophoneCodec.Matches(effects.Read(Selected), desired)) throw new InvalidOperationException("Microphone processing UI failed.");
        SaveProfileClick(this, new RoutedEventArgs());
        if (ProfileList.Items.Count != 1) throw new InvalidOperationException("Profile save failed.");
        LevelSlider.Value = 30; MuteCheck.IsChecked = false; ApplyClick(this, new RoutedEventArgs());
        SidetoneSlider.Value = 0; SidetoneMuteCheck.IsChecked = false; ApplySidetoneClick(this, new RoutedEventArgs());
        GateSlider.Value = 12; FilterLevel.SelectedIndex = 0; ApplyEffectsClick(this, new RoutedEventArgs());
        FilterEnabledCheck.IsChecked = true; HighPassCheck.IsChecked = true; EqPreset.SelectedIndex = 2; eqSliders[8].Value = -2.5;
        if (EqPreset.SelectedIndex != 3) throw new InvalidOperationException("Custom EQ selection failed.");
        var customEq = DesiredEffects().Processing!.Equalizer;
        GateEnabledCheck.IsChecked = false; EqEnabledCheck.IsChecked = false;
        ApplyEffectsClick(this, new RoutedEventArgs());
        var disabled = effects.Read(Selected).Processing!;
        if (disabled.GateEnabled || disabled.EqualizerEnabled || disabled.Equalizer != customEq)
            throw new InvalidOperationException("Disabling processing discarded saved settings.");
        ApplyProfileClick(this, new RoutedEventArgs());
        state = backend.Read(Selected.Id);
        if (Math.Abs(state.Level - .53f) > .001 || !state.Muted) throw new InvalidOperationException("Profile apply failed.");
        if (!ApoMicrophoneCodec.Matches(effects.Read(Selected), desired)) throw new InvalidOperationException("Profile effects restore failed.");
        if (sidetone.Read(Selected).Settings != profileSidetone) throw new InvalidOperationException("Profile sidetone restore failed.");
        var remembered = processingStates.Load(Selected) ?? throw new InvalidOperationException("Processing apply did not save its restore state.");
        if (!ApoMicrophoneCodec.Matches(remembered.Effects, desired) || remembered.RestoreOnConnect ||
            new ProcessingStateStore(processingStates.DirectoryPath).Load(Selected) != remembered)
            throw new InvalidOperationException("Processing state or default restore policy did not survive reload.");
        GateSlider.Value = 17; PollCurrent();
        if (processingStates.Load(Selected) != remembered) throw new InvalidOperationException("Pending UI edits were persisted before apply.");
        RestoreProcessingClick(this, new RoutedEventArgs());
        if (!ApoMicrophoneCodec.Matches(effects.Read(Selected), desired) || GateSlider.Value != desired.GatePercent)
            throw new InvalidOperationException("Manual processing restore failed.");
        RestoreProcessingCheck.IsChecked = true;
        if (!processingStates.Load(Selected)!.RestoreOnConnect) throw new InvalidOperationException("Automatic restore preference was not persisted.");
        effects.Apply(Selected, desired, desired with { GatePercent = 12 });
        processingRestore.Observe([]); CheckProcessingRestores();
        if (!ApoMicrophoneCodec.Matches(effects.Read(Selected), desired)) throw new InvalidOperationException("Opt-in reconnect processing restore failed.");
        var external = effects.Apply(Selected, desired, desired with { GatePercent = 12 }); CheckProcessingRestores();
        if (effects.Read(Selected) != external) throw new InvalidOperationException("Repeated polling overwrote an external processing change.");
        RestoreProcessingCheck.IsChecked = false; RestoreProcessingClick(this, new RoutedEventArgs());
        var stateFile = Directory.EnumerateFiles(processingStates.DirectoryPath, "*.json").Single();
        using (var denyReplace = new FileStream(stateFile, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            GateSlider.Value = 17; ApplyEffectsClick(this, new RoutedEventArgs());
            if (effects.Read(Selected).GatePercent != 17 || !Status.Text.StartsWith("Processing applied, but") || processingStates.Load(Selected)!.Effects != desired)
                throw new InvalidOperationException("A save failure was confused with a hardware apply failure.");
        }
        RestoreProcessingClick(this, new RoutedEventArgs());
        DeviceList.SelectedItem = endpoints.First(e => e.Direction == AudioDirection.Playback);
        if (!catalog.Features(Selected!, playbackEffectsReady: currentPlayback is not null).Any(f => f.Name == "7.1 surround" && f.State == FeatureState.Ready)) throw new InvalidOperationException("Playback capabilities failed.");
        if (ProfileList.Items.Count != 0) throw new InvalidOperationException("Microphone profile leaked into playback.");
        if (EffectsCard.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Mic processing leaked into playback.");
        if (SidetoneCard.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Sidetone leaked into playback.");
        if (PickupPatternInfo.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Pickup-pattern status leaked into playback.");
        if (SavedProcessingPanel.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Processing restore controls leaked into playback.");
        if (PlaybackCard.Visibility != Visibility.Visible || currentPlayback is null || SoundMode.SelectedIndex != 0)
            throw new InvalidOperationException("GSX playback controls were not routed to the output endpoint.");
        var soundEndpoint = Selected!;
        SoundMode.SelectedIndex = 1; PollCurrent();
        if (playback.Read(soundEndpoint).SurroundEnabled || SoundMode.SelectedIndex != 1 || !DraftInfo.Text.Contains("sound mode"))
            throw new InvalidOperationException("Pending sound edits were applied, lost or unmarked during polling.");
        DiscardChangesClick(this, new RoutedEventArgs());
        if (playback.Read(soundEndpoint).SurroundEnabled || SoundMode.SelectedIndex != 0 || DiscardChangesButton.IsEnabled)
            throw new InvalidOperationException("Discarding sound edits changed device settings or retained pending state.");
        SoundMode.SelectedIndex = 1; ApplyPlaybackClick(this, new RoutedEventArgs());
        if (!playback.Read(soundEndpoint).SurroundEnabled || playbackPending) throw new InvalidOperationException("GSX sound-mode apply failed.");
        for (var i = 0; i < 9; i++) if (AutomationProperties.GetName(playbackEqSliders[i]) != $"Playback EQ band {i + 1} at {PlaybackEqualizer.Gsx300BandLabels[i]} in decibels")
            throw new InvalidOperationException("Playback EQ labels or accessibility order failed.");
        playbackEqSliders[0].Value = 3; playbackEqSliders[8].Value = -6; PollCurrent();
        if (playback.Read(soundEndpoint).Equalizer != PlaybackEqualizer.Flat || playbackEqSliders[0].Value != 3 || playbackEqSliders[8].Value != -6)
            throw new InvalidOperationException("Pending playback EQ was lost or applied while polling.");
        ApplyPlaybackClick(this, new RoutedEventArgs());
        var soundCurve = new PlaybackEqualizer(3, 0, 0, 0, 0, 0, 0, 0, -6);
        if (playback.Read(soundEndpoint).Equalizer != soundCurve) throw new InvalidOperationException("Playback EQ apply failed.");
        ProfileName.Text = "Headphones"; SaveProfileClick(this, new RoutedEventArgs());
        SoundMode.SelectedIndex = 0; SaveProfileClick(this, new RoutedEventArgs());
        if (!profiles.ForDevice(soundEndpoint).Single().Playback!.SurroundEnabled)
            throw new InvalidOperationException("Save current persisted unapplied sound edits.");
        FlatPlaybackEqClick(this, new RoutedEventArgs());
        if (playback.Read(soundEndpoint).Equalizer != soundCurve) throw new InvalidOperationException("Flat button applied a draft without Apply.");
        ApplyPlaybackClick(this, new RoutedEventArgs()); ApplyProfileClick(this, new RoutedEventArgs());
        if (!playback.Read(soundEndpoint).SurroundEnabled || SoundMode.SelectedIndex != 1 || playback.Read(soundEndpoint).Equalizer != soundCurve)
            throw new InvalidOperationException("Playback profile did not restore sound mode and EQ.");
        SoundMode.SelectedIndex = 0;
        playback.Apply(soundEndpoint, playback.Read(soundEndpoint), new(false));
        ApplyPlaybackClick(this, new RoutedEventArgs());
        if (playback.Read(soundEndpoint).SurroundEnabled || !Status.Text.StartsWith("Sound mode was not applied"))
            throw new InvalidOperationException("A stale sound draft overwrote a newer external setting.");
        var preciseCurve = soundCurve with { Band3 = 1.234567f };
        playback.Apply(soundEndpoint, playback.Read(soundEndpoint), new(false, preciseCurve)); LoadPlayback(true);
        SoundMode.SelectedIndex = 1; ApplyPlaybackClick(this, new RoutedEventArgs());
        if (playback.Read(soundEndpoint).Equalizer != preciseCurve) throw new InvalidOperationException("Sound-mode UI rounded untouched EQ values.");
        playbackEqSliders[0].Value = 2; ApplyPlaybackClick(this, new RoutedEventArgs());
        if (playback.Read(soundEndpoint).Equalizer != preciseCurve with { Band1 = 2 }) throw new InvalidOperationException("Editing one playback band rounded other bands.");
        FlatPlaybackEqClick(this, new RoutedEventArgs()); ApplyPlaybackClick(this, new RoutedEventArgs());
        if (playback.Read(soundEndpoint).Equalizer != PlaybackEqualizer.Flat) throw new InvalidOperationException("Flat reset did not restore all playback bands.");
        SoundMode.SelectedIndex = 0; ApplyPlaybackClick(this, new RoutedEventArgs());
        MicrophonePageClick(this, new RoutedEventArgs());
        if (Selected?.Direction != AudioDirection.Microphone || Selected.Usb?.InstanceId != soundEndpoint.Usb!.InstanceId || PlaybackCard.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Sound/microphone navigation did not stay on the selected physical GSX.");
        SoundPageClick(this, new RoutedEventArgs());
        if (Selected?.Id != soundEndpoint.Id) throw new InvalidOperationException("Sound navigation did not return to GSX output.");
        if (MicrophoneProcessingExpander.IsExpanded || ProfilesExpander.IsExpanded || FeaturesExpander.IsExpanded || PlaybackEqExpander.IsExpanded)
            throw new InvalidOperationException("Detailed controls did not start collapsed.");
        DeviceList.SelectedItem = endpoints.First(e => e.Direction == AudioDirection.Microphone);
        if (PlaybackCard.Visibility != Visibility.Collapsed || SoundPageButton.IsEnabled)
            throw new InvalidOperationException("GSX sound controls leaked into the B20 microphone page.");
        VerifyLiveDemoUi();
        VerifySetupDemoUi();
        VerifySetupDeviceGroupsDemoUi();
        VerifyMicrophoneMonitorUi();
        VerifyUnavailableEffectsUi();
        VerifyCompactControlsUi();
        VerifyGsxMicrophoneUi();
        VerifyPlaybackMonitorUi();
        VerifyReverbUi();
        VerifyGsxSidetoneUi();
        VerifyGsxAsyncUi();
        VerifyCaptureQualityUi();
    }
    internal void SelectDemoSoundPage()
    {
        if (!demo) throw new InvalidOperationException("Sound preview requires demo devices.");
        DeviceList.SelectedItem = endpoints.First(e => e.Direction == AudioDirection.Playback);
    }
    private void VerifyLiveDemoUi()
    {
        var mic = Selected!;
        var before = backend.Read(mic.Id);
        LiveApplyCheck.IsChecked = true;
        if (ApplyButton.Visibility != Visibility.Collapsed || ApplyPlaybackButton.Visibility != Visibility.Collapsed || ApplyEffectsButton.Visibility != Visibility.Collapsed || ApplySidetoneButton.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Manual Apply buttons remained visible with live changes enabled.");
        if (backend.Read(mic.Id) != before || liveQueue.HasPending) throw new InvalidOperationException("Enabling live mode wrote current controls.");
        LevelSlider.Value = 31; LevelSlider.Value = 32; LevelSlider.Value = 33;
        if (backend.Read(mic.Id) != before || !liveQueue.HasPending) throw new InvalidOperationException("Live volume did not debounce slider changes.");
        var frame = new DispatcherFrame();
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
        deadline.Start(); Dispatcher.PushFrame(frame);
        if (backend.Read(mic.Id).Level != .33f || pending || liveQueue.HasPending) throw new InvalidOperationException("The real live-change timer did not apply the final slider value.");

        // A mute-only live edit preserves an undisplayed fractional volume.
        backend.Apply(mic.Id, .333333f, false); ShowState(backend.Read(mic.Id), true);
        MuteCheck.IsChecked = true; FlushLiveChanges(true);
        if (backend.Read(mic.Id).Level != .333333f || !backend.Read(mic.Id).Muted) throw new InvalidOperationException("Live mute rounded an untouched volume.");
        var processingBefore = effects.Read(mic);
        GateSlider.Value = 23; FilterLevel.SelectedIndex = 2; HighPassCheck.IsChecked = !HighPassCheck.IsChecked;
        eqSliders[2].Value = 1.25; var desiredProcessing = DesiredEffects();
        FlushLiveChanges(true);
        if (!ApoMicrophoneCodec.Matches(effects.Read(mic), desiredProcessing)) throw new InvalidOperationException("Live microphone processing did not apply without an Apply click.");
        SidetoneSlider.Value = -10; SidetoneMuteCheck.IsChecked = true; FlushLiveChanges(true);
        if (sidetone.Read(mic).Settings != new SidetoneSettings(-10, -10, true)) throw new InvalidOperationException("Live sidetone did not apply.");

        // Selection cancels old work before its timer can touch either page.
        GateSlider.Value = 24;
        var output = endpoints.First(e => e.Direction == AudioDirection.Playback);
        DeviceList.SelectedItem = output; FlushLiveChanges(true);
        if (effects.Read(mic).GatePercent != 23 || liveQueue.HasPending) throw new InvalidOperationException("Queued microphone edits survived changing the selected device.");
        var soundBefore = playback.Read(output);
        SoundMode.SelectedIndex = 1; playbackEqSliders[0].Value = 2; playbackEqSliders[8].Value = -2;
        if (playback.Read(output) != soundBefore) throw new InvalidOperationException("Sound preview bypassed its debounce.");
        FlushLiveChanges(true);
        var liveSound = playback.Read(output);
        if (!liveSound.SurroundEnabled || liveSound.Equalizer is not { Band1: 2, Band9: -2 }) throw new InvalidOperationException("Live sound mode / EQ did not apply.");

        // Auditioning never writes a named profile by itself.
        var saved = profiles.ForDevice(output).Single();
        playbackEqSliders[1].Value = 1; FlushLiveChanges(true);
        if (profiles.ForDevice(output).Single() != saved) throw new InvalidOperationException("Auditioning rewrote a named profile.");
        playbackEqSliders[1].Value = 1.5;
        SaveSelectedProfileClick(this, new RoutedEventArgs());
        var updated = profiles.ForDevice(output).Single();
        if (updated.Name != saved.Name || updated.Playback != playback.Read(output) || updated.Playback!.Equalizer!.Band2 != 1.5f || liveQueue.HasPending)
            throw new InvalidOperationException("Save to selected did not flush the latest live edit and update that profile.");
        var micProfiles = profiles.ForDevice(mic);
        if (micProfiles.Count != 1 || micProfiles[0].Direction != AudioDirection.Microphone) throw new InvalidOperationException("Saving a sound profile altered microphone profiles.");

        playbackEqSliders[0].Value = -4; ApplyProfileClick(this, new RoutedEventArgs()); FlushLiveChanges(true);
        if (playback.Read(output) != updated.Playback) throw new InvalidOperationException("Queued edits ran after loading a profile.");
        playbackEqSliders[0].Value = -4; DiscardChangesClick(this, new RoutedEventArgs()); FlushLiveChanges(true);
        if (playback.Read(output) != updated.Playback) throw new InvalidOperationException("Discard failed to cancel queued sound edits.");

        // Stale native state must fail once and preserve the external edit.
        playbackEqSliders[0].Value = -3;
        var external = updated.Playback! with { Equalizer = updated.Playback.Equalizer! with { Band1 = 5 } };
        playback.Apply(output, playback.Read(output), external);
        if (FlushLiveChanges(true) || playback.Read(output) != external || liveQueue.HasPending) throw new InvalidOperationException("Live failure overwrote a newer sound change or retained retry work.");
        FlushLiveChanges(true);
        if (playback.Read(output) != external) throw new InvalidOperationException("Failed live changes retried without another edit.");

        // Saving while a live edit conflicts must not save a misleading profile.
        var beforeFailedSave = profiles.ForDevice(output).Single();
        playbackEqSliders[0].Value = -3;
        var newer = external with { Equalizer = external.Equalizer! with { Band1 = 4.5f } };
        playback.Apply(output, external, newer);
        SaveSelectedProfileClick(this, new RoutedEventArgs());
        if (profiles.ForDevice(output).Single() != beforeFailedSave || playback.Read(output) != newer || !Status.Text.EndsWith("Profile was not saved."))
            throw new InvalidOperationException("A failed live update still saved a named profile.");
        external = newer;

        playbackEqSliders[0].Value = 0; LiveApplyCheck.IsChecked = false; FlushLiveChanges(true);
        if (ApplyPlaybackButton.Visibility != Visibility.Visible) throw new InvalidOperationException("Manual Apply button did not return when live changes were disabled.");
        if (playback.Read(output) != external || !playbackPending) throw new InvalidOperationException("Disabling live changes applied or lost its pending draft.");
        LiveApplyCheck.IsChecked = true; FlushLiveChanges(true);
        if (playback.Read(output) != external) throw new InvalidOperationException("Enabling live changes applied an old manual draft.");
        FlatPlaybackEqClick(this, new RoutedEventArgs()); FlushLiveChanges(true);
        if (playback.Read(output).Equalizer != PlaybackEqualizer.Flat) throw new InvalidOperationException("Live Flat reset did not apply.");
        ProfileName.Text = "Listening"; SoundMode.SelectedIndex = 0; SaveProfileClick(this, new RoutedEventArgs());
        if (profiles.ForDevice(output).Count != 2 || (ProfileList.SelectedItem as AudioProfile)?.Name != "Listening" || playback.Read(output).SurroundEnabled)
            throw new InvalidOperationException("Save as profile did not create/select a device profile with current live edits.");
        var profileBefore = profiles.ForDevice(output).Single(p => p.Name == "Listening");
        profiles.SaveForDevice(output, profileBefore with { Level = .45f });
        SaveSelectedProfileClick(this, new RoutedEventArgs());
        if (!Status.Text.StartsWith("Cannot save to selected profile") || profiles.ForDevice(output).Single(p => p.Name == "Listening").Level != .45f)
            throw new InvalidOperationException("Saving to a stale selected profile overwrote newer saved data.");
        CancelLiveChanges();
        DeviceList.SelectedItem = mic; LoadEffects(true);
        effects.Apply(mic, effects.Read(mic), processingBefore); LoadEffects(true);
        var existingMicProfile = profiles.ForDevice(mic).Single();
        currentEffects = null; SaveSelectedProfileClick(this, new RoutedEventArgs());
        if (profiles.ForDevice(mic).Single() != existingMicProfile || !Status.Text.StartsWith("Cannot save to selected profile"))
            throw new InvalidOperationException("Unavailable processing was dropped from a saved profile.");
        LoadEffects(true);
    }
}
