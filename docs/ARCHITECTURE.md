# EZDiag architecture

Read the section you need. The design has one aim: **fast to build and test with AI.** Every check runs against recorded
data, so almost all of the work is tested without real hardware and without opening a window.

## 1. Data flow

```
 Collectors (WMI, registry, event logs, IOCTL, LHM sensors, files)
     │  run in parallel, each with a timeout, each output timed
     ▼
 Snapshot  (one plain C# object tree = raw facts about the PC; serializable to JSON)
     │
     ▼
 Checks  (pure functions: Snapshot → Finding[])
     │
     ▼
 Report  (device summary + findings + snapshot + timings)  →  UI tabs / JSON / HTML / ticket text
```

- The **UI never queries the system directly.** The info tabs (CPU, Memory, Drivers…) display the Snapshot. The only exceptions are the live views (Processes, Sensors), which have their own polling collectors.
- A **scan profile** picks which collectors and checks run: Quick (everything fast), Full (adds slow ones like the event-log history and network tests), or one or more categories (for example Storage).
- Each check declares the Snapshot sections it needs, so a category scan runs only the collectors it needs.

## 2. Solution layout

```
EZDiag/
  EZDiag.sln
  src/
    EZDiag.Core/          netstandard2.0  Snapshot model, Finding, ICheck, ICollector, scan engine, report writers
    EZDiag.Collectors/    netstandard2.0  WMI, registry, event log, IOCTL (SMART/NVMe), files, network, LHM adapter
    EZDiag.Checks/        netstandard2.0  one class per check, grouped by category folder
    EZDiag.Cli/           net472          `ezdiag scan|collect|report`, used by tests and by AI
    EZDiag.App/           net472 WPF      UI shell, tabs, theme, screenshot mode
  tests/
    EZDiag.Tests/         net10.0;net472  xUnit: check tests, golden fixture tests, parser tests
    fixtures/             *.snap.json     recorded, anonymized real PCs + hand-made edge cases
  docs/                   this file, CHECKS.md
  scripts/                publish.ps1, compat helpers
```

Packages (keep the list short): `System.Management` (WMI), `System.Diagnostics.EventLog`, `LibreHardwareMonitorLib`,
`xunit` + `Verify.Xunit` (snapshot-style golden tests), `Costura.Fody` (merges the DLLs into one exe at release).
JSON goes through `System.Text.Json` (works on net472 via its NuGet package).

## 3. Core types (keep them this simple)

```csharp
enum Severity { Ok, Info, Warning, Critical }
enum Category { Storage, Memory, CpuThermal, Graphics, WindowsHealth, Updates, Drivers, Crashes,
                Performance, Network, Security, Battery, Peripherals }

record Finding(string CheckId, Category Category, Severity Severity, string Title,
               string Evidence, string Advice, string? FixActionId = null);

interface ICheck {
  string Id { get; }                 // "STO-004", matches docs/CHECKS.md
  Category Category { get; }
  IReadOnlyList<string> Needs { get; } // Snapshot sections, e.g. "Disks", "Smart"
  IEnumerable<Finding> Run(Snapshot s);
}

interface ICollector {
  string Section { get; }            // fills one Snapshot section
  bool NeedsAdmin { get; }
  bool Slow { get; }                 // excluded from Quick scan
  TimeSpan Timeout { get; }
  Task CollectAsync(Snapshot s, CollectContext ctx, CancellationToken ct);
}
```

- Every Snapshot section is a `Section<T>`: either `Value` or `Unavailable(reason)`. Checks that need missing data return nothing, and the report lists "not checked: needs admin / not supported on this Windows".
- Checks discover each other by reflection (every `ICheck` in EZDiag.Checks), so adding one is a single file.
- Thresholds live as constants at the top of each check class, with a comment saying where the number came from.

## 4. Collectors: speed and compatibility rules

- **Run all collectors in parallel** (`Task.WhenAll`), each with its own timeout and cancellation. Record the duration of each in `Report.Timings`.
- **WMI:** one shared `ManagementScope` per namespace; select only the columns you need (`SELECT Name, Status FROM …`, never `SELECT *` on big classes); use `EnumerationOptions { ReturnImmediately = true, Rewindable = false }`. Watch for classes that don't exist on old Windows (`root\Microsoft\Windows\Storage` / `MSFT_PhysicalDisk` is Windows 8+; Windows 7 falls back to `Win32_DiskDrive` + IOCTL).
- **Prefer faster sources:** registry > IOCTL / Win32 API > WMI > spawning a process. Spawning `powercfg`, `netsh` or `wevtutil` is a last resort (about 100–500 ms each) and must have a timeout.
- **Event logs:** always query with an XPath filter on event ids and a time limit (`TimeCreated[timediff(@SystemTime) <= 2592000000]` = 30 days) through `EventLogReader`; never read whole logs.
- **Registry:** read both 64- and 32-bit views (`RegistryView.Registry64/32`) where it matters (Uninstall, Run keys). The app runs as a 64-bit process on 64-bit Windows (AnyCPU, Prefer32Bit=false).
- **Admin:** the manifest uses `highestAvailable` (admins get the UAC prompt and full data; standard users still get a reduced scan). Collectors that need admin check first and mark themselves Unavailable("needs admin").
- **IOCTL / P/Invoke:** all signatures in one `Native/` folder, `SetLastError = true`, handles in `SafeFileHandle`, and every buffer size checked before parsing.
- **Driver-level data (LHM + PawnIO):** off by default. The Sensors tab offers "Enable advanced sensors", which explains that it installs the signed PawnIO driver (and that some anti-cheats such as FACEIT block it), then installs it after confirmation. Without it, temperatures come from what Windows exposes (ACPI thermal zone, NVMe/SMART temperature, the GPU through vendor APIs if available).

