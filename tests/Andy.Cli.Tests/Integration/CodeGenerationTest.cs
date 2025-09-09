using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Parsing;
using Andy.Cli.Parsing.Compiler;
using Andy.Cli.Parsing.Parsers;
using Andy.Cli.Parsing.Rendering;
using Andy.Cli.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// Tests code generation scenarios to ensure proper parsing and rendering
/// </summary>
public class CodeGenerationTest
{
    private readonly LlmResponseCompiler _compiler;
    private readonly IJsonRepairService _jsonRepair;
    private readonly ILogger<LlmResponseCompiler> _logger;

    public CodeGenerationTest()
    {
        _jsonRepair = new JsonRepairService();
        _logger = new Mock<ILogger<LlmResponseCompiler>>().Object;
        _compiler = new LlmResponseCompiler("default", _jsonRepair, _logger);
    }

    [Fact]
    public void Should_Parse_CSharp_Code_Without_Hallucination()
    {
        // Arrange - Response for "write a sample C# program"
        var csharpResponse = @"Here is a simple ""Hello World"" C# program:

```csharp
// This is a simple console application.
using System;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine(""Hello, World!""); // Output ""Hello, World!"" to the console.
    }
}
```

This program does the following:

1. It uses the System namespace to use the Console class.
2. It defines a Program class in which it has a Main method.
3. The Main method is the entry point of the program, where it starts executing when you run it.
4. Inside the Main method, it calls Console.WriteLine to print the string ""Hello, World!"" to the console.

To compile and run this program, you can use the command-line compiler, csc.exe, or use Visual Studio.

Compiling with Command Line Tool

Open a terminal, change to the directory containing this file (e.g., ""Andy.Cli/src/Andy.Cli/""), and run the following command to compile it:

```bash
csc Program.cs -out:HelloWorld.exe -target:exe
```

Then, you can run the program from the same directory:

```bash
./HelloWorld.exe
```

And you will see the message ""Hello, World!"" printed in the terminal.";

        // Act
        var result = _compiler.Compile(csharpResponse);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Ast);

        // Should have code blocks
        var codeNodes = result.Ast.Children.OfType<CodeNode>().ToList();
        Assert.NotEmpty(codeNodes);

        // Should have C# code block
        var csharpCode = codeNodes.FirstOrDefault(c => c.Language == "csharp");
        Assert.NotNull(csharpCode);
        Assert.Contains("class Program", csharpCode.Code);
        Assert.Contains("Hello, World!", csharpCode.Code);

        // Should have bash command blocks
        var bashCommands = codeNodes.Where(c => c.Language == "bash").ToList();
        Assert.Equal(2, bashCommands.Count);
        Assert.Contains("csc Program.cs", bashCommands[0].Code);
        Assert.Contains("./HelloWorld.exe", bashCommands[1].Code);

        // Should not have hallucination warnings
        var errors = result.Ast.Children.OfType<ErrorNode>().ToList();
        var hallucinationWarnings = errors.Where(e => e.ErrorCode == "HALLUCINATION_DETECTED").ToList();
        Assert.Empty(hallucinationWarnings);
    }

    [Fact]
    public void Should_Detect_Hallucination_In_Python_Response()
    {
        // Arrange - Response with hallucinated tool results
        var pythonResponseWithHallucination = @"[Tool Results]
{""tool"":""read_file"",""parameters"":{""file_path"":""/Users/samibengrine/Devel/rivoli-ai/andy-cli/src/Andy.Cli/Program.cs""}}

Here's a simple Python program:

```python
#!/usr/bin/env python3

def main():
    print(""Hello, World!"")

if __name__ == ""__main__"":
    main()
```

This is a basic Python program that prints ""Hello, World!"" to the console.";

        // Act
        var result = _compiler.Compile(pythonResponseWithHallucination);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Ast);

        // Should detect hallucination
        var errors = result.Ast.Children.OfType<ErrorNode>().ToList();
        var hallucinationWarnings = errors.Where(e => e.ErrorCode == "HALLUCINATION_DETECTED").ToList();
        Assert.NotEmpty(hallucinationWarnings);

        // Should still extract the Python code
        var codeNodes = result.Ast.Children.OfType<CodeNode>().ToList();
        var pythonCode = codeNodes.FirstOrDefault(c => c.Language == "python");
        Assert.NotNull(pythonCode);
        Assert.Contains("Hello, World!", pythonCode.Code);

        // Should NOT have the fake tool call JSON in the output
        var renderer = new AstRenderer();
        var rendered = renderer.Render(result.Ast);
        Assert.DoesNotContain("[Tool Results]", rendered);
        Assert.DoesNotContain("read_file", rendered);
    }

    [Fact]
    public void Should_Handle_Multiple_Code_Blocks_In_Sequence()
    {
        // Arrange - Response with multiple languages
        var multiLanguageResponse = @"Here are examples in different languages:

First, C#:

```csharp
Console.WriteLine(""Hello from C#"");
```

Then Python:

```python
print(""Hello from Python"")
```

And finally, JavaScript:

```javascript
console.log(""Hello from JavaScript"");
```

All three print a greeting to the console.";

        // Act
        var result = _compiler.Compile(multiLanguageResponse);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Ast);

        // Should have all three code blocks
        var codeNodes = result.Ast.Children.OfType<CodeNode>().ToList();
        Assert.Equal(3, codeNodes.Count);

        // Verify each language
        Assert.Contains(codeNodes, c => c.Language == "csharp" && c.Code.Contains("Hello from C#"));
        Assert.Contains(codeNodes, c => c.Language == "python" && c.Code.Contains("Hello from Python"));
        Assert.Contains(codeNodes, c => c.Language == "javascript" && c.Code.Contains("Hello from JavaScript"));
    }

    [Fact]
    public void Should_Not_Show_Raw_Backticks_In_Rendered_Output()
    {
        // Arrange
        var responseWithCode = @"Here's a simple function:

```python
def greet(name):
    return f""Hello, {name}!""
```

This function takes a name and returns a greeting.";

        // Act
        var result = _compiler.Compile(responseWithCode);
        var renderer = new AstRenderer(new RenderOptions { UseCodeBlockMarkers = false });
        var rendered = renderer.Render(result.Ast);

        // Assert
        // Should not contain raw backticks
        Assert.DoesNotContain("```python", rendered);
        Assert.DoesNotContain("```", rendered);

        // Should contain the code content
        Assert.Contains("def greet(name):", rendered);
        Assert.Contains("Hello, {name}!", rendered);
    }

    [Fact]
    public void Should_Handle_Bash_Tool_Calls_As_Commands()
    {
        // Arrange - Response with bash commands that look like tool calls
        var responseWithBashCommands = @"To run the program, use these commands:

{""tool"":""bash"",""parameters"":{""command"":""python hello.py""}}

Or compile it first:

{""tool"":""bash"",""parameters"":{""command"":""pyinstaller --onefile hello.py""}}

This will create an executable.";

        // Act
        var result = _compiler.Compile(responseWithBashCommands);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Ast);

        // Should extract bash commands as tool calls
        var toolCalls = result.Ast.Children.OfType<ToolCallNode>().ToList();
        Assert.Equal(2, toolCalls.Count);
        Assert.All(toolCalls, tc => Assert.Equal("bash", tc.ToolName));

        // Verify commands are captured
        var firstCommand = toolCalls[0].Arguments["command"]?.ToString();
        Assert.Equal("python hello.py", firstCommand);

        var secondCommand = toolCalls[1].Arguments["command"]?.ToString();
        Assert.Equal("pyinstaller --onefile hello.py", secondCommand);
    }
}