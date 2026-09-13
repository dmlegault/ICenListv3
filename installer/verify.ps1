#Requires -Version 5.1
<#
.SYNOPSIS
    Proves the three MSIs do what the silent command line says they will.

.DESCRIPTION
    Two halves, because only one of them needs administrator rights.

    OFFLINE (the default, no elevation): opens each package as a real Windows Installer session,
    sets the properties from Installer-UI-Design.md section 10's own examples, runs costing and the
    property-composing actions, and asserts on the service command line that would be created. This
    is most of what can go wrong. A binPath is a formatted string assembled from directory properties
    that do not have their final values until CostFinalize, and getting it wrong produces a service
    that installs perfectly and then fails at first start with nothing pointing at the cause.

    It also parses each command line with CommandLineToArgvW - the same parser .NET uses - because a
    string that LOOKS right is not the same as one that parses right. That check is not decoration:
    it is what caught the trailing-backslash defect on 2026-09-13, where every directory argument
    ended in a backslash directly before its closing quote, Windows read that as an escaped quote,
    and the next flag was swallowed into the previous argument.

    LIVE (-Live, requires an elevated shell): actually installs each package silently, asserts the
    service that resulted, upgrades in place, and uninstalls - the part that cannot be faked.

.EXAMPLE
    .\verify.ps1
    .\verify.ps1 -Live          # from an elevated PowerShell
#>
[CmdletBinding()]
param(
    [switch] $Live,
    [string] $OutDir = (Join-Path $PSScriptRoot 'out')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failures = 0

function Assert-That([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Host "    ok    $what" -ForegroundColor DarkGreen
    }
    else {
        Write-Host "    FAIL  $what" -ForegroundColor Red
        $script:failures++
    }
}

# The Windows parser, so a command line is judged by how it will actually be read rather than by how
# it looks. Anything that assembles a quoted path deserves this and almost nothing gets it.
Add-Type -Namespace Enlist -Name NativeCmd -MemberDefinition @'
[DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);
[DllImport("kernel32.dll")]
public static extern IntPtr LocalFree(IntPtr hMem);
'@

function Split-CommandLine([string] $line) {
    $count = 0
    $block = [Enlist.NativeCmd]::CommandLineToArgvW($line, [ref] $count)
    if ($block -eq [IntPtr]::Zero) { throw "CommandLineToArgvW refused: $line" }
    try {
        0..($count - 1) | ForEach-Object {
            [Runtime.InteropServices.Marshal]::PtrToStringUni(
                [Runtime.InteropServices.Marshal]::ReadIntPtr($block, $_ * [IntPtr]::Size))
        }
    }
    finally { [Enlist.NativeCmd]::LocalFree($block) | Out-Null }
}

<#
  Opens the package, applies properties, runs costing and the named actions, and hands back the
  composed command line. A fresh Installer object per call on purpose: Windows Installer allows one
  session at a time, and a session left un-released makes the NEXT OpenPackage fail with a COM error
  that says nothing about the real cause.

  Each action's condition is read from InstallExecuteSequence and evaluated BEFORE running it, which
  is the whole reason this function is more than three lines. Session.DoAction executes an action
  outright: a custom action's condition lives in the sequence table, not in the action, so calling
  DoAction directly runs it whether its condition holds or not. A harness that skipped this would
  have reported every conditional argument as present in every case - passing loudly while testing
  nothing, which is worse than failing.
#>
function Get-SequenceCondition {
    param($Database, [string] $Action)

    $view = $Database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $Database,
        @("SELECT Condition FROM InstallExecuteSequence WHERE Action = '$Action'"))
    try {
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if (-not $record) { throw "$Action is not in InstallExecuteSequence; the .wxs no longer schedules it." }
        return [string] $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
    }
    finally {
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    }
}

