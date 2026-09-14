#Requires -Version 5.1
<#
.SYNOPSIS
    A real install of enList, end to end, on this machine.

.DESCRIPTION
    Everything verify.ps1 -Live cannot reach, because it needs the components to actually RUN:
    a Server install over TLS, the schema applied, the portal's key minted and stored, both
    services started and answering HTTPS, an agent enrolled with a real join token against the
    control plane that issued it, and then all of it removed again.

    Driven through the BUNDLE and silently, so it exercises the same path a fleet uses -
    InstallPlan.FromVariables rebuilding the plan with no wizard, and the post-install steps
    running with nobody watching.

    Always uninstalls, in a finally. A machine left with three services and a database after a
    verification run is worse than no verification.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Thumbprint,
    [Parameter(Mandatory = $true)] [string] $Transcript,
    [string] $OutDir = (Join-Path $PSScriptRoot 'out'),

    # NOT LocalDB, and that is the whole point of running this rather than reading it.
    #
    # A LocalDB instance is PER USER. The schema is applied by the installer, running as the elevated
    # operator; the service then runs as a service account and looks for an instance belonging to
    # somebody else. It finds nothing, and the control plane refuses to start with "Cannot connect to
    # the control plane database" - which is exactly what happened the first time this was run for
    # real, when LocalDB was still the installer's default. It no longer is: the packages have no
    # default server, the wizard blocks LocalDB, and the control plane refuses it as a service.
    [string] $DatabaseServer = '.\SQLEXPRESS',

    # LocalSystem rather than the NETWORK SERVICE default, because SYSTEM is the service account that
    # has a login on a default SQL Server Express install. Named here rather than assumed, because the
    # database grant below has to name the same account.
    [string] $ServiceAccount = 'LocalSystem',

    # Trust the test certificate MACHINE-WIDE for the duration of this run, and put the trust store
    # back afterwards.
    #
    # Needed because the agent runs as LocalSystem and consults LocalMachine\Root, while a client
    # running as the operator consults CurrentUser\Root - two different stores, one certificate. A
    # self-signed development certificate trusted only by the user is exactly the state that makes
    # the agent refuse, correctly, with Runbook 3.22's message.
    #
    # Off by default and removed in the finally either way. A verification run that permanently adds
    # a self-signed certificate to a machine's trusted roots would be a poor trade for a green tick.
    [switch] $TrustCertificate,

    # The engine the agent is installed with, so an application can be deployed to it in a container
    # as well as as a process. wslc by default, as the wizard defaults: it answered LocalSystem at every
    # sample after a reboot, while Docker Desktop never started without someone signing in and starting
    # it (tools\probe-container-engines.ps1 -ArmStartupProbe).
    #
    # Either way nobody side-loads the image: the runner image download sits beside the setup in out\,
    # the installer copies it into the agent's Images folder, and the AGENT loads it into its engine as
    # its own account. That matters for wslc, whose image store is per account - an image loaded in this
    # elevated shell would be in the operator's store, where a LocalSystem agent never looks. So wslc's
    # store is asked as SYSTEM here, which is also why a wslc run needs the agent to be LocalSystem.
    [ValidateSet('docker', 'wslc')] [string] $ContainerEngine = 'wslc',

    # Deploy as a process only. For a machine without the container engine - said in the transcript,
    # because the container half of the product is then simply not being tested.
    [switch] $SkipContainers
)

$ErrorActionPreference = 'Continue'
$script:failures = 0
$script:step = 0

# Written before anything else can go wrong, so an empty transcript means the process never started
# rather than that it started and said nothing. The first attempt at this produced a console window
# that opened and closed with no output at all, which is indistinguishable from UAC being dismissed.
"started $(Get-Date -Format s) as $env:USERNAME, elevated=$(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)), pid=$PID" |
    Out-File -FilePath $Transcript -Encoding utf8   # no -Append: a fresh run starts a fresh transcript,
                                                    # because two runs in one file read as one confusing run

trap {
    "TRAP: $($_.Exception.Message)" | Out-File -FilePath $Transcript -Encoding utf8 -Append
    $_.ScriptStackTrace | Out-File -FilePath $Transcript -Encoding utf8 -Append
    continue
}

function Say([string] $text) {
    $text | Out-File -FilePath $Transcript -Encoding utf8 -Append
}

function Phase([string] $text) {
    $script:step++
    Say ""
    Say "=== $script:step. $text"
}

function Check([bool] $condition, [string] $what, [string] $detail = '') {
    if ($condition) {
        Say "    ok    $what"
    }
    else {
        Say "    FAIL  $what$(if ($detail) { "  [$detail]" })"
        $script:failures++
    }
}

$setup = (Get-ChildItem (Join-Path $OutDir '*Setup.exe') | Select-Object -First 1).FullName
$productVersion = [regex]::Match([string] $setup, 'enList-(.+)-Setup\.exe$').Groups[1].Value
$runnerImage = "enlist/runner:$productVersion"
$runnerImageFile = Join-Path $OutDir "enlist-runner-$productVersion.tar"
$repoRoot = Split-Path $PSScriptRoot -Parent
$cpDir = Join-Path $env:ProgramFiles 'enList\ControlPlane'
$portalDir = Join-Path $env:ProgramFiles 'enList\Portal'
$agentDir = Join-Path $env:ProgramFiles 'enList\Agent'
$agentData = Join-Path $env:ProgramData 'enList\Agent'
$agentImages = Join-Path $agentData 'Images'
$wslcExe = Join-Path $env:ProgramFiles 'WSL\wslc.exe'
$cpUrl = 'https://localhost:5293'

Say "enList live end-to-end, $(Get-Date -Format s)"
Say "  bundle      : $setup"
Say "  certificate : $Thumbprint"
Say "  engine      : $(if ($SkipContainers) { '(none - process only)' } else { $ContainerEngine })"

# TLS trust is reported rather than assumed. The certificate here is a development one, and whether
# this machine trusts its issuer is a fact about the machine, not about the install.
Add-Type @'
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class TlsProbe {
    public static SslPolicyErrors LastErrors = SslPolicyErrors.None;
    public static void Accept() {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.ServerCertificateValidationCallback =
            delegate(object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { LastErrors = e; return true; };
    }
}
'@ -ErrorAction SilentlyContinue
[TlsProbe]::Accept()

function Wait-Http([string] $url, [int] $seconds = 90) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
            return $response
        }
        catch {
            $inner = $_.Exception
            if ($inner.Response -and $inner.Response.StatusCode) { return $inner.Response }
            Start-Sleep -Milliseconds 1500
        }
    }
    return $null
}

