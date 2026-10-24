# Check catalogue

Every diagnostic EZDiag runs. **Id** matches the check class. **Src** = data source: WMI, REG (registry), EVT (event
log), IOCTL, API (Win32), LHM (sensors, needs PawnIO), FILE, PROC (spawned tool, last resort). **A** = needs admin.
**P** = build phase (see PLAN.md). **Q** = included in Quick scan. Severities: C = Critical, W = Warning, I = Info.
Mark a row done with ✅ when the check and its tests exist. Thresholds are starting points; tune them with real fixtures.

## Storage (STO)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| STO-001 | SMART overall health (ATA) | IOCTL SMART_RCV_DRIVE_DATA, WMI MSStorageDriver_FailurePredictStatus | ✓ | C if predict-failure flag | ✓ | 3 |
| STO-002 | Reallocated / pending / uncorrectable sectors (ATA 05, C5, C6) | IOCTL | ✓ | C if pending or uncorrectable > 0; W if reallocated > 0 | ✓ | 3 |
| STO-003 | NVMe critical warning byte, available spare vs threshold, media errors | IOCTL StorageDeviceProtocolSpecificProperty (log page 02h) | ✓ | C if critical warning ≠ 0 or spare < threshold; W if media errors > 0 | ✓ | 3 |
| STO-004 | SSD wear (NVMe percentage used, ATA wear attributes E7/E9/A9/AD by vendor) | IOCTL | ✓ | W ≥ 80% used, C ≥ 100% | ✓ | 3 |
| STO-005 | Drive temperature now (SMART C2 / NVMe composite) | IOCTL | ✓ | W HDD > 50 °C, NVMe > 70 °C; C above the drive's critical temp | ✓ | 3 |
| STO-006 | Power-on hours and age context | IOCTL | ✓ | I; W HDD > 40,000 h | ✓ | 3 |
| STO-007 | Disk errors in the System log (disk 7, 11, 51, 153; storahci/stornvme 129; Ntfs 55, 98) in 30 days | EVT | | C if bad-block events (7) are recent; W for timeouts/resets | ✓ | 3 |
| STO-008 | Volume free space (system drive and others) | API GetDiskFreeSpaceEx | | C system < 5% or < 5 GB; W < 15% | ✓ | 1 |
| STO-009 | File system dirty bit / chkdsk scheduled | IOCTL FSCTL_IS_VOLUME_DIRTY | ✓ | W | ✓ | 3 |
| STO-010 | Windows runs on an HDD (slow-PC cause no. 1) | WMI MSFT_PhysicalDisk MediaType / seek-penalty IOCTL | | W if the system drive is a spinning disk | ✓ | 3 |
| STO-011 | TRIM disabled on SSD | REG / PROC fsutil behavior query DisableDeleteNotify | | W | ✓ | 3 |
| STO-012 | Storage Spaces / RAID degraded | WMI MSFT_VirtualDisk HealthStatus | | C if Unhealthy, W if Warning | ✓ | 3 |
| STO-013 | MSFT_PhysicalDisk HealthStatus + reliability counters (read/write errors) | WMI root\Microsoft\Windows\Storage (Win8+) | ✓ | W/C by status | ✓ | 3 |
| STO-014 | Partition alignment (offset not 4 KiB-aligned on SSD) | WMI Win32_DiskPartition | | W | | 3 |
| STO-015 | Disk link speed / mode (SATA at 1.5/3 Gb/s, NVMe at reduced PCIe lanes or generation) | IOCTL / WMI MSFT_PhysicalDisk | ✓ | I/W | | 3 |
| STO-016 | Drive behind a USB bridge (SMART may be hidden) | IOCTL bus type | | I "connect directly for full health data" | ✓ | 3 |
| STO-017 | BitLocker on, and is the recovery key known? (before repair work) | WMI Win32_EncryptableVolume | ✓ | W "back up the key before touching the drive" | ✓ | 8 |
| STO-018 | Large temp / Windows.old / update cache use | FILE sizes | | I with the amount reclaimable | | 6 |
| STO-019 | Pagefile location and size sanity (none, on a full or USB drive) | REG / WMI Win32_PageFileUsage | | W | ✓ | 6 |
| STO-020 | Average disk queue / active time during scan (disk at 100%) | API PDH counters, 3 s sample | | W if > 90% active for the whole sample | | 6 |

