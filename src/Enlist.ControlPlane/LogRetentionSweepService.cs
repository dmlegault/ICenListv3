using Enlist.ControlPlane.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enlist.ControlPlane;

public sealed class LogRetentionOptions
{
    /// <summary>Forwarded log rows are a live-tailing convenience, not a durable audit trail (the agent's own local files, pruned on their own separate schedule, are that) — so a much shorter window than package retention is fine here.</summary>
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(3);

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>Deletes forwarded log rows older than RetentionPeriod — without this, AgentLogs grows forever since nothing else ever removes a row from it.</summary>
public sealed class LogRetentionSweepService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly LogRetentionOptions _options;
    private readonly ILogger<LogRetentionSweepService> _logger;

    public LogRetentionSweepService(IServiceProvider services, IOptions<LogRetentionOptions> options, ILogger<LogRetentionSweepService> logger)
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
                // A failed sweep costs disk space, not correctness — must not crash the control plane.
                // Said rather than swallowed: a database refusing every sweep is news before the disk fills.
                _logger.LogWarning(ex, "Log retention sweep failed; will retry in {Interval}.", _options.SweepInterval);
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

        var cutoffUtc = DateTimeOffset.UtcNow - _options.RetentionPeriod;
        var deleted = await db.AgentLogs.Where(l => l.TimestampUtc < cutoffUtc).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
        {
            _logger.LogInformation("Log retention: deleted {Count} forwarded log rows older than {Retention}.", deleted, _options.RetentionPeriod);
        }
    }
}