## 5. Tests (the main reason this design exists)

- **Unit tests per check:** build a tiny Snapshot in code (builder helpers in `tests/EZDiag.Tests/Snap.cs`), run the check, assert on the findings. Cover: healthy → no finding, each threshold edge, data Unavailable → no finding and no crash.
- **Golden fixture tests:** for each `tests/fixtures/*.snap.json`, run all checks and compare the findings with the stored `*.verified.txt` (Verify library). A changed result shows as a diff; accept it only if the change is intended.
- **Fixtures come from real PCs:** `ezdiag collect --anonymize --out x.snap.json` on any PC (this laptop, the desktop, client PCs with consent). Each fixture name says what's interesting (`win10-1809-failing-hdd.snap.json`). Hand-edit copies to create edge cases.
- **Parser tests:** SMART attribute tables, NVMe health log bytes, minidump headers and battery reports are parsed from byte arrays or text saved in `tests/fixtures/raw/`.
- **Collector smoke test:** `ezdiag scan --json --timings` on this PC; it must finish and every section must be Value or a clear Unavailable reason. This is the only test that touches real hardware.
- **UI screenshots without windows:** `EZDiag.App --screenshot <tab|all> --fixture <file> --out <dir>` loads a fixture, renders each tab off-screen with `RenderTargetBitmap` at 1280×800 in light and dark, saves PNGs and exits. Look at them after every UI change.
- **Both runtimes:** tests target net10.0 (fast, the default) and net472 (run before releases, since that's what ships).
- **CI:** GitHub Actions `windows-latest` runs build + tests + `scan` on every push (a free second, clean machine).

## 6. UI shell (WPF)

- **Home (Diagnose):** a header with the device summary (name, model, Windows version and build, uptime, admin yes/no); big **Quick scan** and **Full scan** buttons; a grid of category tiles (Storage, Memory, CPU & Thermals, Graphics, Windows Health, Updates, Drivers & Devices, Crashes & Errors, Performance & Startup, Network, Security, Battery & Power, Peripherals). Clicking tiles selects them, and "Scan selected" runs only those. Results stream in as each check finishes, sorted Critical → Info, each expandable to evidence + advice (+ a Fix button where one exists).
- **Info tabs:** Summary, System, CPU, Memory, Storage, Graphics, Motherboard & BIOS, Drivers, Devices, Events & Crashes, Processes (live), Startup, Software, Network, Security, Battery, Sensors (live), Report.
- Plain WPF with one ResourceDictionary theme (light/dark following Windows), virtualized `ListView`/`DataGrid` for long lists, and no third-party UI framework (size and startup time). Every table is sortable, filterable, and copies as text.
- **Startup:** show the window immediately, then start the always-on collectors (System summary) in the background. Nothing scans automatically except the summary header, unless the setting "Quick scan on start" is on.

## 7. Report and outputs

- `Report` = schema version, app version, scan time, device summary, findings, timings, the Snapshot (optional), the fix actions taken.
- Writers: **JSON** (for Plannr, Claude and tests), **HTML** (one self-contained printable page, inkjet-friendly: no dark backgrounds), **ticket text** (a short plain-text summary to paste into any ticket system).
- Reports save to `<app folder>\Reports\<device>_<yyyy-MM-dd_HHmm>.*`, which is the USB stick in normal use.

## 8. Future integrations (design for them now, build later)

- **Plannr:** Plannr gets an "Import diagnostic report" action (drop the JSON onto a ticket, or pick the file). It matches the device by name and serial, then attaches the summary to the open ticket for that customer or creates a new one. Later, an optional direct send through Plannr's sync Worker. The JSON schema is the contract, so keep it versioned and backward compatible.
- **Claude (tech edition):** an "Ask about this PC" panel calling the Anthropic Messages API with the user's own API key (stored in settings and encrypted with Windows DPAPI, never in code). Context = the anonymized report JSON; tool calls can request more detail (for example "list the last 20 System errors"). Same codebase, and the panel is hidden until a key is set, so the public build has it too as bring-your-own-key. Check the current model names in the Anthropic docs when building it.
- **Fix actions:** each one is an `IFixAction` (id, title, what it changes, needs admin, `RunAsync`). Examples: run SFC/DISM, reset the network stack, flush DNS, clear temp files, restart the print spooler, open Windows Update. Each always asks for confirmation and logs its output to the report.
