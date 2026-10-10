using System.Windows.Threading;

namespace AnyCam.Desktop.Services;

/// <summary>
/// Rate-limits actions per key (UI thread): the first call runs immediately, later calls within the
/// interval are coalesced and the last one runs when the interval ends. Keeps slider drags smooth
/// without flooding the phone.
/// </summary>
public sealed class Throttler
{
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, (DispatcherTimer Timer, Action? Pending)> _entries = new();

    public Throttler(TimeSpan interval) => _interval = interval;

    public void Run(string key, Action action)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = (entry.Timer, action);
            return;
        }

        action();
        var timer = new DispatcherTimer { Interval = _interval };
        timer.Tick += (_, _) =>
        {
            var pending = _entries[key].Pending;
            if (pending is null)
            {
                timer.Stop();
                _entries.Remove(key);
                return;
            }
            _entries[key] = (timer, null);
            pending();
        };
        _entries[key] = (timer, null);
        timer.Start();
    }
}
