using System.Globalization;

using Enlist.ControlPlane.Contracts;
using Enlist.ControlPlane.Data;

using Microsoft.EntityFrameworkCore;

namespace Enlist.ControlPlane.Authentication;

/// <summary>
/// The bootstrap: verbs on the control plane executable that talk to the database directly and need
/// no HTTP credential, because the first Operator key cannot be created by an Operator who does not
/// exist yet (Authentication-Design.md section 10). Same posture as migrations — run out of band, on
/// the control plane host, by someone with database access. Each prints its secret exactly once.
/// Day to day the same things are done from the portal's Access page, through
/// <see cref="AccessEndpoints"/>; both go through <see cref="CredentialIssuer"/>.
///
///   Enlist.ControlPlane.exe apply-schema
///   Enlist.ControlPlane.exe create-api-key    --name portal --role Operator [--expires 90d|never]
///   Enlist.ControlPlane.exe create-join-token [--expires 24h] [--uses 50]
///   Enlist.ControlPlane.exe revoke-api-key    --name ci-main
///   Enlist.ControlPlane.exe revoke-agent      --name WEB-07
///   Enlist.ControlPlane.exe list-keys
///   Enlist.ControlPlane.exe list-join-tokens
/// </summary>
public static class ManagementCli
{
    /// <summary>The same default Program.cs falls back to, so the verbs and the server always look at the same database when nothing is configured.</summary>
    public const string DefaultConnectionString = "Server=(localdb)\\mssqllocaldb;Database=EnlistControlPlane;Trusted_Connection=True;TrustServerCertificate=True;";

    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "apply-schema", "create-api-key", "create-join-token", "revoke-api-key", "revoke-agent", "list-keys", "list-join-tokens",
    };

    public static bool IsVerb(string[] args) => args.Length > 0 && Verbs.Contains(args[0]);

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args[0].ToLowerInvariant();

        try
        {
            var options = ParseOptions(args.Skip(1));

            await using var db = new ControlPlaneDbContext(
                new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlServer(ResolveConnectionString()).Options);

            return verb switch
            {
                "apply-schema" => await ApplySchemaAsync(db),
                "create-api-key" => await CreateApiKeyAsync(db, options),
                "create-join-token" => await CreateJoinTokenAsync(db, options),
                "revoke-api-key" => await RevokeApiKeyAsync(db, options),
                "revoke-agent" => await RevokeAgentAsync(db, options),
                "list-keys" => await ListKeysAsync(db),
                "list-join-tokens" => await ListJoinTokensAsync(db),
                _ => Usage(),
            };
        }
        catch (CredentialRequestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{verb} failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Who ran the verb, for CreatedBy: the Windows account at the console.</summary>
    private static string Operator => $"{Environment.UserDomainName}\\{Environment.UserName} (cli)";

    /// <summary>
    /// Creates the database if it is absent and applies every pending migration - the ONE place in
    /// this product that is allowed to, and deliberately not the server.
    ///
    /// Program.cs refuses to migrate outside Development, and that refusal is about PERMISSIONS
    /// rather than tidiness: creating a database needs dbcreator and applying a migration needs
    /// CREATE/ALTER TABLE, neither of which an application login should hold. The answer there is
    /// "apply schema as a deploy step with an account that has DDL rights" - and this verb is that
    /// step, named and run on purpose by someone who already has those rights, which is exactly the
    /// posture of every other verb in this file.
    ///
    /// It exists because the installer needs it. create-api-key writes to the database directly, so
    /// minting the portal's key on a fresh machine requires a schema before there is a control plane
    /// running to ask for one. Idempotent: applying nothing is a successful outcome, reported as
    /// such, because an upgrade re-runs this and "already up to date" is the common case.
    /// </summary>
    private static async Task<int> ApplySchemaAsync(ControlPlaneDbContext db)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0 && await db.Database.CanConnectAsync())
        {
            Console.WriteLine("The control plane database is already up to date.");
            return 0;
        }

        // Said before the work rather than after, because on a new database this creates it, and a
        // silent pause against a server that is not answering is the worst moment to say nothing.
        Console.WriteLine(pending.Count == 0
            ? "Creating the control plane database."
            : $"Applying {pending.Count} migration(s): {string.Join(", ", pending)}.");

        await db.Database.MigrateAsync();
        Console.WriteLine("The control plane database is ready.");
        return 0;
    }

    private static async Task<int> CreateApiKeyAsync(ControlPlaneDbContext db, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            return Usage("create-api-key needs --name.");
        }

        if (!options.TryGetValue("role", out var role) || !ManagementRoles.IsValid(role))
        {
            return Usage($"create-api-key needs --role {ManagementRoles.Operator} or {ManagementRoles.Viewer}.");
        }

        // --replace revokes a live key of the same name first, which is what an INSTALLER needs and a
        // person at a terminal does not.
        //
        // The control plane's database is deliberately Permanent: it survives an uninstall, so an
        // operator who removes enList and puts it back finds their policy rules and packages intact.
        // The consequence nobody had met until a reinstall was tried: the portal's key is already
        // there, create-api-key refuses a duplicate name - correctly, for a person - and the whole
        // install fails on its third step.
        //
        // Replacing rather than reusing, because a key is only ever readable once. The stored plaintext
        // does not exist anywhere to be recovered, so the portal must be given a new one either way,
        // and leaving the old one live would be an extra Operator credential nobody is holding.
        var replace = options.ContainsKey("replace");
        if (replace && await CredentialIssuer.RevokeApiKeyAsync(db, name))
        {
            Console.WriteLine($"Revoked the existing API key '{name.Trim()}'.");
        }

        var (entity, key) = await CredentialIssuer.CreateApiKeyAsync(db, name, role, options.GetValueOrDefault("expires"), Operator);

        Console.WriteLine($"Created API key '{entity.Name}' ({entity.Role}, expires {Describe(entity.ExpiresAtUtc)}).");
        Console.WriteLine($"  key: {key}");
        Console.WriteLine("Store it now; it is not recoverable. Present it as: Authorization: Bearer <key>");
        return 0;
    }

    private static async Task<int> CreateJoinTokenAsync(ControlPlaneDbContext db, Dictionary<string, string> options)
    {
        int? uses = null;
        if (options.TryGetValue("uses", out var usesText))
        {
            if (!int.TryParse(usesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Usage("--uses must be a positive whole number.");
            }

            uses = parsed;
        }

        var (entity, token) = await CredentialIssuer.CreateJoinTokenAsync(db, options.GetValueOrDefault("expires"), uses, Operator);

        Console.WriteLine($"Created join token {entity.Id} (expires {Describe(entity.ExpiresAtUtc)}, uses: {(uses is null ? "unlimited until expiry" : uses.Value.ToString(CultureInfo.InvariantCulture))}).");
        Console.WriteLine($"  token: {token}");
        Console.WriteLine("Give it to the agents being enrolled (the installer's Agent page, or --join-token). It is not recoverable.");
        return 0;
    }

    private static async Task<int> RevokeApiKeyAsync(ControlPlaneDbContext db, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            return Usage("revoke-api-key needs --name.");
        }

        if (!await CredentialIssuer.RevokeApiKeyAsync(db, name))
        {
            Console.Error.WriteLine($"No live API key named '{name}'.");
            return 1;
        }

        Console.WriteLine($"Revoked API key '{name}'. Anything still presenting it gets 401 from now on.");
        return 0;
    }

    private static async Task<int> RevokeAgentAsync(ControlPlaneDbContext db, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            return Usage("revoke-agent needs --name.");
        }

        if (!await CredentialIssuer.RevokeAgentCredentialAsync(db, name))
        {
            Console.Error.WriteLine($"Agent '{name}' holds no live credential.");
            return 1;
        }

        Console.WriteLine($"Revoked the credential for agent '{name}'. It keeps running what it runs, and it can re-enroll with a new join token.");
        return 0;
    }

    private static async Task<int> ListKeysAsync(ControlPlaneDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var keys = await db.ApiKeys.OrderBy(k => k.Name).ToListAsync();
        if (keys.Count == 0)
        {
            Console.WriteLine("No API keys.");
            return 0;
        }

        Console.WriteLine($"{"name",-24} {"role",-9} {"created",-20} {"expires",-20} {"last used",-20} status");
        foreach (var k in keys)
        {
            Console.WriteLine($"{k.Name,-24} {k.Role,-9} {Stamp(k.CreatedAtUtc),-20} {Describe(k.ExpiresAtUtc),-20} {(k.LastUsedAtUtc is { } u ? Stamp(u) : "never"),-20} {CredentialIssuer.StatusOf(k, now)}");
        }

        return 0;
    }

    private static async Task<int> ListJoinTokensAsync(ControlPlaneDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tokens = await db.JoinTokens.OrderByDescending(t => t.CreatedAtUtc).ToListAsync();
        if (tokens.Count == 0)
        {
            Console.WriteLine("No join tokens.");
            return 0;
        }

        Console.WriteLine($"{"id",-36} {"created",-20} {"expires",-20} {"uses left",-10} status");
        foreach (var t in tokens)
        {
            Console.WriteLine($"{t.Id,-36} {Stamp(t.CreatedAtUtc),-20} {Stamp(t.ExpiresAtUtc),-20} {(t.UsesRemaining?.ToString(CultureInfo.InvariantCulture) ?? "unlimited"),-10} {CredentialIssuer.StatusOf(t, now)}");
        }

        return 0;
    }

    /// <summary>"--name x --role y" into a dictionary. A flag with no value is an error rather than a silent empty string.</summary>
    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        // Flags that are a yes or a no rather than a setting. Named rather than inferred, so a value
        // left off by accident is still the error it has always been - that check is what turns
        // "--name" with nothing after it into a message instead of a silently missing name.
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "replace" };

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"unexpected argument '{list[i]}'. Options are --name, --role, --expires and --uses, each followed by a value, and --replace.");
            }

            var name = list[i][2..];
            if (switches.Contains(name))
            {
                options[name] = "yes";
                continue;
            }

            if (i + 1 >= list.Count)
            {
                throw new InvalidOperationException($"'--{name}' needs a value. Options are --name, --role, --expires and --uses, each followed by a value, and --replace.");
            }

            options[name] = list[++i];
        }

        return options;
    }

    private static string ResolveConnectionString()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return configuration.GetConnectionString("ControlPlane") ?? DefaultConnectionString;
    }

    private static string Describe(DateTimeOffset? when) => when is { } w ? Stamp(w) : "never";

    private static string Stamp(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture);

    private static int Usage(string? error = null)
    {
        if (error is not null)
        {
            Console.Error.WriteLine(error);
        }

        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  apply-schema");
        Console.Error.WriteLine("  create-api-key    --name <name> --role Operator|Viewer [--expires 90d|never] [--replace]");
        Console.Error.WriteLine("  create-join-token [--expires 24h] [--uses <n>]");
        Console.Error.WriteLine("  revoke-api-key    --name <name>");
        Console.Error.WriteLine("  revoke-agent      --name <agent>");
        Console.Error.WriteLine("  list-keys");
        Console.Error.WriteLine("  list-join-tokens");
        return 2;
    }
}
