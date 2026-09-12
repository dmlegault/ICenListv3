using Enlist.Agent.Configuration;

namespace Enlist.Agent.Tests;

/// <summary>Pure unit test of the pruning mechanism itself — no download, no control plane, just the local filesystem bookkeeping.</summary>
public sealed class PackageCacheTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "enlist-pkgcache-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
        catch
        {
        }
    }

    private void SeedCachedDigest(string digest)
    {
        var dir = Path.Combine(_cacheRoot, digest);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dummy.txt"), "content");
        File.WriteAllText(Path.Combine(_cacheRoot, digest + ".complete"), DateTimeOffset.UtcNow.ToString("O"));
    }

    [Fact]
    public async Task PruneUnusedAsync_removes_cached_digests_not_in_the_use_set_and_keeps_the_rest()
    {
        var cache = new PackageCache(new HttpClient(), _cacheRoot);
        SeedCachedDigest("digest-keep");
        SeedCachedDigest("digest-remove");

        await cache.PruneUnusedAsync(new HashSet<string> { "digest-keep" }, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_cacheRoot, "digest-keep")));
        Assert.True(File.Exists(Path.Combine(_cacheRoot, "digest-keep.complete")));

        Assert.False(Directory.Exists(Path.Combine(_cacheRoot, "digest-remove")));
        Assert.False(File.Exists(Path.Combine(_cacheRoot, "digest-remove.complete")));
    }
    [Fact]
    public async Task PruneUnusedAsync_reclaims_an_interrupted_extraction_that_never_got_a_marker()
    {
        var cache = new PackageCache(new HttpClient(), _cacheRoot);

        // What a crash mid-extraction leaves behind: a half-written directory, or a downloaded zip,
        // and NO completion marker. That absence is exactly how EnsureExtractedAsync recognises the
        // interruption and redoes the work - and it was also what hid these bytes from every sweep,
        // because the sweep enumerated markers and nothing else. They stayed on disk until somebody
        // deleted the cache by hand.
        var interrupted = Path.Combine(_cacheRoot, "digest-interrupted");
        Directory.CreateDirectory(interrupted);
        await File.WriteAllTextAsync(Path.Combine(interrupted, "partial.txt"), "half an extraction");
        await File.WriteAllTextAsync(Path.Combine(_cacheRoot, "digest-abandoned.zip"), "a download nobody finished");

        // An extraction in progress has the same shape on disk as an abandoned one, and must survive:
        // it has no marker yet either, but its digest is in use, which is why it is being extracted.
        var extracting = Path.Combine(_cacheRoot, "digest-extracting");
        Directory.CreateDirectory(extracting);

        SeedCachedDigest("digest-keep");

        await cache.PruneUnusedAsync(new HashSet<string> { "digest-keep", "digest-extracting" }, CancellationToken.None);

        Assert.False(Directory.Exists(interrupted));
        Assert.False(File.Exists(Path.Combine(_cacheRoot, "digest-abandoned.zip")));
        Assert.True(Directory.Exists(extracting));
        Assert.True(Directory.Exists(Path.Combine(_cacheRoot, "digest-keep")));
        Assert.True(File.Exists(Path.Combine(_cacheRoot, "digest-keep.complete")));
    }

    [Fact]
    public async Task A_digest_that_is_not_a_digest_is_refused_before_it_can_become_a_cache_path()
    {
        var cache = new PackageCache(new HttpClient(), _cacheRoot);

        await Assert.ThrowsAsync<ArgumentException>(() => cache.EnsureExtractedAsync("..\\..\\escape", CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(_cacheRoot));
    }
}