function Service-Of([string] $name) {
    Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
}

<#
  Starts a service WITHOUT waiting for it to settle, which is the opposite of what Start-Service does
  and the reason this script would not stop.

  Every enList service is configured to restart on failure (util:ServiceConfig), so a service that
  cannot start does not fail - it crashes, waits sixty seconds, and crashes again, for ever.
  Start-Service sits in that loop. The control plane refusing to start because it cannot reach its
  database is precisely that case, and it turned a clear failure into a script nobody could get out
  of except by killing it.

  sc.exe returns as soon as the SCM accepts the request. Whether the thing actually came up is then
  answered by asking it over HTTP, which is the question worth asking anyway.
#>
function Request-ServiceStart([string] $name) {
    & sc.exe start $name *>$null
}

<#
  Stops a service and its restart-on-failure loop. `sc stop` alone races the recovery action, so the
  recovery is disarmed first - otherwise the uninstall meets a service that keeps coming back.
#>
function Request-ServiceStop([string] $name) {
    if (-not (Service-Of $name)) { return }
    & sc.exe failure $name reset= 0 actions= "" *>$null
    & sc.exe stop $name *>$null

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $state = (Service-Of $name).State
        if (-not $state -or $state -eq 'Stopped') { return }
        Start-Sleep -Milliseconds 500
    }
}

<#
  Runs the bundle and waits for THE BUNDLE, which is not what Start-Process -Wait does.

  -Wait waits for the process TREE, and an MSI install leaves msiexec.exe alive for about ten
  minutes afterwards before it idles out. The bundle itself exits in seconds. Using -Wait made the
  first successful run of this script look like a hang: setup.exe had finished, the install had
  worked, every post-install step had run, and PowerShell sat there waiting for a service process
  that had nothing to do with it.

  $p.Handle is touched before waiting on purpose. A Process object from Start-Process -PassThru does
  not cache the process handle, and reading ExitCode after the process has gone then throws
  "process has exited, so the requested information is not available".
#>
function Invoke-Bundle([string[]] $arguments, [int] $timeoutSeconds = 600) {
    $p = Start-Process -FilePath $setup -ArgumentList $arguments -PassThru
    $null = $p.Handle

    if (-not $p.WaitForExit($timeoutSeconds * 1000)) {
        Say "    (the bundle did not exit within $timeoutSeconds seconds; killing it)"
        try { $p.Kill() } catch { }
        return -1
    }

    return $p.ExitCode
}

# ---- Talking to the installed control plane as an Operator ----------------------------------------
$script:apiKey = ''
$script:policyIds = @{}

function Invoke-Api([string] $method, [string] $path, $body = $null) {
    $request = @{ Method = $method; Uri = "$cpUrl$path"; Headers = @{ Authorization = "Bearer $script:apiKey" }; TimeoutSec = 30 }
    if ($null -ne $body) {
        $request.Body = ($body | ConvertTo-Json -Depth 8)
        $request.ContentType = 'application/json'
    }
    Invoke-RestMethod @request
}

# The agent's last report as the control plane holds it, or $null.
function Get-AgentReport {
    try { Invoke-Api GET "/api/agents/$env:COMPUTERNAME/report/latest" } catch { $null }
}

function Get-ReportedApplication([string] $name) {
    $report = Get-AgentReport
    if (-not $report) { return $null }
    @($report.applications) | Where-Object { $_.name -eq $name } | Select-Object -First 1
}

<#
  Waits until the application AND its "Sample Service" report Running. The application is marked
  Running the moment its runner starts; service states arrive afterwards, so waiting on the first alone
  would pass for a runner whose service never came up.
#>
function Wait-ApplicationRunning([string] $name, [int] $seconds = 150) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Get-ReportedApplication $name
        if ($last -and $last.state -eq 'Running' -and
            @(@($last.services) | Where-Object { $_.name -eq 'Sample Service' -and $_.state -eq 'Running' }).Count -gt 0) {
            return $last
        }
        Start-Sleep -Seconds 3
    }
    return $last
}

function Wait-ApplicationGone([string] $name, [int] $seconds = 90) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $app = Get-ReportedApplication $name
        if (-not $app -or $app.state -eq 'Stopped') { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

<#
  One rule per application for this agent, created or updated. The database survives an uninstall by
  design, so a rule from a previous run is still there - and a SECOND rule for the same application and
  agent that disagrees with the first is a conflict the agent reports as Failed. So an existing rule is
  updated in place rather than added to.
#>
function Set-Policy([string] $app, [string] $digest, $isolation) {
    # Assigned before it is filtered: Windows PowerShell's Invoke-RestMethod hands a JSON array back as ONE
    # object, and piping a variable is what enumerates it.
    $all = Invoke-Api GET '/api/application-policies'
    $existing = $all |
        Where-Object { $_.applicationName -eq $app -and $_.tagSelector -and $_.tagSelector.agent -eq $env:COMPUTERNAME } |
        Select-Object -First 1
    if ($existing) {
        $policy = Invoke-Api PUT "/api/application-policies/$($existing.id)" @{ packageDigest = $digest; desiredState = 'Running'; isolation = $isolation }
    }
    else {
        $policy = Invoke-Api POST '/api/application-policies' @{
            applicationName = $app; desiredState = 'Running'
            tagSelector = @{ agent = $env:COMPUTERNAME }
            packageDigest = $digest; isolation = $isolation
        }
    }
    $script:policyIds[$app] = $policy.id
    return $policy
}

<#
  Runs a command line as LocalSystem and returns what it printed. A scheduled task is the one way an
  elevated script can put a process in the account a service runs as, which is the only account whose
  answer matters for "can the agent use this".
#>
function Invoke-AsSystem([string] $name, [string] $commandLine, [int] $seconds = 60) {
    $dir = Join-Path $env:ProgramData 'enList\e2e-probe'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $cmdFile = Join-Path $dir "$name.cmd"
    $output = Join-Path $dir "$name.txt"
    if (Test-Path $output) { Remove-Item $output -Force }
    # The redirection goes FIRST on the exit line. `echo exit=%errorlevel%>> file` hands cmd "exit=1>>",
    # and cmd reads the 1 as the handle to redirect - so the file got "exit=" and the code was lost. And
    # not `echo exit=%errorlevel% >> file` either, which writes the space.
    Set-Content -Path $cmdFile -Encoding ASCII -Value "@echo off`r`n$commandLine > `"$output`" 2>&1`r`n>> `"$output`" echo exit=%errorlevel%`r`n"

    $task = "enlist-e2e-$name"
    & schtasks.exe /create /tn $task /ru SYSTEM /sc once /st 23:59 /f /tr $cmdFile *>$null
    & schtasks.exe /run /tn $task *>$null
    $deadline = (Get-Date).AddSeconds($seconds)
    $text = ''
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $output) {
            $text = Get-Content $output -Raw -ErrorAction SilentlyContinue
            if ($text -match 'exit=') { break }
        }
        Start-Sleep -Seconds 1
    }
    # Whatever it managed to print is kept even on a timeout - partial output is the evidence of a hang.
    if (-not $text -and (Test-Path $output)) { $text = Get-Content $output -Raw -ErrorAction SilentlyContinue }
    & schtasks.exe /end /tn $task *>$null
    & schtasks.exe /delete /tn $task /f *>$null
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    return [string] $text
}

