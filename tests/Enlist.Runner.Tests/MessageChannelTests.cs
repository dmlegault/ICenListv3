using System.Text;

using Enlist.Runner.Protocol;

namespace Enlist.Runner.Tests;

/// <summary>
/// The wire's own tolerance, with no process involved: a line the receiver cannot read as a message
/// is skipped and reported, never thrown — a throw there used to end the receive loop for the whole
/// connection — and a line past the length limit is dropped rather than allocated.
/// </summary>
public sealed class MessageChannelTests
{
    [Fact]
    public async Task A_line_with_a_kind_this_side_does_not_know_is_skipped_and_the_next_message_still_arrives()
    {
        var bytes = Concat(
            Encoding.UTF8.GetBytes("{\"kind\":\"somethingFromTheFuture\",\"payload\":1}\n"),
            await OnTheWireAsync(new ShutdownCommand(5000)));

        await using var channel = new MessageChannel<RunnerMessage, AgentCommand>(new MemoryStream(bytes));
        var skipped = new List<string>();
        channel.MessageSkipped += skipped.Add;

        Assert.IsType<ShutdownCommand>(await channel.ReceiveAsync());
        Assert.Null(await channel.ReceiveAsync());

        var notice = Assert.Single(skipped);
        Assert.Contains("skipped", notice);
        Assert.Contains("somethingFromTheFuture", notice);
    }

    [Fact]
    public async Task A_line_longer_than_the_limit_is_dropped_and_the_next_message_still_arrives()
    {
        var runaway = "{\"kind\":\"log\",\"text\":\"" + new string('x', MessageChannel<RunnerMessage, AgentCommand>.MaxLineChars + 100) + "\"}\n";
        var bytes = Concat(Encoding.UTF8.GetBytes(runaway), await OnTheWireAsync(new ShutdownCommand(5000)));

        await using var channel = new MessageChannel<RunnerMessage, AgentCommand>(new MemoryStream(bytes));
        var skipped = new List<string>();
        channel.MessageSkipped += skipped.Add;

        Assert.IsType<ShutdownCommand>(await channel.ReceiveAsync());
        Assert.Null(await channel.ReceiveAsync());

        Assert.Contains(skipped, s => s.Contains("longer than"));
    }

    [Fact]
    public async Task Messages_arriving_in_one_read_or_split_across_reads_come_out_the_same()
    {
        // Three commands in one buffer, the last one cut mid-line by the stream's chunking — the reader
        // must keep what follows a newline and finish a line that arrives in pieces.
        var bytes = Concat(
            await OnTheWireAsync(new StartServiceCommand("a")),
            await OnTheWireAsync(new StopServiceCommand("b", 1000)),
            await OnTheWireAsync(new ShutdownCommand(5000)));

        await using var channel = new MessageChannel<RunnerMessage, AgentCommand>(new TrickleStream(bytes, chunk: 7));

        Assert.Equal("a", Assert.IsType<StartServiceCommand>(await channel.ReceiveAsync()).Service);
        Assert.Equal("b", Assert.IsType<StopServiceCommand>(await channel.ReceiveAsync()).Service);
        Assert.IsType<ShutdownCommand>(await channel.ReceiveAsync());
        Assert.Null(await channel.ReceiveAsync());
    }

    /// <summary>Exactly the bytes the real sender puts on the wire for one command — produced by the real sender.</summary>
    private static async Task<byte[]> OnTheWireAsync(AgentCommand command)
    {
        var buffer = new MemoryStream();
        await using (var sender = new MessageChannel<AgentCommand, RunnerMessage>(buffer))
        {
            await sender.SendAsync(command);
        }

        return buffer.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new MemoryStream();
        foreach (var part in parts)
        {
            all.Write(part);
        }

        return all.ToArray();
    }

    /// <summary>Hands out at most `chunk` bytes per read, so line boundaries land mid-read. Writable only so the channel's writer can be constructed over it; nothing is written.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunk), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
