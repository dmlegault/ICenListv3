<#
.SYNOPSIS
    Starts (or restarts) the demo fleet: the demo SQL Server on wslc, the control plane on :5293
    pointed at it, the two demo agents from .demo/, and the portal on :5231.

.DESCRIPTION
    Idempotent. Any control plane already on :5293 and any running demo agent are stopped first --
    a second agent with the same name silently corrupts that agent's reports, so a stale one is
    never left behind. Logs go to .demo/logs/.

    The control plane runs as Production, which means it verifies the schema and refuses to start
    on a pending migration rather than applying it. Apply migrations first with
    `dotnet ef database update --project src/Enlist.ControlPlane` (with ConnectionStrings__ControlPlane
    set to `demo/sql-server/demo-db.ps1 connstring`).

.PARAMETER ContainerEngine
    Engine for DEV-AGENT-02's container application: wslc (default) or docker.

.PARAMETER JoinToken
    Enroll both agents on this run: the token is passed as --join-token, each agent exchanges it for
    its own credential (stored DPAPI-protected at .demo/agent-0N/credential) and never needs it again.
    Mint one with `dotnet src/Enlist.ControlPlane/bin/Debug/net10.0/Enlist.ControlPlane.dll create-join-token`
    with ConnectionStrings__ControlPlane set to `demo/sql-server/demo-db.ps1 connstring`. Optional: the
    demo control plane runs with authentication off, so the agents work without a credential too.

.PARAMETER Stop
    Stop the demo processes and return. The SQL Server keeps running; `demo/sql-server/demo-db.ps1 down` stops it.

.EXAMPLE
    ./demo/start-demo.ps1
    ./demo/start-demo.ps1 -Stop
#>
[CmdletBinding()]
param(
    [ValidateSet('wslc', 'docker')]
    [string]$ContainerEngine = 'wslc',

    [string]$JoinToken,

    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..')).Path
$controlPlaneUrl = 'http://localhost:5293'
$logs = Join-Path $repo '.demo\logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

function Stop-DemoProcesses {
    # The control plane, both agents and the portal are all dotnet.exe processes this script started;
    # each is matched by the DLL AND the demo's own arguments on its command line. The arguments are
    # not optional: the test suite spawns real Enlist.ControlPlane.dll processes on other ports, and a
    # match on the DLL name alone killed nine of them mid-run. Name AND command line, because a
    # command-line match alone would also catch whatever shell is running this script.
    $demo = [ordered]@{
        'control plane' = '*Enlist.ControlPlane.dll*localhost:5293*'
        'agent'         = '*enlist-agent.dll*localhost:5293*'
        'portal'        = '*Enlist.Portal.dll*localhost:5231*'
    }
    foreach ($label in $demo.Keys) {
        foreach ($p in (Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like $demo[$label] })) {
            Stop-Process -Id $p.ProcessId -Force
            Write-Host "  stopped $label (pid $($p.ProcessId))"
        }
    }
}

function Start-Background {
    param([string]$Name, [string[]]$Arguments)
    $out = Join-Path $logs "$Name.log"
    $err = Join-Path $logs "$Name.err.log"
    # Each child runs in its own hidden `cmd /c`, which does its own redirection, rather than through
    # Start-Process -NoNewWindow. -NoNewWindow shares the parent's console and leaks its inheritable
    # handles to the child, so when this script's own output is read through a pipe (a caller doing
    # `... | something`, or CI), the child holds the pipe open for its whole life and the caller never
    # sees EOF. A separate hidden console inherits none of that: the child is fully detached, the
    # caller gets its prompt back, and the logs still land in files.
    $command = 'dotnet ' + ($Arguments -join ' ') + " > `"$out`" 2> `"$err`""
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $command -WorkingDirectory $repo -WindowStyle Hidden | Out-Null
}

function Get-States {
    param([string]$Agent)
    try {
        $json = (Invoke-WebRequest -UseBasicParsing -Uri "$controlPlaneUrl/api/agents/$Agent/report/latest" -TimeoutSec 5).Content
    }
    catch {
        return 'no report yet'
    }
    $counts = @{}
    foreach ($m in [regex]::Matches($json, '"state":"([A-Za-z]+)"')) { $counts[$m.Groups[1].Value] = 1 + [int]$counts[$m.Groups[1].Value] }
    if ($counts.Count -eq 0) { return 'no report yet' }
    return (($counts.Keys | Sort-Object | ForEach-Object { "$($counts[$_]) $_" }) -join ', ')
}

Write-Host "Stopping anything already running..."
Stop-DemoProcesses
if ($Stop) { Write-Host "Demo stopped. The SQL Server is still up; demo/sql-server/demo-db.ps1 down stops it."; return }

Write-Host "Demo SQL Server..."
$demoDb = Join-Path $repo 'demo\sql-server\demo-db.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $demoDb up | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The demo SQL Server did not start. Run demo/sql-server/demo-db.ps1 up to see why." }

# The standard ASP.NET override; the child processes inherit it. Only the control plane reads it.
$env:ConnectionStrings__ControlPlane = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $demoDb connstring | Select-Object -Last 1).Trim()

