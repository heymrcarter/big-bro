using Microsoft.Extensions.Logging;

namespace BigBro.Collector.Interop;

internal sealed class WinEventHookManager : IDisposable
{
    private readonly ILogger _logger;
    private IntPtr _hookHandle;
    private NativeMethods.WinEventDelegate? _callback;
    private readonly Action<IntPtr> _onForegroundChanged;

    public WinEventHookManager(Action<IntPtr> onForegroundChanged, ILogger logger)
    {
        _onForegroundChanged = onForegroundChanged;
        _logger = logger;
    }

    public void Install()
    {
        // Must hold a reference to prevent GC collection of the delegate
        _callback = WinEventProc;
        _hookHandle = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (_hookHandle == IntPtr.Zero)
        {
            _logger.LogError("Failed to install WinEvent hook.");
            return;
        }

        _logger.LogInformation("WinEvent foreground hook installed.");
    }

    private void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND && hwnd != IntPtr.Zero)
        {
            _onForegroundChanged(hwnd);
        }
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _logger.LogInformation("WinEvent hook removed.");
        }
    }
}
