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
  The images an icon is made of, hashed and joined into one key, so "does this carry enlist.ico" is an
  equality rather than a judgement. An .ico file and an icon resource store each image identically;
  only the directory in front of them differs (an .ico points at file offsets, a resource group at
  RT_ICON ids), so reading through each directory to the images gives the same key for the same icon.

  C# 5 syntax throughout: Add-Type in Windows PowerShell compiles with the framework's own csc, which
  predates expression-bodied members.
#>
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Enlist {
    public static class IconImages {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        delegate bool EnumNamesProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
        [DllImport("kernel32.dll")] static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumNamesProc callback, IntPtr param);
        [DllImport("kernel32.dll")] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
        [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);

        const uint LoadAsDataFile = 0x2;
        static readonly IntPtr RtIcon = (IntPtr)3;
        static readonly IntPtr RtGroupIcon = (IntPtr)14;

        static string Hash(byte[] bytes) {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes));
        }

        static string Key(IEnumerable<string> hashes) { return string.Join("|", hashes.OrderBy(h => h)); }

        public static string OfIcoFile(string path) {
            var file = File.ReadAllBytes(path);
            int count = BitConverter.ToUInt16(file, 4);
            var hashes = new List<string>();
            for (int i = 0; i < count; i++) {
                int entry = 6 + i * 16;
                var image = new byte[BitConverter.ToInt32(file, entry + 8)];
                Buffer.BlockCopy(file, BitConverter.ToInt32(file, entry + 12), image, 0, image.Length);
                hashes.Add(Hash(image));
            }
            return Key(hashes);
        }

        // One key per icon group in the executable; a file with no icon returns none.
        public static string[] OfExecutable(string path) {
            var module = LoadLibraryEx(path, IntPtr.Zero, LoadAsDataFile);
            if (module == IntPtr.Zero) return new string[0];
            var keys = new List<string>();
            try {
                EnumResourceNames(module, RtGroupIcon, delegate (IntPtr m, IntPtr type, IntPtr name, IntPtr param) {
                    var group = Read(m, name, type);
                    int count = BitConverter.ToUInt16(group, 4);
                    var hashes = new List<string>();
                    for (int i = 0; i < count; i++) {
                        var image = Read(m, (IntPtr)BitConverter.ToUInt16(group, 6 + i * 14 + 12), RtIcon);
                        hashes.Add(image == null ? "missing" : Hash(image));
                    }
                    keys.Add(Key(hashes));
                    return true;
                }, IntPtr.Zero);
            }
            finally { FreeLibrary(module); }
            return keys.ToArray();
        }

        static byte[] Read(IntPtr module, IntPtr name, IntPtr type) {
            var resource = FindResource(module, name, type);
            if (resource == IntPtr.Zero) return null;
            var bytes = new byte[SizeofResource(module, resource)];
            Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, bytes.Length);
            return bytes;
        }
    }

    // A package's embedded cabinet, written to a file expand.exe can open. msi.dll directly rather
    // than the WindowsInstaller COM object, because COM hands a stream back as a string and a
    // cabinet is not one.
    public static class MsiCabinet {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr database);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);
        [DllImport("msi.dll")] static extern uint MsiViewExecute(IntPtr view, IntPtr record);
        [DllImport("msi.dll")] static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint size);
        [DllImport("msi.dll")] static extern uint MsiRecordDataSize(IntPtr record, uint field);
        [DllImport("msi.dll")] static extern uint MsiRecordReadStream(IntPtr record, uint field, byte[] buffer, ref uint size);
        [DllImport("msi.dll")] static extern uint MsiCloseHandle(IntPtr handle);

        static void Ok(uint result, string what) {
            if (result != 0) throw new InvalidOperationException(what + " failed with Windows Installer error " + result);
        }

        static IntPtr FetchFirst(IntPtr database, string query) {
            IntPtr view, record;
            Ok(MsiDatabaseOpenView(database, query, out view), query);
            try {
                Ok(MsiViewExecute(view, IntPtr.Zero), query);
                if (MsiViewFetch(view, out record) != 0) throw new InvalidOperationException("no row for " + query);
                return record;
            }
            finally { MsiCloseHandle(view); }
        }

        // Every file the package installs, as { File key, long file name, Directory Id }.
        public static string[][] Files(string package) {
            IntPtr database, view, record;
            Ok(MsiOpenDatabase(package, IntPtr.Zero, out database), "opening " + package);
            try {
                const string query = "SELECT `File`.`File`, `File`.`FileName`, `Component`.`Directory_` FROM `File`, `Component` WHERE `File`.`Component_` = `Component`.`Component`";
                Ok(MsiDatabaseOpenView(database, query, out view), query);
                var files = new System.Collections.Generic.List<string[]>();
                try {
                    Ok(MsiViewExecute(view, IntPtr.Zero), query);
                    while (MsiViewFetch(view, out record) == 0) {
                        try {
                            // FileName is "SHORT~1.EXE|long-name.exe" when the long name is not 8.3.
                            var name = Field(record, 2);
                            var longName = name.Contains("|") ? name.Substring(name.IndexOf('|') + 1) : name;
                            files.Add(new[] { Field(record, 1), longName, Field(record, 3) });
                        }
                        finally { MsiCloseHandle(record); }
                    }
                }
                finally { MsiCloseHandle(view); }
                return files.ToArray();
            }
            finally { MsiCloseHandle(database); }
        }

        // The File table key of the file called fileName in the directory with that Id - the name the
        // file carries inside the cabinet. Harvested files get generated keys, so a check has to look
        // the key up rather than name it. Exactly one match, or it is not the file the check means.
        public static string FileKey(string package, string directory, string fileName) {
            var matches = Files(package)
                .Where(f => f[2] == directory && string.Equals(f[1], fileName, StringComparison.OrdinalIgnoreCase))
                .Select(f => f[0])
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(package + " has " + matches.Length + " files named " + fileName + " in " + directory);
            return matches[0];
        }

        static string Field(IntPtr record, uint field) {
            uint size = 0;
            MsiRecordGetString(record, field, new StringBuilder(1), ref size);
            size++;
            var value = new StringBuilder((int)size);
            Ok(MsiRecordGetString(record, field, value, ref size), "reading a string field");
            return value.ToString();
        }

        public static void Export(string package, string cabinetPath) {
            IntPtr database;
            Ok(MsiOpenDatabase(package, IntPtr.Zero, out database), "opening " + package);  // IntPtr.Zero is read-only
            try {
                var media = FetchFirst(database, "SELECT `Cabinet` FROM `Media`");
                string cabinet;
                try {
                    uint size = 256;
                    var name = new StringBuilder((int)size);
                    Ok(MsiRecordGetString(media, 1, name, ref size), "reading the cabinet name");
                    cabinet = name.ToString();
                }
                finally { MsiCloseHandle(media); }

                // "#name" is a cabinet embedded as a stream; anything else is a file beside the package.
                if (!cabinet.StartsWith("#")) throw new InvalidOperationException(package + " does not embed its cabinet: " + cabinet);

                var stream = FetchFirst(database, "SELECT `Data` FROM `_Streams` WHERE `Name` = '" + cabinet.Substring(1) + "'");
                try {
                    uint size = MsiRecordDataSize(stream, 1);
                    var bytes = new byte[size];
                    Ok(MsiRecordReadStream(stream, 1, bytes, ref size), "reading " + cabinet);
                    File.WriteAllBytes(cabinetPath, bytes);
                }
                finally { MsiCloseHandle(stream); }
            }
            finally { MsiCloseHandle(database); }
        }
    }
}
'@

