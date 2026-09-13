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

<#
  Every custom action scheduled before a named standard action, in sequence order. Used to run the
  property defaults, which are type-51 actions sitting before CostInitialize.
#>
function Get-CustomActionsBefore {
    param($Database, [string] $Marker)

    $query = "SELECT Action, Sequence FROM InstallExecuteSequence WHERE Sequence < " +
        "(SELECT Sequence FROM InstallExecuteSequence WHERE Action = '$Marker')"

    # MSI's SQL has no subqueries, so the marker's sequence number is read first.
    $markerSequence = $null
    $view = $Database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $Database,
        @("SELECT Sequence FROM InstallExecuteSequence WHERE Action = '$Marker'"))
    try {
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if (-not $record) { throw "$Marker is not in InstallExecuteSequence." }
        $markerSequence = [int] $record.GetType().InvokeMember('IntegerData', 'GetProperty', $null, $record, @(1))
    }
    finally {
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    }

    # Only CUSTOM actions: the standard ones before costing do real work and must not be invoked here.
    $custom = @{}
    $view = $Database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $Database, @("SELECT Action FROM CustomAction"))
    try {
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        while ($true) {
            $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if (-not $record) { break }
            $custom[[string] $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))] = $true
        }
    }
    finally {
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    }

    $ordered = New-Object System.Collections.ArrayList
    $view = $Database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $Database,
        @("SELECT Action, Sequence FROM InstallExecuteSequence ORDER BY Sequence"))
    try {
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        while ($true) {
            $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if (-not $record) { break }
            $action = [string] $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
            $sequence = [int] $record.GetType().InvokeMember('IntegerData', 'GetProperty', $null, $record, @(2))
            if ($sequence -gt 0 -and $sequence -lt $markerSequence -and $custom.ContainsKey($action)) {
                [void] $ordered.Add($action)
            }
        }
    }
    finally {
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    }

    return $ordered
}

