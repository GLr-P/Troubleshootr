using System.Windows;
using System.Windows.Controls;
using Troubleshootr.App.Views;

namespace Troubleshootr.App;

/// <summary>Left navigation + the selected page. Pages are created on first visit and kept.</summary>
public sealed class Shell : UserControl
{
    public static readonly string[] Pages = ["Diagnose", .. InfoPages.Names, "Fixes", "Ask Claude", "Report"];

    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["Diagnose"] = "", ["Summary"] = "", ["System"] = "", ["CPU"] = "", ["Memory"] = "",
        ["Motherboard"] = "", ["Storage"] = "", ["Graphics"] = "", ["Devices"] = "", ["Performance"] = "", ["Processes"] = "", ["Startup"] = "", ["Software"] = "",["Drivers"] = "", ["Updates"] = "",["Events & Crashes"] = "", ["Report"] = "", ["Fixes"] = "", ["Ask Claude"] = "",
        ["Network"] = "", ["Security"] = "", ["Sensors"] = "", ["Peripherals"] = "", ["Battery"] = "",
    };

    private readonly MainViewModel _vm;
    private readonly ContentControl _content = new();
    private readonly ListBox _nav = new() { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
    private readonly Dictionary<string, UIElement> _cache = new();

    public Shell(MainViewModel vm)
    {
        _vm = vm;
        SetResourceReference(BackgroundProperty, "Bg");
        foreach (var p in Pages) _nav.Items.Add(NavItem(p));
        _nav.SelectionChanged += (_, _) => { if (_nav.SelectedItem is ListBoxItem { Tag: string p }) Show(p); };

        var side = new DockPanel { Width = 200 };
        side.SetResourceReference(BackgroundProperty, "Surface");
        var brand = new TextBlock { Text = "Troubleshootr", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 18, 0, 2) };
        var version = new TextBlock { Text = $"v{MainViewModel.Version}", Margin = new Thickness(18, 0, 0, 14), FontSize = 11 };
        version.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        DockPanel.SetDock(brand, Dock.Top);
        DockPanel.SetDock(version, Dock.Top);
        side.Children.Add(brand);
        side.Children.Add(version);
        side.Children.Add(_nav);

        var line = new Border { Width = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(line, 1);
        Grid.SetColumn(_content, 2);
        grid.Children.Add(side);
        grid.Children.Add(line);
        grid.Children.Add(_content);
        Content = grid;
        _nav.SelectedIndex = 0;
        vm.Navigate += Show;
    }

    public void Show(string page)
    {
        if (!_cache.TryGetValue(page, out var view))
            _cache[page] = view = page switch
            {
                "Diagnose" => new DiagnosePage(_vm),
                "Report" => new ReportPage(_vm),
                "Fixes" => new FixesPage(_vm),
                "Ask Claude" => new AskClaudePage(_vm),
                "Processes" => new ProcessesPage(_vm),
                "Sensors" => new SensorsPage(_vm),
                _ => new InfoPage(page, _vm),
            };
        _content.Content = view;
        var index = Array.IndexOf(Pages, page);
        if (_nav.SelectedIndex != index) _nav.SelectedIndex = index;
    }

    private static ListBoxItem NavItem(string page)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = Glyphs.TryGetValue(page, out var g) ? g : "", Style = (Style)Application.Current.FindResource("Icon"), FontSize = 14, Width = 26 });
        row.Children.Add(new TextBlock { Text = page, FontSize = 13.5 });
        return new ListBoxItem { Content = row, Tag = page, Style = (Style)Application.Current.FindResource("Nav") };
    }
}
