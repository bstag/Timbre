# Managed GSX processing lifecycle

Updated 2026-10-08. See [current validation status](../validation/status.md) for hardware evidence and remaining checks.

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
