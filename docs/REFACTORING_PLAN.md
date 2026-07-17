# CLI Refactoring Plan

## Overview
This document outlines the refactoring strategy for the Andy CLI to improve maintainability, testability, and consistency.

## Current file sizes (issue #177 baseline)

Measured on the `fix/177-composition-root-refactor` branch:

- `src/Andy.Cli/Program.cs` - ~2,180 lines (was ~2,244 before this pass).
  Combines DI composition, provider/permission setup, headless/ACP/command
  dispatch, input handling, TUI lifecycle, and rendering.
- `src/Andy.Cli/Widgets/FeedView.cs` - ~3,150 lines, ~15 types in one file.
  See `docs/feedview-inventory.md` for the per-component inventory and the
  reusable-vs-CLI-only classification.

## Issue #177 first increment (composition-root extraction)

Safe, behaviour-preserving extractions landed on this branch. Logic was moved
**verbatim**; the whole existing test suite still passes.

- **`src/Andy.Cli/Hosting/CliMode.cs` + `CliModeSelector.cs`** - the top-level
  mode-dispatch decision (`version` / `acp` / `headless` / `command` /
  `interactive`) that used to be a chain of `if` blocks at the top of
  `Program.Main`. Now a single pure `CliModeSelector.Select(args)` consumed by a
  `switch` in `Main`. Unit tested (`Hosting/CliModeSelectorTests.cs`), including
  the branch-precedence cases.
- **`src/Andy.Cli/Hosting/AppCompositionRoot.cs`** - the single application
  composition root for the tool service graph. `AddCoreToolServices(services,
  broker)` and `InitializeToolRegistry(provider)` replace a ~25-line block of
  Andy.Tools registrations plus a registry-init loop that were **duplicated
  verbatim in three places** (interactive `Main`, `RunAcpServerModeAsync`,
  `HandleCommandLineArgs`). All three call sites now share one definition. Unit
  tested (`Hosting/AppCompositionRootTests.cs`).
- **`src/Andy.Cli/Hosting/ProviderUrlResolver.cs`** - the provider-URL mapping
  (with env overrides) extracted from the private `Program.GetProviderUrl`. Unit
  tested (`Hosting/ProviderUrlResolverTests.cs`).

### Frame-scheduler recreation (documented, not changed)

`Program.Main` recreates `new Andy.Tui.Core.FrameScheduler(...)` on every reflow/
scroll (the `reflowSig != lastReflowSig` branch) to force a full clear + repaint,
because `FrameScheduler` exposes no "reset" hook. Hoisting this to a single
lifecycle-owned instance is **not** a trivially safe change: the full-repaint
behaviour currently *depends* on a fresh instance having no previous grid.
A safe fix requires a reset/invalidate API on `FrameScheduler` (an `andy-tui2`
change). **Recommendation:** add `FrameScheduler.Invalidate()` (or a
`ForceFullRepaint()` flag) in andy-tui2, then replace the recreation with a call
to it here. Left unchanged on this branch.

### Direct Console output mixed with TUI rendering (noted)

`Program.Main` writes cursor-position / DECSCUSR escapes directly via
`Console.Write` after each frame (see the "NOTE" comment near the end of the
render loop) instead of routing through the PTY. This is called out in #177 as a
mixed-output concern. Routing cursor ops through the TUI/PTY interface is an
andy-tui2-facing change and was left unchanged (out of scope for a safe first
increment).

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
- ~2,180 lines; `Main` is still a very large method
- Mixed concerns: initialization, rendering, input handling
- Hard to test

**Landed (issue #177 first increment)** - see the "Issue #177 first increment"
section above:
- `Hosting/CliModeSelector.cs` - mode dispatch (tested)
- `Hosting/AppCompositionRoot.cs` - shared tool-service DI graph (tested)
- `Hosting/ProviderUrlResolver.cs` - provider URL mapping (tested)

**Remaining target state** (subsequent, larger increments):
```
Hosting/ (or Services/)
  AppBootstrap.cs   - LLM + configuration service initialization
  RenderLoop.cs     - Main rendering loop
  InputHandler.cs   - Keyboard input handling (the HandleKey local function)
  HeaderRenderer.cs - Header rendering logic
  Dialogs/          - ShowExitConfirmationAsync / ShowPermissionDialogAsync
```
Note: the render loop, input handling, and modal dialogs are tightly coupled to
local closures over `viewport`, `scheduler`, `feed`, `prompt`, etc. Extracting
them is higher-risk and should follow only after the composition-root pieces are
stable.

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
- [x] Document migration for remaining classes (see `docs/feedview-inventory.md`)
- [x] Extract CLI mode-dispatch into `Hosting/CliModeSelector` (#177)
- [x] Extract shared tool-service DI into `Hosting/AppCompositionRoot` (#177)
- [x] Extract provider URL mapping into `Hosting/ProviderUrlResolver` (#177)
- [ ] Extract render loop / input handler / dialogs out of `Program.Main` (#177, follow-up)
- [ ] Cross-repo: add `FrameScheduler` reset/invalidate API in andy-tui2 and stop recreating it per reflow (#177)
- [ ] Cross-repo: move REUSABLE feed items into andy-tui2 (#177, see inventory)
