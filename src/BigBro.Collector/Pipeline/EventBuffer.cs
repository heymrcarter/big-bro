using System.Collections.Concurrent;
using BigBro.Common.Models;

namespace BigBro.Collector.Pipeline;

/// <summary>
/// Thread-safe event buffer. Collectors enqueue events; the flusher drains them to SQLite.
/// </summary>
internal sealed class EventBuffer
{
    private readonly ConcurrentQueue<ActivityEvent> _queue = new();

    public void Enqueue(ActivityEvent evt)
    {
        _queue.Enqueue(evt);
    }

    public int Count => _queue.Count;

    public List<ActivityEvent> DrainAll()
    {
        var events = new List<ActivityEvent>();
        while (_queue.TryDequeue(out var evt))
        {
            events.Add(evt);
        }
        return events;
    }

    public List<ActivityEvent> DrainUpTo(int maxItems)
    {
        var events = new List<ActivityEvent>(Math.Min(maxItems, _queue.Count));
        for (int i = 0; i < maxItems && _queue.TryDequeue(out var evt); i++)
        {
            events.Add(evt);
        }
        return events;
    }
}
