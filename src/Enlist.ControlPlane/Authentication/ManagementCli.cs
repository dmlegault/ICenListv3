using System.Globalization;
using System.Text.RegularExpressions;

using Enlist.ControlPlane.Contracts;
using Enlist.ControlPlane.Data;

using Microsoft.EntityFrameworkCore;

namespace Enlist.ControlPlane.Authentication;

/// <summary>
/// The bootstrap: verbs on the control plane executable that talk to the database directly and need
/// no HTTP credential, because the first Operator key cannot be created by an Operator who does not
/// exist yet (Authentication-Design.md section 10). Same posture as migrations — run out of band, on
/// the control plane host, by someone with database access. Each prints its secret exactly once.
///
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
        "create-api-key", "create-join-token", "revoke-api-key", "revoke-agent", "list-keys", "list-join-tokens",
    };

    public static bool IsVerb(string[] args) => args.Length > 0 && Verbs.Contains(args[0]);

    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1));

        await using var db = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlServer(ResolveConnectionString()).Options);

        try
        {
            return verb switch
            {
                "create-api-key" => await CreateApiKeyAsync(db, options),
                "create-join-token" => await CreateJoinTokenAsync(db, options),
                "revoke-api-key" => await RevokeApiKeyAsync(db, options),
                "revoke-agent" => await RevokeAgentAsync(db, options),
                "list-keys" => await ListKeysAsync(db),
                "list-join-tokens" => await ListJoinTokensAsync(db),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{verb} failed: {ex.Message}");
            return 1;
        }
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

        if (await db.ApiKeys.AnyAsync(k => k.Name == name && k.RevokedAtUtc == null))
        {
            Console.Error.WriteLine($"An API key named '{name}' already exists. Revoke it first, or pick another name.");
            return 1;
        }

        var now = DateTimeOffset.UtcNow;
        var expires = ParseExpiry(options.GetValueOrDefault("expires"), TimeSpan.FromDays(90), now);
        var key = Tokens.Generate(Tokens.ApiKeyPrefix);
        db.ApiKeys.Add(new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            KeyHash = Tokens.Hash(key),
            Role = role,
            CreatedBy = Environment.UserName,
            CreatedAtUtc = now,
            ExpiresAtUtc = expires,
        });
        await db.SaveChangesAsync();

        Console.WriteLine($"Created API key '{name}' ({role}, expires {Describe(expires)}).");
        Console.WriteLine($"  key: {key}");
        Console.WriteLine("Store it now; it is not recoverable. Present it as: Authorization: Bearer <key>");
        return 0;
    }

    private static async Task<int> CreateJoinTokenAsync(ControlPlaneDbContext db, Dictionary<string, string> options)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = ParseExpiry(options.GetValueOrDefault("expires"), TimeSpan.FromHours(24), now)
            ?? throw new InvalidOperationException("a join token cannot be created with --expires never; it is meant to be short-lived.");

        int? uses = null;
        if (options.TryGetValue("uses", out var usesText))
        {
            if (!int.TryParse(usesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Usage("--uses must be a positive whole number.");
            }

            uses = parsed;
        }

        var token = Tokens.Generate(Tokens.JoinPrefix);
        var entity = new JoinTokenEntity
        {
            Id = Guid.NewGuid(),
            TokenHash = Tokens.Hash(token),
            CreatedBy = Environment.UserName,
            CreatedAtUtc = now,
            ExpiresAtUtc = expires,
            UsesRemaining = uses,
        };
        db.JoinTokens.Add(entity);
        await db.SaveChangesAsync();

        Console.WriteLine($"Created join token {entity.Id} (expires {Describe(expires)}, uses: {(uses is null ? "unlimited until expiry" : uses.Value.ToString(CultureInfo.InvariantCulture))}).");
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

        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Name == name && k.RevokedAtUtc == null);
        if (key is null)
        {
            Console.Error.WriteLine($"No live API key named '{name}'.");
            return 1;
        }

        key.RevokedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Console.WriteLine($"Revoked API key '{name}'. Anything still presenting it gets 401 from now on.");
        return 0;
    }

    private static async Task<int> RevokeAgentAsync(ControlPlaneDbContext db, Dictionary<string, string> options)
    {
        if (!options.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            return Usage("revoke-agent needs --name.");
        }

        var credential = await db.AgentCredentials.SingleOrDefaultAsync(c => c.AgentName == name && c.RevokedAtUtc == null);
        if (credential is null)
        {
            Console.Error.WriteLine($"Agent '{name}' holds no live credential.");
            return 1;
        }

        credential.RevokedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Console.WriteLine($"Revoked the credential for agent '{name}'. It keeps running what it runs, and it can re-enroll with a new join token.");
        return 0;
    }

    private static async Task<int> ListKeysAsync(ControlPlaneDbContext db)
    {
        var keys = await db.ApiKeys.OrderBy(k => k.Name).ToListAsync();
        if (keys.Count == 0)
        {
            Console.WriteLine("No API keys.");
            return 0;
        }

        Console.WriteLine($"{"name",-24} {"role",-9} {"created",-20} {"expires",-20} {"last used",-20} status");
        foreach (var k in keys)
        {
            var status = k.RevokedAtUtc is not null ? "revoked" : k.ExpiresAtUtc is { } e && e <= DateTimeOffset.UtcNow ? "expired" : "live";
            Console.WriteLine($"{k.Name,-24} {k.Role,-9} {Stamp(k.CreatedAtUtc),-20} {Describe(k.ExpiresAtUtc),-20} {(k.LastUsedAtUtc is { } u ? Stamp(u) : "never"),-20} {status}");
        }

        return 0;
    }

    private static async Task<int> ListJoinTokensAsync(ControlPlaneDbContext db)
    {
        var tokens = await db.JoinTokens.OrderByDescending(t => t.CreatedAtUtc).ToListAsync();
        if (tokens.Count == 0)
        {
            Console.WriteLine("No join tokens.");
            return 0;
        }

        Console.WriteLine($"{"id",-36} {"created",-20} {"expires",-20} {"uses left",-10} status");
        foreach (var t in tokens)
        {
            var status = t.RevokedAtUtc is not null ? "revoked" : t.ExpiresAtUtc <= DateTimeOffset.UtcNow ? "expired" : t.UsesRemaining == 0 ? "used up" : "live";
            Console.WriteLine($"{t.Id,-36} {Stamp(t.CreatedAtUtc),-20} {Stamp(t.ExpiresAtUtc),-20} {(t.UsesRemaining?.ToString(CultureInfo.InvariantCulture) ?? "unlimited"),-10} {status}");
        }

        return 0;
    }

    /// <summary>"--name x --role y" into a dictionary. A flag with no value is an error rather than a silent empty string.</summary>
    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= list.Count)
            {
                throw new InvalidOperationException($"unexpected argument '{list[i]}'. Options are --name, --role, --expires and --uses, each followed by a value.");
            }

            options[list[i][2..]] = list[++i];
        }

        return options;
    }

    /// <summary>"24h", "90d" or "never"; null for a default that is itself "never".</summary>
    private static DateTimeOffset? ParseExpiry(string? text, TimeSpan defaultLifetime, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return now + defaultLifetime;
        }

        if (text.Equals("never", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = Regex.Match(text.Trim(), @"^(\d+)([hd])$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidOperationException($"--expires must be like 24h, 7d or never, got '{text}'.");
        }

        var amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return now + (match.Groups[2].Value.Equals("h", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(amount) : TimeSpan.FromDays(amount));
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
        Console.Error.WriteLine("  create-api-key    --name <name> --role Operator|Viewer [--expires 90d|never]");
        Console.Error.WriteLine("  create-join-token [--expires 24h] [--uses <n>]");
        Console.Error.WriteLine("  revoke-api-key    --name <name>");
        Console.Error.WriteLine("  revoke-agent      --name <agent>");
        Console.Error.WriteLine("  list-keys");
        Console.Error.WriteLine("  list-join-tokens");
        return 2;
    }
}
