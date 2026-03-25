namespace BigBro.Common.Config;

public sealed class AgentConfig
{
    public int InputAggregationIntervalSec { get; set; } = 30;
    public int IdleThresholdMin { get; set; } = 5;
    public int FlushIntervalSec { get; set; } = 5;
    public int FlushBatchSize { get; set; } = 100;
    public string DataPath { get; set; } = @"C:\ProgramData\BigBro\data";
    public ExportConfig Export { get; set; } = new();
    public WatchdogConfig Watchdog { get; set; } = new();
    public string CollectorPath { get; set; } = "BigBro.Collector.exe";
}

public sealed class ExportConfig
{
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 4;
    public string Mode { get; set; } = "UncShare";
    public string UncPath { get; set; } = string.Empty;
    public int MaxLocalDbSizeMb { get; set; } = 50;
    public bool CsvExport { get; set; } = true;
}

public sealed class WatchdogConfig
{
    public int MaxRestartsPerWindow { get; set; } = 5;
    public int RestartWindowMinutes { get; set; } = 10;
    public int RestartDelaySec { get; set; } = 5;
}
