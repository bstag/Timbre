# Using EPOS Control

Build a source checkout using the [build guide](../development/building-and-testing.md), then run `Start-EPOS-Control.cmd`. For a portable package, extract the complete ZIP, run `Verify-Setup.cmd`, and follow `START-HERE.md`. Advanced processing requires the compatible EPOS installation described in [validation status](../validation/status.md).

## Choose an audio page

Select the physical device in the device list. **Sound** and **Microphone** navigate between endpoints of the same USB device. A GSP 301 plugged into GSX 300 uses GSX's pages. B20 sidetone is heard through the B20 headphone jack; GSX sidetone uses headphones plugged into GSX.

If the selected endpoint disappears, the app selects another available endpoint. A returning device becomes available without overriding the current selection. Use **Refresh devices** to refresh discovery.

## Adjust controls

**Apply changes live** defaults on. Supported level/mute, microphone processing/sidetone, and GSX sound mode/EQ/reverb edits preview as you adjust them. Turn live mode off to prepare drafts and use the corresponding Apply buttons. **Discard edits** cancels pending work and reloads readings without writing.

The Microphone studio opens with its main controls. EQ sliders, device details, and profiles start collapsed. Click an effect icon to enable/disable it; a dim icon with a red slash means disabled, with its values retained. **Mic** and B20 **Sidetone** show the slash when muted. GSX sidetone has a percentage slider without a separate monitoring mute. Tooltips and accessibility help describe each state. Unsupported controls remain unavailable.

The nine EQ bands are 64, 125, 250, 500 Hz, 1, 2, 4, 8, and 16 kHz. Click a graph band to open EQ controls. B20 microphone supports Flat/Warm/Clear/Custom curves. GSX Sound offers stereo/virtual 7.1, custom EQ, and reverb through its installed processor.

## Activity views

Check **Live activity** for microphone level/frequency activity or playback activity on the Sound page. Opening the studio alone does not capture. Capture stops on collapse, device changes, or app close; no recording is saved.

Blue frequency bars share the plot with the EQ curve. Activity is measured in dBFS; EQ settings are in dB. The gate percentage guide is not a calibrated threshold. Microphone activity does not play your voice to an output. See [microphone measurements](live-microphone-view.md) and [playback activity](live-playback-view.md).

## Profiles and setups

Expand **Device profiles** to **Save as profile**, load a profile, or **Save to selected profile**. A profile belongs to one physical device and audio page. Named profiles change only on explicit saves; saving in live mode finishes pending edits first.

Expand **Setup profiles** above the device list to save selected pages together as Gaming, Meeting, or Streaming. **Devices to include** groups Sound/Microphone pages by physical device. Select a parent for all its listed pages or choose child pages independently. New setups start with nothing included.

For example, include B20 Microphone and GSX Sound while leaving B20 Sound unchecked. **Save as**, then **Apply** switches the included snapshots together. **Save to selected setup** replaces settings and membership from the current checkboxes. Save membership changes before applying. Checked disconnected members retain saved snapshots; applying skips disconnected pages and reports partial failures. Setups do not change Windows defaults or application routing.

**Select edited pages** optionally selects pages whose direct adjustments successfully applied during this app session. It selects membership, not a preset; save explicitly. See [profile behavior and compatibility](profiles-and-live-controls.md).

## Storage and restore

Profiles use `%LOCALAPPDATA%\EPOS-Control\profiles.json`; setups use `setups.json` beside it. They survive replacement of the app directory. Existing app-directory profiles are imported only when the per-user destination is absent, with the original retained.

B20 Apply processing saves verified gate/filter/high-pass/EQ state under the same user folder's `processing-state` directory. **Restore saved processing** restores it manually. Automatic restore on app launch or an observed reconnect defaults to off; an available processing interface is required. Named profiles still require explicit application. GSX automatic processing restore is not implemented. See [B20 processing restoration](processing-state.md).

## Diagnostics and hardware checks

Diagnostics export control availability/failure reasons without settings changes, USB queries, or recording. Reports can contain local device identifiers. Optional hardware scripts can change settings or capture metrics; follow the [home checklist](../validation/home-validation.md) and save a baseline before using them.
