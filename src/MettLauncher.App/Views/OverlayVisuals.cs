using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MettLauncher.App.Controls;
using MettLauncher.App.ViewModels;

namespace MettLauncher.App.Views;

/// <summary>Fertiges Bild fürs Overlay, RGBA vormultipliziert.</summary>
public sealed record OverlayImage(int Width, int Height, byte[] Rgba);

/// <summary>Baut die Bilder, die die Schicht im Spiel zeigt. Längen gelten bei 1080 Zeilen.</summary>
public static class OverlayVisuals
{
    public const double ToastWidth = 400, ToastHeight = 92, BarHeight = 34;

    private static T Res<T>(string key) => Application.Current!.TryGetResource(key, null, out var v) && v is T t
        ? t : throw new KeyNotFoundException(key);

    private static IBrush Brush(string key) => Res<IBrush>(key);

    private static TextBlock Text(string text, double size, string brush, FontWeight weight = FontWeight.Normal, string font = "UiFont") => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        FontFamily = Res<FontFamily>(font),
        Foreground = Brush(brush),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static Control Icon(Bitmap? image, double size, bool unlocked)
    {
        Control inner = image is not null
            ? new Image { Source = image, Stretch = Stretch.UniformToFill, Opacity = unlocked ? 1 : 0.3 }
            : new LucideIcon
            {
                Width = size * 0.5, Height = size * 0.5, Data = Res<Geometry>(unlocked ? "IconTrophy" : "IconLock"),
                Foreground = Brush(unlocked ? "Brand" : "TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(3), ClipToBounds = true, Background = Brush("BgSelected"), Child = inner,
        };
    }

    private static Control Meter(double fraction, double width)
    {
        var fill = new Border { Width = Math.Round(width * Math.Clamp(fraction, 0, 1)), Background = Brush("AccentHover"), HorizontalAlignment = HorizontalAlignment.Left };
        return new Border { Width = width, Height = 5, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), ClipToBounds = true, Background = Brush("BgSelected"), Child = fill };
    }

    /// <summary>Einblendung: neuer Erfolg oder Zwischenstand.</summary>
    public static Control Toast(string head, string title, string text, Bitmap? icon, double? fraction)
    {
        var lines = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(Text(head, 11, "AccentHover", FontWeight.Bold));
        lines.Children.Add(Text(title, 17, "TextPrimary", FontWeight.SemiBold));
        if (fraction is { } f) lines.Children.Add(Meter(f, 290));
        else if (text.Length > 0) lines.Children.Add(Text(text, 13, "TextSecondary"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*"), ColumnSpacing = 14, Margin = new Thickness(15, 0) };
        grid.Children.Add(Icon(icon, 60, true));
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);
        return new Border
        {
            Width = ToastWidth, Height = ToastHeight, Background = Brush("BgSurface"), BorderBrush = Brush("BorderStrong"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = grid,
        };
    }

    /// <summary>Leiste mit Bildrate, Uhr und Sitzungsdauer.</summary>
    public static Control Bar(string text) => new Border
    {
        Height = BarHeight, Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x12, 0x0E, 0x0B)), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(12, 0),
        Child = new TextBlock
        {
            Text = text, FontSize = 14, FontFamily = Res<FontFamily>("MonoFont"), Foreground = Brush("TextPrimary"), VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>Zeichnet ein Bauteil ohne Fenster. Auf dem UI-Thread; <paramref name="scale"/> = Bildpunkte je Einheit.</summary>
    /// <returns>Bild und Größe in Einheiten.</returns>
    public static (OverlayImage Image, Size Size) Render(Control control, double scale)
    {
        control.Measure(Size.Infinity);
        var size = control.DesiredSize;
        control.Arrange(new Rect(size));
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
        using var target = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        target.Render(control);
        return (Pixels(target, pixels), size);
    }

    private static unsafe OverlayImage Pixels(RenderTargetBitmap source, PixelSize size)
    {
        var bytes = new byte[size.Width * size.Height * 4];
        var bgra = source.Format == PixelFormats.Bgra8888;
        fixed (byte* p = bytes)
            source.CopyPixels(new PixelRect(size), (nint)p, bytes.Length, size.Width * 4);
        if (bgra)
            for (var i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
        return new OverlayImage(size.Width, size.Height, bytes);
    }
}
