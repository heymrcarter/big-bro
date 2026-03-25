# BigBro - Windows Task Mining Agent

BigBro is an enterprise task mining collection agent for Windows. It runs silently on company workstations and records how employees interact with applications — which apps they use, what documents they work on, and their activity patterns. This data enables business process discovery and optimization.

Comparable to commercial tools like skan.ai, Celonis Task Mining, or UiPath Task Mining, but self-hosted with no cloud infrastructure required.

## What It Collects

| Data | Method | Detail |
|------|--------|--------|
| Active application | Win32 `SetWinEventHook` | Process name, friendly app name, window title (contains document names) |
| Browser URLs | UI Automation | Address bar contents from Chrome, Edge, Firefox, Brave, Vivaldi |
| Keyboard activity | Low-level hook (`WH_KEYBOARD_LL`) | Key press **count** per 30-second interval (not a keylogger — no key content is recorded) |
| Mouse activity | Low-level hook (`WH_MOUSE_LL`) | Click count (left/right/middle) and scroll event count per 30-second interval |
| Idle periods | `GetLastInputInfo` polling | Detects when users are idle for 5+ minutes, records duration |
| Session events | `WM_WTSSESSION_CHANGE` | Login, logout, lock, unlock, remote connect/disconnect |

All events are timestamped in UTC and tagged with machine name, username, and session ID.

## Architecture

BigBro uses a **two-process design** to work around Windows Session 0 Isolation (services cannot access user desktops):

```
┌─────────────────────────────────────────────────────────┐
│  Session 0 (Service Session)                            │
│  ┌───────────────────────────────────────────────────┐  │
│  │  BigBro.Service.exe (Windows Service)             │  │
│  │  - Launches collectors into user sessions         │  │
│  │  - Watchdog: restarts crashed collectors          │  │
│  │  - Export scheduler: ships data to network share  │  │
│  └───────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────┤
│  Session 1+ (User Desktop Sessions)                     │
│  ┌───────────────────────────────────────────────────┐  │
│  │  BigBro.Collector.exe (hidden, per user session)  │  │
│  │  - Win32 hooks for window/input tracking          │  │
│  │  - UI Automation for browser URLs                 │  │
│  │  - Writes events to local SQLite database         │  │
│  │  - Sends heartbeats to service via named pipe     │  │
│  └───────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
           │
           ▼
   C:\ProgramData\BigBro\data\events.db
```

**BigBro.Service** runs as `LocalSystem` in Session 0. It uses `CreateProcessAsUser` to launch **BigBro.Collector** into each active user's desktop session. The collector runs the Win32 hooks (which require a message pump in the user's session) and writes events to a shared SQLite database. The service monitors collector health via named pipe heartbeats and relaunches it if it crashes.

## Prerequisites

### Build Machine

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (version 8.0.100 or later) — .NET 10 SDK also works
- Windows, macOS, or Linux (cross-compilation is supported via `EnableWindowsTargeting` in `Directory.Build.props`)
- Visual Studio 2022 (optional, for IDE development) or just the CLI

### Target Machines

- Windows 10 (1809+) or Windows 11, or Windows Server 2016+
- No .NET runtime required (published as self-contained)
- Admin access for installation
- Network share access (if using UNC export)

## Building

### Clone and Build

```powershell
git clone <repo-url> BigBro
cd BigBro

# Restore and build (debug)
dotnet build BigBro.sln

# Run the Collector standalone for development/testing (on a Windows machine):
dotnet run --project src/BigBro.Collector
```

### Publish for Deployment

Publish both executables as self-contained single files. This bundles the .NET runtime so target machines need no prerequisites.

```powershell
# Create a publish directory
mkdir publish

# Publish the service
dotnet publish src/BigBro.Service -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o publish

# Publish the collector into the same directory
dotnet publish src/BigBro.Collector -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o publish
```

After publishing, `publish/` should contain at minimum:
- `BigBro.Service.exe`
- `BigBro.Collector.exe`

