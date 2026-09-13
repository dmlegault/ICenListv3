using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Win32;

namespace Enlist.Installer.Detection
{
    /// <summary>What the installer found, for one prerequisite row on the Prerequisites page.</summary>
    public sealed class PrerequisiteResult
    {
        public PrerequisiteResult(bool present, string detail)
        {
            Present = present;
            Detail = detail;
        }

        public bool Present { get; }

        /// <summary>What to show beside the row: a version when it was found, a reason when it was not.</summary>
        public string Detail { get; }

        public override string ToString() => (Present ? "present: " : "missing: ") + Detail;
    }

    /// <summary>
    /// The .NET runtimes, read from the registry the way the .NET installers actually write it.
    ///
    /// They record one value per installed version under the shared-framework key - a DWORD named
    /// "10.0.12", not a "Version" value to read - so the only question that can be asked is "is this
    /// version present". That shapes the bundle too: its prerequisite DetectConditions pin an exact
    /// patch for the same reason (see src/Prerequisites.wxs). Here, where there is a whole registry
    /// enumeration available rather than one Burn search expression, the question can be asked
    /// properly: what is the newest 10.x present, if any.
    ///
    /// THE VIEW MATTERS. These values are written to the 32-bit view, so a 64-bit process reading
    /// HKLM\SOFTWARE\dotnet finds nothing at all. The bootstrapper is 32-bit and would stumble into
    /// the right answer; the tests are not, and would not. Both open Registry32 explicitly rather
    /// than relying on what the host process happens to be.
    /// </summary>
    public static class RuntimeDetection
    {
        private const string SharedFrameworkKey = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx";

        /// <summary>The ASP.NET Core shared framework, which the control plane and the portal need.</summary>
        public const string AspNetCore = "Microsoft.AspNetCore.App";

        /// <summary>The base runtime, which all three components need.</summary>
        public const string NetCore = "Microsoft.NETCore.App";

        /// <summary>
        /// Every version of one shared framework installed for x64, newest first. Empty when the
        /// framework is not installed at all, which is not an error.
        /// </summary>
        public static IReadOnlyList<Version> InstalledVersions(string framework)
        {
            if (string.IsNullOrWhiteSpace(framework))
            {
                throw new ArgumentException("A shared framework name is required.", nameof(framework));
            }

            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var key = baseKey.OpenSubKey(SharedFrameworkKey + "\\" + framework))
            {
                if (key == null)
                {
                    return new Version[0];
                }

                var found = new List<Version>();
                foreach (var name in key.GetValueNames())
                {
                    // A value that is not a version, or is not set to 1, is not an installation. Both
                    // are skipped rather than guessed at.
                    Version parsed;
                    if (!Version.TryParse(name, out parsed))
                    {
                        continue;
                    }

                    var value = key.GetValue(name);
                    if (value is int && (int)value == 1)
                    {
                        found.Add(parsed);
                    }
                }

                found.Sort();
                found.Reverse();
                return found;
            }
        }

        /// <summary>
        /// Whether a runtime of at least <paramref name="minimum"/> and the same MAJOR version is
        /// installed, and which one. Same major deliberately: .NET majors are not
        /// backward-compatible for a framework-dependent application, so a machine with only .NET 9
        /// does not satisfy a .NET 10 requirement however new the 9 is.
        /// </summary>
        public static PrerequisiteResult Check(string framework, Version minimum)
        {
            if (minimum == null)
            {
                throw new ArgumentNullException(nameof(minimum));
            }

            var installed = InstalledVersions(framework);
            var usable = installed.FirstOrDefault(v => v.Major == minimum.Major && v >= minimum);

            if (usable != null)
            {
                return new PrerequisiteResult(true, usable.ToString());
            }

            if (installed.Count == 0)
            {
                return new PrerequisiteResult(false, framework + " is not installed");
            }

            return new PrerequisiteResult(
                false,
                "found " + string.Join(", ", installed.Select(v => v.ToString()).ToArray()) + ", need " + minimum + " or newer");
        }

        /// <summary>
        /// .NET Framework 4.7.2 or newer, which the agent needs ONLY to host net472 applications
        /// through the legacy runner. Informational on the Prerequisites page: its absence never
        /// blocks an install, it just means net472 policy rules on this agent will fail with a clear
        /// reason when they are assigned.
        ///
        /// 461808 is the documented release number for 4.7.2. The value is cumulative, so any later
        /// .NET Framework reports a higher one.
        /// </summary>
        public static PrerequisiteResult CheckNetFramework472()
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                var release = key?.GetValue("Release") as int?;
                if (release == null)
                {
                    return new PrerequisiteResult(false, ".NET Framework 4.x is not installed");
                }

                return release.Value >= 461808
                    ? new PrerequisiteResult(true, "release " + release.Value)
                    : new PrerequisiteResult(false, "release " + release.Value + " is older than 4.7.2");
            }
        }
    }
}
