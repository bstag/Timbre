# Managed GSX processing lifecycle

Updated 2026-10-09. See [current validation status](../validation/status.md) for hardware evidence and remaining checks.

The experimental helper now supports `--manage-gsx`: it waits for one explicitly selected physical GSX 300, holds its processing objects, observes both pages and reconnects when its endpoint pair changes. This extends the earlier [fresh-initialization experiment](gsx-initialization.md). It does not install a Windows service, change service startup or stop/restart audio. The normal WPF app still uses the installed processor interface and explicit device/setup profiles; this helper's saved-state policy is not yet connected to the UI.

## Saved state and restoration

`GsxProcessingStateStore` stores one typed JSON file per physical USB instance in an explicitly supplied directory. The record contains schema version, stable USB identity, UTC save time, complete microphone and playback processing, and `RestoreOnConnect`, which defaults to false. Endpoint GUIDs and friendly names are excluded from its identity. Saves use a bounded exclusive lock and atomic replacement. Corrupt records are refused and preserved rather than overwritten.

Saved microphone controls include gate/filter amounts, their switches and nine-band EQ. Playback includes Stereo/7.1, nine-band EQ and reverb switch/amount. The unsupported GSX high-pass bit is observed but cannot be changed: a restore requesting a different value fails before either page is written. Windows gain/mute and USB sidetone are outside this processing record and remain separate controls/profile fields. B20 state uses its existing store and separate model mapping.

Both GSX pages occupy one shared buffer. `ApoGsxProcessingBackend` reads and compares both expected pages under one mutex, validates every patch before the first write, applies the supported fields, signals once and verifies both pages. A failure restores only attempted fields whose values still match the transaction's before/after values. A newer edit to an owned field prevents rollback; newer unowned bytes are preserved. Failed hardware transactions never update saved state; a persistence error after a completed apply is reported separately.

The managed helper validates the explicit diagnostic snapshot and saved record before opening native objects. The diagnostic snapshot is still required to preserve unmapped configuration during fresh creation; it is separate from typed persistence.

| Connection | Restore opt-in off | Restore opt-in on |
| --- | --- | --- |
| Complete existing objects | Observe without changing either page | Restore the saved pair once |
| All objects absent | Create from the explicit diagnostic snapshot | Create from that snapshot, then apply the saved pair |
| Partial, corrupt or ambiguous objects/devices | Refuse | Refuse |

Automatic restoration happens once per connection generation. Health polling observes subsequent edits without replaying saved values. Losing either endpoint closes the lease and waits for the complete pair; a change to either endpoint ID reconnects and reloads the latest saved state. Transient transport/discovery failures allow three attempts, two seconds apart. Identity, schema, permissions and unsupported-control errors fail closed.

## Bounded helper

Build the helper from a source checkout using `tools/Build-App.ps1` or normal verification. The portable app ZIP does not contain the experimental host.

After preparing a validated diagnostic snapshot and an isolated saved-state directory, the advanced command shape is:

```powershell
.\dist\host\Timbre.Host.exe --manage-gsx `
  --initial-state '.\artifacts\gsx-startup.json' `
  --state-directory '.\artifacts\gsx-saved-state' `
  --device-instance '<exact GSX USB instance>' `
  --report-directory '.\artifacts\gsx-managed-run' `
  --stop-file '.\artifacts\stop-gsx-managed' `
  --seconds 120
```

The report directory must be empty and the stop file absent. Duration is limited to 30–600 seconds; creating the stop file or pressing Ctrl+C shuts down. Reports include waiting/retrying/faulted states, both endpoint identities, paired readback, restore source and connection generation. The helper shares an owner lock with the existing GSX initializer so competing helpers are refused. The EPOS service must already be stopped before connection or writes; the helper does not stop it.

This mode uses live metadata discovery with Windows Audio running. It does not implement the paused-audio preparation/request handshake used by `--initialize-gsx-paused`. The previously successful fresh-initialization/audio-engine experiment remains a separate workflow. A real managed pilot still needs baseline capture, isolated opt-in state, service ownership handoff and independent restoration verification before changing normal app startup.

## Verification on 2026-10-08

Offline verification passed **588 regression checks, 121 WPF scenarios, 51 fixture-free PowerShell runner checks and 9 USB probe guards**, with no build warnings. The 51 new core checks cover paired transactions, failure rollback, stale state on either page, unsupported high-pass, malformed/oversized saved records, identity changes, concurrent writers, lock contention, missing/ambiguous devices, saved-state precedence, polling, reconnection, bounded retries and shutdown.

Native tests create unique private `Local\Timbre.Tests.*` objects, save typed processing, destroy/recreate the complete set, restore both pages and compare the entire resulting buffer with the diagnostic seed plus validated patches. They also check the native write whitelist, corrupt playback refusal and isolation from a separate B20 mapping. They do not open real EPOS processing objects or change services. Evidence: `artifacts/gsx-managed-verification-20261008.log`, normal JSON/JUnit reports and the WPF demo report.

The actual bounded executable passed absent-target waiting, competing-owner refusal and prompt stop/restart: `artifacts/apo-host/managed-process-gsx300-fdd8f2ab622744a7a90ae653d41ffa73/assessment.json`. Its target was intentionally nonexistent, and the seed/store paths were absent. No readiness was reported and no saved-state directory was created. Reproduce this check without administrator service control:

```powershell
.\tools\Test-ManagedHostProcess.ps1 -Device GSX300
```

The final build also passed that GSX executable check under PowerShell 7 (`managed-process-gsx300-e033f8ae4fef4a2ebff2c44b7ce08daf`) and the default B20 check under Windows PowerShell 5.1 (`managed-process-b20-67296f7789994b20abe29265df3c810d`). Both report sets are under `artifacts/apo-host/` and remain local.

