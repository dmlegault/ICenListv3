using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Data.SqlClient;

namespace Enlist.TestSupport;

/// <summary>
/// Spawns the real Enlist.ControlPlane.exe as its own OS process, bound to a free port, against a
/// throwaway LocalDB database unique to this instance — same "prove it against the real thing"
/// approach as StubAgent and AgentJobObjectTests, one level further up the stack.
/// The control plane runs its own EF Core migrations on startup, so this needs nothing beyond
/// pointing it at a fresh database name. Shared by Enlist.ControlPlane.Tests (pure API tests) and
/// Enlist.Agent.Tests (agent-vs-control-plane integration tests) rather than duplicated in each.
///
/// StopAsync / StartAgainAsync take the same control plane down and bring it back on the same address
/// with the same database — a control-plane restart as an agent experiences one (a deploy, a reboot),
/// which is the scenario the agent's reconnect handling exists for.
/// </summary>
public sealed class ControlPlaneTestServer : IAsyncDisposable
{
    private Process _process;
    private StringBuilder _output;
    private readonly string _databaseName;
    private readonly string _packageStorageRoot;
    private readonly IReadOnlyDictionary<string, string>? _extraEnvironment;

    public Uri BaseUri { get; }

    /// <summary>The throwaway database this instance runs against, for a test that has to look behind the API — a row count no endpoint exposes, say. Read-only by convention; the server owns the schema.</summary>
    public string ConnectionString { get; }

    /// <summary>Where this instance keeps its package blobs — for a test that has to prove nothing was left on disk.</summary>
    public string PackageStorageRoot => _packageStorageRoot;

    /// <summary>Everything the control plane has written to stdout and stderr so far - its log. For a test that has to prove a line was (or was not) logged, such as the audit line on a write.</summary>
    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private ControlPlaneTestServer(Process process, Uri baseUri, string databaseName, string packageStorageRoot, IReadOnlyDictionary<string, string>? extraEnvironment, StringBuilder output)
    {
        _process = process;
        _output = output;
        BaseUri = baseUri;
        _databaseName = databaseName;
        _packageStorageRoot = packageStorageRoot;
        _extraEnvironment = extraEnvironment;
        ConnectionString = ConnectionStringFor(databaseName);
    }

    public static Task<ControlPlaneTestServer> StartAsync(TimeSpan readyTimeout) => StartAsync(readyTimeout, extraEnvironment: null);

