using McpServices.FileSystem;
using McpServices.Hosting;

namespace McpServices.FileSystem.Tests;

public class FileEditorTests
{
    [Fact]
    public void Applies_exact_match()
    {
        var result = FileEditor.Apply("a\nb\nc\n", [new TextEdit("b", "B")], "f.txt");

        Assert.Equal("a\nB\nc\n", result.NewContent);
        Assert.Contains("-b", result.Diff, StringComparison.Ordinal);
        Assert.Contains("+B", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public void Falls_back_to_whitespace_insensitive_match_and_keeps_indentation()
    {
        const string original = "class A\n{\n    void M()\n    {\n        Foo();\n    }\n}\n";
        var result = FileEditor.Apply(original, [new TextEdit("void M()\n{\n    Foo();\n}", "void M()\n{\n    Bar();\n    Baz();\n}")], "A.cs");

        Assert.Equal("class A\n{\n    void M()\n    {\n        Bar();\n        Baz();\n    }\n}\n", result.NewContent);
    }

    [Fact]
    public void Rejects_ambiguous_match()
    {
        var ex = Assert.Throws<ToolException>(() => FileEditor.Apply("x\ny\nx\n", [new TextEdit("x", "z")], "f"));
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_missing_match()
    {
        var ex = Assert.Throws<ToolException>(() => FileEditor.Apply("x\ny\n", [new TextEdit("q", "z")], "f"));
        Assert.Contains("could not find", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preserves_crlf_line_endings()
    {
        var result = FileEditor.Apply("a\r\nb\r\n", [new TextEdit("b", "c")], "f");
        Assert.Equal("a\r\nc\r\n", result.NewContent);
    }

    [Fact]
    public void Applies_multiple_edits_in_order()
    {
        var result = FileEditor.Apply("one\ntwo\nthree\n", [new TextEdit("one", "1"), new TextEdit("three", "3")], "f");
        Assert.Equal("1\ntwo\n3\n", result.NewContent);
        Assert.Equal(2, result.AppliedEdits);
    }

    [Fact]
    public void Diff_is_empty_when_nothing_changes()
    {
        var result = FileEditor.Apply("same\n", [new TextEdit("same", "same")], "f");
        Assert.Equal(string.Empty, result.Diff);
    }
}
