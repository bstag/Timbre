# Local EPOS interface investigation

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

Observed on this PC on 2026-10-05. User priority: microphone gain, noise reduction, sidetone.

This document records the initial read-only investigation. A working level/mute/profile app and subsequent control verification are described in [the implementation guide](../development/app-design.md).

## Outcome

A replacement control app appears feasible. Windows volume/mute controls have been queried successfully by our own code. The installed UI/service protocol is partially recovered from assembly metadata and IL. Full independence from EPOS still requires mapping device controls and replacing or independently controlling its PC audio processing. No setting-changing commands have been tested.

## Connected hardware and software

| Item | Confirmed local evidence |
| --- | --- |
| USB sound card | EPOS GSX 300, USB VID `1395`, PID `0098` |
| Microphone | EPOS B20, USB VID `1395`, PID `009F` |
| Installed suite | EPOS Gaming Suite `1.12.2.1185`, installed executable reports version in the inventory |
| UI | `C:\Program Files (x86)\EPOS\Gaming Suite\EPOSGamingSuite.exe`, running |
| Service | `EPOSGamingSuiteService`, running |
| GSX 300 audio interface | `MI_00`, Microsoft `usbaudio`, INF `wdma_usb.inf`, version `10.0.26100.9457` |
| B20 audio interface | `MI_00`, vendor `EPOSAudio`, INF `oem84.inf`, version `1.3.1.0` |
| HID transport | Both devices expose `MI_03` using Windows `HidUsb` |
| B20 effects package | Installed `EAPO.dll`; registry maps APO CLSID `{27201018-29C6-446A-9308-AA97260FEED1}` to `C:\Windows\System32\eapo.dll` |

The bundled `Config\GSADriver\eposaudio.inf` registers the EPOS APO as an endpoint effect and lists the B20 PID. This does not by itself establish which effect DLL is attached to every GSX endpoint or whether every application uses the effect path.

There is also an Elgato virtual endpoint named `Microphone (EPOS B20) (Elgato Virtual Audio)`. It is a **render** endpoint according to its Windows endpoint ID, despite its friendly name. A future app should select the actual physical capture endpoint using identity and data flow, not just a name substring.

## Three control layers

| Requested feature | Evidence and implementation route | Remaining work |
| --- | --- | --- |
| Microphone gain/level and mute | EPOS maps gain to Windows. Our Core Audio probe successfully reads both physical capture endpoints through `IAudioEndpointVolume`. Both report hardware volume/mute support (`3`). | Add explicit endpoint selection, callbacks, range queries and reversible setter validation. Do not assume endpoint percentage is identical to an analog knob's gain or calibrated amplification. |
| Noise reduction and gate | EPOS maps these to PC APO processing. Installed UI has `ApoParams`; service and EAPO contain shared-memory IPC code. | Validate the UI-service APO payload, valid ranges and defaults. For EPOS independence, implement/choose DSP and an audio path that exposes processed capture to other apps, such as a virtual capture device or an APO integration. |
| Sidetone | EPOS documents device-resident settings. Installed UI has a sidetone request and service includes CMedia/Conexant-related adapters. | Trace a known sidetone change to identify the actual device-control route, payload and limits for each model. Presence of HID alone does not prove sidetone is sent over HID. |

At capture time our own code read B20 microphone scalar volume `0.414549` (`-9.5 dB`) and GSX microphone scalar volume `0.58823574` (`-3.1966248 dB`), both unmuted. These are Windows endpoint readings, not acoustic measurements or a hardware microphone-preamp specification.

[Microsoft EndpointVolume API](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpointvolume-api) documents hardware/software endpoint control. [Microsoft APO implementation documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects) explains PC-side effects. The EPOS feature mapping is cited in [official source research](official-sources.md).

## UI to service protocol: recovered static evidence

Both `GSAgentPipe` and `GSUIPipe` exist in the live Windows named-pipe inventory. The inspected binaries contain `\\.\pipe\GSAgentPipe` and `\\.\pipe\GSUIPipe`. No TCP listening endpoints were found for the two EPOS processes at the time of collection.

`GamingSuite.UI.Services.dll` is managed .NET. The supplied inspector reads its metadata and IL without executing it. Recovered types include `Request`, `RequestToDevice`, `RequestToApoDevice`, `DeviceApoUniqueId`, `ApoParams`, `Client` and `NamedPipeServer` in `Sennheiser.Gs.UI.Services.Model`.

`Client.SendMessage` puts the request into a collection, calls `JsonConvert.SerializeObject`, encodes with `ASCIIEncoding`, writes the resulting bytes to a file stream and flushes. The method contains no application-level length prefix or newline append. This establishes a JSON collection transport in the inspected method; a complete live request/response exchange and message-boundary behavior remain unvalidated.

The `.RequestType` enum has these relevant numeric values:

| Constant | Value |
| --- | --- |
| `REQ_CONNECTED_DEVICE` | 1 |
| `REQ_SW_VERSION` | 4 |
| `REQ_NOISE_REDUCTION` | 5 |
| `REQ_SET_SIDE_TONE` | 6 |
| `REQ_SET_APO_PARAMS` | 13 |
| `REQ_SET_MICVOLUME` | 17 |
| `REQ_SET_MICMUTE` | 18 |

