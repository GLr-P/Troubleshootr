// Builds tests/fixtures/demo-many-problems.snap.json from the real laptop fixture, adding one of every problem
// the checks know about, so UI screenshots show every kind of finding. Run: node scripts/make-demo-fixture.js
const fs = require('fs');
const path = require('path');
const dir = path.join(__dirname, '..', 'tests', 'fixtures');
const s = JSON.parse(fs.readFileSync(path.join(dir, 'win11-25h2-laptop-msi.snap.json'), 'utf8'));

s.meta.utcOffsetMinutes = -420; // the owner's laptop is on Pacific time
s.meta.note = 'Hand-made demo: one of each finding, for UI screenshots. Rebuild with scripts/make-demo-fixture.js';
const sys = s.system.value;
Object.assign(sys, { build: 26100, windowsDisplayVersion: '24H2', uptimeSeconds: 40 * 86400, fastStartupEnabled: true, pendingRebootReasons: ['Windows Update'] });
const vol = s.volumes.value[0];
vol.freeBytes = Math.round(vol.totalBytes * 0.03);
vol.dirty = true;
const mem = s.memory.value;
mem.modules = mem.modules.slice(0, 1);
Object.assign(mem.modules[0], { configuredSpeedMts: 4800, partNumber: 'F5-6000J3038F16G' });
Object.assign(mem, { slotCount: 2, availableMB: 600 });
s.board.value.biosDate = '2021-03-01T00:00:00';
s.gpus = { unavailable: 'WMI: 0x80041010 in root' + String.fromCharCode(92) + 'cimv2 (SELECT Name FROM Win32_VideoController)' };

// A worn, hot NVMe system drive and a failing SATA hard drive.
const st = s.storage.value;
Object.assign(st.disks[0].nvme, { percentageUsed: 86, temperatureC: 73, mediaErrors: 4 });
const attr = (id, name, current, worst, threshold, raw) => ({ id, name, current, worst, threshold, raw, rawHex: raw.toString(16).toUpperCase().padStart(12, '0') });
st.disks.push({
  number: 1, model: 'WDC WD10EZEX-08WN4A0', serialNumber: '[redacted]', firmware: '01.01A01', busType: 'SATA', mediaType: 'HDD',
  sizeBytes: 1000204886016, spindleRpm: 7200, isBoot: false, driveLetters: ['D:'], health: 'Warning', operationalStatus: ['Predictive Failure'],
  ata: {
    predictFailure: false,
    attributes: [
      attr(0x01, 'Read error rate', 200, 200, 51, 14), attr(0x05, 'Reallocated sectors', 189, 189, 140, 312),
      attr(0x09, 'Power-on hours', 41, 41, 0, 43210), attr(0x0C, 'Power cycle count', 98, 98, 0, 2311),
      attr(0xC2, 'Temperature', 108, 95, 0, 39), attr(0xC5, 'Pending sectors', 200, 200, 0, 24),
      attr(0xC6, 'Uncorrectable sectors', 200, 200, 0, 8), attr(0xC7, 'UltraDMA CRC errors', 200, 200, 0, 0),
    ],
  },
});
st.partitions.push({ diskNumber: 1, partitionNumber: 0, driveLetter: 'D:', offsetBytes: 32256, sizeBytes: 0 });
st.trimDisabled = true;

