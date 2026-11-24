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

/// <summary>Was das Overlay-Feld zeigt.</summary>
public sealed record OverlayPanelContent
{
    public required string Game { get; init; }
    public required string Clock { get; init; }
    public required string Session { get; init; }
    public required string Total { get; init; }
    public required string Count { get; init; }
    public required double Fraction { get; init; }
    public required IReadOnlyList<AchievementViewModel> Achievements { get; init; }
    public string? Multiplayer { get; init; }
    public IReadOnlyList<string> Players { get; init; } = [];
    public IReadOnlyList<string> Buttons { get; init; } = [];
    public int Selected { get; init; }
    public int Scroll { get; init; }
}

/// <summary>Baut die Bilder, die die Schicht im Spiel zeigt. Längen gelten bei 1080 Zeilen.</summary>
public static class OverlayVisuals
{
    public const double ToastWidth = 400, ToastHeight = 92, BarHeight = 34, PanelWidth = 1040, PanelHeight = 720;
    public const int PanelRows = 7;
    private const double SideLeft = 704, ButtonLeft = SideLeft, ButtonWidth = 304, ButtonHeight = 44, ButtonGap = 8, ButtonBottom = 32;

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

    /// <summary>Fläche eines Knopfs im Feld.</summary>
    public static Rect ButtonRect(int index, int count) => new(ButtonLeft,
        PanelHeight - ButtonBottom - (count - index) * (ButtonHeight + ButtonGap) + ButtonGap, ButtonWidth, ButtonHeight);

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

    /// <summary>Das Overlay-Feld.</summary>
    public static Control Panel(OverlayPanelContent c)
    {
        var canvas = new Canvas { Width = PanelWidth, Height = PanelHeight };
        void Put(Control control, double x, double y)
        {
            Canvas.SetLeft(control, x);
            Canvas.SetTop(control, y);
            canvas.Children.Add(control);
        }

        var name = Text(c.Game, 26, "TextPrimary", font: "DisplayFont");
        name.MaxWidth = 640;
        Put(name, 32, 26);
        Put(Text(c.Clock, 26, "TextSecondary", FontWeight.SemiBold), SideLeft, 26);

        Put(Text("ERFOLGE", 12, "TextMuted", FontWeight.Bold), 32, 88);
        Put(Text(c.Count, 12, "TextSecondary", FontWeight.SemiBold), 110, 88);
        Put(Meter(c.Fraction, 632), 32, 112);
        if (c.Achievements.Count == 0) Put(Text("Dieses Spiel hat noch keine Erfolge.", 15, "TextSecondary"), 32, 140);
        var row = 0;
        foreach (var a in c.Achievements.Skip(c.Scroll).Take(PanelRows)) Put(Row(a), 32, 132 + row++ * 80);

        Put(Text("SITZUNG", 12, "TextMuted", FontWeight.Bold), SideLeft, 88);
        Put(Text(c.Session, 20, "TextPrimary", FontWeight.SemiBold), SideLeft, 108);
        Put(Text($"Insgesamt {c.Total}", 13, "TextSecondary"), SideLeft, 138);
        if (c.Multiplayer is not null)
        {
            Put(Text("MEHRSPIELER", 12, "TextMuted", FontWeight.Bold), SideLeft, 184);
            var state = Text(c.Multiplayer, 15, "TextPrimary", FontWeight.SemiBold);
            state.MaxWidth = ButtonWidth;
            Put(state, SideLeft, 206);
            var y = 236;
            foreach (var p in c.Players.Take(8))
            {
                var player = Text(p, 14, "TextSecondary");
                player.MaxWidth = ButtonWidth;
                Put(player, SideLeft, y);
                y += 24;
            }
        }
        for (var i = 0; i < c.Buttons.Count; i++)
        {
            var r = ButtonRect(i, c.Buttons.Count);
            var label = Text(c.Buttons[i], 15, "TextPrimary", FontWeight.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            Put(new Border
            {
                Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(3),
                Background = Brush(i == c.Selected ? "Accent" : "BgRaised"), BorderThickness = new Thickness(1),
                BorderBrush = Brush(i == c.Selected ? "AccentHover" : "BorderDefault"), Child = label,
            }, r.X, r.Y);
        }
        return new Border
        {
            Width = PanelWidth, Height = PanelHeight, Background = Brush("BgApp"), BorderBrush = Brush("BorderStrong"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = canvas,
        };
    }

    private static Control Row(AchievementViewModel a)
    {
        var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(Text(a.Name, 16, a.Unlocked ? "TextPrimary" : "TextSecondary", FontWeight.SemiBold));
        if (a.Text.Length > 0) lines.Children.Add(Text(a.Text, 13, a.Unlocked ? "TextSecondary" : "TextMuted"));
        if (a.ShowProgress) lines.Children.Add(Meter(a.Fraction, 360));
        var grid = new Grid { Width = 632, Height = 72, ColumnDefinitions = new ColumnDefinitions("52,*,Auto"), ColumnSpacing = 14, Background = Brush("BgSurface") };
        var icon = Icon(a.Icon, 52, a.Unlocked);
        icon.Margin = new Thickness(10, 0, 0, 0);
        grid.ColumnDefinitions[0].Width = new GridLength(62);
        grid.Children.Add(icon);
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);
        var info = Text(a.Info, 13, "TextMuted");
        info.VerticalAlignment = VerticalAlignment.Center;
        info.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(info, 2);
        grid.Children.Add(info);
        return new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = grid };
    }

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
