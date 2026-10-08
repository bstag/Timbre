# Release preparation

## Verify source

From a clean source checkout on 64-bit Windows:

```powershell
.\tools\Test-Documentation.ps1
.\tools\Test-App.ps1
```

Keep the resulting offline/demo reports local. Hardware listening, service-stop, and cold-boot evidence require separate opt-in checks. Update [validation status](../validation/status.md) when the established scope changes.

## Portable ZIP

```powershell
.\tools\Package-App.ps1 -SkipBuild
```

Use `-SkipBuild` only after current-source verification; without it, packaging runs `Test-App.ps1`. Packaging checks matching app/test core DLLs, copies the app/check payload and reviewed fixtures, preserves the documentation hierarchy, includes license/notices, hashes each file, creates a ZIP, and verifies the extracted hashes and documentation links.

Output is under `artifacts/releases/`; `latest-package.json` identifies the generated ZIP, SHA256, and verified extraction. That pointer and all release outputs are ignored. Do not add binaries to the source commit. The portable helper pilot is not installed or included as automatic background support.

Extract the complete ZIP into a writable directory. Run `Verify-Setup.cmd` on a destination with the .NET 9 Desktop Runtime. Its default path checks package hashes, offline tests, demo UI, and read-only device diagnostics. Hardware flags are optional. Source-checkout links in packaged docs are converted to labeled source references; fixture references point into `checks/Fixtures`.

## GitHub preparation

Review `git status --short` and the candidate file list before staging. Expected content is source, scripts, docs, fixtures, license/notices, and repository configuration. `artifacts`, `dist`, `preservation`, `bin`, `obj`, and user data must remain ignored.

For a repository with no commits yet:

```powershell
git add .
git diff --cached --check
git diff --cached --stat
git commit -m "Initial Timbre source"
```

After creating an empty repository in your GitHub account, add its actual clone URL as `origin`, then push the local `main` branch. Avoid creating a separate remote README/license when the local initial commit already contains them. Keep generated ZIPs for a GitHub Release after source verification rather than force-adding them to Git.

The [Windows workflow](../../.github/workflows/windows.yml) uses GitHub's documented [checkout](https://github.com/actions/checkout), [.NET setup](https://github.com/actions/setup-dotnet), and [artifact upload](https://github.com/actions/upload-artifact) actions. It runs offline checks and packaging; its first hosted execution is available after a push.