// Phase 4: crashes, event-log problems and Windows health. Times are relative to the snapshot time.
const now = new Date(s.meta.collectedUtc);
const ago = (days) => new Date(now.getTime() - days * 86400000).toISOString();
const ev = (provider, id, days, level, message, data, log = 'System') => ({ log, provider, id, level, timeUtc: ago(days), message, data: data || {} });
const bsod = (code, days, ps) => ({ file: 'C:\\Windows\\Minidump\\' + days + '.dmp', timeUtc: ago(days), sizeBytes: 1048576, bugCheck: code, parameters: ps || [0, 2, 0, 0] });
const events = [];
const repeat = (n, f) => { for (let i = 0; i < n; i++) events.push(f(i)); };
repeat(4, (i) => ev('Application Error', 1000, 1 + i, 2, 'Faulting application name: PhotoEditor.exe', { AppName: 'PhotoEditor.exe', ModuleName: 'gpu_helper.dll' }, 'Application'));
repeat(3, (i) => ev('Application Error', 1000, 0.5 + i, 2, 'Faulting application name: explorer.exe', { AppName: 'explorer.exe', ModuleName: 'ShellExt.dll' }, 'Application'));
events.push(ev('disk', 7, 2, 2, 'The device, \\Device\\Harddisk1\\DR1, has a bad block.'));
repeat(3, (i) => ev('storahci', 129, 3 + i, 3, 'Reset to device, \\Device\\RaidPort0, was issued.'));
repeat(2, (i) => ev('Microsoft-Windows-Kernel-Power', 41, 6 + i * 5, 1, 'The system has rebooted without cleanly shutting down first.', { BugcheckCode: '0', LongPowerButtonPressDetected: 'false' }));
events.push(ev('Microsoft-Windows-WHEA-Logger', 19, 4, 3, 'A corrected hardware error has occurred. Reported by component: Processor Core Error Source: Corrected Machine Check Error Type: Cache Hierarchy Error Processor APIC ID: 6'));
events.push(ev('Microsoft-Windows-WHEA-Logger', 47, 4, 3, 'A corrected hardware error has occurred. Component: Memory Error Source: Corrected Machine Check'));
events.push(ev('Microsoft-Windows-MemoryDiagnostics-Results', 1202, 8, 2, 'The Windows Memory Diagnostic tested the computer\'s memory and detected hardware errors.'));
repeat(4, (i) => ev('Display', 4101, 1 + i, 3, 'Display driver nvlddmkm stopped responding and has successfully recovered.'));
repeat(3, (i) => ev('Service Control Manager', 7031, 2 + i, 2, 'The Contoso Updater service terminated unexpectedly.', { param1: 'Contoso Updater' }));
events.push(ev('Microsoft-Windows-Resource-Exhaustion-Detector', 2004, 5, 3, 'Windows successfully diagnosed a low virtual memory condition. The following programs consumed the most virtual memory: chrome.exe (8124) consumed 12884901888 bytes, PhotoEditor.exe (4410) consumed 4294967296 bytes.'));
events.push(ev('Microsoft-Windows-GroupPolicy', 1129, 3, 2, 'The processing of Group Policy failed because of lack of network connectivity to a domain controller.'));
events.push(ev('Microsoft-Windows-Diagnostics-Performance', 100, 1, 2, 'Windows has started up', { BootTime: '98000' }, 'Diagnostics-Performance'));
events.push(ev('Microsoft-Windows-Diagnostics-Performance', 101, 1, 3, 'This application took longer than usual to start up', { Name: 'GameLauncher.exe', FriendlyName: 'Game Launcher', DegradationTime: '21500' }, 'Diagnostics-Performance'));
events.push(ev('Microsoft-Windows-User Profiles Service', 1515, 9, 3, 'Windows has backed up this user\'s profile.', {}, 'Application'));
events.sort((a, b) => b.timeUtc.localeCompare(a.timeUtc));
const timeline = events.filter((e) => e.level <= 2).slice(0, 12).map((e) => ({ ...e, data: {} }));
s.events = { value: { days: 30, events, timeline, timelineDays: 7, unreadable: [] } };
s.crashes = { value: {
  dumps: [bsod(0xD1, 1, [0, 2, 0, 0]), bsod(0x1A, 4), bsod(0x116, 9), bsod(0x3B, 15)].map((d, i) => i === 0 ? { ...d, driver: 'contosonet.sys' } : d),
  crashDumpEnabled: 7, autoReboot: true, reports: [{ kind: 'LiveKernelEvent', subject: '141', timeUtc: ago(2), queued: true }], queuedReports: 1,
} };
s.health = { value: {
  services: [
    { name: 'wuauserv', displayName: 'Windows Update', status: 'Stopped', startType: 'Disabled' },
    { name: 'Audiosrv', displayName: 'Windows Audio', status: 'Running', startType: 'Automatic' },
  ],
  cbs: { cannotRepair: 3, repaired: 0, storeCorrupt: 1, lastWriteUtc: ago(2) },
  tempProfile: false, profileBackups: 0, stabilityIndex: 2.4, stabilityLowest30Days: 1.2,
} };
// Phase 5: devices, drivers, displays and Windows Update.
const dev = (cls, name, extra) => ({ name, class: cls, problemCode: 0, ...extra });
s.devices = { value: {
  devices: [
    dev('Display', 'Intel(R) Arc(TM) Graphics', { instanceId: 'PCI\\VEN_8086&DEV_7D55\\3&1', driverProvider: 'Intel Corporation', driverVersion: '31.0.101.5333', driverDate: '2024-02-20T00:00:00Z' }),
    dev('Net', 'Intel(R) Wi-Fi 6E AX211 160MHz', { problemCode: 10, hardwareId: 'PCI\\VEN_8086&DEV_51F0&SUBSYS_00948086', driverProvider: 'Intel', driverVersion: '23.40.0.4', driverDate: '2024-03-01T00:00:00Z' }),
    dev('Net', 'Realtek PCIe GbE Family Controller', { driverProvider: 'Realtek', driverVersion: '10.12.1012.2016', driverDate: '2016-10-12T00:00:00Z' }),
    dev(undefined, 'Unknown device', { problemCode: 28, hardwareId: 'PCI\\VEN_10EC&DEV_5228&SUBSYS_11131462' }),
    dev('Camera', 'Integrated Webcam', { problemCode: 22 }),
    dev('SecurityDevices', 'Trusted Platform Module 2.0'),
    dev('AudioEndpoint', 'Speakers (Realtek(R) Audio)', { driverProvider: 'Realtek', driverVersion: '6.0.9600.1', driverDate: '2023-05-04T00:00:00Z' }),
  ],
  hidden: [dev('Display', 'NVIDIA GeForce GTX 1060 6GB', { problemCode: 45, instanceId: 'PCI\\VEN_10DE&DEV_1C03\\4&2' })],
  hiddenCount: 61,
  kernelDrivers: [{ name: 'WinRing0_1_2_0', file: 'WinRing0x64.sys', start: 3 }, { name: 'stornvme', file: 'stornvme.sys', start: 0 }],
  testSigning: true, integrityChecksOff: false,
} };
s.displays = { value: {
  displays: [
    { adapter: 'Intel(R) Arc(TM) Graphics', monitor: 'Generic PnP Monitor', primary: true, width: 2560, height: 1600, refreshHz: 60, maxRefreshHz: 165, bitsPerPixel: 32,
      monitorMaker: 'AU Optronics', monitorYear: 2021, sizeInches: 16, nativeWidth: 2560, nativeHeight: 1600 },
    { adapter: 'Intel(R) Arc(TM) Graphics', monitor: 'Generic PnP Monitor', primary: false, width: 1920, height: 1080, refreshHz: 60, maxRefreshHz: 60, bitsPerPixel: 32,
      monitorMaker: 'Dell', monitorModel: 'DELL U2722D', monitorYear: 2022, sizeInches: 27, nativeWidth: 2560, nativeHeight: 1440 },
  ],
  powerSavingApps: [],
} };
const upd = (days, title, result, hResult = 0) => ({ dateUtc: ago(days), title, result, hResult, operation: 1 });
s.updates = { value: {
  history: [
    upd(1, 'Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.461.184.0) - Current Channel (Broad)', 2),
    upd(3, '2026-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5099999)', 4, 0x800F081F | 0),
    upd(10, '2026-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5099999)', 4, 0x800F081F | 0),
    upd(75, '2026-08 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5090000)', 2),
  ],
  policy: { usesWsus: false, autoUpdateOff: false, windowsUpdateBlocked: false, pausedUntilUtc: ago(-21) },
  upgradeBlocks: [{ target: 'GE25H2', redReason: 'None', gatedBlockId: '56318982' }],
} };
// Phase 6: startup, software, a busy performance sample and slow settings.
const run = (name, location, enabled = true) => ({ name, location, enabled, command: 'C:\\Program Files\\' + name + '\\' + name + '.exe' });
s.startup = { value: { entries: [
  ...['Steam', 'Discord', 'Spotify', 'Epic Games Launcher', 'Adobe Creative Cloud', 'Dropbox', 'Zoom', 'Teams', 'Razer Synapse', 'iCUE', 'OneDrive', 'Skype',
      'Logitech G HUB', 'Battle.net', 'Opera GX', 'Wallpaper Engine', 'EA app'].map((n) => run(n, 'HKCU Run')),
  run('MSI Center', 'HKLM Run', false),
  { name: 'GoogleUpdateTaskMachineUA', location: 'Scheduled task', enabled: true, command: 'C:\\Program Files (x86)\\Google\\Update\\GoogleUpdate.exe /ua' },
] } };
s.software = { value: {
  programs: [
    { name: '7-Zip 24.08 (x64)', version: '24.08', publisher: 'Igor Pavlov', installDate: '2026-01-12T00:00:00Z', sizeKB: 5800, scope: 'Machine' },
    { name: 'Driver Booster 12', version: '12.0.0', publisher: 'IObit', installDate: '2026-09-30T00:00:00Z', sizeKB: 120000, scope: 'Machine (32-bit)' },
    { name: 'Google Chrome', version: '141.0.7390.65', publisher: 'Google LLC', installDate: '2026-10-01T00:00:00Z', scope: 'Machine' },
    { name: 'Wave Browser', version: '1.5.1', publisher: 'Wavesor Software', installDate: '2026-10-02T00:00:00Z', scope: 'User' },
  ],
  defaultBrowser: 'ChromeHTML', browserExtensions: { 'Chrome: Default': 27 },
} };
s.performance = { value: {
  sampleSeconds: 3, cpuPercent: 94, diskActivePercent: 100, processorPerformancePercent: 38, processCount: 212,
  processes: [
    { name: 'svchosts.exe', pid: 6120, cpuPercent: 81.5, privateMB: 220, workingSetMB: 240, diskBytesPerSec: 0, path: 'C:\\Users\\Public\\svchosts.exe' },
    { name: 'chrome.exe', pid: 4410, cpuPercent: 6.2, privateMB: 2100, workingSetMB: 1800, diskBytesPerSec: 120000 },
    { name: 'SearchIndexer.exe', pid: 3120, cpuPercent: 1.1, privateMB: 90, workingSetMB: 60, diskBytesPerSec: 64 * 1048576, path: 'C:\\Windows\\System32\\SearchIndexer.exe' },
  ],
} };
s.tuning = { value: {
  powerPlan: 'Power saver', powerPlanGuid: 'a1841308-3541-4fab-bc81-f71556f20b4a', maxCpuOnAc: 80, maxCpuOnBattery: 50, onAcPower: true, batterySaverOn: false,
  pageFileSettings: [], pageFiles: [], visualEffects: 1, transparency: true, gameMode: true, gpuScheduling: false,
  cleanup: [
    { name: 'Previous Windows installation (Windows.old)', path: 'C:\\Windows.old', bytes: Math.round(23.4 * 1073741824), atLeast: false },
    { name: 'Your temp files', path: 'C:\\Users\\[user]\\AppData\\Local\\Temp\\', bytes: Math.round(4.1 * 1073741824), atLeast: true },
  ],
} };
s.health.value.licenseStatus = 5;
s.health.value.licenseName = 'Windows(R), Professional edition';
s.cpu.value.packages[0] ={ ...s.cpu.value.packages[0], name: '13th Gen Intel(R) Core(TM) i9-13900K', identifier: 'Intel64 Family 6 Model 183 Stepping 1', microcode: '0x119' };

