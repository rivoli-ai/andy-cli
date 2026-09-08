# MCP gateway and JSON subprocess input

Completed 2026-09-08 (PR #159). Set `mcp_gateway` to an HTTP(S) base URL or
`$ANDY_MCP_URL`; MCP tools without explicit endpoints use the base plus their
escaped tool name. Explicit endpoints take precedence. Missing or invalid
resolved endpoints fail configuration validation. Existing runtime bearer-token
forwarding and reserved environment-variable protections remain in force.

CLI tools default to `input_mode: "argv"`. With `input_mode: "json"`, the
`arguments` object is serialized to UTF-8 stdin (maximum 1 MiB), followed by EOF.
The fixed command prefix is retained. Stdout and stderr drain concurrently with
stdin, including during large requests. Cancellation kills the process tree and
propagates to the caller.
