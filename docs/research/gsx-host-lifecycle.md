# Managed GSX processing lifecycle

Updated 2026-10-09. See [current validation status](../validation/status.md) for hardware evidence and remaining checks.

The experimental helper now supports `--manage-gsx`: it waits for one explicitly selected physical GSX 300, holds its processing objects, observes both pages and reconnects when its endpoint pair changes. This extends the earlier [fresh-initialization experiment](gsx-initialization.md). It does not install a Windows service, change service startup or stop/restart audio. The normal WPF app uses the installed processor interface. Its separate Saved device processing drawer now shares the typed saved-state format and explicit opt-in, but does not launch this helper or create missing objects. See [device processing restore](../user-guide/processing-state.md).

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

The managed hardware pilot below passed. This control-only pilot does not establish physical managed reconnect, cold boot, actual DSP response or differing saved-state precedence on hardware; isolated/native tests already cover the latter policy. Managed mode cannot be combined with audio-engine restart, fresh-initializer `-ReconnectGsx` or audio measurements. The separate `-ManagedReconnect` extension below observes a second connection of the same running managed helper.

## Managed hardware pilot on 2026-10-09

Administrator run `artifacts/gsx-initialization/20261009-173200-45067797` passed managed restoration, microphone/playback controls and recovery. Windows Audio stayed Running throughout; no restart, audio measurement or physical reconnect was requested. The helper retained existing complete objects (`CreatedFresh=false`) and reported generation 1, `Connected`, `AutomaticRestore=true` and `LastSavedProcessing`. Both pages matched the isolated opted-in saved record and baseline. The original record contained baseline values, so this proves the saved-state path and readback rather than differing saved-value precedence on hardware.

The EPOS service was Stopped before connection and through both microphone gate/filter/EQ and playback mode/EQ/reverb control/restoration checks. Both hardware control reports passed. The complete GSX and B20 buffers matched baseline while the managed helper remained connected.

Recovery observed the restarted vendor process holding all three GSX objects after **4.41 seconds**, while the managed helper also held them. Both managed GSX and read-only B20 helpers then exited successfully. The first final diagnostic snapshot had zero captured control differences; both services were Running, and the isolated saved record was unchanged. The runner compared both complete buffers after helper release.

Independent read-only post-release diagnostics and captures again found zero differences across Windows level/mute, microphone processing, playback processing and sidetone, with both complete 4096-byte buffers byte-identical to baseline. `independent-managed-assessment.json` checks paired readiness, saved record identity/values/timestamp, stopped-service control reports, concurrent vendor/helper handle ownership, successful helper exits, independent buffers and current service/helper state. Capture files `artifacts/gsx-managed-post-release-20261009.bin` and `b20-managed-post-release-20261009.bin` remain local.

Managed warm-session restoration and recovery are now verified. The normal WPF UI now exposes explicit paired saves and opt-in restore using existing objects; its separate live UI check is recorded below. Physical managed reconnect was still outstanding at this milestone and subsequently passed below. Fresh managed creation, cold boot, service-account/storage access and startup without the diagnostic seed remain outstanding.

## Live UI save and app-start restoration on 2026-10-09

After the user closed Timbre, the normal `dist` launch folder was updated with the verified `04ce3a3` build. All 15 app/helper files matched the tested build manifest, and the previous binaries were preserved locally. The reopened live window exposed **Saved device processing** on both GSX pages. This check used existing processor objects with EPOS support and Windows Audio running throughout; neither service was stopped or restarted.

**Save current processing** created the GSX's first typed saved pair with automatic restore off. Both pages matched the fresh live baseline. Through the UI, noise filtering was temporarily bypassed and the 64 Hz playback EQ band raised from 0 to +1 dB. Diagnostic readbacks confirmed both changes; the saved pair remained intact. **Restore saved processing** restored both pages, and the complete 4096-byte GSX and B20 buffers matched baseline exactly.

The startup checkbox was temporarily enabled. That preference change left the GSX buffer unchanged. The same two UI edits were then applied again, and only Timbre was closed/reopened. It restored the saved microphone/sound pair while B20 was the startup selection. Readbacks and both complete buffers again matched baseline. This establishes warm app-start restoration with the vendor processing interface available, not a Windows cold boot or independent processor initialization.

