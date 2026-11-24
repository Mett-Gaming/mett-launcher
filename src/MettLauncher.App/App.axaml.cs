using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using MettLauncher.App.Services;
using MettLauncher.App.ViewModels;
using MettLauncher.App.Views;

namespace MettLauncher.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            var vm = new MainViewModel(services);
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            if (OperatingSystem.IsWindows()) UseTray(desktop, window, services);
            desktop.ShutdownRequested += (_, _) => services.Dispose();
            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Symbol im Infobereich, solange das Fenster versteckt ist.</summary>
    private void UseTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, AppServices services)
    {
        void Open()
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
        var open = new NativeMenuItem("Mett Launcher öffnen");
        open.Click += (_, _) => Open();
        var quit = new NativeMenuItem("Beenden");
        quit.Click += (_, _) => desktop.Shutdown();
        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MettLauncher/Assets/icon.ico"))),
            ToolTipText = "Mett Launcher", Menu = [open, quit], IsVisible = false,
        };
        tray.Clicked += (_, _) => Open();
        TrayIcon.SetIcons(this, [tray]);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty) tray.IsVisible = !window.IsVisible && window.IsLoaded;
        };
        Program.ShowRequested += () => Dispatcher.UIThread.Post(Open);
        // Wer das Fenster schon geschlossen hat, will nach dem Spiel keinen Launcher mehr.
        services.Launcher.GameExited += (_, _) => DispatcherTimer.RunOnce(() =>
        {
            if (!window.IsVisible && !services.Launcher.AnyRunning) desktop.Shutdown();
        }, TimeSpan.FromSeconds(3));
    }
}
