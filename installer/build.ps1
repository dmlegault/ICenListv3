#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes every enList component and builds the three MSIs.

.DESCRIPTION
    Three MSIs, not one, because "agent only" is the common install and has to be independently
    installable, upgradable and removable (Installer-UI-Design.md section 2). The Burn bundle that
    chains them, and the bootstrapper that drives them, are separate work; this script produces the
    packages they will chain.

    Deliberately NOT part of enList_v3.slnx. Adding a WiX project to the solution would put an MSI
    build in the middle of every `dotnet build` and every test run, for an artifact that changes far
    less often than the code does. The cost is that this has to be run on purpose; the benefit is
    that the inner loop stays a few seconds.

    The version comes from Directory.Build.props, read here rather than repeated, for the same reason
    that file exists at all.

.PARAMETER Configuration
    Build configuration to publish. Release by default; Debug is for trying a change quickly.

.PARAMETER SkipPublish
    Reuse whatever is already in publish\. Saves about a minute when only the WiX changed.

.PARAMETER DotnetRuntimeVersion
    The .NET patch the bundle requires and, when it is missing, downloads. The payload hashes for it
    live in src\Prerequisites.wxs; tools\refresh-prerequisites.ps1 regenerates them for a new version.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipPublish,
    [string] $DotnetRuntimeVersion = '10.0.12',

    # The oldest .NET the bundle accepts as already present. Separate from the version above, which is
    # what it INSTALLS when nothing satisfies this. They were the same value once, and a machine with
    # .NET 10.0.3 downloaded 30 MB it did not need.
    [string] $DotnetRuntimeMinimum = '10.0.0',

    # Override the product version. Only for testing an upgrade, where a higher version of the same
    # source is needed to install over the one already there. An ordinary build leaves this empty and
    # takes the version from Directory.Build.props.
    [string] $Version = '',

    # Build somewhere other than out\, so a test build does not replace the real one.
    [string] $OutputDirectory = '',

    # Chain the stock WixStdBA instead of the wizard in ba\. The fallback if the wizard regresses:
    # the silent surface is identical either way, because a silent install never reaches a window.
    [switch] $StandardBootstrapper
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerRoot
$publishRoot = Join-Path $installerRoot 'publish'
$stagingRoot = Join-Path $installerRoot 'staging'
$outRoot = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $installerRoot 'out' }

# One product version, from the one place that defines it.
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if ($Version) {
    $version = $Version
}
else {
    $version = ([xml](Get-Content $propsPath)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "No <Version> found in $propsPath." }
}
Write-Host "enList $version" -ForegroundColor Cyan

# Each component, the project that produces it, and the executable its service runs. That executable
# is the one file the harvester must NOT pick up: MSI derives a service's binary path from the key
# path of the component the ServiceInstall sits in, so it has to be declared by hand in the .wxs.
# WiX 5's Files element has no exclude, hence the staged copy.
$components = @(
    @{ Name = 'Enlist.ControlPlane'; Project = 'src\Enlist.ControlPlane\Enlist.ControlPlane.csproj'; ServiceExe = 'Enlist.ControlPlane.exe' }
    @{ Name = 'Enlist.Portal';       Project = 'src\Enlist.Portal\Enlist.Portal.csproj';             ServiceExe = 'Enlist.Portal.exe' }
    @{ Name = 'Enlist.Agent';        Project = 'src\Enlist.Agent\Enlist.Agent.csproj';               ServiceExe = 'enlist-agent.exe' }
    # Both runners ship inside the agent package, in their own directories. Neither runs as a
    # service, so neither needs staging.
    @{ Name = 'runner';              Project = 'src\Enlist.Runner\Enlist.Runner.csproj';             ServiceExe = $null }
    @{ Name = 'runner-legacy';       Project = 'src\Enlist.Runner.Legacy\Enlist.Runner.Legacy.csproj'; ServiceExe = $null }
)

