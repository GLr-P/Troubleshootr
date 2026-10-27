using System.Windows;
using System.Windows.Controls;
using Troubleshootr.Collectors.Fixes;
using Troubleshootr.Core;

namespace Troubleshootr.App.Views;

/// <summary>Fixes tab: every fix action with what it changes, a Run button (after a confirmation) and the live output.</summary>
public sealed class FixesPage : UserControl
{
    private readonly MainViewModel _vm;
    private readonly List<Button> _buttons = new();
    private readonly TextBox _output = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12,
        Padding = new Thickness(10), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 220, Margin = new Thickness(0, 6, 0, 0),
    };

    public FixesPage(MainViewModel vm)
    {
        _vm = vm;
        _output.SetResourceReference(BackgroundProperty, "Surface");
        _output.SetResourceReference(ForegroundProperty, "Text");
        _output.SetResourceReference(BorderBrushProperty, "Line");

        var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 28) };
        stack.Children.Add(new TextBlock { Text = "Fixes", Style = (Style)Application.Current.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6) });
        var hint = Dim("Common repairs. Each one asks first, and what it did goes in the report and in Troubleshootr-actions.log next to the app.", 0);
        hint.Margin = new Thickness(0, 0, 0, 14);
        stack.Children.Add(hint);
        foreach (var fix in FixRegistry.All) stack.Children.Add(Card(fix));
        stack.Children.Add(new TextBlock { Text = "Output", Style = (Style)Application.Current.FindResource("H2"), Margin = new Thickness(0, 10, 0, 0) });
        stack.Children.Add(_output);
        Content = new ScrollViewer { Content = stack };

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.FixOutput)) { _output.Text = vm.FixOutput; _output.ScrollToEnd(); }
            if (e.PropertyName == nameof(MainViewModel.RunningFix)) Refresh();
        };
        _output.Text = vm.FixOutput.Length > 0 ? vm.FixOutput : "Nothing run yet.";
        Refresh();
    }

    private void Refresh()
    {
        foreach (var b in _buttons) b.IsEnabled = _vm.FixIdle;
    }

    private Border Card(IFixAction fix)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = fix.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        text.Children.Add(Dim(fix.Changes, 4));
        text.Children.Add(Dim($"Takes {fix.Duration}{(fix.NeedsAdmin ? " · needs admin" : "")}", 0, 11.5));
        var run = new Button { Content = "Run", MinWidth = 80, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 0, 0, 0) };
        run.Click += async (_, _) => await Run(_vm, fix);
        _buttons.Add(run);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(run, 1);
        grid.Children.Add(text);
        grid.Children.Add(run);
        return new Border { Style = (Style)Application.Current.FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Child = grid };
    }

    /// <summary>Confirms, then runs the fix and shows the Fixes page. Offers to restart as admin when the fix needs it.</summary>
    public static async Task Run(MainViewModel vm, IFixAction fix)
    {
        if (!vm.FixIdle) return;
        if (fix.NeedsAdmin && !vm.IsAdmin)
        {
            if (MessageBox.Show($"\"{fix.Title}\" needs admin rights.\n\nRestart Troubleshootr as admin now? Scan again afterwards.", "Troubleshootr",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                MainViewModel.RestartAsAdmin();
            return;
        }
        if (MessageBox.Show($"{fix.Changes}\n\nTakes {fix.Duration}. Run it now?", fix.Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        vm.GoTo("Fixes");
        var record = await vm.RunFixAsync(fix);
        if (record is not null)
            MessageBox.Show(record.Summary, fix.Title, MessageBoxButton.OK, record.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static TextBlock Dim(string text, double top, double size = 13)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), FontSize = size };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        return t;
    }
}