These results establish offline policy/native transport and executable lifecycle behavior. They do not establish managed restoration on the physical GSX, physical reconnect, cold boot or service-account access. The installed EPOS driver/APO remains required. Prior fresh-session microphone-gate and playback-EQ DSP passes retain their separate evidence; they do not validate this new managed mode.

## Managed pilot preparation on 2026-10-09

`Test-GsxInitialization.ps1 -ManagedLifecycle` now prepares a current diagnostic snapshot plus an **isolated opted-in typed store with the same baseline values**. Its read-only `--validate-managed-gsx` helper parses the actual stored record, checks physical identity and unsupported high-pass changes, then exits without opening vendor objects. The runner requires matching app/helper/test core binaries before preparation. Your normal saved state and restore policy are untouched.

Read-only preparation passed on the real GSX/B20 pair: `artifacts/gsx-initialization/20261009-173029-40df91da`. The validator exited successfully, both complete before/after buffers were byte-identical, the isolated saved record was unchanged and both services remained Running. No service stop, managed restore, object creation, control changes or audio measurement was requested. `preparation-assessment.json` independently compares the saved buffer hashes and validates the outcome boundaries.

The normal verification path passed **589 regressions, 121 WPF scenarios, 66 fixture-free PowerShell checks and 9 USB guards**, with no build warnings. Fifteen new runner checks cover option separation, canonical identity, read-only validation evidence, paired readiness, missing/non-boolean flags, restore policy and outcome requirements. Windows PowerShell 5.1 and PowerShell 7 passed those pure checks. One core regression covers complete managed-validation command input and refusal of those restore options in ordinary validation mode. Evidence: `artifacts/gsx-managed-pilot-verification-20261009.log` and `gsx-managed-runner-checks-20261009-ps51.json` / `-ps7.json`.

Timbre was open during verification. The first normal build encountered its locked DLLs; verification then passed using `Test-App.ps1 -OutputDirectory artifacts/gsx-managed-pilot-build-20261009`. That publishes app/helper binaries separately and renders the chosen app in demo mode, leaving the running app open. Use `-BuildDirectory` on the pilot runner to select those verified outputs.

When brief microphone/playback control changes are acceptable, run in administrator PowerShell with Timbre and Gaming Suite controls idle:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\tools\Test-GsxInitialization.ps1' `
  -ManagedLifecycle -StopSuiteTemporarily `
  -BuildDirectory '.\artifacts\gsx-managed-pilot-build-20261009'
```

The directory above must first be built and verified as described. If the normal `dist` build is current and the core binaries match, omit `-BuildDirectory`. Without `-StopSuiteTemporarily`, the same managed option performs only read-only preparation and needs no administrator service control.

The live pilot captures a fresh baseline, holds B20 read-only, briefly closes the original Suite UI/stops only EPOS support, starts `--manage-gsx` with the isolated store and requires `Connected` / `LastSavedProcessing` readback for both pages. It can retain existing objects; `CreatedFresh=false` is valid and is not reported as fresh initialization. Gate/filter/EQ and playback mode/EQ/reverb control checks restore their starting values. No tone, microphone capture, unplugging, reboot or Windows Audio restart is performed.

Recovery starts EPOS support while the helper still holds objects, requires the current service process to own all three GSX handles, then releases our helpers. Both complete buffers are compared before and after release, all captured Windows/processing/sidetone controls are compared with baseline, and the isolated saved file must remain unchanged. Missing evidence, forced helper termination, handoff failure, recovery errors or setting differences fail the overall result. Reports distinguish read-only preparation, managed restoration, fresh creation and audio measurements.

The managed hardware pilot below passed. This control-only pilot does not establish physical managed reconnect, cold boot, actual DSP response or differing saved-state precedence on hardware; isolated/native tests already cover the latter policy. Managed mode cannot be combined with audio-engine restart, physical reconnect or audio measurements in this pilot runner.

## Managed hardware pilot on 2026-10-09

Administrator run `artifacts/gsx-initialization/20261009-173200-45067797` passed managed restoration, microphone/playback controls and recovery. Windows Audio stayed Running throughout; no restart, audio measurement or physical reconnect was requested. The helper retained existing complete objects (`CreatedFresh=false`) and reported generation 1, `Connected`, `AutomaticRestore=true` and `LastSavedProcessing`. Both pages matched the isolated opted-in saved record and baseline. The original record contained baseline values, so this proves the saved-state path and readback rather than differing saved-value precedence on hardware.

The EPOS service was Stopped before connection and through both microphone gate/filter/EQ and playback mode/EQ/reverb control/restoration checks. Both hardware control reports passed. The complete GSX and B20 buffers matched baseline while the managed helper remained connected.

Recovery observed the restarted vendor process holding all three GSX objects after **4.41 seconds**, while the managed helper also held them. Both managed GSX and read-only B20 helpers then exited successfully. The first final diagnostic snapshot had zero captured control differences; both services were Running, and the isolated saved record was unchanged. The runner compared both complete buffers after helper release.

Independent read-only post-release diagnostics and captures again found zero differences across Windows level/mute, microphone processing, playback processing and sidetone, with both complete 4096-byte buffers byte-identical to baseline. `independent-managed-assessment.json` checks paired readiness, saved record identity/values/timestamp, stopped-service control reports, concurrent vendor/helper handle ownership, successful helper exits, independent buffers and current service/helper state. Capture files `artifacts/gsx-managed-post-release-20261009.bin` and `b20-managed-post-release-20261009.bin` remain local.

Managed warm-session restoration and recovery are now verified. The normal WPF UI still uses its existing explicit profiles and has no GSX automatic-restore setting. Physical managed reconnect, fresh managed creation, cold boot, service-account/storage access and startup without the diagnostic seed remain outstanding.
