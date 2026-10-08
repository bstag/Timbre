# Contributing to EPOS Control

Start with the [architecture guide](docs/development/app-design.md) and [build/test instructions](docs/development/building-and-testing.md).

## Development checks

From 64-bit PowerShell on Windows with a .NET 9 SDK and Desktop Runtime:

```powershell
.\tools\Test-App.ps1
```

This is the normal pre-commit check. Hardware flags, service experiments, and capture diagnostics are separate opt-in workflows. CI runs the offline/demo path on a Windows runner; its results cannot validate physical hardware.

## Change boundaries

- Put device I/O and protocol rules in core adapters. Keep WPF handlers focused on UI coordination. Add new device capabilities only with model-specific evidence.
- Preserve complete unowned state and device/page isolation. Live writes need identity checks, stale-state handling, readback, and explicit recovery outcomes.
- Keep profile saves explicit, capture opt-in, and automatic restoration governed by the existing opt-in policy.
- Add focused regression coverage for behavior changes and failures. Documentation-only changes need link/package verification rather than new behavior tests.
- Preserve fixture provenance and pinned hashes. Do not replace evidence with app-generated output or automatically refresh the fixture manifest. See [fixture maintenance](docs/development/microphone-protocol-and-tests.md#fixture-maintenance).

## Documentation and pull requests

Update the relevant user/development guide when behavior changes. Keep dated investigation results in research/validation, and link them from the current [status](docs/validation/status.md). Historical test counts describe their milestone, not the current suite.

Describe the problem, resulting behavior, validation, and remaining limitations. Separate control readback from listening or DSP measurements. Record hardware results with device, driver, service/helper state, restoration outcome, and test date.

## Files to keep local

Build outputs, raw investigation reports, preservation copies, and user profiles are ignored. Do not force-add them. Diagnostics can contain physical device identifiers. Commit only reviewed fixtures, source, scripts, and documentation. Keep credentials and machine-specific paths out of examples.

When documentation moves, update links, the documentation index, fixture references, and portable packaging. See [release preparation](docs/development/releasing.md) before preparing a ZIP or pushing the first commit.
