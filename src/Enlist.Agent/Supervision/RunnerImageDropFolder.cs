using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;

namespace Enlist.Agent.Supervision;

/// <summary>
/// The agent's Images folder: enList runner image archives put there are loaded into the container
/// engine BY THE AGENT, as the account the agent runs as.
///
/// Why the agent does this rather than the operator. wslc keeps a separate image store for every
/// account (under that account's %LOCALAPPDATA%), and an installed agent runs as LocalSystem. So the
/// obvious step - `wslc load -i` in the operator's own shell - puts the image where the agent never
/// looks, and every container application fails with the image missing. Verified 2026-09-14: as
/// LocalSystem, wslc's image list was empty while the operator's held the image. Loading from inside
/// the agent lands the image in the one store that matters, for any engine and any service account,
/// with nobody needing to know which account that is.
///
/// What is loaded, and what is refused:
///   - only *.tar archives whose every tag is in the enlist/runner repository. This folder exists to
///     deliver THE runner image, and a folder that loaded anything would let whoever can write to it
///     replace any image this engine runs. The folder's ACL (Administrators and SYSTEM, set by
///     Enlist.Agent.msi) is the real control; this narrows what even an administrator's mistake can do;
///   - an archive with a .sha256 beside it has to match it. It guards against a truncated copy, not an
///     attacker - anyone who can write the archive can write its checksum;
///   - each archive once. What was loaded is recorded by name, size and time in loaded.json, and a
///     changed file is loaded again. The record can be wrong - someone can remove the image from the
///     engine - which is what <see cref="ForgetAsync"/> is for: the backend calls it when a container fails
///     for want of its image, and the retry reloads.
///
/// Checked before every container start rather than watched: that covers the first start after an
/// install, every retry, and an archive dropped in later, without a watcher thread to keep alive.
/// </summary>
public sealed class RunnerImageDropFolder
{
    /// <summary>The only repository this folder loads images for.</summary>
    public const string RunnerRepository = "enlist/runner";

    private const string StateFileName = "loaded.json";

    private readonly IContainerEngine _engine;
    private readonly Func<string, Task> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Refusals and failures are said once per version of a file, not on every container start.
    private readonly HashSet<string> _alreadySaid = new(StringComparer.OrdinalIgnoreCase);

    public RunnerImageDropFolder(string folder, IContainerEngine engine, Func<string, Task> log)
    {
        Folder = folder;
        _engine = engine;
        _log = log;
    }

    public string Folder { get; }

    private string StatePath => Path.Combine(Folder, StateFileName);

    /// <summary>Loads every archive in the folder not already loaded into this engine. Returns the file names it loaded. Never throws for a bad archive - that is logged and skipped.</summary>
    public async Task<IReadOnlyList<string>> LoadNewImagesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = ReadState();
            var loaded = new List<string>();

            foreach (var path in Directory.EnumerateFiles(Folder, "*.tar").Order(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                var file = new FileInfo(path);
                if (state.TryGetValue(file.Name, out var record) &&
                    record.Length == file.Length &&
                    record.LastWriteUtc == file.LastWriteTimeUtc &&
                    string.Equals(record.Engine, _engine.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var version = $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{_engine.Name}";

                string[] tags;
                try
                {
                    tags = ReadRepoTags(path);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
                {
                    await SayOnceAsync(version, $"Runner image {file.Name} not loaded: it could not be read as an image archive ({ex.Message}).").ConfigureAwait(false);
                    continue;
                }

                if (tags.Length == 0 || tags.Any(t => !IsRunnerTag(t)))
                {
                    await SayOnceAsync(version,
                        $"Runner image {file.Name} not loaded: only {RunnerRepository} images are loaded from {Folder}, " +
                        $"and this archive is tagged [{string.Join(", ", tags)}].").ConfigureAwait(false);
                    continue;
                }

                if (ChecksumMismatch(path) is { } mismatch)
                {
                    await SayOnceAsync(version, $"Runner image {file.Name} not loaded: {mismatch}").ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await _engine.LoadImageAsync(path, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await SayOnceAsync(version, $"Runner image {file.Name}: {ex.Message}").ConfigureAwait(false);
                    continue;
                }

                state[file.Name] = new LoadedImage(file.Length, file.LastWriteTimeUtc, _engine.Name, tags, DateTimeOffset.UtcNow);
                WriteState(state);
                loaded.Add(file.Name);
                await _log($"Runner image {file.Name} loaded into {_engine.Name} ({string.Join(", ", tags)}).").ConfigureAwait(false);
            }

            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the record of what was loaded, so the next <see cref="LoadNewImagesAsync"/> loads everything
    /// again. Called when a container fails because its image is missing: the record said it was loaded,
    /// and the engine says otherwise - the engine is the one to believe.
    /// </summary>
    public async Task ForgetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }

            _alreadySaid.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsRunnerTag(string tag) =>
        tag.StartsWith(RunnerRepository + ":", StringComparison.Ordinal) && tag.Length > RunnerRepository.Length + 1;

    /// <summary>The RepoTags from the archive's manifest.json - the name the image will have once loaded.</summary>
    internal static string[] ReadRepoTags(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            if (name != "manifest.json" || entry.DataStream is null)
            {
                continue;
            }

            using var manifest = JsonDocument.Parse(entry.DataStream);
            return manifest.RootElement.EnumerateArray()
                .Where(image => image.TryGetProperty("RepoTags", out var t) && t.ValueKind == JsonValueKind.Array)
                .SelectMany(image => image.GetProperty("RepoTags").EnumerateArray())
                .Select(tag => tag.GetString() ?? "")
                .ToArray();
        }

        throw new InvalidDataException("it has no manifest.json");
    }

    /// <summary>A reason when a .sha256 beside the archive disagrees with it; null when it agrees or there is none.</summary>
    private static string? ChecksumMismatch(string archivePath)
    {
        var sidecar = archivePath + ".sha256";
        if (!File.Exists(sidecar))
        {
            return null;
        }

        var expected = File.ReadAllText(sidecar).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"its SHA-256 is {actual.ToLowerInvariant()}, and {Path.GetFileName(sidecar)} says {expected} - the copy is probably incomplete.";
    }

    private async Task SayOnceAsync(string version, string message)
    {
        if (_alreadySaid.Add(version))
        {
            await _log(message).ConfigureAwait(false);
        }
    }

    private Dictionary<string, LoadedImage> ReadState()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<Dictionary<string, LoadedImage>>(File.ReadAllText(StatePath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable record costs one reload of each archive; that is the cheap direction to err in.
            return new();
        }
    }

    private void WriteState(Dictionary<string, LoadedImage> state)
    {
        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to write the record means loading again next time, not failing to run.
            _ = _log($"Could not record loaded runner images in {StatePath}: {ex.Message}");
        }
    }

    private sealed record LoadedImage(long Length, DateTime LastWriteUtc, string Engine, string[] Tags, DateTimeOffset LoadedAtUtc);
}
