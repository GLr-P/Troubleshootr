using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Troubleshootr.Checks;
using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>
/// Off-screen screenshots for testing without opening a window:
/// Troubleshootr.exe --screenshot all|&lt;page&gt; --fixture tests/fixtures/x.snap.json --out test-results/screens [--theme light|dark|both]
/// </summary>
public static class Screenshots
{
    private const int Width = 1280, Height = 800;

    public static int Run(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var which = Arg("--screenshot") ?? "all";
        var fixture = Arg("--fixture") ?? throw new ArgumentException("--fixture <snap.json> is required");
        var outDir = Arg("--out") ?? "test-results/screens";
        var theme = Arg("--theme") ?? "both";
        Directory.CreateDirectory(outDir);

        var snapshot = Json.Deserialize<Snapshot>(File.ReadAllText(fixture));
        var report = ScanEngine.Evaluate(snapshot, CheckRegistry.All, new ScanOptions { ToolVersion = MainViewModel.Version });
        var pages = which == "all" ? Shell.Pages : [which];
        var themes = theme == "both" ? new[] { "light", "dark" } : [theme];
        var name = Path.GetFileName(fixture).Replace(".snap.json", "");

        foreach (var t in themes)
        {
            Ui.ApplyTheme(t == "dark");
            var vm = new MainViewModel();
            vm.Show(report);
            var shell = new Shell(vm);
            TextOptions.SetTextFormattingMode(shell, TextFormattingMode.Display);
            foreach (var page in pages)
            {
                shell.Show(page);
                var file = Path.Combine(outDir, $"{name}_{page.ToLowerInvariant()}_{t}.png");
                Render(shell, file);
                Console.WriteLine(file);
            }
        }
        return 0;
    }

    private static void Render(FrameworkElement element, string file)
    {
        element.Measure(new Size(Width, Height));
        element.Arrange(new Rect(0, 0, Width, Height));
        element.UpdateLayout();
        // Let bindings and deferred layout run before rendering.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
        var bmp = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        png.Save(fs);
    }
}
