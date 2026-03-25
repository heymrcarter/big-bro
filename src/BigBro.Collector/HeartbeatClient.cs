using System.Diagnostics;
using System.IO.Pipes;
using BigBro.Common.IPC;
using Microsoft.Extensions.Logging;

namespace BigBro.Collector;

/// <summary>
/// Sends periodic heartbeats to the BigBro.Service watchdog over a named pipe.
/// Runs on a background thread. If the pipe is unavailable, logs and retries.
/// </summary>
internal sealed class HeartbeatClient : IDisposable
{
    private readonly ILogger _logger;
    private readonly int _sessionId;
    private readonly Func<long> _getEventCount;
    private System.Threading.Timer? _timer;
    private NamedPipeClientStream? _pipe;

    public HeartbeatClient(int sessionId, Func<long> getEventCount, ILogger logger)
    {
        _sessionId = sessionId;
        _getEventCount = getEventCount;
        _logger = logger;
    }

    public void Start()
    {
        _timer = new System.Threading.Timer(SendHeartbeat, null,
            PipeConstants.HeartbeatIntervalMs, PipeConstants.HeartbeatIntervalMs);
        _logger.LogInformation("Heartbeat client started.");
    }

    private void SendHeartbeat(object? state)
    {
        try
        {
            EnsureConnected();
            if (_pipe is null || !_pipe.IsConnected)
                return;

            var msg = new PipeMessage
            {
                Type = PipeMessageType.Heartbeat,
                ProcessId = Environment.ProcessId,
                SessionId = _sessionId,
                EventCount = _getEventCount()
            };

            var data = msg.Serialize();
            var lengthBytes = BitConverter.GetBytes(data.Length);
            _pipe.Write(lengthBytes, 0, 4);
            _pipe.Write(data, 0, data.Length);
            _pipe.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heartbeat send failed. Will retry.");
            DisconnectPipe();
        }
    }

    private void EnsureConnected()
    {
        if (_pipe is not null && _pipe.IsConnected)
            return;

        DisconnectPipe();

        try
        {
            _pipe = new NamedPipeClientStream(".", PipeConstants.WatchdogPipeName,
                PipeDirection.Out, PipeOptions.Asynchronous);
            _pipe.Connect(1000); // 1 second timeout
            _logger.LogDebug("Connected to watchdog pipe.");
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("Watchdog pipe not available. Collector continues independently.");
            DisconnectPipe();
        }
    }

    private void DisconnectPipe()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        DisconnectPipe();
    }
}
