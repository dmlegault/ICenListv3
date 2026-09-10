using System.Text;

namespace Enlist.Runner.Dev;

/// <summary>
/// The few console operations the line editor needs, behind an interface purely so the editing
/// semantics can be unit-tested without a real terminal — `Console.ReadKey` and cursor positioning
/// both require a console buffer that no test host has.
/// </summary>
internal interface IConsoleSurface
{
    int Width { get; }

    void Write(string text);

    void WriteLine(string text);

    /// <summary>Blank the current row and return the cursor to its first column.</summary>
    void ClearRow();

    void SetColumn(int column);
}

/// <summary>
/// A console that lets streaming output and a half-typed command coexist.
///
/// The problem this solves: the dev host prints log lines from a background receive loop while you
/// are typing a command on the same terminal. With plain Console.WriteLine those writes land wherever
/// the cursor happens to be — in the middle of your input — so a busy application makes the prompt
/// effectively unusable.
///
/// The fix is the standard one: every write and every keystroke goes through one lock. Before output
/// is printed the input line is erased; afterwards the prompt is redrawn with your partial input and
/// the cursor is put back where it was. Your line is never lost and never interleaved — it just keeps
/// moving down the screen ahead of the output.
///
/// Cursor movement goes through <see cref="IConsoleSurface"/> using column positioning rather than
/// ANSI escapes, because this class is mirrored into the net472 runner where VT processing is not
/// guaranteed to be enabled.
///
/// Degrades on purpose. When stdin or stdout is redirected there is no cursor to move and no
/// keystrokes to read, so this falls straight through to Console.ReadLine and plain writes — which is
/// what keeps `enlist-runner --dev &lt; script.txt` and the scripted test sessions working. Any
/// unexpected console failure (a host with no real console buffer, e.g. some IDE output windows)
/// latches the same fallback rather than throwing on every subsequent line.
/// </summary>
internal sealed class DevConsole
{
    internal const string Prompt = "> ";

    private readonly IConsoleSurface _surface;
    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _history = [];

    private int _cursor;
    private int _historyIndex;
    private bool _promptVisible;

    /// <summary>When set, output goes to a separate console window instead of this one — see RouteOutputTo.</summary>
    private Func<string, bool>? _sink;

    /// <summary>Latched on the first console operation that fails, so a terminal that cannot do cursor work degrades once rather than throwing forever.</summary>
    private bool _degraded;

    public DevConsole(TextWriter output)
        : this(new SystemConsoleSurface(output), Console.IsInputRedirected || Console.IsOutputRedirected)
    {
    }

    internal DevConsole(IConsoleSurface surface, bool degraded)
    {
        _surface = surface;
        _degraded = degraded;
    }

    /// <summary>True when the line editor is active. False under redirection — see the class remarks.</summary>
    public bool IsInteractive => !_degraded;

    internal string CurrentInput => _buffer.ToString();

    internal int CursorPosition => _cursor;

    public void WriteLine() => WriteLine(string.Empty);

    /// <summary>
    /// Sends all subsequent output to a separate window instead of this console (see LogWindow). The
    /// prompt and the echo of submitted commands stay here, which is the entire point: with output
    /// elsewhere, this console is only ever a command line.
    /// </summary>
    public void RouteOutputTo(Func<string, bool> sink)
    {
        lock (_gate)
        {
            _sink = sink;
        }
    }

    /// <summary>
    /// Writes to THIS console even when output is routed to a log window — for the few lines that
    /// belong where the person is typing: which application is running, where its output went, and
    /// that the session ended. Without this the command window is completely blank, which reads as a
    /// host that failed to start.
    /// </summary>
    public void WriteLocal(string text)
    {
        lock (_gate)
        {
            if (_degraded || !_promptVisible)
            {
                _surface.WriteLine(text);
                return;
            }

            try
            {
                _surface.ClearRow();
                _surface.WriteLine(text);
                DrawInputLine();
            }
            catch (Exception)
            {
                _degraded = true;
                _surface.WriteLine(text);
            }
        }
    }

    public void WriteLine(string text)
    {
        lock (_gate)
        {
            if (_sink is not null)
            {
                if (_sink(text))
                {
                    return;
                }

                // The window was closed by hand, or its process died. Dropping output silently would
                // be the worst outcome for a debugging tool, so this reverts to printing locally and
                // says so once.
                _sink = null;
                _surface.WriteLine("  warn log window closed - output returns to this console.");
            }

            if (_degraded || !_promptVisible)
            {
                _surface.WriteLine(text);
                return;
            }

            try
            {
                _surface.ClearRow();
                _surface.WriteLine(text);
                DrawInputLine();
            }
            catch (Exception)
            {
                _degraded = true;
                _surface.WriteLine(text);
            }
        }
    }

