using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BigBro.Common.Config;
using Microsoft.Extensions.Options;

namespace BigBro.Service;

/// <summary>
/// Launches BigBro.Collector.exe into active user sessions.
/// Uses CreateProcessAsUser to break out of Session 0 isolation.
/// </summary>
public sealed class CollectorLauncher
{
    private readonly ILogger<CollectorLauncher> _logger;
    private readonly AgentConfig _config;
    private readonly CollectorWatchdog _watchdog;
    private readonly ConcurrentDictionary<int, LaunchedCollector> _collectors = new();

    public CollectorLauncher(
        ILogger<CollectorLauncher> logger,
        CollectorWatchdog watchdog,
        IOptions<AgentConfig> config)
    {
        _logger = logger;
        _watchdog = watchdog;
        _config = config.Value;
    }

    public void LaunchForAllSessions()
    {
        foreach (var sessionId in GetActiveUserSessions())
        {
            LaunchForSession(sessionId);
        }
    }

    public void CheckForNewSessions()
    {
        foreach (var sessionId in GetActiveUserSessions())
        {
            if (!_collectors.ContainsKey(sessionId))
            {
                LaunchForSession(sessionId);
            }
        }

        // Clean up sessions that no longer exist
        foreach (var kvp in _collectors)
        {
            if (!IsSessionActive(kvp.Key))
            {
                _logger.LogInformation("Session {SessionId} no longer active. Cleaning up.", kvp.Key);
                if (_collectors.TryRemove(kvp.Key, out var collector))
                {
                    TryKill(collector);
                }
            }
        }
    }

    public void LaunchForSession(int sessionId)
    {
        if (_collectors.ContainsKey(sessionId))
        {
            _logger.LogDebug("Collector already running for session {SessionId}.", sessionId);
            return;
        }

        try
        {
            var collectorExePath = ResolveCollectorPath();
            var pid = CreateProcessInSession(sessionId, collectorExePath);

            if (pid > 0)
            {
                var info = new LaunchedCollector
                {
                    ProcessId = pid,
                    SessionId = sessionId,
                    LaunchedAt = DateTime.UtcNow
                };
                _collectors[sessionId] = info;
                _watchdog.RegisterCollector(sessionId, pid);

                _logger.LogInformation("Launched collector PID {Pid} in session {SessionId}.", pid, sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch collector for session {SessionId}.", sessionId);
        }
    }

    public void RelaunchForSession(int sessionId)
    {
        if (_collectors.TryRemove(sessionId, out var old))
        {
            TryKill(old);
        }
        LaunchForSession(sessionId);
    }

    public void TerminateAll()
    {
        foreach (var kvp in _collectors)
        {
            TryKill(kvp.Value);
        }
        _collectors.Clear();
    }

    private int CreateProcessInSession(int sessionId, string exePath)
    {
        IntPtr userToken = IntPtr.Zero;
        IntPtr duplicatedToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            // Get the user token for the target session
            if (!WTSQueryUserToken((uint)sessionId, out userToken))
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogError("WTSQueryUserToken failed for session {SessionId}. Error: {Error}",
                    sessionId, error);
                return 0;
            }

            // Duplicate the token as a primary token
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = false
            };

            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, ref sa,
                SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                TOKEN_TYPE.TokenPrimary, out duplicatedToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }

            // Create the user's environment block
            if (!CreateEnvironmentBlock(out envBlock, duplicatedToken, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");
            }

            // Set up startup info
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
                dwFlags = STARTF_USESHOWWINDOW,
                wShowWindow = SW_HIDE
            };

            var creationFlags = CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT;

            if (!CreateProcessAsUser(
                duplicatedToken,
                exePath,
                null,
                ref sa, ref sa,
                false,
                creationFlags,
                envBlock,
                Path.GetDirectoryName(exePath),
                ref si,
                out PROCESS_INFORMATION pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);

            return (int)pi.dwProcessId;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (duplicatedToken != IntPtr.Zero) CloseHandle(duplicatedToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    private string ResolveCollectorPath()
    {
        var servicePath = AppContext.BaseDirectory;
        var collectorPath = Path.Combine(servicePath, _config.CollectorPath);

        if (!File.Exists(collectorPath))
            throw new FileNotFoundException($"Collector executable not found at {collectorPath}");

        return collectorPath;
    }

    private static void TryKill(LaunchedCollector collector)
    {
        try
        {
            var proc = Process.GetProcessById(collector.ProcessId);
            if (!proc.HasExited)
                proc.Kill();
        }
        catch { }
    }

    private static List<int> GetActiveUserSessions()
    {
        var sessions = new List<int>();
        IntPtr pSessions = IntPtr.Zero;

        try
        {
            if (WTSEnumerateSessions(WTS_CURRENT_SERVER_HANDLE, 0, 1, out pSessions, out uint count))
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                var current = pSessions;

                for (uint i = 0; i < count; i++)
                {
                    var session = Marshal.PtrToStructure<WTS_SESSION_INFO>(current);
                    // Only interested in active sessions (not the service session 0)
                    if (session.State == WTS_CONNECTSTATE_CLASS.WTSActive && session.SessionId > 0)
                    {
                        sessions.Add(session.SessionId);
                    }
                    current = IntPtr.Add(current, size);
                }
            }
        }
        finally
        {
            if (pSessions != IntPtr.Zero)
                WTSFreeMemory(pSessions);
        }

        return sessions;
    }

    private static bool IsSessionActive(int sessionId)
    {
        return GetActiveUserSessions().Contains(sessionId);
    }

    private sealed class LaunchedCollector
    {
        public int ProcessId { get; init; }
        public int SessionId { get; init; }
        public DateTime LaunchedAt { get; init; }
    }

    #region P/Invoke

    private static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, int Reserved, int Version,
        out IntPtr ppSessionInfo, out uint pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
        TOKEN_TYPE TokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string? lpCommandLine,
        ref SECURITY_ATTRIBUTES lpProcessAttributes,
        ref SECURITY_ATTRIBUTES lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_HIDE = 0;

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation
    }

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1, TokenImpersonation
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected,
        WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPStr)]
        public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    #endregion
}