// Phase 7: weak 2.4 GHz Wi-Fi with loss, a gigabit cable stuck at 100 Mb/s without an address, DNS down, and redirection.
const adapter = (o) => Object.assign({ type: 'Ethernet', status: 'Up', speedMbps: 0, dhcp: true, ipv4: [], ipv6: [], gateways: [], hasIpv6Gateway: false, dnsServers: [], staticDns: false }, o);
s.network = { value: {
  adapters: [
    adapter({ name: 'Wi-Fi', description: 'Intel(R) Wi-Fi 6E AX211 160MHz', type: 'Wi-Fi', speedMbps: 58, mac: '[redacted]', ipv4: ['192.168.0.42/24'], gateways: ['192.168.0.1'], dnsServers: ['85.255.112.10', '85.255.113.20'], staticDns: true }),
    adapter({ name: 'Ethernet', description: 'Realtek PCIe GbE Family Controller', speedMbps: 100, mac: '[redacted]', ipv4: ['169.254.31.7/16'] }),
    adapter({ name: 'ProtonVPN', description: 'ProtonVPN Tunnel', type: 'Other', speedMbps: 100000, dhcp: false, ipv4: ['10.2.0.2/32'] }),
  ],
  wifi: [{ adapter: 'Intel(R) Wi-Fi 6E AX211 160MHz', state: 'connected', ssid: '[redacted]', bssid: '[redacted]', signalPercent: 31, rssi: -81, channel: 6, frequencyMHz: 2437, phyType: 7, rxMbps: 58, txMbps: 26, auth: 7 }],
  proxy: { enabled: true, server: '127.0.0.1:8877', autoDetect: true },
  hosts: [{ address: '0.0.0.0', host: 'update.microsoft.com' }, { address: '0.0.0.0', host: 'www.malwarebytes.com' }, { address: '45.83.10.2', host: 'www.paypal.com' }],
  hostsCount: 3,
  winsock: [
    { catalog: 'Protocol', path: 'C:\\Program Files (x86)\\WebBoost\\wbsp64.dll', standard: false },
    { catalog: 'Protocol', path: '%SystemRoot%\\system32\\mswsock.dll', standard: true },
  ],
} };
s.connectivity = { value: {
  gateway: '192.168.0.1',
  gatewayPing: { target: '192.168.0.1', sent: 20, received: 15, avgMs: 64, maxMs: 410 },
  internetPing: { target: '1.1.1.1', sent: 20, received: 14, avgMs: 180, maxMs: 620 },
  internetTcp: true, dnsOk: false, dnsError: 'No such host is known', webOk: false, webProblem: "The test page didn't load: The remote name could not be resolved",
  clockOffsetSeconds: -540, clockSource: 'time.windows.com (NTP)', timeSyncType: 'NoSync', timeServiceStart: 3,
} };
s.devices.value.devices.push({ name: 'Intel(R) Ethernet Connection I219-V', class: 'Net', problemCode: 22, driverProvider: 'Intel' });

