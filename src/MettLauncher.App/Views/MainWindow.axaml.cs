using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using MettLauncher.App.ViewModels;

namespace MettLauncher.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsLinux())
        {
            // X11/Wayland verhalten sich bei eigener Titelleiste uneinheitlich -> Systemleiste nutzen.
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaToDecorationsHint = false;
            ChromeButtons.IsVisible = false;
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MettLauncher/Assets/icon.png")));
        }
        DragArea.PointerPressed += OnDragAreaPressed;
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Close();
        Scrim.PointerPressed += (_, _) => (DataContext as MainViewModel)?.CloseDialogs();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) (DataContext as MainViewModel)?.CloseDialogs(); };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            vm.PickFolder = PickFolderAsync;
            vm.CopyText = async text => { if (Clipboard is { } cb) await cb.SetTextAsync(text); };
        }
    }

    private void OnDragAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (OperatingSystem.IsLinux()) return; // Systemleiste übernimmt das Ziehen
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else BeginMoveDrag(e);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async Task<string?> PickFolderAsync()
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Speicherort für Spiele wählen",
            AllowMultiple = false,
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }
}
