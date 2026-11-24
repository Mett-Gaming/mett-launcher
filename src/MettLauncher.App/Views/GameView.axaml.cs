using Avalonia.Controls;

namespace MettLauncher.App.Views;

public partial class GameView : UserControl
{
    public GameView()
    {
        InitializeComponent();
        // In schmalen Fenstern reicht die Karte rechts.
        SizeChanged += (_, e) => AchievementsStat.IsVisible = e.NewSize.Width >= 900;
    }
}
