# GSX 300 microphone controls — 2026-10-06

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

The native app now supports GSX microphone gate percentage, noise-filter levels 0/1/2, nine-band EQ and separate gate/filter/EQ enable switches. They share the microphone studio, live-apply queue, activity/EQ plot and device/setup profile workflow used by the B20. The GSP 301 microphone uses this input when connected to the GSX. Hardware sidetone is available through its separately validated USB adapter and saves exact raw values in profiles. High-pass remains unavailable; it is not inherited from the B20 adapter. See [GSX sidetone protocol and validation](gsx-remaining-controls.md).

## Live mapping

In installed Gaming Suite 1.12.2.1185, recording was explicitly set to **EPOS GSX 300** before opening its microphone page. Playback remained GSX. Both devices' complete 4096-byte buffers were captured for each stage.

| Control | Observed GSX field | Evidence |
| --- | --- | --- |
| Noise gate | float32 at 152, fraction 0–1 | Earlier minimum/maximum/minimum captures; this session 51% = 0.51 |
| Noise filter | int32 at 160, 0/1/2 | Low → high → middle → low; only that field changed |
| Microphone EQ | nine float32 gains at 112–144, dB | Warm, Clear and Off/Flat captures matched all nine values |
| EQ/filter/gate enabled | bytes 66/67/68 respectively | Existing indexed mapping plus native apply/readback/restoration checks |

Warm: `[6, 6, 4.02, -0.52, -3.02, -2.31, 1.04, -3.02, 0]` dB. Clear: `[-6, -6, -3.02, 0, 0, 1.04, 2.03, 0, 0]` dB. Frequencies are 64, 125, 250, 500 Hz, 1, 2, 4, 8 and 16 kHz, consistent with the recovered ordered writer and Suite EQ labels.

Opening the Suite microphone page independently set bytes 66, 68 and 70 to one. These initialization changes were captured before control edits. Leaving the page reversed them. The Suite initially showed a cached filter position that differed from applied state; readback, rather than UI position alone, is authoritative.

The complete B20 buffer stayed identical at every stage. After restoring GSX gate 0, filter 0 and Flat EQ, leaving the microphone page and returning Suite's recording selection to B20, both complete buffers matched the session's starting captures. Playback remained GSX, with the original Stereo/Flat settings. Raw session captures are `artifacts/gsx-mic-controls-*.bin`; their comparison report is `artifacts/gsx-mic-controls-session.json`.

## Adapter boundaries

Microphone and playback sessions select distinct write whitelists. GSX uses the existing `_1395_0098` objects; B20 uses `_1395_009f`. Endpoint identity, direction, current connection, unique physical unit of the model, header and size are validated before a transaction. Changed fields are compared, written under the vendor mutex, signaled, read back and conditionally rolled back on failure. GSX high-pass writes at 71 are rejected both during profile preflight and by the native patch whitelist; the UI hides that control. Other unsupported fields are preserved.

The actual GSX Suite microphone page did not expose a high-pass switch. Read-only Windows topology showed input and output volume/mute nodes but no B20-style sidetone monitoring mixer. Subsequent research validated a separate GSX HID getter/setter and integrated its monitoring slider. Reverb is now supported in the playback whitelist; microphone patches continue to preserve its enable/amount fields.

The app opens existing GSX processing objects; it does not initialize them or restore GSX processing automatically at startup/reconnect. Keep the compatible installed EPOS processor and Gaming Suite service for normal use. The separate [GSX fresh-initialization experiment](gsx-initialization.md) passed actual fresh creation and microphone/playback control transactions with the vendor service stopped on 2026-10-08, using a controlled Windows Audio restart. Both full device buffers and all diagnostic controls were restored exactly. Audible DSP after fresh creation, cold boot and managed GSX recovery still require validation. Managed reconnect and the automatic processing-state store remain B20-only. GSX named device and setup profiles are saved by our app and can be applied explicitly when the interface is available.

## Validation

That GSX control milestone passed **483 offline regression checks and 121 WPF scenarios**, including complete-byte GSX golden transitions, unsupported-control rejection before writes, microphone/playback/B20 isolation, live edits, profile snapshots and combined graph drafts. Fixtures are pinned by SHA-256; historical captures were retained.

