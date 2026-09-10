using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Tests;

/// <summary>The contract type on its own, no server: AgreesWith is what resolution and the agent's reconcile compare with, and WithMode is what an edit that only restates the mode goes through.</summary>
public sealed class IsolationSpecTests
{
    private static readonly IsolationSpec Full = new(
        IsolationModes.Container,
        Image: "enlist/runner:pinned",
        Ports: [new PortMapping(8080, null, "tcp", "http")],
        Networks: ["backend"],
        Env: new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Staging" });

    [Fact]
    public void Restating_container_mode_keeps_every_container_detail()
    {
        var restated = Full.WithMode(IsolationModes.Container);

        Assert.True(restated.AgreesWith(Full));
        Assert.Same(Full.Ports, restated.Ports);
    }

    [Fact]
    public void Switching_to_process_drops_the_container_details()
    {
        var asProcess = Full.WithMode(IsolationModes.Process);

        Assert.True(asProcess.AgreesWith(IsolationSpec.ProcessDefault));
        Assert.Null(asProcess.Ports);
    }

    [Fact]
    public void A_process_rule_restated_as_container_starts_with_no_container_details()
    {
        var asContainer = IsolationSpec.ProcessDefault.WithMode(IsolationModes.Container);

        Assert.True(asContainer.IsContainer);
        Assert.True(asContainer.AgreesWith(new IsolationSpec(IsolationModes.Container)));
    }

    [Fact]
    public void AgreesWith_compares_ports_by_value_where_record_equality_compares_the_list_instance()
    {
        var a = new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, 9000)]);
        var b = new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, 9000)]);

        Assert.True(a.AgreesWith(b));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AgreesWith_sees_a_changed_host_port()
    {
        var a = new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, 9000)]);
        var b = new IsolationSpec(IsolationModes.Container, Ports: [new PortMapping(8080, 9001)]);

        Assert.False(a.AgreesWith(b));
    }
}
