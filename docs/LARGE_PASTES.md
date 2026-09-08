# Large pasted text

Bracketed terminal pastes of at least 10 KiB (UTF-8) or 500 lines appear as an item
such as `[Pasted text #1: 520 lines, 12000 chars]`. This is a display label, not a
summary sent to the model. The exact original payload, including CRLF, indentation,
Unicode and trailing whitespace, is retained separately and sent in its original
position among the surrounding text. Multiple items and an image attachment can
coexist. Pasted `@path` text is literal; type file mentions outside the item to attach files.

- Type before or after an item normally. Left/Right and vertical movement skip its interior.
- Backspace after an item or Delete before it removes the whole item in one action.
  Ctrl+K and Ctrl+U line cuts also remove whole intersected items.
- Ctrl+Z undoes an edit; Ctrl+Y or Ctrl+Shift+Z redoes it. Undo retains up to 100 edits.
  No deletion confirmation is needed because deletion is undoable.
- F5 displays retained pasted text in the scrollable feed without submitting it.
- Ctrl+Up/Down recalls structured prompt history. Recalling a queued message removes
  it from the queue until it is submitted again, retaining its paste payloads.
- Ctrl+X or `/editor` opens the external editor. Keep or move complete paste labels
  to preserve the items; remove a whole label to remove that item. The full payload
  stays in memory while the external editor edits the surrounding text.

Pastes over 1 MiB or containing NUL data are rejected with an explanation; the draft
is unchanged. A terminal input event marked truncated is rejected entirely. Smaller
pastes remain inline with normalized line endings. In explicit shell mode, pastes
remain inline shell text. Compact items require a terminal supporting bracketed paste;
the legacy key-by-key console fallback retains its existing inline paste behavior.

## Completion - 2026-09-08

- [x] Separate pasted payloads from compact labels.
- [x] Preserve items through editing, undo/redo, prompt history, queue recall, and editor round trips.
- [x] Expand exact payloads at delivery without resolving embedded file mentions.
- [x] Reject incomplete/oversized pastes and provide retained-text inspection.
