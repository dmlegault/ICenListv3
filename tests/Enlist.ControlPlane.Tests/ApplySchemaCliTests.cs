using Enlist.TestSupport;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// <c>Enlist.ControlPlane.exe apply-schema</c> - the one operation in this product allowed to create
/// a database and apply migrations, and the reason the installer can mint the portal's key on a
/// machine where nothing has run yet.
///
/// The server itself refuses to migrate outside Development, deliberately and for permissions
/// reasons (Program.cs, Deployment-IaC section 1.4). This verb is the sanctioned other half of that
/// decision: named, run on purpose, by an account that already has DDL rights.
/// </summary>
public sealed class ApplySchemaCliTests : IAsyncLifetime
{
    private ControlPlaneTestServer? _server;

    public async Task InitializeAsync() => _server = await ControlPlaneTestServer.StartAsync(TimeSpan.FromSeconds(30));

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Applying_a_schema_that_is_already_current_succeeds_and_says_so()
    {
        // The test server's database is already migrated, which is exactly the state an UPGRADE finds:
        // the installer runs this every time, and "nothing to do" has to be a success rather than an
        // error that fails an install which was fine.
        var output = await _server!.RunCliAsync("apply-schema");

        Assert.Contains("up to date", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_is_idempotent()
    {
        // Run twice in a row: a repair, or a re-run after an operator fixed something else, must not
        // be a different outcome from the first time.
        await _server!.RunCliAsync("apply-schema");
        var second = await _server.RunCliAsync("apply-schema");

        Assert.Contains("up to date", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_creates_a_database_that_does_not_exist_yet()
    {
        // The install-time case, and the one that cannot be inferred from the two above: on a fresh
        // machine there is no database at all, and create-api-key has nowhere to write the portal's
        // key until this has run.
        var database = "EnlistApplySchemaTest" + Guid.NewGuid().ToString("N")[..8];
        var connectionString = $"Server=(localdb)\\mssqllocaldb;Database={database};Trusted_Connection=True;TrustServerCertificate=True;";

        try
        {
            var output = await _server!.RunCliAgainstAsync(connectionString, "apply-schema");
            Assert.Contains("ready", output, StringComparison.OrdinalIgnoreCase);

            // And it is genuinely usable afterwards - a key can be minted against it, which is the
            // only reason the installer runs this at all.
            var key = await _server.RunCliAgainstAsync(connectionString, "create-api-key", "--name", "portal", "--role", "Operator", "--expires", "never");
            Assert.Contains("enlk_", key, StringComparison.Ordinal);
        }
        finally
        {
            await LocalDb.DropDatabaseAsync(database);
        }
    }
}
