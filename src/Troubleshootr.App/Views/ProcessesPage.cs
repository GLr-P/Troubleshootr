using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Troubleshootr.Collectors;
using Troubleshootr.Core;

namespace Troubleshootr.App.Views;

/// <summary>
/// Live Processes tab (a 1.5 s refresh while the tab is visible): CPU, memory and disk per process, sortable, with
/// "open file location". Read-only: ending processes comes with the fix-action framework (Phase 12). In screenshot
/// mode it shows the scan's sample instead of live data.
/// </summary>
public sealed class ProcessesPage : UserControl
{
    private const int Rows = 60;
    private static readonly string[] Columns = ["Process", "CPU", "Memory", "Disk", ""];

    private readonly MainViewModel _vm;
    private readonly Grid _table = new();
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBox _filter = new() { Width = 260, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private ProcessSampler? _sampler;
    private List<ProcessSample> _rows = new();
    private string _sort = "CPU";
    private bool _busy;

    public ProcessesPage(MainViewModel vm)
    {
        _vm = vm;
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _filter.SetResourceReference(BackgroundProperty, "Surface2");
        _filter.SetResourceReference(ForegroundProperty, "Text");
        _filter.SetResourceReference(BorderBrushProperty, "Line");
        _filter.TextChanged += (_, _) => Render();

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 28) };
        root.Children.Add(new TextBlock { Text = "Processes", Style = St("H1"), Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(_summary);
        root.Children.Add(_filter);
        root.Children.Add(new Border { Style = St("Card"), Child = _table });
        Content = new ScrollViewer { Content = root };

        _timer.Tick += async (_, _) => await Refresh();
        IsVisibleChanged += async (_, _) =>
        {
            if (!_vm.Live) return;
            if (IsVisible) { _sampler ??= new ProcessSampler(); _timer.Start(); await Refresh(); }
            else _timer.Stop();
        };
        _vm.SnapshotChanged += () => { if (!_vm.Live) ShowSnapshot(); };
        if (!_vm.Live) ShowSnapshot();
        else _summary.Text = "Live view of what's running, updated every 1.5 seconds. Click a column to sort; the folder icon opens the file's location.";
    }

    private void ShowSnapshot()
    {
        var p = _vm.Snapshot.Performance?.Value;
        _rows = p?.Processes ?? new();
        _summary.Text = p is null ? "No sample in this report." : $"The busiest processes during the scan's {p.SampleSeconds:0} s sample (live when the app runs).";
        Render();
    }

    private async Task Refresh()
    {
        if (_busy || _sampler is null) return;
        _busy = true;
        try
        {
            var sampler = _sampler;
            _rows = await Task.Run(sampler.Sample);
            var cpu = _rows.Sum(r => r.CpuPercent);
            _summary.Text = $"{_rows.Count} processes · {Math.Min(100, cpu):0}% CPU in use by the ones listed · updated {DateTime.Now:HH:mm:ss}";
            Render();
        }
        finally { _busy = false; }
    }

    private void Render()
    {
        var text = _filter.Text.Trim();
        IEnumerable<ProcessSample> rows = _rows;
        if (text.Length > 0) rows = rows.Where(r => r.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || (r.Path ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        rows = _sort switch
        {
            "Memory" => rows.OrderByDescending(r => r.PrivateMB),
            "Disk" => rows.OrderByDescending(r => r.DiskBytesPerSec),
            "Process" => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(r => r.CpuPercent).ThenByDescending(r => r.PrivateMB),
        };
        var list = rows.Take(Rows).ToList();

        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Clear();
        _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var w in new[] { 80, 100, 100, 40 }) _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        for (var r = 0; r <= list.Count; r++)
        {
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < Columns.Length; c++)
            {
                FrameworkElement cell;
                if (r == 0)
                {
                    var name = Columns[c];
                    var header = new TextBlock { Text = name == _sort ? $"{name} ▾" : name, Style = St("Dim"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 8), Cursor = name.Length > 0 ? Cursors.Hand : null };
                    if (name.Length > 0) header.MouseLeftButtonUp += (_, _) => { _sort = name; Render(); };
                    cell = header;
                }
                else
                {
                    var p = list[r - 1];
                    if (c == 4)
                    {
                        var open = new TextBlock { Text = p.Path is null ? "" : "", Style = St("Icon"), Cursor = p.Path is null ? null : Cursors.Hand, ToolTip = p.Path, Margin = new Thickness(0, 3, 0, 3) };
                        if (p.Path is { } path) open.MouseLeftButtonUp += (_, _) => OpenLocation(path);
                        cell = open;
                    }
                    else
                    {
                        var value = c switch
                        {
                            0 => p.Name, 1 => $"{p.CpuPercent:0.#}%", 2 => p.PrivateMB >= 1024 ? $"{p.PrivateMB / 1024.0:0.#} GB" : $"{p.PrivateMB} MB",
                            _ => p.DiskBytesPerSec <= 0 ? "" : p.DiskBytesPerSec >= 1048576 ? $"{p.DiskBytesPerSec / 1048576.0:0.#} MB/s" : $"{p.DiskBytesPerSec / 1024} KB/s",
                        };
                        cell = new TextBlock { Text = value, Margin = new Thickness(0, 3, 12, 3), ToolTip = c == 0 ? p.Path : null };
                    }
                }
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                _table.Children.Add(cell);
            }
        }
    }

    /// <summary>Opens Explorer with the file selected. Harmless, so no confirmation.</summary>
    private static void OpenLocation(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static Style St(string key) => (Style)Application.Current.FindResource(key);
}
