<#
.SYNOPSIS
    Shows everything the demo is running, in one view: the control plane, both agents and the portal
    (with their PIDs and ports), the SQL Server container, any application containers, and each
    agent's live application states. Read-only -- starts and stops nothing.

.EXAMPLE
    ./demo/demo-status.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$controlPlaneUrl = 'http://localhost:5293'
$portalUrl = 'http://localhost:5231'

function Get-WslcPath {
    $onPath = Get-Command wslc -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $installed = Join-Path $env:ProgramFiles 'WSL\wslc.exe'
    if (Test-Path -LiteralPath $installed) { return $installed }
    return $null
}

function Get-ListeningPort {
    param([int]$ProcessId)
    $conn = Get-NetTCPConnection -State Listen -OwningProcess $ProcessId -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) { return $conn.LocalPort }
    return $null
}

function Test-Url {
    param([string]$Url)
    try { Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 3 | Out-Null; return $true } catch { return $false }
}

function Write-Line {
    param([string]$Label, [string]$Value, [string]$Color = 'Gray')
    Write-Host ('  {0,-16}' -f $Label) -NoNewline
    Write-Host $Value -ForegroundColor $Color
}

$procs = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' }

Write-Host ""
Write-Host "enList demo status" -ForegroundColor Cyan
Write-Host "==================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Processes" -ForegroundColor White

$components = @(
    @{ Name = 'Control plane'; Match = '*Enlist.ControlPlane.dll*' },
    @{ Name = 'DEV-AGENT-01';  Match = '*enlist-agent.dll*DEV-AGENT-01*' },
    @{ Name = 'DEV-AGENT-02';  Match = '*enlist-agent.dll*DEV-AGENT-02*' },
    @{ Name = 'Portal';        Match = '*Enlist.Portal.dll*' }
)
foreach ($c in $components) {
    $p = $procs | Where-Object { $_.CommandLine -like $c.Match } | Select-Object -First 1
    if ($p) {
        $port = Get-ListeningPort -ProcessId $p.ProcessId
        $portText = if ($port) { " on :$port" } else { "" }
        Write-Line $c.Name ("running  pid $($p.ProcessId)$portText") 'Green'
    }
    else {
        Write-Line $c.Name 'not running' 'Red'
    }
}

Write-Host ""
Write-Host "Database (wslc)" -ForegroundColor White
$wslc = Get-WslcPath
if (-not $wslc) {
    Write-Line 'SQL Server' 'wslc not found' 'Red'
}
else {
    $inspect = & $wslc inspect enlist-demo-sql --format json 2>$null
    $json = ($inspect -join '') -replace '\0', ''
    if ($json -match '"Status":"(\w+)"') {
        $status = $matches[1]
        $color = if ($status -eq 'running') { 'Green' } else { 'Yellow' }
        Write-Line 'enlist-demo-sql' "$status  (127.0.0.1,14330)" $color
    }
    else {
        Write-Line 'enlist-demo-sql' 'does not exist' 'Red'
    }

    Write-Host ""
    Write-Host "Application containers (wslc)" -ForegroundColor White
    $list = (& $wslc list 2>$null) | Where-Object { $_ -match 'enlist-' -and $_ -notmatch 'enlist-demo-sql' }
    if ($list) {
        foreach ($line in $list) { Write-Host "  $($line -replace '\s{2,}', '  ')" -ForegroundColor Gray }
    }
    else {
        Write-Host "  (none running)" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Endpoints" -ForegroundColor White
$cpUp = Test-Url "$controlPlaneUrl/api/agents"
$cpText = if ($cpUp) { "$controlPlaneUrl  reachable" } else { "$controlPlaneUrl  no response" }
$cpColor = if ($cpUp) { 'Green' } else { 'Red' }
Write-Line 'Control plane' $cpText $cpColor
$portalUp = Test-Url "$portalUrl/"
$portalText = if ($portalUp) { "$portalUrl  reachable" } else { "$portalUrl  no response" }
$portalColor = if ($portalUp) { 'Green' } else { 'Red' }
Write-Line 'Portal' $portalText $portalColor

if ($cpUp) {
    Write-Host ""
    Write-Host "Applications per agent" -ForegroundColor White
    foreach ($agent in 'DEV-AGENT-01', 'DEV-AGENT-02') {
        try {
            $report = Invoke-RestMethod -UseBasicParsing -Uri "$controlPlaneUrl/api/agents/$agent/report/latest" -TimeoutSec 4
        }
        catch {
            Write-Line $agent 'no report' 'Yellow'
            continue
        }
        $apps = @($report.applications)
        if ($apps.Count -eq 0) {
            Write-Line $agent 'no applications' 'Yellow'
            continue
        }
        Write-Host "  $agent" -ForegroundColor Gray
        foreach ($app in $apps) {
            $running = @($app.services | Where-Object { $_.state -eq 'Running' }).Count
            $svc = @($app.services).Count
            $jobs = @($app.jobs).Count
            $iso = $app.isolationMode
            Write-Host ("      {0,-16} {1,-10} services {2}/{3} running, {4} job(s)" -f $app.name, $iso, $running, $svc, $jobs) -ForegroundColor DarkGray
        }
    }
}

Write-Host ""
