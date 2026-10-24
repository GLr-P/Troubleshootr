using System.Windows;
using System.Windows.Media;

namespace Troubleshootr.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "Troubleshootr error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        if (Array.IndexOf(e.Args, "--screenshot") >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = Screenshots.Run(e.Args);
            Shutdown(code);
            return;
        }

        Ui.ApplyTheme(Ui.SystemUsesDarkTheme());
        var vm = new MainViewModel();
        var window = new Window
        {
            Title = $"Troubleshootr {MainViewModel.Version}{(vm.IsAdmin ? " (admin)" : "")}",
            Width = 1180, Height = 780, MinWidth = 820, MinHeight = 540,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Shell(vm),
        };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        MainWindow = window;
        window.Show();
        await vm.LoadDeviceAsync();
    }
}
