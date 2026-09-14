#Requires -Version 5.1
<#
.SYNOPSIS
    Asks Docker and wslc whether they answer the identities an installed agent can run as.

.DESCRIPTION
    Answers the two questions Installer-UI-Design section 12 item 11 left open, by trying rather
    than reasoning:

      1. Does wslc work for an agent running as a NAMED USER account, rather than LocalSystem? A
         LocalSystem agent is already known not to work: `wslc list` hung for three minutes.
      2. Does Docker Desktop's engine answer a service when NOBODY IS SIGNED IN? Docker Desktop runs
         in a user's session, so after a reboot the engine may simply not be there yet.

    -Now answers the first, in about five minutes at most, by running the same two commands as three
    identities:

      you             this elevated shell, signed in
      LocalSystem     a scheduled task as SYSTEM - what the agent service runs as by default
      you, as a       a scheduled task with an S4U logon: your account, in a non-interactive session
      service         with no desktop - the nearest thing to a service configured to log on as you,
                      without anyone typing your password into anything

    -ArmStartupProbe sets up the second: a one-time task that runs as SYSTEM at the next boot and
    samples `docker version` and who is signed in, every two minutes, eight times - then deletes
    itself. Reboot, wait at the sign-in screen for at least four minutes, then sign in as normal. Once
    the machine has been up for about twenty minutes, -ReadStartupProbe prints what it saw and cleans up.

    Needs an elevated PowerShell: creating a task that runs as SYSTEM or with an S4U logon requires it.
    Everything it creates it removes, including when a command hangs.

.EXAMPLE
    .\probe-container-engines.ps1 -Now
    .\probe-container-engines.ps1 -ArmStartupProbe      # then reboot, wait 4+ minutes before signing in
    .\probe-container-engines.ps1 -ReadStartupProbe
