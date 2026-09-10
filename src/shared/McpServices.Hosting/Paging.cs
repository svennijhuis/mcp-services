using System.Text;

namespace McpServices.Hosting;

/// <summary>One page of results. <see cref="NextPageToken"/> is null on the last page.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, string? NextPageToken)
{
    public bool Truncated => NextPageToken is not null;
}

/// <summary>Opaque offset-based page tokens so every list-returning tool paginates the same way.</summary>
public static class Paging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    public static PagedResult<T> Page<T>(IReadOnlyList<T> all, string? pageToken, int? pageSize)
    {
        ArgumentNullException.ThrowIfNull(all);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var offset = DecodeOffset(pageToken);
        if (offset > all.Count)
        {
            offset = all.Count;
        }

        var items = all.Skip(offset).Take(size).ToList();
        var next = offset + size < all.Count ? EncodeOffset(offset + size) : null;
        return new PagedResult<T>(items, all.Count, next);
    }

    public static int DecodeOffset(string? pageToken)
    {
        if (string.IsNullOrWhiteSpace(pageToken))
        {
            return 0;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(pageToken));
            return text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text.AsSpan(2), out var offset) && offset >= 0
                ? offset
                : throw new ToolException("Invalid pageToken.");
        }
        catch (FormatException)
        {
            throw new ToolException("Invalid pageToken.");
        }
    }

    public static string EncodeOffset(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"o:{offset}"));
}
