# Remote completion goal

Historical record of the completed 2026-10-07 remote-work pass. Statements below describe that pass; subsequent listening, B20 reconnect/audio, and GSX initialization results are summarized in [current validation status](../validation/status.md). Referenced artifacts remain local and are not included in Git.

Goal started on 2026-10-07: complete the remaining core EPOS Control work that can be implemented and verified without physical access to the B20/GSX 300. Current baseline: 477 regressions, 109 WPF scenarios and a verified portable package. Existing device settings and named profiles must be preserved during live checks.

## Work to complete remotely

- [x] Audit outstanding controls, runtime dependencies and known limitations against source and recorded evidence. Keep unsupported commands disabled and distinguish missing evidence from implementation defects.
- [x] Verify GSX USB live-edit responsiveness, queued edits, cancellation, stale-state handling, close/refresh guards and profile/setup transactions. Improve behavior where an actual test exposes a gap.
- [x] Diagnose capture-quality flags using a repeatable packet/analysis check. Separate startup transitions, timestamp uncertainty and ongoing discontinuities in evidence rather than hiding flags. Validate frequency-analysis behavior with deterministic signals and real quiet input.
- [x] Exercise real multi-device profile/setup membership, restoration, independent state and app lifecycle through computer use using isolated test data. Validate unavailable/disconnected paths offline when physical device removal is required.
- [x] Review B20 pickup-pattern query reliability and host/GSX initialization evidence. Run safe bounded investigations and helper/process checks; record any administrator-only experiment separately if this session cannot perform it.
- [x] Tighten diagnostics, portable verification and operational documentation around actual capabilities and limitations. Complete targeted regression/failure tests and check that default verification sends no USB reports or audio.
- [x] Build the final matching app/check payload, verify extraction and applicable native checks, and retain evidence plus a concise physical validation checklist.

This is a finite audit of current core functionality, not a request to add arbitrary future DSP/VST/firmware features. Any new work item needs a concrete current defect, missing validation or dependency requirement.

## Checks requiring physical access or separate access

- Audible GSX microphone effects, EQ/surround/reverb and sidetone; calibrated acoustic measurements.
- B20 pickup-switch positions other than the known cardioid position, analog plugs and physical reconnects.
- Cold boot and startup/reconnect behavior that could disrupt this remotely used PC.
- Privileged service-stop experiments if the current tools cannot obtain administrator access. An access limitation does not turn untested service-independent behavior into a pass.

## Evidence and progress

The starting release is recorded in `artifacts/gsx-sidetone-release-validation-20261007.json`. Detailed results and completed items will be appended here as work progresses. Physical-only checks do not block unrelated remote work, and readback is not treated as listening or proof of independent DSP startup.

