using System.Text.Json;
using Avalonia;
using MettLauncher.App.ViewModels;
using MettLauncher.App.Views;
using MettLauncher.Core.Achievements;
using MettLauncher.Core.Overlay;
using MettLauncher.Core.Settings;

namespace MettLauncher.App.Services;

/// <summary>Was das Overlay über ein Spiel wissen muss; von den ViewModels geliefert.</summary>
public interface IOverlayGame
{
    string Name { get; }
    TimeSpan TotalPlaytime { get; }
    string? SessionText { get; }
    string? SessionCode { get; }
    IReadOnlyList<string> Players { get; }
    IReadOnlyList<OverlayNews> OverlayNews { get; }
    Task CopyCodeAsync();
    Task StopAsync();
}

/// <summary>
/// Der Overlay-Bildschirm eines laufenden Spiels: Ränder, Symbolleiste und verschiebbare Fenster. Zeichnet nur neu, was
/// sich geändert hat, und deutet Maus, Tasten und Controller. Nur auf dem UI-Thread.
/// </summary>
public sealed class OverlayScreen(string gameId, DateTimeOffset startedAt, AchievementService achievements, SettingsStore settings,
    Func<IOverlayGame?> findGame)
{
    public const uint DimImage = 3;
    private const uint FirstImage = 70, ImageCount = 16;
    private const double EdgeX = 40, EdgeY = 28, Rows = 1080;

    private sealed class Part
    {
        public uint Image;
        public string Key = "";
        public Point At;
        public Size Size;
        public IReadOnlyList<OverlayHit> Hits = [];
        public bool Seen;
    }

    public enum Outcome { None, Changed, Close }

    private readonly Dictionary<string, Part> _parts = [];
    private readonly List<string> _order = [];
    /// <summary>Offene Fenster, vorderstes zuletzt.</summary>
    private readonly List<string> _windows = ["uebersicht"];
    /// <summary>Mitte der Fenster als Anteil am Bildschirm.</summary>
    private readonly Dictionary<string, Point> _places = [];
    private readonly Dictionary<string, int> _scroll = [];
    private string? _focus;
    private (string Window, Point Grab)? _drag;
    private bool _confirm, _copied;
    private uint _nextImage = FirstImage;
    private int _achievementVersion;
    private DateTimeOffset _openedAt;
    private Size _screen = new(1920, Rows);

    public bool IsOpen { get; private set; }

    /// <summary>Das Spiel meldet auch das Loslassen der Maus; sonst bleiben Fenster stehen.</summary>
    public bool CanDrag { get; set; }

    public void Open()
    {
        IsOpen = true;
        _confirm = _copied = false;
        _drag = null;
        _focus = null;
        _openedAt = DateTimeOffset.UtcNow;
    }

    public void Close() => IsOpen = false;

    /// <summary>Die Schicht kennt keine Bilder mehr (neue Verbindung) oder die Größe hat sich geändert.</summary>
    public void Forget()
    {
        foreach (var p in _parts.Values) p.Key = "";
    }

    public void AchievementsChanged() => _achievementVersion++;

    public static string Duration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} Std. {t.Minutes} Min." : $"{Math.Max(0, (int)t.TotalMinutes)} Min.";

    public static string KeyName(string key) => key == "shift+tab" ? "Umschalt+Tab" : key.ToUpperInvariant();

    private static string CornerName(OverlayAnchor a) => a switch
    {
        OverlayAnchor.TopLeft => "Oben links",
        OverlayAnchor.TopRight => "Oben rechts",
        OverlayAnchor.BottomLeft => "Unten links",
        _ => "Unten rechts",
    };

    // ---- Zeichnen ----

    /// <param name="send">Schickt ein Teil als Bild und liefert seine Größe.</param>
    /// <returns>Rechtecke von hinten nach vorn.</returns>
    public IReadOnlyList<OverlayQuad> Draw(OverlayStats stats, Func<uint, OverlayPart, Size> send, Action<uint> remove)
    {
        _screen = new Size(Rows * stats.Width / Math.Max(1, stats.Height), Rows);
        foreach (var p in _parts.Values) p.Seen = false;
        _order.Clear();
        var game = findGame();
        var name = game?.Name ?? gameId;
        var now = DateTime.Now;
        var session = Duration(DateTimeOffset.UtcNow - startedAt);
        var key = KeyName(settings.Current.OverlayKey);

        void Show(string part, string content, Func<OverlayPart> build, Func<Size, Point> place) => ShowPart(part, content, build, place, send);
        Show("ecke", $"{now:HH:mm}{session}", () => OverlayParts.Corner(now.ToString("HH:mm"), now.ToString("ddd, d. MMMM yyyy", Formatting.De), session, _focus),
            _ => new Point(EdgeX, EdgeY));
        Show("titel", name, () => OverlayParts.Title(name), size => new Point((_screen.Width - size.Width) / 2, EdgeY));
        Show("zurueck", key, () => OverlayParts.Back(key, _focus), size => new Point(_screen.Width - EdgeX - size.Width, EdgeY));
        foreach (var w in _windows) ShowWindow(w, game, session, Show);
        // Die Symbolleiste bleibt über den Fenstern erreichbar.
        var open = _windows.ToHashSet();
        Show("leiste", string.Join(',', open.Order()), () => OverlayParts.Dock(open, _focus),
            size => new Point((_screen.Width - size.Width) / 2, Rows - EdgeY - size.Height));
        if (_confirm) Show("frage", name, () => OverlayParts.Confirm(name, _focus), size => new Point((_screen.Width - size.Width) / 2, (Rows - size.Height) / 2));

        foreach (var (gone, p) in _parts.Where(p => !p.Value.Seen).ToList())
        {
            _parts.Remove(gone);
            remove(p.Image);
        }
        var fade = DateTimeOffset.UtcNow - _openedAt < TimeSpan.FromMilliseconds(250) ? 0.15f : 0f;
        return
        [
            new OverlayQuad(DimImage, OverlayAnchor.Center, 0, 0, 8000, 2000) { FadeIn = fade },
            .. _order.Select(n => _parts[n]).Select(p =>
                new OverlayQuad(p.Image, OverlayAnchor.TopLeft, (float)p.At.X, (float)p.At.Y, (float)p.Size.Width, (float)p.Size.Height) { FadeIn = fade }),
        ];
    }

    private void ShowPart(string name, string content, Func<OverlayPart> build, Func<Size, Point> place, Func<uint, OverlayPart, Size> send)
    {
        if (!_parts.TryGetValue(name, out var p))
        {
            // Nummern wiederverwenden: Die Schicht merkt sich nur wenige Bilder.
            while (_parts.Values.Any(x => x.Image == _nextImage)) _nextImage = Next(_nextImage);
            _parts[name] = p = new Part { Image = _nextImage };
            _nextImage = Next(_nextImage);
        }
        p.Seen = true;
        _order.Add(name);
        // Nur mit dem Zeiger über einem eigenen Knopf sieht das Teil anders aus.
        string Key() => $"k|{content}|{(p.Hits.Any(h => h.Id == _focus) ? _focus : null)}";
        if (p.Key != Key())
        {
            var part = build();
            p.Hits = part.Hits;
            p.Key = Key();
            p.Size = send(p.Image, part);
        }
        p.At = place(p.Size);

        static uint Next(uint n) => FirstImage + (n + 1 - FirstImage) % ImageCount;
    }

    private void ShowWindow(string w, IOverlayGame? game, string session, Action<string, string, Func<OverlayPart>, Func<Size, Point>> show)
    {
        var s = settings.Current;
        var news = game?.OverlayNews ?? [];
        var scroll = Scroll(w, news.Count);
        void Show(string content, Func<OverlayPart> build) => show(w, content, build, size => PlaceOf(w, size));
        IReadOnlyList<AchievementViewModel> List() => AchievementViewModel.All(achievements.Definitions(gameId), achievements.States(gameId));
        static int Unlocked(IReadOnlyList<AchievementViewModel> l) => l.Count(a => a.Unlocked);
        static string Count(IReadOnlyList<AchievementViewModel> l) =>
            l.Count == 0 ? "Dieses Spiel hat noch keine Erfolge." : $"Freigeschaltet: {Unlocked(l)} von {l.Count}";
        static double Fraction(IReadOnlyList<AchievementViewModel> l) => l.Count == 0 ? 0 : (double)Unlocked(l) / l.Count;
        switch (w)
        {
            case "uebersicht":
                Show($"{session}|{_achievementVersion}|{news.Count}", () =>
                {
                    var l = List();
                    var total = Formatting.Playtime((game?.TotalPlaytime ?? TimeSpan.Zero) + (DateTimeOffset.UtcNow - startedAt));
                    return OverlayParts.Overview(total, session, Count(l), Fraction(l), l.FirstOrDefault(a => a.Unlocked), news, _focus);
                });
                break;
            case "erfolge":
                Show($"{_achievementVersion}|{scroll}", () =>
                {
                    var l = List();
                    return OverlayParts.Achievements(l, Count(l), Fraction(l), scroll, _focus);
                });
                break;
            case "mitspieler":
                Show($"{game?.SessionText}|{game?.SessionCode is not null}|{_copied}|{string.Join(',', game?.Players ?? [])}",
                    () => OverlayParts.Players(game?.SessionText, game?.Players ?? [], game?.SessionCode is not null, _copied, _focus));
                break;
            case "neues":
                Show($"{news.Count}|{scroll}", () => OverlayParts.News(news, scroll, _focus));
                break;
            case "einstellungen":
                Show($"{s.OverlayBar}|{s.OverlaySound}|{s.OverlayCorner}",
                    () => OverlayParts.Settings(s.OverlayBar, s.OverlaySound, CornerName(s.OverlayCorner), _focus));
                break;
        }
    }

    /// <summary>Erst an einem festen Platz, danach wo man es hingeschoben hat; immer ganz im Bild.</summary>
    private Point PlaceOf(string w, Size size)
    {
        if (!_places.TryGetValue(w, out var center))
            _places[w] = center = w switch
            {
                "uebersicht" => new Point(0.84, 0.56),
                "erfolge" => new Point(0.19, 0.53),
                "mitspieler" => new Point(0.59, 0.36),
                "neues" => new Point(0.50, 0.62),
                _ => new Point(0.5, 0.45),
            };
        return new Point(
            Math.Clamp(center.X * _screen.Width - size.Width / 2, 0, Math.Max(0, _screen.Width - size.Width)),
            Math.Clamp(center.Y * Rows - size.Height / 2, 0, Math.Max(0, Rows - size.Height)));
    }

    private int Scroll(string w, int news)
    {
        var max = w switch
        {
            "erfolge" => achievements.Definitions(gameId).Count - OverlayParts.AchievementRows,
            "neues" => news - OverlayParts.NewsRows,
            _ => 0,
        };
        return _scroll[w] = Math.Clamp(_scroll.GetValueOrDefault(w), 0, Math.Max(0, max));
    }

    // ---- Bedienung ----

    /// <summary>Eingabe des Spiels, z. B. <c>["move",0.5,0.5]</c> (siehe docs/erfolge.md der Engine).</summary>
    public Outcome Input(JsonElement ui)
    {
        if (ui.ValueKind != JsonValueKind.Array || ui.GetArrayLength() == 0 || ui[0].ValueKind != JsonValueKind.String) return Outcome.None;
        double Number(int i) => ui.GetArrayLength() > i && ui[i].ValueKind == JsonValueKind.Number ? Math.Clamp(ui[i].GetDouble(), -100, 100) : 0;
        var mouse = new Point(Number(1) * _screen.Width, Number(2) * Rows);
        switch (ui[0].GetString())
        {
            case "move":
                if (_drag is { } d)
                {
                    Drag(d.Window, mouse - d.Grab);
                    return Outcome.Changed;
                }
                var focus = HitAt(mouse) is { Focus: true } over ? over.Id : null;
                if (focus == _focus) return Outcome.None;
                _focus = focus;
                return Outcome.Changed;
            case "click":
                _drag = null;
                return HitAt(mouse) is { } hit ? Click(hit.Id, mouse) : Outcome.None;
            case "release":
                _drag = null;
                return Outcome.None;
            case "scroll":
                return ScrollBy(-Math.Sign(Number(1)));
            case "up" or "left":
                return MoveFocus(-1);
            case "down" or "right":
                return MoveFocus(1);
            case "ok":
                return _focus is { } f ? Click(f, default) : MoveFocus(1);
            case "back":
                if (!_confirm) return Outcome.Close;
                (_confirm, _focus) = (false, null);
                return Outcome.Changed;
            default:
                return Outcome.None;
        }
    }

    /// <summary>Solange gefragt wird, zählt nur die Frage.</summary>
    private IEnumerable<string> Reachable() => _confirm ? _order.Where(n => n == "frage") : _order;

    private OverlayHit? HitAt(Point at)
    {
        foreach (var name in Reachable().Reverse())
        {
            var p = _parts[name];
            if (p.Hits.LastOrDefault(h => h.Area.Contains(at - p.At)) is { } hit) return hit;
        }
        return null;
    }

    private Outcome MoveFocus(int step)
    {
        var all = Reachable().SelectMany(n => _parts[n].Hits).Where(h => h.Focus).Select(h => h.Id).ToList();
        if (all.Count == 0) return Outcome.None;
        var i = all.IndexOf(_focus ?? "");
        // Der erste Tastendruck beginnt in der Symbolleiste.
        _focus = i < 0 ? all.FirstOrDefault(h => h.StartsWith("dock:")) ?? all[0] : all[(i + step + all.Count) % all.Count];
        return Outcome.Changed;
    }

    private Outcome ScrollBy(int step)
    {
        if (_windows.LastOrDefault(w => w is "erfolge" or "neues") is not { } front) return Outcome.None;
        _scroll[front] = _scroll.GetValueOrDefault(front) + step;
        return Outcome.Changed;
    }

    private void Drag(string w, Point topLeft)
    {
        if (!_parts.TryGetValue(w, out var p)) return;
        Point Center(Point at) => new((at.X + p.Size.Width / 2) / _screen.Width, (at.Y + p.Size.Height / 2) / Rows);
        _places[w] = Center(topLeft);
        _places[w] = Center(PlaceOf(w, p.Size)); // am Rand hängen bleiben
    }

    private Outcome Click(string id, Point at)
    {
        if (id.StartsWith("dock:"))
        {
            // Symbol unten: Fenster an oder aus
            if (!_windows.Remove(id[5..])) _windows.Add(id[5..]);
            return Outcome.Changed;
        }
        if (id.StartsWith("win:"))
        {
            var (w, what) = (id.Split(':')[1], id.Split(':')[2]);
            _windows.Remove(w);
            if (what == "close") _focus = null;
            else _windows.Add(w); // nach vorn
            if (what == "title" && CanDrag && _parts.TryGetValue(w, out var p)) _drag = (w, at - p.At);
            return Outcome.Changed;
        }
        switch (id)
        {
            case "close":
                return Outcome.Close;
            case "quit":
                (_confirm, _focus) = (true, "quit-no");
                break;
            case "quit-no":
                (_confirm, _focus) = (false, null);
                break;
            case "quit-yes":
                _ = findGame()?.StopAsync();
                return Outcome.Close;
            case "copy":
                _copied = true;
                _ = findGame()?.CopyCodeAsync();
                break;
            case "set:bar":
                settings.Update(s => s with { OverlayBar = !s.OverlayBar });
                break;
            case "set:sound":
                settings.Update(s => s with { OverlaySound = !s.OverlaySound });
                break;
            case "set:corner":
                settings.Update(s => s with { OverlayCorner = (OverlayAnchor)(((int)s.OverlayCorner + 1) % 4) });
                break;
            default:
                return Outcome.None;
        }
        return Outcome.Changed;
    }
}
