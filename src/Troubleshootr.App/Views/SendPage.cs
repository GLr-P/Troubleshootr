using System.Windows;
using System.Windows.Controls;
using Troubleshootr.Collectors.Plannr;
using Troubleshootr.Core;

namespace Troubleshootr.App.Views;

/// <summary>
/// "Send to Plannr": puts the scan into the technician's Plannr as a new ticket, an addition to an open ticket, or a note,
/// with the full report attached. Goes through the Plannr inbox link (Settings), so nothing is copied off the stick by hand.
/// </summary>
public sealed class SendPage : UserControl
{
    private readonly MainViewModel _vm;
    private readonly Settings _settings = Settings.Current;
    private PlannrLink? _link;

    private readonly StackPanel _setup = new();
    private readonly StackPanel _send = new();
    private readonly TextBox _linkBox = new() { Width = 520, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4, 6, 4) };
    private readonly RadioButton _newTicket = new() { Content = "Create a new ticket", IsChecked = true, GroupName = "target" };
    private readonly RadioButton _existing = new() { Content = "Add to an open ticket", GroupName = "target", Margin = new Thickness(0, 12, 0, 0) };
    private readonly RadioButton _note = new() { Content = "Create a note", GroupName = "target", Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBox _customer = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4, 6, 4) };
    private readonly ComboBox _tickets = new() { Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _sendButton = new() { Content = "Send to Plannr", Tag = "primary", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(18, 8, 18, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private bool _busy;

    public SendPage(MainViewModel vm)
    {
        _vm = vm;
        _link = PlannrLink.TryParse(_settings.PlannrLink);
        foreach (var box in new Control[] { _linkBox, _customer })
        {
            box.SetResourceReference(BackgroundProperty, "Surface");
            box.SetResourceReference(ForegroundProperty, "Text");
            box.SetResourceReference(BorderBrushProperty, "Line");
        }
        foreach (var r in new Control[] { _newTicket, _existing, _note }) r.SetResourceReference(ForegroundProperty, "Text");
        // The ComboBox keeps Windows' light look in both themes, so its text stays dark.
        // The app-wide TextBlock style paints text in the theme colour (white in dark mode), so each item sets black itself.
        var item = new FrameworkElementFactory(typeof(TextBlock));
        item.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PlannrTicket.Label)));
        item.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Black);
        _tickets.ItemTemplate = new DataTemplate { VisualTree = item };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        BuildSetup();
        BuildSend();
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 28) };
        root.Children.Add(new TextBlock { Text = "Send to Plannr", Style = (Style)Application.Current.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6) });
        var hint = Dim("Put this scan straight into Plannr: the device, specs and every problem, with the full report attached so it can be opened any time, on any of your devices.");
        hint.Margin = new Thickness(0, 0, 0, 14);
        root.Children.Add(hint);
        root.Children.Add(_setup);
        root.Children.Add(_send);
        root.Children.Add(_status);
        Content = new ScrollViewer { Content = root };

        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Report)) Refresh(); };
        Refresh();
        if (_link is not null && vm.Live) _ = LoadTicketsAsync();
    }

    private void BuildSetup()
    {
        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = "Connect to your Plannr (once)", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        card.Children.Add(Dim("In Plannr on your PC: Settings → Sync & devices → Troubleshootr → Connect Troubleshootr, then Copy link and paste it here. The link is saved with Troubleshootr (on the USB stick when it runs from one); it can only send reports to your Plannr and see open ticket numbers.", 4));
        _linkBox.Margin = new Thickness(0, 10, 0, 0);
        card.Children.Add(_linkBox);
        var save = new Button { Content = "Connect", Tag = "primary", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        save.Click += (_, _) =>
        {
            var link = PlannrLink.TryParse(_linkBox.Text);
            if (link is null) { _status.Text = "That isn't a Plannr link. Copy it again from Plannr (Settings → Sync & devices → Troubleshootr)."; return; }
            _settings.PlannrLink = _linkBox.Text.Trim();
            _status.Text = _settings.Save() ? "Connected." : "Connected for now, but the link couldn't be saved (read-only drive).";
            _link = link;
            _linkBox.Clear();
            Refresh();
            _ = LoadTicketsAsync();
        };
        card.Children.Add(save);
        _setup.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Card"), Child = card });
    }

    private void BuildSend()
    {
        var card = new StackPanel();
        card.Children.Add(_newTicket);
        var customerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 6, 0, 0) };
        customerRow.Children.Add(new TextBlock { Text = "Customer (optional)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        customerRow.Children.Add(_customer);
        card.Children.Add(customerRow);
        card.Children.Add(_existing);
        var ticketRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 6, 0, 0) };
        ticketRow.Children.Add(_tickets);
        var reload = new Button { Content = "Refresh", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
        reload.Click += async (_, _) => await LoadTicketsAsync();
        ticketRow.Children.Add(reload);
        card.Children.Add(ticketRow);
        card.Children.Add(_note);
        _customer.GotFocus += (_, _) => _newTicket.IsChecked = true;
        _tickets.DropDownOpened += (_, _) => _existing.IsChecked = true;
        _sendButton.Click += async (_, _) => await SendAsync();
        card.Children.Add(_sendButton);

        var forget = new Button { Content = "Disconnect from Plannr", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 3, 10, 3), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
        forget.Click += (_, _) => { _settings.PlannrLink = null; _settings.Save(); _link = null; _status.Text = ""; Refresh(); };
        _send.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Card"), Child = card });
        _send.Children.Add(forget);
    }

    private void Refresh()
    {
        _setup.Visibility = _link is null ? Visibility.Visible : Visibility.Collapsed;
        _send.Visibility = _link is null ? Visibility.Collapsed : Visibility.Visible;
        _sendButton.IsEnabled = _vm.Report is not null && !_busy;
        if (_link is not null && _vm.Report is null && !_busy) _status.Text = "Run a scan first (Diagnose tab), then send it.";
    }

    private async Task LoadTicketsAsync()
    {
        if (_link is not { } link) return;
        try
        {
            using var drop = new PlannrDrop(link);
            var list = await drop.OpenTicketsAsync(CancellationToken.None);
            _tickets.ItemsSource = list;
            if (list.Count > 0 && _tickets.SelectedIndex < 0) _tickets.SelectedIndex = 0;
            _existing.IsEnabled = list.Count > 0;
            _existing.Content = list.Count > 0 ? "Add to an open ticket" : "Add to an open ticket (none open in Plannr)";
        }
        catch (Exception e) when (e is PlannrException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or System.IO.InvalidDataException)
        {
            _status.Text = $"Couldn't load the open tickets: {e.Message}";
        }
    }

    /// <summary>Screenshot mode: example tickets instead of asking a Plannr.</summary>
    public void ShowExample()
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // The P-256 generator point: a valid public key, so the link parses (nothing is sent in screenshot mode).
        var publicKey = new byte[] { 4 }.Concat(Hex("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296")).Concat(Hex("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5")).ToArray();
        _link ??= PlannrLink.TryParse($"https://plannr-sync.example.workers.dev/#troubleshootr=ExampleSpace0001.{B64(new byte[32])}.{B64(publicKey)}");
        _tickets.ItemsSource = new[] { new PlannrTicket(42, "Jane Doe", "Dell XPS 13", "intake", "T-0042"), new PlannrTicket(41, "Sam Lee", "HP Pavilion", "ready", "T-0041") };
        _tickets.SelectedIndex = 0;
        Refresh();
    }

    private static byte[] Hex(string s) => Enumerable.Range(0, s.Length / 2).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray();

    private async Task SendAsync()
    {
        if (_link is not { } link || _vm.Report is not { } report || _busy) return;
        var target = _existing.IsChecked == true ? PlannrTarget.Ticket : _note.IsChecked == true ? PlannrTarget.Note : PlannrTarget.NewTicket;
        var ticket = _tickets.SelectedItem as PlannrTicket;
        if (target == PlannrTarget.Ticket && ticket is null) { _status.Text = "Pick a ticket first."; return; }
        _busy = true;
        Refresh();
        _status.Text = "Sending…";
        try
        {
            using var drop = new PlannrDrop(link);
            var customer = _customer.Text;
            // The full report can be megabytes of JSON: build it off the UI thread so the window stays responsive.
            var payload = await Task.Run(() => PlannrPayload.Build(report, target, ticket?.Number, customer));
            await drop.SendReportAsync(payload, CancellationToken.None);
            _status.Text = target switch
            {
                PlannrTarget.Ticket => $"Sent. Plannr adds it to ticket {ticket!.Name} within about 20 seconds while Plannr is open (a few minutes if it is closed to the tray).",
                PlannrTarget.Note => "Sent. Plannr creates the note within about 20 seconds while Plannr is open (a few minutes if it is closed to the tray).",
                _ => "Sent. Plannr creates the ticket within about 20 seconds while Plannr is open (a few minutes if it is closed to the tray).",
            };
            ActionLog.Write($"Sent the scan to Plannr ({target}{(ticket is null || target != PlannrTarget.Ticket ? "" : $" {ticket.Name}")})");
        }
        catch (PlannrException e) { _status.Text = e.Message; }
        catch (Exception e)
        {
            // An async click handler must never throw (it would close the app); say what went wrong instead.
            _status.Text = $"Couldn't send: {e.Message}";
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private static TextBlock Dim(string text, double top = 0)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        return t;
    }
}
