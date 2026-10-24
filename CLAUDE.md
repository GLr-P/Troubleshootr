# EZDiag: instructions for AI agents

EZDiag is a free, open-source, portable Windows diagnostic tool: **the first thing a technician opens when a PC has a
problem.** It runs from a USB stick without being installed, scans the PC in seconds, flags the most common problems
by how serious they are, and shows full hardware, driver and system information. It's for advanced users: technicians
and enthusiasts.

Hub page: [../Information/projects/ezdiag.md](../Information/projects/ezdiag.md). Plan and progress: [PLAN.md](PLAN.md).

## Read on demand (don't preload)

| File | When |
|---|---|
| [PLAN.md](PLAN.md) | Start of work: find the current phase, then read only that phase |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Before touching the engine, collectors, the UI shell or the tests |
| [docs/CHECKS.md](docs/CHECKS.md) | Before adding or changing a check: the catalogue of every check, with ids, data sources and thresholds |

## Stack (decided 2026-10-24, see PLAN.md "Decisions")

- **C#, .NET Framework 4.7.2**, so there's no runtime to ship: Windows 10 1803+ and Windows 11 already have it, and Windows 7 SP1/8.1 run it if 4.7.2+ is installed. **WPF** UI. Do NOT move to .NET 5+ or add any web views (no WebView2 or Electron): it has to work on damaged PCs.
- The engine (`EZDiag.Core`, `EZDiag.Collectors`, `EZDiag.Checks`) targets **netstandard2.0**, so the unit tests run fast on modern .NET while the app runs on net472.
- Hardware sensors: **LibreHardwareMonitorLib** (MPL-2.0). Its kernel driver (PawnIO) is **optional and opt-in**; everything except the live sensors and RAM SPD/timings must work without it and without admin rights.

## Commands

The SDK is the .NET 10 SDK. If `dotnet` isn't found, use `"C:\Program Files\dotnet\dotnet.exe"`.

| Task | Command |
|---|---|
| Build everything | `dotnet build -c Release` |
| Unit and golden tests (fast, no hardware) | `dotnet test tests/EZDiag.Tests` |
| Scan this PC from the command line | `dotnet run --project src/EZDiag.Cli -- scan --json --timings` |
| Record this PC as a test fixture | `dotnet run --project src/EZDiag.Cli -- collect --out tests/fixtures/<name>.snap.json --anonymize` |
| UI screenshots (off-screen, no window) | `dotnet run --project src/EZDiag.App -- --screenshot all --fixture tests/fixtures/<name>.snap.json --out test-results/screens` |
| Release build (one exe) | `pwsh scripts/publish.ps1` (output in `release/`) |

## Rules (non-negotiable)

1. **Checks are pure functions of a Snapshot.** A check never calls WMI, the registry or the disk. Collectors gather raw data into the Snapshot; checks read it and return Findings. That's what makes every check testable with a JSON fixture.
2. **Every collector must survive a broken PC:** missing WMI classes, a corrupt WMI repository, no admin, Windows 7/8.1 APIs, timeouts. On failure it records `Unavailable(reason)` and the scan carries on. Never throw out of a collector.
3. **Speed budget:** the window appears in < 1 s; Quick scan finishes in < 10 s on a normal PC; each collector has a timeout (default 5 s). Run `scan --timings` after adding a collector and keep it within budget. Never use `Win32_Product` (it triggers MSI self-repair and takes minutes); use the registry Uninstall keys.
4. **Read-only by default.** Anything that changes the PC (fix actions, installing PawnIO, running SFC) needs an explicit button press and a confirmation, and is logged in the report.
5. **Portable:** no installer, no registry writes, no files outside the app folder except `%TEMP%`. Settings and reports live next to the exe (fall back to `%LOCALAPPDATA%\EZDiag` if the drive is read-only).
6. **No false alarms.** A finding marked Critical must be near-certain. When in doubt, use Info or Warning and explain. Every finding has a plain-English title, the evidence (the actual numbers), and a "what to do" line.
7. **Privacy:** the report has an anonymize option (strips user names, serial numbers, MACs, Wi-Fi names). Fixtures committed to the repo are always anonymized. Never commit a client's data.
8. **Size:** the release exe plus DLLs stay under 15 MB. Ask before adding any dependency over 1 MB. Prefer P/Invoke and the built-in APIs.

## How to add a check (the common task)

1. Find or add its row in `docs/CHECKS.md` (id like `STO-004`, category, data source, thresholds).
2. If the data isn't collected yet, add it to the right collector and to the Snapshot model (with `[Unavailable]` handling).
3. Write the check class in `src/EZDiag.Checks/<Category>/` implementing `ICheck`.
4. Add a golden test: a fixture snapshot (or a small hand-made one) plus the expected findings.
5. `dotnet test`, then `scan --timings` on this PC, then mark it done in CHECKS.md.

## Testing tiers (the owner's PC is in use, so don't open windows)

- No visible change: `dotnet test` only.
- UI change: also the off-screen screenshot of the affected tab; open the PNG and look at it.
- Before a release: full test run + `scan` on this PC + screenshots of every tab + the net472 test pass (`dotnet test -f net472`).

## Commit identity

`GLr-P <241555371+GLr-P@users.noreply.github.com>`. Push without asking. Never commit fixtures that aren't anonymized.
