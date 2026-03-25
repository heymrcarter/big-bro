using System.Diagnostics;
using BigBro.Common.Models;
using BigBro.Collector.Interop;
using BigBro.Collector.Pipeline;
using Microsoft.Extensions.Logging;

namespace BigBro.Collector.Collectors;

internal sealed class WindowTracker : IDisposable
{
    private readonly EventBuffer _buffer;
    private readonly BrowserUrlReader _browserUrlReader;
    private readonly ILogger _logger;
    private readonly string _userName;
    private readonly int _sessionId;
    private WinEventHookManager? _hookManager;

    // Deduplication state
    private uint _lastPid;
    private string? _lastTitle;

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "iexplore", "vivaldi"
    };

    public WindowTracker(EventBuffer buffer, BrowserUrlReader browserUrlReader,
        string userName, int sessionId, ILogger logger)
    {
        _buffer = buffer;
        _browserUrlReader = browserUrlReader;
        _userName = userName;
        _sessionId = sessionId;
        _logger = logger;
    }

    public void Start()
    {
        _hookManager = new WinEventHookManager(OnForegroundChanged, _logger);
        _hookManager.Install();

        // Capture the current foreground window at startup
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
            OnForegroundChanged(hwnd);
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;

            var title = GetWindowTitle(hwnd);

            // Deduplicate
            if (pid == _lastPid && title == _lastTitle)
                return;
            _lastPid = pid;
            _lastTitle = title;

            string? processName = null;
            string? appDisplayName = null;
            string? url = null;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;

                try
                {
                    appDisplayName = proc.MainModule?.FileVersionInfo.FileDescription;
                }
                catch
                {
                    // Access denied for some system processes
                }

                if (string.IsNullOrWhiteSpace(appDisplayName))
                    appDisplayName = processName;

                // Attempt to read browser URL
                if (BrowserProcesses.Contains(processName))
                {
                    url = _browserUrlReader.TryGetUrl(hwnd, processName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not get process info for PID {Pid}.", pid);
            }

            var evt = new ActivityEvent
            {
                EventType = EventType.WindowChange,
                UserName = _userName,
                SessionId = _sessionId,
                ProcessName = processName,
                AppDisplayName = appDisplayName,
                WindowTitle = title,
                Url = url
            };

            _buffer.Enqueue(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in foreground window handler.");
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new char[512];
        int length = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public void Dispose()
    {
        _hookManager?.Dispose();
    }
}