### Verify the Build

```powershell
# Check both executables exist
ls publish\BigBro.Service.exe
ls publish\BigBro.Collector.exe
```

## Installation

### Quick Install (Interactive)

Run from an elevated (Administrator) PowerShell prompt on the target machine:

```powershell
# Without network export (data stays local, collect manually later)
.\tools\Install-BigBro.ps1 -SourcePath .\publish

# With automatic export to a network share
.\tools\Install-BigBro.ps1 -SourcePath .\publish -UncExportPath "\\fileserver\bigbro-data$"
```

### What the Installer Does

1. Copies `BigBro.Service.exe` and `BigBro.Collector.exe` to `C:\Program Files\BigBro\`
2. Creates data directories under `C:\ProgramData\BigBro\`
3. Generates `appsettings.json` in `C:\ProgramData\BigBro\config\`
4. Registers `BigBroAgent` as a Windows service (automatic start, LocalSystem)
5. Configures service recovery: auto-restart on failure (60-second delay)
6. Applies anti-tamper protections:
   - File permissions: standard users get read+execute only on the install directory
   - Service DACL: standard users can query service status but cannot stop or pause it
7. Starts the service

### Silent / Unattended Install

For deployment via SCCM, Intune, GPO, or other management tools:

```powershell
# Copy the publish directory and tools to target machines, then run:
powershell.exe -ExecutionPolicy Bypass -File "\\deploy-share\BigBro\tools\Install-BigBro.ps1" `
    -SourcePath "\\deploy-share\BigBro\publish" `
    -UncExportPath "\\fileserver\bigbro-data$"
```

### Manual Install (Without the Script)

If you prefer to install manually or need to customize:

```powershell
# 1. Create directories
mkdir "C:\Program Files\BigBro"
mkdir "C:\ProgramData\BigBro\config"
mkdir "C:\ProgramData\BigBro\data"

# 2. Copy binaries
copy .\publish\* "C:\Program Files\BigBro\"

# 3. Copy config (edit UncPath before copying if using network export)
copy .\config\appsettings.json "C:\ProgramData\BigBro\config\"

# 4. Register the service
sc.exe create BigBroAgent binPath= "\"C:\Program Files\BigBro\BigBro.Service.exe\"" start= auto obj= LocalSystem DisplayName= "\"BigBro Task Mining Agent\""

# 5. Configure auto-restart on failure
sc.exe failure BigBroAgent reset= 86400 actions= restart/60000/restart/60000/restart/60000

# 6. (Optional) Restrict service to prevent non-admin stop
sc.exe sdset BigBroAgent "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)"

# 7. Start the service
net start BigBroAgent
```

### Verifying Installation

```powershell
# Check service is running
Get-Service BigBroAgent

# Check the collector process is running in the user's session
Get-Process BigBro.Collector -ErrorAction SilentlyContinue

# Check that data is being written
ls "C:\ProgramData\BigBro\data\events.db"