# Production on purpose: the control plane verifies the schema and refuses to start on a pending
# migration rather than silently applying one. Set explicitly (not just left to default) so a
# Development value left in this shell by a prior portal launch below cannot leak into it.
$env:ASPNETCORE_ENVIRONMENT = 'Production'
# Authentication off is permitted here, and only here, because the demo binds to loopback -- the hard
# rule in docs/03-architecture/Authentication-Design.md section 8. The control plane refuses this
# setting on any other address, so this line cannot be copied into a real deployment by accident.
$env:Authentication__Mode = 'Off'
Write-Host "Control plane on $controlPlaneUrl (Production, database on 127.0.0.1,14330, authentication off on loopback)..."
Start-Background -Name 'control-plane' -Arguments @("`"$(Join-Path $repo 'src\Enlist.ControlPlane\bin\Debug\net10.0\Enlist.ControlPlane.dll')`"", '--urls', $controlPlaneUrl)
$deadline = (Get-Date).AddSeconds(60)
$ready = $false
while ((Get-Date) -lt $deadline) {
    try { Invoke-WebRequest -UseBasicParsing -Uri "$controlPlaneUrl/api/agents" -TimeoutSec 3 | Out-Null; $ready = $true; break } catch { Start-Sleep -Seconds 1 }
}
if (-not $ready) {
    Get-Content (Join-Path $logs 'control-plane.log') -Tail 15 | ForEach-Object { Write-Host "  $_" }
    throw "The control plane did not answer on $controlPlaneUrl within 60s. A pending migration is the usual cause -- see the log above."
}

Write-Host "Agents (DEV-AGENT-02 on $ContainerEngine)..."
$image = if ($ContainerEngine -eq 'wslc') { 'enlist/runner:wslc' } else { 'enlist/runner:dev' }
$runnerBin = Join-Path $repo 'src\Enlist.Runner\bin\Debug\net10.0'
$legacyBin = Join-Path $repo 'src\Enlist.Runner.Legacy\bin\Debug\net472'
$agentDll = Join-Path $repo 'src\Enlist.Agent\bin\Debug\net10.0\enlist-agent.dll'
$common = @("`"$agentDll`"", '--control-plane', $controlPlaneUrl, '--runner-bin', "`"$runnerBin`"", '--legacy-runner-bin', "`"$legacyBin`"")
if ($JoinToken) {
    # First start only: an agent that already holds a credential ignores this (and says so in its log).
    $common += @('--join-token', $JoinToken)
    Write-Host "  enrolling both agents with the join token"
}
Start-Background -Name 'agent-01' -Arguments ($common + @('--agent', 'DEV-AGENT-01', '--data', "`"$(Join-Path $repo '.demo\agent-01')`""))
Start-Background -Name 'agent-02' -Arguments ($common + @('--agent', 'DEV-AGENT-02', '--container-image', $image, '--container-engine', $ContainerEngine, '--data', "`"$(Join-Path $repo '.demo\agent-02')`""))

Write-Host "Waiting for both agents to report..."
$deadline = (Get-Date).AddSeconds(90)
do {
    Start-Sleep -Seconds 5
    $s1 = Get-States 'DEV-AGENT-01'
    $s2 = Get-States 'DEV-AGENT-02'
} while ((Get-Date) -lt $deadline -and ($s1 -eq 'no report yet' -or $s2 -eq 'no report yet'))

$portalUrl = 'http://localhost:5231'
Write-Host "Portal on $portalUrl..."
# The portal reads the control plane over HTTP; it never touches SQL, so it needs only the base URL.
$env:ControlPlane__BaseUrl = $controlPlaneUrl
# Development, so static web assets (MudBlazor's CSS/JS) are served from the build output. Run from a
# plain `dotnet Enlist.Portal.dll` in any other environment they come back 200 but EMPTY, which leaves
# the page unstyled and crashes the Blazor circuit on the first MudBlazor JS interop call.
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Start-Background -Name 'portal' -Arguments @("`"$(Join-Path $repo 'src\Enlist.Portal\bin\Debug\net10.0\Enlist.Portal.dll')`"", '--urls', $portalUrl)
$deadline = (Get-Date).AddSeconds(40)
$portalReady = $false
while ((Get-Date) -lt $deadline) {
    try { Invoke-WebRequest -UseBasicParsing -Uri "$portalUrl/" -TimeoutSec 3 | Out-Null; $portalReady = $true; break } catch { Start-Sleep -Seconds 1 }
}

Write-Host ""
Write-Host "Demo is up." -ForegroundColor Green
Write-Host "  control plane  $controlPlaneUrl  (database 127.0.0.1,14330 on wslc)"
Write-Host "  DEV-AGENT-01   $s1"
Write-Host "  DEV-AGENT-02   $s2  (containers on $ContainerEngine)"
Write-Host "  portal         $portalUrl  $(if ($portalReady) { '' } else { '(not answering yet -- see .demo/logs/portal.log)' })"
Write-Host "  logs           $logs"
