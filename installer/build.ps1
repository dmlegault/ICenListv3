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
    [switch] $StandardBootstrapper,

    # Do not build the runner image. For a machine without Docker, or a build whose point is the MSIs
    # alone (verify.ps1 -Live's upgrade build). Said out loud when used, because the agent package still
    # names an image, and without this build nothing has produced it.
    [switch] $SkipRunnerImage
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

<#
  Each publish goes into an EMPTY folder. dotnet publish adds and overwrites but never removes, and
  the packages harvest the whole folder - so a file a project stopped publishing stayed in publish\
  and went on being installed. That is not hypothetical: when the control plane stopped publishing
  Roslyn's BuildHost folders, the old copies were still there, and the MSI would have gone on
  carrying them with nothing in the project to say why.
#>
if (-not $SkipPublish) {
    foreach ($c in $components) {
        $target = Join-Path $publishRoot $c.Name
        if (Test-Path $target) { Remove-Item -Recurse -Force $target }
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

<#
  The runner image, tagged with the product version: enlist/runner:<version>.

  That is the name the agent package gives every agent that runs applications in containers
  (AGENT_IMAGE in Bundle.wxs and Agent.wxs), and until 2026-09-14 nothing produced it - this
  repository built enlist/runner:dev for the tests and nothing else. An agent installed with a
  container engine therefore pointed at an image that existed nowhere, and while agents still
  pulled, a machine without it would have fetched whatever Docker Hub had under that name.

  Built with Docker from the same Dockerfile the tests' :dev image comes from. Two build arguments
  become labels that verify.ps1 reads back: VERSION, and SOURCE_SHA256 - a hash over exactly the files
  the image is built from, so a stale image is detected by content.

  THE DOWNLOAD. The installer does not carry this image; it is handed to operators as a separate file
  they side-load into their own engine (Installer-UI-Design section 12 item 5). So it is saved into
  out\ beside the installers, as enlist-runner-<version>.tar, with a .sha256 beside it in the format
  sha256sum -c reads. A plain tar and not gzipped: `docker save` already writes compressed layers, and
  gzip took 79.5 MB to 78.9. Both engines load it as it is:

      docker load -i enlist-runner-3.0.0.tar
      wslc load -i enlist-runner-3.0.0.tar

  Any earlier download in out\ is deleted first, including when this step is skipped - a file left
  from a previous build would otherwise be handed out as if it were this one.

  A missing Docker stops the build rather than skipping quietly: the packages would still name the
  image, and a build that says it succeeded while leaving that name unfilled is the defect this fixes.
#>
$runnerImage = "enlist/runner:$version"
$runnerImageFile = Join-Path $outRoot "enlist-runner-$version.tar"
$runnerImageReadme = Join-Path $outRoot "enlist-runner-$version-README.md"
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
Get-ChildItem $outRoot -Filter 'enlist-runner-*' -ErrorAction SilentlyContinue | Remove-Item -Force

if ($SkipRunnerImage) {
    Write-Host "  NOT building the runner image $runnerImage (-SkipRunnerImage), so out\ has no image download" -ForegroundColor Yellow
}
else {
    Write-Host "  building the runner image $runnerImage" -ForegroundColor DarkGray

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw "The runner image needs Docker, and there is no docker command on this machine. Run build.ps1 -SkipRunnerImage to build the MSIs without it."
    }

    # Native stderr under 'Stop' becomes a terminating error in Windows PowerShell, and a stopped
    # Docker daemon writes exactly that. Each call is judged by its exit code instead.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $engine = & docker version --format '{{.Server.Version}}' 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ("The runner image needs Docker, and its engine is not answering: $(($engine | Select-Object -First 1))`n" +
                "Start Docker Desktop, or run build.ps1 -SkipRunnerImage to build the MSIs without it.")
        }

        # The source hash verify.ps1 compares against - see tools\RunnerSourceHash.ps1 for why content
        # and not timestamps.
        . (Join-Path $installerRoot 'tools\RunnerSourceHash.ps1')
        $sourceHash = Get-RunnerSourceHash -RepoRoot $repoRoot

        # --quiet: BuildKit writes its progress to stderr, which would bury a real error in noise.
        $built = & docker build --quiet -f (Join-Path $repoRoot 'src\Enlist.Runner\Dockerfile') `
            --build-arg "VERSION=$version" --build-arg "SOURCE_SHA256=$sourceHash" -t $runnerImage $repoRoot 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "docker build failed for $runnerImage`n$($built -join "`n")"
        }

        Write-Host "  saving $(Split-Path -Leaf $runnerImageFile)" -ForegroundColor DarkGray
        $saved = & docker save -o $runnerImageFile $runnerImage 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "docker save failed for $runnerImage`n$($saved -join "`n")"
        }
    }
    finally { $ErrorActionPreference = $previous }

    # Two spaces and a bare file name, LF-terminated: what `sha256sum -c` expects, so a recipient on any
    # platform can check the file they were given. Get-FileHash reads the same value on Windows.
    $digest = (Get-FileHash $runnerImageFile -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$runnerImageFile.sha256", "$digest  $(Split-Path -Leaf $runnerImageFile)`n", (New-Object Text.UTF8Encoding $false))

    # Travels with the image, so whoever is handed the file is also handed what to do with it.
    $imageName = Split-Path -Leaf $runnerImageFile
    $readme = @(
        "# $imageName - the enList runner image"
        ''
        'A user loads it on each machine that runs containers:'
        ''
        '```'
        "docker load -i $imageName"
        '```'
        ''
        "or for the WSL engine: ``wslc load -i $imageName``."
        ''
    ) -join "`n"
    [IO.File]::WriteAllText($runnerImageReadme, $readme, (New-Object Text.UTF8Encoding $false))
}

# Harvest paths are resolved relative to the .wxs file, so every path handed to wix is absolute.
$defs = @(
    "ProductVersion=$version"
    # The same .ico everywhere - bundle, bootstrapper executable, wizard window and all three packages -
    # so enList looks like one product wherever Windows shows it.
    "IconFile=$(Join-Path $installerRoot 'ba\Enlist.Installer.Ba\media\enlist.ico')"
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
if (-not $SkipRunnerImage) {
    $file = Get-Item $runnerImageFile
    Write-Host ("  {0,-32} {1,8:N1} MB  the runner image download, for operators to side-load" -f $file.Name, ($file.Length / 1MB)) -ForegroundColor Green
}
