using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Services.Formatting;
using Xunit;

namespace Andy.Cli.Tests.Commands;

/// <summary>
/// <c>/formatters status</c> exists to answer "why did (or did not) a formatter run on this file",
/// so these tests assert on the explanation, not just on the exit status.
/// </summary>
public class FormattersCommandTests
{
    private const string Root = "/projects/demo";

    private static FormatterDefinition Def(
        string name, string command, int order = 100, bool enabled = true, params string[] extensions)
        => new()
        {
            Name = name,
            Command = command,
            Order = order,
            Enabled = enabled,
            Extensions = extensions.Length == 0 ? new[] { ".cs" } : extensions,
        };

    private static FormattersCommand Command(IEnumerable<string> installed, params FormatterDefinition[] definitions)
    {
        var set = new HashSet<string>(installed, StringComparer.Ordinal);
        return new FormattersCommand(
            Root,
            _ => new FormatterCatalog(definitions, command => set.Contains(command) ? "/usr/bin/" + command : null));
    }

    [Fact]
    public async Task Status_ListsTheMatchingFormattersInRunOrder()
    {
        var command = Command(
            new[] { "a", "b" },
            Def("second", "b", order: 20),
            Def("first", "a", order: 10));

        var result = await command.ExecuteAsync(new[] { "status", "src/Thing.cs" });

        Assert.True(result.Success);
        Assert.Contains("1. first", result.Message);
        Assert.Contains("2. second", result.Message);
        Assert.True(
            result.Message.IndexOf("1. first", StringComparison.Ordinal)
                < result.Message.IndexOf("2. second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_ExplainsWhyAFormatterMatched()
    {
        var command = Command(new[] { "csfmt" }, Def("cs", "csfmt", order: 5) with { Source = FormatterSource.Project });

        var result = await command.ExecuteAsync(new[] { "status", "src/Thing.cs" });

        Assert.Contains("matched on extension", result.Message);
        Assert.Contains("project configuration", result.Message);
        Assert.Contains("/usr/bin/csfmt", result.Message);
        Assert.Contains("order 5", result.Message);
    }

    [Fact]
    public async Task Status_ExplainsWhyAFormatterWasSkipped()
    {
        var command = Command(
            Array.Empty<string>(),
            Def("missing", "csfmt"),
            Def("other", "gofmt", extensions: ".go"),
            Def("off", "offfmt", enabled: false));

        var result = await command.ExecuteAsync(new[] { "status", "src/Thing.cs" });

        Assert.Contains("Nothing will run for this file.", result.Message);
        Assert.Contains("not found on PATH", result.Message);
        Assert.Contains("does not handle this file", result.Message);
        Assert.Contains("disabled", result.Message);
    }

    [Fact]
    public async Task Status_ShowsTheCommandLineThatWouldRun()
    {
        var definition = Def("cs", "csfmt") with { Arguments = new[] { "--write", "$FILE" } };
        var command = Command(new[] { "csfmt" }, definition);

        var result = await command.ExecuteAsync(new[] { "status", "src/Thing.cs" });

        Assert.Contains("csfmt --write", result.Message);
        Assert.Contains(Path.Combine(Root, "src", "Thing.cs"), result.Message);
    }

    [Fact]
    public async Task ABarePathIsTreatedAsStatus()
    {
        var command = Command(new[] { "csfmt" }, Def("cs", "csfmt"));

        var result = await command.ExecuteAsync(new[] { "src/Thing.cs" });

        Assert.Contains("Will run", result.Message);
    }

    [Fact]
    public async Task List_ShowsEveryDefinitionWithItsState()
    {
        var command = Command(
            new[] { "csfmt" },
            Def("ready", "csfmt"),
            Def("uninstalled", "nope"),
            Def("off", "csfmt", enabled: false));

        var result = await command.ExecuteAsync(new[] { "list" });

        Assert.Contains("ready", result.Message);
        Assert.Contains("not installed", result.Message);
        Assert.Contains("disabled", result.Message);
    }

    [Fact]
    public async Task StatusWithoutAFile_FallsBackToTheList()
    {
        var command = Command(new[] { "csfmt" }, Def("cs", "csfmt"));

        var result = await command.ExecuteAsync(new[] { "status" });

        Assert.Contains("Configured formatters", result.Message);
    }

    [Fact]
    public async Task Path_ShowsBothConfigurationLayers_AndTheNoInstallPromise()
    {
        var command = Command(Array.Empty<string>());

        var result = await command.ExecuteAsync(new[] { "path" });

        Assert.Contains(FormatterConfigLoader.ProjectPath(Root), result.Message);
        Assert.Contains("never installs", result.Message);
    }

    [Fact]
    public async Task Help_DocumentsTheSubcommandsAndTheConfigShape()
    {
        var command = Command(Array.Empty<string>());

        var result = await command.ExecuteAsync(new[] { "help" });

        Assert.Contains("formatters status <file>", result.Message);
        Assert.Contains("$FILE", result.Message);
    }

    [Fact]
    public async Task EveryOutput_IsAsciiOnly()
    {
        // Project rule: ASCII only in terminal UI text.
        var command = Command(new[] { "csfmt" }, Def("cs", "csfmt"));
        foreach (var args in new[]
        {
            new[] { "status", "src/Thing.cs" }, new[] { "list" }, new[] { "path" }, new[] { "help" },
        })
        {
            var message = (await command.ExecuteAsync(args)).Message;
            var offenders = message.Where(ch => ch != '\n' && (ch < ' ' || ch > '~')).Distinct().ToArray();
            Assert.True(offenders.Length == 0,
                "Non-ASCII characters in formatters output: " + string.Join(", ", offenders.Select(c => $"U+{(int)c:X4}")));
        }
    }

    [Fact]
    public void TheCommandIsInTheSlashCatalog()
    {
        var names = SlashCommandCatalog.CreateInlineHelpCommands().Select(c => c.Name).ToArray();
        Assert.Contains("formatters", names);
    }
}