<#
  Asks the container engine something AS THE AGENT'S ACCOUNT, which is the only answer that counts, and
  returns { Exit; Text }.

  Docker has one image store and its engine pipe grants SYSTEM, so the operator's docker sees exactly
  what the agent's does. wslc keeps a store per account, so it is asked as SYSTEM through a scheduled
  task: asking it in this shell would answer for the operator's store, and a check that looks in the
  wrong store passes or fails for reasons that have nothing to do with the agent.
#>
function Invoke-Engine([string] $name, [string[]] $arguments, [int] $seconds = 120) {
    if ($ContainerEngine -eq 'docker') {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return [pscustomobject] @{ Exit = -1; Text = 'docker is not on PATH' } }
        $text = & docker @arguments 2>&1 | Out-String
        return [pscustomobject] @{ Exit = $LASTEXITCODE; Text = $text.Trim() }
    }

    $quoted = $arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }
    $text = Invoke-AsSystem $name "`"$wslcExe`" $($quoted -join ' ')" $seconds
    $exit = [regex]::Match($text, 'exit=(-?\d+)\s*$')
    return [pscustomobject] @{
        Exit = $(if ($exit.Success) { [int] $exit.Groups[1].Value } else { -1 })   # -1: no answer in time
        Text = ($text -replace 'exit=-?\d+\s*$', '').Trim()
    }
}

# The JSON an engine's inspect printed, as one object - both engines print an array of one. Assigned
# first and walked with foreach: Windows PowerShell hands a JSON array back as ONE pipeline object.
function ConvertFrom-Inspect([string] $text) {
    try { $parsed = ConvertFrom-Json -InputObject $text } catch { return $null }
    foreach ($item in $parsed) { return $item }
    return $null
}

$script:trustAdded = $false

try {
    # ---------------------------------------------------------------------------------------------
    Phase "Start from a clean slate"

    if ($TrustCertificate) {
        $root = Get-Item "Cert:\LocalMachine\Root\$Thumbprint" -ErrorAction SilentlyContinue
        if ($root) {
            Say "    the certificate is already trusted machine-wide; leaving the trust store alone"
        }
        else {
            $source = Get-Item "Cert:\LocalMachine\My\$Thumbprint" -ErrorAction SilentlyContinue
            if (-not $source) {
                Say "    cannot trust $Thumbprint - it is not in LocalMachine\My"
            }
            else {
                $store = New-Object System.Security.Cryptography.X509Certificates.X509Store 'Root', 'LocalMachine'
                $store.Open('ReadWrite')
                $store.Add($source)
                $store.Close()
                $script:trustAdded = $true
                Say "    added $Thumbprint to LocalMachine\Root for this run - it is removed again at the end"
            }
        }
    }

    # Repeatable on purpose. This gets run several times while something is being got right, and an
    # install over an existing one proves something quite different from a first install - Burn would
    # detect the packages present and do nothing, and every assertion below would pass while testing
    # nothing at all.
    if (@(Get-Package -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^enList' }).Count -gt 0) {
        Say "    enList is already installed; removing it first"
        foreach ($name in "enlist-agent", "enlist-portal", "enlist-controlplane") { Request-ServiceStop $name }

        $exit = Invoke-Bundle @('/uninstall', '/quiet', '/norestart')
        Check ($exit -eq 0) "the previous install was removed" "exit $exit"
        Start-Sleep -Seconds 3
    }
    else {
        Say "    nothing installed"
    }

    Check ((Get-CimInstance Win32_Service -Filter "Name LIKE 'enlist-%'" -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) "no enList service to begin with"

    # The agent's Images folder goes too. ProgramData outlives an uninstall by design, so without this a
    # previous run's archive, and its record of having been loaded, would still be there - and "the
    # installer copied the image in" and "the agent loaded it" would pass on the last run's evidence.
    if (Test-Path $agentImages) {
        Remove-Item $agentImages -Recurse -Force -ErrorAction SilentlyContinue
        Check (-not (Test-Path $agentImages)) "the previous run's agent Images folder is removed"
    }

    # And the runner image leaves the engine's store for the agent's account, so that whatever runs in a
    # container below can only have been loaded by the agent from the file the installer copied in -
    # the agent never pulls. build.ps1 leaves the image in Docker; an earlier run, or the engine probe,
    # leaves it in SYSTEM's wslc store.
    if (-not $SkipContainers) {
        if ($ContainerEngine -eq 'wslc') {
            Check ($ServiceAccount -match '^(LocalSystem|NT AUTHORITY\\SYSTEM)$') "a wslc run installs the agent as LocalSystem, whose store is the one this script asks" "the agent would run as $ServiceAccount"
            Check (Test-Path $wslcExe) "wslc is installed ($wslcExe)"
        }
        $removed = Invoke-Engine 'image-rm' @('image', 'rm', '--force', $runnerImage)
        Say "    $ContainerEngine image rm $runnerImage (as the agent's account): exit $($removed.Exit) $(($removed.Text -split "`r?`n" | Select-Object -First 1))"
        $before = Invoke-Engine 'image-inspect' @('image', 'inspect', $runnerImage)
        Check ($before.Exit -ne 0 -and $before.Text -match 'No such image|not found') "$runnerImage is not in $ContainerEngine's store for the agent's account to begin with" "exit $($before.Exit): $(($before.Text -split "`r?`n" | Select-Object -Last 1))"
    }

    # ---------------------------------------------------------------------------------------------
    Phase "Install the Server role over TLS, silently, through the bundle"

    $arguments = @(
        '/quiet', '/norestart',
        "/log", (Join-Path $env:TEMP 'enlist-e2e-install.log'),
        'INSTALLTYPE=Server',
        "CP_CERT=$Thumbprint",
        "PORTAL_CERT=$Thumbprint",
        "PORTAL_CPURL=$cpUrl",
        "DB_SERVER=$DatabaseServer",
        "CP_ACCOUNT=$ServiceAccount",
        "PORTAL_ACCOUNT=$ServiceAccount"
    )
    Say "    $setup $($arguments -join ' ')"
    $exit = Invoke-Bundle $arguments
    Check ($exit -eq 0) "the bundle exits 0" "exit $exit"

    $cp = Service-Of 'enlist-controlplane'
    $portal = Service-Of 'enlist-portal'
    Check ($null -ne $cp) "the control plane service exists"
    Check ($null -ne $portal) "the portal service exists"

    if ($cp) {
        Check ($cp.State -eq 'Stopped') "it is created STOPPED, as designed" $cp.State
        Check ($cp.StartMode -eq 'Auto') "start type is Auto" $cp.StartMode
        # Quotes optional: MSI writes the value quoted, and asserting the bare form reported a FAIL
        # for a certificate that had arrived perfectly.
        # The quote is optional in this pattern because MSI writes the value quoted. Asserting the
        # bare form reported FAIL for a certificate that had arrived perfectly correctly.
        # Single-quoted: PowerShell does not escape with a backslash, so "\"" ends the string.
        Check ($cp.PathName -match ('--Certificate:Thumbprint="?' + [regex]::Escape($Thumbprint))) "the certificate reached the service command line"
        Check ($cp.PathName -notmatch 'JOINTOKEN|enlj_|Password') "no secret is on the command line"
    }

    # ---------------------------------------------------------------------------------------------
    Phase "The post-install steps actually ran"

    $settings = Join-Path $portalDir 'appsettings.json'
    Check (Test-Path $settings) "the portal has an appsettings.json"
    if (Test-Path $settings) {
        $raw = Get-Content $settings -Raw
        Check ($raw -match 'dpapi:') "the portal's key was minted and stored protected"
        Check ($raw -notmatch 'enlk_') "the key is NOT in the clear"
        Check ($raw -match '"Logging"') "the published logging configuration survived the merge"

        $acl = Get-Acl $settings
        Check ($acl.AreAccessRulesProtected) "its ACL is closed (inheritance off)"
        # Whichever account the services were told to run as - the grant is made for that one, so the
        # assertion has to look for that one rather than for the default.
        $expectedReader = if ($ServiceAccount -match 'LocalSystem|SYSTEM') { '*SYSTEM*' } else { '*' + ($ServiceAccount -split '\\')[-1] + '*' }
        Check (@($acl.Access | Where-Object { $_.IdentityReference -like $expectedReader }).Count -gt 0) "the portal's service account can read it"
    }

    # The schema: the control plane refuses to start without it, so this is proved by the start below
    # as well, but named here because a missing schema and a bad certificate look identical from
    # outside and this separates them.
    $dbOk = $false
    try {
        $connection = New-Object System.Data.SqlClient.SqlConnection "Server=$DatabaseServer;Database=EnlistControlPlane;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=15"
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT COUNT(*) FROM sys.tables"
        $dbOk = ([int]$command.ExecuteScalar()) -gt 0
        $connection.Close()
    }
    catch { Say "    (database probe: $($_.Exception.Message))" }
    Check $dbOk "apply-schema created and migrated the database"

    # ---------------------------------------------------------------------------------------------
    Phase "Give the service account rights on the database it will use"

    <#
      THE INSTALLER DOES NOT DO THIS, AND SHOULD NOT. apply-schema runs as the elevated operator,
      who has DDL rights; the service then connects as its own account, which has none. Granting
      database rights is a DBA's decision about a server the installer may not own - Deployment-IaC
      1.4 splits the work across three identities precisely so an installer never holds them all.

      So this is the step an operator performs, performed here, to prove the rest works once it has
      been. It is the one part of this script that is setup rather than verification.
    #>
    $login = if ($ServiceAccount -match '^LocalSystem$') { 'NT AUTHORITY\SYSTEM' } else { $ServiceAccount }
    $granted = $false
    try {
        $connection = New-Object System.Data.SqlClient.SqlConnection "Server=$DatabaseServer;Database=EnlistControlPlane;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=20"
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$login')
    CREATE LOGIN [$login] FROM WINDOWS;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$login')
    CREATE USER [$login] FOR LOGIN [$login];
ALTER ROLE db_owner ADD MEMBER [$login];
"@
        $command.ExecuteNonQuery() | Out-Null
        $connection.Close()
        $granted = $true
    }
    catch { Say "    (grant failed: $($_.Exception.Message.Split([Environment]::NewLine)[0]))" }

    Check $granted "$login can use the control plane database"

    # ---------------------------------------------------------------------------------------------
    Phase "Start the control plane and talk to it over TLS"

    Request-ServiceStart "enlist-controlplane"
    $health = Wait-Http "$cpUrl/health"
    Check ($null -ne $health) "it answers on $cpUrl"
    if ($health) {
        Check ([int]$health.StatusCode -eq 200) "/health returns 200" "$($health.StatusCode)"
        $body = if ($health.Content) { $health.Content } else { '' }
        Say "    /health: $body"
        Check ($body -match 'Required') "authentication reports Required"
        Say "    TLS chain errors seen by the client: $([TlsProbe]::LastErrors)"
    }
    else {
        $log = Join-Path $env:ProgramData 'enList\ControlPlane\Logs'
        if (Test-Path $log) { Say "    control plane log tail:"; Get-ChildItem $log -File | Sort-Object LastWriteTime -Desc | Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 20 | ForEach-Object { Say "      $_" } } }
        Get-EventLog -LogName Application -Newest 15 -ErrorAction SilentlyContinue |
            Where-Object { $_.Source -match 'enlist|\.NET Runtime|Application Error' } |
            ForEach-Object { Say "      [$($_.Source)] $(($_.Message -split "`n")[0])" }
    }

    Check ((Service-Of 'enlist-controlplane').State -eq 'Running') "the service is Running"

    # ---------------------------------------------------------------------------------------------
    Phase "Start the portal and talk to it over TLS"

    Request-ServiceStart "enlist-portal"
    $portalResponse = Wait-Http 'https://localhost:5231/' 60
    Check ($null -ne $portalResponse) "the portal answers on https://localhost:5231"
    if ($portalResponse) { Say "    portal status: $([int]$portalResponse.StatusCode)" }
    Check ((Service-Of 'enlist-portal').State -eq 'Running') "the portal service is Running"

    # ---------------------------------------------------------------------------------------------
    Phase "Mint a join token and enroll an agent against the running control plane"

    $env:ConnectionStrings__ControlPlane = "Server=$DatabaseServer;Database=EnlistControlPlane;Trusted_Connection=True;TrustServerCertificate=True;"
    $tokenOutput = & (Join-Path $cpDir 'Enlist.ControlPlane.exe') create-join-token --uses 1 2>&1 | Out-String
    $joinToken = ([regex]::Match($tokenOutput, 'enlj_\S+')).Value
    Check ($joinToken.Length -gt 5) "create-join-token produced a token"

    if ($joinToken) {
        # Adding a component to an installed bundle: the same setup.exe, with the agent switched on.
        # This is how an operator adds an agent to the box already hosting enList.
        $arguments = @(
            '/quiet', '/norestart',
            "/log", (Join-Path $env:TEMP 'enlist-e2e-agent.log'),
            'INSTALLTYPE=Server', 'InstallAgent=1',
            "CP_CERT=$Thumbprint", "PORTAL_CERT=$Thumbprint", "PORTAL_CPURL=$cpUrl",
            # The SAME settings as the first call, not just the new ones. A bundle variable not given
            # again falls back to its declared default, and the plan is rebuilt from those variables.
            # Post-install steps are now gated on the packages Burn actually executed, so this matters
            # less than it did - but a plan that disagrees with what is installed is still a plan that
            # would configure the wrong thing the moment a step did run.
            "DB_SERVER=$DatabaseServer", "CP_ACCOUNT=$ServiceAccount", "PORTAL_ACCOUNT=$ServiceAccount",
            "AGENT_CPURL=$cpUrl",
            "AGENT_ACCOUNT=$ServiceAccount",
            "AGENT_JOINTOKEN=$joinToken"
        )
        # The engine, and NOT the image: the image the agent gets is the bundle's own default,
        # enlist/runner:<version>, which is exactly the default an operator gets and so is the one worth
        # proving.
        if (-not $SkipContainers) { $arguments += "AGENT_ENGINE=$ContainerEngine" }

        # The live 'portal' key BEFORE the agent is added. Adding an agent used to re-run every
        # post-install step, and create-api-key --replace revoked the key the running portal held -
        # found in this very table after a run that otherwise passed. The portal reads its key once at
        # startup, so it would go on presenting a revoked one and get 401 from everything.
        function Get-LivePortalKeyCreated {
            try {
                $c = New-Object System.Data.SqlClient.SqlConnection "Server=$DatabaseServer;Database=EnlistControlPlane;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=15"
                $c.Open()
                $q = $c.CreateCommand()
                $q.CommandText = "SELECT TOP 1 CONVERT(varchar(40), CreatedAtUtc, 126) FROM ApiKeys WHERE Name = 'portal' AND RevokedAtUtc IS NULL"
                $v = $q.ExecuteScalar()
                $c.Close()
                return [string] $v
            }
            catch { return "" }
        }
        $portalKeyBefore = Get-LivePortalKeyCreated

        $exit = Invoke-Bundle $arguments
        Check ($exit -eq 0) "adding the agent exits 0" "exit $exit"

        $portalKeyAfter = Get-LivePortalKeyCreated
        Say "    live portal key created at: before $portalKeyBefore, after $portalKeyAfter"
        Check ($portalKeyBefore -and $portalKeyBefore -eq $portalKeyAfter) "ADDING THE AGENT LEFT THE RUNNING PORTAL'S KEY ALONE"

        $agent = Service-Of 'enlist-agent'
        Check ($null -ne $agent) "the agent service exists"
        if ($agent) {
            Check ($agent.PathName -notmatch 'enlj_') "THE JOIN TOKEN IS NOT IN THE SERVICE COMMAND LINE"
            if (-not $SkipContainers) {
                Check ($agent.PathName -match ('--container-engine "?' + [regex]::Escape($ContainerEngine))) "the agent is given the $ContainerEngine engine"
                Check ($agent.PathName -match ('--container-image "?' + [regex]::Escape($runnerImage))) "and the default image, $runnerImage" $agent.PathName
            }
        }

        $credential = Join-Path $agentData 'credential'
        Check (Test-Path $credential) "the agent enrolled and stored its credential"
        if (Test-Path $credential) {
            $bytes = [IO.File]::ReadAllBytes($credential)
            Check (-not ([Text.Encoding]::UTF8.GetString($bytes) -match 'enla_')) "the credential is encrypted at rest, not plain text"
            $acl = Get-Acl $credential
            Check ($acl.AreAccessRulesProtected) "its ACL is closed"
        }

        # The control plane's own view: an agent it has never heard of would not be registered.
        #
        # --replace, and a FAIL when there is no key rather than a skip. This block used to sit inside
        # `if ($apiKey)`, and the database survives an uninstall by design - so on the second run an
        # "e2e" key already existed, create-api-key refused a duplicate name, the key came back empty,
        # and the registration check silently did not run. The transcript said "all checks passed"
        # while the check that mattered most had not been attempted. A check that can vanish without
        # saying so is worse than no check.
        $env:ASPNETCORE_ENVIRONMENT = $null
        $keyOutput = & (Join-Path $cpDir 'Enlist.ControlPlane.exe') create-api-key --name e2e --role Operator --expires never --replace 2>&1 | Out-String
        $apiKey = ([regex]::Match($keyOutput, 'enlk_\S+')).Value
        $script:apiKey = $apiKey
        Check ($apiKey.Length -gt 5) "an Operator key was minted to ask the control plane with" ($keyOutput.Trim() -replace '\s+', ' ')

        $names = @()
        if ($apiKey) {
            try {
                $agents = Invoke-RestMethod -Uri "$cpUrl/api/agents" -Headers @{ Authorization = "Bearer $apiKey" } -TimeoutSec 20
                $names = @($agents | ForEach-Object { $_.name })
            }
            catch { Say "    (listing agents failed: $($_.Exception.Message))" }
        }
        Say "    agents the control plane knows: $(if ($names.Count) { $names -join ', ' } else { '(none returned)' })"
        Check ($names -contains $env:COMPUTERNAME) "the control plane has this agent registered"

        # The runner image, delivered the way a recipient's is: the download sits beside the setup (out\
        # is exactly what gets handed out), and the installer copies it into the agent's Images folder.
        # Nobody loads it into an engine - the agent does that itself, below, as its own account.
        if (-not $SkipContainers) {
            $imageName = Split-Path -Leaf $runnerImageFile
            Check (Test-Path $runnerImageFile) "the runner image download is beside the setup in out\ ($imageName)"
            $stagedImage = Join-Path $agentImages $imageName
            Check (Test-Path $stagedImage) "THE INSTALLER COPIED IT INTO THE AGENT'S IMAGES FOLDER, $stagedImage"
            if ((Test-Path $stagedImage) -and (Test-Path $runnerImageFile)) {
                Check ((Get-FileHash $stagedImage).Hash -eq (Get-FileHash $runnerImageFile).Hash) "byte for byte"
                Check (Test-Path "$stagedImage.sha256") "with its .sha256, which the agent checks the copy against"
            }

            <#
              Whoever can write to this folder chooses the image the agent loads - as LocalSystem - so it
              must not inherit ProgramData's "Users may create files". Enlist.Agent.msi locks it to SYSTEM
              and Administrators. verify.ps1 reads that from the MSI; this is the folder it actually made.
            #>
            if (Test-Path $agentImages) {
                $imagesAcl = Get-Acl $agentImages
                $who = @($imagesAcl.Access | ForEach-Object { [string] $_.IdentityReference })
                Check ($imagesAcl.AreAccessRulesProtected) "the Images folder's ACL is closed (inheritance off)"
                Check (($who -match 'SYSTEM').Count -gt 0 -and ($who -match 'Administrators').Count -gt 0) "it grants SYSTEM and Administrators" ($who -join ', ')
                Check (($who -match '\\Users$|Authenticated Users|Everyone|INTERACTIVE').Count -eq 0) "and no ordinary user can write an image into it" ($who -join ', ')
            }
        }

        # And the agent actually runs against it.
        Request-ServiceStart "enlist-agent"
        Start-Sleep -Seconds 12
        $agent = Service-Of 'enlist-agent'
        Check ($agent.State -eq 'Running') "the agent service is Running" $agent.State
        $agentLog = Join-Path $agentData 'Logs'
        if (Test-Path $agentLog) {
            Get-ChildItem $agentLog -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Desc | Select-Object -First 1 |
                ForEach-Object { Say "    agent log tail:"; Get-Content $_.FullName -Tail 8 | ForEach-Object { Say "      $_" } }
        }

        # -----------------------------------------------------------------------------------------
        Phase "Deploy a real application to the installed agent - as a process, then in a container"

        <#
          Everything above proves enList INSTALLS. This proves it WORKS: a package uploaded with the
          real deploy tool, a rule placing it on this agent, and the agent - a LocalSystem service,
          started by the SCM, using the runners and the image the installer and the download put there
          - actually running it. Until this phase existed every run ended with "Applications": [] and
          nothing had ever been deployed to an installed agent.

          The sample is deploy\SampleService from this repository, and the deploy tool is its own build:
          neither ships with the installer. Both are built by `dotnet build enList_v3.slnx`.
        #>
        $samplePackage = Join-Path $repoRoot 'deploy\SampleService'
        $deployTool = Join-Path $repoRoot 'src\Enlist.Deploy\bin\Debug\net10.0\enlist-deploy.dll'
        $haveSample = Test-Path (Join-Path $samplePackage 'Enlist.Sample.Service.dll')
        $haveTool = Test-Path $deployTool
        Check $haveSample "the sample package is built ($samplePackage)" 'run: dotnet build enList_v3.slnx'
        Check $haveTool "the deploy tool is built ($deployTool)" 'run: dotnet build enList_v3.slnx'

        if ($haveSample -and $haveTool -and $apiKey -and (Service-Of 'enlist-agent').State -eq 'Running') {
            # The key through the environment, as the tool supports - not as --api-key, where it would
            # be readable out of the process list for as long as the upload takes.
            $env:ENLIST_API_KEY = $apiKey
            $digest = ''
            foreach ($app in 'e2e-process', 'e2e-container') {
                if ($app -eq 'e2e-container' -and $SkipContainers) { continue }
                $deployed = & dotnet $deployTool --control-plane $cpUrl --app $app --source $samplePackage 2>&1 | Out-String
                $deployExit = $LASTEXITCODE
                $digest = [regex]::Match($deployed, 'digest:\s*([0-9a-f]{64})').Groups[1].Value
                Check ($deployExit -eq 0 -and $digest) "enlist-deploy uploaded the sample as $app, over TLS, with the Operator key" ($deployed.Trim() -replace '\s+', ' ')
            }
            $env:ENLIST_API_KEY = $null

            # ---- As a process ----
            $processPid = 0
            if ($digest) {
                try { $null = Set-Policy 'e2e-process' $digest @{ mode = 'process' }; $ruled = $true } catch { $ruled = $false; Say "    (rule failed: $($_.Exception.Message))" }
                Check $ruled "a rule places e2e-process on $env:COMPUTERNAME as a process"

                $running = Wait-ApplicationRunning 'e2e-process'
                Check ($running -and $running.state -eq 'Running') "THE AGENT RUNS IT: e2e-process is Running, with Sample Service Running" $(if ($running) { "state $($running.state)" } else { 'never reported' })
                if ($running) {
                    Check ($running.isolationMode -eq 'process' -or -not $running.isolationMode) "it runs as a process" $running.isolationMode
                    $processPid = [int] $running.pid
                    $runner = Get-CimInstance Win32_Process -Filter "ProcessId=$processPid" -ErrorAction SilentlyContinue

                    <#
                      NOT enlist-runner.exe by name, and that is by design: RunnerStaging copies the
                      installed runner into <data>\Runners\<app>\ and renames the executable to the
                      application's, so Task Manager shows "e2e-process.exe" rather than a column of
                      identical runners. The first version of this check expected the canonical name and
                      failed a runner that was exactly right. What is worth proving is that the process
                      is that staged copy, and that the copy is byte-for-byte the runner the installer
                      laid down.
                    #>
                    $staged = Join-Path $agentData 'Runners\e2e-process\e2e-process.exe'
                    Check ($null -ne $runner -and $runner.ExecutablePath -eq $staged) "its pid $processPid is the runner staged for it, $staged" $(if ($runner) { $runner.ExecutablePath } else { 'no such process' })
                    $installedRunner = Join-Path $agentDir 'runner\enlist-runner.exe'
                    if ((Test-Path $staged) -and (Test-Path $installedRunner)) {
                        Check ((Get-FileHash $staged).Hash -eq (Get-FileHash $installedRunner).Hash) "which is a copy of the installed runner, $installedRunner"
                    }
                    if ($runner) {
                        $owner = Invoke-CimMethod -InputObject $runner -MethodName GetOwner -ErrorAction SilentlyContinue
                        Check ($owner.User -eq 'SYSTEM') "started by the agent service, so running as SYSTEM" "$($owner.Domain)\$($owner.User)"
                    }
                }
            }

            # ---- In a container ----
            $containerId = ''
            if (-not $SkipContainers -and $digest) {
                # What the agent - as LocalSystem, not as the person running this - found when it probed
                # the engine. This is the question a desk test cannot answer.
                $engineSeen = $null
                $capDeadline = (Get-Date).AddSeconds(60)
                while ((Get-Date) -lt $capDeadline) {
                    try { $engineSeen = (Invoke-Api GET "/api/agents/$env:COMPUTERNAME").capabilities } catch { }
                    if ($engineSeen -and $engineSeen.containerEngine) { break }
                    Start-Sleep -Seconds 3
                }
                $engineOk = $engineSeen -and $engineSeen.containerEngine -and $engineSeen.containerEngine.available -eq $true
                $engineDetail = if ($engineSeen -and $engineSeen.containerEngine) { "engine $($engineSeen.containerEngine.engine) $($engineSeen.containerEngine.version) error: $($engineSeen.containerEngine.error)" } else { 'no capability reported' }
                Say "    the agent's container engine, as LocalSystem: $engineDetail"
                Check $engineOk "the agent service, as LocalSystem, can use $ContainerEngine" $engineDetail
                Check ($engineSeen -and @($engineSeen.isolationModes) -contains 'container') "it advertises container isolation" "$(@($engineSeen.isolationModes) -join ', ')"

                if ($engineOk) {
                    try { $null = Set-Policy 'e2e-container' $digest @{ mode = 'container' }; $ruled = $true } catch { $ruled = $false; Say "    (rule failed: $($_.Exception.Message))" }
                    Check $ruled "a rule places e2e-container on $env:COMPUTERNAME in a container"

                    $running = Wait-ApplicationRunning 'e2e-container'
                    Check ($running -and $running.state -eq 'Running') "THE AGENT RUNS IT IN A CONTAINER: e2e-container is Running, with Sample Service Running" $(if ($running) { "state $($running.state)" } else { 'never reported' })
                    if ($running) {
                        Check ($running.isolationMode -eq 'container') "it runs in a container" $running.isolationMode
                        $containerId = [string] $running.runtimeId
                        $shortId = $containerId.Substring(0, [Math]::Min(12, $containerId.Length))

                        # The image got into the engine because the AGENT loaded it from its Images folder:
                        # the clean slate took it out of the store, and nothing in this script put it back.
                        $imageName = Split-Path -Leaf $runnerImageFile
                        $record = Join-Path $agentImages 'loaded.json'
                        $recorded = if (Test-Path $record) { Get-Content $record -Raw } else { '' }
                        Check ($recorded -match [regex]::Escape($imageName) -and $recorded -match ('"Engine":\s*"' + $ContainerEngine + '"')) "THE AGENT LOADED THE IMAGE ITSELF: $record records $imageName loaded into $ContainerEngine" ($recorded -replace '\s+', ' ')
                        $said = @(Get-ChildItem (Join-Path $agentData 'Logs') -Filter 'agent-*.log' -File -ErrorAction SilentlyContinue |
                            ForEach-Object { Get-Content $_.FullName -ErrorAction SilentlyContinue } |
                            Where-Object { $_ -match "Runner image $([regex]::Escape($imageName)) (loaded|not loaded|:)" })
                        foreach ($line in $said) { Say "    agent log: $line" }
                        Check (@($said | Where-Object { $_ -match "loaded into $ContainerEngine" }).Count -gt 0) "and its log says so"

                        $imageNow = Invoke-Engine 'image-after' @('image', 'inspect', $runnerImage)
                        $imageInfo = ConvertFrom-Inspect $imageNow.Text
                        Check ($imageNow.Exit -eq 0 -and $null -ne $imageInfo) "$runnerImage is in $ContainerEngine's store for the agent's account now" "exit $($imageNow.Exit): $(($imageNow.Text -split "`r?`n" | Select-Object -Last 1))"

                        # Read as JSON rather than through --format, which both engines spell differently and
                        # whose quotes Windows PowerShell strips on the way to a native command.
                        $inspect = Invoke-Engine 'inspect' @('inspect', $containerId)
                        $container = ConvertFrom-Inspect $inspect.Text
                        Check ($null -ne $container -and $container.State.Running -eq $true) "container $shortId is running in $ContainerEngine" "exit $($inspect.Exit): $(($inspect.Text -split "`r?`n" | Select-Object -First 1))"
                        if ($container) {
                            # By name, or - wslc shows it this way once the tag has moved on - by the image's id.
                            $usedImage = @([string] $container.Config.Image, [string] $container.Image) | Where-Object { $_ }
                            Check ($usedImage -contains $runnerImage -or ($imageInfo -and $usedImage -contains [string] $imageInfo.Id)) "from $runnerImage, the image the agent loaded" ($usedImage -join ' / ')
                        }
                    }
                }
            }

            # ---- Taken away again ----
            foreach ($app in @($script:policyIds.Keys)) {
                try { Invoke-Api DELETE "/api/application-policies/$($script:policyIds[$app])" | Out-Null; $script:policyIds.Remove($app) } catch { Say "    (removing the $app rule failed: $($_.Exception.Message))" }
            }
            # Only for what actually ran: an application that never started is trivially "gone", and a
            # check that passes for that reason is not a check.
            if ($processPid -gt 0) {
                Check (Wait-ApplicationGone 'e2e-process') "removing the rule stops e2e-process"
                Start-Sleep -Seconds 2
                Check (-not (Get-Process -Id $processPid -ErrorAction SilentlyContinue)) "and its runner process is gone"
            }
            if ($containerId) {
                Check (Wait-ApplicationGone 'e2e-container') "removing the rule stops e2e-container"
                Start-Sleep -Seconds 2
                # Gone from the engine altogether, or still there but stopped. An engine that did not answer
                # is neither, and must not read as "not running".
                $after = Invoke-Engine 'inspect-after' @('inspect', $containerId)
                $stopped = ConvertFrom-Inspect $after.Text
                $removed = $after.Exit -ne 0 -and $after.Text -match 'No such|not found'
                Check ($removed -or ($null -ne $stopped -and $stopped.State.Running -ne $true)) "and its container is no longer running" "exit $($after.Exit): $(($after.Text -split "`r?`n" | Select-Object -First 1))"
            }
        }

        <#
          (A wslc-as-LocalSystem probe used to follow here, printed as a FINDING. Its one run reported
          "no answer within 3 minutes", which was read as wslc hanging for a service. It does not:
          installer\tools\probe-container-engines.ps1 -Wslc then ran version, image list, load, run and
          remove as SYSTEM in five seconds. What was true is that LocalSystem's wslc image store is its
          own and starts empty - which the agent's Images folder now answers, and the deploy above proves.)
        #>
    }
}
catch {
    Say ""
    Say "UNEXPECTED: $($_.Exception.Message)"
    Say $_.ScriptStackTrace
    $script:failures++
}
finally {
    # ---------------------------------------------------------------------------------------------
    Phase "Uninstall, and leave the machine as it was found"

    # Rules this run created and did not get to remove, while the control plane is still up to take them:
    # the database survives the uninstall, and a stale rule would place the sample on the next run's agent.
    foreach ($app in @($script:policyIds.Keys)) {
        try { Invoke-Api DELETE "/api/application-policies/$($script:policyIds[$app])" | Out-Null; Say "    removed the leftover $app rule" } catch { }
    }

    foreach ($name in "enlist-agent", "enlist-portal", "enlist-controlplane") { Request-ServiceStop $name }

    # A container the agent did not get to stop outlives the agent service - it belongs to the engine,
    # and for wslc to SYSTEM's session, which is where it is looked for.
    if (-not $SkipContainers) {
        $listed = if ($ContainerEngine -eq 'docker') {
            Invoke-Engine 'orphans' @('ps', '--all', '--quiet', '--filter', "label=enlist.agent=$env:COMPUTERNAME")
        } else {
            Invoke-Engine 'orphans' @('list', '--all', '--quiet', '--filter', "label=enlist.agent=$env:COMPUTERNAME")
        }
        $orphans = @(if ($listed.Exit -eq 0) { $listed.Text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[0-9a-f]{12,}$' } })
        foreach ($id in $orphans) { $null = Invoke-Engine 'orphan-rm' @('rm', '--force', $id) }
        if ($orphans.Count) { Say "    removed $($orphans.Count) container(s) the agent left behind" }
    }

    # Only if there is something to remove. This runs in a finally, so it runs even when the install
    # never happened - and uninstalling nothing exits 1, which reported a failure for the one thing
    # that had gone right. A cleanup step that cries wolf is worse than none.
    $registered = @(Get-Package -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^enList' }).Count
    $present = (Get-CimInstance Win32_Service -Filter "Name LIKE 'enlist-%'" -ErrorAction SilentlyContinue | Measure-Object).Count

    if ($registered -eq 0 -and $present -eq 0 -and -not (Test-Path (Join-Path $env:ProgramFiles 'enList'))) {
        Say "    nothing was installed, so there is nothing to remove"
    }
    else {
        $exit = Invoke-Bundle @("/uninstall", "/quiet", "/norestart", "/log", (Join-Path $env:TEMP "enlist-e2e-uninstall.log"))
        Check ($exit -eq 0) "the uninstall exits 0" "exit $exit"

        Start-Sleep -Seconds 3
        Check ((Get-CimInstance Win32_Service -Filter "Name LIKE 'enlist-%'" -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) "no enList service remains"
        Check (-not (Test-Path (Join-Path $env:ProgramFiles 'enList'))) "no binaries remain"
        Check (@(Get-Package -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^enList' }).Count -eq 0) "nothing is left registered"

        # ProgramData is Permanent by design - an operator's data outlives an uninstall.
        Check (Test-Path (Join-Path $env:ProgramData 'enList')) "ProgramData is retained, as designed"
    }

    # The trust store goes back exactly as it was. Done here rather than at the end of the happy path
    # so that a run which fails, or is interrupted, still does not leave a self-signed certificate in
    # a machine's trusted roots.
    if ($script:trustAdded) {
        try {
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store 'Root', 'LocalMachine'
            $store.Open('ReadWrite')
            $leaving = $store.Certificates | Where-Object { $_.Thumbprint -eq $Thumbprint }
            foreach ($c in $leaving) { $store.Remove($c) }
            $store.Close()
            Check (-not (Get-Item "Cert:\LocalMachine\Root\$Thumbprint" -ErrorAction SilentlyContinue)) "the machine trust store is back as it was"
        }
        catch { Check $false "the machine trust store is back as it was" $_.Exception.Message }
    }

    Say ""
    Say "RESULT: $(if ($script:failures -eq 0) { 'all checks passed' } else { "$script:failures check(s) failed" })"
}
