using McpServices.Hosting;

namespace McpServices.Hosting.Tests;

public class CommandLineTests
{
    [Fact]
    public void Parses_options_flags_and_positionals()
    {
        var cl = CommandLine.Parse(["--http", "--port", "5200", "--db=main=sqlite:x.db", "--db", "other=postgres:y", "/tmp/a", "--", "--not-an-option"], "http");

        Assert.True(cl.HasFlag("http"));
        Assert.Equal(5200, cl.GetInt("port", 0));
        Assert.Equal(["main=sqlite:x.db", "other=postgres:y"], cl.GetOptions("db"));
        Assert.Equal(["/tmp/a", "--not-an-option"], cl.Positionals);
    }

    [Fact]
    public void Unknown_flag_followed_by_value_is_option()
    {
        var cl = CommandLine.Parse(["--log-level", "debug", "--verbose"]);
        Assert.Equal("debug", cl.GetOption("log-level"));
        Assert.True(cl.HasFlag("verbose"));
        Assert.Null(cl.GetOption("verbose"));
    }

    [Fact]
    public void GetBool_accepts_flag_and_values()
    {
        Assert.True(CommandLine.Parse(["--watch"]).GetBool("watch", false));
        Assert.True(CommandLine.Parse(["--watch=true"]).GetBool("watch", false));
        Assert.False(CommandLine.Parse(["--watch=false"]).GetBool("watch", true));
        Assert.True(CommandLine.Parse([]).GetBool("watch", true));
    }
}

public class PagingTests
{
    [Fact]
    public void Pages_and_round_trips_tokens()
    {
        var items = Enumerable.Range(0, 120).ToList();
        var first = Paging.Page(items, null, 50);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal(120, first.TotalCount);
        Assert.True(first.Truncated);

        var second = Paging.Page(items, first.NextPageToken, 50);
        Assert.Equal(50, second.Items[0]);

        var third = Paging.Page(items, second.NextPageToken, 50);
        Assert.Equal(20, third.Items.Count);
        Assert.Null(third.NextPageToken);
    }

    [Fact]
    public void Rejects_garbage_tokens()
    {
        Assert.Throws<ToolException>(() => Paging.Page([1, 2, 3], "not-base64!", 10));
        Assert.Throws<ToolException>(() => Paging.Page([1, 2, 3], Convert.ToBase64String("x:1"u8.ToArray()), 10));
    }

    [Fact]
    public void Clamps_page_size()
    {
        var page = Paging.Page(Enumerable.Range(0, 1000).ToList(), null, 10_000);
        Assert.Equal(Paging.MaxPageSize, page.Items.Count);
    }
}
