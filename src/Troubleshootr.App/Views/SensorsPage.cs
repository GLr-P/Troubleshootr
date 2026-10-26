using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Troubleshootr.Collectors.Sensors;
using Troubleshootr.Core;

namespace Troubleshootr.App.Views;

/// <summary>
/// Live Sensors tab (1 s refresh while visible): temperatures, fans, clocks, power and loads with min/max, the
/// "Enable advanced sensors" flow (explain, confirm, download the signed PawnIO setup, install with one UAC prompt),
/// and the 30 s CPU stress test. In screenshot/report mode it shows the scan's readings.
/// </summary>
public sealed class SensorsPage : UserControl
{
    private static readonly string[] Shown = ["Temperature", "Fan", "Clock", "Power", "Load", "Voltage", "Control"];

    private readonly MainViewModel _vm;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly WrapPanel _buttons = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _testResult = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
    private readonly StackPanel _cards = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SensorSession? _session;
    private List<SensorReading> _thermal = new();
    private bool _busy;
    private bool _testing;

    public SensorsPage(MainViewModel vm)
    {
        _vm = vm;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _testResult.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 28) };
        root.Children.Add(new TextBlock { Text = "Sensors", Style = St("H1"), Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(_status);
        root.Children.Add(_buttons);
        root.Children.Add(_testResult);
        root.Children.Add(_cards);
        Content = new ScrollViewer { Content = root };

        _timer.Tick += async (_, _) => await Refresh();
        IsVisibleChanged += async (_, _) =>
        {
            if (!_vm.Live) return;
            if (IsVisible) { _timer.Start(); await Refresh(); } else _timer.Stop();
        };
        _vm.SnapshotChanged += () => { if (!_vm.Live) ShowSnapshot(); };
        if (_vm.Live) UpdateControls(); else ShowSnapshot();
    }

    private void ShowSnapshot()
    {
        var x = _vm.Snapshot.Sensors?.Value;
        _status.Text = x is null ? "No sensor readings in this report." : $"Readings from the scan. Advanced sensors (PawnIO driver): {(x.DriverInstalled ? "installed" : "not installed")}.";
        Render(x?.Readings ?? [], x?.ThermalZones ?? []);
    }

    private async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_session is null)
            {
                _status.Text = "Starting sensors...";
                _session = await Task.Run(SensorSession.Open);
                _thermal = await Task.Run(SensorsCollector.ThermalZones);
                UpdateControls();
            }
            var session = _session;
            var readings = await Task.Run(session.Read);
            Render(readings, _thermal);
        }
        finally { _busy = false; }
    }

    /// <summary>The status line and buttons depend on: driver installed, running as admin, a test running.</summary>
    private void UpdateControls()
    {
        var driver = PawnIo.Installed;
        _status.Text = (_session?.Problem is { } p ? $"Sensors limited: {p}. " : "")
            + (!driver ? "Graphics-card sensors work now. CPU, memory and motherboard temperatures need advanced sensors (the free, signed PawnIO driver)."
               : !_vm.IsAdmin ? "Advanced sensors are installed. Restart Troubleshootr as admin to read CPU, memory and motherboard temperatures."
               : "All sensors, updated every second. Min and max are since this tab opened.");
        _buttons.Children.Clear();
        if (!driver) _buttons.Children.Add(Button("Enable advanced sensors", EnableDriver, primary: true));
        else if (!_vm.IsAdmin) _buttons.Children.Add(Button("Restart as admin", MainViewModel.RestartAsAdmin, primary: true));
        _buttons.Children.Add(Button(_testing ? "Stress test running..." : $"Run {LoadTest.Seconds} s CPU stress test", RunLoadTest, enabled: !_testing));
        if (driver) _buttons.Children.Add(Button("Remove advanced sensors", RemoveDriver));
    }

    private void Render(List<SensorReading> readings, List<SensorReading> thermal)
    {
        _cards.Children.Clear();
        foreach (var hw in readings.Where(r => Shown.Contains(r.Type) && !(r.Type is "Power" or "Clock" && r.Value == 0)).GroupBy(r => (r.Kind, r.Hardware)).OrderBy(g => Order(g.Key.Kind)))
        {
            var g = new InfoGroup($"{hw.Key.Hardware}") { Columns = ["Sensor", "Now", "Min", "Max"] };
            foreach (var r in hw.OrderBy(r => Array.IndexOf(Shown, r.Type)))
                g.Table.Add([r.Name.IndexOf(r.Type, StringComparison.OrdinalIgnoreCase) >= 0 ? r.Name : $"{r.Name} ({r.Type.ToLowerInvariant()})", r.Format(r.Value), r.Min is { } mn ? r.Format(mn) : "", r.Max is { } mx ? r.Format(mx) : ""]);
            _cards.Children.Add(InfoPage.Card(g));
        }
        if (thermal.Count > 0)
        {
            var g = new InfoGroup("Windows thermal zones") { Note = "From the motherboard's ACPI tables, no driver needed. Often a board or chipset sensor, sometimes a fixed value.", Columns = ["Zone", "Temperature"] };
            foreach (var t in thermal) g.Table.Add([t.Name, t.Format(t.Value)]);
            _cards.Children.Add(InfoPage.Card(g));
        }
        if (_cards.Children.Count == 0) _cards.Children.Add(new TextBlock { Text = "No sensors found.", Style = St("Dim") });
    }

    private static int Order(string kind) => kind switch { "CPU" => 0, "GPU" => 1, "Memory" => 2, "Motherboard" => 3, _ => 4 };

    private async void EnableDriver()
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            "Advanced sensors use PawnIO, a free, signed driver (pawnio.eu) that lets programs read CPU, memory and motherboard sensors safely. "
            + "LibreHardwareMonitor, FanControl and similar tools use it too.\n\n"
            + "Troubleshootr will download the official setup from GitHub, check its digital signature, and install it. Windows will ask for admin permission once.\n\n"
            + "Some game anti-cheats refuse to run while hardware drivers like this are installed. You can remove it here at any time.\n\nInstall it now?",
            "Enable advanced sensors", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await RunSetup(uninstall: false);
    }

    private async void RemoveDriver()
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "Remove the PawnIO driver? CPU, memory and motherboard temperatures will stop working until it's installed again.",
                "Remove advanced sensors", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunSetup(uninstall: true);
    }

    private async Task RunSetup(bool uninstall)
    {
        // Stop reading and close the sensors first: an open session holds the driver, which blocks removal, and must
        // not be closed in the middle of a read.
        await PauseLive();
        _status.Text = uninstall ? "Downloading the PawnIO setup to remove it..." : "Downloading the PawnIO setup...";
        string message;
        try
        {
            using var setup = await PawnIo.DownloadAsync(CancellationToken.None);
            _status.Text = $"Setup signed by {setup.Publisher}. Waiting for the Windows admin prompt...";
            var (ok, text) = await Task.Run(() => PawnIo.Run(setup, uninstall));
            message = ok ? text : $"Didn't work: {text}.";
            if (ok) ActionLog.Write($"{(uninstall ? "Removed" : "Installed")} PawnIO (setup signed by {setup.Publisher}): {text}");
        }
        catch (Exception e)  // no internet, GitHub down, bad signature: say so, change nothing
        {
            message = $"Didn't work: {e.Message}.";
        }
        MessageBox.Show(Window.GetWindow(this)!, message, "Advanced sensors", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdateControls();
        ResumeLive();
    }

    /// <summary>Stops the timer, waits for a read in progress, and closes the session (reopened by the next Refresh).</summary>
    private async Task PauseLive()
    {
        _timer.Stop();
        while (_busy) await Task.Delay(50);
        _session?.Dispose();
        _session = null;
    }

    private void ResumeLive()
    {
        if (!IsVisible) return;
        _timer.Start();
        _ = Refresh();
    }

    private async void RunLoadTest()
    {
        _testing = true;
        UpdateControls();
        _testResult.Text = "Stress test: every core busy, the PC may feel slow and the fans will spin up...";
        // The test reads the sensors itself (one reader at a time), so live updates pause until it's done.
        _timer.Stop();
        while (_busy) await Task.Delay(50);
        try
        {
            var session = _session;
            var progress = new Progress<LoadTest.Sample>(s => _testResult.Text = $"Stress test: {s.Second} of {LoadTest.Seconds} s, speed {s.SpeedPercent:0}% of base{(s.TempC is { } t ? $", {t:0} °C" : "")}");
            var result = await Task.Run(() => LoadTest.RunAsync(((IProgress<LoadTest.Sample>)progress).Report, CancellationToken.None, session));
            _testResult.Text = "Stress test result: " + result.Summary;
        }
        finally
        {
            _testing = false;
            UpdateControls();
            ResumeLive();
        }
    }

    private static Button Button(string text, Action click, bool primary = false, bool enabled = true)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 10, 8), Tag = primary ? "primary" : null, IsEnabled = enabled };
        b.Click += (_, _) => click();
        return b;
    }

    private static Style St(string key) => (Style)Application.Current.FindResource(key);
}
