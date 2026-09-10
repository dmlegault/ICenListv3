using System.IO.Compression;

namespace Enlist.TestSupport;

public static class TestPackaging
{
    /// <summary>Zips a directory's contents. extraFileContent lets two zips built from the SAME source directory get deliberately different digests, for testing a package upgrade.</summary>
    public static byte[] ZipDirectory(string sourceDir, string? extraFileContent = null)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var entryName = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, entryName);
            }

            if (extraFileContent is not null)
            {
                var entry = archive.CreateEntry("version-marker.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(extraFileContent);
            }
        }

        return ms.ToArray();
    }
}
