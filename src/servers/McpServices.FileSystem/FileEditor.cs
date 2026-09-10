using System.Text;
using McpServices.Hosting;

namespace McpServices.FileSystem;

public sealed record TextEdit(string OldText, string NewText);

public sealed record EditResult(string NewContent, string Diff, int AppliedEdits);

/// <summary>
/// Applies line-oriented text edits. Each edit is matched exactly first; when that fails the
/// match is retried line by line with whitespace normalized, preserving the indentation found
/// in the file. Produces a unified diff so callers can preview with <c>dryRun</c>.
/// </summary>
public static class FileEditor
{
    public static EditResult Apply(string original, IReadOnlyList<TextEdit> edits, string displayPath)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            throw new ToolException("At least one edit is required.");
        }

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var content = Normalize(original);

        foreach (var (index, edit) in edits.Index())
        {
            var oldText = Normalize(edit.OldText ?? string.Empty);
            var newText = Normalize(edit.NewText ?? string.Empty);
            if (oldText.Length == 0)
            {
                throw new ToolException($"Edit #{index + 1}: oldText must not be empty.");
            }

            var position = content.IndexOf(oldText, StringComparison.Ordinal);
            if (position >= 0)
            {
                if (content.IndexOf(oldText, position + 1, StringComparison.Ordinal) >= 0)
                {
                    throw new ToolException($"Edit #{index + 1}: oldText matches more than once; include more surrounding lines to make it unique.");
                }

                content = string.Concat(content.AsSpan(0, position), newText, content.AsSpan(position + oldText.Length));
                continue;
            }

            content = ApplyFlexible(content, oldText, newText, index + 1);
        }

        var finalContent = newline == "\n" ? content : content.Replace("\n", newline, StringComparison.Ordinal);
        var diff = UnifiedDiff(Normalize(original), content, displayPath);
        return new EditResult(finalContent, diff, edits.Count);
    }

    private static string ApplyFlexible(string content, string oldText, string newText, int editNumber)
    {
        var contentLines = content.Split('\n');
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');

        var matchStart = -1;
        for (var i = 0; i <= contentLines.Length - oldLines.Length; i++)
        {
            var matches = true;
            for (var j = 0; j < oldLines.Length; j++)
            {
                if (!contentLines[i + j].Trim().Equals(oldLines[j].Trim(), StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                if (matchStart >= 0)
                {
                    throw new ToolException($"Edit #{editNumber}: oldText matches more than once (ignoring whitespace); include more surrounding lines.");
                }

                matchStart = i;
            }
        }

        if (matchStart < 0)
        {
            throw new ToolException($"Edit #{editNumber}: could not find oldText in the file, even ignoring leading/trailing whitespace. Read the file again and copy the exact text.");
        }

        var originalIndent = LeadingWhitespace(contentLines[matchStart]);
        var firstOldIndent = LeadingWhitespace(oldLines[0]);
        var replacement = new List<string>(newLines.Length);
        for (var j = 0; j < newLines.Length; j++)
        {
            var line = newLines[j];
            if (j == 0)
            {
                replacement.Add(originalIndent + line.TrimStart());
                continue;
            }

            // Keep the relative indentation of the replacement, re-based on the file's indentation.
            var newIndent = LeadingWhitespace(line);
            var relative = Math.Max(0, newIndent.Length - firstOldIndent.Length);
            replacement.Add(line.Length == 0 ? string.Empty : originalIndent + new string(' ', relative) + line.TrimStart());
        }

        var result = new List<string>(contentLines.Length - oldLines.Length + replacement.Count);
        result.AddRange(contentLines[..matchStart]);
        result.AddRange(replacement);
        result.AddRange(contentLines[(matchStart + oldLines.Length)..]);
        return string.Join('\n', result);
    }

    public static string UnifiedDiff(string before, string after, string path, int context = 3) =>
        TextDiff.Unified(before, after, path, context);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string LeadingWhitespace(string line)
    {
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }

        return line[..count];
    }
}
