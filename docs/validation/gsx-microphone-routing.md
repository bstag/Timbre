# GSX 300 microphone routing check — 2026-10-06

Dated validation record. See [current validation status](status.md) for later results. Referenced artifacts are local evidence excluded from the repository.

Gaming Suite can address the GSX 300 microphone separately from the B20. In Suite 1.12.2.1185 on this PC, explicitly choosing **Recording device → EPOS GSX 300** and moving its noise gate from minimum to maximum and back changed only the GSX processing buffer. The B20's complete 4096-byte buffer remained identical throughout. Both complete buffers and the original Suite device selections were restored at the end.

## Selection and evidence

The starting Settings page showed **Playback device: EPOS GSX 300** and **Recording device: EPOS B20**. These are independent choices. Selecting GSX for recording changed neither buffer. Opening its microphone page changed GSX bytes 66, 68 and 70 from zero to one; B20 remained unchanged. These page-initialization changes were captured separately from the gate experiment.

Dragging the GSX gate to 100% gave a UI slider value of 255 and changed only the float32 field at offset 152 from 0 to 1 (changed bytes 154–155). Returning it to 0% gave UI value 0 and restored the exact pre-gate GSX buffer. Leaving the microphone page restored the page-initialization flags; returning the recording selection to B20 left both complete buffers identical to their starting snapshots.

The Suite initially displayed a cached GSX gate knob value of 126 while the GSX buffer's gate field was zero. UI knob position alone therefore does not establish applied state. The test restored the applied gate to its original zero; the GSX UI knob now reflects zero. It did not save a vendor preset, adjust Windows gain/mute, change sidetone or play/record a microphone test.

The earlier attempted GSX capture changed the B20 instead. The current experiment establishes independent routing and makes an unexpected recording selection a plausible explanation for that earlier observation. It does not reconstruct the historical request or prove a specific Suite bug. No named-pipe messages were intercepted or injected. Static Suite code independently prepares processing request 13 using microphone/playback selection plus VID, PID, serial and device type.

## Fixtures and regression coverage

The new `gsx-microphone-*` and `b20-during-gsx-gate-*` fixtures are copies of these live captures, pinned in the SHA256 manifest. They preserve page initialization separately, compare both gate transitions against the existing codec, verify B20 isolation and check complete GSX restoration. Raw captures and the generated stage/hash report are in `artifacts/gsx-mic-routing-*.bin` and `artifacts/gsx-microphone-routing-report.json`.

Validation passed: **416 regression checks and 99 WPF scenarios**, including five new capture-based routing checks. The test build completed with zero warnings/errors. Log: `artifacts/gsx-microphone-routing-tests.log`.

This initial experiment established routing and gate endpoints. A subsequent [GSX microphone controls session](../research/gsx-microphone-controls.md) captured intermediate gate scaling, all filter levels and complete Warm/Clear EQ curves. The native adapter is enabled for gate/filter/EQ and their switches, with readback, restoration and isolation checks passing. GSX high-pass remains unavailable. Hardware sidetone was subsequently mapped and implemented through its [separate USB adapter](../research/gsx-remaining-controls.md), with exact profile restoration. Audible processing/service-independent GSX startup remain unvalidated. Opening the Suite page is itself a settings-changing operation and must remain separate from control captures.
