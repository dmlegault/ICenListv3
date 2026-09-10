using System.Diagnostics;
using System.IO.Pipes;

namespace Enlist.Runner.Legacy.Dev;

/// <summary>
/// `--dev --log-window` for net472 — the mirror of Enlist.Runner/Dev/LogWindow.cs.
///
/// A process owns exactly one console, so a second window necessarily means a second PROCESS. This
/// starts the same enlist-runner executable in a small sink mode (`--log-sink &lt;pipe&gt;`), spawned with
/// UseShellExecute so Windows gives it its own window, then relays every output line to it over a
/// named pipe. The dev host keeps only the prompt and the echo of what you typed.
///
/// KEEP IN STEP with the modern LogWindow. The net472 differences here are forced, not stylistic:
/// ProcessStartInfo has no ArgumentList on this framework (so the command line is quoted by hand),
/// and Environment.ProcessPath does not exist (so the executable path comes from MainModule).
///
/// Every failure path degrades to ordinary single-window behaviour rather than throwing — a debugging
/// convenience must never be able to stop the thing being debugged.
/// </summary>
internal sealed class LogWindow : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamWriter _writer;

    private LogWindow(NamedPipeServerStream pipe, StreamWriter writer)
    {
        _pipe = pipe;
        _writer = writer;
    }

    /// <summary>
    /// Starts the sink process and waits for it to attach. Returns null (with a reason) rather than
    /// throwing, so the caller can carry on in one window.
    /// </summary>
    public static LogWindow? TryStart(string title, out string? failure)
    {
        failure = null;

        var executable = CurrentExecutable();
        if (executable is null)
        {
            failure = "could not determine this executable's own path";
            return null;
        }

        var pipeName = "enlist-dev-log-" + Guid.NewGuid().ToString("N");
        NamedPipeServerStream? pipe = null;

        try
        {
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var psi = new ProcessStartInfo(executable)
            {
                // The whole point: a shell-executed console application is given its OWN console
                // window. With UseShellExecute = false it would inherit this process's console and
                // both would fight over the same screen, which is the problem being solved.
                UseShellExecute = true,

                // net472 has no ArgumentList, so the command line is assembled and quoted by hand.
                // The title is the only part that can contain spaces.
                Arguments = "--log-sink " + Quote(pipeName) + " --title " + Quote(title),
            };

            var process = Process.Start(psi);
            if (process is null)
            {
                pipe.Dispose();
                failure = "the log-window process did not start";
                return null;
            }

            process.Dispose();

            // Bounded: if the child never attaches (blocked by policy, killed instantly), the dev host
            // must not hang waiting for a window that is never coming.
            var connected = pipe.WaitForConnectionAsync();
            if (!connected.Wait(TimeSpan.FromSeconds(10)))
            {
                pipe.Dispose();
                failure = "the log window did not attach within 10s";
                return null;
            }

            var writer = new StreamWriter(pipe) { AutoFlush = true };
            return new LogWindow(pipe, writer);
        }
        catch (Exception ex)
        {
            if (pipe != null)
            {
                pipe.Dispose();
            }

            failure = ex.GetType().Name + " - " + ex.Message;
            return null;
        }
    }

    /// <summary>Returns false once the window is gone (closed by hand, or crashed), so the caller can fall back to printing locally instead of silently dropping output.</summary>
    public bool TryWriteLine(string text)
    {
        try
        {
            _writer.WriteLine(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        // Closing the pipe is what tells the sink the session is over, so it can say so and keep its
        // window open for reading rather than vanishing with the logs still in it.
        try
        {
            _writer.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            _pipe.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string? CurrentExecutable()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var path = current.MainModule?.FileName;
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The child half: owns a console window, prints whatever arrives on the pipe, and stays open
    /// afterwards.
    ///
    /// Staying open matters. A console window closes when its process exits, so exiting on
    /// end-of-stream would make the window disappear at the exact moment the session ended — taking
    /// the log of what just happened with it, which is usually the part worth reading.
    /// </summary>
    public static async Task<int> RunSinkAsync(string pipeName, string title)
    {
        try
        {
            Console.Title = title;
        }
        catch (Exception)
        {
            // Title is cosmetic; a host that does not support setting it must not stop the window.
        }

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(15000).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not attach to the dev host: " + ex.GetType().Name + " - " + ex.Message);
            Console.Error.WriteLine("Press Enter to close.");
            Console.ReadLine();
            return 3;
        }

        Console.WriteLine("=== " + title + " ===");
        Console.WriteLine("Log output from the enList dev host. Type commands in the other window.");
        Console.WriteLine();

        using var reader = new StreamReader(client);

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("=== dev host exited - this window is now idle. Press Enter to close. ===");
        Console.ReadLine();
        return 0;
    }
}
