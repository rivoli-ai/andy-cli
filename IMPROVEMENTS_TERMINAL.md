# Terminal Display Improvements

## Issues Fixed

### 1. Terminal Resize Detection
**Problem**: The application wasn't detecting terminal resize events, causing display issues when the terminal window was resized.

**Solution**: 
- Added continuous monitoring of `Console.WindowWidth` and `Console.WindowHeight` in the main render loop
- When a resize is detected:
  - Updates the viewport dimensions
  - Clears the console to force a full redraw
  - All components now render with the new dimensions

### 2. Word Wrapping for Long Text
**Problem**: Long responses would overflow beyond the terminal width, making text unreadable.

**Solutions Implemented**:

#### MarkdownRendererItem Improvements
- Enhanced `MeasureLineCount()` to accurately calculate wrapped lines
- Takes into account the effective width (80% of available width for markdown content)
- Properly calculates line breaks for long text strings
- Returns accurate line count for proper scrolling

#### UserBubbleItem Improvements  
- Updated to track wrapped lines within the user message bubble
- Accounts for border characters and padding when calculating available width
- Properly wraps text within the bubble boundaries

## Technical Details

### Viewport Management
```csharp
// Check for terminal resize
if (Console.WindowWidth != lastWidth || Console.WindowHeight != lastHeight)
{
    viewport = (Width: Console.WindowWidth, Height: Console.WindowHeight);
    lastWidth = viewport.Width;
    lastHeight = viewport.Height;
    Console.Clear(); // Force full redraw
}
```

### Word Wrap Calculation
```csharp
// Calculate wrapped lines for content
int effectiveWidth = Math.Max(1, (int)(width * 0.8));
int wrappedLines = (line.Length + effectiveWidth - 1) / effectiveWidth;
totalLines += Math.Max(1, wrappedLines);
```

## Benefits

1. **Responsive UI**: The application now properly adapts to terminal size changes
2. **Readable Text**: Long responses are properly wrapped within the terminal width
3. **Better Scrolling**: Accurate line count calculations ensure proper scrolling behavior
4. **Improved UX**: Users can resize their terminal without losing content or breaking the display

## Testing

To test these improvements:
1. Run the application: `dotnet run --project src/Andy.Cli`
2. Try resizing the terminal window - the display should adapt immediately
3. Send a long message or receive a long response - text should wrap properly
4. Test with very narrow and very wide terminal sizes

## Future Enhancements

- Consider implementing smart word breaking (avoiding breaking in the middle of words)
- Add support for horizontal scrolling for code blocks
- Implement dynamic reflowing of existing content on resize
- Add configuration options for wrap behavior