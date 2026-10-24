using System.IO;
using System.Windows;
using System.Windows.Controls;
using Troubleshootr.Core;

namespace Troubleshootr.App.Views;

/// <summary>Report tab: the ticket summary text, plus save and copy buttons. Reports go to the Reports folder next to the exe.</summary>
public sealed class ReportPage : UserControl
{
    private readonly MainViewModel _vm;
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12.5,
        Padding = new Thickness(12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 320,
    };
    private readonly TextBlock _saved = new() { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public ReportPage(MainViewModel vm)
    {
        _vm = vm;
        _text.SetResourceReference(BackgroundProperty, "Surface");
        _text.SetResourceReference(ForegroundProperty, "Text");
        _text.SetResourceReference(BorderBrushProperty, "Line");
        _saved.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        buttons.Children.Add(Button("Copy ticket summary", (_, _) => { if (_vm.Report is { } r) Clipboard.SetText(TextReport.Ticket(r)); _saved.Text = "Copied."; }, primary: true));
        buttons.Children.Add(Button("Save report files", (_, _) => Save()));
        buttons.Children.Add(_saved);

        var root = new DockPanel { Margin = new Thickness(28, 24, 28, 28), LastChildFill = true };
        var title = new TextBlock { Text = "Report", Style = (Style)Application.Current.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6) };
        var hint = new TextBlock { Text = "A short summary for a ticket, and full reports (text + JSON) saved next to the app, for example on the USB stick.", Margin = new Thickness(0, 0, 0, 14) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        foreach (var e in new UIElement[] { title, hint, buttons }) { DockPanel.SetDock(e, Dock.Top); root.Children.Add(e); }
        root.Children.Add(_text);
        Content = root;

        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Report)) Render(); };
        Render();
    }

    private void Render() => _text.Text = _vm.Report is { } r ? TextReport.Ticket(r) : "Run a scan first; the summary appears here.";

    private void Save()
    {
        if (_vm.Report is not { } r) return;
        try
        {
            var dir = ReportsFolder();
            var name = $"{Safe(r.Device.DeviceName ?? "PC")}_{r.StartedUtc.ToLocalTime():yyyy-MM-dd_HHmm}";
            File.WriteAllText(Path.Combine(dir, name + ".json"), Json.Serialize(r));
            File.WriteAllText(Path.Combine(dir, name + ".txt"), TextReport.Write(r, timings: true));
            _saved.Text = $"Saved to {dir}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _saved.Text = $"Couldn't save: {e.Message}"; }
    }

    /// <summary>"Reports" next to the exe (the USB stick); %LOCALAPPDATA% if that drive is read-only.</summary>
    public static string ReportsFolder()
    {
        foreach (var dir in new[] { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports"),
                                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Troubleshootr", "Reports") })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        throw new IOException("No writable folder for reports");
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static Button Button(string text, RoutedEventHandler click, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 10, 0), Tag = primary ? "primary" : null };
        b.Click += click;
        return b;
    }
}
