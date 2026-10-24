# Troubleshootr plan

Status: **Phases 0–2 done** (2026-10-24): engine, CLI, 15 checks, WPF app. Next: Phase 3 (Storage). Work phase by phase. Each phase ends with tests passing, a commit and a
push, and a line in the Progress log. Phases 0–2 come first and in order. After that, any category phase can be done in
any order (they all plug into the same engine).

## Goal

One portable tool instead of 70: open it on any Windows PC from a USB stick, click Quick scan, and within about 10
seconds see every common problem ranked by severity, with evidence and advice, plus CPU-Z / CrystalDiskInfo /
Event Viewer / Device Manager / Task Manager-level detail in tabs. Later: a technician edition with Claude Q&A and
Plannr ticket export.

**Targets:** Windows 10 1607+ and 11 fully supported; Windows 7 SP1 / 8.1 best effort (if .NET 4.7.2+ is present,
it runs with fewer features). x64 first, with x86 and ARM64 also running (AnyCPU). Release size under 15 MB. Window in
under 1 s; Quick scan under 10 s.

## Decisions (2026-10-24, researched)

1. **Stack: C# on .NET Framework 4.7.2 + WPF.** Why:
   - .NET Framework 4.8 is built into every Windows 10 from 1903 on and every Windows 11, so the exe runs with nothing to install and stays only a few MB.
   - Windows 7 is at 0.55% of Windows PCs (StatCounter, Sept 2026) and Windows 10 at about 28%, so "older versions" really means old Windows 10 builds, which this covers. Windows 7/8.1 still run it if the free 4.7.2+ runtime is installed.
   - LibreHardwareMonitorLib (the sensor library behind many tools) ships a net472 build.
   - Mature WMI, registry, event-log and P/Invoke support, and AI tools write C# well.

   Rejected options:
   - **.NET 10:** needs Windows 10 1607+, and WinForms/WPF can't be AOT-compiled, so it means a 60 MB+ bundle or a runtime install.
   - **Rust:** Windows 7 is a tier-3 target, there's no equivalent sensor library, and building would be slower.
   - **C++/ImGui:** the smallest option, but slow and error-prone to build.
   - **Tauri / Electron:** need a WebView2 or Chromium. Tauri is Windows 10+ only and a web view can be broken on exactly the PCs being diagnosed. Electron is about 100 MB.
2. **Engine as netstandard2.0 libraries + pure checks over a recorded Snapshot,** so tests run on modern .NET in seconds with no hardware (see docs/ARCHITECTURE.md).
3. **Kernel-driver features are opt-in.** WinRing0 (the old driver behind most of these tools) is flagged by Microsoft Defender since 2025. LibreHardwareMonitor moved to **PawnIO**, which is signed, installed by its own installer and blocked by some anti-cheats (FACEIT). So Troubleshootr works fully without a driver, and only live CPU temperatures/voltages and RAM SPD/timings ask to install PawnIO.
4. **Disk health without a driver:**
   - ATA SMART through `SMART_RCV_DRIVE_DATA`;
   - NVMe health through `IOCTL_STORAGE_QUERY_PROPERTY` (protocol-specific property, log page 02h), which is documented by Microsoft;
   - some drivers and USB bridges hide the data, so report "not available" honestly.
