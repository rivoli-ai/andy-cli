using Andy.Cli.Services.ContentPipeline;
using Xunit;

namespace Andy.Cli.Tests.Services.ContentPipeline;

/// <summary>
/// Golden tests for #178: structural (not phrase-based) tool-call filtering plus Markdown
/// fidelity. Prose that mentions "tool call" or a registered tool name must survive intact;
/// only confirmed protocol/tool-call envelopes are stripped.
/// </summary>
public class SanitizerStructuralFilteringTests
{
    private readonly TextContentSanitizer _sanitizer = new();

    private string SanitizeText(string content)
    {
        var result = _sanitizer.Sanitize(new TextBlock("test", content)) as TextBlock;
        Assert.NotNull(result);
        return result!.Content;
    }

    [Fact]
    public void Preserves_Prose_Containing_The_Phrase_Tool_Call()
    {
        var input = "I will now make a tool call to gather the data you asked about.";
        Assert.Equal(input, SanitizeText(input));
    }

    [Fact]
    public void Preserves_Prose_Mentioning_Registered_Tool_Names()
    {
        var input = "You can use the list_directory tool or read_file to inspect the project, "
                    + "and execute_command runs shell commands.";
        Assert.Equal(input, SanitizeText(input));
    }

    [Fact]
    public void Preserves_Prose_With_Words_Invoke_And_Function_Call()
    {
        var input = "To invoke the helper, call the function that wraps the API. "
                    + "This function call returns a promise.";
        Assert.Equal(input, SanitizeText(input));
    }

    [Fact]
    public void Preserves_Multi_Paragraph_Markdown_Spacing()
    {
        var input = "# Heading\n\nFirst paragraph explaining the plan.\n\nSecond paragraph with more detail.";
        Assert.Equal(input, SanitizeText(input));
    }

    [Fact]
    public void Preserves_Markdown_List()
    {
        var input = "Steps:\n\n- First step uses read_file\n- Second step uses execute_command\n- Third step is done";
        Assert.Equal(input, SanitizeText(input));
    }

    [Fact]
    public void Preserves_Markdown_Table()
    {
        var input = "| Tool | Purpose |\n| --- | --- |\n| read_file | reads a file |\n| list_directory | lists a directory |";
        Assert.Equal(input, SanitizeText(input));
    }

    [Fact]
    public void Preserves_Code_Fence_In_Text_Block()
    {
        var input = "Example:\n\n```csharp\nvar tool = new Tool();\n```\n\nThat calls the tool constructor.";
        Assert.Equal(input, SanitizeText(input));
    }

    [Fact]
    public void Preserves_Non_ToolCall_Json_In_Prose()
    {
        // A JSON object that is NOT a tool-call envelope (name but no arguments) must survive.
        var input = "Here is a record: {\"name\": \"Alice\", \"age\": 30} for reference.";
        Assert.Equal(input, SanitizeText(input));
    }

    [Fact]
    public void Strips_Xml_Tool_Call_Envelope()
    {
        var input = "Let me check that.<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.txt\"}}</tool_call>";
        var output = SanitizeText(input);
        Assert.Equal("Let me check that.", output);
        Assert.DoesNotContain("tool_call", output);
        Assert.DoesNotContain("arguments", output);
    }

    [Fact]
    public void Strips_Json_Tool_Call_Envelope_With_Name_And_Arguments()
    {
        var input = "Working on it. {\"name\": \"execute_command\", \"arguments\": {\"command\": \"ls -la\"}} Done.";
        var output = SanitizeText(input);
        Assert.DoesNotContain("execute_command\", \"arguments", output);
        Assert.DoesNotContain("\"arguments\"", output);
        Assert.Contains("Working on it.", output);
        Assert.Contains("Done.", output);
    }

    [Fact]
    public void Strips_Json_Envelope_With_ToolCall_Key()
    {
        var input = "Prefix {\"tool_call\": {\"name\": \"list_directory\"}} suffix";
        var output = SanitizeText(input);
        Assert.DoesNotContain("tool_call", output);
        Assert.Contains("Prefix", output);
        Assert.Contains("suffix", output);
    }

    [Fact]
    public void Strips_Fenced_Tool_Call_Block()
    {
        var input = "Thinking...\n\n```tool_call\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"x\"}}\n```\n\nContinuing.";
        var output = SanitizeText(input);
        Assert.DoesNotContain("tool_call", output);
        Assert.DoesNotContain("arguments", output);
        Assert.Contains("Thinking...", output);
        Assert.Contains("Continuing.", output);
    }

    [Fact]
    public void Drops_Code_Block_Whose_Language_Is_Tool_Call()
    {
        var block = new CodeBlock("c1", "{\"name\": \"read_file\", \"arguments\": {}}", "tool_call");
        var result = _sanitizer.Sanitize(block) as CodeBlock;
        Assert.NotNull(result);
        Assert.False(result!.IsComplete);
    }

    [Fact]
    public void Keeps_Ordinary_Code_Block()
    {
        var block = new CodeBlock("c1", "var x = 5;", "csharp");
        var result = _sanitizer.Sanitize(block) as CodeBlock;
        Assert.NotNull(result);
        Assert.True(result!.IsComplete);
        Assert.Equal("var x = 5;", result.Code);
    }
}
