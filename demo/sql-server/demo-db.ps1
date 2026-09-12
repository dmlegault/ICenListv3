<#
.SYNOPSIS
    Manages the demo SQL Server container on WSL containers (wslc). Does not touch LocalDB, which is
    what the test suite uses.

.DESCRIPTION
    Wraps wslc with the things it does not do on its own: creating the named volume the data lives
    on, waiting for SQL Server to actually accept a login (the container reports "running" long
    before that), starting a stopped container again after a reboot, and keeping the package blob
    store in step with the database on a reset.

    No Docker anywhere. wslc ships with WSL 2.9.11 and later and needs no daemon, no Docker Desktop
    and no licence -- the same engine the agent's --container-engine wslc option uses.

.PARAMETER Action
    up         Create (first time) or start the container and block until it accepts a login.
    down       Shut SQL Server down cleanly and stop the container. It and its data volume SURVIVE -- the everyday stop.
    reset      Destroy the container, the volume AND the package blobs, for a demo from zero. Needs -Force.
    status     Container state, whether it accepts a login, and the databases on the server.
    connstring Print the connection string to point the control plane at.

.PARAMETER Force
    Required by `reset`, which is irreversible.

.EXAMPLE
    ./demo-db.ps1 up
    $env:ConnectionStrings__ControlPlane = ./demo-db.ps1 connstring
    dotnet run --project ../../src/Enlist.ControlPlane
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'down', 'reset', 'status', 'connstring')]
    [string]$Action = 'status',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $here '..\..')

$ContainerName = 'enlist-demo-sql'
$VolumeName = 'enlist-demo-sql-data'
$script:Wslc = $null

function Read-DemoEnv {
    # One file, parsed here, for everything: the container's environment, the connection string,
    # and talking to the server directly. Defaults for the optional settings live here too, so they
    # cannot drift out of step with a second copy.
    $envPath = Join-Path $here '.env'
    if (-not (Test-Path -LiteralPath $envPath)) {
        throw "No .env in $here. Copy .env.example to .env and set MSSQL_SA_PASSWORD first."
    }

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $envPath) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $split = $trimmed.IndexOf('=')
        if ($split -lt 1) { continue }
        $values[$trimmed.Substring(0, $split).Trim()] = $trimmed.Substring($split + 1).Trim()
    }

    if (-not $values.ContainsKey('MSSQL_SA_PASSWORD') -or $values['MSSQL_SA_PASSWORD'] -eq '') {
        throw "MSSQL_SA_PASSWORD is not set in $envPath."
    }
    if (-not $values.ContainsKey('MSSQL_PORT')) { $values['MSSQL_PORT'] = '14330' }
    if (-not $values.ContainsKey('MSSQL_TAG')) { $values['MSSQL_TAG'] = '2025-CU3-ubuntu-24.04' }

    return $values
}

function Get-WslcPath {
    # The WSL installer puts wslc.exe next to wsl.exe and does not always put it on PATH -- the same
    # lookup the agent's WslContainerEngine makes.
    $onPath = Get-Command wslc -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $installed = Join-Path $env:ProgramFiles 'WSL\wslc.exe'
    if (Test-Path -LiteralPath $installed) { return $installed }
    throw "wslc was not found on PATH or at $installed. It ships with WSL 2.9.11 and later: run 'wsl --update'."
}

