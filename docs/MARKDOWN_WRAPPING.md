# Markdown wrapping

Rich Markdown is parsed into styled text before wrapping. Lines prefer spaces; words longer than the available width are split. Inline bold/code styles survive wrapping, and list continuation rows keep their indentation. Paragraph spacing comes from the Markdown parser.

Measurement and scrolling use one cached row layout. Resizing or changing theme colors rebuilds it. Only requested rows are emitted when scrolling. There is no fixed 8,192-row cutoff hiding the end of a response.

Completed 2026-09-08: revised PR #30 onto the current feed, with regressions for word boundaries, styled text, HTML links, hanging list indentation, resize, every scroll slice, and a 9,000-line response. The original obsolete feed replacement is superseded by this integration.

Issue #28 retains its P2 follow-up scope: language-aware hyphenation, Unicode line-breaking behavior across languages, a measured performance target, and configurable typography quality. These are not implemented by this revision.