# The first column of the first row, or $null.
function Get-TableValue {
    param($Database, [string] $Query)

    $view = $Database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $Database, @($Query))
    try {
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if (-not $record) { return $null }
        return [string] $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
    }
    finally {
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
    }
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

# ---- Both hosts: the TLS certificate, which a service that serves https cannot start without -----
Write-Host ""
Write-Host "  The TLS certificate"
$thumbprint = 'A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4'
$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.ControlPlane.msi') `
    -Properties @{ CP_URLS = 'https://+:5293'; DB_AUTH = 'Windows'; CP_CERT = $thumbprint } `
    -Actions @('SetCpArgs', 'SetCpArgsWindowsAuth', 'SetCpArgsCertificate') `
    -ArgsProperty 'CP_SERVICE_ARGS' -ServiceExe 'Enlist.ControlPlane.exe'
$parsed = @(Split-CommandLine $line)
Assert-That ($parsed -contains "--Certificate:Thumbprint=$thumbprint") 'the control plane is told which certificate to serve'

$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.Portal.msi') `
    -Properties @{ PORTAL_URLS = 'https://+:5231'; PORTAL_CPURL = 'https://localhost:5293'; PORTAL_CERT = $thumbprint } `
    -Actions @('SetPortalArgs', 'SetPortalArgsControlPlane', 'SetPortalArgsCertificate') `
    -ArgsProperty 'PORTAL_SERVICE_ARGS' -ServiceExe 'Enlist.Portal.exe'
