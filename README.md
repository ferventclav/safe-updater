# Safe Updater

A Windows app that checks everything on your PC that can be updated, lists only the updates that are safe to
install, and installs the ones you tick. Anything that could break something is left out of the list, and a
"Left out for safety" section at the end says why.

Built with WinUI 3 and .NET 10. Self-contained: unzip and run `SafeUpdater.exe`, nothing to install.

## What it checks

Each group appears only when the tool is on the PC. A missing tool is skipped, never an error.

| Group | How it checks | How it updates |
|---|---|---|
| Apps | `winget upgrade` (works in any display language) | `winget upgrade --id <id> -e` |
| Chocolatey | `choco outdated` | `choco upgrade <id>` (admin step) |
| Scoop | `scoop status` | `scoop update <app>` |
| Windows and drivers | Windows Update API, each driver matched to its device | Windows Update API (admin step) |
| NVIDIA driver | The NVIDIA app's local service, if present | Opens the NVIDIA app |
| Python packages | `pip list --outdated`, user site and the interpreter's own site | `pip install --upgrade <pkg>==<version>` |
| Node packages | `npm outdated -g` | `npm install -g <pkg>@<version>` |
| Rust | `rustup check` | `rustup update` |
| Claude Code | `claude --version` | `claude update` |

Windows asks for admin once, for winget, Chocolatey and Windows Update together. Everything else runs as you.

## How it decides what is safe

Every update gets one of three outcomes: **hidden** (unsafe or redundant, can't be ticked), **listed but
unticked** (safe, your call) or **listed and ticked** (routine). The hide rules run in this order and the first
match gives the reason shown:

| # | Rule | Hidden when |
|---|---|---|
| 1 | Hidden by you | You pressed "Don't show this update" (undo with Show again) |
| 2 | Not safe here | The source found a problem on this PC: conda-managed or OS-managed Python, a read-only install folder, a Scoop app that is held, global or failed |
| 3 | Pinned | Pinned in winget or Chocolatey |
| 4 | Owned by another tool | A winget app that Chocolatey manages; winget's Rustup entry when rustup is present; the npm copy of Claude Code |
| 5 | Older version | The offered version is older than the installed one |
| 6 | Pre-release | Alpha, beta, rc, preview or nightly offered for a stable install |
| 7 | Would break another package | Another installed pip package requires the current version |
| 8 | Windows Update | Optional updates (previews); drivers with no date, whose device can't be found, or that are not newer than the driver already on the device |
| 9 | Major version | A new major version of a pip or npm library, or of a runtime or database (Node, Python, Java, PostgreSQL and so on), until you turn on Include major versions |

Listed but unticked: winget apps with an unknown installed version, optional drivers that are newer, the
same-family entry once majors are on, and the NVIDIA driver (installed in the NVIDIA app). Everything else that
survives the rules is ticked. When in doubt a rule hides rather than updates: a missed update can wait for the next
check, a broken tool can't.

Every rule has a row in `tests/SafeUpdater.Core.Tests/PolicyTests.cs`, and a test fails if a new rule is added
without one.

## Safety nets

- **Restore point** before the admin step (on by default; skipped if Windows made one in the last 24 hours).
- **Python snapshot** of both scopes before any pip update, a dry run that refuses unpicked major jumps or
  downgrades of dependencies, `pip check` afterwards, and **Roll back the last Python update** in the menu.
- **Tool check** before and after a pip update: imports and exercises what is installed (PDF, Office files,
  images, Whisper on a cached model, Playwright). A check that passed before and fails after rolls the update
  back. Nothing is downloaded; missing libraries are skipped.
- **PATH repair**: winget's command links and your user PATH are snapshotted and restored if an upgrade drops them.
- **Warnings** before updating: pending restart, running on battery, under 10 GB free, and open apps that an
  installer would close.

## Build

```bash
dotnet test tests/SafeUpdater.Core.Tests -c Release
```

```bash
dotnet publish src/SafeUpdater/SafeUpdater.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o publish/SafeUpdater
```

Releases are built by GitHub Actions from a `v*` tag (`.github/workflows/release.yml`). The app is not
code-signed, so SmartScreen may warn on first run: choose More info, then Run anyway.

Settings and logs live in `%LOCALAPPDATA%\SafeUpdater`.

## Licence

MIT
