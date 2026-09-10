using Enlist.ControlPlane.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enlist.ControlPlane;

public sealed class ReportRetentionOptions
{
    /// <summary>
    /// How long a status report is kept once a newer one from the same agent exists. The only reader
    /// is "the latest report for this agent" (the portal and the endpoint feed), so anything older is a
    /// forensic trail, not data the system needs — a day of it answers "what was this agent reporting
    /// last night" without letting the table grow without bound (about 8 MB per agent per day at the
    /// default heartbeat).
    /// </summary>
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(1);

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Deletes status reports older than RetentionPeriod — except the newest report from each agent, which
/// is kept no matter how old it is. Without the exception, an agent silent for longer than the window
/// (switched off, or long gone) would vanish from the portal's view rather than showing its last known
/// state beside an Offline chip, and the endpoint feed would forget it before an operator did.
/// Every insert into AgentReports is a full snapshot (see AgentReportEntity), and nothing else ever
/// removes a row, so this sweep is the only thing standing between the table and the disk.
/// </summary>
public sealed class ReportRetentionSweepService : BackgroundService
{
    // Deleted in slices so the first sweep on a table that has been growing for months is many short
    // transactions rather than one that holds locks and log space for as long as it takes.
    private const int BatchSize = 5000;

    private readonly IServiceProvider _services;
    private readonly ReportRetentionOptions _options;
    private readonly ILogger<ReportRetentionSweepService> _logger;

    public ReportRetentionSweepService(IServiceProvider services, IOptions<ReportRetentionOptions> options, ILogger<ReportRetentionSweepService> logger)
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
                var deleted = await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                if (deleted > 0)
                {
                    _logger.LogInformation("Report retention: deleted {Count} status reports older than {Retention}.", deleted, _options.RetentionPeriod);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep costs disk space, not correctness — must not crash the control plane.
                // Logged rather than swallowed: a database that refuses every sweep for a week is a
                // condition an operator needs to hear about before the disk does.
                _logger.LogWarning(ex, "Report retention sweep failed; will retry in {Interval}.", _options.SweepInterval);
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

    /// <summary>Returns how many rows were deleted.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var cutoffUtc = DateTimeOffset.UtcNow - _options.RetentionPeriod;
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            // "Older than the cutoff, and not the agent's newest" — the newest has no row after it.
            var ids = await db.AgentReports
                .Where(r => r.ReportedAtUtc < cutoffUtc &&
                            db.AgentReports.Any(newer => newer.AgentName == r.AgentName && newer.ReportedAtUtc > r.ReportedAtUtc))
                .OrderBy(r => r.Id)
                .Select(r => r.Id)
                .Take(BatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (ids.Count == 0)
            {
                return total;
            }

            total += await db.AgentReports.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        return total;
    }
}
