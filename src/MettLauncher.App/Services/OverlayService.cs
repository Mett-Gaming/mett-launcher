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

/// <summary>
/// Zeigt Erfolge, Leiste und Overlay-Bildschirm in laufenden Spielen. Alles läuft auf dem UI-Thread; die Bilder gehen fertig an die
/// Schicht im Spiel. Ist keine Schicht verbunden, erscheint die Einblendung als kleines Fenster.
/// </summary>
public sealed class OverlayService
{
    private const uint BarImage = 1, FirstToast = 10, ToastImages = 48;
    /// <summary>Zeichenreihenfolge: Leiste, Overlay-Bildschirm, Einblendungen.</summary>
    private const int BarOrder = 1, ScreenOrder = 10, ToastOrder = 100;
    private const float Margin = 24, Fade = 0.3f, Hold = 5f;
    private static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(Hold + 2 * Fade);

    private sealed record Toast(Func<Control> Build)
    {
        public uint Image { get; set; }
        public Window? Window { get; set; }
    }

    private sealed class Running(GameSession session, OverlayScreen screen)
    {
        public GameSession Session { get; } = session;
        public ToastQueue<Toast> Toasts { get; } = new(3, ToastTime);
        public SortedDictionary<int, OverlayQuad> Quads { get; } = [];
        public OverlayStats? Stats { get; set; }
        public uint NextToast { get; set; }
        public string BarText { get; set; } = "";
        public OverlayScreen Screen { get; } = screen;
        /// <summary>Zuletzt gezeigter Overlay-Bildschirm.</summary>
        public IReadOnlyList<OverlayQuad> ScreenQuads { get; set; } = [];
        public bool DimSent { get; set; }
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
        link.LinkClosed += id => Post(() => { if (_games.TryGetValue(id, out var g)) CloseScreen(id, g); });
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
        var id = session.GameId;
        _games[id] = new Running(session, new OverlayScreen(id, session.StartedAt, _achievements, _settings, () => FindGame(id)));
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
        g.BarText = "";
        g.ScreenQuads = [];
        g.DimSent = false;
        g.Screen.Forget();
        if (!on) g.Stats = null;
    }

    private void OnStats(string id, OverlayStats stats)
    {
        // Ganz am Anfang gibt es noch kein Bild.
        if (stats.Height <= 0 || !_games.TryGetValue(id, out var g)) return;
        // Andere Größe: alles in passender Schärfe neu zeichnen
        if (g.Stats?.Height != stats.Height)
        {
            g.BarText = "";
            g.Screen.Forget();
        }
        g.Stats = stats;
    }

    private void OnAchievement(AchievementChange c)
    {
        if (!_settings.Current.Overlay || !_games.TryGetValue(c.GameId, out var g)) return;
        g.Screen.AchievementsChanged();
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
            changed |= UpdateScreen(id, g);
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
                g.Quads[ToastOrder + slot] = new OverlayQuad(t.Image, corner, Margin, Margin + slot * ((float)size.Height + 12), (float)size.Width, (float)size.Height)
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
        if (!s.Overlay || !s.OverlayBar) return g.Quads.Remove(BarOrder);
        var text = $"{Math.Round(g.Stats!.Fps)} Bilder/s   {DateTime.Now:HH:mm}   {OverlayScreen.Duration(DateTimeOffset.UtcNow - g.Session.StartedAt)}";
        if (text == g.BarText && g.Quads.ContainsKey(BarOrder)) return false;
        g.BarText = text;
        SendImage(id, g, BarImage, OverlayVisuals.Bar(text), out var size);
        g.Quads[BarOrder] = new OverlayQuad(BarImage, s.OverlayBarCorner, Margin, Margin, (float)size.Width, (float)size.Height);
        return true;
    }

    // ---- Overlay-Bildschirm ----

    private bool UpdateScreen(string id, Running g)
    {
        IReadOnlyList<OverlayQuad> quads = [];
        if (g.Screen.IsOpen)
        {
            if (!g.DimSent)
            {
                Send(g, () => _server.SendImageAsync(id, OverlayScreen.DimImage, 1, 1, [0, 0, 0, 205]));
                g.DimSent = true;
            }
            quads = g.Screen.Draw(g.Stats!, (image, part) =>
            {
                SendImage(id, g, image, part.Canvas, out var size);
                return size;
            }, image => Send(g, () => _server.RemoveImageAsync(id, image)));
        }
        if (quads.SequenceEqual(g.ScreenQuads)) return false;
        g.ScreenQuads = quads;
        foreach (var key in g.Quads.Keys.Where(k => k is >= ScreenOrder and < ToastOrder).ToList()) g.Quads.Remove(key);
        for (var i = 0; i < quads.Count; i++) g.Quads[ScreenOrder + i] = quads[i];
        return true;
    }

    private void OpenScreen(string id, Running g)
    {
        if (!_settings.Current.Overlay || !_server.IsConnected(id) || g.Stats is null) return;
        g.Screen.CanDrag = _link.LinkVersion(id) >= 2;
        g.Screen.Open();
        _ = _link.SendAsync(id, "{\"overlay\":true}");
        Update(id, g);
    }

    private void CloseScreen(string id, Running g)
    {
        if (!g.Screen.IsOpen) return;
        g.Screen.Close();
        _ = _link.SendAsync(id, "{\"overlay\":false}");
        Update(id, g);
    }

    private void OnMessage(string id, JsonElement json)
    {
        if (!_games.TryGetValue(id, out var g)) return;
        if (json.TryGetProperty("overlay", out var o))
        {
            if (o.ValueKind != JsonValueKind.String || o.GetString() != "toggle") return;
            if (g.Screen.IsOpen) CloseScreen(id, g);
            else OpenScreen(id, g);
            return;
        }
        if (!g.Screen.IsOpen || !json.TryGetProperty("ui", out var ui)) return;
        switch (g.Screen.Input(ui))
        {
            case OverlayScreen.Outcome.Close:
                CloseScreen(id, g);
                break;
            case OverlayScreen.Outcome.Changed:
                Update(id, g);
                break;
        }
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