## Memory (MEM)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| MEM-001 | Installed vs usable RAM (hardware-reserved, iGPU, 32-bit OS) | WMI / API GlobalMemoryStatusEx | | W if > 10% unusable | ✓ | 1 |
| MEM-002 | Module list: size, speed, configured speed, type, part number, slot | WMI Win32_PhysicalMemory, SMBIOS | | I | ✓ | 1 |
| MEM-003 | Running below rated speed (XMP/EXPO off) | WMI Speed vs ConfiguredClockSpeed / SPD | | I "enable XMP/EXPO in BIOS" | ✓ | 1 |
| MEM-004 | Mismatched modules (size, speed, part number) | WMI | | I/W | ✓ | 1 |
| MEM-005 | Single-channel population (one stick, or wrong slots) | WMI BankLabel/DeviceLocator heuristic | | I "dual channel would be faster" | ✓ | 1 |
| MEM-006 | Memory pressure: commit near the limit, sustained high use | API | | W > 90% committed | ✓ | 6 |
| MEM-007 | WHEA memory errors / corrected ECC events | EVT WHEA-Logger 19/47 | | C if uncorrected; W corrected | ✓ | 4 |
| MEM-008 | Windows Memory Diagnostic past results | EVT MemoryDiagnostics-Results 1101/1102 | | C if errors found | ✓ | 4 |
| MEM-009 | Bugchecks that point to RAM (0x1A, 0x50, 0x3B patterns across dumps) | Crashes section | | W "run a memory test" | ✓ | 4 |
| MEM-010 | SPD data and XMP/EXPO profiles; actual primary timings | LHM/RAMSPDToolkit SPD (PawnIO); ZenStates-Core on AMD | ✓ | I (display) | | 9 |
| MEM-011 | DIMM temperature (DDR5) | LHM | ✓ | W > 85 °C | | 9 |
| MEM-012 | Launch the Windows Memory Diagnostic (fix action) | PROC mdsched | ✓ | n/a | | 12 |

## CPU and thermals (CPU)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| CPU-001 | CPU identity: name, cores/threads, base/boost, socket, microcode, virtualization on | WMI Win32_Processor, REG, API | | I | ✓ | 1 |
| CPU-002 | Sustained high CPU usage and the top processes (3 s sample) | API | | W > 85% average | ✓ | 6 |
| CPU-003 | Throttling: current clock far below base under load | WMI Win32_PerfFormattedData_Counters_ProcessorInformation (% Processor Performance) | | W | ✓ | 6 |
| CPU-004 | Temperature: package / cores | LHM, fallback ACPI MSAcpi_ThermalZoneTemperature | ✓ | W > 90 °C, C > 100 °C or at TjMax | ✓ | 9 |
| CPU-005 | Thermal-throttling events / kernel-power thermal events | EVT Kernel-Processor-Power 37, Kernel-Power 86? (verify ids) | | W | ✓ | 4 |
| CPU-006 | WHEA CPU / cache / bus errors (machine checks: unstable OC or failing hardware) | EVT WHEA-Logger 18/19 | | C if fatal; W corrected | ✓ | 4 |
| CPU-007 | Known-bad CPU microcode (Intel 13th/14th-gen Vmin instability: microcode < 0x12B) | REG microcode revision + model | | C on affected models below the fixed version | ✓ | 4 |
| CPU-008 | Power plan (Power saver on a desktop; max processor state < 100%) | API PowerGetActiveScheme / REG | | W | ✓ | 6 |
| CPU-009 | Windows 11 CPU support (unsupported CPU = no feature updates) | lookup list | | I | | 5 |

