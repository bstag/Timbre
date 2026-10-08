# Microphone effects and regression tests

## Current controls

B20 processing includes a 0–100% noise gate, filter presets 0/1/2, separate gate/filter enable switches, high-pass filtering, and nine-band microphone EQ (-6 to +6 dB) with Flat, Warm, Clear and Custom curves. Level 0 is a filter preset; use its checkbox to disable filtering. Disabling gate/filter/EQ preserves their levels and curve. Changes apply only when requested, and stale state is rejected. Profiles include the complete processing state. Older level/mute profiles still work; older gate/filter profiles preserve current switches and EQ.

Processing uses the installed EPOS audio processor. This does not yet replace its DSP or audio driver. The vendor processor must be active in the Windows audio path for audible processing. Changes target its current shared state; they are not written into Gaming Suite's saved preset files. Gaming Suite can display cached values or reapply its presets. Use this app's profiles to restore settings explicitly after a restart.

GSX 300 microphone gate/filter/EQ and their enable switches are available after a successful native read. Explicit Suite recording selection plus individual captures established model isolation, intermediate gate scaling, all filter presets and the nine-band Warm/Clear curves; native application and restoration passed. GSX high-pass is hidden and rejected before writes, preserving its observed state. GSX sidetone remains unavailable. See [GSX microphone controls and tests](../research/gsx-microphone-controls.md). B20 sidetone uses the validated Windows hardware adapter described in [B20 sidetone](../research/b20-sidetone.md). Playback EQ/surround and output activity have separate GSX controls.

## Evidence captured on this PC

Read-only snapshots were taken while the user moved one control at a time in Gaming Suite 1.12.2.1185. No vendor assemblies were executed by the inspection tool.

Named objects use the prefix `Global\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_009f`, followed by `_memory`, `_mutex`, or `_event`. GSX 300 uses `0098`. They identify a model without a USB serial, so the adapter refuses to address multiple connected units of the same model. USB endpoint identity and microphone direction are checked before opening the objects.

| Field | Offset | Encoding | Verified values |
| --- | ---: | --- | --- |
| Layout header | 0, 4 | Two little-endian int32 values | 2, 4 |
| Microphone EQ enabled | 66 | Boolean byte, 0/1 | Native settings code and live toggle/readback |
| Noise filter enabled | 67 | Boolean byte, 0/1 | Native settings code and live toggle/readback |
| Noise gate enabled | 68 | Boolean byte, 0/1 | Native settings code and live toggle/readback |
| High-pass enabled | 71 | Boolean byte, 0/1 | Suite off/on captures and live toggle/readback |
| Microphone EQ | 112–147 | Nine little-endian float32 gains in dB | Band 1 +6, complete Warm and Clear captures |
| Noise gate | 152 | Little-endian float32, normalized | 0, 0.49, 1 |
| Noise filter preset | 160 | Little-endian int32 | 2 → 1 |

The entire mapping is 4096 bytes. The header is a compatibility signature, not a claimed product version. Unknown sizes, headers, nonfinite floats, and out-of-range values fail closed.

Managed Suite IL confirms `noiseGateLevel` uses 0–100, converted from its UI's 0–255 knob. Shared memory uses 0–1. The approximate 50% position produced 0.49 in a rounded path and 0.49411765 when Suite reapplied the knob directly. Our adapter exposes the processor percentage and preserves exact bytes for unchanged settings. IL confirms `micNrPreset` is 0–2 and separate from `micNrEnabled`.

Read-only disassembly of `C:\Windows\System32\EAPO.dll` ties the registry keys `mic_eq_enabled`, `mic_nr_enabled`, `noise_gate_enabled` and `mic_hpf_enabled` to offsets 0x42, 0x43, 0x44 and 0x47. Its `mic_eq_levels` buffer is 0x24 bytes at offset 0x70. High-pass and EQ positions also match independent Suite captures. SHA256 of the inspected binary: `F83F40E12FA2683A8C625863D3A459D92BDD30549E7DA74AC43EED84751388CA`. Local disassembly stays in ignored artifacts; no vendor executable is included in source.

Suite's high-pass-off action also reapplied cached gate/filter values. The reverse toggle changed only byte 71, and its golden test compares the entire transition. Our adapter changes only fields whose requested values differ. The native settings code identifies the other enable bits; their off/on state access was verified by our controlled live test, rather than a Suite checkbox capture.

