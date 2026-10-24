using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Troubleshootr.Core;

namespace Troubleshootr.App;

/// <summary>Display names, icons and theme switching.</summary>
public static class Ui
{
    /// <summary>Category label, Segoe MDL2/Fluent glyph, and the one-line hint shown on its tile.</summary>
    public static readonly IReadOnlyDictionary<Category, (string Label, string Glyph, string Hint)> Categories =
        new Dictionary<Category, (string, string, string)>
        {
            [Category.Storage] = ("Storage", "", "Drive health, space, errors"),
            [Category.Memory] = ("Memory", "", "RAM speed, layout, errors"),
            [Category.CpuThermal] = ("CPU & temps", "", "Load, heat, throttling"),
            [Category.Graphics] = ("Graphics", "", "GPU, driver, display"),
            [Category.WindowsHealth] = ("Windows health", "", "Version, restarts, services"),
            [Category.Updates] = ("Updates", "", "Windows Update status"),
            [Category.Drivers] = ("Drivers & devices", "", "Problem and missing drivers"),
            [Category.Crashes] = ("Crashes & errors", "", "Blue screens, app crashes"),
            [Category.Performance] = ("Performance", "", "Startup, hogs, slowness"),
            [Category.Network] = ("Network", "", "Connection, Wi-Fi, DNS"),
            [Category.Security] = ("Security", "", "Antivirus, malware signs"),
            [Category.Battery] = ("Battery & power", "", "Wear, charging, sleep"),
            [Category.Peripherals] = ("Peripherals", "", "BIOS, audio, printers, USB"),
        };

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static string SeverityKey(Severity s) => s switch
    {
        Severity.Critical => "Critical", Severity.Warning => "Warning", Severity.Info => "Info", _ => "Ok",
    };

    public static string SeverityGlyph(Severity s) => s switch
    {
        Severity.Critical => "", Severity.Warning => "", Severity.Info => "", _ => "",
    };

    public static bool SystemUsesDarkTheme()
    {
        var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        return v is int i && i == 0;
    }

    public static void ApplyTheme(bool dark)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Troubleshootr;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        if (dicts.Count > 0) dicts[0] = palette; else dicts.Add(palette);
    }

    public static string Duration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{t.Hours} h {t.Minutes} min" : $"{t.Minutes} min";
    }
}
