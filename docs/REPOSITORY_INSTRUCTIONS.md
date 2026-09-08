# Repository instructions and skills

Place UTF-8 `AGENTS.md` files at the repository root or in nested directories.
The nearest ancestor containing a `.git` file or directory defines the repository
boundary. Without Git metadata, the selected working directory is the boundary.
Only the boundary-to-working-directory chain loads, in that order. Deeper files
override parent guidance in their scope. Sibling and descendant scopes do not
load until the session is launched in that directory; merely reading a file does
not expand the instruction scope. Ancestors outside the boundary are excluded.

Example root `AGENTS.md`:

```text
Build with dotnet build. Run dotnet test before reporting completion.
```

A nested `tests/AGENTS.md` may specify test conventions for sessions started in
`tests/`. TUI, headless workspace roots, and ACP session working directories use
the same resolver. ACP restoration resolves current checked-in instructions.

Use `/skills instructions` in the TUI or `andy-cli skills instructions` to list
applicable sources and skipped-file diagnostics. Headless runs write the same
report to stderr, leaving the NDJSON event schema unchanged. Instructions load
when an agent is created; restart the agent after changing the files.

Limits: 16 KiB per file, 64 KiB prompt inclusion including source framing, at most
32 scoped directories. Invalid UTF-8, binary data, oversized files, and symbolic
links are skipped with diagnostics. Content is never truncated into partial rules.
Repository text cannot grant permissions, enable tools, or override user requests
or host constraints. Permission checks still apply at the executor boundary.

Portable skills are already supported from `.andy/skills/<name>/SKILL.md` and
`~/.andy/skills/<name>/SKILL.md`. A skill manifest contains YAML `name` and
`description`, followed by Markdown instructions. Keep referenced resources inside
the skill directory. The existing catalog exposes names and descriptions first;
the `skill` and `skill_file` tools load selected content on demand. Use `/skills
list`, `/skills info <name>`, `/skills diagnostics`, and `/skills disable <name>`
to inspect discovery, diagnose malformed manifests, and control selection.

## Completion - 2026-09-07

Added shared instruction resolution, bounded source attribution, containment
checks, cross-mode prompt tests, and inspectable load diagnostics (#212).
