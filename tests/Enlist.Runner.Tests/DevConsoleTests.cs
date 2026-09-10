using System.Text;

using Enlist.Runner.Dev;

namespace Enlist.Runner.Tests;

/// <summary>
/// The dev host's line editor — the part that lets streaming log output and a half-typed command
/// share one terminal.
///
/// Tested through <see cref="IConsoleSurface"/> rather than a real console, because
/// <c>Console.ReadKey</c> and cursor positioning both require a console buffer no test host has. What
/// is worth pinning here is the editing arithmetic (cursor index, history index, which keys must not
/// fall through to "insert this character") and the redraw contract that output must not eat input —
/// all of which fail silently and only in a developer's hands.
/// </summary>
public sealed class DevConsoleTests
{
    /// <summary>Records what a real terminal would have been told to do, so a test can assert on the redraw rather than on pixels.</summary>
    private sealed class FakeSurface : IConsoleSurface
    {
        private readonly StringBuilder _row = new();

        public int Width => 80;

        public List<string> Lines { get; } = [];

        public int? Column { get; private set; }

        public int ClearCount { get; private set; }

        /// <summary>What is currently painted on the prompt row.</summary>
        public string Row => _row.ToString();

        public void Write(string text) => _row.Append(text);

        public void WriteLine(string text)
        {
            Lines.Add(text);
            _row.Clear();
        }

        public void ClearRow()
        {
            ClearCount++;
            _row.Clear();
        }

        public void SetColumn(int column) => Column = column;
    }

    private static (DevConsole Console, FakeSurface Surface) Create()
    {
        var surface = new FakeSurface();
        return (new DevConsole(surface, degraded: false), surface);
    }

    private static void Type(DevConsole console, string text)
    {
        foreach (var c in text)
        {
            console.TryHandleKey(new ConsoleKeyInfo(c, ConsoleKey.A, false, false, false), out _);
        }
    }

    private static bool Press(DevConsole console, ConsoleKey key, out string? submitted) =>
        console.TryHandleKey(new ConsoleKeyInfo('\0', key, false, false, false), out submitted);

    [Fact]
    public void Typed_characters_accumulate_and_the_cursor_follows()
    {
        var (console, surface) = Create();
        console.BeginPrompt();

        Type(console, "run");

        Assert.Equal("run", console.CurrentInput);
        Assert.Equal(3, console.CursorPosition);
        Assert.Equal(DevConsole.Prompt + "run", surface.Row);
        Assert.Equal(DevConsole.Prompt.Length + 3, surface.Column);
    }

    /// <summary>
    /// The whole point of the class: a log line arriving mid-command must not eat what was typed.
    /// </summary>
    [Fact]
    public void Output_arriving_mid_command_preserves_and_redraws_the_input()
    {
        var (console, surface) = Create();
        console.BeginPrompt();
        Type(console, "run Nightly");

        console.WriteLine("  info [Order Intake Listener] order received #1");

        // The log line was emitted as its own line...
        Assert.Contains("  info [Order Intake Listener] order received #1", surface.Lines);

        // ...and the partially-typed command is still there, repainted, with the cursor back at its end.
        Assert.Equal("run Nightly", console.CurrentInput);
        Assert.Equal(DevConsole.Prompt + "run Nightly", surface.Row);
        Assert.Equal(DevConsole.Prompt.Length + "run Nightly".Length, surface.Column);
    }

    [Fact]
    public void Output_before_any_prompt_is_written_plainly()
    {
        var (console, surface) = Create();

        // The banner prints before the first ReadLine. With no prompt on screen there is nothing to
        // erase, and clearing the row would blank the line just written.
        console.WriteLine("=== enList dev host ===");

        Assert.Equal(0, surface.ClearCount);
        Assert.Equal("=== enList dev host ===", Assert.Single(surface.Lines));
    }

    [Fact]
    public void Backspace_deletes_before_the_cursor_and_stops_at_the_start()
    {
        var (console, _) = Create();
        console.BeginPrompt();
        Type(console, "runx");

        Press(console, ConsoleKey.Backspace, out _);
        Assert.Equal("run", console.CurrentInput);

        Press(console, ConsoleKey.Backspace, out _);
        Press(console, ConsoleKey.Backspace, out _);
        Press(console, ConsoleKey.Backspace, out _);

        // The guard that matters: backspace at column 0 must be a no-op, not an exception and not a
        // fallthrough that inserts the key's own character.
        Press(console, ConsoleKey.Backspace, out _);

        Assert.Equal("", console.CurrentInput);
        Assert.Equal(0, console.CursorPosition);
    }

    [Fact]
    public void Editing_happens_at_the_cursor_not_at_the_end()
    {
        var (console, _) = Create();
        console.BeginPrompt();
        Type(console, "run Job");

        Press(console, ConsoleKey.Home, out _);
        Assert.Equal(0, console.CursorPosition);

        Type(console, "x");
        Assert.Equal("xrun Job", console.CurrentInput);

        Press(console, ConsoleKey.Delete, out _);
        Assert.Equal("xun Job", console.CurrentInput);

        Press(console, ConsoleKey.End, out _);
        Assert.Equal("xun Job".Length, console.CursorPosition);
    }