- Capture diagnosis: repeated GSX red runs reproduced initial-only flags being shown as continuing interruption. Green native captures on GSX and B20 each received about 300 packets, one initial flag, no later flags and no timestamp flags. Spectrum tests now retain valid samples on timestamp uncertainty and ignore zero-frame quality flags. Strict audio-effect validation is unchanged. The real GSX UI also displayed the distinct initial-capture label with live RMS values. Reports: `artifacts/monitor-packets-*-20261007.json` and `artifacts/monitor-flags-regression-green-20261007.json`.
- Delayed WPF USB checks reproduced latest-edit loss and a cancelled queue returning success to a waiter. The fixes preserve newer slider drafts, serialize live updates, retain manual drafts and return cancellation/failure correctly. Named profile preservation, external-change rejection and close guards are tested. See `artifacts/gsx-async-red-20261007.log` and `artifacts/gsx-async-final-20261007.log`.
- Managed helper process validation passed absent-device waiting, competing-owner refusal, graceful release and restart, without settings writes or creating vendor objects. The PowerShell harness now retains the process handle so a rapid refusal cannot lose its exit code. Report: `artifacts/apo-host/managed-process-9a59689eb3d2412b810ef377bc6f54fa/assessment.json`.
- This session is not Windows administrator, including outside the filesystem sandbox. Service-stop/fresh global-object experiments therefore remain access-limited. Previous successful B20 administrator warm-session evidence is retained; it is not a GSX initializer or cold-boot proof.
- B20 pattern status remains experimental after a fresh 1.5-second query timed out. Previous alternative I/O and service-stop evidence was reviewed instead of repeating unproductive command variations. Routing guards now require complete B20 physical ancestry, exactly one selected endpoint and an unchanged post-query HID interface. The GSX has no validated independent initializer; B20-only host guards reject other products. Cold/reconnect and service-independent GSX DSP remain explicit gaps.
- Native computer use saved the exact GSX sidetone byte 226, applied a slider drag (73.1% draft → 71% quantized readback), then restored the profile. A separate Gaming snapshot included only GSX Sound and B20 Microphone. B20 gate 25% and GSX reverb 40% applied live; Gaming restored both. Every mapped control and both entire 4096-byte buffers matched baseline. Excluded B20 Sound and GSX Microphone stayed unchanged during auditioning. App close/reopen preserved named test files and controls, with capture off and no automatic setup inclusion/application. Real user profile hashes were unchanged. Evidence: `artifacts/remote-goal-native-restoration-20261007.json`, `artifacts/remote-goal-reopen-validation-20261007.json`, and the referenced snapshots/isolated test stores.
- Final source checks pass 481 regressions, 116 WPF scenarios and nine offline GSX research guards, with zero build warnings/errors. The delayed test now invokes a real Save profile handler during pending USB work and verifies no snapshot is written until the latest value finishes. Scoped device-list colors avoid its native disabled-template white flash. Log: `artifacts/remote-goal-final-tests-20261007.log`.
- Portable/source verification adds an explicit `-MonitorPackets 009f|0098` option: three seconds of capture metrics, separate quality counts, no recording/playback/settings changes. Default verification still sends no USB reports and starts no audio. Live-view, profile/setup, diagnostics and dependency docs now describe the observed boundaries, including reverb/sidetone updates missing from older historical notes.
- Final package: `artifacts/releases/EPOS-Control-win-x64-20261007-150802-1253a693.zip`, SHA256 `C25F4133EB41199D2E637E7573F673B7265A34B64B690BB9ECB7160AE8C0BF53`. All 93 payload files matched after extraction. Extracted verification passed 481 regressions/116 WPF scenarios and discovered four endpoints. Its explicit GSX five-level USB test restored byte 226 and preserved both full buffers/all Windows controls. Packaged GSX/B20 metrics checks each observed one initial flag and no later/timestamp flags. Final independent captures and diagnostics again matched every original byte/control; real user settings hashes remained unchanged. Portable log: `artifacts/remote-goal-portable-verification-20261007.log`; aggregate: `artifacts/remote-goal-completion-20261007.json`.

The finite remote completion pass is finished. The remaining home checklist is [home-validation.md](../validation/home-validation.md): listening, physical patterns/connections and cold-start behavior. The administrator service-stop boundary and unproven GSX initializer remain separately recorded limitations, not passes. B20 pickup status stays experimental. No service, driver, startup policy or user profile was replaced by this pass.

## Audited capability and dependency boundary

| Area | Current remotely established behavior | Remaining evidence/dependency |
| --- | --- | --- |
| B20 microphone | Windows gain/mute; independent topology sidetone level/mute; gate/filter/high-pass/nine-band EQ; profiles and opt-in processing restore | Reliable pickup status, additional physical positions and cold/reconnect behavior. Existing filter/high-pass/EQ/sidetone listening was user-confirmed. |
| GSX microphone | Windows gain/mute; independently routed gate/filter/EQ and switches; live frequency metrics; exact-byte USB sidetone and profiles | Audible effects/monitoring, service-independent startup. High-pass and separate monitoring mute remain unmapped. |
| GSX sound | Windows volume/mute; stereo/virtual 7.1; nine-band EQ; reverb switch/amount; optional loopback metrics | Listening with multichannel material; location of loopback capture relative to vendor processing. EQ-enable byte 65 remains unowned. |
| B20 sound | Windows volume/mute, independently excluded from setups | No validated custom sound-effect adapter; 8-channel Windows metadata is not proof of B20 surround. |
| Runtime/preservation | Framework-dependent x64 WPF app, .NET 9 Desktop Runtime, matching portable checks/fixtures; EPOS signed driver/APO installation remains necessary | This package does not install/remove drivers or retire the service. Vendor redistribution permission and clean-machine installation are unresolved. |
| Helper lifecycle | Separate B20-only source-built pilot; validated warm-session administrator creation and typed restore; absent-device process ownership/stop/restart checks | No validated GSX initializer, installer service, physical reconnect or cold-boot DSP proof. |

Unowned FRC/AGC/shared-buffer fields, unsupported USB commands, firmware, VST routing and a generic replacement driver are outside the current core release. Their existence in vendor code is not a capability claim. The finite remote goal is complete when the implemented controls, guards, lifecycle boundaries and portable release below are verified; it does not invent those missing hardware/protocol facts.
