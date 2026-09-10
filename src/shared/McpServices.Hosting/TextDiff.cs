using System.Text;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace McpServices.Hosting;

/// <summary>Unified diff rendering shared by every tool that previews a change with <c>dryRun</c>.</summary>
public static class TextDiff
{
    public static string Unified(string before, string after, string path, int context = 3)
    {
        var model = InlineDiffBuilder.Diff(before, after, ignoreWhiteSpace: false, ignoreCase: false);
        if (!model.HasDifferences)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("--- ").Append(path).Append('\n');
        sb.Append("+++ ").Append(path).Append('\n');

        var lines = model.Lines;
        var i = 0;
        var oldLine = 1;
        var newLine = 1;
        while (i < lines.Count)
        {
            if (lines[i].Type == ChangeType.Unchanged)
            {
                oldLine++;
                newLine++;
                i++;
                continue;
            }

            var hunkStart = Math.Max(0, i - context);
            var hunkEnd = i;
            var lastChange = i;
            while (hunkEnd < lines.Count && hunkEnd - lastChange <= context)
            {
                if (lines[hunkEnd].Type != ChangeType.Unchanged)
                {
                    lastChange = hunkEnd;
                }

                hunkEnd++;
            }

            hunkEnd = Math.Min(lines.Count, lastChange + context + 1);

            var oldStart = oldLine - (i - hunkStart);
            var newStart = newLine - (i - hunkStart);
            var oldCount = 0;
            var newCount = 0;
            var body = new StringBuilder();
            for (var k = hunkStart; k < hunkEnd; k++)
            {
                var line = lines[k];
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        body.Append('+').Append(line.Text).Append('\n');
                        newCount++;
                        break;
                    case ChangeType.Deleted:
                        body.Append('-').Append(line.Text).Append('\n');
                        oldCount++;
                        break;
                    default:
                        body.Append(' ').Append(line.Text).Append('\n');
                        oldCount++;
                        newCount++;
                        break;
                }
            }

            sb.Append("@@ -").Append(oldStart).Append(',').Append(oldCount).Append(" +").Append(newStart).Append(',').Append(newCount).Append(" @@\n");
            sb.Append(body);

            for (var k = i; k < hunkEnd; k++)
            {
                switch (lines[k].Type)
                {
                    case ChangeType.Inserted: newLine++; break;
                    case ChangeType.Deleted: oldLine++; break;
                    default: oldLine++; newLine++; break;
                }
            }

            i = hunkEnd;
        }

        return sb.ToString();
    }
}
