using Enlist.Installer.Detection;

namespace Enlist.Installer.Detection.Tests;

/// <summary>
/// Which pages appear, when Next is allowed, and what the whole thing becomes on the way to Burn.
///
/// This is the wizard's actual behaviour. What the WPF adds on top is binding and navigation, so
/// testing it here is testing the part that can be wrong in a way nobody notices - a page skipped
/// for a component that IS being installed, a Next allowed with a field empty, or a variable passed
/// blank and quietly overriding a default that mattered.
/// </summary>
public sealed class InstallPlanTests
{
    [Fact]
    public void A_server_install_walks_the_control_plane_database_and_portal_pages_and_not_the_agent()
    {
        var plan = new InstallPlan { Type = InstallType.Server };

        Assert.Equal(
            [WizardPage.InstallType, WizardPage.Prerequisites, WizardPage.ControlPlane, WizardPage.Database, WizardPage.Portal, WizardPage.Ready],
            plan.Pages());
    }

    [Fact]
    public void An_agent_only_install_walks_one_configuration_page()
    {
        var plan = new InstallPlan { Type = InstallType.AgentOnly };

        Assert.Equal(
            [WizardPage.InstallType, WizardPage.Prerequisites, WizardPage.Agent, WizardPage.Ready],
            plan.Pages());
    }

    [Fact]
    public void A_server_install_can_add_a_local_agent_and_then_the_agent_page_appears()
    {
        // Section 3: "Control Plane + Portal, optionally a local Agent". Off unless asked for.
        var plan = new InstallPlan { Type = InstallType.Server, Agent = true };

        Assert.Contains(WizardPage.Agent, plan.Pages());
        Assert.True(plan.InstallsAgent);
    }

    [Fact]
    public void A_custom_install_with_nothing_ticked_cannot_go_forward_and_says_why()
    {
        var plan = new InstallPlan { Type = InstallType.Custom };

        Assert.True(plan.InstallsNothing);
        Assert.Equal("Choose at least one component to install.", plan.WhyNextIsBlocked(WizardPage.InstallType));
    }

    [Fact]
    public void A_custom_install_of_the_portal_alone_walks_only_the_portal_page()
    {
        var plan = new InstallPlan { Type = InstallType.Custom, Portal = true };

        Assert.Equal(
            [WizardPage.InstallType, WizardPage.Prerequisites, WizardPage.Portal, WizardPage.Ready],
            plan.Pages());
        Assert.Null(plan.WhyNextIsBlocked(WizardPage.InstallType));
    }

    [Fact]
    public void The_agent_page_insists_on_a_name_and_a_control_plane_before_Next()
    {
        var plan = new InstallPlan { Type = InstallType.AgentOnly, AgentName = "" };
        Assert.Contains("needs a name", plan.WhyNextIsBlocked(WizardPage.Agent));

        plan.AgentName = "WEB-07";
        Assert.Contains("control plane", plan.WhyNextIsBlocked(WizardPage.Agent));

        plan.AgentControlPlaneUrl = "https://enlist.corp.local:5293";
        Assert.Null(plan.WhyNextIsBlocked(WizardPage.Agent));
    }

    [Fact]
    public void An_engine_without_an_image_is_refused_because_it_would_start_nothing()
    {
        var plan = new InstallPlan
        {
            Type = InstallType.AgentOnly,
            AgentName = "WEB-07",
            AgentControlPlaneUrl = "https://enlist.corp.local:5293",
            AgentEngine = "wslc",
            AgentImage = "",
        };

        Assert.Contains("runner image", plan.WhyNextIsBlocked(WizardPage.Agent));

        plan.AgentImage = "enlist/runner:3.0.0";
        Assert.Null(plan.WhyNextIsBlocked(WizardPage.Agent));
    }

    [Theory]
    [InlineData("LocalSystem")]
    [InlineData(@"NT AUTHORITY\NetworkService")]
    [InlineData(@"NT AUTHORITY\LocalService")]
    [InlineData(@"CORP\enlist-agents$")]
    public void A_built_in_or_managed_service_account_is_never_asked_for_a_password(string account)
    {
        var plan = new InstallPlan
        {
            Type = InstallType.AgentOnly,
            AgentName = "WEB-07",
            AgentControlPlaneUrl = "https://enlist.corp.local:5293",
            AgentAccount = account,
            AgentPassword = "",
        };

        Assert.Null(plan.WhyNextIsBlocked(WizardPage.Agent));
    }

