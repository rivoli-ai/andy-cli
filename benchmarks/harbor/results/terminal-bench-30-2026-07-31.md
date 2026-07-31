# Terminal-Bench 2 30-task result

Date: 2026-07-31

## Configuration

- Job: `jobs/2026-07-31__00-00-25`
- Harbor: `0.20.0`
- Dataset: `terminal-bench/terminal-bench-2`
- Dataset digest:
  `sha256:c6fc2e2382c1dbae99b2d5ecd2f4f4a60c3c01e0d84642d69b4afd92e99d078b`
- Model: `openrouter/xiaomi/mimo-v2.5`
- Tasks: `30`
- Concurrent trials: `8`
- Harbor agent timeout: `3600` seconds
- CLI timeout: `3300` seconds
- Engine timeout: `3201` seconds
- Command timeout: `900` seconds
- Maximum iterations: `150`
- Continuation window: `50` iterations
- Maximum response tokens: `8192`
- Wall-clock duration: `1:25:50`
- Agent archive SHA-256:
  `7d2e4899c54d37d3d551ea7b5b71c3dc1d1ebadfebc6fe0f9fc5110800696cd1`

The archive used `Andy.Engine` `2026.7.30-rc.94` and a locally packed
`Andy.Tools` build from commit `9861651`
(`2026.7.30-rc.89.local.9861651`). The local Tools package was necessary
because the repository's NuGet publish credential was returning HTTP 403; that
release-infrastructure failure is tracked separately.

## Aggregate

- Passed: `12`
- Failed: `18`
- Mean reward: `0.400`
- Harbor `AgentTimeoutError`: `0`
- Harbor `NonZeroAgentExitCodeError`: `17`
- Provider rate-limit errors: `0`
- Token usage and model cost: not reported by the adapter

This improves on the 2026-07-28 sample from 10/30 (`0.333`) to 12/30
(`0.400`). More importantly for the timeout work, Harbor-level agent timeouts
fell from 16 to zero. One task, `build-pov-ray`, passed its verifier even though
the agent reached the 150-iteration cap.

## Exit classification

The 17 non-zero agent exits were:

- 9 internal cancellation paths while the Harbor and CLI deadlines were still
  active.
- 5 total-turn limit exits at 150 iterations.
- 1 continuation-time limit exit.
- 1 rolling no-progress exit.
- 1 malformed JSON response-stream exit.

The internal cancellation bucket exposed a separate Engine defect: an
`OperationCanceledException` raised by a provider's own HTTP timeout was
reported as caller cancellation. `circuit-fibsqrt` confirmed this path after
OpenRouter returned HTTP 200 headers and then failed to deliver a complete
response within the provider HTTP client deadline. The follow-up Engine change
retries once before any response data is emitted and does not replay partial
streams.

Two zero-exit agents, `cancel-async-tasks` and `openssl-selfsigned-cert`, failed
their verifier. Nine other zero-exit agents passed. `build-pov-ray` supplied the
twelfth pass after its non-zero turn-limit exit.

## Per-task results

| Task | Reward | Agent result |
| --- | ---: | --- |
| `break-filter-js-from-html` | 1.0 | Completed |
| `build-cython-ext` | 1.0 | Completed |
| `build-pov-ray` | 1.0 | Passed verifier after total-turn limit |
| `caffe-cifar-10` | 0.0 | Total-turn limit |
| `cancel-async-tasks` | 0.0 | Failed verifier |
| `circuit-fibsqrt` | 0.0 | Internal cancellation path |
| `compile-compcert` | 1.0 | Completed |
| `constraints-scheduling` | 1.0 | Completed |
| `crack-7z-hash` | 0.0 | Total-turn limit |
| `custom-memory-heap-crash` | 0.0 | Internal cancellation path |
| `db-wal-recovery` | 0.0 | Internal cancellation path |
| `distribution-search` | 1.0 | Completed |
| `dna-assembly` | 0.0 | Internal cancellation path |
| `dna-insert` | 1.0 | Completed |
| `extract-elf` | 1.0 | Completed |
| `feal-linear-cryptanalysis` | 0.0 | Internal cancellation path |
| `git-leak-recovery` | 1.0 | Completed |
| `headless-terminal` | 1.0 | Completed |
| `install-windows-3-11` | 0.0 | Total-turn limit |
| `log-summary-date-ranges` | 1.0 | Completed |
| `make-mips-interpreter` | 0.0 | No progress |
| `openssl-selfsigned-cert` | 0.0 | Failed verifier |
| `overfull-hbox` | 0.0 | Internal cancellation path |
| `path-tracing` | 0.0 | Internal cancellation path |
| `polyglot-rust-c` | 0.0 | Malformed JSON response stream |
| `protein-assembly` | 0.0 | Continuation-time limit |
| `rstan-to-pystan` | 0.0 | Total-turn limit |
| `video-processing` | 0.0 | Internal cancellation path |
| `vulnerable-secret` | 1.0 | Completed |
| `winning-avg-corewars` | 0.0 | Internal cancellation path |

## Excluded setup runs

Only `jobs/2026-07-31__00-00-25` is a valid scored run. These earlier attempts
were stopped after they exposed harness defects and are excluded:

- `jobs/2026-07-30__23-09-36`: Docker network address-pool exhaustion.
- `jobs/2026-07-30__23-19-31`: trimmed headless event serialization failure.
- `jobs/2026-07-30__23-41-33`: Harbor's normalized outer agent phase still
  imposed an approximately five-minute deadline.

## Completion summary

The corrected harness produced one verifier result for every selected task and
completed all 30 trials without a Harbor timeout. The work added explicit outer
and nested deadlines, a trimmed-publish smoke gate, reflection-free headless
event serialization, and reliable parameterless activation for built-in command
tools. The scored run then identified provider-internal cancellation handling as
the next Engine reliability improvement.
