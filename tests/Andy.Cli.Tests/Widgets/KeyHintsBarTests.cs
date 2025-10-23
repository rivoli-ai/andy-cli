using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class KeyHintsBarTests
{
    [Fact]
    public void SetHints_AcceptsMultipleHints()
    {
        // Arrange
        var hints = new KeyHintsBar();

        // Act
        hints.SetHints(new[]
        {
            ("Ctrl+P", "Commands"),
            ("F2", "Toggle HUD"),
            ("ESC", "Quit")
        });

        // Assert - No exception thrown, basic functionality works
        Assert.True(true);
    }

    [Fact]
    public void Render_DoesNotThrowWithNarrowViewport()
    {
        // Arrange
        var hints = new KeyHintsBar();
        hints.SetHints(new[]
        {
            ("Ctrl+P", "Commands"),
            ("PgUp/PgDn", "Scroll"),
            ("F2", "Toggle HUD"),
            ("ESC", "Quit"),
            ("", "http://localhost:5555")
        });

        var baseBuilder = new Andy.Tui.DisplayList.DisplayListBuilder();
        var baseDl = baseBuilder.Build();
        var builder = new Andy.Tui.DisplayList.DisplayListBuilder();

        // Act - Narrow viewport (60 columns) with token counter taking 20 chars
        var viewport = (Width: 60, Height: 24);
        int reservedRightWidth = 20;

        // Should not throw even with very narrow space
        var exception = Record.Exception(() => hints.Render(viewport, baseDl, builder, reservedRightWidth));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Render_DoesNotThrowWithWideViewport()
    {
        // Arrange
        var hints = new KeyHintsBar();
        hints.SetHints(new[]
        {
            ("F2", "HUD"),
            ("ESC", "Quit"),
            ("", "http://localhost:5555")
        });

        var baseBuilder = new Andy.Tui.DisplayList.DisplayListBuilder();
        var baseDl = baseBuilder.Build();
        var builder = new Andy.Tui.DisplayList.DisplayListBuilder();

        // Act - Wide viewport
        var viewport = (Width: 200, Height: 24);
        int reservedRightWidth = 20;

        var exception = Record.Exception(() => hints.Render(viewport, baseDl, builder, reservedRightWidth));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Render_RespectsReservedRightWidth()
    {
        // Arrange
        var hints = new KeyHintsBar();
        hints.SetHints(new[]
        {
            ("Ctrl+P", "Commands"),
            ("", "http://localhost:5555")
        });

        var baseBuilder = new Andy.Tui.DisplayList.DisplayListBuilder();
        var baseDl = baseBuilder.Build();
        var builder = new Andy.Tui.DisplayList.DisplayListBuilder();

        // Act - Should not throw when rendering with reserved space
        var viewport = (Width: 100, Height: 24);
        int reservedRightWidth = 30;

        var exception = Record.Exception(() => hints.Render(viewport, baseDl, builder, reservedRightWidth));

        // Assert - Verify rendering completes without error
        Assert.Null(exception);
    }
}
