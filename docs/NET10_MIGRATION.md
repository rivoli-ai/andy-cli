# .NET 10 migration

Updated: 2026-07-30

## Scope

Andy CLI and its Andy package dependency closure now target `net10.0`:

- Andy ACP
- Andy Code Index
- Andy Configuration
- Andy Context
- Andy Data
- Andy Engine
- Andy LLM
- Andy MCP
- Andy Model
- Andy Permissions
- Andy Skills
- Andy Tools
- Andy TUI
- Andy PDF
- Andy CLI

Unrelated sibling applications, archived copies, and existing worktrees are outside
this dependency migration.

## SDK policy

Each repository pins a `10.0.100` baseline in `global.json`, rolls forward within
the latest installed .NET 10 feature band, and disallows preview SDKs. CI,
containers, package-consumer tests, scripts, examples, and maintained setup
documentation target .NET 10.

## Completion summary

Validation used .NET SDK 10.0.302:

- All 15 solutions restore and build for `net10.0`.
- 9,910 tests pass across the dependency closure.
- The 8 failing Andy PDF tests reproduce unchanged on the original `net8.0`
  revision (2,218 other PDF tests pass), so they are recorded as pre-existing
  baseline debt rather than migration regressions.
- NuGet locked restore succeeds for Andy CLI.
- Transitive vulnerability audits report no vulnerable packages in the 15
  migrated solutions.
- Coverage was collected for the highest-impact migration surfaces:
  Andy CLI 67.3% line coverage, Andy Code Index 72.2%, and Andy Tools 75.3%.

Compatibility work included ASP.NET Core and EF Core 10 test-host alignment in
Andy Code Index, OpenAPI 2 and Swashbuckle 10 migration, .NET 10 analyzer and
certificate-loading updates in Andy PDF, and package-consumer validation for
Andy TUI.
