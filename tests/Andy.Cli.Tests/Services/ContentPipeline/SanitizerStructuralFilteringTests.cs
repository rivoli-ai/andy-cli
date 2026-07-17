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

    // ---- Review fixes for #178 ----

    // Fix 1: a <tool_call> ... </tool_call> tag split across two streamed blocks must never leak
    // its raw protocol tag or JSON fragment. The same sanitizer instance processes both blocks in
    // arrival order (as the pipeline's single consumer does).
    [Fact]
    public void Streaming_Split_Tool_Call_Tag_Does_Not_Leak_Across_Blocks()
    {
        var block1 = "Let me help you.\n<tool_call>\n{\"name\": \"read_file\", \"argum";
        var block2 = "ents\": {\"path\": \"a.txt\"}}\n</tool_call>\nAll done.";

        var out1 = SanitizeText(block1);
        var out2 = SanitizeText(block2);

        // The opening marker and the partial JSON are suppressed in the first block.
        Assert.Equal("Let me help you.", out1);
        Assert.DoesNotContain("tool_call", out1);
        Assert.DoesNotContain("read_file", out1);

        // The continuation up to the close marker is suppressed; only the trailing prose renders.
        Assert.Equal("All done.", out2);
        Assert.DoesNotContain("tool_call", out2);
        Assert.DoesNotContain("arguments", out2);
        Assert.DoesNotContain("path", out2);
    }

    // Fix 1: a tool call whose body spans a middle block (no markers at all in that block) stays
    // suppressed until the close arrives.
    [Fact]
    public void Streaming_Tool_Call_Body_Spanning_Three_Blocks_Is_Suppressed()
    {
        Assert.Equal("Start.", SanitizeText("Start.<tool_call>{\"name\": \"x\","));
        Assert.Equal("", SanitizeText("\"arguments\": {\"a\": 1,"));
        Assert.Equal("End.", SanitizeText("\"b\": 2}}</tool_call>End."));
    }

    // Fix 2: an inline function-schema JSON literal (name + "parameters") that a user is asking
    // about is preserved exactly - it is not a concrete invocation.
    [Fact]
    public void Preserves_Inline_Function_Schema_Json_In_Prose()
    {
        var input = "The schema is {\"name\": \"get_weather\", \"parameters\": {\"location\": \"string\"}} for reference.";
        Assert.Equal(input, SanitizeText(input));
    }

    // Fix 2/3: a stray UNBALANCED '{' in prose before a genuine envelope must neither eat the
    // prose nor stop the later envelope from being stripped.
    [Fact]
    public void Unbalanced_Brace_In_Prose_Preserved_And_Later_Envelope_Stripped()
    {
        var input = "Use { here and {\"tool_call\": {\"name\": \"x\"}} done.";
        Assert.Equal("Use { here and done.", SanitizeText(input));
    }

    // Fix 3: a balanced but non-envelope brace span in prose is preserved while a later real
    // envelope is stripped.
    [Fact]
    public void Stray_Balanced_Braces_Preserved_And_Later_Envelope_Stripped()
    {
        var input = "Use {braces} and {\"tool_call\": {\"name\": \"x\"}} here.";
        Assert.Equal("Use {braces} and here.", SanitizeText(input));
    }

    // Fix 4: a bare single-key tool marker (no arguments) is a protocol artifact and is stripped
    // when it stands alone on its own line.
    [Fact]
    public void Strips_Standalone_Bare_Tool_Marker()
    {
        var input = "Result:\n{\"tool\": \"read_file\"}\nDone.";
        Assert.Equal("Result:\nDone.", SanitizeText(input));
    }

    [Fact]
    public void Strips_Standalone_Bare_Function_Marker()
    {
        var input = "Result:\n{\"function\": \"x\"}\nDone.";
        Assert.Equal("Result:\nDone.", SanitizeText(input));
    }

    // Fix 4: the SAME bare marker inside a sentence is preserved (it may be JSON a user discusses).
    [Fact]
    public void Preserves_Inline_Bare_Tool_Marker_In_Prose()
    {
        var input = "The object {\"tool\": \"read_file\"} is what we send.";
        Assert.Equal(input, SanitizeText(input));
    }

    // Fix 5: removing a standalone envelope leaves no blank line behind.
    [Fact]
    public void Standalone_Envelope_Removal_Leaves_No_Blank_Line()
    {
        var input = "Here:\n{\"tool_call\": {\"name\": \"x\", \"arguments\": {}}}\nThere.";
        Assert.Equal("Here:\nThere.", SanitizeText(input));
    }

    // Fix 5: removing an inline envelope collapses the surrounding whitespace to a single space,
    // not a double space.
    [Fact]
    public void Inline_Envelope_Removal_Collapses_To_Single_Space()
    {
        var input = "Working on it. {\"name\": \"execute_command\", \"arguments\": {\"command\": \"ls\"}} Done.";
        Assert.Equal("Working on it. Done.", SanitizeText(input));
    }

    // Fix 3: a genuine envelope is still stripped even when a truly-unbalanced earlier '{' would
    // previously have caused the scanner to bail out and leak it.
    [Fact]
    public void Standalone_Parameters_Invocation_Is_Stripped()
    {
        var input = "Plan:\n{\"tool\": \"format_text\", \"parameters\": {\"op\": \"sort\"}}\nContinuing.";
        Assert.Equal("Plan:\nContinuing.", SanitizeText(input));
    }
}
