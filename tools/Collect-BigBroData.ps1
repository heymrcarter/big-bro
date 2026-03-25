<#
.SYNOPSIS
    Collects BigBro task mining data from remote Windows machines.

.DESCRIPTION
    Connects to target machines via admin shares (\\machine\C$) and copies
    the BigBro SQLite databases and CSV exports to a central location.
    Run from an admin workstation with domain admin or equivalent credentials.

.PARAMETER ComputerListFile
    Path to a text file with one hostname per line.

.PARAMETER ComputerNames
    Array of computer names to collect from.

.PARAMETER OutputPath
    Local or UNC path to save collected data. Defaults to .\collected

.PARAMETER SourceRelativePath
    Relative path under C$ where BigBro data lives. Default: ProgramData\BigBro\data

.EXAMPLE
    .\Collect-BigBroData.ps1 -ComputerListFile .\machines.txt -OutputPath \\fileserver\bigbro-analysis

.EXAMPLE
    .\Collect-BigBroData.ps1 -ComputerNames "PC001","PC002","PC003" -OutputPath C:\BigBroData
#>
[CmdletBinding()]
param(
    [Parameter(ParameterSetName = "File")]
    [string]$ComputerListFile,

    [Parameter(ParameterSetName = "Names")]
    [string[]]$ComputerNames,

    [string]$OutputPath = ".\collected",

    [string]$SourceRelativePath = "ProgramData\BigBro\data"
)

$ErrorActionPreference = "Continue"

# Resolve computer list
if ($ComputerListFile) {
    if (-not (Test-Path $ComputerListFile)) {
        Write-Error "Computer list file not found: $ComputerListFile"
        exit 1
    }
    $computers = Get-Content $ComputerListFile | Where-Object { $_.Trim() -ne "" }
} elseif ($ComputerNames) {
    $computers = $ComputerNames
} else {
    Write-Error "Specify -ComputerListFile or -ComputerNames."
    exit 1
}

Write-Host "Collecting BigBro data from $($computers.Count) machine(s)..." -ForegroundColor Cyan
Write-Host "Output: $OutputPath" -ForegroundColor Cyan
Write-Host ""

$totalFiles = 0
$failedMachines = @()

foreach ($computer in $computers) {
    $computer = $computer.Trim()
    Write-Host "[$computer] " -NoNewline

    $remotePath = "\\$computer\C`$\$SourceRelativePath"

    if (-not (Test-Path $remotePath -ErrorAction SilentlyContinue)) {
        Write-Host "SKIP - Path not accessible" -ForegroundColor Yellow
        $failedMachines += $computer
        continue
    }

    $destDir = Join-Path $OutputPath $computer
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null

    # Collect .db files (main + archives)
    $files = @()
    $files += Get-ChildItem -Path $remotePath -Filter "*.db" -Recurse -ErrorAction SilentlyContinue
    $files += Get-ChildItem -Path $remotePath -Filter "*.csv" -Recurse -ErrorAction SilentlyContinue

    if ($files.Count -eq 0) {
        Write-Host "No data files found" -ForegroundColor Yellow
        continue
    }

    $copied = 0
    foreach ($file in $files) {
        try {
            # Preserve subdirectory structure (e.g., archive/)
            $relativePath = $file.FullName.Substring($remotePath.Length).TrimStart('\')
            $destFile = Join-Path $destDir $relativePath
            $destFileDir = Split-Path $destFile -Parent
            New-Item -ItemType Directory -Path $destFileDir -Force | Out-Null

            Copy-Item $file.FullName -Destination $destFile -Force
            $copied++
        } catch {
            Write-Warning "  Failed to copy $($file.Name): $_"
        }
    }

    $totalFiles += $copied
    Write-Host "OK - $copied file(s) collected" -ForegroundColor Green
}

Write-Host ""
Write-Host "Collection complete." -ForegroundColor Cyan
Write-Host "  Total files: $totalFiles"
Write-Host "  Machines processed: $($computers.Count - $failedMachines.Count) / $($computers.Count)"

if ($failedMachines.Count -gt 0) {
    Write-Host "  Failed machines: $($failedMachines -join ', ')" -ForegroundColor Yellow
}
