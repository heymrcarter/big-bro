namespace BigBro.Common.Models;

public sealed class ActivityEvent
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string MachineName { get; init; } = Environment.MachineName;
    public string UserName { get; init; } = string.Empty;
    public int SessionId { get; init; }
    public EventType EventType { get; init; }

    // Window context
    public string? ProcessName { get; init; }
    public string? AppDisplayName { get; init; }
    public string? WindowTitle { get; init; }
    public string? Url { get; init; }

    // Input activity (for InputActivity events)
    public int MouseClicks { get; init; }
    public int KeyPresses { get; init; }
    public int ScrollEvents { get; init; }

    // Session events
    public SessionAction? SessionAction { get; init; }

    // Idle state
    public bool? IsIdle { get; init; }
    public double? IdleDurationSec { get; init; }
}
