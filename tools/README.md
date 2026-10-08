# Tools

Run scripts from the repository root in 64-bit PowerShell. Normal source verification needs no EPOS hardware or vendor service changes.

## Build and distribution

| Script | Purpose |
| --- | --- |
| `Build-App.ps1` | Build app/helper into ignored `dist`; `-RunTests` also runs regressions. |
| `Test-App.ps1` | Build and run offline regressions, probe guards, and demo WPF scenarios. Hardware flags are explicit opt-in. |
| `Test-Documentation.ps1` | Check local Markdown file links in source or a portable package. |
| `Package-App.ps1` | Verify source by default, prepare ZIP, hash payload, and check extraction/docs. |
| `portable/` | Templates copied to the extracted ZIP root, including `START-HERE.md` and verification/launch scripts. |

See [building/testing](../docs/development/building-and-testing.md) and [releasing](../docs/development/releasing.md).

`Test-App.ps1 -HardwarePlaybackAudio` explicitly opts into an approximately eight-second quiet GSX front-pair tone and flat/+6 dB/flat 1 kHz playback EQ comparison. It restores the original playback curve/reverb, checks Windows controls and B20 preservation, and saves metrics only. No services/default devices change and no reboot is needed. Keep other playback and controllers idle. An unchanged/unstable/interrupted loopback measurement is inconclusive, not proof that the device's EQ is broken. The same switch is available in portable `Verify-Setup.ps1`; default verification remains offline/demo plus its existing read-only diagnostics.

## Hardware and service experiments

`Test-ApoHost.ps1`, `Test-GsxInitialization.ps1`, and `Test-SuiteDependencies.ps1` support bounded experiments with explicit opt-in switches. Depending on arguments, they can stop services, change controls, or restart Windows Audio. Read the [B20 helper](../docs/research/apo-control-host.md), [GSX initialization](../docs/research/gsx-initialization.md), or [dependency plan](../docs/roadmap/installer-and-dependencies.md) first and preserve a fresh baseline.

`Test-ManagedHostProcess.ps1` checks an intentionally absent B20 target without hardware writes. `Test-GsxPreparedHost.ps1` exercises prepared discovery/refusal paths; consult its parameters and GSX guide before running it.

`Start-B20ReconnectCheck.ps1` launches a previously prepared administrator pilot. It requires ignored preparation/report inputs under `artifacts/apo-host` and validates saved-state identity/hash. A fresh checkout does not contain those inputs. `Test-ApoRecoveryChecks.ps1` replays a specific historical report set; it is not part of the clean-checkout suite.

## Investigation utilities

- `Inspect-Epos.ps1` and `Preserve-Epos.ps1`: local inventory and preservation. Preservation output stays ignored.
- `Capture-Apo.ps1`, `Recover-ApoInitializer.ps1`, `Find-PeReferences.ps1`, `AssemblyInspector/`, and the C# inventory/probe files: capture/static investigation support; results belong in local artifacts with provenance.
- `Get-ApoHandleOwners.ps1`: bounded read-only object-holder diagnostics.
- `Read-B20InputState.ps1` and `Read-B20SynchronousStatus.ps1`: experimental pickup-status queries; they send USB requests.
- `GsxSidetoneProbe/`: guarded research query/setter; `--self-test` is offline and is included in normal verification.

Diagnostics can contain machine/device identifiers. Add only reviewed, sanitized evidence to [fixtures](../tests/Timbre.Tests/Fixtures/README.md), and preserve pinned hashes. A captured value, live readback, listening result, and startup/DSP result establish different things.
