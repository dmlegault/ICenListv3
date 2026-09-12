namespace Enlist.Portal.Services;

/// <summary>
/// Turning a list of key/value rows someone is still typing into a dictionary, without the two ways
/// that goes wrong.
///
/// ToDictionary throws ArgumentException on a duplicate key. That is a reasonable default for data
/// from a database and a bad one for data from a keyboard, because the moment a second row's key
/// matches a first the throw happens - and in the policy wizard the selector is built inside a
/// PROPERTY read during render, so the exception left the render loop entirely. Nothing catches it
/// (it is not an ApiException, which is all the dialogs guard against), so the circuit died: the page
/// went blank mid-edit, everything typed into the dialog was gone, and the only trace was a
/// duplicate-key message in the server log. All the person did was type a key twice.
///
/// So: Collapse never throws, and Duplicates gives the save path something specific to say instead of
/// silently keeping whichever row happened to be last.
/// </summary>
public static class KeyedRows
{
    /// <summary>Rows to a dictionary, blank keys dropped, keys trimmed, last occurrence winning. Never throws.</summary>
    public static Dictionary<string, string> Collapse(IEnumerable<(string? Key, string? Value)> rows)
    {
        var collapsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in rows)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                collapsed[key.Trim()] = value ?? "";
            }
        }

        return collapsed;
    }

    /// <summary>The keys entered more than once, in the order first seen, so a message can name them.</summary>
    public static List<string> Duplicates(IEnumerable<string?> keys) =>
        keys.Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!.Trim())
            .GroupBy(k => k, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

    /// <summary>The message for those duplicates, or null when there are none — so a caller reads as "if there is something to say, say it and stop".</summary>
    public static string? DuplicateMessage(IEnumerable<string?> keys, string what)
    {
        var duplicates = Duplicates(keys);
        return duplicates.Count == 0
            ? null
            : $"Each {what} can only appear once. Repeated: {string.Join(", ", duplicates.Select(d => $"'{d}'"))}.";
    }
}
