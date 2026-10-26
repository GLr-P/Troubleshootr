# Troubleshootr plan

Status: **Phases 0–3 done** (2026-10-24): engine, CLI, WPF app, drive health. Next: Phase 4 (crashes and event logs). Work phase by phase. Each phase ends with tests passing, a commit and a
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

### Phase 3: Storage (CrystalDiskInfo level): done except STO-007 (needs the Phase 4 event-log collector) and STO-015 (link speed)
- [x] ATA SMART reader + attribute names table; NVMe health log reader; bus type and USB-bridge detection.
- [x] Storage tab: per-drive health card (Good / Caution / Bad like CrystalDiskInfo), attributes table, temperature, hours, wear.
- [x] Parser tests from saved raw SMART/NVMe bytes (record the laptop's NVMe and the desktop's drives).
- [x] SMART through USB enclosures via SCSI ATA pass-through (SAT, IOCTL_SCSI_PASS_THROUGH, admin) like CrystalDiskInfo. Verified 2026-10-24 on a Samsung 860 EVO in a USB enclosure (fixture win11-laptop-admin-usb-sat-smart). NVMe-in-USB bridges (Realtek RTL9210, JMicron JMS583) need vendor commands: not done.

### Phase 4: Crashes, errors and Windows health: done (v0.3.0)
- [x] Event-log collector (one filtered XPath query per provider, 30 days), minidump header parser (bugcheck code, parameters, driver at fault from the triage driver list), bugcheck table (CRA-007), CBS.log/services/profile/Reliability Monitor collector.
- [x] Events & Crashes tab: blue screens, unexpected shutdowns, app-crash groups, hardware errors, service failures, crash settings, error timeline with a filter box.

### Phase 5: Drivers, devices, updates, GPU details: done (v0.4.0)
- [x] SetupAPI collector (faster than WMI), problem-code meanings table, compact PCI/USB vendor table (only vendor names, to keep it small).
- [x] Windows Update COM history and policy checks; activation; Windows 11 readiness.
- [x] Devices, Drivers and Updates tabs; displays on the Graphics tab. UPD-004 (online search for pending updates, slow) moved to Phase 12.

### Phase 6: Performance and startup (Task Manager / Autoruns-lite): done (v0.5.0)
- [x] Live Processes tab (1.5 s refresh, sortable, filter, open file location), Startup tab, Software tab, Performance tab (scan sample, power plan, page file, visual settings, reclaimable space).
- [ ] Moved to Phase 12 (fix-action framework, needs confirmation + logging): end process, enable/disable startup items.

### Phase 7: Network: NET-001..010, WIN-012: done (v0.6.0)
- [x] Adapter collector, connectivity ladder (adapter → IP → gateway → internet → DNS → web) with a clear "fails at step N".
- [x] Network tab (ladder, speed test button, Wi-Fi, adapters, proxy, hosts file, Winsock). Metered connection (NET-008) left out: it needs WinRT, which netstandard2.0 can't call simply.

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
- Phase 6: open the Processes tab in the real app for a few seconds and say whether the numbers update and look like Task Manager's.
- Phase 9: approve installing PawnIO on one PC to test sensors (not on a PC used for FACEIT games).
- Phase 13: an Anthropic API key in the app's settings (console.anthropic.com), and a test import into Plannr.

## Progress log

