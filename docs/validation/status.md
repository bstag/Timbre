# Validation status

Updated 2026-10-08. This is the current summary; dated research and audit files preserve the evidence available at their milestone.

## Source verification

The source-only readiness run on 2026-10-08 built without existing `dist`, `bin`, or `obj` outputs, then passed **511 regression checks, 121 WPF scenarios, and 9 offline GSX probe guards**. Files under `artifacts/github-readiness-40a010ec/` are local evidence excluded from Git. The first sandboxed run failed temporary-file/native-permission checks; the same source passed outside that filesystem sandbox.

Run [normal verification](../development/building-and-testing.md#normal-verification) to obtain current counts. These numbers record this verification date, not a fixed requirement for future changes.

## Device evidence

| Area | Established on the local installation | Remaining boundary |
| --- | --- | --- |
| B20 microphone | Gain/mute; gate/filter/high-pass/EQ; hardware sidetone level/mute; profiles and opt-in processing restore. Filter/high-pass/EQ/sidetone listening confirmed. | Other physical pickup positions and reliable pickup-status querying; clean-machine installation. |
| B20 helper | Fresh-object creation, saved-state restoration, physical reconnect, and service-stopped gate measurement (49.7 dB attenuation) with exact restoration. | Cold boot, startup without diagnostic seed, installed background lifecycle. |
| GSX microphone | Gain/mute; independently routed gate/filter/EQ; USB sidetone and exact-value profiles. Basic processing and sidetone listening confirmed. | High-pass and separate sidetone mute unavailable; acoustic calibration and fresh-initializer DSP measurement. |
| GSX sound | Stereo/virtual 7.1, nine-band EQ, reverb, readback/restoration/isolation and user listening confirmed after compatible driver selection/reboot. | Calibrated surround/frequency response; fresh-initializer audio behavior. |
| GSX helper | Experimental fresh-object creation and microphone/playback control pilot with vendor service stopped at a controlled Windows Audio boundary on 2026-10-08; both buffers and diagnostic controls restored. | Audible DSP after fresh creation, cold boot, managed reconnect, automatic saved-state restoration. |
| App lifecycle | User-confirmed reopening and basic discovery/settings retention for both devices; instrumented profile/setup isolation and app-file persistence. | Cold-start chosen-state restoration and broader driver/PC coverage. |

Normal app processing uses existing EPOS interfaces and vendor background support. Experimental helpers are separate bounded processes, not installed replacement services. The installed driver/APO remains necessary. Successful readback alone does not prove audio processing.

Detailed evidence: [home checklist](home-validation.md), [B20 lifecycle](../research/b20-host-lifecycle.md), [GSX microphone](../research/gsx-microphone-controls.md), [GSX playback](../research/gsx-playback-controls.md), and [GSX initialization](../research/gsx-initialization.md).

## Distribution and next work

The portable release is framework-dependent Windows x64 and requires the .NET 9 Desktop Runtime. It includes project binaries, reviewed fixtures, and docs. User data and vendor installers/drivers are excluded. An installer and independent DSP/driver are future work in the [dependency plan](../roadmap/installer-and-dependencies.md).

Prioritize fresh-GSX audio validation, cold-start/lifecycle checks, clean-machine dependency verification, and pickup-status evidence before claiming broader independence. Keep unsupported commands disabled and preserve exact starting state during optional hardware checks.
