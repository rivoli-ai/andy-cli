# Thinking blocks

Completed 2026-09-08 (PR #161). Explicit provider `thinking` or
`reasoning_content` metadata appears in a dim, ASCII-framed block. Complete
`<think>...</think>` blocks in response text are also recognized. Inline text is
parsed at completion; structured streaming metadata updates the block as it arrives.
Ordinary assistant narration is not classified as thinking.

F4 toggles visibility retroactively. `--hide-thinking` overrides the startup
`ANDY_SHOW_THINKING` setting (`false`, `0`, or `no` hides blocks).
The toggle only affects rendering. Original provider responses and engine
transcripts remain unchanged, and ThinkingEvent records are published whether
visible or hidden. Inline thinking is omitted from the ordinary answer display
to avoid duplication. Instrumentation history retains its existing bounded FIFO.
