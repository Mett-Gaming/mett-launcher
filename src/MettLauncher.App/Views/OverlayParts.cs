using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MettLauncher.App.Controls;
using MettLauncher.App.ViewModels;

namespace MettLauncher.App.Views;

/// <summary>Anklickbare Fläche in einem Teil des Overlays.</summary>
/// <param name="Focus">Mit Pfeiltasten erreichbar.</param>
public sealed record OverlayHit(string Id, Rect Area, bool Focus = true);

/// <summary>Neuigkeit mit Datum.</summary>
public sealed record OverlayNews(string Title, string Date);

/// <summary>Ein gezeichneter Teil des Overlays: Fläche mit Knöpfen. Längen gelten bei 1080 Zeilen.</summary>
public sealed class OverlayPart(double width, double height, string? focus)
{
    private readonly List<OverlayHit> _hits = [];

    public Canvas Canvas { get; } = new() { Width = width, Height = height };
    public IReadOnlyList<OverlayHit> Hits => _hits;

    private static T Res<T>(string key) => Application.Current!.TryGetResource(key, null, out var v) && v is T t ? t : throw new KeyNotFoundException(key);
    private static IBrush Brush(string key) => Res<IBrush>(key);

    public static TextBlock Text(string text, double size, string brush, FontWeight weight = FontWeight.Normal, string font = "UiFont",
        double maxWidth = double.PositiveInfinity) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, FontFamily = Res<FontFamily>(font), Foreground = Brush(brush),
        TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = maxWidth,
    };

    public void Put(Control control, double x, double y)
    {
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        Canvas.Children.Add(control);
    }

    public void PutRight(Control control, double right, double y)
    {
        control.Measure(Size.Infinity);
        Put(control, right - control.DesiredSize.Width, y);
    }

    public void PutCentered(Control control, double center, double y)
    {
        control.Measure(Size.Infinity);
        var w = control.DesiredSize.Width;
        Put(control, Math.Clamp(center - w / 2, 0, Math.Max(0, width - w)), y);
    }

    public void Hit(string id, Rect area, bool canFocus = true) => _hits.Add(new OverlayHit(id, area, canFocus));

    public void Box(Rect r, string background, string? border = null, double radius = 3) => Put(new Border
    {
        Width = r.Width, Height = r.Height, Background = Brush(background), CornerRadius = new CornerRadius(radius),
        BorderBrush = border is null ? null : Brush(border), BorderThickness = new Thickness(border is null ? 0 : 1),
    }, r.X, r.Y);

    public void Button(string id, string text, Rect r)
    {
        var on = focus == id;
        var label = Text(text, 14, "TextPrimary", FontWeight.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        Put(new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(3), Child = label, BorderThickness = new Thickness(1),
            Background = Brush(on ? "Accent" : "BgHover"), BorderBrush = Brush(on ? "AccentHover" : "BorderDefault"),
        }, r.X, r.Y);
        Hit(id, r);
    }

    public void IconButton(string id, string icon, Rect r, bool active = false, double size = 20)
    {
        var on = focus == id;
        Put(new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
            Background = Brush(active ? "Accent" : on ? "BgHover" : "BgRaised"), BorderBrush = Brush(on ? "AccentHover" : "BorderSubtle"),
            Child = Icon(icon, size, "TextPrimary"),
        }, r.X, r.Y);
        Hit(id, r);
    }

    public static LucideIcon Icon(string key, double size, string brush) => new()
    {
        Width = size, Height = size, Data = Res<Geometry>(key), Foreground = Brush(brush),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    public void Meter(double fraction, Rect r)
    {
        Box(r, "BgSelected", radius: 2);
        var w = Math.Round(r.Width * Math.Clamp(fraction, 0, 1));
        if (w > 0) Box(r.WithWidth(w), "AccentHover", radius: 2);
    }

    /// <summary>Überschrift eines Abschnitts in einem Fenster.</summary>
    public void Section(string title, string icon, double y)
    {
        Box(new Rect(16, y, width - 32, 30), "BgHover");
        Put(Text(title, 13, "TextPrimary", FontWeight.Bold), 28, y + 7);
        Put(Icon(icon, 16, "TextSecondary"), width - 46, y + 7);
    }

    public void Scrollbar(int count, int rows, int scroll, Rect r)
    {
        if (count <= rows) return;
        Box(r, "BgRaised", radius: 2);
        Box(new Rect(r.X, r.Y + r.Height * scroll / count, r.Width, r.Height * rows / count), "BorderStrong", radius: 2);
    }
}

/// <summary>Die Teile des Overlays: Ränder, Symbolleiste (schaltet die Fenster) und Fenster.</summary>
public static class OverlayParts
{
    public const int AchievementRows = 6, NewsRows = 9;
    private const double EdgeWidth = 420, TitleWidth = 700, DockButton = 46, DockGap = 10, WindowHead = 46;

