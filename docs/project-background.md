# Why Timbre exists

I started Timbre to keep using hardware I already owned and to understand how it works on Windows. This background was recorded on 2026-10-08; the [validation status](validation/status.md) describes what the implementation currently establishes.

## Keep useful hardware useful

I built this because EPOS was sunsetting its gaming business and I had an issue getting Gaming Suite from its website. I already had it installed on another machine, so I had a working installation to investigate. I wanted to continue using the products I owned even if the software downloads, supporting services, or vendor support became unavailable in the future.

EPOS publicly describes the phase-out of its gaming headset portfolio. My difficulty obtaining the software was my own experience; the announcement does not establish a Gaming Suite shutdown date. That uncertainty was enough to make continued access worth investigating. [EPOS gaming announcement](https://www.eposaudio.com/en/na/gaming)

At the time I started, I was still seeing these devices for sale, often much cheaper than their original prices. The aim was to retain their capabilities and make good use of the hardware beyond the period when its manufacturer actively supported the gaming range. My immediate targets were the B20 microphone and GSX 300 sound card, including the analog headset connected through GSX.

## Discover what I was missing

During the investigation, I discovered that my own GSX 300 was using a generic Windows USB audio driver rather than the compatible EPOS driver. It produced sound, which made the installation look functional, but the EPOS processor was not registered on its endpoints. A group of features was missing from the actual audio path, and I had not understood what I was unable to use.

Selecting the already-installed compatible EPOS driver and rebooting registered the processor on the GSX microphone and playback endpoints. Playback exposed eight channels, the gate measurement passed, and I confirmed noise gate, noise filter, EQ, virtual 7.1, and reverb by listening. The investigation helped me recover capabilities in hardware I already owned, as well as build a new interface for controlling them. See [the driver and listening results](validation/home-validation.md#connection-and-startup).

That experience shapes the project: a slider accepting a value does not establish that the effect reaches the audio stream. Device discovery, driver selection, control readback, and listening each answer a different part of the question.

## Learn how the system works

I wanted to break the system apart, understand its use cases, and learn new technology while building something useful. The research follows the layers involved in Windows audio: endpoints and topology, USB/HID device interfaces, vendor drivers, audio processing objects, and the interface between a control app and background support.

Memory mapping is part of that learning. Recovering the control-buffer layout, identifying which fields change for an effect, and examining the mutex/event coordination helps explain how separate processes share settings and signal updates. I want to understand the design choices and their consequences, including ownership, startup, reconnect, stale state, and preserving settings that my app does not own.

The [architecture guide](development/app-design.md), [protocol and fixture documentation](development/microphone-protocol-and-tests.md), and [initial investigation](research/local-investigation.md) record that work so it can be understood and extended. Observed behavior and hypotheses about the vendor's design are kept distinct.

## Build a tool I can maintain

The practical outcome I want on my machine is straightforward: use different profiles depending on what I am doing, switch the relevant microphone and playback settings together, and get the capabilities I paid for from the hardware I already have.

Device and setup profiles make that usable day to day. The research, source, reviewed captures, and regression checks make it possible to maintain the tool myself with my coding agents, even as the original support situation changes. Recording the reasons and evidence behind an implementation matters as much as retaining the code.

The long-term goal is continued access to the devices' capabilities. The current app still uses compatible installed EPOS driver/processing components, and normal startup uses vendor background support. Experimental helper work investigates how much of that lifecycle can be maintained independently. Cold-start behavior, fresh-GSX audio, installation, and wider compatibility remain separate milestones in the [dependency plan](roadmap/installer-and-dependencies.md).

Success for this project means useful profiles on my Windows setup, a better understanding of the underlying interfaces, and enough documented knowledge to keep supporting the hardware. Sharing the source under MIT lets others build on the original project code; [third-party notices](../THIRD-PARTY-NOTICES.md) explain the separate provenance of vendor-derived evidence.
