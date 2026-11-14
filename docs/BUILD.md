# Build, icon diagnosis, and release preparation

This remains a C#/.NET 8 Windows Forms application. Version 1.1.0 is a review candidate, not a published release. No storage device was formatted during this work.

## Icon findings

The original ICO is preserved. Its directory contains 32-bit 16, 32, 48, 64, 128 and 256 pixel frames, with valid bounds. The native resources in Debug, Release, Formatter and both win-x64 published executables match all six original frames byte-for-byte. `ApplicationIcon` was already working for these fresh native executables; adding another native icon reference would not fix them.

The window previously used `Icon.ExtractAssociatedIcon(Application.ExecutablePath)`. That asks the shell for the *process* icon and depends on launch context: DLL execution through `dotnet` can identify the .NET host. The form now loads a cloned icon from an assembly resource, independent of the shell and executable location. The confirmation dialog inherits it from the main form. The ICO is not copied beside the executable and is not required at runtime. Explorer uses the native EXE resources; title bar, taskbar and Alt+Tab use the form icon. Their actual appearance still needs Windows shell testing.

Run the resource test to distinguish embedding from shell caching:

```powershell
./tests/Test-ExecutableIcon.ps1 -Executable @(
  'ioDDRebuilder/bin/Debug/net8.0-windows/ioDDRebuilder.exe',
  'ioDDRebuilder/bin/Release/net8.0-windows/ioDDRebuilder.exe',
  'ioDDRebuilder.Formatter/bin/Release/net8.0-windows/IODDRebuilder.Formatter.exe',
  'DIST_ioDDRebuilder_win-x64/ioDDRebuilder.exe',
  'DIST_ioDDRebuilder_win-x64/formatter/IODDRebuilder.Formatter.exe')
```

If that passes but Explorer shows an old icon, close the old process and launch the newly published EXE directly from a fresh folder. Check its file version (1.1.0.0). Remove and recreate any pinned taskbar shortcut that points to an old build. Refresh Explorer or sign out/in if necessary. A matching native resource with a stale shell image is evidence of caching or an old shortcut, not broken embedding. A failing resource test is a build/package problem. Do not start by deleting icon caches.

Public GitHub metadata checked on November 14, 2026 lists v1.0.0, published January 18, 2026, with `ioDD-Rebuilder_v1.0.0_win-x64.zip`. That is the old package identified in the README as missing safety improvements. It is not the candidate prepared here. The ZIP was downloaded into a temporary audit folder and inspected without executing it: its EXE also contains all six original icon resources byte-for-byte. Therefore an Explorer icon failure on that package cannot be attributed to a missing native icon. Shell caching, a shortcut pointing elsewhere, or launch context remain explanations to check on the affected machine; the exact observed shell behavior was not reproduced here.

## Architecture and safety

The UI now owns a single asynchronous operation, locking selection and preventing closure until the worker stops. Scanning, revalidation, formatting, copying and optional verification run on workers. Progress returns through the WinForms dispatcher. Copying remains sequential, with large disk images first, and excluded legacy files remain excluded. Overall progress measures bytes; current-file progress has a separate bar. Speed is a whole-copy average; ETA is an estimate. Completion includes file count, bytes, elapsed time and verification status. A cancellation request reports stopping until I/O returns; partial destination files remain, and the user is told to rebuild again. Cancellation cannot interrupt formatting or undo erasure.

`Shared/FormatSafety.cs` remains the sole format implementation for both entry points. USB, system/source disk, identity, size and GUID checks remain mandatory. The elevated script now additionally rejects ambiguous system/volume/disk/application mappings and the physical disk hosting the application. No fallback formatter or cleanup was added. Files are rescanned after confirmation, including captured modification times. Source ancestry and file links are checked again before copying each file. All destination writes still use the confirmed GUID path and CreateNew.

Read-only SHA-256 comparison is isolated in `Shared/FileVerification.cs`, so matching files, mismatch detection and cancellation can be tested using temporary files without storage commands. Verification compares the current source and copied bytes; it does not provide a signed source manifest. Keep the source unchanged throughout the rebuild. Checks reduce accidental erasure risk; they do not make arbitrary hot-plugging or concurrent path changes safe.

Drive discovery shows local fixed/removable volumes with letter, label, capacity and filesystem. This list is not a promise of USB eligibility: the shared storage guard determines eligibility before confirmation. Internal drives cannot pass that guard. Source scanning and actual capacity checks occur when Review rebuild is clicked, before the erasure confirmation appears.