Assert-That (@(Split-CommandLine $line) -contains "--Certificate:Thumbprint=$thumbprint") 'the portal is told which certificate to serve'

# A thumbprint is not a secret - it NAMES a certificate rather than granting access to one - which is
# the whole reason it may sit on a command line the machine can read. The private key is what matters,
# and the bootstrapper grants the service account read access to it separately.
$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.ControlPlane.msi') `
    -Properties @{ CP_URLS = 'https://+:5293'; DB_AUTH = 'Windows'; CP_CERT = '' } `
    -Actions @('SetCpArgs', 'SetCpArgsWindowsAuth', 'SetCpArgsCertificate') `
    -ArgsProperty 'CP_SERVICE_ARGS' -ServiceExe 'Enlist.ControlPlane.exe'
Assert-That ($line -notmatch 'Certificate:Thumbprint') 'an unset certificate is omitted rather than passed empty'

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

# DB_SERVER is the one property with NO default, and these two checks changed direction on
# 2026-09-13 because of it. It used to fall back to (localdb)\MSSQLLocalDB, which cannot work for a
# service - a LocalDB instance belongs to whoever starts it, so the database the installer creates as
# the operator is not the one the service sees. Now an unset server omits the connection string
# entirely, and the control plane's own refusal is what the operator reads.
Assert-That ($line -notmatch 'ConnectionStrings') 'an unset DB_SERVER writes no connection string, rather than one naming LocalDB'
Assert-That ($line -notmatch 'Server=;') 'the connection string never names an empty server'
Assert-That ($line -notmatch 'localdb') 'no install can be pointed at LocalDB by default, because a service cannot use it'
Assert-That ($line -match 'https://\+:5293') 'an empty CP_URLS falls back to the documented listener'

# DB_NAME's fallback needs a SERVER to be observed at all, now that an unset server writes no
# connection string. Named separately rather than folded into the case above, because the two
# properties no longer behave the same way and a check that tested both at once would be asserting
# the wrong thing about one of them.
$line = Get-ServiceCommandLine `
    -Msi (Join-Path $OutDir 'Enlist.ControlPlane.msi') `
    -Properties @{ CP_URLS = ''; DB_SERVER = 'sql01.corp.local'; DB_NAME = ''; DB_AUTH = '' } `
    -Actions @('SetCpArgs', 'SetCpArgsWindowsAuth') `
    -ArgsProperty 'CP_SERVICE_ARGS' -ServiceExe 'Enlist.ControlPlane.exe'
