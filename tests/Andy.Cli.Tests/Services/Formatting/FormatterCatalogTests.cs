using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.Formatting;
using Xunit;

namespace Andy.Cli.Tests.Services.Formatting;

public class FormatterCatalogTests
{
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

    private static FormatterCatalog Catalog(IEnumerable<string> installed, params FormatterDefinition[] definitions)
    {
        var set = new HashSet<string>(installed, StringComparer.Ordinal);
        return new FormatterCatalog(definitions, command => set.Contains(command) ? "/usr/bin/" + command : null);
    }

    [Fact]
    public void SelectFor_OrdersByOrderThenNameOrdinal_RegardlessOfDefinitionOrder()
    {
        var catalog = Catalog(
            new[] { "b", "a", "c" },
            Def("zeta", "a", order: 5),
            Def("alpha", "b", order: 5),
            Def("mid", "c", order: 1));

        var selected = catalog.SelectFor("/tmp/File.cs").Select(m => m.Definition.Name).ToArray();

        Assert.Equal(new[] { "mid", "alpha", "zeta" }, selected);
    }

    [Fact]
    public void SelectFor_IsStableAcrossRepeatedCalls()
    {
        var catalog = Catalog(
            new[] { "x", "y" },
            Def("one", "x", order: 10),
            Def("two", "y", order: 10));

        var first = catalog.SelectFor("/tmp/a.cs").Select(m => m.Definition.Name).ToArray();
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(first, catalog.SelectFor("/tmp/a.cs").Select(m => m.Definition.Name).ToArray());
        }
    }

    [Fact]
    public void SelectFor_SkipsFormattersThatDoNotDeclareTheExtension()
    {
        var catalog = Catalog(new[] { "csfmt", "gofmt" },
            Def("cs", "csfmt", extensions: ".cs"),
            Def("go", "gofmt", extensions: ".go"));

        Assert.Equal(new[] { "cs" }, catalog.SelectFor("/tmp/a.cs").Select(m => m.Definition.Name));
        Assert.Equal(new[] { "go" }, catalog.SelectFor("/tmp/a.go").Select(m => m.Definition.Name));
        Assert.Empty(catalog.SelectFor("/tmp/a.txt"));
    }

    [Fact]
    public void SelectFor_SkipsDisabledFormatters_AndExplainsWhy()
    {
        var catalog = Catalog(new[] { "csfmt" }, Def("cs", "csfmt", enabled: false));

        Assert.Empty(catalog.SelectFor("/tmp/a.cs"));

        var match = catalog.Explain("/tmp/a.cs").Single();
        Assert.Equal(FormatterMatchState.Disabled, match.State);
        Assert.Contains("disabled", match.Reason);
    }

    [Fact]
    public void SelectFor_SkipsFormattersWhoseCommandIsNotInstalled_AndSaysAndyNeverInstalls()
    {
        var catalog = Catalog(Array.Empty<string>(), Def("cs", "csfmt"));

        Assert.Empty(catalog.SelectFor("/tmp/a.cs"));

        var match = catalog.Explain("/tmp/a.cs").Single();
        Assert.Equal(FormatterMatchState.CommandNotFound, match.State);
        Assert.Contains("not found on PATH", match.Reason);
        Assert.Contains("never installs", match.Reason);
    }

    [Fact]
    public void Explain_PutsRunnableFormattersFirst_InExecutionOrder()
    {
        var catalog = Catalog(new[] { "b" },
            Def("missing", "a", order: 1),
            Def("ready", "b", order: 2),
            Def("other-ext", "b", order: 3, extensions: ".go"));

        var explained = catalog.Explain("/tmp/a.cs");

        Assert.Equal("ready", explained[0].Definition.Name);
        Assert.True(explained[0].WillRun);
        Assert.False(explained[1].WillRun);
    }

    [Fact]
    public void Explain_ReportsTheConfigLayerThatDefinedTheFormatter()
    {
        var definition = Def("cs", "csfmt") with { Source = FormatterSource.User };
        var catalog = Catalog(new[] { "csfmt" }, definition);

        Assert.Contains("user configuration", catalog.Explain("/tmp/a.cs").Single().Reason);
    }

    [Fact]
    public void ExtensionMatching_IsCaseInsensitiveAndDotOptional()
    {
        var catalog = Catalog(new[] { "csfmt" }, Def("cs", "csfmt", extensions: "CS"));

        Assert.Single(catalog.SelectFor("/tmp/A.Cs"));
    }

    [Fact]
    public void ADefinitionWithNoExtensions_NeverMatches()
    {
        var withoutExtensions = new FormatterDefinition { Name = "greedy", Command = "csfmt" };
        var catalog = Catalog(new[] { "csfmt" }, withoutExtensions);

        Assert.Empty(catalog.SelectFor("/tmp/a.cs"));
        Assert.Empty(catalog.SelectFor("/tmp/a"));
    }

    [Fact]
    public void ResolveArguments_SubstitutesThePlaceholder_OrAppendsThePath()
    {
        var withPlaceholder = Def("a", "x") with { Arguments = new[] { "--write", "$FILE" } };
        Assert.Equal(new[] { "--write", "/tmp/a.cs" }, withPlaceholder.ResolveArguments("/tmp/a.cs"));

        var withoutPlaceholder = Def("b", "x") with { Arguments = new[] { "--write" } };
        Assert.Equal(new[] { "--write", "/tmp/a.cs" }, withoutPlaceholder.ResolveArguments("/tmp/a.cs"));
    }

    [Fact]
    public void Timeout_IsClampedToASaneRange()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), (Def("a", "x") with { TimeoutSeconds = 0 }).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(600), (Def("a", "x") with { TimeoutSeconds = 100000 }).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), (Def("a", "x") with { TimeoutSeconds = -5 }).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(1), (Def("a", "x") with { TimeoutSeconds = 1 }).Timeout);
    }
}
