using Enlist.ControlPlane.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enlist.ControlPlane;

public sealed class PackageRetentionOptions
{
    /// <summary>How long a package stays around after it stops being referenced by any assignment — the rollback window. Design doc: "rollback expressed as pointing an assignment at the previous digest" only works while that digest still exists.</summary>
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);
}

/// <summary>
/// Mark-and-sweep garbage collection for package blobs. Every sweep: any package with zero
/// assignments currently pointing at it gets marked (UnreferencedSinceUtc set, if not already) or, if
/// it was already marked more than RetentionPeriod ago, deleted. A package that's referenced again
/// (rolled back to) gets its mark cleared, resetting the clock entirely — same semantics as "still in
/// use" for any garbage collector.
/// </summary>
public sealed class PackageRetentionSweepService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly PackageRetentionOptions _options;
    private readonly ILogger<PackageRetentionSweepService> _logger;

    public PackageRetentionSweepService(IServiceProvider services, IOptions<PackageRetentionOptions> options, ILogger<PackageRetentionSweepService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep costs disk space, not correctness — every other assignment/package
                // operation is unaffected, so this must not crash the whole control plane process.
                // Said rather than swallowed: a database refusing every sweep is news before the disk fills.
                _logger.LogWarning(ex, "Package retention sweep failed; will retry in {Interval}.", _options.SweepInterval);
            }

            try
            {
                await Task.Delay(_options.SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<PackageBlobStore>();

        var referencedDigests = await db.ApplicationPolicies
            .Where(a => a.PackageDigest != null)
            .Select(a => a.PackageDigest!)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var referenced = new HashSet<string>(referencedDigests, StringComparer.OrdinalIgnoreCase);

        var packages = await db.Packages.ToListAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var changed = false;

        foreach (var package in packages)
        {
            if (referenced.Contains(package.Digest))
            {
                if (package.UnreferencedSinceUtc is not null)
                {
                    package.UnreferencedSinceUtc = null;
                    changed = true;
                }

                continue;
            }

            if (package.UnreferencedSinceUtc is null)
            {
                // Newly unreferenced this sweep — start the grace period, don't delete yet.
                package.UnreferencedSinceUtc = now;
                changed = true;
                continue;
            }

            if (now - package.UnreferencedSinceUtc.Value >= _options.RetentionPeriod)
            {
                if (store.TryDelete(package.Digest))
                {
                    db.Packages.Remove(package);
                    changed = true;
                    _logger.LogInformation("Package retention: deleted {Digest} ({Application} v{Version}), unreferenced since {Since}.", package.Digest, package.ApplicationName ?? "no application", package.VersionNumber, package.UnreferencedSinceUtc);
                }
                else
                {
                    _logger.LogWarning("Package retention: could not delete the blob for {Digest}; it will be retried next sweep.", package.Digest);
                }
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
