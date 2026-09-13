#Requires -Version 5.1
<#
.SYNOPSIS
    Regenerates the .NET prerequisite payload metadata in src\Prerequisites.wxs.

.DESCRIPTION
    The bundle carries no Microsoft installers, only a URL, a SHA-512 hash and a size for each. Burn
    downloads and verifies them at install time. That keeps 42 MB of somebody else's binaries out of
    this repository and out of every copy of the setup executable, and it means the hashes have to be
    regenerated whenever the required .NET patch changes.

    This downloads both installers to a temporary folder, runs `wix burn remotepayload` over them,
    prints the fragments, and deletes the downloads. It does not edit Prerequisites.wxs: paste the
    Hash, Size and Version across by hand and change $(DotnetRuntimeVersion) at the same time. Three
    values, once per .NET patch, is not worth the risk of a script rewriting XML it half understands.

    WHY A PINNED PATCH AT ALL. The .NET installers record themselves in the registry as one value per
    installed version, so detection can only ask "is this exact version here". Pinning is therefore
    not a limitation of this script; it is the shape of what can be detected. Being wrong costs a
    redundant prerequisite install, which the Microsoft installers no-op in seconds.

.PARAMETER Version
    The .NET patch to generate metadata for. Must be a real published version.

.EXAMPLE
    .\tools\refresh-prerequisites.ps1 -Version 10.0.13
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerRoot = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) "enlist-prereq-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $work | Out-Null

# The two runtimes, and deliberately not the ASP.NET Core Hosting Bundle. The hosting bundle is
# 117 MB against 11 MB because the difference is the IIS module, and nothing enList installs runs
# under IIS: the control plane and portal are Kestrel behind a Windows service. See the header of
# src\Prerequisites.wxs.
$payloads = @(
    @{ Name = 'dotnet-runtime';    Url = "https://builds.dotnet.microsoft.com/dotnet/Runtime/$Version/dotnet-runtime-$Version-win-x64.exe" }
    @{ Name = 'aspnetcore-runtime'; Url = "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/$Version/aspnetcore-runtime-$Version-win-x64.exe" }
)

try {
    foreach ($payload in $payloads) {
        $file = Join-Path $work (Split-Path -Leaf $payload.Url)
        Write-Host "Downloading $($payload.Url)" -ForegroundColor DarkGray

        # A 404 here means the version does not exist. Say so plainly rather than handing wix an
        # HTML error page to hash.
        try { Invoke-WebRequest -Uri $payload.Url -OutFile $file -UseBasicParsing }
        catch { throw "Could not download $($payload.Url). Check that .NET $Version is published. ($($_.Exception.Message))" }

        Write-Host ""
        Write-Host "=== $($payload.Name) ===" -ForegroundColor Cyan
        Push-Location $installerRoot
        try { & dotnet wix burn remotepayload -packagetype exe -du $payload.Url $file }
        finally { Pop-Location }
        Write-Host ""
    }

    Write-Host "Copy Hash, Size and Version into src\Prerequisites.wxs, and set" -ForegroundColor Yellow
    Write-Host "-DotnetRuntimeVersion $Version in build.ps1's default." -ForegroundColor Yellow
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
