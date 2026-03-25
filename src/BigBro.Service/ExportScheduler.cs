using BigBro.Common.Config;
using BigBro.Common.Data;
using BigBro.Service.Exporters;
using Microsoft.Extensions.Options;

namespace BigBro.Service;

/// <summary>
/// Periodically triggers data export from the local SQLite store.
/// </summary>
public sealed class ExportScheduler
{
    private readonly ILogger<ExportScheduler> _logger;
    private readonly AgentConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private Timer? _timer;

    public ExportScheduler(
        ILogger<ExportScheduler> logger,
        ILoggerFactory loggerFactory,
        IOptions<AgentConfig> config)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config.Value;
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (!_config.Export.Enabled)
        {
            _logger.LogInformation("Export is disabled.");
            return;
        }

        var intervalMs = _config.Export.IntervalHours * 3600 * 1000;

        // Run first export after 1 minute, then on schedule
        _timer = new Timer(
            _ => RunExport(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(intervalMs));

        _logger.LogInformation("Export scheduler started. Interval: {Hours}h. Mode: {Mode}.",
            _config.Export.IntervalHours, _config.Export.Mode);

        cancellationToken.Register(() => _timer?.Dispose());
    }

    private void RunExport()
    {
        try
        {
            var dbPath = Path.Combine(_config.DataPath, "events.db");
            if (!File.Exists(dbPath))
            {
                _logger.LogDebug("No database file to export.");
                return;
            }

            using var store = new SqliteStore(dbPath, _loggerFactory.CreateLogger<SqliteStore>());

            IDataExporter exporter = _config.Export.Mode.ToLowerInvariant() switch
            {
                "uncshare" => new UncShareExporter(
                    _config.Export.UncPath, _config.Export.CsvExport,
                    _loggerFactory.CreateLogger<UncShareExporter>()),
                _ => new LocalArchiveExporter(
                    _config.DataPath, _config.Export.MaxLocalDbSizeMb,
                    _loggerFactory.CreateLogger<LocalArchiveExporter>())
            };

            exporter.Export(store);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed.");
        }
    }
}
