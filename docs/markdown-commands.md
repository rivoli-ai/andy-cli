# Markdown slash commands

Andy discovers slash commands from Markdown files, so a team can check repeatable
prompts into a repository and a user can keep personal prompt workflows outside
any source tree. A Markdown command is a prompt template and nothing else: it
produces text that is submitted exactly as if you had typed it.

## Where commands come from

| Scope | Directory | Typical use |
| --- | --- | --- |
| User | `~/.andy/commands/**/*.md` | Personal workflows, not checked in. |
| Project | `<workspace>/.andy/commands/**/*.md` | Commands shared with the repository. |

Both roots are scanned on first use and cached. `/commands reload` re-scans
without restarting the TUI, and the autocomplete list and command palette are
rebuilt at the same time.

## Naming

The file's path relative to its root, minus the `.md` extension, becomes the
command name. Directory separators become colons.

| File | Command | Also accepted |
| --- | --- | --- |
| `.andy/commands/review.md` | `/review` | - |
| `.andy/commands/git/commit.md` | `/git:commit` | `/git/commit` |
| `.andy/commands/git/pr/describe.md` | `/git:pr:describe` | `/git/pr/describe` |

Names are lower-cased. Each segment must start with a letter or digit and may
contain letters, digits, `.`, `_`, and `-`. **Spaces are not allowed in command
file names**; a file named `my command.md` is rejected with a diagnostic and the
rest of the directory still loads.

## Precedence

1. A project command always wins over a user command with the same name,
   regardless of which file was written first.
2. Two files in the same root that normalize to the same name are resolved by
   ordinal path order; the loser is reported by `/commands diagnostics`.
3. Built-in command names and their aliases are reserved. A file called
   `help.md`, `exit.md`, `permissions.md`, `m.md`, or `perms.md` is rejected, so a
   checked-in repository can never repoint a built-in command at a prompt
   template.

The command list is sorted by name and is stable across scans.

## Frontmatter schema

Frontmatter is optional. When present it must be the first thing in the file,
opened and closed by a line containing only `---`.

| Field | Required | Meaning |
| --- | --- | --- |
| `description` | no | One-line summary shown in `/help`, autocomplete, the palette, and `/commands list`. Defaults to the first meaningful body line. |
| `provider` | no | Preferred provider. Advisory metadata only; it does not switch providers. |
| `model` | no | Preferred model. Advisory metadata only. |
| `mode` | no | Free-form mode hint. Advisory metadata only; it can never widen permissions. |

Only simple `key: value` scalars are supported, with optional single or double
quotes and full-line `#` comments. Anything else - lists, nested blocks,
duplicate keys, unknown keys - is reported as a diagnostic and ignored; the
command still loads. Unclosed frontmatter is a warning and the whole file is
treated as the template.

The following fields are **refused with an error** because they would let a
checked-in file escalate what Andy is allowed to do: `agent`, `subagent`,
`allowed-tools`, `allowed_tools`, `tools`, `permission`, `permissions`, `shell`,
`bash`, `command`, `exec`, `run`, `disable-model-invocation`, `template`.

## Argument expansion

The text after the command name is expanded into the template.

| Placeholder | Expands to |
| --- | --- |
| `$ARGUMENTS`, `${ARGUMENTS}` | The raw argument text exactly as typed, quotes included. |
| `$1` .. `$9`, `${1}` .. `${9}` | The n-th argument with its surrounding quotes removed. |
| `$$` | A literal `$`. |

Quoting rules:

- Arguments are separated by runs of whitespace.
- Double or single quotes group whitespace into one argument and are removed from
  the positional value. `/review "src/a b.cs" style` gives `$1 = src/a b.cs` and
  `$2 = style`.
- Inside double quotes, `\"` and `\\` are escapes. Single quotes are literal
  throughout, so `'C:\path'` keeps its backslash.
- An unterminated quote is not an error: the rest of the line becomes the final
  argument.
- A missing positional expands to the empty string, never to the literal `$3`.
  `/commands info <name>` shows which placeholders a template uses, and a
  reminder is printed when a template asks for more arguments than were given.
- Substitution is single-pass. Text that arrives through an argument is never
  rescanned, so a `$ARGUMENTS` typed as an argument stays literal.
- Any other `$` is left alone (`$PATH`, `$0`, `$10`, a trailing `$`). Note that
  `$5.00` reads as placeholder `$5` followed by `.00` - write `$$5.00` for a
  price.

## File mentions

An `@path` mention inside the expanded template is resolved to a structured file
part: the mention stays in the prose and the file content is attached alongside
it, together with the resolved path and whether it was truncated. Mentions
resolve relative to the workspace root, and a path that escapes the workspace is
refused.

This is currently a small local resolver; it is scheduled to be replaced by the
shared structured file-mention resolver from issue #277 through the
`ICustomCommandFileResolver` seam, without changing the result shape.

## Managing commands

| Command | Behavior |
| --- | --- |
| `/commands` | List Markdown commands with `[user]`/`[project]` source labels. |
| `/commands info <name>` | Show the file, metadata, placeholder usage, shadowed files, and template. |
| `/commands reload` | Re-scan both roots and refresh autocomplete and the palette. |
| `/commands diagnostics` | Show every problem found while loading command files. |

`/cmds` is an alias. Custom commands also appear in the inline autocomplete list
under the prompt and in the command palette under the "Markdown Commands"
category, each labelled with its source.

Nothing here can prevent Andy from starting: a malformed template, an unreadable
directory, an oversized file, or a reserved name becomes a diagnostic rather than
an exception.

## Security limitations

These are deliberate constraints, not gaps to be filled in later without review.

- **No shell or process interpolation.** Expansion is pure string substitution.
  There is no command substitution of any kind, and argument text is inert.
- **No privilege changes.** A command cannot grant a permission, enable a tool,
  choose a different agent, or bypass an active Plan-mode overlay. The expanded
  prompt is submitted through the same path as anything you type, so every
  existing approval and plan gate still applies. `provider`, `model`, and `mode`
  are metadata that the UI displays; the catalog never applies them.
- **Size limits are enforced before a prompt is constructed.** A command file
  over 64 KB is rejected without being read into a prompt. A referenced file is
  size-checked before the read and truncated at 64 KB, at most 10 mentions are
  resolved per command, and the combined mention budget is 256 KB.
- **Bounded discovery.** At most 500 command files per root and 8 directory
  levels are scanned, and symlinked directories are skipped so a link cannot pull
  templates in from elsewhere on disk.
- **Workspace-scoped mentions.** `@path` mentions may only reference files inside
  the workspace.

## Examples

`.andy/commands/review.md`:

```markdown
---
description: Review the working tree for correctness and style
---

Review the current working tree.

Focus on: $ARGUMENTS

Point out correctness problems first, then style. Do not change any files.
```

Run it with `/review error handling and naming`.

`.andy/commands/git/commit.md`:

```markdown
---
description: Draft a commit message for the staged changes
model: gpt-5
---

Draft a commit message for the staged changes.

Scope: $1
Style: $2 (default to imperative mood when this is empty)
```

Run it with `/git:commit parser "past tense"`.

`~/.andy/commands/explain.md`:

```markdown
---
description: Explain a file in plain language
---

Explain $1 in plain language.

@$1
```

Run it with `/explain src/Andy.Cli/Program.cs`.
