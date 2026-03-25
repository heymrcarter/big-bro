using System.Text.Json;

namespace BigBro.Common.IPC;

public enum PipeMessageType
{
    Heartbeat = 1,
    Shutdown = 2,
    Status = 3
}

public sealed class PipeMessage
{
    public PipeMessageType Type { get; set; }
    public int ProcessId { get; set; }
    public int SessionId { get; set; }
    public long EventCount { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this);
    }

    public static PipeMessage? Deserialize(byte[] data)
    {
        return JsonSerializer.Deserialize<PipeMessage>(data);
    }
}
