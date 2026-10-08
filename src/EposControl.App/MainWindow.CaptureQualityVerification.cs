using EposControl.Core;

namespace EposControl.App;

public partial class MainWindow
{
    private void VerifyCaptureQualityUi()
    {
        var mic = endpoints.First(IsB20);
        var sound = endpoints.Single(e => e.Direction == AudioDirection.Playback && e.Usb?.ProductId == "0098");
        var frame = new MicrophoneMonitorFrame(1, DateTimeOffset.UtcNow, -40, -30,
            Array.AsReadOnly<double?>(Enumerable.Repeat<double?>(-50, 9).ToArray()), false, 0, 0, 1, 0);
        foreach (var endpoint in new[] { mic, sound }) {
            DeviceList.SelectedItem = endpoint;
            void Set(MicrophoneMonitorFrame value) {
                if (endpoint.Direction == AudioDirection.Microphone) { microphoneMonitor = new FrozenMonitor(value, null); PollMicrophoneMonitor(); }
                else { playbackMonitor = new FrozenMonitor(value, null); PollPlaybackMonitor(); }
            }
            string StatusText() => endpoint.Direction == AudioDirection.Microphone ? MonitorStatus.Text : PlaybackMonitorStatus.Text;
            Set(frame);
            if (!StatusText().Contains("initial capture discontinuity") || StatusText().Contains("stream interruptions"))
                throw new InvalidOperationException("An initial capture flag was presented as a later stream interruption.");
            Set(frame with { InitialDiscontinuities = 0, TimestampErrors = 1 });
            if (!StatusText().Contains("timestamp uncertainty") || StatusText().Contains("stream interruptions"))
                throw new InvalidOperationException("Timestamp uncertainty was presented as an audio interruption.");
            Set(frame with { InitialDiscontinuities = 0, Discontinuities = 1 });
            if (!StatusText().Contains("stream interruptions")) throw new InvalidOperationException("A genuine later interruption was hidden.");
            StopMicrophoneMonitor("Stopped."); StopPlaybackMonitor("Stopped.");
        }
        DeviceList.SelectedItem = mic;
    }
}
