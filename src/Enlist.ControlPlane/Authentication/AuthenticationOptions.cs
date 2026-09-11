namespace Enlist.ControlPlane.Authentication;

/// <summary>
/// Bound from the "Authentication" section. The mode is Required unless configuration says Off, and
/// Off is only honoured when every listener is loopback — <see cref="ListenerRules"/> refuses startup
/// otherwise. Anything that is not literally "Off" is treated as Required, so a typo cannot disable
/// authentication; a value that is neither is a startup error, so a typo cannot go unnoticed either.
/// </summary>
public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";
    public const string Required = "Required";
    public const string Off = "Off";

    public string Mode { get; set; } = Required;

    public bool IsOff => string.Equals(Mode, Off, StringComparison.OrdinalIgnoreCase);

    public bool IsRequired => !IsOff;

    public bool IsKnownMode => IsOff || string.Equals(Mode, Required, StringComparison.OrdinalIgnoreCase);
}