// Phase 8: two antiviruses (one out of date), a threat that wasn't removed, UAC off, open sharing, scam remote access.
s.security = { value: {
  securityCenter: true,
  antivirus: [
    { name: 'Windows Defender', enabled: false, upToDate: true, state: 393472 },
    { name: 'Norton 360', enabled: true, upToDate: false, state: 266256 },
    { name: 'McAfee LiveSafe', enabled: true, upToDate: true, state: 266240 },
  ],
  firewalls: [],
  defender: { runningMode: 'Passive Mode', antivirusEnabled: true, realTimeProtection: false, tamperProtection: false, signaturesUpdatedUtc: '2026-10-20T10:00:00Z', signatureAgeDays: 4 },
  threats: [
    { name: 'Trojan:Win32/Wacatac.B!ml', severity: 5, detectedUtc: '2026-10-22T18:31:00Z', active: true, removed: false, where: 'file:_C:\\Users\\[user]\\Downloads\\free-game-setup.exe' },
    { name: 'PUA:Win32/Presenoker', severity: 1, detectedUtc: '2026-10-15T09:12:00Z', active: false, removed: true, where: 'file:_C:\\Users\\[user]\\Downloads\\pdf-converter.exe' },
  ],
  firewallDomain: true, firewallPrivate: true, firewallPublic: false,
  uacOn: false, uacAdminPrompt: 0,
  tpmVersion: '2.0', tpmPresent: true,
  bitLocker: [{ drive: 'C:', protection: 1 }],
  accounts: [
    { name: '[redacted]', rid: 500, admin: true, disabled: true, passwordNotRequired: false },
    { name: '[redacted]', rid: 501, admin: false, disabled: false, passwordNotRequired: true },
    { name: '[redacted]', rid: 1001, admin: true, disabled: false, passwordNotRequired: false, lastLogonUtc: '2026-10-24T08:00:00Z' },
  ],
  smb1Client: true, smb1Server: true, rdpEnabled: true, rdpNla: false, rdpPort: 3389, listeningPorts: [445, 3389],
  browserPolicies: [
    { browser: 'Chrome', kind: 'Search engine', value: 'https://search.yourfinder.example/?q={searchTerms}' },
    { browser: 'Chrome', kind: 'Forced extensions', value: 'ohmpcdmgbjnfkfjkgjphnljpcdmkhiad;https://clients2.google.com/service/update2/crx' },
  ],
  remoteToolsRunning: ['AnyDesk'],
  suspiciousTasks: [
    { name: '\\SystemHealthUpdate', command: 'powershell.exe -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAAuAFcAZQBiAEMAbABpAGUAbgB0ACkA', reason: 'encoded PowerShell command' },
  ],
} };
s.board.value.secureBoot = false;
s.startup.value.entries.push({ name: 'WinSvcHost', location: 'HKCU Run', command: 'C:\\Users\\[user]\\AppData\\Local\\Temp\\winsvchost.exe /silent', enabled: true, file: 'C:\\Users\\[user]\\AppData\\Local\\Temp\\winsvchost.exe', signed: false });
s.software.value.programs.push({ name: 'AnyDesk', version: '9.0.4', publisher: 'AnyDesk Software GmbH', installDate: '2026-10-23T00:00:00', scope: 'Machine (32-bit)' });

