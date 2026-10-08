# Managed B20 helper lifecycle

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

The helper can now read the app's typed processing store and recover an observed B20 connection. This is a bounded experimental mode, not an installed service or a replacement installer.

## Behavior

`--manage-b20` requires an explicit physical USB instance, a device-matched diagnostic startup file, a processing-state directory, an empty report directory, a stop file and a 30–600 second duration. The app's live state directory is `%LOCALAPPDATA%\EPOS-Control\processing-state`; the administrator pilot instead uses a private directory under its reports.

| Situation | Action |
| --- | --- |
| Target B20 absent at launch | Report `WaitingForDevice`; do not load startup/state files or open vendor control objects. |
| Different physical B20 connected | Keep waiting for the specified instance. |
| Multiple physical B20 units or duplicate microphone endpoints | Fail closed. Vendor control objects cannot isolate multiple units of the same model. |
| Target appears | Validate current endpoint identity and all startup/state input before connecting. Require Gaming Suite's service to be stopped or absent. |
| Complete compatible objects already exist | Retain them. Preserve current processing unless saved per-device restore is explicitly enabled. |
| Objects missing | Attempt fresh-only initialization using the diagnostic seed. Partial objects, incompatible layouts and races are refused without resetting existing state. |
| Saved restore enabled | Apply the saved complete processing state once, through the usual compare/apply/readback transaction. This takes precedence over the diagnostic snapshot's mapped settings. |
| Fresh creation with saved restore disabled or no saved state | Apply the explicit startup snapshot's processing. This is explicit initialization input, not an implicit opt-in to the app's remembered settings. |
| Healthy connection | Read/publish health; preserve later edits. Do not continually replay the store. |
| Observed disconnect or endpoint/name change | Release our handles. Revalidate the same physical instance on return and reload the latest startup/state files. |
| Transient discovery/transport failure | Close our connection and retry at two-second intervals, up to three consecutive failures. Successful connection resets the failure count. |
| Corrupt state, wrong device, ambiguity or access refusal | Enter terminal `Faulted`; require an explicit new run. |
| Stop file, Ctrl+C or deadline | Close connection and release helper ownership. Deadline before any successful connection is an error; explicitly stopping a waiting helper is allowed. |

Discovery/health runs once per second. A fast disconnect/reconnect between observations may be missed if the endpoint ID stays unchanged. The helper has no USB firmware operations, audio rerouting, VST host or driver installation.

The production connector rechecks the service immediately before processing Apply as well as before object attachment/creation. It does not stop or start services itself. Restarting vendor support while a connected helper is read-only is permitted for the restoration script; further reconnect Apply would be refused while the vendor service is running.

## Ownership and reports

Initializers and managed helpers acquire `Global\EposControl.B20Host.v1` on their control thread. A competing process is rejected. A crashed owner's abandoned lock can be reacquired; the connection still validates existing native state and refuses partial or corrupt objects. Read-only retention helpers can coexist because they never initialize or restore settings. The owner lock uses the creating account's Windows default security; cross-account/service-account access has not been established and must be designed before service installation.

`starting.json` records managed configuration. `heartbeat.json` describes current state, endpoint, processing, generation, retry count and error. `ready.json` records the first successful connection; use heartbeat for current availability after that point. `connection-0001.json`, etc. record each new connection generation and its actual restore source: `LastSavedProcessing`, `ExplicitSnapshot` or `None`. `stopped.json` records shutdown/result. Startup `SettingsWrites` means a restore transaction was requested for that generation, not continuous writes on every heartbeat.

The helper reads the store but does not replace remembered settings or opt-in preferences. The app remains their writer after successful user Apply. No settings are stored as opaque memory dumps. The separate diagnostic seed is still necessary to preserve unmapped configuration when creating new objects.

New startup snapshots include the canonical `DeviceIdentity` used by the typed store. They survive friendly-name and endpoint GUID changes for the same physical USB instance. Older snapshots without that optional field retain their stricter profile/name identity check; mismatched snapshots fail closed.

## Verification on 2026-10-06

The 2026-10-06 managed-helper milestone passed **286 regression checks and 25 WPF scenarios**, with no build warnings. Twenty-eight checks were added after the persistence milestone: canonical snapshot identity, absent/replaced/ambiguous devices, saved-state precedence, preservation of existing state and external edits, reconnect reload, corruption, bounded retries, access refusal, failure disposal, shutdown, command parsing and helper ownership. Native lifecycle tests destroy and recreate unique private Windows object sets and compare the restored full memory with the diagnostic seed plus validated patches. They do not open actual EPOS object names or manipulate the vendor service.

