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
    [string] $DotnetRuntimeVersion = '10.0.12'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerRoot
$publishRoot = Join-Path $installerRoot 'publish'
$stagingRoot = Join-Path $installerRoot 'staging'
$outRoot = Join-Path $installerRoot 'out'

# One product version, from the one place that defines it.
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$version = ([xml](Get-Content $propsPath)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $propsPath." }
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
        '-d', "ProductVersion=$version", '-d', "DotnetRuntimeVersion=$DotnetRuntimeVersion", '-d', "OutDir=$outRoot",
        '-o', $setup, 'src\Bundle.wxs', 'src\Prerequisites.wxs')
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed for the bundle' }
}
finally { Pop-Location }

Write-Host ""
Get-ChildItem $outRoot -Filter *.msi | ForEach-Object {
    Write-Host ("  {0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) -ForegroundColor Green
}
