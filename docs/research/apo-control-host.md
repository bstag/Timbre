# Replacing Gaming Suite's processing control service

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

Status: experimental B20 object-retention helper implemented on 2026-10-05; explicit fresh-initialization and [managed lifecycle](b20-host-lifecycle.md) modes were added on 2026-10-06. It is a bounded console process, not an installed Windows service. Windows continues to host the installed `EAPO.dll`; our helper does not load the DLL or process audio samples.

## Implemented milestone

In retention mode, `src/Timbre.Host` opens the existing B20 shared memory, mutex and notification event, validates the mapped settings, and keeps those handles open. It releases the mutex after each read so the existing control app and processor can continue using it. This mode never creates missing objects, initializes memory, changes settings, signals/resets the event, or restores a profile. It exits on its stop file, Ctrl+C, a deadline, disconnected/replaced device identity, unavailable state or incompatible memory.

The new [fresh-initialization mode](apo-initialization.md) is explicit and experimental. It passes isolated native tests, a live running-service refusal check, and actual B20 fresh-object creation/control checks with the service stopped. The [managed pilot](b20-host-lifecycle.md) also passes real B20 fresh creation, typed saved-state restoration and service-stopped control checks, with all initial settings/service/UI restored. That milestone passed 286 regression checks and 25 WPF scenarios. Subsequent B20 physical helper reconnect and service-stopped gate audio passed with exact restoration; see [the later lifecycle results](b20-host-lifecycle.md). Cold boot and startup without a diagnostic seed remain unvalidated.

Retention and fresh-only modes require exactly one active B20 microphone. Managed mode can wait for an explicitly specified physical instance. The global object names contain VID/PID without a serial number; the same-model ambiguity guard remains necessary. GSX 300 retention and advanced processing are outside this first adapter. The EPOS driver, processor and Gaming Suite service remain installed and enabled.

The app writes its [last-successful processing store and restore preference](../user-guide/processing-state.md). Managed helper mode now reads that store and honors per-device opt-in once per connection; healthy polling preserves later edits. Explicit profiles remain separate. Fresh creation still requires diagnostic startup input to preserve unmapped configuration. See [managed lifecycle behavior and validation](b20-host-lifecycle.md).

## Tests and local evidence

All **198 regression checks and 19 WPF scenarios passed**, with a warning-free build. Fourteen new checks cover B20-only naming, read-only retention, signalled-event preservation, control updates from another thread, multiple owners, final handle release, disposal, missing/partial objects, invalid layouts, native mapping size, timeout, abandoned mutex cleanup and retention by a separate process. Windows object tests use unique `Local\Timbre.Tests.*` names and captured fixtures; they do not open actual EPOS objects.

The live read-only experiment passed with Gaming Suite running:

`artifacts/apo-host/20261006-012543-9c9c2e5a/summary.json`

The helper became ready, processing remained readable, the helper shut down cleanly, and every mapped processing setting, sidetone state and Windows endpoint level/mute matched baseline. The service remained running. This first run established coexistence.

### Service-stopped experiment, 2026-10-06

The user ran the administrator experiment with `-StopSuiteTemporarily -HardwareAudio`. Reports: `artifacts/apo-host/20261006-110831-9524dbb8`.

| Check | Result |
| --- | --- |
| Processing interface with Suite UI closed/service stopped and our helper running | Available |
| Gate/filter values, enable switches, high-pass and nine-band EQ control transaction | Passed readback; original values restored |
| Actual gate audio on a new capture stream | Inconclusive: ungated RMS changed from −46.52 to −55.45 dBFS. Gated RMS was −99.96 dBFS, a conservative 44.50 dB drop, but unstable input prevents a formal pass. No clipping, invalid samples or capture glitches were recorded. |
| End-of-run state | No processing, sidetone or endpoint level/mute differences from baseline; service running, Suite UI restored, helper shut down successfully |

This confirms warm-session control operation with the vendor service stopped. Actual DSP verification still needs a steady-input repeat. Cold-start object creation, automatic persistence, reconnect and reboot independence remain untested.

The original `summary.json` records `Passed: false` because the old script put the inconclusive audio result in `Errors` and threw an exception after successful restoration. That original report is preserved. The script now separates `ControlReadbackOutcome`, `AudioOutcome`, `OverallOutcome` and `Warnings`. Inconclusive audio keeps the overall outcome inconclusive and returns exit code **2** without a red exception; genuine failures still fail. Five isolated replays of the actual outcome/exit logic passed, including failure and settings-difference precedence; see `artifacts/apo-host/reporting-replay-results.json`.

A second live run, `artifacts/apo-host/20261006-111358-7c4b2728`, again passed service-stopped control checks and restored every starting setting, the service and Suite UI. The helper shut down cleanly. The updated summary correctly reports controls `Passed`, audio/overall `Inconclusive`, no errors and one warning. This time input was steadier but too quiet: ungated RMS was −80.94/−82.81 dBFS, and gated RMS was −132.38 dBFS. The measured drop of 49.56 dB remains insufficient for a formal pass when the reference input is this weak. Keep the existing signal-quality checks; repeat audio later with an adequate, steady background signal at the microphone. This does not block developing cold-start initialization in isolated tests.

Build and run the read-only experiment:

```powershell
.\tools\Test-App.ps1
.\tools\Test-ApoHost.ps1
```