Automatic restore was returned to off, and the starting GSX pair was left saved. Final readbacks found zero differences across all captured endpoint levels/mutes, microphone/playback processing and B20 sidetone. Both full buffers matched baseline, all pre-existing user JSON files retained their hashes, and the final GSX record exactly matched its first save including metadata and preference. The GSX USB sidetone display also remained at its starting 34.1%; this UI observation is separate from the diagnostic/complete-buffer comparisons, which do not query GSX USB sidetone.

Local evidence is under `artifacts/gsx-ui-live-20261009-195017`: baseline, edited, manually restored, startup-restored and final diagnostics; first/opted-in/final saved records; user-file hashes; and the independent read-only `assessment.json`. The full captures are the corresponding `gsx-ui-*` and `b20-ui-*` files under `artifacts`. No microphone recording, generated sound, audio measurement, physical unplugging, Windows audio restart or reboot was required. The subsequent physical app reconnect check is below; helper reconnect, cold boot, service-account installation and startup without the diagnostic seed remain separate checks.

## Physical UI reconnect restoration on 2026-10-09

The physical GSX unplug/replug check passed automatic restoration of a deliberately different saved microphone/playback pair through the running Timbre UI. EPOS support and Windows Audio stayed Running with unchanged process IDs. No helper, service restart, audio-engine restart, generated sound, audio capture or reboot was used.

The original typed record, preference, complete buffers and captured controls were preserved first. Through the UI, microphone filtering was bypassed and the 64 Hz playback EQ band raised from 0 to +1 dB; this distinct pair was explicitly saved and opted into connection restoration. Active processing was then returned to the original values **without updating the saved target**. Both full buffers matched baseline before unplugging, so retaining the active state could not satisfy the test.

On removal, Timbre reported only the two B20 endpoints and selected B20 microphone. Independent discovery found no GSX endpoints, and B20's complete buffer and captured controls remained unchanged. When the same physical GSX returned, Windows reused both endpoint IDs. Timbre nevertheless reported restoration and applied the saved pair while B20 remained selected. Both processing pages matched the saved target; the complete GSX buffer changed only the verified filter-enable byte and the playback band's float. All unowned bytes and the entire B20 buffer were preserved. This validates the observed-absence connection reset on this installation, including reconnect with unchanged endpoint IDs.

Cleanup restored the exact original saved file, timestamp and opt-out preference, then explicitly restored its pair through the UI. Independent final diagnostics found zero captured control differences; both complete 4096-byte buffers matched their starting captures, and all pre-existing user JSON hashes were unchanged. GSX sidetone remained at its starting 34.1% in the UI; read-only diagnostics do not issue its USB status query. A local cleanup-script replacement error was corrected before these checks passed; no application source change was required.

Local evidence is under `artifacts/gsx-ui-reconnect-20261009-200706`: preparation, removal, returned and final diagnostics; original/test saved records; user-file/service baselines; and `assessment.json`. Complete captures are the corresponding `gsx-ui-reconnect-*` and `b20-ui-reconnect-*` files under `artifacts`. This proves physical **app** reconnect and differing saved-value precedence with the vendor interface available. The separate physical **managed-helper** reconnect with vendor support stopped subsequently passed below. Fresh managed creation, cold boot and service installation remain outstanding; control readback does not establish a new DSP/audio result.

## Managed physical reconnect preparation on 2026-10-09

`Test-GsxInitialization.ps1 -ManagedLifecycle -ManagedReconnect` prepares the same isolated opted-in baseline record and validates it without changing services or processing. Adding `-StopSuiteTemporarily` opts into the administrator physical reconnect test. Unlike `-ReconnectGsx`, the managed option starts the helper before unplugging and requires its first saved-state connection, independently observed device removal, a `WaitingForDevice` heartbeat with cleared endpoints/effects, and a second `Connected` generation with paired saved-state readback. Reusing the first readiness report cannot pass.

When a brief GSX interruption and physical unplug/replug are acceptable, leave Timbre and Gaming Suite controls idle and run in administrator PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\tools\Test-GsxInitialization.ps1' `
  -ManagedLifecycle -ManagedReconnect -StopSuiteTemporarily
