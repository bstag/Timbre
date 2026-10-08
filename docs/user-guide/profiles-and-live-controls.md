# Device profiles and live controls

## Using the app

Microphone studio opens with compact icon switches below the activity/EQ graph. Noise gate, filter, high-pass and EQ use a bright icon when on and a dimmed icon with a red slash when off. Values are retained while an effect is disabled. Mic/Sound and B20 Sidetone icons show a red slash when muted. GSX sidetone uses a percentage slider and a static headphone icon; no separate monitoring mute is mapped. Control names remain visible; tooltips and accessibility help describe the state explicitly. The gate slider sits directly beneath the EQ, and clicking a graph band reveals its adjustment slider. Profiles, restoration and hardware details remain expandable. Setup profiles still offer whole-device and individual-page inclusion.

**Apply changes live** is enabled by default each time the app starts. Adjusting a supported volume/mute, microphone processing, sidetone, sound mode or EQ control previews the change on the selected endpoint. Manual Apply buttons are hidden in this mode. Uncheck the option to prepare drafts and use the separate Apply buttons. Switching the option on does not submit old manual drafts; a new edit in that section queues an update.

Slider events are combined over a 150 ms quiet interval, capped at 300 ms during continued movement; a 50 ms UI timer submits the latest values. This avoids a write for every pointer event while allowing continued auditioning during a long drag. Windows/native call latency can add to those intervals. Flat EQ reset and microphone EQ presets follow the same live policy.

Opening the app, reading settings, selecting an endpoint or selecting a profile in the dropdown does not apply controls. The separate B20 restore-on-connect option remains explicit opt-in. Pending live work is bound to the current endpoint ID and device identity and is canceled on a page/device change, disconnection, refresh, profile load, saved-processing restore, discard, switching live mode off or window close. A failed live update is reported and is not retried automatically.

Expand **Device profiles** on either the Sound or Microphone page:

1. Enter a name and choose **Save as profile** to save the current settings under that name. Reusing a name replaces that device/page's matching entry.
2. Choose a saved profile and use **Apply profile** to load it.
3. Audition changes with the controls. Choose **Save to selected profile** to update the chosen profile without retyping its name.

Live auditioning never updates named profiles implicitly. Saving in live mode finishes queued changes first; if applying them fails, the profile is not saved. Manual-mode drafts remain excluded. Saving to an existing profile refuses stale saved data or unavailable control groups rather than losing their stored values. B20's separate last-applied processing restore state still updates after successful processing changes.

Device profiles belong to a physical device and an audio page. For example, GSX Sound contains volume/mute/mode/EQ, while its Microphone profile is separate. The same name can be used for the B20 and GSX and for both pages. Use a setup profile to save several pages together.

## Setup profiles

Expand **Setup profiles** above the connected device list. Names such as **Gaming**, **Meeting** and **Streaming** describe your own snapshots; the app does not install preset curves or change settings based on these names.

1. Adjust the device controls. Live changes apply while you audition; manual-mode drafts are excluded from saves.
2. In **Devices to include**, check a physical device to include all its listed pages, or use the indented **Sound** and **Microphone** checkboxes to select only the parts you want. The device checkbox shows a partial selection when some pages are excluded. On the active page, **Include whole device in setup** selects or clears its listed pages, and **Include this page in setup** changes just that page. A new setup starts with no automatically included pages; choosing a saved setup checks its existing members. Discovery currently lists EPOS endpoints only.
3. Enter a name and choose **Save as**. Reusing an existing name replaces that setup with the current checked pages and their settings. Each page has its own level/mute and all currently mapped controls: B20 microphone processing and sidetone, GSX microphone processing and USB sidetone, and GSX sound mode/EQ/reverb. Other discovered endpoints contribute level/mute only.
4. Choose a saved setup and **Apply** to load its snapshots on the connected members. Choosing it in the dropdown only displays its membership; it does not apply settings.
5. After auditioning changes, choose **Save to selected setup** to save exactly the checked pages into the selected setup. This can add or remove pages. Unchecked pages are excluded from its saved snapshots and future application. Inclusion changes must be saved before applying: Apply is disabled while the checkbox selection differs from the saved setup. An empty selection cannot overwrite a setup.

**Select edited pages** is an optional shortcut inside Devices to include. It selects pages with direct adjustments successfully applied through this app during the current session, and unchecks the rest. Pending live edits finish first; a failed update leaves the checkbox selection unchanged. Reads, profile/setup loads, automatic restore, unapplied manual drafts and failed writes do not count. A page remains marked as edited for the session even if you later adjust it back to its starting settings. This is edit history, not a comparison against the current preset. You can adjust the checkboxes afterward. It does not save or apply a preset by itself.

For **Gaming**, you can include **B20 → Microphone**, leave **B20 → Sound** unchecked, and include **GSX 300 → Sound**. Saving and applying that setup leaves the B20 speaker level/mute untouched. Clearing a device checkbox excludes all its listed pages, including saved disconnected pages. Grouping uses the physical USB instance, so two devices of the same model remain separate. Unknown or legacy disconnected identities remain separate pages until their physical identity can be established. Checking a whole device selects the pages currently listed; a newly discovered page is not automatically added to an existing saved setup. Named device profiles keep their existing per-page scope.

Setup snapshots are independent of named device profiles. Live edits, saving a different setup, and changing device profiles never implicitly modify them. Live saves finish pending edits first and abort if that update fails. Saving refuses unavailable mapped controls for checked connected pages and stale saved data, preserving the last saved file. Unchecked adapters do not participate in capture. A disconnected member of the selected setup is shown with a checkbox: leave it checked to preserve its existing saved snapshot, or uncheck it to remove it. If it reconnects before capture, saving refuses to reuse an old snapshot and asks you to refresh. **Reload setups** refreshes saved snapshots and restores the selected setup's saved checkbox selection.

