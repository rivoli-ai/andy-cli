using RetryTools;

var failures = new List<string>();
var policy = new RetryPolicy(4, TimeSpan.FromMilliseconds(125), TimeSpan.FromMilliseconds(400));

Check(policy.MaxAttempts == 4, "MaxAttempts was not retained");
Check(policy.ShouldRetry(0), "zero completed attempts should allow an attempt");
Check(policy.ShouldRetry(3), "three completed attempts should allow the fourth attempt");
Check(!policy.ShouldRetry(4), "four completed attempts should exhaust the policy");
Check(!policy.ShouldRetry(100), "completed attempts above the maximum should stay exhausted");

CheckDelay(policy, 1, 0);
CheckDelay(policy, 2, 125);
CheckDelay(policy, 3, 250);
CheckDelay(policy, 4, 400);
CheckDelay(policy, 20, 400);
CheckDelay(
    new RetryPolicy(3, TimeSpan.FromTicks(long.MaxValue / 2), TimeSpan.MaxValue),
    20,
    TimeSpan.MaxValue.TotalMilliseconds);

ExpectOutOfRange(() => new RetryPolicy(0, TimeSpan.Zero, TimeSpan.Zero), "maxAttempts");
ExpectOutOfRange(() => new RetryPolicy(1, TimeSpan.FromTicks(-1), TimeSpan.Zero), "initialDelay");
ExpectOutOfRange(() => new RetryPolicy(1, TimeSpan.Zero, TimeSpan.FromTicks(-1)), "maxDelay");
ExpectOutOfRange(
    () => new RetryPolicy(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)),
    "maxDelay below initialDelay");
ExpectOutOfRange(() => policy.ShouldRetry(-1), "negative completedAttempts");
ExpectOutOfRange(() => policy.DelayBeforeAttempt(0), "attempt zero");

if (failures.Count == 0)
{
    Console.WriteLine("Retry policy verification passed.");
    return 0;
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return 1;

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

void CheckDelay(RetryPolicy retryPolicy, int attempt, double expectedMilliseconds)
{
    var actual = retryPolicy.DelayBeforeAttempt(attempt).TotalMilliseconds;
    if (actual != expectedMilliseconds)
    {
        failures.Add($"attempt {attempt} expected {expectedMilliseconds} ms, got {actual} ms");
    }
}

void ExpectOutOfRange(Action action, string scenario)
{
    try
    {
        action();
        failures.Add($"{scenario} did not throw ArgumentOutOfRangeException");
    }
    catch (ArgumentOutOfRangeException)
    {
    }
    catch (Exception ex)
    {
        failures.Add($"{scenario} threw {ex.GetType().Name}");
    }
}
