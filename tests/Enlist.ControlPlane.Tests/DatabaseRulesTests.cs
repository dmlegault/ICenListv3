using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Tests;

/// <summary>
/// The hard rule about where the database lives: a Windows service cannot use LocalDB.
///
/// Found by installing for real. A LocalDB instance belongs to the account that starts it, so the
/// database the installer creates as the elevated operator is not the one the service sees - it gets
/// an empty instance of its own, the control plane correctly refuses to create a schema outside
/// Development, and the operator is left with "Cannot connect to the control plane database" after an
/// install that reported success. That message reads like a network problem and is hours from the
/// real mistake, which is what makes this worth refusing by name.
/// </summary>
public sealed class DatabaseRulesTests
{
    private const string LocalDb = @"Server=(localdb)\MSSQLLocalDB;Database=EnlistControlPlane;Trusted_Connection=True;";
    private const string Express = @"Server=.\SQLEXPRESS;Database=EnlistControlPlane;Trusted_Connection=True;";

    [Theory]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=X;")]
    [InlineData(@"Server=(LocalDB)\MSSQLLocalDB;Database=X;")]
    [InlineData(@"Data Source=(localdb)\.\MyInstance;")]
    [InlineData(@"Server=np:\\.\pipe\LOCALDB#E15D8E5A\tsql\query;")]
    public void LocalDB_is_recognised_however_it_is_spelled(string connectionString)
    {
        // The last one is the resolved pipe name rather than the instance name - it does NOT contain
        // "(localdb)" and is deliberately not matched, because by then it names a specific running
        // instance rather than the per-user alias. Asserted so the limit is written down.
        var recognised = DatabaseRules.IsLocalDb(connectionString);
        Assert.Equal(connectionString.Contains("(localdb)", StringComparison.OrdinalIgnoreCase), recognised);
    }

    [Theory]
    [InlineData(Express)]
    [InlineData(@"Server=sql01.corp.local;Database=X;")]
    [InlineData(@"Server=tcp:x.database.windows.net,1433;Database=X;")]
    [InlineData(null)]
    [InlineData("")]
    public void A_real_server_is_not_LocalDB(string? connectionString)
    {
        Assert.False(DatabaseRules.IsLocalDb(connectionString));
    }

    [Fact]
    public void A_service_pointed_at_LocalDB_is_refused_by_name()
    {
        var violation = DatabaseRules.Violation(LocalDb, isWindowsService: true);

        Assert.NotNull(violation);
        Assert.Contains("LocalDB", violation);

        // The message has to explain WHY, because the operator can see the database they created and
        // will otherwise reasonably conclude the installer is wrong.
        Assert.Contains("belongs to the account that starts it", violation);
    }

    [Fact]
    public void A_developer_at_a_terminal_is_not_refused()
    {
        // LocalDB is exactly right here and is what Developer-Setup-Guide tells people to use: the
        // instance belongs to the person running it, which is the whole point. The rule is about the
        // identity, not the database.
        Assert.Null(DatabaseRules.Violation(LocalDb, isWindowsService: false));
    }

    [Fact]
    public void A_service_pointed_at_a_real_server_is_not_refused()
    {
        Assert.Null(DatabaseRules.Violation(Express, isWindowsService: true));
    }

    [Fact]
    public void Nothing_configured_is_not_this_rules_problem()
    {
        // Program.cs already refuses an absent connection string outside Development, with its own
        // message. Two rules claiming the same failure would produce whichever message ran first.
        Assert.Null(DatabaseRules.Violation(null, isWindowsService: true));
        Assert.Null(DatabaseRules.Violation("", isWindowsService: true));
    }
}