Actual executable checks also passed:

- `artifacts/apo-host/managed-process-a3607129a73a4289bfc026ae411efd3d`: intentionally absent target, no seed/store access, no false readiness, competing-process refusal, prompt stop, and acquisition by a subsequent process. No device writes or vendor-object creation.
- `artifacts/apo-host/managed-guard-dda880543f064985947d3f77a7ef7b3c`: pilot typed-state file parsed successfully; actual running-service guard rejected the connection before vendor objects were opened/created. No readiness or settings writes; service remained running.
- `artifacts/apo-host/20261006-121255-f68c58d6`: legacy read-only retention mode passed after command-line changes, preserving initial settings/service/UI.
- `artifacts/apo-host/20261006-121852-f77a9672`: administrator managed pilot passed on the real B20. Processing objects were unavailable before initialization. The managed helper created fresh objects and restored from the isolated opted-in typed store (`LastSavedProcessing`). Gaming Suite's service remained stopped through the gate/filter/high-pass/EQ control checks. Independent comparison found zero effects, sidetone or Windows endpoint-state differences afterward; service/UI were restored and the helper shut down successfully. Original reports are unchanged; `assessment.json` records the independent verification.

Managed fresh startup and saved-state control restoration now pass in a warm Windows session using the diagnostic seed. These checks do not establish unplug/replug, cold-boot independence, startup without a seed or actual DSP output. Audio was not requested in the managed pilot; the earlier service-stopped gate audio measurements remain inconclusive. The pilot's saved settings matched baseline, while isolated/native tests separately prove precedence when saved controls differ from the seed.

## Real-device pilot

Run in administrator PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\tools\Test-ApoHost.ps1' -StopSuiteTemporarily -ManagedLifecycle
```

The script captures baseline processing and its full diagnostic layout while vendor support is running. It prepares an isolated, opted-in typed store with the **same baseline settings**, then temporarily closes the Suite UI/stops its service and launches managed mode. Readiness must report fresh creation, `Connected`, `LastSavedProcessing` and matching baseline readback. It runs gate/filter/high-pass/EQ control/restoration checks, restores vendor service/UI, stops our helper and checks final settings. Your real app store and restore preference are untouched. No listening, physical switch changes, unplugging, reboot or permanent startup-policy change is needed.

Saved-state precedence with settings different from the seed is proved by isolated/native tests; this first hardware pilot intentionally keeps starting settings unchanged at startup. Optional `-HardwareAudio` retains the existing signal-quality checks and restoration but requires steady adequate input. The pilot leaves `ColdStartTested` and `ReconnectTested` false.

Read-only process verification is reproducible with `tools/Test-ManagedHostProcess.ps1`; it uses an intentionally absent target and does not require administrator rights to stop services.

The 2026-10-06 managed hardware pilot passed. The subsequent physical reconnect and gate audio checks are recorded below. Startup with the real B20 absent, cold boot and service account/storage access remain to be validated before installing a background service.

## Prepared manual reconnect pilot, 2026-10-07

After the user stopped the vendor service, B20 processing objects were absent; GSX's existing interface remained readable. A reconnect run was prepared under `artifacts/apo-host/manual-reconnect-20261007-194220`. It uses the captured device-matched diagnostic layout plus an isolated copy of the app's newer verified saved processing. Only the isolated copy is opted in; the app's real state file and preference remain unchanged. The older seed's mapped settings are superseded by the typed store through the already-tested saved-state precedence rule.

The first copied state was rejected because PowerShell converted its UTC timestamp to a local offset; copying the source JSON verbatim apart from the isolated opt-in corrected that preparation error. Native initialization then failed at global memory creation. The non-elevated token lacks `SeCreateGlobalPrivilege`, required for creating global file mappings outside session zero ([Microsoft documentation](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces)). No helper reached readiness or performed settings writes in those attempts. Reports preserve both failed attempts.

The prepared launcher checks the unchanged source-state hash, physical identity, stopped service and absent competing helper. It validates readiness and restored settings, then leaves a ten-minute helper running for the physical check. It does not install a service, change vendor startup policy or change the app's actual restore preference. Syntax and `-WhatIf` passed in Windows PowerShell; evidence is `artifacts/b20-reconnect-launcher-validation-20261007.json`.

Run in administrator PowerShell while the service remains stopped:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\tools\Start-B20ReconnectCheck.ps1'
```