    [Fact]
    public void Arrow_keys_at_the_edges_do_not_insert_characters()
    {
        var (console, _) = Create();
        console.BeginPrompt();

        // Every arrow key arrives with KeyChar '\0'. If an edge case fell through to the default
        // branch it would append a NUL and quietly corrupt the command about to be sent.
        Press(console, ConsoleKey.LeftArrow, out _);
        Press(console, ConsoleKey.RightArrow, out _);
        Press(console, ConsoleKey.UpArrow, out _);
        Press(console, ConsoleKey.DownArrow, out _);
        Press(console, ConsoleKey.Delete, out _);

        Assert.Equal("", console.CurrentInput);
    }

    [Fact]
    public void Enter_submits_echoes_the_command_and_clears_the_buffer()
    {
        var (console, surface) = Create();
        console.BeginPrompt();
        Type(console, "run Tax Rate Refresh");

        var handled = Press(console, ConsoleKey.Enter, out var submitted);

        Assert.True(handled);
        Assert.Equal("run Tax Rate Refresh", submitted);
        Assert.Equal("", console.CurrentInput);

        // Echoed into the transcript, so what you ran stays visible next to the output it produced.
        Assert.Contains(DevConsole.Prompt + "run Tax Rate Refresh", surface.Lines);
    }

    [Fact]
    public void History_walks_back_through_submitted_commands()
    {
        var (console, _) = Create();

        console.BeginPrompt();
        Type(console, "list");
        Press(console, ConsoleKey.Enter, out _);

        console.BeginPrompt();
        Type(console, "run Legacy Job");
        Press(console, ConsoleKey.Enter, out _);

        console.BeginPrompt();
        Press(console, ConsoleKey.UpArrow, out _);
        Assert.Equal("run Legacy Job", console.CurrentInput);

        Press(console, ConsoleKey.UpArrow, out _);
        Assert.Equal("list", console.CurrentInput);

        // Past the oldest entry it stays put rather than wrapping or emptying.
        Press(console, ConsoleKey.UpArrow, out _);
        Assert.Equal("list", console.CurrentInput);

        Press(console, ConsoleKey.DownArrow, out _);
        Assert.Equal("run Legacy Job", console.CurrentInput);

        // Down past the newest returns to the empty line being composed.
        Press(console, ConsoleKey.DownArrow, out _);
        Assert.Equal("", console.CurrentInput);
        Assert.Equal(0, console.CursorPosition);
    }

    [Fact]
    public void Blank_submissions_are_not_added_to_history()
    {
        var (console, _) = Create();

        console.BeginPrompt();
        Type(console, "list");
        Press(console, ConsoleKey.Enter, out _);

        console.BeginPrompt();
        Press(console, ConsoleKey.Enter, out var blank);
        Assert.Equal("", blank);

        console.BeginPrompt();
        Press(console, ConsoleKey.UpArrow, out _);

        // Pressing Enter on an empty prompt is how people check the connection is alive; it must not
        // push a blank entry that then has to be skipped past.
        Assert.Equal("list", console.CurrentInput);
    }

    [Fact]
    public void Routed_output_goes_to_the_log_window_and_not_this_console()
    {
        var (console, surface) = Create();
        console.BeginPrompt();
        Type(console, "run Nightly");

        var routed = new List<string>();
        console.RouteOutputTo(line => { routed.Add(line); return true; });

        console.WriteLine("  info [Order Intake Listener] order received #1");

        Assert.Equal("  info [Order Intake Listener] order received #1", Assert.Single(routed));
        Assert.Empty(surface.Lines);

        // With output elsewhere there is nothing to erase and repaint, so the typed command is
        // untouched — the point of the split.
        Assert.Equal("run Nightly", console.CurrentInput);
    }

    [Fact]
    public void WriteLocal_stays_here_even_when_output_is_routed()
    {
        var (console, surface) = Create();
        console.RouteOutputTo(_ => true);

        // Otherwise the command window is completely blank, which reads as a host that failed to
        // start rather than one whose output went to the other window.
        console.WriteLocal("Log output is in the separate window.");

        Assert.Equal("Log output is in the separate window.", Assert.Single(surface.Lines));
    }

    [Fact]
    public void Closing_the_log_window_returns_output_here_instead_of_dropping_it()
    {
        var (console, surface) = Create();

        // The sink reports failure once the window is gone. Silently discarding output would be the
        // worst possible outcome for a debugging tool.
        console.RouteOutputTo(_ => false);

        console.WriteLine("  info first line after the window closed");

        Assert.Contains(surface.Lines, l => l.Contains("log window closed"));
        Assert.Contains("  info first line after the window closed", surface.Lines);

        // And it stays local from then on, without repeating the warning.
        surface.Lines.Clear();
        console.WriteLine("  info second line");

        Assert.Equal("  info second line", Assert.Single(surface.Lines));
    }

    [Fact]
    public void A_redirected_console_reports_itself_non_interactive()
    {
        // The degraded path is what keeps `--dev < script.txt` and the scripted sessions working: no
        // cursor to move, no keystrokes to read, so writes must go straight out.
        var surface = new FakeSurface();
        var console = new DevConsole(surface, degraded: true);

        Assert.False(console.IsInteractive);

        console.WriteLine("plain");

        Assert.Equal(0, surface.ClearCount);
        Assert.Equal("plain", Assert.Single(surface.Lines));
    }
}
