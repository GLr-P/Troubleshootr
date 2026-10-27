using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Troubleshootr.Core;

namespace Troubleshootr.App.Views;

public partial class DiagnosePage : UserControl
{
    private readonly MainViewModel _vm;

    public DiagnosePage(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MainViewModel.Device) or nameof(MainViewModel.Report) or nameof(MainViewModel.Scanning)) Refresh(); };
        vm.Findings.CollectionChanged += (_, _) => Refresh();
        AdminBadge.Visibility = ElevateButton.Visibility = vm.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private void Refresh()
    {
        var d = _vm.Device;
        ModelLine.Text = $"{d.Manufacturer} {d.Model}".Trim();
        var parts = new[] { d.Cpu, d.RamGB is { } gb ? $"{gb:0.#} GB RAM" : null, d.Gpu }.Where(p => !string.IsNullOrEmpty(p));
        SpecLine.Text = string.Join("  ·  ", parts);
        NoProblems.Visibility = _vm.HasReport && !_vm.Scanning && _vm.Findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotCheckedBox.Visibility = _vm.NotChecked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotCheckedHeader.Text = $"{_vm.NotChecked.Count} check{(_vm.NotChecked.Count == 1 ? "" : "s")} not run (show why)";
    }

    private List<Category> SelectedCategories() => _vm.Tiles.Where(t => t.Selected).Select(t => t.Category).ToList();

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        var n = SelectedCategories().Count;
        SelectedButton.IsEnabled = n > 0;
        SelectedButton.Content = n > 0 ? $"Scan selected ({n})" : "Scan selected";
    }

    private async void Quick_Click(object sender, RoutedEventArgs e) => await _vm.ScanAsync(full: false, null);
    private async void Full_Click(object sender, RoutedEventArgs e) => await _vm.ScanAsync(full: true, null);
    private async void Selected_Click(object sender, RoutedEventArgs e) => await _vm.ScanAsync(full: true, SelectedCategories());

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FindingItem { Fix: { } fix } }) await FixesPage.Run(_vm, fix);
    }

    private void Elevate_Click(object sender, RoutedEventArgs e) => MainViewModel.RestartAsAdmin();
}