# The toolchain, restored rather than assumed. dotnet-tools.json pins the WiX version; extensions are
# a separate restore that `wix build -ext` does NOT do for itself, and their absence surfaces as a
# schema error about an unknown namespace rather than as anything about a missing extension.
#
# BootstrapperApplications, not Bal: the package WixToolset.Bal.wixext still exists on NuGet, but in
# WiX 5 its contents were renamed. Adding it under the old name caches an extension the toolset then
# reports as "damaged", which is a confusing way to learn about a rename.
$extensions = @('WixToolset.Util.wixext', 'WixToolset.BootstrapperApplications.wixext')
Push-Location $installerRoot
try {
    & dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    $present = (& dotnet wix extension list) -join "`n"
    foreach ($extension in $extensions) {
        if ($present -notmatch [regex]::Escape($extension)) {
            Write-Host "  restoring $extension" -ForegroundColor DarkGray
            & dotnet wix extension add "$extension/5.0.2"
            if ($LASTEXITCODE -ne 0) { throw "Could not add $extension." }
        }
    }
}
finally { Pop-Location }

<#
  -SkipPublish is for "only the WiX changed", and it will happily ship a MONTH-old binary otherwise.

  That is not hypothetical. A change to ManagementCli.cs was built with -SkipPublish, packaged an
  Enlist.ControlPlane.exe that predated it by an hour and three quarters, and the installer then
  failed on a verb the source plainly supported - which reads as a bug in the new code rather than as
  a stale artifact, and cost a full install-and-uninstall cycle to work out.

  So the flag now refuses rather than warns. Its documented purpose still works: a pure WiX change
  leaves every source file older than its published output.
