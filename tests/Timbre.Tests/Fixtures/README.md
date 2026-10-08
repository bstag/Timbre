# Golden control-state captures

Captured locally on 2026-10-05 and 2026-10-06 using Gaming Suite 1.12.2.1185. APO binaries are each 4096 bytes, header int32 values 2, 4. Those files are control buffers, not audio recordings or USB reports. The separate JSON topology captures below record hardware control state; the four-byte pickup-pattern fixture is a USB input report.

| File | Device | Gate | Filter | Proven transition |
| --- | --- | ---: | ---: | --- |
| apo-memory-009f.bin | B20 | 0% | 2 | Initial baseline |
| apo-gate50-009f.bin | B20 | 49% | 2 | Only float32 at 152 changed |
| apo-gate100-009f.bin | B20 | 100% | 2 | Only float32 at 152 changed |
| apo-filter1-009f.bin | B20 | 100% | 1 | Only int32 at 160 changed |
| apo-memory-0098.bin | GSX 300 | Raw field 0 | Raw field 0 | Initial buffer only; routing not validated |
| b20-controls-start.bin | B20 | 0% | 2 | Starting state for expanded controls: all four switches on, EQ flat |
| b20-hpf-off.bin | B20 | 49.411766% | 1 | High-pass off; Suite also reapplied cached gate/filter values |
| b20-hpf-on.bin | B20 | 49.411766% | 1 | Only byte 71 reversed to 1 |
| b20-eq-band1-plus6.bin | B20 | 49.411766% | 1 | Only first EQ gain at 112 changed to +6 dB |
| b20-eq-warm.bin | B20 | 49.411766% | 1 | EQ [6,6,4.02,-0.52,-3.02,-2.31,1.04,-3.02,0] |
| b20-eq-clear.bin | B20 | 49.411766% | 1 | EQ [-6,-6,-3.02,0,0,1.04,2.03,0,0] |

`manifest.json` pins SHA256 for every binary, topology capture, and the EQ screenshot/mapping below. B20 APO transitions compare all 4096 bytes, so even an unrelated EQ or enable-bit change fails. GSX decoding alone does not prove a working GSX adapter.

The 2026-10-06 GSX microphone routing session explicitly selected GSX as the Suite **recording** device. `gsx-microphone-routing-start.bin` captures the starting Settings page; `gsx-microphone-gate-zero.bin` captures microphone-page initialization (bytes 66, 68 and 70 set to one). `gsx-microphone-gate-max.bin` changes only float32 offset 152 from zero to one; `gsx-microphone-gate-return.bin` reverses it exactly. `gsx-microphone-routing-restored.bin` matches the complete starting buffer after leaving that page and restoring B20 as recording device. `b20-during-gsx-gate-start.bin`, `b20-during-gsx-gate-max.bin` and `b20-during-gsx-gate-restored.bin` are identical companion captures proving B20 control-buffer isolation. These fixtures establish gate endpoints and routing, not audible processing or the remaining GSX microphone controls. See [session evidence](../../../docs/validation/gsx-microphone-routing.md).

`b20-eq-frequencies.png` is the user's unmodified Gaming Suite microphone-EQ screenshot supplied on 2026-10-05. `b20-eq-frequency-map.json` transcribes its nine left-to-right labels (64, 125, 250, 500 Hz, 1, 2, 4, 8, 16 kHz) and associates them with the previously captured EQ field order at offsets 112 through 144. Frequency labels are vendor UI evidence, not calibrated frequency-response measurements. The screenshot's plotted gains are not a new preset or control-state capture. The regression checks label order and each band's prepared field offset; the WPF check verifies displayed/accessibility labels.

The subsequent GSX controls session adds `gsx-microphone-page.bin`, `gsx-microphone-filter2.bin`, `gsx-microphone-filter1.bin`, `gsx-microphone-filter0.bin`, `gsx-microphone-warm.bin`, `gsx-microphone-clear.bin`, `gsx-microphone-eq-restored.bin`, `gsx-microphone-gate-mid.bin` and `gsx-microphone-restored.bin`. Page → filter2 → filter1 → filter0 proves the three preset values at 160. Filter0 → Warm → Clear → EQ restored proves all nine floats at 112–144. Page → gate mid proves 51% = 0.51 at 152. EQ restored matches Page; restored matches the historical starting buffer. Tests compare complete 4096-byte transitions and decode both preset curves. Companion B20 snapshots remained identical and are recorded in the local session report. These fixtures and native readback/restoration enable the bounded GSX adapter; audible processing, sidetone and high-pass remain separate. See [GSX microphone controls](../../../docs/research/gsx-microphone-controls.md).