    /// <summary>Fenster in der Reihenfolge der Symbolleiste.</summary>
    public static readonly (string Key, string Title, string Icon, Size Size)[] Windows =
    [
        ("uebersicht", "Spielübersicht", "IconInfo", new Size(420, 470)),
        ("erfolge", "Erfolge", "IconTrophy", new Size(560, 566)),
        ("mitspieler", "Mitspieler", "IconUsers", new Size(420, 420)),
        ("neues", "Neuigkeiten", "IconNews", new Size(520, 440)),
        ("einstellungen", "Overlay-Einstellungen", "IconSettings", new Size(420, 274)),
    ];

    public static OverlayPart Corner(string clock, string date, string session, string? focus)
    {
        var p = new OverlayPart(EdgeWidth, 140, focus);
        p.Put(OverlayPart.Text(clock, 30, "TextPrimary", FontWeight.Bold), 0, 0);
        p.Put(OverlayPart.Text(date, 15, "TextSecondary"), 0, 42);
        p.Put(OverlayPart.Text($"Diese Sitzung: {session}", 15, "TextSecondary"), 0, 64);
        p.Button("quit", "SPIEL BEENDEN", new Rect(0, 96, 150, 36));
        return p;
    }

    public static OverlayPart Title(string game)
    {
        var p = new OverlayPart(TitleWidth, 80, null);
        p.PutCentered(OverlayPart.Text(game, 44, "TextPrimary", font: "DisplayFont", maxWidth: TitleWidth), TitleWidth / 2, 0);
        return p;
    }

    public static OverlayPart Back(string key, string? focus)
    {
        var p = new OverlayPart(EdgeWidth, 56, focus);
        p.PutRight(OverlayPart.Text("Zurück zum Spiel", 20, focus == "close" ? "AccentHover" : "TextPrimary", FontWeight.SemiBold), EdgeWidth - 66, 2);
        if (key.Length > 0) p.PutRight(OverlayPart.Text($"({key})", 14, "TextSecondary"), EdgeWidth - 66, 30);
        p.IconButton("close", "IconX", new Rect(EdgeWidth - 52, 0, 52, 52));
        return p;
    }

    public static OverlayPart Dock(IReadOnlySet<string> open, string? focus)
    {
        const double side = 90; // Platz für lange Namen am Rand
        var p = new OverlayPart(2 * side + Windows.Length * (DockButton + DockGap) - DockGap, 80, focus);
        for (var i = 0; i < Windows.Length; i++)
        {
            var (key, title, icon, _) = Windows[i];
            var r = new Rect(side + i * (DockButton + DockGap), 32, DockButton, DockButton);
            p.IconButton("dock:" + key, icon, r, open.Contains(key), 22);
            if (focus == "dock:" + key) p.PutCentered(OverlayPart.Text(title, 15, "TextPrimary", FontWeight.SemiBold), r.Center.X, 4);
        }
        return p;
    }

    private static OverlayPart Window(string key, string? focus)
    {
        var (_, title, _, size) = Windows.First(w => w.Key == key);
        var p = new OverlayPart(size.Width, size.Height, focus);
        p.Box(new Rect(size), "BgSurface", "BorderStrong", 4);
        p.Hit($"win:{key}:body", new Rect(size), false);
        p.Hit($"win:{key}:title", new Rect(0, 0, size.Width, WindowHead), false);
        p.Put(OverlayPart.Text(title, 20, "TextPrimary", FontWeight.SemiBold), 16, 11);
        p.IconButton($"win:{key}:close", "IconX", new Rect(size.Width - 42, 10, 28, 28), size: 14);
        return p;
    }

    private static void NewsRow(OverlayPart p, OverlayNews n, double y, double width)
    {
        p.Box(new Rect(16, y, width, 34), "BgApp");
        p.Put(OverlayPart.Text(n.Title, 14, "TextPrimary", maxWidth: width - 120), 28, y + 8);
        p.PutRight(OverlayPart.Text(n.Date, 13, "TextMuted"), 4 + width, y + 9);
    }

    public static OverlayPart Overview(string total, string session, string count, double fraction, AchievementViewModel? latest,
        IReadOnlyList<OverlayNews> news, string? focus)
    {
        var p = Window("uebersicht", focus);
        p.Section("SPIELZEIT", "IconClock", 56);
        p.Put(OverlayPart.Text($"Insgesamt: {total}", 15, "TextPrimary"), 28, 94);
        p.Put(OverlayPart.Text($"Diese Sitzung: {session}", 15, "TextSecondary"), 28, 118);
        p.Section("ERFOLGE", "IconTrophy", 152);
        p.Put(OverlayPart.Text(count, 15, "TextPrimary"), 28, 190);
        p.Meter(fraction, new Rect(28, 216, 364, 5));
        if (latest is not null) p.Put(OverlayPart.Text($"Zuletzt: {latest.Name}", 14, "TextSecondary", maxWidth: 364), 28, 230);
        p.Section("AKTUELLE NEUIGKEITEN", "IconNews", 264);
        if (news.Count == 0) p.Put(OverlayPart.Text("Noch keine Meldungen.", 14, "TextSecondary"), 28, 304);
        for (var i = 0; i < Math.Min(4, news.Count); i++) NewsRow(p, news[i], 302 + i * 38, 388);
        return p;
    }