#>
[CmdletBinding(DefaultParameterSetName = 'Now')]
param(
    [Parameter(ParameterSetName = 'Now')] [switch] $Now,

    # wslc as LocalSystem, all the way: does SYSTEM see any images, can the runner image download be
    # loaded into SYSTEM's store, and does it run from there - each step timed. -Now showed that wslc
    # answers SYSTEM (and that every identity, even an elevated token, gets its own empty store); this
    # is the question that follows.
    [Parameter(ParameterSetName = 'Wslc')] [switch] $Wslc,
    [Parameter(ParameterSetName = 'Wslc')] [string] $ImageFile = (Join-Path (Split-Path $PSScriptRoot -Parent) 'out\enlist-runner-3.0.0.tar'),
    [Parameter(ParameterSetName = 'Arm')] [switch] $ArmStartupProbe,
    [Parameter(ParameterSetName = 'Read')] [switch] $ReadStartupProbe,

    # Where -Now writes what it found, so it can be read without scrolling a console.
    [string] $Transcript = (Join-Path $env:TEMP 'enlist-engine-probe.txt')
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this from an elevated PowerShell: it creates scheduled tasks that run as SYSTEM and as you.' }

$wslc = Join-Path $env:ProgramFiles 'WSL\wslc.exe'
$probeRoot = Join-Path $env:ProgramData 'enList\engine-probe'
$startupTask = 'enlist-engine-probe-at-startup'
$me = "$env:USERDOMAIN\$env:USERNAME"

# The two commands, as cmd lines, each followed by its exit code. Docker first: wslc is the one that
# hangs, and a hang must not cost the Docker answer.
function Get-ProbeCommands([string] $output) {
    @(
        "echo === whoami >> `"$output`""
        "whoami >> `"$output`" 2>&1"
        "echo === docker >> `"$output`""
        "docker version --format `"{{.Server.Version}}`" >> `"$output`" 2>&1"
        "echo exit=%errorlevel% >> `"$output`""
        "echo === wslc >> `"$output`""
        "`"$wslc`" list --quiet >> `"$output`" 2>&1"
        "echo exit=%errorlevel% >> `"$output`""
        "echo === DONE >> `"$output`""
    )
}

# Reads the markers back into { who; docker; wslc }. A section with no exit= line is a command that
# never finished - which, for wslc, is the finding.
function Read-ProbeOutput([string] $text) {
    # [ \t]* after each marker, and it is not optional: `echo === docker >> file` writes "=== docker "
    # WITH the space before >>. The first run of this script matched the markers exactly, found none of
    # them, and reported every engine as NOT REACHED for every identity.
    function Section([string] $name) {
        $m = [regex]::Match($text, "(?s)=== $name[ \t]*\r?\n(.*?)(?=\r?\n=== |\z)")
        if (-not $m.Success) { return 'NOT REACHED' }
        $body = $m.Groups[1].Value.Trim()
        $exit = [regex]::Match($body, 'exit=(-?\d+)').Groups[1].Value
        $first = (($body -split "`r?`n") | Where-Object { $_ -and $_ -notmatch '^exit=' } | Select-Object -First 1)
        if ($exit -eq '0') { return "works ($first)" }
        if ($exit) { return "fails, exit ${exit}: $first" }
        return "HANGS - no answer$(if ($first) { " (printed: $first)" })"
    }
    $who = [regex]::Match([string] $text, '(?s)=== whoami[ \t]*\r?\n(.*?)\r?\n').Groups[1].Value.Trim()
    [pscustomobject]@{ Who = $who; Docker = Section 'docker'; Wslc = Section 'wslc'; Raw = [string] $text }
}

function Invoke-AsTask([string] $name, $principal, [int] $seconds) {
    New-Item -ItemType Directory -Force -Path $probeRoot | Out-Null
    $output = Join-Path $probeRoot "$name.txt"
    $cmdFile = Join-Path $probeRoot "$name.cmd"
    Remove-Item $output -Force -ErrorAction SilentlyContinue
    Set-Content -Path $cmdFile -Encoding ASCII -Value (@('@echo off') + (Get-ProbeCommands $output))

    # The output file is written by an identity other than this one, so it is opened up first.
    Set-Content -Path $output -Value '' -Encoding ASCII
    & icacls.exe $output /grant '*S-1-1-0:(M)' *>$null

    $task = "enlist-engine-probe-$name"
    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$cmdFile`""
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $task

    $deadline = (Get-Date).AddSeconds($seconds)
    $text = ''
    while ((Get-Date) -lt $deadline) {
        $text = Get-Content $output -Raw -ErrorAction SilentlyContinue
        if ($text -match '=== DONE') { break }
        Start-Sleep -Seconds 2
    }
    $text = Get-Content $output -Raw -ErrorAction SilentlyContinue

    # Ending the task ends cmd; a wslc it started can outlive that, in session 0, as that identity.
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='wslc.exe' AND SessionId=0" -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    return [string] $text
}

if ($Now) {
    $lines = New-Object System.Collections.Generic.List[string]
    function Say([string] $s) { $lines.Add($s); Write-Host $s }

    Say "Container engines, as the identities an installed agent can run as - $(Get-Date -Format s)"
    Say ""

    # You: the same commands, here. wslc is given a limit even here, so a hang cannot stall the script.
    $output = Join-Path $env:TEMP 'enlist-engine-probe-you.txt'
    $cmdFile = Join-Path $env:TEMP 'enlist-engine-probe-you.cmd'
    Remove-Item $output -Force -ErrorAction SilentlyContinue
    Set-Content -Path $cmdFile -Encoding ASCII -Value (@('@echo off') + (Get-ProbeCommands $output))
    $p = Start-Process cmd.exe -ArgumentList "/c `"$cmdFile`"" -WindowStyle Hidden -PassThru
    $null = $p.Handle
    if (-not $p.WaitForExit(150000)) { try { $p.Kill() } catch { } }
    $you = Read-ProbeOutput (Get-Content $output -Raw -ErrorAction SilentlyContinue)
    Remove-Item $output, $cmdFile -Force -ErrorAction SilentlyContinue

    $system = Read-ProbeOutput (Invoke-AsTask 'system' (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest) 150)
    $s4u = Read-ProbeOutput (Invoke-AsTask 's4u' (New-ScheduledTaskPrincipal -UserId $me -LogonType S4U -RunLevel Highest) 150)

    Say ("{0,-34} {1,-40} {2}" -f 'identity', 'docker', 'wslc')
    Say ("{0,-34} {1,-40} {2}" -f "you, signed in ($($you.Who))", $you.Docker, $you.Wslc)
    Say ("{0,-34} {1,-40} {2}" -f "LocalSystem ($($system.Who))", $system.Docker, $system.Wslc)
    Say ("{0,-34} {1,-40} {2}" -f "you as a service, S4U ($($s4u.Who))", $s4u.Docker, $s4u.Wslc)
    Say ""
    Say "S4U is a non-interactive logon of your account with no password stored - the nearest thing to a"
    Say "service set to log on as you. It has no network credentials, which neither engine needs locally."

    # The raw output too, always. The table is a reading of it, and a reading can be wrong - the first
    # run's was, and with the raw text already deleted there was nothing left to check it against.
    foreach ($probe in @(@('you', $you), @('LocalSystem', $system), @('S4U', $s4u))) {
        Say ""
        Say "--- raw: $($probe[0]) ---"
        Say $(if ($probe[1].Raw) { $probe[1].Raw.TrimEnd() } else { '(nothing was written)' })
    }

    Remove-Item $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
    [IO.File]::WriteAllLines($Transcript, $lines)
    Write-Host ""
    Write-Host "Written to $Transcript"
}

if ($Wslc) {
    if (-not (Test-Path $ImageFile)) { throw "No image download at $ImageFile. Run build.ps1 first, or pass -ImageFile." }
    $image = 'enlist/runner:' + [regex]::Match((Split-Path -Leaf $ImageFile), 'enlist-runner-(.+)\.tar$').Groups[1].Value

    New-Item -ItemType Directory -Force -Path $probeRoot | Out-Null
    $output = Join-Path $probeRoot 'wslc-deep.txt'
    $cmdFile = Join-Path $probeRoot 'wslc-deep.cmd'
    Set-Content -Path $output -Value '' -Encoding ASCII
    & icacls.exe $output /grant '*S-1-1-0:(M)' *>$null

    # Each step: a marker with the clock, the command, its exit code. !time! needs delayed expansion.
    function Step([string] $label, [string] $command) {
        @(
            "echo === $label at !time!>> `"$output`""
            "$command >> `"$output`" 2>&1"
            "echo exit=!errorlevel!>> `"$output`""
        )
    }
    $lines = @('@echo off', 'setlocal EnableDelayedExpansion', "echo === whoami>> `"$output`"", "whoami >> `"$output`" 2>&1")
    $lines += Step 'version (starts the session)' "`"$wslc`" version"
    $lines += Step 'images before' "`"$wslc`" image list"
    $lines += Step "load $ImageFile" "`"$wslc`" load -i `"$ImageFile`""
    $lines += Step 'images after' "`"$wslc`" image list"
    $lines += Step "run $image (prints the runner's usage and exits)" "`"$wslc`" run --pull never --rm $image"
    $lines += Step "remove $image again" "`"$wslc`" image remove $image"
    $lines += "echo === DONE at !time!>> `"$output`""
    Set-Content -Path $cmdFile -Encoding ASCII -Value $lines

    $task = 'enlist-engine-probe-wslc-deep'
    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$cmdFile`""
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    Write-Host "Running wslc as LocalSystem: version, image list, load, run, remove. Up to 10 minutes..."

    $deadline = (Get-Date).AddMinutes(10)
    $text = ''
    while ((Get-Date) -lt $deadline) {
        $text = Get-Content $output -Raw -ErrorAction SilentlyContinue
        if ($text -match '=== DONE') { break }
        Start-Sleep -Seconds 3
    }
    $text = Get-Content $output -Raw -ErrorAction SilentlyContinue
    if ($text -notmatch '=== DONE') { $text += "`r`n(NOT FINISHED within 10 minutes - the last step shown is the one that did not return)" }

    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='wslc.exe' AND SessionId=0" -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item $probeRoot -Recurse -Force -ErrorAction SilentlyContinue

    $report = "wslc as LocalSystem, all the way - $(Get-Date -Format s)`r`n`r`n$($text.Trim())"
    Write-Host $report
    [IO.File]::WriteAllText($Transcript, $report)
    Write-Host ""
    Write-Host "Written to $Transcript"
}

if ($ArmStartupProbe) {
    New-Item -ItemType Directory -Force -Path $probeRoot | Out-Null
    $output = Join-Path $probeRoot 'startup.txt'
    $cmdFile = Join-Path $probeRoot 'startup.cmd'
    Remove-Item $output -Force -ErrorAction SilentlyContinue

    # ping, not timeout: timeout refuses to run without a console, which a startup task does not have,
    # and would let all eight samples fire in the same second.
    $script = @(
        '@echo off'
        'setlocal EnableDelayedExpansion'
        "echo === boot !date! !time! >> `"$output`""
        'for /L %%i in (1,1,8) do ('
        '  ping -n 121 127.0.0.1 > nul'
        "  echo --- sample %%i at !time! >> `"$output`""
        "  echo signed in: >> `"$output`""
        "  quser >> `"$output`" 2>&1"
        "  echo docker as SYSTEM: >> `"$output`""
        "  docker version --format `"{{.Server.Version}}`" >> `"$output`" 2>&1"
        "  echo exit=!errorlevel! >> `"$output`""
        ')'
        "echo === finished !time! >> `"$output`""
        "schtasks /delete /tn $startupTask /f > nul 2>&1"
    )
    Set-Content -Path $cmdFile -Encoding ASCII -Value $script

    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$cmdFile`""
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $startupTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

    Write-Host "Armed: '$startupTask' runs once at the next boot, as SYSTEM, and deletes itself after ~17 minutes."
    Write-Host ""
    Write-Host "  1. Reboot."
    Write-Host "  2. Wait at the sign-in screen for AT LEAST 4 MINUTES - the first two samples need nobody signed in."
    Write-Host "  3. Sign in as normal, and let Docker Desktop start the way it always does."
    Write-Host "  4. About 20 minutes after the reboot, run: .\probe-container-engines.ps1 -ReadStartupProbe"
}

if ($ReadStartupProbe) {
    $output = Join-Path $probeRoot 'startup.txt'
    if (-not (Test-Path $output)) {
        Write-Host "No startup probe results at $output. Was the machine rebooted after -ArmStartupProbe?"
    }
    else {
        $text = Get-Content $output -Raw
        Write-Host $text
        [IO.File]::WriteAllText($Transcript, $text)
        Write-Host "Copied to $Transcript"
    }

    if (Get-ScheduledTask -TaskName $startupTask -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $startupTask -Confirm:$false
        Write-Host "(the startup task was still registered - removed)"
    }
    Remove-Item $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
}
