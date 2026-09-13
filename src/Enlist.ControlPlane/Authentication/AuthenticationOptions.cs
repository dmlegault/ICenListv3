using Enlist.ControlPlane.Contracts;

namespace Enlist.ControlPlane.Authentication;

/// <summary>
/// Bound from the "Authentication" section. The mode is Required unless configuration says Off, and
/// Off is only honoured when every listener is loopback — <see cref="ListenerRules"/> refuses startup
/// otherwise. The vocabulary (<see cref="AuthenticationModes"/>) is shared with the portal.
/// </summary>
public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";
    public const string Required = AuthenticationModes.Required;
    public const string Off = AuthenticationModes.Off;

    public string Mode { get; set; } = Required;

    public bool IsOff => AuthenticationModes.IsOff(Mode);

    public bool IsRequired => !IsOff;
}
