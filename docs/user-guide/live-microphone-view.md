# Live microphone view

The microphone page opens an expanded **Microphone studio**, combining the activity/EQ chart with gate, filter, high-pass, EQ and sidetone controls. Check **Live activity** to analyze the selected Windows microphone. Opening the app or expanding the panel alone does not start capture. Unchecking it, collapsing the panel, switching devices or closing the app stops analysis. A capture error stops the view and requires an explicit restart.

The app displays RMS and sample peak in dBFS, a level meter, and activity in nine frequency regions centered on **64, 125, 250, 500 Hz, 1, 2, 4, 8 and 16 kHz**. A single plot overlays blue activity bars with the teal nine-band EQ settings curve. Activity uses the left dBFS axis (−90 to 0); EQ gain uses the right dB axis (−6 to +6). Both share the same frequency positions. Disabled EQ is grey; unapplied settings are marked as a draft. Clicking a frequency band opens the existing EQ controls without enabling EQ or changing a setting. The existing live-apply policy governs subsequent adjustments.

Hovering or focusing the noise-gate slider or on/off icon shows an amber dashed guide with the gate percentage, positioned using the activity axis. This is a visual guide recovered from Suite's display geometry, **not a calibrated DSP threshold or a gate-open indicator**. The analysis does not change settings, save recordings, send audio to an output, or update device/setup profiles. Monitor visibility and activation are not saved in those profiles.

## What the measurements mean

Capture quality distinguishes an initial data-discontinuity flag, later stream interruptions, and timestamp uncertainty. An initial flag remains visible as **initial capture discontinuity observed**; it is not silently discarded or assumed harmless. Later discontinuities reset the spectrum window and display **stream interruptions observed**. Timestamp uncertainty is counted separately and preserves valid sample data. Microsoft defines [the WASAPI buffer flags](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags) separately; uncertain timestamps alone do not establish missing samples. Strict gate/audio-effect validation retains its existing signal-quality requirements.

The metrics-only diagnostic `tools/Test-App.ps1 -SkipBuild -MonitorPackets 0098` (use `009f` for B20) captures three seconds and saves packet counts, flag events, spacing and analysis summaries. It saves no audio, plays no sound and changes no settings. Portable verification exposes the same optional `-MonitorPackets` parameter. Normal verification does not capture. The local 2026-10-07 red/green reports show one first-packet flag and no subsequent flags on both devices; this limited quiet-input observation does not establish long-term stream stability or audible DSP behavior. Reports: `artifacts/monitor-packets-gsx-red-20261007.json`, `artifacts/monitor-packets-gsx-green-20261007.json`, and `artifacts/monitor-packets-b20-green-20261007.json`.

The source uses shared-mode Windows microphone capture. Its samples can already include Windows/EPOS processing; this is not a raw hardware input or a before/after comparison. Microsoft documents processing-mode differences in [Audio Signal Processing Modes](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-signal-processing-modes). EQ points represent stored or draft gains, not a measured filter response. The visualization provides feedback while adjusting the existing controls, without requiring Suite's FFT telemetry.

`WasapiMeasurementSource` exposes bounded packet reads through `IMicrophonePacketSource`. `MicrophoneMonitor` opens, reads and disposes capture on its own thread, and publishes immutable summaries about every 100 ms. The UI drops stale values after one second. Shutdown waits at most 250 ms for the worker; a slow native call can finish cleanup in the background. Packet buffers are released in `finally`, including silent and zero-frame packets, following [IAudioCaptureClient.GetBuffer](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer).

`MicrophoneSpectrum` accepts float32 or PCM16/24/32 samples. It uses a rolling power-of-two window of approximately 85 ms at common sample rates, a periodic Hann window, and a radix-2 FFT. Frequency regions have geometric boundaries between adjacent centers. Channel powers are combined separately so opposite-phase stereo does not cancel. The band values are integrated RMS energy in each region, not individual FFT-bin peaks. Regions whose center reaches or exceeds Nyquist are shown as unavailable. The numerical floor is −120 dBFS; the level strip displays −90 to 0 dBFS. Clipping is flagged when the window's sample peak reaches 0.999. Invalid samples are replaced with zero and flagged; stream interruptions reset the analysis window. Pending audio buffers are cleared on shutdown.

## Findings in the installed Suite

On 2026-10-06, read-only inspection of the installed managed assemblies found:

- `MicrophoneViewModel.MicMonitorGraph` and `NoiseGateGraphViewModel.CheckNoiseGateGraph` request FFT data using `DeviceDataModel.pipeServer_SendFFTRequest`, request type 14. The request carries the microphone's vendor/product/serial identity.
- `DeviceDataModel.SetFFTLevels` consumes nine microphone FFT values. `MicrophoneInterface.GetPlotPoints` maps values with `(value + 60) × 1.66666666666667`, giving a 0–100 display range for −60–0.
- `UCNoiseGate.DrawNoiseGateBar` places its guide at `NoiseGateKnobValueUVGraph / 255 × graph height`. Together these imply a display-scale position of `−60 + 0.6 × gate percentage`. This supports the guide's geometry; it does not establish the actual processor's gating threshold or Suite's FFT calibration.
- Suite also scales some plotted activity by its gain control. Our chart instead labels captured signal values directly in dBFS. It does not reproduce Suite's gate-state coloring.

The service executable is native, so the managed metadata inspector did not expose its FFT implementation. We have not reproduced its pipe telemetry or verified equivalent numerical output.

Local inspection artifacts are `artifacts/microphone-monitor-ui-il.json`, `microphone-monitor-service-il.json`, `microphone-monitor-details-il.json`, `microphone-fft-service-il.json` and `microphone-gate-layout-il.json`. No vendor assemblies are bundled with our app. Inspected SHA-256 values:

| Installed file | SHA-256 |
| --- | --- |
| `EPOSGamingSuite.exe` | `FA0E1F053EFD62936415221F9CBC7E554AAD0B7E4B9156EC38C9BCE3DB88D70C` |
| `GamingSuite.UI.Services.dll` | `2DACEC2266097F08F15C82B0D4324626FD33D667422FB2FBC743E3AB8BFEF963` |

## Validation

The original live-view milestone passed **430/430 offline regression checks and 103 WPF scenarios**, with no compiler warnings or errors. Coverage exercises calibrated known tones, silence, all supported sample types, opposite-phase stereo, Nyquist limits, packet boundaries, interruption recovery, invalid samples, clipping recovery, worker ownership, error handling and bounded shutdown. WPF checks cover opt-in start, collapse/device-switch stop, stale/error states, EQ navigation, gate-guide visibility and preservation of settings/profile data. The validated GSX microphone adapter now supplies gate/filter/EQ to this same view; high-pass remains unavailable on GSX. A separate USB sidetone slider is now supported; see [current validation status](../validation/status.md). See [GSX control evidence](../research/gsx-microphone-controls.md) and the separate [playback activity/EQ view](live-playback-view.md).

`artifacts/merged-microphone-preview.png` is rendered from the actual combined WPF view with explicitly labeled synthetic demo activity. The [2026-10-06 live UI audit](../validation/live-ui-audit-20261006.md) exercised actual capture on both B20 and GSX microphones: numerical levels updated, collapse/device changes stopped analysis, and returning to B20 did not restart capture. Both independent two-second measurements contained 96,480 frames with zero invalid/clipped samples or capture timing errors. Ambient inputs were quiet, so these results validate capture and lifecycle rather than physical frequency response or an audible DSP comparison. Audit evidence and the latest regression log are under `artifacts/live-ui-audit-20261006`. Earlier listening results remain separate evidence.
