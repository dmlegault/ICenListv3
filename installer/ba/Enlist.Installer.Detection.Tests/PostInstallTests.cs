using Enlist.Installer.Detection;

namespace Enlist.Installer.Detection.Tests;

/// <summary>
/// The work the MSIs deliberately refuse to do: the schema, the portal's key, the agent's
/// enrollment (Installer-UI-Design section 8).
///
/// Worth testing here rather than by running an installer, because the interesting part is WHICH
/// steps a given plan produces and in what order - a dependency chain that is easy to get subtly
/// wrong and impossible to notice until a portal 401s on a machine somebody else owns.
/// </summary>
public sealed class PostInstallTests
{
    private static InstallPlan Server() => new()
    {
        Type = InstallType.Server,
        DatabaseServer = "sql01",
        DatabaseName = "EnlistControlPlane",
    };

    private static InstallPlan AgentOnly(string joinToken = "enlj_token") => new()
    {
        Type = InstallType.AgentOnly,
        AgentName = "WEB-07",
        AgentControlPlaneUrl = "https://cp.example:5293",
        AgentJoinToken = joinToken,
    };

    private static PostInstallStepKind[] Kinds(InstallPlan plan) =>
        PostInstall.Steps(plan).Select(s => s.Kind).ToArray();

    [Fact]
    public void A_server_install_prepares_the_schema_then_mints_and_stores_the_portal_key()
    {
        // The order is a dependency chain, not a preference: there is nothing to mint a key in until
        // the schema exists, and nothing to store until one is minted.
        Assert.Equal(
            new[] { PostInstallStepKind.ApplySchema, PostInstallStepKind.CreatePortalKey, PostInstallStepKind.StorePortalKey },
            Kinds(Server()));
    }

    [Fact]
    public void A_portal_without_a_control_plane_mints_nothing()
    {
        var splitTier = new InstallPlan { Type = InstallType.Custom, Portal = true };

        // create-api-key writes to the database directly, so the key can only be minted where the
        // database is. A split-tier portal gets its key from an Operator on the control plane host -
        // and trying here would fail against a database this machine cannot reach.
        Assert.Empty(Kinds(splitTier));
    }

    [Fact]
    public void A_control_plane_without_a_portal_still_prepares_the_schema()
    {
        var plan = new InstallPlan { Type = InstallType.Custom, ControlPlane = true };

        Assert.Equal(new[] { PostInstallStepKind.ApplySchema }, Kinds(plan));
    }