5. **License: GPL-3.0** (same as Plannr; lets us reuse GPL code such as ZenStates-Core for Ryzen timings if needed). LibreHardwareMonitor's MPL-2.0 is compatible.
6. **Unsigned exe** (the owner's standing decision): SmartScreen shows "Windows protected your PC" on first run, so click "More info → Run anyway". Document it in the README.
7. **Name: Troubleshootr** (chosen by the owner 2026-10-24, after the working names Triagr and EZDiag).

## Phases

### Phase 0: Setup
- [x] Install the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`); confirm it builds a net472 WPF project (needs the `Microsoft.NETFramework.ReferenceAssemblies` package).
- [x] Solution skeleton exactly as in docs/ARCHITECTURE.md §2; `Directory.Build.props` with LangVersion latest, nullable, warnings as errors; the net472 app manifest (asInvoker + a "Restart as admin" button, DPI-aware PerMonitorV2, supportedOS entries for 7–11).
- [x] GitHub repo `GLr-P/Troubleshootr` (public), GPL-3.0, README, .gitignore, and GitHub Actions: build + test + `troubleshootr scan` on windows-latest.
- **Done when:** `dotnet build` and `dotnet test` pass locally and in CI.

### Phase 1: Engine + CLI + first collectors
- [x] Core types (Snapshot, Section<T>, Finding, ICheck, ICollector), the parallel scan engine with timeouts and timings, scan profiles.
- [x] JSON report writer + schema version; `troubleshootr scan|collect` with `--json --timings --anonymize --categories`.
- [x] Collectors: System (OS, build, edition, device, uptime, pending reboot), CPU, Memory modules, Motherboard/BIOS, Volumes, GPU basic.
- [x] Checks: STO-008, MEM-001..005, WIN-001, 002, 004, 005, 011, PER-007.
- [x] First fixture recorded from this laptop (anonymized) + golden test.
- **Done when:** `troubleshootr scan` on this laptop finishes in < 3 s with correct info, and the tests pass on net10 and net472.

### Phase 2: WPF shell
- [x] Theme (light/dark), Home with the device header, Quick/Full scan, the category tiles and "Scan selected"; streaming results list (severity-sorted, expandable).
- [x] Info tabs reading the Snapshot: Summary, System, CPU, Memory, Motherboard, Storage (basic), Graphics (basic).
- [x] Screenshot mode (`--screenshot`), Report tab with Save JSON + text and Copy ticket summary. (HTML report moves to Phase 12.)
- **Done when:** screenshots of every tab look right in both themes; the window appears in < 1 s (measured).

### Phase 3: Storage (CrystalDiskInfo level): STO-001..016
- [ ] ATA SMART reader + attribute names table; NVMe health log reader; bus type and USB-bridge detection.
- [ ] Storage tab: per-drive health card (Good / Caution / Bad like CrystalDiskInfo), attributes table, temperature, hours, wear.
- [ ] Parser tests from saved raw SMART/NVMe bytes (record the laptop's NVMe and the desktop's drives).

### Phase 4: Crashes, errors and Windows health: CRA-*, WIN-006..017, MEM-007..009, CPU-005..007, GPU-004/005, DRV-007
- [ ] Event-log collector (filtered XPath queries, 30 days), minidump header parser (bugcheck code, parameters, faulting module), bugcheck table.
- [ ] Events & Crashes tab: BSOD list, app-crash groups, error timeline with filters.

### Phase 5: Drivers, devices, updates, GPU details: DRV-*, UPD-*, GPU-*, WIN-003/018, CPU-009
- [ ] SetupAPI collector (faster than WMI), problem-code meanings table, compact PCI/USB vendor table (only vendor names, to keep it small).
- [ ] Windows Update COM history and policy checks.
- [ ] Drivers and Devices tabs.

### Phase 6: Performance and startup (Task Manager / Autoruns-lite): PRF-*, CPU-002/003/008, MEM-006, STO-018..020
- [ ] Live Processes tab (1 s refresh, sortable, kill/open-location with confirmation), Startup tab (enable/disable with confirmation, logged), Software tab.

### Phase 7: Network: NET-001..010, WIN-012
- [ ] Adapter collector, connectivity ladder (adapter → IP → gateway → internet → DNS) with a clear "fails at step N".

### Phase 8: Security: SEC-*, STO-017, UPD-007, PRF-004, NET-011
- [ ] Signature checking (WinVerifyTrust) shared with the Startup and Processes tabs.

### Phase 9: Sensors and advanced hardware (PawnIO, opt-in): CPU-004, GPU-008, MEM-010/011
- [ ] LHM adapter; "Enable advanced sensors" flow (explain, confirm, run the PawnIO installer, detect it); live Sensors tab with min/max; SPD + XMP/EXPO profiles (RAMSPDToolkit: check its license first) and AMD trained timings (ZenStates-Core) on the Memory tab.
- [ ] Optional 30 s CPU load test while watching temperatures and clocks (throttling check).

### Phase 10: Peripherals: PER-001..006, DRV-009

### Phase 11: Battery and power: BAT-*, PRF-010

### Phase 12: Reports, fix actions, polish, v1.0 release
- [ ] HTML report (printable, inkjet-friendly), ticket text, anonymize toggle; fix actions (SFC/DISM, network reset, DNS flush, temp cleanup, spooler restart, Memory Diagnostic) with confirmation + logged output.
- [ ] Single-exe release (Costura), size check, GitHub release + Pages site, compatibility pass (see Risks), `/code-review` before release.

### Phase 13: Technician edition
- [ ] **Claude panel:** bring-your-own API key (DPAPI-encrypted), anonymized report as context, follow-up tool calls into the Snapshot, model from the current Anthropic docs.
- [ ] **Plannr export:** JSON contract doc; in Plannr, "Import diagnostic report" (match the customer/device, attach to the open ticket or create one; summary = possible issues, Windows version, device name, specs). Later: direct send through the Plannr sync Worker.
- [ ] Optional: a USB "kit" layout (Troubleshootr + Reports folder + the owner's other portable tools).

## Risks

| Risk | Mitigation |
|---|---|
| No old Windows PCs to test on (this laptop is Windows 11 Home: no Hyper-V or Sandbox) | CI on windows-latest; VirtualBox VMs with Windows 10 1809/22H2 evaluation images when needed; fixtures and checks from client PCs; Windows 7/8.1 is best effort |
| NVMe health IOCTL fails on some drivers / bridges | Honest "not available"; record raw bytes from every drive met as fixtures |
| WMI slow or broken on damaged PCs | Per-collector timeouts; registry/API fallbacks; WIN-011 tells the tech |
| Antivirus flags a hardware tool (unsigned, low-level) | No driver unless opted in; no packers or obfuscation; submit false positives to Microsoft |
| False alarms destroy trust | Conservative Critical rules; thresholds tuned on real fixtures; every finding shows evidence |
| Scope is huge (≈130 checks) | Engine first, then category phases; each check is one file + one test |
| Kernel driver (PawnIO) friction / anti-cheat | Opt-in only, explained, and uninstallable from the app |

## User checks (only the owner can do these)

- Phase 0: create nothing. The GitHub repo is made by the AI (gh is logged in).
- Phase 3+: run the release from a USB stick on the desktop and on a client PC or two; say whether anything looks wrong.
- Phase 9: approve installing PawnIO on one PC to test sensors (not on a PC used for FACEIT games).
- Phase 13: an Anthropic API key in the app's settings (console.anthropic.com), and a test import into Plannr.

## Progress log

- 2026-10-24: project folder, plan, architecture, check catalogue and AI instructions written. No code yet.
- 2026-10-24: Phases 0–2 done. .NET 10 SDK installed; solution builds net472 + netstandard2.0 with warnings as errors. Engine runs collectors in parallel with timeouts; checks stream in as their sections arrive. CLI `troubleshootr-cli.exe scan|collect|check`. Quick scan on the owner's laptop: 0.3 s. App window appears in 0.48 s (measured). 38 tests pass on net10 and net472. Fixtures: the laptop (real, anonymized) and demo-many-problems (hand-made). Off-screen screenshots of all 9 tabs in light and dark checked by eye. Repo: https://github.com/GLr-P/Troubleshootr. Decision: asInvoker manifest + "Restart as admin" (highestAvailable would show a UAC prompt on every AI test run). Microcode reads as empty on the laptop's Core Ultra (registry format to check in Phase 4, CPU-007).
