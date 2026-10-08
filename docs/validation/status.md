# Validation status

Updated 2026-10-08. This is the current summary; dated research and audit files preserve the evidence available at their milestone.

## Source verification

The source-only readiness run on 2026-10-08 built without existing `dist`, `bin`, or `obj` outputs, then passed **511 regression checks, 121 WPF scenarios, and 9 offline GSX probe guards**. Files under `artifacts/github-readiness-40a010ec/` are local evidence excluded from Git. The first sandboxed run failed temporary-file/native-permission checks; the same source passed outside that filesystem sandbox.

Run [normal verification](../development/building-and-testing.md#normal-verification) to obtain current counts. These numbers record this verification date, not a fixed requirement for future changes.

An additional run on 2026-10-08 passed **517 regression checks, 121 WPF scenarios, and 9 offline GSX probe guards** after adding startup-storage coverage. Real startup and tests now share `ApplicationStorage`; six cases check fresh Timbre stores, preservation of prior EPOS-Control/executable data, existing-state reload, directory overrides, and demo/render isolation. Evidence is in `artifacts/startup-storage-20261008/verification.log` and the normal JSON/JUnit reports. The initial restricted run encountered filesystem/native-object access errors; the complete offline run outside that restriction passed. No hardware or services were changed by verification.

## Device evidence

| Area | Established on the local installation | Remaining boundary |
| --- | --- | --- |
| B20 microphone | Gain/mute; gate/filter/high-pass/EQ; hardware sidetone level/mute; profiles and opt-in processing restore. Filter/high-pass/EQ/sidetone listening confirmed. | Other physical pickup positions and reliable pickup-status querying; clean-machine installation. |
| B20 helper | Fresh-object creation, saved-state restoration, physical reconnect, and service-stopped gate measurement (49.7 dB attenuation) with exact restoration. | Cold boot, startup without diagnostic seed, installed background lifecycle. |
| GSX microphone | Gain/mute; independently routed gate/filter/EQ; USB sidetone and exact-value profiles. Basic processing and sidetone listening confirmed. Fresh-initializer, service-stopped gate measurement passed with 49.84 dB attenuation and exact restoration. | High-pass and separate sidetone mute unavailable; acoustic calibration and fresh-session filter/EQ audio validation. |
| GSX sound | Stereo/virtual 7.1, nine-band EQ, reverb, readback/restoration/isolation and user listening confirmed after compatible driver selection/reboot. | Calibrated surround/frequency response; fresh-initializer audio behavior. |
| GSX helper | Experimental fresh-object creation, microphone/playback controls and microphone gate audio measurement with vendor service stopped at a controlled Windows Audio boundary on 2026-10-08; both buffers and diagnostic controls restored. | Fresh-session playback/filter/EQ audio validation, cold boot, managed reconnect, automatic saved-state restoration. |
| App lifecycle | User-confirmed reopening and basic discovery/settings retention for both devices; instrumented profile/setup isolation and app-file persistence. | Cold-start chosen-state restoration and broader driver/PC coverage. |

Normal app processing uses existing EPOS interfaces and vendor background support. Experimental helpers are separate bounded processes, not installed replacement services. The installed driver/APO remains necessary. Successful readback alone does not prove audio processing.

Detailed evidence: [home checklist](home-validation.md), [B20 lifecycle](../research/b20-host-lifecycle.md), [GSX microphone](../research/gsx-microphone-controls.md), [GSX playback](../research/gsx-playback-controls.md), and [GSX initialization](../research/gsx-initialization.md).

The fresh-GSX audio run `artifacts/gsx-initialization/20261008-172241-ad0175d4` passed initialization, microphone/playback control checks, gate audio measurement and recovery. Ungated input returned within 0.42 dB of its starting RMS; all three measurement windows had no discontinuities, timestamp errors, clipped samples or invalid samples. Both full 4096-byte buffers matched baseline, final diagnostics had zero differences, both helpers exited successfully and both services returned to Running. `saved-evidence-assessment.json` independently compares the saved captures and buffers; it does not perform another live check. This establishes microphone gate processing after fresh creation, while the installed EPOS driver/APO remains required.

## Distribution and next work

The portable release is framework-dependent Windows x64 and requires the .NET 9 Desktop Runtime. It includes project binaries, reviewed fixtures, and docs. User data and vendor installers/drivers are excluded. An installer and independent DSP/driver are future work in the [dependency plan](../roadmap/installer-and-dependencies.md).

Prioritize fresh-GSX playback/filter/EQ audio validation, cold-start/lifecycle checks, clean-machine dependency verification, and pickup-status evidence before claiming broader independence. Cold boot and managed reconnect are deferred while the user is actively using this PC. Keep unsupported commands disabled and preserve exact starting state during optional hardware checks.
