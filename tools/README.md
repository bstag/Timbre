# Tools

Run scripts from the repository root in 64-bit PowerShell. Normal source verification needs no EPOS hardware or vendor service changes.

## Build and distribution

| Script | Purpose |
| --- | --- |
| `Build-App.ps1` | Build app/helper into ignored `dist`; `-RunTests` also runs regressions. |
| `Test-App.ps1` | Build and run offline regressions, probe guards, and demo WPF scenarios. Hardware flags are explicit opt-in. |
| `Test-GsxInitializationChecks.ps1` | Fixture-free offline option/evidence/outcome checks for the fresh GSX runner; included in normal source verification. |
| `Test-Documentation.ps1` | Check local Markdown file links in source or a portable package. |
| `Package-App.ps1` | Verify source by default, prepare ZIP, hash payload, and check extraction/docs. Build/test/package accept `-OutputDirectory` to keep the running `dist` app open. |
| `portable/` | Templates copied to the extracted ZIP root, including `START-HERE.md` and verification/launch scripts. |

See [building/testing](../docs/development/building-and-testing.md) and [releasing](../docs/development/releasing.md).

`Test-App.ps1 -HardwarePlaybackAudio` explicitly opts into an approximately eight-second quiet GSX front-pair tone and flat/+6 dB/flat 1 kHz playback EQ comparison. It restores the original playback curve/reverb, checks Windows controls and B20 preservation, and saves metrics only. No services/default devices change and no reboot is needed. Keep other playback and controllers idle. An unchanged/unstable/interrupted loopback measurement is inconclusive, not proof that the device's EQ is broken. The same switch is available in portable `Verify-Setup.ps1`; default verification remains offline/demo plus its existing read-only diagnostics.

## Hardware and service experiments

`Start-SupportSession.ps1` is the explicit app-owned background supervisor. `-PrepareOnly` validates fresh device snapshots/stores and checks preservation without stopping services or launching hosts. Live mode requires administrator approval, the original controller PID/start instant, selected GSX/B20 identities and caller storage. It preserves latest applied settings on cooperative stop/controller exit instead of running the test runner's baseline-restoration semantics. The source UI supplies these arguments; no sign-in registration or installed service is added. See the [background support guide](../docs/user-guide/background-support.md). `SupportSessionChecks.ps1` supplies shared pure rules included in normal verification.

`Test-ApoHost.ps1`, `Test-GsxInitialization.ps1`, and `Test-SuiteDependencies.ps1` support bounded experiments with explicit opt-in switches. Depending on arguments, they can stop services, change controls, or restart Windows Audio. Read the [B20 helper](../docs/research/apo-control-host.md), [GSX initialization](../docs/research/gsx-initialization.md), or [dependency plan](../docs/roadmap/installer-and-dependencies.md) first and preserve a fresh baseline.

For playback DSP after fresh initialization, `Test-GsxInitialization.ps1 -StopSuiteTemporarily -RestartAudioEngine -HardwarePlaybackAudio` requires administrator PowerShell and briefly interrupts all PC audio. It binds the quiet tone/EQ measurement to the captured GSX, checks stopped vendor support throughout capture and reports `PlaybackAudioOutcome` separately. No microphone input, unplugging or reboot is required. Restoration failures remain failures; inadequate capture remains inconclusive. The fresh playback EQ measurement passed on hardware, but that run lost its GSX processing interface after helper release and remains failed overall; consult the GSX guide before repeating it.

`Restore-GsxVendorSupport.ps1 -Baseline PATH` is a bounded administrator recovery for that missing-interface condition. It validates the same GSX pair, holds B20's existing objects read-only, restarts only EPOS support if needed and reports baseline differences without direct processing writes. It leaves Windows Audio running and does not change the original failed initialization reports.

