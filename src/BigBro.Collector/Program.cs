using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BigBro.Collector.Collectors;
using BigBro.Collector.Interop;
using BigBro.Collector.Pipeline;
using BigBro.Common.Config;
using BigBro.Common.Data;
using Microsoft.Extensions.Logging;

namespace BigBro.Collector;

internal static class Program
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BigBro", "config", "appsettings.json");

    private static readonly string DefaultDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BigBro", "data");

    [STAThread]
    static void Main(string[] args)
    {
        // Prevent duplicate instances in the same session
        using var mutex = new Mutex(true, $"Global\\BigBroCollector_Session{GetSessionId()}", out bool createdNew);
        if (!createdNew)
        {
            return; // Another instance already running
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddConsole(); // Visible in debug; no console allocated in WinExe release
        });

        var logger = loggerFactory.CreateLogger("BigBro.Collector");

        try
        {
            Run(loggerFactory, logger);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Collector crashed.");
            // Exit with non-zero so the watchdog knows we crashed
            Environment.ExitCode = 1;
        }
    }

    private static void Run(ILoggerFactory loggerFactory, ILogger logger)
    {
        var config = LoadConfig(logger);
        var dataPath = config.DataPath ?? DefaultDataPath;
        var dbPath = Path.Combine(dataPath, "events.db");

        var userName = Environment.UserName;
        var sessionId = GetSessionId();

        logger.LogInformation("BigBro Collector starting. User={User}, Session={Session}, DB={Db}",
            userName, sessionId, dbPath);

        // Initialize core components
        using var store = new SqliteStore(dbPath, loggerFactory.CreateLogger<SqliteStore>());
        var buffer = new EventBuffer();
        using var flusher = new EventFlusher(buffer, store,
            config.FlushIntervalSec, config.FlushBatchSize,
            loggerFactory.CreateLogger("EventFlusher"));
        flusher.Start();

        // Start collectors
        var browserUrlReader = new BrowserUrlReader(loggerFactory.CreateLogger("BrowserUrlReader"));

        using var windowTracker = new WindowTracker(buffer, browserUrlReader, userName, sessionId,
            loggerFactory.CreateLogger("WindowTracker"));
        windowTracker.Start();

        using var inputMonitor = new InputMonitor(buffer, userName, sessionId,
            config.InputAggregationIntervalSec,
            loggerFactory.CreateLogger("InputMonitor"));
        var inputHook = inputMonitor.Start();

        using var idleDetector = new IdleDetector(buffer, userName, sessionId,
            config.IdleThresholdMin,
            loggerFactory.CreateLogger("IdleDetector"));
        idleDetector.Start();

        using var sessionMonitor = new SessionMonitor(buffer, userName, sessionId,
            loggerFactory.CreateLogger("SessionMonitor"));
        sessionMonitor.Start();

        // Start heartbeat to service watchdog
        using var heartbeat = new HeartbeatClient(sessionId,
            () => flusher.TotalFlushed,
            loggerFactory.CreateLogger("HeartbeatClient"));
        heartbeat.Start();

        // Set up a periodic timer to verify hooks are still installed
        var hookCheckTimer = new System.Threading.Timer(_ =>
        {
            if (!inputHook.AreHooksInstalled())
            {
                logger.LogWarning("Input hooks were uninstalled. Reinstalling...");
                // Post a message to the main thread to reinstall
                // (hooks must be installed on the thread with the message pump)
                NativeMethods.PostThreadMessage(_mainThreadId, WM_REINSTALL_HOOKS, IntPtr.Zero, IntPtr.Zero);
            }
        }, null, 60_000, 60_000);

        logger.LogInformation("All collectors started. Entering message loop.");

        // Record the main thread ID for cross-thread communication
        _mainThreadId = NativeMethods.GetCurrentThreadId();

        // Run the Win32 message loop (required for SetWinEventHook and SetWindowsHookEx)
        RunMessageLoop(inputHook, logger);

        // Shutdown
        hookCheckTimer.Dispose();
        flusher.FlushRemaining();
        logger.LogInformation("Collector shut down. Total events flushed: {Count}", flusher.TotalFlushed);
    }

    private static uint _mainThreadId;
    private const uint WM_REINSTALL_HOOKS = 0x0400 + 1; // WM_USER + 1

    private static void RunMessageLoop(LowLevelInputHook inputHook, ILogger logger)
    {
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_REINSTALL_HOOKS)
            {
                logger.LogInformation("Reinstalling input hooks on message pump thread.");
                inputHook.Reinstall();
                continue;
            }

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private static AgentConfig LoadConfig(ILogger logger)
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var wrapper = JsonSerializer.Deserialize<JsonElement>(json);
                if (wrapper.TryGetProperty("BigBro", out var section))
                {
                    return JsonSerializer.Deserialize<AgentConfig>(section.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new AgentConfig();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load config from {Path}. Using defaults.", ConfigPath);
        }
        return new AgentConfig();
    }

    private static int GetSessionId()
    {
        // Process.SessionId gives us the Terminal Services session ID
        return Process.GetCurrentProcess().SessionId;
    }
}
