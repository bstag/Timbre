# B20 shared-object initialization

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

Implemented 2026-10-06 as an explicit console-helper experiment. The existing EPOS driver and Windows-hosted `EAPO.dll` remain in use. This is not an installed background service or proof of reboot/reconnect independence.

## Evidence

The initialization milestone passed **234 regression checks and 19 WPF scenarios**, without warnings. Thirty-six checks were added beyond the retention milestone. Native tests use unique `Local\Timbre.Tests.*` names, never actual EPOS objects. They verify fresh memory byte for byte, object ACLs, manual-reset event behavior, all six partial-set combinations, incompatible layout/type rejection, locking, lifetime and device-bound startup input. The real control transport applies/restores settings against the isolated native objects while preserving unowned bytes. The subsequent [processing persistence milestone](../user-guide/processing-state.md) brought source to 258 checks and 25 WPF scenarios; the subsequent [managed lifecycle](b20-host-lifecycle.md) brought that milestone to 286 checks and 25 scenarios.

The revised read-only live check passed: `artifacts/apo-host/20261006-113517-d3f65e93`. Fresh startup also correctly refused the running EPOS service without readiness or setting writes: `artifacts/apo-host/initializer-guard-cfc1392c40774faa84550a99ac05713b`.

**Fresh creation on the real B20 passed on 2026-10-06.** Reports: `artifacts/apo-host/20261006-114006-e916489e`. The pre-initialization snapshot recorded no processing interface (`No handle of the given name exists`) while the service was stopped and our host absent. Our host then reported `Mode: CreateFreshB20Objects` and `CreatedFresh: true`. Gate/filter/high-pass/EQ control readback passed with the service still stopped. Every starting processing/sidetone/endpoint setting matched after recovery; the service/UI were restored and our helper shut down cleanly. There were no errors or warnings.

This proves fresh shared-object creation and control operation in this warm Windows session, using the explicit device-matched diagnostic seed. Audio was not requested in this run. DSP output, standalone startup without a diagnostic seed, cold boot and reconnect remain unproven. The earlier retained-object audio attempts remained inconclusive. See [those results](apo-control-host.md#service-stopped-experiment-2026-10-06).

## Golden and ownership

`tools/Recover-ApoInitializer.ps1` verifies the inspected service image's hash, then reads its static instructions and −120 float constant without loading/executing vendor code. It independently recovers all 60 initializer writes. Reviewed output is in `tests/Timbre.Tests/Fixtures/apo-cold-initializer-009f.bin` and `.json`, both protected by the fixture SHA256 manifest. Binary hash: `2B358E0D9F812EF8E26FC061A8A023DC55B07566B8AB6F4E7AD6EEBE7AE153F4`.

This golden represents the recovered initializer on a fresh zeroed page, **not a live cold-start capture**. Logical size is 2112 bytes; the mapped view is 4096 bytes. [Native defaults and source addresses](apo-control-host.md#native-initialization-recovered-for-the-next-milestone).

`WindowsApoObjectHost` creates native objects using the recovered descriptor. It owns a newly created mutex initially; it acquires an existing mutex with a bounded wait. Empty sets are initialized. Complete compatible sets can be reused unchanged in the private test interface. Partial sets, type collisions and incompatible layouts are rejected without repair or existing-state writes. Failed startup releases our lock, views and handles.

The explicit live B20 mode is stricter: Windows x64, exactly one physical B20, vendor service stopped or absent, and **fresh objects required**. Existing objects cause refusal. The global mapping needs the appropriate Windows privilege, supplied by the administrator experiment. Permission failures are reported. API behavior is documented by Microsoft: [CreateFileMappingW](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw), [MapViewOfFile](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-mapviewoffile), and [CreateMutexExW](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createmutexexw).

Kernel inspection confirms only Builtin Users and Local Service trustees. Windows limits the legacy `FA` mask to each object type: mutex `0x1F0001`, event `0x1F0003`, mapping `0xF001F`. ACL inspection requires a handle including read-permission access.

## Startup preservation

`ApoStartupState` validates schema, physical/profile identity, complete processing settings and the diagnostic layout. Missing/unknown fields, legacy incomplete state, invalid values, mismatched devices and oversized files are rejected before creation.

The live experiment captures the current typed state and a diagnostic layout before stopping the service. Captures contain unmapped configuration, including bytes 65, 70, 72 and the field at 164. Resetting those to defaults could change behavior outside our mapped controls. The diagnostic seed preserves the logical structure **only in newly created memory**, then signals initialization. It never overwrites reused memory. The normal Apply transaction still checks typed settings and readback.

This device-matched snapshot is explicit experiment/recovery input saved with that run. It contains opaque/status fields and is **not the long-term settings format**. User profiles are unchanged. A separate [typed processing store and app restore policy](../user-guide/processing-state.md) are now integrated with [managed helper lifecycle](b20-host-lifecycle.md). Managed mode passes isolated/native, process and actual B20 fresh-creation/saved-restore/control checks. Subsequent physical managed reconnect and warm-session gate audio passed. Unmapped-field identification and cold-start validation remain future work; see [current status](../validation/status.md).

## Repeating the fresh-object experiment

In administrator PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\tools\Test-ApoHost.ps1' -StopSuiteTemporarily -FreshInitialization
```

The script saves current state, closes the Suite UI, stops its service and records the pre-initialization snapshot. **Then** it launches fresh creation; no helper holds vendor objects before the stop. Readiness must report `CreatedFresh: true`. It runs the existing control transaction and restores its state, restarts the original service/UI, releases our handles and compares all endpoints with baseline. Failure paths also attempt service/UI restoration. No reboot, driver removal or permanent startup-policy change occurs.

Add `-HardwareAudio` when steady input is available. Signal-quality thresholds remain unchanged. A fresh-object control pass does not establish actual DSP output or a true cold boot; reports retain `ColdStartTested: false` and `ReconnectTested: false` until those conditions are exercised.

Reports are in `artifacts/apo-host/<UTC timestamp>-<id>`: startup input, pre-initialization snapshot, readiness/shutdown, hardware checks, recovery snapshots and summary. Existing objects or failed creation produce refusal, not a successful fresh-start claim.