Repeating the service-stopped audio experiment requires administrator PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\tools\Test-ApoHost.ps1' -StopSuiteTemporarily -HardwareAudio
```

The script saves a baseline, starts the helper and verifies readiness, closes the Suite UI and stops its service temporarily. It checks processing availability, runs existing B20 gate/filter/switch/EQ control transactions with restoration, and optionally measures gate attenuation on a newly opened Windows capture stream. It then restarts the original service/UI before releasing our handles and compares all endpoints with baseline. Failures are collected in `summary.json`; recovery reports are retained. An audio result can be inconclusive with silence, clipping, glitches or unstable input. Gate audio verification does not verify filtering/EQ by listening.

There is no reboot, uninstall, startup-policy change, driver replacement, permanent service stop or profile auto-apply. Without `-StopSuiteTemporarily`, the script leaves the vendor service/UI running and does not run settings-write or audio tests. Reports go to `artifacts/apo-host/<UTC timestamp>-<id>`.

## Native initialization recovered for the next milestone

These are static findings from the installed PE32 `C:\Program Files (x86)\EPOS\Gaming Suite\EPOSGamingSuiteService`:

SHA256 `350D5EC4DEA1F6440C83DA82A0155AF1D7ACA02250E3F31EB7E3EBF472B8DD44`.

Addresses refer to this exact image. Evidence is in `artifacts/service-disassembly.txt`, `artifacts/apo-init-native-references.json`, and `artifacts/apo-create-disassembly.txt`. Those files are local investigation artifacts, not vendor APIs or redistributable runtime dependencies.

| Recovered operation | Static evidence |
| --- | --- |
| Pagefile-backed shared memory | Constructor `0x00553540`; `CreateFileMappingW` at `0x005535C4`; size `0x840` = **2112 bytes**; protection `0x08000004` |
| Mapped view | `MapViewOfFile` at `0x00553646`, requested length 2112. Our private Windows test confirms the full accessor exposes a **4096-byte view**, matching live captures. Logical structure size and mapped view capacity are separate. |
| Notification event | Constructor `0x005547A0`; `CreateEventExW` at `0x00554808`; flags 1 = manual reset, initially nonsignalled |
| Mutex | Constructor `0x00554890`; `CreateMutexExW` at `0x005548F8`; flags 1 = initial ownership for a newly created mutex |
| Object security | Constructor `0x00554710`; SDDL string at `0x007B9218`: `D:(A;OICI;FA;;;BU)(A;OICI;FA;;;LS)`. Record the legacy descriptor; design and validate a replacement service's account/access before fresh production creation. Retention leaves the existing descriptor intact. |
| Fresh versus existing state | Shared-state constructor `0x005536E0` detects reused memory, returns a distinct status and skips the fresh-memory initializer. Fresh state calls `0x00553D10`; later listener restore is a separate step. Never overwrite existing state as a startup shortcut. |

Initializer `0x00553D10` writes these defaults:

| Offset(s), decimal | Type / initial value |
| --- | --- |
| 0, 4 | Int32: 2, 4 |
| 64–72 | Bytes: 0 |
| 76–152, step 4 | Float32: 0 |
| 156–172, step 4 | Int32: 0 |
| 176, 177 | Bytes: 0 |
| 180 | Int32: **2**, not zero; semantic meaning unconfirmed |
| 184–186 | Bytes: 0 |
| 1088–1144, step 4 | Float32: **−120**, read from constant at `0x007B9930` |
| 1148, 1152, 1156 | Float32: 0 |

The preceding call to `0x0048DD70` returns the supplied pointer; it does **not** clear the whole structure. Do not infer extra initializer writes in the reserved gaps or treat this as a live cold-start golden. These findings drive our initializer. Actual B20 creation now passes with diagnostic state replay; pure defaults on hardware remain unvalidated.

## Remaining implementation

1. Retained and freshly created B20 interfaces pass control transactions with the vendor service stopped. The subsequent managed pilot passed gate audio and physical reconnect with exact restoration; preserve a fresh baseline when repeating those checks on another installation.
2. Fresh creation, independent static goldens and failure/lifetime tests are implemented. The [explicit B20 creation experiment](apo-initialization.md#repeating-the-fresh-object-experiment) and later managed reconnect/audio pilot pass. Validate cold boot and startup without a diagnostic seed separately.
3. The app's [last-successful typed settings store and opt-in restore policy](../user-guide/processing-state.md) are integrated with [managed helper lifecycle](b20-host-lifecycle.md). Isolated/native restoration, executable wait/ownership/guard checks and the administrator B20 creation/restore/control pilot pass. Physical reconnect and warm-session gate audio subsequently passed; cold startup while preserving unmapped configuration remains to be validated.
4. Managed B20 mode includes absent-device waiting, reconnect handling, single-owner coordination, bounded health checks and shutdown. Cold boot remains unvalidated. A separate [GSX initializer](gsx-initialization.md) now passes a fresh-object control pilot; managed GSX reconnect and fresh-session audio remain separate work.
5. Choose and install a background service only after those responsibilities work. Its lifecycle, storage/access permissions and recovery must be tested before retiring the vendor service. Keep the existing driver/APO package available for reinstall.

Warm retention, fresh creation and B20 managed reconnect/audio have removed several obstacles. Cold-start independence, replacing EPOS DSP, and the installer remain separate work. See [installer and dependency plan](../roadmap/installer-and-dependencies.md).
