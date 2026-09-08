# Provider error diagnostics

Provider failures carry an `Andy.Llm.Errors.LlmProviderError` with provider,
nullable HTTP status, nullable retry-after seconds and a bounded human message.
Andy.Llm captures HTTP metadata before disposing responses; the Engine normalizes
completion failures at the call boundary and retains details on
`SimpleAgentResult.ProviderError`. Cancellation and unsupported-stream fallback
keep their existing semantics, and provider capability detection is preserved.

The CLI renders these fields as plain wrapped text using the theme's error
foreground. It does not inject ANSI escapes or interpret provider text as Markdown.
Missing status and retry values stay absent. Raw JSON envelopes are not displayed.
`SimpleAssistantService.LastProviderError` exposes the latest structured failure
and clears at the start of the next request.

Example:

```text
Provider error: Moonshot AI (HTTP 429)
Retry after: 1 second
Model is temporarily rate-limited.
```

## Completion - 2026-09-08

Structured errors are implemented across Andy.Llm 2026.9.8-rc.85 and
Andy.Engine 2026.9.8-rc.102, both published to NuGet. The CLI consumes those
packages and renders provider identity, HTTP status, retry delay, and a bounded,
redacted message using the error foreground. Regression tests cover metadata
propagation, plain rendering, absent fields, wrapping, and recovery after failure.
