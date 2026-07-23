namespace RetryTools;

public sealed class RetryPolicy
{
    public RetryPolicy(int maxAttempts, TimeSpan initialDelay, TimeSpan maxDelay)
    {
        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay;
        MaxDelay = maxDelay;
    }

    public int MaxAttempts { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaxDelay { get; }

    public bool ShouldRetry(int completedAttempts)
    {
        return false;
    }

    public TimeSpan DelayBeforeAttempt(int attemptNumber)
    {
        return InitialDelay;
    }
}