    [Fact]
    public void An_agent_with_a_join_token_enrolls()
    {
        Assert.Equal(new[] { PostInstallStepKind.EnrollAgent }, Kinds(AgentOnly()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_agent_without_a_join_token_does_nothing(string token)
    {
        // Not every agent needs one: a control plane running Off issues no credentials, and an
        // operator may prefer to enroll by hand later.
        Assert.Empty(Kinds(AgentOnly(token)));
    }

    [Fact]
    public void Everything_at_once_keeps_the_schema_first_and_the_agent_last()
    {
        var plan = Server();
        plan.Agent = true;
        plan.AgentControlPlaneUrl = "https://cp.example:5293";
        plan.AgentJoinToken = "enlj_token";

        Assert.Equal(
            new[]
            {
                PostInstallStepKind.ApplySchema,
                PostInstallStepKind.CreatePortalKey,
                PostInstallStepKind.StorePortalKey,
                PostInstallStepKind.EnrollAgent,
            },
            Kinds(plan));
    }

    [Fact]
    public void No_step_carries_a_secret_in_its_arguments()
    {
        var plan = Server();
        plan.Agent = true;
        plan.AgentControlPlaneUrl = "https://cp.example:5293";
        plan.AgentJoinToken = "enlj_the_actual_token";
        plan.DatabaseWindowsAuthentication = false;
        plan.DatabaseUser = "sa";
        plan.DatabasePassword = "the_actual_password";

        // THIS IS THE POINT OF THE WHOLE SHAPE. A step gets written to a log and shown in a progress
        // message, so the join token, the SQL password and the connection string are supplied by the
        // runner at the moment of launching and never live in the description of the work.
        foreach (var step in PostInstall.Steps(plan))
        {
            var line = step.Executable + " " + string.Join(" ", step.Arguments) + " " + step.Description;
            Assert.DoesNotContain("enlj_the_actual_token", line, StringComparison.Ordinal);
            Assert.DoesNotContain("the_actual_password", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_steps_that_need_the_database_are_the_ones_that_talk_to_it()
    {
        var plan = Server();
        plan.Agent = true;
        plan.AgentControlPlaneUrl = "https://cp.example:5293";
        plan.AgentJoinToken = "enlj_token";

        var needsDatabase = PostInstall.Steps(plan).Where(s => s.NeedsDatabase).Select(s => s.Kind).ToArray();

        // Storing the key and enrolling the agent do not touch SQL, and handing them a connection
        // string would put a SQL password in the environment of a process with no use for it.
        Assert.Equal(new[] { PostInstallStepKind.ApplySchema, PostInstallStepKind.CreatePortalKey }, needsDatabase);
    }

    [Fact]
    public void Only_enrollment_is_optional()
    {
        var plan = Server();
        plan.Agent = true;
        plan.AgentControlPlaneUrl = "https://cp.example:5293";
        plan.AgentJoinToken = "enlj_token";

        foreach (var step in PostInstall.Steps(plan))
        {
            // A schema that will not apply means the control plane cannot work. Enrollment failing
            // usually just means the control plane is not running yet - which on a single-box install
            // it is not, because every service is created stopped.
            Assert.Equal(step.Kind == PostInstallStepKind.EnrollAgent, step.Optional);
        }
    }

    [Fact]
    public void Each_step_runs_the_executable_the_component_it_belongs_to_installed()
    {
        var plan = Server();
        plan.Agent = true;
        plan.AgentControlPlaneUrl = "https://cp.example:5293";
        plan.AgentJoinToken = "enlj_token";
        plan.ControlPlaneInstallDir = @"D:\cp";
        plan.PortalInstallDir = @"D:\portal";
        plan.AgentInstallDir = @"D:\agent";

        var steps = PostInstall.Steps(plan).ToDictionary(s => s.Kind);

        Assert.Equal(@"D:\cp\Enlist.ControlPlane.exe", steps[PostInstallStepKind.ApplySchema].Executable);
        Assert.Equal(@"D:\cp\Enlist.ControlPlane.exe", steps[PostInstallStepKind.CreatePortalKey].Executable);
        Assert.Equal(@"D:\portal\Enlist.Portal.exe", steps[PostInstallStepKind.StorePortalKey].Executable);
        Assert.Equal(@"D:\agent\enlist-agent.exe", steps[PostInstallStepKind.EnrollAgent].Executable);
    }

    [Fact]
    public void An_unchosen_directory_falls_back_to_where_the_package_actually_installs()
    {
        var plan = Server();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // Matches StandardDirectory/ProgramFiles64Folder in ControlPlane.wxs. If the two ever drift,
        // the bootstrapper launches something that is not there.
        Assert.Equal(Path.Combine(programFiles, "enList", "ControlPlane"), plan.ResolvedControlPlaneDir);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "enList", "Agent"),
            plan.ResolvedAgentDataDir);
    }

    [Fact]
    public void The_portal_key_is_read_out_of_what_create_api_key_actually_prints()
    {
        var output = string.Join(Environment.NewLine,
            "Created API key 'portal' (Operator, expires never).",
            "  key: enlk_abc123DEF456",
            "Store it now; it is not recoverable. Present it as: Authorization: Bearer <key>");

        Assert.Equal("enlk_abc123DEF456", PostInstall.ParseApiKey(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Created API key 'portal'.")]
    [InlineData("  key: enlk_")]
    public void Output_with_no_key_in_it_produces_no_key(string? output)
    {
        // Returning something invented here would turn a failed step into a portal that stores
        // nonsense and 401s later, which is a much worse place to find out.
        Assert.Null(PostInstall.ParseApiKey(output));
    }

    [Fact]
    public void A_key_is_recognised_even_if_the_words_around_it_change()
    {
        // The prefix is the credential format and is not free to change; the sentence is for a person
        // and may reasonably be reworded.
        Assert.Equal("enlk_xyz", PostInstall.ParseApiKey("anything at all enlk_xyz"));
        Assert.Equal("enlk_xyz", PostInstall.ParseApiKey("  key: enlk_xyz  "));
    }
}