## Build and publish

Use Windows with the .NET 8 SDK or a compatible newer SDK. NuGet access may be needed for targeting packs. For framework-dependent output, install the x64 .NET 8 Desktop Runtime on the target Windows machine.

```powershell
dotnet build ioDDRebuilder/ioDDRebuilder.csproj -c Debug
dotnet build ioDDRebuilder/ioDDRebuilder.csproj -c Release
dotnet build ioDDRebuilder.Formatter/ioDDRebuilder.Formatter.csproj -c Release
dotnet run --project tests/SafetyTests.csproj -c Release -- docs/images
./tests/Test-StorageGuard.ps1
dotnet publish ioDDRebuilder/ioDDRebuilder.csproj -c Release -r win-x64 --self-contained false -o DIST_ioDDRebuilder_win-x64
dotnet publish ioDDRebuilder.Formatter/ioDDRebuilder.Formatter.csproj -c Release -r win-x64 --self-contained false -o DIST_ioDDRebuilder_win-x64/formatter
```

Run the native icon command above after publishing. Run `./scripts/Build-Candidate.ps1` in PowerShell 7 on Windows for the complete pipeline. The CI workflow calls this same script: it builds both configurations, runs safety tests, mock scenarios and syntax validation, publishes both projects, verifies native icons, and creates a ZIP plus SHA-256 checksum under `artifacts/`. Each ZIP includes `BUILDINFO.json` with the source commit and `MANIFEST.json` with file hashes. The archive is extracted again, its file hashes checked, and both packaged EXE icons rechecked before upload. It does not create a GitHub release. Remote CI validation is pending.

If PowerShell script execution is disabled, a process-scoped invocation such as `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-StorageGuard.ps1` runs the reviewed local test without changing the machine policy. Windows Application Control is separate; do not disable it to run unsigned builds. Use an approved development machine or CI.

## Local validation results

- Final GUI Debug and Release builds: passed, zero warnings/errors.
- Final Formatter Release build and safety-test build: passed, zero warnings/errors.
- Final win-x64 framework-dependent publish, GUI and Formatter: passed.
- Native icon test: passed for all five executable paths listed above, six matching frames each.
- First GUI revision: all 32 pre-existing C# non-destructive safety checks passed. Its screenshots exposed layout issues, which led to wider labels, a larger initial client size and explicit DPI baseline settings.
- Final expanded C# suite: builds successfully but execution is blocked by Windows Application Control (0x800711C7), even outside the sandbox. The new icon, confirmation-default and checksum tests therefore remain unexecuted locally. The earlier pass is not a final-suite pass.
- Final shared PowerShell guard templates: syntax parsed successfully; all 16 mocked scenarios passed. Because the final C# runner was blocked, this local fixture was expanded directly from the shared script templates. CI must repeat the tests using the C#-generated fixture.
- No destructive tests, commits, pushes or releases were performed. Existing screenshots are historical; final UI, multi-monitor DPI and shell behavior are not visually certified here.

## Windows review and release gate

1. Extract the complete candidate ZIP to a local folder outside any proposed destination. Launch `ioDDRebuilder.exe` directly, without clicking Review rebuild. Confirm version, title bar, Explorer, taskbar and Alt+Tab icons. The folder contains no loose ICO.
2. Run the entire non-destructive test suite and CI successfully on an approved Windows development machine. Capture updated screenshots with `dotnet run --project tests/SafetyTests.csproj -c Release -- docs/images --screenshots`.
3. Inspect layout, keyboard navigation, resizing, long folder paths and drive labels at 100%, 150% and 200% DPI and when moving between monitors. Review the preview and confirmation without accepting erasure. Confirm Enter/Escape cancel and the format button starts disabled.
4. Inspect the safety changes independently. Automated tests must continue to use mocks or temporary files, never actual storage formatting.
5. Only if a maintainer intentionally authorizes a manual hardware acceptance test: use an expendable, fully backed-up USB device with no valuable data. Check UAC refusal, format failure handling, cancellation during copying and verification, checksum mismatch and iODD compatibility. Do not test against an internal, system or source disk.
6. Regenerate the package from the reviewed commit, rerun native icon tests, include checksums and release notes, and consider code signing. Label the framework-dependent runtime requirement. Publish v1.1.0 only after explicit approval; keep the v1.0.0 warning visible until a reviewed replacement is available.