# Query the database to verify events are being recorded (requires sqlite3)
sqlite3 "C:\ProgramData\BigBro\data\events.db" "SELECT COUNT(*) FROM events;"
sqlite3 "C:\ProgramData\BigBro\data\events.db" "SELECT * FROM events ORDER BY id DESC LIMIT 5;"
```

## Configuration

Configuration lives at `C:\ProgramData\BigBro\config\appsettings.json`. Changes are picked up on service restart.

```jsonc
{
  "BigBro": {
    // How often (seconds) to aggregate input activity into one event
    "InputAggregationIntervalSec": 30,

    // Minutes of no input before a user is considered idle
    "IdleThresholdMin": 5,

    // How often (seconds) to flush buffered events to SQLite
    "FlushIntervalSec": 5,

    // Max events per SQLite batch insert
    "FlushBatchSize": 100,

    // Where the SQLite database and archives are stored
    "DataPath": "C:\\ProgramData\\BigBro\\data",

    "Export": {
      // Enable/disable automatic export
      "Enabled": true,

      // Hours between export runs
      "IntervalHours": 4,

      // "UncShare" = push to network share; "LocalArchive" = rotate locally
      "Mode": "UncShare",

      // UNC path for network export (leave empty if using LocalArchive)
      "UncPath": "\\\\fileserver\\bigbro-data$",

      // Max database size (MB) before archiving/rotation
      "MaxLocalDbSizeMb": 50,

      // Export as CSV (true) in addition to database operations
      "CsvExport": true
    },

    "Watchdog": {
      // Max collector restarts allowed within the time window
      "MaxRestartsPerWindow": 5,

      // Time window (minutes) for restart limiting
      "RestartWindowMinutes": 10,

      // Delay (seconds) before restarting a crashed collector
      "RestartDelaySec": 5
    },

    // Filename of the collector executable (relative to service install dir)
    "CollectorPath": "BigBro.Collector.exe"
  }
}
```

### Configuration Tips

- **Reduce noise**: Increase `InputAggregationIntervalSec` to 60 or 120 for less granular but smaller data.
- **Longer idle threshold**: Set `IdleThresholdMin` to 10 or 15 if 5 minutes is too sensitive for your environment.
- **Large deployments**: Keep `MaxLocalDbSizeMb` at 50 and `IntervalHours` at 4 to avoid disk issues.
- **Config changes require a service restart**: `Restart-Service BigBroAgent` (from an admin prompt).

## Data Collection and Export

BigBro provides two methods to get data from endpoint machines to an analyst. You can use either or both depending on your infrastructure.

### Option A: Automatic UNC Share Export (Recommended)

If you have a Windows file server or network share available:

1. Create a shared folder, e.g., `\\fileserver\bigbro-data$` (the `$` hides it from casual browsing).
2. Grant the machine accounts (or `SYSTEM` / `Domain Computers`) write access to the share.
3. Set `Export.Mode` to `"UncShare"` and `Export.UncPath` to the share path in the config.
4. The service will automatically export new events as CSV files to the share every `IntervalHours` hours.
5. Exported files are named: `{MachineName}_{timestamp}_{batchId}.csv`

### Option B: Manual Collection via PowerShell

For environments without a dedicated file share, IT can pull data directly from machines using admin shares:

```powershell
# Collect from a list of machines
.\tools\Collect-BigBroData.ps1 -ComputerListFile .\machines.txt -OutputPath C:\BigBroAnalysis

# Or specify machines directly
.\tools\Collect-BigBroData.ps1 -ComputerNames "PC001","PC002","PC003" -OutputPath C:\BigBroAnalysis
```

**Requirements**: Run from a machine with domain admin (or local admin on targets) access. Uses `\\machine\C$\ProgramData\BigBro\data\` admin shares.

**Machine list file** (`machines.txt`) — one hostname per line:
```
PC001
PC002
WORKSTATION-JDOE
LAPTOP-ASMITH
```

The script copies all `.db` and `.csv` files from each machine, organized into subdirectories:
```
C:\BigBroAnalysis\
  PC001\
    events.db
  PC002\
    events.db
    archive\
      events_20260320_140000.db
```

### Merging Collected Data

After collecting databases from multiple machines, merge them into one file for analysis:

```powershell
# Merge all collected databases into one
.\tools\Merge-BigBroData.ps1 -InputPath C:\BigBroAnalysis -OutputFile C:\BigBroAnalysis\all_events.db

