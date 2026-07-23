namespace RetryTools;

public sealed class RetryPolicy
{
    public RetryPolicy(int maxAttempts, TimeSpan initialDelay, TimeSpan maxDelay)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        if (initialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        if (maxDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay));
        }

        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay;
        MaxDelay = maxDelay;
    }

    public int MaxAttempts { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaxDelay { get; }

    public bool ShouldRetry(int completedAttempts)
    {
        if (completedAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAttempts));
        }

        return completedAttempts < MaxAttempts;
    }

    public TimeSpan DelayBeforeAttempt(int attemptNumber)
    {
        if (attemptNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }

        if (attemptNumber == 1 || InitialDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var delayTicks = InitialDelay.Ticks;
        var maximumTicks = MaxDelay.Ticks;

        for (var attempt = 2; attempt < attemptNumber; attempt++)
        {
            if (delayTicks > maximumTicks / 2)
            {
                return MaxDelay;
            }

            delayTicks *= 2;
        }

        return TimeSpan.FromTicks(Math.Min(delayTicks, maximumTicks));
    }
}