Warm and Clear constants came from `GamingSuite.UI.Services.Constants.Constant`, using `round(knob / 127 * 6, 2)`. Both nine-value curves match Suite captures exactly. On 2026-10-05 the user supplied a Gaming Suite microphone-EQ screenshot identifying the nine labels, left to right: **64 Hz, 125 Hz, 250 Hz, 500 Hz, 1 kHz, 2 kHz, 4 kHz, 8 kHz, 16 kHz**. The app now uses those labels for the already mapped EQ band order. This establishes the vendor UI frequency mapping; it is not a measurement of filter center frequency, bandwidth, or response. The first label is 64 Hz as shown, not an assumed 63 Hz. Internal speech-compressor and direction-dependent fields remain untouched.

The [original screenshot](../../tests/EposControl.Tests/Fixtures/b20-eq-frequencies.png) and [transcribed mapping](../../tests/EposControl.Tests/Fixtures/b20-eq-frequency-map.json) are pinned in the fixture SHA256 manifest. A regression checks the label/field order against that mapping, and the WPF check verifies actual slider labels and accessibility names.

On the same date, the user reported that the noise filter and high-pass work and that the EQ presets make an audible difference. Those controls now have manual listening confirmation in addition to state readback and rollback tests. The listening setup, filter levels and preset choices were not recorded; this confirmation does not establish a measured transfer function or a complete per-band response. Local record: `artifacts/b20-processing-listening-confirmation.json`. Formal frequency-response and noise-suppression measurements remain optional deeper validation, rather than a pending basic listening check.

The adapter holds the existing named mutex with a one-second timeout, changes only validated one-byte switches and four-byte level/EQ fields, signals the existing event, reads back, and releases every handle. On failure it restores only attempted fields when their state still matches the old or requested bytes. If state becomes ambiguous, it preserves the newer state and reports both update and restoration errors. Profiles also restore endpoint level/mute if effects fail, provided those endpoint values still match the profile's write.

The first B20 live test changed gate/filter and restored the pre-capture values. The expanded test also flipped all four enable switches and changed the nine-band EQ. It restored gate 0%, filter 2, all four switches on, and flat EQ. The full 4096-byte buffer matched the starting capture exactly. This verifies control-state access and restoration, not an acoustic measurement.

Current validation: 430 regression checks and 103 WPF scenarios passed, including GSX captured transitions/isolation, screenshot frequency mapping, sidetone, combined-profile rollback and playback activity. Profiles are tested with complete processing state and with legacy JSON. Missing fields in a provided processing/EQ object are rejected rather than filled with guessed defaults. Audio tests cover float/PCM decoding, RMS and peak measurement, silent packets, capture validity, comparison criteria and restoration after capture failure or concurrent edits.

## Run the suite

```powershell
.\tools\Test-App.ps1
```

Default tests use captures, in-memory transports, and the demo WPF window. They do not change any live device. `-SkipBuild` uses the last built binaries. Reports: `artifacts/tests.json`, `artifacts/tests.xml` (JUnit), and `artifacts/app-preview.png.json`. Failures return a nonzero exit code; independent checks continue so the report contains all failures.

Tests cover golden byte transitions and SHA256 fixture integrity, all 303 legal gate/filter combinations, all 16 enable-switch combinations, every EQ band at negative/fractional/boundary gains, device/direction isolation, unknown layouts, stale edits, no-op writes, timeouts, read/write/notification failures, partial writes, rollback failure, concurrent edits, profile compatibility, and actual WPF event handlers for apply/save/restore. The initial 20 endpoint/profile/surround-preparation checks remain included. Surround tests are synthetic preparation tests and do not validate live surround processing.

Optional live test (briefly changes levels, switches and EQ, then restores their starting values):

```powershell
.\tools\Test-App.ps1 -HardwareEffects 009f
```

Hardware checks are explicit opt-in and separate from normal regression runs. Lost connections or concurrent edits can prevent restoration; the report records that outcome. A readback pass does not prove audible DSP operation. No audio is recorded. Use `-HardwareEffects 0098` for the validated GSX microphone test; it preserves high-pass and checks B20, playback/unowned configuration and Windows audio controls.

