using System.Text;
using System.Text.Json;

namespace Enlist.Runner.Legacy.Protocol;

/// <summary>
/// Newline-delimited JSON over a Stream — in this build always a named pipe, since the legacy runner
/// deliberately carries no socket transports (see Program.cs). System.IO.Pipes is BCL on both net10.0
/// and net472. One JSON object per line: System.Text.Json escapes embedded newlines within string
/// values, so a multi-line log message still serializes to exactly one line on the wire.
///
/// TOut/TIn are the polymorphic base types (RunnerMessage/AgentCommand) — generic so the SAME class
/// serves both ends of the pipe: this runner is a MessageChannel&lt;RunnerMessage, AgentCommand&gt;,
/// and Enlist.Agent is the mirror image, MessageChannel&lt;AgentCommand, RunnerMessage&gt;.
/// Wire-identical to the modern Enlist.Runner's own
/// MessageChannel, duplicated here as source for the same zero-shared-assembly reason.
///
/// Reading is bounded and tolerant. A line longer than <see cref="MaxLineChars"/> is dropped rather
/// than allocated (the only thing that size is a runaway log line), and a line that is not a message
/// this side knows — a kind from a peer of another version, or malformed JSON — is skipped rather than
/// thrown, because a throw here ends the receive loop for the whole connection. Both are reported
/// through <see cref="MessageSkipped"/>; neither ends the channel.
/// </summary>
public sealed class MessageChannel<TOut, TIn> : IDisposable
    where TOut : notnull
{
    /// <summary>The longest line either side will read. The largest legitimate message is a log line; a megabyte of one is a bug in the plugin, not something to carry.</summary>
    public const int MaxLineChars = 1024 * 1024;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly char[] _readBuffer = new char[4096];
    private readonly StringBuilder _line = new();
    private int _bufferedStart;
    private int _bufferedEnd;
    private bool _endOfStream;

    /// <summary>Raised for every line skipped, with what was wrong. The channel goes on to the next line; the subscriber decides where the line is written down.</summary>
    public event Action<string>? MessageSkipped;

    public MessageChannel(Stream stream)
    {
        _writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 4096, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };
        _reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
    }

    public async Task SendAsync(TOut message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message, typeof(TOut), JsonOptions.Default);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Returns null at end of stream (the other side closed the pipe) rather than throwing. Skips, and reports, a line it cannot read as a message.</summary>
    public async Task<TIn?> ReceiveAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return default;
            }

            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                var message = (TIn?)JsonSerializer.Deserialize(line, typeof(TIn), JsonOptions.Default);
                if (message is not null)
                {
                    return message;
                }

                MessageSkipped?.Invoke("an empty message (JSON null) was skipped.");
            }
            catch (JsonException ex)
            {
                MessageSkipped?.Invoke($"a line that could not be read as a {typeof(TIn).Name} was skipped - {FirstSentence(ex.Message)}: {Preview(line)}");
            }
        }
    }

    /// <summary>
    /// net472's StreamReader.ReadAsync has no CancellationToken overload, which is the whole of this
    /// method's reason for existing. Without it the token reaching ReceiveAsync was accepted and
    /// ignored: the read blocked until the far end sent something or closed, so RunnerHost's
    /// `catch (OperationCanceledException)` around its receive loop was unreachable and the dev
    /// host's "Forcing exit" could not interrupt a runner that had not yet been told to shut down.
    /// The modern runner threads the token all the way down and behaved correctly; this is the one
    /// place the two genuinely diverged in behaviour rather than syntax.
    ///
    /// The read itself cannot be stopped - nothing on net472 can stop it - so what is cancelled is
    /// the WAIT. The abandoned read may still complete later and write into _readBuffer; that is
    /// harmless because cancellation here only ever happens on the way out, and its exception is
    /// observed below so it cannot surface as an unobserved task fault.
    /// </summary>
    private async Task<int> ReadWithCancellationAsync(CancellationToken ct)
    {
        var read = _reader.ReadAsync(_readBuffer, 0, _readBuffer.Length);
        if (!ct.CanBeCanceled || read.IsCompleted)
        {
            return await read.ConfigureAwait(false);
        }

        var cancelled = new TaskCompletionSource<bool>();
        using (ct.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) != read)
            {
                _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                throw new OperationCanceledException(ct);
            }
        }

        return await read.ConfigureAwait(false);
    }

    /// <summary>
    /// A line, without its newline, or null at end of stream; an empty string for a line that was
    /// dropped for length (already reported). Reads in chunks and keeps what follows a newline for the
    /// next call, so a line arriving in pieces or several lines arriving at once both come out right.
    /// </summary>
    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        _line.Clear();
        var overLimit = false;

        while (true)
        {
            if (_bufferedStart == _bufferedEnd)
            {
                var read = _endOfStream ? 0 : await ReadWithCancellationAsync(ct).ConfigureAwait(false);
                if (read == 0)
                {
                    _endOfStream = true;
                    return _line.Length == 0 && !overLimit ? null : Complete(overLimit);
                }

                _bufferedStart = 0;
                _bufferedEnd = read;
            }

            var newline = Array.IndexOf(_readBuffer, '\n', _bufferedStart, _bufferedEnd - _bufferedStart);
            var chunkEnd = newline < 0 ? _bufferedEnd : newline;

            if (!overLimit)
            {
                if (_line.Length + (chunkEnd - _bufferedStart) > MaxLineChars)
                {
                    // Nothing more of this line is kept; the rest is read and thrown away up to its newline.
                    overLimit = true;
                    _line.Clear();
                }
                else
                {
                    _line.Append(_readBuffer, _bufferedStart, chunkEnd - _bufferedStart);
                }
            }

            _bufferedStart = newline < 0 ? _bufferedEnd : newline + 1;

            if (newline >= 0)
            {
                return Complete(overLimit);
            }
        }
    }

    private string Complete(bool overLimit)
    {
        if (overLimit)
        {
            MessageSkipped?.Invoke($"a line longer than {MaxLineChars:N0} characters was skipped - a message that size is a runaway log line, not anything this channel carries.");
            return "";
        }

        if (_line.Length > 0 && _line[_line.Length - 1] == '\r')
        {
            _line.Length--;
        }

        return _line.ToString();
    }

    private static string FirstSentence(string message)
    {
        var end = message.IndexOf('.');
        return end > 0 ? message.Substring(0, end) : message;
    }

    private static string Preview(string line) => line.Length <= 120 ? line : line.Substring(0, 120) + "...";

    private bool _disposed;

    /// <summary>
    /// Idempotent, and tolerant of the underlying pipe already being gone — a caller that closes the
    /// pipe out from under this channel and then disposes the channel too must not get an exception
    /// for it; Dispose is supposed to be safe to call more than once and safe to call on a
    /// half-torn-down connection. Synchronous (unlike the modern runner's DisposeAsync) — the body
    /// underneath was always purely synchronous disposal of Dispose()-only types, so IAsyncDisposable
    /// bought nothing here beyond stylistic consistency with the rest of that codebase; net472 has no
    /// guaranteed IAsyncDisposable/ValueTask without an extra package, so this sidesteps that entirely.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _writeLock.Dispose();
        TryDispose(_writer);
        TryDispose(_reader);
    }

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