function Get-ServiceCommandLine {
    param(
        [string] $Msi,
        [hashtable] $Properties,
        [string[]] $Actions,
        [string] $ArgsProperty,
        [string] $ServiceExe
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $session = $null
    try {
        $installer.GetType().InvokeMember('UILevel', 'SetProperty', $null, $installer, @(2)) | Out-Null
        $session = $installer.GetType().InvokeMember('OpenPackage', 'InvokeMethod', $null, $installer, @($Msi, 1))

        foreach ($name in $Properties.Keys) {
            $session.GetType().InvokeMember('Property', 'SetProperty', $null, $session, @($name, $Properties[$name])) | Out-Null
        }

        foreach ($action in @('CostInitialize', 'FileCost', 'CostFinalize')) {
            $session.GetType().InvokeMember('DoAction', 'InvokeMethod', $null, $session, @($action)) | Out-Null
        }

        $database = $session.GetType().InvokeMember('Database', 'GetProperty', $null, $session, $null)
        foreach ($action in $Actions) {
            $condition = Get-SequenceCondition -Database $database -Action $action

            # msiEvaluateConditionNone (2) means there is no condition, so the action always runs.
            $verdict = 2
            if ($condition) {
                $verdict = [int] $session.GetType().InvokeMember('EvaluateCondition', 'InvokeMethod', $null, $session, @($condition))
                if ($verdict -eq 3) { throw "Could not evaluate the condition on ${action}: $condition" }
            }

            if ($verdict -ne 0) {
                $session.GetType().InvokeMember('DoAction', 'InvokeMethod', $null, $session, @($action)) | Out-Null
            }
        }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database)

        $installFolder = [string] $session.GetType().InvokeMember('Property', 'GetProperty', $null, $session, @('INSTALLFOLDER'))
        $arguments = [string] $session.GetType().InvokeMember('Property', 'GetProperty', $null, $session, @($ArgsProperty))
        return '"' + $installFolder + $ServiceExe + '" ' + $arguments
    }
    finally {
        if ($session) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($session) }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
}

# ---- The detection library ------------------------------------------------------------------------
<#
  Run first, because everything the wizard decides lives there and none of it can be reached once it
  is behind a bootstrapper's pages. The registry shape .NET really uses, the wslc and docker probes,
  how /health is read, which wizard pages appear for which install type, and what the whole thing
  becomes on the way to Burn.
#>
Write-Host "Detection library" -ForegroundColor Cyan
$detectionTests = Join-Path $PSScriptRoot 'ba\Enlist.Installer.Detection.Tests'
if (Test-Path $detectionTests) {
    $output = & dotnet test $detectionTests --nologo -v q 2>&1
    $summary = ($output | Select-String -Pattern 'Passed!|Failed!' | Select-Object -First 1)
    if ($summary) {
        Write-Host "    $($summary.Line.Trim())" -ForegroundColor DarkGray
    }

    Assert-That ($LASTEXITCODE -eq 0) 'the detection and wizard-logic tests pass'
}
else {
    Assert-That $false "the detection tests are missing from $detectionTests"
}

Write-Host ""
Write-Host "Offline checks (no elevation needed)" -ForegroundColor Cyan

# ---- Agent: the AgentOnly example from section 10 ------------------------------------------------
Write-Host "  Enlist.Agent.msi, section 10's agent-only example"
$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.Agent.msi') `
    -Properties @{ AGENT_NAME = 'WEB-07'; AGENT_CPURL = 'https://enlist.corp.local:5293'; AGENT_ENGINE = 'wslc'; AGENT_IMAGE = 'enlist/runner:3.0.0' } `
    -Actions @('SetAgentArgs', 'SetAgentArgsEngine') `
    -ArgsProperty 'AGENT_SERVICE_ARGS' -ServiceExe 'enlist-agent.exe'
$parsed = @(Split-CommandLine $line)

