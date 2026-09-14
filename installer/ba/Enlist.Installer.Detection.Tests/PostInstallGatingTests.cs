using Enlist.Installer.Detection;

namespace Enlist.Installer.Detection.Tests;

/// <summary>
/// Post-install steps run only for the packages Burn actually executed - not for everything the plan
/// names.
///
/// Found in the database after a live end-to-end run. Adding an agent to a Server box is setup.exe
/// run again with the agent switched on; the plan still names the control plane and the portal, but
/// Burn executes only the Agent package. Computing steps from the plan re-ran all of them, and
/// create-api-key --replace revoked the key the already-running portal held:
///
///     portal  created 13:21:51  revoked 13:21:59   (the key the running portal started with)
///     portal  created 13:22:00  live               (minted when the agent was ADDED)
///
/// The portal reads its key once, at startup, so it went on presenting the revoked one and every call
/// to the control plane came back 401 until somebody restarted it.
/// </summary>
public sealed class PostInstallGatingTests
{
    private static InstallPlan ServerWithAgent() => new()
    {
        Type = InstallType.Server,
        Agent = true,
        DatabaseServer = @".\SQLEXPRESS",
        ControlPlaneCertificate = "AABB",
        PortalCertificate = "CCDD",
        AgentControlPlaneUrl = "https://cp.example:5293",
        AgentJoinToken = "enlj_example",
    };

    private static IEnumerable<PostInstallStepKind> Kinds(IReadOnlyList<PostInstallStep> steps) => steps.Select(s => s.Kind);

    [Fact]
    public void Adding_an_agent_does_not_touch_the_portal_or_its_key()
    {
        // THE defect. Only the Agent package ran, so nothing about the portal may move.
        var steps = PostInstall.Steps(ServerWithAgent(), new[] { PostInstall.Packages.Agent });

        Assert.DoesNotContain(PostInstallStepKind.CreatePortalKey, Kinds(steps));
        Assert.DoesNotContain(PostInstallStepKind.StorePortalKey, Kinds(steps));

        // And the agent still enrolls - which is the whole reason the apply happened.
        Assert.Contains(PostInstallStepKind.EnrollAgent, Kinds(steps));
    }

    [Fact]
    public void Adding_an_agent_does_not_reapply_the_schema_or_regrant_certificates()
    {
        var steps = PostInstall.Steps(ServerWithAgent(), new[] { PostInstall.Packages.Agent });

        // Neither is harmful the way re-keying is, but neither package ran, so neither has anything
        // to configure - and a step list that only contains what this apply actually did is one an
        // operator can read and believe.
        Assert.DoesNotContain(PostInstallStepKind.ApplySchema, Kinds(steps));
        Assert.DoesNotContain(PostInstallStepKind.GrantCertificateAccess, Kinds(steps));
        Assert.Equal(new[] { PostInstallStepKind.EnrollAgent }, Kinds(steps).ToArray());
    }

    [Fact]
    public void A_first_install_runs_everything_exactly_as_before()
    {
        var plan = ServerWithAgent();
        var everything = new[] { PostInstall.Packages.ControlPlane, PostInstall.Packages.Portal, PostInstall.Packages.Agent };

        var gated = Kinds(PostInstall.Steps(plan, everything)).ToArray();
        var ungated = Kinds(PostInstall.Steps(plan)).ToArray();

        // The convenience overload is "a first install of this plan", and the two must agree - the
        // fix narrows a modify without changing a single thing about a fresh install.
        Assert.Equal(ungated, gated);
        Assert.Contains(PostInstallStepKind.CreatePortalKey, gated);
        Assert.Contains(PostInstallStepKind.ApplySchema, gated);
    }

    [Fact]
    public void Upgrading_only_the_control_plane_migrates_without_rotating_the_portal_key()
    {
        var steps = PostInstall.Steps(ServerWithAgent(), new[] { PostInstall.Packages.ControlPlane });

        // A new control plane needs its migrations; the portal, still running untouched, keeps its key.
        Assert.Contains(PostInstallStepKind.ApplySchema, Kinds(steps));
        Assert.DoesNotContain(PostInstallStepKind.CreatePortalKey, Kinds(steps));
    }

