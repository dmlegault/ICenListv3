using System.Text;
using System.Text.Json;

namespace Enlist.Runner.Protocol;

/// <summary>
/// Newline-delimited JSON over a Stream (in practice a NamedPipeClientStream/NamedPipeServerStream —
/// System.IO.Pipes is BCL, so this stays within the zero-PackageReference rule). One JSON object per
/// line: System.Text.Json escapes embedded newlines within string values, so a multi-line log message
/// still serializes to exactly one line on the wire.
///
/// TOut/TIn are the polymorphic base types (RunnerMessage/AgentCommand) — generic so the SAME class
/// serves both ends of the pipe: the runner is a MessageChannel&lt;RunnerMessage, AgentCommand&gt;,
/// and a test harness or the future agent is the mirror image, MessageChannel&lt;AgentCommand,
/// RunnerMessage&gt;, all without either side referencing the other's project.
///
/// Reading is bounded and tolerant. A line longer than <see cref="MaxLineChars"/> is dropped rather
/// than allocated (the only thing that size is a runaway log line), and a line that is not a message
/// this side knows — a kind from a peer of another version, or malformed JSON — is skipped rather than
/// thrown, because a throw here ends the receive loop for the whole connection. Both are reported
/// through <see cref="MessageSkipped"/>; neither ends the channel.
/// </summary>
public sealed class MessageChannel<TOut, TIn> : IAsyncDisposable
    where TOut : notnull
{
    /// <summary>The longest line either side will read. The largest legitimate message is a log line; a megabyte of one is a bug in the plugin, not something to carry.</summary>
    public const int MaxLineChars = 1024 * 1024;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _stream;
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
        _stream = stream;
        _writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 4096, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };
        _reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
    }

    public async Task SendAsync(TOut message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message, typeof(TOut), JsonOptions.Default);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
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

                MessageSkipped?.Invoke($"an empty message (JSON null) was skipped.");
            }
            catch (JsonException ex)
            {
                MessageSkipped?.Invoke($"a line that could not be read as a {typeof(TIn).Name} was skipped - {FirstSentence(ex.Message)}: {Preview(line)}");
            }
        }
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
                var read = _endOfStream ? 0 : await _reader.ReadAsync(_readBuffer.AsMemory(), ct).ConfigureAwait(false);
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
        return end > 0 ? message[..end] : message;
    }

    private static string Preview(string line) => line.Length <= 120 ? line : line[..120] + "...";

    private bool _disposed;

    /// <summary>
    /// Idempotent, and tolerant of the underlying pipe already being gone: StreamWriter.Dispose()
    /// flushes before it closes, and that flush touches the (possibly already-closed) stream even
    /// though leaveOpen only promised not to close it ourselves. A caller that closes the pipe out
    /// from under this channel — see StubAgent.ClosePipeAsync, simulating an agent that vanished —
    /// and then disposes the channel too must not get an exception for it; Dispose is supposed to be
    /// safe to call more than once and safe to call on a half-torn-down connection.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        _writeLock.Dispose();
        TryDispose(_writer);
        TryDispose(_reader);
        return ValueTask.CompletedTask;
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