// Phase 9: a hot CPU near its limit, a hot graphics card and a warm DDR5 stick (with the PawnIO driver, as admin).
const reading = (kind, hardware, name, type, value, min, max) => ({ hardware, kind, name, type, value, min: min ?? value, max: max ?? value });
s.sensors = { value: {
  driverInstalled: true,
  readings: [
    reading('CPU', 'Intel Core i7-13700K', 'CPU Package', 'Temperature', 97, 61, 100),
    reading('CPU', 'Intel Core i7-13700K', 'Core Max', 'Temperature', 96, 60, 100),
    reading('CPU', 'Intel Core i7-13700K', 'P-Core #1 Distance to TjMax', 'Temperature', 3, 0, 39),
    reading('CPU', 'Intel Core i7-13700K', 'P-Core #1', 'Clock', 4190, 800, 5390),
    reading('CPU', 'Intel Core i7-13700K', 'CPU Package', 'Power', 181.4, 12.2, 224.9),
    reading('CPU', 'Intel Core i7-13700K', 'CPU Total', 'Load', 100, 2, 100),
    reading('GPU', 'NVIDIA GeForce RTX 3080', 'GPU Core', 'Temperature', 88, 41, 89),
    reading('GPU', 'NVIDIA GeForce RTX 3080', 'GPU Hot Spot', 'Temperature', 104, 50, 106),
    reading('GPU', 'NVIDIA GeForce RTX 3080', 'GPU Fan', 'Fan', 2950, 0, 3010),
    reading('GPU', 'NVIDIA GeForce RTX 3080', 'GPU Core', 'Load', 99, 0, 100),
    reading('Memory', 'DIMM #1', 'Temperature', 'Temperature', 87, 44, 88),
    reading('Motherboard', 'Nuvoton NCT6798D', 'CPU Fan', 'Fan', 1980, 650, 2100),
    reading('Motherboard', 'Nuvoton NCT6798D', 'Vcore', 'Voltage', 1.312, 0.71, 1.38),
  ],
  thermalZones: [reading('Motherboard', 'ACPI thermal zone', '_TZ.THRM', 'Temperature', 27.8)],
} };

