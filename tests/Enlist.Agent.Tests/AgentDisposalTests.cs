using Enlist.Agent.Configuration;
using Enlist.Agent.Hosting;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// Shutting down twice, and what the agent leaves behind when it does.
///
/// All of this is invisible while everything goes right, which is why none of it had a test. Nothing
/// here changes what the agent DOES; it changes what happens on the second pass through a teardown
/// path, and what accumulates on a process that runs for months rather than for the length of a test.
/// </summary>
public sealed class AgentDisposalTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-disposal-tests-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Stopping_twice_and_then_disposing_is_safe()
    {
        // `await using var host = ...` after an explicit StopAsync is the ordinary shape here and in
        // Program.cs, so the second pass is not an edge case - it is the common one. It used to cancel
        // an already-disposed token source and close the job object handle a second time. A double
        // CloseHandle is the one worth naming: Windows recycles handle values, so closing a stale one
        // can close whatever now holds that number.
        var host = CreateHost();
        await host.StartAsync();

        await host.StopAsync();
        await host.StopAsync();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_without_ever_starting_is_safe()
    {
        var host = CreateHost();

        await host.DisposeAsync();
        await host.StopAsync();
    }

    [SkippableFact]
    public void A_job_object_closes_its_handle_once_however_many_times_it_is_disposed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Job Objects are a Windows facility.");

        var job = new JobObject();

        job.Dispose();
        job.Dispose();
        job.Dispose();
    }

    [Fact]
    public async Task A_retry_that_finishes_immediately_does_not_stay_on_the_pending_list()
    {
        // The leak: the tracking handle used to be assigned AFTER the work started, so a body that
        // completed synchronously removed nothing and was then added and left there. It is not
        // hypothetical - ScheduleRetryOrGiveUpAsync returns early for any application that has been
        // deassigned, which is exactly what happens here, and the crash path calls it on every
        // unexpected exit.
        var host = CreateHost();

        // Driven straight at the mechanism rather than through a staged crash, because the case that
        // breaks is a body which completes WITHOUT EVER YIELDING, and that is difficult to provoke
        // reliably from the outside and trivial to state here. Task.CompletedTask is precisely the
        // shape of ScheduleRetryOrGiveUpAsync's early returns.
        for (var i = 0; i < 25; i++)
        {
            host.FireAndForget(() => Task.CompletedTask);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && host.PendingRetryCount > 0)
        {
            await Task.Delay(50);
        }

        Assert.Equal(0, host.PendingRetryCount);

        // And one that genuinely yields still tracks and clears, so the fix did not simply stop
        // recording anything.
        var gate = new TaskCompletionSource();
        host.FireAndForget(() => gate.Task);
        Assert.Equal(1, host.PendingRetryCount);

        gate.SetResult();

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && host.PendingRetryCount > 0)
        {
            await Task.Delay(50);
        }

        Assert.Equal(0, host.PendingRetryCount);

        await host.StopAsync();
    }

    private AgentHost CreateHost() =>
        new(new AgentAssignments(), RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), Path.Combine(_dataRoot, "Logs"));
}
