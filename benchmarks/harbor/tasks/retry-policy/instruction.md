Repair `RetryPolicy` in the `/workspace` .NET project while preserving its public API.

Required behavior:

- Construction rejects `maxAttempts < 1`, negative delays, and a maximum delay smaller than the initial delay by throwing `ArgumentOutOfRangeException`.
- `ShouldRetry(completedAttempts)` returns true while another attempt is available and false once `MaxAttempts` have completed. Negative values throw `ArgumentOutOfRangeException`.
- `DelayBeforeAttempt(1)` is zero.
- Starting with attempt 2, delay is `InitialDelay * 2^(attemptNumber - 2)`, capped at `MaxDelay` without numeric overflow.
- Attempt numbers below 1 throw `ArgumentOutOfRangeException`.

Build and check the project before finishing.
