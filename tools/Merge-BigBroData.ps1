<#
.SYNOPSIS
    Merges collected BigBro SQLite databases into a single consolidated database.

.DESCRIPTION
    Takes a directory of collected BigBro data (from Collect-BigBroData.ps1) and
    merges all events into a single SQLite database for analysis. Requires sqlite3.exe
    on the PATH or specify its location with -SqlitePath.

.PARAMETER InputPath
    Directory containing collected data (subdirectories per machine with .db files).

.PARAMETER OutputFile
    Path for the merged output database. Default: merged_events.db

.PARAMETER SqlitePath
    Path to sqlite3.exe. Defaults to "sqlite3" (assumes it's on PATH).

.PARAMETER ExportCsv
    Also export the merged data as a CSV file alongside the database.

.EXAMPLE
    .\Merge-BigBroData.ps1 -InputPath .\collected -OutputFile .\analysis\all_events.db -ExportCsv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputPath,

    [string]$OutputFile = "merged_events.db",

    [string]$SqlitePath = "sqlite3",

    [switch]$ExportCsv
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $InputPath)) {
    Write-Error "Input path not found: $InputPath"
    exit 1
}

# Test sqlite3 is available
try {
    $null = & $SqlitePath --version 2>&1
} catch {
    Write-Error "sqlite3 not found. Install it or specify -SqlitePath. Download: https://www.sqlite.org/download.html"
    exit 1
}

# Remove existing output
if (Test-Path $OutputFile) {
    Remove-Item $OutputFile -Force
}

# Create the merged database with the schema
$schema = @"
CREATE TABLE IF NOT EXISTS events (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_utc     TEXT    NOT NULL,
    machine_name      TEXT    NOT NULL,
    user_name         TEXT    NOT NULL,
    session_id        INTEGER NOT NULL,
    event_type        INTEGER NOT NULL,
    process_name      TEXT,
    app_display_name  TEXT,
    window_title      TEXT,
    url               TEXT,
    mouse_clicks      INTEGER DEFAULT 0,
    key_presses       INTEGER DEFAULT 0,
    scroll_events     INTEGER DEFAULT 0,
    session_action    TEXT,
    is_idle           INTEGER,
    idle_duration_sec REAL,
    exported          INTEGER DEFAULT 0,
    export_batch_id   TEXT,
    source_file       TEXT
);
CREATE INDEX IF NOT EXISTS idx_merged_timestamp ON events(timestamp_utc);
CREATE INDEX IF NOT EXISTS idx_merged_machine ON events(machine_name);
CREATE INDEX IF NOT EXISTS idx_merged_user ON events(user_name);
CREATE INDEX IF NOT EXISTS idx_merged_type ON events(event_type);
"@

$schema | & $SqlitePath $OutputFile

# Find all .db files
$dbFiles = Get-ChildItem -Path $InputPath -Filter "events*.db" -Recurse
Write-Host "Found $($dbFiles.Count) database file(s) to merge." -ForegroundColor Cyan

$totalEvents = 0

foreach ($db in $dbFiles) {
    $sourceLabel = $db.FullName.Replace($InputPath, "").TrimStart('\')
    Write-Host "  Merging: $sourceLabel ... " -NoNewline

    $attachSql = @"
ATTACH DATABASE '$($db.FullName.Replace("'","''"))' AS src;
INSERT INTO events (
    timestamp_utc, machine_name, user_name, session_id, event_type,
    process_name, app_display_name, window_title, url,
    mouse_clicks, key_presses, scroll_events,
    session_action, is_idle, idle_duration_sec,
    exported, export_batch_id, source_file
)
SELECT
    timestamp_utc, machine_name, user_name, session_id, event_type,
    process_name, app_display_name, window_title, url,
    mouse_clicks, key_presses, scroll_events,
    session_action, is_idle, idle_duration_sec,
    exported, export_batch_id, '$($sourceLabel.Replace("'","''"))'
FROM src.events;
DETACH DATABASE src;
"@

    try {
        $attachSql | & $SqlitePath $OutputFile

        # Count what we just added
        $count = ("SELECT changes();" | & $SqlitePath $OutputFile).Trim()
        $totalEvents += [int]$count
        Write-Host "$count events" -ForegroundColor Green
    } catch {
        Write-Host "FAILED: $_" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Merge complete. Total events: $totalEvents" -ForegroundColor Cyan
Write-Host "Output: $OutputFile"

# Optional CSV export
if ($ExportCsv) {
    $csvFile = [System.IO.Path]::ChangeExtension($OutputFile, ".csv")
    Write-Host "Exporting CSV to $csvFile ..."

    $csvSql = @"
.headers on
.mode csv
.output $($csvFile.Replace("'","''"))
SELECT * FROM events ORDER BY timestamp_utc;
.output stdout
"@

    $csvSql | & $SqlitePath $OutputFile
    Write-Host "CSV export complete." -ForegroundColor Green
}
