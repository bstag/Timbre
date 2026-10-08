# GSX 300 remaining control evidence

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

These findings come from installed Gaming Suite 1.12.2.1185 managed code and service disassembly. GSX reverb and hardware sidetone were subsequently validated through Suite captures and the app on 2026-10-07. The user confirmed the GSX sidetone listening comparison, then confirmed gate, filter, EQ, virtual 7.1 and reverb after the EPOS driver repair on the same date. See [listening and lifecycle status](../validation/home-validation.md). Calibrated sidetone units and a separate monitoring mute remain unmapped.

## Sidetone

The Suite selects a GSX-specific conversion instead of the B20 topology/dB path. `GetDeviceValue` indexes `SidetoneValueGSX300` with the slider position. Its change handler sends request type 6 and caches the result for the matching PID, VID and serial.

The two recovered 256-entry int32 tables are pinned in `tests/EposControl.Tests/Fixtures/gsx-sidetone-ui-map.json`. They encode 58 distinct hardware bytes. UI positions 0, 64, 127, 128, 191 and 255 map to bytes 199, 222, 239, 239, 250 and 0. The inverse returns representative positions; quantization prevents exact UI-position round trips. Tests verify original PE-data hashes and round trips for every supported hardware byte.

These are opaque encoded values: acoustic units and mute semantics are unverified. Byte 0 must not be treated as mute. No USB reports were sent to obtain the tables.

Native `CUSBCXAdapter::setSideToneValue` opens a device file and uses `WriteFile` with a 39-byte report. The getter also writes a query, so a hardware read is not passive. The complete layout and matching HID collection were recovered and tested on 2026-10-07:

- Getter: 39 bytes, `04 00 01 12 7B` followed by zeros.
- Setter: 39 bytes, `04 40 01 12 7B VV` followed by zeros, with `VV` the encoded hardware byte.
- Response: 35 bytes; observed report ID `05`, sidetone value at byte 1, zeros in the remaining bytes.
- GSX control collection: VID/PID `1395:0098`, interface `MI_03`, collection `COL01`, usage page `000C`, usage `0001`, input/output lengths 35/39, no feature reports. The other GSX collection has 2-byte reports and must not receive this query. Selection also binds the exact physical USB instance, not just model or endpoint name.

The research probe in `tools/GsxSidetoneProbe` obtained repeated starting reads of byte 226. Independent Gaming Suite slider changes at minimum, midpoint (50%) and maximum produced bytes 199, 239 and 0, matching the static conversion. The original Suite slider reported 72/255 despite the table representing byte 226 at 74/255: percentage/position is not sufficient for exact restoration. After the coarse Suite slider produced adjacent values, the recovered setter restored the saved byte 226 directly, and the complete subsequent response matched the baseline. This is an actual native setter/readback check, not an audible sidetone check.

During the sidetone reference changes and exact-byte restoration, both complete 4096-byte processor buffers and all four Windows level/mute states matched the microphone-page baseline. Leaving that Suite page subsequently cleared its known initialization flags at 66, 68 and 70; this is the same page-entry/exit behavior recorded in the earlier GSX routing investigation, not a sidetone-command effect. Final diagnostics matched every mapped control and endpoint format in the user's pre-test listening report. GSX 7.1 and its existing reverb amount were preserved. The Suite's original B20 recording selection was restored; GSX remained its playback selection.

Five 35-byte status goldens are pinned in the fixture manifest. Two additional main regression cases verify Suite endpoints and complete status restoration; nine separate offline research checks cover command shape, physical identity, ambiguity, descriptors, cancellation and exact-capture recovery. `tools/Test-App.ps1` runs the research guards when their build is available. The probe is excluded from portable releases and is not called by the app or read-only diagnostics. Its default mode enumerates descriptors only; `--query` explicitly sends the getter. Its recovery mode requires a successful saved capture for the same physical GSX and verifies a fresh read afterward.

### App adapter and remote validation

The production adapter now exposes a percentage slider on the GSX Microphone page. It uses the recovered conversion tables, never the B20 topology/dB adapter. Its headphone icon has no mute action: minimum is byte 199 and maximum is byte 0, so neither establishes a separate mute. The hardware has discrete steps; a requested 50% reads back as approximately 48.6%. Device and setup profiles store the exact raw byte, avoiding percentage rounding during restoration. Older profiles without this field leave GSX monitoring unchanged.

Transactions bind the active microphone endpoint to one physical USB instance and the validated HID collection/descriptor. A per-device named mutex serializes this app's queries and writes. Each query opens a fresh handle with bounded overlapped I/O; two identical independent reads are required. Apply compares the current value/identity against the draft, rechecks immediately before writing, and verifies exact readback. Failed updates restore only the starting or submitted value on the same control identity; a newer value or replacement device is preserved. USB work runs off the UI thread, with navigation and conflicting controls briefly disabled. Live edits use the existing 150 ms quiet / 300 ms maximum queue policy. No firmware or driver is installed or changed.

The native five-level check passed at 25/50/75/0/100%, then restored byte 226. Both full processor buffers and all Windows level/mute states stayed unchanged. Computer-use checks on the real app passed for initial read, live apply, manual draft/polling, discard, manual Apply, device-profile restoration and microphone-only setup restoration. Test profiles were isolated with `--data-directory`; the saved raw byte was retained during auditioning. All mapped controls matched the initial diagnostics after restoration, including B20 and GSX playback. GSX live input analysis received very quiet samples and reported stream interruptions; meaningful voice/band activity and audible behavior still need home testing.