# Merge and also export as CSV (for Excel, Python pandas, etc.)
.\tools\Merge-BigBroData.ps1 -InputPath C:\BigBroAnalysis -OutputFile C:\BigBroAnalysis\all_events.db -ExportCsv
```

**Requires**: `sqlite3.exe` on PATH. Download the precompiled binary from [sqlite.org/download.html](https://www.sqlite.org/download.html) (look for "sqlite-tools-win-x64") and add it to your PATH.

## Data Schema

All events are stored in a single flat `events` table, designed for direct import into process mining tools:

| Column | Type | Description |
|--------|------|-------------|
| `id` | INTEGER | Auto-increment primary key |
| `timestamp_utc` | TEXT | ISO 8601 UTC timestamp |
| `machine_name` | TEXT | Computer hostname |
| `user_name` | TEXT | Windows username |
| `session_id` | INTEGER | Terminal Services session ID |
| `event_type` | INTEGER | 1=WindowChange, 2=InputActivity, 3=Session, 4=IdleState |
| `process_name` | TEXT | e.g., "chrome", "EXCEL", "WINWORD" |
| `app_display_name` | TEXT | e.g., "Google Chrome", "Microsoft Excel" |
| `window_title` | TEXT | Full title bar text (contains document names) |
| `url` | TEXT | Browser URL (Chrome/Edge/Firefox only) |
| `mouse_clicks` | INTEGER | Click count in the aggregation interval |
| `key_presses` | INTEGER | Key press count in the aggregation interval |
| `scroll_events` | INTEGER | Scroll event count in the aggregation interval |
| `session_action` | TEXT | Login, Logout, Lock, Unlock, RemoteConnect, RemoteDisconnect |
| `is_idle` | INTEGER | 1 = went idle, 0 = returned from idle |
| `idle_duration_sec` | REAL | How long the user was idle (set on return from idle) |
| `exported` | INTEGER | 0 = not yet exported, 1 = exported |
| `export_batch_id` | TEXT | Batch identifier for the export run |

### Example Queries

```sql
-- What applications does a user spend the most time in?
-- (count window-change events as a proxy for time-in-app)
SELECT app_display_name, COUNT(*) as switches
FROM events
WHERE event_type = 1 AND user_name = 'jdoe'
GROUP BY app_display_name
ORDER BY switches DESC;

-- What documents were opened in Excel?
SELECT DISTINCT window_title, MIN(timestamp_utc) as first_seen
FROM events
WHERE process_name = 'EXCEL' AND event_type = 1
ORDER BY first_seen;

-- What websites are visited most?
SELECT url, COUNT(*) as visits
FROM events
WHERE url IS NOT NULL AND event_type = 1
GROUP BY url
ORDER BY visits DESC
LIMIT 20;

-- Daily activity pattern (events per hour)
SELECT strftime('%H', timestamp_utc) as hour, COUNT(*) as events
FROM events
WHERE event_type = 2
GROUP BY hour
ORDER BY hour;

-- Idle time per user per day
SELECT user_name,
       date(timestamp_utc) as day,
       SUM(idle_duration_sec) / 60.0 as idle_minutes
FROM events
WHERE event_type = 4 AND is_idle = 0
GROUP BY user_name, day
ORDER BY day, user_name;
```

## Upgrading

To upgrade BigBro on a machine that already has it installed:

```powershell
# Rebuild and republish
dotnet publish src/BigBro.Service -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
dotnet publish src/BigBro.Collector -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish

# Re-run the installer (it detects the existing service and upgrades in place)
.\tools\Install-BigBro.ps1 -SourcePath .\publish
```

The installer will stop the running service, copy new binaries, and restart it. Configuration is preserved. No data is lost.

## Uninstalling

```powershell
# Run from an elevated PowerShell prompt
.\tools\Install-BigBro.ps1 -Uninstall
```

This stops the service, removes the service registration, and deletes the binaries from `C:\Program Files\BigBro\`. The data directory (`C:\ProgramData\BigBro\`) is intentionally **not deleted** so you can collect any remaining data. Delete it manually if no longer needed:

```powershell
Remove-Item "C:\ProgramData\BigBro" -Recurse -Force
```

## Troubleshooting

### Service won't start

Check the Windows Event Log:
```powershell
Get-EventLog -LogName Application -Source "BigBro*" -Newest 20
# Or in Event Viewer: Windows Logs > Application, filter by source "BigBroAgent"
```

Common causes:
- Missing `BigBro.Collector.exe` in the install directory (service expects it alongside itself)
- Config file parse error — validate JSON at `C:\ProgramData\BigBro\config\appsettings.json`

### Collector not running

```powershell
# Check if the process exists
Get-Process BigBro.Collector -ErrorAction SilentlyContinue