## Graphics and display (GPU)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| GPU-001 | GPUs: name, VRAM, driver version and date, which one is active | WMI Win32_VideoController, REG (VRAM over 4 GB is in the registry, not WMI) | | I | ✓ | 1 |
| GPU-002 | Microsoft Basic Display Adapter in use (no GPU driver) | WMI | | C | ✓ | 5 |
| GPU-003 | GPU driver older than about 12 months | WMI DriverDate | | I/W | ✓ | 5 |
| GPU-004 | Driver timeouts / TDR resets (Display 4101, nvlddmkm 13/14/153, amdkmdag) | EVT | | W; C if many per day | ✓ | 4 |
| GPU-005 | Bugchecks in GPU drivers (0x116, 0x119, nvlddmkm.sys / amdkmdag.sys in dumps) | Crashes section | | W | ✓ | 4 |
| GPU-006 | Monitor connected to the motherboard while a dedicated GPU exists (desktop) | API EnumDisplayDevices + adapter | | W | ✓ | 5 |
| GPU-007 | Refresh rate below the monitor's maximum (144 Hz panel running at 60 Hz) | API EnumDisplaySettings | | I | ✓ | 5 |
| GPU-008 | GPU temperature, hotspot, fan, load | LHM (NVAPI/ADL work without PawnIO) | | W > 85 °C core | | 9 |
| GPU-009 | Hybrid laptops: app forced to the iGPU | REG UserGpuPreferences | | I | | 5 |
| GPU-010 | HDR / scaling / resolution summary | API | | I | | 5 |

## Windows health (WIN)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| WIN-001 | Windows edition, version, build, install date, architecture, activation | REG CurrentVersion, WMI SoftwareLicensingProduct | | I | ✓ | 1 |
| WIN-002 | Windows version out of support (Win7/8.1, Win10 after Oct 2025, old Win11 builds) | lookup table in code | | W | ✓ | 1 |
| WIN-003 | Not activated | WMI SoftwareLicensingProduct LicenseStatus | | W | ✓ | 5 |
| WIN-004 | Pending reboot (CBS, WU, PendingFileRenameOperations, ComputerName change) | REG | | W | ✓ | 1 |
| WIN-005 | Uptime very long (no restart in > 14 days; Fast Startup hides it) | API GetTickCount64, REG HiberbootEnabled | | I "restart properly" | ✓ | 1 |
| WIN-006 | Component store / system file corruption reported (CBS.log, DISM log, last SFC result) | FILE CBS.log tail | ✓ | W if corruption recorded | ✓ | 4 |
| WIN-007 | Unexpected shutdowns (Kernel-Power 41, EventLog 6008) in 30 days | EVT | | W; C if > 5 | ✓ | 4 |
| WIN-008 | Boot time and slow boot (Diagnostics-Performance 100 + degradation 101–110) | EVT | ✓ | W boot > 60 s; names the culprit app/driver | | 4 |
| WIN-009 | Service failures (SCM 7000, 7001, 7009, 7023, 7031, 7034) summary | EVT | | W for repeated | ✓ | 4 |
| WIN-010 | Important services stopped or disabled (Windows Update, BITS, WMI, Spooler, Audio, DHCP, DNS client, Defender, Time) | API ServiceController | | W | ✓ | 4 |
| WIN-011 | WMI repository broken (the collectors fail) | collector failures | | W "repair WMI" | ✓ | 1 |
| WIN-012 | Wrong system time / time sync failing | API + EVT Time-Service | | W > 2 min off | ✓ | 7 |
| WIN-013 | User profile problems (temp profile, User Profile Service 1511/1515) | EVT / REG ProfileList .bak | | C on a temp profile | ✓ | 4 |
| WIN-014 | Low virtual memory events (Resource-Exhaustion-Detector 2004) | EVT | | W | ✓ | 4 |
| WIN-015 | Group Policy errors (1085, 1096, 1129) | EVT | | I | | 4 |
| WIN-016 | Search indexer / Explorer crash loops | Crashes section | | W | | 4 |
| WIN-017 | Reliability Monitor stability index and history | WMI Win32_ReliabilityStabilityMetrics / Records | | I/W | | 4 |
| WIN-018 | Firmware: UEFI vs legacy boot, Secure Boot, TPM version, Windows 11 readiness | API GetFirmwareType, REG SecureBoot, WMI Win32_Tpm | | I | ✓ | 5 |

