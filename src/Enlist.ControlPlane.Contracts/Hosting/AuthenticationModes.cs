namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// The one setting the control plane and the portal share: "Authentication": { "Mode": "Required" | "Off" }.
/// Required unless configuration literally says Off, so a typo cannot disable authentication; a value
/// that is neither is refused at startup (see <see cref="ListenerRules"/>), so a typo cannot go
/// unnoticed either. Lives in Contracts because both hosts apply it and the rules around it
/// (Authentication-Design.md section 8), and a rule with two copies is a rule with two meanings.
/// </summary>
public static class AuthenticationModes
{
    public const string Required = "Required";
    public const string Off = "Off";

    public static bool IsOff(string? mode) => string.Equals(mode, Off, StringComparison.OrdinalIgnoreCase);

    public static bool IsKnown(string? mode) => IsOff(mode) || string.Equals(mode, Required, StringComparison.OrdinalIgnoreCase);
}
