using Microsoft.EntityFrameworkCore;

namespace Enlist.ControlPlane.Data;

public sealed class ControlPlaneDbContext : DbContext
{
    public ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : base(options)
    {
    }

    public DbSet<ApplicationPolicyEntity> ApplicationPolicies => Set<ApplicationPolicyEntity>();

    public DbSet<AgentReportEntity> AgentReports => Set<AgentReportEntity>();

    public DbSet<PackageEntity> Packages => Set<PackageEntity>();

    public DbSet<AgentEntity> Agents => Set<AgentEntity>();

    public DbSet<AgentLogEntity> AgentLogs => Set<AgentLogEntity>();

    public DbSet<AgentCredentialEntity> AgentCredentials => Set<AgentCredentialEntity>();

    public DbSet<JoinTokenEntity> JoinTokens => Set<JoinTokenEntity>();

    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentCredentialEntity>(entity =>
        {
            // One credential per agent name, and it goes when the agent does: DELETE /api/agents/{name}
            // is a revocation with no second step (Authentication-Design.md section 4.4).
            entity.HasKey(c => c.AgentName);
            entity.HasIndex(c => c.TokenHash).IsUnique();
            entity.HasOne<AgentEntity>().WithOne().HasForeignKey<AgentCredentialEntity>(c => c.AgentName).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JoinTokenEntity>(entity =>
        {
            entity.HasIndex(t => t.TokenHash).IsUnique();
        });

        modelBuilder.Entity<ApiKeyEntity>(entity =>
        {
            entity.HasIndex(k => k.KeyHash).IsUnique();

            // A name is unique among LIVE keys: revoking "ci-main" and creating a new "ci-main" is the
            // ordinary rotation, and the revoked row keeps its name for the audit trail.
            entity.HasIndex(k => k.Name).IsUnique().HasFilter("[RevokedAtUtc] IS NULL");
        });

        // No uniqueness constraint on ApplicationPolicyEntity: with tags-only targeting, two rules
        // "colliding" isn't a fixed (agent, application) key collision anymore — it's two selectors
        // that HAPPEN to both currently match the same agent for the same application, which can
        // change any time a tag changes on either side. That's a runtime resolution question, not a
        // schema constraint — see ResolveEffectivePoliciesForAgentAsync's conflict detection in Program.cs.

        modelBuilder.Entity<AgentReportEntity>(entity =>
        {
            entity.HasIndex(r => new { r.AgentName, r.ReportedAtUtc });
        });

        modelBuilder.Entity<PackageEntity>(entity =>
        {
            entity.HasKey(p => p.Digest);

            // Filtered so the rows with no application (or no number yet) never collide with each
            // other; unique so two concurrent uploads cannot both become v7 — see SaveWithNextVersionAsync.
            entity.HasIndex(p => new { p.ApplicationName, p.VersionNumber })
                .IsUnique()
                .HasFilter("[ApplicationName] IS NOT NULL AND [VersionNumber] IS NOT NULL");
        });

        modelBuilder.Entity<AgentEntity>(entity =>
        {
            entity.HasKey(m => m.Name);
        });

        modelBuilder.Entity<AgentLogEntity>(entity =>
        {
            // The log-tail page's own access pattern: "lines for this agent+application, after the
            // last Id I've already shown" — a compound index covering exactly that filter+order.
            entity.HasIndex(l => new { l.AgentName, l.ApplicationName, l.Id });
        });
    }
}