Assert-That ($line -match 'Database=EnlistControlPlane') 'an empty DB_NAME falls back, given a server to go with it'
Assert-That ($line -match 'Server=sql01\.corp\.local') 'and the server that was given is the one used'

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

        <#
          The bootstrapper decides which post-install steps to run by the package IDs Burn reports
          executing, compared against PostInstall.Packages. If the two ever disagreed - a package
          renamed in Bundle.wxs and not in the library - every step for that component would silently
          stop running and the install would still report success. So the constants are read out of
          the built assembly itself rather than restated here, which would only test that two copies of
          the same mistake agree.
        #>
        $detectionDll = Join-Path $PSScriptRoot 'ba\Enlist.Installer.Ba\bin\Release\net472\Enlist.Installer.Detection.dll'
        if (Test-Path $detectionDll) {
            $bytes = [IO.File]::ReadAllBytes($detectionDll)
            $asm = [Reflection.Assembly]::Load($bytes)
            $packagesType = $asm.GetTypes() | Where-Object { $_.FullName -eq 'Enlist.Installer.Detection.PostInstall+Packages' }
            $expected = @($packagesType.GetFields() | ForEach-Object { [string] $_.GetValue($null) })
            foreach ($id in $expected) {
                Assert-That ($chain -contains $id) "the bundle has a package with Id '$id', which the post-install gating keys on"
            }
        }
        else {
            Assert-That $false "the bootstrapper is built, so its package IDs can be checked against the bundle"
        }

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

        <#
          THE iC MARK, on every installer. Windows shows an icon for enList in four places, each set in
          a different way - which is exactly why they drifted apart. The bundle's IconSourceFile was set
          before anyone looked at Installed apps; the bootstrapper executable's ApplicationIcon was
          missed until it sat in Task Manager with the generic glyph beside a setup.exe carrying the
          real one; and the three packages had none at all, which only shows when one is installed
          directly with msiexec - the moment an operator is already off the usual path.

          Compared as the exact image bytes, not as pictures. The first version of this check rendered
          both icons at 32x32 and compared the pixels, and could not tell the generic application icon
          from the real one: the rendering differs between an .ico and the same images read back out
          of a resource. The images themselves do not - an icon resource carries each image of the
          .ico verbatim, and a package's Icon table carries the whole file - so a hash says yes or no
          where a picture only says "similar".

          The bootstrapper executable is the one out of the bundle just extracted, not the build
          output beside it: a stale payload is precisely the kind of thing that would carry the old
          icon while the fresh build looked right.
        #>
        Write-Host ""
        Write-Host "  Every installer, and every enList executable it installs, carries the iC mark"
        $iconFile = Join-Path $PSScriptRoot 'ba\Enlist.Installer.Ba\media\enlist.ico'
        $expected = [Enlist.IconImages]::OfIcoFile($iconFile)

        Assert-That (@([Enlist.IconImages]::OfExecutable($setup.FullName)) -contains $expected) 'the bundle carries it, which is what Installed apps shows'
        Assert-That (@([Enlist.IconImages]::OfExecutable((Join-Path $extract 'ba\Enlist.Installer.Ba.exe'))) -contains $expected) 'the bootstrapper executable carries it, which is what Task Manager shows'

        # Negative control: an unbranded executable must not pass, or the two checks above prove nothing.
        Assert-That (-not (@([Enlist.IconImages]::OfExecutable((Get-Command powershell.exe).Source)) -contains $expected)) 'an executable without the iC mark is told apart (control)'

        <#
          The services and the runners, read out of the packages that install them. Each MSI's cabinet
          is written out and the executable expanded from it by its File table key, so what is checked
          is the file a machine actually receives - an icon set in the project but missing from a stale
          publish\ would pass a check of the build output and fail this one.

          Found by directory and name rather than by key: the service executables have hand-written
          keys, but the runners are harvested, and a harvested key is generated. Both runners are
          called enlist-runner.exe (Enlist.Runner.Legacy.csproj says why), so the directory is what
          tells them apart.
        #>
        $executables = @(
            @{ Msi = 'Enlist.ControlPlane.msi'; Directory = 'INSTALLFOLDER';   Exe = 'Enlist.ControlPlane.exe'; Shown = 'the running service' },
            @{ Msi = 'Enlist.Portal.msi';       Directory = 'INSTALLFOLDER';   Exe = 'Enlist.Portal.exe';       Shown = 'the running service' },
            @{ Msi = 'Enlist.Agent.msi';        Directory = 'INSTALLFOLDER';   Exe = 'enlist-agent.exe';        Shown = 'the running service' },
            @{ Msi = 'Enlist.Agent.msi';        Directory = 'RUNNERDIR';       Exe = 'enlist-runner.exe';       Shown = 'each application the agent hosts'; Label = 'runner\enlist-runner.exe' },
            @{ Msi = 'Enlist.Agent.msi';        Directory = 'RUNNERLEGACYDIR'; Exe = 'enlist-runner.exe';       Shown = 'each .NET Framework application the agent hosts'; Label = 'runner-legacy\enlist-runner.exe' },
            # The copy the agent's project reference to Enlist.Runner lays beside enlist-agent.exe. The
            # agent starts runners from runner\, not this one, but it is installed, so it is branded.
            @{ Msi = 'Enlist.Agent.msi';        Directory = 'INSTALLFOLDER';   Exe = 'enlist-runner.exe';       Shown = 'it if anything starts it'; Label = 'the enlist-runner.exe copy in the agent folder' }
        )
        foreach ($executable in $executables) {
            $label = if ($executable.ContainsKey('Label')) { $executable.Label } else { $executable.Exe }
            [string] $package = Join-Path $OutDir $executable.Msi
            [string] $unpacked = Join-Path ([IO.Path]::GetTempPath()) "enlist-cab-$([guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory $unpacked | Out-Null
            $carries = $false
            try {
                $fileKey = [Enlist.MsiCabinet]::FileKey($package, $executable.Directory, $executable.Exe)
                [string] $cabinet = Join-Path $unpacked 'package.cab'
                [Enlist.MsiCabinet]::Export($package, $cabinet)
                & expand.exe $cabinet "-F:$fileKey" $unpacked | Out-Null
                $extracted = Join-Path $unpacked $fileKey
                $carries = (Test-Path $extracted) -and (@([Enlist.IconImages]::OfExecutable($extracted)) -contains $expected)
            }
            catch { Write-Host "          $($_.Exception.Message)" -ForegroundColor Red }
            finally { Remove-Item -Recurse -Force $unpacked -ErrorAction SilentlyContinue }
            Assert-That $carries "$label as installed by $($executable.Msi) carries it, which is what Task Manager shows for $($executable.Shown)"
        }

        foreach ($msi in 'Enlist.ControlPlane.msi', 'Enlist.Portal.msi', 'Enlist.Agent.msi') {
            # [string], and it matters: Join-Path hands back a wrapped string that COM rejects with
            # DISP_E_TYPEMISMATCH, which the first version of this check swallowed and reported as a
            # package with no icon.
            [string] $package = Join-Path $OutDir $msi
            [string] $exported = Join-Path ([IO.Path]::GetTempPath()) "enlist-icon-$([guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory $exported | Out-Null
            $installer = New-Object -ComObject WindowsInstaller.Installer
            $database = $null
            try {
                $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($package, 0))
                $arp = [string] (Get-TableValue $database "SELECT Value FROM Property WHERE Property = 'ARPPRODUCTICON'")
                # Export writes each binary column to Icon\<name>.ibd, which is the file as it was embedded.
                $database.GetType().InvokeMember('Export', 'InvokeMethod', $null, $database, @('Icon', $exported, 'Icon.idt')) | Out-Null
            }
            finally {
                if ($database) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
            }

            # ARPPRODUCTICON names an Icon row; a property pointing at a row that is not there shows the
            # generic glyph just as surely as no property at all.
            $embedded = Join-Path $exported "Icon\$arp.ibd"
            $carries = $arp -and (Test-Path $embedded) -and
                ((Get-FileHash $embedded).Hash -eq (Get-FileHash $iconFile).Hash)
            Remove-Item -Recurse -Force $exported -ErrorAction SilentlyContinue
            Assert-That $carries "$msi carries it, which is what Installed apps shows if it is installed on its own"
        }
    }
    finally { Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue }
}

