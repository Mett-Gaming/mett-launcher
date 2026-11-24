namespace MettLauncher.Core.Overlay;

/// <summary>Verteilt Einblendungen auf wenige Plätze; der Rest wartet. Nicht threadsicher.</summary>
public sealed class ToastQueue<T>(int slots, TimeSpan duration)
{
    private readonly Queue<T> _waiting = new();
    private readonly (T Item, DateTimeOffset Until)?[] _shown = new (T, DateTimeOffset)?[slots];

    public void Add(T item) => _waiting.Enqueue(item);

    /// <summary>Nimmt Abgelaufene heraus (<paramref name="removed"/>) und rückt Wartende nach (<paramref name="added"/>).</summary>
    public void Step(DateTimeOffset now, Action<T> removed, Action<int, T> added)
    {
        for (var i = 0; i < _shown.Length; i++)
            if (_shown[i] is { } s && now >= s.Until)
            {
                _shown[i] = null;
                removed(s.Item);
            }
        for (var i = 0; i < _shown.Length && _waiting.Count > 0; i++)
            if (_shown[i] is null)
            {
                var item = _waiting.Dequeue();
                _shown[i] = (item, now + duration);
                added(i, item);
            }
    }

    public IEnumerable<(int Slot, T Item)> Shown
    {
        get
        {
            for (var i = 0; i < _shown.Length; i++)
                if (_shown[i] is { } s) yield return (i, s.Item);
        }
    }

    public bool IsEmpty => _waiting.Count == 0 && _shown.All(s => s is null);
}
