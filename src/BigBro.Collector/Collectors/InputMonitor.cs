using BigBro.Common.Models;
using BigBro.Collector.Interop;
using BigBro.Collector.Pipeline;
using Microsoft.Extensions.Logging;

namespace BigBro.Collector.Collectors;

internal sealed class InputMonitor : IDisposable
{
    private readonly EventBuffer _buffer;
    private readonly ILogger _logger;
    private readonly string _userName;
    private readonly int _sessionId;
    private readonly int _aggregationIntervalMs;

    private LowLevelInputHook? _hook;
    private System.Threading.Timer? _aggregationTimer;

    // Counters — accessed from hook callbacks on the message pump thread
    // and from the timer callback on a thread pool thread.
    private int _keyPresses;
    private int _mouseClicks;
    private int _scrollEvents;

    public InputMonitor(EventBuffer buffer, string userName, int sessionId,
        int aggregationIntervalSec, ILogger logger)
    {
        _buffer = buffer;
        _userName = userName;
        _sessionId = sessionId;
        _aggregationIntervalMs = aggregationIntervalSec * 1000;
        _logger = logger;
    }

    public LowLevelInputHook Start()
    {
        _hook = new LowLevelInputHook(
            onKeyPress: () => Interlocked.Increment(ref _keyPresses),
            onMouseClick: () => Interlocked.Increment(ref _mouseClicks),
            onScroll: () => Interlocked.Increment(ref _scrollEvents),
            _logger);

        _hook.Install();

        _aggregationTimer = new System.Threading.Timer(
            FlushCounters, null, _aggregationIntervalMs, _aggregationIntervalMs);

        return _hook;
    }

    private void FlushCounters(object? state)
    {
        var keys = Interlocked.Exchange(ref _keyPresses, 0);
        var clicks = Interlocked.Exchange(ref _mouseClicks, 0);
        var scrolls = Interlocked.Exchange(ref _scrollEvents, 0);

        // Only emit if there was any activity
        if (keys == 0 && clicks == 0 && scrolls == 0)
            return;

        // Tag with current foreground window context
        string? processName = null;
        string? windowTitle = null;
        string? appDisplayName = null;

        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0)
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                    windowTitle = GetWindowTitle(hwnd);
                    try
                    {
                        appDisplayName = proc.MainModule?.FileVersionInfo.FileDescription;
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(appDisplayName))
                        appDisplayName = processName;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not get foreground window for input event.");
        }

        var evt = new ActivityEvent
        {
            EventType = EventType.InputActivity,
            UserName = _userName,
            SessionId = _sessionId,
            ProcessName = processName,
            AppDisplayName = appDisplayName,
            WindowTitle = windowTitle,
            KeyPresses = keys,
            MouseClicks = clicks,
            ScrollEvents = scrolls
        };

        _buffer.Enqueue(evt);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new char[512];
        int length = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public void Dispose()
    {
        _aggregationTimer?.Dispose();
        _hook?.Dispose();
    }
}
