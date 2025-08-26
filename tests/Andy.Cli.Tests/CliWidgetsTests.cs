using Andy.Cli.Widgets;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests;

public class UserBubbleItemTests
{
    [Fact]
    public void UserBubbleItem_Renders_With_Rounded_Corners()
    {
        // Arrange
        var bubble = new UserBubbleItem("Test message");
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        
        // Act
        bubble.RenderSlice(0, 0, 20, 0, 10, baseDl, builder);
        var dl = builder.Build();
        
        // Assert - check for rounded corner characters
        var textRuns = dl.Ops.OfType<DL.TextRun>().ToList();
        
        // Should have top border with rounded corners
        var hasTopBorder = textRuns.Any(tr => tr.Content.Contains("╭") && tr.Content.Contains("╮"));
        Assert.True(hasTopBorder, "Top border with rounded corners should be present");
        
        // Should have bottom border with rounded corners  
        var hasBottomBorder = textRuns.Any(tr => tr.Content.Contains("╰") && tr.Content.Contains("╯"));
        Assert.True(hasBottomBorder, "Bottom border with rounded corners should be present");
        
        // Should have vertical borders with continuous characters
        var verticalBorders = textRuns.Where(tr => tr.Content == "│").ToList();
        Assert.NotEmpty(verticalBorders);
    }
    
    [Fact]
    public void UserBubbleItem_Measures_Correct_Height()
    {
        // Arrange
        var bubble = new UserBubbleItem("Test\nMulti-line\nMessage");
        
        // Act
        int lineCount = bubble.MeasureLineCount(40);
        
        // Assert - should be content lines + 2 for top/bottom borders
        Assert.Equal(5, lineCount); // 3 content lines + 2 border lines
    }
    
