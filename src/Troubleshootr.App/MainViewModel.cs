using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Troubleshootr.Checks;
using Troubleshootr.Collectors;
using Troubleshootr.Core;

namespace Troubleshootr.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}

public sealed class CategoryTile(Category category) : Observable
{
    private bool _selected;
    public Category Category { get; } = category;
    public string Label => Ui.Categories[Category].Label;
    public string Glyph => Ui.Categories[Category].Glyph;
    /// <summary>False until the category has at least one check (the tile shows "coming soon").</summary>
    public bool Available { get; } = CheckRegistry.All.Any(c => c.Category == category);
    public string Hint => Available ? Ui.Categories[Category].Hint : "Coming in a later version";
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class FindingItem(Finding f)
{
    public Finding Finding { get; } = f;
    public string Title => Finding.Title;
    public string Evidence => Finding.Evidence;
    public string Advice => Finding.Advice;
    public string Meta => $"{Ui.Categories[Finding.Category].Label} · {Finding.CheckId}";
    public string Glyph => Ui.SeverityGlyph(Finding.Severity);
    public string Key => Ui.SeverityKey(Finding.Severity);
}

public sealed class MainViewModel : Observable
{
    private Report? _report;
    private bool _scanning;
    private string _status = "Ready. Pick a scan, or select areas and click Scan selected.";

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public Snapshot Snapshot { get; private set; } = new();
    /// <summary>False in screenshot mode: live views (Processes) show the loaded snapshot instead of this PC.</summary>
    public bool Live { get; set; } = true;
    public DeviceSummary Device => DeviceSummary.From(Snapshot);
    public bool IsAdmin { get; } = CollectorRegistry.IsElevated();
    public ObservableCollection<CategoryTile> Tiles { get; } = new(Enum.GetValues(typeof(Category)).Cast<Category>().Select(c => new CategoryTile(c)));
    public ObservableCollection<FindingItem> Findings { get; } = new();
    public ObservableCollection<NotChecked> NotChecked { get; } = new();

    public Report? Report { get => _report; private set { Set(ref _report, value); Raise(nameof(HasReport)); } }
    public bool HasReport => Report is not null;
    public bool Scanning { get => _scanning; private set { Set(ref _scanning, value); Raise(nameof(Idle)); } }
    public bool Idle => !Scanning;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public int Critical => Findings.Count(f => f.Finding.Severity == Severity.Critical);
    public int Warnings => Findings.Count(f => f.Finding.Severity == Severity.Warning);
    public int Infos => Findings.Count(f => f.Finding.Severity == Severity.Info);

    /// <summary>Raised on the UI thread whenever the snapshot gains data (the info tabs redraw).</summary>
    public event Action? SnapshotChanged;

    /// <summary>Collects the header sections only (no checks); runs at startup.</summary>
    public Task LoadDeviceAsync() => RunAsync(new ScanOptions { Categories = [], IsAdmin = IsAdmin, ToolVersion = Version }, header: true);

    public Task ScanAsync(bool full, IReadOnlyCollection<Category>? categories) =>
        RunAsync(new ScanOptions { Full = full, Categories = categories, IsAdmin = IsAdmin, ToolVersion = Version }, header: false);

    private async Task RunAsync(ScanOptions options, bool header)
    {
        if (Scanning) return;
        Scanning = true;
        var dispatcher = Application.Current.Dispatcher;
        if (!header)
        {
            Findings.Clear();
            NotChecked.Clear();
            RaiseCounts();
            Status = options.Full ? "Full scan running…" : "Scanning…";
        }
        try
        {
            var report = await Task.Run(() => ScanEngine.ScanAsync(CollectorRegistry.All, CheckRegistry.All, options,
                f => dispatcher.BeginInvoke(new Action(() => { Insert(f); RaiseCounts(); }))));
            Snapshot.MergeFrom(report.Snapshot!);
            report.Snapshot = Snapshot;
            Raise(nameof(Device));
            SnapshotChanged?.Invoke();
            if (header) return;
            Show(report);
            Status = $"{(options.Full ? "Full" : options.Categories is null ? "Quick" : "Selected")} scan finished in {report.DurationMs / 1000.0:0.0} s: " +
                     (report.Findings.Count == 0 ? "no problems found." : $"{Critical} critical, {Warnings} warnings, {Infos} notes.");
        }
        finally { Scanning = false; }
    }

    /// <summary>Shows an existing report (screenshots, opening a saved report).</summary>
    public void Show(Report report)
    {
        if (report.Snapshot is { } s && !ReferenceEquals(s, Snapshot)) { Snapshot = s; Raise(nameof(Device)); SnapshotChanged?.Invoke(); }
        Report = report;
        Findings.Clear();
        foreach (var f in report.Findings) Findings.Add(new FindingItem(f));
        NotChecked.Clear();
        foreach (var n in report.NotChecked) NotChecked.Add(n);
        RaiseCounts();
    }

    private void Insert(Finding f)
    {
        var i = 0;
        while (i < Findings.Count && Findings[i].Finding.Severity >= f.Severity) i++;
        Findings.Insert(i, new FindingItem(f));
    }

    private void RaiseCounts() { Raise(nameof(Critical)); Raise(nameof(Warnings)); Raise(nameof(Infos)); }
}