    [Fact]
    public void An_ordinary_account_is_asked_for_one_and_the_message_names_it()
    {
        var plan = new InstallPlan
        {
            Type = InstallType.AgentOnly,
            AgentName = "WEB-07",
            AgentControlPlaneUrl = "https://enlist.corp.local:5293",
            AgentAccount = @"CORP\svc-enlist",
            AgentPassword = "",
        };

        Assert.Contains(@"CORP\svc-enlist", plan.WhyNextIsBlocked(WizardPage.Agent));

        plan.AgentPassword = "hunter2";
        Assert.Null(plan.WhyNextIsBlocked(WizardPage.Agent));
    }

    [Fact]
    public void A_sql_login_needs_a_user_and_windows_authentication_does_not()
    {
        var plan = new InstallPlan
        {
            Type = InstallType.Server,
            DatabaseServer = "sql01.corp.local",
            DatabaseWindowsAuthentication = true,
        };
        Assert.Null(plan.WhyNextIsBlocked(WizardPage.Database));

        plan.DatabaseWindowsAuthentication = false;
        Assert.Contains("SQL login", plan.WhyNextIsBlocked(WizardPage.Database));

        plan.DatabaseUser = "enlist";
        Assert.Null(plan.WhyNextIsBlocked(WizardPage.Database));
    }

    [Fact]
    public void An_agent_only_plan_passes_no_control_plane_or_portal_variables_at_all()
    {
        var plan = new InstallPlan
        {
            Type = InstallType.AgentOnly,
            AgentName = "WEB-07",
            AgentControlPlaneUrl = "https://enlist.corp.local:5293",
        };

        var variables = plan.ToBundleVariables();

        Assert.Equal("AgentOnly", variables["INSTALLTYPE"]);
        Assert.Equal("WEB-07", variables["AGENT_NAME"]);
        Assert.DoesNotContain(variables.Keys, k => k.StartsWith("CP_") || k.StartsWith("PORTAL_") || k.StartsWith("DB_"));
    }

    [Fact]
    public void An_empty_field_is_omitted_rather_than_passed_blank()
    {
        // The failure this prevents: an empty CP_URLS would override the bundle's own
        // https://+:5293 default with nothing, and the service would install and never start.
        var plan = new InstallPlan { Type = InstallType.Server, ControlPlaneUrls = "", DatabaseServer = "sql01" };

        var variables = plan.ToBundleVariables();

        Assert.False(variables.ContainsKey("CP_URLS"));
        Assert.Equal("sql01", variables["DB_SERVER"]);
    }

