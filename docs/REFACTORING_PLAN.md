# CLI Refactoring Plan

## Overview
This document outlines the refactoring strategy for the Andy CLI to improve maintainability, testability, and consistency.

## Completed

### 1. Centralized Theme System ✓
- **File**: `src/Andy.Cli/Themes/Theme.cs`
- **Purpose**: Single source of truth for all colors used in the UI
- **Benefits**:
  - Easy theme switching
  - Consistent color usage across all widgets
  - No more scattered color definitions

**Usage Example**:
```csharp
using Andy.Cli.Themes;

// Instead of:
var fg = new DL.Rgb24(180, 180, 180);

// Use:
var fg = Theme.Current.Text;
```

## In Progress

### 2. FeedView Refactoring
**Current State**: `FeedView.cs` is 2144 lines with 10+ classes defined in one file

**Target State**: Break into multiple files:
```
Widgets/
  FeedView.cs (main view class only)
  FeedItems/
    IFeedItem.cs ✓
    MarkdownItem.cs
    CodeBlockItem.cs
    UserBubbleItem.cs
    ToolExecutionItem.cs
    ProcessingIndicatorItem.cs
    RunningToolItem.cs
    StreamingMessageItem.cs
    SpacerItem.cs
    MarkdownRendererItem.cs
```

**Benefits**:
- Each class is independently testable
- Easier to understand and maintain
- Better code organization
- Faster compilation (smaller files)

**Migration Strategy**:
1. Extract interface (IFeedItem.cs) ✓
2. Extract one item class at a time
3. Update FeedView.cs to use the extracted classes
4. Write unit tests for each extracted class
5. Remove extracted code from FeedView.cs

## Planned

### 3. Widget Theme Integration
Update all widgets to use `Theme.Current` instead of hardcoded colors:

**Priority Files**:
- [ ] KeyHintsBar.cs
- [ ] ToastStatus.cs
- [ ] PromptLine.cs
- [ ] MarkdownDisplay.cs
- [ ] StatusMessage.cs
- [ ] TokenCounter.cs
- [ ] CommandOutput.cs
- [ ] CommandOutputView.cs
- [ ] ModelListItem.cs
- [ ] ToolListItem.cs
- [ ] ResponseSeparator.cs

### 4. Program.cs Refactoring
**Current Issues**:
- 1450+ lines in a single method (Main)
- Mixed concerns: initialization, rendering, input handling
- Hard to test

**Target State**:
```
Services/
  AppBootstrap.cs - Service initialization
  RenderLoop.cs - Main rendering loop
  InputHandler.cs - Keyboard input handling
  HeaderRenderer.cs - Header rendering logic
```

## Guidelines

### Theme Usage Pattern
```csharp
// At the top of your file
using Andy.Cli.Themes;

// In your render method
public void Render(...)
{
    var theme = Theme.Current;
    b.DrawText(new DL.TextRun(x, y, text, theme.Text, theme.Background, attrs));
}
```

### Feed Item Extraction Pattern
```csharp
// 1. Create new file: src/Andy.Cli/Widgets/FeedItems/YourItem.cs
// 2. Add namespace and using statements
using System;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.FeedItems
{
    /// <summary>
    /// Your item description
    /// </summary>
    public sealed class YourItem : IFeedItem
    {
        // ... implementation
    }
}

// 3. Update FeedView.cs to add:
using Andy.Cli.Widgets.FeedItems;

// 4. Remove the extracted class from FeedView.cs
```

## Testing Strategy

### Unit Tests for Feed Items
Each feed item should have tests for:
- MeasureLineCount() - correct line counting
- RenderSlice() - correct rendering at various widths
- Edge cases (empty content, very long lines, etc.)

**Example Test**:
```csharp
[Fact]
public void MarkdownItem_MeasuresCorrectLineCount()
{
    var item = new MarkdownItem("Line 1\nLine 2\nLine 3");
    Assert.Equal(3, item.MeasureLineCount(80));
}
```

## Future Considerations

### Theme Switching
Once theme system is fully integrated:
- Add ability to switch themes at runtime
- Provide light/dark theme presets
- Allow user-defined themes via configuration

### Configuration System
- Move theme configuration to appsettings.json
- Allow per-user theme overrides
- Support terminal color scheme detection

## Progress Tracking

- [x] Create Theme.cs
- [x] Extract IFeedItem interface
- [ ] Update 5 key widgets to use Theme
- [ ] Extract 3 feed item classes
- [ ] Write tests for extracted classes
- [ ] Document migration for remaining classes
