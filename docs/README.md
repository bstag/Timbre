# Documentation

Start with [using the app](user-guide/using-app.md) or [building and testing](development/building-and-testing.md). [Validation status](validation/status.md) summarizes current capabilities and remaining checks as of 2026-10-08.

Read [project background and goals](project-background.md) for why this exists: continued use of EPOS hardware, recovering missing capabilities, learning Windows audio internals, and maintaining a practical tool with coding agents.

## User guides

- [Using the app](user-guide/using-app.md): navigation, live edits, profiles, and storage.
- [Device profiles and live controls](user-guide/profiles-and-live-controls.md): explicit saves, setup membership, and compatibility.
- [Live microphone view](user-guide/live-microphone-view.md): activity/EQ display and capture-quality meaning.
- [Live playback view](user-guide/live-playback-view.md): opt-in output activity.
- [Device processing persistence](user-guide/processing-state.md): B20 last-applied state, GSX paired saves and opt-in restoration.
- [Control diagnostics](user-guide/control-diagnostics.md): availability and failure reasons.
- [Guided GSX helper check](user-guide/helper-check.md): explicit administrator test sessions, progress, reports and recovery.
- [Background support session](user-guide/background-support.md): explicit supervised startup/stop, latest-setting preservation and recovery.

## Development

- [Building and testing](development/building-and-testing.md): source checkout prerequisites and verification commands.
- [Architecture and extension guide](development/app-design.md): modules, adapters, UI behavior, and device extensions.
- [Microphone protocol and regression tests](development/microphone-protocol-and-tests.md): captures, coverage, and fixture maintenance.
- [Release preparation](development/releasing.md): portable ZIPs, CI, and first-push checklist.
- [Contributing](../CONTRIBUTING.md): change boundaries and review expectations.

## Research

- B20: [sidetone](research/b20-sidetone.md), [pickup pattern](research/b20-pickup-pattern.md), and [feature findings](research/b20-feature-findings.md).
- Processing helper: [object retention](research/apo-control-host.md), [B20 initialization](research/apo-initialization.md), and [managed B20 lifecycle](research/b20-host-lifecycle.md).
- GSX 300: [microphone controls](research/gsx-microphone-controls.md), [playback controls](research/gsx-playback-controls.md), [sidetone/reverb evidence](research/gsx-remaining-controls.md), [initialization](research/gsx-initialization.md), and [managed processing lifecycle](research/gsx-host-lifecycle.md).
- Background: [initial local investigation](research/local-investigation.md) and [official-source findings](research/official-sources.md).

## Validation

- [Current status](validation/status.md): current verified scope and remaining work.
- [Home validation checklist](validation/home-validation.md): listening, reconnect, and startup results/checks.
- [GSX microphone routing session](validation/gsx-microphone-routing.md): independent routing evidence from 2026-10-06.
- [Live UI audit](validation/live-ui-audit-20261006.md): dated usability findings and fixes.

## Roadmap

- [Installer and dependency plan](roadmap/installer-and-dependencies.md): proposed installation lifecycle and remaining EPOS dependencies.
- [Completed remote-work pass](roadmap/remote-completion-plan.md): historical scope/results of the 2026-10-07 remote audit.

## Reading evidence

Research and validation files retain dated observations, failed experiments, and milestone-specific test counts. Read their current-status links before treating an older result as a present limitation. Settings-interface readback, manual listening, audio measurements, reconnect, and cold boot are separate evidence categories.

Paths under `artifacts/` and `preservation/` refer to ignored local evidence; those files are not included in a source checkout. Reviewed fixtures remain in [the fixture inventory](../tests/Timbre.Tests/Fixtures/README.md). See [third-party notices](../THIRD-PARTY-NOTICES.md) for provenance and licensing scope. In a portable ZIP, references to source-only files are labeled as source-checkout references.