Offline tests cover tables, status goldens, exact packet shape, descriptor/identity ambiguity, cancellation, stale/unstable reads, no-op updates, readback failure, conditional rollback, replaced controls, profile preflight/rollback, legacy data and excluded setup devices. `Test-App.ps1 -HardwareGsxSidetone` and portable `Verify-Setup.ps1 -HardwareGsxSidetone` explicitly opt into live level changes with exact restoration, without playing or recording audio. Normal verification and diagnostics still send no USB reports. Opening the GSX microphone page queries status and polls while selected.

The response has no echoed command or transaction ID. Fresh handles, repeated reads and this app's mutex reduce stale reads but cannot prove correlation or eliminate races with Gaming Suite or another external controller. Keep Suite idle during adjustments. User headphone listening passed; concurrent external writes, physical reconnect and operation without the service remain unvalidated. Separate sidetone mute and calibrated dB units remain unverified. B20 sidetone continues to use its independent Windows topology control.

Local adapter evidence: `artifacts/gsx-sidetone-adapter-hardware-20261007.json`, `artifacts/gsx-sidetone-ui-validation-20261007.json` and the referenced UI query/diagnostic snapshots.

Local evidence: `artifacts/gsx-sidetone-value-il.json`, `artifacts/gsx-sidetone-lambda-il.json`, `artifacts/gsx-sidetone-value-array.json`, `artifacts/gsx-sidetone-inverse-array.json`, `artifacts/gsx-sidetone-query-*-20261007.json`, `artifacts/gsx-sidetone-validation-20261007.json` and `artifacts/service-disassembly.txt` (getter initialization `004D8AB4`, input `004D8FCD`, setter `004D91E0`, value store/write `004D9594`). The array extractor reads bounded static PE data without executing vendor code.

## Live USB interaction follow-up, 2026-10-07

The sidetone slider remains interactive during its native USB transaction; navigation, profile/setup actions and conflicting settings remain guarded. A completed response updates the hardware baseline without replacing a newer slider draft. The newest live draft applies serially after the bounded quiet interval. Multiple flush callers await the same complete queue, so saves cannot capture an intermediate value. A newer manual draft stays unapplied. Navigation/discard/live-mode cancellation prevents subsequent queued writes and returns cancellation to waiting saves. Failed stale writes cancel the queue rather than retrying. Close remains blocked during an owned USB transaction, and polling cannot refresh selection during that transaction.

Deterministic WPF checks use a delayed worker with demo adapters to exercise the actual asynchronous dispatcher guards and queue, including latest-value preservation, serial execution, concurrent flushes, manual drafts, cancellation and external edits. The previous behavior failed this delayed latest-edit test. Logs: `artifacts/gsx-async-red-20261007.log` and `artifacts/gsx-async-final-20261007.log`. These checks supplement hardware transactions; they do not simulate USB acoustics.

## Slider feedback correction, 2026-10-07

A deterministic WPF reproduction confirmed the reported sidebar flash and slider rollback: an accepted 43% request moved the thumb to 41.961% on apply completion and polling. The disabled native ListBox template also rendered a white background during the pending USB transaction. Replacing that template with explicit background-preserving chrome removes the flash while device selection stays guarded.

Successful local sidetone changes now retain the requested slider position. An unchanged hardware read preserves it during polling; a changed hardware value or control identity still synchronizes the slider. Initial load, explicit discard and profile application show the recovered hardware position. Accessible help distinguishes the selected percentage from the verified hardware step. Profiles still store the exact raw byte, and the USB validation, serialization and rollback rules are unchanged.

The regression exercises a held USB transaction and compares every rendered device-list pixel before, during and after the guard, including the device names and direction labels. Explicit item foreground styling prevents the native disabled text color from dimming and brightening on each GSX update, while selection remains disabled. It also checks 43% apply/polling, ten real small-increment slider commands, external updates and explicit discard. Existing delayed-write, profile-save and manual-draft checks also run. Invoke `tools/Test-App.ps1` for the complete offline suite; its WPF verification covers these cases without sending USB reports.

## Playback reverb

The Suite normalizes its slider by 255 and assigns `ApoParams.reverbLevel`. Native `CApoIPC::UpdateApoBufferLevelThree` stores enable at buffer offset 69 (`0x45`) and float level at 148 (`0x94`). Earlier stereo/surround captures also changed byte 69, so it needs an independent check.

Live Suite captures confirm these fields. Minimum/middle/maximum give 0, 129/255 (the UI rounded it to 51%), and 1 at float offset 148; only that float changes during slider edits. Returning to stereo clears flags 64 and 69 but retains the amount. Both complete processor buffers were restored exactly and B20 stayed unchanged. The app now whitelists byte 69 and the complete four-byte float at 148, with bounded validation, compare-before-write, readback and rollback. See [GSX playback controls](gsx-playback-controls.md).

Local evidence: `artifacts/gsx-remaining-ui-il.json` and `artifacts/service-disassembly.txt` (enable store `0055B3A5`, level store `0055B4B5`). Audible reverb/surround require listening checks.