    public static OverlayPart Achievements(IReadOnlyList<AchievementViewModel> list, string count, double fraction, int scroll, string? focus)
    {
        var p = Window("erfolge", focus);
        p.Put(OverlayPart.Text(count, 14, "TextSecondary"), 16, 50);
        p.Meter(fraction, new Rect(16, 76, 528, 5));
        var y = 92.0;
        foreach (var a in list.Skip(scroll).Take(AchievementRows))
        {
            p.Box(new Rect(16, y, 516, 72), "BgApp");
            p.Put(OverlayVisuals.Icon(a.Icon, 52, a.Unlocked), 26, y + 10);
            var up = a.ShowProgress ? 8 : 15;
            p.Put(OverlayPart.Text(a.Name, 16, a.Unlocked ? "TextPrimary" : "TextSecondary", FontWeight.SemiBold, maxWidth: 320), 92, y + up);
            p.Put(OverlayPart.Text(a.Text, 13, a.Unlocked ? "TextSecondary" : "TextMuted", maxWidth: 320), 92, y + up + 23);
            if (a.ShowProgress) p.Meter(a.Fraction, new Rect(92, y + 55, 320, 5));
            p.PutRight(OverlayPart.Text(a.Info, 13, "TextMuted"), 520, y + 28);
            y += 78;
        }
        p.Scrollbar(list.Count, AchievementRows, scroll, new Rect(540, 92, 4, AchievementRows * 78 - 6));
        return p;
    }

    public static OverlayPart Players(string? state, IReadOnlyList<string> players, bool canInvite, bool copied, string? focus)
    {
        var p = Window("mitspieler", focus);
        p.Section("SITZUNG", "IconUsers", 56);
        p.Put(OverlayPart.Text(state ?? "Du spielst gerade allein.", 15, "TextPrimary", FontWeight.SemiBold, maxWidth: 364), 28, 96);
        var y = 126.0;
        foreach (var name in players.Take(7))
        {
            p.Box(new Rect(16, y, 388, 28), "BgApp");
            p.Put(OverlayPart.Text(name, 14, "TextSecondary", maxWidth: 364), 28, y + 5);
            y += 32;
        }
        if (canInvite) p.Button("copy", copied ? "Code kopiert" : "Einladungscode kopieren", new Rect(16, 364, 388, 40));
        return p;
    }

    public static OverlayPart News(IReadOnlyList<OverlayNews> news, int scroll, string? focus)
    {
        var p = Window("neues", focus);
        if (news.Count == 0) p.Put(OverlayPart.Text("Noch keine Meldungen.", 15, "TextSecondary"), 16, 60);
        var y = 56.0;
        foreach (var n in news.Skip(scroll).Take(NewsRows))
        {
            NewsRow(p, n, y, 480);
            y += 41;
        }
        p.Scrollbar(news.Count, NewsRows, scroll, new Rect(504, 56, 4, NewsRows * 41 - 7));
        return p;
    }

    public static OverlayPart Settings(bool bar, bool sound, string corner, string? focus)
    {
        var p = Window("einstellungen", focus);
        (string Label, string Value, string Id)[] rows =
        [
            ("Leiste mit Bildrate und Uhr", bar ? "An" : "Aus", "set:bar"),
            ("Ton bei neuen Erfolgen", sound ? "An" : "Aus", "set:sound"),
            ("Ecke für Einblendungen", corner, "set:corner"),
        ];
        var y = 60.0;
        foreach (var (label, value, id) in rows)
        {
            p.Box(new Rect(16, y, 388, 58), "BgApp");
            p.Put(OverlayPart.Text(label, 15, "TextPrimary"), 28, y + 19);
            p.Button(id, value, new Rect(264, y + 11, 128, 36));
            y += 66;
        }
        return p;
    }

    public static OverlayPart Confirm(string game, string? focus)
    {
        var p = new OverlayPart(460, 190, focus);
        p.Box(new Rect(0, 0, 460, 190), "BgSurface", "BorderStrong", 4);
        p.Put(OverlayPart.Text($"{game} beenden?", 22, "TextPrimary", FontWeight.SemiBold, maxWidth: 412), 24, 24);
        p.Put(OverlayPart.Text("Nicht gespeicherter Fortschritt geht verloren.", 15, "TextSecondary"), 24, 66);
        p.Button("quit-no", "Abbrechen", new Rect(24, 124, 196, 42));
        p.Button("quit-yes", "Spiel beenden", new Rect(240, 124, 196, 42));
        return p;
    }
}