function Invoke-Wslc {
    <#
        Every wslc call goes through here, for one Windows PowerShell 5.1 reason: redirecting a
        native command's stderr with 2>&1 wraps each line in an ErrorRecord, and under
        $ErrorActionPreference = 'Stop' that surfaces as a TERMINATING NativeCommandError -- even
        when the command exited 0. `wslc inspect` on a container that does not exist yet writes to
        stderr routinely, so without this the polling loop in Wait-ForSql would abort on its first
        iteration instead of waiting. Dropping to 'Continue' for the duration of the call and
        flattening ErrorRecords back to strings makes exit codes, not stream plumbing, the signal.

        Takes ONE array rather than loose remaining arguments: with ValueFromRemainingArguments,
        PowerShell binds anything that looks like a switch (sqlcmd's -W, say) as a parameter of THIS
        function first. A single array reaches wslc as plain strings.

        wslc ends most output with a boilerplate footer about filing issues; those lines are
        dropped so callers parse only what the command actually said.
    #>
    param([Parameter(Mandatory = $true, Position = 0)][string[]]$Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $script:Wslc @Arguments 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { "$_" }
        } | ForEach-Object { $_ -replace "`0", '' } | Where-Object {
            $_ -notmatch '^(The WSL |Learn more|https://aka\.ms|Copyright \(c\) Microsoft|For privacy information)'
        }
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = @($output)
            Text     = (@($output) -join [Environment]::NewLine).Trim()
        }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Invoke-WslcStreaming {
    # For the slow commands (pull) whose progress should reach the console rather than a variable.
    # Same stream-preference reasoning as Invoke-Wslc; the exit code stays the only success signal.
    param([string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $script:Wslc @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "wslc $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Assert-Wslc {
    $script:Wslc = Get-WslcPath
    # `version` prints from the CLI alone; `list` needs the container service to answer, and a
    # service that does not answer is the state worth reporting.
    $probe = Invoke-Wslc @('list', '--quiet')
    if ($probe.ExitCode -ne 0) {
        throw "wslc is installed but its container service is not answering: $($probe.Text)"
    }
}

function Find-Property {
    # Depth-first search of parsed JSON for one property name. wslc's inspect document nests the
    # State block, and this keeps the script indifferent to exactly how deep.
    param($Node, [string]$Name)
    if ($null -eq $Node) { return $null }
    if ($Node -is [System.Array]) {
        foreach ($item in $Node) {
            $found = Find-Property -Node $item -Name $Name
            if ($null -ne $found) { return $found }
        }
        return $null
    }
    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Node.PSObject.Properties) {
            if ($property.Name -eq $Name) { return $property.Value }
        }
        foreach ($property in $Node.PSObject.Properties) {
            $found = Find-Property -Node $property.Value -Name $Name
            if ($null -ne $found) { return $found }
        }
    }
    return $null
}

function Get-ContainerState {
    # Returns @{ Status = running|exited|created|... } or $null when the container does not exist.
    $inspect = Invoke-Wslc @('inspect', $ContainerName, '--format', 'json')
    if ($inspect.ExitCode -ne 0) { return $null }
    try {
        $document = $inspect.Text | ConvertFrom-Json
    }
    catch {
        throw "wslc inspect printed something that is not JSON: $($inspect.Text)"
    }
    $state = Find-Property -Node $document -Name 'State'
    if ($null -eq $state) { return $null }
    return @{ Status = $state.Status }
}

function Get-ConnectionString {
    param([hashtable]$Values)
    # TrustServerCertificate because the server presents a self-signed certificate it generates on
    # first boot, and Microsoft.Data.SqlClient encrypts by default and would otherwise refuse.
    return "Server=127.0.0.1,$($Values['MSSQL_PORT']);Database=EnlistControlPlane;User Id=sa;Password=$($Values['MSSQL_SA_PASSWORD']);TrustServerCertificate=True;"
}

function Invoke-ContainerSql {
    param([string]$Query, [hashtable]$Values)
    # Runs inside the container, so it needs no sqlcmd on the host and does not depend on the host
    # port mapping. Falls back to the pre-18 tools path so this keeps working if MSSQL_TAG is rolled
    # back to a 2019-era image.
    $result = Invoke-Wslc @(
        'exec', $ContainerName, '/opt/mssql-tools18/bin/sqlcmd',
        '-S', 'localhost', '-U', 'sa', '-P', $Values['MSSQL_SA_PASSWORD'],
        '-C', '-h', '-1', '-W', '-Q', $Query
    )
    if ($result.ExitCode -ne 0) {
        $result = Invoke-Wslc @(
            'exec', $ContainerName, '/opt/mssql-tools/bin/sqlcmd',
            '-S', 'localhost', '-U', 'sa', '-P', $Values['MSSQL_SA_PASSWORD'],
            '-h', '-1', '-W', '-Q', $Query
        )
    }
    if ($result.ExitCode -ne 0) {
        throw "sqlcmd failed inside the container: $($result.Text)"
    }
    return $result.Output
}

function Show-ContainerLog {
    param([int]$Lines = 20)
    Write-Host "  Last $Lines log lines:" -ForegroundColor Red
    (Invoke-Wslc @('logs', '--tail', "$Lines", $ContainerName)).Output | ForEach-Object { Write-Host "    $_" }
}

function Wait-ForSql {
    param([hashtable]$Values, [int]$TimeoutSeconds = 120)
    # A login is the readiness signal, tried from inside the container: the container reports
    # "running" from the moment its process exists, some 30 seconds before the server is usable.
    Write-Host "Waiting for SQL Server to accept a login (first boot creates the system databases; ~30s)..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = Get-ContainerState
        if ($null -eq $state -or $state.Status -eq 'exited') {
            # The image refuses to start at all on a bad ACCEPT_EULA or a password that fails SQL
            # Server's complexity rules, and says why near the top of its log.
            Write-Host "  container is not running." -ForegroundColor Red
            Show-ContainerLog
            throw "The container exited instead of starting. A password that fails SQL Server's complexity rules is the usual cause."
        }

        $probe = Invoke-Wslc @(
            'exec', $ContainerName, '/opt/mssql-tools18/bin/sqlcmd',
            '-S', 'localhost', '-U', 'sa', '-P', $Values['MSSQL_SA_PASSWORD'], '-C', '-Q', 'SELECT 1'
        )
        if ($probe.ExitCode -eq 0) {
            Write-Host "  ready." -ForegroundColor Green
            return
        }
        Start-Sleep -Seconds 3
    }
    Show-ContainerLog
    throw "Timed out after ${TimeoutSeconds}s waiting for SQL Server to accept a login."
}

