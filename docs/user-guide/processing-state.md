# B20 processing persistence and restore

The app now saves its own last successfully applied B20 processing state. This is separate from named profiles and the diagnostic snapshots used by the experimental object host.

## Using it

1. Reopen `Start-EPOS-Control.cmd`, select B20 and choose **Apply processing**. A successful device transaction saves its verified readback.
2. **Restore saved processing** explicitly restores that state whenever the processing interface is available.
3. Optionally check **Restore processing when this B20 connects or the app starts**. This preference defaults to off. Turning it on schedules restoration for the next observed connection or app launch; it does not immediately change processing.

Restore includes gate percentage, filter preset, filter/gate/high-pass/EQ enable switches and all nine EQ gains. It preserves settings for disabled effects. Endpoint volume/mute and sidetone remain in explicitly applied named profiles; this new processing restore does not cover them.

The app discovers connection changes every two seconds. An opted-in B20 can restore even when another endpoint is selected. It waits for a readable processing interface, validates the current physical device and attempts restore once per observed connection. It does not repeat writes on every poll or save unapplied slider edits. A failed automatic attempt requires manual retry or another observed connection. A disconnect and reconnect entirely between polls may be missed if Windows keeps the same endpoint ID.

## Storage and identity

Live files are under `%LOCALAPPDATA%\EPOS-Control\processing-state`. Each physical B20 has a SHA256-named JSON file and a companion lock file. Demo and rendered verification use separate directories and cannot modify live state. Named profiles now live in `%LOCALAPPDATA%\EPOS-Control\profiles.json`, with import from the earlier app-directory file when the new destination is absent. See [profiles and live controls](profiles-and-live-controls.md). Live processing edits update this last-applied B20 state after successful apply; they do not change named profiles until explicitly saved.

Schema version 1 stores:

- Validated complete `MicrophoneEffects`, including fractional values.
- Canonical VID/PID, microphone direction and Windows physical USB instance identity.
- UTC time of the last remembered processing state.
- The explicit `RestoreOnConnect` preference.

Endpoint GUID and friendly names are deliberately excluded from the new identity. Changing them does not invalidate processing state. A different USB instance gets a different file. For devices without stable serial-derived instances, changing USB ports may create a new identity; the app does not guess that two instances are the same device. Multiple connected B20 units remain refused because the vendor objects are shared by model, without a serial.

This format contains no memory dump, opaque configuration, meters, audio, native handles or diagnostic seed. The core `ProcessingStateStore` can also be used by a future helper with an explicitly configured directory; a service account's default data folder would not be the user's folder.

## Transaction and recovery behavior

Successful **Apply processing** saves the adapter's validated readback. Successfully applying a complete processing profile also remembers that state after the entire profile transaction has completed. Failed hardware/profile transactions never update the saved state. Reads, polling, saving a named profile and changes made in Gaming Suite do not replace our remembered state.

Writes use a flushed temporary file in the same directory and an atomic replace. An exclusive per-device lock serializes settings and preference updates across store instances/processes; waits are bounded to approximately one second. A failed replace leaves the last good file intact and removes our temporary file. A crash before replacement can leave an unreferenced temporary file; loads ignore it.

If hardware Apply succeeds but persistence fails, the UI explicitly reports **Processing applied, but its restore state was not saved**. It leaves the accepted hardware state alone and retains the earlier saved state. Manual restore reads fresh hardware state and uses the existing compare/apply/readback transaction, preserving its concurrent-edit protections and write whitelist.

Unknown versions/fields, missing required fields, malformed or oversized files, invalid control values and identity mismatches are refused. A corrupt file is preserved for investigation instead of being silently reset. Back it up and move that device's JSON file aside before applying settings to create a new state; this also resets its automatic-restore preference to off.

## Verification and limits

The persistence milestone passed **258 regression checks and 25 WPF scenarios**, without warnings. The subsequent [managed-helper milestone](../research/b20-host-lifecycle.md) brought that milestone to 286 checks and 25 scenarios. See [current validation status](../validation/status.md) for later verification. Twenty-four persistence cases cover storage reload, physical identity, changed endpoint GUIDs/names, unsupported endpoints, strict schema validation, partial states, failed applies/saves, last-good preservation, bounded lock contention, concurrent preference/settings writers and once-per-connection restoration. The native persistence test destroys and recreates unique `Local\EposControl.Tests.*` objects, then restores saved controls through the real memory/event transport and compares every byte with the independent initializer golden plus verified patches.

Six new WPF scenarios verify durable processing state/default policy, pending-edit isolation, manual restore, opt-in reconnect restoration, hardware-success/save-failure reporting and control visibility. Reports are `artifacts/tests.json` and `artifacts/app-preview.png.json`. All new tests use isolated storage and demo/private objects; they do not change the real B20, Gaming Suite or its service.

Actual hardware unplug/replug and reboot recovery are still unvalidated. This app restore requires an available APO interface: it does not create missing objects or run when the app is closed. The bounded experimental host now has [managed lifecycle mode](../research/b20-host-lifecycle.md), which reads this store and honors its explicit opt-in. Fresh object creation still requires device-matched diagnostic startup input. Managed mode passes isolated/native, executable guard and actual service-stopped B20 creation/restore/control checks. This warm-session result does not prove pure cold-start defaults or DSP audio operation. Validate reconnect, startup and audio behavior before installing a replacement background service.