    /// <summary>
    /// Blocking. Returns null at end of input, matching Console.ReadLine, so the caller's existing
    /// "stdin closed" handling is unchanged.
    /// </summary>
    public string? ReadLine()
    {
        if (_degraded)
        {
            return Console.ReadLine();
        }

        lock (_gate)
        {
            BeginPrompt();

            try
            {
                DrawInputLine();
            }
            catch (Exception)
            {
                _degraded = true;
                _promptVisible = false;
                return Console.ReadLine();
            }
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                // OUTSIDE the lock, deliberately: this blocks until a keystroke, and the output thread
                // must be able to take the lock and redraw meanwhile. Holding it here would freeze all
                // output until the developer happened to type something.
                key = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                lock (_gate)
                {
                    _degraded = true;
                    _promptVisible = false;
                }

                return Console.ReadLine();
            }

            lock (_gate)
            {
                try
                {
                    if (TryHandleKey(key, out var submitted))
                    {
                        return submitted;
                    }
                }
                catch (Exception)
                {
                    _degraded = true;
                    _promptVisible = false;
                    return Console.ReadLine();
                }
            }
        }
    }

    internal void BeginPrompt()
    {
        _historyIndex = _history.Count;
        _buffer.Clear();
        _cursor = 0;
        _promptVisible = true;
    }

    /// <summary>Returns true when a line was submitted; <paramref name="submitted"/> then carries it.</summary>
    internal bool TryHandleKey(ConsoleKeyInfo key, out string? submitted)
    {
        submitted = null;

        switch (key.Key)
        {
            case ConsoleKey.Enter:
            {
                var line = _buffer.ToString();

                // The submitted command is echoed as an ordinary output line, so the transcript keeps
                // showing what was run, in order, next to the log lines it produced.
                _surface.ClearRow();
                _surface.WriteLine(Prompt + line);
                _promptVisible = false;

                if (!string.IsNullOrWhiteSpace(line))
                {
                    _history.Add(line);
                }

                _buffer.Clear();
                _cursor = 0;
                submitted = line;
                return true;
            }

            case ConsoleKey.Backspace when _cursor > 0:
                _buffer.Remove(_cursor - 1, 1);
                _cursor--;
                DrawInputLine();
                return false;

            case ConsoleKey.Delete when _cursor < _buffer.Length:
                _buffer.Remove(_cursor, 1);
                DrawInputLine();
                return false;

            case ConsoleKey.LeftArrow when _cursor > 0:
                _cursor--;
                DrawInputLine();
                return false;

            case ConsoleKey.RightArrow when _cursor < _buffer.Length:
                _cursor++;
                DrawInputLine();
                return false;

            case ConsoleKey.Home:
                _cursor = 0;
                DrawInputLine();
                return false;

            case ConsoleKey.End:
                _cursor = _buffer.Length;
                DrawInputLine();
                return false;

            case ConsoleKey.UpArrow when _historyIndex > 0:
                _historyIndex--;
                ReplaceBuffer(_history[_historyIndex]);
                return false;

            case ConsoleKey.DownArrow when _historyIndex < _history.Count:
                _historyIndex++;
                ReplaceBuffer(_historyIndex == _history.Count ? string.Empty : _history[_historyIndex]);
                return false;

            case ConsoleKey.Escape:
                ReplaceBuffer(string.Empty);
                return false;

            // Backspace at column 0, Delete at end, arrows at either edge: no-ops that must NOT fall
            // through to the default branch, or the key's char would be inserted into the buffer.
            case ConsoleKey.Backspace:
            case ConsoleKey.Delete:
            case ConsoleKey.LeftArrow:
            case ConsoleKey.RightArrow:
            case ConsoleKey.UpArrow:
            case ConsoleKey.DownArrow:
                return false;

            default:
                // Control characters are dropped rather than inserted — a stray Tab or Ctrl+key would
                // otherwise corrupt the line and the redraw width calculation.
                if (!char.IsControl(key.KeyChar))
                {
                    _buffer.Insert(_cursor, key.KeyChar);
                    _cursor++;
                    DrawInputLine();
                }

                return false;
        }
    }

    private void ReplaceBuffer(string text)
    {
        _buffer.Clear();
        _buffer.Append(text);
        _cursor = _buffer.Length;
        DrawInputLine();
    }

    /// <summary>
    /// Repaints the prompt row. Only ever one row: an input line long enough to wrap would need its
    /// row count tracked to erase fully, and a command in this shell is a verb plus a name.
    /// </summary>
    private void DrawInputLine()
    {
        _surface.ClearRow();
        _surface.Write(Prompt);
        _surface.Write(_buffer.ToString());

        // Clamped so a command approaching the terminal width cannot throw on the cursor set — the
        // editing would be wrong past the edge, but the host must not die over a long line.
        var column = Math.Min(Prompt.Length + _cursor, Math.Max(0, _surface.Width - 1));
        _surface.SetColumn(column);
    }

    private sealed class SystemConsoleSurface(TextWriter output) : IConsoleSurface
    {
        public int Width
        {
            get
            {
                // WindowWidth throws when there is no real console; DevConsole latches _degraded on
                // that. The floor guards the zero width some hosts report mid-resize.
                var width = Console.WindowWidth;
                return width < 2 ? 80 : width;
            }
        }

        public void Write(string text)
        {
            output.Write(text);
            output.Flush();
        }

        public void WriteLine(string text) => output.WriteLine(text);

        public void ClearRow()
        {
            Console.SetCursorPosition(0, Console.CursorTop);
            output.Write(new string(' ', Width - 1));
            output.Flush();
            Console.SetCursorPosition(0, Console.CursorTop);
        }

        public void SetColumn(int column) => Console.CursorLeft = column;
    }
}
