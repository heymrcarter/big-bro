using BigBro.Common.Data;

namespace BigBro.Service.Exporters;

/// <summary>
/// Archives local SQLite databases when they exceed size/age thresholds.
/// Used as fallback when no UNC share is configured.
/// </summary>
public sealed class LocalArchiveExporter : IDataExporter
{
    private readonly string _dataPath;
    private readonly int _maxDbSizeMb;
    private readonly ILogger<LocalArchiveExporter> _logger;

    public LocalArchiveExporter(string dataPath, int maxDbSizeMb, ILogger<LocalArchiveExporter> logger)
    {
        _dataPath = dataPath;
        _maxDbSizeMb = maxDbSizeMb;
        _logger = logger;
    }

    public void Export(SqliteStore store)
    {
        var sizeBytes = store.GetDatabaseSizeBytes();
        var sizeMb = sizeBytes / (1024.0 * 1024.0);

        if (sizeMb < _maxDbSizeMb)
        {
            _logger.LogDebug("Database size {Size:F1}MB is under threshold {Max}MB. No rotation needed.",
                sizeMb, _maxDbSizeMb);
            return;
        }

        RotateDatabase();
    }

    private void RotateDatabase()
    {
        var dbPath = Path.Combine(_dataPath, "events.db");
        if (!File.Exists(dbPath)) return;

        var archiveDir = Path.Combine(_dataPath, "archive");
        Directory.CreateDirectory(archiveDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var archiveName = $"events_{timestamp}.db";
        var archivePath = Path.Combine(archiveDir, archiveName);

        try
        {
            // Copy the database (we can't move it while it's open by the collector)
            File.Copy(dbPath, archivePath, overwrite: false);
            _logger.LogInformation("Archived database to {Path}.", archivePath);

            // Also copy the WAL and SHM files if they exist
            CopyIfExists(dbPath + "-wal", archivePath + "-wal");
            CopyIfExists(dbPath + "-shm", archivePath + "-shm");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive database.");
        }
    }

    private static void CopyIfExists(string source, string dest)
    {
        if (File.Exists(source))
            File.Copy(source, dest, overwrite: false);
    }
}