- 2026-10-24: project folder, plan, architecture, check catalogue and AI instructions written. No code yet.
- 2026-10-24: Phases 0–2 done. .NET 10 SDK installed; solution builds net472 + netstandard2.0 with warnings as errors. Engine runs collectors in parallel with timeouts; checks stream in as their sections arrive. CLI `troubleshootr-cli.exe scan|collect|check`. Quick scan on the owner's laptop: 0.3 s. App window appears in 0.48 s (measured). 38 tests pass on net10 and net472. Fixtures: the laptop (real, anonymized) and demo-many-problems (hand-made). Off-screen screenshots of all 9 tabs in light and dark checked by eye. Repo: https://github.com/GLr-P/Troubleshootr. Decision: asInvoker manifest + "Restart as admin" (highestAvailable would show a UAC prompt on every AI test run). Microcode reads as empty on the laptop's Core Ultra (registry format to check in Phase 4, CPU-007).
- 2026-10-24: Phase 3 done (v0.2.0). Storage collector: MSFT_Disk/PhysicalDisk (+ Win32_DiskDrive for Windows 7), NVMe health log via IOCTL_STORAGE_QUERY_PROPERTY, which **works without admin** on the laptop's Micron 2400 (stornvme), ATA SMART via root\wmi MSStorageDriver_FailurePredict* and MSFT_StorageReliabilityCounter (both admin), drive letters via IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS (MSFT_Partition returns nothing without admin). 13 storage checks + DiskHealth.Assess (Good/Caution/Bad shared by the tab and checks). Storage tab: per-drive card + SMART table. 57 tests. Storage collection 170 ms warm, ~0.9 s cold. Not verified on real hardware: ATA SMART path (the laptop is NVMe only; parser tested with built bytes), admin-only paths (CI runs elevated on a VM). The volume dirty flag reads null without admin.
- 2026-10-24: v0.2.1. SMART through USB enclosures (SAT pass-through, admin) verified on the owner's external Samsung 860 EVO: 9,551 h, 24 °C (attribute BE), 1% worn. Also: `troubleshootr-cli anonymize` (turns an admin run's report into a fixture), dirty-flag read retried with a read handle. Still unverified: MSFT_StorageReliabilityCounter returned nothing even as admin on the laptop (check the DeviceId match).
- 2026-10-24: Phase 4 done (v0.3.0). 26 new checks (CRA-001..007, WIN-006..010/013..017, MEM-007..009, CPU-005..007, GPU-004/005, DRV-007, STO-007). Collectors: Events (1.6 s on the laptop, inside the Quick budget; Diagnostics-Performance needs admin), Crashes (dump files need admin to read; listing and the WER 1001 event work without), Health (services, CBS.log tail, temp profile, Reliability index ~0.7 s). Verified on the owner's real 0x119 dump (2026-09-23): header code and parameters match the event log; the triage driver list (file-relative offsets, stride 0x90, 249 drivers) parses. A driver is only blamed when the bugcheck's own address parameter points into it; a stack scan was tried and dropped because it blamed WdFilter.sys (Defender) for the GPU crash. Not verified on real data: the driver-at-fault match (no 0xD1/0x0A/0x50-type dump on hand; tested with built bytes), WHEA/memory-diagnostic/TDR events (none on the laptop). Microcode now reads on Windows 11 25H2 (4-byte "Update Revision"). New CLI command `dump`. Anonymizer now scrubs user/device names and C:\Users\<name> out of event text. 89 tests on net10 + net472.
- 2026-10-24: Phase 5 done (v0.4.0). 18 new checks (68 total): DRV-001/002/004/005/006/008/010, GPU-002/003/006/007/009, UPD-001/002/003/005, WIN-003, WIN-018, CPU-009; DRV-003, GPU-010, UPD-006 are tab displays. Collectors: Devices (SetupAPI + CM_Get_DevNode_Status, driver keys read through one open Class key: 0.9–1.2 s for 283 devices; registry root reopened per value took 3.5 s), Displays (EnumDisplayDevices/Settings, 0.1 s), Updates (WU agent COM QueryHistory, late-bound, no admin, 0.2–0.3 s), activation in Health (WMI SoftwareLicensingProduct, 1.2 s). Events now run 4 queries in parallel (1.2 s, was 2.5–3.5 s). Quick scan 2.5 s on the laptop. Left out: per-driver unsigned scan (Win32_PnPSignedDriver took 11 s). Reports no longer store computed properties (IgnoreReadOnlyProperties). Anonymizer blanks Bluetooth device names/ids and printer MAC fragments. New fixture win11-laptop-devices (real, non-admin, all sections). False alarms found and fixed on real data: WUDFRd 219 boot noise, a USB display adapter called an old graphics card, a VPN adapter flagged as an old driver. 113 tests on net10 + net472.
- 2026-10-24: Phase 6 done (v0.5.0). 10 new checks (78 total): PRF-001/003/005/009, CPU-002/003/008, STO-018/019/020; PRF-002/006/008 are tab displays; PRF-007 is covered by WIN-009 + PRF-003. Collectors: Performance (3 s sample: GetSystemTimes minus our own CPU, PDH per-disk % Idle Time and % Processor Performance, per-process CPU/IO/memory via a shared ProcessSampler), Startup (Run keys both views, Startup folders, StartupApproved on/off, non-Microsoft logon/boot tasks via Task Scheduler COM), Software (Uninstall keys, default browser, Chromium extension counts), Tuning (power plan + max processor state, power status, page file, visual effects/Game Mode/HAGS, cleanup sizes counted in parallel with a 2.5 s budget each). .NET PerformanceCounter took 13 s to initialise, so PDH is called directly (0.4 s). Quick scan about 4 s on the laptop. Real-data fixes: startup count now matches Task Manager (scheduled updater tasks listed separately); cleanup budget per folder (one temp folder starved the rest); account SIDs scrubbed from task names. Live Processes tab not exercised in a real window (screenshot mode shows the scan sample; ProcessSampler is unit-tested on this PC). 121 tests on net10 + net472.
- 2026-10-25: Phase 7 done (v0.6.0). 9 new checks (87 total): NET-002..009, WIN-012; NET-001 and NET-010 are the Network tab (NET-010 = a user-pressed download test, also `troubleshootr-cli speedtest`). Collectors: Network (NetworkInterface/GetAdaptersAddresses, static DNS from the Tcpip interface keys, wlanapi current connection + BSS list for the exact frequency, WinINET/WinHTTP proxy, hosts file, Winsock protocol and name-space catalogs; 0.1 s) and Connectivity (20 pings each to the router, 1.1.1.1 and 8.8.8.8 fired 50 ms apart (the better internet target counts), TCP 1.1.1.1:443 for networks that block ping, DNS lookup through Windows' resolver with the cache bypassed (DnsQuery_W; NCSI keeps the test name cached), Microsoft's connecttest page with redirects off to spot sign-in pages, SNTP clock offset from time.windows.com with the web Date header as fallback; 1.3 s, all in parallel with a 6.5 s backstop). Code review fixes before release: bridges/Hyper-V switches/cellular count as connected, only 169.254 counts as "no IP", hosts hijacks kept even past entry 200, ad-block lists no longer warn, speed test can't hang on a stalled download. Quick scan 3.4 s on the laptop. Verified on the laptop: Wi-Fi values match netsh (5 GHz ch 161, 802.11ax, WPA2-Personal), ladder all OK, clock 3.4 s off, speed test 220 Mb/s. Wi-Fi works here because location access is on; on 24H2+ with it off, SSID reads fail (handled, the Wi-Fi card just doesn't show). Not verified on a real broken network (ladder failures, APIPA, captive portal: unit-tested only). Anonymizer: MAC, SSID, BSSID, global IPv6, DNS suffix and hosts-file names are blanked; [Sensitive] now works on string lists. New fixture win11-laptop-network (real, non-admin, all sections). 134 tests on net10 + net472.