// Phase 10: peripherals. No working speakers, a stuck default printer, a USB 3 drive at USB 2 speed, a port that cut
// power, the camera blocked for desktop apps, and a USB display adapter keeping the laptop awake.
s.peripherals = { value: {
  audio: [
    { name: 'Speakers', device: 'Realtek(R) Audio', playback: true, state: 2, isDefault: false },
    { name: 'Headphones', device: 'Realtek(R) Audio', playback: true, state: 8, isDefault: false },
    { name: 'Microphone Array', device: 'Intel® Smart Sound Technology for Digital Microphones', playback: false, state: 1, isDefault: true },
  ],
  printers: [
    { name: 'Brother HL-L2350DW series', driver: 'Brother HL-L2350DW series', port: 'USB001', isDefault: true, isNetwork: false, offline: false, status: 3, errorState: 4, jobs: 3, oldestJobMinutes: 190, errorJobs: 1 },
    { name: 'Microsoft Print to PDF', driver: 'Microsoft Print To PDF', port: 'PORTPROMPT:', isDefault: false, isNetwork: false, offline: false, status: 3, errorState: 2, jobs: 0, errorJobs: 0 },
  ],
  spoolerStatus: 'Running', spoolerStartType: 'Automatic',
  usb: [
    { hub: 'USB Root Hub (USB 3.0)', port: 1, device: 'Samsung Portable SSD T7', vendorId: 0x04E8, productId: 0x4001, isHub: false, status: 1, speed: 2, superSpeedCapable: true, portSupportsUsb3: true },
    { hub: 'USB Root Hub (USB 3.0)', port: 3, vendorId: 0, productId: 0, isHub: false, status: 4, speed: 0 },
    { hub: 'USB Root Hub (USB 3.0)', port: 5, device: 'FHD Camera', vendorId: 0x5986, productId: 0x1193, isHub: false, status: 1, speed: 2, superSpeedCapable: false, portSupportsUsb3: false },
    { hub: 'USB Root Hub (USB 3.0)', port: 10, device: 'Intel(R) Wireless Bluetooth(R)', vendorId: 0x8087, productId: 0x0036, isHub: false, status: 1, speed: 1, superSpeedCapable: false, portSupportsUsb3: false },
  ],
  usbSelectiveSuspendAc: true, usbSelectiveSuspendDc: true,
  bluetooth: { adapters: ['Intel(R) Wireless Bluetooth(R)'], radioFound: true, connectable: true, paired: 2, serviceStatus: 'Running', serviceStartType: 'Manual' },
  camera: { device: 'Allow', apps: 'Allow', desktopApps: 'Deny', devices: ['FHD Camera', 'FHD IR Camera'] },
  microphone: { device: 'Allow', apps: 'Allow', desktopApps: 'Allow', devices: ['Microphone Array (Intel® Smart Sound Technology for Digital Microphones)'] },
} };
const mct = { fid_idVendor: '1809', fid_idProduct: '22528' };
for (let i = 0; i < 3; i++) s.events.value.events.push(ev('Microsoft-Windows-USB-USBHUB3', 196, 2 + i * 3, 3, 'USB device draining system power when system is idle.', mct));

fs.writeFileSync(path.join(dir, 'demo-many-problems.snap.json'), JSON.stringify(s, null, 2) + '\n');
console.log('wrote demo-many-problems.snap.json');
