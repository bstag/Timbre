# Building and testing

## Prerequisites

Use 64-bit Windows and PowerShell with a .NET 9 SDK including Windows Desktop targeting support. The app is WPF and built for x64. Running its framework-dependent executable requires `Microsoft.WindowsDesktop.App` 9.x. The SDK can build the console tests/helper; WPF verification also needs the Desktop Runtime.

Check the installation with `dotnet --list-sdks` and `dotnet --list-runtimes`. No external NuGet packages are used. The repository's `NuGet.Config` clears package sources; required framework packs must be available with the SDK installation.

EPOS hardware, Gaming Suite, and the vendor driver are unnecessary for offline tests/demo mode. Real advanced controls require the compatible installed EPOS components in [validation status](../validation/status.md).

## Source checkout

Run from the repository root:

```powershell
.\tools\Build-App.ps1
.\dist\Timbre.exe --demo
```

Build produces `dist/Timbre.exe` and the separate experimental helper under `dist/host`. These outputs are ignored. `Start-Timbre.cmd` requires a successful build; it is not a prebuilt download.

Close a running copy from `dist` before rebuilding into that directory; Windows locks its loaded DLLs. To keep the live app open, publish and verify separate outputs:

```powershell
.\tools\Test-App.ps1 -OutputDirectory artifacts/verification-build
```

`Build-App.ps1` and `Package-App.ps1` also accept `-OutputDirectory`. These scripts resolve relative paths against the repository root and retain `dist` by default. The test runner uses the chosen executable for demo UI verification. Experimental GSX checks can consume those matching outputs with `Test-GsxInitialization.ps1 -BuildDirectory artifacts/verification-build`; this does not change the running app or its storage. After verification, `Package-App.ps1 -SkipBuild -OutputDirectory artifacts/verification-build` packages the same selected binaries and checks their core hash against the tests.

## Normal verification

```powershell
.\tools\Test-Documentation.ps1
.\tools\Test-App.ps1
```

`Test-App.ps1` builds the app/helper, regression suite, and GSX probe, then runs regression checks, fixture-free GSX initialization rules, offline probe guards, and WPF scenarios with demo adapters. It never opts into live hardware checks. Native tests use isolated object names. Reports are `artifacts/tests.json`, `artifacts/tests.xml`, `artifacts/gsx-initialization-checks.json`, `artifacts/app-preview.png`, and `artifacts/app-preview.png.json`.

`Build-App.ps1 -RunTests` builds and runs regressions but does not perform the full WPF/probe verification. `Test-App.ps1 -SkipBuild` runs the last built binaries; use it only when those binaries match the intended source.

The separate inspection utility can be built with:

```powershell
dotnet build tools/AssemblyInspector -c Release --configfile tools/AssemblyInspector/NuGet.Config
```

Offline native tests need ordinary Windows file rename and named-object permissions. A restricted execution sandbox can produce access-denied failures or different ACLs; inspect those failures before classifying them as project defects. Hardware/service experiments are unnecessary to resolve offline verification.

## Optional hardware workflows

Hardware flags are explicit opt-in. They may temporarily alter processing, query USB, capture microphone metrics, or play a quiet test tone. See [tools](../../tools/README.md), [protocol/tests](microphone-protocol-and-tests.md), and the [home checklist](../validation/home-validation.md). Service-stop/audio-engine experiments belong to their device's research guide and require a fresh baseline and recovery verification.

Some historical replay/prepared-run scripts require ignored local reports or startup snapshots. They are not part of the clean-checkout regression command. Do not manufacture fixtures to make an investigation appear to have passed.

## CI and packages

[Windows CI](../../.github/workflows/windows.yml) runs documentation checks, normal verification, the inspection utility build, and portable packaging on pushes and pull requests. It uploads the offline/demo reports. A hosted CI pass does not establish physical hardware behavior. See [release preparation](releasing.md) for package verification and the first GitHub push.
