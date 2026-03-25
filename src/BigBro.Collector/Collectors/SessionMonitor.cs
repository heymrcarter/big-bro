using System.Runtime.InteropServices;
using BigBro.Common.Models;
using BigBro.Collector.Interop;
using BigBro.Collector.Pipeline;
using Microsoft.Extensions.Logging;

namespace BigBro.Collector.Collectors;

/// <summary>
/// Monitors Windows session changes (lock/unlock/logon/logoff) via a hidden message window.
/// </summary>
internal sealed class SessionMonitor : IDisposable
{
    private readonly EventBuffer _buffer;
    private readonly ILogger _logger;
    private readonly string _userName;
    private readonly int _sessionId;
    private SessionNotificationWindow? _window;

    public SessionMonitor(EventBuffer buffer, string userName, int sessionId, ILogger logger)
    {
        _buffer = buffer;
        _userName = userName;
        _sessionId = sessionId;
        _logger = logger;
    }

    public void Start()
    {
        // The hidden window must be created on the message pump thread
        _window = new SessionNotificationWindow(OnSessionChange);

        if (!NativeMethods.WTSRegisterSessionNotification(_window.Handle, NativeMethods.NOTIFY_FOR_THIS_SESSION))
        {
            _logger.LogWarning("Failed to register for session notifications.");
        }
        else
        {
            _logger.LogInformation("Session monitor registered.");
        }
    }

    private void OnSessionChange(int changeType)
    {
        SessionAction? action = changeType switch
        {
            NativeMethods.WTS_SESSION_LOGON => SessionAction.Login,
            NativeMethods.WTS_SESSION_LOGOFF => SessionAction.Logout,
            NativeMethods.WTS_SESSION_LOCK => SessionAction.Lock,
            NativeMethods.WTS_SESSION_UNLOCK => SessionAction.Unlock,
            NativeMethods.WTS_REMOTE_CONNECT => SessionAction.RemoteConnect,
            NativeMethods.WTS_REMOTE_DISCONNECT => SessionAction.RemoteDisconnect,
            _ => null
        };

        if (action is null) return;

        _buffer.Enqueue(new ActivityEvent
        {
            EventType = EventType.Session,
            UserName = _userName,
            SessionId = _sessionId,
            SessionAction = action
        });

        _logger.LogInformation("Session event: {Action}", action);
    }

    public void Dispose()
    {
        if (_window is not null)
        {
            NativeMethods.WTSUnRegisterSessionNotification(_window.Handle);
            _window.DestroyHandle();
        }
    }

    /// <summary>
    /// A minimal NativeWindow that receives WM_WTSSESSION_CHANGE messages.
    /// </summary>
    private sealed class SessionNotificationWindow : System.Windows.Forms.NativeWindow
    {
        private readonly Action<int> _onSessionChange;

        public SessionNotificationWindow(Action<int> onSessionChange)
        {
            _onSessionChange = onSessionChange;
            var cp = new System.Windows.Forms.CreateParams
            {
                Caption = "BigBroSessionMonitor",
                // HWND_MESSAGE parent for a message-only window
                Parent = new IntPtr(-3)
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == NativeMethods.WM_WTSSESSION_CHANGE)
            {
                _onSessionChange(m.WParam.ToInt32());
            }
            base.WndProc(ref m);
        }
    }
}
