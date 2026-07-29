# Terminal-Bench 2 30-task result

Date: 2026-07-28

## Configuration

- Andy CLI: `2026.07.23-dev`
- Harbor: `0.20.0`
- Dataset: `terminal-bench/terminal-bench-2`
- Model: `openrouter/xiaomi/mimo-v2.5`
- Agent maximum iterations: `1000`
- Agent timeout: `12000` seconds
- Requested concurrency: `30`
- Effective concurrency: `9`

The run selected Harbor's deterministic first 30 tasks from the 89-task
dataset. Docker exhausted its predefined network address pools after creating
nine isolated task networks, so 21 initial trials failed before Andy started.
Those infrastructure-only attempts were rerun in waves of no more than nine.
The aggregate below contains exactly one verifier result for each of the 30
unique tasks and excludes the 21 infrastructure-only attempts.

## Aggregate

- Passed: 10
- Failed: 20
- Mean reward: 0.333
- Trials marked `AgentTimeoutError`: 16
- Provider rate-limit errors: 0

`overfull-hbox` received reward 1.0 even though Harbor marked the agent as
timed out, because the persisted workspace passed the verifier. The other 15
timeout-marked trials received reward 0.0. The adapter did not report token
usage or model cost for this run.

## Per-task results

| Task | Reward | Result |
| --- | ---: | --- |
| `break-filter-js-from-html` | 0.0 | Agent timeout |
| `build-cython-ext` | 1.0 | Passed |
| `build-pov-ray` | 1.0 | Passed |
| `caffe-cifar-10` | 0.0 | Agent timeout |
| `cancel-async-tasks` | 0.0 | Failed verifier |
| `circuit-fibsqrt` | 0.0 | Agent timeout |
| `compile-compcert` | 0.0 | Agent timeout |
| `constraints-scheduling` | 1.0 | Passed |
| `crack-7z-hash` | 0.0 | Agent timeout |
| `custom-memory-heap-crash` | 0.0 | Agent timeout |
| `db-wal-recovery` | 0.0 | Agent timeout |
| `distribution-search` | 1.0 | Passed |
| `dna-assembly` | 0.0 | Agent timeout |
| `dna-insert` | 0.0 | Agent timeout |
| `extract-elf` | 0.0 | Failed verifier |
| `feal-linear-cryptanalysis` | 0.0 | Agent timeout |
| `git-leak-recovery` | 0.0 | Agent timeout |
| `headless-terminal` | 1.0 | Passed |
| `install-windows-3-11` | 0.0 | Failed verifier |
| `log-summary-date-ranges` | 1.0 | Passed |
| `make-mips-interpreter` | 0.0 | Agent timeout |
| `openssl-selfsigned-cert` | 1.0 | Passed |
| `overfull-hbox` | 1.0 | Passed verifier after agent timeout |
| `path-tracing` | 0.0 | Agent timeout |
| `polyglot-rust-c` | 0.0 | Agent timeout |
| `protein-assembly` | 0.0 | Agent timeout |
| `rstan-to-pystan` | 1.0 | Passed |
| `video-processing` | 0.0 | Failed verifier |
| `vulnerable-secret` | 1.0 | Passed |
| `winning-avg-corewars` | 0.0 | Failed verifier |

## Completion summary

The adapter produced valid verifier results for all 30 selected tasks. The run
also exposed two harness constraints that were corrected during execution:
compatibility with Harbor 0.20.0's published installed-agent base class, and
provider key retention when secrets are passed through `--agent-env`. The
adapter now installs its system dependencies without relying on an unreleased
Harbor helper and transfers provider keys through an ephemeral container file.
