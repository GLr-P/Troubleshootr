// Builds tests/fixtures/demo-many-problems.snap.json from the real laptop fixture, adding one of every problem
// the checks know about, so UI screenshots show every kind of finding. Run: node scripts/make-demo-fixture.js
const fs = require('fs');
const path = require('path');
const dir = path.join(__dirname, '..', 'tests', 'fixtures');
const s = JSON.parse(fs.readFileSync(path.join(dir, 'win11-25h2-laptop-msi.snap.json'), 'utf8'));

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

fs.writeFileSync(path.join(dir, 'demo-many-problems.snap.json'), JSON.stringify(s, null, 2) + '\n');
console.log('wrote demo-many-problems.snap.json');
