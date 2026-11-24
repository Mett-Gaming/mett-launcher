using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MettLauncher.App.ViewModels;
using MettLauncher.App.Views;
using MettLauncher.Core.Achievements;
using MettLauncher.Core.Launch;
using MettLauncher.Core.Overlay;
using MettLauncher.Core.Presence;
using MettLauncher.Core.Settings;

namespace MettLauncher.App.Services;

/// <summary>Was das Overlay-Feld über ein Spiel wissen muss; von den ViewModels geliefert.</summary>
public interface IOverlayGame
{
    string Name { get; }
    TimeSpan TotalPlaytime { get; }
    string? SessionText { get; }
    string? SessionCode { get; }
    IReadOnlyList<string> Players { get; }
    Task CopyCodeAsync();
}

/// <summary>
/// Zeigt Erfolge, Leiste und Feld in laufenden Spielen. Alles läuft auf dem UI-Thread; die Bilder gehen fertig an die
/// Schicht im Spiel. Ist keine Schicht verbunden, erscheint die Einblendung als kleines Fenster.
/// </summary>
public sealed class OverlayService
{
    private const uint BarImage = 1, PanelImage = 2, DimImage = 3, FirstToast = 10, ToastImages = 48;
    private const float Margin = 24, Fade = 0.3f, Hold = 5f;
    private static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(Hold + 2 * Fade);

    private sealed record Toast(Func<Control> Build)
    {
        public uint Image { get; set; }
        public Window? Window { get; set; }
    }

    private sealed class Running(GameSession session)
    {
        public GameSession Session { get; } = session;
        public ToastQueue<Toast> Toasts { get; } = new(3, ToastTime);
        public SortedDictionary<int, OverlayQuad> Quads { get; } = [];
        public OverlayStats? Stats { get; set; }
        public uint NextToast { get; set; }
        public string BarText { get; set; } = "";
        public bool PanelOpen { get; set; }
        public int Selected { get; set; }
        public int Scroll { get; set; }
        public string PanelKey { get; set; } = "";
        /// <summary>Sendet in der richtigen Reihenfolge, ohne die Oberfläche anzuhalten.</summary>
        public Task Sending { get; set; } = Task.CompletedTask;
    }

    private readonly OverlayServer _server;
    private readonly PresenceServer _link;
    private readonly OverlayLayer _layer;
    private readonly AchievementService _achievements;
    private readonly SettingsStore _settings;
    private readonly Dictionary<string, Running> _games = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;

    public OverlayService(OverlayServer server, PresenceServer link, OverlayLayer layer, AchievementService achievements,
        GameLauncher launcher, SettingsStore settings)
    {
        (_server, _link, _layer, _achievements, _settings) = (server, link, layer, achievements, settings);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        achievements.Changed += c => Post(() => OnAchievement(c));
        launcher.GameStarted += (_, s) => Post(() => OnStarted(s));
        launcher.GameExited += (_, e) => Post(() => OnExited(e.GameId));
        server.Connected += (id, on) => Post(() => OnConnected(id, on));
        server.Stats += (id, s) => Post(() => OnStats(id, s));
        link.Overlay += (id, json) => Post(() => OnMessage(id, json));
        link.LinkClosed += id => Post(() => { if (_games.TryGetValue(id, out var g)) ClosePanel(id, g); });
        settings.Changed += (_, _) => Post(() => { foreach (var (id, g) in _games) g.BarText = ""; });
    }

    /// <summary>Von der Oberfläche gesetzt.</summary>
    public Func<string, IOverlayGame?> FindGame { get; set; } = _ => null;

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>Umgebung für einen Spielstart: <paramref name="other"/> plus alles fürs Overlay.</summary>
    public IReadOnlyDictionary<string, string> Environment(string gameId, IReadOnlyDictionary<string, string> other)
    {
        _achievements.Reload(gameId);
        var env = new Dictionary<string, string>(other);
        var s = _settings.Current;
        if (!s.Overlay) return env;
        foreach (var (k, v) in _layer.Environment(System.Environment.GetEnvironmentVariable)) env[k] = v;
        foreach (var (k, v) in _server.Register(gameId)) env[k] = v;
        if (s.OverlayKey.Length > 0) env["METT_OVERLAY_KEY"] = s.OverlayKey;
        return env;
    }

