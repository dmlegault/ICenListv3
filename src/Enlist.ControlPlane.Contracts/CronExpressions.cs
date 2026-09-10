using Cronos;

namespace Enlist.ControlPlane.Contracts;

/// <summary>
/// The one definition of what enList accepts as a cron expression — used by the agent's scheduler to
/// fire jobs, by the control plane to refuse a bad override at the API, and by the portal to say so
/// before the request is sent. Two dialects, told apart by counting fields: the standard five
/// (minute hour day month weekday) and the six-field seconds-first form (second minute hour day month
/// weekday) that [EnlistJob]'s own examples use — "0 3 * * *" and "0 0 3 * * *" are the same 03:00
/// daily. Quartz-style '?' is accepted for '*' (Cronos itself does not know it; the fire-time
/// arithmetic is identical, and every job declared the way the design doc's example shows uses it).
/// </summary>
public static class CronExpressions
{
    public static string Requirement => "a cron expression has five fields (minute hour day month weekday) or six (second first); '?' may stand for '*'";

    /// <summary>Parses, or throws FormatException naming what was wrong — the field count, or the field Cronos objected to.</summary>
    public static CronExpression Parse(string expression)
    {
        var normalized = (expression ?? "").Trim().Replace('?', '*');
        var fields = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields switch
        {
            5 => CronFormat.Standard,
            6 => CronFormat.IncludeSeconds,
            _ => throw new FormatException($"'{expression}' has {fields} field(s); {Requirement}."),
        };

        try
        {
            return CronExpression.Parse(normalized, format);
        }
        catch (CronFormatException ex)
        {
            throw new FormatException($"'{expression}' is not a valid cron expression: {ex.Message}", ex);
        }
    }

    /// <summary>Null when valid; otherwise the reason, in the words the API and the portal both show.</summary>
    public static string? Validate(string expression)
    {
        try
        {
            Parse(expression);
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }
}