# If not, the watchdog should restart it. Check service logs for restart errors.
# If the watchdog has hit its restart limit (5 in 10 minutes), restart the service:
Restart-Service BigBroAgent
```

### No events in the database

1. Verify the collector is running (see above).
2. Check the database file exists: `Test-Path "C:\ProgramData\BigBro\data\events.db"`
3. Wait 30+ seconds — events are buffered and flushed every 5 seconds by default.
4. Query the database: `sqlite3 "C:\ProgramData\BigBro\data\events.db" "SELECT COUNT(*) FROM events;"`

### Export to UNC share not working

1. Verify the share path is accessible from the machine: `Test-Path "\\fileserver\bigbro-data$"`
2. The service runs as `SYSTEM` — ensure the computer account has write access to the share.
3. Check that `Export.Enabled` is `true` and `Export.Mode` is `"UncShare"` in the config.
4. Restart the service after config changes.

### Users can stop the service

The install script applies a restrictive DACL. Verify it was applied:
```powershell
sc.exe sdshow BigBroAgent
```
Expected output should contain `(A;;CCLCSWLOCRRC;;;IU)` — interactive users can only query, not control. If the DACL was not applied, re-run:
```powershell
sc.exe sdset BigBroAgent "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)"
```

### High CPU or memory usage

BigBro targets under 1% CPU and under 30 MB RAM combined. If you observe higher usage:
- Check if the database has grown very large (`C:\ProgramData\BigBro\data\events.db` over 100 MB). Enable export or reduce `MaxLocalDbSizeMb`.
- Increase `InputAggregationIntervalSec` to reduce event volume.
- Increase `FlushIntervalSec` to reduce write frequency.

## File Locations

| Path | Purpose |
|------|---------|
| `C:\Program Files\BigBro\` | Executables (read-only for standard users) |
| `C:\ProgramData\BigBro\config\appsettings.json` | Configuration |
| `C:\ProgramData\BigBro\data\events.db` | Active event database |
| `C:\ProgramData\BigBro\data\archive\` | Rotated/archived databases |

## Project Structure

```
BigBro/
├── BigBro.sln
├── src/
│   ├── BigBro.Common/           # Shared library (models, SQLite store, config, IPC)
│   ├── BigBro.Collector/        # User-session data collection process
│   │   ├── Collectors/          # WindowTracker, InputMonitor, IdleDetector, etc.
│   │   ├── Interop/             # Win32 P/Invoke declarations and hook wrappers
│   │   └── Pipeline/            # Event buffering and SQLite flushing
│   └── BigBro.Service/          # Windows Service supervisor
│       └── Exporters/           # UNC share and local archive exporters
├── tools/
│   ├── Install-BigBro.ps1       # Install/uninstall script
│   ├── Collect-BigBroData.ps1   # Pull data from machines via admin shares
│   └── Merge-BigBroData.ps1     # Merge multiple databases for analysis
└── config/
    └── appsettings.json         # Default configuration template
```

## Security Considerations

- **No key content is recorded.** The keyboard hook counts key presses per interval. Individual keystrokes are never captured or stored.
- **Browser URLs are captured.** This may include sensitive URLs (webmail, banking, healthcare). Plan for data handling policies accordingly.
- **Window titles are captured.** These often contain document names, email subjects, and other potentially sensitive information.
- **Data is stored unencrypted** in SQLite on the local disk and exported as plaintext CSV. Restrict access to the data directory and network share.
- **The service runs as LocalSystem**, which has broad privileges. This is required for `CreateProcessAsUser` to launch collectors into user sessions.
