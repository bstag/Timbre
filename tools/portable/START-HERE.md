# EPOS Control: test on another PC

Extract the entire ZIP into a writable folder on a 64-bit Windows PC. Keep the `app` and `checks` folders beside the launchers.

## Setup

1. Install the **.NET 9 Desktop Runtime, Windows x64** if it is missing. The Desktop Runtime includes the console runtime needed by the checks. Download it from [Microsoft's .NET 9 page](https://dotnet.microsoft.com/en-us/download/dotnet/9.0). A development SDK is not required.
2. Connect the B20 and/or GSX 300. Microphone level/mute use Windows audio controls. B20 sidetone currently requires the compatible EPOS driver exposing the mapped hardware topology. Gate/filter/high-pass/EQ still require the installed EPOS audio processor, active shared-state objects and the Gaming Suite service. GSX microphone gate/filter/EQ and sound mode/EQ/reverb also require the installed processor and existing service-created interface. GSX hardware sidetone uses a separate validated USB adapter, with a percentage slider on its Microphone page. GSX high-pass remains unavailable. For the complete current feature set, use a machine with the preserved compatible Gaming Suite/driver installation. Those vendor components are not bundled or installed by this ZIP.
3. Double-click **Verify-Setup.cmd**. This checks package hashes, runs the offline regressions and demo UI scenarios, and discovers connected devices with read-only control availability/error details. It does not change live audio settings, send USB reports or capture audio.
4. Double-click **Start-EPOS-Control.cmd**, select the physical B20 microphone, and inspect the available controls. Missing or incompatible processing/driver controls are reported as unavailable.

Source-machine profiles are not included in this package. On the destination, profiles live in `%LOCALAPPDATA%\EPOS-Control\profiles.json`; an existing profile file for that Windows user is retained. A first launch imports an older app-directory profile file only when the destination is absent. Use Save as profile to create one, or Save to selected profile to update the chosen device/audio-page profile. See `docs/user-guide/profiles-and-live-controls.md`.

Setup profiles save checked device pages together in `%LOCALAPPDATA%\EPOS-Control\setups.json`, also retained across app updates. New setups include nothing until you check Include this page in setup on the desired device pages. Expand Setup profiles and Save as under a name such as Gaming, Meeting or Streaming. Save to selected setup also uses the current checkboxes, removing unchecked pages. Save inclusion changes before applying. Checked disconnected members retain saved snapshots; uncheck them to remove them. Select edited pages optionally selects pages with direct adjustments successfully applied in this app session. Apply loads connected members and reports skipped/failed pages. Setups change endpoint settings; choose input/output routing in Windows or your apps. The multi-device workflow has demo and native restoration/isolation evidence on the source PC; repeat the applicable checks on the destination.

Apply changes live defaults on for supported controls; turn it off to prepare drafts with manual Apply buttons. Named profiles change only when you save. For a GSX 300, select its playback endpoint (Speakers with the EPOS driver verified on the source PC; the GSP 301 uses this output). Expand Playback equalizer for the sliders and Flat reset. Sound mode/EQ preview automatically in live mode; Apply sound settings returns in manual mode. These controls require the installed EPOS processor and existing shared-state interface. The source PC has passed user listening for gate, filter, EQ, virtual 7.1 and reverb; repeat those checks on the destination. Automatic GSX startup is not implemented. A separate experimental initializer has fresh-object control evidence; it is not part of normal portable startup and does not establish fresh-session audible DSP. Advanced sections start collapsed, and Discard edits cancels pending work and reloads without writing. See `docs/research/gsx-playback-controls.md`.

A shared processing interface and successful settings readback do not prove the EPOS processor is attached to an endpoint. On the source PC, GSX initially used the generic Microsoft audio driver and lacked EPOS effects registration. Selecting the already-installed compatible EPOS driver and completing its required reboot registered the processor on input/output; the measured gate check then passed. Verify the compatible driver/processor on a destination where controls appear to work but audio does not change. Vendor components are not included in this package.

**Apply processing** also saves verified B20 gate/filter/high-pass/EQ state under `%LOCALAPPDATA%\EPOS-Control\processing-state`. **Restore saved processing** restores it manually. Automatic processing restore on app launch or an observed connection defaults to off; enable its checkbox if wanted. This requires an available processing interface while the app runs. A previous installation on this PC may already have state for the same physical B20. See `docs/user-guide/processing-state.md`; state from the source machine is not packaged.

The app's B20 pickup-pattern display is experimental. It sends the recovered status query and reports unavailable when it receives no fresh valid response. Cardioid has a live capture, but repeated requests can time out, and the other three physical positions remain untested. Pattern selection stays on the microphone's physical switch. See `docs/research/b20-pickup-pattern.md`.

## Sidetone listening check

Use headphones connected to the **B20's headphone jack**. Its sidetone does not route to a headset plugged into a GSX 300.

1. Note the starting sidetone level and mute state, or use **Save as profile** to save a temporary profile containing all current settings.
2. In **Microphone studio**, set sidetone to **-12 dB** and leave its icon without a red slash. Live mode applies the change; in manual mode choose Apply sidetone. Speak and confirm that your voice is monitored at the new level.
3. Click the **Sidetone** icon so it shows a red slash (muted). Use Apply sidetone if live mode is off. Confirm that your monitored voice stops. This is separate from microphone mute.
4. Restore your saved profile or your original sidetone level/mute when finished. Report whether the level and mute both work.

The slider uses native hardware dB. Lower is quieter. A level change sets both monitoring channels; a mute-only change retains each channel's exact level. Gaming Suite's 0% setting was observed to reduce sidetone to -31 dB without muting it, so the explicit mute is useful.

For **GSX 300**, use its Microphone page and headphones connected to GSX. Its sidetone slider uses the recovered Suite percentage scale, with discrete hardware steps. Successful local edits retain the requested slider position while verifying the hardware step; profiles preserve the exact raw value. No separate GSX monitoring mute or calibrated dB scale is mapped. Save a profile before listening and apply it afterward for exact restoration. Live USB, profile/setup restoration and user sidetone listening pass on the source PC; repeat listening on the destination. Keep Gaming Suite idle while adjusting the app.

## Optional live checks

For capture-quality diagnosis without saving recordings or changing settings, use `./Verify-Setup.ps1 -MonitorPackets 0098` for GSX microphone or `-MonitorPackets 009f` for B20. It captures three seconds of packet and frequency metrics, with separate initial discontinuity, later interruption and timestamp-uncertainty counts. Quiet input is sufficient for transport diagnosis; this is not listening or an audible effects check. Default verification does not capture audio or send USB status commands.

From 64-bit PowerShell in the extracted folder:

```powershell
.\Verify-Setup.ps1 -HardwareSidetone
.\Verify-Setup.ps1 -HardwareEffects
.\Verify-Setup.ps1 -HardwareAudio
.\Verify-Setup.ps1 -HardwarePlayback
.\Verify-Setup.ps1 -HardwareGsxMicrophone
.\Verify-Setup.ps1 -HardwareGsxSidetone
.\Verify-Setup.ps1 -HardwareLoopback
```

These switches explicitly enable brief live changes and restoration. Run them separately. Sidetone and effects checks test state readback and restoration. The GSX playback check toggles sound mode, applies a nine-band EQ curve and checks reverb amount/bypass, checks microphone and unowned-field isolation and restores the starting mode/curve; it does not validate audible surround. The audio check temporarily switches the B20 gate off / maximum / off and saves numerical measurements only, with no recording. Keep the unmuted mic input steady for about eight seconds. A quiet or unstable signal can be inconclusive. Concurrent edits or a disconnection can prevent restoration; inspect the reported result before proceeding.

For an optional B20 status/dependency snapshot, run `./Verify-Setup.ps1 -HardwareStatus`. This sends the recovered USB status query without changing settings or stopping services. It saves adapter availability/errors and warns if pattern status is unavailable. Completing the snapshot is not a hardware status pass.

The default verifier also works without an EPOS device connected. Passing the offline/UI checks in that situation does not validate another PC's audio hardware.

Each run writes a new folder under `reports`. Device diagnostics and live test reports can contain local device identifiers; these reports are generated on the destination machine and are not in the original ZIP. The package itself includes only app/test binaries, reviewed control fixtures and documentation; no source-machine profiles, raw captures or vendor installers.



The GSX Sound studio also offers opt-in playback activity over its EQ curve, with no recording saved. The loopback switch plays a quiet two-second 1 kHz test tone and checks that frequency is captured, without changing settings. The GSX microphone switch changes only its supported processing and verifies restoration and other-device isolation. See [the home validation checklist](docs/validation/home-validation.md), [GSX microphone controls](docs/research/gsx-microphone-controls.md) and [playback activity](docs/user-guide/live-playback-view.md).