#>
if ($SkipPublish) {
    foreach ($c in $components) {
        $published = Join-Path $publishRoot $c.Name
        if (-not (Test-Path $published)) {
            throw "-SkipPublish was given but $published does not exist. Run without it at least once."
        }

        $builtAt = (Get-ChildItem $published -Filter *.dll -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime

        $projectDir = Split-Path (Join-Path $repoRoot $c.Project) -Parent
        $newest = Get-ChildItem $projectDir -Recurse -File -Include *.cs, *.csproj, *.razor, *.json -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1

        if ($newest -and $builtAt -and $newest.LastWriteTime -gt $builtAt) {
            throw ("-SkipPublish would package a stale $($c.Name): $($newest.Name) changed at " +
                "$($newest.LastWriteTime.ToString('HH:mm:ss')) but publish\ was written at $($builtAt.ToString('HH:mm:ss')). " +
                "Run build.ps1 without -SkipPublish.")
        }
    }
}

if (-not $SkipPublish) {
    foreach ($c in $components) {
        $target = Join-Path $publishRoot $c.Name
        Write-Host "  publishing $($c.Name)" -ForegroundColor DarkGray
        & dotnet publish (Join-Path $repoRoot $c.Project) -c $Configuration -o $target --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $($c.Name)" }
    }
}

# Stage: a copy of each publish output with the service executable removed.
if (Test-Path $stagingRoot) { Remove-Item -Recurse -Force $stagingRoot }
foreach ($c in $components | Where-Object { $_.ServiceExe }) {
    $from = Join-Path $publishRoot $c.Name
    $to = Join-Path $stagingRoot $c.Name
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Copy-Item -Path (Join-Path $from '*') -Destination $to -Recurse -Force
    $exe = Join-Path $to $c.ServiceExe
    if (-not (Test-Path $exe)) { throw "Expected $($c.ServiceExe) in $from; the publish output has changed shape." }
    Remove-Item $exe -Force
}

# The bootstrapper application, unless the stock one was asked for. Built here rather than published:
# a net472 WPF executable has no publish step worth the name, and the bundle needs exactly the four
# files beside it that a build produces.
$baDir = Join-Path $installerRoot 'ba\Enlist.Installer.Ba\bin\Release\net472'
if (-not $StandardBootstrapper) {
    Write-Host "  building the wizard" -ForegroundColor DarkGray
    & dotnet build (Join-Path $installerRoot 'ba\Enlist.Installer.Ba\Enlist.Installer.Ba.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'build failed for the bootstrapper application' }

    # mbanative.dll is the one file that is copied rather than compiled, so it is the one that can be
    # silently absent - and its absence is a DllNotFoundException on the first call into the engine,
    # long after the bundle looks like it built correctly.
    foreach ($required in @('Enlist.Installer.Ba.exe', 'Enlist.Installer.Ba.exe.config', 'mbanative.dll',
                            'WixToolset.BootstrapperApplicationApi.dll', 'Enlist.Installer.Detection.dll')) {
        if (-not (Test-Path (Join-Path $baDir $required))) {
            throw "The bootstrapper build produced no $required in $baDir."
        }
    }
}

New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

# Harvest paths are resolved relative to the .wxs file, so every path handed to wix is absolute.
$defs = @(
    "ProductVersion=$version"
    "ControlPlaneStaging=$(Join-Path $stagingRoot 'Enlist.ControlPlane')"
    "ControlPlanePublish=$(Join-Path $publishRoot 'Enlist.ControlPlane')"
    "PortalStaging=$(Join-Path $stagingRoot 'Enlist.Portal')"
    "PortalPublish=$(Join-Path $publishRoot 'Enlist.Portal')"
    "AgentStaging=$(Join-Path $stagingRoot 'Enlist.Agent')"
    "AgentPublish=$(Join-Path $publishRoot 'Enlist.Agent')"
    "RunnerPublish=$(Join-Path $publishRoot 'runner')"
    "RunnerLegacyPublish=$(Join-Path $publishRoot 'runner-legacy')"
)

$packages = @(
    @{ Source = 'ControlPlane.wxs'; Msi = 'Enlist.ControlPlane.msi' }
    @{ Source = 'Portal.wxs';       Msi = 'Enlist.Portal.msi' }
    @{ Source = 'Agent.wxs';        Msi = 'Enlist.Agent.msi' }
)

Push-Location $installerRoot
try {
    foreach ($p in $packages) {
        Write-Host "  building $($p.Msi)" -ForegroundColor DarkGray
        $args = @('wix', 'build', '-arch', 'x64', '-ext', 'WixToolset.Util.wixext', '-I', 'src')
        foreach ($d in $defs) { $args += @('-d', $d) }
        $args += @('-o', (Join-Path $outRoot $p.Msi), (Join-Path 'src' $p.Source))
        & dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "wix build failed for $($p.Msi)" }
    }

    # The bundle, last, because it embeds the three MSIs built above.
    $setup = Join-Path $outRoot "enList-$version-Setup.exe"
    Write-Host "  building $(Split-Path -Leaf $setup)" -ForegroundColor DarkGray
    $args = @('wix', 'build', '-arch', 'x64',
        '-ext', 'WixToolset.Util.wixext', '-ext', 'WixToolset.BootstrapperApplications.wixext', '-I', 'src',
        '-d', "ProductVersion=$version", '-d', "DotnetRuntimeVersion=$DotnetRuntimeVersion",
        '-d', "DotnetRuntimeMinimum=$DotnetRuntimeMinimum", '-d', "OutDir=$outRoot",
        '-d', "BaDir=$baDir",
        '-d', "IconFile=$(Join-Path $installerRoot 'ba\Enlist.Installer.Ba\media\enlist.ico')",
        '-o', $setup, 'src\Bundle.wxs', 'src\Prerequisites.wxs')
    if ($StandardBootstrapper) { $args += @('-d', 'StandardBootstrapper=1') }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed for the bundle' }
}
finally { Pop-Location }

Write-Host ""
Get-ChildItem $outRoot -Filter *.msi | ForEach-Object {
    Write-Host ("  {0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green
}
