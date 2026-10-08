# Live playback activity and EQ

The GSX **Sound studio** overlays nine frequency-activity bars and the configured playback EQ curve in one plot. Activity uses the left dBFS axis; EQ gains use the right dB axis. Check **Live activity** to analyze the selected output. Click a band to expand and focus its existing EQ slider. Live/manual apply and draft indicators work as on the microphone page. Opening Sound does not capture; unchecking, changing pages/devices or closing the app stops capture. Returning to a page does not restart it. A capture error stops the view and requires an explicit restart.

The source reads the selected Windows output mix through shared-mode WASAPI loopback, without changing Windows defaults, routing audio or saving a recording. Microsoft documents selecting a render endpoint and using `AUDCLNT_STREAMFLAGS_LOOPBACK` in [Loopback Recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording). The source reuses the bounded packet reader and worker/FFT analysis described in [live microphone view](live-microphone-view.md).

The chart is useful for seeing which frequencies are active while editing EQ. Its curve represents configured or draft gains; it is not a measured transfer function. The capture's position relative to the vendor's EQ/surround processor has not been verified. Activity changing does not prove the physical headphones received processed audio. Playback EQ enablement byte 65 remains outside the write whitelist. Reverb enable/amount were subsequently validated and implemented; see [GSX playback controls](../research/gsx-playback-controls.md). Capture quality uses the same separate initial-discontinuity, later-interruption and timestamp-uncertainty labels as the microphone view.

## Tests and local evidence — 2026-10-06

Four additional WPF scenarios cover explicit opt-in, nine-band activity, curve/draft agreement, discard, selection stop and unchanged profiles/settings. The complete suite passes 430 regressions and 103 UI scenarios. `artifacts/gsx-playback-activity-preview.png` renders the actual compact WPF Sound studio with synthetic demo activity clearly labeled.

The native GSX loopback diagnostic generated a quiet 1 kHz tone on the selected GSX endpoint for two seconds: peak amplitude 0.005, about −46 dBFS. It captured 95,520 frames at 48 kHz; the dominant region was 1 kHz, RMS −49.04 dBFS, peak −46.02 dBFS, zero invalid samples and no clipping. One stream discontinuity was reported, so this is a signal-detection check, not a glitch-free timing certification. All Windows levels/mutes and both complete EPOS processing buffers were unchanged. Report: `artifacts/gsx-loopback-hardware.json`; before/after captures: `artifacts/gsx-loopback-{before,after}-{0098,009f}.bin`.

This diagnostic runs only with explicit opt-in, requires stereo float32 GSX output and saves numerical metrics only. Other active audio or mute can prevent detection. Tone rendering follows Microsoft's [shared-mode rendering sequence](https://learn.microsoft.com/en-us/windows/win32/coreaudio/rendering-a-stream). Normal regression/package verification never plays it.

```powershell
.\tools\Test-App.ps1 -HardwareLoopback
```

The app's real output view also displayed RMS −49 dBFS and peak −46 dBFS during a separate quiet-tone probe. Switching to Microphone stopped output analysis, and returning to Sound left it off. Probe: `artifacts/gsx-loopback-ui-probe.json`. Listening, channel placement and surround remain on the [home checklist](../validation/home-validation.md).
