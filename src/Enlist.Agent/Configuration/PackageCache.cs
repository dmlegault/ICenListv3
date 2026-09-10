using System.IO.Compression;
using System.Security.Cryptography;

using Enlist.ControlPlane.Contracts;

namespace Enlist.Agent.Configuration;

/// <summary>
/// Resolves a package digest to a local, ready-to-run directory — downloading and extracting it if
/// this is the first time this digest has been seen, or just returning the existing extraction if
/// not. This IS the payoff of content addressing the design doc calls out: redeploying the SAME
/// digest to a machine that already has it costs nothing (no download, no extraction, just a path
/// lookup), and rolling back is "point the assignment at a digest already cached everywhere."
/// </summary>
public sealed class PackageCache
{
    private readonly HttpClient _http;
    private readonly string _cacheRoot;

    public PackageCache(HttpClient http, string cacheRoot)
    {
        _http = http;
        _cacheRoot = cacheRoot;
        Directory.CreateDirectory(_cacheRoot);
    }

    /// <summary>Returns the local directory for this digest, downloading and extracting it first if it isn't already cached. A directory that already exists is assumed complete — see the "marker file" note below for why that assumption is safe.</summary>
    public async Task<string> EnsureExtractedAsync(string digest, CancellationToken ct)
    {
        // The digest becomes a directory name under the cache root. Refused here rather than trusted
        // from the control plane, so a bad row there can never become a path here.
        if (!PackageDigests.IsValid(digest))
        {
            throw new ArgumentException($"'{digest}' is not a package digest: {PackageDigests.Requirement}.", nameof(digest));
        }

        digest = PackageDigests.Normalize(digest);

        var extractDir = Path.Combine(_cacheRoot, digest);
        var markerFile = Path.Combine(_cacheRoot, digest + ".complete");

        // The marker is written only after extraction fully succeeds — its absence is what tells a
        // resumed agent "the last attempt was interrupted, redo it" rather than trusting a
        // partially-extracted directory left behind by a prior crash mid-extraction.
        if (File.Exists(markerFile))
        {
            return extractDir;
        }

        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }

        var zipPath = Path.Combine(_cacheRoot, digest + ".zip");
        await DownloadAndVerifyAsync(digest, zipPath, ct).ConfigureAwait(false);

        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        File.Delete(zipPath);

        await File.WriteAllTextAsync(markerFile, DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);
        return extractDir;
    }

    /// <summary>
    /// Deletes every cached digest NOT in digestsInUse. Safe to do unconditionally and immediately —
    /// unlike the control plane's own package retention (which has to preserve a rollback window),
    /// this is only ever a local, freely re-downloadable cache, never the durable copy. If a pruned
    /// digest is needed again later, EnsureExtractedAsync just re-fetches it.
    /// </summary>
    public Task PruneUnusedAsync(IReadOnlySet<string> digestsInUse, CancellationToken ct) => Task.Run(() =>
    {
        foreach (var markerFile in SafeEnumerateFiles(_cacheRoot, "*.complete"))
        {
            var digest = Path.GetFileNameWithoutExtension(markerFile);
            if (digestsInUse.Contains(digest))
            {
                continue;
            }

            TryDeleteDirectory(Path.Combine(_cacheRoot, digest));
            TryDeleteFile(markerFile);
            TryDeleteFile(Path.Combine(_cacheRoot, digest + ".zip")); // an orphaned partial download, if extraction was ever interrupted
        }
    }, ct);

    private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private async Task DownloadAndVerifyAsync(string digest, string destinationZipPath, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"/api/packages/{Uri.EscapeDataString(digest)}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string actualDigest;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sha256 = SHA256.Create())
        await using (var hashingStream = new CryptoStream(destination, sha256, CryptoStreamMode.Write))
        {
            await source.CopyToAsync(hashingStream, ct).ConfigureAwait(false);
            await hashingStream.FlushFinalBlockAsync(ct).ConfigureAwait(false);
            actualDigest = Convert.ToHexStringLower(sha256.Hash!);
        }

        if (!string.Equals(actualDigest, digest, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destinationZipPath);
            throw new InvalidOperationException(
                $"Downloaded package for digest '{digest}' does not match its own content (got '{actualDigest}') - corrupted transfer or a control plane bug. Not extracting it.");
        }
    }
}