```

Keep B20 connected. Unplug GSX only when the script asks, and reconnect only after it reports that **the helper observed removal**. Each physical step allows 60 seconds; helper evidence waits are bounded to ten seconds. Keep GSX connected through control checks and recovery. `-BuildDirectory` can select a separately verified matching build as in the earlier pilot.

The helper must remain alive and EPOS support stopped through both connections and the microphone/playback checks. Returned endpoint IDs may differ, but the physical USB pair must match the captured device. The isolated save uses baseline values; this tests the restoration path/readback rather than differing saved-value precedence. The runner requires the complete GSX buffer to match baseline after reconnect and B20's full buffer to remain unchanged while GSX is absent. Captured Windows level/mute settings are restored if USB reconnect resets them. Windows Audio remains running; USB sidetone is outside the read-only diagnostic comparison.

Recovery uses the established observed vendor-handle handoff, successful helper shutdown, exact before/after-release buffers, captured-control comparisons and unchanged isolated store. Missing removal/waiting/return/generation evidence fails the overall result even if control checks passed. The summary reports physical managed reconnect separately from read-only preparation, fresh creation and audio measurements. Preparation alone establishes no physical reconnect pass; the subsequent hardware result is below.

Normal offline/demo verification passed **596 regressions, 131 WPF scenarios, 75 fixture-free PowerShell checks and 9 USB guards**. Nine additional PowerShell cases cover mode separation, changed endpoint IDs, stale/non-numeric generations, helper waiting evidence, required removal/return flags and recovery failures. Both Windows PowerShell 5.1 and PowerShell 7 passed these pure checks. The runner parsed successfully; no app/core/helper binaries changed. Evidence: `artifacts/gsx-managed-reconnect-verification-20261009.log` and the corresponding `gsx-managed-reconnect-rules-ps51-20261009.json` / `-ps7-20261009.json`.

Read-only preparation on the real pair passed in `artifacts/gsx-initialization/20261009-202635-5e4ca896`. The validator read the isolated saved record successfully, both complete buffers matched baseline, final captured controls had zero differences and the isolated store was unchanged. Independent `preparation-assessment.json` also verified every pre-existing user JSON hash and both service process IDs/states unchanged. `ManagedReconnectRequested=true` selects the preparation mode; `ManagedReconnectRecoveryTested=false`, no physical observations and `AutomaticRestore=false` preserve its read-only scope.

## Managed physical reconnect passed on 2026-10-09

Administrator run `artifacts/gsx-initialization/20261009-202815-b2cf3cd3` passed physical managed reconnect, paired saved-state restoration, microphone/playback controls and recovery. EPOS support was stopped through both connections and the control checks; Windows Audio stayed running. No audio-engine restart, generated sound, microphone capture or reboot was requested. Both connections retained existing complete objects (`CreatedFresh=false`), so this is warm managed reconnect evidence.

The same helper reported generation 1 with `LastSavedProcessing`, then `WaitingForDevice` after physical removal with endpoints/effects cleared. B20's full buffer remained byte-identical while GSX was absent. After reconnecting the same physical pair, both endpoint IDs were reused; the helper still reported generation 2, `Connected`, `AutomaticRestore=true`, and both pages matching the isolated opted-in saved baseline. The complete GSX buffer matched baseline immediately after reconnect. The stored pair contained baseline values; this proves the saved-state path/readback, while differing saved-value precedence has separate app/private-object evidence.

Both stopped-service microphone and playback control/restoration reports passed. Captured Windows level/mute restoration was verified, and complete GSX/B20 buffers remained exact through checks and helper release. Recovery observed the current vendor process holding all three GSX objects after **4.85 seconds**, concurrently with our helper. Both GSX and read-only B20 helpers exited successfully; the first final diagnostic had zero control differences, both services were Running and the isolated record was unchanged.

Independent post-release diagnostics and complete captures again matched every captured starting control and both 4096-byte buffers. `independent-managed-reconnect-assessment.json` validates removal, both saved-pair generations, metadata/values in the isolated record, all eight stored/independent buffer comparisons, concurrent vendor/helper ownership and successful shutdown. The pre-existing user JSON files also matched the earlier preserved UI baseline hashes, including the original GSX opt-out record. No helper remained running. UTC timestamps in the saved file and validator output were compared as instants rather than requiring identical offset text.

Full independent captures are `artifacts/gsx-managed-reconnect-post-release-20261009.bin` and `b20-managed-reconnect-post-release-20261009.bin`. Raw device identities and reports remain local. This closes physical managed warm reconnect on this installation. Fresh managed creation, cold boot, installed background lifecycle/service-account storage, remaining fresh-session DSP effects and broader driver/PC coverage remain separate. Read-only diagnostics do not query GSX USB sidetone; this run does not establish a new sidetone or DSP measurement.

## Guided helper check UI on 2026-10-09

Both GSX pages now expose a collapsed **Device details → Experimental helper check** panel. Its explicit button launches the existing bounded managed runner in administrator PowerShell. The optional physical reconnect checkbox starts unchecked. The selected physical device is checked again before service changes; report directories must be new descendants of the local GSX evidence folder. App/helper/test core binaries must match, and source-runner availability is required. Portable packages do not enable this experiment.

The app refuses launch with pending edits, stops its own activity monitoring, and pauses competing controls, discovery/restoration and window closing until the runner completes recovery. Progress reflects the managed heartbeat; successful completion requires the selected physical identity, paired restoration/control checks, vendor ownership handoff, unchanged isolated saved state, exact GSX/B20 buffers through helper release, running services and no errors/control differences. A requested reconnect additionally needs removal, return and generation-2 evidence. Malformed/incomplete reports cannot produce a pass. The app releases only the process handle; it never kills the administrator runner during recovery.

Normal verification passed **624 regressions, 138 WPF scenarios, 78 fixture-free PowerShell checks and 9 USB guards**, with no build warnings. Twenty-eight new core checks cover literal process arguments, approval cancellation, binary/device guards, progress, malformed reports and strict recovery evidence. Seven WPF scenarios cover panel availability, draft preservation, duplicate/closing guards, paused automatic restoration, completion/failure and unchanged settings/stores. Three new pure PowerShell checks cover selected-device replacement and report-path containment/existing-directory refusal. Both PowerShell 5.1 and 7 passed. Evidence: `artifacts/gsx-pilot-ui-verification-20261009.log` and the rendered compact `gsx-helper-check-preview-20261009.png` with its passing scenario report. The separate verified build is `artifacts/gsx-pilot-ui-build-20261009`; the running normal app was left untouched.

Real read-only preparation using that build and the new explicit physical-device/report-directory arguments passed in `artifacts/gsx-initialization/ui-readonly-603060557dff4f1f9b949dd7a5588468`. The isolated saved record validated, both full buffers matched baseline after validation, final captured controls had zero differences, and the isolated file remained unchanged. Independent `preparation-assessment.json` also verified all pre-existing Timbre JSON hashes and both service process IDs/states unchanged. No service stop, control write, automatic restore, object creation, physical reconnect, audio measurement or reboot was requested.

The underlying managed administrator workflow has the hardware evidence above. At this preparation milestone, the new button's real elevation/launch path still needed a user-operated pilot; it subsequently passed below. These offline/demo and read-only checks do not establish that UI pilot, installed background operation, fresh managed creation or cold boot. See the [guided helper check guide](../user-guide/helper-check.md).

## Button-driven helper check passed on 2026-10-09

The user-operated ordinary helper check produced `artifacts/gsx-initialization/ui-pilot-d2517932d9264728818c185e4d6adf10` and passed on the verified `a80a354` app. Its selected physical device matched the GSX pair. EPOS support was stopped through paired saved-state restoration and microphone/playback control checks; Windows Audio stayed running. Reconnect, audio-engine restart, audio measurement and reboot were not requested. The managed helper retained existing objects (`CreatedFresh=false`), reported generation 1 / `Connected` / `LastSavedProcessing`, and read back both pages from the isolated opted-in baseline record.

Both stopped-service control reports passed with exact restoration and preservation of the other device/unowned fields. Recovery observed the current vendor process holding all three GSX objects after **4.76 seconds**, while the helper also held them. Managed GSX and read-only B20 helpers exited successfully. The first final diagnostic found zero captured control differences; complete GSX/B20 buffers matched before and after helper release, the isolated saved record was unchanged and both services were Running.

Independent read-only post-release diagnostics and captures also found zero control differences. `independent-ui-pilot-assessment.json` verifies six complete-buffer comparisons (stored and independent), isolated saved-pair values/identity/timestamp, concurrent vendor/helper ownership, successful helper shutdown and no remaining helper. Every pre-existing Timbre JSON hash matched the earlier read-only preparation baseline. Full independent captures are `artifacts/gsx-ui-helper-post-release-20261009.bin` and `b20-ui-helper-post-release-20261009.bin`; raw reports and device identities remain local.

This validates the ordinary button-driven administrator launch and runner recovery on this installation. The completion footer was not separately inspected; its strict report handling has offline/demo coverage. Optional physical reconnect through the button remains separate from the earlier runner reconnect pass. Everyday background startup/shutdown, fresh managed creation, cold boot, installed service-account storage and broader driver/PC coverage remain outstanding. Control readback does not establish a new DSP measurement, and read-only diagnostics do not query GSX USB sidetone.