Paste the readiness output before unplugging. An observed `WaitingForDevice` followed by a new connected generation, verified restored settings and unchanged other-device state is needed for the replacement-helper reconnect pass. The helper stops at its deadline; use its prepared stop file for an earlier graceful stop.

The user launched the prepared helper in administrator PowerShell. `host-admin-f0d9b2e5/ready.json` and the current heartbeat report `Connected`, generation 1, `CreatedFresh=true`, `LastSavedProcessing` and the expected newer saved state. The original store hash is unchanged, with the vendor service Stopped. This actual run demonstrates saved-state precedence over a different seed: the seed had microphone EQ disabled, while the newer saved state has it enabled with the same Flat curve. Readiness and typed processing restoration are passed; assessment is `admin-readiness-assessment.json` under this pilot directory.

The B20 native gate/filter/high-pass/EQ control test then passed while the vendor service remained Stopped. Both full 4096-byte B20 and GSX buffers were identical before and after the temporary test, and Windows levels/mutes were preserved. Report: `artifacts/b20-managed-service-stopped-effects-20261007.json`; complete comparisons: `artifacts/b20-managed-reconnect-{before,after-control-check}-20261007.bin` and `artifacts/gsx-during-b20-managed-{reconnect-before,after-control-check}-20261007.bin`.

The user unplugged only B20 and waited for confirmation before reconnecting. The helper reported `WaitingForDevice` at generation 1, then `Connected` at generation 2 with `CreatedFresh=true` and `LastSavedProcessing`. Reconnected processing matched the saved state; the entire B20 and GSX 4096-byte buffers matched their pre-disconnect snapshots. Windows controls, B20 sidetone and processing diagnostics had zero differences, the original saved-state hash remained unchanged and the EPOS service stayed Stopped. Physical managed-helper reconnect **passed**. Evidence in this pilot directory: `disconnected-observed.json`, `disconnected-assessment.json`, `reconnected-observed.json`, `reconnect-control-differences.json` and `reconnect-assessment.json`; full buffers are `artifacts/b20-managed-reconnect-{before,after}-20261007.bin` and `artifacts/gsx-during-b20-managed-reconnect-{before,after}-20261007.bin`. The reconnect assessment's `AudioTested=false` describes that stage; the subsequent audio check is recorded separately below.

With the user's steady room noise, B20 shared-mode gate off/maximum/off audio **passed** while Gaming Suite's service remained stopped and the managed helper was connected. RMS was -52.750, -102.450 and -51.884 dBFS, giving 49.700 dB attenuation under the unchanged 12 dB criterion. There were no invalid or clipped samples, discontinuities or timestamp errors. The original processing was restored, and both complete device buffers matched their pre-audio snapshots. This establishes actual gate processing through the retained EPOS driver/APO with our helper supplying its configuration objects; filter, high-pass and EQ were tested as control transactions, rather than individually measured audio responses in this run. Evidence: `artifacts/b20-managed-service-stopped-audio-20261007.json`, this pilot's `audio-runtime-assessment.json` and `live-validation-summary.json`.

The helper released its connection and ownership cleanly at the ten-minute deadline, reporting `Success=true` in `host-admin-f0d9b2e5/stopped.json`. Vendor startup policy and the real saved-state file/restore preference were unchanged. With the vendor service still stopped, the B20 page initially reported unavailable processing after helper shutdown; after restoring the original live capture view, a later diagnostic and the UI read back the expected processing again. This is a warm-session observation, not evidence of fresh initialization without a helper. Evidence: `artifacts/post-b20-helper-shutdown-controls-20261007.json`. Normal vendor service recovery was requested separately. Cold boot, seed-free startup and an installed service remain outstanding.

On 2026-10-08 the user restarted the vendor service. Read-only verification found `EPOSGamingSuiteService` Running with automatic startup and no replacement helper process. Both devices' audio, microphone, playback and diagnostic sidetone states matched the restored post-test baseline with zero differences; no corrective writes were needed. Evidence: `artifacts/vendor-service-recovery-controls-20261008.json` and `artifacts/vendor-service-recovery-assessment-20261008.json`.
