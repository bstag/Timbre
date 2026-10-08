# B20 physical pickup-pattern status

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

Investigated on 2026-10-05 with B20 `1395:009F` and Gaming Suite 1.12.2.1185. The physical switch is currently cardioid, confirmed by the user remotely. No other physical switch position, unplug/replug, or reboot has been tested in this session.

## Recovered protocol and evidence

Static inspection of the installed native service found `CMediaAdapter::getMicDirection` at `0x00492E00`. It calls `getResponse` at `0x004922F0`, passing the four-byte constant at `0x00826EC0`: **`F0 05 00 FF`**. That routine opens a shared read/write HID handle, writes four bytes and reads four bytes. Inspection reads the executable as data; it does not load or execute vendor code.

The B20 vendor collection is usage page `FFCD`, usage `0001`, with four-byte input and output reports and no feature reports. The consumer-control collection is a different interface and is excluded. A live input report **`80 03 02 FF`** was captured and is stored in the hashed `b20-pattern-cardioid.bin` fixture. An unrelated `80 01 13 FF` report was also observed and must not be interpreted as pattern status.

| Pattern value | Managed Suite enum | Live validation |
| --- | --- | --- |
| 1 | Bidirectional | Static enum and synthetic decoder test only |
| 2 | Cardioid | Live report, matching the user's physical switch position |
| 3 | Omnidirectional | Static enum and synthetic decoder test only |
| 4 | Stereo | Static enum and synthetic decoder test only |

Names and numeric values come from `GamingSuite.UI.Services.MicDirections`. Native event parsing and the captured report support byte 1 as the event code and byte 2 as its value. The decoder accepts only the captured four-byte framing, event 3, and values 1–4. This is a status query, not a software pattern setter. The switch remains physical.

Local research artifacts: `artifacts/b20-pattern-service-il.json`, `artifacts/b20-pattern-ui-il.json`, `artifacts/b20-pattern-native-references.json`, `artifacts/b20-pattern-query-cardioid.json`, and the installed-service disassembly. Raw artifacts contain local device identities and are excluded from release packages.

## Current implementation and limits

`IPickupPatternBackend` separates status from gain, sidetone and PC processing. The native adapter matches the selected microphone's physical USB parent, freshly discovers the Windows endpoint before and after the request, validates the HID descriptor, and rejects missing or ambiguous interfaces. It opens a new handle for each request, sends only the recovered query, uses asynchronous I/O with a 1.5-second deadline, and bounds unrelated traffic to 32 reports. Timeout, removal, cancellation or unknown data yields an error rather than a cached or guessed pattern.

The WPF header shows the latest successful result for the selected B20 microphone. Selection changes cancel pending work and prevent an older request from updating another endpoint. Playback selections hide the display. Patterns are excluded from profiles because this app cannot move the physical switch.

**The native readout remains experimental.** A query succeeded with the Gaming Suite UI closed and its service running, but repeated queries with the UI open have timed out. Separate-handle and shared-handle read-ahead experiments did not resolve this. The implementation retains the write-then-read sequence used by the recovered vendor routine and reports unavailable when it receives no fresh response. One successful capture does not establish reliable polling, coexistence, or reconnect behavior.

The later administrator dependency run also timed out with UI closed and with UI/service stopped, so UI coexistence alone does not explain the failures. A one-off `HidD_GetInputReport` request for the observed report ID `0x80` returned `80 00 00 00`, which contains no valid pattern status and is not used as a fallback. Its research result is `artifacts/b20-input-state-query.json`.

A bounded synchronous native `WriteFile`/`ReadFile` research probe captured cardioid once, but its next three runs received only the unrelated gain report `80 01 13 FF` before timing out. Unbuffered .NET I/O and direct native overlapped I/O also timed out. These results do not establish buffering or asynchronous I/O as the cause. The app retains the simpler cancellable, unbuffered stream implementation. Evidence: `artifacts/b20-pattern-synchronous.json`, `artifacts/b20-pattern-synchronous-repeated.json`, `artifacts/pattern-unbuffered-1.json` through `-3.json`, and `artifacts/pattern-native-overlapped-1.json` through `-3.json`. The blocking research probe is excluded from the package and must only run with an external process timeout.

Microsoft documents asynchronous `ReadFile` for ongoing HID input and distinguishes it from one-off `HidD_GetInputReport` collection-state requests. The adapter uses the recovered vendor query and normal input reads; it does not probe arbitrary output reports or feature setters. [Obtaining HID reports](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/obtaining-hid-reports), [HidD_GetInputReport](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getinputreport).

## Pattern-related processing

Managed IL for `MicrophonePageView.ShowMicDirectionButton` updates the Suite pattern display and assigns `micFrcEnabled = 1` and `micFrcPreset = directionValue` in its APO parameter request. This links those FRC parameters to pickup-pattern-dependent processing. It does not establish the algorithm, a hidden compressor, or software control of the physical switch. Our app leaves these unowned parameters untouched.

## Validation and next checks

The 2026-10-07 audit added explicit rejection of incomplete/wrong-model USB ancestor identities and duplicated selected endpoints. The post-query inventory must match the original HID descriptor and path, so a changed interface cannot publish an older response. Offline regression `Pattern guard rejects ambiguous endpoints and incomplete physical identities` failed before these guards and passes after them. These guards improve routing safety; they do not cure intermittent device responses. A new bounded native request with the service/UI running still timed out (`artifacts/remote-goal-dependencies-before-20261007.json`). The prior synchronous, read-ahead and service-stopped experiments already failed to establish a reliable alternative; no new guessed commands or setters were added.

Offline checks cover the live cardioid golden, all four static enum values, unrelated events, malformed reports, immutable query bytes, one-query behavior, timeout/cancellation/disposal, bounded traffic, physical identity, unknown/ambiguous descriptors, and reconnect with a changed Windows endpoint ID. WPF demo checks cover all four displayed names and hiding status on playback. Synthetic switch changes are not live hardware validation.

Before marking the display ready, repeat status requests with Suite UI closed and with its service stopped, then test all four physical positions and unplug/replug when someone can access the microphone. A later planned reboot must verify startup behavior separately. Do not reboot this remotely used PC as part of the automated check.