| Topology capture | Sidetone, both channels | Verified transition |
| --- | ---: | --- |
| b20-topology-before-sidetone.json | 9.0859375 dB | Original hardware topology before the user's sidetone changes |
| b20-topology-sidetone50.json | 2.9765625 dB | User set Suite sidetone to approximately 50%; only node 0x20007 levels changed |
| b20-topology-sidetone0.json | -31 dB | User set Suite sidetone to 0%; only node 0x20007 levels changed; mute remained off |

Topology JSON preserves all eight subunits, ranges, subtypes and incoming/outgoing links. The endpoint ID and device/global-ID prefixes are replaced with fixture identifiers to omit machine-specific USB parent IDs. No values, node IDs or links are changed. See `docs/research/b20-sidetone.md` for native API evidence and live write/restoration verification.

See `docs/development/microphone-protocol-and-tests.md` for provenance, semantics, limitations, and intentional-update rules. Do not refresh the manifest automatically in tests.

GSX playback captures from 2026-10-06 are `gsx-playback-stereo.bin`, `gsx-playback-surround71.bin`, `gsx-playback-stereo-return.bin`, `gsx-playback-eq-band1-plus6.bin` and `gsx-playback-eq-band9-minus6.bin`. The return-to-stereo buffer matches its starting buffer exactly. Mode changed byte 64 and Suite's separate reverb flag at 69; our mode adapter intentionally preserves 69. The first/last EQ captures differ from Flat only at floats 76/108, reading +6/−6 dB. `gsx-playback-eq-frequencies.png` is the user's original screenshot; `gsx-playback-control-map.json` combines screenshot frequency order, endpoint-band captures and the independently recovered native indexed nine-band writer. The intermediate bands are covered by that indexed writer and synthetic range/order tests, not separate user captures. See `docs/research/gsx-playback-controls.md` for hardware readback evidence and the remaining listening/lifecycle limits. The old `apo-memory-0098.bin` remains historical microphone-layout evidence only.

`b20-pattern-cardioid.bin` is the live four-byte B20 vendor-HID input report `80 03 02 FF`, matching the user's confirmed cardioid switch position. It is not an APO buffer and is excluded from APO layout/prepared-write tests. The manifest pins its SHA256. The status query `F0 05 00 FF` and values 1–4 were recovered statically from the installed service and managed enum. Only cardioid has a live capture; the other patterns are synthetic decoder tests. Query reliability and physical reconnect remain unverified. See `docs/research/b20-pickup-pattern.md`.


GSX sidetone static evidence from 2026-10-07 is pinned in `gsx-sidetone-ui-map.json`. Its 256-entry forward/inverse tables came from Gaming Suite PE data, without USB queries or vendor code execution. Tests verify both original array hashes, 58 distinct encoded values and inverse consistency. These tables are static evidence; acoustic units and separate mute behavior remain unverified. See `docs/research/gsx-remaining-controls.md`.

Subsequent live sidetone goldens are `gsx-sidetone-baseline.bin`, `gsx-sidetone-min.bin`, `gsx-sidetone-mid.bin`, `gsx-sidetone-max.bin` and `gsx-sidetone-restored.bin`. They are complete 35-byte HID responses to the recovered getter, not APO buffers. The Suite independently set minimum/midpoint/maximum, producing bytes 199/239/0 at response byte 1. Maximum byte 0 is not a mute encoding. The baseline and restored responses are identical (byte 226). Restoration used the exact saved hardware byte with the recovered native setter; it does not prove calibrated units, independent mute or listening behavior. Both processor buffers and all Windows level/mute states stayed unchanged. No source-machine identity is included in these binary status fixtures.

GSX reverb goldens from 2026-10-07 pin slider minimum/middle/maximum, stereo retaining the maximum amount, zero return and complete restoration. Slider-only transitions own float 148; the Suite mode transition owns 64 and 69. B20 companion captures prove isolation at maximum and restoration. The midpoint is 129/255, displayed as 51% in the Suite. These are independent Suite captures; app-generated 50% captures stay in artifacts and are not golden sources.
