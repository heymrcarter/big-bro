using Microsoft.Extensions.Logging;

namespace BigBro.Collector.Interop;

internal sealed class LowLevelInputHook : IDisposable
{
    private readonly ILogger _logger;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    // Must keep references alive to prevent GC of the delegates
    private NativeMethods.LowLevelHookProc? _keyboardProc;
    private NativeMethods.LowLevelHookProc? _mouseProc;

    private readonly Action _onKeyPress;
    private readonly Action _onMouseClick;
    private readonly Action _onScroll;

    public LowLevelInputHook(Action onKeyPress, Action onMouseClick, Action onScroll, ILogger logger)
    {
        _onKeyPress = onKeyPress;
        _onMouseClick = onMouseClick;
        _onScroll = onScroll;
        _logger = logger;
    }

    public void Install()
    {
        var moduleHandle = NativeMethods.GetModuleHandle(null);

        _keyboardProc = KeyboardProc;
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);

        if (_keyboardHook == IntPtr.Zero)
            _logger.LogError("Failed to install keyboard hook.");
        else
            _logger.LogInformation("Low-level keyboard hook installed.");

        _mouseProc = MouseProc;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);

        if (_mouseHook == IntPtr.Zero)
            _logger.LogError("Failed to install mouse hook.");
        else
            _logger.LogInformation("Low-level mouse hook installed.");
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= NativeMethods.HC_ACTION)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
            {
                _onKeyPress();
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= NativeMethods.HC_ACTION)
        {
            int msg = wParam.ToInt32();
            switch (msg)
            {
                case NativeMethods.WM_LBUTTONDOWN:
                case NativeMethods.WM_RBUTTONDOWN:
                case NativeMethods.WM_MBUTTONDOWN:
                    _onMouseClick();
                    break;
                case NativeMethods.WM_MOUSEWHEEL:
                    _onScroll();
                    break;
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public bool AreHooksInstalled()
    {
        return _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;
    }

    public void Reinstall()
    {
        Dispose();
        Install();
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }
}