The GSX initializer now waits up to 45 seconds for actual vendor handle ownership before helper release, instead of relying on service Running alone. Its corrected hardware run passed, with ownership observed after a 4.62-second wait and exact post-release restoration. The recovery script observes asynchronous service completion for up to 60 seconds while retaining B20; exceptions still fail its result. The first recovery timed out but later independent captures confirmed exact recovery; the separately modified recovery script's delayed-transition path still needs a new hardware run. Neither script is an installed background helper.

`Test-ManagedHostProcess.ps1` checks an intentionally absent B20 target without hardware writes; `-Device GSX300` exercises the GSX managed helper instead. Both check waiting, competing-owner refusal and prompt stop/restart, with no vendor-object creation or service changes. See [managed GSX processing](../docs/research/gsx-host-lifecycle.md) for typed saved state and the separate bounded helper. `Test-GsxPreparedHost.ps1` exercises prepared discovery/refusal paths; consult its parameters and GSX guide before running it.

`Test-GsxInitialization.ps1 -ManagedLifecycle` performs read-only preparation with an isolated copy of the current GSX saved settings. Adding `-StopSuiteTemporarily` explicitly opts into the administrator managed-restoration/control/recovery pilot; Windows Audio remains running, and no audio measurement, unplugging or reboot is needed. It requires paired saved-state readback, actual vendor handle ownership before helper release, complete buffer preservation and unchanged saved state. It refuses combinations with the restart/fresh-reconnect/audio switches. A separate verified build can be selected using `-BuildDirectory`, prepared with `Test-App.ps1 -OutputDirectory`; both use `dist` by default. Read [the pilot guide](../docs/research/gsx-host-lifecycle.md#managed-pilot-preparation-on-2026-10-09) before service-stop testing.

Adding `-ManagedReconnect` to the managed pilot selects its separate physical unplug/replug check. Without `-StopSuiteTemporarily`, it still performs only read-only preparation. The administrator test prompts for unplugging after initial helper readiness and permits reconnecting only after the helper reports removal. It requires generation-two saved-pair readback, complete buffer preservation and the same recovery checks. No Windows Audio restart, tone or reboot is requested. Keep B20 connected and GSX connected through recovery. See [managed reconnect preparation](../docs/research/gsx-host-lifecycle.md#managed-physical-reconnect-preparation-on-2026-10-09).

The source app's [guided helper check](../docs/user-guide/helper-check.md) launches this managed administrator runner with `-ExpectedDeviceInstance` and a fresh `-ReportDirectory`. A changed selected USB instance is refused before service changes. Explicit report paths must be new and within the checkout's `artifacts/gsx-initialization` directory; existing/outside paths are refused. Ordinary command-line runs can omit both options and keep their original discovery/generated-report behavior.

`Start-B20ReconnectCheck.ps1` launches a previously prepared administrator pilot. It requires ignored preparation/report inputs under `artifacts/apo-host` and validates saved-state identity/hash. A fresh checkout does not contain those inputs. `Test-ApoRecoveryChecks.ps1` replays a specific historical report set; it is not part of the clean-checkout suite.

## Investigation utilities

- `Inspect-Epos.ps1` and `Preserve-Epos.ps1`: local inventory and preservation. Preservation output stays ignored.
- `Capture-Apo.ps1`, `Recover-ApoInitializer.ps1`, `Find-PeReferences.ps1`, `AssemblyInspector/`, and the C# inventory/probe files: capture/static investigation support; results belong in local artifacts with provenance.
- `Get-ApoHandleOwners.ps1`: bounded read-only object-holder diagnostics.
- `Read-B20InputState.ps1` and `Read-B20SynchronousStatus.ps1`: experimental pickup-status queries; they send USB requests.
- `GsxSidetoneProbe/`: guarded research query/setter; `--self-test` is offline and is included in normal verification.

Diagnostics can contain machine/device identifiers. Add only reviewed, sanitized evidence to [fixtures](../tests/Timbre.Tests/Fixtures/README.md), and preserve pinned hashes. A captured value, live readback, listening result, and startup/DSP result establish different things.
