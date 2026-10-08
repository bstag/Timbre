# EPOS Control

A Windows desktop control app for EPOS B20 microphones and GSX 300 sound cards. It provides device volume/mute, supported processing controls, live activity views, and device/setup profiles through a native WPF interface.

This is an experimental personal project developed with the help of coding agents. Advanced processing still depends on a compatible installed EPOS driver/audio processor and, during normal app use, the Gaming Suite background service. Vendor installers, drivers, and processing binaries are not included.

## Why I built this

I started EPOS Control because EPOS was winding down its gaming portfolio and I had trouble getting Gaming Suite from its website. I already had a working installation on another machine, and I wanted to keep using my B20 and GSX 300 even if downloads or vendor support became harder to obtain. I was also seeing these products still for sale at much lower prices than their original prices. Useful hardware should have a future beyond the availability of its original control software. [EPOS's portfolio announcement](https://www.eposaudio.com/en/na/gaming)

The investigation revealed something I had missed on my own PC: my GSX 300 was using a generic USB audio driver. Basic sound worked, but the EPOS processing path and a whole group of features were missing. Selecting the compatible EPOS driver and rebooting exposed capabilities I had not realized I was missing, including the processing and surround features later confirmed by listening.

I also wanted to learn how Windows audio, drivers, device interfaces, and memory-mapped control structures work together. Building a practical app gives that investigation a purpose: switch profiles for different activities, understand what my hardware can do, and maintain the controls myself with my coding agents as vendor support changes. The longer-term goal is to preserve the devices' capabilities; the current app's remaining EPOS dependencies are documented below.

See [the project background and goals](docs/project-background.md) for the fuller story and [the recorded driver/validation results](docs/validation/home-validation.md) for the technical evidence.

## Supported controls

| Device/page | Available controls |
| --- | --- |
| B20 microphone | Volume/mute, noise gate/filter, high-pass, nine-band EQ, hardware sidetone level and separate monitoring mute |
| B20 sound | Windows volume/mute |
| GSX 300 microphone | Volume/mute, noise gate/filter, nine-band EQ, USB sidetone level |
| GSX 300 sound | Volume/mute, stereo/virtual 7.1, nine-band EQ, reverb enable/amount |

The analog GSP 301 uses the GSX 300 endpoints when plugged into that sound card. Other discovered EPOS USB models may offer basic Windows controls; advanced support requires a verified model-specific adapter.

Device profiles save one audio page. Setup profiles save selected pages across devices. Live edits apply by default; named profiles change only when explicitly saved. Microphone/playback activity is opt-in and saves no recordings.

## Build and run

Use 64-bit Windows, 64-bit PowerShell, and a .NET 9 SDK with Windows Desktop support. Running a framework-dependent build requires the .NET 9 Desktop Runtime for Windows x64. No external NuGet packages are required; `NuGet.Config` clears package sources.

A source checkout does not include `dist`. Build it first:

```powershell
.\tools\Build-App.ps1
.\Start-EPOS-Control.cmd
```

To try the interface without EPOS hardware:

```powershell
.\dist\EposControl.exe --demo
```

For a portable ZIP, extract the whole package and follow its `START-HERE.md`. The package requires the Desktop Runtime and compatible EPOS components for advanced controls.

## Verify and package

```powershell
.\tools\Test-App.ps1
.\tools\Package-App.ps1 -SkipBuild
```

Default verification builds the app/helper and tests, runs offline regressions and USB command guards, and checks the WPF interface with demo adapters. It does not control live audio hardware. Reports and portable packages are generated under ignored `artifacts/` directories. Use `-SkipBuild` for packaging only after a successful verification of the current source.

## Known limitations

- GSX high-pass and a separate monitoring mute are unavailable. B20 pickup-pattern status is experimental; pattern selection remains on the physical switch.
- B20 processing restoration on app launch/reconnect defaults to off. GSX automatic processing restoration is not implemented.
- Separate experimental B20/GSX helpers have service-stopped initialization evidence. They are not installed services; cold boot and automatic GSX lifecycle remain unvalidated. Normal app startup uses vendor support.
- Control readback, listening, reconnect behavior, and fresh-start audio are different checks. Local hardware results do not establish behavior on every PC or driver installation.
- There is no installer or independent replacement audio processor/driver yet.

## Project and documentation

| Location | Purpose |
| --- | --- |
| `src/` | WPF app, core adapters/state, and experimental helper |
| `tests/` | Regression suite and reviewed control-state fixtures |
| `tools/` | Build/package scripts, optional hardware checks, and investigation utilities |
| `docs/` | User guides, development instructions, protocol research, validation, and roadmap |

Start with the [documentation index](docs/README.md), [user guide](docs/user-guide/using-app.md), [build and test guide](docs/development/building-and-testing.md), or [contributor guide](CONTRIBUTING.md). Current capabilities and remaining hardware checks are summarized in [validation status](docs/validation/status.md).

## License and provenance

Original project code is licensed under the [MIT License](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for the scope of that license and the provenance of vendor-derived research fixtures. EPOS and Sennheiser names identify the devices and software investigated; this project is not an official EPOS product.