# ---- Nothing design-time ships ---------------------------------------------------------------------
<#
  `dotnet ef` needs Microsoft.EntityFrameworkCore.Design and everything it pulls in - Roslyn, MSBuild
  loading, T4 templating, Humanizer - and a running control plane needs none of it. PrivateAssets=all
  keeps the assemblies out of publish, but Roslyn's build host arrives as NuGet content files, which
  take another route: Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe and about 4 MB beside it
  were installed with every control plane until 2026-09-14.

  Read from each package's File table, so the check sees what installs rather than what a project was
  meant to publish - which also covers a stale publish\ folder, the other way it stayed in.
#>
Write-Host ""
Write-Host "  Nothing design-time ships"
$designTime = '(?i)BuildHost|^Microsoft\.CodeAnalysis\.|^Microsoft\.EntityFrameworkCore\.Design\.dll$|^Microsoft\.Build\.|^Mono\.TextTemplating\.|^Humanizer\.'
foreach ($msi in 'Enlist.ControlPlane.msi', 'Enlist.Portal.msi', 'Enlist.Agent.msi') {
    $found = @([Enlist.MsiCabinet]::Files([string] (Join-Path $OutDir $msi)) | ForEach-Object { $_[1] } | Where-Object { $_ -match $designTime } | Sort-Object -Unique)
    Assert-That ($found.Count -eq 0) "$msi installs no design-time tooling$(if ($found.Count) { " (found $($found.Count): $(($found | Select-Object -First 4) -join ', '))" })"
}

