using System.Security.Cryptography;

using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Data;

/// <summary>
/// Filesystem-backed, content-addressed blob storage — the design doc's own choice ("Package blobs on
/// the filesystem or blob storage, keyed by digest"). One file per digest; writing the same digest
/// twice is a no-op (SaveAsync checks existence first), which is what makes re-uploading an unchanged
/// package idempotent rather than an error.
/// </summary>
public sealed class PackageBlobStore
{
    private readonly string _root;

    public PackageBlobStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>Hashes the stream while writing it to a temp file, then atomically moves it into place under its own digest — the digest is computed from what was actually received, never trusted from the caller.</summary>
    public async Task<(string Digest, long SizeBytes, bool AlreadyExisted)> SaveAsync(Stream content, CancellationToken ct)
    {
        var tempPath = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        long size;
        string digest;

        try
        {
            await using var tempFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var sha256 = SHA256.Create();
            await using var hashingStream = new CryptoStream(tempFile, sha256, CryptoStreamMode.Write);

            await content.CopyToAsync(hashingStream, ct).ConfigureAwait(false);
            await hashingStream.FlushFinalBlockAsync(ct).ConfigureAwait(false);
            digest = Convert.ToHexStringLower(sha256.Hash!);
            size = tempFile.Length;
        }
        catch
        {
            // A client that abandons a 500 MB upload, a full disk, a cancelled request. Without this
            // the partial .tmp stayed under the blob root forever: nothing sweeps it, because the
            // retention sweep walks package ROWS and this upload never produced one.
            TryDeleteFile(tempPath);
            throw;
        }

        var finalPath = PathFor(digest);
        if (File.Exists(finalPath))
        {
            TryDeleteFile(tempPath);
            return (digest, new FileInfo(finalPath).Length, AlreadyExisted: true);
        }

        try
        {
            File.Move(tempPath, finalPath);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // Two uploads of identical bytes in flight at once: both saw no file, and the loser's
            // Move lands on one that exists by the time it runs. This is not a failure - the name IS
            // the hash of the content, so the bytes now on disk are byte-for-byte what this caller
            // was uploading. Only this copy of them is redundant. It used to be a 500 for a package
            // that was, in fact, successfully stored.
            TryDeleteFile(tempPath);
            return (digest, new FileInfo(finalPath).Length, AlreadyExisted: true);
        }

        return (digest, size, AlreadyExisted: false);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a file nothing references. Worth disk space, never worth failing
            // the request that was on its way to succeeding.
        }
    }

    public bool Exists(string digest) => File.Exists(PathFor(digest));

    public Stream OpenRead(string digest) => File.OpenRead(PathFor(digest));

    /// <summary>Best-effort — called only by PackageRetentionSweepService after it's already decided a digest is safely past its grace period; a delete failing (file locked, permissions) just means it's retried on the next sweep, never a reason to fail the sweep itself.</summary>
    public bool TryDelete(string digest)
    {
        try
        {
            File.Delete(PathFor(digest));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every endpoint that takes a digest from a request checks it first; this checks it again, because this is the one line that turns a string into a path under the blob root, and so the one line that must never see "..".</summary>
    private string PathFor(string digest)
    {
        if (!PackageDigests.IsValid(digest))
        {
            throw new ArgumentException($"'{digest}' is not a package digest.", nameof(digest));
        }

        return System.IO.Path.Combine(_root, PackageDigests.Normalize(digest) + ".zip");
    }
}
