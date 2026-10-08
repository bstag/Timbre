using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Timbre.Core;

namespace Timbre.App;

public partial class App : Application
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try {
            var args = e.Args;
            if (args.Contains("--diagnostics")) {
                var path = ArgumentValue(args, "--diagnostics");
                var backend = new CoreAudioBackend();
                var diagnosticCatalog = new DeviceCatalog(JsonSerializer.Deserialize<List<DeviceDefinition>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "devices.json"))) ?? []);
                var report = new ControlDiagnostics(backend, diagnosticCatalog, WindowsApoMemory.Create(backend),
                    WindowsApoMemory.CreatePlayback(backend), WindowsSidetone.Create(backend)).Collect();
                SaveJson(path, report); Shutdown(); return;
            }
            if (args.Contains("--verify-control")) {
                VerifyControl(ArgumentValue(args, "--verify-control"), ArgumentValue(args, "--report")); Shutdown(); return;
            }
            var render = args.Contains("--render-demo");
            var demo = render || args.Contains("--demo");
            var catalog = new DeviceCatalog(JsonSerializer.Deserialize<List<DeviceDefinition>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "devices.json"))) ?? []);
            IAudioBackend audio = demo ? new DemoAudioBackend() : new CoreAudioBackend();
            var customData = args.Contains("--data-directory") ? Path.GetFullPath(ArgumentValue(args, "--data-directory")) : null;
            var storage = ApplicationStorage.Open(AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), demo, customData,
                render ? ArgumentValue(args, "--render-demo") : null);
            var window = new MainWindow(audio, catalog, storage.Profiles, storage.Processing, storage.Setups, demo);
            if (render) {
                window.VerifyDemoUi();
                if (args.Contains("--sound-page")) window.SelectDemoSoundPage();
                if (args.Contains("--setup-profiles")) window.ShowDemoSetupProfiles();
                if (args.Contains("--microphone-monitor")) window.ShowDemoMicrophoneMonitor();
                if (args.Contains("--playback-monitor")) window.ShowDemoPlaybackMonitor();
                var height = args.Contains("--compact-preview") ? 720 : 1450;
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1040, height)); content.Arrange(new Rect(0, 0, 1040, height)); content.UpdateLayout();
                var image = new RenderTargetBitmap(1040, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                var destination = Path.GetFullPath(ArgumentValue(args, "--render-demo")); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var stream = File.Create(destination)) encoder.Save(stream);
                SaveJson(destination + ".json", new { Passed = true, Scenarios = new[] { "microphone level and mute", "pending edits survive polling without writes", "switches preserve unrelated fractional gate settings", "noise gate and filter apply", "filter, gate, high-pass and EQ switches", "Warm/Clear and custom nine-band EQ", "B20 frequency labels and accessibility order", "disabled effects retain their stored settings", "profile save and restore including processing and sidetone", "playback capability routing", "profile direction isolation", "processing controls hidden on playback", "sidetone availability follows a live read", "sidetone level and mute", "pending sidetone edits survive polling without writes", "sidetone mute preserves asymmetric channel levels", "sidetone controls hidden on playback", "physical pickup-pattern status updates", "pickup-pattern status hidden on playback", "processing save survives store reload with automatic restore off", "pending processing edits are not persisted", "manual saved processing restore", "opt-in reconnect restore runs once", "save failure reports hardware success separately", "processing restore controls hidden on playback", "GSX sound mode availability and apply", "pending sound edits survive polling", "discard sound edits without device writes", "playback profiles include only applied sound settings", "stale sound drafts preserve external changes", "sound/microphone navigation stays on the physical device", "detailed controls start collapsed", "GSX sound controls hidden on B20", "playback EQ labels and accessibility order", "playback EQ drafts survive polling", "playback EQ curve applies and profiles restore it", "Flat reset is a draft until Apply", "sound mode preserves fractional EQ values", "one playback band edit preserves the other eight", "live mode defaults on without startup writes", "real timer coalesces volume slider changes", "live mute preserves precise volume", "live microphone processing and EQ", "live sidetone level and mute", "device selection cancels queued live edits", "live sound mode and playback EQ", "auditioning leaves named profiles unchanged", "save to selected flushes live edits and isolates device profiles", "profile load cancels queued live edits", "discard cancels queued live edits", "failed live updates preserve newer settings without retries", "manual/live toggles do not apply old drafts", "live Flat reset", "save as creates and selects a new device profile", "stale selected profile save is rejected", "manual Apply buttons follow live mode", "failed live update blocks profile saving", "unavailable controls preserve saved profile fields", "setup controls start collapsed without automatic inclusion", "save as setup creates and selects snapshots", "save to selected setup captures queued microphone edits", "save as setup captures queued sound edits", "setup snapshots stay independent during auditioning", "manual drafts are excluded from setup capture", "setup load restores selected pages and cancels queued edits", "setup operations preserve named device profiles", "setup application remembers B20 processing for restore", "selected setup membership follows the checked pages", "stale setup saves are rejected and reload refreshes snapshots", "failed live application blocks setup saving", "device-page inclusion checkbox synchronizes with setup choices", "empty selections preserve saved setups", "unsaved inclusion changes block old preset application", "saved setup loads are excluded from direct edit tracking", "select edited pages flushes and selects successful direct changes", "manual drafts and failed applies are excluded from edited-page selection", "disconnected checkbox exclusions survive refresh and reconnect", "whole-device setup inclusion selects and clears all listed pages", "page choices synchronize partial device selection", "physical grouping preserves B20 speaker exclusions and separates USB instances", "whole-device exclusions include disconnected saved pages", "group refresh and inclusion preserve saved setups and hardware settings", "microphone view starts off and expansion does not capture", "demo activity displays live level and nine frequency bands", "microphone EQ preview follows its settings", "gate hover guide is visual and follows the current slider", "graph band selection reveals EQ controls", "collapsing the view stops analysis and clears data", "device selection stops analysis without automatic restart", "stale live audio values are cleared", "capture errors stop without automatic retry", "analysis and graph interactions preserve settings profiles and edit history", "unavailable microphone processing hides and disables stale editors", "successful processing reads recover controls without modifying profiles", "compact primary controls fit below EQ at normal window size", "effect and mute icons show readable states and the correct slash", "accessible gate toggle preserves its level and never mutes the endpoint", "compact switches preserve manual drafts and named profiles", "narrow layouts wrap controls and device navigation resets scrolling", "GSX microphone live controls preserve B20 playback and high-pass", "GSX profiles include only validated microphone settings", "playback activity is opt-in and combined with EQ drafts", "playback activity stops on device changes without modifying settings or profiles", "reverb drafts discard and icon toggle retain precise amounts", "reverb live apply profile restore and stereo availability preserve other controls", "microphone capture distinguishes initial flags timestamp uncertainty and later interruptions", "playback capture distinguishes initial flags timestamp uncertainty and later interruptions", "delayed GSX USB writes keep the slider interactive and serialize newest live edits", "concurrent GSX save waits for the final queued hardware value", "manual edits during USB apply remain drafts without implicit profile saves", "device navigation cancels subsequent USB writes and waiting saves", "delayed stale GSX writes preserve external state without retries", "window close is blocked while a GSX USB transaction completes", "GSX sidetone routing units drafts and discard preserve exact hardware state", "GSX live sidetone and device profiles retain raw values without implicit saves", "GSX sidetone selection cancellation and external edit protection preserve other controls", "Connected Devices stays dark during a held USB transaction", "Connected Devices text renders identically before during and after USB guards", "accepted GSX slider position survives quantized apply and polling", "repeated small slider commands advance across hardware steps", "external sidetone changes and explicit discard synchronize hardware position" } });
                Shutdown(); return;
            }
            MainWindow = window; window.Show();
        } catch (Exception ex) {
            if (e.Args.Contains("--diagnostics") || e.Args.Contains("--render-demo") || e.Args.Contains("--verify-control")) {
                var errorFile = Path.Combine(AppContext.BaseDirectory, "startup-error.txt"); File.WriteAllText(errorFile, ex.ToString());
            } else MessageBox.Show(ex.Message, "Timbre", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private static string ArgumentValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name); if (index < 0 || index + 1 == args.Length) throw new ArgumentException("Missing value for " + name); return args[index + 1];
    }
    private static void SaveJson(string path, object value)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }
    private static void VerifyControl(string id, string report)
    {
        var backend = new CoreAudioBackend();
        var endpoint = backend.Discover().Single(e => e.Id == id && e.Direction == AudioDirection.Microphone);
        var before = backend.Read(id); AudioState? changed = null; AudioState? restored = null; Exception? failure = null;
        try {
            var target = before.Level >= .04f ? before.Level - .02f : before.Level + .02f;
            changed = backend.Apply(id, target, before.Muted);
            if (Math.Abs(changed.Level - before.Level) < .0001) throw new InvalidOperationException("The endpoint did not accept a level change.");
            if (changed.Muted != before.Muted) throw new InvalidOperationException("Mute state changed unexpectedly.");
        } catch (Exception ex) { failure = ex; }
        finally { restored = backend.Apply(id, before.Level, before.Muted); }
        var passed = failure is null && Math.Abs(restored.Level - before.Level) < .001 && restored.Muted == before.Muted;
        SaveJson(report, new { Passed = passed, endpoint.Name, Before = before, Changed = changed, Restored = restored, Error = failure?.Message });
        if (!passed) throw new InvalidOperationException("Control verification failed; see report.");
    }
}
