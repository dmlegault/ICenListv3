namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// The hard rule about WHERE the control plane's database lives, in the same shape as
/// <see cref="ListenerRules"/> and <see cref="ServerCertificate"/>: a pure function that returns the
/// message to refuse startup with, or null.
///
/// THERE IS ONE RULE AND IT IS ABOUT LocalDB. A LocalDB instance is PER USER - it is started on
/// demand under the profile of whoever connects, and two accounts asking for
/// (localdb)\MSSQLLocalDB get two different databases. That makes it unusable by a Windows service,
/// and unusable in a way that looks like something else entirely:
///
///   - the installer applies the schema as the elevated operator, creating the database in THAT
///     operator's instance;
///   - the service then starts as NETWORK SERVICE or LocalSystem, asks for the same instance name,
///     and gets an empty one belonging to itself;
///   - the control plane correctly refuses to create a database outside Development, and reports
///     "Cannot connect to the control plane database" - which reads like a network or permissions
///     problem, hours from the actual mistake.
///
/// Found by installing for real on 2026-09-13. Everything reported success and the control plane
/// would not start.
///
/// Program.cs already refuses to FALL BACK to LocalDB outside Development, and says so. This is the
/// other half of the same decision: it also refuses to be POINTED at LocalDB by a service.
/// </summary>
public static class DatabaseRules
{
    /// <summary>
    /// Whether a connection string names a LocalDB instance. Matched on the data-source prefix
    /// because that is the only spelling LocalDB has - "(localdb)\instance" or "(LocalDB)\instance",
    /// optionally with "np:" in front where a pipe name has been resolved.
    /// </summary>
    public static bool IsLocalDb(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        return connectionString!.IndexOf("(localdb)", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Why this process will not start, or null.
    ///
    /// Only refused for a SERVICE. Run from a terminal by a developer, LocalDB is exactly the right
    /// thing and is what Developer-Setup-Guide tells people to use - the instance belongs to the
    /// person running it, which is the whole point. The rule is about the identity, not the database.
    /// </summary>
    public static string? Violation(string? connectionString, bool isWindowsService)
    {
        if (!isWindowsService || !IsLocalDb(connectionString))
        {
            return null;
        }

        return
            "The control plane is running as a Windows service and its connection string names LocalDB. " +
            "A LocalDB instance belongs to the account that starts it, so the database the installer created " +
            "as the operator is not the one this service can see - it would find an empty instance of its own. " +
            "Point ConnectionStrings:ControlPlane at a SQL Server instance the service account can reach " +
            "(SQL Server, SQL Server Express, or Azure SQL), and grant that account access to the database. " +
            "See docs/05-operations/Deployment-IaC.md section 1.4.";
    }
}
