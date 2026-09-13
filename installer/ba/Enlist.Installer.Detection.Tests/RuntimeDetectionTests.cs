using System.Runtime.Versioning;

using Enlist.Installer.Detection;

namespace Enlist.Installer.Detection.Tests;

/// <summary>
/// Reading the .NET runtimes off THIS machine, which is the only way to find out whether the
/// registry shape assumed here is the one the .NET installers actually write.
///
/// The shape is the whole point. It is not a "Version" value to read: the key holds one DWORD per
/// installed version, named for that version, and it lives in the 32-bit view - so a 64-bit process
/// reading HKLM\SOFTWARE\dotnet finds nothing at all and concludes .NET is absent on a machine that
/// is running .NET. Both mistakes are invisible in code review and obvious here.
/// </summary>
public sealed class RuntimeDetectionTests
{
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void The_dotnet_runtime_this_test_is_running_on_is_found_in_the_registry()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is a Windows facility.");

        // These tests run on net10.0, so a .NET 10 runtime is installed by definition. If detection
        // says otherwise, detection is wrong - there is no third possibility, which is what makes
        // this worth asserting rather than a tautology.
        var installed = RuntimeDetection.InstalledVersions(RuntimeDetection.NetCore);

        Assert.NotEmpty(installed);
        Assert.Contains(installed, v => v.Major == 10);
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Versions_come_back_newest_first()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is a Windows facility.");

        var installed = RuntimeDetection.InstalledVersions(RuntimeDetection.NetCore);
        Skip.If(installed.Count < 2, "This machine has only one .NET runtime installed.");

        // The page shows the newest, so the order is part of the contract rather than incidental.
        Assert.Equal(installed.OrderByDescending(v => v).ToArray(), installed.ToArray());
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void A_framework_that_is_not_installed_is_reported_as_absent_rather_than_throwing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is a Windows facility.");

        // A missing key is an ordinary answer on a machine that has never had this framework, and the
        // Prerequisites page has a row for it either way.
        var installed = RuntimeDetection.InstalledVersions("Microsoft.NotAFramework.App");
        Assert.Empty(installed);

        var check = RuntimeDetection.Check("Microsoft.NotAFramework.App", new Version(10, 0, 0));
        Assert.False(check.Present);
        Assert.Contains("not installed", check.Detail);
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void A_newer_patch_satisfies_the_requirement_and_a_newer_major_does_not()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is a Windows facility.");

        var installed = RuntimeDetection.InstalledVersions(RuntimeDetection.NetCore);
        var ten = installed.FirstOrDefault(v => v.Major == 10);
        Skip.If(ten is null, "This machine has no .NET 10 runtime.");

        // A patch older than what is installed is satisfied by it.
        Assert.True(RuntimeDetection.Check(RuntimeDetection.NetCore, new Version(10, 0, 0)).Present);

        // A major that is not installed is not satisfied by a NEWER one, because a
        // framework-dependent .NET 11 application does not run on .NET 10 and the reverse is just as
        // untrue. This is the check that would quietly pass if Check compared versions alone.
        var absurdlyOld = RuntimeDetection.Check(RuntimeDetection.NetCore, new Version(3, 0, 0));
        Assert.False(absurdlyOld.Present);
        Assert.Contains("need 3.0.0", absurdlyOld.Detail);
    }

    [Theory]
    [InlineData("10.0.5", "10.0.5")]
    [InlineData("  10.0.5  ", "10.0.5")]
    [InlineData("10.0", "10.0")]
    public void A_minimum_that_parses_is_used(string text, string expected)
    {
        Assert.Equal(Version.Parse(expected), RuntimeDetection.ParseMinimum(text, new Version(9, 9, 9)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("10.0.x")]
    [InlineData("not a version at all")]
    public void A_minimum_that_does_not_parse_falls_back_rather_than_throwing(string? text)
    {
        // This value arrives from a Burn variable and is read BEFORE the wizard is on screen. An
        // exception here would end the bootstrapper process, and all the engine could report is
        // 0x800700e8, a closed pipe - so a mistyped version number would look exactly like a crash.
        var fallback = new Version(10, 0, 0);
        Assert.Equal(fallback, RuntimeDetection.ParseMinimum(text!, fallback));
    }

    [Fact]
    public void A_minimum_with_no_fallback_to_fall_back_to_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => RuntimeDetection.ParseMinimum("nonsense", null!));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Asp_net_core_is_judged_by_comparison_rather_than_by_an_exact_patch()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is a Windows facility.");

        var installed = RuntimeDetection.InstalledVersions(RuntimeDetection.AspNetCore);
        var ten = installed.FirstOrDefault(v => v.Major == 10);
        Skip.If(ten is null, "This machine has no ASP.NET Core 10 runtime.");

        // The defect this replaced: the bundle asked whether one exact patch was present, so a
        // machine with 10.0.11 failed a check for 10.0.12 and downloaded a runtime it already had a
        // newer copy of. Asking for a patch OLDER than the installed one has to succeed.
        var older = new Version(ten!.Major, ten.Minor, 0);
        Assert.True(
            RuntimeDetection.Check(RuntimeDetection.AspNetCore, older).Present,
            "ASP.NET Core " + ten + " is installed, so " + older + " or newer must be satisfied.");

        // And a patch NEWER than anything installed must not be, or the check would wave through a
        // machine that genuinely needs the download.
        var newer = new Version(ten.Major, ten.Minor, ten.Build + 1000);
        Assert.False(RuntimeDetection.Check(RuntimeDetection.AspNetCore, newer).Present);
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Net_framework_472_is_reported_with_its_release_number()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The registry is a Windows facility.");

        // Every supported Windows has 4.7.2 or newer inbox, so this is present on any machine that
        // can run these tests. It is informational on the page: the agent installs without it and
        // only net472 applications are affected.
        var check = RuntimeDetection.CheckNetFramework472();
        Assert.True(check.Present, check.Detail);
        Assert.Contains("release", check.Detail);
    }
}
