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
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = g.Title, Style = St("H2"), VerticalAlignment = VerticalAlignment.Center });
        if (g.Badge is not null)
        {
            var text = new TextBlock { Text = g.Badge, FontSize = 12, FontWeight = FontWeights.SemiBold };
            text.SetResourceReference(TextBlock.ForegroundProperty, g.BadgeKey);
            var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(12, 0, 0, 0), Child = text, VerticalAlignment = VerticalAlignment.Center };
            pill.SetResourceReference(Border.BackgroundProperty, g.BadgeKey + "Soft");
            header.Children.Add(pill);
        }
        panel.Children.Add(header);
        if (g.Note is not null) panel.Children.Add(new TextBlock { Text = g.Note, Style = St("Dim") });
        if (g.Rows.Count > 0) panel.Children.Add(KeyValues(g.Rows));
        if (g.Columns is not null)
        {
            if (g.Table.Count == 0) panel.Children.Add(new TextBlock { Text = "None found.", Style = St("Dim") });
            else if (g.Filterable) panel.Children.Add(FilteredTable(g.Columns, g.Table));
            else panel.Children.Add(Table(g.Columns, LimitRows(g.Table, out _)));
            if (!g.Filterable && g.Table.Count > MaxRows) panel.Children.Add(new TextBlock { Text = $"Showing the first {MaxRows} of {g.Table.Count}.", Style = St("Dim") });
        }
        if (g.Action is { } action) panel.Children.Add(ActionRow(action.Label, action.Run));
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

    /// <summary>A button that runs work in the background and shows its result (or error) next to it.</summary>
    private static StackPanel ActionRow(string label, Func<CancellationToken, Task<string>> run)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var button = new Button { Content = label, Tag = "primary" };
        var result = new TextBlock { Style = St("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            result.Text = "Running...";
            try { result.Text = await Task.Run(() => run(CancellationToken.None)); }
            catch (Exception e) { result.Text = $"Failed: {e.GetBaseException().Message}"; }
            finally { button.IsEnabled = true; }
        };
        row.Children.Add(button);
        row.Children.Add(result);
        return row;
    }

    /// <summary>Tables are grids of text boxes, so very long ones are cut to keep the tab fast.</summary>
    private const int MaxRows = 150;

    private static List<string[]> LimitRows(List<string[]> rows, out int more)
    {
        more = Math.Max(0, rows.Count - MaxRows);
        return more == 0 ? rows : rows.Take(MaxRows).ToList();
    }

    /// <summary>A filter box (matches any column, case-insensitive) above the table.</summary>
    private static StackPanel FilteredTable(string[] columns, List<string[]> rows)
    {
        var panel = new StackPanel();
        var box = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(4, 3, 4, 3) };
        box.SetResourceReference(TextBox.BackgroundProperty, "Surface2");
        box.SetResourceReference(TextBox.ForegroundProperty, "Text");
        box.SetResourceReference(TextBox.BorderBrushProperty, "Line");
        var hint = new TextBlock { Style = St("Dim"), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 8) };
        var holder = new ContentControl();
        void Apply()
        {
            var text = box.Text.Trim();
            var match = text.Length == 0 ? rows : rows.Where(r => r.Any(c => c.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            holder.Content = match.Count == 0 ? new TextBlock { Text = "Nothing matches.", Style = St("Dim") } : Table(columns, LimitRows(match, out _));
            hint.Text = $"Filter: type a word, source or ID. {match.Count} of {rows.Count} shown{(match.Count > MaxRows ? $" (first {MaxRows})" : "")}.";
        }
        box.TextChanged += (_, _) => Apply();
        Apply();
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(holder);
        return panel;
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