These are **internal service request codes**, not USB opcodes. `REQ_NOISE_REDUCTION` must not be assumed to be the B20/GSX APO noise-suppression control; the newer APO path has separate parameters.

`pipeServer_SendMessageCommon` converts a numeric request code to a **string** and fills `requestType` and `SelectedDevice`. The value-changing overload prepares a device request using VID, PID and serial number, then fills `changedValue`. `PrepareMicrophoneRequest` confirms those three identity fields. Their precise serialized types, null/default behavior and value ranges need validation.

`pipeServer_SendAgentParamSet` constructs device entries containing VID, PID, serial number, device type and `apoParams`; it sets root `requestType` to `"13"`. `ApoParams` exposes:

```text
directSoundEnabled, micMetersEnabled, micNrPreset, reverbLevel,
noiseGateLevel, speakerEqLevels, micEqLevels, micHpfEnabled,
micFrcPreset, micFrcEnabled, micNrEnabled,
enableVoiceActivityMonitor, enableVoicePeakMonitor,
resetVoiceActivityMonitor, enableAutomaticGainAdjustment
```

No fabricated payloads were sent. The running UI may own a callback pipe and service connection, so an experimental client needs to account for existing connections rather than taking over a pipe blindly.

## Service to effects and devices

The native service contains `CApoIPC::CreateSharedMemory`, `GetApoSettings`, `UpdateApoSettings`, `UpdateApoMicBands`, `UpdateApoMicHPFAndFrc`, `UpdateApoMicMonitor`, `UpdateApoSpeakerEqLevels`, and `CApoIPCListener` backup/restore callbacks. It imports `CreateFileMappingW` and `MapViewOfFile`. EAPO and the service both contain shared-memory/mutex/event errors, `Global\` and `Software\EPOS\SCAPO` strings.

This is strong static evidence of a shared-memory effects-control path. The mapping names, layouts, synchronization, persistence and attached-endpoint behavior have not been established. No matching SCAPO registry data was visible in the inspected HKLM/HKCU locations; a string in a binary is not evidence that a registry key exists on this PC.

The vendor's `DeviceInfoConfig.txt` identifies GSX 300 as adapter family `Boston` and B20 as `CMedia`. These are implementation clues, not proven control protocols. The service contains CMedia microphone pattern/gain/mute/event methods and sidetone-change diagnostics. The suite includes `CxAudioHidDll32.dll`, which contains HID APIs and output-report communication methods.

## HID descriptor results

Our descriptor-only probe opens matching interfaces with **zero desired access**. It does not call `ReadFile`, `WriteFile`, `HidD_GetFeature`, `HidD_SetFeature`, or any vendor routine. Values below are maximum Windows report-buffer lengths, including report-ID space; they do not reveal USB command meanings.

| Device | Collection | Usage page | Usage | Input bytes | Output bytes | Feature bytes |
| --- | --- | --- | --- | ---: | ---: | ---: |
| GSX 300 | `MI_03 COL01` | `000C` | `0001` | 35 | 39 | 0 |
| GSX 300 | `MI_03 COL02` | `FFFF` | `0001` | 2 | 2 | 0 |
| B20 | `MI_03 COL01` | `000C` | `0001` | 2 | 0 | 0 |
| B20 | `MI_03 COL02` | `FFCD` | `0001` | 4 | 4 | 0 |

The GSX collection on the consumer-control page has larger report buffers than the vendor-page collection. Do not assume all vendor control traffic uses the vendor-page collection.

## Preservation and verification

`preservation/20261005-071818/` contains 21 copied files: the cached Gaming Suite 1.12.2.1185 MSI package and metadata, bundled driver directories, presets and selected configuration. Each file has a SHA-256 entry in `manifest.json`. Nothing was installed or flashed. Authentication files and microphone recordings were excluded. The installer and driver package have been copied and hashed, not tested on a clean Windows installation.

The inspector built with zero warnings/errors. The full read-only diagnostic completed with **15 matching PnP nodes, four HID collections, five matching audio endpoints, both named pipes, and zero report errors**. Source correction of HID BOOLEAN return marshaling is validated by a fresh diagnostic run.

Local evidence is in `artifacts/machine-inventory.json`, `ui-metadata.json`, `pipe-methods.json`, `control-methods.json`, `framing-methods.json`, `pipe-directions.json` and `apo-strings.json`. Installed binary hashes are recorded in the inventory, tying static results to this installation.

## Next implementation step

Build a small Windows microphone-control interface with endpoint-specific gain and mute through Core Audio. For sidetone and noise reduction, first trace one known UI change per setting and validate request ranges, response routing and persistence. A transitional interface can reuse the preserved EPOS service/APO, but that still depends on vendor components. A fully independent version requires device-command mapping for sidetone and a maintained DSP/capture integration. USB traffic capture is a possible next method if static inspection and existing logs are insufficient; no capture driver was installed during this pass.
