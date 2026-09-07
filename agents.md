# Claude Code Assistant Configuration

This file contains authorized commands and configuration for Claude Code Assistant sessions.

## Authorized Commands

### Testing and Coverage

- `dotnet test --collect:\"XPlat Code Coverage\" --results-directory ./TestResults` - Run tests with code coverage collection
- `reportgenerator -reports:\"./TestResults/*/coverage.cobertura.xml\" -targetdir:\"./TestResults/CoverageReport\" -reporttypes:Html` - Generate HTML coverage report
- `reportgenerator -reports:\"./TestResults/*/coverage.cobertura.xml\" -targetdir:\"./TestResults/CoverageReport\" -reporttypes:TextSummary` - Generate text summary coverage report

### Build and Development

- `dotnet build` - Build the solution
- `dotnet restore` - Restore NuGet packages
- `dotnet run --project <project_path>` - Run a .NET project (macOS/cross-platform)

### Platform-Specific Notes (macOS)

- **NEVER use mono or .exe files** - This is a .NET 10.0 project running natively on macOS
- **Use `dotnet run`** instead of compiling to .exe and running with mono
- **Use `dotnet <command>`** for all .NET operations (build, test, run, etc.)

### Git Operations

- `git status` - Check repository status
- `git add .` - Stage all changes
- `git commit -m \"message\"` - Commit changes
- `git pull` - Pull changes from remote
- `git push` - Push changes to remote

## Project Information

- **Target Framework**: .NET 10.0
- **Test Framework**: xUnit
- **Coverage Tool**: Coverlet
- **Report Generator**: ReportGenerator global tool

## Development Workflow Instructions

### Code Style Guidelines

- **NO EMOJIS**: Do not use emojis in code, comments, or application output
- Keep all text plain and professional
- Use ASCII characters only for terminal UI elements

### Task Completion Tracking

When completing a set of tasks or phase milestones:

1. **Check the task list**: ALWAYS review the conversion plan to ensure all tasks in the current phase are complete
2. **Mark tasks completed**: when a task is in a docs md file, and [ ] available for that task, mark it with [x] when it is complete
3. **Add completion summary**: Include a dated summary section with key achievements
4. **Update project status**: Reflect current phase progress in README.md
5. **Commit changes**: Use descriptive commit messages and do not add a comment mentioning Claude Code, Anthropic, or any other code assistant in the commits messages, issues descriptions, PR or merges.

**IMPORTANT**: Before marking a phase as complete, systematically review ALL tasks in that phase section of the conversion plan to ensure nothing was missed. Check both main tasks and sub-tasks.

### Code Quality Standards

- Always write test in the tests/ assemblies for new code or code changes in the src/ directory
- Run `dotnet format` before committing to keep formatting consistent (there is no git-hook setup script; the only script in `scripts/` is `build-release.sh`)
- Ensure all tests pass: `dotnet test`
- Generate coverage reports for significant changes

### Testing Requirements for Code Changes

**CRITICAL**: When making code changes or claiming fixes:

1. **Modify or create tests** - Update existing tests or create new tests to verify the fix
2. **Run tests before claiming completion** - Always run `dotnet test` to confirm fixes work
3. **Test implementation location** - Tests must be implemented in .NET within existing test folders and assemblies
4. **Verify the actual behavior** - Don't assume a fix works; verify it through tests and actual execution
5. **Update test expectations** - If behavior changes are intentional, update test expectations accordingly

### Documentation Updates

- Keep README.md current with latest .NET version and features
- Update local development setup guide when adding new tools or processes
- Maintain conversion plan progress tracking for transparency

## Notes

- Coverage reports are generated in `./TestResults/CoverageReport/`
- Integration tests may fail due to CLI execution context but unit tests provide good coverage

<!-- coord:begin -->
## Coordination

Several agents and people change this repository at the same time. Coordination runs through
`coord`, a local service. It is not paperwork about the work: it is how you get a place to work
and how you find out what everybody else is doing.

Read `docs/coordination/README.md` once. Then, every session:

```bash
coord register --agent <your-id>     # opus5, codex, kimik3, and so on. Once per session.
coord next                           # what to work on. Never empty while work exists.
coord start <target> --next "..."    # your claim, your branch, your worktree, in one call.
```

### The rules that are enforced, not requested

- **You may not commit in the primary checkout, on a protected branch, or in a worktree you do
  not hold.** A pre-commit hook checks this. Run `coord start` and work in the directory it
  gives you.
- **Two agents cannot hold one issue.** `coord start` either gives you the claim or tells you
  who has it. Do not work around a refusal; take something else from `coord next`.
- **An issue does not close without evidence.** `coord close_issue` requires the commit or pull
  request, the command you ran, and its result. Anything closed another way is detected and
  queued for audit.

### The rules that depend on you

- **Keep your claim current.** `coord update --next "..."` whenever scope, ownership, a blocker
  or the integration order changes. Updates renew your lease. During long work, call
  `coord heartbeat` before the configured lease expires (30 minutes by default). Other agents read
  your next action to decide whether to wait for you or route around you.
- **A blocker is not an idle state.** Record it with `coord update --status blocked --blockedOn
  "..."`, then call `coord next` and take unblocked work on a surface that does not overlap.
- **Never idle while work exists.** After finishing, handing off, or blocking anything, call
  `coord next` immediately. It ranks open issues first, then unreviewed pull requests, then
  unverified closures, then stale sweep areas, then orphaned worktrees. Only when it returns
  nothing is there nothing to do.
- **Stay inside your claimed surface.** Expand the claim before expanding the change. Stage
  explicit paths; never `git add -A` in a checkout that could hold another lane's work.
- **Mirror decisions to the issue.** The board is live state, the GitHub issue is the durable
  record. Any ownership decision, blocker or changed integration order belongs on both.
- **Do not merge, push, close, or release unless you have been given integration authority.**
  Report a branch and a commit to whoever has it.

### What every tool result tells you

Every `coord` call returns the live board and everything that changed since your last call. You
do not need to poll and you should not: if you have nothing to do, `coord next` will find you
something, and `coord wait` blocks properly when it genuinely cannot.
<!-- coord:end -->
