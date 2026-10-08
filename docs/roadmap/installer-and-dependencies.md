# Timbre installer and dependency plan

Status: proposed installer, updated through 2026-10-08. See [current validation status](../validation/status.md) for the verified scope. An experimental B20 helper now initializes and maintains processor objects; no installed replacement background service, installer or replacement audio driver has been built. The current transferable app deliverable is a verified portable ZIP.

## Intended result

Provide one `Timbre-Setup.exe` that installs the control app and its required components, checks the connected device, and supports repair, upgrade, and removal. The installed app, background processes, audio processor, and driver remain separate Windows components. A single download does not make those components independent of EPOS.

The first release should preserve the working B20 controls and minimize dependencies. Replacing the DSP or USB audio driver is a separate milestone. GSX 300 microphone gate/filter/EQ, playback mode/EQ/reverb and hardware USB sidetone now pass control/readback and restoration checks. User listening and basic reconnect/settings retention now pass on the local installation. An experimental fresh-object control pilot also passes with vendor support stopped; audible DSP after fresh GSX creation, cold boot, and automatic lifecycle still need validation. Other models need their own adapters and evidence before inclusion.

## What is working and what it depends on

| Component / feature | Current implementation | Packaging implication |
| --- | --- | --- |
| Control app | WPF, .NET 9, Windows x64 | Current ZIP requires a separately installed Desktop Runtime. A self-contained release can include its runtime. |
| Microphone/playback gain and mute | Windows Core Audio | Uses the selected Windows audio endpoint; our control code does not call Gaming Suite. |
| B20 sidetone level and monitoring mute | Windows DeviceTopology controls | Requires the compatible EPOS driver exposing the validated B20 topology. Behavior with the generic USB audio driver has not been established. |
| Gate, noise filter, high-pass, microphone EQ | EPOS audio processor (`EAPO.dll`) and existing shared memory/mutex/event | Processor must be installed, attached to the endpoint, and active. Our app currently opens existing objects; it does not initialize or maintain them. |
| GSX sidetone | Device-matched HID query/setter with verified raw readback | Uses the existing GSX control collection. The app adds no driver. Exact-value profiles, guarded recovery, and user listening pass on the source PC. This does not establish calibrated units or a separate mute. |
| Profiles | Our local JSON stores for device pages and selected multi-device setups | Profiles/setup/processing state now use per-user LocalAppData and survive app replacement. The rename starts with fresh Timbre data; explicit isolated data directories remain supported. Source-machine data is excluded from ZIPs. |
| B20 pickup-pattern display | Experimental vendor-HID status query | Cardioid captured; repeated requests can time out. Physical switching/reconnect remain unvalidated. |
| Background operation | EPOS components currently installed and running; separate experimental B20/GSX helpers | B20 fresh creation, managed reconnect and gate audio pass with vendor support stopped. GSX fresh-object creation/control checks pass separately. Cold boot, installed lifecycle and fresh-GSX audio remain unvalidated. Normal startup keeps vendor support enabled. |

Evidence: [current implementation](../development/app-design.md), [processing protocol](../development/microphone-protocol-and-tests.md), [sidetone mapping](../research/b20-sidetone.md), [WindowsApoMemory source](../../src/Timbre.Core/WindowsApoMemory.cs), and [profile source](../../src/Timbre.Core/Profiles.cs).

