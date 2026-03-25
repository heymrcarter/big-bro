using System.Globalization;
using System.Text;
using BigBro.Common.Data;
using BigBro.Common.Models;

namespace BigBro.Service.Exporters;

/// <summary>
/// Exports unexported events as CSV to a UNC network share.
/// Marks events as exported on success.
/// </summary>
public sealed class UncShareExporter : IDataExporter
{
    private readonly string _uncPath;
    private readonly bool _csvExport;
    private readonly ILogger<UncShareExporter> _logger;

    public UncShareExporter(string uncPath, bool csvExport, ILogger<UncShareExporter> logger)
    {
        _uncPath = uncPath;
        _csvExport = csvExport;
        _logger = logger;
    }

    public void Export(SqliteStore store)
    {
        if (string.IsNullOrWhiteSpace(_uncPath))
        {
            _logger.LogWarning("UNC path not configured. Skipping export.");
            return;
        }

        var events = store.GetUnexportedEvents();
        if (events.Count == 0)
        {
            _logger.LogDebug("No unexported events.");
            return;
        }

        var batchId = Guid.NewGuid().ToString("N")[..12];
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var machineName = Environment.MachineName;
        var fileName = $"{machineName}_{timestamp}_{batchId}.csv";

        try
        {
            Directory.CreateDirectory(_uncPath);
            var filePath = Path.Combine(_uncPath, fileName);

            if (_csvExport)
            {
                WriteCsv(filePath, events);
            }

            // Get the IDs of the events we just exported and mark them
            var ids = store.GetUnexportedEventIds(events.Count);
            store.MarkExported(ids, batchId);

            _logger.LogInformation("Exported {Count} events to {Path}.", events.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export to UNC share {Path}.", _uncPath);
        }
    }

    private static void WriteCsv(string path, List<ActivityEvent> events)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("timestamp_utc,machine_name,user_name,session_id,event_type," +
            "process_name,app_display_name,window_title,url," +
            "mouse_clicks,key_presses,scroll_events," +
            "session_action,is_idle,idle_duration_sec");

        foreach (var e in events)
        {
            sb.Append(e.TimestampUtc.ToString("o", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(CsvEscape(e.MachineName)).Append(',');
            sb.Append(CsvEscape(e.UserName)).Append(',');
            sb.Append(e.SessionId).Append(',');
            sb.Append(e.EventType).Append(',');
            sb.Append(CsvEscape(e.ProcessName)).Append(',');
            sb.Append(CsvEscape(e.AppDisplayName)).Append(',');
            sb.Append(CsvEscape(e.WindowTitle)).Append(',');
            sb.Append(CsvEscape(e.Url)).Append(',');
            sb.Append(e.MouseClicks).Append(',');
            sb.Append(e.KeyPresses).Append(',');
            sb.Append(e.ScrollEvents).Append(',');
            sb.Append(e.SessionAction?.ToString() ?? "").Append(',');
            sb.Append(e.IsIdle.HasValue ? (e.IsIdle.Value ? "1" : "0") : "").Append(',');
            sb.AppendLine(e.IdleDurationSec?.ToString(CultureInfo.InvariantCulture) ?? "");
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

}
