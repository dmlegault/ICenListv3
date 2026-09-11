using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

using Enlist.ControlPlane;
using Enlist.ControlPlane.Contracts;
using Enlist.ControlPlane.Data;
using Enlist.ControlPlane.Hubs;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

// Hosting-mode neutral: the same binary runs under IIS, as a Windows Service, or from `dotnet run`.
//
// ContentRootPath has to be decided HERE, in the options, not afterwards. WebApplicationBuilder
// resolves the content root during CreateBuilder, from the current working directory — and the SCM
// starts a service with its working directory set to %SystemRoot%\System32. Setting it later is not
// possible either: ConfigureHostBuilder.UseContentRoot throws NotSupportedException, which is exactly
// why UseWindowsService() (which would otherwise do this for us) cannot be used on a web host.
//
// Left null outside a service so IIS and `dotnet run` keep the content root they already resolve
// correctly — the ASP.NET Core Module sets it for IIS, and the SDK sets the working directory for a
// dev run. IsWindowsService() checks that the parent process is services.exe, so it is false under
// both, and false on non-Windows.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

// No-ops unless actually started by the SCM. Also routes logging to the Windows Event Log in that
// case, which is the only place a service can report a startup failure — including the two
// migration-verification failures below, which are precisely what a first production start hits.
builder.Services.AddWindowsService(options => options.ServiceName = "enlist-controlplane");

var connectionString = builder.Configuration.GetConnectionString("ControlPlane")
    ?? "Server=(localdb)\\mssqllocaldb;Database=EnlistControlPlane;Trusted_Connection=True;TrustServerCertificate=True;";

builder.Services.AddDbContext<ControlPlaneDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

var packageStorageRoot = builder.Configuration["PackageStorage:Root"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "PackageBlobs");
builder.Services.AddSingleton(new PackageBlobStore(packageStorageRoot));

// Kestrel's stock request-body limit is 30,000,000 bytes — smaller than a real application with its
// dependency closure. Applied to the upload endpoint only; everything else keeps the default.
var maxUploadBytes = builder.Configuration.GetValue<long?>("PackageStorage:MaxUploadBytes") ?? 512L * 1024 * 1024;

// Mark-and-sweep retention for package blobs (see PackageRetentionSweepService) — nothing prunes
// itself just by existing; without this, every uploaded digest lives forever, agent-cache-eviction
// notwithstanding (the control plane is the durable copy agents re-download from).
builder.Services.Configure<PackageRetentionOptions>(builder.Configuration.GetSection("PackageRetention"));
builder.Services.AddHostedService<PackageRetentionSweepService>();

builder.Services.Configure<LogRetentionOptions>(builder.Configuration.GetSection("LogRetention"));
builder.Services.AddHostedService<LogRetentionSweepService>();

// Status reports are the one table every agent appends to on every heartbeat, and nothing else ever
// deletes from — see ReportRetentionSweepService for why the newest report per agent is exempt.
builder.Services.Configure<ReportRetentionOptions>(builder.Configuration.GetSection("ReportRetention"));
builder.Services.AddHostedService<ReportRetentionSweepService>();

var app = builder.Build();