function Invoke-ActionIfConditionHolds {
    param($Session, $Database, [string] $Action)

    $condition = Get-SequenceCondition -Database $Database -Action $Action

    # msiEvaluateConditionNone (2) means there is no condition, so the action always runs.
    $verdict = 2
    if ($condition) {
        $verdict = [int] $Session.GetType().InvokeMember('EvaluateCondition', 'InvokeMethod', $null, $Session, @($condition))
        if ($verdict -eq 3) { throw "Could not evaluate the condition on ${Action}: $condition" }
    }

    if ($verdict -ne 0) {
        $Session.GetType().InvokeMember('DoAction', 'InvokeMethod', $null, $Session, @($Action)) | Out-Null
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

        $database = $session.GetType().InvokeMember('Database', 'GetProperty', $null, $session, $null)

        <#
          The property defaults run FIRST, before costing, exactly as the sequence table schedules
          them. Skipping them was a real hole in this harness: every "an empty value falls back" check
          reported a failure, because the actions that do the falling back had never run. An install
          runs the whole immediate sequence; so does this.
        #>
        foreach ($action in (Get-CustomActionsBefore -Database $database -Marker 'CostInitialize')) {
            Invoke-ActionIfConditionHolds -Session $session -Database $database -Action $action
        }

        foreach ($action in @('CostInitialize', 'FileCost', 'CostFinalize')) {
            $session.GetType().InvokeMember('DoAction', 'InvokeMethod', $null, $session, @($action)) | Out-Null
        }

        foreach ($action in $Actions) {
            Invoke-ActionIfConditionHolds -Session $session -Database $database -Action $action
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

# ---- An empty value must never blank a default ----------------------------------------------------
<#
  The bundle hands every variable to every MSI unconditionally, so any variable that is empty at the
  bundle level arrives as an empty PROPERTY - and a property passed on the command line replaces the
  Property table default, including with nothing.

  That is not a hypothetical. On the first real run of this bundle DB_SERVER was empty, which produced

      ConnectionStrings:ControlPlane="Server=;Database=EnlistControlPlane;..."

  and a control plane service that installs perfectly and never starts. Every default is now applied
  with a conditional SetProperty instead, so an empty incoming value falls back. These checks pass
  empty for everything and assert that the composed command lines are still whole.
#>
Write-Host ""
Write-Host "  Defaults survive an empty value"

$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.ControlPlane.msi') `
    -Properties @{ CP_URLS = ''; CP_ACCOUNT = ''; DB_SERVER = ''; DB_NAME = ''; DB_AUTH = ''; CP_INSTALLDIR = ''; CP_DATADIR = '' } `
    -Actions @('SetCpArgs', 'SetCpArgsWindowsAuth') `
    -ArgsProperty 'CP_SERVICE_ARGS' -ServiceExe 'Enlist.ControlPlane.exe'

Assert-That ($line -match 'Server=\(localdb\)') 'an empty DB_SERVER falls back rather than blanking the connection string'
Assert-That ($line -notmatch 'Server=;') 'the connection string never names an empty server'
Assert-That ($line -match 'Database=EnlistControlPlane') 'an empty DB_NAME falls back'
Assert-That ($line -match 'https://\+:5293') 'an empty CP_URLS falls back to the documented listener'

$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.Portal.msi') `
    -Properties @{ PORTAL_URLS = ''; PORTAL_ACCOUNT = ''; PORTAL_CPURL = '' } `
    -Actions @('SetPortalArgs', 'SetPortalArgsControlPlane') `
    -ArgsProperty 'PORTAL_SERVICE_ARGS' -ServiceExe 'Enlist.Portal.exe'
Assert-That ($line -match 'https://\+:5231') 'an empty PORTAL_URLS falls back to the documented listener'

$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.Agent.msi') `
    -Properties @{ AGENT_NAME = ''; AGENT_IMAGE = ''; AGENT_ACCOUNT = ''; AGENT_DATADIR = ''; AGENT_ENGINE = 'wslc' } `
    -Actions @('SetAgentArgs', 'SetAgentArgsEngine') `
    -ArgsProperty 'AGENT_SERVICE_ARGS' -ServiceExe 'enlist-agent.exe'
$parsed = @(Split-CommandLine $line)
Assert-That ($parsed -contains $env:COMPUTERNAME) 'an empty AGENT_NAME falls back to the machine name'
Assert-That ($line -match 'enlist/runner:') 'an empty AGENT_IMAGE falls back to the product image'
Assert-That ($parsed -contains 'C:\ProgramData\enList\Agent\') 'an empty AGENT_DATADIR falls back to ProgramData'

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

        <#
          How the BASE runtime is detected, which was wrong on the first real run and wrong
          invisibly: the install succeeded, it just fetched 30 MB it did not need, on every machine
          with .NET already on it, for ever.

          The shared-framework key records one value NAMED for each installed version, so matching a
          patch exactly means a machine with 10.0.3 fails a check for 10.0.12. hostfxr's Version is
          the one value in the tree that names a version rather than being named for one, so the
          condition can be a real comparison. Every agent in a fleet pays for this one, which is why
          it is asserted rather than trusted.

          No check on the registry view: Burn's RegistrySearch already reads the 32-bit view where
          .NET records itself, so Bitness="always32" is explicit documentation and emits nothing to
          assert against.
        #>
        # Decoded the same way InstallCondition is above: the manifest keeps &gt; and &quot; encoded,
        # and asserting against the encoded form tests the XML writer rather than the condition.
        $detectConditions = @([regex]::Matches($manifest, 'DetectCondition="([^"]*)"') | ForEach-Object { ($_.Groups[1].Value -replace '&gt;', '>') -replace '&quot;', '"' })
        Assert-That (@($detectConditions | Where-Object { $_ -match 'NetCoreHostVersion >= v' }).Count -eq 1) 'the base runtime is detected by comparing a version, not by matching one patch'
        Assert-That (-not ($detectConditions | Where-Object { $_ -match '^NetCoreRuntimeDetected$' })) 'the old exact-patch check for the base runtime is gone'
        Assert-That ($manifest -match 'Key="SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64\\hostfxr"') 'it reads the host version, which is updated by every runtime install'

        <#
          ASP.NET Core cannot be compared by a RegistrySearch at all - the framework records one
          value per version installed and nothing that names the newest, so the only question the
          engine can ask is "is this exact patch here". On a machine with 10.0.11 that answer is No,
          followed by an 11 MB download of something it already has a newer copy of.

          The bootstrapper sets AspNetCorePresent from a real comparison before Detect. Both halves
          of the OR are asserted: the variable because it is the fix, and the search because it is
          the fallback that keeps -StandardBootstrapper builds detecting anything at all.
        #>
        $aspNet = @($detectConditions | Where-Object { $_ -match 'AspNetCore' })
        Assert-That ($aspNet.Count -eq 1) 'ASP.NET Core has exactly one detect condition'
        Assert-That ($aspNet[0] -match 'AspNetCorePresent = "1"') 'ASP.NET Core detection asks the bootstrapper, which can compare versions'
        Assert-That ($aspNet[0] -match 'AspNetCoreRuntimeDetected') 'the registry search is kept as the fallback for a standard-bootstrapper build'
        # Id, not Name: <Variable Name="..."> in the source is emitted as Id in the Burn manifest.
        Assert-That ($manifest -match '<Variable[^>]*Id="AspNetCorePresent"') 'the variable the bootstrapper writes is declared by the bundle'
        Assert-That ($manifest -match '<Variable[^>]*Id="DotnetRuntimeMinimum"') 'the minimum is published to the bootstrapper rather than duplicated in it'

        <#
          THE JOIN TOKEN. Two properties, and both are the difference between a secret that lives for
          a second and one that lives for the life of the installation.

          Not an MsiProperty: an MSI property becomes part of the service's command line, and `sc qc`
          shows a service's arguments to any local user, for ever. The bootstrapper reads the bundle
          variable, hands it to `enlist-agent enroll`, and what persists is the agent's own credential
          - DPAPI-protected, closed ACL.

          Hidden: Burn writes every variable into its log when it shuts down, and that log is a
          world-readable file in %TEMP%. Without Hidden the token would be disclosed by the back door,
          having been kept out of the front one.
        #>
        Write-Host ""
        Write-Host "  Credentials the bootstrapper handles"
        $tokenVariable = [regex]::Match($manifest, '<Variable[^>]*Id="AGENT_JOINTOKEN"[^>]*>').Value
        Assert-That ($tokenVariable -ne '') 'the join token is a bundle variable, so a silent install can enroll'
        Assert-That ($tokenVariable -match 'Hidden="yes"') 'the join token is Hidden, so it is not written to the bundle log in the clear'
        Assert-That (-not ($manifest -match '<MsiProperty[^>]*Id="AGENT_JOINTOKEN"')) 'the join token is passed to no package, so it cannot reach a service binPath'

        # The same rule for the two passwords that do reach a package: they go to the SCM and nowhere
        # else, so the bundle must not log them either.
        foreach ($secret in @('CP_PASSWORD', 'PORTAL_PASSWORD', 'AGENT_PASSWORD')) {
            $declared = [regex]::Match($manifest, "<Variable[^>]*Id=`"$secret`"[^>]*>").Value
            Assert-That ($declared -match 'Hidden="yes"') "$secret is Hidden, so the bundle log does not carry it"
        }
    }
    finally { Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue }
}

# ---- Live -----------------------------------------------------------------------------------------
<#
  The half that cannot be faked: install, upgrade to a higher version, uninstall.

  Driven through the BUNDLE rather than msiexec, for two reasons. It is what a person actually runs,
  and it is the only way this works from an ordinary shell: Burn launches its own elevated engine and
  raises one UAC prompt per run, whereas msiexec /qn against a per-machine package cannot prompt and
  simply fails. Expect to approve three prompts.

  The upgrade is a REAL one, against a 3.0.1 built from this same source, not a reinstall over the
  top. A major upgrade has to remove the old registration rather than sit beside it, and a reinstall
  would never show that.
#>
if ($Live) {
    Write-Host ""
    Write-Host "Live install (this machine, really)" -ForegroundColor Cyan

    $setup = Get-ChildItem $OutDir -Filter 'enList-*-Setup.exe' | Select-Object -First 1
    if (-not $setup) { throw "No setup executable in $OutDir. Run build.ps1 first." }

    function Invoke-Bundle([string[]] $arguments, [string] $what) {
        $log = Join-Path $env:TEMP "enlist-verify-$what.log"
        Write-Host "    running: $what (approve the elevation prompt)" -ForegroundColor DarkGray
        $process = Start-Process $setup.FullName -ArgumentList $arguments -PassThru -Wait
        Assert-That ($process.ExitCode -eq 0) "$what returned 0 (log: $log)"
        return $process.ExitCode
    }

    $canary = 'C:\ProgramData\enList\Agent\upgrade-canary.txt'

    # ---- install ----
    Invoke-Bundle @('/quiet', '/log', (Join-Path $env:TEMP 'enlist-verify-install.log'),
        'INSTALLTYPE=AgentOnly', 'AGENT_NAME=VERIFY-01', 'AGENT_CPURL=https://localhost:5293') 'install' | Out-Null

    $service = Get-CimInstance Win32_Service -Filter "Name='enlist-agent'" -ErrorAction SilentlyContinue
    Assert-That ($null -ne $service) 'the enlist-agent service exists'
    if ($service) {
        Assert-That ($service.StartMode -eq 'Auto') 'it starts automatically'
        Assert-That ($service.StartName -eq 'LocalSystem') 'it runs as Local System'
        Assert-That ($service.State -eq 'Stopped') 'it is installed stopped, waiting to be enrolled'
        Assert-That ($service.PathName -match 'VERIFY-01') 'the agent name reached the binPath'

        # The defect this whole file exists for: a directory argument whose trailing backslash
        # escapes its own closing quote and swallows the next flag.
        $parsed = @(Split-CommandLine $service.PathName)
        Assert-That ($parsed -contains '--data') 'every flag survives parsing of the real binPath'
        Assert-That (-not ($parsed | Where-Object { $_ -match '"' })) 'no argument in the real binPath contains a stray quote'
    }

    Assert-That (Test-Path 'C:\Program Files\enList\Agent\enlist-agent.exe') 'the agent is on disk'
    Assert-That (Test-Path 'C:\Program Files\enList\Agent\runner\enlist-runner.dll') 'the net10 runner is on disk'
    Assert-That (Test-Path 'C:\Program Files\enList\Agent\runner-legacy\enlist-runner.exe.config') 'the net472 runner is on disk'
    Assert-That (Test-Path 'C:\ProgramData\enList\Agent') 'the data root was created'

    # Something that belongs to the operator, not the installer, and must outlive both.
    Set-Content -Path $canary -Value 'written between install and upgrade' -Encoding utf8

    # ---- upgrade ----
    $upgradeDir = Join-Path $env:TEMP 'enlist-verify-upgrade'
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipPublish -Version '3.0.1' -OutputDirectory $upgradeDir | Out-Null
    $upgradeSetup = Join-Path $upgradeDir 'enList-3.0.1-Setup.exe'
    Assert-That (Test-Path $upgradeSetup) 'a 3.0.1 build exists to upgrade to'

    $process = Start-Process $upgradeSetup -ArgumentList @('/quiet',
        '/log', (Join-Path $env:TEMP 'enlist-verify-upgrade.log'),
        'INSTALLTYPE=AgentOnly', 'AGENT_NAME=VERIFY-01', 'AGENT_CPURL=https://localhost:5293') -PassThru -Wait
    Assert-That ($process.ExitCode -eq 0) 'the upgrade returned 0'

    Assert-That ($null -ne (Get-CimInstance Win32_Service -Filter "Name='enlist-agent'" -ErrorAction SilentlyContinue)) 'the service survived the upgrade'
    Assert-That (Test-Path $canary) 'ProgramData survived the upgrade'

    $registered = @(Get-Package -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'enList Agent' })
    Assert-That ($registered.Count -eq 1) "exactly one agent is registered after the upgrade, not two (found $($registered.Count))"
    Assert-That ($registered.Count -eq 0 -or $registered[0].Version -eq '3.0.1') 'the registered version is the new one'

    # ---- uninstall ----
    $process = Start-Process $upgradeSetup -ArgumentList @('/uninstall', '/quiet',
        '/log', (Join-Path $env:TEMP 'enlist-verify-uninstall.log')) -PassThru -Wait
    Assert-That ($process.ExitCode -eq 0) 'the uninstall returned 0'

    Assert-That ($null -eq (Get-CimInstance Win32_Service -Filter "Name='enlist-agent'" -ErrorAction SilentlyContinue)) 'the service is gone'
    Assert-That (-not (Test-Path 'C:\Program Files\enList')) 'the binaries are gone, and the enList folder with them'
    Assert-That (@(Get-Package -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^enList' }).Count -eq 0) 'nothing of enList is left registered'

    # Permanent, both of them, and for different reasons: the data is the operator's, and the .NET
    # runtime is shared with whatever else on the machine may now depend on it.
    Assert-That (Test-Path $canary) 'ProgramData survived the uninstall, which is the point of Permanent'
    Assert-That ($null -ne (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\hostfxr' -ErrorAction SilentlyContinue)) 'the .NET runtime was not removed with enList'

    Remove-Item $canary -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $upgradeDir -ErrorAction SilentlyContinue
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