The preserved `eposaudio.inf` installs `eposaudio.sys`, registers `EAPO.dll` as an endpoint effect, and includes B20 VID/PID `1395:009F`. Its `EPOSAudio` service is the kernel driver service; it is distinct from `EPOSGamingSuiteService`, the user-mode Gaming Suite service. Both the preserved driver binary and catalog passed local Authenticode verification on 2026-10-05. That check is evidence about the preserved files, not proof that they install successfully on every target Windows version. See [preservation evidence](../research/local-investigation.md#preservation-and-verification).

## First decision: the minimum working installation

Before implementing service replacement, run a controlled dependency experiment. Record every change and restore the original process/service state afterward. This plan does not stop services or uninstall anything automatically.

| Test condition | Question to resolve |
| --- | --- |
| Current installation, Suite UI and service running | Establish complete control and audio baseline. |
| Suite UI closed, service running | Can our app provide all supported controls without the vendor interface? Does any Suite process silently restart? |
| Suite UI closed, Gaming Suite service temporarily stopped | Which shared objects survive? Can effects be read, applied, and heard on a newly opened capture stream? |
| Reconnect B20 under each verified condition | Who recreates device state and shared objects? Does identity/profile matching remain correct? |
| Reboot and sign in under the chosen configuration | Which components must start before the app can control processing? Which settings persist? |
| Clean second Windows installation with B20 connected | Can the minimum component set be installed and reproduced without relying on this machine's registry or cached state? |

For each condition, record endpoint identity, driver/component versions, component startup state, shared-object availability, control readback, actual audio behavior, and restoration outcome. Test a fresh capture stream: objects or cached settings left by an earlier process can make a stopped-service test appear independent. A reboot test is necessary to settle startup behavior.

Reuse existing offline tests and explicit hardware diagnostics. Gate already has an audio-path check; the user confirmed noise filtering, high-pass and audible differences between EQ presets on 2026-10-05. Sidetone has hardware readback/isolation checks and user-confirmed listening. Repeat those checks on the chosen minimum installation: listening confirmation under the current configuration does not establish service independence. Do not treat state readback alone as DSP verification.

### Recorded dependency experiment, 2026-10-05

The user ran `tools/Test-SuiteDependencies.ps1 -StopSuiteTemporarily` from administrator PowerShell. The script closed the Suite UI, temporarily stopped `EPOSGamingSuiteService`, and restored both. Reports are in `artifacts/dependencies/20261005-194706`.

| Condition | Observed result |
| --- | --- |
| UI and service running | Processing and sidetone readable; pattern query timed out |
| UI closed, service running | Processing and sidetone still readable; pattern query timed out in this run |
| UI closed, service stopped | Processing shared interface unavailable (`No handle of the given name exists`); effects-write and gate-audio checks skipped. Sidetone live level/mute/isolation and restoration passed. |
| Immediately after restart | Service/UI running, but processing interface still unavailable |
| Later recovery snapshot | Processing interface available; every mapped processing setting, sidetone setting and all endpoint Windows level/mute states matched baseline. No corrective settings writes were needed. |

The initial warning “Effects differs after Suite restart” compared available state with an unavailable interface, rather than two different settings. `recovery-followup.json` and `recovery-assessment.json` document eventual recovery with zero differences. The helper now waits for interfaces that were present at baseline, separately reports availability failures, and records skipped hardware checks explicitly.

**Keep the Gaming Suite service as a current processing-control dependency.** This experiment establishes interface availability under these conditions; it does not prove whether already initialized audio streams continue applying DSP after the service stops, nor whether our own helper can recreate the missing objects. Sidetone needs no Gaming Suite service in this warm-session test, but still uses the installed EPOS driver. No driver removal, reboot, physical reconnect or clean installation was tested. The dependency outcome does not justify removing vendor components yet.

Follow-up on 2026-10-06: with our object-retention helper running, the Suite-stopped B20 processing interface stayed available and all tested gate/filter/high-pass/EQ control transactions passed and restored their initial state. The actual gate measurement was inconclusive because ambient input changed. The service/UI were restored and no settings differences remained. See [helper evidence](../research/apo-control-host.md#service-stopped-experiment-2026-10-06). This narrows the service requirement to unresolved initialization/lifecycle responsibilities; it does not establish cold-start independence or justify permanent removal yet.

## Installer implementation after dependency discovery

1. **Publish a self-contained x64 app and matching checks.** This removes the separate .NET installation step. Maintain the bundled runtime through app releases. Microsoft documents both the deployment mode and its runtime servicing tradeoff in [.NET publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/).
2. **Choose the vendor-component source.** For a public bundle, first establish permission to redistribute the installer, driver, processor, and any required service files. The preservation copy does not establish those permissions. Until resolved, design setup to accept a compatible user-supplied original package or detect an existing compatible installation. Do not distribute local preservation binaries by default.
3. **Install intact driver packages through Windows installation mechanisms.** Preserve the vendor INF, catalog, and binary contents. Copying a DLL or SYS file alone does not install the endpoint effect or driver. Verify package identity/version/signature, handle failure and reboot requirements, and record which components setup actually installed. The original full MSI may install more than the eventual minimum set; separate component installation remains unproven.
4. **Install background support only for established responsibilities.** If the vendor service remains necessary, detect it and report its requirement. If our own helper can replace it, implement and test initialization, persistence, reconnect handling, bounded IPC, and shutdown first. Choose a per-user startup helper or Windows service based on those needs. A service is not inherently required for the controls that already work through Windows APIs.
5. **Separate program files and user settings.** Install immutable app files under Program Files. Device profiles use `%LOCALAPPDATA%\Timbre\profiles.json`; the rename starts with fresh settings without automatic import from the old app. Multi-device setup snapshots use `setups.json` in the same user folder. Preserve the live/manual control option and explicit named-profile/setup saves. Any future automatic profile restoration should require a user-selected profile and retain device identity and concurrent-edit protections.
6. **Provide setup lifecycle support.** Include installation logging, dependency diagnostics, repair, version-aware upgrades, interrupted-install recovery, and uninstall. Remove only components owned by our installation; avoid removing an EPOS package used by another connected device. Preserve profiles unless the user explicitly chooses to remove them.
7. **Keep verification available after setup.** Run safe diagnostics by default, with separate explicit live hardware checks. Ship reviewed fixtures and matching check binaries. Continue hashing release payloads and verifying extracted/installed contents.

Update [Package-App.ps1](../../tools/Package-App.ps1) and [Verify-Setup.ps1](../../tools/portable/Verify-Setup.ps1) along with the publishing change: today's package script copies an explicit framework-dependent file list, and the checker invokes a separately installed `dotnet` executable for the test DLL. Publishing only the app as self-contained would leave verification dependent on an installed runtime. Choose and verify an app-local runtime or self-contained check executable as part of the same release.

The installer technology is not selected yet. Select it after the component installation and servicing requirements are known.

## Completion criteria for the first installer

| Area | Required evidence |
| --- | --- |
| Existing behavior | Current source and portable checks: 481 regressions and 116 WPF scenarios pass. GSX microphone/playback processing and USB sidetone readback/restoration pass with the service running. Native computer-use device/setup profile restoration preserves excluded controls. The portable app includes these controls and matching checks; the experimental B20 host remains a separate source-built pilot. Listening and startup checks are still separate. |
| Clean setup | Installs and starts on a second supported Windows machine without a development SDK or preinstalled .NET runtime. All remaining EPOS requirements are explicit. |
| B20 function | Gain/mute, sidetone, gate/filter/high-pass/EQ and full profile restore work under the documented component configuration. Audio-effect checks cover more than readback. |
| Restart/reconnect | Reboot, sign-in, unplug/replug, and launching with the B20 absent yield correct availability and identity handling. |
| Failure handling | Missing/incompatible components, denied elevation, failed installation and interrupted upgrade produce actionable diagnostics without leaving our settings half-applied. |
| Removal/upgrade | Profiles survive an upgrade; repair restores our files; uninstall preserves unrelated devices and packages. |
| Distribution | Vendor component source and permission status are documented. The installer accurately states its remaining EPOS dependencies. |

## Later independence milestone

A fully independent implementation must replace every vendor component that remains necessary. Replacing the Gaming Suite UI or service alone does not replace `EAPO.dll`'s processing algorithms or the USB audio driver.

Windows supports custom user-mode audio processing objects packaged with audio installation components. An independent processor would need gate, filtering, high-pass and EQ implementations, a supported endpoint integration, realtime-safe processing, and audio behavior tests. Noise suppression is likely to need the most algorithm evaluation; matching EPOS sound exactly has not been established. [Microsoft APO implementation and packaging](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects)

Whether B20 hardware controls can operate with the Windows class driver still needs investigation. If a new kernel-mode driver is necessary, production deployment brings Microsoft's driver-signing requirements and a separate driver validation effort. That work is not required merely to package the current app with an existing compatible package. [Microsoft kernel-mode signing requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-requirements--windows-vista-and-later-)

Direct USB access cannot replace processing that happens on the PC. USB capture, a new DSP engine, VST hosting, surround replacement and firmware updating remain outside the first installer scope.

## Immediate next work

The app now has [typed processing persistence and opt-in connection restore](../user-guide/processing-state.md), with isolated native restoration tests. The [managed helper](../research/b20-host-lifecycle.md) now reads that store and passes isolated/native, executable process and actual B20 fresh-creation/saved-restore/control checks with the service stopped. Physical B20 reconnect and gate audio subsequently passed; validate cold startup next. The [fresh initializer](../research/apo-initialization.md) passed actual B20 creation/control checks with the service stopped. That experiment used a current diagnostic snapshot to preserve unmapped configuration. B20 service-stopped gate audio now passes with a fresh-object managed helper. Cold boot remains unvalidated; fresh-session GSX DSP measurement remains a separate next check. Retain the existing service until those lifecycle checks pass. The user has confirmed basic B20 processing by listening and supplied Suite's EQ frequency labels. See [B20 feature findings and control priorities](../research/b20-feature-findings.md) and [pickup-pattern investigation](../research/b20-pickup-pattern.md).