# ---- The runner image the agent is told to use -----------------------------------------------------
<#
  The agent package names a runner image for every agent that runs applications in containers, and
  until 2026-09-14 nothing built an image by that name. The agent never pulls (--pull never), so an
  image that is not on the machine is a clear failure rather than a download - which makes producing
  it part of the build, and this the check that the build did.

  The name is read out of Enlist.Agent.msi - the AGENT_IMAGE default its SetProperty action carries -
  rather than composed here, so a change to the default and a build that forgot to follow it cannot
  both pass.

  What is checked is the DOWNLOAD in out\, not Docker's image store, because the download is what an
  operator is handed and side-loads (Installer-UI-Design section 12 item 5). A `docker save` file is
  a plain tar with its manifest and image config inside, so the tag it loads under, its version label
  and its source hash can all be read with tar.exe - no Docker needed to verify it.
#>
Write-Host ""
Write-Host "  The runner image download, and the image the agent is told to use"
[string] $agentMsi = Join-Path $OutDir 'Enlist.Agent.msi'
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
try {
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($agentMsi, 0))
    $namedImage = [string] (Get-TableValue $database "SELECT Target FROM CustomAction WHERE Source = 'AGENT_IMAGE'")
}
finally {
    if ($database) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
}
$productVersion = ([xml](Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Assert-That ($namedImage -eq "enlist/runner:$productVersion") "the agent package names enlist/runner:$productVersion (found '$namedImage')"

$imageFile = Join-Path $OutDir "enlist-runner-$productVersion.tar"
$haveImageFile = Test-Path $imageFile
Assert-That $haveImageFile "out\ has the runner image download, $(Split-Path -Leaf $imageFile) (build.ps1 without -SkipRunnerImage)"

if ($haveImageFile) {
    # Read defensively: strict mode turns a missing property into an exception, which would end the run
    # instead of failing the check - and an image built before these labels existed has none.
    function Get-JsonProperty($object, [string] $name) {
        if ($null -ne $object -and $object.PSObject.Properties[$name]) { return $object.PSObject.Properties[$name].Value }
        return $null
    }

    # -InputObject, not the pipeline: Windows PowerShell's ConvertFrom-Json hands a JSON array down the
    # pipeline as ONE object, so @(... | ConvertFrom-Json)[0] is the whole array and every property on
    # it reads as missing.
    $manifest = ConvertFrom-Json -InputObject ((& tar.exe -xOf $imageFile manifest.json) -join "`n")
    $manifestEntry = @($manifest)[0]
    $tags = @(Get-JsonProperty $manifestEntry 'RepoTags')
    Assert-That ($tags -contains $namedImage) "it loads as $namedImage, the name the agent package uses (found '$($tags -join ', ')')"

    # Never ask tar for an empty member name: `tar -xOf file ""` extracts EVERY member to stdout, which
    # for this file is 80 MB of layers poured into PowerShell strings. That happened once, through the
    # quirk above, and turned a one-second check into a ten-minute hang.
    $configMember = [string] (Get-JsonProperty $manifestEntry 'Config')
    $labels = $null
    if ($configMember) {
        $imageConfig = ConvertFrom-Json -InputObject ((& tar.exe -xOf $imageFile $configMember) -join "`n")
        $labels = Get-JsonProperty (Get-JsonProperty $imageConfig 'config') 'Labels'
    }

    $label = [string] (Get-JsonProperty $labels 'org.opencontainers.image.version')
    Assert-That ($label -eq $productVersion) "its version label says $productVersion (found '$label')"

    # Stale is the failure that matters and the one nothing else would notice: the tests' :dev image went
    # four days and seventeen runner commits out of date without a single failure. Compared by content -
    # tools\RunnerSourceHash.ps1 says why not by time.
    . (Join-Path $PSScriptRoot 'tools\RunnerSourceHash.ps1')
    $expectedHash = Get-RunnerSourceHash -RepoRoot (Split-Path $PSScriptRoot -Parent)
    $imageHash = [string] (Get-JsonProperty $labels 'enlist.source-sha256')
    Assert-That ($imageHash -eq $expectedHash) "it was built from the runner source as it is now (image $(if ($imageHash.Length -ge 12) { $imageHash.Substring(0, 12) } else { "'$imageHash'" }), source $($expectedHash.Substring(0, 12)))"

    # The checksum a recipient checks it against has to describe this file, or it teaches them to ignore it.
    $checksumFile = "$imageFile.sha256"
    $recorded = if (Test-Path $checksumFile) { ([IO.File]::ReadAllText($checksumFile)).Trim() } else { '' }
    $actual = "$((Get-FileHash $imageFile -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path -Leaf $imageFile)"
    Assert-That ($recorded -eq $actual) "its .sha256 matches the file, in the format sha256sum -c reads"

    # The README goes wherever the file goes. It has to point at the agent's Images folder - the way that
    # works for every engine and account - and name THIS file in both by-hand load commands.
    $imageName = Split-Path -Leaf $imageFile
    $readmeFile = Join-Path $OutDir "enlist-runner-$productVersion-README.md"
    $readmeText = if (Test-Path $readmeFile) { [IO.File]::ReadAllText($readmeFile) } else { '' }
    Assert-That ($readmeText.Contains('C:\ProgramData\enList\Agent\Images') -and $readmeText.Contains("docker load -i $imageName") -and $readmeText.Contains("wslc load -i $imageName")) "its README points at the agent's Images folder and says how to load it by hand with docker and wslc"
}

# ---- The agent's Images folder is locked down --------------------------------------------------------
<#
  The agent loads every enlist/runner archive in <data>\Images into its container engine, as SYSTEM. So
  whoever can write there chooses the runner image this machine's container applications run in, and
  ProgramData's default ACL lets any signed-in user create files. Enlist.Agent.msi therefore sets a
  PROTECTED DACL on that folder (MsiLockPermissionsEx) naming SYSTEM and Administrators, and nobody else -
  checked here in the built package, because an ACL that quietly went back to inheriting would look
  exactly the same in every other respect.
#>
Write-Host ""
Write-Host "  The agent's Images folder is locked down"
[string] $agentPackage = Join-Path $OutDir 'Enlist.Agent.msi'
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
try {
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($agentPackage, 0))
    $imagesParent = [string] (Get-TableValue $database "SELECT Directory_Parent FROM Directory WHERE Directory = 'AGENTIMAGES'")
    $imagesName = [string] (Get-TableValue $database "SELECT DefaultDir FROM Directory WHERE Directory = 'AGENTIMAGES'")
    # SDDLText - the table's column is not called SDDL, and a wrong column name fails OpenView outright.
    $imagesSddl = [string] (Get-TableValue $database "SELECT ``SDDLText`` FROM ``MsiLockPermissionsEx`` WHERE ``LockObject`` = 'AGENTIMAGES'")
}
finally {
    if ($database) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
}
Assert-That ($imagesParent -eq 'AGENTDATA' -and $imagesName -eq 'Images') "Enlist.Agent.msi creates <data>\Images (found parent '$imagesParent', name '$imagesName')"
Assert-That ($imagesSddl -match '^D:P') "its DACL is protected, so it does not inherit ProgramData's (found '$imagesSddl')"
Assert-That ($imagesSddl -match ';;;SY\)' -and $imagesSddl -match ';;;BA\)') "it grants SYSTEM and Administrators"
Assert-That ($imagesSddl -notmatch ';;;(BU|AU|WD|IU)\)') "and not Users, Authenticated Users, Everyone or Interactive"

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
    # -SkipRunnerImage: this build exists to test an MSI upgrade, and an enlist/runner:3.0.1 image
    # left behind by it would be a release tag nobody meant to make.
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipPublish -SkipRunnerImage -Version '3.0.1' -OutputDirectory $upgradeDir | Out-Null
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







