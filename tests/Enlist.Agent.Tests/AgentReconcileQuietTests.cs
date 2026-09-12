using Enlist.Agent.Configuration;
using Enlist.Agent.Logging;
using Enlist.ControlPlane.Contracts;
using Enlist.Runner.Protocol;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// What a reconcile pass that carries NO news is allowed to do, which is almost nothing.
///
/// ControlPlaneAssignmentSource has a three-minute refetch floor, so WaitForChangeAsync completes on
/// a timer whether or not anything changed and ReconcileAsync then runs over an identical list. Every
/// pass used to be treated as news: a stopped application re-logged "not starting", a terminally
/// failed one re-logged its failure AND pushed a fresh status snapshot, and - the one that does real
/// damage - an application the agent had already given up on was put through the whole backoff cycle
/// again. Every three minutes. For as long as the agent ran.
///
/// The source here is driven by hand instead of by a timer, so a pass costs milliseconds rather than
/// three minutes and the test can make several of them.
/// </summary>
public sealed class AgentReconcileQuietTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "enlist-quiet-tests-" + Guid.NewGuid().ToString("N"));
    private AgentHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch
        {
        }
    }

    private string LogRoot => Path.Combine(_dataRoot, "Logs");

    /// <summary>Every agent-*.log at once, so a run straddling midnight does not read half its own output.</summary>
    private string AgentLog()
    {
        try
        {
            return string.Join(Environment.NewLine, Directory
                .EnumerateFiles(LogRoot, "agent-*.log", SearchOption.TopDirectoryOnly)
                .SelectMany(File.ReadAllLines));
        }
        catch (DirectoryNotFoundException)
        {
            return "";
        }
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private AgentHost CreateHost(IAssignmentSource source, AgentHostOptions? options = null, ILogForwarder? forwarder = null) =>
        new(source, RepoPaths.RunnerBinDirectory(), Path.Combine(_dataRoot, "Runners"), LogRoot, options, logForwarder: forwarder);

    /// <summary>Pushes a change signal without changing anything, which is exactly what the refetch floor does.</summary>
    private static async Task RefetchAsync(HandDrivenAssignmentSource source, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await source.SignalAsync();
        }
    }

    [Fact]
    public async Task A_stopped_application_is_explained_once_not_once_per_refetch()
    {
        var app = new ApplicationAssignment
        {
            Name = RepoPaths.UniqueAppName(),
            Path = RepoPaths.SampleServiceDir(),
            DesiredState = DesiredState.Stopped,
        };

        var source = new HandDrivenAssignmentSource(new AgentAssignments { Applications = { app } });
        _host = CreateHost(source);
        await _host.StartAsync();

        await RefetchAsync(source, 4);

        Assert.Equal(1, Occurrences(AgentLog(), "desired state is Stopped - not starting"));

        // Proves the four passes above were real passes and not a test signalling into the void, and
        // proves the other half of the rule at the same time: silence is for an UNCHANGED list, and a
        // changed one is still announced.
        source.Push(new AgentAssignments
        {
            Applications = { new ApplicationAssignment { Name = app.Name, Path = app.Path, DesiredState = DesiredState.Stopped, RuntimeFlavor = "dotnet-on-a-toaster" } },
        });

        await WaitUntilAsync(() => Occurrences(AgentLog(), "desired state is Stopped - not starting") == 2);
        Assert.Equal(2, Occurrences(AgentLog(), "desired state is Stopped - not starting"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task A_terminal_configuration_failure_is_said_once_not_once_per_refetch()
    {
        // A runtime flavor this agent has no runner-bin for: a configuration problem with no
        // transient component, so it goes straight to Failed and retrying cannot help. Which is
        // precisely why repeating it every three minutes is noise - nothing about it can change until
        // somebody edits the policy, and when they do, that pass carries news and says so.
        var app = new ApplicationAssignment
        {
            Name = RepoPaths.UniqueAppName(),
            Path = RepoPaths.SampleServiceDir(),
            RuntimeFlavor = "dotnet-on-a-toaster",
        };

        var source = new HandDrivenAssignmentSource(new AgentAssignments { Applications = { app } });
        _host = CreateHost(source);
        await _host.StartAsync();

        await RefetchAsync(source, 4);

        Assert.Equal(1, Occurrences(AgentLog(), "requires runtime flavor 'dotnet-on-a-toaster'"));

        // Same guard as above: a changed assignment still gets an answer, so the four silent passes
        // were silence by decision rather than a source that never signalled.
        source.Push(new AgentAssignments
        {
            Applications = { new ApplicationAssignment { Name = app.Name, Path = app.Path, RuntimeFlavor = "dotnet-on-a-bicycle" } },
        });

        await WaitUntilAsync(() => AgentLog().Contains("dotnet-on-a-bicycle", StringComparison.Ordinal));
        Assert.Contains("requires runtime flavor 'dotnet-on-a-bicycle'", AgentLog());
        Assert.Equal(1, Occurrences(AgentLog(), "requires runtime flavor 'dotnet-on-a-toaster'"));
    }

    [Fact]
    public async Task An_application_the_agent_gave_up_on_stays_given_up_until_something_changes()
    {
        // The same permanently-invalid path AgentStartupFailureTests uses: the runner validates --app
        // itself and exits immediately, so every attempt fails the same way and the backoff runs out.
        var appName = RepoPaths.UniqueAppName();
        var options = new AgentHostOptions
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
            RestartBaseDelay = TimeSpan.FromMilliseconds(50),
            RestartMaxDelay = TimeSpan.FromMilliseconds(100),
            RestartSettledDuration = TimeSpan.FromSeconds(30),
            MaxRestartAttempts = 2,
        };

        var source = new HandDrivenAssignmentSource(new AgentAssignments
        {
            Applications = { new ApplicationAssignment { Name = appName, Path = Path.Combine(_dataRoot, "nowhere-1") } },
        });

        _host = CreateHost(source, options);
        await _host.StartAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !AgentLog().Contains("giving up after", StringComparison.Ordinal))
        {
            await Task.Delay(100);
        }

        Assert.Contains("giving up after", AgentLog());
        var attemptsAtGiveUp = Occurrences(AgentLog(), "retrying in");

        // "Not retrying again automatically" has to survive contact with the refetch timer, or the
        // message is simply untrue: this is the pass that used to restart the entire backoff cycle.
        await RefetchAsync(source, 4);
        await Task.Delay(500);

        Assert.Equal(attemptsAtGiveUp, Occurrences(AgentLog(), "retrying in"));
        Assert.Equal(1, Occurrences(AgentLog(), "giving up after"));

        // A CHANGED assignment is new information - an operator fixing the thing that was broken -
        // and must un-give-up, with the failure count starting from zero rather than resuming a
        // streak that belonged to the old assignment.
        source.Push(new AgentAssignments
        {
            Applications = { new ApplicationAssignment { Name = appName, Path = Path.Combine(_dataRoot, "nowhere-2") } },
        });

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && Occurrences(AgentLog(), "retrying in") <= attemptsAtGiveUp)
        {
            await Task.Delay(100);
        }

        Assert.True(Occurrences(AgentLog(), "retrying in") > attemptsAtGiveUp, "a changed assignment did not restart the attempt.");
    }

    [Fact]
    public async Task A_failing_first_fetch_does_not_stop_the_agent_from_starting()
    {
        // Unguarded, this threw out of StartAsync, the hosted service failed to start, and the SCM
        // restart-looped the whole agent: one unresolvable digest and NOTHING runs on the box, for
        // good. The agent should come up, say so, and pick the applications up on a later pass.
        var source = new HandDrivenAssignmentSource(new AgentAssignments())
        {
            ThrowOnNextFetch = new InvalidOperationException("the control plane returned a package digest that does not resolve"),
        };

        _host = CreateHost(source);
        await _host.StartAsync();

        Assert.Contains("Initial reconciliation failed", AgentLog());
        Assert.Contains("does not resolve", AgentLog());

        // Still live: a later pass reconciles normally.
        var app = new ApplicationAssignment
        {
            Name = RepoPaths.UniqueAppName(),
            Path = RepoPaths.SampleServiceDir(),
            DesiredState = DesiredState.Stopped,
        };

        source.Push(new AgentAssignments { Applications = { app } });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !AgentLog().Contains("desired state is Stopped", StringComparison.Ordinal))
        {
            await Task.Delay(100);
        }

        Assert.Contains("desired state is Stopped", AgentLog());
    }

    [Fact]
    public async Task The_log_forwarder_is_disposed_on_shutdown_so_its_last_lines_are_flushed()
    {
        // Nothing disposed the forwarder, so ControlPlaneLogForwarder's "final best-effort flush on
        // shutdown" never ran and the last flush interval of forwarded lines was dropped on every
        // clean stop - which is exactly where the lines saying the agent stopped live.
        var forwarder = new DisposalRecordingForwarder();
        var source = new HandDrivenAssignmentSource(new AgentAssignments());

        var host = CreateHost(source, forwarder: forwarder);
        await host.StartAsync();
        Assert.False(forwarder.Disposed, "disposed while still running");

        await host.StopAsync();

        Assert.True(forwarder.Disposed, "StopAsync did not dispose the log forwarder.");
    }

    /// <summary>
    /// An assignment source the test drives directly: SignalAsync completes one WaitForChangeAsync
    /// without changing anything (the refetch floor), Push changes the list and signals, and
    /// ThrowOnNextFetch makes the next GetCurrentAsync fail.
    /// </summary>
    private sealed class HandDrivenAssignmentSource : IAssignmentSource
    {
        private readonly SemaphoreSlim _changed = new(0);
        private volatile AgentAssignments _current;

        public HandDrivenAssignmentSource(AgentAssignments initial) => _current = initial;

        public Exception? ThrowOnNextFetch { get; set; }

        public Task<AgentAssignments> GetCurrentAsync(CancellationToken ct)
        {
            if (ThrowOnNextFetch is { } failure)
            {
                ThrowOnNextFetch = null;
                throw failure;
            }

            return Task.FromResult(_current);
        }

        public Task WaitForChangeAsync(CancellationToken ct) => _changed.WaitAsync(ct);

        public void Push(AgentAssignments next)
        {
            _current = next;
            _changed.Release();
        }

        /// <summary>Releases one wait and gives the reconcile it triggers time to finish, so the passes a test asks for are passes that actually happened.</summary>
        public async Task SignalAsync()
        {
            _changed.Release();
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private sealed class DisposalRecordingForwarder : ILogForwarder, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public void Enqueue(string applicationName, LogMessage message)
        {
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
