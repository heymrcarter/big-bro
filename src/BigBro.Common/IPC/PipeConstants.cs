namespace BigBro.Common.IPC;

public static class PipeConstants
{
    public const string WatchdogPipeName = "BigBroWatchdog";
    public const int HeartbeatIntervalMs = 30_000;
    public const int HeartbeatTimeoutMs = 60_000;
}
