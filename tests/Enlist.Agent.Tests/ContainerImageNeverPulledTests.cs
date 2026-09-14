using Enlist.Agent.Supervision;

namespace Enlist.Agent.Tests;

/// <summary>
/// The agent never pulls a container image.
///
/// Both engines' default pull policy is "missing": an image that is not already on the machine is
/// fetched from the default registry and run. For the name the installer gives an agent,
/// enlist/runner:3.0.0, that registry is Docker Hub and that namespace is not ours - so a machine
/// without the image would have run whatever somebody else published under it, as LocalSystem, with
/// an application package mounted inside. Every `run` therefore passes --pull never, and a missing
/// image is reported as the installation problem it is.
///
/// The first two tests use a stand-in engine that records its arguments, so they need no Docker and
/// no wslc and run everywhere the suite does. The third asks real Docker, when there is one, and
/// proves the engine itself honours the flag rather than only that the agent passes it.
/// </summary>
public sealed class ContainerImageNeverPulledTests : IDisposable
{
    private const string MissingImage = "enlist/runner:not-on-this-machine";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "enlist-pull-tests-" + Guid.NewGuid().ToString("N"));

    public ContainerImageNeverPulledTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [SkippableFact]
    public async Task Docker_is_told_never_to_pull_and_a_missing_image_says_what_to_do()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the stand-in engine is a .cmd script");

        var stub = StandIn("docker.cmd", "Error response from daemon: No such image: " + MissingImage, exitCode: 125);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DockerContainerEngine(stub).RunAsync(Spec()));

        AssertNeverPulled(RecordedArguments());
        Assert.Contains("never pulls", ex.Message);
        Assert.Contains(MissingImage, ex.Message);
    }

    [SkippableFact]
    public async Task Wslc_is_told_never_to_pull_and_a_missing_image_says_what_to_do()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the stand-in engine is a .cmd script");

        // What wslc 2.9.11 prints, first line and error code, for an image it does not have.
        var stub = StandIn("wslc.cmd", "No such image: " + MissingImage + "\r\necho Error code: WSLC_E_IMAGE_NOT_FOUND", exitCode: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WslContainerEngine(stub).RunAsync(Spec()));

        AssertNeverPulled(RecordedArguments());
        Assert.Contains("never pulls", ex.Message);
        Assert.Contains(MissingImage, ex.Message);
    }

    [SkippableFact]
    public async Task Real_docker_refuses_an_image_it_does_not_have_instead_of_fetching_it()
    {
        Skip.IfNot(await Docker.SucceedsAsync("version", "--format", "{{.Server.Version}}"), "Docker is unavailable.");

        // A tag nobody has published anywhere, so the only way this image could appear is a pull.
        var image = "enlist/runner:never-pulled-" + Guid.NewGuid().ToString("N")[..8];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DockerContainerEngine().RunAsync(Spec(image)));

        // With the default policy Docker would have gone to the registry and reported "pull access
        // denied" or "manifest unknown" instead; "No such image" is the answer only --pull never gives.
        Assert.Contains("never pulls", ex.Message);
        Assert.False(await Docker.SucceedsAsync("image", "inspect", image), "the image is on the machine now - it was pulled.");
    }

    private ContainerSpec Spec(string image = MissingImage) =>
        new(image, _dir, 5000, "enlist-pull-test-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A stand-in engine: records its command line beside itself, prints the given error, exits with the given code.</summary>
    private string StandIn(string name, string error, int exitCode)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path,
            "@echo off\r\n" +
            "echo %*> \"%~dp0args.txt\"\r\n" +
            $"(echo {error}) 1>&2\r\n" +
            $"exit /b {exitCode}\r\n");
        return path;
    }

    private string RecordedArguments() => File.ReadAllText(Path.Combine(_dir, "args.txt")).Trim();

    /// <summary>--pull never, as a pair, on the run itself - before the image, since everything after the image is the container's own command line.</summary>
    private static void AssertNeverPulled(string arguments)
    {
        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("run", tokens[0]);

        var pull = Array.IndexOf(tokens, "--pull");
        var image = Array.IndexOf(tokens, MissingImage);
        Assert.True(pull > 0, $"no --pull on the command line: {arguments}");
        Assert.Equal("never", tokens[pull + 1]);
        Assert.True(image > pull, $"--pull comes after the image, where it would be the container's argument, not the engine's: {arguments}");
    }
}
