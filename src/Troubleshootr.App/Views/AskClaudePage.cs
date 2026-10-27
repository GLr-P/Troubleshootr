using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Troubleshootr.Collectors.Assistant;
using Troubleshootr.Core;

namespace Troubleshootr.App.Views;

/// <summary>
/// "Ask Claude": questions about the scanned PC, answered by Claude with the anonymized report and read-only tools into
/// the scan. Bring-your-own API key (Settings, DPAPI). A new scan starts a new conversation.
/// </summary>
public sealed class AskClaudePage : UserControl
{
    private static readonly string[] Suggestions =
    [
        "What's the most likely cause of this PC's problems, and what should I check first?",
        "Why could this PC be slow?",
        "Is any drive failing? How sure are you?",
        "Explain the blue screens and what to do about them.",
    ];

    private readonly MainViewModel _vm;
    private readonly Settings _settings = Settings.Load();
    private string? _key;
    private ClaudeChat? _chat;
    private Report? _chatReport;
    private CancellationTokenSource? _cts;

    private readonly StackPanel _keyPanel = new();
    private readonly DockPanel _chatPanel = new() { LastChildFill = true };
    private readonly PasswordBox _keyBox = new() { Width = 420, Padding = new Thickness(6, 4, 6, 4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _remember = new() { Content = "Remember the key on this PC (encrypted for this Windows user)", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _transcript = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Padding = new Thickness(12),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBox _question = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 140, Padding = new Thickness(8, 6, 8, 6),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13.5,
    };
    private readonly Button _send = new() { Content = "Ask", MinWidth = 90, Tag = "primary", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _suggestions = new() { Margin = new Thickness(0, 0, 0, 10) };

    public AskClaudePage(MainViewModel vm)
    {
        _vm = vm;
        _key = _settings.ClaudeKey;
        foreach (var box in new Control[] { _transcript, _question, _keyBox })
        {
            box.SetResourceReference(BackgroundProperty, "Surface");
            box.SetResourceReference(ForegroundProperty, "Text");
            box.SetResourceReference(BorderBrushProperty, "Line");
        }
        _remember.SetResourceReference(ForegroundProperty, "Text");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        BuildKeyPanel();
        BuildChatPanel();

        var root = new DockPanel { Margin = new Thickness(28, 24, 28, 24), LastChildFill = true };
        var title = new TextBlock { Text = "Ask Claude", Style = (Style)Application.Current.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6) };
        var hint = Dim("Ask about this PC in plain words. Claude reads the scan (names, serial numbers and network names removed) and looks up the details it needs.");
        hint.Margin = new Thickness(0, 0, 0, 14);
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(_keyPanel, Dock.Top);
        root.Children.Add(title);
        root.Children.Add(hint);
        root.Children.Add(_keyPanel);
        root.Children.Add(_chatPanel);
        Content = root;

        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Report)) Refresh(); };
        Refresh();
        if (!vm.Live) ShowExample();
    }

    /// <summary>Screenshot mode: the chat layout with an example exchange (no key, nothing sent).</summary>
    private void ShowExample()
    {
        _key = "example";
        Refresh();
        _send.IsEnabled = true;
        foreach (UIElement b in _suggestions.Children) b.IsEnabled = true;
        _transcript.Text = "You: Is the hard drive dying?\n\nClaude: Yes, Disk 1 (WDC WD10EZEX) is failing. SMART shows 312 reallocated sectors, 24 pending and 8 uncorrectable, " +
                           "and the System log has bad-block events for disk 1 in the last 30 days. Back up anything on it first (copy the user folders before running anything heavy), " +
                           "then replace it. The NVMe system drive is healthy (86% worn but no errors), so Windows itself is fine.";
        _status.Text = $"{ClaudeChat.Model} · 18,240 tokens in (12,900 from cache), 210 out this conversation";
    }

    private void BuildKeyPanel()
    {
        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = "Add your Claude API key", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        card.Children.Add(Dim("Create one at console.anthropic.com → API keys. Questions are billed to that Anthropic account (usually a few cents each). The key never goes anywhere except Anthropic.", 4));
        _keyBox.Margin = new Thickness(0, 10, 0, 0);
        card.Children.Add(_keyBox);
        card.Children.Add(_remember);
        var save = new Button { Content = "Use this key", Tag = "primary", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        save.Click += (_, _) => SaveKey();
        _keyBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveKey(); };
        card.Children.Add(save);
        _keyPanel.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Card"), Child = card, Margin = new Thickness(0, 0, 0, 12) });
    }

    private void BuildChatPanel()
    {
        foreach (var s in Suggestions)
        {
            var b = new Button { Content = s, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 4, 10, 4), FontSize = 12.5 };
            b.Click += async (_, _) => await Ask(s);
            _suggestions.Children.Add(b);
        }
        var input = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_send, Dock.Right);
        input.Children.Add(_send);
        input.Children.Add(_question);
        _send.Click += async (_, _) => await Ask(_question.Text);
        _question.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; await Ask(_question.Text); }
        };

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var fresh = new Button { Content = "New conversation", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
        fresh.Click += (_, _) => { ResetChat(); Refresh(); };
        var forget = new Button { Content = "Forget the key", Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
        forget.Click += (_, _) => { _settings.SetClaudeKey(null); _key = null; ResetChat(); Refresh(); };
        tools.Children.Add(fresh);
        tools.Children.Add(forget);

        var bottom = new StackPanel();
        bottom.Children.Add(input);
        bottom.Children.Add(_status);
        bottom.Children.Add(tools);
        DockPanel.SetDock(_suggestions, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        _chatPanel.Children.Add(_suggestions);
        _chatPanel.Children.Add(bottom);
        _chatPanel.Children.Add(_transcript);
    }

    private void SaveKey()
    {
        var key = _keyBox.Password.Trim();
        if (!key.StartsWith("sk-ant-", StringComparison.Ordinal)) { _status.Text = "That doesn't look like a Claude API key (they start with sk-ant-)."; return; }
        _key = key;
        _keyBox.Clear();
        var saved = _remember.IsChecked != true || _settings.SetClaudeKey(key);
        _status.Text = _remember.IsChecked == true ? saved ? "Key saved for this PC." : "Couldn't save the key; it's used until Troubleshootr closes." : "Key used until Troubleshootr closes.";
        Refresh();
    }

    private void Refresh()
    {
        _keyPanel.Visibility = _key is null ? Visibility.Visible : Visibility.Collapsed;
        _chatPanel.Visibility = _key is null ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(_chatReport, _vm.Report)) ResetChat();
        var ready = _vm.Report is not null && _key is not null && _cts is null;
        _send.IsEnabled = ready;
        foreach (UIElement b in _suggestions.Children) b.IsEnabled = ready;
        if (_vm.Report is null) _transcript.Text = "Run a scan first (Diagnose tab); Claude answers from its results.";
        else if (_transcript.Text.Length == 0 || _chat is null && _transcript.Text.StartsWith("Run a scan", StringComparison.Ordinal))
            _transcript.Text = "Ask a question below, or pick one of the suggestions.";
    }

    private void ResetChat()
    {
        _cts?.Cancel();
        _chat?.Dispose();
        _chat = null;
        _chatReport = _vm.Report;
        _transcript.Text = "";
    }

    private async Task Ask(string question)
    {
        question = question.Trim();
        if (question.Length == 0 || _vm.Report is not { } report || _key is null || _cts is not null) return;
        if (_chat is null)
        {
            _chat = new ClaudeChat(_key, new AssistantContext(report));
            _chatReport = report;
            _transcript.Text = "";
        }
        var chat = _chat;
        _question.Clear();
        Append($"You: {question}");
        _cts = new CancellationTokenSource();
        Refresh();
        try
        {
            var answer = await chat.AskAsync(question, s => Dispatcher.BeginInvoke(new Action(() => _status.Text = s)), _cts.Token);
            if (!ReferenceEquals(chat, _chat)) return;  // a new scan or "New conversation" replaced it meanwhile
            Append($"Claude: {answer}");
            _status.Text = $"{ClaudeChat.Model} · {chat.InputTokens:N0} tokens in ({chat.CachedTokens:N0} from cache), {chat.OutputTokens:N0} out this conversation";
        }
        catch (ClaudeException e) { if (ReferenceEquals(chat, _chat)) { _status.Text = e.Message; _question.Text = question; } }
        catch (OperationCanceledException) { }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Refresh();
        }
    }

    private void Append(string text)
    {
        _transcript.AppendText((_transcript.Text.Length > 0 ? "\n\n" : "") + text);
        _transcript.ScrollToEnd();
    }

    private static TextBlock Dim(string text, double top = 0)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        return t;
    }
}
