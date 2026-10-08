# Read-only control diagnostics

The Diagnostics button and `Timbre.exe --diagnostics report.json` export the same control availability report. The portable verifier saves it as `reports/<run>/devices.json` and prints each endpoint's availability. Missing controls have startup, permissions, busy-interface or incompatible-state reasons.

Schema version 1 retains top-level `Endpoints` and adds `Controls`, keyed by endpoint ID. Each row includes a fresh Windows audio read, model-appropriate microphone/playback/sidetone probes and feature availability. Each probe has `Status`, `Value`, `ErrorType` and `Reason`:

| Status | Meaning |
| --- | --- |
| Available | The mapped adapter returned valid current state. |
| Unavailable | A mapped adapter failed to read; the reason identifies the failure. |
| NotSupported | The control does not apply to this model/page, or requires an explicit USB query omitted by diagnostics. The reason distinguishes these cases. |

Export does not apply profiles, restore saved state, change settings, send hardware-status USB queries or start audio capture. Its three operation flags are false. Reports can contain local endpoint identifiers and current settings. Export success does not prove audible processing or independent startup without Gaming Suite.

## Validation on 2026-10-07

The remote completion pass also compared baseline, deliberate B20 gate/GSX reverb edits, setup restoration and app restart through this report. Every mapped control returned exactly to baseline, excluded pages stayed unchanged and both full processor buffers matched. See `artifacts/remote-goal-native-restoration-20261007.json`. Diagnostics still omit USB status queries and audio capture; the new optional portable `-MonitorPackets 009f|0098` check is a separate metrics-only capture operation.

`artifacts/startup-controls-isolation-20261007.json` reports four connected endpoints after Gaming Suite startup. Windows audio reads succeeded on all four; B20 microphone processing and sidetone, GSX microphone processing and GSX playback processing were available. That historical report predates the GSX USB sidetone adapter. B20 playback processing remains unmapped. GSX monitoring now works in the app but is deliberately not queried by diagnostics; its reason directs users to the microphone page and explains that diagnostics send no USB reports.

Both complete 4096-byte B20 and GSX processor buffers were captured before and after export and remained identical. Seven offline cases cover routing/no writes, missing startup interfaces, independent failures, incompatible state, fresh Windows state, unknown models, empty discovery and permission/busy failures. Physical reconnect and audible behavior are separate checks.
