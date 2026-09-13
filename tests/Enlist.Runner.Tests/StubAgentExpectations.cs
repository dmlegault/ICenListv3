using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// Waiting for what a runner says next.
///
/// Every test in this project drives a REAL enlist-runner over a real channel, so every assertion
/// about its behaviour is an assertion about a message that has or has not arrived. Eight test
/// classes had grown their own copy of that wait, and the copies had quietly diverged into two
/// DIFFERENT contracts wearing the same name `Receive&lt;T&gt;`: some asserted the very next message
/// was a T, others skipped over anything that was not. Reading a test told you which one it meant
/// only if you scrolled to the bottom of the file.
///
/// They are one implementation now, under names that say which is which. Choosing between them is a
/// real decision: <see cref="NextAsync"/> also asserts that nothing came BEFORE the message being
/// waited for, which is the whole point in an ordering test and an unwanted flake everywhere else.
///
/// The timeout stays the caller's, passed in rather than defaulted here. It is the per-class `Step`,
/// and it varies for good reason - a legacy net472 runner takes longer to start than a net10 one.
/// </summary>
internal static class StubAgentExpectations
{
    /// <summary>
    /// The NEXT message must be a <typeparamref name="T"/>. Use this when the ORDER is part of what
    /// the test asserts; anything arriving first fails it.
    /// </summary>
    public static async Task<T> NextAsync<T>(this StubAgent agent, TimeSpan timeout) where T : RunnerMessage
    {
        using var cts = new CancellationTokenSource(timeout);
        var message = await agent.Channel.ReceiveAsync(cts.Token);
        return Assert.IsType<T>(message);
    }

    /// <summary>
    /// The next <typeparamref name="T"/>, skipping anything else on the way. Use this when the test
    /// is about one message and the runner may legitimately say other things around it - a log line
    /// while a service starts, say.
    /// </summary>
    public static Task<T> NextOfTypeAsync<T>(this StubAgent agent, TimeSpan timeout) where T : RunnerMessage =>
        agent.NextMatchingAsync<T>(timeout, _ => true);

    /// <summary>
    /// The next <typeparamref name="T"/> the predicate accepts. Same skipping as
    /// <see cref="NextOfTypeAsync"/>, for when the type alone does not identify the message - several
    /// StateChangedMessages arrive for one service, and only one of them is the state being waited for.
    /// </summary>
    public static async Task<T> NextMatchingAsync<T>(this StubAgent agent, TimeSpan timeout, Func<T, bool> predicate) where T : RunnerMessage
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var message = await agent.Channel.ReceiveAsync(cts.Token)
                ?? throw new InvalidOperationException($"The channel closed before a matching {typeof(T).Name} arrived.");

            if (message is T match && predicate(match))
            {
                return match;
            }
        }
    }

    /// <summary>
    /// Wait for the runner process to exit, reading and discarding whatever it says on the way out.
    ///
    /// The draining is not optional. A runner that is shutting down may still have messages queued,
    /// and if nothing reads them it blocks on a full channel instead of exiting - so a test that
    /// waited for exit WITHOUT draining would time out against a runner that was behaving perfectly.
    /// The drain task is left to finish on its own (bounded, not awaited to completion): the read it
    /// is sitting in throws when the pipe goes away, which is the expected end of it, and the exit
    /// result is already known by then.
    /// </summary>
    public static async Task<bool> DrainUntilExitAsync(this StubAgent agent, TimeSpan timeout)
    {
        var drain = Task.Run(async () =>
        {
            try
            {
                while (await agent.Channel.ReceiveAsync(CancellationToken.None).ConfigureAwait(false) is not null)
                {
                }
            }
            catch
            {
                // The pipe going away as the runner exits is the expected end of this loop.
            }
        });

        var exited = await agent.WaitForExitAsync(timeout).ConfigureAwait(false);
        await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        return exited;
    }
}
