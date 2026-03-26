<#
.SYNOPSIS
    Installs the BigBro task mining agent as a Windows service.

.DESCRIPTION
    Copies binaries, creates directories, registers the Windows service,
    and configures it for automatic start with anti-tamper protections.

.PARAMETER SourcePath
    Path to the published binaries directory.

.PARAMETER UncExportPath
    Optional UNC path for automatic data export. If not specified,
    data stays local and must be collected via Collect-BigBroData.ps1.

.PARAMETER Uninstall
    Remove the BigBro service and clean up.

.EXAMPLE
    .\Install-BigBro.ps1 -SourcePath .\publish -UncExportPath "\\fileserver\bigbro-data$"

.EXAMPLE
    .\Install-BigBro.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [Parameter(ParameterSetName = "Install")]
    [string]$SourcePath = ".\publish",

    [Parameter(ParameterSetName = "Install")]
    [string]$UncExportPath = "",

    [Parameter(ParameterSetName = "Uninstall")]
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$ServiceName = "BigBroAgent"
$DisplayName = "BigBro Task Mining Agent"
$InstallDir = "C:\Program Files\BigBro"
$DataDir = "C:\ProgramData\BigBro"
$ConfigDir = "$DataDir\config"
$DataStoreDir = "$DataDir\data"

# Require elevation
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator."
    exit 1
}

if ($Uninstall) {
    Write-Host "Uninstalling BigBro..." -ForegroundColor Yellow

    # Stop and remove service
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -eq "Running") {
            Write-Host "  Stopping service..."
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 3
        }
        Write-Host "  Removing service..."
        sc.exe delete $ServiceName | Out-Null
    }

    # Remove install directory
    if (Test-Path $InstallDir) {
        Write-Host "  Removing $InstallDir..."
        Remove-Item $InstallDir -Recurse -Force
    }

    Write-Host "Uninstall complete." -ForegroundColor Green
    Write-Host "Note: Data directory $DataDir was preserved. Delete manually if not needed."
    exit 0
}

# --- Install ---

Write-Host "Installing BigBro Task Mining Agent" -ForegroundColor Cyan
Write-Host ""

# Validate source
$serviceExe = Join-Path $SourcePath "BigBro.Service.exe"
$collectorExe = Join-Path $SourcePath "BigBro.Collector.exe"

if (-not (Test-Path $serviceExe)) {
    Write-Error "BigBro.Service.exe not found in $SourcePath. Build with: dotnet publish src/BigBro.Service -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish"
    exit 1
}
if (-not (Test-Path $collectorExe)) {
    Write-Error "BigBro.Collector.exe not found in $SourcePath."
    exit 1
}

# Stop existing service if upgrading
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingSvc -and $existingSvc.Status -eq "Running") {
    Write-Host "  Stopping existing service for upgrade..."
    Stop-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 3
}

# Create directories
Write-Host "  Creating directories..."
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
New-Item -ItemType Directory -Path $DataStoreDir -Force | Out-Null

# Grant Users write access to the data directory.
# The collector process runs as the logged-in user (not SYSTEM), so it
# needs write permissions to create and update the SQLite database.
Write-Host "  Granting write access to data directory..."
$dataAcl = Get-Acl $DataStoreDir
$dataWriteRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "BUILTIN\Users", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
$dataAcl.AddAccessRule($dataWriteRule)
Set-Acl $DataStoreDir $dataAcl

# Copy binaries
Write-Host "  Copying binaries to $InstallDir..."
Copy-Item "$SourcePath\*" -Destination $InstallDir -Recurse -Force

# Create config if not exists
$configFile = "$ConfigDir\appsettings.json"
if (-not (Test-Path $configFile)) {
    Write-Host "  Creating default configuration..."
    $config = @{
        BigBro = @{
            InputAggregationIntervalSec = 30
            IdleThresholdMin = 5
            FlushIntervalSec = 5
            FlushBatchSize = 100
            DataPath = $DataStoreDir
            Export = @{
                Enabled = ($UncExportPath -ne "")
                IntervalHours = 4
                Mode = if ($UncExportPath) { "UncShare" } else { "LocalArchive" }
                UncPath = $UncExportPath
                MaxLocalDbSizeMb = 50
                CsvExport = $true
            }
            Watchdog = @{
                MaxRestartsPerWindow = 5
                RestartWindowMinutes = 10
                RestartDelaySec = 5
            }
            CollectorPath = "BigBro.Collector.exe"
        }
    }
    $config | ConvertTo-Json -Depth 5 | Set-Content $configFile -Encoding UTF8
} else {
    Write-Host "  Config exists, preserving: $configFile"
}

# Set directory permissions — standard users get read+execute on install dir
Write-Host "  Setting permissions..."
$acl = Get-Acl $InstallDir
$acl.SetAccessRuleProtection($true, $false) # Disable inheritance
$adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$userRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "BUILTIN\Users", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($adminRule)
$acl.AddAccessRule($systemRule)
$acl.AddAccessRule($userRule)
Set-Acl $InstallDir $acl

# Register service
$svcExePath = Join-Path $InstallDir "BigBro.Service.exe"

if (-not $existingSvc) {
    Write-Host "  Registering Windows service..."
    sc.exe create $ServiceName binPath= "`"$svcExePath`"" start= auto obj= LocalSystem DisplayName= "`"$DisplayName`"" | Out-Null
} else {
    Write-Host "  Updating existing service binary path..."
    sc.exe config $ServiceName binPath= "`"$svcExePath`"" | Out-Null
}

# Configure service recovery (restart on failure)
Write-Host "  Configuring service recovery..."
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# Set service description
sc.exe description $ServiceName "BigBro Task Mining Agent - Collects application usage data for process analysis." | Out-Null

# Restrict service permissions (prevent non-admin stop)
Write-Host "  Applying anti-tamper service DACL..."
# D: DACL
# (A;;CCLCSWRPWPDTLOCRRC;;;SY) - SYSTEM: full
# (A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA) - Admins: full
# (A;;CCLCSWLOCRRC;;;IU) - Interactive Users: query only (no stop/pause)
sc.exe sdset $ServiceName "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)" | Out-Null

# Start the service
Write-Host "  Starting service..."
Start-Service -Name $ServiceName

Write-Host ""
Write-Host "Installation complete!" -ForegroundColor Green
Write-Host "  Service: $ServiceName (running)"
Write-Host "  Binaries: $InstallDir"
Write-Host "  Config: $configFile"
Write-Host "  Data: $DataStoreDir"
if ($UncExportPath) {
    Write-Host "  Export: $UncExportPath (every 4 hours)"
} else {
    Write-Host "  Export: Local only. Use Collect-BigBroData.ps1 to retrieve data."
}
