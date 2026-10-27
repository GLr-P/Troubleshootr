using System.IO;
using System.Windows;
using System.Windows.Controls;
using Troubleshootr.Core;

namespace Troubleshootr.App.Views;

/// <summary>
/// Report tab: the ticket summary, copy/save/print buttons and the anonymize toggle. Reports (HTML, text, JSON) go to the
/// Reports folder next to the exe.
/// </summary>
public sealed class ReportPage : UserControl
{
    private readonly MainViewModel _vm;
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12.5,
        Padding = new Thickness(12), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 320,
    };
    private readonly TextBlock _saved = new() { Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _anonymize = new()
    {
        Content = "Anonymize: remove user and PC names, serial numbers, MAC addresses and Wi-Fi names", Margin = new Thickness(0, 0, 0, 12),
    };

    public ReportPage(MainViewModel vm)
    {
        _vm = vm;
        _text.SetResourceReference(BackgroundProperty, "Surface");
        _text.SetResourceReference(ForegroundProperty, "Text");
        _text.SetResourceReference(BorderBrushProperty, "Line");
        _saved.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        buttons.Children.Add(Button("Copy ticket summary", (_, _) => { if (Current() is { } r) { Clipboard.SetText(TextReport.Ticket(r)); _saved.Text = "Copied."; } }, primary: true));
        buttons.Children.Add(Button("Open printable report", (_, _) => { if (Save() is { } html) Open(html); }));
        buttons.Children.Add(Button("Save report files", (_, _) => Save()));
        _anonymize.SetResourceReference(ForegroundProperty, "Text");
        _anonymize.Click += (_, _) => Render();

        var root = new DockPanel { Margin = new Thickness(28, 24, 28, 28), LastChildFill = true };
        var title = new TextBlock { Text = "Report", Style = (Style)Application.Current.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6) };
        var hint = new TextBlock { Text = "A short summary for a ticket, a printable report for the customer, and full reports (HTML, text and JSON) saved next to the app, for example on the USB stick.", Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        foreach (var e in new UIElement[] { title, hint, _anonymize, buttons, _saved }) { DockPanel.SetDock(e, Dock.Top); root.Children.Add(e); }
        root.Children.Add(_text);
        Content = root;

        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Report)) Render(); };
        Render();
    }

    private void Render() => _text.Text = Current() is { } r ? TextReport.Ticket(r) : "Run a scan first; the summary appears here.";

    /// <summary>The report as it will be shared: an anonymized copy when the box is ticked.</summary>
    private Report? Current() => _vm.Report is not { } r ? null : _anonymize.IsChecked == true ? Anonymizer.Apply(r) : r;

    /// <summary>Writes the HTML, text and JSON reports; returns the HTML file, or null if nothing was saved.</summary>
    private string? Save()
    {
        if (Current() is not { } r) return null;
        try
        {
            var dir = ReportsFolder();
            var device = _anonymize.IsChecked == true ? "PC" : r.Device.DeviceName ?? "PC";
            var name = $"{Safe(device)}_{r.StartedUtc.ToLocalTime():yyyy-MM-dd_HHmm}{(_anonymize.IsChecked == true ? "_anonymized" : "")}";
            var html = Path.Combine(dir, name + ".html");
            File.WriteAllText(html, HtmlReport.Write(r));
            File.WriteAllText(Path.Combine(dir, name + ".json"), Json.Serialize(r));
            File.WriteAllText(Path.Combine(dir, name + ".txt"), TextReport.Write(r, timings: true));
            _saved.Text = $"Saved {name}.html, .txt and .json to {dir}";
            return html;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _saved.Text = $"Couldn't save: {e.Message}"; return null; }
    }

    private void Open(string file)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception e) { _saved.Text = $"Saved, but couldn't open it: {e.Message}"; }
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
