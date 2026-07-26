using System;
using System.IO;
using System.Linq;
using Andy.Cli.Services.Formatting;
using Xunit;

namespace Andy.Cli.Tests.Services.Formatting;

public class FormatterConfigLoaderTests
{
    [Fact]
    public void Parse_ReadsEveryDefinitionField()
    {
        const string json = """
        {
          "formatters": {
            "csharpier": {
              "command": "csharpier",
              "arguments": ["format", "$FILE"],
              "extensions": [".cs", "csx"],
              "workingDirectory": "src",
              "timeoutSeconds": 45,
              "enabled": false,
              "order": 7
            }
          }
        }
        """;

        var definitions = FormatterConfigLoader.Parse(json, FormatterSource.Project, out var error);

        Assert.Null(error);
        var definition = Assert.Single(definitions);
        Assert.Equal("csharpier", definition.Name);
        Assert.Equal("csharpier", definition.Command);
        Assert.Equal(new[] { "format", "$FILE" }, definition.Arguments);
        Assert.Equal(new[] { ".cs", "csx" }, definition.Extensions);
        Assert.Equal("src", definition.WorkingDirectory);
        Assert.Equal(45, definition.TimeoutSeconds);
        Assert.False(definition.Enabled);
        Assert.Equal(7, definition.Order);
        Assert.Equal(FormatterSource.Project, definition.Source);
    }

    [Fact]
    public void Parse_MalformedJson_YieldsNoDefinitionsAndReportsError()
    {
        var definitions = FormatterConfigLoader.Parse("{ not json", FormatterSource.User, out var error);

        Assert.Empty(definitions);
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_EntryWithoutCommand_IsRejectedRatherThanGuessed()
    {
        const string json = """{ "formatters": { "broken": { "extensions": [".cs"] } } }""";

        var definitions = FormatterConfigLoader.Parse(json, FormatterSource.Project, out var error);

        Assert.Empty(definitions);
        Assert.Contains("command", error);
    }

    [Fact]
    public void Parse_AcceptsABareStringWhereAListIsExpected()
    {
        const string json = """
        { "formatters": { "gofmt": { "command": "gofmt", "extensions": ".go", "arguments": "-w" } } }
        """;

        var definition = Assert.Single(FormatterConfigLoader.Parse(json, FormatterSource.User, out _));
        Assert.Equal(new[] { ".go" }, definition.Extensions);
        Assert.Equal(new[] { "-w" }, definition.Arguments);
    }

    [Fact]
    public void Merge_ProjectLayerReplacesUserLayerOfTheSameName()
    {
        var user = new[]
        {
            new FormatterDefinition { Name = "fmt", Command = "user-fmt", Extensions = new[] { ".cs" }, Source = FormatterSource.User },
        };
        var project = new[]
        {
            new FormatterDefinition { Name = "fmt", Command = "project-fmt", Extensions = new[] { ".cs" }, Source = FormatterSource.Project },
        };

        var merged = FormatterConfigLoader.Merge(new[] { (System.Collections.Generic.IReadOnlyList<FormatterDefinition>)user, project });

        var definition = Assert.Single(merged);
        Assert.Equal("project-fmt", definition.Command);
        Assert.Equal(FormatterSource.Project, definition.Source);
    }

    [Fact]
    public void Merge_ProducesADeterministicOrderIndependentOfInputOrder()
    {
        var a = new FormatterDefinition { Name = "zzz", Command = "z", Order = 10 };
        var b = new FormatterDefinition { Name = "aaa", Command = "a", Order = 10 };
        var c = new FormatterDefinition { Name = "mmm", Command = "m", Order = 1 };

        var first = FormatterConfigLoader.Merge(new[] { new[] { a, b, c }.AsReadOnlyList() });
        var second = FormatterConfigLoader.Merge(new[] { new[] { c, a, b }.AsReadOnlyList() });

        Assert.Equal(new[] { "mmm", "aaa", "zzz" }, first.Select(d => d.Name));
        Assert.Equal(first.Select(d => d.Name), second.Select(d => d.Name));
    }

    [Fact]
    public void Load_ReadsTheProjectFileAndSkipsDetectedDefaultsWhenAsked()
    {
        var root = Path.Combine(Path.GetTempPath(), "andy-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".andy"));
        try
        {
            File.WriteAllText(
                FormatterConfigLoader.ProjectPath(root),
                """{ "formatters": { "local": { "command": "local-fmt", "extensions": [".cs"] } } }""");

            var definitions = FormatterConfigLoader.Load(root, includeDetectedDefaults: false);

            Assert.Contains(definitions, d => d.Name == "local" && d.Source == FormatterSource.Project);
            Assert.DoesNotContain(definitions, d => d.Source == FormatterSource.Detected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DetectedDefaults_NeverImplyInstallation_TheyOnlyDescribeCommands()
    {
        // Every detected default must declare at least one extension and a command; nothing here
        // is allowed to be a catch-all, and none of it is installed by Andy.
        Assert.NotEmpty(FormatterConfigLoader.DetectedDefaults);
        Assert.All(FormatterConfigLoader.DetectedDefaults, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Command));
            Assert.NotEmpty(d.Extensions);
            Assert.Equal(FormatterSource.Detected, d.Source);
        });
    }
}

internal static class ReadOnlyListExtensions
{
    public static System.Collections.Generic.IReadOnlyList<T> AsReadOnlyList<T>(this T[] items) => items;
}