    /// <summary>
    /// extraEnvironment lets a test override things like PackageRetention__RetentionPeriod /
    /// PackageRetention__SweepInterval to run a retention sweep on a testable timescale instead of the
    /// real 30-day/6-hour defaults — or Authentication__Mode, which this class sets to Off by default so
    /// every existing test keeps calling the API anonymously (loopback, so the control plane allows it).
    /// listenUrls exists for the tests of the startup rules themselves, which need an address that is
    /// deliberately NOT loopback; passing it empty omits --urls entirely, for the rule tests that
    /// need the listener to come from somewhere else (ASPNETCORE_HTTP_PORTS).
    /// </summary>
    public static async Task<ControlPlaneTestServer> StartAsync(TimeSpan readyTimeout, IReadOnlyDictionary<string, string>? extraEnvironment, string listenUrls = "http://127.0.0.1:0")
    {
        // BEFORE the child is spawned, and it has to be. LocalDB is started by whichever process
        // connects to it first, as a CHILD of that process — so if a spawned control plane gets
        // there first, sqlservr.exe joins that child's process tree and the Kill(entireProcessTree)
        // in ProcessKill.Quietly takes the database server down with it, wrecking every other test
        // running in parallel. Pinning it to the test host first makes that impossible. See LocalDb.
        await LocalDb.EnsureStartedAsync().ConfigureAwait(false);

        var databaseName = "EnlistControlPlaneTest_" + Guid.NewGuid().ToString("N");

        // Package blobs default to a folder under the server's own content root, which is the SAME
        // physical location for every spawned instance (same built dll) — without isolating this too,
        // one test's uploaded package (byte-identical zips hash identically) would physically already
        // exist on disk for the NEXT test's freshly-created, otherwise-empty database, desyncing the
        // blob store and the metadata table in a way that only this kind of parallel-test setup would
        // ever hit.
        var packageStorageRoot = Path.Combine(Path.GetTempPath(), "enlist-cp-packages-" + Guid.NewGuid().ToString("N"));

        try
        {
            // Port 0 = Kestrel picks one and tells us which, via its startup log. See ListeningOn.
            var (process, baseUri, output) = await LaunchAsync(listenUrls, databaseName, packageStorageRoot, extraEnvironment, readyTimeout).ConfigureAwait(false);
            return new ControlPlaneTestServer(process, baseUri, databaseName, packageStorageRoot, extraEnvironment, output);
        }
        catch
        {
            // Cleaned up here as well as in DisposeAsync: on this path no ControlPlaneTestServer is ever
            // constructed, so nothing will be disposed and the blob directory would simply be abandoned.
            // Rare, but it is exactly the failure a machine under load produces, and a run that fails this
            // way leaves no object behind to tidy up after it.
            DeleteQuietly(packageStorageRoot);
            await DropDatabaseAsync(databaseName).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs one management verb (create-api-key, create-join-token, revoke-agent, ...) against THIS
    /// instance's database, the way an operator would on the control plane host, and returns its
    /// stdout — which is where the verb prints the secret it created. A nonzero exit fails the test
    /// with the verb's stderr.
    /// </summary>
    public async Task<string> RunCliAsync(params string[] verbAndOptions)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(RepoPaths.ControlPlaneDll());
        foreach (var argument in verbAndOptions)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["ConnectionStrings__ControlPlane"] = ConnectionString;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the control plane CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{string.Join(' ', verbAndOptions)}' exited {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
        }

        return await stdout.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one SQL statement against this instance’s database and returns the rows it affected.
    ///
    /// For the one thing a test cannot otherwise reach: TIME. The shortest expiry any surface accepts
    /// is an hour (CredentialIssuer.Expiry), deliberately, so a test that wants to prove expiry is
    /// actually ENFORCED cannot wait one out and has no clock to move. Ageing the row is the honest
    /// alternative to either loosening the parser for tests or introducing a time abstraction that
    /// only tests use - both of which would mean the thing under test is no longer the thing that
    /// ships. Everything else in this class drives the real surfaces.
    /// </summary>
    public async Task<int> ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>A join token minted by the CLI (options as the verb takes them: --uses, --expires), for a test that enrolls an agent the way an operator provisions one.</summary>
    public async Task<string> CreateJoinTokenAsync(params string[] options) =>
        Secret(await RunCliAsync(["create-join-token", .. options]).ConfigureAwait(false), "token");

    /// <summary>A management API key minted by the CLI, with the given role, for a test that drives the API as an operator would.</summary>
    public async Task<string> CreateApiKeyAsync(string name, string role, params string[] options) =>
        Secret(await RunCliAsync(["create-api-key", "--name", name, "--role", role, .. options]).ConfigureAwait(false), "key");

    /// <summary>The verbs print their one secret on a line of the form "  token: enlj_..." / "  key: enlk_...".</summary>
    private static string Secret(string cliOutput, string label)
    {
        var match = Regex.Match(cliOutput, $@"^\s*{label}:\s*(\S+)\s*$", RegexOptions.Multiline);
        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException($"The CLI printed no '{label}:' line:{Environment.NewLine}{cliOutput}");
    }

    /// <summary>Kills the control plane and waits until the process is actually gone. The database and blob store stay, so <see cref="StartAgainAsync"/> brings the SAME control plane back.</summary>
    public async Task StopAsync()
    {
        ProcessKill.Quietly(_process);
        await WaitForExitQuietlyAsync(_process).ConfigureAwait(false);
        _process.Dispose();
    }

    /// <summary>The counterpart of <see cref="StopAsync"/>: relaunches on the address the first launch chose, so an agent holding that address reconnects to it.</summary>
    public async Task StartAgainAsync(TimeSpan readyTimeout)
    {
        var (process, baseUri, output) = await LaunchAsync(BaseUri.ToString().TrimEnd('/'), _databaseName, _packageStorageRoot, _extraEnvironment, readyTimeout).ConfigureAwait(false);

        if (baseUri != BaseUri)
        {
            ProcessKill.Quietly(process);
            throw new InvalidOperationException($"Enlist.ControlPlane came back on {baseUri} rather than {BaseUri}; a relaunch has to keep the address an agent already holds.");
        }

        _process = process;
        _output = output;
    }

    private static async Task<(Process Process, Uri BaseUri, StringBuilder Output)> LaunchAsync(
        string urls, string databaseName, string packageStorageRoot, IReadOnlyDictionary<string, string>? extraEnvironment, TimeSpan readyTimeout)
    {
        var connectionString = ConnectionStringFor(databaseName);

        var dll = RepoPaths.ControlPlaneDll();
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException($"Enlist.ControlPlane.dll not found at {dll} - build src/Enlist.ControlPlane first.", dll);
        }

        // Captured, not inherited. Without this the child's own stderr goes to the test runner's console
        // and is effectively lost, leaving "exited early with code -532462766" as the entire diagnosis —
        // which says only "a .NET process threw", never WHAT. Both failure paths below now quote it.
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(dll);

        // An EMPTY listenUrls deliberately passes no --urls at all, so the child falls through to
        // whatever else configures a listener - which is the only way to exercise
        // ASPNETCORE_HTTP_PORTS, the variable the official ASP.NET Core container images set and
        // the one ListenerRules was blind to until 2026-09-12.
        if (!string.IsNullOrWhiteSpace(urls))
        {
            psi.ArgumentList.Add("--urls");
            psi.ArgumentList.Add(urls);
        }
        psi.Environment["ConnectionStrings__ControlPlane"] = connectionString;
        psi.Environment["PackageStorage__Root"] = packageStorageRoot;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        // Off by default so the rest of the suite keeps calling the API anonymously; the authentication
        // tests override it to Required. Allowed because the address is loopback.
        psi.Environment["Authentication__Mode"] = "Off";

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                psi.Environment[key] = value;
            }
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Enlist.ControlPlane.");