When applying, a disconnected member is explicitly skipped and the connected members can still apply. Nothing queues for automatic application on reconnection. Ambiguous device matches or unavailable connected controls abort preflight before any writes. Each connected page then uses the existing guarded adapters. If a later page changes externally or fails, further pages stop; earlier successful pages remain applied. The result lists Applied, disconnected, failed or not applied per page. This is not an atomic transaction across multiple USB devices. B20 processing that successfully applies is also remembered in its separate restore store; a failure to save that restore state is reported separately from hardware success.

Setups change settings on endpoints, not Windows default input/output, application routing or analog headphone/speaker identity. Headphones plugged into the GSX use its sound endpoint. A different supported sound endpoint can be included in a later saved setup when connected. Device matching retains the USB instance and audio page; a different USB instance is not substituted automatically.

## Storage and compatibility

The 2026-10-07 native remote session verified a Gaming setup with B20 Microphone and GSX Sound only. Live gate/reverb edits were restored together, excluded B20 Sound/GSX Microphone stayed unchanged, and both complete processor buffers matched baseline. App restart retained saved files without capture or automatic setup application; explicitly choosing Gaming repopulated its saved membership. See `artifacts/remote-goal-native-restoration-20261007.json` and `artifacts/remote-goal-reopen-validation-20261007.json`. The tests used `--data-directory` and preserved the real user's stored profiles.

GSX microphone profiles store monitoring as `GsxSidetone.RawValue`, separately from B20's `Sidetone` dB/mute state. Saving requires a fresh GSX USB status read; unavailable monitoring aborts a save rather than dropping its state. Applying preflights USB state before volume/processing writes and conditionally restores those controls if the monitoring transaction fails. Legacy profiles without this field leave monitoring alone.

For isolated testing or portable data, launch `Timbre.exe --data-directory ".\artifacts\my-test-data"`. This explicitly routes profiles, setups and B20 processing restore state to that folder and still uses real hardware. It does not apply saved settings on startup; the normal restore opt-in still governs B20 processing. Default launches retain the per-user locations below.

Live profiles now live in `%LOCALAPPDATA%\Timbre\profiles.json`. They survive replacing the app directory. Data is per Windows user; the file contains locally identifying USB instance IDs and should not be included in a distributable package.

Setup profiles use the separate `%LOCALAPPDATA%\Timbre\setups.json` with schema version 1. They use complete inline snapshots, required JSON fields, validation, a bounded writer lock and flushed atomic replacement. A setup name is unique across the user's setups. Save to selected compares the original complete snapshot to the current file before replacing its settings and membership. The schema remains compatible with earlier setup files. Invalid or incomplete files are kept and reported; no empty-list fallback overwrites them. Setup and device-profile files are never bundled as source-machine data. Demo and rendered checks isolate both stores.

Timbre starts with fresh profiles, setups, and processing state under its own user-data folder. It does not automatically import former app-directory data or data from `%LOCALAPPDATA%\EPOS-Control`, and leaves those files untouched. Existing Timbre files survive app updates. Malformed or incomplete data is rejected rather than overwritten with an empty fallback.

New profile identities include USB vendor/model/instance and audio direction. Friendly-name changes and new Windows endpoint GUIDs do not orphan a profile for the same physical USB instance. Devices without USB ancestry remain bound to the endpoint ID. Legacy profiles retain their earlier strict endpoint-role/name binding until explicitly saved; Save as or Save to selected upgrades their identity without creating a duplicate device/name entry. A different USB instance remains a different device.

Writes use a bounded per-file exclusive lock and a flushed temporary file followed by atomic replacement. Save to selected compares its original saved record with the current file and rejects changes made by another app instance. Invalid values, missing fields, replacement failure and lock contention preserve the last good file. Demo and rendered verification use separate data folders.

## Verification

The original profile/UI milestone passed 411 offline regressions and 99 WPF scenarios. See [current validation status](../validation/status.md) for the later source verification. Coverage added at that milestone includes:

- Setup capture and apply across B20/GSX pages, missing/ambiguous identity handling, preflight refusal, external changes during multi-page application and explicit partial results.
- Setup snapshot persistence, concurrent-save conflicts, explicit membership replacement, retained disconnected snapshots, unavailable checked adapter refusal, malformed JSON, writer contention and atomic replacement failure.
- Collapsed setup UI, default exclusion, whole-device selection/clear, partial selection, synchronized page checkboxes, physical-instance separation, same-device speaker exclusion, disconnected grouping, explicit page capture, live flush, direct-edit selection, empty selection refusal, unsaved membership guards, reconnect metadata, profile-store isolation, B20 restore persistence and reload behavior. These checks use demo adapters; the new multi-device workflow has not yet been exercised against live hardware.

- Real WPF timer application, slider coalescing, capped waits, live volume/mute, microphone processing/EQ, sidetone and GSX sound/EQ.
- Exact untouched volume/EQ preservation, cancellation on selection/profile load/discard, manual/live toggling and preservation of newer external edits after failure.
- Save to selected, save-time live flush, named-profile isolation, stale file conflicts and retaining stored groups while controls are unavailable.
- Stable physical binding, legacy import without overwriting or deleting originals, strict JSON, bounded writer locks and atomic-write failure cleanup.

Default tests use demo devices, temporary profile files and private native objects; they do not audition live audio. The existing B20/GSX native control readback tests remain separate. No new USB or driver transport is introduced by this UI workflow.