    [Fact]
    public void Adding_a_portal_to_an_existing_control_plane_host_still_gets_it_a_key()
    {
        // The portal ran; the control plane did not, but it is INSTALLED - and create-api-key only
        // needs its database, which is there either way. This is the case a naive "gate everything on
        // both packages running" would have broken.
        var steps = PostInstall.Steps(ServerWithAgent(), new[] { PostInstall.Packages.Portal });

        Assert.Contains(PostInstallStepKind.CreatePortalKey, Kinds(steps));
        Assert.Contains(PostInstallStepKind.StorePortalKey, Kinds(steps));
        Assert.DoesNotContain(PostInstallStepKind.ApplySchema, Kinds(steps));
    }

    [Fact]
    public void A_portal_with_no_control_plane_on_the_box_gets_no_key_step_even_when_it_ran()
    {
        // A split tier. Its key comes from an Operator on the control plane host.
        var plan = new InstallPlan { Type = InstallType.Custom, Portal = true, PortalCertificate = "CCDD" };

        var steps = PostInstall.Steps(plan, new[] { PostInstall.Packages.Portal });

        Assert.DoesNotContain(PostInstallStepKind.CreatePortalKey, Kinds(steps));
    }

    [Fact]
    public void An_apply_that_executed_no_enList_package_configures_nothing()
    {
        // A modify that only touched a runtime, say. Nothing ran, so nothing is re-minted.
        Assert.Empty(PostInstall.Steps(ServerWithAgent(), Array.Empty<string>()));
    }

    [Fact]
    public void A_package_the_plan_does_not_install_is_ignored_even_if_named()
    {
        // Belt and braces: an executed ID for a component this plan does not include cannot conjure
        // steps for it.
        var agentOnly = new InstallPlan { Type = InstallType.AgentOnly, AgentControlPlaneUrl = "https://cp", AgentJoinToken = "enlj_x" };

        var steps = PostInstall.Steps(agentOnly, new[] { PostInstall.Packages.Portal, PostInstall.Packages.ControlPlane, PostInstall.Packages.Agent });

        Assert.Equal(new[] { PostInstallStepKind.EnrollAgent }, Kinds(steps).ToArray());
    }

    [Fact]
    public void Package_ids_match_case_insensitively_because_Burn_ids_are_not_ours_to_capitalise()
    {
        var steps = PostInstall.Steps(ServerWithAgent(), new[] { "agent" });
        Assert.Contains(PostInstallStepKind.EnrollAgent, Kinds(steps));
    }

    [Fact]
    public void Null_executed_packages_is_treated_as_nothing_executed_rather_than_everything()
    {
        // The safe reading. "I do not know what ran" must never become "re-key everything".
        Assert.Empty(PostInstall.Steps(ServerWithAgent(), null!));
    }

    [Fact]
    public void The_runner_image_beside_setup_is_copied_into_the_agents_Images_folder_before_enrollment()
    {
        var setup = Path.Combine(Path.GetTempPath(), "enlist-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(setup);
        try
        {
            var archive = Path.Combine(setup, "enlist-runner-3.0.0.tar");
            File.WriteAllText(archive, "stand-in");
            var plan = ServerWithAgent();
            plan.AgentEngine = "wslc";
            plan.AgentImage = "enlist/runner:3.0.0";
            plan.SetupFolder = setup;

            var steps = PostInstall.Steps(plan, new[] { PostInstall.Packages.Agent });
            var stage = Assert.Single(steps, s => s.Kind == PostInstallStepKind.StageRunnerImage);

            Assert.Equal(archive, stage.Executable);
            Assert.Equal([plan.ResolvedAgentImagesDir], stage.Arguments);
            Assert.True(stage.Optional);   // without it every process application still runs
            Assert.True(steps.ToList().IndexOf(stage) < steps.ToList().FindIndex(s => s.Kind == PostInstallStepKind.EnrollAgent),
                "staged before enrollment, so it is in place however soon the service starts");

            // Not when the agent package did not run - adding a portal must not re-copy anything.
            Assert.DoesNotContain(PostInstall.Steps(plan, new[] { PostInstall.Packages.Portal }), s => s.Kind == PostInstallStepKind.StageRunnerImage);

            // And not when there is nothing beside the setup to copy.
            File.Delete(archive);
            Assert.DoesNotContain(PostInstall.Steps(plan, new[] { PostInstall.Packages.Agent }), s => s.Kind == PostInstallStepKind.StageRunnerImage);
        }
        finally
        {
            Directory.Delete(setup, recursive: true);
        }
    }
}