        // Drained continuously on background tasks: a redirected pipe nobody reads fills up and BLOCKS
        // the child, which would turn a diagnostic aid into a hang.
        //
        // Read LINE BY LINE, never ReadToEndAsync. ReadToEndAsync only returns at end-of-stream — i.e.
        // when the child exits — so the buffer would stay empty for the entire life of a HEALTHY server,
        // and the "Now listening on" line this class needs in order to learn the port would never arrive
        // until it was far too late to be useful.
        var output = new System.Text.StringBuilder();

        async Task DrainAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
            }
        }

        _ = Task.Run(() => DrainAsync(process.StandardOutput));
        _ = Task.Run(() => DrainAsync(process.StandardError));

        string CapturedOutput()
        {
            lock (output) { return output.Length == 0 ? "(no output captured)" : output.ToString().Trim(); }
        }

        using var http = new HttpClient();
        var deadline = DateTime.UtcNow + readyTimeout;
        Uri? baseUri = null;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                // Give the drain tasks a moment to finish; a process that just died usually has its
                // reason in the last line it wrote.
                await Task.Delay(250).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Enlist.ControlPlane exited early with code {process.ExitCode} before becoming ready " +
                    $"(database {databaseName}).{Environment.NewLine}" +
                    $"--- control plane output ---{Environment.NewLine}{CapturedOutput()}");
            }

            // The address is not known until Kestrel has bound and said so, which is the whole point of
            // letting the child choose. Until then there is nothing to poll.
            if (baseUri is null)
            {
                var match = ListeningOn.Match(CapturedOutput());
                if (match.Success)
                {
                    baseUri = new Uri(match.Groups[1].Value.TrimEnd('/') + "/");
                }
            }

            if (baseUri is not null)
            {
                try
                {
                    // /health is anonymous in every authentication mode, and a 200 from it means the
                    // database is reachable and migrations finished — exactly "ready". The previous
                    // probe, /api/application-policies, would be a 401 under Required.
                    var response = await http.GetAsync(new Uri(baseUri, "/health")).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return (process, baseUri, output);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        ProcessKill.Quietly(process);
        throw new TimeoutException(
            $"Enlist.ControlPlane did not become ready within {readyTimeout.TotalSeconds:0}s " +
            $"(database {databaseName}, address {baseUri?.ToString() ?? "never announced"}).{Environment.NewLine}" +
            $"--- control plane output ---{Environment.NewLine}{CapturedOutput()}", lastError);
    }

    public async ValueTask DisposeAsync()
    {
        ProcessKill.Quietly(_process);

        // WAIT for it to actually be gone before deleting its blob directory. Kill() only ASKS; the OS
        // releases the process's file handles asynchronously, so deleting immediately races them and
        // loses often enough to leak a directory per test run. This is the same lesson AgentHost's own
        // StopApplicationAsync already records — "awaits the OS process being gone, not just asked to
        // stop" — applied one layer down.
        await WaitForExitQuietlyAsync(_process).ConfigureAwait(false);
        _process.Dispose();

        DeleteQuietly(_packageStorageRoot);

        await DropDatabaseAsync().ConfigureAwait(false);
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string ConnectionStringFor(string databaseName) =>
        $"Server=(localdb)\\mssqllocaldb;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True;";

    private Task DropDatabaseAsync() => DropDatabaseAsync(_databaseName);

    /// <summary>
    /// Static so the start-failure paths can call it too. They already delete the blob directory; not
    /// dropping the database there was the same oversight one layer down, and it is the WORSE of the
    /// two — a failed run creates one database per attempt, and a systematically-broken run (every
    /// server failing to start) leaves one behind for every test in the suite. That is exactly how 135
    /// of them accumulated on this machine before anyone looked.
    ///
    /// The drop itself lives in LocalDb because the reason it used to fail had nothing to do with
    /// this class: a sibling test's Kill(entireProcessTree) was killing the database server, so the
    /// cleanup ran against a server that was restarting. LocalDb now prevents that AND retries, and
    /// reports anything that still could not be dropped instead of swallowing it — the silence is
    /// what let the last 16 accumulate unnoticed.
    /// </summary>
    private static Task DropDatabaseAsync(string databaseName) => LocalDb.DropDatabaseAsync(databaseName);

    /// <summary>
    /// Finds the port Kestrel actually bound, by reading the "Now listening on:" line it writes at
    /// startup.
    ///
    /// This replaced a `TcpListener(port 0) → read port → Stop() → hand the number to the child`
    /// helper, which had a genuine time-of-check/time-of-use race: between releasing the port and the
    /// child binding it, anything could take it — including a sibling test class doing the very same
    /// thing a millisecond later, since the OS will happily hand out a just-freed ephemeral port twice.
    /// The loser's Kestrel then failed to bind, the process died with an unhandled exception (exit code
    /// -532462766), and the suite reported an unexplained flake in whichever test drew the short straw.
    ///
    /// Letting the CHILD choose its own port (`--urls http://127.0.0.1:0`) removes the window
    /// completely rather than narrowing it: the same process that picks the port is the one that holds
    /// it, so there is no interval for anyone to race into.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ListeningOn =
        new(@"Now listening on:\s*(http://\S+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Best-effort — a leaked throwaway blob directory costs disk space on a dev machine, not the correctness of any other test (each instance gets its own unique path).</summary>
    private static void DeleteQuietly(string directory)
    {
        // Retried briefly: even after the owning process is gone, Windows can hold a directory for a
        // moment longer (virus scanners, indexing, lazily-released handles). One attempt was enough to
        // leak on a busy machine.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
            catch
            {
                return;
            }
        }
    }
}
