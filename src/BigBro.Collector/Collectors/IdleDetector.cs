using BigBro.Common.Models;
using BigBro.Collector.Interop;
using BigBro.Collector.Pipeline;
using Microsoft.Extensions.Logging;

namespace BigBro.Collector.Collectors;

internal sealed class IdleDetector : IDisposable
{
    private readonly EventBuffer _buffer;
    private readonly ILogger _logger;
    private readonly string _userName;
    private readonly int _sessionId;
    private readonly int _idleThresholdMs;

    private System.Threading.Timer? _pollTimer;
    private bool _isIdle;
    private DateTime _idleStartUtc;

    private const int PollIntervalMs = 5_000;

    public IdleDetector(EventBuffer buffer, string userName, int sessionId,
        int idleThresholdMin, ILogger logger)
    {
        _buffer = buffer;
        _userName = userName;
        _sessionId = sessionId;
        _idleThresholdMs = idleThresholdMin * 60 * 1000;
        _logger = logger;
    }

    public void Start()
    {
        _pollTimer = new System.Threading.Timer(CheckIdle, null, PollIntervalMs, PollIntervalMs);
    }

    private void CheckIdle(object? state)
    {
        try
        {
            var idleMs = GetIdleTimeMs();

            if (!_isIdle && idleMs >= _idleThresholdMs)
            {
                _isIdle = true;
                _idleStartUtc = DateTime.UtcNow.AddMilliseconds(-idleMs);

                _buffer.Enqueue(new ActivityEvent
                {
                    EventType = EventType.IdleState,
                    UserName = _userName,
                    SessionId = _sessionId,
                    IsIdle = true
                });

                _logger.LogDebug("User went idle after {IdleMs}ms.", idleMs);
            }
            else if (_isIdle && idleMs < _idleThresholdMs)
            {
                var duration = (DateTime.UtcNow - _idleStartUtc).TotalSeconds;
                _isIdle = false;

                _buffer.Enqueue(new ActivityEvent
                {
                    EventType = EventType.IdleState,
                    UserName = _userName,
                    SessionId = _sessionId,
                    IsIdle = false,
                    IdleDurationSec = duration
                });

                _logger.LogDebug("User returned from idle. Duration: {Duration:F1}s.", duration);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in idle detection.");
        }
    }

    private static uint GetIdleTimeMs()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
            return 0;

        return (uint)Environment.TickCount - info.dwTime;
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
    }
}
