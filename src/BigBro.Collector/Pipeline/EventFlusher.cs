using BigBro.Common.Data;
using Microsoft.Extensions.Logging;

namespace BigBro.Collector.Pipeline;

/// <summary>
/// Periodically drains the EventBuffer and batch-inserts events into SQLite.
/// Runs on a background thread, decoupled from the message pump.
/// </summary>
internal sealed class EventFlusher : IDisposable
{
    private readonly EventBuffer _buffer;
    private readonly SqliteStore _store;
    private readonly ILogger _logger;
    private readonly int _flushIntervalMs;
    private readonly int _batchSize;
    private System.Threading.Timer? _timer;
    private long _totalFlushed;

    public long TotalFlushed => Interlocked.Read(ref _totalFlushed);

    public EventFlusher(EventBuffer buffer, SqliteStore store,
        int flushIntervalSec, int batchSize, ILogger logger)
    {
        _buffer = buffer;
        _store = store;
        _flushIntervalMs = flushIntervalSec * 1000;
        _batchSize = batchSize;
        _logger = logger;
    }

    public void Start()
    {
        _timer = new System.Threading.Timer(Flush, null, _flushIntervalMs, _flushIntervalMs);
        _logger.LogInformation("Event flusher started. Interval: {Interval}s, Batch: {Batch}.",
            _flushIntervalMs / 1000, _batchSize);
    }

    private void Flush(object? state)
    {
        try
        {
            while (_buffer.Count > 0)
            {
                var events = _buffer.DrainUpTo(_batchSize);
                if (events.Count == 0) break;

                _store.InsertEvents(events);
                Interlocked.Add(ref _totalFlushed, events.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing events to SQLite.");
        }
    }

    /// <summary>
    /// Final flush on shutdown — drain everything remaining.
    /// </summary>
    public void FlushRemaining()
    {
        try
        {
            var events = _buffer.DrainAll();
            if (events.Count > 0)
            {
                _store.InsertEvents(events);
                Interlocked.Add(ref _totalFlushed, events.Count);
                _logger.LogInformation("Final flush: {Count} events.", events.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in final flush.");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
