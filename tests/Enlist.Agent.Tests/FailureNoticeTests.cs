using Enlist.Agent.Logging;

namespace Enlist.Agent.Tests;

/// <summary>Pure: what a stream of best-effort failures becomes on the agent log — a first line, quiet while it lasts, a line when it recovers.</summary>
public sealed class FailureNoticeTests
{
    [Fact]
    public void The_first_failure_is_said_at_once_and_the_next_ones_inside_the_window_are_not()
    {
        var notice = new FailureNotice("Status report to the control plane", TimeSpan.FromMinutes(5));

        var first = notice.Failed("connection refused");
        Assert.NotNull(first);
        Assert.Contains("Status report to the control plane failed: connection refused", first);

        Assert.Null(notice.Failed("connection refused"));
        Assert.Null(notice.Failed("connection refused"));
    }

    [Fact]
    public void A_failure_after_the_window_is_said_again_with_the_count_so_far()
    {
        var notice = new FailureNotice("Status report to the control plane", TimeSpan.Zero);

        notice.Failed("connection refused");
        var later = notice.Failed("connection refused");

        Assert.NotNull(later);
        Assert.Contains("still failing (2 in a row)", later);
    }

    [Fact]
    public void A_success_after_failures_is_said_once_with_the_count_and_a_success_after_nothing_is_not()
    {
        var notice = new FailureNotice("Status report to the control plane", TimeSpan.FromMinutes(5));
        Assert.Null(notice.Succeeded());

        notice.Failed("500 Internal Server Error");
        notice.Failed("500 Internal Server Error");

        Assert.Equal("Status report to the control plane succeeded again after 2 failure(s).", notice.Succeeded());
        Assert.Null(notice.Succeeded());
    }
}
