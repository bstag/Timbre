# B20 hardware sidetone

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

The app now controls B20 headphone-monitoring level and mute through Windows audio topology. Use headphones connected to the B20's headphone jack. Changing this control does not route monitoring into the GSX 300. The installed B20 driver remains required; our adapter does not use Gaming Suite's pipes or load vendor DLLs.

Select the B20 microphone, adjust **Sidetone**, and click **Apply sidetone**. The slider shows native hardware dB rather than guessing a percentage scale. Lower is quieter. **Mute sidetone** switches off the hardware monitoring branch independently of microphone mute. A level change sets both monitoring channels; a mute-only change preserves each channel's exact level. Opening the app and moving sliders do not write settings until Apply. Pending edits survive polling, and stale edits are rejected.

**Save current** includes the actual sidetone settings alongside Windows level/mute and microphone processing. **Apply profile** restores all of them. Older profiles preserve the existing sidetone setting. If sidetone fails after a profile has applied processing, the profile restores its earlier processing and endpoint level where their state still matches its writes. Newer edits are preserved and restoration errors are reported.

## Confirmed route and captures

Read-only static inspection of the installed service found `CSideToneGain::getSideToneVolume`, `setSideToneVolume` and `dump_node`, and calls to `IAudioVolumeLevel`. Its setter at virtual address `0x00546500` converts the requested integer to `(value + 1) / 100`, takes log10, multiplies by 20 and adds the channel's maximum dB. The constants at `0x007AC510` and `0x007AC50C` are 100 and 20. No inspected vendor code was executed.

Windows topology snapshots were taken before the user changed the B20 sidetone slider, at approximately 50%, and at 0%. Only the stereo volume node below changed. Capture and render endpoint queries returned the same physical B20 topology. GSX controls stayed unchanged.

| B20 control | Local ID | Route / observed state |
| --- | --- | --- |
| Microphone mute | `0x20000` | Unchanged by sidetone captures |
| Microphone gain | `0x20001` | Both channels remained -9.5 dB |
| Playback volume | `0x20004` | Both channels remained -2.2617188 dB |
| Sidetone mute | `0x20006` | Microphone connector `0x10000` → mute → sidetone volume |
| Sidetone volume | `0x20007` | Mute → volume → playback summing node `0x20005` |

Sidetone reports two channels with minimum -34.5 dB, maximum 9 dB and nominal step 1.5 dB. Actual levels have finer fixed-point resolution. Starting levels were 9.0859375 dB; the 50% capture was 2.9765625 dB; the 0% capture was -31 dB. All were unmuted. Suite's `+1` conversion explains why 0% is still a low audible level and its highest setting can exceed the reported maximum by approximately 0.0864 dB. The adapter allows this captured upper value so it can restore existing settings exactly. Normal slider maximum is 9 dB unless a higher compatible starting value needs to be preserved.

The adapter requires B20 VID/PID and microphone direction, checks the selected active endpoint's physical identity, then validates the driver topology suffix `epostopology`, node subtypes, neighboring links, both channel ranges and mute availability. An unfamiliar layout fails before writes. Unlike the APO model-wide shared objects, this control is addressed through the selected device's own topology.

Writes use `IAudioVolumeLevel::SetLevelAllChannels` and `IAudioMute::SetMute`. Muting precedes level changes; unmuting follows them. Readback must match within one 1/256-dB fixed-point unit. Updates compare the full prior control identity, range, channels and mute state. Failure restoration touches only attempted fields and refuses ambiguous newer levels. This is a compare/read/write sequence; Windows does not supply an atomic transaction across the volume and mute interfaces.

The interface declarations follow the installed Windows SDK `devicetopology.h` and Microsoft's [IDeviceTopology](https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nn-devicetopology-idevicetopology), [IAudioVolumeLevel](https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nn-devicetopology-iaudiovolumelevel), and [IAudioMute](https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nn-devicetopology-iaudiomute) documentation.

## Validation

On 2026-10-05 the live test changed sidetone from -31 to -29.5 dB on both channels and switched its mute on. Readback matched. It checked every other B20 topology control and the Windows level/mute of all four EPOS endpoints for isolation, then restored the pre-capture levels of 9.0859375 dB and unmuted state exactly. Report: `artifacts/sidetone-verification-009f.json`. This is a hardware control/readback check; a headphone listening check remains separate.

The full suite now has 184 offline checks and 19 WPF scenarios. Sidetone coverage includes three hashed captures, routing/range/channel guards, device/direction identity, stale state, no-op writes, invalid levels, fixed-point rounding, partial stereo writes, write and restoration failures, preserved concurrent edits, complete/legacy profiles, and rollback across profile components. UI checks exercise pending edits, level/mute, preservation of asymmetric channels, profile restoration and hiding sidetone on playback endpoints.

On 2026-10-05 the user confirmed that the requested headphone listening check worked: sidetone level adjustment and monitoring mute are now validated by user listening as well as hardware readback. This is a manual confirmation; the test PC's configuration was not recorded. B20 sidetone listening validation is no longer pending.

Run the normal suite:

```powershell
.\tools\Test-App.ps1
```

Optional live sidetone check (briefly changes its level and mute, then restores its starting settings):

```powershell
.\tools\Test-App.ps1 -HardwareSidetone 009f
```

Read-only topology capture:

```powershell
dotnet .\tests\Timbre.Tests\bin\Release\net9.0\Timbre.Tests.dll --topology --report artifacts\audio-topology.json
```

Reviewed topology fixtures omit local endpoint IDs and USB parent identifiers. Raw local captures and hardware reports remain in ignored artifacts. GSX sidetone still needs its own protocol mapping.
