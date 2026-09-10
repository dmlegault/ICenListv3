using System.Collections.Concurrent;
using System.Text;

using Microsoft.Data.SqlClient;

namespace Enlist.TestSupport;

/// <summary>
/// Owns the LocalDB instance the control-plane tests run against. Exists for one reason that is not
/// obvious from anywhere else in the suite: keeping <c>sqlservr.exe</c> OUT of every test-spawned
/// process tree.
///
/// LocalDB is started ON DEMAND BY THE CLIENT. The first process to open a connection to
/// <c>(localdb)\mssqllocaldb</c> causes sqlservr.exe to be spawned AS A CHILD OF THAT PROCESS —
/// verifiable at any time with <c>Get-CimInstance Win32_Process -Filter "Name='sqlservr.exe'"</c>,
/// which reports whichever process happened to connect first as its parent.
///
/// Before this class existed, that first connector was whichever spawned Enlist.ControlPlane child
/// won the race, so the database server became part of a TEST'S process tree. ControlPlaneTestServer
/// kills its child with <c>Kill(entireProcessTree: true)</c>, so disposing one test server killed
/// LocalDB out from under every other test running in parallel. Each of those tests then lost its
/// connection, its control plane died, and its cleanup path called DropDatabaseAsync against a
/// server that was still restarting — which failed, silently, because the drop was best-effort.
/// The databases simply leaked.
///
/// That is not theoretical. On 2026-09-08 a single run left 16 databases behind, all created inside
/// a four-second window. The LocalDB error log records three instance restarts between 18:17:31 and
/// 18:17:38, two of them ending with no shutdown message at all — killed, not stopped — and the
/// last line before one of those deaths is a SINGLE_USER being set on a database that was still
/// there the next morning: the drop got half way through and the server vanished underneath it.
///
/// <see cref="EnsureStartedAsync"/> opens one connection to master FROM THE TEST HOST PROCESS and
/// never closes it. That fixes the ownership (sqlservr.exe is parented to testhost.exe, which
/// nothing in this suite kills) and, as a bonus, holds LocalDB open for the whole run so it cannot
/// hit its RANU idle timeout between slow test classes and restart mid-suite.
/// </summary>
public static class LocalDb
{
    /// <summary>
    /// master, for creating and dropping the per-test databases. Also the single place the LocalDB
    /// instance name is written down — ControlPlaneTestServer builds its own per-test connection
    /// string in code rather than reading configuration, deliberately, so that no environment
    /// variable set for a demo (see demo/sql-server/) can ever redirect a test run at a real server.
    /// </summary>
    public const string MasterConnectionString =
        "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";

    // Pooling=False because this connection must map to a physical connection that stays open for
    // the life of the process. A pooled one could in principle be reset or reclaimed underneath us,
    // and "the connection is definitely still there" is the entire job of this object. The
    // application name is there so `SELECT program_name FROM sys.dm_exec_sessions` explains what
    // this session is to anyone who finds it later.
    private const string PinConnectionString =
        MasterConnectionString + "Pooling=False;Application Name=Enlist test host (LocalDB pin);";

