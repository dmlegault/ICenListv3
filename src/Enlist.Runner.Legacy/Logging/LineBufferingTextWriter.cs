using System.Text;

namespace Enlist.Runner.Legacy.Logging;

/// <summary>
/// Turns arbitrary Console.Write/WriteLine calls into complete lines, since the wire protocol frames
/// one log entry per line (see Protocol/MessageChannel.cs). Used both for Console.Out/Error
/// redirection (design doc section 7) and for the injectable TextWriter parameter plugins may ask for
/// directly. Identical to the modern Enlist.Runner's own LineBufferingTextWriter.
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
        // Explicit null check rather than relying on string.IsNullOrEmpty's null-flow annotation —
        // net472's mscorlib predates [NotNullWhen], so the compiler can't prove `value` is non-null
        // past an IsNullOrEmpty guard the way it can on the modern runner's BCL.
        if (value is null || value.Length == 0)
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
