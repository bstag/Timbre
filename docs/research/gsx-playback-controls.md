# GSX 300 playback controls

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

The app now provides Stereo (2.0), virtual 7.1, nine-band custom playback EQ, and reverb for the GSX 300. The GSP 301 uses these controls through the sound card. Open its playback endpoint (Speakers with the verified EPOS driver on this PC) or choose **Sound** from its microphone page. Expand **Playback equalizer** to edit the curve or reset it to Flat. Apply changes live defaults on, so these changes preview automatically. Uncheck it to prepare drafts and use Apply sound settings; volume/mute then retain their separate Apply button.

The compact Sound studio now overlays the configured/draft EQ curve with opt-in live playback activity. Check Live activity for Windows output-mix RMS, peak and nine frequency regions; click a band to open its EQ slider. Capture stops on selection changes or shutdown, saves no recording and never changes profiles or sound controls. Its position relative to vendor processing is unverified; the chart is not a measured EQ/surround response. Native quiet-tone detection passed without changing Windows controls or either device's processing buffer. See [playback activity and evidence](../user-guide/live-playback-view.md).

Profiles save the currently applied mode, EQ, reverb, volume and mute in `%LOCALAPPDATA%\Timbre\profiles.json`. Live saves finish queued edits first; manual drafts are excluded. Save to selected updates the chosen profile without renaming it. Playback profiles remain isolated from microphone profiles and other USB instances. Earlier profiles without playback effects preserve current mode/EQ/reverb; older playback snapshots without reverb preserve its switch/amount. GSX processing does not yet have automatic startup/reconnect restore or a managed helper. See [profile storage and live control behavior](../user-guide/profiles-and-live-controls.md).

## Local control evidence, 2026-10-06

Gaming Suite's Stereo → 7.1 → Stereo transitions changed GSX byte 64 from 0 → 1 → 0. The native `CApoIPC::UpdateApoBufferLevelOne` writes `directSoundEnabled` at `0055AEB9` to offset `0x40`. The recovered managed handlers independently establish true = virtual 7.1 and false = stereo.

Gaming Suite also changed byte 69 with the mode. Static `CApoIPC::UpdateApoBufferLevelThree` identifies this as separate reverb enablement (`0055B3A5`, offset `0x45`). Our mode control owns byte 64 only and preserves reverb, EQ enablement and microphone fields. Reverb was validated separately on 2026-10-07; see below.

The first playback EQ band at +6 dB changed only float32 offset 76; the final band at −6 dB changed only float32 offset 108. The B20 buffer remained unchanged during these EQ captures. The native `CApoIPC::UpdateApoSpeakerEqLevels` loops over nine ordered values and writes to `base + 0x4C + index * 4` at `0055CAED`. These are little-endian floats in dB, separate from microphone EQ at offsets 112–144.

| Band | Frequency from the user's Suite screenshot | Float32 offset |
| --- | --- | ---: |
| 1 | 64 Hz | 76 |
| 2 | 125 Hz | 80 |
| 3 | 250 Hz | 84 |
| 4 | 500 Hz | 88 |
| 5 | 1 kHz | 92 |
| 6 | 2 kHz | 96 |
| 7 | 4 kHz | 100 |
| 8 | 8 kHz | 104 |
| 9 | 16 kHz | 108 |

The screenshot, captures and independent mapping are pinned in `tests/Timbre.Tests/Fixtures/manifest.json`. The native service source hash is `350D5EC4DEA1F6440C83DA82A0155AF1D7ACA02250E3F31EB7E3EBF472B8DD44`. Local static metadata/disassembly lives under `artifacts`; no vendor executable is packaged with these fixtures. User captures returned to exactly the original GSX Flat/stereo buffer. Selection/mode activity also changed B20 enable flags during the first observation; those unrelated changes were recorded separately, not attributed to GSX fields or written by our adapter.

The user could not find vendor sound presets in their current interface. Only custom EQ and our Flat reset are provided. Legacy five-band XML files are not used as nine-band curves.

## Adapter and tests

`IPlaybackEffectsBackend` exposes read / compare / apply. The WPF app uses the same contract for native and demo devices. The native path validates the current GSX playback identity, rejects multiple physical units sharing this model's vendor objects, acquires the existing mutex, opens the existing 4096-byte memory/event objects, validates header 2/4 and writes only byte 64 (mode), byte 69 (reverb enable), complete EQ floats at 76–108 or the complete reverb float at 148. It neither creates shared objects nor sends USB, firmware or Suite pipe commands.

