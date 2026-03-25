using System.Collections.Concurrent;
using System.IO.Pipes;
using BigBro.Common.Config;
using BigBro.Common.IPC;
using Microsoft.Extensions.Options;

namespace BigBro.Service;

/// <summary>
/// Monitors collector processes via named pipe heartbeats.
/// Requests relaunch from CollectorLauncher when a collector goes silent.
/// </summary>
public sealed class CollectorWatchdog
{
    private readonly ILogger<CollectorWatchdog> _logger;
    private readonly IServiceProvider _services;
    private readonly WatchdogConfig _config;
    private readonly ConcurrentDictionary<int, CollectorState> _states = new();

    public CollectorWatchdog(
        ILogger<CollectorWatchdog> logger,
        IServiceProvider services,
        IOptions<AgentConfig> config)
    {
        _logger = logger;
        _services = services;
        _config = config.Value.Watchdog;
    }

    public void RegisterCollector(int sessionId, int pid)
    {
        _states[sessionId] = new CollectorState
        {
            ProcessId = pid,
            LastHeartbeat = DateTime.UtcNow,
            RestartCount = 0
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Two concurrent tasks: listen for heartbeats + check for timeouts
        var listenTask = ListenForHeartbeatsAsync(cancellationToken);
        var checkTask = CheckTimeoutsAsync(cancellationToken);
        await Task.WhenAll(listenTask, checkTask);
    }

    private async Task ListenForHeartbeatsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeConstants.WatchdogPipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);
                _ = Task.Run(() => HandlePipeClient(server, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in heartbeat listener.");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private async Task HandlePipeClient(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var lengthBuf = new byte[4];
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var bytesRead = await pipe.ReadAsync(lengthBuf, 0, 4, ct);
                if (bytesRead < 4) break;

                int msgLen = BitConverter.ToInt32(lengthBuf, 0);
                if (msgLen <= 0 || msgLen > 65536) break;

                var msgBuf = new byte[msgLen];
                bytesRead = await pipe.ReadAsync(msgBuf, 0, msgLen, ct);
                if (bytesRead < msgLen) break;

                var message = PipeMessage.Deserialize(msgBuf);
                if (message is not null)
                    ProcessHeartbeat(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pipe client disconnected.");
        }
    }

    private void ProcessHeartbeat(PipeMessage message)
    {
        if (_states.TryGetValue(message.SessionId, out var state))
        {
            state.LastHeartbeat = DateTime.UtcNow;
            state.EventCount = message.EventCount;
            _logger.LogDebug("Heartbeat from session {Session}, PID {Pid}, events: {Events}",
                message.SessionId, message.ProcessId, message.EventCount);
        }
    }

    private async Task CheckTimeoutsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PipeConstants.HeartbeatIntervalMs, cancellationToken);

            var now = DateTime.UtcNow;
            foreach (var kvp in _states)
            {
                var state = kvp.Value;
                var timeSinceHeartbeat = now - state.LastHeartbeat;

                if (timeSinceHeartbeat.TotalMilliseconds > PipeConstants.HeartbeatTimeoutMs)
                {
                    if (CanRestart(state))
                    {
                        _logger.LogWarning(
                            "Collector in session {Session} missed heartbeat ({Elapsed:F0}s). Relaunching.",
                            kvp.Key, timeSinceHeartbeat.TotalSeconds);

                        state.RestartCount++;
                        state.RestartTimes.Add(now);

                        var launcher = _services.GetRequiredService<CollectorLauncher>();
                        launcher.RelaunchForSession(kvp.Key);
                    }
                    else
                    {
                        _logger.LogError(
                            "Collector in session {Session} exceeded max restarts ({Max} in {Window}min). Not restarting.",
                            kvp.Key, _config.MaxRestartsPerWindow, _config.RestartWindowMinutes);
                    }
                }
            }
        }
    }

    private bool CanRestart(CollectorState state)
    {
        var windowStart = DateTime.UtcNow.AddMinutes(-_config.RestartWindowMinutes);
        var recentRestarts = state.RestartTimes.Count(t => t > windowStart);
        return recentRestarts < _config.MaxRestartsPerWindow;
    }

    private sealed class CollectorState
    {
        public int ProcessId { get; init; }
        public DateTime LastHeartbeat { get; set; }
        public long EventCount { get; set; }
        public int RestartCount { get; set; }
        public List<DateTime> RestartTimes { get; } = new();
    }
}