    [Fact]
    public void The_join_token_never_becomes_a_bundle_variable()
    {
        // Section 8: it is exchanged for the agent's credential BEFORE the service exists, precisely
        // so it never reaches a binPath that any local user can read out of the process list.
        var plan = new InstallPlan
        {
            Type = InstallType.AgentOnly,
            AgentName = "WEB-07",
            AgentControlPlaneUrl = "https://enlist.corp.local:5293",
            AgentJoinToken = "enlj_notasecretanymore",
        };

        var variables = plan.ToBundleVariables();

        Assert.DoesNotContain(variables, kv => kv.Value.Contains("enlj_"));
        Assert.DoesNotContain(variables.Keys, k => k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_image_is_only_passed_when_there_is_an_engine_to_run_it()
    {
        var plan = new InstallPlan
        {
            Type = InstallType.AgentOnly,
            AgentName = "WEB-07",
            AgentControlPlaneUrl = "https://enlist.corp.local:5293",
            AgentImage = "enlist/runner:3.0.0",
        };

        Assert.False(plan.ToBundleVariables().ContainsKey("AGENT_IMAGE"));

        plan.AgentEngine = "wslc";
        Assert.Equal("enlist/runner:3.0.0", plan.ToBundleVariables()["AGENT_IMAGE"]);
    }

    [Fact]
    public void There_is_no_runner_image_note_without_an_agent_and_an_engine()
    {
        var plan = new InstallPlan { Type = InstallType.AgentOnly, AgentImage = "enlist/runner:3.0.0" };
        Assert.Null(plan.RunnerImageNote());   // process isolation only: nothing to load

        plan.AgentEngine = "docker";
        plan.Type = InstallType.Server;
        plan.Agent = false;
        Assert.Null(plan.RunnerImageNote());   // an engine typed on a page for an agent that is not being installed
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("wslc")]
    public void Without_the_download_beside_setup_the_note_says_where_to_put_it_and_that_the_agent_loads_it(string engine)
    {
        using var setup = new TempFolder();
        var plan = new InstallPlan { Type = InstallType.AgentOnly, AgentEngine = engine, AgentImage = "enlist/runner:3.0.0", SetupFolder = setup.Path };

        var note = plan.RunnerImageNote();
        var brief = plan.RunnerImageNote(brief: true);

        foreach (var text in new[] { note, brief })
        {
            Assert.NotNull(text);
            Assert.Contains("enlist-runner-3.0.0.tar", text);
            Assert.Contains(plan.ResolvedAgentImagesDir, text);
            Assert.Contains("the agent loads it into " + engine + " itself", text, StringComparison.OrdinalIgnoreCase);

            // Not "wslc load -i": loading in the operator's own shell is exactly the step that puts a wslc
            // image in the wrong account's store. The agent's Images folder is the instruction now.
            Assert.DoesNotContain("load -i", text);
        }

        Assert.Contains("beside this setup", brief);           // the Agent page can still say "hand them out together"
        Assert.Contains("does not include", note);
        Assert.Null(plan.RunnerImageDownloadBesideSetup());
    }

    [Fact]
    public void With_the_download_beside_setup_the_note_says_it_is_copied_in()
    {
        using var setup = new TempFolder();
        var archive = System.IO.Path.Combine(setup.Path, "enlist-runner-3.0.0.tar");
        File.WriteAllText(archive, "stand-in");
        var plan = new InstallPlan { Type = InstallType.AgentOnly, AgentEngine = "wslc", AgentImage = "enlist/runner:3.0.0", SetupFolder = setup.Path };

        Assert.Equal(archive, plan.RunnerImageDownloadBesideSetup());
        Assert.Contains("found beside this setup", plan.RunnerImageNote(brief: true));
        Assert.Contains("copied to " + plan.ResolvedAgentImagesDir, plan.RunnerImageNote());
    }

    [Fact]
    public void The_download_is_only_looked_for_when_this_plan_will_use_it()
    {
        using var setup = new TempFolder();
        File.WriteAllText(System.IO.Path.Combine(setup.Path, "enlist-runner-3.0.0.tar"), "stand-in");
        var plan = new InstallPlan { Type = InstallType.AgentOnly, AgentEngine = "wslc", AgentImage = "enlist/runner:3.0.0", SetupFolder = setup.Path };
        Assert.NotNull(plan.RunnerImageDownloadBesideSetup());

        plan.AgentEngine = "";
        Assert.Null(plan.RunnerImageDownloadBesideSetup());        // no engine: nothing will run in a container

        plan.AgentEngine = "wslc";
        plan.AgentImage = "enlist/runner:3.1.0";
        Assert.Null(plan.RunnerImageDownloadBesideSetup());        // a different version is not this plan's image

        plan.AgentImage = "enlist/runner:3.0.0";
        plan.SetupFolder = "";
        Assert.Null(plan.RunnerImageDownloadBesideSetup());        // no idea where setup was started from
    }

    [Fact]
    public void An_image_that_is_not_enlists_is_named_without_a_download_file_it_does_not_have()
    {
        var plan = new InstallPlan { Type = InstallType.AgentOnly, AgentEngine = "docker", AgentImage = "registry.corp.local/enlist-runner:pinned" };

        var note = plan.RunnerImageNote();

        Assert.NotNull(note);
        Assert.Contains("registry.corp.local/enlist-runner:pinned", note);
        Assert.Contains("as the account the agent runs as", note);   // the Images folder only takes enlist/runner images
        Assert.DoesNotContain(".tar", note);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "enlist-plan-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Theory]
    [InlineData("enlist/runner:3.0.0", "enlist-runner-3.0.0.tar")]
    [InlineData("enlist/runner:3.1.0-beta", "enlist-runner-3.1.0-beta.tar")]
    [InlineData("enlist/runner:", null)]
    [InlineData("enlist/runner", null)]
    [InlineData("other/runner:3.0.0", null)]
    public void The_download_file_name_follows_the_image_tag_the_way_build_ps1_names_it(string image, string? expected)
    {
        Assert.Equal(expected, InstallPlan.RunnerImageDownloadFor(image));
    }

    [Theory]
    [InlineData("env=prod role=web", 2)]
    [InlineData("env=prod,role=web", 2)]
    [InlineData("  env=prod   ", 1)]
    [InlineData("", 0)]
    [InlineData("nonsense", 0)]
    [InlineData("trailing=", 0)]
    [InlineData("=leading", 0)]
    public void Tags_are_parsed_the_way_the_agent_page_takes_them(string typed, int expected)
    {
        var plan = new InstallPlan { AgentTags = typed };
        Assert.Equal(expected, plan.ParsedTags().Count);
    }

    [Fact]
    public void Tag_keys_are_matched_case_insensitively_the_way_the_control_plane_does()
    {
        var plan = new InstallPlan { AgentTags = "env=prod ENV=staging" };

        // Last one wins, and there is only one key - which is what the control plane would also do.
        var tags = plan.ParsedTags();
        Assert.Single(tags);
        Assert.Equal("staging", tags["env"]);
    }
}
