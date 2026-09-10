using System.Text;

namespace Enlist.Runner.Logging;

/// <summary>
/// Turns arbitrary Console.Write/WriteLine calls into complete lines, since the wire protocol frames
/// one log entry per line (see Protocol/MessageChannel.cs). Used both for Console.Out/Error
/// redirection (design doc section 7: "the runner captures stdout/stderr... frames each line") and
/// for the injectable TextWriter parameter plugins may ask for directly.
/// </summary>
public sealed class LineBufferingTextWriter : TextWriter
{
    private readonly Action<string> _onLine;
    private readonly StringBuilder _buffer = new();

    public LineBufferingTextWriter(Action<string> onLine) => _onLine = onLine;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            Flush();
        }
        else if (value != '\r')
        {
            _buffer.Append(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Write(buffer[index + i]);
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var ch in value)
        {
            Write(ch);
        }
    }

    /// <summary>Flushes a partial line as-is. Called at process shutdown so a final unterminated Write isn't lost.</summary>
    public override void Flush()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        var line = _buffer.ToString();
        _buffer.Clear();
        _onLine(line);
    }
}
