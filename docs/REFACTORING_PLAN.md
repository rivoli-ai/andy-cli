# CLI Refactoring Plan

Updated: 2026-07-22

## Overview
This document outlines the refactoring strategy for the Andy CLI to improve maintainability, testability, and consistency.

## Current file sizes (issue #177 baseline)

Measured on `main` on 2026-07-21:

- `src/Andy.Cli/Program.cs` - 2,204 lines.
  Combines DI composition, provider/permission setup, headless/ACP/command
  dispatch, input handling, TUI lifecycle, and rendering.
- `src/Andy.Cli/Widgets/FeedView.cs` - 2,791 lines after the first three
  feed-item extractions; 11 container, helper, and item types remain.
  See `docs/feedview-inventory.md` for the per-component inventory and the
  reusable-vs-CLI-only classification.

## Issue #177 first increment (composition-root extraction)

Safe, behavior-preserving extractions have landed. Logic was moved verbatim and
is covered by the existing test suite.

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
to it here. The recreation remains in the current implementation.

### Direct Console output mixed with TUI rendering (noted)

`Program.Main` writes cursor-position / DECSCUSR escapes directly via
`Console.Write` after each frame (see the "NOTE" comment near the end of the
render loop) instead of routing through the PTY. This is called out in #177 as a
mixed-output concern. Routing cursor ops through the TUI/PTY interface is an
andy-tui2-facing change and was left unchanged (out of scope for a safe first
increment).

## Completed

### 1. Centralized theme definitions and runtime switching
- **File**: `src/Andy.Cli/Themes/Theme.cs`
- **Purpose**: Single source of truth for all colors used in the UI
- **Benefits**:
  - Easy theme switching
  - Consistent color usage across all widgets
  - No more scattered color definitions

Theme selection, persistence, 34 built-in themes, and optional transparent
backgrounds are implemented. Full widget adoption is not complete; several
widgets still contain hard-coded colors, tracked in item 3.

**Usage Example**:
```csharp
using Andy.Cli.Themes;

// Instead of:
var fg = new DL.Rgb24(180, 180, 180);

// Use:
var fg = Theme.Current.Text;
```

## In Progress

### 2. FeedView refactoring (#211)
**Current State**: `IFeedItem`, `FileDiffItem`, `UserBubbleItem`, and
`StreamingMessageItem` are independently defined under `Widgets/FeedItems/`.
The first issue #211 extraction tranche is complete and reduced `FeedView.cs`
from 3,152 to 2,791 lines without changing their public namespaces.

**Target State**: Break into multiple files:
```
Widgets/
  FeedView.cs (main view class only)
  FeedItems/
    IFeedItem.cs (complete)
    MarkdownItem.cs
    CodeBlockItem.cs
    FileDiffItem.cs (complete)
    UserBubbleItem.cs (complete)
    ToolExecutionItem.cs
    ProcessingIndicatorItem.cs
    RunningToolItem.cs
    StreamingMessageItem.cs (complete)
    SpacerItem.cs
    MarkdownRendererItem.cs
```

**Benefits**:
- Each class is independently testable
- Easier to understand and maintain
- Better code organization
- Faster compilation (smaller files)

**Migration Strategy**:
1. Extract interface (`IFeedItem.cs`) (complete)
2. Extract `FileDiffItem`, `UserBubbleItem`, and `StreamingMessageItem`
   (complete)
3. Extract the remaining item classes one at a time
4. Update FeedView.cs to use the extracted classes
5. Write unit tests for each extracted class
6. Remove extracted code from FeedView.cs

## Planned

### 3. Widget Theme Integration
Update all widgets to use `Theme.Current` instead of hardcoded colors:

**Priority Files**:
- [ ] Remove remaining hard-coded colors from `FeedView.cs` feed items.
- [ ] Theme `CommandPalette`, `PermissionsManager`, `InlineCommandHelp`, and
  `ToolExecutionDisplay`.
- [ ] Theme remaining fallback/status colors in `MarkdownDisplay`,
  `ContextStatusBar`, `StatusMessage`, `TokenCounter`, `CommandOutput`,
  `CommandOutputView`, `ModelListItem`, `ToolListItem`, and `ResponseSeparator`.
- [ ] Add regression coverage for light and transparent themes across the
  remaining widgets.

### 4. Program.cs Refactoring
**Current Issues**:
- 2,204 lines; `Main` is still a very large method
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

## Future considerations

### User-defined themes

Runtime switching and built-in light/dark themes are complete. A future config
format could allow user-defined theme palettes without recompiling the CLI.

### Configuration System
- Move theme configuration to appsettings.json
- Allow per-user theme overrides
- Support terminal color scheme detection

## Progress Tracking

- [x] Create Theme.cs
- [x] Extract IFeedItem interface
- [ ] Remove remaining hard-coded widget colors
- [x] Extract 3 feed item classes (#211)
- [x] Write tests for extracted classes
- [x] Document migration for remaining classes (see `docs/feedview-inventory.md`)
- [x] Extract CLI mode-dispatch into `Hosting/CliModeSelector` (#177)
- [x] Extract shared tool-service DI into `Hosting/AppCompositionRoot` (#177)
- [x] Extract provider URL mapping into `Hosting/ProviderUrlResolver` (#177)
- [ ] Extract render loop / input handler / dialogs out of `Program.Main` (#177, follow-up)
- [ ] Cross-repo: add `FrameScheduler` reset/invalidate API in andy-tui2 and stop recreating it per reflow (#177)
- [ ] Cross-repo: move REUSABLE feed items into andy-tui2 (see inventory)

## Completion summary

2026-07-21: Re-audited every checklist item against `main`, corrected the
Program/FeedView baselines, recorded the completed runtime theme system, and
kept the remaining widget-theme, FeedView extraction (#211), Program
decomposition, and cross-repository TUI work open.

2026-07-22: Completed the first #211 file-split tranche by moving
`FileDiffItem`, `UserBubbleItem`, and `StreamingMessageItem` under
`Widgets/FeedItems/`. Preserved their public API and added or retained focused
measure/render regression coverage for all three.