    [Fact]
    public void PromptLine_Uses_Light_Blue_Text_Color()
    {
        // Arrange
        var prompt = new PromptLine();
        prompt.OnKey(new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false));
        prompt.OnKey(new ConsoleKeyInfo('e', ConsoleKey.E, false, false, false));
        prompt.OnKey(new ConsoleKeyInfo('s', ConsoleKey.S, false, false, false));
        prompt.OnKey(new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false));
        
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        
        // Act
        prompt.Render(new Andy.Tui.Layout.Rect(0, 0, 20, 3), baseDl, builder);
        var dl = builder.Build();
        
        // Assert - check for light blue text color (150, 200, 255)
        var textRuns = dl.Ops.OfType<DL.TextRun>().ToList();
        var textWithContent = textRuns.FirstOrDefault(tr => tr.Content == "test");
        
        Assert.NotEqual(default(DL.TextRun), textWithContent);
        Assert.Equal(150, textWithContent.Fg.R);
        Assert.Equal(200, textWithContent.Fg.G);
        Assert.Equal(255, textWithContent.Fg.B);
    }
    
    [Fact]
    public void MarkdownRenderer_Uses_Colored_Headers()
    {
        // Arrange
        var markdown = "# Header 1\n## Header 2\n### Header 3";
        var item = new MarkdownRendererItem(markdown);
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        
        // Act
        item.RenderSlice(0, 0, 50, 0, 10, baseDl, builder);
        var dl = builder.Build();
        
        // Assert - check for colored headers
        var textRuns = dl.Ops.OfType<DL.TextRun>().ToList();
        
        // H1 should be blue (100,200,255)
        var h1Runs = textRuns.Where(tr => tr.Fg.R == 100 && tr.Fg.G == 200 && tr.Fg.B == 255).ToList();
        Assert.NotEmpty(h1Runs);
        
        // H2 should be green (150,220,150)  
        var h2Runs = textRuns.Where(tr => tr.Fg.R == 150 && tr.Fg.G == 220 && tr.Fg.B == 150).ToList();
        Assert.NotEmpty(h2Runs);
        
        // H3 should be orange (255,180,100)
        var h3Runs = textRuns.Where(tr => tr.Fg.R == 255 && tr.Fg.G == 180 && tr.Fg.B == 100).ToList();
        Assert.NotEmpty(h3Runs);
    }
    
    [Fact] 
    public void MarkdownRenderer_Uses_Colored_List_Markers()
    {
        // Arrange
        var markdown = "- Bullet item\n1. Numbered item";
        var item = new MarkdownRendererItem(markdown);
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        
        // Act
        item.RenderSlice(0, 0, 50, 0, 10, baseDl, builder);
        var dl = builder.Build();
        
        // Assert - check for colored list markers
        var textRuns = dl.Ops.OfType<DL.TextRun>().ToList();
        
        // List markers should be light red (255,150,150) and bold
        var listMarkers = textRuns.Where(tr => 
            tr.Fg.R == 255 && tr.Fg.G == 150 && tr.Fg.B == 150 && 
            tr.Attrs.HasFlag(DL.CellAttrFlags.Bold)).ToList();
        Assert.NotEmpty(listMarkers);
        
        // Should have bullet and number markers
        var bulletMarker = textRuns.FirstOrDefault(tr => tr.Content == "•");
        Assert.NotEqual(default(DL.TextRun), bulletMarker);
        
        var numberMarker = textRuns.FirstOrDefault(tr => tr.Content == "1");
        Assert.NotEqual(default(DL.TextRun), numberMarker);
    }
    
    [Fact]
    public void CodeBlockItem_Renders_With_Line_Numbers()
    {
        // Arrange
        var code = "function hello() {\n    console.log('Hello');\n    return true;\n}";
        var codeBlock = new CodeBlockItem(code, "javascript");
        var baseDl = new DL.DisplayListBuilder().Build();
        var builder = new DL.DisplayListBuilder();
        
        // Act
        codeBlock.RenderSlice(0, 0, 60, 0, 10, baseDl, builder);
        var dl = builder.Build();
        
        // Assert - check for line numbers
        var textRuns = dl.Ops.OfType<DL.TextRun>().ToList();
        
        // Should have line number "  1" (right-aligned)
        var lineOne = textRuns.FirstOrDefault(tr => tr.Content.Trim() == "1");
        Assert.NotEqual(default(DL.TextRun), lineOne);
        
        // Should have line number "  2"
        var lineTwo = textRuns.FirstOrDefault(tr => tr.Content.Trim() == "2");
        Assert.NotEqual(default(DL.TextRun), lineTwo);
        
        // Line numbers should have different color than code content
        var lineNumColor = new DL.Rgb24(120,140,160);
        var lineNumberRuns = textRuns.Where(tr => 
            tr.Fg.R == lineNumColor.R && tr.Fg.G == lineNumColor.G && tr.Fg.B == lineNumColor.B).ToList();
        Assert.NotEmpty(lineNumberRuns);
        
        // Should have actual code content
        var codeContent = textRuns.FirstOrDefault(tr => tr.Content.Contains("function"));
        Assert.NotEqual(default(DL.TextRun), codeContent);
    }
    
    [Fact]
    public void CodeBlockItem_Calculates_Correct_Line_Count_With_Wrapping()
    {
        // Arrange
        var code = "// This is a very long comment that should wrap across multiple lines when rendered\nfunction test() { return 42; }";
        var codeBlock = new CodeBlockItem(code, "javascript");
        
        // Act - test with narrow width that will cause wrapping
        int lineCount = codeBlock.MeasureLineCount(30); // 30 chars total, 26 available for content (30-4 for line numbers)
        
        // Assert - should account for wrapped lines
        // First line is ~80 chars, so with 26 available: Math.Ceiling(80/26) = 4 lines
        // Second line is ~30 chars, so: Math.Ceiling(30/26) = 2 lines  
        // Total should be more than 2 logical lines
        Assert.True(lineCount > 2, $"Expected more than 2 lines due to wrapping, got {lineCount}");
    }
}