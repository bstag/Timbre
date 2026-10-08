# Tests

`Timbre.Tests` is a console regression harness, not an external test-framework project. Run the full offline/demo check from the repository root:

```powershell
.\tools\Test-App.ps1
```

The harness writes JSON/JUnit reports under ignored `artifacts/`. WPF scenarios are executed by the app's `--render-demo` mode with isolated demo state. The separate GSX probe runs offline command/descriptor guards. Native object tests use unique private names rather than real vendor objects.

Startup storage checks exercise the same `ApplicationStorage` factory used by the app: fresh Timbre data without importing or modifying prior EPOS-Control/executable data, existing-state reload, explicit directory overrides, and separate demo/render stores. They use temporary folders and never read the actual user's settings.

Playback diagnostic regressions cover the quiet stereo/eight-channel tone (front pair only), buffer continuity, and EQ measurement/restoration under capture failure, stale processing, changed volume and missing devices. `--playback-diagnostic-self-test` runs this focused offline group. Only `--hardware-playback-audio` or the scripts' `-HardwarePlaybackAudio` switch plays the tone and temporarily edits EQ; these are never part of the default offline run.

The focused group also checks physical-device binding and stopped-service context with fake callbacks. `tools/Test-GsxInitializationChecks.ps1`, included in `Test-App.ps1`, exercises the fresh runner's option guards, playback evidence requirements and combined outcomes using synthetic reports. It runs in Windows PowerShell 5.1 and PowerShell 7 without hardware or ignored historical captures; its report is `artifacts/gsx-initialization-checks.json`.

See [building and testing](../docs/development/building-and-testing.md) for prerequisites and [protocol/test coverage](../docs/development/microphone-protocol-and-tests.md) for semantics. Optional hardware arguments are separate from the default command.

[Fixtures](Timbre.Tests/Fixtures/README.md) contain reviewed control-state evidence, topology, screenshots, lookup maps, and hashes. Preserve their provenance and do not automatically regenerate the manifest. Historical reports under `artifacts/` are local investigation output, not required clean-checkout test inputs.