Writes notify the installed processor and verify readback. A failed transaction restores only attempted fields when their values remain attributable to the transaction; newer conflicting edits are preserved and restoration failure is reported. Microphone effects and EQ enablement remain outside the playback write whitelist. Legacy updates without reverb preserve its exact stored switch/amount. Untouched fractional EQ values are preserved by the UI, including when editing one band or changing only sound mode.

Offline coverage includes live goldens, all nine band positions and range edges, malformed layouts/JSON, stale state, native write isolation, failure/rollback injection, full playback profiles and legacy profiles. WPF scenarios cover drafts, polling, discard, Flat reset, profile restore, precise-value preservation, collapsed sections and physical-device navigation.

The opt-in hardware test briefly toggles mode, applies a distinctive nine-band curve and restores the complete starting settings. It also checks unowned GSX configuration, Windows level/mute and connected B20 configuration:

```powershell
.\tools\Test-App.ps1 -HardwarePlayback
```

The first live run passed, with all starting GSX settings restored and B20 processing preserved. The report is `artifacts/playback-verification-0098.json`. These are control/readback checks. Subsequent user listening confirmation covers EQ, virtual 7.1 and reverb, as described below. The Gaming Suite service was running; GSX startup with that service stopped, physical reconnect and chosen-state cold-start restoration remain unvalidated. Keep the installed EPOS processor and service for this GSX build.

## Reverb controls and evidence, 2026-10-07

The compact Reverb icon and amount slider sit below the combined playback EQ/activity chart. Select virtual 7.1 to edit them. The icon switches only reverb and keeps its amount; it uses the same red slash as microphone effects. Mode changes in our app preserve the independent stored reverb switch/amount. Enable reverb explicitly when wanted. Stereo disables its editors. Live apply, manual drafts/discard, device profiles and checked setup Sound pages all include reverb. Older profiles lacking reverb preserve it. Untouched fractional amounts are retained exactly, including when toggling or changing another control.

Gaming Suite slider-only captures changed the float at offset 148 from 0 to 129/255 (its midpoint UI showed 51%) to 1 and back to 0. Mode transitions separately changed enable byte 69 with mode byte 64, retaining the reverb amount. The complete initial GSX and B20 buffers were restored; B20 remained identical at every captured stage. These independent captures are pinned as gsx-reverb-*.bin and b20-during-gsx-reverb-*.bin in the fixture manifest.

Native backend validation passed in artifacts/gsx-reverb-hardware-20261007.json. A separate real-app UI exercise applied 50% automatically, toggled off while retaining 50%, and restored the entire starting buffers and Windows levels/mutes. Native evidence is in artifacts/gsx-reverb-ui-*.bin and artifacts/gsx-reverb-ui-restored-20261007.json. These prove control routing/readback/restoration with the Suite service running. The offline/UI checks cover goldens, legacy preservation, invalid values, stale snapshots, rollback/conflicts, profiles, drafts, precise-value retention and stereo availability.

## Post-driver listening confirmation, 2026-10-07

After switching GSX to the installed EPOSAudio 1.3.1.0 driver and rebooting, both GSX endpoints register the EPOS processor and playback exposes eight channels. Native microphone/playback control tests passed again. The user then reported that noise gate, noise filter, EQ, virtual 7.1 and reverb work. This is functional listening confirmation on the current machine; the report does not identify EQ direction or establish calibrated channel placement/frequency response. Evidence: `artifacts/gsx-listening-confirmation-20261007.json`, `artifacts/gsx-effects-post-reboot-20261007.json` and `artifacts/gsx-playback-post-reboot-validation-20261007.json`. See [the home checklist](../validation/home-validation.md) for remaining lifecycle checks and repeat validation on another installation.
## Fresh initialization follow-up — 2026-10-08

The controlled audio-engine pilot `artifacts/gsx-initialization/20261008-125145-6502c99f` passed fresh GSX object creation with EPOS support stopped, followed by playback stereo/7.1, nine-band EQ and reverb apply/readback/restoration. Microphone and unowned fields were preserved at every playback step; the complete GSX and B20 buffers and all diagnostic controls matched baseline afterward. Both services recovered and our helpers exited cleanly. See [GSX initialization evidence](gsx-initialization.md).

This validates the playback settings interface after fresh creation. Audible playback DSP in that fresh, vendor-service-stopped session was not measured or listened to; `AudioListeningValidated=false` remains unchanged in the playback report. The earlier user listening results with vendor support running are separate evidence.
