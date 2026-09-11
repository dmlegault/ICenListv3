using Enlist.Portal.Services;

namespace Enlist.Portal.Tests;

/// <summary>The dpapi: configuration value the installer writes for the portal's key (PortalCredential): a round trip on this machine, a value in the clear passed through, nothing for nothing.</summary>
public sealed class ProtectedSettingsTests
{
    [Fact]
    public void A_protected_value_reveals_the_secret_and_does_not_contain_it()
    {
        var protectedValue = ProtectedSettings.Protect("enlk_secret_value");

        Assert.StartsWith(ProtectedSettings.Prefix, protectedValue);
        Assert.DoesNotContain("enlk_secret_value", protectedValue);
        Assert.Equal("enlk_secret_value", ProtectedSettings.Reveal(protectedValue));
    }

    [Fact]
    public void A_value_in_the_clear_is_returned_as_is_and_an_empty_one_is_nothing()
    {
        Assert.Equal("enlk_plain", ProtectedSettings.Reveal("  enlk_plain "));
        Assert.Null(ProtectedSettings.Reveal(null));
        Assert.Null(ProtectedSettings.Reveal("   "));
    }

    [Fact]
    public void A_protected_value_that_is_not_this_machines_is_reported_not_guessed()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => ProtectedSettings.Reveal(ProtectedSettings.Prefix + Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));
        Assert.Contains("protect", refused.Message);
    }
}