Native GSX effects test passed with the service running: gate/filter/EQ and enable switches read back correctly; unowned configuration, B20 configuration and all Windows levels/mutes stayed unchanged; starting GSX configuration was restored. Report: `artifacts/gsx-mic-controls-hardware.json`. The real app showed the mapped controls with high-pass/sidetone absent, and GSX microphone activity updated around −82 dBFS. Switching to Sound stopped that capture.

These initial checks validated controls and capture. Subsequent post-reboot measurement verified gate audio processing, and the user confirmed gate, filter and EQ work on 2026-10-07, as recorded below. Physical reconnect and managed startup remain in the [home validation checklist](../validation/home-validation.md). See also [routing history](../validation/gsx-microphone-routing.md) and [playback activity](../user-guide/live-playback-view.md).

## Audio-path investigation, 2026-10-07

The user could not distinguish GSX gate processing during listening. A shared-mode off/maximum/off measurement read back the expected gate settings, but measured essentially unchanged RMS: -9.159, -9.163 and -9.211 dBFS. There were no interrupted, clipped or invalid samples. Both complete 4096-byte GSX and B20 buffers matched their baselines after restoration. The test's 12 dB attenuation criterion failed. Input at this level may exceed the gate threshold, so this measurement alone does not prove the processor is absent.

Read-only endpoint/driver inspection found a separate installation problem: the GSX microphone has an empty `FxProperties` key, and GSX playback registers Microsoft Audio Home Theater Effects rather than the EPOS APO. GSX `MI_00` is bound to `wdma_usb.inf` / `usbaudio` version 10.0.26100.9457. The B20 uses `oem84.inf` / `EPOSAudio` 1.3.1.0 and registers EPOS EFX CLSID `{27201018-29C6-446A-9308-AA97260FEED1}`. Microsoft documents that APOs must be associated with the audio processing graph; shared settings memory is not that association. See [APO implementation](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects).

The already-installed, Microsoft-signed `oem84.inf` explicitly supports `USB\VID_1395&PID_0098&MI_00` and registers this EPOS EFX. Initial inspection listed it as Best Ranked, while the generic driver was Outranked / Installed. Hardware sidetone is a separate USB path and already has user listening confirmation.

With explicit user authorization, the user selected the existing EPOS driver in Device Manager. SetupAPI recorded a successful install of version 1.3.1.0 on 2026-10-07, but device removal was vetoed by `PNP_VetoOutstandingOpen`; Windows explicitly requires a reboot. The GSX endpoints still have their old effects registration, so selection of the EPOS driver does not yet establish active EPOS processing. After the user reboots, rediscover endpoint identities, verify EPOS EFX registration and repeat audio validation with quieter steady input. Evidence: `artifacts/gsx-driver-install-20261007.log` and `artifacts/gsx-driver-install-status-20261007.json`.

The post-install settings snapshot is `artifacts/gsx-driver-controls-after-20261007.json`. Windows levels/mutes, B20 controls and GSX playback matched the earlier baseline. GSX microphone gate/filter values were 58%/level 0 rather than the earlier 25%/level 2; their origin is not established by these snapshots. These current values were retained, without overwriting them from an older baseline. Compare the latest snapshot after restart before considering restoration. Registration evidence remains `artifacts/gsx-driver-effects-after-20261007.json`.

After the user rebooted, read-only checks confirmed `oem84.inf` / `EPOSAudio` 1.3.1.0 with the restart-needed flag cleared. Both new GSX endpoints register EPOS EFX CLSID `{27201018-29C6-446A-9308-AA97260FEED1}`; the playback mix format is now eight channels at 48 kHz rather than two. Playback is named Speakers (EPOS GSX 300), and both GSX endpoint GUIDs changed while the physical USB identity stayed the same. This confirms the driver/registration repair; acoustic gate/filter/EQ and surround behavior still need validation.