## B20 audio-path check

```powershell
.\tools\Test-App.ps1 -HardwareAudio 009f
```

This opt-in check opens the selected B20 microphone through shared-mode WASAPI, using its mix format. It keeps Windows gain/mute and other effects unchanged, measures two seconds with the gate disabled, two seconds with the gate enabled at maximum, then two seconds with the gate disabled again. Each phase drains queued audio and waits 400 ms for processing to settle. The complete starting processing state is restored using the last state owned by the test. A concurrent edit is preserved and reported rather than overwritten. Opening capture is also checked for unexpected processing changes before any test writes.

Keep ambient input steady for approximately eight seconds. The microphone must be unmuted with a usable level. Measurements contain frame counts, RMS/peak in dBFS, clipping, invalid samples and capture errors. Samples are consumed in memory and discarded; no recordings are saved. The report is `artifacts/audio-verification-009f.json`.

A pass requires at least 12 dB attenuation, usable ungated signal above -70 dBFS, and the two ungated windows within 6 dB. Silent/short/clipped/interrupted or unstable captures are inconclusive, not passing. A stable usable signal without sufficient attenuation fails the diagnostic and suggests checking whether the installed EPOS processor is active in this audio path. These criteria are an ambient-input diagnostic, not a calibrated acoustic specification. Filter/high-pass/EQ audio response is not inferred from the gate result.

On 2026-10-05, the live B20 gate check passed. The 48 kHz stereo float32 stream measured -55.44 dBFS before, -105.46 dBFS at maximum gate, and -55.53 dBFS afterward: 49.93 dB attenuation. There were no clipped/invalid samples, discontinuities or timestamp errors. The starting gate 49.411766%, filter level 1, all four enabled effects and flat EQ were restored. All 48 mapped control bytes matched their pre-test snapshot exactly. Other buffer differences were at offsets 1088–1110 during live capture; those unowned fields were left untouched.

The CLI returns 0 for a pass with restoration, 1 for failure, and 2 for inconclusive with restoration. The PowerShell wrapper warns on an inconclusive diagnostic while retaining the successful offline test results. Normal regression runs never capture the microphone or change live settings.

Capture follows Microsoft's [IAudioClient shared-mode API](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iaudioclient) and [IAudioCaptureClient packet lifecycle](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer). All capture COM calls and releases run on the same dedicated STA thread; this diagnostic does not change default endpoints or route microphone audio to other applications.

## When adding direct USB access

`IMicrophoneEffectsBackend` is the public read/compare/apply contract. A new adapter must pass the same validation, readback, stale-edit, and isolation tests. USB traffic needs its own captured reports, report IDs, lengths, identity rules, and failure/timeout tests. The APO memory layout must not be reused as a USB report layout.

Keep Windows level/mute, device controls such as sidetone, and PC DSP as distinct adapter responsibilities. Direct USB cannot by itself reproduce a filter or surround engine that runs in the PC processor. Replacing those effects needs our own DSP and an appropriate Windows audio integration.

The first direct HID adapter is the experimental B20 physical pickup-pattern reader. It has a separate four-byte live fixture, fixed status query, decoder, USB-parent and descriptor guards, bounded traffic, timeout, cancellation and disposal checks. WPF displays its result only for the selected B20 microphone. Only cardioid has been captured live, and native query reliability remains unresolved. See [pickup-pattern protocol and limitations](../research/b20-pickup-pattern.md). Offline and demo checks do not contact hardware; `--dependencies --report FILE` explicitly performs a live status query while reading adapter availability.

## Fixture maintenance

`tests/EposControl.Tests/Fixtures` contains captured control buffers and a hash manifest, with no vendor executable, recording, or USB serial. Every buffer is documented in its README. Do not regenerate goldens to make a failure pass. Record a new one-control-at-a-time capture, explain the changed layout or behavior, and review the exact byte diff before updating expectations and hashes.

Read-only local captures:

```powershell
.\tools\Capture-Apo.ps1 -Name my-b20-capture -ProductId 009f
.\tools\Capture-Apo.ps1 -Name my-next-capture -Compare artifacts/my-b20-capture.bin
```

Raw research captures, hardware reports with local device identities, binaries, and build outputs stay in ignored directories. Only reviewed control fixtures belong with the tests.
