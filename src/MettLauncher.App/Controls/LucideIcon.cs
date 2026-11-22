using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MettLauncher.App.Controls;

/// <summary>
/// Zeichnet ein Lucide-Icon (24er Raster, Strich 2) in beliebiger Größe. Farbe folgt <see cref="Foreground"/>,
/// damit Icons in Buttons automatisch die Textfarbe des Zustands übernehmen.
/// </summary>
public sealed class LucideIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LucideIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LucideIcon>();

    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<LucideIcon, bool>(nameof(Filled));

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<LucideIcon, double>(nameof(StrokeWidth), 2);

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(DataProperty, ForegroundProperty, FilledProperty, StrokeWidthProperty);
        WidthProperty.OverrideDefaultValue<LucideIcon>(16);
        HeightProperty.OverrideDefaultValue<LucideIcon>(16);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        if (scale <= 0) return;
        var pen = new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(Filled ? Foreground : null, pen, Data);
    }
}