The first post-reboot snapshot showed GSX shared processing at gate 0/off, Flat EQ, filter level 0/on, surround off and reverb 0/off. Windows GSX volume readback differs only by small driver quantization (less than 0.002 dB); mutes and B20 controls were unchanged at that snapshot. A separate USB getter returned sidetone raw byte 230 versus 225 in the earlier pre-install snapshot, with no attribution of that change to driver installation. No old settings were reapplied by our diagnostics. Evidence: `artifacts/gsx-driver-post-reboot-20261007.json`, `artifacts/gsx-effects-post-reboot-20261007.json`, `artifacts/gsx-post-reboot-settings-differences-20261007.json`, `artifacts/gsx-sidetone-post-reboot-20261007.json` and the complete post-reboot GSX/B20 buffer captures.

Subsequent snapshots showed further changes before validation: GSX gate amount became 24.705883% while remaining disabled, and B20 playback/reverb fields changed. The source of these inter-snapshot changes is not established. The microphone and playback tests each passed restoration and isolation against their own immediate starting state; a comparison against the earlier snapshot is not evidence of test restoration failure. The audio test therefore captured fresh complete buffers immediately before its measurement rather than restoring an older startup state.

### Post-reboot gate audio validation

With user-provided quiet steady background input, the GSX off/maximum/off measurement **passed**: RMS was -50.096, -99.532 and -47.267 dBFS. The evaluator reports 49.436 dB attenuation, exceeding the unchanged 12 dB criterion, with zero invalid/clipped samples, discontinuities or timestamp errors. Settings restoration passed and both complete 4096-byte GSX and B20 buffers matched their immediate pre-audio captures exactly. This validates actual gate processing through the shared-mode GSX capture path in this configuration; it does not independently validate filter, EQ, surround or reverb. The earlier generic-driver test also used much louder input, so the two measurements are not a controlled comparison of driver causality.

Reports: `artifacts/gsx-gate-audio-post-reboot-20261007.json`, `artifacts/gsx-effects-post-reboot-validation-20261007.json`, `artifacts/gsx-playback-post-reboot-validation-20261007.json`; complete isolation evidence: `artifacts/gsx-audio-post-reboot-{before,after}-20261007.bin` and `artifacts/b20-during-gsx-audio-post-reboot-{before,after}-20261007.bin`.

### User listening confirmation

On 2026-10-07, after the EPOS driver switch and reboot, the user reported that noise gate, noise filter, EQ, virtual 7.1 and reverb work. This is manual functional confirmation on this machine, separate from the measured gate result above. The report does not specify the listening application, EQ input/output direction or every preset/filter level; no calibrated response is claimed. The confirmation is recorded in `artifacts/gsx-listening-confirmation-20261007.json`. Current settings were captured read-only in `artifacts/gsx-listening-confirmed-settings-20261007.json` and complete GSX/B20 buffers; this documentation step did not change settings or apply older snapshots.

The explicit source diagnostic now accepts either mapped microphone model: `tools/Test-App.ps1 -HardwareAudio 0098`. It captures metrics only, validates identity and concurrent edits, restores starting processing, and treats silence/unstable audio as inconclusive. A failed attenuation criterion is a measurement failure, not proof of driver causality. Offline GSX cases cover off/maximum/off sequencing and restoration after unchanged audio. Evidence: `artifacts/gsx-gate-audio-20261007.json`, `artifacts/gsx-audio-effect-registration-20261007.json`, `artifacts/gsx-driver-binding-before-20261007.txt`, and matching before/after buffer captures.

Later that day, with the vendor service stopped, the existing GSX processing interface remained readable in the warm Windows session. Two gate audio measurements were **inconclusive** because ungated input was below the signal-quality floor: approximately -94 dBFS with the original level-2 filter, and -84 dBFS with the filter temporarily bypassed. The evaluator's floor and 12 dB attenuation criterion were unchanged. The original filter/gate settings, both complete device buffers and diagnostic controls were restored exactly. Evidence: `artifacts/gsx-service-stopped-warm-audio-20261007.json`, `artifacts/gsx-service-stopped-isolated-audio-20261007.json` and `artifacts/apo-host/manual-reconnect-20261007-194220/live-validation-summary.json`. These observations do not establish fresh GSX initialization or independent startup.

```powershell
.\tools\Test-App.ps1 -HardwareEffects 0098
```

This explicit check briefly changes effects and restores their starting state. Keep Suite and other controllers idle during it.
