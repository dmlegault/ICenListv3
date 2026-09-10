using Enlist.Agent.Scheduling;

namespace Enlist.Agent.Tests;

/// <summary>
/// Pure unit tests, no processes involved — proving JobScheduler's own correctness independent of
/// AgentHost's crash-restart wiring, which relies on Register being safe to call again for the same
/// (app, job) key without leaking a duplicate loop (see AgentHost.StartApplicationAsync's
/// UnregisterAll call and JobScheduler.Register's own Unregister-then-add).
/// </summary>
public sealed class JobSchedulerTests
{
    [Fact]
    public async Task Re_registering_the_same_job_cancels_the_previous_loop_instead_of_running_both()
    {
        var callCount = 0;
        await using var scheduler = new JobScheduler((_, _, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.CompletedTask;
        });

        scheduler.Register("App", "Job", "*/1 * * * * *"); // fires every second
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.True(Volatile.Read(ref callCount) >= 1, "the first registration should have fired at least once before being replaced");

        // Replaced with a cron that will not fire again for a long time. If Register left the OLD
        // "every second" loop running instead of cancelling it, callCount keeps climbing regardless.
        scheduler.Register("App", "Job", "0 0 0 1 1 *"); // once a year
        var countRightAfterReregister = Volatile.Read(ref callCount);

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        Assert.Equal(countRightAfterReregister, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task UnregisterAll_stops_every_job_for_that_application_but_leaves_other_applications_running()
    {
        var counts = new Dictionary<string, int>();
        var sync = new object();

        await using var scheduler = new JobScheduler((app, job, _) =>
        {
            lock (sync)
            {
                counts[$"{app}/{job}"] = counts.GetValueOrDefault($"{app}/{job}") + 1;
            }

            return Task.CompletedTask;
        });

        scheduler.Register("AppA", "Job1", "*/1 * * * * *");
        scheduler.Register("AppB", "Job1", "*/1 * * * * *");
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        scheduler.UnregisterAll("AppA");

        int countAAfterUnregister, countBAfterUnregister;
        lock (sync)
        {
            countAAfterUnregister = counts.GetValueOrDefault("AppA/Job1");
            countBAfterUnregister = counts.GetValueOrDefault("AppB/Job1");
        }

        await Task.Delay(TimeSpan.FromSeconds(2.2));

        int countAFinal, countBFinal;
        lock (sync)
        {
            countAFinal = counts.GetValueOrDefault("AppA/Job1");
            countBFinal = counts.GetValueOrDefault("AppB/Job1");
        }

        Assert.Equal(countAAfterUnregister, countAFinal); // AppA stopped firing
        Assert.True(countBFinal > countBAfterUnregister); // AppB kept going
    }
    [Fact]
    public async Task A_delay_longer_than_one_sleep_slice_is_slept_in_pieces_and_still_fires_exactly_once_on_time()
    {
        var fired = new List<DateTimeOffset>();
        await using var scheduler = new JobScheduler((_, _, _) =>
        {
            lock (fired)
            {
                fired.Add(DateTimeOffset.UtcNow);
            }

            return Task.CompletedTask;
        }, maxSleep: TimeSpan.FromMilliseconds(400));

        // One specific second, three seconds from now: with 400 ms slices the loop wakes several times
        // before it is due, and none of those wakes may count as the firing.
        var due = DateTimeOffset.UtcNow.AddSeconds(3);
        due = new DateTimeOffset(due.Year, due.Month, due.Day, due.Hour, due.Minute, due.Second, TimeSpan.Zero);
        scheduler.Register("App", "Job", $"{due.Second} {due.Minute} {due.Hour} * * *");

        await Task.Delay(TimeSpan.FromSeconds(4.5));

        DateTimeOffset firedAt;
        lock (fired)
        {
            firedAt = Assert.Single(fired);
        }

        Assert.InRange(firedAt, due, due.AddSeconds(1.5));
    }

    [Fact]
    public async Task A_yearly_cron_does_not_fault_its_loop()
    {
        var diagnostics = new List<string>();
        await using var scheduler = new JobScheduler((_, _, _) => Task.CompletedTask, message =>
        {
            lock (diagnostics)
            {
                diagnostics.Add(message);
            }

            return Task.CompletedTask;
        });

        // Further out than the ~49.7 days a single Task.Delay accepts, for most of the year — the loop
        // used to die here, unobserved, and the job never fired again.
        scheduler.Register("App", "Job", "0 0 0 1 1 *");
        await Task.Delay(300);

        lock (diagnostics)
        {
            Assert.Empty(diagnostics);
        }
    }

    [Fact]
    public async Task A_due_callback_that_throws_is_reported_and_the_job_is_tried_again_next_time()
    {
        var calls = 0;
        var diagnostics = new List<string>();
        await using var scheduler = new JobScheduler((_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("deliberate");
            }

            return Task.CompletedTask;
        }, message =>
        {
            lock (diagnostics)
            {
                diagnostics.Add(message);
            }

            return Task.CompletedTask;
        });

        scheduler.Register("App", "Job", "*/1 * * * * *");
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        Assert.True(Volatile.Read(ref calls) >= 2, "the job was not tried again after its callback threw");
        lock (diagnostics)
        {
            Assert.Contains(diagnostics, d => d.Contains("deliberate"));
        }
    }
}