function Test-VolumeExists {
    $list = Invoke-Wslc @('volume', 'list')
    return ($list.ExitCode -eq 0 -and ($list.Output | Where-Object { $_ -match "(^|\s)$([regex]::Escape($VolumeName))(\s|$)" }))
}

function Test-ImagePresent {
    param([string]$Image)
    $repo, $tag = $Image -split ':', 2
    $images = Invoke-Wslc @('images')
    return ($images.ExitCode -eq 0 -and ($images.Output | Where-Object { $_ -match "^$([regex]::Escape($repo))\s+$([regex]::Escape($tag))\s" }))
}

switch ($Action) {

    'up' {
        Assert-Wslc
        $values = Read-DemoEnv
        $image = "mcr.microsoft.com/mssql/server:$($values['MSSQL_TAG'])"
        $state = Get-ContainerState

        if ($null -ne $state -and $state.Status -eq 'running') {
            Write-Host "$ContainerName is already running."
        }
        elseif ($null -ne $state) {
            # After a reboot (or `down`) the container still exists, stopped, with its volume attached.
            # wslc has no restart policy, so this is what brings it back.
            Write-Host "Starting the existing $ContainerName container (its data volume is intact)..."
            $start = Invoke-Wslc @('start', $ContainerName)
            if ($start.ExitCode -ne 0) { throw "wslc start failed: $($start.Text)" }
        }
        else {
            # A NAMED VOLUME, not a bind mount. SQL Server runs as non-root (uid 10001) inside the
            # image and needs to own its data directory; a Windows bind mount cannot express that
            # ownership and the server fails on its first write. The named volume also makes "wipe
            # the demo database" one explicit `volume remove` rather than a recursive delete aimed
            # at a host path.
            if (-not (Test-VolumeExists)) {
                Write-Host "Creating volume $VolumeName..."
                $volume = Invoke-Wslc @('volume', 'create', $VolumeName)
                if ($volume.ExitCode -ne 0) { throw "wslc volume create failed: $($volume.Text)" }
            }

            if (-not (Test-ImagePresent -Image $image)) {
                # Pulled explicitly so the download progress reaches the console; `run` would pull
                # silently and look hung for the minutes a 1.5 GB image takes.
                Write-Host "Pulling $image (about 1.5 GB, once)..."
                Invoke-WslcStreaming @('pull', $image)
            }

            Write-Host "Creating $ContainerName..."
            $run = Invoke-Wslc @(
                'run', '--detach', '--name', $ContainerName,
                '--label', 'enlist.demo=sql-server',

                # No host address: wslc publishes to 127.0.0.1 by default, which is exactly right for
                # a server running as sa with a password in a plaintext .env. Port 14330 rather than
                # 1433 so this cannot collide with a SQL Server already installed on the host.
                '--publish', "$($values['MSSQL_PORT']):1433",
                '--volume', "${VolumeName}:/var/opt/mssql",

                '--env', 'ACCEPT_EULA=Y',
                '--env', 'MSSQL_PID=Developer',
                '--env', "MSSQL_SA_PASSWORD=$($values['MSSQL_SA_PASSWORD'])",

                # PID 1 in this image is launch_sqlservr, which ignores SIGTERM, so a plain stop always
                # ends in a kill after this many seconds. `down` asks SQL Server to shut itself down
                # first and only falls back to that.
                '--stop-timeout', '10',

                $image
            )
            if ($run.ExitCode -ne 0) { throw "wslc run failed: $($run.Text)" }
        }

        Wait-ForSql -Values $values
        Write-Host ""
        Write-Host "Demo SQL Server is up on 127.0.0.1,$($values['MSSQL_PORT']) (user: sa)."
        Write-Host ""
        # The connection string carries the SA password, so it is NOT printed here. This ran on every
        # `up`, which put the password into any transcript, scrollback or CI log that captured it -
        # and `up` is the one action nobody thinks of as revealing a secret. `connstring` exists for
        # when the caller actually wants it, and says so on the line below.
        Write-Host "Point the control plane at it:" -ForegroundColor Cyan
        Write-Host "  `$env:ConnectionStrings__ControlPlane = (./demo-db.ps1 connstring)"
        Write-Host ""
        Write-Host "Then apply the schema and start it. The demo control plane runs in Production and"
        Write-Host "refuses to start on a pending migration rather than applying it, so migrate first:"
        Write-Host "  dotnet ef database update --project src/Enlist.ControlPlane"
        Write-Host "  dotnet run --project src/Enlist.ControlPlane"
        Write-Host ""
        Write-Host "(A Development run auto-migrates and needs no separate step - see demo/start-demo.ps1.)"
    }

    'down' {
        Assert-Wslc
        $state = Get-ContainerState
        if ($null -eq $state) {
            Write-Host "$ContainerName does not exist. Nothing to stop."
            return
        }
        if ($state.Status -ne 'running') {
            Write-Host "$ContainerName is already stopped ($($state.Status))."
            return
        }
        # A T-SQL SHUTDOWN checkpoints every database and exits in about a second, and the log says
        # "Server shut down by request from login sa". The engine's own stop cannot do that here:
        # PID 1 is launch_sqlservr, which ignores SIGTERM, so `wslc stop` alone ends in a SIGKILL
        # after the timeout and the next start pays for crash recovery. Afterwards the container
        # shows "Exited (255)" -- what a clean SQL Server exit looks like in wslc list; 137 is a kill.
        $values = Read-DemoEnv
        $shutdown = Invoke-Wslc @(
            'exec', $ContainerName, '/opt/mssql-tools18/bin/sqlcmd',
            '-S', 'localhost', '-U', 'sa', '-P', $values['MSSQL_SA_PASSWORD'], '-C', '-Q', 'SHUTDOWN'
        )
        # Judged by the server's reply, not sqlcmd's exit code: the server severs the session as it
        # goes down, which sqlcmd reports as a failure even though the request was honoured.
        if ($shutdown.Text -match 'shut down by request') {
            $deadline = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $deadline -and (Get-ContainerState).Status -eq 'running') { Start-Sleep -Milliseconds 500 }
        }
        else {
            Write-Host "SQL Server did not accept the shutdown request (still starting, or the password in .env is not the one the volume was created with); stopping the container instead." -ForegroundColor Yellow
        }
        if ((Get-ContainerState).Status -eq 'running') {
            $stop = Invoke-Wslc @('stop', '--time', '10', $ContainerName)
            if ($stop.ExitCode -ne 0) { throw "wslc stop failed: $($stop.Text)" }
        }
        Write-Host "Stopped. The container and its data volume were kept -- 'demo-db.ps1 up' brings the same data back." -ForegroundColor Green
    }

    'reset' {
        Assert-Wslc

        # Blobs are deleted alongside the database ON PURPOSE. Package bytes live on the host
        # filesystem under PackageStorage:Root, entirely outside SQL Server, so dropping the
        # database alone leaves them orphaned. The next demo then uploads a package whose bytes
        # ALREADY exist on disk (identical zips hash identically) against a metadata table that has
        # never heard of it -- the blob store and the database disagree from the first upload.
        # ControlPlaneTestServer hit exactly this and isolates a blob root per test for the same
        # reason; see the comment above packageStorageRoot in that file.
        $blobDirs = @(
            (Join-Path $repoRoot 'PackageBlobs'),
            (Join-Path $repoRoot 'src\Enlist.ControlPlane\PackageBlobs')
        ) | Where-Object { Test-Path -LiteralPath $_ }

        if (-not $Force) {
            Write-Host "reset is irreversible. It will delete:" -ForegroundColor Yellow
            Write-Host "  - container $ContainerName"
            Write-Host "  - volume $VolumeName (the entire demo database)"
            foreach ($d in $blobDirs) {
                $n = (Get-ChildItem -LiteralPath $d -File -ErrorAction SilentlyContinue).Count
                Write-Host "  - $d  ($n package blob(s))"
            }
            Write-Host ""
            Write-Host "Re-run with -Force to proceed." -ForegroundColor Yellow
            return
        }

        if ($null -ne (Get-ContainerState)) {
            $remove = Invoke-Wslc @('remove', '--force', $ContainerName)
            if ($remove.ExitCode -ne 0) { throw "wslc remove failed: $($remove.Text)" }
            Write-Host "  removed container $ContainerName"
        }
        if (Test-VolumeExists) {
            $volume = Invoke-Wslc @('volume', 'remove', $VolumeName)
            if ($volume.ExitCode -ne 0) { throw "wslc volume remove failed: $($volume.Text)" }
            Write-Host "  removed volume $VolumeName"
        }
        foreach ($d in $blobDirs) {
            Get-ChildItem -LiteralPath $d -File -ErrorAction SilentlyContinue | Remove-Item -Force
            Write-Host "  cleared $d"
        }
        Write-Host "Reset complete. 'demo-db.ps1 up' starts from an empty server." -ForegroundColor Green
    }

    'status' {
        Assert-Wslc
        $state = Get-ContainerState
        if ($null -eq $state) {
            Write-Host "$ContainerName does not exist. Create it with: ./demo-db.ps1 up"
            return
        }
        Write-Host "${ContainerName}: $($state.Status)"
        if ($state.Status -ne 'running') {
            Write-Host "Start it with: ./demo-db.ps1 up"
            return
        }
        $values = Read-DemoEnv
        $probe = Invoke-Wslc @(
            'exec', $ContainerName, '/opt/mssql-tools18/bin/sqlcmd',
            '-S', 'localhost', '-U', 'sa', '-P', $values['MSSQL_SA_PASSWORD'], '-C', '-Q', 'SELECT 1'
        )
        if ($probe.ExitCode -ne 0) {
            Write-Host "SQL Server is not accepting logins (still starting, or the password in .env is not the one the volume was created with)." -ForegroundColor Yellow
            return
        }
        Write-Host "SQL Server is accepting logins on 127.0.0.1,$($values['MSSQL_PORT'])."
        Write-Host ""
        Write-Host "Databases on the demo server:"
        Invoke-ContainerSql -Query "SET NOCOUNT ON; SELECT name FROM sys.databases ORDER BY name;" -Values $values
    }

    'connstring' {
        # Bare stdout so it composes: $env:ConnectionStrings__ControlPlane = ./demo-db.ps1 connstring
        Write-Output (Get-ConnectionString -Values (Read-DemoEnv))
    }
}
