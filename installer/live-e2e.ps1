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
    [string] $OutDir = (Join-Path $PSScriptRoot 'out')
)

$ErrorActionPreference = 'Continue'
$script:failures = 0
$script:step = 0

# Written before anything else can go wrong, so an empty transcript means the process never started
# rather than that it started and said nothing. The first attempt at this produced a console window
# that opened and closed with no output at all, which is indistinguishable from UAC being dismissed.
"started $(Get-Date -Format s) as $env:USERNAME, elevated=$(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)), pid=$PID" |
    Out-File -FilePath $Transcript -Encoding utf8 -Append

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
$cpDir = Join-Path $env:ProgramFiles 'enList\ControlPlane'
$portalDir = Join-Path $env:ProgramFiles 'enList\Portal'
$agentDir = Join-Path $env:ProgramFiles 'enList\Agent'
$agentData = Join-Path $env:ProgramData 'enList\Agent'
$cpUrl = 'https://localhost:5293'

Say "enList live end-to-end, $(Get-Date -Format s)"
Say "  bundle      : $setup"
Say "  certificate : $Thumbprint"

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

try {
    # ---------------------------------------------------------------------------------------------
    Phase "Start from a clean slate"

    # Repeatable on purpose. This gets run several times while something is being got right, and an
    # install over an existing one proves something quite different from a first install - Burn would
    # detect the packages present and do nothing, and every assertion below would pass while testing
    # nothing at all.
    if (@(Get-Package -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^enList' }).Count -gt 0) {
        Say "    enList is already installed; removing it first"
        foreach ($name in 'enlist-agent', 'enlist-portal', 'enlist-controlplane') {
            Stop-Service $name -Force -ErrorAction SilentlyContinue
        }

        $exit = Invoke-Bundle @('/uninstall', '/quiet', '/norestart')
        Check ($exit -eq 0) "the previous install was removed" "exit $exit"
        Start-Sleep -Seconds 3
    }
    else {
        Say "    nothing installed"
    }

    Check ((Get-CimInstance Win32_Service -Filter "Name LIKE 'enlist-%'" -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) "no enList service to begin with"

    # ---------------------------------------------------------------------------------------------
    Phase "Install the Server role over TLS, silently, through the bundle"

    $arguments = @(
        '/quiet', '/norestart',
        "/log", (Join-Path $env:TEMP 'enlist-e2e-install.log'),
        'INSTALLTYPE=Server',
        "CP_CERT=$Thumbprint",
        "PORTAL_CERT=$Thumbprint",
        "PORTAL_CPURL=$cpUrl"
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
        Check ($cp.PathName -match [regex]::Escape("--Certificate:Thumbprint=$Thumbprint")) "the certificate reached the service command line"
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
        Check (@($acl.Access | Where-Object { $_.IdentityReference -like '*NETWORK SERVICE*' }).Count -gt 0) "the portal's service account can read it"
    }

    # The schema: the control plane refuses to start without it, so this is proved by the start below
    # as well, but named here because a missing schema and a bad certificate look identical from
    # outside and this separates them.
    $dbOk = $false
    try {
        $connection = New-Object System.Data.SqlClient.SqlConnection "Server=(localdb)\MSSQLLocalDB;Database=EnlistControlPlane;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=15"
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT COUNT(*) FROM sys.tables"
        $dbOk = ([int]$command.ExecuteScalar()) -gt 0
        $connection.Close()
    }
    catch { Say "    (database probe: $($_.Exception.Message))" }
    Check $dbOk "apply-schema created and migrated the database"

    # ---------------------------------------------------------------------------------------------
    Phase "Start the control plane and talk to it over TLS"

    Start-Service enlist-controlplane -ErrorAction SilentlyContinue
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

    Start-Service enlist-portal -ErrorAction SilentlyContinue
    $portalResponse = Wait-Http 'https://localhost:5231/' 60
    Check ($null -ne $portalResponse) "the portal answers on https://localhost:5231"
    if ($portalResponse) { Say "    portal status: $([int]$portalResponse.StatusCode)" }
    Check ((Service-Of 'enlist-portal').State -eq 'Running') "the portal service is Running"

    # ---------------------------------------------------------------------------------------------
    Phase "Mint a join token and enroll an agent against the running control plane"

    $env:ConnectionStrings__ControlPlane = "Server=(localdb)\MSSQLLocalDB;Database=EnlistControlPlane;Trusted_Connection=True;TrustServerCertificate=True;"
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
            "AGENT_CPURL=$cpUrl",
            "AGENT_JOINTOKEN=$joinToken"
        )
        $exit = Invoke-Bundle $arguments
        Check ($exit -eq 0) "adding the agent exits 0" "exit $exit"

        $agent = Service-Of 'enlist-agent'
        Check ($null -ne $agent) "the agent service exists"
        if ($agent) {
            Check ($agent.PathName -notmatch 'enlj_') "THE JOIN TOKEN IS NOT IN THE SERVICE COMMAND LINE"
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
        $env:ASPNETCORE_ENVIRONMENT = $null
        $keyOutput = & (Join-Path $cpDir 'Enlist.ControlPlane.exe') create-api-key --name e2e --role Operator --expires never 2>&1 | Out-String
        $apiKey = ([regex]::Match($keyOutput, 'enlk_\S+')).Value
        if ($apiKey) {
            try {
                $agents = Invoke-RestMethod -Uri "$cpUrl/api/agents" -Headers @{ Authorization = "Bearer $apiKey" } -TimeoutSec 20
                $names = @($agents | ForEach-Object { $_.name })
                Say "    agents the control plane knows: $($names -join ', ')"
                Check ($names -contains $env:COMPUTERNAME) "the control plane has this agent registered"
            }
            catch { Check $false "the control plane has this agent registered" $_.Exception.Message }
        }

        # And the agent actually runs against it.
        Start-Service enlist-agent -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 12
        $agent = Service-Of 'enlist-agent'
        Check ($agent.State -eq 'Running') "the agent service is Running" $agent.State
        $agentLog = Join-Path $agentData 'Logs'
        if (Test-Path $agentLog) {
            Get-ChildItem $agentLog -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Desc | Select-Object -First 1 |
                ForEach-Object { Say "    agent log tail:"; Get-Content $_.FullName -Tail 8 | ForEach-Object { Say "      $_" } }
        }
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

    foreach ($name in 'enlist-agent', 'enlist-portal', 'enlist-controlplane') {
        Stop-Service $name -Force -ErrorAction SilentlyContinue
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

    Say ""
    Say "RESULT: $(if ($script:failures -eq 0) { 'all checks passed' } else { "$script:failures check(s) failed" })"
}
