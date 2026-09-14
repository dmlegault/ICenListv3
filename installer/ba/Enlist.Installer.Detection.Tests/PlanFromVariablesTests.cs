using Enlist.Installer.Detection;

namespace Enlist.Installer.Detection.Tests;

/// <summary>
/// Rebuilding the plan from the bundle's variables - what a SILENT install works to, since there is
/// no wizard to have assembled one.
///
/// This is the path a fleet actually uses. A wizard install is watched by someone who would notice a
/// wrong answer; a silent install across a hundred machines is not, so the reading has to be right
/// for the same reasons and with the same defaults.
/// </summary>
public sealed class PlanFromVariablesTests
{
    private static InstallPlan From(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        return InstallPlan.FromVariables(name => map.TryGetValue(name, out var value) ? value : null);
    }

    [Fact]
    public void Nothing_set_is_the_documented_default_install()
    {
        var plan = From();

        // The bundle's own default is INSTALLTYPE=Server, so an empty read has to agree with it.
        Assert.Equal(InstallType.Server, plan.Type);
        Assert.True(plan.InstallsControlPlane);
        Assert.True(plan.InstallsPortal);
        Assert.False(plan.InstallsAgent);
    }

    [Theory]
    [InlineData("AgentOnly", InstallType.AgentOnly)]
    [InlineData("agentonly", InstallType.AgentOnly)]
    [InlineData("Custom", InstallType.Custom)]
    [InlineData("Server", InstallType.Server)]
    [InlineData("nonsense", InstallType.Server)]
    public void The_install_type_is_read_case_insensitively_and_falls_back_to_Server(string value, InstallType expected)
    {
        Assert.Equal(expected, From(("INSTALLTYPE", value)).Type);
    }

    [Fact]
    public void The_component_switches_add_rather_than_override()
    {
        var plan = From(("INSTALLTYPE", "Server"), ("InstallAgent", "1"));

        // Section 3's "Server, optionally a local Agent". Reading these any other way would have the
        // bootstrapper working to a different plan from the one the chain just installed.
        Assert.True(plan.InstallsControlPlane);
        Assert.True(plan.InstallsPortal);
        Assert.True(plan.InstallsAgent);
    }

    [Fact]
    public void An_agent_only_install_with_a_join_token_produces_an_enrollment_step()
    {
        var plan = From(
            ("INSTALLTYPE", "AgentOnly"),
            ("AGENT_NAME", "WEB-07"),
            ("AGENT_CPURL", "https://cp.example:5293"),
            ("AGENT_JOINTOKEN", "enlj_silent"));

        // The whole reason AGENT_JOINTOKEN is a bundle variable: without it a silent install could
        // never enroll, and the token would have to go somewhere that persists.
        Assert.Equal(new[] { PostInstallStepKind.EnrollAgent }, PostInstall.Steps(plan).Select(s => s.Kind).ToArray());
        Assert.Equal("enlj_silent", plan.AgentJoinToken);
    }

    [Fact]
    public void Sql_authentication_is_only_chosen_when_it_is_asked_for_by_name()
    {
        Assert.True(From().DatabaseWindowsAuthentication);
        Assert.True(From(("DB_AUTH", "Windows")).DatabaseWindowsAuthentication);
        Assert.False(From(("DB_AUTH", "Sql")).DatabaseWindowsAuthentication);
        Assert.False(From(("DB_AUTH", "sql")).DatabaseWindowsAuthentication);

        // Anything unrecognised stays on Windows authentication, which stores no secret. Guessing the
        // other way would be guessing towards a password.
        Assert.True(From(("DB_AUTH", "whatever")).DatabaseWindowsAuthentication);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_variable_falls_back_rather_than_blanking_a_default(string value)
    {
        // The same defect the MSIs' conditional SetProperty defaults exist to prevent, arriving from
        // the other direction: the bundle hands every variable over whether it was set or not.
        var plan = From(("DB_NAME", value), ("CP_URLS", value), ("AGENT_ACCOUNT", value));

        Assert.Equal("EnlistControlPlane", plan.DatabaseName);
        Assert.Equal("https://+:5293", plan.ControlPlaneUrls);
        Assert.Equal("LocalSystem", plan.AgentAccount);
    }

    [Fact]
    public void Values_are_trimmed_because_a_command_line_is_typed_by_a_person()
    {
        var plan = From(("AGENT_NAME", "  WEB-07  "), ("DB_SERVER", " sql01 "));

        Assert.Equal("WEB-07", plan.AgentName);
        Assert.Equal("sql01", plan.DatabaseServer);
    }

    [Fact]
    public void A_silent_install_knows_where_setup_was_started_from_so_it_can_find_the_runner_image_beside_it()
    {
        // Burn's own variable for the ORIGINAL location - not the package cache it actually runs from,
        // where nothing handed out beside the setup would be.
        var plan = From(("WixBundleOriginalSourceFolder", @"D:\enList 3.0.0\"));

        Assert.Equal(@"D:\enList 3.0.0\", plan.SetupFolder);
    }

    [Fact]
    public void A_null_reader_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => InstallPlan.FromVariables(null!));
    }

    [Fact]
    public void What_the_wizard_sets_is_what_a_silent_install_reads_back()
    {
        // The round trip that keeps the two paths honest: a plan turned into bundle variables and read
        // back has to be the same plan, or a wizard install and a silent one would configure
        // differently from the same answers.
        var original = new InstallPlan
        {
            Type = InstallType.Custom,
            ControlPlane = true,
            Portal = true,
            Agent = true,
            DatabaseServer = "sql01.corp.local",
            DatabaseName = "Enlist",
            AgentName = "WEB-07",
            AgentControlPlaneUrl = "https://cp.example:5293",
            PortalControlPlaneUrl = "https://cp.example:5293",
        };

        var variables = original.ToBundleVariables();
        var rebuilt = InstallPlan.FromVariables(name => variables.TryGetValue(name, out var value) ? value : null);

        Assert.Equal(original.Type, rebuilt.Type);
        Assert.Equal(original.InstallsControlPlane, rebuilt.InstallsControlPlane);
        Assert.Equal(original.InstallsPortal, rebuilt.InstallsPortal);
        Assert.Equal(original.InstallsAgent, rebuilt.InstallsAgent);
        Assert.Equal(original.DatabaseServer, rebuilt.DatabaseServer);
        Assert.Equal(original.DatabaseName, rebuilt.DatabaseName);
        Assert.Equal(original.AgentName, rebuilt.AgentName);
        Assert.Equal(original.AgentControlPlaneUrl, rebuilt.AgentControlPlaneUrl);
        Assert.Equal(original.PortalControlPlaneUrl, rebuilt.PortalControlPlaneUrl);
    }

    [Fact]
    public void The_join_token_does_not_survive_the_round_trip_because_it_is_never_an_msi_property()
    {
        var original = new InstallPlan { Type = InstallType.AgentOnly, AgentJoinToken = "enlj_secret" };

        // ToBundleVariables is what reaches the PACKAGES, and the token must not be among them: an MSI
        // property becomes a service argument, and `sc qc` shows those to anyone. The bootstrapper
        // reads the token straight off the bundle variable instead, which nothing passes on.
        Assert.DoesNotContain("AGENT_JOINTOKEN", original.ToBundleVariables().Keys);
    }
}