    // ---- Ereignisse ----

    private void OnStarted(GameSession session)
    {
        _games[session.GameId] = new Running(session);
        _timer.Start();
    }

    private void OnExited(string id)
    {
        _server.Unregister(id);
        if (_games.Remove(id, out var g))
            foreach (var (_, t) in g.Toasts.Shown) t.Window?.Close();
        if (_games.Count == 0) _timer.Stop();
    }

    private void OnConnected(string id, bool on)
    {
        if (!_games.TryGetValue(id, out var g)) return;
        // Eine neue Verbindung kennt keine Bilder.
        g.Quads.Clear();
        g.BarText = g.PanelKey = "";
        if (!on) g.Stats = null;
    }

    private void OnStats(string id, OverlayStats stats)
    {
        // Ganz am Anfang gibt es noch kein Bild.
        if (stats.Height <= 0 || !_games.TryGetValue(id, out var g)) return;
        // Andere Größe: alles in passender Schärfe neu zeichnen
        if (g.Stats?.Height != stats.Height) g.BarText = g.PanelKey = "";
        g.Stats = stats;
    }

    private void OnAchievement(AchievementChange c)
    {
        if (!_settings.Current.Overlay || !_games.TryGetValue(c.GameId, out var g)) return;
        var d = c.Definition;
        if (c.Unlocked)
        {
            g.Toasts.Add(new Toast(() => OverlayVisuals.Toast("ERFOLG FREIGESCHALTET", d.Name, d.Text, AchievementViewModel.LoadIcon(d), null)));
            if (_settings.Current.OverlaySound) OverlaySound.Play();
        }
        else if (c.Milestone > 0 && !d.Hidden)
        {
            g.Toasts.Add(new Toast(() => OverlayVisuals.Toast($"FORTSCHRITT  {c.Now.Progress.ToString("N0", Formatting.De)} / {d.Goal.ToString("N0", Formatting.De)}",
                d.Name, d.Text, AchievementViewModel.LoadIcon(d), (double)c.Now.Progress / d.Goal)));
        }
        Update(c.GameId, g);
    }

    private void Tick()
    {
        foreach (var (id, g) in _games.ToList()) Update(id, g);
    }

    private void Update(string id, Running g)
    {
        var connected = _server.IsConnected(id);
        var changed = UpdateToasts(id, g, connected);
        if (connected && g.Stats is not null)
        {
            changed |= UpdateBar(id, g);
            changed |= UpdatePanel(id, g);
        }
        if (changed && connected)
        {
            var quads = g.Quads.Values.ToList();
            Send(g, () => _server.SendQuadsAsync(id, quads));
        }
    }

    // ---- Einblendungen ----

    private bool UpdateToasts(string id, Running g, bool connected)
    {
        var changed = false;
        g.Toasts.Step(DateTimeOffset.UtcNow,
            removed: t =>
            {
                t.Window?.Close();
                if (t.Image == 0) return;
                foreach (var key in g.Quads.Where(q => q.Value.Image == t.Image).Select(q => q.Key).ToList()) g.Quads.Remove(key);
                Send(g, () => _server.RemoveImageAsync(id, t.Image));
                changed = true;
            },
            added: (slot, t) =>
            {
                if (!connected)
                {
                    t.Window = ShowWindow(t.Build(), slot);
                    return;
                }
                t.Image = FirstToast + g.NextToast++ % ToastImages;
                SendImage(id, g, t.Image, t.Build(), out var size);
                var corner = _settings.Current.OverlayCorner;
                var top = corner is OverlayAnchor.TopLeft or OverlayAnchor.TopRight;
                g.Quads[100 + slot] = new OverlayQuad(t.Image, corner, Margin, Margin + slot * ((float)size.Height + 12), (float)size.Width, (float)size.Height)
                {
                    FadeIn = Fade, Hold = Hold, FadeOut = Fade, SlideY = top ? -20 : 20,
                };
                changed = true;
            });
        return changed;
    }

