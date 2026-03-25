using BigBro.Common.Config;
using Microsoft.Extensions.Options;

namespace BigBro.Service;

public sealed class ServiceWorker : BackgroundService
{
    private readonly ILogger<ServiceWorker> _logger;
    private readonly CollectorLauncher _launcher;
    private readonly CollectorWatchdog _watchdog;
    private readonly ExportScheduler _exportScheduler;
    private readonly AgentConfig _config;

    public ServiceWorker(
        ILogger<ServiceWorker> logger,
        CollectorLauncher launcher,
        CollectorWatchdog watchdog,
        ExportScheduler exportScheduler,
        IOptions<AgentConfig> config)
    {
        _logger = logger;
        _launcher = launcher;
        _watchdog = watchdog;
        _exportScheduler = exportScheduler;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BigBro Service starting.");

        // Start the export scheduler
        _exportScheduler.Start(stoppingToken);

        // Start the watchdog (listens for heartbeats from collectors)
        _ = Task.Run(() => _watchdog.RunAsync(stoppingToken), stoppingToken);

        // Launch collectors for all active user sessions
        _launcher.LaunchForAllSessions();

        // Monitor for new sessions
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _launcher.CheckForNewSessions();
                await Task.Delay(10_000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in service main loop.");
                await Task.Delay(5_000, stoppingToken);
            }
        }

        _logger.LogInformation("BigBro Service stopping. Terminating collectors.");
        _launcher.TerminateAll();
    }
}
