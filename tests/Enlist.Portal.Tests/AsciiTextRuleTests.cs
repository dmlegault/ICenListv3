using System.Text;
using System.Text.RegularExpressions;

using Enlist.TestSupport;

namespace Enlist.Portal.Tests;

/// <summary>
/// The project's ASCII rule, enforced rather than remembered.
///
/// Every character a user can see is plain ASCII: log lines, console output, API messages, and - as
/// of 2026-09-12 - Razor markup too. The rule always covered runtime strings; markup was the
/// undocumented exception, which is how the portal accumulated 150 em dashes, four ellipses, two
/// arrows, a middle dot and a pencil glyph while every other project stayed clean. An exception
/// nobody wrote down is indistinguishable from a rule nobody follows.
///
/// Comments are deliberately exempt and stay that way. They are for the people reading the source,
/// the codebase uses em dashes in them heavily and well, and holding them to the rule would mean a
/// large diff that improves nothing.
///
/// Scoped to the portal because that is where user-visible text lives and where the drift happened.
/// The mechanical scan in the review covers the rest, and every other project was already clean.
/// </summary>
public sealed class AsciiTextRuleTests
{
    [Fact]
    public void No_user_visible_text_in_the_portal_uses_a_non_ascii_character()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(PortalRoot(), "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                Path.GetExtension(file) is not (".razor" or ".cs"))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            var inBlockComment = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = WithoutComments(lines[i], ref inBlockComment);
                if (i == 0)
                {
                    // A UTF-8 byte-order mark is file ENCODING, not text anybody reads.
                    line = line.TrimStart('﻿');
                }

                var bad = line.Where(c => c > 127).Distinct().ToList();
                if (bad.Count > 0)
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} uses {string.Join(" ", bad.Select(c => $"'{c}' (U+{(int)c:X4})"))}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "User-visible text must be plain ASCII - use \" - \" for a dash, \"...\" for an ellipsis, \"->\" for an arrow. " +
            $"See docs/03-architecture/UI-UX-Design-Spec.md.{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>
    /// The line with its comments removed: Razor's @* *@, C#'s // and /* */. Crude on purpose - it
    /// only has to be right about which text a USER sees, and the one way it can err (treating
    /// something as a comment that is not) makes the guard quieter, never wrong about a real find.
    /// </summary>
    private static string WithoutComments(string line, ref bool inBlockComment)
    {
        var kept = new StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                if (Ahead(line, i, "*@") || Ahead(line, i, "*/"))
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (Ahead(line, i, "@*") || Ahead(line, i, "/*"))
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (Ahead(line, i, "//"))
            {
                break;
            }

            kept.Append(line[i]);
        }

        return kept.ToString();
    }

    private static bool Ahead(string line, int index, string token) =>
        index + token.Length <= line.Length && line.AsSpan(index, token.Length).SequenceEqual(token);

    private static string PortalRoot() => Path.Combine(RepoPaths.RepositoryRoot(), "src", "Enlist.Portal");
}
