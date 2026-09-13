namespace Enlist.TestSupport;

/// <summary>
/// The environment override that turns authentication on for a spawned server.
///
/// <see cref="ControlPlaneTestServer"/> starts with Authentication:Mode set to Off, because most
/// tests are about something else entirely and a bearer token on every call would be noise in them.
/// The tests that ARE about authentication pass this instead. Five of them had written out the same
/// one-entry dictionary, in four different assemblies; the spelling of the key is the part worth
/// having in one place, since it is the double-underscore environment-variable form rather than the
/// colon form the configuration uses.
/// </summary>
public static class AuthenticationMode
{
    public static readonly IReadOnlyDictionary<string, string> Required =
        new Dictionary<string, string> { ["Authentication__Mode"] = "Required" };

    public static readonly IReadOnlyDictionary<string, string> Off =
        new Dictionary<string, string> { ["Authentication__Mode"] = "Off" };
}
