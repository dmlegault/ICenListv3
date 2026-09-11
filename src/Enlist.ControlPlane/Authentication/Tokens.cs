using System.Security.Cryptography;
using System.Text;

namespace Enlist.ControlPlane.Authentication;

/// <summary>
/// The three token kinds, told apart by prefix so a log line or an error message can say WHAT was
/// pasted in the wrong place without saying what it was. 32 bytes from the OS RNG, base64url, and
/// only the SHA-256 is ever stored — Authentication-Design.md section 11.
/// </summary>
public static class Tokens
{
    public const string AgentPrefix = "enla_";
    public const string JoinPrefix = "enlj_";
    public const string ApiKeyPrefix = "enlk_";

    public static string Generate(string prefix)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