    // ExecutionAndPublication: the pin is opened exactly once per test host, by whichever test
    // class starts first, with every concurrent caller awaiting the same task. A faulted task stays
    // faulted for the rest of the run, which is the behaviour we want — if LocalDB cannot be
    // started, no control-plane test can pass and each should say so for the same reason.
    private static readonly Lazy<Task<SqlConnection>> Pin =
        new(OpenPinAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentBag<string> LeakedDatabases = [];

    static LocalDb()
    {
        // A leaked database costs disk space, not correctness, so nothing here fails a test over
        // one. But it must not be SILENT: the drops were already best-effort before this, and the
        // silence is precisely why 16 of them (and, per the note in ControlPlaneTestServer, 135
        // before that) piled up unnoticed. One summary at the end of the run, with the command to
        // clean them up, costs nothing and makes the next occurrence self-reporting.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReportLeakedDatabases();
    }

    /// <summary>
    /// Starts LocalDB under THIS process if it is not already running, and holds it open. Must be
    /// awaited before spawning any child process that will connect to LocalDB — see the class
    /// remarks for what happens otherwise. Cheap and idempotent after the first call.
    /// </summary>
    public static Task EnsureStartedAsync() => Pin.Value;

    private static async Task<SqlConnection> OpenPinAsync()
    {
        var connection = new SqlConnection(PinConnectionString);

        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            connection.Dispose();

            // The remedies are ordered least-destructive first, and they are specific because the
            // generic SqlException for this ("The server was not found or was not accessible")
            // reads like a typo in a connection string when it is nothing of the sort.
            throw new InvalidOperationException(
                "Could not open (localdb)\\mssqllocaldb, which every control-plane test needs.\n" +
                "\n" +
                "If the inner error is \"SQL Server process failed to start\" (0x89c5010a), the\n" +
                "instance REGISTRATION is out of sync with reality rather than the instance being\n" +
                "genuinely absent: `sqllocaldb info MSSQLLocalDB` reports Stopped with no pipe name\n" +
                "while an orphaned sqlservr.exe is still running and still holding the instance's\n" +
                "files, so the runtime tries to start a second one and collides with it. This\n" +
                "happens when the process that owned the instance died without shutting it down.\n" +
                "Confirm with:\n" +
                "    Get-CimInstance Win32_Process -Filter \"Name='sqlservr.exe'\"\n" +
                "and repair, in this order:\n" +
                "    1. sqllocaldb stop MSSQLLocalDB -k      (kill the orphan; it restarts clean)\n" +
                "    2. Stop-Process -Id <the sqlservr.exe PID above>\n" +
                "    3. sqllocaldb delete MSSQLLocalDB && sqllocaldb create MSSQLLocalDB\n" +
                "       (last resort - this discards the instance's master, so any databases you\n" +
                "        care about on it need re-attaching afterwards)\n" +
                "\n" +
                "Note that a running instance can still be reachable over its named pipe while\n" +
                "being unreachable by name, so \"SSMS can connect\" does not rule this out.", ex);
        }

        // Never disposed, deliberately. This open connection IS the fix; releasing it would hand
        // ownership of sqlservr.exe back to whichever process next triggers an auto-start.
        return connection;
    }

    /// <summary>
    /// Drops a per-test database, retrying briefly. Retries matter because the most likely moment
    /// for a drop to fail is exactly when several parallel tests are tearing down at once, which is
    /// also the moment a transient failure is most likely to clear a few hundred milliseconds later.
    /// Never throws: a failed cleanup must not turn a passing test red, or mask why a failing one
    /// failed. It reports instead, via <see cref="ReportLeakedDatabases"/> at process exit.
    /// </summary>
    internal static async Task DropDatabaseAsync(string databaseName)
    {
        const int attempts = 4;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(MasterConnectionString);
                await connection.OpenAsync().ConfigureAwait(false);

                await using var command = connection.CreateCommand();

                // SINGLE_USER WITH ROLLBACK IMMEDIATE first: the control plane process has just
                // exited, but connection pooling can leave a lingering connection for a moment and
                // a plain DROP would fail with "database is in use" if it has not been released
                // yet. Note this is also re-entrant against a database left SINGLE_USER by a
                // previous half-completed attempt, which is the exact state a killed server leaves
                // behind — setting it again is a no-op and the DROP then proceeds.
                command.CommandText =
                    $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN " +
                    $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    $"DROP DATABASE [{databaseName}]; END";

                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;

                if (attempt < attempts)
                {
                    // Backs off rather than hammering: if LocalDB is restarting, it needs time to
                    // finish recovery before it will accept a connection at all.
                    await Task.Delay(250 * attempt).ConfigureAwait(false);
                }
            }
        }

        LeakedDatabases.Add(databaseName);

        Console.Error.WriteLine(
            $"[Enlist.TestSupport] Could not drop test database {databaseName} after {attempts} attempts: " +
            $"{lastError?.Message}");
    }

    private static void ReportLeakedDatabases()
    {
        var leaked = LeakedDatabases.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (leaked.Count == 0)
        {
            return;
        }

        var message = new StringBuilder();
        message.AppendLine();
        message.AppendLine(
            $"[Enlist.TestSupport] {leaked.Count} test database(s) survived this run and are still " +
            "on (localdb)\\mssqllocaldb:");

        foreach (var name in leaked)
        {
            message.AppendLine($"    {name}");
        }

        message.AppendLine();
        message.AppendLine("Drop them all with:");
        message.AppendLine(
            "    sqlcmd -S \"(localdb)\\mssqllocaldb\" -Q \"DECLARE @s nvarchar(max)=''; " +
            "SELECT @s=@s+'ALTER DATABASE ['+name+'] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            "DROP DATABASE ['+name+'];' FROM sys.databases " +
            "WHERE name LIKE 'EnlistControlPlaneTest[_]%'; EXEC sp_executesql @s;\"");

        Console.Error.Write(message.ToString());
    }
}
