using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Editor;
using Xunit;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// VISUAL/EDITOR precedence and the actionable guidance shown when neither is usable
/// (issue #287). The environment is injected, so nothing here mutates process state.
/// </summary>
public class EditorResolverTests
{
    private static EditorResolver With(params (string Name, string? Value)[] env)
    {
        var map = env.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        return new EditorResolver(name => map.TryGetValue(name, out var v) ? v : null);
    }

    [Fact]
    public void PrecedenceOrder_IsVisualThenEditor()
        => Assert.Equal(new[] { "VISUAL", "EDITOR" }, EditorResolver.VariableOrder.ToArray());

    [Fact]
    public void VisualWins_WhenBothAreSet()
    {
        var r = With(("VISUAL", "nvim"), ("EDITOR", "vi")).Resolve();

        Assert.True(r.Success);
        Assert.Equal("VISUAL", r.Variable);
        Assert.Equal("nvim", r.FileName);
        Assert.Empty(r.Arguments);
    }

    [Fact]
    public void EditorIsUsed_WhenVisualIsAbsent()
    {
        var r = With(("EDITOR", "vi")).Resolve();

        Assert.True(r.Success);
        Assert.Equal("EDITOR", r.Variable);
        Assert.Equal("vi", r.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void BlankVisual_FallsThroughToEditor(string blank)
    {
        var r = With(("VISUAL", blank), ("EDITOR", "nano")).Resolve();

        Assert.True(r.Success);
        Assert.Equal("EDITOR", r.Variable);
        Assert.Equal("nano", r.FileName);
    }

    [Fact]
    public void ConfiguredArguments_AreCarriedThrough()
    {
        var r = With(("VISUAL", "code --wait -n")).Resolve();

        Assert.True(r.Success);
        Assert.Equal("code", r.FileName);
        Assert.Equal(new[] { "--wait", "-n" }, r.Arguments.ToArray());
    }

    [Fact]
    public void QuotedProgramPathWithSpaces_ResolvesToOneProgram()
    {
        var r = With(("VISUAL", "\"/Applications/My Editor/bin/edit\" --wait")).Resolve();

        Assert.True(r.Success);
        Assert.Equal("/Applications/My Editor/bin/edit", r.FileName);
        Assert.Equal(new[] { "--wait" }, r.Arguments.ToArray());
    }

    [Fact]
    public void NeitherSet_ProducesActionableGuidance()
    {
        var r = With().Resolve();

        Assert.False(r.Success);
        Assert.Null(r.Variable);
        var message = r.Message!;
        Assert.Contains("VISUAL", message);
        Assert.Contains("EDITOR", message);
        Assert.Contains("export VISUAL=", message);
        Assert.Contains("vim", message);
        Assert.Contains("nvim", message);
        Assert.Contains("code --wait", message);
        Assert.Contains("nano", message);
        Assert.Contains(EditorSetupGuidance.DocsPath, message);
    }

    [Fact]
    public void UnparsableValue_ReportsTheVariable_AndDoesNotFallThrough()
    {
        // A typo must not silently launch whatever EDITOR points at.
        var r = With(("VISUAL", "\"/opt/my editor --wait"), ("EDITOR", "vi")).Resolve();

        Assert.False(r.Success);
        Assert.Equal("VISUAL", r.Variable);
        Assert.Contains("VISUAL", r.Message);
        Assert.Contains("double quote", r.Message);
        Assert.Contains(EditorSetupGuidance.DocsPath, r.Message);
    }

    [Fact]
    public void EnvironmentReaderThrowing_IsTreatedAsUnset()
    {
        var resolver = new EditorResolver(name => name == "VISUAL"
            ? throw new InvalidOperationException("boom")
            : "vi");

        var r = resolver.Resolve();

        Assert.True(r.Success);
        Assert.Equal("EDITOR", r.Variable);
    }

    [Fact]
    public void DefaultConstructor_ReadsTheProcessEnvironment()
    {
        // Only asserts the wiring; the value itself depends on the developer's shell.
        var resolver = new EditorResolver();
        var r = resolver.Resolve();
        bool anySet = EditorResolver.VariableOrder
            .Any(v => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)));
        Assert.Equal(anySet, r.Success || r.Variable is not null);
    }
}
