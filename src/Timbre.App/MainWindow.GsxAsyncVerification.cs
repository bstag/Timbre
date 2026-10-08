using System.IO;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private void VerifyGsxAsyncUi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "epos-async-ui-" + Guid.NewGuid().ToString("N"));
        var runner = new HeldUsbWorkRunner();
        var window = new MainWindow(new DemoAudioBackend(), catalog, new ProfileStore(Path.Combine(directory, "profiles.json")),
            new ProcessingStateStore(Path.Combine(directory, "processing")), new SetupProfileStore(Path.Combine(directory, "setups.json")), true, runner);
        Task<bool>? apply = null;
        try {
            var mic = window.endpoints.Single(GsxSidetoneProtocol.Supports); window.DeviceList.SelectedItem = mic;
            var before = window.gsxSidetone.Read(mic);
            window.VerifyGsxReadbackInteraction(runner);
            var callsBefore = runner.Calls;
            runner.HoldNext(); window.GsxSidetoneSlider.Value = 25; apply = window.FlushGsxSidetoneAsync();
            if (!window.usbWorkBusy || !window.DetailPanel.IsEnabled || !window.GsxSidetoneSlider.IsEnabled || window.DeviceList.IsEnabled || window.ProfilesExpander.IsEnabled)
                throw new InvalidOperationException("A USB apply must leave its slider interactive while guarding conflicting operations.");
            window.GsxSidetoneSlider.Value = 75;
            var saveWait = window.FlushGsxSidetoneAsync();
            window.ProfileName.Text = "Latest async value"; window.SaveProfileClick(window, new System.Windows.RoutedEventArgs());
            if (window.profiles.ForDevice(mic).Count != 0) throw new InvalidOperationException("A profile was saved before pending USB work completed.");
            runner.Release(); PumpUsbCompletion(Task.WhenAll(apply, saveWait));
            if (!apply.Result || !saveWait.Result || window.gsxSidetonePending || window.gsxSidetone.Read(mic).Settings != GsxSidetoneSettings.FromPercent(75) ||
                window.GsxSidetoneSlider.Value != 75 || runner.MaxConcurrent != 1 || runner.Calls - callsBefore != 3)
                throw new InvalidOperationException("Continued GSX dragging or a save waiter lost the newest edit or overlapped USB writes.");
            if (window.profiles.ForDevice(mic).Single().GsxSidetone != GsxSidetoneSettings.FromPercent(75))
                throw new InvalidOperationException("The saved profile did not retain the final async hardware value.");
            window.LiveApplyCheck.IsChecked = false; runner.HoldNext(); window.GsxSidetoneSlider.Value = 50;
            apply = window.FlushGsxSidetoneAsync(); window.GsxSidetoneSlider.Value = 20;
            runner.Release(); PumpUsbCompletion(apply);
            if (!apply.Result || !window.gsxSidetonePending || window.GsxSidetoneSlider.Value != 20 || window.gsxSidetone.Read(mic).Settings != GsxSidetoneSettings.FromPercent(50))
                throw new InvalidOperationException("Finishing a manual USB apply overwrote or applied a newer manual draft.");
            window.DiscardChangesClick(window, new System.Windows.RoutedEventArgs());
            window.LiveApplyCheck.IsChecked = true; runner.HoldNext(); window.GsxSidetoneSlider.Value = 30;
            apply = window.FlushGsxSidetoneAsync(); window.GsxSidetoneSlider.Value = 90;
            runner.Release(); PumpUsbCondition(() => !window.usbWorkBusy);
            var calls = runner.Calls;
            window.DeviceList.SelectedItem = window.endpoints.First(IsB20); PumpUsbCompletion(apply);
            if (apply.Result || runner.Calls != calls || window.gsxSidetone.Read(mic).Settings != GsxSidetoneSettings.FromPercent(30))
                throw new InvalidOperationException($"Device selection did not cancel the subsequent USB write: result={apply.Result}, calls={runner.Calls}/{calls}, raw={window.gsxSidetone.Read(mic).Settings.RawValue}, epoch={window.gsxEditEpoch}.");
            window.DeviceList.SelectedItem = mic; runner.HoldNext(); window.GsxSidetoneSlider.Value = 40;
            apply = window.FlushGsxSidetoneAsync(); window.GsxSidetoneSlider.Value = 60;
            window.gsxSidetone.Apply(mic, window.gsxSidetone.Read(mic), new(250)); calls = runner.Calls;
            runner.Release(); PumpUsbCompletion(apply);
            if (apply.Result || window.gsxSidetonePending || window.gsxLiveQueue.HasPending || runner.Calls != calls || window.gsxSidetone.Read(mic).Settings.RawValue != 250)
                throw new InvalidOperationException("A failed delayed apply retried or overwrote externally changed GSX state.");
            if (window.profiles.ForDevice(mic).Single().GsxSidetone != GsxSidetoneSettings.FromPercent(75))
                throw new InvalidOperationException("Asynchronous audition or failure modified a named profile.");
            window.gsxSidetone.Apply(mic, window.gsxSidetone.Read(mic), before.Settings);
        } finally {
            runner.Release(); if (apply is { IsCompleted: false }) PumpUsbCompletion(apply);
            window.Close(); runner.Dispose();
        }
    }
    private void VerifyGsxReadbackInteraction(HeldUsbWorkRunner runner)
    {
        var endpoint = Selected!; var before = gsxSidetone.Read(endpoint);
        var failures = new List<string>();
        var content = (FrameworkElement)Content;
        content.Measure(new Size(1040, 1450)); content.Arrange(new Rect(0, 0, 1040, 1450)); content.UpdateLayout();
        byte[] CaptureDeviceList() {
            DeviceList.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(DeviceList.ActualWidth), (int)Math.Ceiling(DeviceList.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(DeviceList);
            var result = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(result, bitmap.PixelWidth * 4, 0);
            return result;
        }
        var enabledPixels = CaptureDeviceList();
        runner.HoldNext(); GsxSidetoneSlider.Value = 43;
        var apply = FlushGsxSidetoneAsync();
        DeviceList.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(DeviceList.ActualWidth), (int)Math.Ceiling(DeviceList.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(DeviceList);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var sample = ((image.PixelHeight - 12) * image.PixelWidth + image.PixelWidth / 2) * 4;
        if (pixels[sample] > 200 && pixels[sample + 1] > 200 && pixels[sample + 2] > 200 && pixels[sample + 3] > 200)
            failures.Add("Connected Devices renders a white background during a held USB update.");
        if (!enabledPixels.SequenceEqual(pixels))
            failures.Add("Connected Devices changes its rendered text/colors during a held USB update.");
        runner.Release(); PumpUsbCompletion(apply);
        if (!enabledPixels.SequenceEqual(CaptureDeviceList()))
            failures.Add("Connected Devices changes its rendered text/colors after the USB guard is released.");
        if (!apply.Result || GsxSidetoneSlider.Value != 43)
            failures.Add($"Sidetone requested 43%, but apply moved the slider to {GsxSidetoneSlider.Value:0.###}%.");
        PollCurrent();
        if (GsxSidetoneSlider.Value != 43) failures.Add("Polling moved the accepted sidetone position backward.");
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("\n", failures));
        if (gsxSidetone.Read(endpoint).Settings != GsxSidetoneSettings.FromPercent(43) || gsxSidetonePending ||
            !System.Windows.Automation.AutomationProperties.GetHelpText(GsxSidetoneSlider).Contains("hardware step"))
            throw new InvalidOperationException("Accepted sidetone must verify and expose the actual hardware step without retaining a draft.");
        for (var step = 0; step < 10; step++) {
            var requested = GsxSidetoneSlider.Value + GsxSidetoneSlider.SmallChange;
            System.Windows.Controls.Slider.IncreaseSmall.Execute(null, GsxSidetoneSlider);
            PumpUsbCompletion(FlushGsxSidetoneAsync()); PollCurrent();
            if (Math.Abs(GsxSidetoneSlider.Value - requested) > 0.000001 || gsxSidetone.Read(endpoint).Settings != GsxSidetoneSettings.FromPercent(requested))
                throw new InvalidOperationException("Repeated small slider commands stick at a quantized hardware position.");
        }
        gsxSidetone.Apply(endpoint, gsxSidetone.Read(endpoint), new(250)); PollCurrent();
        if (GsxSidetoneSlider.Value != new GsxSidetoneSettings(250).Percent)
            throw new InvalidOperationException("Preserving accepted slider positions hides a genuine external hardware change.");
        GsxSidetoneSlider.Value = 43; PumpUsbCompletion(FlushGsxSidetoneAsync());
        DiscardChangesClick(this, new RoutedEventArgs());
        if (GsxSidetoneSlider.Value != gsxSidetone.Read(endpoint).Settings.Percent)
            throw new InvalidOperationException("Explicit reload/discard must show the exact hardware position.");
        // Return this isolated demo to its baseline before the remaining asynchronous scenarios.
        gsxSidetone.Apply(endpoint, gsxSidetone.Read(endpoint), before.Settings); LoadGsxSidetone(true);
    }
    private static void PumpUsbCompletion(Task task)
    {
        PumpUsbCondition(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static void PumpUsbCondition(Func<bool> completed)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (completed() || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame);
        if (!completed()) throw new TimeoutException("USB UI verification did not finish within five seconds.");
    }
    private sealed class HeldUsbWorkRunner : IUsbWorkRunner, IDisposable
    {
        private readonly ManualResetEventSlim release = new(true);
        private bool hold;
        private int concurrent;
        public int MaxConcurrent { get; private set; }
        public int Calls { get; private set; }
        public void HoldNext() { release.Reset(); hold = true; }
        public void Release() => release.Set();
        public Task<T> Run<T>(Func<T> work)
        {
            Calls++; MaxConcurrent = Math.Max(MaxConcurrent, ++concurrent);
            if (!hold) { try { return Task.FromResult(work()); } finally { concurrent--; } }
            hold = false;
            return Task.Run(() => { try { if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Held USB test work timed out."); return work(); } finally { Interlocked.Decrement(ref concurrent); } });
        }
        public void Dispose() => release.Dispose();
    }
}
