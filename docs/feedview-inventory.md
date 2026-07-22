# FeedView.cs component inventory

Updated: 2026-07-21

`src/Andy.Cli/Widgets/FeedView.cs` is 3,152 lines and defines roughly 15
types in a single file. Issue #177 calls for moving the general-purpose,
reusable rendering pieces into the `andy-tui2` package (owner of TUI work) while
keeping CLI-only domain presentation here. This remains the working inventory
for the refactoring tracked in issue #177.

**Important:** `Andy.Tui` is consumed here as a NuGet **package**; its source
lives in the sibling `andy-tui2` repository and is **not editable from this
repo**. Actually moving any component into `andy-tui2` is therefore a
**cross-repo change**. This document is the inventory and migration
recommendation for coordinating that work.

## Classification legend

- **REUSABLE** - general-purpose widget / rendering with no CLI domain
  knowledge. Recommended to move to `andy-tui2` (as a widget or a reusable
  `IFeedItem`-style renderer) once that repo exposes a suitable extension point.
- **CLI-ONLY** - depends on Andy CLI domain concepts (tool execution model,
  chat turns, assistant/user roles, permission/diff presentation). Stays in
  andy-cli.
- **INFRA** - the container/orchestrator and shared view state. Stays here.

## Inventory

| # | Type | Approx. lines | Location | Class | Notes |
|---|------|--------------:|----------|-------|-------|
| 1 | `ToolOutputView` (static) | ~25-47 | FeedView.cs | INFRA (CLI) | Shared Ctrl+O expand/collapse flag read by tool items at render time. CLI view state; stays. |
| 2 | `FeedView` | ~48-982 | FeedView.cs | INFRA | The scrollable feed container: item list, measure/measure-cache, bottom-follow, scroll clamp, animation ticks, reflow signalling. Orchestrator; stays, but is a candidate to slim by extracting the item classes below into their own files first (see plan doc). |
| 3 | `MarkdownItem` | ~1005-1048 | FeedView.cs | REUSABLE | Naive line-by-line markdown renderer with fenced-code detection. Generic. |
| 4 | `FileDiffItem` (+ `FileChangeKind`, `RowKind`, `Row`) | ~1049-1202 | FeedView.cs | CLI-ONLY (borderline) | Git-style unified-diff presentation of a file write/update. The diff-row rendering is generic, but it is driven by the CLI's tool-result/diff domain. Recommend keeping in CLI; optionally split a generic "unified diff view" primitive into andy-tui2 later. |
| 5 | `FeedMarkdown` (static) | ~1203-1285 | FeedView.cs | REUSABLE | Markdown normalization (blank-line collapsing, heading spacing). Pure text transform; strong andy-tui2 candidate. |
| 6 | `MarkdownLinkStyle` (internal static) | ~1286-1348 | FeedView.cs | REUSABLE | Replays a display list transforming link TextRuns (styling without underline). Generic display-list post-processing utility. |
| 7 | `MarkdownRendererItem` | ~1349-1483 | FeedView.cs | REUSABLE | Wraps `Andy.Tui.Widgets.MarkdownRenderer` as a feed item with measure==render parity. The richest markdown item; belongs next to the renderer it wraps in andy-tui2. |
| 8 | `TableItem` | ~1484-1640 | FeedView.cs | REUSABLE | General table widget with explicit vertical separators and measure==render parity. Generic; strong andy-tui2 candidate. |
| 9 | `CodeBlockItem` | ~1641-1786 | FeedView.cs | REUSABLE | Shaded-background code block with syntax highlighting. Generic; strong andy-tui2 candidate (pairs with the code highlighter). |
| 10 | `SpacerItem` | ~1787-1797 | FeedView.cs | REUSABLE | Trivial vertical spacer. Generic; move alongside the other items. |
| 11 | `UserBubbleItem` | ~1798-1911 | FeedView.cs | CLI-ONLY (borderline) | Bordered "user message" bubble. The bordered-bubble rendering is generic, but the concept (user chat turn, message numbering) is CLI chat domain. Recommend a generic "bordered text block" primitive in andy-tui2 with the chat-specific bubble staying here. |
| 12 | `ToolExecutionItem` | ~1912-2126 | FeedView.cs | CLI-ONLY | Tool-call display (params, result label/body, dotted gutter). Tied to the tool execution model. Stays. |
| 13 | `StreamingMessageItem` | ~2127-2219 | FeedView.cs | CLI-ONLY (borderline) | Progressive/streamed assistant text. Streaming-text rendering is generic; the assistant-turn semantics are CLI. Recommend a generic streaming-text item in andy-tui2, CLI keeps the assistant wrapper. |
| 14 | `ProcessingIndicatorItem` | ~2220-2298 | FeedView.cs | CLI-ONLY (borderline) | Animated "thinking" indicator with turn stats. Spinner animation is generic; the turn-stat content is CLI. |
| 15 | `RunningToolItem` | ~2299-3152 (~850 lines) | FeedView.cs | CLI-ONLY | The largest component: live tool-execution rendering in "Claude style" with per-tool status, result formatting, and detail lines. Deeply tied to the CLI tool-execution domain. Stays, but is itself a candidate for its own file and further decomposition. |

## Migration recommendation

1. **First, in-repo (no cross-repo dependency):** split FeedView.cs into one
   file per type under `src/Andy.Cli/Widgets/FeedItems/` (the direction the
   existing `REFACTORING_PLAN.md` already set, and where `IFeedItem` already
   lives). This is mechanical and low-risk, shrinks the 3,150-line file, and
   makes each item independently testable and independently movable. Do it
   **before** any cross-repo move so the move is a file relocation, not a
   surgical extraction.

2. **Then, cross-repo to `andy-tui2`:** promote the REUSABLE set - items #3
   `MarkdownItem`, #5 `FeedMarkdown`, #6 `MarkdownLinkStyle`, #7
   `MarkdownRendererItem`, #8 `TableItem`, #9 `CodeBlockItem`, #10 `SpacerItem`
   - into `andy-tui2` as widgets/renderers. This requires:
   - andy-tui2 to expose a reusable feed-item contract (or accept these as
     stand-alone widgets), and
   - a coordinated package version bump consumed here.

3. **Keep in andy-cli:** the INFRA orchestrator (`FeedView`, `ToolOutputView`)
   and the CLI-ONLY domain items (`ToolExecutionItem`, `RunningToolItem`,
   `FileDiffItem`, `UserBubbleItem`, `StreamingMessageItem`,
   `ProcessingIndicatorItem`). Where a borderline item has a generic core
   (bordered block, streaming text, spinner), extract that core into andy-tui2
   and keep the thin CLI-specific wrapper here.

## Current status

No feed-item implementations have moved yet; only `IFeedItem` is already in
`Widgets/FeedItems/`. The in-repository file split remains the next low-risk
increment in `REFACTORING_PLAN.md`, followed by coordinated package work for
components that belong in `andy-tui2`.
