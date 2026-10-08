# EPOS replacement application: official source findings

This file preserves dated investigation results. Consult [current validation status](../validation/status.md) for subsequent results and remaining limits. Referenced `artifacts/` and `preservation/` files are local evidence excluded from source checkouts and ZIPs.

Researched 2026-10-05. These are public-document findings, not confirmation of the USB model or software installed on this PC.

## Product and software support status

EPOS states that it has phased out its Gaming headset portfolio to focus on Enterprise solutions. Its gaming landing page still directs customers to technical support and says warranties are unaffected. This establishes the portfolio phase-out; it does **not** establish a specific end-of-support date for Gaming Suite, server shutdown date, or whether every download currently works. [EPOS gaming landing page](https://www.eposaudio.com/en/na/gaming)

The published Gaming Suite FAQ explicitly lists B20 and GSX 300 compatibility. An analog headset connected through either can use Suite playback and microphone features. This is not evidence that the user's unidentified USB sound device is a GSX 300. [EPOS Gaming Suite FAQ](https://www.eposaudio.com/globalassets/blocks/gaming/gaming-suite/epos-gaming-suite_faq.pdf)

## Where the settings live

EPOS defines an APO as PC-side audio processing and publishes the following identical mapping for **B20 and GSX 300**:

| Feature | Setting location | Included in Suite presets |
| --- | --- | --- |
| Noise cancellation | APO on PC | Yes |
| Noise gate | APO on PC | Yes |
| Sidetone | Device | No |
| Gain | Windows | No |
| Volume | Windows | No |

The matrix does not give USB commands, HID report identifiers, or APO control interfaces. [EPOS device feature matrix](https://www.eposaudio.com/en-gb/support/knowledge-base-gaming/gaming/gaming/gaming-suite---device-feature-matrix/)

## B20 capabilities documented by EPOS

The B20 manual calls the microphone USB plug-and-play and tells Windows users to select B20 as both input and output. Its headphone socket provides direct monitoring. The hardware has gain, volume, mute, and pickup-pattern controls. Gaming Suite displays pattern/gain/volume/mute state and can mute the microphone. It exposes noise cancellation, sidetone, and microphone EQ. The manual specifically says a sidetone setting remains in the device until changed. Firmware updates are initiated through Suite. It does not publish their protocol. [B20 user guide, pages 7, 9, 11, 13, 15–18 and 21](https://www.eposaudio.com/globalassets/__pim/products/b20-series/b20---grey/1e8d40d5-3106-4a4a-b0eb-9265a2ceb6aa_26740_b20_userguide_a02_0122_en_int_original.pdf)

## Existing SDK and API routes

The public SDK page describes **call control and interoperability**: accept/reject/end/mute/unmute/hold/resume and busylight presence. Available integration routes include a native C++ library; a background SDK service exposing TCP/IP sockets, WebSocket and REST; and JavaScript using that service or WebHID. It mentions demo application source. This page does not establish B20/GSX 300 support, expose gaming EQ/noise-gate/sidetone controls, or identify Gaming Suite's internal interface. Do not treat this SDK as a verified replacement API. [EPOS SDK downloads](https://www.eposaudio.com/en-us/software/developer-portal/sdk-downloads/)

EPOS also describes organizational device-management APIs: asset/report data, update management, and configuration management over HTTPS REST with JSON. Public content does not demonstrate local B20 controls or gaming DSP capabilities. [EPOS API downloads](https://www.eposaudio.com/en-us/software/developer-portal/api-downloads/)

The Connect manual separates Connect's device update/configuration role from DSEA SDK's call-control role, explaining why finding DSEA components alone would not establish gaming feature support. [EPOS Connect 7.3 manual, section 3](https://www.eposaudio.com/contentassets/20c8968b11c048e4b1d33640a8526697/epos_connect_manual_7.3.0_int.pdf)

## Useful local evidence

EPOS documents Suite's service and diagnostics: `C:\ProgramData\EPOS\Gaming Suite\Config\AppConfig.txt`, trace/UI log levels, and `C:\ProgramData\EPOS\Gaming Suite\Logs\`. Its procedure requires stopping and restarting the service, so it is an available future observation method rather than something this research changes. [EPOS trace/debug logging instructions](https://www.eposaudio.com/en-gb/support/knowledge-base-gaming/gaming/gaming/gaming-suite---collecting-trace-and-debug-logs/)

## Implications and remaining unknowns

Inference from the documented separation: a replacement must address Windows audio controls, device-resident settings, and PC-side processing separately. USB control replication alone cannot account for the documented APO effects.

Local inspection still needs to establish the USB sound-device model, VID/PID and interfaces, driver/APO packages, Suite binaries and configuration, and how a known control change reaches the device or APO. No verified B20/GSX HID report map, vendor command set, public gaming-control SDK, or firmware-update protocol was found in these official documents. Exact proprietary DSP equivalence remains unestablished.
