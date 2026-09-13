using Enlist.Installer.Detection;

namespace Enlist.Installer.Detection.Tests;

/// <summary>
/// The wizard's half of the LocalDB rule: caught on the Database page rather than at first start.
///
/// Found by installing for real on 2026-09-13. The Database page prefilled
/// (localdb)\MSSQLLocalDB - to save the operator filling in a box on a page they had no reason to
/// touch, which is a good instinct and was the wrong answer. A LocalDB instance belongs to whoever
/// starts it, so the database the installer creates as the elevated operator is not the one a
/// service can see. The install reported success and the control plane would not start.
/// </summary>
public sealed class LocalDbRefusalTests
{
    [Theory]
    [InlineData(@"(localdb)\MSSQLLocalDB")]
    [InlineData(@"(LocalDB)\MSSQLLocalDB")]
    [InlineData(@"(localdb)\.\Instance")]
    public void LocalDB_is_recognised_however_it_is_spelled(string server)
    {
        Assert.True(InstallPlan.IsLocalDb(server));
    }

    [Theory]
    [InlineData(@".\SQLEXPRESS")]
    [InlineData("sql01.corp.local")]
    [InlineData("tcp:x.database.windows.net,1433")]
    [InlineData("")]
    public void A_real_server_is_not_LocalDB(string server)
    {
        Assert.False(InstallPlan.IsLocalDb(server));
    }

    [Fact]
    public void The_database_page_refuses_LocalDB_and_says_why()
    {
        var plan = new InstallPlan { Type = InstallType.Server, DatabaseServer = @"(localdb)\MSSQLLocalDB" };

        var blocked = plan.WhyNextIsBlocked(WizardPage.Database);

        Assert.NotNull(blocked);
        Assert.Contains("LocalDB", blocked);

        // The reason matters more than the refusal. An operator who can see the database they just
        // created will otherwise conclude the installer is wrong.
        Assert.Contains("belongs to whoever starts it", blocked);
    }

    [Fact]
    public void There_is_no_prefilled_server_any_more()
    {
        // Prefilling a value that cannot work is worse than an empty box: it is an empty box the
        // operator does not know to look at. There is no honest default for "where is your SQL
        // Server", so the page asks.
        var plan = new InstallPlan();

        Assert.Equal("", plan.DatabaseServer);
        Assert.Equal("Enter the SQL Server to use.", plan.WhyNextIsBlocked(WizardPage.Database));
    }

    [Fact]
    public void A_real_server_passes()
    {
        var plan = new InstallPlan { Type = InstallType.Server, DatabaseServer = @".\SQLEXPRESS" };

        Assert.Null(plan.WhyNextIsBlocked(WizardPage.Database));
    }

    [Fact]
    public void The_database_name_still_has_a_default_because_that_one_is_honest()
    {
        // A database NAME is a naming choice and enList can reasonably pick one. A server address is
        // a fact about somebody's estate and it cannot.
        Assert.Equal("EnlistControlPlane", new InstallPlan().DatabaseName);
    }

    [Fact]
    public void A_silent_install_that_names_no_server_carries_none()
    {
        // ToBundleVariables omits what was not set, so an unset server reaches the MSI as absent
        // rather than empty - and the package then writes no connection string at all.
        var plan = InstallPlan.FromVariables(name => name == "INSTALLTYPE" ? "Server" : null);

        Assert.Equal("", plan.DatabaseServer);
        Assert.DoesNotContain("DB_SERVER", plan.ToBundleVariables().Keys);
    }
}
