using Enlist.ControlPlane.Contracts;

namespace Enlist.Agent.Staging;

/// <summary>
/// Copies the one canonical enlist-runner build into a per-instance folder, renaming just the .exe to
/// the application's name — verified by hand to work with a plain file copy, no re-patching: the
/// apphost resolves its companion .dll by a name baked into the native stub at build time, not by its
/// own filename. This is Aware's ServiceProjectInstaller.CreateClrHost trick (see
/// docs/06-background/Aware-Architecture-Notes.md section 1, "per-service host copies"), minus the native C++ host —
/// same reason to do it: Task Manager and the SCM show "SampleService.exe", not a wall of identical
/// "enlist-runner.exe" or "dotnet.exe" entries.
///
/// One staged copy per CONCURRENTLY RUNNING instance is required — two processes can't share one exe
/// file under two different names — so this runs once per app start, not once ever.
/// </summary>
public static class RunnerStaging
{
    /// <summary>Stages a fresh copy under stagingRoot/applicationName and returns the path to the renamed executable.</summary>
    public static string Stage(string runnerBinDirectory, string stagingRoot, string applicationName)
    {
        // The name is a directory under stagingRoot and the process name an operator sees. The control
        // plane refuses anything else at its API; an --assignments file is not behind that API.
        if (!ApplicationNames.IsValid(applicationName))
        {
            throw new ArgumentException($"'{applicationName}' is not a valid application name: {ApplicationNames.Requirement}.", nameof(applicationName));
        }

        var instanceDir = Path.Combine(stagingRoot, applicationName);
        Directory.CreateDirectory(instanceDir);

        var exeSource = Path.Combine(runnerBinDirectory, "enlist-runner.exe");
        if (!File.Exists(exeSource))
        {
            throw new FileNotFoundException(
                $"Canonical runner apphost not found: {exeSource}. Build Enlist.Runner (or Enlist.Runner.Legacy, for a net472-flavored runner-bin) before starting the agent.", exeSource);
        }

        // Copy every companion file the runner build actually produced — its own managed .dll (plus
        // .deps.json/.runtimeconfig.json) for a framework-dependent net10.0 build, or an arbitrary set
        // of NuGet dependency assemblies for the net472 legacy runner (System.Text.Json.dll and its own
        // transitive dependencies) — whatever THAT build needs sitting next to the renamed exe, rather
        // than a fixed list of filenames that only ever matched the modern runner's own output shape.
        //
        // Every file keeps its ORIGINAL name except enlist-runner.exe.config, which — like the exe
        // itself — must be renamed to match: .NET Framework looks up an app's config file strictly by
        // "<exe filename>.config", so a config left as "enlist-runner.exe.config" next to a renamed
        // "<app>.exe" would silently never be found. This is deliberately the ONE exception: the
        // modern runner's own apphost trick needs the OPPOSITE (its companion .dll must KEEP the
        // "enlist-runner" name — the renamed apphost looks for its managed dll by a name baked in at
        // build time, not by its own filename) — see this class's own header comment.
        foreach (var file in Directory.EnumerateFiles(runnerBinDirectory))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, "enlist-runner.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue; // staged separately below, under the application's own name
            }

            var destName = string.Equals(fileName, "enlist-runner.exe.config", StringComparison.OrdinalIgnoreCase)
                ? applicationName + ".exe.config"
                : fileName;

            File.Copy(file, Path.Combine(instanceDir, destName), overwrite: true);
        }

        var renamedExe = Path.Combine(instanceDir, applicationName + ".exe");
        File.Copy(exeSource, renamedExe, overwrite: true);

        return renamedExe;
    }

    /// <summary>Best-effort — a staging folder left behind after a crash costs disk space, not correctness, so cleanup failures are never fatal.</summary>
    public static void Cleanup(string stagingRoot, string applicationName)
    {
        try
        {
            var instanceDir = Path.Combine(stagingRoot, applicationName);
            if (Directory.Exists(instanceDir))
            {
                Directory.Delete(instanceDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
