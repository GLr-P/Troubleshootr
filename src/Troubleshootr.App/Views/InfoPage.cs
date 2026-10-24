using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Troubleshootr.Core;

namespace Troubleshootr.App.Views;

/// <summary>Renders any info tab (a list of InfoGroups) as cards. Built in code so every tab shares one layout.</summary>
public sealed class InfoPage : UserControl
{
    private readonly string _page;
    private readonly MainViewModel _vm;
    private readonly StackPanel _root = new() { Margin = new Thickness(28, 24, 28, 28) };

    public InfoPage(string page, MainViewModel vm)
    {
        _page = page;
        _vm = vm;
        Content = new ScrollViewer { Content = _root };
        vm.SnapshotChanged += Render;
        Render();
    }

    private void Render()
    {
        _root.Children.Clear();
        _root.Children.Add(new TextBlock { Text = _page, Style = St("H1"), Margin = new Thickness(0, 0, 0, 16) });
        foreach (var g in InfoPages.Build(_page, _vm.Snapshot)) _root.Children.Add(Card(g));
    }

    private static Border Card(InfoGroup g)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = g.Title, Style = St("H2"), Margin = new Thickness(0, 0, 0, 10) });
        if (g.Note is not null) panel.Children.Add(new TextBlock { Text = g.Note, Style = St("Dim") });
        if (g.Rows.Count > 0) panel.Children.Add(KeyValues(g.Rows));
        if (g.Columns is not null) panel.Children.Add(g.Table.Count == 0 ? new TextBlock { Text = "None found.", Style = St("Dim") } : Table(g.Columns, g.Table));
        return new Border { Style = (Style)Application.Current.FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = panel };
    }

    private static Grid KeyValues(List<(string Label, string Value)> rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Style = St("Dim"), Margin = new Thickness(0, 3, 12, 3) };
            var value = Selectable(rows[i].Value);
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        return grid;
    }

    private static Grid Table(string[] columns, List<string[]> rows)
    {
        var grid = new Grid();
        foreach (var _ in columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions[columns.Length - 1].Width = new GridLength(1, GridUnitType.Star);
        for (var r = 0; r <= rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns.Length; c++)
            {
                var header = r == 0;
                var text = header ? columns[c] : c < rows[r - 1].Length ? rows[r - 1][c] : "";
                var cell = header
                    ? new TextBlock { Text = text, Style = St("Dim"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 18, 6) }
                    : (FrameworkElement)Selectable(text, new Thickness(0, 3, 18, 3));
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    /// <summary>A borderless read-only TextBox, so values can be selected and copied (serials, versions, device ids).</summary>
    private static TextBox Selectable(string text, Thickness? margin = null) => new()
    {
        Text = text,
        IsReadOnly = true,
        BorderThickness = new Thickness(0),
        Background = System.Windows.Media.Brushes.Transparent,
        Foreground = Ui.Brush("Text"),
        TextWrapping = TextWrapping.Wrap,
        Padding = new Thickness(0),
        Margin = margin ?? new Thickness(0, 3, 0, 3),
        Cursor = Cursors.IBeam,
    };

    private static Style St(string key) => (Style)Application.Current.FindResource(key);
}
