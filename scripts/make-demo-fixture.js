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
  displays: [{ adapter: 'Intel(R) Arc(TM) Graphics', monitor: 'Generic PnP Monitor', primary: true, width: 2560, height: 1600, refreshHz: 60, maxRefreshHz: 165, bitsPerPixel: 32 }],
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
s.health.value.licenseStatus = 5;
s.health.value.licenseName = 'Windows(R), Professional edition';
s.cpu.value.packages[0] ={ ...s.cpu.value.packages[0], name: '13th Gen Intel(R) Core(TM) i9-13900K', identifier: 'Intel64 Family 6 Model 183 Stepping 1', microcode: '0x119' };

fs.writeFileSync(path.join(dir, 'demo-many-problems.snap.json'), JSON.stringify(s, null, 2) + '\n');
console.log('wrote demo-many-problems.snap.json');