    /// <summary>Für Spiele ohne Schicht (z. B. OpenGL): kleines Fenster, das den Fokus nicht nimmt.</summary>
    private Window ShowWindow(Control content, int slot)
    {
        var w = new Window
        {
            Content = content, SizeToContent = SizeToContent.WidthAndHeight, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, CanResize = false, Background = null, TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.Manual, Focusable = false,
        };
        if (w.Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var corner = _settings.Current.OverlayCorner;
            int Px(double v) => (int)(v * screen.Scaling);
            var left = corner is OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft;
            var top = corner is OverlayAnchor.TopLeft or OverlayAnchor.TopRight;
            var offset = Px(Margin + slot * (OverlayVisuals.ToastHeight + 12));
            w.Position = new PixelPoint(
                left ? area.X + Px(Margin) : area.Right - Px(Margin + OverlayVisuals.ToastWidth),
                top ? area.Y + offset : area.Bottom - offset - Px(OverlayVisuals.ToastHeight));
        }
        w.Show();
        return w;
    }

    // ---- Leiste ----

    private bool UpdateBar(string id, Running g)
    {
        var s = _settings.Current;
        if (!s.Overlay || !s.OverlayBar) return g.Quads.Remove(1);
        var text = $"{Math.Round(g.Stats!.Fps)} Bilder/s   {DateTime.Now:HH:mm}   {Duration(DateTimeOffset.UtcNow - g.Session.StartedAt)}";
        if (text == g.BarText && g.Quads.ContainsKey(1)) return false;
        g.BarText = text;
        SendImage(id, g, BarImage, OverlayVisuals.Bar(text), out var size);
        g.Quads[1] = new OverlayQuad(BarImage, s.OverlayBarCorner, Margin, Margin, (float)size.Width, (float)size.Height);
        return true;
    }

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} Std. {t.Minutes} Min." : $"{Math.Max(0, (int)t.TotalMinutes)} Min.";

    // ---- Feld ----

    private OverlayPanelContent Content(string id, Running g)
    {
        var game = FindGame(id);
        var list = AchievementViewModel.All(_achievements.Definitions(id), _achievements.States(id));
        var unlocked = list.Count(a => a.Unlocked);
        g.Scroll = Math.Clamp(g.Scroll, 0, Math.Max(0, list.Count - OverlayVisuals.PanelRows));
        List<string> buttons = ["Zurück zum Spiel"];
        if (game?.SessionCode is not null) buttons.Insert(0, "Einladungscode kopieren");
        g.Selected = Math.Clamp(g.Selected, 0, buttons.Count - 1);
        return new OverlayPanelContent
        {
            Game = game?.Name ?? id,
            Clock = DateTime.Now.ToString("HH:mm"),
            Session = Duration(DateTimeOffset.UtcNow - g.Session.StartedAt),
            Total = Formatting.Playtime((game?.TotalPlaytime ?? TimeSpan.Zero) + (DateTimeOffset.UtcNow - g.Session.StartedAt)),
            Count = $"{unlocked} von {list.Count}",
            Fraction = list.Count == 0 ? 0 : (double)unlocked / list.Count,
            Achievements = list,
            Multiplayer = game?.SessionText,
            Players = game?.Players ?? [],
            Buttons = buttons,
            Selected = g.Selected,
            Scroll = g.Scroll,
        };
    }

    private bool UpdatePanel(string id, Running g)
    {
        if (!g.PanelOpen) return false;
        var game = FindGame(id);
        var unlocked = _achievements.States(id);
        // Nur neu zeichnen, wenn sich etwas Sichtbares ändert
        var key = string.Join('|', DateTime.Now.ToString("HH:mm"), Duration(DateTimeOffset.UtcNow - g.Session.StartedAt), g.Selected, g.Scroll,
            game?.SessionText, game?.SessionCode is not null, string.Join(',', game?.Players ?? []), g.Stats!.Height,
            string.Join(',', unlocked.Select(u => $"{u.Key}{u.Value.Progress}")));
        if (key == g.PanelKey) return false;
        g.PanelKey = key;
        Send(g, () => _server.SendImageAsync(id, DimImage, 1, 1, [0, 0, 0, 160]));
        SendImage(id, g, PanelImage, OverlayVisuals.Panel(Content(id, g)), out var size);
        g.Quads[200] = new OverlayQuad(DimImage, OverlayAnchor.Center, 0, 0, 8000, 2000);
        g.Quads[201] = new OverlayQuad(PanelImage, OverlayAnchor.Center, 0, 0, (float)size.Width, (float)size.Height);
        return true;
    }

    private void OpenPanel(string id, Running g)
    {
        if (!_settings.Current.Overlay || !_server.IsConnected(id) || g.Stats is null) return;
        (g.PanelOpen, g.Scroll, g.Selected, g.PanelKey) = (true, 0, 0, "");
        _ = _link.SendAsync(id, "{\"overlay\":true}");
        Update(id, g);
    }

    private void ClosePanel(string id, Running g)
    {
        if (!g.PanelOpen) return;
        g.PanelOpen = false;
        g.Quads.Remove(200);
        g.Quads.Remove(201);
        var quads = g.Quads.Values.ToList();
        Send(g, () => _server.SendQuadsAsync(id, quads));
        _ = _link.SendAsync(id, "{\"overlay\":false}");
    }

    private void OnMessage(string id, JsonElement json)
    {
        if (!_games.TryGetValue(id, out var g)) return;
        if (json.TryGetProperty("overlay", out var o))
        {
            if (o.ValueKind != JsonValueKind.String || o.GetString() != "toggle") return;
            if (g.PanelOpen) ClosePanel(id, g);
            else OpenPanel(id, g);
            return;
        }
        if (!g.PanelOpen || !json.TryGetProperty("ui", out var ui) || ui.ValueKind != JsonValueKind.Array || ui.GetArrayLength() == 0
            || ui[0].ValueKind != JsonValueKind.String) return;
        double Number(int i) => ui.GetArrayLength() > i && ui[i].ValueKind == JsonValueKind.Number ? ui[i].GetDouble() : 0;
        var buttons = FindGame(id)?.SessionCode is null ? 1 : 2;
        switch (ui[0].GetString())
        {
            case "move": // Mauszeiger, 0..1 im Fenster
                if (ButtonAt(g, buttons, Number(1), Number(2)) is { } over) g.Selected = over;
                break;
            case "click":
                if (ButtonAt(g, buttons, Number(1), Number(2)) is { } hit) Press(id, g, hit, buttons);
                break;
            case "scroll":
                g.Scroll = Math.Max(0, g.Scroll - Math.Sign(Number(1)));
                break;
            case "up":
                if (g.Selected > 0) g.Selected--;
                else g.Scroll = Math.Max(0, g.Scroll - 1);
                break;
            case "down":
                if (g.Selected < buttons - 1) g.Selected++;
                else g.Scroll++;
                break;
            case "ok":
                Press(id, g, g.Selected, buttons);
                break;
            case "back":
                ClosePanel(id, g);
                break;
        }
        // Grenzen gleich anwenden, sonst zählt das Blättern über das Ende hinaus weiter
        g.Scroll = Math.Clamp(g.Scroll, 0, Math.Max(0, _achievements.Definitions(id).Count - OverlayVisuals.PanelRows));
        if (g.PanelOpen) Update(id, g);
    }

    private static int? ButtonAt(Running g, int count, double x, double y)
    {
        if (g.Stats is not { Width: > 0, Height: > 0 } s) return null;
        var width = 1080.0 * s.Width / s.Height;
        var p = new Point(x * width - (width - OverlayVisuals.PanelWidth) / 2, y * 1080 - (1080 - OverlayVisuals.PanelHeight) / 2);
        for (var i = 0; i < count; i++)
            if (OverlayVisuals.ButtonRect(i, count).Contains(p)) return i;
        return null;
    }

    private void Press(string id, Running g, int button, int count)
    {
        if (button == count - 1) ClosePanel(id, g);
        else _ = FindGame(id)?.CopyCodeAsync();
    }

    // ---- Senden ----

    private void SendImage(string id, Running g, uint image, Control control, out Size size)
    {
        var scale = Math.Clamp((g.Stats?.Height ?? 1080) / 1080.0, 0.5, 2);
        (var picture, size) = OverlayVisuals.Render(control, scale);
        Send(g, () => _server.SendImageAsync(id, image, picture.Width, picture.Height, picture.Rgba));
    }

    private static void Send(Running g, Func<Task> send) =>
        g.Sending = g.Sending.ContinueWith(_ => send(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
}
