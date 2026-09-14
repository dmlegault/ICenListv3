using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;

using Enlist.Agent.Logging;
using Enlist.Agent.Supervision;
using Enlist.ControlPlane.Contracts;
using Enlist.TestSupport;

namespace Enlist.Agent.Tests;

/// <summary>
/// The agent's Images folder: runner image archives the agent loads into its container engine itself,
/// as the account it runs as.
///
/// That is the fix for wslc's per-account image store - an image an operator loads in their own shell is
/// not in the store of a LocalSystem agent - so what matters here is that the right archives are loaded,
/// exactly once, that anything else is refused, and that a record gone stale is corrected by the retry of
/// the container that found the image missing. The archives are built in the test with the manifest a
/// `docker save` writes; the engine is a stand-in that records what it was asked to load.
/// </summary>
public sealed class RunnerImageDropFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enlist-image-drop-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public RunnerImageDropFolderTests() => Directory.CreateDirectory(Images);

    private string Images => Path.Combine(_root, "Images");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task A_runner_image_archive_is_loaded_once_and_again_only_when_the_file_changes()
    {
        var archive = WriteArchive("enlist-runner-3.0.0.tar", "enlist/runner:3.0.0");
        var engine = new RecordingEngine();
        var folder = new RunnerImageDropFolder(Images, engine, Log);

        Assert.Equal(["enlist-runner-3.0.0.tar"], await folder.LoadNewImagesAsync());
        Assert.Equal([archive], engine.Loaded);
        Assert.Contains(_log, line => line.Contains("loaded into stand-in") && line.Contains("enlist/runner:3.0.0"));

        // Nothing new: no second load, even from a fresh instance reading the record back from disk.
        Assert.Empty(await folder.LoadNewImagesAsync());
        Assert.Empty(await new RunnerImageDropFolder(Images, engine, Log).LoadNewImagesAsync());
        Assert.Single(engine.Loaded);

        // A new copy of the file - a newer build under the same name - is loaded again.
        WriteArchive("enlist-runner-3.0.0.tar", "enlist/runner:3.0.0", padding: 64);
        Assert.Equal(["enlist-runner-3.0.0.tar"], await folder.LoadNewImagesAsync());
        Assert.Equal(2, engine.Loaded.Count);
    }

    [Theory]
    [InlineData("alpine:3")]
    [InlineData("enlist/runner:3.0.0", "someone/else:latest")]   // one foreign tag is enough to refuse
    [InlineData("enlist/runnerx:3.0.0")]                           // the repository, not a prefix of it
    [InlineData("enlist/runner:")]
    public async Task Only_enlist_runner_images_are_loaded_and_a_refusal_is_said_once(params string[] tags)
    {
        WriteArchive("other.tar", tags);
        var engine = new RecordingEngine();
        var folder = new RunnerImageDropFolder(Images, engine, Log);

        Assert.Empty(await folder.LoadNewImagesAsync());
        Assert.Empty(await folder.LoadNewImagesAsync());

        Assert.Empty(engine.Loaded);
        Assert.Single(_log, line => line.Contains("not loaded") && line.Contains("only enlist/runner images"));
    }

    [Fact]
    public async Task An_archive_that_disagrees_with_its_sha256_is_refused_and_one_that_agrees_is_loaded()
    {
        var archive = WriteArchive("enlist-runner-3.0.0.tar", "enlist/runner:3.0.0");
        File.WriteAllText(archive + ".sha256", new string('0', 64) + "  enlist-runner-3.0.0.tar\n");
        var engine = new RecordingEngine();
        var folder = new RunnerImageDropFolder(Images, engine, Log);

        Assert.Empty(await folder.LoadNewImagesAsync());
        Assert.Contains(_log, line => line.Contains("SHA-256") && line.Contains("incomplete"));
        Assert.Empty(engine.Loaded);

        File.WriteAllText(archive + ".sha256", Sha256(archive) + "  enlist-runner-3.0.0.tar\n");
        Assert.Equal(["enlist-runner-3.0.0.tar"], await folder.LoadNewImagesAsync());
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_archive_is_refused_without_throwing()
    {
        File.WriteAllText(Path.Combine(Images, "notes.tar"), "this is not a tar archive");
        WriteArchive("no-manifest.tar", tags: null);
        var engine = new RecordingEngine();

        Assert.Empty(await new RunnerImageDropFolder(Images, engine, Log).LoadNewImagesAsync());
        Assert.Empty(engine.Loaded);
        Assert.Equal(2, _log.Count(line => line.Contains("could not be read as an image archive")));
    }

    [Fact]
    public async Task A_load_the_engine_refuses_is_tried_again_next_time_rather_than_recorded()
    {
        WriteArchive("enlist-runner-3.0.0.tar", "enlist/runner:3.0.0");
        var engine = new RecordingEngine { FailLoads = true };
        var folder = new RunnerImageDropFolder(Images, engine, Log);

        Assert.Empty(await folder.LoadNewImagesAsync());
        Assert.Contains(_log, line => line.Contains("stand-in load failed"));

        engine.FailLoads = false;
        Assert.Equal(["enlist-runner-3.0.0.tar"], await folder.LoadNewImagesAsync());
    }

    [Fact]
    public async Task No_folder_means_nothing_to_load()
    {
        var engine = new RecordingEngine();
        Assert.Empty(await new RunnerImageDropFolder(Path.Combine(_root, "absent"), engine, Log).LoadNewImagesAsync());
        Assert.Empty(engine.Loaded);
    }

    [Fact]
    public async Task A_container_that_finds_its_image_missing_makes_the_retry_load_the_archive_again()
    {
        // The record says "loaded", the engine says the image is not there (removed by hand, an engine
        // reset). The backend must believe the engine: forget, so the retry reloads.
        WriteArchive("enlist-runner-3.0.0.tar", "enlist/runner:3.0.0");
        var engine = new RecordingEngine { ImageMissingOnRun = true };
        var folder = new RunnerImageDropFolder(Images, engine, Log);
        var backend = new ContainerRunnerBackend(engine, "enlist/runner:3.0.0", TimeSpan.FromSeconds(5), new AgentFileLogSink(Path.Combine(_root, "Logs")), "drop-test", images: folder);

        await Assert.ThrowsAsync<ContainerImageMissingException>(() => backend.StartAsync(Request()));
        Assert.Single(engine.Loaded);

        await Assert.ThrowsAsync<ContainerImageMissingException>(() => backend.StartAsync(Request()));
        Assert.Equal(2, engine.Loaded.Count);
    }

    [SkippableTheory]
    [InlineData("docker")]
    [InlineData("wslc")]
    public async Task Both_engines_are_asked_to_load_with_load_dash_i_and_a_failure_names_the_engine(string engine)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the stand-in engine is a .cmd script");

        var stub = Path.Combine(_root, engine + ".cmd");
        File.WriteAllText(stub,
            "@echo off\r\n" +
            "echo %*> \"%~dp0load-args.txt\"\r\n" +
            "(echo no space left on device) 1>&2\r\n" +
            "exit /b 1\r\n");
        IContainerEngine subject = engine == "docker" ? new DockerContainerEngine(stub) : new WslContainerEngine(stub);
        var archive = Path.Combine(_root, "enlist-runner-3.0.0.tar");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => subject.LoadImageAsync(archive));

        Assert.Equal($"load -i {archive}", File.ReadAllText(Path.Combine(_root, "load-args.txt")).Trim());
        Assert.Contains($"{engine} load failed", ex.Message);
        Assert.Contains("no space left on device", ex.Message);
    }

    private Task Log(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }

        return Task.CompletedTask;
    }

    private static RunnerStartRequest Request() =>
        new(RepoPaths.UniqueAppName(), "", Path.GetTempPath(), new IsolationSpec(IsolationModes.Container), (_, _) => Task.CompletedTask);

    /// <summary>An archive shaped like `docker save` output: manifest.json with RepoTags, and a blob. tags null writes no manifest at all.</summary>
    private string WriteArchive(string name, params string[]? tags) => WriteArchive(name, tags, 0);

    private string WriteArchive(string name, string tag, int padding) => WriteArchive(name, [tag], padding);

    private string WriteArchive(string name, string[]? tags, int padding)
    {
        var path = Path.Combine(Images, name);
        using (var stream = File.Create(path))
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax))
        {
            if (tags is not null)
            {
                var manifest = $"[{{\"Config\":\"blobs/sha256/abc\",\"RepoTags\":[{string.Join(",", tags.Select(t => $"\"{t}\""))}],\"Layers\":[]}}]";
                AddEntry(writer, "manifest.json", manifest);
            }

            AddEntry(writer, "blobs/sha256/abc", "{}" + new string(' ', padding));
        }

        return path;
    }

    private static void AddEntry(TarWriter writer, string name, string content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
        writer.WriteEntry(entry);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class RecordingEngine : IContainerEngine
    {
        public List<string> Loaded { get; } = [];

        public bool FailLoads { get; set; }

        public bool ImageMissingOnRun { get; set; }

        public string Name => "stand-in";

        public Task LoadImageAsync(string archivePath, CancellationToken ct = default)
        {
            if (FailLoads)
            {
                throw new InvalidOperationException("stand-in load failed (1): no space left on device");
            }

            Loaded.Add(archivePath);
            return Task.CompletedTask;
        }

        public Task<string> RunAsync(ContainerSpec spec, CancellationToken ct = default) =>
            ImageMissingOnRun
                ? throw new ContainerImageMissingException($"stand-in run failed: the image '{spec.Image}' is not on this machine.")
                : Task.FromResult("standin00001");

        public Task<int> GetPublishedPortAsync(string containerId, int containerPort, string protocol = "tcp", CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> WaitForExitAsync(string containerId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken ct = default) => Task.FromResult(true);

        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ContainerEngineProbe> ProbeAsync(CancellationToken ct = default) => Task.FromResult(new ContainerEngineProbe(true));

        public Task<IReadOnlyList<string>> ListByLabelsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