// Auto-migrate is DEVELOPMENT ONLY, and the split is about database PERMISSIONS, not tidiness.
//
// MigrateAsync does two things a production SQL login is normally not allowed to do: it CREATES the
// database when it is absent (that needs dbcreator, which an application login does not have), and
// applying a migration needs CREATE/ALTER TABLE (an app login is typically db_datareader +
// db_datawriter + EXECUTE and nothing more). Left unconditional, the first production start fails on
// a permissions error that reads like a connection-string problem.
//
// So production splits the work across three identities: a DBA creates an empty database, the deploy
// pipeline applies schema with an elevated account (`dotnet ef migrations bundle` — see
// docs/05-operations/Deployment-IaC.md), and this process runs read/write only and never touches DDL.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database.MigrateAsync();
}
else
{
    // Verify, never modify. Starting against a stale schema is worse than refusing to start: the
    // damage shows up later as scattered runtime errors on whichever endpoint first touches a missing
    // column, far from the deploy that caused it.
    using var scope = app.Services.CreateScope();
    var database = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database;

    // Checked separately from the pending-migration query below, because the two failures have
    // completely different fixes and GetPendingMigrationsAsync would otherwise report an absent
    // database as an opaque connection exception.
    if (!await database.CanConnectAsync())
    {
        throw new InvalidOperationException(
            "Cannot connect to the control plane database. Outside Development this process never creates it - " +
            "the database must already exist and be reachable via ConnectionStrings:ControlPlane. " +
            "See docs/05-operations/Deployment-IaC.md section 1.4.");
    }

    var pending = (await database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        throw new InvalidOperationException(
            $"The control plane database is missing {pending.Count} migration(s): {string.Join(", ", pending)}. " +
            "Apply them as a deploy step with an account that has DDL rights, then restart. " +
            "This process deliberately does not migrate outside Development. See docs/05-operations/Deployment-IaC.md section 1.4.");
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHub<ApplicationPolicyHub>(ApplicationPolicyHubContract.HubPath);

// Liveness for load balancers, the installer's "verify this control plane URL", and anyone wondering
// which build is answering. Unauthenticated like everything else here (review finding C2), and it
// deliberately says nothing secret: a version and whether the database answers, never the connection
// string. 200 when the database is reachable, 503 when it is not — the one degradation this process
// can detect about itself, and the one a probe should act on. It can only ever report Unhealthy after
// a successful start: outside Development the process refuses to start against an absent database.
app.MapGet("/health", async (ControlPlaneDbContext db, CancellationToken requestAborted) =>
{
    var assembly = typeof(Program).Assembly;
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";

    // SourceLink appends "+<commit>" build metadata; a probe wants the version, not the hash.
    var plus = version.IndexOf('+');
    if (plus > 0)
    {
        version = version[..plus];
    }

    // Bounded: a database that accepts the connection and never answers must not turn a health probe
    // into a hung request. Longer than any healthy round trip, shorter than a probe's patience.
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));

    HealthDatabaseDto database;
    try
    {
        var reachable = await db.Database.CanConnectAsync(timeout.Token);
        if (!reachable)
        {
            // A pool severed by an outage hands out its dead connections one at a time before it
            // discards them, so the first probes after a database comes back still report it down — a
            // false negative that a one-shot check, like the installer's Verify, would act on. Clear
            // the pool and ask once more, so the answer reflects the database rather than the pool.
            // The timeout above bounds both attempts together.
            if (db.Database.GetDbConnection() is SqlConnection sql)
            {
                SqlConnection.ClearPool(sql);
            }

            reachable = await db.Database.CanConnectAsync(timeout.Token);
        }

        var latest = reachable ? (await db.Database.GetAppliedMigrationsAsync(timeout.Token)).LastOrDefault() : null;
        database = new HealthDatabaseDto(reachable, latest, reachable ? null : "The database did not accept a connection.");
    }
    catch (Exception ex) when (ex is not OperationCanceledException || !requestAborted.IsCancellationRequested)
    {
        // A message only. SqlException text names the server, never the credentials, and the
        // connection string itself is never echoed.
        database = new HealthDatabaseDto(false, null, ex is OperationCanceledException ? "The database did not answer within 5s." : ex.Message);
    }

    var dto = new HealthDto(database.Reachable ? "Healthy" : "Unhealthy", "enList control plane", version, database, DateTimeOffset.UtcNow);
    return Results.Json(dto, statusCode: database.Reachable ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

// What an agent calls: everything currently assigned to it, resolved down to at most one outcome per
// ApplicationName — Running and Stopped alike, the agent's own reconciliation loop decides what to do
// with each. Unlike the portal-facing endpoint below, this collapses multiple matching rules into one
// decision (or a conflict) because the agent needs exactly one answer to reconcile against — see
// ResolveEffectivePoliciesForAgentAsync. Doubles as the liveness signal: every real agent calls this
// constantly by construction, so upserting the agent's LastSeenUtc here needs no separate heartbeat
// protocol.
app.MapGet("/api/agents/{agentName}/policies", async (string agentName, ControlPlaneDbContext db) =>
{
    var agent = await UpsertAgentSeenAsync(db, agentName);
    var resolved = await ResolveEffectivePoliciesForAgentAsync(db, agentName, agent);

    return Results.Ok(resolved.Select(r => ToDto(r.Entity, r.ConflictReason)));
});

app.MapPost("/api/agents/{agentName}/report", async (string agentName, HttpRequest request, ControlPlaneDbContext db) =>
{
    await UpsertAgentSeenAsync(db, agentName);

    using var reader = new StreamReader(request.Body);
    var snapshotJson = await reader.ReadToEndAsync();

    db.AgentReports.Add(new AgentReportEntity
    {
        AgentName = agentName,
        SnapshotJson = snapshotJson,
        ReportedAtUtc = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync();

    return Results.Accepted();
});

app.MapGet("/api/agents/{agentName}/report/latest", async (string agentName, ControlPlaneDbContext db) =>
{
    var latest = await db.AgentReports
        .Where(r => r.AgentName == agentName)
        .OrderByDescending(r => r.ReportedAtUtc)
        .FirstOrDefaultAsync();

    return latest is null ? Results.NotFound() : Results.Text(latest.SnapshotJson, "application/json");
});

// Imperative, on-demand control — start/stop one service, or enable/disable one job, on one agent, without
// touching anything else that application is running. Relayed over the same hub connection every agent
// already holds open for policy-changed pushes (see AgentCommandRequest for why this doesn't go
// through the declarative policy/reconcile path instead). Fire-and-forget from here: there's no ack,
// the command's effect shows up in the agent's next status snapshot.
app.MapPost("/api/agents/{name}/commands", async (string name, AgentCommandRequest request, ControlPlaneDbContext db, IHubContext<ApplicationPolicyHub> hub) =>
{
    if (await db.Agents.FindAsync(name) is null)
    {
        return Results.NotFound($"Agent '{name}' is not registered.");
    }

    if (request.TargetKind is not (AgentCommandRequest.Service or AgentCommandRequest.Job))
    {
        return Results.BadRequest($"TargetKind must be '{AgentCommandRequest.Service}' or '{AgentCommandRequest.Job}', got '{request.TargetKind}'.");
    }

    if (request.Action is not (AgentCommandRequest.Start or AgentCommandRequest.Stop))
    {
        return Results.BadRequest($"Action must be '{AgentCommandRequest.Start}' or '{AgentCommandRequest.Stop}', got '{request.Action}'.");
    }

    await ApplicationPolicyHubNotifier.SendCommandAsync(hub, name, request);
    return Results.NoContent();
});

// Log forwarding — agents POST buffered batches here (see Enlist.Agent's ControlPlaneLogForwarder);
// the portal's log-tail page GETs them back paged by Id ("everything after the last line I've already
// shown"). Best-effort mirror of the agent's own local log files, not a durable audit trail — see
// LogRetentionSweepService for how short-lived these rows are.
app.MapPost("/api/agents/{agentName}/logs", async (string agentName, SubmitLogEntriesRequest request, ControlPlaneDbContext db) =>
{
    if (request.Entries.Count == 0)
    {
        return Results.Accepted();
    }

    db.AgentLogs.AddRange(request.Entries.Select(e => new AgentLogEntity
    {
        AgentName = agentName,
        ApplicationName = e.ApplicationName,
        Level = e.Level,
        Source = e.Source,
        Text = e.Text,
        TimestampUtc = e.TimestampUtc,
    }));
    await db.SaveChangesAsync();

    return Results.Accepted();
});

app.MapGet("/api/agents/{agentName}/logs", async (string agentName, string application, long? sinceId, int? take, ControlPlaneDbContext db) =>
{
    var effectiveTake = Math.Clamp(take ?? 200, 1, 1000);

    var entries = await db.AgentLogs
        .Where(l => l.AgentName == agentName && l.ApplicationName == application && l.Id > (sinceId ?? 0))
        .OrderBy(l => l.Id)
        .Take(effectiveTake)
        .ToListAsync();

    return Results.Ok(entries.Select(e => new AgentLogEntryDto(e.Id, e.ApplicationName, e.Level, e.Source, e.Text, e.TimestampUtc)));
});

// Agent registry — tags for placement selectors, and a record of every agent ever heard from. Rows are
// created implicitly the first time an agent calls in (see UpsertAgentSeenAsync above), or explicitly
// here if you want to set tags before that agent ever connects.
app.MapGet("/api/agents", async (ControlPlaneDbContext db) =>
    Results.Ok((await db.Agents.OrderBy(m => m.Name).ToListAsync()).Select(AgentToDto)));

app.MapGet("/api/agents/{name}", async (string name, ControlPlaneDbContext db) =>
{
    var agent = await db.Agents.FindAsync(name);
    return agent is null ? Results.NotFound() : Results.Ok(AgentToDto(agent));
});

// Reported BY the agent, not set by an operator: only the agent can know whether a container engine on
// its own host actually answers. Upserts the agent row so a capability report from an agent the control
// plane has never seen still lands somewhere.
app.MapPut("/api/agents/{name}/capabilities", async (string name, SetAgentCapabilitiesRequest request, ControlPlaneDbContext db) =>
{
    var agent = await UpsertAgentSeenAsync(db, name);
    agent.CapabilitiesJson = JsonSerializer.Serialize(request.Capabilities);
    await db.SaveChangesAsync();

    return Results.NoContent();
});

app.MapPut("/api/agents/{name}/tags", async (string name, SetAgentTagsRequest request, ControlPlaneDbContext db, IHubContext<ApplicationPolicyHub> hub) =>
{
    var agent = await db.Agents.FindAsync(name);
    var now = DateTimeOffset.UtcNow;

    if (agent is null)
    {
        agent = new AgentEntity { Name = name, FirstSeenUtc = now, LastSeenUtc = now };
        db.Agents.Add(agent);
    }

    agent.TagsJson = JsonSerializer.Serialize(request.Tags);
    await db.SaveChangesAsync();

    // Tags changing can newly match or unmatch existing tag-selector policy rules for this specific
    // agent — always worth telling it to re-fetch, even if nothing actually changed for it this time
    // (cheap: the agent just finds nothing new).
    await ApplicationPolicyHubNotifier.NotifyChangedAsync(hub, name);

    return Results.Ok(AgentToDto(agent));
});

// The one-click "stop everything on this agent, and don't give it any new work" control (Agents.razor's
// Scheduling toggle). Deliberately control-plane-only: excluding an agent just makes
// ResolveMatchingPoliciesForAgentAsync return an empty list for it, and AgentHost.ReconcileAsync's
// EXISTING "stop whatever isn't in the new list" logic tears down everything currently running for
// free — no agent-side code needed at all. Re-including makes it immediately eligible again for
// whatever already targets it, without touching any policy row.
app.MapPut("/api/agents/{name}/scheduling", async (string name, SetAgentSchedulingRequest request, ControlPlaneDbContext db, IHubContext<ApplicationPolicyHub> hub) =>
{
    var agent = await db.Agents.FindAsync(name);
    if (agent is null)
    {
        return Results.NotFound();
    }

    agent.SchedulingEnabled = request.SchedulingEnabled;
    await db.SaveChangesAsync();

    // Push immediately so the agent reconciles right away (tears everything down, or picks back up)
    // instead of waiting for its next unrelated poll.
    await ApplicationPolicyHubNotifier.NotifyChangedAsync(hub, name);

    return Results.Ok(AgentToDto(agent));
});

// No "still referenced, can't delete" check here (unlike packages below) — with tags-only targeting,
// a policy rule doesn't belong to any one agent the way an explicit-AgentName rule used to. Deleting
// this registry row doesn't touch any policy row (nothing references an agent by foreign key, only by
// tag VALUE), and if this name reconnects later it just re-registers and immediately starts matching
// whatever it always would have — the same posture a broad tag-selector rule already had even before
// this collapse.
app.MapDelete("/api/agents/{name}", async (string name, ControlPlaneDbContext db) =>
{
    var agent = await db.Agents.FindAsync(name);
    if (agent is null)
    {
        return Results.NotFound();
    }

    db.Agents.Remove(agent);
    await db.SaveChangesAsync();

    // AgentReports/AgentLogs for this agent are deliberately left in place, not cascade-deleted — same
    // posture as everywhere else in this schema (no FK between them and Agents): they're history keyed
    // by a name string, not owned rows, and if this agent name registers again later (a
    // renamed/rebuilt box reusing the old hostname) that history still makes sense to have around.
    // They age out on the same retention sweeps as everyone else's (the newest report is always kept).
    return Results.NoContent();
});

// Bare-bones application-policy CRUD — enough for a script or curl to drive desired state until
// there's a portal. Every mutation notifies the affected agent(s) over SignalR so they reconcile
// without waiting for their next poll (see Enlist.Agent's ControlPlaneAssignmentSource).
//
// ?agentName= returns the RAW set of rules currently matching that agent — deliberately NOT collapsed
// through conflict resolution the way the agent-facing endpoint above is. A human looking at "what
// governs agent X" (Agents.razor's detail panel) benefits from seeing every actual rule, conflicts
// included, so they can go fix the conflict; an agent reconciling needs exactly one answer instead.
// Both still start from the identical match set (ResolveMatchingPoliciesForAgentAsync), so they can
// never disagree about WHICH rules match — only about how each one summarizes that same set for its
// own purpose.
app.MapGet("/api/application-policies", async (ControlPlaneDbContext db, string? agentName) =>
{
    if (string.IsNullOrWhiteSpace(agentName))
    {
        return Results.Ok((await db.ApplicationPolicies.ToListAsync()).Select(e => ToDto(e)));
    }

    var agent = await db.Agents.FindAsync(agentName);
    var matches = await ResolveMatchingPoliciesForAgentAsync(db, agentName, agent);

    return Results.Ok(matches.Select(e => ToDto(e)));
});

app.MapPost("/api/application-policies", async (CreateApplicationPolicyRequest request, ControlPlaneDbContext db, IHubContext<ApplicationPolicyHub> hub) =>
{
    if (!IsValidDesiredState(request.DesiredState))
    {
        return Results.BadRequest($"DesiredState must be '{DesiredStates.Running}' or '{DesiredStates.Stopped}', got '{request.DesiredState}'.");
    }

    if (!ApplicationNames.IsValid(request.ApplicationName))
    {
        return Results.BadRequest($"'{request.ApplicationName}' is not a valid application name: {ApplicationNames.Requirement}.");
    }

    if (request.PackageDigest is not null && !PackageDigests.IsValid(request.PackageDigest))
    {
        return Results.BadRequest($"'{request.PackageDigest}' is not a package digest: {PackageDigests.Requirement}.");
    }

    var packageDigest = request.PackageDigest is null ? null : PackageDigests.Normalize(request.PackageDigest);

    if (ValidateCronOverrides(request.CronOverrides) is { } cronError)
    {
        return Results.BadRequest(cronError);
    }

    if (!HasExactlyOne(request.Path is not null, request.PackageDigest is not null))
    {
        return Results.BadRequest("Specify exactly one of Path or PackageDigest, not both and not neither.");
    }

    if (request.RuntimeFlavor is not null && !IsValidRuntimeFlavor(request.RuntimeFlavor))
    {
        return Results.BadRequest($"RuntimeFlavor must be one of [{string.Join(", ", RuntimeFlavors.All)}], got '{request.RuntimeFlavor}'.");
    }

    if (request.Isolation is not null && !IsolationModes.IsValid(request.Isolation.Mode))
    {
        return Results.BadRequest($"Isolation.Mode must be one of [{string.Join(", ", IsolationModes.All)}], got '{request.Isolation.Mode}'.");
    }

    var (resolvedFlavor, flavorError) = await ResolveRuntimeFlavorAsync(db, packageDigest, request.RuntimeFlavor);
    if (flavorError is not null)
    {
        return Results.BadRequest(flavorError);
    }

    if (ValidatePorts(request.Isolation) is {} portError)
    {
        return Results.BadRequest(portError);
    }

    if (ValidateIsolationAgainstFlavor(resolvedFlavor!, request.Isolation) is {} isolationError)
    {
        return Results.BadRequest(isolationError);
    }

    var entity = new ApplicationPolicyEntity
    {
        Id = Guid.NewGuid(),
        TagSelectorJson = JsonSerializer.Serialize(request.TagSelector ?? new Dictionary<string, string>()),
        ApplicationName = request.ApplicationName,
        Path = request.Path,
        PackageDigest = packageDigest,
        DesiredState = request.DesiredState,
        CronOverridesJson = JsonSerializer.Serialize(request.CronOverrides ?? new Dictionary<string, string>()),
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        RuntimeFlavor = resolvedFlavor!,

        // Null rather than a serialized process default, so a rule created without an opinion is
        // stored indistinguishably from a rule that never mentioned isolation at all.
        IsolationJson = SerializeIsolation(request.Isolation),
    };

    db.ApplicationPolicies.Add(entity);
    await db.SaveChangesAsync();

    await NotifyPolicyAsync(hub, db, entity);
    return Results.Created($"/api/application-policies/{entity.Id}", ToDto(entity));
});

app.MapPut("/api/application-policies/{id:guid}", async (Guid id, UpdateApplicationPolicyRequest request, ControlPlaneDbContext db, IHubContext<ApplicationPolicyHub> hub) =>
{
    var entity = await db.ApplicationPolicies.FindAsync(id);
    if (entity is null)
    {
        return Results.NotFound();
    }

    if (request.DesiredState is not null)
    {
        if (!IsValidDesiredState(request.DesiredState))
        {
            return Results.BadRequest($"DesiredState must be '{DesiredStates.Running}' or '{DesiredStates.Stopped}', got '{request.DesiredState}'.");
        }

        entity.DesiredState = request.DesiredState;
    }

    if (request.Path is not null && request.PackageDigest is not null)
    {
        return Results.BadRequest("Specify at most one of Path or PackageDigest in a single update - not both.");
    }

    // Switching source type is explicit: setting one clears the other, since they're alternatives, not
    // independent fields — see ApplicationPolicyDto.
    if (request.Path is not null)
    {
        entity.Path = request.Path;
        entity.PackageDigest = null;
    }

    if (request.PackageDigest is not null)
    {
        if (!PackageDigests.IsValid(request.PackageDigest))
        {
            return Results.BadRequest($"'{request.PackageDigest}' is not a package digest: {PackageDigests.Requirement}.");
        }

        entity.PackageDigest = PackageDigests.Normalize(request.PackageDigest);
        entity.Path = null;
    }

    if (request.CronOverrides is not null)
    {
        if (ValidateCronOverrides(request.CronOverrides) is { } updateCronError)
        {
            return Results.BadRequest(updateCronError);
        }

        entity.CronOverridesJson = JsonSerializer.Serialize(request.CronOverrides);
    }

    if (request.RuntimeFlavor is not null)
    {
        if (!IsValidRuntimeFlavor(request.RuntimeFlavor))
        {
            return Results.BadRequest($"RuntimeFlavor must be one of [{string.Join(", ", RuntimeFlavors.All)}], got '{request.RuntimeFlavor}'.");
        }

        entity.RuntimeFlavor = request.RuntimeFlavor;
    }

    // Same idempotent create-or-update posture as RuntimeFlavor, and for the same reason: migrating an
    // application between process and container isolation is a legitimate redeploy-time change, and is
    // in fact the headline capability here (docs/03-architecture/Container-Story.md §2.1 — move one agent at a time,
    // roll back instantly). Null leaves the existing setting alone rather than resetting it to process.
    if (request.Isolation is not null)
    {
        if (!IsolationModes.IsValid(request.Isolation.Mode))
        {
            return Results.BadRequest($"Isolation.Mode must be one of [{string.Join(", ", IsolationModes.All)}], got '{request.Isolation.Mode}'.");
        }

        if (ValidatePorts(request.Isolation) is {} updatePortError)
        {
            return Results.BadRequest(updatePortError);
        }

        entity.IsolationJson = SerializeIsolation(request.Isolation);
    }

    // Validated on the RESULTING combination, not on what this request happened to mention. An update
    // that changes only the package can make an untouched container setting invalid, and one that
    // changes only the isolation can collide with an untouched flavor — checking either field in
    // isolation would miss half the ways this rule can become unrunnable.
    // Only re-derived when the rule actually points at a package. A Path-based rule has nothing to
    // inspect, so its flavor is whatever the explicit block above set or whatever it already was —
    // deriving unconditionally here would silently reset such a rule to the default on every update.
    if (entity.PackageDigest is not null)
    {
        var (effectiveFlavor, updateFlavorError) = await ResolveRuntimeFlavorAsync(
            db, entity.PackageDigest, request.RuntimeFlavor);

        if (updateFlavorError is not null)
        {
            return Results.BadRequest(updateFlavorError);
        }

        entity.RuntimeFlavor = effectiveFlavor!;
    }

    if (ValidateIsolationAgainstFlavor(entity.RuntimeFlavor, DeserializeIsolation(entity.IsolationJson)) is { } updateIsolationError)
    {
        return Results.BadRequest(updateIsolationError);
    }

    entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    await NotifyPolicyAsync(hub, db, entity);
    return Results.Ok(ToDto(entity));
});

app.MapDelete("/api/application-policies/{id:guid}", async (Guid id, ControlPlaneDbContext db, IHubContext<ApplicationPolicyHub> hub) =>
{
    var entity = await db.ApplicationPolicies.FindAsync(id);
    if (entity is null)
    {
        return Results.NotFound();
    }

    db.ApplicationPolicies.Remove(entity);
    await db.SaveChangesAsync();

    await NotifyPolicyAsync(hub, db, entity);
    return Results.NoContent();
});

// Package distribution — the other half of "log into each agent and copy files by hand." Upload once,
// content-addressed by SHA-256 computed server-side from what was actually received (never trusted
// from the client); agents fetch by digest and cache locally, so redeploying an unchanged package to N
// agents costs one upload, not N manual copies.
// application is optional — enlist-deploy always sends its --app name; a raw POST here can omit it
// (see PackageEntity.ApplicationName for the first-write-wins backfill rule when it's later supplied
// for a digest that already exists without one).
app.MapPost("/api/packages", async (HttpRequest request, string? application, PackageBlobStore store, ControlPlaneDbContext db) =>
{
    if (application is not null && !ApplicationNames.IsValid(application))
    {
        return Results.BadRequest($"'{application}' is not a valid application name: {ApplicationNames.Requirement}.");
    }

    // Before the body is read — the feature turns read-only once reading starts. Over the limit, Kestrel
    // answers 413 by itself.
    if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
    {
        bodySize.MaxRequestBodySize = maxUploadBytes;
    }

    var (digest, sizeBytes, alreadyExisted) = await store.SaveAsync(request.Body, request.HttpContext.RequestAborted);
    string runtimeFlavor;
    try
    {
        runtimeFlavor = DetectRuntimeFlavor(store, digest);
    }
    catch (InvalidDataException ex)
    {
        // Not a zip at all. The bytes were saved before anything could look at them (the digest is of
        // what was received), so a blob that is new is removed again; one that already existed is a
        // real package somebody uploaded correctly and stays.
        if (!alreadyExisted)
        {
            store.TryDelete(digest);
        }

        return Results.BadRequest($"The upload is not a zip archive ({ex.Message}) - nothing was stored.");
    }

    var manifestJson = TryScanManifestJson(store, digest, app.Logger);

    if (!alreadyExisted)
    {
        var entity = new PackageEntity { Digest = digest, SizeBytes = sizeBytes, UploadedAtUtc = DateTimeOffset.UtcNow, ApplicationName = application, RuntimeFlavor = runtimeFlavor, ManifestJson = manifestJson };
        db.Packages.Add(entity);
        await SaveWithNextVersionAsync(db, entity, application);
    }
    else
    {
        // Re-detect and overwrite even on a re-upload of an existing digest — self-heals any row that
        // predates this field (RuntimeFlavor defaulting silently to "net10.0" forever would be exactly
        // the kind of guessed, unverified value this codebase avoids everywhere else) at zero extra
        // cost, since the blob is already open to hash on every SaveAsync call regardless.
        var existing = await db.Packages.FindAsync(digest);
        if (existing is not null)
        {
            existing.RuntimeFlavor = runtimeFlavor;
            existing.ManifestJson ??= manifestJson;
            if (application is not null && existing.ApplicationName is null)
            {
                existing.ApplicationName = application;
                await SaveWithNextVersionAsync(db, existing, application);
            }
            else
            {
                await db.SaveChangesAsync();
            }
        }
    }

    return Results.Ok(new UploadPackageResponse(digest, sizeBytes, alreadyExisted, runtimeFlavor));
});

// The digest is the one route value that becomes a path (PackageBlobStore.PathFor), so it is checked
// against its shape before anything looks for it — a "..\..\name" is a 400 here, never a file lookup.
app.MapGet("/api/packages/{digest}", (string digest, PackageBlobStore store) =>
{
    if (!PackageDigests.IsValid(digest))
    {
        return Results.BadRequest($"'{digest}' is not a package digest: {PackageDigests.Requirement}.");
    }

    digest = PackageDigests.Normalize(digest);
    return store.Exists(digest) ? Results.Stream(store.OpenRead(digest), "application/zip") : Results.NotFound();
});

// The static "what does this package contain" view — a metadata-only scan (see PackageManifestScanner),
// so it works the moment a package is uploaded, before any policy targets it and before any agent has
// ever run it. Scanned once, at upload, and served from the row: the manifest is a fact about immutable
// bytes, and re-extracting the zip on every request was the cost the Applications page paid once per
// digest on every refresh. A row from before the column existed (or whose scan failed at upload) is
// scanned on its first request and kept.
app.MapGet("/api/packages/{digest}/manifest", async (string digest, PackageBlobStore store, ControlPlaneDbContext db) =>
{
    if (!PackageDigests.IsValid(digest))
    {
        return Results.BadRequest($"'{digest}' is not a package digest: {PackageDigests.Requirement}.");
    }

    digest = PackageDigests.Normalize(digest);

    var package = await db.Packages.FindAsync(digest);
    if (package is null || !store.Exists(digest))
    {
        return Results.NotFound();
    }

    if (package.ManifestJson is null)
    {
        package.ManifestJson = JsonSerializer.Serialize(ScanManifest(store, digest), JsonSerializerOptions.Web);
        await db.SaveChangesAsync();
    }

    return Results.Text(package.ManifestJson, "application/json");
});

app.MapGet("/api/packages", async (ControlPlaneDbContext db) =>
    Results.Ok((await db.Packages.OrderByDescending(p => p.UploadedAtUtc).ToListAsync())
        .Select(p => new PackageInfo(p.Digest, p.SizeBytes, p.UploadedAtUtc, p.ApplicationName, p.VersionNumber, p.RuntimeFlavor))));

// Manual delete — the ONLY other path that ever removes a package row is
// PackageRetentionSweepService, which waits out a grace period after a digest stops being referenced.
// This is the escape hatch for "I know this build is bad, get rid of it now," and it's deliberately
// strict: any policy rule (Running or Stopped — Stopped still means "this is what would run if flipped
// back on") currently pointing at this digest blocks the delete outright. No force option — the portal
// disables the button for the same reason, but the check belongs here too since the endpoint is
// reachable directly.
app.MapDelete("/api/packages/{digest}", async (string digest, PackageBlobStore store, ControlPlaneDbContext db) =>
{
    if (!PackageDigests.IsValid(digest))
    {
        return Results.BadRequest($"'{digest}' is not a package digest: {PackageDigests.Requirement}.");
    }

    digest = PackageDigests.Normalize(digest);

    var package = await db.Packages.FindAsync(digest);
    if (package is null)
    {
        return Results.NotFound();
    }

    var referencedBy = await db.ApplicationPolicies
        .Where(a => a.PackageDigest == digest)
        .Select(a => a.ApplicationName)
        .ToListAsync();

    if (referencedBy.Count > 0)
    {
        return Results.Conflict(
            $"'{digest}' is still referenced by {referencedBy.Count} policy rule(s) ({string.Join(", ", referencedBy)}) - repoint or delete those first.");
    }

    store.TryDelete(digest);
    db.Packages.Remove(package);
    await db.SaveChangesAsync();

    return Results.NoContent();
});

// Level 3's boundary, made concrete (docs/03-architecture/Container-Story.md §8.4): everything enList knows about
// where applications are actually listening, in one pull. A reverse proxy polls this. enList never
// pushes, never routes, and holds no opinion about hostnames or TLS.
app.MapGet("/api/endpoints", async (string? application, ControlPlaneDbContext db) =>
    Results.Ok(await GetEndpointsAsync(db, application)));

// The one real proxy integration, in Traefik's HTTP-provider shape.
//
// Deliberately emits `services` and NOT `routers`. A service is the part enList actually knows: the
// live set of servers for an application. A router is a hostname, a TLS certificate and a path rule —
// facts about how an organisation wants traffic to arrive, which enList has no business inventing and
// §8.4 explicitly refuses to own. An operator declares routers once in their own Traefik config and
// points each at a service named here; enList keeps the server list underneath it current as
// applications move between agents.
//
// Stale endpoints are EXCLUDED here rather than flagged. A human reading /api/endpoints benefits from
// seeing a last-known value; a proxy routing live traffic to a host presumed dead does not.
app.MapGet("/api/endpoints/traefik", async (ControlPlaneDbContext db) =>
{
    var services = (await GetEndpointsAsync(db, application: null))
        .Where(e => !e.Stale)
        .GroupBy(e => e.ApplicationName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            g => TraefikServiceName(g.Key),
            g => new TraefikService(new TraefikLoadBalancer(
                g.Select(e => new TraefikServer($"http://{e.HostAddress}:{e.HostPort}")).ToList())));

    return Results.Ok(new TraefikDynamicConfig(new TraefikHttp(services)));
});

app.Run();

/// <summary>Highest VersionNumber ever assigned under this name, plus one — including packages already garbage-collected, since VersionNumber is never reused. Not called concurrently for the same name in practice (uploads are one-at-a-time operator actions), so no extra locking beyond the DbContext's own.</summary>
/// <summary>
/// Numbers the package (highest existing + 1 for its application) and saves. Two uploads for one
/// application at the same instant compute the same next number; the unique index on
/// (ApplicationName, VersionNumber) turns the loser into a DbUpdateException here, and it is simply
/// numbered again — two v7s is what this replaces.
/// </summary>
static async Task SaveWithNextVersionAsync(ControlPlaneDbContext db, PackageEntity entity, string? application)
{
    for (var attempt = 1; ; attempt++)
    {
        entity.VersionNumber = application is null ? null : await NextVersionNumberAsync(db, application);
        try
        {
            await db.SaveChangesAsync();
            return;
        }
        catch (DbUpdateException) when (application is not null && attempt < 32)
        {
            // Another upload took this number between the read and the write; read again, after a
            // short random pause so a burst of N losers does not all re-read the same maximum at once.
            // N concurrent uploads can cost the unluckiest of them N - 1 collisions, so the bound is generous.
            await Task.Delay(Random.Shared.Next(2, 30));
        }
    }
}

static async Task<int> NextVersionNumberAsync(ControlPlaneDbContext db, string applicationName)
{
    var highest = await db.Packages
        .Where(p => p.ApplicationName == applicationName)
        .Select(p => (int?)p.VersionNumber)
        .MaxAsync();

    return (highest ?? 0) + 1;
}

/// <summary>
/// A net10.0 (SDK-style) build always emits a "&lt;AssemblyName&gt;.deps.json" alongside its DLL — a
/// pure .NET-Core-and-later convention net472 doesn't have at all (confirmed against this repo's own
/// two runner builds: only the modern Enlist.Runner produces one, Enlist.Runner.Legacy's net472 output
/// never does). That single, reliable signal is all detection needs — no manifest, no user-declared
/// flag. Reads the just-saved blob back from disk rather than the original request stream, which
/// SaveAsync has already fully consumed by this point.
/// </summary>
/// <summary>Extracts the blob to a throwaway temp directory and reflects over it — MetadataLoadContext needs real file paths to resolve each DLL's dependencies against its siblings. Throws when the package cannot be scanned.</summary>
static PackageManifestDto ScanManifest(PackageBlobStore store, string digest)
{
    var tempDir = Path.Combine(Path.GetTempPath(), "enlist-manifest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try
    {
        using (var zipStream = store.OpenRead(digest))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(tempDir);
        }

        return PackageManifestScanner.Scan(tempDir);
    }
    finally
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — an OS temp directory left behind on failure is harmless and gets
            // swept by the OS eventually; not worth failing the request over.
        }
    }
}

/// <summary>The upload-time scan: a package that cannot be scanned is still stored (the bytes are what was uploaded, and an agent may run them fine); the manifest is logged as missing and scanned again on first request.</summary>
static string? TryScanManifestJson(PackageBlobStore store, string digest, ILogger logger)
{
    try
    {
        return JsonSerializer.Serialize(ScanManifest(store, digest), JsonSerializerOptions.Web);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Package {Digest} was stored, but its manifest could not be scanned; it will be scanned again on first request.", digest);
        return null;
    }
}

static string DetectRuntimeFlavor(PackageBlobStore store, string digest)
{
    using var zipStream = store.OpenRead(digest);
    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
    var isModern = archive.Entries.Any(e => e.Name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase));
    return isModern ? RuntimeFlavors.Default : RuntimeFlavors.NetFramework472;
}

static bool IsValidDesiredState(string value) => DesiredStates.All.Contains(value);

static bool IsValidRuntimeFlavor(string value) => RuntimeFlavors.All.Contains(value);

static bool HasExactlyOne(bool a, bool b) => a ^ b;

static Dictionary<string, string> DeserializeTags(string json) =>
    JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

/// <summary>An agent's own stored tags, plus the implicit self-tag every agent carries without it ever being persisted — "target this one agent" is just the most specific possible selector, {"agent": name}. The synthesized value always wins over a same-named real tag (an operator setting an actual "agent" tag to something else would be confusing regardless; this keeps the self-tag authoritative rather than silently shadowable).</summary>
static Dictionary<string, string> EffectiveAgentTags(string agentName, string agentTagsJson)
{
    var tags = DeserializeTags(agentTagsJson);
    tags["agent"] = agentName;
    return tags;
}

/// <summary>True if tags contains every key/value pair in selector — an empty selector matches every agent.</summary>
static bool SelectorMatches(IReadOnlyDictionary<string, string> tags, IReadOnlyDictionary<string, string> selector)
{
    foreach (var (key, value) in selector)
    {
        if (!tags.TryGetValue(key, out var actual) || actual != value)
        {
            return false;
        }
    }

    return true;
}

static bool CronOverridesEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
{
    if (a.Count != b.Count)
    {
        return false;
    }

    foreach (var (key, value) in a)
    {
        if (!b.TryGetValue(key, out var otherValue) || otherValue != value)
        {
            return false;
        }
    }

    return true;
}

/// <summary>Every policy rule whose TagSelector is satisfied by agentName's effective tags (its own stored tags plus the implicit self-tag — see EffectiveAgentTags), UN-deduplicated and UN-resolved: a single application can legitimately appear more than once here if more than one rule currently matches it for this agent. The one place this matching happens — both /api/agents/{name}/policies (via ResolveEffectivePoliciesForAgentAsync below) and /api/application-policies?agentName= call this, so they can never disagree about WHICH rules match, only about how each summarizes the result.
///
/// An excluded agent (SchedulingEnabled false) qualifies for NOTHING, full stop — checked here, the one
/// shared entry point, rather than in each caller, so the agent-facing and portal-facing endpoints can't
/// drift on what "excluded" means the same way they can't drift on tag matching. agent is null for a
/// not-yet-registered name, which is never excluded (there's nothing to exclude yet).</summary>
static async Task<List<ApplicationPolicyEntity>> ResolveMatchingPoliciesForAgentAsync(ControlPlaneDbContext db, string agentName, AgentEntity? agent)
{
    if (agent is { SchedulingEnabled: false })
    {
        return [];
    }

    var effectiveTags = EffectiveAgentTags(agentName, agent?.TagsJson ?? "{}");
    var candidates = await db.ApplicationPolicies.ToListAsync();
    return candidates.Where(a => SelectorMatches(effectiveTags, DeserializeTags(a.TagSelectorJson))).ToList();
}

/// <summary>
/// Collapses ResolveMatchingPoliciesForAgentAsync's raw match set down to at most one outcome per
/// ApplicationName — what the agent actually needs to reconcile against. Two or more rules matching
/// the same application are fine as long as they AGREE on DesiredState/Path/PackageDigest/
/// CronOverrides (the common case: a canary rule and a fallback rule that happen to reference the same
/// build right now) — arbitrarily, the first is returned since they're equivalent. If they DISAGREE on
/// any of those, there is no single correct answer to give the agent, so this fails loud with a
/// ConflictReason instead of silently picking one — the same "configuration problem, not transient"
/// posture AgentHost.StartApplicationAsync already uses for a missing runtime flavor, now surfaced from
/// the control plane instead of discovered agent-side.
/// </summary>
static async Task<List<(ApplicationPolicyEntity Entity, string? ConflictReason)>> ResolveEffectivePoliciesForAgentAsync(ControlPlaneDbContext db, string agentName, AgentEntity? agent)
{
    var matches = await ResolveMatchingPoliciesForAgentAsync(db, agentName, agent);

    var resolved = new List<(ApplicationPolicyEntity, string?)>();
    foreach (var group in matches.GroupBy(a => a.ApplicationName, StringComparer.OrdinalIgnoreCase))
    {
        var rules = group.ToList();
        var first = rules[0];

        var allAgree = rules.All(r =>
            r.DesiredState == first.DesiredState &&
            r.Path == first.Path &&
            r.PackageDigest == first.PackageDigest &&
            CronOverridesEqual(DeserializeTags(r.CronOverridesJson), DeserializeTags(first.CronOverridesJson)) &&

            // Isolation joins the same agreement test as everything else that decides what actually
            // runs. Two rules matching one agent that disagree on process-vs-container are as
            // unresolvable as two disagreeing on which digest to run — there is no sensible winner, and
            // silently picking one would start the application the wrong way with no indication why.
            DeserializeIsolation(r.IsolationJson).AgreesWith(DeserializeIsolation(first.IsolationJson)));

        if (allAgree)
        {
            resolved.Add((first, null));
        }
        else
        {
            var reason =
                $"{rules.Count} policy rules currently match this agent for '{group.Key}' and disagree on what should run " +
                $"(rule IDs: {string.Join(", ", rules.Select(r => r.Id))}) - resolve by editing or disabling one of them.";
            resolved.Add((first, reason));
        }
    }

    return ApplyPortCollisions(resolved);
}

/// <summary>
/// A second, cross-application conflict class (docs/03-architecture/Container-Story.md §8.2).
///
/// The conflict above asks "do two rules for the SAME application disagree?" and is resolved per
/// application group. A static host port collides differently: two rules for DIFFERENT applications,
/// each internally consistent, both demanding host port 8080 on the same agent. Whichever starts second
/// fails at the engine with a port-in-use error that names nothing useful, so it is caught here and both
/// sides are told, by name, who they are colliding with.
///
/// Only STATIC ports can collide. A null HostPort means "let the engine allocate", which is exactly the
/// mechanism that lets one tag-selector rule fan out across many agents without contention.
/// </summary>
static List<(ApplicationPolicyEntity Entity, string? ConflictReason)> ApplyPortCollisions(
    List<(ApplicationPolicyEntity Entity, string? ConflictReason)> resolved)
{
    var claims = new Dictionary<(int Port, string Protocol), List<string>>();

    foreach (var (entity, conflictReason) in resolved)
    {
        // An already-conflicted rule has no single winning spec to read ports from — same reason
        // Path/PackageDigest are meaningless on a conflicted row.
        if (conflictReason is not null)
        {
            continue;
        }

        foreach (var port in DeserializeIsolation(entity.IsolationJson).Ports ?? [])
        {
            if (port.HostPort is not {} hostPort)
            {
                continue;
            }

            var key = (hostPort, port.Protocol);
            if (!claims.TryGetValue(key, out var claimants))
            {
                claims[key] = claimants = [];
            }

            if (!claimants.Contains(entity.ApplicationName, StringComparer.OrdinalIgnoreCase))
            {
                claimants.Add(entity.ApplicationName);
            }
        }
    }

    var contested = claims.Where(c => c.Value.Count > 1).ToList();
    if (contested.Count == 0)
    {
        return resolved;
    }

    return resolved.Select(r =>
    {
        if (r.ConflictReason is not null)
        {
            return r;
        }

        var hits = contested
            .Where(c => c.Value.Contains(r.Entity.ApplicationName, StringComparer.OrdinalIgnoreCase))
            .Select(c => $"host port {c.Key.Port}/{c.Key.Protocol} is also claimed by " +
                         string.Join(", ", c.Value.Where(n => !string.Equals(n, r.Entity.ApplicationName, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        return hits.Count == 0
            ? r
            : (r.Entity, $"Port collision on this agent - {string.Join("; ", hits)}. Change one of the static host ports, or leave it unset to let the engine allocate.");
    }).ToList();
}

static async Task<AgentEntity> UpsertAgentSeenAsync(ControlPlaneDbContext db, string agentName)
{
    var agent = await db.Agents.FindAsync(agentName);
    var now = DateTimeOffset.UtcNow;

    if (agent is null)
    {
        agent = new AgentEntity { Name = agentName, FirstSeenUtc = now, LastSeenUtc = now };
        db.Agents.Add(agent);
    }
    else
    {
        agent.LastSeenUtc = now;
    }

    await db.SaveChangesAsync();
    return agent;
}

/// <summary>Notifies whichever agents a policy mutation actually affects — every currently-registered agent whose effective tags satisfy this rule's selector. An agent that's never registered yet has nothing to notify (there's no hub group for it to be listening on); its first-ever GET /api/agents/{name}/policies resolves fresh from the database regardless, so nothing is missed.</summary>
static async Task NotifyPolicyAsync(IHubContext<ApplicationPolicyHub> hub, ControlPlaneDbContext db, ApplicationPolicyEntity entity)
{
    var selector = DeserializeTags(entity.TagSelectorJson);
    var agents = await db.Agents.ToListAsync();
    foreach (var agent in agents)
    {
        if (SelectorMatches(EffectiveAgentTags(agent.Name, agent.TagsJson), selector))
        {
            await ApplicationPolicyHubNotifier.NotifyChangedAsync(hub, agent.Name);
        }
    }
}

static ApplicationPolicyDto ToDto(ApplicationPolicyEntity entity, string? conflictReason = null) => new(
    entity.Id,
    entity.ApplicationName,
    entity.Path,
    entity.DesiredState,
    DeserializeTags(entity.CronOverridesJson),
    entity.UpdatedAtUtc,
    DeserializeTags(entity.TagSelectorJson),
    entity.PackageDigest,
    entity.RuntimeFlavor,
    conflictReason,
    DeserializeIsolation(entity.IsolationJson));

/// <summary>Null in, null out: a rule with no isolation opinion stays null, so nothing needs backfilling and "process" is never spuriously persisted.</summary>
static string? SerializeIsolation(IsolationSpec? spec) => spec is null ? null : JsonSerializer.Serialize(spec);

/// <summary>A null column and an explicit process spec mean the same thing — see ApplicationPolicyEntity.IsolationJson. Malformed JSON also falls back to the process default rather than throwing: one unreadable row must not take down policy resolution for every agent, and "ran the way it always did" is the safe reading of an unparseable isolation block.</summary>
static IsolationSpec DeserializeIsolation(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return IsolationSpec.ProcessDefault;
    }

    try
    {
        return JsonSerializer.Deserialize<IsolationSpec>(json) ?? IsolationSpec.ProcessDefault;
    }
    catch (JsonException)
    {
        return IsolationSpec.ProcessDefault;
    }
}

static AgentDto AgentToDto(AgentEntity entity) => new(
    entity.Name,
    DeserializeTags(entity.TagsJson),
    entity.FirstSeenUtc,
    entity.LastSeenUtc,
    entity.SchedulingEnabled,
    DeserializeCapabilities(entity.CapabilitiesJson));

/// <summary>Null stays null — "this agent has not told us" is a real, displayable state, distinct from "it told us it has nothing". Malformed JSON is treated the same way rather than throwing: one bad row must not break the agent list.</summary>
static AgentCapabilitiesDto? DeserializeCapabilities(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return null;
    }

    try
    {
        return JsonSerializer.Deserialize<AgentCapabilitiesDto>(json);
    }
    catch (JsonException)
    {
        return null;
    }
}

/// <summary>The same 5-minute liveness window the portal already uses to call an agent Online/Offline. Defined once here so the endpoint feed and the UI can never disagree about whether an agent counts as alive.</summary>
static TimeSpan AgentLivenessThreshold() => TimeSpan.FromMinutes(5);

/// <summary>
/// Every endpoint every agent last reported, optionally narrowed to one application.
///
/// **On reading the opaque blob.** `AgentReportEntity.SnapshotJson` is stored unparsed on purpose, so
/// the agent's status shape can evolve with no control-plane migration (docs/03-architecture/SAD.md §5). This
/// projection necessarily reads it — but reading at QUERY time is a far weaker coupling than storing
/// parsed columns: there is no schema and no migration, and a shape this build cannot understand
/// degrades the feed (an agent is skipped) instead of breaking ingestion for everyone. The parse is
/// deliberately tolerant for exactly that reason.
/// </summary>
static async Task<List<EndpointDto>> GetEndpointsAsync(ControlPlaneDbContext db, string? application)
{
    var results = new List<EndpointDto>();

    foreach (var agent in await db.Agents.ToListAsync())
    {
        var latest = await db.AgentReports
            .Where(r => r.AgentName == agent.Name)
            .OrderByDescending(r => r.ReportedAtUtc)
            .FirstOrDefaultAsync();

        if (latest is null)
        {
            continue;
        }

        // An agent that stopped checking in still has a last-known report. Its endpoints are reported
        // with Stale = true rather than dropped: "the agent went quiet" and "the application stopped"
        // are different events, and an operator has to be able to tell them apart.
        var stale = DateTimeOffset.UtcNow - agent.LastSeenUtc >= AgentLivenessThreshold();

        ReportedSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ReportedSnapshot>(latest.SnapshotJson, EndpointFeedJson());
        }
        catch (JsonException)
        {
            continue;
        }

        foreach (var reported in snapshot?.Applications ?? [])
        {
            if (application is not null && !string.Equals(reported.Name, application, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var endpoint in reported.Endpoints ?? [])
            {
                results.Add(new EndpointDto(
                    reported.Name, agent.Name, endpoint.Name, endpoint.Protocol,
                    endpoint.ContainerPort, endpoint.HostPort, endpoint.HostAddress,
                    stale, latest.ReportedAtUtc));
            }
        }
    }

    return [.. results.OrderBy(e => e.ApplicationName).ThenBy(e => e.AgentName)];
}

/// <summary>Case-insensitive because the agent writes this blob with its own serializer settings; this projection must not depend on matching them exactly, which is the point of reading the blob tolerantly.</summary>
static JsonSerializerOptions EndpointFeedJson() => new() { PropertyNameCaseInsensitive = true };

/// <summary>Traefik service names are referenced from an operator's own router config, so they must be stable and predictable — derived from the application name, with anything Traefik would not accept replaced.</summary>
static string TraefikServiceName(string applicationName) =>
    new string(applicationName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).ToLowerInvariant();

/// <summary>
/// The runtime flavor a policy should carry, given what it points at.
///
/// When the rule references a PACKAGE, the package's own detected flavor is authoritative: it was
/// determined by inspecting the actual uploaded bytes (see DetectRuntimeFlavor), whereas anything the
/// caller supplies is a claim about them. Were they independent, a policy would default to net10.0 no
/// matter what the package actually is, making upload-time detection
/// decorative and a net472 package would be routed to the modern runner. Every LegacySample rule on
/// this project's own demo was in exactly that state.
///
/// A rule pointing at a local PATH has no package to inspect, so the caller's value (or the default)
/// stands — that is the original step-3 shape and is unaffected.
/// </summary>
static async Task<(string? Flavor, string? Error)> ResolveRuntimeFlavorAsync(
    ControlPlaneDbContext db, string? packageDigest, string? requested)
{
    if (packageDigest is null)
    {
        return (requested ?? RuntimeFlavors.Default, null);
    }

    var package = await db.Packages.FirstOrDefaultAsync(p => p.Digest == packageDigest);
    if (package is null)
    {
        // Accepted, this would fail only on an agent, at download time, with nothing in the portal to
        // say why the application never started.
        return (null,
            $"PackageDigest '{packageDigest}' is not a package this control plane has - upload it first " +
            "(enlist-deploy, or the portal's Upload), then reference the digest that returns.");
    }

    // Disagreement is rejected rather than silently resolved. Whichever way it was decided, one of the
    // two values would be wrong in a way nobody could see afterwards.
    if (requested is not null && !string.Equals(requested, package.RuntimeFlavor, StringComparison.OrdinalIgnoreCase))
    {
        return (null,
            $"RuntimeFlavor '{requested}' contradicts the package, which was detected as '{package.RuntimeFlavor}' " +
            "when it was uploaded. The package's own flavor is authoritative - omit RuntimeFlavor and it will be used.");
    }

    return (package.RuntimeFlavor, null);
}

/// <summary>
/// Cron overrides checked where the person who typed them is still standing. Without this an override
/// with a typo was stored, pushed, and discovered by the agent — which logged a FormatException and
/// left the job unscheduled, with nothing in the portal to say why.
/// </summary>
static string? ValidateCronOverrides(IReadOnlyDictionary<string, string>? overrides)
{
    foreach (var (job, cron) in overrides ?? new Dictionary<string, string>())
    {
        if (string.IsNullOrWhiteSpace(job))
        {
            return "CronOverrides has an entry with an empty job name.";
        }

        if (CronExpressions.Validate(cron) is { } error)
        {
            return $"CronOverrides['{job}']: {error}";
        }
    }

    return null;
}

/// <summary>
/// Port numbers a policy declares, checked before they can reach the engine.
///
/// Without this, a typo'd port travels all the way to `docker run`, which rejects it with a message
/// about its own command line — naming neither the rule nor the application. Cheap to catch here,
/// where the person who typed it is still standing.
/// </summary>
static string? ValidatePorts(IsolationSpec? isolation)
{
    foreach (var port in isolation?.Ports ?? [])
    {
        if (port.ContainerPort is < 1 or > 65535)
        {
            return $"Container port must be between 1 and 65535, got {port.ContainerPort}.";
        }

        if (port.HostPort is {} hostPort && hostPort is < 1 or > 65535)
        {
            return $"Host port must be between 1 and 65535, got {hostPort}. Omit it entirely to let the engine allocate one.";
        }

        if (!string.Equals(port.Protocol, "tcp", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(port.Protocol, "udp", StringComparison.OrdinalIgnoreCase))
        {
            return $"Port protocol must be tcp or udp, got '{port.Protocol}'.";
        }
    }

    return null;
}

/// <summary>
/// Whether a flavor and an isolation mode can actually run together (docs/03-architecture/Container-Story.md §4.2).
///
/// The two axes are independent by design, but not every square of the grid is implemented: the enList
/// runner image is Linux .NET 10, so a net472 package inside it has no .NET Framework to run on.
///
/// Rejected here, loudly, because the runtime symptom is silent. A net472 assembly that cannot load is
/// SKIPPED by discovery (see Enlist.Runner's DiscoverPlugins, which swallows load failures so one bad
/// DLL cannot kill an application), so the application would report Running with zero services and zero
/// jobs — no error anywhere, nothing to search for.
/// </summary>
static string? ValidateIsolationAgainstFlavor(string flavor, IsolationSpec? isolation)
{
    if (isolation?.IsContainer != true || !string.Equals(flavor, RuntimeFlavors.NetFramework472, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return
        $"'{RuntimeFlavors.NetFramework472}' cannot run in a container: the enList runner image is Linux .NET 10, " +
        "so the application's assemblies would fail to load and it would report Running with nothing in it. " +
        "Windows containers for net472 are not supported yet - run this application as a process instead.";
}

/// <summary>Just enough of the agent's snapshot to find endpoints — deliberately NOT the full AgentStatusSnapshot type, which lives in Enlist.Agent and which the control plane does not reference. Extra fields in the blob are ignored.</summary>
internal sealed record ReportedSnapshot(List<ReportedApplication>? Applications);

internal sealed record ReportedApplication(string Name, List<ResolvedEndpointDto>? Endpoints);

internal sealed record TraefikDynamicConfig(TraefikHttp Http);

internal sealed record TraefikHttp(Dictionary<string, TraefikService> Services);

internal sealed record TraefikService(TraefikLoadBalancer LoadBalancer);

internal sealed record TraefikLoadBalancer(List<TraefikServer> Servers);

internal sealed record TraefikServer(string Url);
/// <summary>Exposed for WebApplicationFactory-based integration tests (Enlist.ControlPlane.Tests).</summary>
public partial class Program;