Assert-That ($parsed[0] -eq 'C:\Program Files\enList\Agent\enlist-agent.exe') 'the service runs the installed agent'
Assert-That ($parsed -contains 'https://enlist.corp.local:5293') 'the control plane URL survives parsing'
Assert-That ($parsed -contains 'WEB-07') 'the agent name survives parsing'
Assert-That ($parsed -contains 'C:\Program Files\enList\Agent\runner\') 'the runner path is a whole argument'
Assert-That ($parsed -contains 'C:\Program Files\enList\Agent\runner-legacy\') 'the legacy runner path is a whole argument'
Assert-That ($parsed -contains 'C:\ProgramData\enList\Agent\') 'the data path is a whole argument'
Assert-That ($parsed -contains 'wslc' -and $parsed -contains 'enlist/runner:3.0.0') 'the container engine and image are passed'

# The regression this file exists for. Every flag must arrive as its own argument; the failure mode
# was one flag being absorbed into the previous argument's value.
foreach ($flag in '--control-plane', '--agent', '--runner-bin', '--legacy-runner-bin', '--data', '--container-engine', '--container-image') {
    Assert-That ($parsed -contains $flag) "$flag survives as its own argument"
}
Assert-That (-not ($parsed | Where-Object { $_ -match '"' })) 'no argument contains a stray quote'

# ---- Agent: no container engine means the flags are absent, not empty ----------------------------
Write-Host "  Enlist.Agent.msi, no container engine"
$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.Agent.msi') `
    -Properties @{ AGENT_NAME = 'WEB-08'; AGENT_CPURL = 'https://enlist.corp.local:5293' } `
    -Actions @('SetAgentArgs', 'SetAgentArgsEngine') `
    -ArgsProperty 'AGENT_SERVICE_ARGS' -ServiceExe 'enlist-agent.exe'
$parsed = @(Split-CommandLine $line)
Assert-That (-not ($parsed -contains '--container-engine')) 'no engine means the engine flag is omitted entirely'
Assert-That ($parsed -contains 'WEB-08') 'the rest of the command line is unaffected'

# ---- Control plane: Windows auth composes a connection string ------------------------------------
Write-Host "  Enlist.ControlPlane.msi, Windows authentication"
$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.ControlPlane.msi') `
    -Properties @{ CP_URLS = 'https://+:5293'; DB_SERVER = 'sql01.corp.local'; DB_NAME = 'EnlistControlPlane'; DB_AUTH = 'Windows' } `
    -Actions @('SetCpArgs', 'SetCpArgsWindowsAuth') `
    -ArgsProperty 'CP_SERVICE_ARGS' -ServiceExe 'Enlist.ControlPlane.exe'
$parsed = @(Split-CommandLine $line)
Assert-That ($parsed -contains 'https://+:5293') 'the listen URL survives parsing'
Assert-That ($parsed -contains '--PackageStorage:Root=C:\ProgramData\enList\ControlPlane\PackageBlobs\') 'the blob root is absolute and whole'
Assert-That ($parsed -contains '--ConnectionStrings:ControlPlane=Server=sql01.corp.local;Database=EnlistControlPlane;Trusted_Connection=True;TrustServerCertificate=True;') 'Windows auth composes the connection string'

# ---- Control plane: a SQL login is deliberately NOT put on the command line ----------------------
Write-Host "  Enlist.ControlPlane.msi, SQL authentication"
$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.ControlPlane.msi') `
    -Properties @{ CP_URLS = 'https://+:5293'; DB_SERVER = 'sql01.corp.local'; DB_NAME = 'EnlistControlPlane'; DB_AUTH = 'Sql' } `
    -Actions @('SetCpArgs', 'SetCpArgsWindowsAuth') `
    -ArgsProperty 'CP_SERVICE_ARGS' -ServiceExe 'Enlist.ControlPlane.exe'
Assert-That ($line -notmatch 'ConnectionStrings') 'a SQL login never reaches the binPath, where any local user could read it'

# ---- Portal --------------------------------------------------------------------------------------
Write-Host "  Enlist.Portal.msi"
$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.Portal.msi') `
    -Properties @{ PORTAL_URLS = 'https://+:5231'; PORTAL_CPURL = 'https://localhost:5293' } `
    -Actions @('SetPortalArgs', 'SetPortalArgsControlPlane') `
    -ArgsProperty 'PORTAL_SERVICE_ARGS' -ServiceExe 'Enlist.Portal.exe'
$parsed = @(Split-CommandLine $line)
Assert-That ($parsed -contains 'https://+:5231') 'the listen URL survives parsing'
Assert-That ($parsed -contains '--ControlPlane:BaseUrl=https://localhost:5293') 'the control plane URL is passed as configuration'
Assert-That ($line -notmatch 'ApiKey') 'the portal key never reaches the binPath'

Write-Host "  Enlist.Portal.msi, no control plane named"
$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.Portal.msi') `
    -Properties @{ PORTAL_URLS = 'https://+:5231' } `
    -Actions @('SetPortalArgs', 'SetPortalArgsControlPlane') `
    -ArgsProperty 'PORTAL_SERVICE_ARGS' -ServiceExe 'Enlist.Portal.exe'
Assert-That ($line -notmatch 'BaseUrl') 'an unset control plane URL is omitted rather than passed empty'

# ---- The bundle ----------------------------------------------------------------------------------
<#
  The bundle is checked by reading the manifest Burn will actually execute, extracted from the built
  setup executable. Everything that decides WHAT gets installed lives there: the chain order, each
  package's install condition, and the variables a silent command line is allowed to override. None
  of it can be confirmed by reading the .wxs, because a variable that is not declared Overridable is
  accepted on the command line and silently ignored - the install succeeds, and the service is
  configured with defaults nobody chose.
#>
Write-Host ""
Write-Host "  Bundle manifest"
$setup = Get-ChildItem $OutDir -Filter 'enList-*-Setup.exe' | Select-Object -First 1
if (-not $setup) {
    Assert-That $false 'the setup executable was built'
}
else {
    $extract = Join-Path ([IO.Path]::GetTempPath()) "enlist-bundle-$([guid]::NewGuid().ToString('N'))"
    try {
        Push-Location $PSScriptRoot
        try { & dotnet wix burn extract $setup.FullName -o $extract -oba (Join-Path $extract 'ba') | Out-Null }
        finally { Pop-Location }

        $manifest = Get-Content (Join-Path $extract 'ba\manifest.xml') -Raw

        # The order matters: a runtime installed after the thing that needs it is no runtime at all.
        $chain = @([regex]::Matches($manifest, '<(?:MsiPackage|ExePackage)[^>]*\sId="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
        Assert-That ($chain.Count -eq 5) "the chain has five packages, two runtimes and three MSIs (found $($chain.Count))"

        $conditions = @([regex]::Matches($manifest, 'InstallCondition="([^"]*)"') | ForEach-Object { $_.Groups[1].Value -replace '&quot;', '"' })
        Assert-That (@($conditions | Where-Object { $_ -eq 'INSTALLTYPE = "Server" OR InstallControlPlane = "1"' }).Count -eq 1) 'the control plane installs on Server, or when asked for directly'
        Assert-That (@($conditions | Where-Object { $_ -eq 'INSTALLTYPE = "Server" OR InstallPortal = "1"' }).Count -eq 1) 'the portal installs on Server, or when asked for directly'
        Assert-That (@($conditions | Where-Object { $_ -eq 'INSTALLTYPE = "AgentOnly" OR InstallAgent = "1"' }).Count -eq 1) 'the agent installs on AgentOnly, or when asked for directly'
        Assert-That (@($conditions | Where-Object { $_ -match 'InstallPortal' -and $_ -match 'InstallControlPlane' }).Count -eq 1) 'ASP.NET Core is skipped entirely on an agent-only install'

        # No condition on the base runtime: every component needs it.
        Assert-That ($conditions.Count -eq 4) 'the base .NET runtime carries no install condition, because all three components need it'

        <#
          Every property section 10 documents must be declared AND overridable, and the two live in
          different files. manifest.xml declares the variables; whether the standard bootstrapper will
          ACCEPT one from a command line is recorded separately, in BootstrapperApplicationData.xml,
          as a WixStdbaOverridableVariable. A variable declared but not listed there is taken on the
          command line and silently ignored - the install succeeds and the service is configured with
          defaults nobody chose, which is the failure this check exists for. (That file is UTF-16;
          Get-Content follows its byte-order mark.)
        #>
        $baData = Get-Content (Join-Path $extract 'ba\BootstrapperApplicationData.xml') -Raw
        $overridable = @([regex]::Matches($baData, '<WixStdbaOverridableVariable\s+Name="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
        foreach ($name in 'INSTALLTYPE', 'CP_URLS', 'CP_ACCOUNT', 'DB_SERVER', 'DB_NAME', 'DB_AUTH',
                          'PORTAL_URLS', 'PORTAL_CPURL', 'AGENT_NAME', 'AGENT_CPURL', 'AGENT_ENGINE', 'AGENT_IMAGE') {
            Assert-That ($overridable -contains $name) "$name can be set on a silent command line"
        }

        Assert-That ($manifest -match 'builds\.dotnet\.microsoft\.com') 'the runtimes are fetched from Microsoft rather than embedded'
        Assert-That ($manifest -notmatch 'dotnet-hosting') 'the 117 MB IIS hosting bundle is not what gets downloaded'
    }
    finally { Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue }
}

# ---- Live -----------------------------------------------------------------------------------------
if ($Live) {
    $elevated = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $elevated) { throw "-Live installs services and needs an elevated PowerShell." }

    Write-Host ""
    Write-Host "Live install (this machine, really)" -ForegroundColor Cyan

    $log = Join-Path $env:TEMP 'enlist-agent-install.log'
    $install = Start-Process msiexec.exe -Wait -PassThru -NoNewWindow -ArgumentList @(
        '/i', "`"$(Join-Path $OutDir 'Enlist.Agent.msi')`"", '/qn', '/l*v', "`"$log`"",
        'AGENT_NAME=VERIFY-01', 'AGENT_CPURL=https://localhost:5293')
    Assert-That ($install.ExitCode -eq 0) "silent install returned 0 (log: $log)"

    $service = Get-CimInstance Win32_Service -Filter "Name='enlist-agent'" -ErrorAction SilentlyContinue
    Assert-That ($null -ne $service) 'the enlist-agent service exists'
    if ($service) {
        Assert-That ($service.StartMode -eq 'Auto') 'it starts automatically'
        Assert-That ($service.StartName -eq 'LocalSystem') 'it runs as Local System'
        Assert-That ($service.State -eq 'Stopped') 'it is installed stopped, waiting to be enrolled'
        Assert-That ($service.PathName -match 'VERIFY-01') 'the agent name reached the binPath'
        Assert-That ($service.PathName -notmatch 'runner\\"') 'no path in the binPath escapes its closing quote'
        Write-Host "      binPath: $($service.PathName)" -ForegroundColor DarkGray
    }

    Assert-That (Test-Path 'C:\Program Files\enList\Agent\enlist-agent.exe') 'the agent is on disk'
    Assert-That (Test-Path 'C:\Program Files\enList\Agent\runner\enlist-runner.dll') 'the net10 runner is on disk'
    Assert-That (Test-Path 'C:\Program Files\enList\Agent\runner-legacy\enlist-runner.exe.config') 'the net472 runner is on disk'
    Assert-That (Test-Path 'C:\ProgramData\enList\Agent') 'the data root was created'

    # Something that must survive an upgrade, and does not belong to the installer.
    $canary = 'C:\ProgramData\enList\Agent\upgrade-canary.txt'
    Set-Content -Path $canary -Value 'written between install and upgrade' -Encoding utf8

    $upgrade = Start-Process msiexec.exe -Wait -PassThru -NoNewWindow -ArgumentList @(
        '/i', "`"$(Join-Path $OutDir 'Enlist.Agent.msi')`"", '/qn', 'REINSTALL=ALL', 'REINSTALLMODE=vomus',
        'AGENT_NAME=VERIFY-01', 'AGENT_CPURL=https://localhost:5293')
    Assert-That ($upgrade.ExitCode -eq 0) 'reinstall over the top returned 0'
    Assert-That (Test-Path $canary) 'ProgramData survived the reinstall'

    $uninstall = Start-Process msiexec.exe -Wait -PassThru -NoNewWindow -ArgumentList @(
        '/x', "`"$(Join-Path $OutDir 'Enlist.Agent.msi')`"", '/qn')
    Assert-That ($uninstall.ExitCode -eq 0) 'silent uninstall returned 0'
    Assert-That ($null -eq (Get-CimInstance Win32_Service -Filter "Name='enlist-agent'" -ErrorAction SilentlyContinue)) 'the service is gone'
    Assert-That (-not (Test-Path 'C:\Program Files\enList\Agent\enlist-agent.exe')) 'the binaries are gone'
    Assert-That (Test-Path $canary) 'ProgramData survived the uninstall, which is the point of Permanent'

    Remove-Item $canary -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host ""
    Write-Host "  Skipping the live install. Re-run with -Live from an elevated PowerShell to" -ForegroundColor DarkYellow
    Write-Host "  install, upgrade and uninstall for real." -ForegroundColor DarkYellow
}

Write-Host ""
if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "All checks passed." -ForegroundColor Green
