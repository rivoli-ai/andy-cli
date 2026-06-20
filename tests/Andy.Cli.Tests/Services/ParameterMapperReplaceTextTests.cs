using System.Collections.Generic;
using Andy.Cli.Services;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Issue #134: the live dispatch path renamed nothing, so a model calling replace_text with the
/// names from its own edit tools (old_string/new_string/file_path) sent null search_pattern/
/// replacement_text - the prompt showed "Replace None ''" and the edit failed. MapAndNormalize maps
/// the curated aliases (no fuzzy guessing) so the real parameters arrive.
/// </summary>
public class ParameterMapperReplaceTextTests
{
    private static ToolMetadata ReplaceTextMeta() => new()
    {
        Id = "replace_text",
        Name = "Replace Text",
        Parameters = new List<ToolParameter>
        {
            new() { Name = "search_pattern", Type = "string", Required = true },
            new() { Name = "replacement_text", Type = "string", Required = true },
            new() { Name = "target_path", Type = "string", Required = false },
            new() { Name = "create_backup", Type = "boolean", Required = false },
        },
    };

    [Fact]
    public void MapsClaudeStyleEditNames_ToReplaceTextParameters()
    {
        var input = new Dictionary<string, object?>
        {
            ["old_string"] = "foo",
            ["new_string"] = "bar",
            ["file_path"] = "/tmp/x.cs",
        };

        var mapped = ParameterMapper.MapAndNormalize("replace_text", input, ReplaceTextMeta());

        Assert.Equal("foo", mapped["search_pattern"]);
        Assert.Equal("bar", mapped["replacement_text"]);
        Assert.Equal("/tmp/x.cs", mapped["target_path"]);
        Assert.False(mapped.ContainsKey("old_string"));
        Assert.False(mapped.ContainsKey("new_string"));
        Assert.False(mapped.ContainsKey("file_path"));
    }

    [Fact]
    public void MapsFindReplaceAndPatternAliases()
    {
        foreach (var (alias, target) in new[]
        {
            ("find", "search_pattern"), ("pattern", "search_pattern"), ("search", "search_pattern"),
            ("replace", "replacement_text"), ("replacement", "replacement_text"), ("new", "replacement_text"),
        })
        {
            var mapped = ParameterMapper.MapAndNormalize("replace_text",
                new Dictionary<string, object?> { [alias] = "v" }, ReplaceTextMeta());
            Assert.True(mapped.ContainsKey(target), $"alias '{alias}' should map to '{target}'");
            Assert.Equal("v", mapped[target]);
        }
    }

    [Fact]
    public void CanonicalNamesPassThroughUnchanged()
    {
        var input = new Dictionary<string, object?>
        {
            ["search_pattern"] = "a",
            ["replacement_text"] = "b",
        };
        var mapped = ParameterMapper.MapAndNormalize("replace_text", input, ReplaceTextMeta());
        Assert.Equal("a", mapped["search_pattern"]);
        Assert.Equal("b", mapped["replacement_text"]);
    }

    [Fact]
    public void UnknownNames_PassThrough_NoFuzzyMisrouting()
    {
        // "something_unrelated" must NOT be force-fit to a parameter (the old fuzzy matcher could).
        var input = new Dictionary<string, object?> { ["something_unrelated"] = "z" };
        var mapped = ParameterMapper.MapAndNormalize("replace_text", input, ReplaceTextMeta());
        Assert.True(mapped.ContainsKey("something_unrelated"));
        Assert.False(mapped.ContainsKey("search_pattern"));
        Assert.False(mapped.ContainsKey("replacement_text"));
    }
}