## Updates (UPD)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| UPD-001 | Last successful update install date | COM Microsoft.Update.Session history / REG | | W > 60 days | ✓ | 5 |
| UPD-002 | Failed updates (WindowsUpdateClient 20, 25, 31; error codes with meaning) | EVT + COM history | | W, listing the KB and error code | ✓ | 5 |
| UPD-003 | Updates paused or blocked by policy (WSUS set, NoAutoUpdate, pause dates, metered connection) | REG Policies\…\WindowsUpdate | | W | ✓ | 5 |
| UPD-004 | Pending updates available (slow: online search) | COM IUpdateSearcher | | I | | 5 |
| UPD-005 | Feature update blocked (safeguard hold, not enough space) | REG / free space | | I | | 5 |
| UPD-006 | Installed hotfix list | WMI Win32_QuickFixEngineering | | I (display) | | 5 |
| UPD-007 | Defender definitions age | WMI root\Microsoft\Windows\Defender MSFT_MpComputerStatus | | W > 7 days | ✓ | 8 |

## Drivers and devices (DRV)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| DRV-001 | Devices with problems (Device Manager yellow-bang: ConfigManagerErrorCode ≠ 0, with the meaning of each code) | WMI Win32_PnPEntity / SetupAPI | | W; C for storage/network/display classes | ✓ | 5 |
| DRV-002 | Unknown devices with no driver, and their hardware ids (vendor names from a compact PCI/USB id table) | SetupAPI | | W | ✓ | 5 |
| DRV-003 | Full driver list: name, provider, version, date, signer, INF | WMI Win32_PnPSignedDriver / SetupAPI (faster) | | I (display) | | 5 |
| DRV-004 | Unsigned or test-signed drivers, test-signing mode on | SetupAPI / BCD (REG) | ✓ | W | ✓ | 5 |
| DRV-005 | Very old chipset / storage / network drivers (generic Microsoft driver where a vendor one is needed) | driver list | | I | | 5 |
| DRV-006 | Known-problem drivers (old Realtek audio, Killer network, vulnerable drivers on Microsoft's blocklist, old WinRing0 copies) | small lookup table | | W | ✓ | 5 |
| DRV-007 | Drivers that crashed the PC (driver names from the minidumps) | Crashes section | | W | ✓ | 4 |
| DRV-008 | Driver install failures (Kernel-PnP 219, 411; DeviceSetupManager) | EVT | | I/W | | 5 |
| DRV-009 | USB problems: connect/disconnect storms, power-surge events, selective suspend | EVT Kernel-PnP / USB + REG | | W | | 10 |
| DRV-010 | Ghost devices from old hardware (hidden non-present devices) | SetupAPI | | I | | 5 |

## Crashes and errors (CRA)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| CRA-001 | Blue screens: list from minidump files + BugCheck 1001 events (code, meaning, parameters, driver at fault) | FILE C:\Windows\Minidump (header parse), EVT | ✓ | C if > 2 in 30 days; W any | ✓ | 4 |
| CRA-002 | Pattern across BSODs (same driver or code → likely cause; different codes → suspect RAM/PSU/overclock) | derived | | W | ✓ | 4 |
| CRA-003 | Application crashes and hangs (Application Error 1000, Hang 1002, .NET Runtime 1026) grouped by app | EVT | | W for repeated | ✓ | 4 |
| CRA-004 | Windows Error Reporting queue and archive summary | FILE ProgramData\Microsoft\Windows\WER | | I | | 4 |
| CRA-005 | Error timeline (all critical/error events for the last 7 days, filterable; the "event viewer that makes sense") | EVT | | I (display) | | 4 |
| CRA-006 | Crash-dump settings (dumps disabled, so BSODs leave no trace) | REG CrashControl | | I | ✓ | 4 |
| CRA-007 | Bugcheck code explanations (table of the ~60 common codes) | data file | | n/a | | 4 |

## Performance and startup (PRF)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| PRF-001 | Startup programs (Run/RunOnce keys in both views, Startup folders, logon scheduled tasks, StartupApproved state) with the estimated impact | REG, FILE, Task Scheduler COM | | I; W if > 15 enabled | ✓ | 6 |
| PRF-002 | Processes (task-manager view): CPU, memory, disk, path, signer, start time | API | | display | | 6 |
| PRF-003 | Resource hogs right now (top CPU / RAM / disk processes during a 3 s sample) | API PDH | | W | ✓ | 6 |
| PRF-004 | Multiple antivirus products running (common slowdown) | WMI root\SecurityCenter2 | | W | ✓ | 8 |
| PRF-005 | Third-party "optimizers" / bloatware / PUPs installed (small lookup list) | REG Uninstall | | I | ✓ | 6 |
| PRF-006 | Visual effects / transparency / Game Mode / hardware-accelerated GPU scheduling summary | REG | | I | | 6 |
| PRF-007 | Services set to Automatic that keep crashing or use lots of CPU | WMI + EVT | | W | | 6 |
| PRF-008 | Installed programs (name, version, publisher, install date, size): no Win32_Product | REG Uninstall + Appx | | display | | 6 |
| PRF-009 | Browser count and default browser, heavy extension count (Chromium profiles) | FILE | | I | | 6 |
| PRF-010 | Thermal or power limits causing slowness on laptops (battery saver on, plugged into a low-watt charger) | API GetSystemPowerStatus + battery | | I | ✓ | 11 |

## Network (NET)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| NET-001 | Adapters: type, status, link speed, MAC, driver, IP/DHCP/DNS/gateway | API GetAdaptersAddresses | | I | ✓ | 7 |
| NET-002 | No IP / APIPA 169.254.x (DHCP failure) | API | | C | ✓ | 7 |
| NET-003 | Gateway reachable (ping), internet reachable (ping 1.1.1.1 + 8.8.8.8), DNS resolves | API Ping, Dns | | C/W by step, saying which step fails | ✓ | 7 |
| NET-004 | Latency and packet loss (20 pings each to the gateway and the internet) | API Ping | | W loss > 2% | | 7 |
| NET-005 | Ethernet link at 10/100 Mb/s on a gigabit adapter (bad cable) | API | | W | ✓ | 7 |
| NET-006 | Wi-Fi: SSID, signal %, band, channel, link rate, adapter standard | API WlanAPI | | W signal < 40% | ✓ | 7 |
| NET-007 | Proxy set (WinINET, WinHTTP, PAC); hosts file changed; custom DNS servers (possible hijack) | REG, FILE | | W if unexpected | ✓ | 7 |
| NET-008 | Disabled adapters, VPN/virtual adapters, metered connection | API / WMI | | I | | 7 |
| NET-009 | Network reset candidates: Winsock catalog LSP entries, broken TCP/IP settings | REG | | W | | 7 |
| NET-010 | Download speed test (optional, user-started) | HTTP to a public test file | | I | | 7 |
| NET-011 | Firewall profiles on/off; ports open to the internet (RDP 3389, SMB) | API / REG | | W | ✓ | 8 |

## Security (SEC)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| SEC-001 | Antivirus present, enabled and up to date | WMI SecurityCenter2 AntiVirusProduct (workstation only) | | C if none active | ✓ | 8 |
| SEC-002 | Defender real-time protection, tamper protection, recent threats found | WMI MSFT_MpComputerStatus / MSFT_MpThreatDetection | ✓ | W/C | ✓ | 8 |
| SEC-003 | UAC disabled | REG EnableLUA | | W | ✓ | 8 |
| SEC-004 | Suspicious startup entries (unsigned, running from Temp / AppData / ProgramData, random names, script hosts) | startup + signature check (WinVerifyTrust) | | W | ✓ | 8 |
| SEC-005 | Browser policies forced (home page / search / extension force-install = adware sign) | REG Policies\Google\Chrome, Edge, Firefox | | W | ✓ | 8 |
| SEC-006 | Remote-access tools installed or running (AnyDesk, TeamViewer, etc.; scam check) | REG Uninstall + processes | | I/W | ✓ | 8 |
| SEC-007 | Secure Boot off, TPM off, BitLocker status | API / WMI | | I | ✓ | 8 |
| SEC-008 | Local accounts: admin accounts, accounts with no password, guest enabled | API NetUserEnum | ✓ | W | | 8 |
| SEC-009 | SMBv1 enabled, RDP enabled without NLA | REG | | W | ✓ | 8 |
| SEC-010 | Windows Script Host / PowerShell scheduled tasks with encoded commands (persistence) | Task Scheduler COM | ✓ | W | | 8 |

## Battery and power (BAT)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| BAT-001 | Battery wear: full-charge vs design capacity, cycle count, chemistry | WMI root\wmi BatteryFullChargedCapacity / BatteryStaticData / BatteryCycleCount | | W < 70%, C < 50% | ✓ | 11 |
| BAT-002 | Charging state, charger wattage, charge limit set by the vendor tool | API / WMI | | I | ✓ | 11 |
| BAT-003 | Sleep problems: what prevents sleep (powercfg /requests), last wake source, wake timers | PROC powercfg (timeout) | ✓ | I | | 11 |
| BAT-004 | Modern Standby drain (sleep study) summary | PROC powercfg /sleepstudy | ✓ | I | | 11 |
| BAT-005 | Kernel-Power 41 with power-button presses (PSU or power-delivery suspicion on desktops) | EVT | | W | ✓ | 11 |

## Peripherals and other (PER)

| Id | Check | Src | A | Rule | Q | P |
|---|---|---|---|---|---|---|
| PER-001 | Audio: default playback/record devices, disabled devices, Audio services | API MMDevice COM | | W if no output device | ✓ | 10 |
| PER-002 | Printers: list, default, stuck jobs, Spooler status, errors (PrintService log) | WMI Win32_Printer, EVT | | W | ✓ | 10 |
| PER-003 | USB controllers and devices tree; devices at USB 2 speed on a USB 3 port | SetupAPI | | I | | 10 |
| PER-004 | Monitors: model (EDID), size, max resolution and refresh | REG EDID | | I | | 10 |
| PER-005 | Bluetooth adapter and radio state | SetupAPI | | I | | 10 |
| PER-006 | Webcam / microphone privacy blocks (the "camera doesn't work" ticket) | REG CapabilityAccessManager | | W | ✓ | 10 |
| PER-007 | Motherboard and BIOS: vendor, model, BIOS version and date (BIOS > 3 years old) | WMI Win32_BaseBoard / Win32_BIOS | | I | ✓ | 1 |

## Info only (no findings, but shown on tabs)

System summary (device name, user, domain/workgroup, model, serial, manufacturer, Windows details), CPU-Z-style
CPU/cache/board/memory/SPD pages, full sensor list, environment variables, scheduled tasks, services, installed
updates, Appx packages, disks/partitions/volumes map, network shares, mapped drives, power settings.
